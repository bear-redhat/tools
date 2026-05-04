using System.Text.Json;
using Investigator.Models;

namespace Investigator.Services;

public sealed class ToolEffectEnricher : IEventEnricher
{
    private readonly string _leadId;
    private readonly Dictionary<int, RoomEvent.ToolRequest> _pending = new();

    public ToolEffectEnricher(string leadId) => _leadId = leadId;

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

        var derived = tres.Tool switch
        {
            "conclude" when req?.From != _leadId => DeriveFromConclude(req!),
            "message" => DeriveFromMessage(req),
            "recall" => DeriveFromRecall(req),
            "delegate" => DeriveFromDelegate(req, tres),
            _ => null,
        };

        return ValueTask.FromResult<IReadOnlyList<RoomEvent>>(
            derived is not null ? [derived] : []);
    }

    private RoomEvent DeriveFromConclude(RoomEvent.ToolRequest req)
    {
        var summary = Prop(req.Input, "summary") ?? "";
        return new RoomEvent.TextMessage(0, req.From, req.Timestamp, summary) { To = _leadId };
    }

    private RoomEvent? DeriveFromMessage(RoomEvent.ToolRequest? req)
    {
        if (req is null) return null;

        var to = Prop(req.Input, "to") ?? "";
        var text = Prop(req.Input, "text") ?? "";

        if (req.From == _leadId)
        {
            if (to is "user" or "client")
                return new RoomEvent.TextMessage(0, req.From, req.Timestamp, text);

            var scoutId = NameToId(to);
            return new RoomEvent.TextMessage(0, req.From, req.Timestamp, text) { To = scoutId };
        }

        return new RoomEvent.TextMessage(0, req.From, req.Timestamp, text) { To = _leadId };
    }

    private RoomEvent? DeriveFromRecall(RoomEvent.ToolRequest? req)
    {
        if (req is null) return null;

        var name = Prop(req.Input, "agent_name") ?? "";
        var scoutId = NameToId(name);
        const string recallMessage = "You have been recalled. Report back immediately with whatever "
            + "you have uncovered thus far. Call conclude now.";
        return new RoomEvent.TextMessage(0, _leadId, req.Timestamp, recallMessage) { To = scoutId };
    }

    private RoomEvent? DeriveFromDelegate(RoomEvent.ToolRequest? req, RoomEvent.ToolResponse tres)
    {
        if (req is null) return null;

        var task = Prop(req.Input, "task") ?? "";
        var scoutName = ExtractAgentName(tres.Output);
        var scoutId = NameToId(scoutName);
        return new RoomEvent.TextMessage(0, "system", req.Timestamp, task) { To = scoutId };
    }

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
