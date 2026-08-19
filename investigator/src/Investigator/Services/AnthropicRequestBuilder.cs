using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Investigator.Contracts;
using Investigator.Models;

namespace Investigator.Services;

public static class AnthropicRequestBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string BuildRequestJson(
        ModelOptions profile,
        List<LlmMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        string? systemPrompt,
        string anthropicVersion,
        bool stream,
        int? thinkingBudgetOverride = null,
        LlmRequestContext? context = null)
    {
        var thinkingBudget = thinkingBudgetOverride ?? profile.ThinkingBudget;

        var request = new LlmRequest
        {
            AnthropicVersion = anthropicVersion,
            Stream = stream ? true : null,
            System = BuildSystem(systemPrompt, profile),
            Messages = BuildMessages(messages, profile),
            MaxTokens = profile.MaxTokens,
            Thinking = new ThinkingConfig { Type = "enabled", BudgetTokens = thinkingBudget },
            Tools = tools.Select(t => new LlmTool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = t.ParameterSchema,
            }).ToList(),
        };

        if (context is { UserId: not null } or { ConversationId: not null })
        {
            var parts = new[] { context!.UserId, context.ConversationId }
                .Where(s => !string.IsNullOrEmpty(s));
            request.Metadata = new LlmRequestMetadata { UserId = string.Join(":", parts) };
        }

        return JsonSerializer.Serialize(request, SerializerOptions);
    }

    private static CacheControl? Breakpoint(ModelOptions profile) =>
        profile.PromptCaching ? new CacheControl { Ttl = profile.PromptCacheTtl } : null;

    /// <summary>
    /// One breakpoint at the end of the system prompt. Render order is tools -> system ->
    /// messages, so this caches the tool schemas and the persona together -- the block
    /// every agent in a room shares byte-for-byte.
    /// </summary>
    private static List<SystemBlock>? BuildSystem(string? systemPrompt, ModelOptions profile)
    {
        if (string.IsNullOrEmpty(systemPrompt)) return null;

        return
        [
            new SystemBlock { Text = systemPrompt, CacheControl = Breakpoint(profile) },
        ];
    }

    /// <summary>
    /// A second breakpoint on the newest turn, so each turn reads the whole prior
    /// conversation from cache and writes only what it added. Without it the transcript --
    /// far larger than the system prompt on a long investigation -- is re-billed in full
    /// on every call.
    /// </summary>
    private static List<LlmMessage> BuildMessages(List<LlmMessage> messages, ModelOptions profile)
    {
        var built = messages
            .Select(m => new LlmMessage { Role = m.Role, Content = m.Content })
            .ToList();

        if (!profile.PromptCaching || built.Count == 0) return built;

        var last = built[^1];
        var marked = MarkLastBlock(last.Content, profile);
        if (marked is not null) last.Content = marked.Value;

        return built;
    }

    /// <summary>
    /// Appends cache_control to the final content block. Message content arrives as opaque
    /// JSON, so it is rewritten through a node tree rather than reconstructed -- a
    /// tool_use/tool_result pair must survive this untouched or the API rejects the turn.
    /// </summary>
    private static JsonElement? MarkLastBlock(JsonElement content, ModelOptions profile)
    {
        var cacheControl = new JsonObject { ["type"] = "ephemeral" };
        if (profile.PromptCacheTtl is { } ttl) cacheControl["ttl"] = ttl;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(content.GetRawText());
        }
        catch (JsonException)
        {
            return null;
        }

        switch (node)
        {
            case JsonArray { Count: > 0 } array when array[^1] is JsonObject lastBlock:
                lastBlock["cache_control"] = cacheControl;
                break;

            case JsonValue value when value.TryGetValue<string>(out var text):
                node = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                    ["cache_control"] = cacheControl,
                });
                break;

            default:
                return null;
        }

        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
    }
}
