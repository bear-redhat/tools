using System.Text.Json;
using Investigator.Models;

namespace Investigator.Services;

public sealed class ToolEffectEnricher : IEventEnricher
{
    private readonly string _leadId;
    private readonly Dictionary<int, RoomEvent.ToolRequest> _pending = new();
    private readonly Dictionary<string, string> _dispatcherOf = new();
    private readonly Dictionary<string, List<string>> _ccTargetsOf = new();

    public ToolEffectEnricher(string leadId) => _leadId = leadId;

    internal void PreloadDispatchers(
        IEnumerable<(string agentId, string dispatcherId, List<string>? ccTargets)> agents)
    {
        foreach (var (agentId, dispatcherId, ccTargets) in agents)
        {
            _dispatcherOf[agentId] = dispatcherId;
            if (ccTargets is { Count: > 0 })
                _ccTargetsOf[agentId] = ccTargets;
        }
    }

    public ValueTask<IReadOnlyList<RoomEvent>> EnrichAsync(RoomEvent evt, CancellationToken ct)
    {
        if (evt is RoomEvent.ToolRequest tr)
        {
            _pending[tr.Seq] = tr;
            return ValueTask.FromResult<IReadOnlyList<RoomEvent>>([]);
        }

        if (evt is not RoomEvent.ToolResponse tres)
            return ValueTask.FromResult<IReadOnlyList<RoomEvent>>([]);

        _pending.TryGetValue(tres.RequestSeq, out var req);

        List<RoomEvent>? derived = tres.Tool switch
        {
            "conclude" when req?.From != _leadId => DeriveFromConclude(req!),
            "message" => DeriveFromMessage(req),
            "recall" => Wrap(DeriveFromRecall(req)),
            "delegate" => Wrap(DeriveFromDelegate(req, tres)),
            _ => null,
        };

        return ValueTask.FromResult<IReadOnlyList<RoomEvent>>(
            derived is { Count: > 0 } ? derived : []);
    }

    private List<RoomEvent> DeriveFromConclude(RoomEvent.ToolRequest req)
    {
        var summary = Prop(req.Input, "summary") ?? "";
        var targetId = _dispatcherOf.GetValueOrDefault(req.From, _leadId);

        var events = new List<RoomEvent>
        {
            new RoomEvent.TextMessage(0, req.From, req.Timestamp, summary) { To = targetId }
        };

        if (_ccTargetsOf.TryGetValue(req.From, out var ccTargets))
        {
            var ccText = $"[CC from {req.From}]: {summary}";
            foreach (var ccId in ccTargets)
                events.Add(new RoomEvent.TextMessage(0, req.From, req.Timestamp, ccText) { To = ccId });
        }

        return events;
    }

    private List<RoomEvent>? DeriveFromMessage(RoomEvent.ToolRequest? req)
    {
        if (req is null) return null;

        var to = Prop(req.Input, "to") ?? "";
        var text = Prop(req.Input, "text") ?? "";

        if (req.From == _leadId)
        {
            if (to is "user" or "client")
                return [new RoomEvent.TextMessage(0, req.From, req.Timestamp, text)];

            var targetId = NameToId(to);
            return [new RoomEvent.TextMessage(0, req.From, req.Timestamp, text) { To = targetId }];
        }

        var recipientId = to is "user" or "client" ? _leadId : NameToId(to);
        if (_dispatcherOf.TryGetValue(req.From, out var dispatcher) && recipientId == dispatcher)
            return [new RoomEvent.TextMessage(0, req.From, req.Timestamp, text) { To = dispatcher }];

        return [new RoomEvent.TextMessage(0, req.From, req.Timestamp, text) { To = recipientId }];
    }

    private RoomEvent? DeriveFromRecall(RoomEvent.ToolRequest? req)
    {
        if (req is null) return null;

        var name = Prop(req.Input, "agent_name") ?? "";
        var scoutId = NameToId(name);
        const string recallMessage = "You have been recalled. Report back immediately with whatever "
            + "you have uncovered thus far. Call conclude now.";
        return new RoomEvent.TextMessage(0, req.From, req.Timestamp, recallMessage) { To = scoutId };
    }

    private RoomEvent? DeriveFromDelegate(RoomEvent.ToolRequest? req, RoomEvent.ToolResponse tres)
    {
        if (req is null) return null;

        var task = Prop(req.Input, "task") ?? "";
        var agentName = ExtractAgentName(tres.Output);
        var agentId = NameToId(agentName);

        _dispatcherOf[agentId] = req.From;

        if (req.Input.TryGetProperty("cc", out var ccArray) && ccArray.ValueKind == JsonValueKind.Array)
        {
            var ccList = new List<string>();
            foreach (var item in ccArray.EnumerateArray())
                if (item.GetString() is { } ccName)
                    ccList.Add(NameToId(ccName));
            if (ccList.Count > 0)
                _ccTargetsOf[agentId] = ccList;
        }

        return new RoomEvent.TextMessage(0, "system", req.Timestamp, task) { To = agentId };
    }

    private static List<RoomEvent>? Wrap(RoomEvent? evt) =>
        evt is not null ? [evt] : null;

    private static string? Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string NameToId(string name) =>
        name.ToLowerInvariant().Replace(" ", "-");

    private static string ExtractAgentName(string output)
    {
        const string prefix = "Dispatched ";
        if (!output.StartsWith(prefix)) return output;
        var rest = output[prefix.Length..];
        var idx = rest.IndexOf(" (", StringComparison.Ordinal);
        return idx > 0 ? rest[..idx] : rest;
    }
}
