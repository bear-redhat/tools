using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Investigator.Tools;

namespace Investigator.Services;

public sealed record AuditEntry(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("conversationId")] string ConversationId,
    [property: JsonPropertyName("userId")] string? UserId,
    [property: JsonPropertyName("ip")] string? Ip,
    [property: JsonPropertyName("ts")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("extra")] Dictionary<string, string>? Extra = null);

public sealed class AuditLog
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WorkspaceManager _workspaceManager;
    private readonly ILogger<AuditLog> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public AuditLog(WorkspaceManager workspaceManager, ILogger<AuditLog> logger)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    public void Record(string conversationId, string eventName, string? userId, string? ip, Dictionary<string, string>? extra = null)
    {
        var entry = new AuditEntry(eventName, conversationId, userId, ip, DateTimeOffset.UtcNow, extra);
        _ = AppendAsync(conversationId, entry);
    }

    private async Task AppendAsync(string conversationId, AuditEntry entry)
    {
        var workspace = _workspaceManager.FindWorkspacePath(conversationId);
        if (workspace is null)
        {
            _logger.LogDebug("Audit skipped for {ConversationId}: workspace not found", conversationId);
            return;
        }

        var sem = _locks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        try
        {
            var path = Path.Combine(workspace, "audit.jsonl");
            var line = JsonSerializer.Serialize(entry, s_jsonOptions);
            await File.AppendAllTextAsync(path, line + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit entry for {ConversationId}", conversationId);
        }
        finally
        {
            sem.Release();
        }
    }
}
