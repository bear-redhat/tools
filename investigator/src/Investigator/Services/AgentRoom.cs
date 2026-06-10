using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Tools;

namespace Investigator.Services;

public abstract class AgentRoom
{
    private const string DelegateToolName = "delegate";

    protected static readonly JsonElement s_emptySchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {},
        "required": []
    }
    """).RootElement.Clone();

    protected readonly ConcurrentDictionary<string, AgentSlot> _agents = new();
    protected readonly JsonElement _delegateSchema;
    protected readonly ILlmClientFactory _llmFactory;
    protected readonly ToolRegistry _toolRegistry;
    protected readonly IReadOnlyList<string> _toolSections;
    protected readonly AgentOptions _agentOptions;
    protected readonly ILogger _logger;
    private readonly SubAgentCoordinator _subAgentCoordinator;
    private readonly ToolScope? _scope;

    protected string _workspacePath = "";
    protected string? _userId;
    protected string? _conversationId;
    protected TimeZoneInfo? _clientTimeZone;
    protected CancellationToken _ct;
    protected int _outputCounter;

    public RoomEventPipeline Pipeline { get; }
    public RoomEventBus Bus => Pipeline.Bus;
    public TranscriptStore TranscriptStore { get; }

    public abstract string LeadId { get; }
    public abstract string LeadName { get; }
    protected abstract string RoomName { get; }
    protected abstract string SubAgentLabel { get; }
    protected abstract string SubAgentExitMessage { get; }
    protected abstract string AllSubAgentsFinishedMessage { get; }

    protected abstract IReadOnlyList<ToolDefinition> BuildLeadTools();

    protected abstract Task<AgentRunner.ToolExecutionResult?> HandleRoomToolAsync(
        AgentSlot caller, AgentRunner.Config config,
        string toolName, JsonElement input, string stepId, CancellationToken ct);

    protected abstract bool IsAllowedWhenStoodDown(string toolName);
    protected abstract string? GetSummaryText(string toolName, JsonElement input);
    protected bool HasTerminalToolResult(RoomEvent.LlmContext ctx, IReadOnlySet<string> terminalTools)
    {
        foreach (var msg in ctx.Messages)
        {
            if (msg.Role != "assistant" || msg.Content.ValueKind != JsonValueKind.Array) continue;
            foreach (var block in msg.Content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_use"
                    && block.TryGetProperty("name", out var n) && terminalTools.Contains(n.GetString() ?? ""))
                    return true;
            }
        }

        return false;
    }

    protected AgentRoom(
        ILlmClientFactory llmFactory,
        ToolRegistry toolRegistry,
        AgentOptions agentOptions,
        RoomEventPipeline pipeline,
        TranscriptStore transcriptStore,
        ILogger logger,
        ToolScope? scope,
        SubAgentConfig subAgentConfig,
        JsonElement subAgentConcludeSchema)
    {
        _llmFactory = llmFactory;
        _toolRegistry = toolRegistry;
        _agentOptions = agentOptions;
        Pipeline = pipeline;
        TranscriptStore = transcriptStore;
        _logger = logger;
        _scope = scope;

        _toolSections = scope.HasValue
            ? toolRegistry.GetSystemPromptContributions(scope.Value)
            : toolRegistry.GetSystemPromptContributions();

        _delegateSchema = BuildDelegateSchema(subAgentConfig.Label);

        _subAgentCoordinator = new SubAgentCoordinator(
            subAgentConfig, _agents, llmFactory, toolRegistry, agentOptions,
            _toolSections, logger,
            RunAgentWithRouting, subAgentConcludeSchema, pipeline.Bus);
    }

    protected IReadOnlyList<ToolDefinition> GetRegistryToolDefinitions() =>
        _scope.HasValue
            ? _toolRegistry.GetToolDefinitions(_scope.Value)
            : _toolRegistry.GetToolDefinitions();

    protected void InitializeRoom(string workspacePath, CancellationToken ct,
        string? userId = null, string? conversationId = null, TimeZoneInfo? clientTimeZone = null)
    {
        _workspacePath = workspacePath;
        _ct = ct;
        _userId = userId;
        _conversationId = conversationId;
        _clientTimeZone = clientTimeZone;
        _subAgentCoordinator.WorkspacePath = workspacePath;
        _subAgentCoordinator.UserId = userId;
        _subAgentCoordinator.ConversationId = conversationId;
        _subAgentCoordinator.ClientTimeZone = clientTimeZone;
        _subAgentCoordinator.RegisterAgentName(LeadName);
    }

    public ValueTask PostUserMessageAsync(string text, CancellationToken ct)
    {
        TranscriptStore.Append(new RoomEvent.ExternalInput(0, "user", DateTimeOffset.UtcNow, text)
            { To = LeadId });
        return ValueTask.CompletedTask;
    }

    public Task RecallSubAgentAsync(string agentName)
    {
        if (!_agents.TryGetValue(agentName, out var slot) || slot.Id == LeadId) return Task.CompletedTask;
        if (slot.Idle || slot.Dismissed || slot.Recalled) return Task.CompletedTask;

        slot.Recalled = true;

        var message = $"Return to {RoomName} at once. Report back immediately with whatever "
            + "you have uncovered thus far. Call conclude now.";

        TranscriptStore.Append(new RoomEvent.ExternalInput(0, LeadId, DateTimeOffset.UtcNow, message)
            { To = slot.Id });
        return Task.CompletedTask;
    }

    public Task StandDownSubAgentAsync(string agentName)
    {
        if (!_agents.TryGetValue(agentName, out var slot) || slot.Id == LeadId) return Task.CompletedTask;

        slot.StoodDown = true;
        slot.CurrentToolCts?.Cancel();

        const string message = "Stand down at once. Your current tasks are abandoned. "
            + "Report back immediately with whatever you have gathered. "
            + "Call conclude now -- no further tool calls are permitted.";

        TranscriptStore.Append(new RoomEvent.ExternalInput(0, LeadId, DateTimeOffset.UtcNow, message)
            { To = slot.Id });
        return Task.CompletedTask;
    }

    internal async Task RunAgentWithRouting(AgentSlot slot, AgentRunner.Config config, CancellationToken ct,
        List<LlmMessage>? initialMessages = null)
    {
        var runner = new AgentRunner(_logger);
        var terminalTools = config.TerminalToolNames ?? new HashSet<string> { "conclude" };
        var concludeOnly = new HashSet<string> { "conclude" };

        ValueTask Store(RoomEvent.LlmContext ctx)
        {
            TranscriptStore.Append(ctx);
            if (ctx.IsInboxBatch)
                slot.Idle = false;
            else if (ctx.IsConcludedBatch || HasTerminalToolResult(ctx, terminalTools))
                slot.Idle = true;

            if (slot.Id != LeadId && HasTerminalToolResult(ctx, concludeOnly))
                slot.HasReported = true;

            return ValueTask.CompletedTask;
        }

        async Task<AgentRunner.ToolExecutionResult> ExecuteTool(string toolName, JsonElement input, string stepId, CancellationToken toolCt)
        {
            return await HandleToolExecution(slot, config, toolName, input, stepId, toolCt);
        }

        try
        {
            await runner.RunAsync(config, slot.Inbox!, Store, ExecuteTool, ct, initialMessages);
        }
        catch (OperationCanceledException) { _logger.LogDebug("Agent {Name} cancelled", config.Name); }
        finally
        {
            if (slot.Id != LeadId)
            {
                var removed = _agents.TryRemove(config.Name, out _);
                Bus.Unsubscribe(slot.Id);

                if (removed && !slot.Idle)
                {
                    TranscriptStore.Append(new RoomEvent.ExternalInput(0, config.Id, DateTimeOffset.UtcNow,
                        SubAgentExitMessage) { To = LeadId });
                }

                if (removed)
                {
                    var remainingSubs = _agents.Where(kv => kv.Value.Id != LeadId && !kv.Value.Dismissed).ToList();
                    if (remainingSubs.Count == 0
                        && _agents.TryGetValue(LeadName, out var lead)
                        && !lead.Idle)
                    {
                        _logger.LogInformation("Last {Label} {Name} finished, nudging {Lead}",
                            SubAgentLabel, config.Name, LeadName);
                        TranscriptStore.Append(new RoomEvent.ExternalInput(0, "system", DateTimeOffset.UtcNow,
                            AllSubAgentsFinishedMessage) { To = LeadId });
                    }
                }
            }
        }
    }

    protected async Task CleanupSubAgentsAsync()
    {
        var subAgents = _agents.Where(kv => kv.Value.Id != LeadId).ToList();
        foreach (var (_, s) in subAgents)
            Bus.Unsubscribe(s.Id);

        var subTasks = subAgents
            .Select(kv => kv.Value.RunTask)
            .Where(t => t is not null)
            .ToArray();
        if (subTasks.Length > 0)
        {
            try { await Task.WhenAll(subTasks!); }
            catch (Exception)
            {
                foreach (var t in subTasks!)
                {
                    if (t!.IsFaulted)
                        _logger.LogError(t.Exception, "{Label} task faulted", SubAgentLabel);
                }
            }
        }
    }

    private async Task<AgentRunner.ToolExecutionResult> HandleToolExecution(
        AgentSlot callerSlot, AgentRunner.Config callerConfig,
        string toolName, JsonElement input, string stepId, CancellationToken ct)
    {
        if (callerSlot.StoodDown && !IsAllowedWhenStoodDown(toolName))
        {
            return new AgentRunner.ToolExecutionResult(
                $"[Stood down] Further tasks are not permitted. Report to {LeadName} -- call conclude now.",
                ExitCode: -1);
        }

        if (toolName == DelegateToolName)
            return await _subAgentCoordinator.HandleDelegate(input, ct);

        var roomResult = await HandleRoomToolAsync(callerSlot, callerConfig, toolName, input, stepId, ct);
        if (roomResult is not null)
            return await TrySummarizeAsync(callerConfig, toolName, input, roomResult, ct);

        callerSlot.CurrentToolCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            return await HandleRegistryTool(callerConfig, toolName, input, stepId, callerSlot.CurrentToolCts.Token);
        }
        catch (OperationCanceledException) when (callerSlot.StoodDown)
        {
            return new AgentRunner.ToolExecutionResult(
                $"[Stood down] Tool execution was aborted. Report to {LeadName} -- call conclude now.",
                ExitCode: -1);
        }
        finally
        {
            callerSlot.CurrentToolCts = null;
        }
    }

    private async Task<AgentRunner.ToolExecutionResult> TrySummarizeAsync(
        AgentRunner.Config config, string toolName, JsonElement input,
        AgentRunner.ToolExecutionResult result, CancellationToken ct)
    {
        if (config.SummarizerClient is null) return result;

        var text = GetSummaryText(toolName, input);
        if (text is null) return result;

        try
        {
            var headline = await SummarizeOneLineAsync(config.SummarizerClient, text, ct);
            return result with { Summary = headline };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Summarization failed for {Tool}", toolName);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Summarization cancelled for {Tool}", toolName);
            return result;
        }
    }

    protected async Task<AgentRunner.ToolExecutionResult> HandleRegistryTool(
        AgentRunner.Config callerConfig, string toolName, JsonElement input,
        string stepId, CancellationToken ct)
    {
        var parentSeq = int.TryParse(stepId, out var ps) ? ps : (int?)null;

        string StartChild(string childTool, string command)
        {
            var childSeq = Pipeline.EmitAsync(new RoomEvent.ToolRequest(0, callerConfig.Id, DateTimeOffset.UtcNow,
                childTool, JsonSerializer.SerializeToElement(command),
                DisplayCommand: command, ParentSeq: parentSeq), _ct).AsTask().GetAwaiter().GetResult();
            return childSeq.ToString();
        }

        void CompleteChild(string childId, string childTool, string output, int exitCode, bool timedOut)
        {
            var reqSeq = int.TryParse(childId, out var r) ? r : 0;
            var task = Pipeline.EmitAsync(new RoomEvent.ToolResponse(0, $"tool:{childTool}", DateTimeOffset.UtcNow,
                childTool, output, RequestSeq: reqSeq, ExitCode: exitCode, TimedOut: timedOut,
                ParentSeq: parentSeq) { To = callerConfig.Id }, _ct);
            if (!task.IsCompletedSuccessfully)
                Task.Run(async () => { try { await task; } catch (Exception ex) { _logger.LogWarning(ex, "CompleteChild emit failed for {Tool}", childTool); } });
        }

        var context = new ToolContext(
            _logger,
            callerConfig.WorkspacePath,
            line => _logger.LogTrace("[{Agent}/{Tool}] {Line}", callerConfig.Name, toolName, line),
            () => Interlocked.Increment(ref _outputCounter),
            callerConfig.Name,
            StartChild,
            CompleteChild);

        var (result, outFile, truncated) = await _toolRegistry.InvokeAsync(toolName, input, context, ct);

        if (result.ExitCode != 0)
            _logger.LogWarning("Tool {Tool} for agent {Agent} exited with code {ExitCode}", toolName, callerConfig.Name, result.ExitCode);
        if (result.TimedOut)
            _logger.LogWarning("Tool {Tool} for agent {Agent} timed out", toolName, callerConfig.Name);

        return new AgentRunner.ToolExecutionResult(
            Output: truncated,
            OutputFile: outFile,
            ExitCode: result.ExitCode,
            TimedOut: result.TimedOut);
    }

    protected static async Task<string> SummarizeOneLineAsync(ILlmClient client, string text, CancellationToken ct)
    {
        var messages = new List<LlmMessage> { new() { Role = "user", Content = JsonSerializer.SerializeToElement(text) } };
        IReadOnlyList<ToolDefinition> noTools = [];
        var sb = new System.Text.StringBuilder();
        await foreach (var block in client.StreamMessageAsync(messages, noTools,
            "Summarise the following in one sentence, max 100 characters. Output only the summary, nothing else.", ct))
        {
            if (block.Type == "text" && block.Text is not null) sb.Append(block.Text);
        }
        return sb.ToString().Trim();
    }

    private JsonElement BuildDelegateSchema(string subAgentLabel)
    {
        var modelNames = _llmFactory.Models.Keys.ToList();
        var modelDesc = modelNames.Count > 1
            ? $"Optional model profile for this {subAgentLabel}. Available: {string.Join(", ", modelNames)}. Omit to use the default ({_llmFactory.DefaultProfileName})."
            : "Model profile (only one configured, typically omitted).";

        var schema = $$"""
        {
            "type": "object",
            "properties": {
                "role": { "type": "string", "description": "Brief role description" },
                "task": { "type": "string", "description": "Specific task to perform. Be precise about what to investigate and what to report back." },
                "model": { "type": "string", "description": "{{modelDesc}}" }
            },
            "required": ["role", "task"]
        }
        """;
        return JsonDocument.Parse(schema).RootElement.Clone();
    }

    public sealed class AgentSlot
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Role { get; init; }
        public ChannelReader<RoomEvent>? Inbox { get; set; }
        public Task? RunTask { get; set; }

        private volatile bool _idle;
        public bool Idle
        {
            get => _idle;
            set => _idle = value;
        }

        public volatile bool StoodDown;
        public volatile bool Dismissed;
        public volatile bool Recalled;
        public volatile bool HasReported;
        public CancellationTokenSource? CurrentToolCts;
    }
}
