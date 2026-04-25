using Investigator.Contracts;
using Investigator.Models;

namespace Investigator.Services;

public interface ILlmClient
{
    IAsyncEnumerable<ContentBlock> StreamMessageAsync(
        List<LlmMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        string? systemPrompt,
        CancellationToken ct,
        int? thinkingBudgetOverride = null);
}
