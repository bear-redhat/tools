namespace Investigator.Models;

public abstract record UxEvent;

public record AddConversationItem(ConversationItem Item) : UxEvent;
public record AddLogEntry(LogEntryModel Entry) : UxEvent;
public record AddChildLogEntry(string ParentStepId, LogEntryModel Entry) : UxEvent;
public record UpdateLogEntry(int RequestSeq, LogEntryStatus Status,
    string? Output = null, string? OutputFile = null, int? ExitCode = null) : UxEvent;
public record AddMember(string Name, string Id) : UxEvent;
public record SetMemberStatus(string Id, MemberStatus Status) : UxEvent;
public record SetInvestigating(bool Active) : UxEvent;
public record AddUsage(string AgentName, UsageInfo Usage, decimal Cost,
    string? ModelProfile = null,
    decimal InputPrice = 0, decimal OutputPrice = 0,
    decimal CacheReadPrice = 0, decimal CacheCreatePrice = 0) : UxEvent;
public record SetPlan(RemediationPlan Plan) : UxEvent;
public record UpdatePlanStep(string StepId, StepStatus Status,
    string? Note = null, string? PatchFile = null) : UxEvent;
