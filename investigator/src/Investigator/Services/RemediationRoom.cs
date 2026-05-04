using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Tools;

namespace Investigator.Services;

public sealed class RemediationRoom
{
    private const string LeadId = "langur";
    private const string LeadName = "Intendant G. Langur";

    private const string SignOffToolName = "sign_off";
    private const string DelegateToolName = "delegate";
    private const string CheckAgentsToolName = "check_agents";
    private const string ReportProgressToolName = "report_progress";
    private const string MessageToolName = "message";
    private const string DismissToolName = "dismiss";
    private const string RecallToolName = "recall";
    private const string PresentPlanToolName = "present_plan";
    private const string UpdateStepToolName = "update_step";
    private const string ReviewPlanToolName = "review_plan";
    private const string ConcludeToolName = "conclude";

    private static readonly JsonElement s_emptySchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {},
        "required": []
    }
    """).RootElement.Clone();

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

    private readonly ConcurrentDictionary<string, InvestigationRoom.AgentSlot> _agents = new();
    private readonly JsonElement _delegateSchema;

    private readonly ILlmClientFactory _llmFactory;
    private readonly ToolRegistry _toolRegistry;
    private readonly IReadOnlyList<string> _toolSections;
    private readonly AgentOptions _agentOptions;
    private readonly ILogger<RemediationRoom> _logger;

    private readonly RemediationToolHandlers _roomToolHandlers;
    private readonly SubAgentCoordinator _rangerCoordinator;
    private RemediationPlan _plan = new();

    private string _workspacePath = "";
    private string? _userId;
    private string? _conversationId;
    private TimeZoneInfo? _clientTimeZone;
    private CancellationToken _ct;
    private int _outputCounter;

    public RoomEventPipeline Pipeline { get; }
    public RoomEventBus Bus => Pipeline.Bus;
    public TranscriptStore TranscriptStore { get; }

    public RemediationRoom(
        ILlmClientFactory llmFactory,
        ToolRegistry toolRegistry,
        AgentOptions agentOptions,
        RoomEventPipeline pipeline,
        TranscriptStore transcriptStore,
        ILogger<RemediationRoom> logger)
    {
        _llmFactory = llmFactory;
        _toolRegistry = toolRegistry;
        _toolSections = toolRegistry.GetSystemPromptContributions(ToolScope.Remediation);
        TranscriptStore = transcriptStore;
        _agentOptions = agentOptions;
        Pipeline = pipeline;
        _logger = logger;

        _delegateSchema = BuildDelegateSchema();

        _roomToolHandlers = new RemediationToolHandlers(
            _agents, _logger, () => _plan);

        var rangerConfig = new SubAgentConfig(
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
            LeadAgentName: LeadName,
            ToolScope: ToolScope.Remediation);

        _rangerCoordinator = new SubAgentCoordinator(
            rangerConfig, _agents, _llmFactory, _toolRegistry, _agentOptions,
            _toolSections, _logger,
            RunAgentWithRouting, s_concludeSchema, Bus);
    }

    public async Task StartAsync(string workspacePath, CaseFile caseFile, CancellationToken ct,
        IReadOnlyList<RoomEvent>? eventLog = null,
        string? userId = null, string? conversationId = null, TimeZoneInfo? clientTimeZone = null)
    {
        _workspacePath = workspacePath;
        _ct = ct;
        _userId = userId;
        _conversationId = conversationId;
        _clientTimeZone = clientTimeZone;
        _rangerCoordinator.WorkspacePath = workspacePath;
        _rangerCoordinator.UserId = userId;
        _rangerCoordinator.ConversationId = conversationId;
        _rangerCoordinator.ClientTimeZone = clientTimeZone;

        var langurSlot = new InvestigationRoom.AgentSlot
        {
            Id = LeadId,
            Name = LeadName,
            Role = "remediation commander",
            Inbox = Bus.Subscribe(LeadId, evt => evt.To == LeadId && evt is not RoomEvent.ToolResponse),
        };
        _agents[LeadName] = langurSlot;
        _rangerCoordinator.RegisterAgentName(LeadName);

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
            Tools: BuildLangurTools(),
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

    public Task RecallRangerAsync(string rangerName)
    {
        if (!_agents.TryGetValue(rangerName, out var slot) || slot.Id == LeadId) return Task.CompletedTask;

        const string message = "Return to The Canopy Post at once. Report back immediately with whatever "
            + "you have uncovered thus far. Call conclude now.";

        TranscriptStore.Append(new RoomEvent.ExternalInput(0, LeadId, DateTimeOffset.UtcNow, message)
            { To = slot.Id });
        return Task.CompletedTask;
    }

    public Task StandDownRangerAsync(string rangerName)
    {
        if (!_agents.TryGetValue(rangerName, out var slot) || slot.Id == LeadId) return Task.CompletedTask;

        slot.StoodDown = true;
        slot.CurrentToolCts?.Cancel();

        const string message = "Stand down at once. Your current tasks are abandoned. "
            + "Report back immediately with whatever you have gathered. "
            + "Call conclude now -- no further tool calls are permitted.";

        TranscriptStore.Append(new RoomEvent.ExternalInput(0, LeadId, DateTimeOffset.UtcNow, message)
            { To = slot.Id });
        return Task.CompletedTask;
    }

    public ValueTask PostUserMessageAsync(string text, CancellationToken ct)
    {
        TranscriptStore.Append(new RoomEvent.ExternalInput(0, "user", DateTimeOffset.UtcNow, text)
            { To = LeadId });
        return ValueTask.CompletedTask;
    }

    internal async Task RunAgentWithRouting(InvestigationRoom.AgentSlot slot, AgentRunner.Config config, CancellationToken ct,
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
                        "Ranger exited without reporting.") { To = LeadId });
                }

                if (removed)
                {
                    var remainingRangers = _agents.Where(kv => kv.Value.Id != LeadId).ToList();
                    if (remainingRangers.Count == 0
                        && _agents.TryGetValue(LeadName, out var lb)
                        && !lb.Idle)
                    {
                        _logger.LogInformation("Last Ranger {Name} finished, nudging Intendant Langur to proceed", config.Name);
                        TranscriptStore.Append(new RoomEvent.ExternalInput(0, "system", DateTimeOffset.UtcNow,
                            "All Rangers have reported back. Review their findings and proceed with the plan.") { To = LeadId });
                    }
                }
            }

        }
    }

    private async Task<AgentRunner.ToolExecutionResult> HandleToolExecution(
        InvestigationRoom.AgentSlot callerSlot, AgentRunner.Config callerConfig,
        string toolName, JsonElement input, string stepId, CancellationToken ct)
    {
        if (callerSlot.StoodDown && toolName != SignOffToolName && toolName != ConcludeToolName)
        {
            return new AgentRunner.ToolExecutionResult(
                "[Stood down] Further tasks are not permitted. Report to Intendant Langur -- call conclude now.",
                ExitCode: -1);
        }

        AgentRunner.ToolExecutionResult result;

        if (toolName == DelegateToolName)
            return await _rangerCoordinator.HandleDelegate(input, ct);
        else if (toolName == CheckAgentsToolName)
        {
            var response = _roomToolHandlers.BuildCheckAgentsResponse();
            if (_roomToolHandlers.HasActiveRangers())
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                response += "\n\nRangers are still at work. Use your other tools or wait -- you will be woken when a Ranger reports.";
            }
            return new AgentRunner.ToolExecutionResult(response);
        }
        else if (toolName == SignOffToolName)
            result = await _roomToolHandlers.HandleSignOff(callerSlot, callerConfig, input);
        else if (toolName == ConcludeToolName)
            result = await _roomToolHandlers.HandleConclude(callerSlot, callerConfig, input);
        else if (toolName == PresentPlanToolName)
            return await _roomToolHandlers.HandlePresentPlan(callerSlot, input);
        else if (toolName == UpdateStepToolName)
            return await _roomToolHandlers.HandleUpdateStep(callerSlot, input);
        else if (toolName == ReviewPlanToolName)
            return _roomToolHandlers.HandleReviewPlan();
        else if (toolName == ReportProgressToolName)
            result = await _roomToolHandlers.HandleReportProgress(callerSlot, input);
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
                    "[Stood down] Tool execution was aborted. Report to Intendant Langur -- call conclude now.",
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

    private static string? GetSummaryText(string toolName, JsonElement input)
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

    private IReadOnlyList<ToolDefinition> BuildLangurTools()
    {
        var tools = _toolRegistry.GetToolDefinitions(ToolScope.Remediation).ToList();

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
            Name: DelegateToolName,
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

    private JsonElement BuildDelegateSchema()
    {
        var modelNames = _llmFactory.Models.Keys.ToList();
        var modelDesc = modelNames.Count > 1
            ? $"Optional model profile for this Ranger. Available: {string.Join(", ", modelNames)}. Omit to use the default ({_llmFactory.DefaultProfileName})."
            : "Model profile (only one configured, typically omitted).";

        var schema = $$"""
        {
            "type": "object",
            "properties": {
                "role": { "type": "string", "description": "Brief role description, e.g. 'cluster verifier', 'config inspector', 'log analyst'" },
                "task": { "type": "string", "description": "Specific task to perform. Be precise about what to inspect, verify, or gather." },
                "model": { "type": "string", "description": "{{modelDesc}}" }
            },
            "required": ["role", "task"]
        }
        """;
        return JsonDocument.Parse(schema).RootElement.Clone();
    }

    private static bool HasTerminalToolResult(RoomEvent.LlmContext ctx, IReadOnlySet<string> terminalTools)
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

