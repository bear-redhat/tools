using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Investigator.Models;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public sealed class VertexEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _http;
    private readonly LlmOptions _llmOptions;
    private readonly GoogleAccessTokenProvider _tokenProvider;
    private readonly ILogger<VertexEmbeddingClient> _logger;

    public VertexEmbeddingClient(HttpClient http, IOptions<LlmOptions> llmOptions, GoogleAccessTokenProvider tokenProvider, ILogger<VertexEmbeddingClient> logger)
    {
        _http = http;
        _llmOptions = llmOptions.Value;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var primaryProfile = _llmOptions.Primary;
        if (string.IsNullOrEmpty(primaryProfile))
            throw new InvalidOperationException("Llm:Primary is required");

        var modelOpts = _llmOptions.Models[primaryProfile];
        var region = modelOpts.Region ?? "us-east5";
        var project = modelOpts.ProjectId ?? "";
        var model = modelOpts.EmbeddingModel ?? "textembedding-gecko@003";

        var url = $"https://{region}-aiplatform.googleapis.com/v1/projects/{project}"
            + $"/locations/{region}/publishers/google/models/{model}:predict";

        var body = JsonSerializer.Serialize(new
        {
            instances = new[] { new { content = text } }
        });

        var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        var token = await _tokenProvider.GetAccessTokenAsync(ct);
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        _logger.LogDebug("Embedding text ({Length} chars) via Vertex model {Model}", text.Length, model);

        using var response = await _http.SendAsync(httpReq, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Vertex embedding failed HTTP {Status}: {Body}", (int)response.StatusCode, errorBody);
            throw new HttpRequestException($"Vertex embedding HTTP {(int)response.StatusCode}: {errorBody}", null, response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("predictions", out var predictions)
            && predictions.GetArrayLength() > 0
            && predictions[0].TryGetProperty("embeddings", out var embeddings)
            && embeddings.TryGetProperty("values", out var values))
        {
            return values.EnumerateArray().Select(e => e.GetSingle()).ToArray();
        }

        _logger.LogError("Vertex embedding response missing expected fields: {Json}", json[..Math.Min(200, json.Length)]);
        throw new InvalidOperationException("Vertex embedding response missing expected fields");
    }
}
