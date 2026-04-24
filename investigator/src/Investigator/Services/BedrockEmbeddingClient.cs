using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Investigator.Models;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public sealed class BedrockEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _http;
    private readonly LlmOptions _llmOptions;
    private readonly ILogger<BedrockEmbeddingClient> _logger;

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
        var bearerToken = _llmOptions.Providers.TryGetValue(provider, out var creds) ? creds.BearerToken : null;

        var url = $"https://bedrock-runtime.{region}.amazonaws.com/model/{Uri.EscapeDataString(model)}/invoke";

        var body = JsonSerializer.Serialize(new { inputText = text });

        var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.ContentType!.CharSet = null;

        var httpReq = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

        if (!string.IsNullOrEmpty(bearerToken))
            httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        _logger.LogDebug("Embedding text ({Length} chars) via Bedrock model {Model}", text.Length, model);

        using var response = await _http.SendAsync(httpReq, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Bedrock embedding failed HTTP {Status}: {Body}", (int)response.StatusCode, errorBody);
            throw new HttpRequestException($"Bedrock embedding HTTP {(int)response.StatusCode}: {errorBody}", null, response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("embedding", out var embeddingProp))
            return embeddingProp.EnumerateArray().Select(e => e.GetSingle()).ToArray();

        _logger.LogError("Bedrock embedding response missing 'embedding' field: {Json}", json[..Math.Min(200, json.Length)]);
        throw new InvalidOperationException("Bedrock embedding response missing 'embedding' field");
    }
}
