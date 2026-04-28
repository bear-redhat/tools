using System.Text.Json;
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
            System = systemPrompt,
            Messages = messages,
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
}
