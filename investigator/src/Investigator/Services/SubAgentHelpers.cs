using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Investigator.Services;

internal static class SubAgentHelpers
{
    internal static AgentRunner.ToolExecutionResult Dismiss(
        ConcurrentDictionary<string, AgentRoom.AgentSlot> agents,
        string leadId, string callerId, JsonElement input, string subAgentLabel, string leadName, ILogger logger)
    {
        var name = input.TryGetProperty("agent_name", out var an) ? an.GetString() : null;
        if (name is null)
            return new AgentRunner.ToolExecutionResult("'agent_name' is required.");

        if (!agents.TryGetValue(name, out var slot) || slot.Id == leadId)
            return new AgentRunner.ToolExecutionResult($"No {subAgentLabel} by the name of '{name}' is present.");

        if (callerId != leadId && slot.DispatcherId != callerId)
            return new AgentRunner.ToolExecutionResult(
                $"{name} was not dispatched by you. Only their dispatcher or the lead may dismiss them.");

        if (!slot.Idle)
            return new AgentRunner.ToolExecutionResult(
                $"{name} is busy. Wait until they are idle, or use recall first.");

        slot.Dismissed = true;
        logger.LogInformation("{Label} {Name} dismissed by {Caller}", subAgentLabel, name, callerId);
        return new AgentRunner.ToolExecutionResult($"{name} dismissed.");
    }

    internal static AgentRunner.ToolExecutionResult Recall(
        ConcurrentDictionary<string, AgentRoom.AgentSlot> agents,
        string leadId, string callerId, JsonElement input, string subAgentLabel, string locationName, ILogger logger, string callerName)
    {
        var name = input.TryGetProperty("agent_name", out var an) ? an.GetString() : null;
        if (name is null)
            return new AgentRunner.ToolExecutionResult("'agent_name' is required.");

        if (!agents.TryGetValue(name, out var slot) || slot.Id == leadId)
            return new AgentRunner.ToolExecutionResult(
                $"No {subAgentLabel} by the name of '{name}' is abroad.");

        if (callerId != leadId && slot.DispatcherId != callerId)
            return new AgentRunner.ToolExecutionResult(
                $"{name} was not dispatched by you. Only their dispatcher or the lead may recall them.");

        if (slot.Dismissed)
            return new AgentRunner.ToolExecutionResult(
                $"{name} has already been dismissed.");

        if (slot.Idle)
            return new AgentRunner.ToolExecutionResult(
                $"{name} is already idle. Use dismiss to send them on their way, or message to give new instructions.");

        if (slot.Recalled)
            return new AgentRunner.ToolExecutionResult(
                $"{name} has already been recalled.");

        slot.Recalled = true;
        logger.LogInformation("{Label} {Name} recalled by {Caller}", subAgentLabel, name, callerName);
        return new AgentRunner.ToolExecutionResult(
            $"Word has been sent to {name}. They will return to {locationName} presently.");
    }

    internal static bool HasActiveSubAgents(
        ConcurrentDictionary<string, AgentRoom.AgentSlot> agents, string leadId) =>
        agents.Any(kv => kv.Value.Id != leadId && !kv.Value.Dismissed && !kv.Value.Idle);

    internal static string BuildCheckAgentsResponse(
        ConcurrentDictionary<string, AgentRoom.AgentSlot> agents,
        string leadId, string groupHeader, string subAgentLabel)
    {
        var subs = agents.Where(kv => kv.Value.Id != leadId && !kv.Value.Dismissed).ToList();
        if (subs.Count == 0)
            return $"No agents are in the field.";

        var sb = new StringBuilder();
        sb.AppendLine($"{groupHeader}:");
        foreach (var (name, slot) in subs)
        {
            var status = slot.Idle ? "idle"
                : slot.RunTask switch
                {
                    null => "working",
                    { IsCompletedSuccessfully: true } => "completed",
                    { IsFaulted: true } => "failed",
                    _ => "working",
                };

            var tier = slot.CanDelegate ? "analyst" : "field";
            var task = !string.IsNullOrEmpty(slot.TaskDescription)
                ? $": {Truncate(slot.TaskDescription, 80)}" : "";
            sb.Append($"- {name} ({tier}, {slot.Role}{task}) -- status: {status}");

            var dispatcher = agents.Values.FirstOrDefault(a => a.Id == slot.DispatcherId);
            if (dispatcher is not null)
                sb.Append($", dispatched by {dispatcher.Name}");

            if (slot.CcTargets.Count > 0)
            {
                var ccNames = slot.CcTargets
                    .Select(ccId => agents.Values.FirstOrDefault(a => a.Id == ccId)?.Name ?? ccId)
                    .ToList();
                sb.Append($", CC: [{string.Join(", ", ccNames)}]");
            }

            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "\u2026";
}
