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
    ToolScope Scope = ToolScope.All);
