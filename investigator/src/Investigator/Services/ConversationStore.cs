using System.Collections.Concurrent;
using Investigator.Tools;
using Visus.Cuid;

namespace Investigator.Services;

public enum ClaimResult { Success, Busy, WrongUser }

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
            session.IsInvestigating = false;
        }
    }
}
