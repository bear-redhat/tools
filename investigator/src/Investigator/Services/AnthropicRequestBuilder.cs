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
        bool stream)
    {
        var request = new LlmRequest
        {
            AnthropicVersion = anthropicVersion,
            Stream = stream ? true : null,
            System = systemPrompt,
            Messages = messages,
            MaxTokens = profile.MaxTokens,
            Thinking = new ThinkingConfig { Type = "enabled", BudgetTokens = profile.ThinkingBudget },
            Tools = tools.Select(t => new LlmTool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = t.ParameterSchema,
            }).ToList(),
        };

        return JsonSerializer.Serialize(request, SerializerOptions);
    }
}
