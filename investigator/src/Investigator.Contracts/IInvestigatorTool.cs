using System.Text.Json;

namespace Investigator.Contracts;

public interface IInvestigatorTool
{
    ToolDefinition Definition { get; }

    Task<ToolResult> InvokeAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct = default);
}
