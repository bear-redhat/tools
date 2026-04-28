using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Investigator.Contracts;
using Investigator.Models;
using ContentBlock = Investigator.Models.ContentBlock;

namespace Investigator.Services;

public sealed class BedrockClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string _profileName;
    private readonly ModelOptions _profile;
    private readonly ProviderCredentials _creds;
    private readonly ILogger _logger;
    private AmazonBedrockRuntimeClient? _sdkClient;

    public BedrockClient(HttpClient http, string name, ModelOptions profile, ProviderCredentials creds, ILogger logger)
    {
        _http = http;
        _profileName = name;
        _profile = profile;
        _creds = creds;
        _logger = logger;
    }

    public async IAsyncEnumerable<ContentBlock> StreamMessageAsync(
        List<LlmMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        string? systemPrompt,
        [EnumeratorCancellation] CancellationToken ct,
        int? thinkingBudgetOverride = null,
        LlmRequestContext? context = null)
    {
        var region = _profile.Region;
        var model = _profile.Model;
        var bearerToken = _creds.BearerToken;

        if (string.IsNullOrEmpty(region))
        {
            _logger.LogError("Region is not configured for Bedrock profile '{Profile}'", _profileName);
            throw new InvalidOperationException($"Region is not configured for Bedrock profile '{_profileName}'.");
        }

        var json = AnthropicRequestBuilder.BuildRequestJson(
            _profile, messages, tools, systemPrompt,
            anthropicVersion: "bedrock-2023-05-31", stream: false,
            thinkingBudgetOverride: thinkingBudgetOverride,
            context: context);

        _logger.LogDebug("Calling Bedrock: region={Region}, model={Model}, profile={Profile}, messages={Count}, auth={AuthType}",
            region, model, _profileName, messages.Count, !string.IsNullOrEmpty(bearerToken) ? "bearer" : "sigv4");

        if (!string.IsNullOrEmpty(bearerToken))
        {
            await foreach (var block in StreamWithBearerToken(region, model, json, bearerToken, ct))
                yield return block;
        }
        else
        {
            await foreach (var block in StreamWithSdkClient(region, model, json, ct))
                yield return block;
        }
    }

    private async IAsyncEnumerable<ContentBlock> StreamWithBearerToken(
        string region, string model, string json, string bearerToken,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var url = $"https://bedrock-runtime.{region}.amazonaws.com/model/{Uri.EscapeDataString(model)}/invoke-with-response-stream";

        var content = new StringContent(json, Encoding.UTF8, "application/json");
        content.Headers.ContentType!.CharSet = null;

        var httpReq = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var response = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);

        _logger.LogDebug("Bedrock response: status={Status}, content-type={ContentType}",
            (int)response.StatusCode, response.Content.Headers.ContentType?.MediaType);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Bedrock returned HTTP {StatusCode}: {Body}", (int)response.StatusCode, errorBody);
            throw new HttpRequestException(
                $"Bedrock HTTP {(int)response.StatusCode}: {errorBody}",
                null,
                response.StatusCode);
        }

        var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var block in ParseAwsEventStream(stream, ct))
            yield return block;
    }

    private async IAsyncEnumerable<ContentBlock> StreamWithSdkClient(
        string region, string model, string json,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var client = GetOrCreateSdkClient(region);

        var invokeRequest = new InvokeModelWithResponseStreamRequest
        {
            ModelId = model,
            ContentType = "application/json",
            Accept = "application/json",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(json)),
        };

        InvokeModelWithResponseStreamResponse response;
        try
        {
            response = await client.InvokeModelWithResponseStreamAsync(invokeRequest, ct);
        }
        catch (AmazonBedrockRuntimeException ex)
        {
            _logger.LogError(ex, "Bedrock InvokeModelWithResponseStream failed for model {Model}", model);
            throw new HttpRequestException($"Bedrock API error: {ex.Message}", ex);
        }

        var processor = new StreamEventProcessor(_logger);

        foreach (var evt in response.Body.AsEnumerable())
        {
            if (ct.IsCancellationRequested) break;

            if (evt is PayloadPart payloadPart)
            {
                var payloadJson = Encoding.UTF8.GetString(payloadPart.Bytes.ToArray());

                StreamEvent? streamEvt;
                try
                {
                    streamEvt = JsonSerializer.Deserialize<StreamEvent>(payloadJson);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize Bedrock stream event: {Data}", payloadJson);
                    continue;
                }

                if (streamEvt is null)
                {
                    _logger.LogWarning("Deserialized Bedrock stream event was null: {Data}", payloadJson);
                    continue;
                }

                foreach (var block in processor.ProcessEvent(streamEvt))
                    yield return block;
            }
        }
    }

    private async IAsyncEnumerable<ContentBlock> ParseAwsEventStream(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var processor = new StreamEventProcessor(_logger);

        while (!ct.IsCancellationRequested)
        {
            var prelude = new byte[12];
            var bytesRead = await ReadExactAsync(stream, prelude, ct);
            if (bytesRead < 12) break;

            var totalLength = ReadBigEndianInt32(prelude, 0);
            var headersLength = ReadBigEndianInt32(prelude, 4);

            var remaining = totalLength - 12;
            var frameData = new byte[remaining];
            bytesRead = await ReadExactAsync(stream, frameData, ct);
            if (bytesRead < remaining) break;

            var payloadLength = totalLength - 12 - headersLength - 4;
            if (payloadLength <= 0) continue;

            var payloadJson = Encoding.UTF8.GetString(frameData, headersLength, payloadLength);
            _logger.LogTrace("Bedrock event stream raw payload: {Payload}", payloadJson);

            var actualJson = UnwrapBedrockPayload(payloadJson, _logger);
            if (actualJson is null)
            {
                _logger.LogDebug("Bedrock event stream payload had no extractable content: {Payload}",
                    payloadJson.Length > 200 ? payloadJson[..200] + "..." : payloadJson);
                continue;
            }

            StreamEvent? streamEvt;
            try
            {
                streamEvt = JsonSerializer.Deserialize<StreamEvent>(actualJson);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize Bedrock event: {Data}", actualJson);
                continue;
            }

            if (streamEvt is null) continue;

            foreach (var block in processor.ProcessEvent(streamEvt))
                yield return block;
        }
    }

    private static string? UnwrapBedrockPayload(string payloadJson, ILogger? logger = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() is { Length: > 0 })
                return payloadJson;

            if (root.TryGetProperty("bytes", out var bytesProp))
            {
                var base64 = bytesProp.GetString();
                if (!string.IsNullOrEmpty(base64))
                    return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            }

            if (root.TryGetProperty("chunk", out var chunkProp))
            {
                if (chunkProp.TryGetProperty("bytes", out var chunkBytes))
                {
                    var base64 = chunkBytes.GetString();
                    if (!string.IsNullOrEmpty(base64))
                        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                }
            }
        }
        catch (JsonException ex)
        {
            logger?.LogDebug(ex, "Failed to parse Bedrock payload JSON");
        }

        return null;
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0) return offset;
            offset += read;
        }
        return offset;
    }

    private static int ReadBigEndianInt32(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    private AmazonBedrockRuntimeClient GetOrCreateSdkClient(string region)
    {
        if (_sdkClient is not null) return _sdkClient;

        var credentials = BedrockCredentialHelper.Resolve(_creds, _profileName, _logger);
        var regionEndpoint = RegionEndpoint.GetBySystemName(region);

        _sdkClient = credentials is not null
            ? new AmazonBedrockRuntimeClient(credentials, regionEndpoint)
            : new AmazonBedrockRuntimeClient(regionEndpoint);

        return _sdkClient;
    }
}
