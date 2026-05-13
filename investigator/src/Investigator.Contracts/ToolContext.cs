using Microsoft.Extensions.Logging;

namespace Investigator.Contracts;

public record ToolContext(
    ILogger Logger,
    string WorkspacePath,
    Action<string>? OnOutputLine,
    Func<int> NextOutputNumber,
    string CallerId,
    Func<string, string, string>? StartChildCall = null,
    Action<string, string, string, int, bool>? CompleteChildCall = null)
{
    /// <summary>
    /// Allocates a numbered output file path under the workspace tool_outputs directory.
    /// Creates the directory if needed. The caller is responsible for writing to it.
    /// </summary>
    public (string FullPath, string RelativePath) AllocateOutputFile(string toolName)
    {
        var outputNum = NextOutputNumber();
        var outputDir = Path.Combine(WorkspacePath, "tool_outputs");
        Directory.CreateDirectory(outputDir);
        var fileName = $"{outputNum:D3}-{toolName}.txt";
        return (Path.Combine(outputDir, fileName), $"tool_outputs/{fileName}");
    }
}
