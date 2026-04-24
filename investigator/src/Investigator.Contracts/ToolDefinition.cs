using System.Text.Json;

namespace Investigator.Contracts;

public record ToolDefinition(
    string Name,
    string Description,
    JsonElement ParameterSchema,
    TimeSpan DefaultTimeout,
    bool TruncateOutput = true);
