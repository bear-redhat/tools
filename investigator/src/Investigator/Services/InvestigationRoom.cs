using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Tools;

namespace Investigator.Services;

public sealed class InvestigationRoom : AgentRoom
{
    private const string ConcludeToolName = "conclude";
    private const string CheckAgentsToolName = "check_agents";
    private const string PresentFindingToolName = "present_finding";
    private const string MessageToolName = "message";
    private const string DismissToolName = "dismiss";
    private const string RecallToolName = "recall";

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

    private static readonly SubAgentConfig s_scoutConfig = new(
        Label: "Scout",
        Adjectives: ["Sharp", "Swift", "Keen", "Quiet", "Steady", "Nimble", "Bold", "Clever", "Bright", "Wary",
                     "Sturdy", "Quick", "Calm", "Watchful", "Diligent", "Faithful", "Plucky", "Resolute", "Canny", "Deft",
                     "Earnest", "Gentle", "Hardy", "Tireless", "Thorough"],
        Animals: ["Badger", "Owl", "Fox", "Rabbit", "Hedgehog", "Mole", "Otter", "Wren", "Hare", "Stoat",
                  "Crow", "Finch", "Vole", "Shrew", "Rook", "Magpie", "Robin", "Sparrow", "Jackdaw", "Dormouse",
                  "Newt", "Toad", "Pipit", "Dunnock", "Fieldfare"],
        BuildPrompt: InvestigationPrompts.BuildScoutSystemPrompt,
        LeadAgentName: "Little Bear",
        ToolScope: ToolScope.Investigation,
        BuildAnalystPrompt: InvestigationPrompts.BuildAnalystSystemPrompt);

    private readonly RoomToolHandlers _roomToolHandlers;

    public override string LeadId => "little-bear";
    public override string LeadName => "Little Bear";
    protected override string RoomName => "Banyan Row";
    protected override string SubAgentLabel => "operative";
    protected override string SubAgentExitMessage => "An operative exited without reporting.";
    protected override string AllSubAgentsFinishedMessage =>
        "All operatives have reported back. Conclude now with the evidence you have.";

    public InvestigationRoom(
        ILlmClientFactory llmFactory,
        ToolRegistry toolRegistry,
        AgentOptions agentOptions,
        RoomEventPipeline pipeline,
        TranscriptStore transcriptStore,
        ILogger<InvestigationRoom> logger)
        : base(llmFactory, toolRegistry, agentOptions, pipeline, transcriptStore, logger,
            scope: null, subAgentConfig: s_scoutConfig, subAgentConcludeSchema: s_concludeSchema)
    {
        _roomToolHandlers = new RoomToolHandlers(_agents, LeadId, _logger);
    }

    public async Task StartAsync(string workspacePath, CancellationToken ct,
        IReadOnlyList<RoomEvent>? eventLog = null,
        string? userId = null, string? conversationId = null, TimeZoneInfo? clientTimeZone = null)
    {
        InitializeRoom(workspacePath, ct, userId, conversationId, clientTimeZone);

        var leadSlot = new AgentSlot
        {
            Id = LeadId,
            Name = LeadName,
            Role = "lead detective",
            Inbox = Bus.Subscribe(LeadId, evt => evt.To == LeadId && evt is not RoomEvent.ToolResponse),
        };
        _agents[LeadName] = leadSlot;

        var primaryOptions = _llmFactory.GetModelOptions(_llmFactory.PrimaryProfileName);
        var summarizerProfile = _llmFactory.DefaultProfileName;
        var summarizerOptions = _llmFactory.GetModelOptions(summarizerProfile);
        var runnerConfig = new AgentRunner.Config(
            Id: LeadId,
            Name: LeadName,
            Role: "lead detective",
            SystemPrompt: InvestigationPrompts.BuildSystemPrompt(
                _toolSections, workspacePath,
                _llmFactory.Models, _llmFactory.DefaultProfileName,
                conversationId, clientTimeZone),
            LlmClient: _llmFactory.GetClient(_llmFactory.PrimaryProfileName),
            Tools: BuildLeadTools(),
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

        leadSlot.RunTask = RunAgentWithRouting(leadSlot, runnerConfig, ct, initialMessages);

        try
        {
            await leadSlot.RunTask;
        }
        finally
        {
            await CleanupSubAgentsAsync();
        }
    }

    protected override IReadOnlyList<ToolDefinition> BuildLeadTools()
    {
        var tools = GetRegistryToolDefinitions().ToList();

        tools.Add(new ToolDefinition(
            Name: ConcludeToolName,
            Description: "Call this when the evidence has converged and you can state the root cause. Provide the summary, evidence chain, and suggested remedy.",
            ParameterSchema: s_concludeSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: "delegate",
            Description: "Dispatch an operative -- a Scout (tier: field) for data gathering, or an Analyst (tier: analyst) for domain-level reasoning. Non-blocking -- returns immediately with their assigned name. Use cc to copy a Scout's report to an Analyst. Use briefing to hand off existing intelligence.",
            ParameterSchema: _delegateSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: CheckAgentsToolName,
            Description: "Review the registry of operatives in the field -- Scouts and Analysts, their tasks, who dispatched them, and CC targets. Consult before dispatching to avoid duplicate work. NOT a polling tool.",
            ParameterSchema: s_emptySchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: PresentFindingToolName,
            Description: "Present a notable discovery to the room. The Client follows the investigation through these findings -- use this for significant clues, confirmed hypotheses, or important eliminations.",
            ParameterSchema: s_presentFindingSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: MessageToolName,
            Description: "Send a message to an operative (Scout or Analyst), or to the user. When messaging an operative, they resume their work with your reply. When messaging the user, you will wait for their response.",
            ParameterSchema: s_messageSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: DismissToolName,
            Description: "Dismiss an operative from the room once they have reported. They depart Banyan Row and cannot be contacted again. If they are still abroad, use recall first.",
            ParameterSchema: s_dismissScoutSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: RecallToolName,
            Description: "Recall an operative to Banyan Row. They will report back immediately with whatever they have uncovered. Use when you no longer require their work or need their interim findings now.",
            ParameterSchema: s_recallScoutSchema,
            DefaultTimeout: TimeSpan.Zero));

        return tools;
    }

    protected override async Task<AgentRunner.ToolExecutionResult?> HandleRoomToolAsync(
        AgentSlot caller, AgentRunner.Config config,
        string toolName, JsonElement input, string stepId, CancellationToken ct)
    {
        switch (toolName)
        {
            case CheckAgentsToolName:
            {
                var response = _roomToolHandlers.BuildCheckAgentsResponse();
                if (_roomToolHandlers.HasActiveScouts())
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    response += "\n\nOperatives are still at work. You will be woken when one reports.";
                }
                return new AgentRunner.ToolExecutionResult(response);
            }
            case ConcludeToolName:
                return await _roomToolHandlers.HandleConclude(caller, config, input);
            case PresentFindingToolName:
                return await _roomToolHandlers.HandlePresentFinding(caller, input);
            case MessageToolName:
                return await _roomToolHandlers.HandleMessage(caller, input);
            case DismissToolName:
                return _roomToolHandlers.HandleDismiss(caller, input);
            case RecallToolName:
                return _roomToolHandlers.HandleRecall(caller, input);
            default:
                return null;
        }
    }

    protected override bool IsAllowedWhenStoodDown(string toolName) =>
        toolName == ConcludeToolName;

    protected override string? GetSummaryText(string toolName, JsonElement input)
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

}
