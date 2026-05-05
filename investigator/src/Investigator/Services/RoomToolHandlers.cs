using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Tools;

namespace Investigator.Services;

internal sealed class RoomToolHandlers
{
    private readonly ConcurrentDictionary<string, AgentRoom.AgentSlot> _agents;
    private readonly ILogger _logger;

    internal RoomToolHandlers(
        ConcurrentDictionary<string, AgentRoom.AgentSlot> agents,
        ILogger logger)
    {
        _agents = agents;
        _logger = logger;
    }

    internal Task<AgentRunner.ToolExecutionResult> HandleConclude(
        AgentRoom.AgentSlot callerSlot, AgentRunner.Config callerConfig,
        JsonElement input)
    {
        var (evidence, fix, summary) = ParseConcludeParams(input);

        if (callerSlot.Id == "little-bear")
        {
            var undismissed = _agents
                .Where(kv => kv.Value.Id != "little-bear")
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
        var to = input.TryGetProperty("to", out var toVal) ? toVal.GetString() ?? "" : "";

        if (callerSlot.Id == "little-bear" && _agents.TryGetValue(to, out var targetSlot)
            && targetSlot.Id != "little-bear")
        {
            return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: $"Message sent to {to}."));
        }

        if (callerSlot.Id == "little-bear" && to is "user" or "client")
        {
            return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: "Message sent to the client."));
        }

        if (callerSlot.Id != "little-bear")
        {
            return Task.FromResult(new AgentRunner.ToolExecutionResult(
                Output: "Message delivered to Little Bear. Wait for a reply."));
        }

        return Task.FromResult(new AgentRunner.ToolExecutionResult(Output: $"Unknown recipient '{to}'."));
    }

    internal AgentRunner.ToolExecutionResult HandleDismiss(JsonElement input)
    {
        var name = input.TryGetProperty("agent_name", out var an) ? an.GetString() ?? "" : "";

        if (!_agents.TryGetValue(name, out var slot) || slot.Id == "little-bear")
            return new AgentRunner.ToolExecutionResult($"No Scout by the name of '{name}' is present.");

        if (!slot.Idle)
            return new AgentRunner.ToolExecutionResult(
                $"{name} is busy. Wait until they are idle, or use recall first.");

        slot.Dismissed = true;
        _logger.LogInformation("Scout {Name} dismissed by Little Bear", name);
        return new AgentRunner.ToolExecutionResult($"{name} dismissed.");
    }

    internal Task<AgentRunner.ToolExecutionResult> HandleRecall(JsonElement input)
    {
        var name = input.TryGetProperty("agent_name", out var an) ? an.GetString() ?? "" : "";

        if (!_agents.TryGetValue(name, out var slot) || slot.Id == "little-bear")
            return Task.FromResult(new AgentRunner.ToolExecutionResult(
                $"No Scout by the name of '{name}' is abroad."));

        if (slot.Idle)
            return Task.FromResult(new AgentRunner.ToolExecutionResult(
                $"{name} is already idle. Use dismiss to send them on their way, or message to give new instructions."));

        _logger.LogInformation("Scout {Name} recalled by Little Bear", name);
        return Task.FromResult(new AgentRunner.ToolExecutionResult(
            $"Word has been sent to {name}. They will return to Banyan Row presently."));
    }

    internal bool HasActiveScouts() =>
        _agents.Any(kv => kv.Value.Id != "little-bear" && !kv.Value.Idle);

    internal string BuildCheckAgentsResponse()
    {
        var scouts = _agents.Where(kv => kv.Value.Id != "little-bear").ToList();
        if (scouts.Count == 0)
            return "No Scouts have been dispatched.";

        var sb = new StringBuilder();
        sb.AppendLine("Banyan Row Scouts:");
        foreach (var (name, slot) in scouts)
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
}
