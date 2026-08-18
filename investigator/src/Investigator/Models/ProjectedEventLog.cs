using System.Text.Json;

namespace Investigator.Models;

/// <summary>
/// One projected event, flattened for consumption by a remote client.
/// </summary>
public sealed record TranscriptEntry
{
    public required int Seq { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>text | tool_call | tool_result | turn | session_ended</summary>
    public required string Kind { get; init; }

    public required string From { get; init; }
    public string? To { get; init; }

    public string? Text { get; init; }
    public string? Tool { get; init; }
    public string? Command { get; init; }

    public int? ExitCode { get; init; }
    public bool? TimedOut { get; init; }
    public string? Summary { get; init; }

    /// <summary>Workspace-relative path holding the untruncated output, when one exists.</summary>
    public string? OutputFile { get; init; }

    /// <summary>Seq of the tool_call this result answers.</summary>
    public int? RequestSeq { get; init; }

    public int? ParentSeq { get; init; }

    /// <summary>True when <see cref="Text"/> was clipped for retention.</summary>
    public bool Clipped { get; init; }
}

/// <summary>
/// Retains the projected event stream so a client that was not attached -- or that
/// attached late -- can still page through every tool call and result.
///
/// <see cref="RoomEventBus"/> fans out to live subscribers only, and the materialized
/// <c>ConversationItem</c> view has no tool-call representation at all, so before this
/// existed a completed investigation could not be inspected step by step.
///
/// Bodies are clipped on the way in and the full text is left on disk under
/// <c>tool_outputs/</c>; the entry keeps the reference so a caller can fetch the rest on
/// demand rather than the log holding megabytes of build logs in memory.
/// </summary>
public sealed class ProjectedEventLog
{
    public const int DefaultMaxTextChars = 2_000;
    public const int DefaultCapacity = 20_000;

    private readonly List<TranscriptEntry> _entries = [];
    private readonly Lock _gate = new();
    private readonly int _maxTextChars;
    private readonly int _capacity;

    private int _droppedFromFront;
    private int _highestSeq;

    public ProjectedEventLog(int capacity = DefaultCapacity, int maxTextChars = DefaultMaxTextChars)
    {
        _capacity = capacity;
        _maxTextChars = maxTextChars;
    }

    /// <summary>Number of entries evicted from the front because the cap was reached.</summary>
    public int Dropped { get { lock (_gate) return _droppedFromFront; } }

    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>
    /// Highest sequence number ever appended, surviving eviction. A room restarting in
    /// process continues from here, so a cursor never sees two events with the same seq.
    /// </summary>
    public int HighestSeq { get { lock (_gate) return _highestSeq; } }

    public void Append(RoomEvent evt)
    {
        var entry = Flatten(evt, _maxTextChars);
        if (entry is null) return;

        lock (_gate)
        {
            _entries.Add(entry);
            if (entry.Seq > _highestSeq) _highestSeq = entry.Seq;
            if (_entries.Count > _capacity)
            {
                var excess = _entries.Count - _capacity;
                _entries.RemoveRange(0, excess);
                _droppedFromFront += excess;
            }
        }
    }

    /// <summary>
    /// Returns entries after <paramref name="sinceSeq"/>, bounded by both a count and a
    /// character budget so a caller with a finite context can page deterministically.
    /// </summary>
    public TranscriptPage Read(int sinceSeq, int maxEntries, int maxChars, string? agentId = null, string? kind = null)
    {
        lock (_gate)
        {
            var selected = new List<TranscriptEntry>();
            var chars = 0;
            var truncated = false;

            foreach (var entry in _entries)
            {
                if (entry.Seq <= sinceSeq) continue;
                if (agentId is not null && !MatchesAgent(entry, agentId)) continue;
                if (kind is not null && entry.Kind != kind) continue;

                var cost = (entry.Text?.Length ?? 0) + (entry.Summary?.Length ?? 0) + 120;
                if (selected.Count > 0 && (selected.Count >= maxEntries || chars + cost > maxChars))
                {
                    truncated = true;
                    break;
                }

                selected.Add(entry);
                chars += cost;
            }

            var nextSeq = selected.Count > 0 ? selected[^1].Seq : sinceSeq;
            var highest = _entries.Count > 0 ? _entries[^1].Seq : sinceSeq;

            // A caller resuming from a cursor older than everything retained has a hole.
            var gap = _droppedFromFront > 0 && _entries.Count > 0 && sinceSeq < _entries[0].Seq - 1;

            return new TranscriptPage(selected, nextSeq, highest, truncated, gap);
        }
    }

