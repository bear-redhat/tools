using Investigator.Contracts;
using Investigator.Tools;

namespace Investigator.Mcp;

/// <summary>
/// Manages a per-MCP-session workspace directory for tool output files.
/// Scoped so each MCP session gets its own workspace.
/// </summary>
public sealed class McpSessionContext(WorkspaceManager workspaceManager, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<McpSessionContext>();
    private string? _workspacePath;
    private int _outputCounter;

    public string EnsureWorkspace()
    {
        if (_workspacePath is not null)
            return _workspacePath;

        var id = $"mcp-{Guid.NewGuid():N}"[..16];
        _workspacePath = workspaceManager.CreateWorkspace(id);
        _logger.LogInformation("Created MCP workspace: {Path}", _workspacePath);
        return _workspacePath;
    }

    public ToolContext CreateToolContext(string callerId = "mcp")
    {
        var workspace = EnsureWorkspace();
        return new ToolContext(
            _logger,
            workspace,
            OnOutputLine: null,
            NextOutputNumber: () => Interlocked.Increment(ref _outputCounter),
            CallerId: callerId,
            RawOutput: true);
    }
}
