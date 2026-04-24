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
            "title": { "type": "string", "description": "Short title for the finding" },
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
                        "step": { "type": "integer" },
                        "reasoning": { "type": "string" },
                        "finding": { "type": "string" },
                        "cluster": { "type": "string" },
                        "command": { "type": "string" }
                    }
                },
                "description": "Only the commands that form the logical proof of the root cause"
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
    private readonly OcExecutor _ocExecutor;
    private readonly ShellExecutor _shellExecutor;
    private readonly WorkspaceManager _workspaceManager;
    private readonly AgentOptions _agentOptions;
    private readonly ILogger<InvestigationRoom> _logger;

    private readonly RoomToolHandlers _roomToolHandlers;
    private readonly ScoutCoordinator _scoutCoordinator;

    private string _workspacePath = "";
    private CancellationToken _ct;
    private int _outputCounter;

    public ChannelReader<AgentEvent> Events => _uiEvents.Reader;

    public InvestigationRoom(
        ILlmClientFactory llmFactory,
        ToolRegistry toolRegistry,
        OcExecutor ocExecutor,
        ShellExecutor shellExecutor,
        WorkspaceManager workspaceManager,
        IOptions<AgentOptions> agentOptions,
        ILogger<InvestigationRoom> logger)
    {
        _llmFactory = llmFactory;
        _toolRegistry = toolRegistry;
        _ocExecutor = ocExecutor;
        _shellExecutor = shellExecutor;
        _workspaceManager = workspaceManager;
        _agentOptions = agentOptions.Value;
        _logger = logger;

        _delegateSchema = BuildDelegateSchema();

        _roomToolHandlers = new RoomToolHandlers(_agents, _workspaceManager, _logger, EmitToUi);
        _scoutCoordinator = new ScoutCoordinator(
            _agents, _llmFactory, _toolRegistry, _agentOptions,
            _shellExecutor.IsPowerShell, _logger,
            EmitToUi, RunAgentWithRouting, s_concludeSchema);
    }

    public async Task StartAsync(string workspacePath, IReadOnlyList<ChatMessage> history, CancellationToken ct)
    {
        _workspacePath = workspacePath;
        _ct = ct;
        _scoutCoordinator.WorkspacePath = workspacePath;

        var clusters = _ocExecutor.ListClusters();
        if (clusters.Count == 0)
            _logger.LogWarning("No clusters registered; agent will have no clusters to investigate");

        var littleBearSlot = new AgentSlot
        {
            Id = "little-bear",
            Name = "Little Bear",
            Role = "lead detective",
        };
        _agents["Little Bear"] = littleBearSlot;
        _scoutCoordinator.RegisterAgentName("Little Bear");

        var primaryOptions = _llmFactory.GetModelOptions(_llmFactory.PrimaryProfileName);
        var runnerConfig = new AgentRunner.Config(
            Id: "little-bear",
            Name: "Little Bear",
            Role: "lead detective",
            SystemPrompt: InvestigationPrompts.BuildSystemPrompt(
                clusters, workspacePath, _shellExecutor.IsPowerShell,
                _llmFactory.Models, _llmFactory.DefaultProfileName),
            LlmClient: _llmFactory.GetClient(_llmFactory.PrimaryProfileName),
            Tools: BuildLittleBearTools(),
            InitialMessages: BuildInitialMessages(history),
            MaxToolCalls: _agentOptions.MaxToolCalls,
            MaxRetries: _agentOptions.LlmRetries,
            WorkspacePath: workspacePath,
            CompactionMaxTokens: primaryOptions.MaxTokens * 4);

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

    public ValueTask PostUserMessageAsync(string text, CancellationToken ct)
    {
        if (_agents.TryGetValue("Little Bear", out var slot))
            return slot.Inbox.Writer.WriteAsync(new RoomMessage("user", text), ct);
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

        async Task<AgentRunner.ToolExecutionResult> ExecuteTool(string toolName, JsonElement input, CancellationToken toolCt)
        {
            return await HandleToolExecution(slot, config, toolName, input, toolCt);
        }

        try
        {
            await runner.RunAsync(config, slot.Inbox.Reader, Emit, ExecuteTool, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent {Name} failed unexpectedly", config.Name);
            await EmitToUi(new AgentEvent.Error($"err-{config.Name}", $"Agent {config.Name} failed: {ex.Message}"));
        }
        finally
        {
            if (slot.Id != "little-bear")
            {
                _agents.TryRemove(config.Name, out _);

                var remainingScouts = _agents.Where(kv => kv.Value.Id != "little-bear").ToList();
                if (remainingScouts.Count == 0 && _agents.TryGetValue("Little Bear", out var lb))
                {
                    _logger.LogInformation("Last scout {Name} finished, nudging Little Bear to conclude", config.Name);
                    await lb.Inbox.Writer.WriteAsync(
                        new RoomMessage("system", "All scouts have reported back. Conclude now with the evidence you have."),
                        CancellationToken.None);
                }
            }
        }
    }

    private async Task<AgentRunner.ToolExecutionResult> HandleToolExecution(
        AgentSlot callerSlot, AgentRunner.Config callerConfig,
        string toolName, JsonElement input, CancellationToken ct)
    {
        if (toolName == ConcludeToolName)
            return await _roomToolHandlers.HandleConclude(callerSlot, callerConfig, input, _workspacePath, ct);

        if (toolName == DelegateToolName)
            return await _scoutCoordinator.HandleDelegate(input, ct);

        if (toolName == CheckAgentsToolName)
            return new AgentRunner.ToolExecutionResult(_roomToolHandlers.BuildCheckAgentsResponse());

        if (toolName == PresentFindingToolName)
            return await _roomToolHandlers.HandlePresentFinding(callerSlot, input);

        if (toolName == ReplyToToolName)
            return await _roomToolHandlers.HandleReplyTo(callerSlot, input, ct);

        return await HandleRegistryTool(callerConfig, toolName, input, ct);
    }

    private async Task<AgentRunner.ToolExecutionResult> HandleRegistryTool(
        AgentRunner.Config callerConfig, string toolName, JsonElement input, CancellationToken ct)
    {
        var context = new ToolContext(
            _logger,
            callerConfig.WorkspacePath,
            line => _logger.LogTrace("[{Agent}/{Tool}] {Line}", callerConfig.Name, toolName, line),
            () => Interlocked.Increment(ref _outputCounter));

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
            lbSlot.Inbox.Writer.TryWrite(new RoomMessage(agentName, $"[enters the room and asks]: {m.Text}"));
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
            Description: "Call this when you have identified the root cause. Provide the summary, evidence chain, and suggested fix.",
            ParameterSchema: s_concludeSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: DelegateToolName,
            Description: "Dispatch a Scout from your Canopy Scouts network. Non-blocking -- returns immediately with their assigned name. They investigate independently in the background and their report arrives as a message. Provide a role, task, and optionally a model profile.",
            ParameterSchema: _delegateSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: CheckAgentsToolName,
            Description: "Check the status of dispatched Scouts. Returns which are still working and which have reported back.",
            ParameterSchema: s_emptySchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: PresentFindingToolName,
            Description: "Present a notable discovery to the room. The Client follows your investigation through these findings. Use this whenever you uncover something significant -- a clue, a confirmed hypothesis, or an important elimination. This is how you narrate the investigation in real time.",
            ParameterSchema: s_presentFindingSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: ReplyToToolName,
            Description: "Reply to a Scout who has entered the room with a question. Provide the Scout's name and your answer. The Scout will resume their work with your reply.",
            ParameterSchema: s_replyToSchema,
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
    }
}
