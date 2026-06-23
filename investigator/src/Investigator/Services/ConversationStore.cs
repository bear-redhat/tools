using System.Collections.Concurrent;
using Investigator.Models;
using Investigator.Tools;
using Visus.Cuid;

namespace Investigator.Services;

public enum ClaimResult { Success, Busy, WrongUser }

public sealed class SessionInfo
{
    public required string Id { get; init; }
    public string? OwnerUserId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string? CaseSummary { get; set; }
    public int AgentCount { get; set; }
    public bool HasWorkingAgents { get; set; }
    public bool HasRemediation { get; set; }
}

public sealed class ConversationStore
{
    private readonly ConcurrentDictionary<string, ConversationSession> _sessions = new();
    private readonly ConcurrentDictionary<string, SessionInfo> _index = new();

    public ConversationSession CreateSession()
    {
        var id = new Cuid2(10).ToString();
        var session = new ConversationSession(id);
        _sessions[id] = session;
        _index[id] = new SessionInfo { Id = id };
        return session;
    }

    public ConversationSession? TryGetSession(string id)
    {
        _sessions.TryGetValue(id, out var session);
        return session;
    }

    /// <summary>
    /// Checks memory first, then falls back to loading from disk.
    /// The loaded session is cached so subsequent lookups are fast.
    /// </summary>
    public async Task<ConversationSession?> TryGetOrLoadSessionAsync(string id, WorkspaceManager workspaceManager)
    {
        if (_sessions.TryGetValue(id, out var session))
            return session;

        var loaded = await workspaceManager.TryLoadSessionAsync(id);
        if (loaded is null)
            return null;

        _sessions.TryAdd(id, loaded);
        UpsertIndex(id, loaded);
        return loaded;
    }

    /// <summary>
    /// Drops the heavy <see cref="ConversationSession"/> from memory,
    /// keeping only lightweight metadata in <see cref="_index"/> for the home page.
    /// </summary>
    public void UnloadSession(string id)
    {
        if (!_sessions.TryGetValue(id, out var session))
            return;

        SnapshotToIndex(id, session);
        _sessions.TryRemove(id, out _);
    }

    /// <summary>
    /// Returns metadata for every known session.
    /// Loaded sessions are enriched from live state; unloaded sessions
    /// return the cached snapshot captured at unload time.
    /// </summary>
    public IReadOnlyList<SessionInfo> GetAllSessionInfo()
    {
        var result = new List<SessionInfo>(_index.Count);
        foreach (var (id, info) in _index)
        {
            if (_sessions.TryGetValue(id, out var session))
                EnrichFromLiveSession(info, session);
            result.Add(info);
        }
        return result;
    }

    /// <summary>
    /// Attempts to claim ownership of the session.
    /// Returns <see cref="ClaimResult.WrongUser"/> when the persistent owner
    /// doesn't match <paramref name="userId"/>, <see cref="ClaimResult.Busy"/>
    /// when the right user is blocked by another circuit, or
    /// <see cref="ClaimResult.Success"/> when the claim is granted.
    /// </summary>
    public ClaimResult TryClaim(string id, string circuitId, string? userId)
    {
        var session = TryGetSession(id);
        if (session is null) return ClaimResult.WrongUser;

        lock (session.Lock)
        {
            if (session.OwnerUserId is not null
                && !string.Equals(session.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase))
                return ClaimResult.WrongUser;

            if (session.OwnerCircuitId is not null && session.OwnerCircuitId != circuitId)
                return ClaimResult.Busy;

            session.OwnerUserId ??= userId;
            session.OwnerCircuitId = circuitId;
            return ClaimResult.Success;
        }
    }

    /// <summary>
    /// Releases ownership if the caller is the current owner.
    /// Resets investigation state so the session is cleanly claimable.
    /// </summary>
    public void Release(string id, string circuitId)
    {
        var session = TryGetSession(id);
        if (session is null) return;

        lock (session.Lock)
        {
            if (session.OwnerCircuitId != circuitId) return;

            session.OwnerCircuitId = null;
        }
    }

    private void UpsertIndex(string id, ConversationSession session)
    {
        var info = _index.GetOrAdd(id, _ => new SessionInfo { Id = id });
        EnrichFromLiveSession(info, session);
    }

    private void SnapshotToIndex(string id, ConversationSession session)
    {
        var info = _index.GetOrAdd(id, _ => new SessionInfo { Id = id });
        info.OwnerUserId = session.OwnerUserId;
        info.StartedAt = session.StartedAt;
        info.HasRemediation = session.Remediation is not null;

        var view = session.Investigation.CurrentView;
        var firstMsg = view.Items.OfType<ConversationItem.UserMessage>().FirstOrDefault();
        var summary = firstMsg?.Content;
        if (summary is not null && summary.Length > 120)
            summary = summary[..120] + "...";
        info.CaseSummary = summary;

        info.AgentCount = 0;
        info.HasWorkingAgents = false;
    }

    private static void EnrichFromLiveSession(SessionInfo info, ConversationSession session)
    {
        info.OwnerUserId = session.OwnerUserId;
        info.StartedAt = session.StartedAt;
        info.HasRemediation = session.Remediation is not null;

        var view = session.Investigation.CurrentView;
        var firstMsg = view.Items.OfType<ConversationItem.UserMessage>().FirstOrDefault();
        var summary = firstMsg?.Content;
        if (summary is not null && summary.Length > 120)
            summary = summary[..120] + "...";
        info.CaseSummary = summary;

        info.AgentCount = view.Members.Count(m => m.Id is not "all" and not "little-bear");
        info.HasWorkingAgents = view.HasWorkingAgents;
    }
}
