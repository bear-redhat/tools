using System.Collections.Concurrent;
using Investigator.Tools;
using Visus.Cuid;

namespace Investigator.Services;

public sealed class ConversationStore
{
    private readonly ConcurrentDictionary<string, ConversationSession> _sessions = new();

    public ConversationSession CreateSession()
    {
        var id = new Cuid2(10).ToString();
        var session = new ConversationSession(id);
        _sessions[id] = session;
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
        return loaded;
    }

    /// <summary>
    /// Attempts to claim ownership of the session. Succeeds if the session
    /// has no current owner (new session or previous owner released).
    /// </summary>
    public bool TryClaim(string id, string circuitId)
    {
        var session = TryGetSession(id);
        if (session is null) return false;

        lock (session.Lock)
        {
            if (session.OwnerCircuitId is not null && session.OwnerCircuitId != circuitId)
                return false;

            session.OwnerCircuitId = circuitId;
            return true;
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
            session.IsInvestigating = false;
        }
    }
}
