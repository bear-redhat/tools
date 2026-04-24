namespace Investigator.Contracts;

public record ToolResult(
    string Output,
    int ExitCode = 0,
    bool TimedOut = false,
    string? ReproCommand = null);
