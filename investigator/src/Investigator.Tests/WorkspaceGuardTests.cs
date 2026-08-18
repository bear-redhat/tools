using Investigator.Contracts;

namespace Investigator.Tests;

/// <summary>
/// Tool inputs are model-authored and several tools join them straight into Path.Combine
/// before deleting, creating or reading.
/// </summary>
public class WorkspaceGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"inv-ws-{Guid.NewGuid():N}");

    public WorkspaceGuardTests() => Directory.CreateDirectory(Path.Combine(_root, "tool_outputs"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("tool_outputs/001-run_oc.txt")]
    [InlineData("tool_outputs/nested/deeper/file.txt")]
    public void PathsInsideTheWorkspace_Resolve(string relative)
    {
        Assert.NotNull(ToolContext.ResolveInside(_root, relative));
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("tool_outputs/../../escape.txt")]
    [InlineData("prow_logs/../../../../../../tmp/x")]
    public void TraversingPaths_AreRejected(string relative)
    {
        Assert.Null(ToolContext.ResolveInside(_root, relative));
    }

    [Fact]
    public void AbsolutePathsOutsideTheWorkspace_AreRejected()
    {
        Assert.Null(ToolContext.ResolveInside(_root, "/etc/passwd"));
    }

    [Fact]
    public void SymlinksPointingOutOfTheWorkspace_AreRejected()
    {
        // The workspace is written by tools: unpacking a must-gather or cloning a repo
        // routinely materialises links, and a textual .. check does not catch them.
        var outside = Path.Combine(Path.GetTempPath(), $"inv-secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outside, "token");
        var link = Path.Combine(_root, "tool_outputs", "innocent.txt");

        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // symlink creation unavailable here
        }

        try
        {
            Assert.Null(ToolContext.ResolveInside(_root, "tool_outputs/innocent.txt"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void APathThatDoesNotExistYet_StillResolves()
    {
        // Write paths are checked before the directory is created.
        Assert.NotNull(ToolContext.ResolveInside(_root, "tool_outputs/prow_logs/new/build-log.txt"));
    }
}
