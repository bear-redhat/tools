using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Tools;

namespace Investigator.Services;

public sealed class InvestigationRoom
{
    private const string ConcludeToolName = "conclude";
    private const string DelegateToolName = "delegate";
    private const string CheckAgentsToolName = "check_agents";
    private const string PresentFindingToolName = "present_finding";
    private const string MessageToolName = "message";
    private const string DismissToolName = "dismiss";
    private const string RecallToolName = "recall";

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

    private static readonly JsonElement s_messageSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "to": { "type": "string", "description": "Recipient name (a Scout name, 'Little Bear', or 'user')" },
            "text": { "type": "string", "description": "The message" }
        },
        "required": ["to", "text"]
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
                        "step": { "type": "integer", "description": "Position in the logical chain (1 = starting point)" },
                        "reasoning": { "type": "string", "description": "Why this step matters -- the inference or causal link to the next step" },
                        "finding": { "type": "string", "description": "What was discovered -- a one or two sentence factual statement" },
                        "cluster": { "type": "string", "description": "The cluster this evidence relates to, if applicable" },
                        "proof": { "type": "string", "description": "The raw evidence that supports this step: paste verbatim log lines, error messages, status fields, or command output. If a command was run, put it on the first line with raw output below." },
                        "source": { "type": "string", "description": "Log file path or URL with optional :line suffix, e.g. 'build.log:142' or 'https://prow.ci/build-log.txt:307'" }
                    },
                    "required": ["step", "reasoning", "finding", "proof"]
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

    private readonly ConcurrentDictionary<string, AgentSlot> _agents = new();
    private readonly JsonElement _delegateSchema;

    private readonly ILlmClientFactory _llmFactory;
    private readonly ToolRegistry _toolRegistry;
    private readonly IReadOnlyList<string> _toolSections;
    private readonly AgentOptions _agentOptions;
    private readonly ILogger<InvestigationRoom> _logger;

    private readonly RoomToolHandlers _roomToolHandlers;
    private readonly SubAgentCoordinator _scoutCoordinator;

    private string _workspacePath = "";
    private string? _userId;
    private string? _conversationId;
    private TimeZoneInfo? _clientTimeZone;
    private CancellationToken _ct;
    private int _outputCounter;

    public RoomEventPipeline Pipeline { get; }
    public RoomEventBus Bus => Pipeline.Bus;
    public TranscriptStore TranscriptStore { get; }
    public string LeadId => "little-bear";

    public InvestigationRoom(
        ILlmClientFactory llmFactory,
        ToolRegistry toolRegistry,
        AgentOptions agentOptions,
        RoomEventPipeline pipeline,
        TranscriptStore transcriptStore,
        ILogger<InvestigationRoom> logger)
    {
        _llmFactory = llmFactory;
        _toolRegistry = toolRegistry;
        _toolSections = toolRegistry.GetSystemPromptContributions();
        _agentOptions = agentOptions;
        Pipeline = pipeline;
        TranscriptStore = transcriptStore;
        _logger = logger;

        _delegateSchema = BuildDelegateSchema();

        _roomToolHandlers = new RoomToolHandlers(_agents, _logger);

        var scoutConfig = new SubAgentConfig(
            Label: "Scout",
            Adjectives: ["Sharp", "Swift", "Keen", "Quiet", "Steady", "Nimble", "Bold", "Clever", "Bright", "Wary",
                         "Sturdy", "Quick", "Calm", "Watchful", "Diligent", "Faithful", "Plucky", "Resolute", "Canny", "Deft",
                         "Earnest", "Gentle", "Hardy", "Tireless", "Thorough"],
            Animals: ["Badger", "Owl", "Fox", "Rabbit", "Hedgehog", "Mole", "Otter", "Wren", "Hare", "Stoat",
                      "Crow", "Finch", "Vole", "Shrew", "Rook", "Magpie", "Robin", "Sparrow", "Jackdaw", "Dormouse",
                      "Newt", "Toad", "Pipit", "Dunnock", "Fieldfare"],
            BuildPrompt: InvestigationPrompts.BuildScoutSystemPrompt,
            LeadAgentName: "Little Bear",
            ToolScope: ToolScope.Investigation);

        _scoutCoordinator = new SubAgentCoordinator(
            scoutConfig, _agents, _llmFactory, _toolRegistry, _agentOptions,
            _toolSections, _logger,
            RunAgentWithRouting, s_concludeSchema, Bus);
    }

    public async Task StartAsync(string workspacePath, CancellationToken ct,
        IReadOnlyList<RoomEvent>? eventLog = null,
        string? userId = null, string? conversationId = null, TimeZoneInfo? clientTimeZone = null)
    {
        _workspacePath = workspacePath;
        _ct = ct;
        _userId = userId;
        _conversationId = conversationId;
        _clientTimeZone = clientTimeZone;
        _scoutCoordinator.WorkspacePath = workspacePath;
        _scoutCoordinator.UserId = userId;
        _scoutCoordinator.ConversationId = conversationId;
        _scoutCoordinator.ClientTimeZone = clientTimeZone;

        var littleBearSlot = new AgentSlot
        {
            Id = "little-bear",
            Name = "Little Bear",
            Role = "lead detective",
            Inbox = Bus.Subscribe("little-bear", evt => evt.To == "little-bear" && evt is not RoomEvent.ToolResponse),
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
                _llmFactory.Models, _llmFactory.DefaultProfileName,
                clientTimeZone),
            LlmClient: _llmFactory.GetClient(_llmFactory.PrimaryProfileName),
            Tools: BuildLittleBearTools(),
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
            TerminalToolNames: new HashSet<string> { "conclude" },
            IsConditionallyTerminal: (tool, input) =>
                tool == "message" && input.TryGetProperty("to", out var to)
                && to.GetString() is "user" or "client");

        List<LlmMessage>? initialMessages = null;
        if (eventLog is { Count: > 0 })
            initialMessages = LlmContextApplier.Replay(eventLog, LeadId);

        littleBearSlot.RunTask = RunAgentWithRouting(littleBearSlot, runnerConfig, ct, initialMessages);

        try
        {
            await littleBearSlot.RunTask;
        }
        finally
        {
            var subAgents = _agents.Where(kv => kv.Value.Id != LeadId).ToList();
            foreach (var (_, s) in subAgents)
                Bus.Unsubscribe(s.Id);

            var subTasks = subAgents
                .Select(kv => kv.Value.RunTask)
                .Where(t => t is not null)
                .ToArray();
            if (subTasks.Length > 0)
                try { await Task.WhenAll(subTasks!); } catch { }
        }
    }

    public Task RecallScoutAsync(string scoutName)
    {
        if (!_agents.TryGetValue(scoutName, out var slot) || slot.Id == "little-bear") return Task.CompletedTask;

        const string message = "Return to Banyan Row at once. Report back immediately with whatever "
            + "you have uncovered thus far. Call conclude now.";

        TranscriptStore.Append(new RoomEvent.ExternalInput(0, "little-bear", DateTimeOffset.UtcNow, message)
            { To = slot.Id });
        return Task.CompletedTask;
    }

    public Task StandDownScoutAsync(string scoutName)
    {
        if (!_agents.TryGetValue(scoutName, out var slot) || slot.Id == "little-bear") return Task.CompletedTask;

        slot.StoodDown = true;
        slot.CurrentToolCts?.Cancel();

        const string message = "Stand down at once. Your current inquiries are abandoned. "
            + "Report back immediately with whatever evidence you have gathered. "
            + "Call conclude now -- no further tool calls are permitted.";

        TranscriptStore.Append(new RoomEvent.ExternalInput(0, "little-bear", DateTimeOffset.UtcNow, message)
            { To = slot.Id });
        return Task.CompletedTask;
    }

    public ValueTask PostUserMessageAsync(string text, CancellationToken ct)
    {
        TranscriptStore.Append(new RoomEvent.ExternalInput(0, "user", DateTimeOffset.UtcNow, text)
            { To = LeadId });
        return ValueTask.CompletedTask;
    }

    internal async Task RunAgentWithRouting(AgentSlot slot, AgentRunner.Config config, CancellationToken ct,
        List<LlmMessage>? initialMessages = null)
    {
        var runner = new AgentRunner(_logger);

        var terminalTools = config.TerminalToolNames ?? new HashSet<string> { "conclude" };

        ValueTask Store(RoomEvent.LlmContext ctx)
        {
            TranscriptStore.Append(ctx);
            if (ctx.IsInboxBatch)
                slot.Idle = false;
            else if (HasTerminalToolResult(ctx, terminalTools))
                slot.Idle = true;
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
                        "Scout exited without reporting.") { To = LeadId });
                }

                if (removed)
                {
                    var remainingScouts = _agents.Where(kv => kv.Value.Id != "little-bear").ToList();
                    if (remainingScouts.Count == 0
                        && _agents.TryGetValue("Little Bear", out var lb)
                        && !lb.Idle)
                    {
                        _logger.LogInformation("Last scout {Name} finished, nudging Little Bear to conclude", config.Name);
                        TranscriptStore.Append(new RoomEvent.ExternalInput(0, "system", DateTimeOffset.UtcNow,
                            "All Scouts have reported back. Conclude now with the evidence you have.") { To = LeadId });
                    }
                }
            }

        }
    }

    private async Task<AgentRunner.ToolExecutionResult> HandleToolExecution(
        AgentSlot callerSlot, AgentRunner.Config callerConfig,
        string toolName, JsonElement input, string stepId, CancellationToken ct)
    {
        if (callerSlot.StoodDown && toolName != ConcludeToolName)
        {
            return new AgentRunner.ToolExecutionResult(
                "[Stood down] Further inquiries are not permitted. Report to Little Bear -- call conclude now.",
                ExitCode: -1);
        }

        AgentRunner.ToolExecutionResult result;

        if (toolName == DelegateToolName)
            return await _scoutCoordinator.HandleDelegate(input, ct);
        else if (toolName == CheckAgentsToolName)
        {
            var response = _roomToolHandlers.BuildCheckAgentsResponse();
            if (_roomToolHandlers.HasActiveScouts())
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                response += "\n\nScouts are still at work. Use your other tools or wait -- you will be woken when a Scout reports.";
            }
            return new AgentRunner.ToolExecutionResult(response);
        }
        else if (toolName == ConcludeToolName)
            result = await _roomToolHandlers.HandleConclude(callerSlot, callerConfig, input);
        else if (toolName == PresentFindingToolName)
            result = await _roomToolHandlers.HandlePresentFinding(callerSlot, input);
        else if (toolName == MessageToolName)
            return await _roomToolHandlers.HandleMessage(callerSlot, input);
        else if (toolName == DismissToolName)
            return _roomToolHandlers.HandleDismiss(input);
        else if (toolName == RecallToolName)
            return await _roomToolHandlers.HandleRecall(input);
        else
        {
            callerSlot.CurrentToolCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                return await HandleRegistryTool(callerConfig, toolName, input, stepId, callerSlot.CurrentToolCts.Token);
            }
            catch (OperationCanceledException) when (callerSlot.StoodDown)
            {
                return new AgentRunner.ToolExecutionResult(
                    "[Stood down] Tool execution was aborted. Report to Little Bear -- call conclude now.",
                    ExitCode: -1);
            }
            finally
            {
                callerSlot.CurrentToolCts = null;
            }
        }

        if (callerConfig.SummarizerClient is not null)
        {
            var textToSummarize = GetSummaryText(toolName, input);
            if (textToSummarize is not null)
            {
                try
                {
                    var headline = await SummarizeOneLineAsync(callerConfig.SummarizerClient, textToSummarize, ct);
                    result = result with { Summary = headline };
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Summarization failed for {Tool}", toolName);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Summarization cancelled for {Tool}", toolName);
                }
            }
        }

        return result;
    }

    private async Task<AgentRunner.ToolExecutionResult> HandleRegistryTool(
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
            _ = Pipeline.EmitAsync(new RoomEvent.ToolResponse(0, $"tool:{childTool}", DateTimeOffset.UtcNow,
                childTool, output, RequestSeq: reqSeq, ExitCode: exitCode, TimedOut: timedOut,
                ParentSeq: parentSeq) { To = callerConfig.Id }, _ct);
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
            Name: MessageToolName,
            Description: "Send a message to a Scout, or to the user. When messaging a Scout, they resume their work with your reply. When messaging the user, you will wait for their response.",
            ParameterSchema: s_messageSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: DismissToolName,
            Description: "Dismiss a Scout from the room once they have reported. They depart Banyan Row and cannot be contacted again. If the Scout is still abroad, use recall to summon them back first.",
            ParameterSchema: s_dismissScoutSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: RecallToolName,
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

    // --- Internal types ---

    private static string? GetSummaryText(string toolName, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        return toolName switch
        {
            "conclude" => input.TryGetProperty("summary", out var s) ? s.GetString() : null,
            "present_finding" or "report_progress" =>
                $"{(input.TryGetProperty("title", out var t) ? t.GetString() : "")}: {(input.TryGetProperty("description", out var d) ? d.GetString() : "")}",
            _ => null,
        };
    }

    private static async Task<string> SummarizeOneLineAsync(ILlmClient client, string text, CancellationToken ct)
    {
        var messages = new List<LlmMessage> { new() { Role = "user", Content = System.Text.Json.JsonSerializer.SerializeToElement(text) } };
        IReadOnlyList<ToolDefinition> noTools = [];
        var sb = new System.Text.StringBuilder();
        await foreach (var block in client.StreamMessageAsync(messages, noTools,
            "Summarise the following in one sentence, max 100 characters. Output only the summary, nothing else.", ct))
        {
            if (block.Type == "text" && block.Text is not null) sb.Append(block.Text);
        }
        return sb.ToString().Trim();
    }

    private static bool HasTerminalToolResult(RoomEvent.LlmContext ctx, IReadOnlySet<string> terminalTools)
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

    internal sealed class AgentSlot
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
        public CancellationTokenSource? CurrentToolCts;
    }
}
