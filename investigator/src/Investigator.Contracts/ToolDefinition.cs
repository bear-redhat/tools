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
    // Overrides ToolOutputOptions.HardCapBytes for tools that do their own paging.
    // The global 8KB cap silently truncated read_output to a fraction of the range the
    // caller asked for, which made the two paging surfaces disagree.
    int? HardCapBytesOverride = null,
    // Surfaced to MCP clients as the tool's readOnlyHint. Defaults to false: tools that
    // cannot mutate external state opt in explicitly. Previously every tool was
    // advertised as read-only, including run_shell, draft_patch and github.
    bool ReadOnlyHint = false);
