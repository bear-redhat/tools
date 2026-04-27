using Microsoft.Extensions.Logging;

namespace Investigator.Contracts;

public record ToolContext(
    ILogger Logger,
    string WorkspacePath,
    Action<string>? OnOutputLine,
    Func<int> NextOutputNumber,
    string CallerId,
    Func<string, string, string>? StartChildCall = null,
    Action<string, string, string, int, bool>? CompleteChildCall = null);
