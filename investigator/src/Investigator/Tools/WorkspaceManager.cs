using System.Text.Json;
using Investigator.Models;
using Investigator.Services;
using Microsoft.Extensions.Options;

namespace Investigator.Tools;

public sealed class WorkspaceManager
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions s_snapshotWriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions s_snapshotReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly WorkspaceOptions _options;
    private readonly ILogger<WorkspaceManager> _logger;

    public WorkspaceManager(IOptions<WorkspaceOptions> workspaceOptions, ILogger<WorkspaceManager> logger)
    {
        _options = workspaceOptions.Value;
        _logger = logger;
    }

    private string GetRoot()
    {
        var root = _options.RootPath;
        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(Path.GetTempPath(), "investigator", "workspaces");
            _logger.LogInformation("Workspace:RootPath not configured, using temp directory: {Path}", root);
        }
        return root;
    }

    public string CreateWorkspace(string conversationId)
    {
        var root = GetRoot();
        var dirName = $"conv-{DateTime.UtcNow:yyyy-MM-dd}-{conversationId}";
        var path = Path.Combine(root, dirName);

        try
        {
            Directory.CreateDirectory(path);
            Directory.CreateDirectory(Path.Combine(path, "tool_outputs"));
            _logger.LogInformation("Created workspace: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create workspace directory at {Path}", path);
            throw;
        }

        return path;
    }

    public string? FindWorkspacePath(string conversationId)
    {
        var root = GetRoot();
        if (!Directory.Exists(root))
            return null;

        var matches = Directory.GetDirectories(root, $"conv-*-{conversationId}");
        return matches.Length > 0 ? matches[0] : null;
    }

    public async Task SaveSessionAsync(ConversationSession session)
    {
        if (session.WorkspacePath is null)
            return;

        var file = Path.Combine(session.WorkspacePath, "session.json");
        var tmp = file + ".tmp";
        try
        {
            var snapshot = SessionSnapshot.FromSession(session);
            var json = JsonSerializer.Serialize(snapshot, s_snapshotWriteOptions);
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, file, overwrite: true);
            _logger.LogInformation("Saved session {Id} to {File}", session.Id, file);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save session {Id} to {File}", session.Id, file);
        }
    }

    public async Task<ConversationSession?> TryLoadSessionAsync(string conversationId)
    {
        var workspacePath = FindWorkspacePath(conversationId);
        if (workspacePath is null)
            return null;

        var file = Path.Combine(workspacePath, "session.json");
        if (!File.Exists(file))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(file);
            var snapshot = JsonSerializer.Deserialize<SessionSnapshot>(json, s_snapshotReadOptions);
            if (snapshot is null)
                return null;

            var session = snapshot.ToSession();
            session.WorkspacePath = workspacePath;
            _logger.LogInformation("Loaded session {Id} from {File}", conversationId, file);
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load session from {File}", file);
            return null;
        }
    }

    public async Task AppendTranscriptAsync(string workspacePath, object entry)
    {
        var file = Path.Combine(workspacePath, "transcript.jsonl");
        try
        {
            var json = JsonSerializer.Serialize(entry, s_jsonOptions);
            await File.AppendAllTextAsync(file, json + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write transcript entry to {File}", file);
        }
    }
}
