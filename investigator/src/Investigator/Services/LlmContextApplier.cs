using System.Text.Json;
using Investigator.Models;

namespace Investigator.Services;

public static class LlmContextApplier
{
    /// <summary>
    /// Rebuilds an agent's LLM message list from the persisted event log.
    ///
    /// An inbound message appears in the log twice: once as the <see cref="RoomEvent.ExternalInput"/>
    /// that delivered it, and again inside the agent's own inbox-batch
    /// <see cref="RoomEvent.LlmContext"/> once the runner drained it. Adding both duplicated
    /// every user message on resume. Dropping the ExternalInput outright is equally wrong --
    /// a message that arrived but was never drained (the pod died first) exists only as an
    /// ExternalInput, and discarding it silently loses what the operator said.
    ///
    /// So an ExternalInput is held back until a later inbox batch from this agent is seen to
    /// carry it; anything still unmatched at the end was genuinely never consumed and is
    /// appended so the resumed agent picks it up.
    /// </summary>
    public static List<LlmMessage> Replay(IEnumerable<RoomEvent> events, string leadId)
    {
        var messages = new List<LlmMessage>();
        var undelivered = new List<RoomEvent.ExternalInput>();

        foreach (var evt in events)
        {
            if (evt is RoomEvent.LlmContext ctx && ctx.From == leadId)
            {
                if (ctx.Removed > 0)
                {
                    messages.RemoveRange(0, Math.Min(ctx.Removed, messages.Count));

                    // Compaction dropped the head of the context. Anything still waiting to
                    // be delivered predates it and would resurface out of order.
                    undelivered.Clear();
                }

                if (ctx.IsInboxBatch)
                    foreach (var msg in ctx.Messages.OfType<LlmInboxMessage>())
                        Consume(undelivered, msg);

                messages.AddRange(ctx.Messages);
            }
            else if (evt is RoomEvent.ExternalInput input && input.To == leadId)
            {
                undelivered.Add(input);
            }
        }

        foreach (var input in undelivered)
            messages.Add(new LlmMessage
            {
                Role = "user",
                Content = JsonSerializer.SerializeToElement(input.Text),
            });

        return messages;
    }

    private static void Consume(List<RoomEvent.ExternalInput> undelivered, LlmInboxMessage delivered)
    {
        for (var i = 0; i < undelivered.Count; i++)
        {
            if (Matches(undelivered[i], delivered))
            {
                undelivered.RemoveAt(i);
                return;
            }
        }
    }

    private static bool Matches(RoomEvent.ExternalInput input, LlmInboxMessage delivered)
    {
        if (!string.Equals(input.From, delivered.SourceFrom, StringComparison.OrdinalIgnoreCase))
            return false;

        var text = delivered.Content.ValueKind == JsonValueKind.String
            ? delivered.Content.GetString()
            : null;
        if (text is null) return false;

        // Messages from anyone other than the user are rendered as "[sender]: text" on the
        // way into the agent's context, so compare on the tail rather than the whole string.
        return text == input.Text || text.EndsWith(input.Text, StringComparison.Ordinal);
    }
}
