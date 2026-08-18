using System.Text.Json;

namespace Investigator.Contracts;

[Flags]
public enum ToolScope
{
    Investigation = 1,
    Remediation = 2,
    All = Investigation | Remediation,
}

public record ToolDefinition(
    string Name,
    string Description,
    JsonElement ParameterSchema,
    TimeSpan DefaultTimeout,
    bool TruncateOutput = true,
    ToolScope Scope = ToolScope.All,
    // Surfaced to MCP clients as the tool's readOnlyHint. Defaults to false: tools that
    // cannot mutate external state opt in explicitly. Previously every tool was
    // advertised as read-only, including run_shell, draft_patch and github.
    bool ReadOnlyHint = false);
