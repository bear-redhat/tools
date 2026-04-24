using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;

namespace Investigator.Services;

public sealed class VertexAiClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string _profileName;
    private readonly ModelOptions _profile;
    private readonly GoogleAccessTokenProvider _tokenProvider;
    private readonly ILogger _logger;

    public VertexAiClient(HttpClient http, string name, ModelOptions profile, GoogleAccessTokenProvider tokenProvider, ILogger logger)
    {
        _http = http;
        _profileName = name;
        _profile = profile;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async IAsyncEnumerable<ContentBlock> StreamMessageAsync(
        List<LlmMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        string? systemPrompt,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var region = _profile.Region;
        var project = _profile.ProjectId;
        var model = _profile.Model;

        if (string.IsNullOrEmpty(region))
        {
            _logger.LogError("Region is not configured for Vertex profile '{Profile}'", _profileName);
            throw new InvalidOperationException($"Region is not configured for Vertex profile '{_profileName}'.");
        }
        if (string.IsNullOrEmpty(project))
        {
            _logger.LogError("ProjectId is not configured for Vertex profile '{Profile}'", _profileName);
            throw new InvalidOperationException($"ProjectId is not configured for Vertex profile '{_profileName}'.");
        }

        var url = $"https://{region}-aiplatform.googleapis.com/v1/projects/{project}"
            + $"/locations/{region}/publishers/anthropic/models/{model}:streamRawPredict";

        _logger.LogDebug("Calling Vertex AI: region={Region}, project={Project}, model={Model}, profile={Profile}, messages={Count}",
            region, project, model, _profileName, messages.Count);

        var json = AnthropicRequestBuilder.BuildRequestJson(
            _profile, messages, tools, systemPrompt,
            anthropicVersion: "vertex-2023-10-16", stream: true);

        var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        var token = await _tokenProvider.GetAccessTokenAsync(ct);
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Vertex AI returned HTTP {StatusCode}: {Body}", (int)response.StatusCode, errorBody);
            throw new HttpRequestException(
                $"Vertex AI HTTP {(int)response.StatusCode}: {errorBody}",
                null,
                response.StatusCode);
        }

        await foreach (var block in SseParser.ParseSseStream(
            await response.Content.ReadAsStreamAsync(ct), _logger, ct))
        {
            yield return block;
        }
    }
}
