using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Investigator.Services;

internal static class SubAgentHelpers
{
    internal static AgentRunner.ToolExecutionResult Dismiss(
        ConcurrentDictionary<string, AgentRoom.AgentSlot> agents,
        string leadId, JsonElement input, string subAgentLabel, string leadName, ILogger logger)
    {
        var name = input.TryGetProperty("agent_name", out var an) ? an.GetString() ?? "" : "";

        if (!agents.TryGetValue(name, out var slot) || slot.Id == leadId)
            return new AgentRunner.ToolExecutionResult($"No {subAgentLabel} by the name of '{name}' is present.");

        if (!slot.Idle)
            return new AgentRunner.ToolExecutionResult(
                $"{name} is busy. Wait until they are idle, or use recall first.");

        slot.Dismissed = true;
        logger.LogInformation("{Label} {Name} dismissed by {Lead}", subAgentLabel, name, leadName);
        return new AgentRunner.ToolExecutionResult($"{name} dismissed.");
    }

    internal static AgentRunner.ToolExecutionResult Recall(
        ConcurrentDictionary<string, AgentRoom.AgentSlot> agents,
        string leadId, JsonElement input, string subAgentLabel, string locationName, ILogger logger, string leadName)
    {
        var name = input.TryGetProperty("agent_name", out var an) ? an.GetString() ?? "" : "";

        if (!agents.TryGetValue(name, out var slot) || slot.Id == leadId)
            return new AgentRunner.ToolExecutionResult(
                $"No {subAgentLabel} by the name of '{name}' is abroad.");

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
        logger.LogInformation("{Label} {Name} recalled by {Lead}", subAgentLabel, name, leadName);
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
            return $"No {subAgentLabel}s have been dispatched.";

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
            sb.AppendLine($"- {name} ({slot.Role}): {status}");
        }
        return sb.ToString();
    }
}
