using System.Globalization;
using System.Text.Json.Serialization;
using Investigator.Models;
using Investigator.Tools;

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

public sealed class SessionView
{
    public IReadOnlyList<ConversationItem> Items { get; init; } = [];
    public IReadOnlyList<GroupMember> Members { get; init; } = [];
    public IReadOnlyList<LogEntryModel> LogEntries { get; init; } = [];
    public bool IsInvestigating { get; init; }
    public bool HasWorkingAgents { get; init; }
    public RoomPhase Phase { get; init; }
    public IReadOnlyDictionary<string, AgentUsage> UsageByAgent { get; init; }
        = new Dictionary<string, AgentUsage>();
    public decimal TotalCost { get; init; }
    public RemediationPlan? Plan { get; init; }
}

public sealed class RoomState
{
    private readonly List<ConversationItem> _items = [];
    private readonly List<GroupMember> _members;
    private readonly List<LogEntryModel> _logEntries = [];
    private readonly Dictionary<string, AgentUsage> _usageByAgent = new(StringComparer.OrdinalIgnoreCase);
    private volatile SessionView _currentView = null!;

    public RoomState(string name, string leadId, List<GroupMember> members,
        IEnumerable<ConversationItem>? initialItems = null, CaseFile? caseFile = null)
    {
        Name = name;
        LeadId = leadId;
        _members = new List<GroupMember>(members);
        if (initialItems is not null) _items.AddRange(initialItems);
        CaseFile = caseFile;
        PublishView();
    }

    public string Name { get; }
    public string LeadId { get; }
    public IReadOnlyList<ConversationItem> Items => _items;
    public IReadOnlyList<GroupMember> Members => _members;
    public IReadOnlyList<LogEntryModel> LogEntries => _logEntries;
    public IReadOnlyDictionary<string, AgentUsage> UsageByAgent => _usageByAgent;
    public bool IsInvestigating { get; private set; }
    public bool HasWorkingAgents { get; private set; }
    public RoomPhase Phase { get; internal set; } = RoomPhase.Idle;
    public RemediationPlan? RemediationPlan { get; private set; }
    public CaseFile? CaseFile { get; init; }
    public object Lock { get; } = new();
    public SessionView CurrentView => _currentView;
    public decimal TotalCost => _usageByAgent.Values.Sum(u => u.Cost);

    public string? ResolveDisplayName(string? id)
    {
        if (id is null) return null;
        var member = _members.FirstOrDefault(m => m.Id == id);
        if (member is not null) return member.Name;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(id.Replace("-", " "));
    }

    public IReadOnlyList<ChatMessage> DeriveHistory()
        => _items
            .Where(i => i is ConversationItem.UserMessage
                      or ConversationItem.AgentMessage
                      or ConversationItem.Conclusion)
            .Select(i => i switch
            {
                ConversationItem.UserMessage u => new ChatMessage(ChatRole.User, u.Content, u.Timestamp),
                ConversationItem.AgentMessage a => new ChatMessage(ChatRole.Assistant, a.Content, a.Timestamp),
                ConversationItem.Conclusion c => new ChatMessage(ChatRole.Assistant, c.Content, c.Timestamp, c.Evidence, c.Fix),
                _ => throw new InvalidOperationException(),
            })
            .ToList();

    public static LogEntryModel? FindLogEntryByStepId(IEnumerable<LogEntryModel> entries, string stepId)
    {
        foreach (var entry in entries)
        {
            if (entry.StepId == stepId) return entry;
            if (entry.Children is not null)
            {
                var found = FindLogEntryByStepId(entry.Children, stepId);
                if (found is not null) return found;
            }
        }
        return null;
    }

    internal void ForceAgentIdle(string agentId)
    {
        lock (Lock)
        {
            var member = _members.FirstOrDefault(m => m.Id == agentId);
            if (member is not null && member.Status is MemberStatus.Working or MemberStatus.Active)
            {
                member.Status = MemberStatus.Idle;
                HasWorkingAgents = _members.Any(m =>
                    m.Status is MemberStatus.Working or MemberStatus.Active
                    && m.Id is not "all");
                var roomMember = _members.FirstOrDefault(m => m.Id == "all");
                if (roomMember is not null)
                    roomMember.Status = HasWorkingAgents ? MemberStatus.Active : MemberStatus.Static;
                PublishView();
            }
        }
    }

