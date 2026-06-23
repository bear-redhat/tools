using System.Collections.Concurrent;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Tools;

namespace Investigator.Services;

internal sealed class RoomToolHandlers
{
    private readonly ConcurrentDictionary<string, AgentRoom.AgentSlot> _agents;
    private readonly string _leadId;
    private readonly ILogger _logger;

    internal RoomToolHandlers(
        ConcurrentDictionary<string, AgentRoom.AgentSlot> agents,
        string leadId,
        ILogger logger)
    {
        _agents = agents;
        _leadId = leadId;
        _logger = logger;
    }

    internal Task<AgentRunner.ToolExecutionResult> HandleConclude(
        AgentRoom.AgentSlot callerSlot, AgentRunner.Config callerConfig,
        JsonElement input)
    {
        var (evidence, fix, summary) = ParseConcludeParams(input);

        if (callerSlot.Id == _leadId)
        {
            var undismissed = _agents
                .Where(kv => kv.Value.Id != _leadId)
                .Where(kv => !kv.Value.Dismissed)
                .ToList();
            if (undismissed.Count > 0)
            {
                var busy = undismissed.Where(kv => !kv.Value.Idle).Select(kv => kv.Key).ToList();
                var idle = undismissed.Where(kv => kv.Value.Idle).Select(kv => kv.Key).ToList();
                var parts = new List<string>();
                if (busy.Count > 0) parts.Add($"working: {string.Join(", ", busy)}");
                if (idle.Count > 0) parts.Add($"idle (dismiss first): {string.Join(", ", idle)}");
                return Task.FromResult(new AgentRunner.ToolExecutionResult(
                    Output: $"You cannot conclude yet -- {undismissed.Count} Scouts not yet dismissed ({string.Join("; ", parts)}). Dismiss idle Scouts before concluding."));
            }

            return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "The matter is concluded."));
        }

        _logger.LogInformation("Scout {Name} reporting back with {EvidenceSteps} evidence steps",
            callerConfig.Name, evidence?.Steps.Count ?? 0);

        return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "Report delivered."));
    }

    internal Task<AgentRunner.ToolExecutionResult> HandlePresentFinding(
        AgentRoom.AgentSlot callerSlot, JsonElement input)
    {
        return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "Finding noted."));
    }

    internal Task<AgentRunner.ToolExecutionResult> HandleMessage(
        AgentRoom.AgentSlot callerSlot, JsonElement input)
    {
        var to = input.TryGetProperty("to", out var toVal) ? toVal.GetString() : null;
        if (to is null)
            return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "'to' is required."));

        if (to is "user" or "client")
        {
            if (callerSlot.Id == _leadId)
                return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "Message sent to the client."));
            return Task.FromResult(new AgentRunner.ToolExecutionResult(
                Output: "Only the lead may message the client directly. Message your dispatcher instead."));
        }

        if (_agents.TryGetValue(to, out var targetSlot) && targetSlot.Id != callerSlot.Id)
        {
            targetSlot.HasReported = false;
            return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: $"Message sent to {to}."));
        }

        return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: $"Unknown recipient '{to}'."));
    }

    internal AgentRunner.ToolExecutionResult HandleDismiss(AgentRoom.AgentSlot caller, JsonElement input) =>
        SubAgentHelpers.Dismiss(_agents, _leadId, caller.Id, input, "Scout", "Little Bear", _logger);

    internal AgentRunner.ToolExecutionResult HandleRecall(AgentRoom.AgentSlot caller, JsonElement input) =>
        SubAgentHelpers.Recall(_agents, _leadId, caller.Id, input, "Scout", "Banyan Row", _logger, caller.Name);

    internal bool HasActiveScouts() =>
        SubAgentHelpers.HasActiveSubAgents(_agents, _leadId);

    internal string BuildCheckAgentsResponse() =>
        SubAgentHelpers.BuildCheckAgentsResponse(_agents, _leadId, "Agents afield", "Scout");

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
}
