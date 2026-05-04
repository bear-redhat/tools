using System.Text.Json;
using System.Threading.Channels;
using Investigator.Models;

namespace Investigator.Services;

public sealed class TranscriptProjector
{
    private readonly string _leadId;
    private readonly Func<RoomEvent, ValueTask<int>> _emit;
    private readonly Dictionary<(string AgentId, string ToolUseId), int> _toolSeqMap = new();
    private readonly Dictionary<int, string> _seqToToolName = new();

    public TranscriptProjector(string leadId, Func<RoomEvent, ValueTask<int>> emit)
    {
        _leadId = leadId;
        _emit = emit;
    }

    public async Task RunLiveAsync(ChannelReader<RoomEvent> reader, CancellationToken ct)
    {
        await foreach (var evt in reader.ReadAllAsync(ct))
            await ProjectAsync(evt);
    }

    public async Task ReplayAsync(IEnumerable<RoomEvent> events)
    {
        foreach (var evt in events)
            await ProjectAsync(evt);
    }

    private async Task ProjectAsync(RoomEvent evt)
    {
        switch (evt)
        {
            case RoomEvent.ExternalInput input:
                await ProjectExternalInput(input);
                break;
            case RoomEvent.LlmContext ctx:
                await ProjectLlmContext(ctx);
                break;
            case RoomEvent.SessionEnded se:
                await _emit(se);
                break;
        }
    }

    private async Task ProjectExternalInput(RoomEvent.ExternalInput input)
    {
        await _emit(new RoomEvent.TextMessage(0, input.From, input.Timestamp, input.Text)
            { To = input.To });
    }

    private async Task ProjectLlmContext(RoomEvent.LlmContext ctx)
    {
        if (ctx.IsInboxBatch)
        {
            await _emit(new RoomEvent.AgentTurn(0, ctx.From, ctx.Timestamp,
                IsNewTurn: true,
                Usage: ctx.Usage,
                ModelProfile: ctx.ModelProfile,
                InputPrice: ctx.InputPrice,
                OutputPrice: ctx.OutputPrice,
                CacheReadPrice: ctx.CacheReadPrice,
                CacheCreatePrice: ctx.CacheCreatePrice));
            return;
        }

        foreach (var msg in ctx.Messages)
        {
            if (msg.Role == "assistant")
                await ProjectAssistantMessage(ctx, msg);
            else if (msg.Role == "user")
                await ProjectUserMessage(ctx, msg);
        }

        await _emit(new RoomEvent.AgentTurn(0, ctx.From, ctx.Timestamp,
            ThinkingText: ctx.ThinkingText,
            Usage: ctx.Usage,
            ModelProfile: ctx.ModelProfile,
            CompactedMessages: ctx.Removed,
            InputPrice: ctx.InputPrice,
            OutputPrice: ctx.OutputPrice,
            CacheReadPrice: ctx.CacheReadPrice,
            CacheCreatePrice: ctx.CacheCreatePrice));
    }

    private async Task ProjectAssistantMessage(RoomEvent.LlmContext ctx, LlmMessage msg)
    {
        if (msg.Content.ValueKind == JsonValueKind.String)
        {
            var text = msg.Content.GetString();
            if (!string.IsNullOrEmpty(text))
                await _emit(new RoomEvent.TextMessage(0, ctx.From, ctx.Timestamp, text));
            return;
        }

        if (msg.Content.ValueKind != JsonValueKind.Array) return;

        foreach (var block in msg.Content.EnumerateArray())
        {
            var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == "tool_use")
            {
                var id = block.TryGetProperty("id", out var idVal) ? idVal.GetString() ?? "" : "";
                var name = block.TryGetProperty("name", out var nameVal) ? nameVal.GetString() ?? "" : "";
                var input = block.TryGetProperty("input", out var inputVal) ? inputVal : default;
                var displayCmd = AgentRunner.FormatDisplayCommand(name, input);

                var seq = await _emit(new RoomEvent.ToolRequest(0, ctx.From, ctx.Timestamp,
                    name, input, displayCmd));
                _toolSeqMap[(ctx.From, id)] = seq;
                _seqToToolName[seq] = name;
            }
            else if (type == "text")
            {
                var text = block.TryGetProperty("text", out var textVal) ? textVal.GetString() : null;
                if (!string.IsNullOrEmpty(text))
                    await _emit(new RoomEvent.TextMessage(0, ctx.From, ctx.Timestamp, text));
            }
        }
    }

    private async Task ProjectUserMessage(RoomEvent.LlmContext ctx, LlmMessage msg)
    {
        if (msg.Content.ValueKind == JsonValueKind.String)
        {
            var text = msg.Content.GetString() ?? "";
            if (text.StartsWith("[system error]"))
                await _emit(new RoomEvent.TextMessage(0, "system", ctx.Timestamp, text));
            return;
        }

        if (msg.Content.ValueKind != JsonValueKind.Array) return;

        var toolMeta = (msg as LlmToolResultMessage)?.ToolMeta;

        foreach (var block in msg.Content.EnumerateArray())
        {
            var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == "tool_result")
            {
                var toolUseId = block.TryGetProperty("tool_use_id", out var idVal) ? idVal.GetString() ?? "" : "";
                var content = block.TryGetProperty("content", out var cVal) ? cVal.GetString() ?? "" : "";
                var meta = toolMeta?.FirstOrDefault(m => m.ToolUseId == toolUseId);

                var requestSeq = _toolSeqMap.TryGetValue((ctx.From, toolUseId), out var rs) ? rs : 0;
                _toolSeqMap.Remove((ctx.From, toolUseId));

                var toolName = ResolveToolName(requestSeq);

                await _emit(new RoomEvent.ToolResponse(0, $"tool:{toolName}", ctx.Timestamp,
                    toolName, content, requestSeq,
                    ExitCode: meta?.ExitCode ?? 0,
                    OutputFile: meta?.OutputFile,
                    TimedOut: meta?.TimedOut ?? false,
                    Summary: meta?.Summary));
            }
        }
    }

    private string ResolveToolName(int requestSeq)
    {
        _seqToToolName.Remove(requestSeq, out var name);
        return name ?? "unknown";
    }
}
