using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Investigator.Models;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public sealed class BedrockEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _http;
    private readonly LlmOptions _llmOptions;
    private readonly ILogger<BedrockEmbeddingClient> _logger;
    private AmazonBedrockRuntimeClient? _sdkClient;

    public BedrockEmbeddingClient(HttpClient http, IOptions<LlmOptions> llmOptions, ILogger<BedrockEmbeddingClient> logger)
    {
        _http = http;
        _llmOptions = llmOptions.Value;
        _logger = logger;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var primaryProfile = _llmOptions.Primary;
        if (string.IsNullOrEmpty(primaryProfile))
            throw new InvalidOperationException("Llm:Primary is required");

        var modelOpts = _llmOptions.Models[primaryProfile];
        var region = modelOpts.Region ?? "us-east-1";
        var model = modelOpts.EmbeddingModel ?? "amazon.titan-embed-text-v2:0";
        var provider = modelOpts.Provider;
        var creds = _llmOptions.Providers.TryGetValue(provider, out var c) ? c : null;
        var bearerToken = creds?.BearerToken;

        _logger.LogDebug("Embedding text ({Length} chars) via Bedrock model {Model}", text.Length, model);

        if (!string.IsNullOrEmpty(bearerToken))
            return await EmbedWithBearerToken(region, model, text, bearerToken, ct);

        return await EmbedWithSdkClient(region, model, text, creds, ct);
    }

    private async Task<float[]> EmbedWithBearerToken(
        string region, string model, string text, string bearerToken, CancellationToken ct)
    {
        var url = $"https://bedrock-runtime.{region}.amazonaws.com/model/{Uri.EscapeDataString(model)}/invoke";
        var body = JsonSerializer.Serialize(new { inputText = text });

        var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.ContentType!.CharSet = null;

        var httpReq = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var response = await _http.SendAsync(httpReq, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Bedrock embedding failed HTTP {Status}: {Body}", (int)response.StatusCode, errorBody);
            throw new HttpRequestException($"Bedrock embedding HTTP {(int)response.StatusCode}: {errorBody}", null, response.StatusCode);
        }

        return ParseEmbeddingResponse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<float[]> EmbedWithSdkClient(
        string region, string model, string text, ProviderCredentials? creds, CancellationToken ct)
    {
        var client = GetOrCreateSdkClient(region, creds);

        var request = new InvokeModelRequest
        {
            ModelId = model,
            ContentType = "application/json",
            Accept = "application/json",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { inputText = text }))),
        };

        InvokeModelResponse response;
        try
        {
            response = await client.InvokeModelAsync(request, ct);
        }
        catch (AmazonBedrockRuntimeException ex)
        {
            _logger.LogError(ex, "Bedrock InvokeModel (embedding) failed for model {Model}", model);
            throw new HttpRequestException($"Bedrock embedding API error: {ex.Message}", ex);
        }

        using var reader = new StreamReader(response.Body);
        return ParseEmbeddingResponse(await reader.ReadToEndAsync(ct));
    }

    private AmazonBedrockRuntimeClient GetOrCreateSdkClient(string region, ProviderCredentials? creds)
    {
        if (_sdkClient is not null) return _sdkClient;

        var credentials = creds is not null
            ? BedrockCredentialHelper.Resolve(creds, "embedding", _logger)
            : null;
        var regionEndpoint = RegionEndpoint.GetBySystemName(region);

        _sdkClient = credentials is not null
            ? new AmazonBedrockRuntimeClient(credentials, regionEndpoint)
            : new AmazonBedrockRuntimeClient(regionEndpoint);

        return _sdkClient;
    }

    private float[] ParseEmbeddingResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("embedding", out var embeddingProp))
            return embeddingProp.EnumerateArray().Select(e => e.GetSingle()).ToArray();

        _logger.LogError("Bedrock embedding response missing 'embedding' field: {Json}", json[..Math.Min(200, json.Length)]);
        throw new InvalidOperationException("Bedrock embedding response missing 'embedding' field");
    }
}
