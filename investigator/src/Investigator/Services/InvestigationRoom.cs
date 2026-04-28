using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Tools;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public sealed class InvestigationRoom
{
    private const string ConcludeToolName = "conclude";
    private const string DelegateToolName = "delegate";
    private const string CheckAgentsToolName = "check_agents";
    private const string PresentFindingToolName = "present_finding";
    private const string ReplyToToolName = "reply_to";
    private const string DismissScoutToolName = "dismiss_scout";
    private const string RecallScoutToolName = "recall_scout";

    private static readonly JsonElement s_emptySchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {},
        "required": []
    }
    """).RootElement.Clone();

    private static readonly JsonElement s_presentFindingSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "title": { "type": "string", "description": "Brief title for the finding" },
            "description": { "type": "string", "description": "What was found and why it matters" }
        },
        "required": ["title", "description"]
    }
    """).RootElement.Clone();

    private static readonly JsonElement s_replyToSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "agent_name": { "type": "string", "description": "Name of the Scout to reply to" },
            "message": { "type": "string", "description": "Your answer" }
        },
        "required": ["agent_name", "message"]
    }
    """).RootElement.Clone();

    private static readonly JsonElement s_dismissScoutSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "agent_name": { "type": "string", "description": "Name of the Scout to dismiss" }
        },
        "required": ["agent_name"]
    }
    """).RootElement.Clone();

    private static readonly JsonElement s_recallScoutSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "agent_name": { "type": "string", "description": "Name of the Scout to recall" }
        },
        "required": ["agent_name"]
    }
    """).RootElement.Clone();

    private static readonly JsonElement s_concludeSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "summary": { "type": "string", "description": "Concise root cause summary in plain text" },
            "evidence": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "step": { "type": "integer", "description": "Position in the logical chain (1 = starting point of the reasoning)" },
                        "reasoning": { "type": "string" },
                        "finding": { "type": "string" },
                        "cluster": { "type": "string" },
                        "command": { "type": "string", "description": "The command run (if any) and the raw log line(s) or output that support this step, copied verbatim. Command on the first line, raw output below it." },
                        "source": { "type": "string", "description": "Log file path or URL with optional :line suffix, e.g. 'build.log:142' or 'https://prow.ci/build-log.txt:307'" }
                    }
                },
                "description": "Ordered chain of proof. Each step must logically connect to the next -- forward (observation to cause) or reverse (symptom to origin). Do NOT submit unrelated findings as a flat list."
            },
            "fix_description": { "type": "string" },
            "fix_commands": { "type": "array", "items": { "type": "string" } },
            "fix_warning": { "type": "string" }
        },
        "required": ["summary", "evidence"]
    }
    """).RootElement.Clone();

    private readonly Channel<AgentEvent> _uiEvents = Channel.CreateUnbounded<AgentEvent>();
    private readonly ConcurrentDictionary<string, AgentSlot> _agents = new();
    private readonly JsonElement _delegateSchema;

    private readonly ILlmClientFactory _llmFactory;
    private readonly ToolRegistry _toolRegistry;
    private readonly IReadOnlyList<string> _toolSections;
    private readonly WorkspaceManager _workspaceManager;
    private readonly AgentOptions _agentOptions;
    private readonly ILogger<InvestigationRoom> _logger;

    private readonly RoomToolHandlers _roomToolHandlers;
    private readonly ScoutCoordinator _scoutCoordinator;

    private string _workspacePath = "";
    private string? _userId;
    private string? _conversationId;
    private CancellationToken _ct;
    private int _outputCounter;

    public ChannelReader<AgentEvent> Events => _uiEvents.Reader;

    public InvestigationRoom(
        ILlmClientFactory llmFactory,
        ToolRegistry toolRegistry,
        WorkspaceManager workspaceManager,
        IOptions<AgentOptions> agentOptions,
        ILogger<InvestigationRoom> logger)
    {
        _llmFactory = llmFactory;
        _toolRegistry = toolRegistry;
        _toolSections = toolRegistry.GetSystemPromptContributions();
        _workspaceManager = workspaceManager;
        _agentOptions = agentOptions.Value;
        _logger = logger;

        _delegateSchema = BuildDelegateSchema();

        _roomToolHandlers = new RoomToolHandlers(_agents, _workspaceManager, _logger, EmitToUi);
        _scoutCoordinator = new ScoutCoordinator(
            _agents, _llmFactory, _toolRegistry, _agentOptions,
            _toolSections, _logger,
            EmitToUi, RunAgentWithRouting, s_concludeSchema);
    }

    public async Task StartAsync(string workspacePath, IReadOnlyList<ChatMessage> history, CancellationToken ct,
        string? userId = null, string? conversationId = null)
    {
        _workspacePath = workspacePath;
        _ct = ct;
        _userId = userId;
        _conversationId = conversationId;
        _scoutCoordinator.WorkspacePath = workspacePath;
        _scoutCoordinator.UserId = userId;
        _scoutCoordinator.ConversationId = conversationId;

        var littleBearSlot = new AgentSlot
        {
            Id = "little-bear",
            Name = "Little Bear",
            Role = "lead detective",
        };
        _agents["Little Bear"] = littleBearSlot;
        _scoutCoordinator.RegisterAgentName("Little Bear");

        var primaryOptions = _llmFactory.GetModelOptions(_llmFactory.PrimaryProfileName);
        var summarizerProfile = _llmFactory.DefaultProfileName;
        var summarizerOptions = _llmFactory.GetModelOptions(summarizerProfile);
        var runnerConfig = new AgentRunner.Config(
            Id: "little-bear",
            Name: "Little Bear",
            Role: "lead detective",
            SystemPrompt: InvestigationPrompts.BuildSystemPrompt(
                _toolSections, workspacePath,
                _llmFactory.Models, _llmFactory.DefaultProfileName),
            LlmClient: _llmFactory.GetClient(_llmFactory.PrimaryProfileName),
            Tools: BuildLittleBearTools(),
            InitialMessages: BuildInitialMessages(history),
            MaxToolCalls: _agentOptions.MaxToolCalls,
            MaxRetries: _agentOptions.LlmRetries,
            WorkspacePath: workspacePath,
            CompactionMaxTokens: primaryOptions.MaxTokens * 4,
            ThinkingBudget: primaryOptions.ThinkingBudget,
            ContextWindowTokens: primaryOptions.ContextWindowTokens,
            ModelProfile: _llmFactory.PrimaryProfileName,
            InputPricePerMToken: primaryOptions.InputPricePerMToken,
            OutputPricePerMToken: primaryOptions.OutputPricePerMToken,
            CacheReadPricePerMToken: primaryOptions.CacheReadPricePerMToken,
            CacheCreationPricePerMToken: primaryOptions.CacheCreationPricePerMToken,
            UserId: userId,
            ConversationId: conversationId,
            SummarizerClient: _llmFactory.GetClient(summarizerProfile),
            SummarizerModelOptions: summarizerOptions,
            IsConcluded: () => littleBearSlot.Concluded);

        littleBearSlot.RunTask = RunAgentWithRouting(littleBearSlot, runnerConfig, ct);

        try
        {
            await littleBearSlot.RunTask;
        }
        finally
        {
            _uiEvents.Writer.TryComplete();
        }
    }

    public async Task RecallScoutAsync(string scoutName)
    {
        if (!_agents.TryGetValue(scoutName, out var slot) || slot.Id == "little-bear") return;

        const string message = "Return to Banyan Row at once. Report back immediately with whatever "
            + "you have uncovered thus far. Call conclude now.";

        await slot.Inbox.Writer.WriteAsync(
            new RoomMessage("Little Bear", message),
            CancellationToken.None);

        await EmitToUi(new AgentEvent.Message($"recall-{slot.Id}", message, Recipient: scoutName));
    }

    public async Task StandDownScoutAsync(string scoutName)
    {
        if (!_agents.TryGetValue(scoutName, out var slot) || slot.Id == "little-bear") return;

        slot.StoodDown = true;
        slot.CurrentToolCts?.Cancel();

        const string message = "Stand down at once. Your current inquiries are abandoned. "
            + "Report back immediately with whatever evidence you have gathered. "
            + "Call conclude now -- no further tool calls are permitted.";

        await slot.Inbox.Writer.WriteAsync(
            new RoomMessage("Little Bear", message),
            CancellationToken.None);

        await EmitToUi(new AgentEvent.Message($"standdown-{slot.Id}", message, Recipient: scoutName));
    }

    public ValueTask PostUserMessageAsync(string text, CancellationToken ct)
    {
        if (_agents.TryGetValue("Little Bear", out var slot))
        {
            slot.Concluded = false;
            return slot.Inbox.Writer.WriteAsync(new RoomMessage("user", text), ct);
        }
        return ValueTask.CompletedTask;
    }

    private async Task RunAgentWithRouting(AgentSlot slot, AgentRunner.Config config, CancellationToken ct)
    {
        var runner = new AgentRunner(_logger);

        async ValueTask Emit(AgentEvent evt)
        {
            if (slot.Id == "little-bear")
            {
                await EmitToUi(evt);
                await WriteTranscript(evt, config.Name);
            }
            else
            {
                await EmitScoutEvent(evt, config.Name);
            }
        }

        async Task<AgentRunner.ToolExecutionResult> ExecuteTool(string toolName, JsonElement input, string stepId, CancellationToken toolCt)
        {
            return await HandleToolExecution(slot, config, toolName, input, stepId, Emit, toolCt);
        }

        try
        {
            await runner.RunAsync(config, slot.Inbox.Reader, Emit, ExecuteTool, ct);
        }
        catch (OperationCanceledException) { _logger.LogDebug("Agent {Name} cancelled", config.Name); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent {Name} failed unexpectedly", config.Name);
            await EmitToUi(new AgentEvent.Error($"err-{config.Name}", $"Agent {config.Name} failed: {ex.Message}"));
        }
        finally
        {
            if (slot.Id != "little-bear")
            {
                var removed = _agents.TryRemove(config.Name, out _);

                if (removed && !slot.Concluded)
                {
                    await EmitToUi(new AgentEvent.SubAgentFailed(
                        $"sa-{config.Name}-fail", config.Name,
                        "Scout exited without reporting."));
                }

                if (removed)
                {
                    var remainingScouts = _agents.Where(kv => kv.Value.Id != "little-bear").ToList();
                    if (remainingScouts.Count == 0
                        && _agents.TryGetValue("Little Bear", out var lb)
                        && !lb.Concluded)
                    {
                        _logger.LogInformation("Last scout {Name} finished, nudging Little Bear to conclude", config.Name);
                        await lb.Inbox.Writer.WriteAsync(
                            new RoomMessage("system", "All Scouts have reported back. Conclude now with the evidence you have."),
                            CancellationToken.None);
                    }
                }
            }
        }
    }

    private async Task<AgentRunner.ToolExecutionResult> HandleToolExecution(
        AgentSlot callerSlot, AgentRunner.Config callerConfig,
        string toolName, JsonElement input, string stepId,
        Func<AgentEvent, ValueTask> emit, CancellationToken ct)
    {
        if (callerSlot.StoodDown && toolName != ConcludeToolName)
        {
            return new AgentRunner.ToolExecutionResult(
                "[Stood down] Further inquiries are not permitted. Report to Little Bear -- call conclude now.",
                ExitCode: -1);
        }

        if (toolName == ConcludeToolName)
            return await _roomToolHandlers.HandleConclude(callerSlot, callerConfig, input, _workspacePath, ct);

        if (toolName == DelegateToolName)
            return await _scoutCoordinator.HandleDelegate(input, ct);

        if (toolName == CheckAgentsToolName)
        {
            var response = _roomToolHandlers.BuildCheckAgentsResponse();
            if (_roomToolHandlers.HasActiveScouts())
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                response += "\n\nScouts are still at work. Do NOT call check_agents again -- end your turn with text only and wait patiently. You will be woken automatically when a Scout reports back.";
            }
            return new AgentRunner.ToolExecutionResult(response);
        }

        if (toolName == PresentFindingToolName)
            return await _roomToolHandlers.HandlePresentFinding(callerSlot, input);

        if (toolName == ReplyToToolName)
            return await _roomToolHandlers.HandleReplyTo(callerSlot, input, ct);

        if (toolName == DismissScoutToolName)
            return _roomToolHandlers.HandleDismissScout(input);

        if (toolName == RecallScoutToolName)
            return await _roomToolHandlers.HandleRecallScout(input);

        callerSlot.CurrentToolCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            return await HandleRegistryTool(callerConfig, toolName, input, stepId, emit, callerSlot.CurrentToolCts.Token);
        }
        finally
        {
            callerSlot.CurrentToolCts = null;
        }
    }

    private async Task<AgentRunner.ToolExecutionResult> HandleRegistryTool(
        AgentRunner.Config callerConfig, string toolName, JsonElement input,
        string stepId, Func<AgentEvent, ValueTask> emit, CancellationToken ct)
    {
        var childCounter = 0;

        string StartChild(string childTool, string command)
        {
            var childId = $"{stepId}-child-{Interlocked.Increment(ref childCounter)}";
            _ = emit(new AgentEvent.ToolCall(childId, childTool, command, default, ParentStepId: stepId));
            return childId;
        }

        void CompleteChild(string childId, string childTool, string output, int exitCode, bool timedOut)
        {
            _ = emit(new AgentEvent.ToolResult(childId, childTool, output, null, exitCode, timedOut, ParentStepId: stepId));
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

        await _workspaceManager.AppendTranscriptAsync(_workspacePath, new
        {
            ts = DateTimeOffset.UtcNow,
            type = "tool_call",
            agent = callerConfig.Name,
            tool = toolName,
            exitCode = result.ExitCode,
            timedOut = result.TimedOut,
            outputFile = outFile,
        });

        return new AgentRunner.ToolExecutionResult(
            Output: truncated,
            OutputFile: outFile,
            ExitCode: result.ExitCode,
            TimedOut: result.TimedOut);
    }

    private ValueTask EmitToUi(AgentEvent evt)
    {
        if (_uiEvents.Writer.TryWrite(evt))
            return ValueTask.CompletedTask;

        _logger.LogWarning("UI event channel closed, dropping {EventType} for step {StepId}", evt.GetType().Name, evt.StepId);
        return ValueTask.CompletedTask;
    }

    private async ValueTask EmitScoutEvent(AgentEvent evt, string agentName)
    {
        var mapped = evt switch
        {
            AgentEvent.Thinking t => (AgentEvent)new AgentEvent.SubAgentThinking(t.StepId, agentName, t.Text),
            AgentEvent.ToolCall tc => new AgentEvent.SubAgentToolCall(tc.StepId, agentName, tc.Tool, tc.DisplayCommand, AgentRunner.ExtractContext(tc.Tool, tc.Parameters)),
            AgentEvent.ToolResult tr => new AgentEvent.SubAgentToolResult(tr.StepId, agentName, tr.Tool, tr.Output, tr.ExitCode, tr.TimedOut),
            AgentEvent.Message m when m.IsIntermediate => new AgentEvent.SubAgentThinking(m.StepId, agentName, m.Text),
            AgentEvent.Message m => HandleScoutMessage(m, agentName),
            AgentEvent.StatusChanged sc => HandleScoutStatusChanged(sc, agentName),
            AgentEvent.Error e => new AgentEvent.Error(e.StepId, $"[{agentName}] {e.ErrorMessage}"),
            AgentEvent.Usage u => u,
            AgentEvent.Compaction c => c,
            _ => evt,
        };

        if (mapped is not null)
            await EmitToUi(mapped);
    }

    private AgentEvent? HandleScoutMessage(AgentEvent.Message m, string agentName)
    {
        if (string.IsNullOrWhiteSpace(m.Text)) return null;

        _logger.LogInformation("Scout {Name} asking Little Bear: {Question}", agentName, m.Text);

        if (_agents.TryGetValue("Little Bear", out var lbSlot))
        {
            lbSlot.Inbox.Writer.TryWrite(new RoomMessage(agentName, $"[enters and asks]: {m.Text}"));
        }

        return new AgentEvent.ScoutAsked($"sa-{agentName}-ask", agentName, m.Text);
    }

    private AgentEvent? HandleScoutStatusChanged(AgentEvent.StatusChanged sc, string agentName)
    {
        return null;
    }

    private async ValueTask WriteTranscript(AgentEvent evt, string agentName)
    {
        object? entry = evt switch
        {
            AgentEvent.Thinking t => new { ts = DateTimeOffset.UtcNow, type = "thinking", stepId = t.StepId, content = t.Text },
            AgentEvent.ToolCall tc => new { ts = DateTimeOffset.UtcNow, type = "tool_call", stepId = tc.StepId, tool = tc.Tool },
            AgentEvent.Message m => new { ts = DateTimeOffset.UtcNow, type = "message", stepId = m.StepId, content = m.Text },
            _ => null,
        };

        if (entry is not null)
            await _workspaceManager.AppendTranscriptAsync(_workspacePath, entry);
    }

    // --- Tool definition builders ---

    private IReadOnlyList<ToolDefinition> BuildLittleBearTools()
    {
        var tools = _toolRegistry.GetToolDefinitions().ToList();

        tools.Add(new ToolDefinition(
            Name: ConcludeToolName,
            Description: "Call this when the evidence has converged and you can state the root cause. Provide the summary, evidence chain, and suggested remedy.",
            ParameterSchema: s_concludeSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: DelegateToolName,
            Description: "Dispatch one of your Banyan Row Scouts. Non-blocking -- returns immediately with their assigned name. They investigate independently and report back as a message. Provide a role, task, and optionally a model profile.",
            ParameterSchema: _delegateSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: CheckAgentsToolName,
            Description: "Review the status of dispatched Scouts. NOT a polling tool -- call only if you have genuinely lost track of which Scouts are afield. You will be woken automatically when Scouts report; do not call this repeatedly to check.",
            ParameterSchema: s_emptySchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: PresentFindingToolName,
            Description: "Present a notable discovery to the room. The Client follows the investigation through these findings -- use this for significant clues, confirmed hypotheses, or important eliminations.",
            ParameterSchema: s_presentFindingSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: ReplyToToolName,
            Description: "Reply to a Scout who has entered the room with a question. Provide the name and your answer; they will resume their work.",
            ParameterSchema: s_replyToSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: DismissScoutToolName,
            Description: "Dismiss a Scout from the room once they have reported. They depart Banyan Row and cannot be contacted again. If the Scout is still abroad, use recall_scout to summon them back first.",
            ParameterSchema: s_dismissScoutSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: RecallScoutToolName,
            Description: "Recall a Scout to Banyan Row. They will report back immediately with whatever they have uncovered. Use when you no longer require their investigation or need their interim findings now.",
            ParameterSchema: s_recallScoutSchema,
            DefaultTimeout: TimeSpan.Zero));

        return tools;
    }

    private JsonElement BuildDelegateSchema()
    {
        var modelNames = _llmFactory.Models.Keys.ToList();
        var modelDesc = modelNames.Count > 1
            ? $"Optional model profile for this Scout. Available: {string.Join(", ", modelNames)}. Omit to use the default ({_llmFactory.DefaultProfileName})."
            : "Model profile (only one configured, typically omitted).";

        var schema = $$"""
        {
            "type": "object",
            "properties": {
                "role": { "type": "string", "description": "Brief role description, e.g. 'log analyst', 'cluster scout', 'config reviewer'" },
                "task": { "type": "string", "description": "Specific task to perform. Be precise about what to investigate and what to report back." },
                "model": { "type": "string", "description": "{{modelDesc}}" }
            },
            "required": ["role", "task"]
        }
        """;
        return JsonDocument.Parse(schema).RootElement.Clone();
    }

    private static List<LlmMessage> BuildInitialMessages(IReadOnlyList<ChatMessage> history)
    {
        var messages = new List<LlmMessage>();
        foreach (var msg in history)
        {
            messages.Add(new LlmMessage
            {
                Role = msg.Role == ChatRole.User ? "user" : "assistant",
                Content = JsonSerializer.SerializeToElement(msg.Content),
            });
        }
        return messages;
    }

    // --- Internal types ---

    internal sealed class AgentSlot
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Role { get; init; }
        public Channel<RoomMessage> Inbox { get; } = Channel.CreateUnbounded<RoomMessage>();
        public Task? RunTask { get; set; }

        private volatile bool _concluded;
        public bool Concluded
        {
            get => _concluded;
            set => _concluded = value;
        }

        public volatile bool StoodDown;
        public CancellationTokenSource? CurrentToolCts;
    }
}
