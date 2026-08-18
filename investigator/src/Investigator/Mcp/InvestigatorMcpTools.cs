using System.ComponentModel;
using System.Text.Json;
using Investigator.Models;
using Investigator.Services;
using Investigator.Tools;
using ModelContextProtocol.Server;

namespace Investigator.Mcp;

[McpServerToolType]
public sealed class InvestigatorMcpTools(
    ConversationStore store,
    InvestigationOrchestrator orchestrator,
    WorkspaceManager workspaceManager,
    ToolRegistry toolRegistry,
    McpSessionContext sessionContext,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<InvestigatorMcpTools>();

    [McpServerTool, Description(
        "Start a new CI infrastructure investigation. " +
        "The investigator's AI agents will autonomously diagnose the problem using " +
        "OpenShift, AWS, Prow, Prometheus, GitHub, and other tools. " +
        "Returns a conversation ID for polling status and a URL to watch live progress.")]
    public async Task<string> investigate(
        [Description("Describe the problem to investigate, e.g. 'Why is job e2e-aws-serial failing on build01?'")] string message,
        CancellationToken ct)
    {
        var session = store.CreateSession();
        session.WorkspacePath = workspaceManager.CreateWorkspace(session.Id);

        const string subscriberId = "__mcp__";
        orchestrator.StartAsync(session.Id, session, subscriberId);
        await orchestrator.PostUserMessageAsync(session.Id, message, ct);
        orchestrator.Unsubscribe(session.Id, subscriberId);

        _logger.LogInformation("MCP: Investigation started: {ConversationId}", session.Id);

        return JsonSerializer.Serialize(new
        {
            conversationId = session.Id,
            url = $"/c/{session.Id}/view",
        });
    }

    [McpServerTool, Description(
        "Send a follow-up message to a running investigation. " +
        "Use this to add context, redirect focus, or ask the investigator to dig deeper into a specific area.")]
    public async Task<string> follow_up(
        [Description("The conversation ID returned by investigate")] string conversationId,
        [Description("Follow-up message to send to the investigation")] string message,
        CancellationToken ct)
    {
        if (!orchestrator.IsRunning(conversationId))
        {
            var session = store.TryGetSession(conversationId);
            if (session is null)
                return JsonSerializer.Serialize(new { error = "not_found", detail = "Investigation not found." });
            return JsonSerializer.Serialize(new { error = "not_running", detail = "Investigation has completed." });
        }

        await orchestrator.PostUserMessageAsync(conversationId, message, ct);
        return JsonSerializer.Serialize(new { status = "sent" });
    }

    [McpServerTool, Description(
        "Check the current status of an investigation. " +
        "Use this to determine whether the investigation is still running or has concluded.")]
    public string get_status(
        [Description("The conversation ID returned by investigate")] string conversationId)
    {
        var session = store.TryGetSession(conversationId);
        if (session is null)
            return JsonSerializer.Serialize(new { error = "not_found" });

        var view = session.Investigation.CurrentView;
        var conclusion = view.Items.OfType<ConversationItem.Conclusion>().LastOrDefault();
        var pending = session.InvestigationEventLog.FindPendingUserRequest();

        return JsonSerializer.Serialize(new
        {
            phase = view.Phase.ToString().ToLowerInvariant(),
            isInvestigating = view.IsInvestigating,
            hasWorkingAgents = view.HasWorkingAgents,
            isComplete = !view.IsInvestigating && !view.HasWorkingAgents && conclusion is not null,
            findingCount = view.Items.OfType<ConversationItem.Finding>().Count(),
            hasConclusion = conclusion is not null,
            totalCost = view.TotalCost,
            // Distinguishes "still thinking" from "parked waiting on you". Answer with
            // follow_up or steer(nudge) to unblock it.
            isWaitingForYou = pending is not null,
            pendingQuestion = pending is null ? null : new
            {
                seq = pending.Seq,
                askedAt = pending.Timestamp,
                text = pending.Text,
                waitingForSeconds = (int)(DateTimeOffset.UtcNow - pending.Timestamp).TotalSeconds,
            },
        });
    }

    [McpServerTool, Description(
        "Retrieve findings and conclusion from an investigation. " +
        "Returns incremental findings, sub-agent (scout) reports, and the final conclusion with evidence chain and fix suggestion.")]
    public string get_findings(
        [Description("The conversation ID returned by investigate")] string conversationId)
    {
        var session = store.TryGetSession(conversationId);
        if (session is null)
            return JsonSerializer.Serialize(new { error = "not_found" });

        var view = session.Investigation.CurrentView;

        var findings = view.Items.OfType<ConversationItem.Finding>()
            .Select(f => new { f.Title, f.Description })
            .ToList();

        var scoutReports = view.Items.OfType<ConversationItem.ScoutReport>()
            .Select(r => new
            {
                scoutId = r.ScoutId,
                report = r.Report,
                summary = r.Summary,
                evidence = r.Evidence,
                fix = r.Fix,
            })
            .ToList();

        var conclusion = view.Items.OfType<ConversationItem.Conclusion>().LastOrDefault();

        return JsonSerializer.Serialize(new
        {
            findings,
            scoutReports,
            conclusion = conclusion is null ? null : new
            {
                summary = conclusion.Content,
                headline = conclusion.Headline,
                evidence = conclusion.Evidence,
                fix = conclusion.Fix,
            },
        });
    }

    [McpServerTool, Description(
        "List past and active investigations. " +
        "Returns a summary of each investigation including its status and case description.")]
    public string list_investigations(
        [Description("Maximum number of investigations to return. Defaults to 20.")] int? count)
    {
        var all = store.GetAllSessionInfo();
        var results = all
            .OrderByDescending(i => i.StartedAt)
            .Take(count ?? 20)
            .Select(i => new
            {
                conversationId = i.Id,
                summary = i.CaseSummary,
                startedAt = i.StartedAt,
                isActive = i.HasWorkingAgents,
                hasRemediation = i.HasRemediation,
            })
            .ToList();

        return JsonSerializer.Serialize(new { investigations = results });
    }

    private const int MaxWaitSeconds = 50;
    private const int DefaultMaxEntries = 100;
    private const int DefaultMaxChars = 40_000;

    [McpServerTool, Description(
        "Read the full step-by-step transcript of an investigation: every agent message, " +
        "every tool call with its command, and every tool result with exit code and summary. " +
        "Paged with a cursor -- pass the returned nextSeq as sinceSeq to continue. " +
        "Tool output is clipped; use get_output with the returned outputFile to read the rest.")]
    public string get_transcript(
        [Description("The conversation ID returned by investigate")] string conversationId,
        [Description("Return only events after this sequence number. Omit or 0 to start from the beginning.")] int? sinceSeq,
        [Description("Maximum events to return. Defaults to 100.")] int? maxEntries,
        [Description("Approximate character budget for the response. Defaults to 40000.")] int? maxChars,
        [Description("Restrict to one agent, e.g. 'little-bear' or a scout's id.")] string? agentId,
        [Description("Restrict to one kind: text, tool_call, tool_result, turn, session_ended.")] string? kind)
    {
        var session = store.TryGetSession(conversationId);
        if (session is null)
            return JsonSerializer.Serialize(new { error = "not_found" });

        var page = session.InvestigationEventLog.Read(
            sinceSeq ?? 0, maxEntries ?? DefaultMaxEntries, maxChars ?? DefaultMaxChars, agentId, kind);

        return SerializePage(conversationId, page, session.InvestigationEventLog);
    }

    [McpServerTool, Description(
        "Wait for new activity in a running investigation, then return it. Blocks up to " +
        "waitSeconds and returns whatever has accumulated -- possibly nothing. Use this to " +
        "follow an investigation live: call it in a loop, passing the previous nextSeq.")]
    public async Task<string> poll(
        [Description("The conversation ID returned by investigate")] string conversationId,
        [Description("Return only events after this sequence number.")] int? sinceSeq,
        [Description("Seconds to wait for new activity. Clamped to 50. Defaults to 25.")] int? waitSeconds,
        [Description("Maximum events to return. Defaults to 100.")] int? maxEntries,
        [Description("Approximate character budget for the response. Defaults to 40000.")] int? maxChars,
        CancellationToken ct)
    {
        var session = store.TryGetSession(conversationId);
        if (session is null)
            return JsonSerializer.Serialize(new { error = "not_found" });

        var cursor = sinceSeq ?? 0;
        var log = session.InvestigationEventLog;

        var page = log.Read(cursor, maxEntries ?? DefaultMaxEntries, maxChars ?? DefaultMaxChars);
        if (page.Entries.Count > 0)
            return SerializePage(conversationId, page, session.InvestigationEventLog);

        // Nothing new yet. Park on the orchestrator's per-subscriber tick until something
        // happens or the budget expires, so following an investigation does not require
        // the caller to spin.
        var wait = Math.Clamp(waitSeconds ?? 25, 0, MaxWaitSeconds);
        if (wait > 0 && orchestrator.IsRunning(conversationId))
        {
            var subscriberId = $"__mcp_poll_{Guid.NewGuid():N}";
            var reader = orchestrator.Subscribe(conversationId, subscriberId);
            if (reader is not null)
            {
                try
                {
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    budget.CancelAfter(TimeSpan.FromSeconds(wait));
                    await reader.WaitToReadAsync(budget.Token);
                }
                catch (OperationCanceledException)
                {
                    // Timed out, or the caller gave up. Either way return the current page.
                }
                finally
                {
                    orchestrator.Unsubscribe(conversationId, subscriberId);
                }

                page = log.Read(cursor, maxEntries ?? DefaultMaxEntries, maxChars ?? DefaultMaxChars);
            }
        }

        return SerializePage(conversationId, page, session.InvestigationEventLog);
    }

    [McpServerTool, Description(
        "Read a tool output file in full, by line range. Paths come from the outputFile " +
        "field on tool_result entries in get_transcript. This is how you inspect a large " +
        "build log or must-gather without pulling it all through context.")]
    public string get_output(
        [Description("The conversation ID returned by investigate")] string conversationId,
        [Description("Workspace-relative path, e.g. 'tool_outputs/031-run_oc.txt'")] string outputFile,
        [Description("First line to return, 1-based. Defaults to 1.")] int? offset,
        [Description("Maximum lines to return. Defaults to 200.")] int? limit)
    {
        var session = store.TryGetSession(conversationId);
        if (session is null)
            return JsonSerializer.Serialize(new { error = "not_found" });
        if (session.WorkspacePath is null)
            return JsonSerializer.Serialize(new { error = "no_workspace" });

        // outputFile comes back to us from a model-authored transcript, so confine it to
        // the conversation's own workspace before touching the filesystem.
        var root = Path.GetFullPath(session.WorkspacePath);
        var resolved = Path.GetFullPath(Path.Combine(root, outputFile));
        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return JsonSerializer.Serialize(new { error = "path_outside_workspace" });
        if (!File.Exists(resolved))
            return JsonSerializer.Serialize(new { error = "not_found", detail = outputFile });

        var start = Math.Max(1, offset ?? 1);
        var take = Math.Clamp(limit ?? 200, 1, 5_000);

        var lines = File.ReadLines(resolved).Skip(start - 1).Take(take).ToList();
        var total = File.ReadLines(resolved).Count();

        return JsonSerializer.Serialize(new
        {
            path = outputFile,
            totalLines = total,
            offset = start,
            lineCount = lines.Count,
            hasMore = start - 1 + lines.Count < total,
            content = string.Join("\n", lines),
        });
    }

    [McpServerTool, Description(
        "Steer a running investigation. 'nudge' sends the lead a redirection; 'recall' asks " +
        "a scout to report back immediately with what it has; 'stand_down' aborts a scout's " +
        "in-flight tool and makes it report; 'cancel' stops the whole investigation.")]
    public async Task<string> steer(
        [Description("The conversation ID returned by investigate")] string conversationId,
        [Description("One of: nudge, recall, stand_down, cancel")] string action,
        [Description("Scout name. Required for recall and stand_down.")] string? agentName,
        [Description("Message text. Required for nudge.")] string? note,
        CancellationToken ct)
    {
        if (!orchestrator.IsRunning(conversationId))
        {
            var known = store.TryGetSession(conversationId);
            return JsonSerializer.Serialize(new
            {
                error = known is null ? "not_found" : "not_running",
            });
        }

        switch (action?.ToLowerInvariant())
        {
            case "nudge":
                if (string.IsNullOrWhiteSpace(note))
                    return JsonSerializer.Serialize(new { error = "note_required" });
                await orchestrator.PostUserMessageAsync(conversationId, note, ct);
                break;

            case "recall":
                if (string.IsNullOrWhiteSpace(agentName))
                    return JsonSerializer.Serialize(new { error = "agent_name_required" });
                await orchestrator.RecallSubAgentAsync(conversationId, agentName);
                break;

            case "stand_down":
                if (string.IsNullOrWhiteSpace(agentName))
                    return JsonSerializer.Serialize(new { error = "agent_name_required" });
                await orchestrator.StandDownSubAgentAsync(conversationId, agentName);
                break;

            case "cancel":
                orchestrator.Cancel(conversationId);
                break;

            default:
                return JsonSerializer.Serialize(new
                {
                    error = "unknown_action",
                    detail = "Expected one of: nudge, recall, stand_down, cancel",
                });
        }

        _logger.LogInformation("MCP: steer {Action} on {ConversationId}", action, conversationId);
        return JsonSerializer.Serialize(new { status = "applied", action });
    }

    private string SerializePage(string conversationId, TranscriptPage page, ProjectedEventLog log)
    {
        var pending = log.FindPendingUserRequest();

        return JsonSerializer.Serialize(new
        {
            conversationId,
            running = orchestrator.IsRunning(conversationId),
            events = page.Entries,
            nextSeq = page.NextSeq,
            highestSeq = page.HighestSeq,
            truncated = page.Truncated,
            gap = page.Gap,
            // Non-null means the investigator asked the human something and its turn ended;
            // it will stay parked until follow_up or steer(nudge) delivers an answer.
            pendingQuestion = pending is null ? null : new
            {
                seq = pending.Seq,
                askedAt = pending.Timestamp,
                from = pending.From,
                text = pending.Text,
                waitingForSeconds = (int)(DateTimeOffset.UtcNow - pending.Timestamp).TotalSeconds,
            },
        });
    }

    [McpServerTool, Description(
        "Search the knowledge base for known patterns, past findings, and investigation playbooks. " +
        "Useful for checking if a similar issue has been seen before.")]
    public async Task<string> search_knowledge(
        [Description("Search query describing the issue or pattern to look for")] string query,
        [Description("Maximum number of results to return. Defaults to 5.")] int? count,
        CancellationToken ct)
    {
        var parameters = JsonSerializer.SerializeToElement(new
        {
            action = "search",
            query,
            count = count ?? 5,
        });

        var context = sessionContext.CreateToolContext("mcp-search");
        var (result, _, _) = await toolRegistry.InvokeAsync("casebook", parameters, context, ct);
        return result.Output;
    }
}
