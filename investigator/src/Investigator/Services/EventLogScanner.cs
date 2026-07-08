using System.Text.Json;
using Investigator.Models;

namespace Investigator.Services;

public record IncompleteAgent(
    string Name,
    string Id,
    string Role,
    string Task,
    string? Model,
    string DispatcherId,
    IReadOnlyList<string> CcTargets,
    bool IsAnalyst,
    List<LlmMessage> ReplayedMessages);

internal static class EventLogScanner
{
    public static List<IncompleteAgent> FindIncompleteAgents(IReadOnlyList<RoomEvent> events, string leadId)
    {
        var startIndex = FindCurrentRunStart(events);
        var dispatched = ScanDispatches(events, startIndex, leadId);
        var concluded = ScanConcluded(events, startIndex, dispatched.Keys);

        var incomplete = new List<IncompleteAgent>();
        foreach (var (id, meta) in dispatched)
        {
            if (concluded.Contains(id))
                continue;

            var replayed = LlmContextApplier.Replay(events, id);
            incomplete.Add(meta with { ReplayedMessages = replayed });
        }

        return incomplete;
    }

    private static int FindCurrentRunStart(IReadOnlyList<RoomEvent> events)
    {
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i] is RoomEvent.SessionEnded)
                return i + 1;
        }
        return 0;
    }

    private static Dictionary<string, IncompleteAgent> ScanDispatches(
        IReadOnlyList<RoomEvent> events, int startIndex, string leadId)
    {
        var result = new Dictionary<string, IncompleteAgent>(StringComparer.OrdinalIgnoreCase);

        for (var i = startIndex; i < events.Count; i++)
        {
            if (events[i] is not RoomEvent.LlmContext ctx)
                continue;

            foreach (var msg in ctx.Messages)
            {
                if (msg.Role != "assistant" || msg.Content.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var block in msg.Content.EnumerateArray())
                {
                    if (!IsToolUse(block, "delegate"))
                        continue;

                    var input = block.TryGetProperty("input", out var inp) ? inp : default;
                    var toolUseId = block.TryGetProperty("id", out var idVal) ? idVal.GetString() : null;

                    var agentName = FindDelegateResult(ctx.Messages, msg, toolUseId);
                    if (agentName is null)
                        continue;

                    var agentId = agentName.ToLowerInvariant().Replace(" ", "-");
                    var role = Prop(input, "role");
                    var task = Prop(input, "task");
                    if (role is null || task is null)
                        continue;
                    var model = Prop(input, "model");
                    var tier = Prop(input, "tier") ?? "field";
                    var isAnalyst = tier == "analyst";

                    var ccTargets = new List<string>();
                    if (input.TryGetProperty("cc", out var ccArray) && ccArray.ValueKind == JsonValueKind.Array)
                        foreach (var item in ccArray.EnumerateArray())
                            if (item.GetString() is { } ccName)
                                ccTargets.Add(ccName.ToLowerInvariant().Replace(" ", "-"));

                    result[agentId] = new IncompleteAgent(
                        Name: agentName,
                        Id: agentId,
                        Role: role,
                        Task: task,
                        Model: model,
                        DispatcherId: ctx.From,
                        CcTargets: ccTargets,
                        IsAnalyst: isAnalyst,
                        ReplayedMessages: []);
                }
            }
        }

        return result;
    }

    private static string? FindDelegateResult(IReadOnlyList<LlmMessage> messages, LlmMessage assistantMsg, string toolUseId)
    {
        var foundAssistant = false;
        foreach (var msg in messages)
        {
            if (ReferenceEquals(msg, assistantMsg))
            {
                foundAssistant = true;
                continue;
            }
            if (!foundAssistant || msg.Role != "user" || msg.Content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var block in msg.Content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_result"
                    && block.TryGetProperty("tool_use_id", out var id) && id.GetString() == toolUseId
                    && block.TryGetProperty("content", out var content))
                {
                    var contentStr = content.GetString();
                    if (contentStr is null) return null;
                    return ExtractAgentName(contentStr);
                }
            }
        }
        return null;
    }

    private static HashSet<string> ScanConcluded(
        IReadOnlyList<RoomEvent> events, int startIndex, IEnumerable<string> agentIds)
    {
        var candidates = new HashSet<string>(agentIds, StringComparer.OrdinalIgnoreCase);
        var concluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = startIndex; i < events.Count; i++)
        {
            if (events[i] is not RoomEvent.LlmContext ctx || !candidates.Contains(ctx.From))
                continue;

            foreach (var msg in ctx.Messages)
            {
                if (msg.Role != "assistant" || msg.Content.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var block in msg.Content.EnumerateArray())
                {
                    if (IsToolUse(block, "conclude"))
                    {
                        concluded.Add(ctx.From);
                        break;
                    }
                }
                if (concluded.Contains(ctx.From))
                    break;
            }
        }

        return concluded;
    }

    /// <summary>
    /// Scans the event log for tool_use blocks in LlmContext messages that lack
    /// matching tool_result blocks. Appends abort LlmContext events so that both
    /// the UX replay (via TranscriptProjector) and the LLM context replay (via
    /// LlmContextApplier) see proper abort results.
    /// </summary>
    public static List<RoomEvent> CloseDanglingToolCalls(IReadOnlyList<RoomEvent> events)
    {
        var startIndex = FindCurrentRunStart(events);
        var dangling = FindDanglingToolUseIds(events, startIndex);
        if (dangling.Count == 0)
            return events as List<RoomEvent> ?? events.ToList();

        var closed = new List<RoomEvent>(events);
        var now = DateTimeOffset.UtcNow;

        foreach (var (agentId, toolUseIds) in dangling)
        {
            var results = toolUseIds.Select(id => new
            {
                type = "tool_result",
                tool_use_id = id,
                content = "[aborted] Pod restarted while tool was executing. You may retry if needed."
            }).ToArray();

            closed.Add(new RoomEvent.LlmContext(0, agentId, now,
                [new LlmMessage { Role = "user", Content = JsonSerializer.SerializeToElement(results) }]));
        }

        return closed;
    }

    private static Dictionary<string, List<string>> FindDanglingToolUseIds(
        IReadOnlyList<RoomEvent> events, int startIndex)
    {
        var pending = new Dictionary<string, Dictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);

        for (var i = startIndex; i < events.Count; i++)
        {
            if (events[i] is not RoomEvent.LlmContext ctx)
                continue;

            foreach (var msg in ctx.Messages)
            {
                if (msg.Role == "assistant" && msg.Content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in msg.Content.EnumerateArray())
                    {
                        if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_use"
                            && block.TryGetProperty("id", out var id) && id.GetString() is { } idStr)
                        {
                            if (!pending.ContainsKey(ctx.From))
                                pending[ctx.From] = new Dictionary<string, bool>();
                            pending[ctx.From][idStr] = true;
                        }
                    }
                }
                else if (msg.Role == "user" && msg.Content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in msg.Content.EnumerateArray())
                    {
                        if (block.TryGetProperty("type", out var tp) && tp.GetString() == "tool_result"
                            && block.TryGetProperty("tool_use_id", out var tuid) && tuid.GetString() is { } resolvedId)
                        {
                            if (pending.TryGetValue(ctx.From, out var agentPending))
                                agentPending.Remove(resolvedId);
                        }
                    }
                }
            }
        }

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (agentId, ids) in pending)
        {
            if (ids.Count > 0)
                result[agentId] = ids.Keys.ToList();
        }
        return result;
    }

    private static bool IsToolUse(JsonElement block, string toolName)
    {
        return block.TryGetProperty("type", out var t) && t.GetString() == "tool_use"
            && block.TryGetProperty("name", out var n) && n.GetString() == toolName;
    }

    private static string? Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? ExtractAgentName(string output)
    {
        const string prefix = "Dispatched ";
        if (!output.StartsWith(prefix)) return null;
        var rest = output[prefix.Length..];
        var idx = rest.IndexOf(" (", StringComparison.Ordinal);
        return idx > 0 ? rest[..idx] : null;
    }
}
