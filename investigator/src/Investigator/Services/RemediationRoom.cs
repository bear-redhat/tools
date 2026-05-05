using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Tools;

namespace Investigator.Services;

public sealed class RemediationRoom : AgentRoom
{
    private const string SignOffToolName = "sign_off";
    private const string CheckAgentsToolName = "check_agents";
    private const string ReportProgressToolName = "report_progress";
    private const string MessageToolName = "message";
    private const string DismissToolName = "dismiss";
    private const string RecallToolName = "recall";
    private const string PresentPlanToolName = "present_plan";
    private const string UpdateStepToolName = "update_step";
    private const string ReviewPlanToolName = "review_plan";
    private const string ConcludeToolName = "conclude";

    private static readonly JsonElement s_signOffSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "outcome": {
                "type": "string",
                "enum": ["fixed", "partial", "blocked", "failed", "clean"],
                "description": "The outcome of the remediation"
            },
            "actions_taken": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "plan_step_id": { "type": "string", "description": "The id of the plan step" },
                        "summary": { "type": "string", "description": "What was prepared, what the Client executed, and the result" }
                    },
                    "required": ["plan_step_id", "summary"]
                },
                "description": "Each remediation step -- reference plan step ids"
            },
            "verification": { "type": "string", "description": "How you confirmed the fix is effective -- paste command output, status fields, metric readings, or log lines" },
            "remaining": { "type": "string", "description": "Anything still outstanding. Null if fully fixed" },
            "warnings": { "type": "string", "description": "Caveats, risks, or things to monitor going forward. Null if none" }
        },
        "required": ["outcome", "actions_taken"]
    }
    """).RootElement.Clone();

    private static readonly JsonElement s_presentPlanSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "steps": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "id": { "type": "string", "description": "Short identifier for the step" },
                        "title": { "type": "string", "description": "What needs to be done" },
                        "rationale": { "type": "string", "description": "Why this step is necessary -- tie it to the case file evidence" },
                        "target": {
                            "type": "object",
                            "description": "What is being changed -- cluster + resource + namespace, or repo + file path + line range, or verification_only",
                            "properties": {
                                "type": { "type": "string" },
                                "cluster": { "type": "string" },
                                "resource": { "type": "string" },
                                "namespace": { "type": "string" },
                                "repo": { "type": "string" },
                                "path": { "type": "string" },
                                "line_range": { "type": "string" }
                            },
                            "required": ["type"]
                        },
                        "change": {
                            "type": "object",
                            "description": "How to make the change",
                            "properties": {
                                "type": { "type": "string", "enum": ["command", "patch", "config", "external", "verification"], "description": "The type of change" },
                                "current_value": { "type": "string", "description": "What the value is now (quote from assessment)" },
                                "desired_value": { "type": "string", "description": "What it should be after the change" },
                                "commands": { "type": "array", "items": { "type": "string" }, "description": "Exact commands the Client should run" },
                                "warnings": { "type": "string", "description": "Precautions or risks" }
                            },
                            "required": ["type"]
                        },
                        "validation": {
                            "type": "object",
                            "description": "How to confirm the step worked",
                            "properties": {
                                "description": { "type": "string", "description": "What to check" },
                                "commands": { "type": "array", "items": { "type": "string" }, "description": "Verification commands (read-only)" },
                                "expected": { "type": "string", "description": "The expected result" }
                            },
                            "required": ["description", "commands"]
                        }
                    },
                    "required": ["id", "title", "rationale", "target", "change", "validation"]
                }
            }
        },
        "required": ["steps"]
    }
    """).RootElement.Clone();

    private static readonly JsonElement s_updateStepSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "id": { "type": "string", "description": "Plan step id to update" },
            "status": { "type": "string", "description": "New status for the step" },
            "patch_file": { "type": "string", "description": "Path to the patch file, if applicable" },
            "note": { "type": "string", "description": "Optional note about the update" }
        },
        "required": ["id", "status"]
    }
    """).RootElement.Clone();

    private static readonly JsonElement s_reportProgressSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "title": { "type": "string", "description": "Brief title for the progress report" },
            "description": { "type": "string", "description": "What was accomplished or discovered" }
        },
        "required": ["title", "description"]
    }
    """).RootElement.Clone();

    private static readonly JsonElement s_messageSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "to": { "type": "string", "description": "Recipient name (a Ranger name, 'Intendant G. Langur', or 'user')" },
            "text": { "type": "string", "description": "The message" }
        },
        "required": ["to", "text"]
    }
    """).RootElement.Clone();

    private static readonly JsonElement s_dismissRangerSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "agent_name": { "type": "string", "description": "Name of the Ranger to dismiss" }
        },
        "required": ["agent_name"]
    }
    """).RootElement.Clone();

    private static readonly JsonElement s_recallRangerSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "agent_name": { "type": "string", "description": "Name of the Ranger to recall" }
        },
        "required": ["agent_name"]
    }
    """).RootElement.Clone();

    private static readonly JsonElement s_concludeSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "summary": { "type": "string", "description": "Concise summary of findings in plain text" },
            "evidence": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "step": { "type": "integer", "description": "Position in the logical chain (1 = starting point)" },
                        "reasoning": { "type": "string", "description": "Why this step matters -- the inference or causal link to the next step" },
                        "finding": { "type": "string", "description": "What was discovered -- a one or two sentence factual statement" },
                        "cluster": { "type": "string", "description": "The cluster this evidence relates to, if applicable" },
                        "proof": { "type": "string", "description": "The raw evidence that supports this step: paste verbatim log lines, error messages, status fields, or command output." },
                        "source": { "type": "string", "description": "Log file path or URL with optional :line suffix" }
                    },
                    "required": ["step", "reasoning", "finding", "proof"]
                },
                "description": "Ordered chain of proof."
            },
            "fix_description": { "type": "string" },
            "fix_commands": { "type": "array", "items": { "type": "string" } },
            "fix_warning": { "type": "string" }
        },
        "required": ["summary", "evidence"]
    }
    """).RootElement.Clone();

    private static readonly SubAgentConfig s_rangerConfig = new(
        Label: "Ranger",
        Adjectives: ["Stalwart", "Vigilant", "Trusted", "Reliable", "Seasoned",
                     "Steadfast", "Rugged", "Grounded", "Tenacious", "Able",
                     "Dauntless", "Stout", "Unfailing", "Proven", "Staunch",
                     "Surefoot", "Unbowed", "Tested", "Dogged", "Granite",
                     "Steely", "Ironclad", "Gritty", "Unyielding", "Fierce"],
        Animals: ["Elk", "Ibex", "Falcon", "Lynx", "Bison",
                  "Boar", "Crane", "Condor", "Osprey", "Panther",
                  "Puma", "Kestrel", "Marten", "Chamois", "Caracal",
                  "Serval", "Civet", "Genet", "Coati", "Tayra",
                  "Wolverine", "Jackal", "Dhole", "Harrier", "Merlin"],
        BuildPrompt: RemediationPrompts.BuildRangerSystemPrompt,
        LeadAgentName: "Intendant G. Langur",
        ToolScope: ToolScope.Remediation);

    private readonly RemediationToolHandlers _roomToolHandlers;
    private RemediationPlan _plan = new();

    public override string LeadId => "langur";
    public override string LeadName => "Intendant G. Langur";
    protected override string RoomName => "The Canopy Post";
    protected override string SubAgentLabel => "Ranger";
    protected override string SubAgentExitMessage => "Ranger exited without reporting.";
    protected override string AllSubAgentsFinishedMessage =>
        "All Rangers have reported back. Review their findings and proceed with the plan.";

    public RemediationRoom(
        ILlmClientFactory llmFactory,
        ToolRegistry toolRegistry,
        AgentOptions agentOptions,
        RoomEventPipeline pipeline,
        TranscriptStore transcriptStore,
        ILogger<RemediationRoom> logger)
        : base(llmFactory, toolRegistry, agentOptions, pipeline, transcriptStore, logger,
            scope: ToolScope.Remediation, subAgentConfig: s_rangerConfig, subAgentConcludeSchema: s_concludeSchema)
    {
        _roomToolHandlers = new RemediationToolHandlers(_agents, _logger, () => _plan);
    }

    public async Task StartAsync(string workspacePath, CaseFile caseFile, CancellationToken ct,
        IReadOnlyList<RoomEvent>? eventLog = null,
        string? userId = null, string? conversationId = null, TimeZoneInfo? clientTimeZone = null)
    {
        InitializeRoom(workspacePath, ct, userId, conversationId, clientTimeZone);

        var langurSlot = new AgentSlot
        {
            Id = LeadId,
            Name = LeadName,
            Role = "remediation commander",
            Inbox = Bus.Subscribe(LeadId, evt => evt.To == LeadId && evt is not RoomEvent.ToolResponse),
        };
        _agents[LeadName] = langurSlot;

        var primaryOptions = _llmFactory.GetModelOptions(_llmFactory.PrimaryProfileName);
        var summarizerProfile = _llmFactory.DefaultProfileName;
        var summarizerOptions = _llmFactory.GetModelOptions(summarizerProfile);
        var runnerConfig = new AgentRunner.Config(
            Id: LeadId,
            Name: LeadName,
            Role: "remediation commander",
            SystemPrompt: RemediationPrompts.BuildSystemPrompt(
                _toolSections, workspacePath, caseFile,
                _llmFactory.Models, _llmFactory.DefaultProfileName,
                clientTimeZone),
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
            TerminalToolNames: new HashSet<string> { SignOffToolName },
            IsConditionallyTerminal: (tool, input) =>
                tool == "message" && input.TryGetProperty("to", out var to)
                && to.GetString() is "user" or "client");

        List<LlmMessage>? initialMessages = null;
        if (eventLog is { Count: > 0 })
        {
            initialMessages = LlmContextApplier.Replay(eventLog, LeadId);
        }
        else
        {
            var caseText = RemediationPrompts.FormatCaseFile(caseFile);
            var syntheticId = "sys_case_file";
            var assistantMsg = new LlmMessage { Role = "assistant", Content = JsonSerializer.SerializeToElement(new object[] {
                new { type = "tool_use", id = syntheticId, name = "receive_case", input = new { } }
            })};
            var resultMsg = new LlmMessage { Role = "user", Content = JsonSerializer.SerializeToElement(new[] {
                new { type = "tool_result", tool_use_id = syntheticId, content = caseText }
            })};
            initialMessages = [assistantMsg, resultMsg];
        }

        langurSlot.RunTask = RunAgentWithRouting(langurSlot, runnerConfig, ct, initialMessages);

        TranscriptStore.Append(new RoomEvent.ExternalInput(0, "system", DateTimeOffset.UtcNow,
            "Case file received. Begin assessment.") { To = LeadId });

        try
        {
            await langurSlot.RunTask;
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
            Name: SignOffToolName,
            Description: "Sign off the remediation. Call when the remedy has been verified or has reached a definitive stopping point. Provide the outcome, actions taken referencing plan step ids, verification evidence, and any remaining items or warnings.",
            ParameterSchema: s_signOffSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: PresentPlanToolName,
            Description: "Present or update the structured remediation plan. Each step is a full remediation brief with target, change details, and validation criteria. The plan appears as a persistent panel visible to the Client.",
            ParameterSchema: s_presentPlanSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: UpdateStepToolName,
            Description: "Update the status of a remediation plan step. Use to track progress: preparing, ready, done, verified, failed, or blocked.",
            ParameterSchema: s_updateStepSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: ReviewPlanToolName,
            Description: "Re-read the current remediation plan and step statuses. Call whenever you are unsure what has been completed or what comes next -- after Ranger reports, after compaction, or when context has drifted. It costs nothing; guessing costs time.",
            ParameterSchema: s_emptySchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: ReportProgressToolName,
            Description: "Report a notable progress update to the room. The Client follows the remediation through these updates -- use for significant discoveries during assessment, confirmed verifications, or important status changes.",
            ParameterSchema: s_reportProgressSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: "delegate",
            Description: "Dispatch one of your Canopy Post Rangers. Non-blocking -- returns immediately with their assigned name. They investigate independently and report back as a message. Provide a role, task, and optionally a model profile.",
            ParameterSchema: _delegateSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: CheckAgentsToolName,
            Description: "Review the status of dispatched Rangers. NOT a polling tool -- call only if you have genuinely lost track of which Rangers are afield. You will be woken automatically when Rangers report; do not call this repeatedly to check.",
            ParameterSchema: s_emptySchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: MessageToolName,
            Description: "Send a message to a Ranger, or to the user. When messaging a Ranger, they resume their work with your reply. When messaging the user, you will wait for their response.",
            ParameterSchema: s_messageSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: DismissToolName,
            Description: "Dismiss a Ranger from the post once they have reported. They depart The Canopy Post and cannot be contacted again. If the Ranger is still abroad, use recall to summon them back first.",
            ParameterSchema: s_dismissRangerSchema,
            DefaultTimeout: TimeSpan.Zero));

        tools.Add(new ToolDefinition(
            Name: RecallToolName,
            Description: "Recall a Ranger to The Canopy Post. They will report back immediately with whatever they have uncovered. Use when you no longer require their reconnaissance or need their interim findings now.",
            ParameterSchema: s_recallRangerSchema,
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
                if (_roomToolHandlers.HasActiveRangers())
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    response += "\n\nRangers are still at work. Use your other tools or wait -- you will be woken when a Ranger reports.";
                }
                return new AgentRunner.ToolExecutionResult(response);
            }
            case SignOffToolName:
                return await _roomToolHandlers.HandleSignOff(caller, config, input);
            case ConcludeToolName:
                return await _roomToolHandlers.HandleConclude(caller, config, input);
            case PresentPlanToolName:
                return await _roomToolHandlers.HandlePresentPlan(caller, input);
            case UpdateStepToolName:
                return await _roomToolHandlers.HandleUpdateStep(caller, input);
            case ReviewPlanToolName:
                return _roomToolHandlers.HandleReviewPlan();
            case ReportProgressToolName:
                return await _roomToolHandlers.HandleReportProgress(caller, input);
            case MessageToolName:
                return await _roomToolHandlers.HandleMessage(caller, input);
            case DismissToolName:
                return _roomToolHandlers.HandleDismiss(input);
            case RecallToolName:
                return await _roomToolHandlers.HandleRecall(input);
            default:
                return null;
        }
    }

    protected override bool IsAllowedWhenStoodDown(string toolName) =>
        toolName is SignOffToolName or ConcludeToolName;

    protected override string? GetSummaryText(string toolName, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        return toolName switch
        {
            "conclude" => input.TryGetProperty("summary", out var s) ? s.GetString() : null,
            "sign_off" => input.TryGetProperty("outcome", out var o) ? o.GetString() : null,
            "report_progress" =>
                $"{(input.TryGetProperty("title", out var t) ? t.GetString() : "")}: {(input.TryGetProperty("description", out var d) ? d.GetString() : "")}",
            _ => null,
        };
    }

    protected override bool HasTerminalToolResult(RoomEvent.LlmContext ctx, IReadOnlySet<string> terminalTools)
    {
        foreach (var msg in ctx.Messages)
        {
            if (msg.Role != "user" || msg.Content.ValueKind != JsonValueKind.Array) continue;
            foreach (var block in msg.Content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_result")
                    return true;
            }
        }

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
}
