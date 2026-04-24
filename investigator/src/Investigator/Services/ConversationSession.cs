using Investigator.Models;

namespace Investigator.Services;

public sealed class ConversationSession
{
    public ConversationSession(string id)
    {
        Id = id;
        Members =
        [
            new("221B Banyan Hollow", "all", MemberStatus.Static),
            new("Client", "user", MemberStatus.Static),
            new("Little Bear", "little-bear", MemberStatus.Idle),
        ];
    }

    public string Id { get; }
    public object Lock { get; } = new();

    public string? WorkspacePath { get; set; }
    public List<ConversationItem> Items { get; } = [];
    public List<LogEntryModel> LogEntries { get; } = [];
    public List<ChatMessage> History { get; } = [];
    public List<GroupMember> Members { get; }
    public bool IsInvestigating { get; set; }
    public bool HasWorkingAgents { get; set; }

    public Dictionary<string, List<ConversationItem>> DetailEvents { get; } = new();
    public Dictionary<string, List<LogEntryModel>> DetailLogEntries { get; } = new();

    public string? OwnerCircuitId { get; set; }
}
