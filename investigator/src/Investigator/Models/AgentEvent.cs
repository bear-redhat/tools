using System.Text.Json;

namespace Investigator.Models;

public abstract record AgentEvent(string StepId)
{
    public record Thinking(string StepId, string Text) : AgentEvent(StepId);

    public record ToolCall(string StepId, string Tool, string DisplayCommand, JsonElement Parameters, string? ParentStepId = null) : AgentEvent(StepId);

    public record ToolResult(string StepId, string Tool, string Output, string? OutputFile, int ExitCode, bool TimedOut, string? ParentStepId = null) : AgentEvent(StepId);

    public record Message(string StepId, string Text, bool IsIntermediate = false, string? Recipient = null) : AgentEvent(StepId);

    public record Conclusion(string StepId, string Summary, EvidenceChain? Evidence, FixSuggestion? Fix) : AgentEvent(StepId);

    public record Error(string StepId, string ErrorMessage) : AgentEvent(StepId);

    public record StatusChanged(string StepId, bool IsActive) : AgentEvent(StepId);

    public record Finding(string StepId, string Title, string Description) : AgentEvent(StepId);

    public record ScoutAsked(string StepId, string AgentName, string Question) : AgentEvent(StepId);

    public record SubAgentStarted(string StepId, string AgentName, string Role, string Task, string? ModelProfile = null) : AgentEvent(StepId);

    public record SubAgentToolCall(string StepId, string AgentName, string Tool, string DisplayCommand, Dictionary<string, string>? Context = null) : AgentEvent(StepId);

    public record SubAgentToolResult(string StepId, string AgentName, string Tool, string Output, int ExitCode, bool TimedOut = false) : AgentEvent(StepId);

    public record SubAgentThinking(string StepId, string AgentName, string Text) : AgentEvent(StepId);

    public record SubAgentMessage(string StepId, string AgentName, string Text) : AgentEvent(StepId);

    public record SubAgentDone(string StepId, string AgentName, string Report, EvidenceChain? Evidence = null, FixSuggestion? Fix = null) : AgentEvent(StepId);

    public record SubAgentFailed(string StepId, string AgentName, string Reason) : AgentEvent(StepId);

    public record Usage(string StepId, string AgentName, int InputTokens, int OutputTokens,
        int CacheReadTokens, int CacheCreateTokens, decimal CostDelta,
        string? ModelProfile = null,
        decimal InputPricePerMToken = 0, decimal OutputPricePerMToken = 0,
        decimal CacheReadPricePerMToken = 0, decimal CacheCreationPricePerMToken = 0) : AgentEvent(StepId);

    public record Compaction(string StepId, string AgentName, int TokensBefore, int TokensAfter,
        int InputTokens, int OutputTokens, int CacheReadTokens, int CacheCreateTokens,
        decimal CostDelta,
        string? ModelProfile = null,
        decimal InputPricePerMToken = 0, decimal OutputPricePerMToken = 0,
        decimal CacheReadPricePerMToken = 0, decimal CacheCreationPricePerMToken = 0) : AgentEvent(StepId);
}
