using Microsoft.Extensions.Logging;

namespace Investigator.Contracts;

public record ToolContext(
    ILogger Logger,
    string WorkspacePath,
    Action<string>? OnOutputLine,
    Func<int> NextOutputNumber,
    string CallerId);
