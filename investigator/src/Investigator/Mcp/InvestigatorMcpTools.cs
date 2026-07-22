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

        return JsonSerializer.Serialize(new
        {
            phase = view.Phase.ToString().ToLowerInvariant(),
            isInvestigating = view.IsInvestigating,
            hasWorkingAgents = view.HasWorkingAgents,
            isComplete = !view.IsInvestigating && !view.HasWorkingAgents && conclusion is not null,
            findingCount = view.Items.OfType<ConversationItem.Finding>().Count(),
            hasConclusion = conclusion is not null,
            totalCost = view.TotalCost,
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
