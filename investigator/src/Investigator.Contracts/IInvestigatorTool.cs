using System.Text.Json;

namespace Investigator.Contracts;

public interface IInvestigatorTool
{
    ToolDefinition Definition { get; }

    Task RegisterAsync(CancellationToken ct = default);

    Task<ToolResult> InvokeAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct = default);
}
