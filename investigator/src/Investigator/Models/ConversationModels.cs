namespace Investigator.Models;

public class ConversationItem
{
    public ConversationItemType Type { get; set; }
    public string Sender { get; set; } = "";
    public string? Recipient { get; set; }
    public string? SenderDisplayName { get; set; }
    public string? StepId { get; set; }
    public string Content { get; set; } = "";
    public string? Summary { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public EvidenceChain? Evidence { get; set; }
    public FixSuggestion? Fix { get; set; }
    public bool SummarizedByAi { get; set; }
    public TurnUsage? Usage { get; set; }
}

public enum ConversationItemType
{
    UserMessage, Thinking, AssistantMessage, Conclusion, Error,
    SubAgentThinking, SubAgentMessage,
    Finding, ScoutQuestion, Dispatch, Welcome
}

public class LogEntryModel
{
    public string Sender { get; set; } = "little-bear";
    public string? SenderDisplayName { get; set; }
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
    public string Name { get; set; } = name;
    public string Id { get; set; } = id;
    public MemberStatus Status { get; set; } = status;
}

public enum MemberStatus { Static, Idle, Active, Working }
