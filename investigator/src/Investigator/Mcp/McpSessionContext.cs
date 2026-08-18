using System.Collections.Concurrent;
using Investigator.Contracts;
using Investigator.Tools;

namespace Investigator.Mcp;

/// <summary>
/// Scratch workspaces for <c>raw_*</c> tool calls made directly over MCP, outside any
/// investigation. Investigations get their own workspace from
/// <see cref="WorkspaceManager.CreateWorkspace"/> keyed on the conversation id; this covers
/// only ad-hoc tool calls that have no conversation.
///
/// Each MCP session gets its own workspace and its own output counter. Sharing one meant
/// two clients numbering <c>tool_outputs/NNN-*.txt</c> into the same directory, so
/// <c>raw_read_output</c> could hand one client another's data.
///
/// The identity comes from the <c>Mcp-Session-Id</c> header, which the streamable HTTP
/// transport issues at initialize and the client echoes on every later request. It is only
/// present when the transport is stateful -- see the explicit <c>Stateless = false</c> in
/// Program.cs. <see cref="ModelContextProtocol.Server.IMcpServer"/> does not surface the id
/// itself in SDK 1.4.1, so the header is the supported way to read it.
/// </summary>
public sealed class McpSessionContext(
    WorkspaceManager workspaceManager,
    IHttpContextAccessor httpContextAccessor,
    ILoggerFactory loggerFactory)
{
    /// <summary>Matches ModelContextProtocol.AspNetCore's <c>McpSessionIdHeaderName</c>.</summary>
    public const string SessionIdHeader = "Mcp-Session-Id";

    /// <summary>
    /// Used when no session id is available: a stdio transport, a stateless deployment, or
    /// a call dispatched off the request thread. Behaves like the previous single shared
    /// workspace rather than failing.
    /// </summary>
    public const string AnonymousSessionId = "anonymous";

    private readonly ILogger _logger = loggerFactory.CreateLogger<McpSessionContext>();
    private readonly ConcurrentDictionary<string, SessionWorkspace> _workspaces = new(StringComparer.Ordinal);

    private sealed class SessionWorkspace
    {
        public required string Path { get; init; }
        private int _outputCounter;
        public int NextOutputNumber() => Interlocked.Increment(ref _outputCounter);
    }

    /// <summary>The calling MCP session, or <see cref="AnonymousSessionId"/> if unknown.</summary>
    public string CurrentSessionId
    {
        get
        {
            var header = httpContextAccessor.HttpContext?.Request.Headers[SessionIdHeader].FirstOrDefault();
            return string.IsNullOrWhiteSpace(header) ? AnonymousSessionId : header;
        }
    }

    public string EnsureWorkspace() => GetOrCreate(CurrentSessionId).Path;

    public ToolContext CreateToolContext(string callerId = "mcp")
    {
        var workspace = GetOrCreate(CurrentSessionId);

        return new ToolContext(
            _logger,
            workspace.Path,
            OnOutputLine: null,
            NextOutputNumber: workspace.NextOutputNumber,
            CallerId: callerId,
            // RawOutput was true, which returned the whole output inline with a null
            // OutputFile. Anything past the client's MCP output cap was then truncated
            // client-side and unrecoverable. Taking the normal path gives head+tail, a
            // summary, and a tool_outputs/ reference that raw_read_output can page.
            RawOutput: false);
    }

    private SessionWorkspace GetOrCreate(string sessionId) =>
        _workspaces.GetOrAdd(sessionId, id =>
        {
            // Session ids are client-supplied; never let one steer a directory name.
            var slug = $"mcp-{Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..12]}"
                .ToLowerInvariant();

            var path = workspaceManager.CreateWorkspace(slug);
            _logger.LogInformation("Created MCP scratch workspace {Path} for session {Session}", path, slug);
            return new SessionWorkspace { Path = path };
        });
}
