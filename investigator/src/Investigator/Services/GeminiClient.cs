using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;

namespace Investigator.Services;

public sealed class GeminiClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string _profileName;
    private readonly ModelOptions _profile;
    private readonly GoogleAccessTokenProvider _tokenProvider;
    private readonly ILogger _logger;

    public GeminiClient(HttpClient http, string name, ModelOptions profile, GoogleAccessTokenProvider tokenProvider, ILogger logger)
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
            _logger.LogError("Region is not configured for Gemini profile '{Profile}'", _profileName);
            throw new InvalidOperationException($"Region is not configured for Gemini profile '{_profileName}'.");
        }
        if (string.IsNullOrEmpty(project))
        {
            _logger.LogError("ProjectId is not configured for Gemini profile '{Profile}'", _profileName);
            throw new InvalidOperationException($"ProjectId is not configured for Gemini profile '{_profileName}'.");
        }

        var url = $"https://{region}-aiplatform.googleapis.com/v1/projects/{project}"
            + $"/locations/{region}/publishers/google/models/{model}:streamGenerateContent?alt=sse";

        _logger.LogDebug("Calling Gemini: region={Region}, project={Project}, model={Model}, profile={Profile}, messages={Count}",
            region, project, model, _profileName, messages.Count);

        var geminiRequest = BuildGeminiRequest(messages, tools, systemPrompt);
        var json = JsonSerializer.Serialize(geminiRequest, s_jsonOptions);

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
            _logger.LogError("Gemini returned HTTP {StatusCode}: {Body}", (int)response.StatusCode, errorBody);
            throw new HttpRequestException(
                $"Gemini HTTP {(int)response.StatusCode}: {errorBody}",
                null,
                response.StatusCode);
        }

        var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var block in ParseGeminiSseStream(stream, ct))
            yield return block;
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private JsonElement BuildGeminiRequest(
        List<LlmMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        string? systemPrompt)
    {
        var request = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            request["system_instruction"] = new
            {
                parts = new[] { new { text = systemPrompt } }
            };
        }

        var geminiContents = new List<object>();
        foreach (var msg in messages)
        {
            var role = msg.Role == "assistant" ? "model" : "user";
            var parts = ConvertMessageToParts(msg);
            geminiContents.Add(new { role, parts });
        }
        request["contents"] = geminiContents;

        if (tools.Count > 0)
        {
            var functionDeclarations = tools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                parameters = t.ParameterSchema,
            }).ToList();
            request["tools"] = new[] { new { function_declarations = functionDeclarations } };
        }

        var genConfig = new Dictionary<string, object>
        {
            ["maxOutputTokens"] = _profile.MaxTokens,
        };
        if (_profile.ThinkingBudget > 0)
        {
            genConfig["thinkingConfig"] = new { thinkingBudget = _profile.ThinkingBudget };
        }
        request["generationConfig"] = genConfig;

        var jsonStr = JsonSerializer.Serialize(request, s_jsonOptions);
        return JsonDocument.Parse(jsonStr).RootElement.Clone();
    }

    private static List<object> ConvertMessageToParts(LlmMessage msg)
    {
        var parts = new List<object>();

        if (msg.Content.ValueKind == JsonValueKind.String)
        {
            parts.Add(new { text = msg.Content.GetString() ?? "" });
            return parts;
        }

        if (msg.Content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in msg.Content.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "text":
                        var text = item.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                        parts.Add(new { text });
                        break;

                    case "tool_use":
                    {
                        var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var input = item.TryGetProperty("input", out var inp) ? inp : default;
                        parts.Add(new
                        {
                            functionCall = new { name, args = input }
                        });
                        break;
                    }

                    case "tool_result":
                    {
                        var toolUseId = item.TryGetProperty("tool_use_id", out var tid) ? tid.GetString() ?? "" : "";
                        var content = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                        parts.Add(new
                        {
                            functionResponse = new
                            {
                                name = toolUseId,
                                response = new { output = content }
                            }
                        });
                        break;
                    }

                    default:
                        if (item.TryGetProperty("text", out var fallbackText))
                            parts.Add(new { text = fallbackText.GetString() ?? "" });
                        break;
                }
            }
        }

        if (parts.Count == 0)
            parts.Add(new { text = msg.Content.GetRawText() });

        return parts;
    }

    private async IAsyncEnumerable<ContentBlock> ParseGeminiSseStream(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        var toolCallCounter = 0;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (ct.IsCancellationRequested) break;
            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (string.IsNullOrWhiteSpace(data) || data == "[DONE]") continue;

            JsonElement root;
            try
            {
                root = JsonDocument.Parse(data).RootElement;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse Gemini SSE event: {Data}", data);
                continue;
            }

            if (!root.TryGetProperty("candidates", out var candidates)) continue;

            foreach (var candidate in candidates.EnumerateArray())
            {
                if (!candidate.TryGetProperty("content", out var content)) continue;
                if (!content.TryGetProperty("parts", out var partsArray)) continue;

                foreach (var part in partsArray.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var textProp))
                    {
                        var text = textProp.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            yield return new ContentBlock
                            {
                                Type = "text",
                                Text = text,
                            };
                        }
                    }
                    else if (part.TryGetProperty("functionCall", out var fc))
                    {
                        var name = fc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var args = fc.TryGetProperty("args", out var a) ? a.Clone() : default(JsonElement?);

                        yield return new ContentBlock
                        {
                            Type = "tool_use",
                            Id = $"gemini_tc_{toolCallCounter++}",
                            Name = name,
                            Input = args,
                        };
                    }
                    else if (part.TryGetProperty("thought", out var thought))
                    {
                        var thinkText = thought.GetString();
                        if (!string.IsNullOrEmpty(thinkText))
                        {
                            yield return new ContentBlock
                            {
                                Type = "thinking",
                                Text = thinkText,
                            };
                        }
                    }
                }
            }
        }
    }
}