    /// <summary>
    /// The agent an addressed tool call is aimed at -- 'user' for a question put to the
    /// human, or a scout name for an inter-agent message.
    /// </summary>
    private static string? Recipient(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty("to", out var to)
        && to.ValueKind == JsonValueKind.String
            ? to.GetString()
            : null;

    /// <summary>
    /// The question the lead is currently blocked on, or null if it is not waiting.
    ///
    /// An agent that calls <c>message(to: 'user')</c> ends its turn and parks on its inbox
    /// indefinitely. That state was previously invisible: it is an emergent consequence of
    /// the tool being conditionally terminal, recorded nowhere. Without it a client cannot
    /// tell "still working" from "has been waiting on you for twenty minutes".
    /// </summary>
    public TranscriptEntry? FindPendingUserRequest()
    {
        lock (_gate)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];

                // A reply from the human clears whatever was outstanding before it.
                if (entry.Kind == "text"
                    && string.Equals(entry.From, "user", StringComparison.OrdinalIgnoreCase))
                    return null;

                if (entry.Kind == "tool_call"
                    && AgentProtocol.IsQuestionForHuman(entry.Tool, entry.To))
                    return entry;
            }

            return null;
        }
    }

    private static bool MatchesAgent(TranscriptEntry entry, string agentId) =>
        string.Equals(entry.From, agentId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(entry.To, agentId, StringComparison.OrdinalIgnoreCase)
        // tool results are attributed to "tool:<name>", so match them by their caller.
        || (entry.Kind == "tool_result" && entry.From.StartsWith("tool:", StringComparison.Ordinal));

    internal static TranscriptEntry? Flatten(RoomEvent evt, int maxTextChars)
    {
        static (string? Text, bool Clipped) Clip(string? text, int max)
        {
            if (text is null || text.Length <= max) return (text, false);
            return (text[..max] + $"\n... [clipped, {text.Length - max} more chars]", true);
        }

        switch (evt)
        {
            case RoomEvent.TextMessage tm:
            {
                var (text, clipped) = Clip(tm.Text, maxTextChars);
                return new TranscriptEntry
                {
                    Seq = tm.Seq, Timestamp = tm.Timestamp, Kind = "text",
                    From = tm.From, To = tm.To, Text = text, Clipped = clipped,
                };
            }

            case RoomEvent.ToolRequest tr:
            {
                // DisplayCommand is a lossy one-liner for the UI. Retain the actual
                // arguments too, so a reader can see exactly what was invoked -- and so a
                // message addressed to the human is legible rather than just "message user".
                var (args, clipped) = tr.Input.ValueKind == JsonValueKind.Undefined
                    ? ((string?)null, false)
                    : Clip(tr.Input.GetRawText(), maxTextChars);

                return new TranscriptEntry
                {
                    Seq = tr.Seq, Timestamp = tr.Timestamp, Kind = "tool_call",
                    From = tr.From, To = tr.To ?? Recipient(tr.Input), Tool = tr.Tool,
                    Command = tr.DisplayCommand, ParentSeq = tr.ParentSeq,
                    Text = args, Clipped = clipped,
                };
            }

            case RoomEvent.ToolResponse tp:
            {
                var (text, clipped) = Clip(tp.Output, maxTextChars);
                return new TranscriptEntry
                {
                    Seq = tp.Seq, Timestamp = tp.Timestamp, Kind = "tool_result",
                    From = tp.From, To = tp.To, Tool = tp.Tool, Text = text,
                    ExitCode = tp.ExitCode, TimedOut = tp.TimedOut,
                    Summary = tp.Summary, OutputFile = tp.OutputFile,
                    RequestSeq = tp.RequestSeq, ParentSeq = tp.ParentSeq,
                    Clipped = clipped,
                };
            }

            case RoomEvent.AgentTurn at:
                return new TranscriptEntry
                {
                    Seq = at.Seq, Timestamp = at.Timestamp, Kind = "turn",
                    From = at.From, To = at.To,
                    Text = at.ThinkingText is null ? null : Clip(at.ThinkingText, maxTextChars).Text,
                };

            case RoomEvent.SessionEnded se:
                return new TranscriptEntry
                {
                    Seq = se.Seq, Timestamp = se.Timestamp, Kind = "session_ended", From = se.From,
                };

            // LlmContext is the raw model context; the projector already derives the
            // readable events above from it, so retaining it would double the memory
            // cost for no additional visibility.
            default:
                return null;
        }
    }
}

public sealed record TranscriptPage(
    IReadOnlyList<TranscriptEntry> Entries,
    int NextSeq,
    int HighestSeq,
    bool Truncated,
    bool Gap);
