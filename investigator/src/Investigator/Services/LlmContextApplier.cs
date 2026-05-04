using System.Text.Json;
using Investigator.Models;

namespace Investigator.Services;

public static class LlmContextApplier
{
    public static List<LlmMessage> Replay(IEnumerable<RoomEvent> events, string leadId)
    {
        var messages = new List<LlmMessage>();
        foreach (var evt in events)
        {
            if (evt is RoomEvent.LlmContext ctx && ctx.From == leadId)
            {
                if (ctx.Removed > 0)
                    messages.RemoveRange(0, Math.Min(ctx.Removed, messages.Count));
                messages.AddRange(ctx.Messages);
            }
            else if (evt is RoomEvent.ExternalInput input && input.To == leadId)
            {
                var msg = new LlmMessage { Role = "user", Content = JsonSerializer.SerializeToElement(input.Text) };
                messages.Add(msg);
            }
        }
        return messages;
    }
}
