using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Tools;

namespace Investigator.Services;

internal sealed class RemediationToolHandlers
{
    private const string LeadAgentId = "langur";
    private const string LeadAgentName = "Intendant G. Langur";

    private readonly ConcurrentDictionary<string, InvestigationRoom.AgentSlot> _agents;
    private readonly ILogger _logger;
    private readonly Func<RemediationPlan?> _getPlan;

    internal RemediationToolHandlers(
        ConcurrentDictionary<string, InvestigationRoom.AgentSlot> agents,
        ILogger logger,
        Func<RemediationPlan?> getPlan)
    {
        _agents = agents;
        _logger = logger;
        _getPlan = getPlan;
    }

    // ── Sign-off (Langur only) ──────────────────────────────────────────

    internal Task<AgentRunner.ToolExecutionResult> HandleSignOff(
        InvestigationRoom.AgentSlot callerSlot, AgentRunner.Config callerConfig,
        JsonElement input)
    {
        if (callerSlot.Id != LeadAgentId)
            return Task.FromResult(new AgentRunner.ToolExecutionResult(
                Output: "Only Intendant Langur may sign off the remediation."));

        var runningRangers = _agents
            .Where(kv => kv.Value.Id != LeadAgentId)
            .Where(kv => !kv.Value.Idle)
            .ToList();
        if (runningRangers.Count > 0)
        {
            var names = string.Join(", ", runningRangers.Select(kv => kv.Key));
            return Task.FromResult(new AgentRunner.ToolExecutionResult(
                Output: $"You cannot sign off yet -- {runningRangers.Count} Rangers are still in the field ({names}). Recall or await their reports before signing off."));
        }

        return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "The remediation is signed off."));
    }

    // ── Conclude (Rangers only) ─────────────────────────────────────────

    internal Task<AgentRunner.ToolExecutionResult> HandleConclude(
        InvestigationRoom.AgentSlot callerSlot, AgentRunner.Config callerConfig,
        JsonElement input)
    {
        var (evidence, fix, summary) = ParseConcludeParams(input);

        _logger.LogInformation("Ranger {Name} reporting back with {EvidenceSteps} evidence steps",
            callerConfig.Name, evidence?.Steps.Count ?? 0);

        return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "Report delivered."));
    }

    // ── Present plan ────────────────────────────────────────────────────

    internal Task<AgentRunner.ToolExecutionResult> HandlePresentPlan(
        InvestigationRoom.AgentSlot callerSlot, JsonElement input)
    {
        return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "Plan presented to the Client."));
    }

    // ── Update step ─────────────────────────────────────────────────────

    internal Task<AgentRunner.ToolExecutionResult> HandleUpdateStep(
        InvestigationRoom.AgentSlot callerSlot, JsonElement input)
    {
        var plan = _getPlan();
        if (plan is null)
            return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "No remediation plan has been presented yet."));

        var stepId = input.TryGetProperty("id", out var sid) ? sid.GetString() ?? "" : "";
        var step = plan.Steps.FirstOrDefault(s => s.Id == stepId);
        if (step is null)
            return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: $"No plan step with id '{stepId}' was found."));

        var statusStr = input.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
        return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: $"Step {stepId} updated to {statusStr}."));
    }

    // ── Review plan ─────────────────────────────────────────────────────

    internal AgentRunner.ToolExecutionResult HandleReviewPlan()
    {
        var plan = _getPlan();
        if (plan is null)
            return new AgentRunner.ToolExecutionResult(Output: "No remediation plan has been presented yet.");

        if (plan.Steps.Count == 0)
            return new AgentRunner.ToolExecutionResult(Output: "The plan has no steps.");

        var sb = new StringBuilder();
        sb.AppendLine($"Remediation plan ({plan.Steps.Count} steps):");
        foreach (var step in plan.Steps)
        {
            sb.AppendLine($"- [{step.Id}] {step.Title} ({step.Status})");
            if (!string.IsNullOrWhiteSpace(step.Note))
                sb.AppendLine($"  Note: {step.Note}");
        }
        return new AgentRunner.ToolExecutionResult(Output: sb.ToString());
    }

    // ── Report progress ───────────────────────────────────────────────────

    internal Task<AgentRunner.ToolExecutionResult> HandleReportProgress(
        InvestigationRoom.AgentSlot callerSlot, JsonElement input)
    {
        return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "Progress noted."));
    }

    // ── Message ──────────────────────────────────────────────────────────

    internal Task<AgentRunner.ToolExecutionResult> HandleMessage(
        InvestigationRoom.AgentSlot callerSlot, JsonElement input)
    {
        var to = input.TryGetProperty("to", out var toVal) ? toVal.GetString() ?? "" : "";

        if (callerSlot.Id == LeadAgentId && _agents.TryGetValue(to, out var targetSlot)
            && targetSlot.Id != LeadAgentId)
        {
            return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: $"Message sent to {to}."));
        }

        if (callerSlot.Id == LeadAgentId && to is "user" or "client")
        {
            return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "Message sent to the client."));
        }

        if (callerSlot.Id != LeadAgentId)
        {
            return Task.FromResult(new AgentRunner.ToolExecutionResult(
                Output: $"Message delivered to {LeadAgentName}. Wait for a reply."));
        }

        return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: $"Unknown recipient '{to}'."));
    }

    // ── Dismiss ranger ──────────────────────────────────────────────────

    internal AgentRunner.ToolExecutionResult HandleDismiss(JsonElement input)
    {
        var name = input.TryGetProperty("agent_name", out var an) ? an.GetString() ?? "" : "";

        if (!_agents.TryGetValue(name, out var slot) || slot.Id == LeadAgentId)
            return new AgentRunner.ToolExecutionResult($"No Ranger by the name of '{name}' is present.");

        if (!slot.Idle)
            return new AgentRunner.ToolExecutionResult(
                $"{name} is busy. Wait until they are idle, or use recall first.");

        _logger.LogInformation("Ranger {Name} dismissed by {Lead}", name, LeadAgentName);
        return new AgentRunner.ToolExecutionResult($"{name} dismissed.");
    }

    // ── Recall ranger ───────────────────────────────────────────────────

    internal Task<AgentRunner.ToolExecutionResult> HandleRecall(JsonElement input)
    {
        var name = input.TryGetProperty("agent_name", out var an) ? an.GetString() ?? "" : "";

        if (!_agents.TryGetValue(name, out var slot) || slot.Id == LeadAgentId)
            return Task.FromResult(new AgentRunner.ToolExecutionResult(
                $"No Ranger by the name of '{name}' is abroad."));

        if (slot.Idle)
            return Task.FromResult(new AgentRunner.ToolExecutionResult(
                $"{name} is already idle. Use dismiss to send them on their way, or message to give new instructions."));

        _logger.LogInformation("Ranger {Name} recalled by {Lead}", name, LeadAgentName);
        return Task.FromResult(new AgentRunner.ToolExecutionResult(
            $"Word has been sent to {name}. They will return to the Canopy Post presently."));
    }

    // ── Status queries ──────────────────────────────────────────────────

    internal bool HasActiveRangers() =>
        _agents.Any(kv => kv.Value.Id != LeadAgentId && !kv.Value.Idle);

    internal string BuildCheckAgentsResponse()
    {
        var rangers = _agents.Where(kv => kv.Value.Id != LeadAgentId).ToList();
        if (rangers.Count == 0)
            return "No Rangers have been dispatched.";

        var sb = new StringBuilder();
        sb.AppendLine("Canopy Post Rangers:");
        foreach (var (name, slot) in rangers)
        {
            var status = slot.Idle ? "idle"
                : slot.RunTask switch
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

    // ── Parsing helpers ─────────────────────────────────────────────────

    internal (EvidenceChain?, FixSuggestion?, string Summary) ParseConcludeParams(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning("conclude tool called with no input");
            return (null, null, "(No summary was provided.)");
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
                    Proof: item.TryGetProperty("proof", out var prf) ? prf.GetString() ?? ""
                        : item.TryGetProperty("command", out var cmd) ? cmd.GetString() ?? "" : "",
                    Source: item.TryGetProperty("source", out var src) ? src.GetString() : null));
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

    private static RemediationTarget ParseTarget(JsonElement step)
    {
        if (!step.TryGetProperty("target", out var t) || t.ValueKind != JsonValueKind.Object)
            return new RemediationTarget("unknown");

        return new RemediationTarget(
            Type: t.TryGetProperty("type", out var ty) ? ty.GetString() ?? "unknown" : "unknown",
            Cluster: t.TryGetProperty("cluster", out var c) ? c.GetString() : null,
            Resource: t.TryGetProperty("resource", out var r) ? r.GetString() : null,
            Namespace: t.TryGetProperty("namespace", out var ns) ? ns.GetString() : null,
            Repo: t.TryGetProperty("repo", out var rp) ? rp.GetString() : null,
            Path: t.TryGetProperty("path", out var p) ? p.GetString() : null,
            LineRange: t.TryGetProperty("line_range", out var lr) ? lr.GetString() : null);
    }

    private static RemediationChange ParseChange(JsonElement step)
    {
        if (!step.TryGetProperty("change", out var ch) || ch.ValueKind != JsonValueKind.Object)
            return new RemediationChange { Type = "unknown" };

        return new RemediationChange
        {
            Type = ch.TryGetProperty("type", out var ty) ? ty.GetString() ?? "unknown" : "unknown",
            CurrentValue = ch.TryGetProperty("current_value", out var cv) ? cv.GetString() : null,
            DesiredValue = ch.TryGetProperty("desired_value", out var dv) ? dv.GetString() : null,
            Commands = ch.TryGetProperty("commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array
                ? cmds.EnumerateArray().Select(c => c.GetString() ?? "").ToList()
                : null,
            PatchFile = ch.TryGetProperty("patch_file", out var pf) ? pf.GetString() : null,
            Warnings = ch.TryGetProperty("warnings", out var w) ? w.GetString() : null,
        };
    }

    private static RemediationValidation ParseValidation(JsonElement step)
    {
        if (!step.TryGetProperty("validation", out var v) || v.ValueKind != JsonValueKind.Object)
            return new RemediationValidation("", []);

        var commands = v.TryGetProperty("commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array
            ? cmds.EnumerateArray().Select(c => c.GetString() ?? "").ToList()
            : [];

        return new RemediationValidation(
            Description: v.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
            Commands: commands,
            Expected: v.TryGetProperty("expected", out var e) ? e.GetString() : null);
    }

    private static string FormatTarget(RemediationTarget target)
    {
        var parts = new List<string> { target.Type };
        if (!string.IsNullOrWhiteSpace(target.Cluster)) parts.Add($"cluster={target.Cluster}");
        if (!string.IsNullOrWhiteSpace(target.Resource)) parts.Add($"resource={target.Resource}");
        if (!string.IsNullOrWhiteSpace(target.Namespace)) parts.Add($"ns={target.Namespace}");
        if (!string.IsNullOrWhiteSpace(target.Repo)) parts.Add($"repo={target.Repo}");
        if (!string.IsNullOrWhiteSpace(target.Path)) parts.Add($"path={target.Path}");
        if (!string.IsNullOrWhiteSpace(target.LineRange)) parts.Add($"lines={target.LineRange}");
        return string.Join(", ", parts);
    }
}
