using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Tools;

namespace Investigator.Services;

internal sealed class RoomToolHandlers
{
    private readonly ConcurrentDictionary<string, InvestigationRoom.AgentSlot> _agents;
    private readonly WorkspaceManager _workspaceManager;
    private readonly ILogger _logger;
    private readonly Func<AgentEvent, ValueTask> _emitToUi;

    internal RoomToolHandlers(
        ConcurrentDictionary<string, InvestigationRoom.AgentSlot> agents,
        WorkspaceManager workspaceManager,
        ILogger logger,
        Func<AgentEvent, ValueTask> emitToUi)
    {
        _agents = agents;
        _workspaceManager = workspaceManager;
        _logger = logger;
        _emitToUi = emitToUi;
    }

    internal async Task<AgentRunner.ToolExecutionResult> HandleConclude(
        InvestigationRoom.AgentSlot callerSlot, AgentRunner.Config callerConfig,
        JsonElement input, string workspacePath, CancellationToken ct)
    {
        var (evidence, fix, summary) = ParseConcludeParams(input);

        if (callerSlot.Id == "little-bear")
        {
            var runningScouts = _agents
                .Where(kv => kv.Value.Id != "little-bear")
                .Where(kv => kv.Value.RunTask is null || !kv.Value.RunTask.IsCompleted)
                .ToList();
            if (runningScouts.Count > 0)
            {
                var names = string.Join(", ", runningScouts.Select(kv => kv.Key));
                return new AgentRunner.ToolExecutionResult(
                    Output: $"Cannot conclude: {runningScouts.Count} Scouts are still working ({names}). Wait for their reports before concluding.",
                    Concluded: false);
            }

            var fullText = summary;
            if (!string.IsNullOrWhiteSpace(summary))
            {
                await _emitToUi(new AgentEvent.Conclusion($"conclude-lb", fullText, evidence, fix));
                await _workspaceManager.AppendTranscriptAsync(workspacePath, new
                {
                    ts = DateTimeOffset.UtcNow,
                    type = "conclusion",
                    summary,
                    evidence,
                    fix,
                });
            }

            await _emitToUi(new AgentEvent.StatusChanged("conclude-lb-status", false));
            return new AgentRunner.ToolExecutionResult(Output: "Concluded.", Concluded: true);
        }

        var report = summary ?? "(No summary provided)";

        _logger.LogInformation("Scout {Name} reporting back with {EvidenceSteps} evidence steps",
            callerConfig.Name, evidence?.Steps.Count ?? 0);

        await _workspaceManager.AppendTranscriptAsync(workspacePath, new
        {
            ts = DateTimeOffset.UtcNow,
            type = "scout_report",
            agent = callerConfig.Name,
            summary,
            evidence,
            fix,
        });

        await _emitToUi(new AgentEvent.SubAgentDone($"sa-{callerConfig.Name}-done", callerConfig.Name, report, evidence, fix));

        if (_agents.TryGetValue("Little Bear", out var lbSlot))
        {
            await lbSlot.Inbox.Writer.WriteAsync(
                new RoomMessage(callerConfig.Name, $"[reports]\n{report}"), ct);
        }

        return new AgentRunner.ToolExecutionResult(Output: "Report delivered.", Concluded: true);
    }

    internal async Task<AgentRunner.ToolExecutionResult> HandlePresentFinding(
        InvestigationRoom.AgentSlot callerSlot, JsonElement input)
    {
        var title = input.TryGetProperty("title", out var ft) ? ft.GetString() ?? "" : "";
        var desc = input.TryGetProperty("description", out var fd) ? fd.GetString() ?? "" : "";
        await _emitToUi(new AgentEvent.Finding($"finding-{callerSlot.Id}", title, desc));
        return new AgentRunner.ToolExecutionResult(Output: "Finding noted.");
    }

    internal async Task<AgentRunner.ToolExecutionResult> HandleReplyTo(
        InvestigationRoom.AgentSlot callerSlot, JsonElement input, CancellationToken ct)
    {
        var targetName = input.TryGetProperty("agent_name", out var an) ? an.GetString() ?? "" : "";
        var replyMsg = input.TryGetProperty("message", out var rm) ? rm.GetString() ?? "" : "";

        if (_agents.TryGetValue(targetName, out var targetSlot))
        {
            await targetSlot.Inbox.Writer.WriteAsync(new RoomMessage("Little Bear", replyMsg), ct);
            if (!string.IsNullOrWhiteSpace(replyMsg))
                await _emitToUi(new AgentEvent.Message($"reply-{callerSlot.Id}", replyMsg));
            return new AgentRunner.ToolExecutionResult(Output: $"Reply sent to {targetName}.");
        }

        return new AgentRunner.ToolExecutionResult(Output: $"Error: no Scout named '{targetName}' is waiting for a reply.");
    }

    internal string BuildCheckAgentsResponse()
    {
        var scouts = _agents.Where(kv => kv.Value.Id != "little-bear").ToList();
        if (scouts.Count == 0)
            return "No Scouts have been dispatched.";

        var sb = new StringBuilder();
        sb.AppendLine("Canopy Scouts:");
        foreach (var (name, slot) in scouts)
        {
            var status = slot.RunTask switch
            {
                null => "working",
                { IsCompletedSuccessfully: true } => "completed",
                { IsFaulted: true } => "failed",
                _ => "working",
            };
            sb.AppendLine($"- {name} ({slot.Role}): {status}");
        }
        return sb.ToString();
    }

    internal (EvidenceChain?, FixSuggestion?, string Summary) ParseConcludeParams(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning("conclude tool called with no input");
            return (null, null, "(No summary provided)");
        }

        var summary = input.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";

        EvidenceChain? evidence = null;
        if (input.TryGetProperty("evidence", out var evidenceArray) && evidenceArray.ValueKind == JsonValueKind.Array)
        {
            var steps = new List<EvidenceStep>();
            foreach (var item in evidenceArray.EnumerateArray())
            {
                steps.Add(new EvidenceStep(
                    Step: item.TryGetProperty("step", out var st) ? st.GetInt32() : steps.Count + 1,
                    Reasoning: item.TryGetProperty("reasoning", out var r) ? r.GetString() ?? "" : "",
                    Finding: item.TryGetProperty("finding", out var f) ? f.GetString() ?? "" : "",
                    Cluster: item.TryGetProperty("cluster", out var c) ? c.GetString() : null,
                    Command: item.TryGetProperty("command", out var cmd) ? cmd.GetString() ?? "" : ""));
            }
            evidence = new EvidenceChain(steps.OrderBy(s => s.Step).ToList());
        }

        FixSuggestion? fix = null;
        var hasFixDesc = input.TryGetProperty("fix_description", out var fd) && fd.ValueKind == JsonValueKind.String;
        var hasFixCmds = input.TryGetProperty("fix_commands", out var fc) && fc.ValueKind == JsonValueKind.Array;
        if (hasFixDesc || hasFixCmds)
        {
            fix = new FixSuggestion(
                Description: hasFixDesc ? fd.GetString() ?? "" : "",
                Commands: hasFixCmds ? fc.EnumerateArray().Select(c => c.GetString() ?? "").ToList() : [],
                Warning: input.TryGetProperty("fix_warning", out var fw) ? fw.GetString() : null);
        }

        return (evidence, fix, summary);
    }
}