    private void PublishView()
    {
        _currentView = new SessionView
        {
            Items = _items.ToArray(),
            Members = _members.ToArray(),
            LogEntries = _logEntries.ToArray(),
            IsInvestigating = IsInvestigating,
            HasWorkingAgents = HasWorkingAgents,
            Phase = Phase,
            UsageByAgent = _usageByAgent.ToDictionary(
                kvp => kvp.Key,
                kvp => new AgentUsage
                {
                    InputTokens = kvp.Value.InputTokens,
                    OutputTokens = kvp.Value.OutputTokens,
                    CacheReadTokens = kvp.Value.CacheReadTokens,
                    CacheCreateTokens = kvp.Value.CacheCreateTokens,
                    Cost = kvp.Value.Cost,
                    ModelProfile = kvp.Value.ModelProfile,
                    InputPricePerMToken = kvp.Value.InputPricePerMToken,
                    OutputPricePerMToken = kvp.Value.OutputPricePerMToken,
                    CacheReadPricePerMToken = kvp.Value.CacheReadPricePerMToken,
                    CacheCreationPricePerMToken = kvp.Value.CacheCreationPricePerMToken,
                },
                StringComparer.OrdinalIgnoreCase),
            TotalCost = TotalCost,
            Plan = RemediationPlan?.Snapshot(),
        };
    }

    internal sealed class Mutator
    {
        private readonly RoomState _s;
        public Mutator(RoomState state) => _s = state;

        public void Apply(UxEvent evt)
        {
            switch (evt)
            {
                case AddConversationItem aci:
                    _s._items.Add(aci.Item);
                    break;

                case AddLogEntry ale:
                    _s._logEntries.Add(ale.Entry);
                    break;

                case AddChildLogEntry acle:
                {
                    var parent = FindLogEntryByStepId(_s._logEntries, acle.ParentStepId);
                    if (parent is not null)
                    {
                        parent.Children ??= [];
                        parent.Children.Add(acle.Entry);
                    }
                    else
                    {
                        _s._logEntries.Add(acle.Entry);
                    }
                    break;
                }

                case UpdateLogEntry ule:
                {
                    var entry = FindByRequestSeq(_s._logEntries, ule.RequestSeq);
                    if (entry is not null)
                    {
                        entry.Status = ule.Status;
                        if (ule.Output is not null) entry.Output = ule.Output;
                        if (ule.OutputFile is not null) entry.OutputFile = ule.OutputFile;
                        if (ule.ExitCode is not null) entry.ExitCode = ule.ExitCode.Value;
                    }
                    break;
                }

                case AddMember am:
                    GetOrAddMember(am.Name);
                    UpdateHasWorkingAgents();
                    break;

                case SetMemberStatus sms:
                    SetMemberStatus(sms.Id, sms.Status);
                    UpdateHasWorkingAgents();
                    break;

                case SetInvestigating si:
                    _s.IsInvestigating = si.Active;
                    break;

                case AddUsage au:
                {
                    if (!_s._usageByAgent.TryGetValue(au.AgentName, out var usage))
                    {
                        usage = new AgentUsage();
                        _s._usageByAgent[au.AgentName] = usage;
                    }
                    usage.InputTokens += au.Usage.InputTokens;
                    usage.OutputTokens += au.Usage.OutputTokens;
                    usage.CacheReadTokens += au.Usage.CacheReadInputTokens;
                    usage.CacheCreateTokens += au.Usage.CacheCreationInputTokens;
                    usage.Cost += au.Cost;
                    usage.ModelProfile ??= au.ModelProfile;
                    if (au.InputPrice > 0) usage.InputPricePerMToken = au.InputPrice;
                    if (au.OutputPrice > 0) usage.OutputPricePerMToken = au.OutputPrice;
                    if (au.CacheReadPrice > 0) usage.CacheReadPricePerMToken = au.CacheReadPrice;
                    if (au.CacheCreatePrice > 0) usage.CacheCreationPricePerMToken = au.CacheCreatePrice;
                    break;
                }

                case SetPlan sp:
                    _s.RemediationPlan = sp.Plan;
                    break;

                case UpdatePlanStep ups:
                {
                    var step = _s.RemediationPlan?.Steps.FirstOrDefault(s => s.Id == ups.StepId);
                    if (step is not null)
                    {
                        step.Status = ups.Status;
                        if (ups.Note is not null) step.Note = ups.Note;
                        if (ups.PatchFile is not null) step.Change.PatchFile = ups.PatchFile;
                    }
                    break;
                }
            }
        }

