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

        Items.Add(new ConversationItem
        {
            Type = ConversationItemType.Welcome,
            Sender = "little-bear",
            Content = WelcomeContent,
            Timestamp = DateTimeOffset.UtcNow,
        });
    }

    private const string WelcomeContent =
        """
        ╭────── 221B BANYAN HOLLOW ──────╮
        │ 🌿            🔥            🌿 │
        │                                │
        │        ╭──────╮ ╭──────╮       │
        │        │  🐻  │ │  👤  │       │
        │        │  🪑  │ │  🪑  │       │
        │        ╰──────╯ ╰──────╯       │
        │                                │
        │           ☕  🔎  🐾           │
        ╰────────────────────────────────╯

        The game is afoot.

        Welcome to 221B Banyan Hollow. I am Little Bear, consulting detective for OpenShift and Prow mysteries.

        Present your case, Client.
        """;

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
