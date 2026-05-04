using System.Text.Json.Serialization;

namespace Investigator.Models;

[JsonDerivedType(typeof(ConversationItem.UserMessage), "user_message")]
[JsonDerivedType(typeof(ConversationItem.Welcome), "welcome")]
[JsonDerivedType(typeof(ConversationItem.Thinking), "thinking")]
[JsonDerivedType(typeof(ConversationItem.AgentMessage), "agent_message")]
[JsonDerivedType(typeof(ConversationItem.Conclusion), "conclusion")]
[JsonDerivedType(typeof(ConversationItem.Error), "error")]
[JsonDerivedType(typeof(ConversationItem.Finding), "finding")]
[JsonDerivedType(typeof(ConversationItem.ScoutThinking), "scout_thinking")]
[JsonDerivedType(typeof(ConversationItem.ScoutReport), "scout_report")]
[JsonDerivedType(typeof(ConversationItem.ScoutQuestion), "scout_question")]
[JsonDerivedType(typeof(ConversationItem.Dispatch), "dispatch")]
[JsonDerivedType(typeof(ConversationItem.PlanItem), "plan")]
[JsonDerivedType(typeof(ConversationItem.SignOffItem), "sign_off")]
[JsonDerivedType(typeof(ConversationItem.CaseReceived), "case_received")]
public abstract record ConversationItem
{
    public required DateTimeOffset Timestamp { get; init; }
    public abstract string SenderId { get; }
    public virtual string? RecipientId { get; init; }

    public sealed record UserMessage : ConversationItem
    {
        public override string SenderId => "user";
        public required string Content { get; init; }
    }

    public sealed record Welcome : ConversationItem
    {
        public override string SenderId => "all";
        public required string Content { get; init; }
        public string? RoomName { get; init; }
        public string? LeadId { get; init; }
    }

    public sealed record Thinking : ConversationItem
    {
        public override string SenderId => LeadId;
        public required string LeadId { get; init; }
        public required string StepId { get; init; }
        public required string Content { get; init; }
        public TurnUsage? Usage { get; init; }
    }

    public sealed record AgentMessage : ConversationItem
    {
        public override string SenderId => LeadId;
        public required string LeadId { get; init; }
        public override string? RecipientId { get; init; }
        public required string StepId { get; init; }
        public required string Content { get; init; }
        public TurnUsage? Usage { get; init; }
    }

    public sealed record Conclusion : ConversationItem
    {
        public override string SenderId => LeadId;
        public required string LeadId { get; init; }
        public required string StepId { get; init; }
        public required string Content { get; init; }
        public string? Headline { get; init; }
        public EvidenceChain? Evidence { get; init; }
        public FixSuggestion? Fix { get; init; }
        public TurnUsage? Usage { get; init; }
    }

    public sealed record Error : ConversationItem
    {
        public override string SenderId => LeadId;
        public required string LeadId { get; init; }
        public string? StepId { get; init; }
        public required string Content { get; init; }
    }

    public sealed record Finding : ConversationItem
    {
        public override string SenderId => LeadId;
        public required string LeadId { get; init; }
        public required string StepId { get; init; }
        public required string Title { get; init; }
        public required string Description { get; init; }
        public string? Summary { get; init; }
    }

    public sealed record ScoutThinking : ConversationItem
    {
        public override string SenderId => ScoutId;
        public required string ScoutId { get; init; }
        public required string StepId { get; init; }
        public required string Content { get; init; }
        public TurnUsage? Usage { get; init; }
    }

    public sealed record ScoutReport : ConversationItem
    {
        public override string SenderId => ScoutId;
        public override string? RecipientId { get => LeadId; init { } }
        public required string LeadId { get; init; }
        public required string ScoutId { get; init; }
        public required string StepId { get; init; }
        public required string Report { get; init; }
        public string? Summary { get; init; }
        public EvidenceChain? Evidence { get; init; }
        public FixSuggestion? Fix { get; init; }
        public TurnUsage? Usage { get; init; }
    }

    public sealed record ScoutQuestion : ConversationItem
    {
        public override string SenderId => ScoutId;
        public override string? RecipientId { get => LeadId; init { } }
        public required string LeadId { get; init; }
        public required string ScoutId { get; init; }
        public required string StepId { get; init; }
        public required string Question { get; init; }
    }

    public sealed record Dispatch : ConversationItem
    {
        public override string SenderId => LeadId;
        public required string LeadId { get; init; }
        public override string? RecipientId { get => ScoutId; init { } }
        public required string ScoutId { get; init; }
        public required string StepId { get; init; }
        public required string Task { get; init; }
        public required string Role { get; init; }
        public string? ModelProfile { get; init; }
    }

    public sealed record PlanItem : ConversationItem
    {
        public override string SenderId => LeadId;
        public string LeadId { get; init; } = "langur";
        public required string StepId { get; init; }
        public required RemediationPlan Plan { get; init; }
    }

    public sealed record SignOffItem : ConversationItem
    {
        public override string SenderId => LeadId;
        public string LeadId { get; init; } = "langur";
        public required string StepId { get; init; }
        public required string Outcome { get; init; }
        public required IReadOnlyList<SignOffAction> ActionsTaken { get; init; }
        public string? Verification { get; init; }
        public string? Remaining { get; init; }
        public string? Warnings { get; init; }
        public TurnUsage? Usage { get; init; }
    }

    public sealed record CaseReceived : ConversationItem
    {
        public override string SenderId => LeadId;
        public string LeadId { get; init; } = "langur";
        public required string CaseStatement { get; init; }
        public required int FindingCount { get; init; }
        public required string Summary { get; init; }
        public IReadOnlyList<CaseFinding> Findings { get; init; } = [];
    }
}

public class LogEntryModel
{
    public string Sender { get; set; } = "";
    public string StepId { get; set; } = "";
    public string Tool { get; set; } = "";
    public string DisplayCommand { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public LogEntryStatus Status { get; set; }
    public string? Output { get; set; }
    public string? OutputFile { get; set; }
    public int ExitCode { get; set; }
    public Dictionary<string, string>? Context { get; set; }
    public List<LogEntryModel>? Children { get; set; }
    public TurnUsage? Usage { get; set; }
}

public class TurnUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheCreateTokens { get; set; }
    public decimal Cost { get; set; }
    public int? CompactionBefore { get; set; }
    public int? CompactionAfter { get; set; }
}

public enum LogEntryStatus { Running, Completed, TimedOut }

public class GroupMember(string name, string id, MemberStatus status)
{
    public string Name { get; } = name;
    public string Id { get; } = id;
    public MemberStatus Status { get; internal set; } = status;
}

public enum MemberStatus { Static, Idle, Active, Working }

public record SignOffAction(string PlanStepId, string Summary);
