using Investigator.Mcp;
using Investigator.Models;
using Investigator.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Investigator.Tests;

public class McpSessionContextTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"inv-mcp-{Guid.NewGuid():N}");

    private McpSessionContext Build(IHttpContextAccessor accessor)
    {
        var manager = new WorkspaceManager(
            Options.Create(new WorkspaceOptions { RootPath = _root }),
            NullLogger<WorkspaceManager>.Instance);

        return new McpSessionContext(manager, accessor, NullLoggerFactory.Instance);
    }

    private static IHttpContextAccessor WithSession(string? sessionId)
    {
        var context = new DefaultHttpContext();
        if (sessionId is not null)
            context.Request.Headers[McpSessionContext.SessionIdHeader] = sessionId;
        return new HttpContextAccessor { HttpContext = context };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void DifferentSessions_GetSeparateWorkspaces()
    {
        var first = Build(WithSession("session-a")).EnsureWorkspace();
        var second = Build(WithSession("session-b")).EnsureWorkspace();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SameSession_ReusesItsWorkspace()
    {
        var context = Build(WithSession("session-a"));

        Assert.Equal(context.EnsureWorkspace(), context.EnsureWorkspace());
    }

    [Fact]
    public void OutputNumbering_IsPerSession_SoFilesDoNotCollide()
    {
        var accessor = WithSession("session-a");
        var context = Build(accessor);

        var a1 = context.CreateToolContext().NextOutputNumber();
        var a2 = context.CreateToolContext().NextOutputNumber();

        // Same instance, different session -> a fresh counter, not a continuation.
        accessor.HttpContext!.Request.Headers[McpSessionContext.SessionIdHeader] = "session-b";
        var b1 = context.CreateToolContext().NextOutputNumber();

        Assert.Equal(1, a1);
        Assert.Equal(2, a2);
        Assert.Equal(1, b1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingSessionHeader_FallsBackToASharedWorkspaceRatherThanFailing(string? header)
    {
        // stdio transports and stateless deployments send no session id.
        var context = Build(WithSession(header));

        Assert.Equal(McpSessionContext.AnonymousSessionId, context.CurrentSessionId);
        Assert.False(string.IsNullOrEmpty(context.EnsureWorkspace()));
    }

    [Fact]
    public void NoHttpContextAtAll_StillWorks()
    {
        var context = Build(new HttpContextAccessor { HttpContext = null });

        Assert.Equal(McpSessionContext.AnonymousSessionId, context.CurrentSessionId);
        Assert.False(string.IsNullOrEmpty(context.EnsureWorkspace()));
    }

    [Fact]
    public void ClientSuppliedSessionId_CannotSteerTheDirectoryName()
    {
        // The header is attacker-controllable; it must never reach a path unescaped.
        var context = Build(WithSession("../../etc/passwd"));

        var workspace = context.EnsureWorkspace();

        Assert.DoesNotContain("..", workspace);
        Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(workspace));
    }

    [Fact]
    public void ToolContext_TakesTheNormalOutputPath_SoLargeOutputIsPageable()
    {
        var context = Build(WithSession("session-a")).CreateToolContext();

        // RawOutput true returned everything inline with no file to page.
        Assert.False(context.RawOutput);
    }
}
