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

    private readonly ConcurrentDictionary<string, AgentRoom.AgentSlot> _agents;
    private readonly ILogger _logger;
    private readonly Func<RemediationPlan?> _getPlan;

    internal RemediationToolHandlers(
        ConcurrentDictionary<string, AgentRoom.AgentSlot> agents,
        ILogger logger,
        Func<RemediationPlan?> getPlan)
    {
        _agents = agents;
        _logger = logger;
        _getPlan = getPlan;
    }

    // ── Sign-off (Langur only) ──────────────────────────────────────────

    internal Task<AgentRunner.ToolExecutionResult> HandleSignOff(
        AgentRoom.AgentSlot callerSlot, AgentRunner.Config callerConfig,
        JsonElement input)
    {
        if (callerSlot.Id != LeadAgentId)
            return Task.FromResult(new AgentRunner.ToolExecutionResult(
                Output: "Only Intendant Langur may sign off the remediation."));

        var runningRangers = _agents
            .Where(kv => kv.Value.Id != LeadAgentId)
            .Where(kv => !kv.Value.Dismissed)
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
        AgentRoom.AgentSlot callerSlot, AgentRunner.Config callerConfig,
        JsonElement input)
    {
        var (evidence, fix, summary) = ParseConcludeParams(input);

        _logger.LogInformation("Ranger {Name} reporting back with {EvidenceSteps} evidence steps",
            callerConfig.Name, evidence?.Steps.Count ?? 0);

        return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "Report delivered."));
    }

    // ── Present plan ────────────────────────────────────────────────────

    internal Task<AgentRunner.ToolExecutionResult> HandlePresentPlan(
        AgentRoom.AgentSlot callerSlot, JsonElement input)
    {
        return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "Plan presented to the Client."));
    }

    // ── Update step ─────────────────────────────────────────────────────

    internal Task<AgentRunner.ToolExecutionResult> HandleUpdateStep(
        AgentRoom.AgentSlot callerSlot, JsonElement input)
    {
        var plan = _getPlan();
        if (plan is null)
            return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "No remediation plan has been presented yet."));

        var stepId = input.TryGetProperty("id", out var sid) ? sid.GetString() : null;
        if (stepId is null)
            return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "'id' is required."));

        var step = plan.Steps.FirstOrDefault(s => s.Id == stepId);
        if (step is null)
            return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: $"No plan step with id '{stepId}' was found."));

        var statusStr = input.TryGetProperty("status", out var st) ? st.GetString() : null;
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
        AgentRoom.AgentSlot callerSlot, JsonElement input)
    {
        return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "Progress noted."));
    }

    // ── Message ──────────────────────────────────────────────────────────

    internal Task<AgentRunner.ToolExecutionResult> HandleMessage(
        AgentRoom.AgentSlot callerSlot, JsonElement input)
    {
        var to = input.TryGetProperty("to", out var toVal) ? toVal.GetString() : null;
        if (to is null)
            return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "'to' is required."));

        if (to is "user" or "client")
        {
            if (callerSlot.Id == LeadAgentId)
                return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "Message sent to the client."));
            return Task.FromResult(new AgentRunner.ToolExecutionResult(
                Output: "Only the Intendant may address the client directly. Message your dispatcher instead."));
        }

        if (_agents.TryGetValue(to, out var targetSlot) && targetSlot.Id != callerSlot.Id)
        {
            targetSlot.HasReported = false;
            return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: $"Message sent to {to}."));
        }

        return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: $"Unknown recipient '{to}'."));
    }

    // ── Dismiss ranger ──────────────────────────────────────────────────

    internal AgentRunner.ToolExecutionResult HandleDismiss(AgentRoom.AgentSlot caller, JsonElement input) =>
        SubAgentHelpers.Dismiss(_agents, LeadAgentId, caller.Id, input, "Ranger", LeadAgentName, _logger);

    // ── Recall Ranger ────────────────────────────────────────────────────

    internal AgentRunner.ToolExecutionResult HandleRecall(AgentRoom.AgentSlot caller, JsonElement input) =>
        SubAgentHelpers.Recall(_agents, LeadAgentId, caller.Id, input, "Ranger", "the Canopy Post", _logger, caller.Name);

    // ── Refer back ────────────────────────────────────────────────────────

    internal Task<AgentRunner.ToolExecutionResult> HandleReferBack(
        AgentRoom.AgentSlot callerSlot, AgentRunner.Config callerConfig,
        JsonElement input)
    {
        if (callerSlot.Id != LeadAgentId)
            return Task.FromResult(new AgentRunner.ToolExecutionResult(
                Output: "Only Intendant Langur may refer a case back to Banyan Row."));

        var runningRangers = _agents
            .Where(kv => kv.Value.Id != LeadAgentId)
            .Where(kv => !kv.Value.Dismissed)
            .Where(kv => !kv.Value.Idle)
            .ToList();
        if (runningRangers.Count > 0)
        {
            var names = string.Join(", ", runningRangers.Select(kv => kv.Key));
            return Task.FromResult(new AgentRunner.ToolExecutionResult(
                Output: $"You cannot refer back yet -- {runningRangers.Count} Rangers are still in the field ({names}). Recall or await their reports first."));
        }

        var reason = input.TryGetProperty("reason", out var r) ? r.GetString() : null;
        if (string.IsNullOrWhiteSpace(reason))
            return Task.FromResult(new AgentRunner.ToolExecutionResult(
                Output: "'reason' is required -- explain why the case file's diagnosis is incorrect.", ExitCode: 1));

        return Task.FromResult(new AgentRunner.ToolExecutionResult(
            Output: "The case has been referred back to Little Bear at Banyan Row."));
    }

    // ── Status queries ──────────────────────────────────────────────────

    internal bool HasActiveRangers() =>
        SubAgentHelpers.HasActiveSubAgents(_agents, LeadAgentId);

    internal string BuildCheckAgentsResponse() =>
        SubAgentHelpers.BuildCheckAgentsResponse(_agents, LeadAgentId, "Agents afield", "Ranger");

    // ── Parsing helpers ─────────────────────────────────────────────────

    internal (EvidenceChain?, FixSuggestion?, string? Summary) ParseConcludeParams(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning("conclude tool called with no input");
            return (null, null, "(No summary was provided.)");
        }

        var summary = input.TryGetProperty("summary", out var s) ? s.GetString() : null;

        EvidenceChain? evidence = null;
        if (input.TryGetProperty("evidence", out var evidenceArray) && evidenceArray.ValueKind == JsonValueKind.Array)
        {
            var steps = new List<EvidenceStep>();
            foreach (var item in evidenceArray.EnumerateArray())
            {
                steps.Add(new EvidenceStep(
                    Step: item.TryGetProperty("step", out var st) ? st.GetInt32() : steps.Count + 1,
                    Reasoning: item.TryGetProperty("reasoning", out var r) ? r.GetString() : null,
                    Finding: item.TryGetProperty("finding", out var f) ? f.GetString() : null,
                    Cluster: item.TryGetProperty("cluster", out var c) ? c.GetString() : null,
                    Proof: item.TryGetProperty("proof", out var prf) ? prf.GetString()
                        : item.TryGetProperty("command", out var cmd) ? cmd.GetString() : null,
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
                Description: hasFixDesc ? fd.GetString() : null,
                Commands: hasFixCmds ? fc.EnumerateArray().Select(c => c.GetString()).OfType<string>().ToList() : null,
                Warning: input.TryGetProperty("fix_warning", out var fw) ? fw.GetString() : null);
        }

        return (evidence, fix, summary);
    }

    private static RemediationTarget ParseTarget(JsonElement step)
    {
        if (!step.TryGetProperty("target", out var t) || t.ValueKind != JsonValueKind.Object)
            return new RemediationTarget(null);

        return new RemediationTarget(
            Type: t.TryGetProperty("type", out var ty) ? ty.GetString() : null,
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
            return new RemediationChange();

        return new RemediationChange
        {
            Type = ch.TryGetProperty("type", out var ty) ? ty.GetString() : null,
            CurrentValue = ch.TryGetProperty("current_value", out var cv) ? cv.GetString() : null,
            DesiredValue = ch.TryGetProperty("desired_value", out var dv) ? dv.GetString() : null,
            Commands = ch.TryGetProperty("commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array
                ? cmds.EnumerateArray().Select(c => c.GetString()).OfType<string>().ToList()
                : null,
            PatchFile = ch.TryGetProperty("patch_file", out var pf) ? pf.GetString() : null,
            Warnings = ch.TryGetProperty("warnings", out var w) ? w.GetString() : null,
        };
    }

    private static RemediationValidation ParseValidation(JsonElement step)
    {
        if (!step.TryGetProperty("validation", out var v) || v.ValueKind != JsonValueKind.Object)
            return new RemediationValidation(null, null);

        var commands = v.TryGetProperty("commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array
            ? cmds.EnumerateArray().Select(c => c.GetString()).OfType<string>().ToList()
            : (List<string>?)null;

        return new RemediationValidation(
            Description: v.TryGetProperty("description", out var d) ? d.GetString() : null,
            Commands: commands,
            Expected: v.TryGetProperty("expected", out var e) ? e.GetString() : null);
    }

    private static string FormatTarget(RemediationTarget target)
    {
        var parts = new List<string>();
        if (target.Type is not null) parts.Add(target.Type);
        if (!string.IsNullOrWhiteSpace(target.Cluster)) parts.Add($"cluster={target.Cluster}");
        if (!string.IsNullOrWhiteSpace(target.Resource)) parts.Add($"resource={target.Resource}");
        if (!string.IsNullOrWhiteSpace(target.Namespace)) parts.Add($"ns={target.Namespace}");
        if (!string.IsNullOrWhiteSpace(target.Repo)) parts.Add($"repo={target.Repo}");
        if (!string.IsNullOrWhiteSpace(target.Path)) parts.Add($"path={target.Path}");
        if (!string.IsNullOrWhiteSpace(target.LineRange)) parts.Add($"lines={target.LineRange}");
        return string.Join(", ", parts);
    }
}
