using Investigator.Services;

namespace Investigator.Models;

public sealed class SessionSnapshot
{
    public required string Id { get; init; }
    public string? OwnerUserName { get; init; }
    public List<ConversationItem> Items { get; init; } = [];
    public List<LogEntryModel> LogEntries { get; init; } = [];
    public List<GroupMember> Members { get; init; } = [];
    public Dictionary<string, List<ConversationItem>> DetailEvents { get; init; } = new();
    public Dictionary<string, List<LogEntryModel>> DetailLogEntries { get; init; } = new();
    public Dictionary<string, AgentUsage> UsageByAgent { get; init; } = new();
    public AgentUsage PanelSummarizationUsage { get; init; } = new();

    public static SessionSnapshot FromSession(ConversationSession session) => new()
    {
        Id = session.Id,
        OwnerUserName = session.OwnerUserName,
        Items = session.Items.ToList(),
        LogEntries = session.LogEntries.ToList(),
        Members = session.Members.ToList(),
        DetailEvents = session.DetailEvents.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToList()),
        DetailLogEntries = session.DetailLogEntries.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToList()),
        UsageByAgent = session.UsageByAgent.ToDictionary(
            kvp => kvp.Key,
            kvp => new AgentUsage
            {
                InputTokens = kvp.Value.InputTokens,
                OutputTokens = kvp.Value.OutputTokens,
                CacheReadTokens = kvp.Value.CacheReadTokens,
                CacheCreateTokens = kvp.Value.CacheCreateTokens,
                Cost = kvp.Value.Cost,
            }),
        PanelSummarizationUsage = new AgentUsage
        {
            InputTokens = session.PanelSummarizationUsage.InputTokens,
            OutputTokens = session.PanelSummarizationUsage.OutputTokens,
            CacheReadTokens = session.PanelSummarizationUsage.CacheReadTokens,
            CacheCreateTokens = session.PanelSummarizationUsage.CacheCreateTokens,
            Cost = session.PanelSummarizationUsage.Cost,
        },
    };

    public ConversationSession ToSession()
    {
        var session = new ConversationSession(Id);
        session.OwnerUserName = OwnerUserName;

        session.Items.Clear();
        session.Items.AddRange(Items);

        session.LogEntries.AddRange(LogEntries);

        session.Members.Clear();
        session.Members.AddRange(Members);

        foreach (var (key, value) in DetailEvents)
            session.DetailEvents[key] = value;

        foreach (var (key, value) in DetailLogEntries)
            session.DetailLogEntries[key] = value;

        foreach (var (key, value) in UsageByAgent)
            session.UsageByAgent[key] = value;

        var ps = session.PanelSummarizationUsage;
        ps.InputTokens = PanelSummarizationUsage.InputTokens;
        ps.OutputTokens = PanelSummarizationUsage.OutputTokens;
        ps.CacheReadTokens = PanelSummarizationUsage.CacheReadTokens;
        ps.CacheCreateTokens = PanelSummarizationUsage.CacheCreateTokens;
        ps.Cost = PanelSummarizationUsage.Cost;

        return session;
    }
}
