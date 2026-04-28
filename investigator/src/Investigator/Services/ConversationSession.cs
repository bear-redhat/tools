using System.Text.Json.Serialization;
using Investigator.Models;

namespace Investigator.Services;

public sealed class AgentUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheCreateTokens { get; set; }
    public decimal Cost { get; set; }
    public string? ModelProfile { get; set; }
    public decimal InputPricePerMToken { get; set; }
    public decimal OutputPricePerMToken { get; set; }
    public decimal CacheReadPricePerMToken { get; set; }
    public decimal CacheCreationPricePerMToken { get; set; }
}

public sealed class ConversationSession
{
    public ConversationSession(string id)
    {
        Id = id;
        Members =
        [
            new("221B Banyan Row", "all", MemberStatus.Static),
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
        ╭─────── 221B BANYAN ROW ────────╮
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

        Pray be seated. You are at 221B Banyan Row, the chambers of Little Bear -- consulting detective for OpenShift and Prow mysteries.

        State your case, if you would.
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

    public string? OwnerUserId { get; set; }
    public string? OwnerCircuitId { get; set; }

    public Dictionary<string, AgentUsage> UsageByAgent { get; } = new(StringComparer.OrdinalIgnoreCase);
    public AgentUsage PanelSummarizationUsage { get; } = new();

    public decimal TotalCost =>
        UsageByAgent.Values.Sum(u => u.Cost) + PanelSummarizationUsage.Cost;
}