        public void PublishView() => _s.PublishView();

        private void SetMemberStatus(string id, MemberStatus status)
        {
            var member = _s._members.FirstOrDefault(m => m.Id == id);
            if (member is not null) member.Status = status;
        }

        private void GetOrAddMember(string agentName)
        {
            var id = agentName.ToLowerInvariant().Replace(" ", "-");
            if (_s._members.Any(m => m.Id == id)) return;
            _s._members.Add(new GroupMember(agentName, id, MemberStatus.Working));
        }

        private void UpdateHasWorkingAgents()
        {
            _s.HasWorkingAgents = _s._members.Any(m =>
                m.Status is MemberStatus.Working or MemberStatus.Active
                && m.Id is not "all");

            var roomMember = _s._members.FirstOrDefault(m => m.Id == "all");
            if (roomMember is not null)
                roomMember.Status = _s.HasWorkingAgents ? MemberStatus.Active : MemberStatus.Static;
        }

        private static LogEntryModel? FindByRequestSeq(IEnumerable<LogEntryModel> entries, int requestSeq)
        {
            var key = requestSeq.ToString();
            foreach (var entry in entries)
            {
                if (entry.StepId == key) return entry;
                if (entry.Children is not null)
                {
                    var found = FindByRequestSeq(entry.Children, requestSeq);
                    if (found is not null) return found;
                }
            }
            return null;
        }
    }
}

public sealed class ConversationSession
{
    public ConversationSession(string id)
    {
        Id = id;
        Investigation = new RoomState("221B Banyan Row", "little-bear",
        [
            new("221B Banyan Row", "all", MemberStatus.Static),
            new("Little Bear", "little-bear", MemberStatus.Idle),
        ],
        initialItems:
        [
            new ConversationItem.Welcome
            {
                Content = InvestigationWelcome,
                RoomName = "221B Banyan Row",
                LeadId = "little-bear",
                Timestamp = DateTimeOffset.UtcNow,
            }
        ]);
    }

    private const string InvestigationWelcome =
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

    private const string RemediationWelcome =
"""
╭──────── THE CANOPY POST ─────────╮
│ 🌲            ⛺            🌲  │
│                                  │
│          ╭────────╮              │
│          │  🐒   │  Case file   │
│          │  🪑   │  received. 📨│
│          ╰────────╯              │
│                                  │
│         🔧  📋  🛡️              │
╰──────────────────────────────────╯

The Canopy Post stands ready.

Intendant G. Langur. The case file from Banyan Row is on my desk. I shall assess the situation, draw up the remediation plan, and prepare your orders. You will execute each step as I present it. We begin at once.
""";

    public string Id { get; }
    public object Lock { get; } = new();
    public string? WorkspacePath { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string? OwnerUserId { get; set; }
    public string? OwnerCircuitId { get; set; }
    public DateTimeOffset LastSavedAt { get; set; }

    public RoomState Investigation { get; }
    public RoomState? Remediation { get; set; }
    public RoomEventPipeline? InvestigationPipeline { get; set; }
    public RoomEventPipeline? RemediationPipeline { get; set; }
    public TranscriptStore? InvestigationTranscriptStore { get; set; }
    public TranscriptStore? RemediationTranscriptStore { get; set; }

    public IReadOnlyList<RoomEvent>? LoadedInvestigationEvents { get; set; }
    public IReadOnlyList<RoomEvent>? LoadedRemediationEvents { get; set; }

    public decimal TotalCost => Investigation.TotalCost + (Remediation?.TotalCost ?? 0m);

    public CaseReferral? PendingReferral { get; set; }

    public void AddRemediationRoom(CaseFile caseFile)
    {
        Remediation = new RoomState("The Canopy Post", "langur",
        [
            new("The Canopy Post", "all", MemberStatus.Static),
            new("Intendant G. Langur", "langur", MemberStatus.Active),
        ],
        initialItems:
        [
            new ConversationItem.Welcome
            {
                Content = RemediationWelcome,
                RoomName = "The Canopy Post",
                LeadId = "langur",
                Timestamp = DateTimeOffset.UtcNow,
            },
            new ConversationItem.CaseReceived
            {
                LeadId = "langur",
                CaseStatement = caseFile.CaseStatement,
                FindingCount = caseFile.Findings.Count,
                Summary = caseFile.Summary,
                Findings = caseFile.Findings,
                Timestamp = DateTimeOffset.UtcNow,
            }
        ],
        caseFile: caseFile);
    }
}
