using System.Text.Json;
using System.Threading.Channels;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Investigator.Tests;

/// <summary>
/// Scripted <see cref="ILlmClient"/>. Each queued turn is either a list of content blocks
/// to yield or an exception to throw, so a test can drive the runner through a precise
/// sequence of model responses.
/// </summary>
internal sealed class ScriptedLlmClient : ILlmClient
{
    private readonly Queue<object> _turns = new();

    public int CallCount { get; private set; }
    public List<int?> ThinkingBudgets { get; } = [];
    public List<int> MessageCountAtCall { get; } = [];

    public ScriptedLlmClient Returns(params ContentBlock[] blocks)
    {
        _turns.Enqueue(blocks.ToList());
        return this;
    }

    public ScriptedLlmClient Throws(Exception ex)
    {
        _turns.Enqueue(ex);
        return this;
    }

    /// <summary>Every turn after the script runs dry throws, so a runaway loop fails fast.</summary>
    public async IAsyncEnumerable<ContentBlock> StreamMessageAsync(
        List<LlmMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        string? systemPrompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
        int? thinkingBudgetOverride = null,
        LlmRequestContext? context = null)
    {
        CallCount++;
        ThinkingBudgets.Add(thinkingBudgetOverride);
        MessageCountAtCall.Add(messages.Count);

        if (_turns.Count == 0)
            throw new InvalidOperationException($"ScriptedLlmClient exhausted after {CallCount} calls");

        var turn = _turns.Dequeue();
        if (turn is Exception ex)
            throw ex;

        foreach (var block in (List<ContentBlock>)turn)
        {
            ct.ThrowIfCancellationRequested();
            yield return block;
            await Task.Yield();
        }
    }
}

internal sealed record ToolCall(string Name, JsonElement Input, string StepId);

/// <summary>
/// Wires an <see cref="AgentRunner"/> to a scripted model, a real inbox channel, and
/// recording <c>store</c> / <c>executeTool</c> delegates.
/// </summary>
internal sealed class AgentRunnerHarness
{
    private readonly Channel<RoomEvent> _inbox = Channel.CreateUnbounded<RoomEvent>();
    private readonly Dictionary<string, Func<AgentRunner.ToolExecutionResult>> _toolResults = new();

    public ScriptedLlmClient Llm { get; } = new();
    public List<ToolCall> ToolCalls { get; } = [];
    public List<RoomEvent.LlmContext> Stored { get; } = [];

    /// <summary>The runner's live message list, captured so tests can assert final ordering.</summary>
    public List<LlmMessage> Messages { get; } = [];

    public Func<string, JsonElement, string, Task>? OnToolCall { get; set; }
    public Func<bool>? ShouldSuppressNextTurn { get; set; }
    public Func<string, JsonElement, bool>? IsConditionallyTerminal { get; set; }
    public int MaxToolCalls { get; set; } = 20;

    public void SetToolResult(string toolName, AgentRunner.ToolExecutionResult result) =>
        _toolResults[toolName] = () => result;

    /// <summary>
    /// Writes to the inbox. Throws rather than no-opping if the channel is already
    /// completed -- a silently dropped message makes a test pass for the wrong reason.
    /// </summary>
    public void Send(string text, string from = "user")
    {
        if (!_inbox.Writer.TryWrite(new RoomEvent.TextMessage(0, from, DateTimeOffset.UtcNow, text)))
            throw new InvalidOperationException($"inbox closed; message '{text}' was dropped");
    }

    /// <summary>Closes the inbox so the runner's outer loop exits once the turn completes.</summary>
    public void CloseInbox() => _inbox.Writer.TryComplete();

    public async Task RunAsync(TimeSpan? timeout = null)
    {
        var runner = new AgentRunner(NullLogger.Instance);
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));

        var config = new AgentRunner.Config(
            Id: "agent-1",
            Name: "Test Agent",
            Role: "tester",
            SystemPrompt: "system",
            LlmClient: Llm,
            Tools: [],
            MaxToolCalls: MaxToolCalls,
            MaxRetries: 0,
            WorkspacePath: "/tmp",
            CompactionMaxTokens: 1_000_000,
            IsConditionallyTerminal: IsConditionallyTerminal,
            ShouldSuppressNextTurn: ShouldSuppressNextTurn);

        await runner.RunAsync(
            config,
            _inbox.Reader,
            ctx => { Stored.Add(ctx); return ValueTask.CompletedTask; },
            async (name, input, stepId, ct) =>
            {
                ToolCalls.Add(new ToolCall(name, input, stepId));
                if (OnToolCall is not null)
                    await OnToolCall(name, input, stepId);
                return _toolResults.TryGetValue(name, out var factory)
                    ? factory()
                    : new AgentRunner.ToolExecutionResult($"{name} ok");
            },
            cts.Token,
            Messages);
    }

    // -- content block builders -------------------------------------------------

    public static ContentBlock Text(string text) => new() { Type = "text", Text = text };

    public static ContentBlock ToolUse(string name, string id, string inputJson = "{}") => new()
    {
        Type = "tool_use",
        Name = name,
        Id = id,
        Input = JsonDocument.Parse(inputJson).RootElement.Clone(),
    };

    public static ContentBlock TruncatedToolUse(string name, string id) => new()
    {
        Type = "tool_use",
        Name = name,
        Id = id,
        Truncated = true,
    };

    public static ContentBlock Usage(int input = 100, int output = 50) => new()
    {
        Type = "usage",
        Usage = new UsageInfo { InputTokens = input, OutputTokens = output },
    };

    // -- assertion helpers ------------------------------------------------------

    /// <summary>Flattens every stored context batch into one ordered message list.</summary>
    public List<LlmMessage> StoredMessages() =>
        Stored.SelectMany(s => s.Messages).ToList();

    public static string ContentText(LlmMessage msg) =>
        msg.Content.ValueKind == JsonValueKind.String
            ? msg.Content.GetString() ?? ""
            : msg.Content.GetRawText();
}
