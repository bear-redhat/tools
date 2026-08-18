using Microsoft.Extensions.Logging;

namespace Investigator.Contracts;

public record ToolContext(
    ILogger Logger,
    string WorkspacePath,
    Action<string>? OnOutputLine,
    Func<int> NextOutputNumber,
    string CallerId,
    Func<string, string, string>? StartChildCall = null,
    Action<string, string, string, int, bool>? CompleteChildCall = null,
    string? ConversationId = null,
    bool RawOutput = false)
{
    /// <summary>
    /// Resolves a model-supplied path against the workspace and returns null if it escapes.
    ///
    /// Tool inputs are authored by the model, and several tools join them straight into
    /// Path.Combine before deleting, creating or reading. Textual normalisation alone is
    /// not enough: the workspace is itself tool-written, so extracting a must-gather or
    /// cloning a repo can materialise symlinks that point anywhere on the pod.
    /// </summary>
    public string? ResolveInsideWorkspace(string relativePath) =>
        ResolveInside(WorkspacePath, relativePath);

    /// <inheritdoc cref="ResolveInsideWorkspace"/>
    public static string? ResolveInside(string workspacePath, string relativePath)
    {
        var root = Path.GetFullPath(workspacePath);

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, relativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        try
        {
            var target = File.ResolveLinkTarget(full, returnFinalTarget: true)
                ?? Directory.ResolveLinkTarget(full, returnFinalTarget: true);
            if (target is not null) full = target.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Dangling link, or a path that does not exist yet on a write path.
        }

        return full == root || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full
            : null;
    }

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
