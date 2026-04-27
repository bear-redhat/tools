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

        return session;
    }
}
