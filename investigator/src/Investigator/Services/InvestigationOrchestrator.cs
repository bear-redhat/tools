using System.Collections.Concurrent;
using System.Threading.Channels;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Tools;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public record ActiveInvestigationInfo(
    string ConversationId,
    DateTimeOffset StartedAt,
    string? OwnerUserId,
    string CaseSummary,
    int AgentCount,
    bool HasWorkingAgents);

public sealed class InvestigationOrchestrator
{
    private readonly ConcurrentDictionary<string, RunningInvestigation> _running = new();

    private readonly ILlmClientFactory _llmFactory;
    private readonly ToolRegistry _toolRegistry;
    private readonly WorkspaceManager _workspaceManager;
    private readonly AgentOptions _agentOptions;
    private readonly SummarizationService _summarizer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<InvestigationOrchestrator> _logger;

    public InvestigationOrchestrator(
        ILlmClientFactory llmFactory,
        ToolRegistry toolRegistry,
        WorkspaceManager workspaceManager,
        IOptions<AgentOptions> agentOptions,
        SummarizationService summarizer,
        ILoggerFactory loggerFactory)
    {
        _llmFactory = llmFactory;
        _toolRegistry = toolRegistry;
        _workspaceManager = workspaceManager;
        _agentOptions = agentOptions.Value;
        _summarizer = summarizer;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<InvestigationOrchestrator>();
    }

    public bool IsRunning(string conversationId) => _running.ContainsKey(conversationId);

    public ChannelReader<AgentEvent> StartAsync(
        string conversationId,
        ConversationSession session,
        string subscriberId,
        TimeZoneInfo? clientTimeZone = null)
    {
        var inv = _running.GetOrAdd(conversationId, _ =>
        {
            var room = new InvestigationRoom(
                _llmFactory, _toolRegistry, _workspaceManager,
                _agentOptions, _loggerFactory.CreateLogger<InvestigationRoom>());

            var cts = new CancellationTokenSource();
            session.StartedAt = DateTimeOffset.UtcNow;

            var created = new RunningInvestigation
            {
                Room = room,
                Session = session,
                Cts = cts,
                StartedAt = session.StartedAt,
                ClientTimeZone = clientTimeZone,
            };

            created.RunTask = RunInvestigationAsync(created, cts.Token);
            return created;
        });

        var sub = Channel.CreateUnbounded<AgentEvent>();
        inv.Subscribers[subscriberId] = sub;
        return sub.Reader;
    }

    public ChannelReader<AgentEvent>? Subscribe(string conversationId, string subscriberId)
    {
        if (!_running.TryGetValue(conversationId, out var inv))
            return null;

        var sub = Channel.CreateUnbounded<AgentEvent>();
        inv.Subscribers[subscriberId] = sub;
        return sub.Reader;
    }

    public void Unsubscribe(string conversationId, string subscriberId)
    {
        if (!_running.TryGetValue(conversationId, out var inv))
            return;

        if (inv.Subscribers.TryRemove(subscriberId, out var sub))
            sub.Writer.TryComplete();
    }

    public ValueTask PostUserMessageAsync(string conversationId, string message, CancellationToken ct)
    {
        if (_running.TryGetValue(conversationId, out var inv))
            return inv.Room.PostUserMessageAsync(message, ct);
        return ValueTask.CompletedTask;
    }

    public void Cancel(string conversationId)
    {
        if (_running.TryGetValue(conversationId, out var inv))
            inv.Cts.Cancel();
    }

    public Task RecallScoutAsync(string conversationId, string scoutName)
    {
        if (_running.TryGetValue(conversationId, out var inv))
            return inv.Room.RecallScoutAsync(scoutName);
        return Task.CompletedTask;
    }

    public Task StandDownScoutAsync(string conversationId, string scoutName)
    {
        if (_running.TryGetValue(conversationId, out var inv))
            return inv.Room.StandDownScoutAsync(scoutName);
        return Task.CompletedTask;
    }

    public IReadOnlyList<ActiveInvestigationInfo> GetActiveInvestigations()
    {
        var list = new List<ActiveInvestigationInfo>();
        foreach (var (convId, inv) in _running)
        {
            var session = inv.Session;
            var firstMsg = session.Items.FirstOrDefault(i => i.Type == ConversationItemType.UserMessage);
            var summary = firstMsg?.Content ?? "";
            if (summary.Length > 120)
                summary = summary[..120] + "...";

            var agentCount = session.Members.Count(m =>
                m.Id is not "all" and not "little-bear");

            list.Add(new ActiveInvestigationInfo(
                convId,
                inv.StartedAt,
                session.OwnerUserId,
                summary,
                agentCount,
                session.HasWorkingAgents));
        }
        return list;
    }

    private async Task RunInvestigationAsync(RunningInvestigation inv, CancellationToken ct)
    {
        var fanOutTask = Task.Run(() => ConsumeAndFanOutAsync(inv, ct), ct);

        try
        {
            await inv.Room.StartAsync(
                inv.Session.WorkspacePath!,
                inv.Session.History,
                ct,
                userId: inv.Session.OwnerUserId,
                conversationId: inv.Session.Id,
                clientTimeZone: inv.ClientTimeZone);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Investigation {Id} cancelled", inv.Session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Investigation {Id} failed", inv.Session.Id);
        }

        try { await fanOutTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fan-out for investigation {Id} failed", inv.Session.Id);
        }
    }

    private async Task ConsumeAndFanOutAsync(RunningInvestigation inv, CancellationToken ct)
    {
        var lastSave = DateTimeOffset.UtcNow;
        try
        {
            await foreach (var evt in inv.Room.Events.ReadAllAsync(ct))
            {
                ApplyEventToSession(inv, evt);

                foreach (var (_, sub) in inv.Subscribers)
                    sub.Writer.TryWrite(evt);

                if (DateTimeOffset.UtcNow - lastSave > TimeSpan.FromSeconds(30))
                {
                    await _workspaceManager.SaveSessionAsync(inv.Session);
                    lastSave = DateTimeOffset.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (inv.Session.Lock)
            {
                inv.Session.IsInvestigating = false;
                inv.Session.HasWorkingAgents = false;
            }

            await _workspaceManager.SaveSessionAsync(inv.Session);

            foreach (var (_, sub) in inv.Subscribers)
                sub.Writer.TryComplete();

            _running.TryRemove(inv.Session.Id, out _);
            inv.Cts.Dispose();
            _logger.LogInformation("Investigation {Id} completed and removed from active list", inv.Session.Id);
        }
    }

    private void ApplyEventToSession(RunningInvestigation inv, AgentEvent evt)
    {
        var session = inv.Session;
        lock (session.Lock)
        {
            switch (evt)
            {
                case AgentEvent.StatusChanged sc:
                    session.IsInvestigating = sc.IsActive;
                    SetMemberStatus(session, "little-bear", sc.IsActive ? MemberStatus.Active : MemberStatus.Idle);
                    break;

                case AgentEvent.Thinking t:
                {
                    var item = new ConversationItem
                    {
                        Type = ConversationItemType.Thinking,
                        Sender = "little-bear",
                        StepId = t.StepId,
                        Content = t.Text,
                        Timestamp = DateTimeOffset.UtcNow,
                    };
                    session.Items.Add(item);
                    TryAttachPendingUsage(inv, "little-bear", item);
                    break;
                }

                case AgentEvent.ToolCall tc:
                {
                    var entry = new LogEntryModel
                    {
                        Sender = "little-bear",
                        StepId = tc.StepId,
                        Tool = tc.Tool,
                        DisplayCommand = tc.DisplayCommand,
                        Timestamp = DateTimeOffset.UtcNow,
                        Status = LogEntryStatus.Running,
                        Context = AgentRunner.ExtractContext(tc.Tool, tc.Parameters),
                    };
                    if (tc.ParentStepId is not null)
                    {
                        var parent = FindLogEntryByStepId(session.LogEntries, tc.ParentStepId);
                        if (parent is not null)
                        {
                            parent.Children ??= [];
                            parent.Children.Add(entry);
                            break;
                        }
                    }
                    session.LogEntries.Add(entry);
                    break;
                }

                case AgentEvent.ToolResult tr:
                {
                    LogEntryModel? entry;
                    if (tr.ParentStepId is not null)
                    {
                        var parent = FindLogEntryByStepId(session.LogEntries, tr.ParentStepId);
                        entry = parent?.Children?.LastOrDefault(e => e.StepId == tr.StepId);
                    }
                    else
                    {
                        entry = session.LogEntries.LastOrDefault(e => e.StepId == tr.StepId)
                            ?? session.LogEntries.LastOrDefault();
                    }
                    if (entry is not null)
                    {
                        entry.Output = tr.Output;
                        entry.OutputFile = tr.OutputFile;
                        entry.ExitCode = tr.ExitCode;
                        entry.Status = tr.TimedOut ? LogEntryStatus.TimedOut : LogEntryStatus.Completed;
                    }
                    break;
                }

                case AgentEvent.Message m:
                {
                    if (!string.IsNullOrWhiteSpace(m.Text))
                    {
                        var item = new ConversationItem
                        {
                            Type = ConversationItemType.AssistantMessage,
                            Sender = "little-bear",
                            Recipient = m.Recipient?.ToLowerInvariant().Replace(" ", "-"),
                            StepId = m.StepId,
                            Content = m.Text,
                            Timestamp = DateTimeOffset.UtcNow,
                        };
                        session.Items.Add(item);
                        TryAttachPendingUsage(inv, "little-bear", item);
                        session.History.Add(new ChatMessage(ChatRole.Assistant, m.Text, DateTimeOffset.UtcNow));
                    }
                    break;
                }

                case AgentEvent.Conclusion c:
                {
                    var item = new ConversationItem
                    {
                        Type = ConversationItemType.Conclusion,
                        Sender = "little-bear",
                        StepId = c.StepId,
                        Content = c.Summary,
                        Evidence = c.Evidence,
                        Fix = c.Fix,
                        Timestamp = DateTimeOffset.UtcNow,
                    };
                    session.Items.Add(item);
                    TryAttachPendingUsage(inv, "little-bear", item);
                    session.History.Add(new ChatMessage(ChatRole.Assistant, c.Summary, DateTimeOffset.UtcNow, c.Evidence, c.Fix));
                    RequestHeadline(session, item);
                    break;
                }

                case AgentEvent.Error e:
                    session.Items.Add(new ConversationItem
                    {
                        Type = ConversationItemType.Error,
                        Sender = "little-bear",
                        StepId = e.StepId,
                        Content = e.ErrorMessage,
                        Timestamp = DateTimeOffset.UtcNow,
                    });
                    break;

                case AgentEvent.Finding f:
                {
                    if (!string.IsNullOrWhiteSpace(f.Title) || !string.IsNullOrWhiteSpace(f.Description))
                    {
                        var item = new ConversationItem
                        {
                            Type = ConversationItemType.Finding,
                            Sender = "little-bear",
                            StepId = f.StepId,
                            Content = $"**{f.Title}**\n{f.Description}",
                            Timestamp = DateTimeOffset.UtcNow,
                        };
                        session.Items.Add(item);
                        RequestSummary(session, item, oneLine: true);
                    }
                    break;
                }

                case AgentEvent.ScoutAsked sq:
                {
                    if (!string.IsNullOrWhiteSpace(sq.Question))
                    {
                        var saId = sq.AgentName.ToLowerInvariant().Replace(" ", "-");
                        session.Items.Add(new ConversationItem
                        {
                            Type = ConversationItemType.ScoutQuestion,
                            Sender = saId,
                            Recipient = "little-bear",
                            SenderDisplayName = sq.AgentName,
                            StepId = sq.StepId,
                            Content = sq.Question,
                            Timestamp = DateTimeOffset.UtcNow,
                        });
                    }
                    break;
                }

                case AgentEvent.SubAgentStarted sa:
                {
                    GetOrAddMember(session, sa.AgentName);
                    UpdateHasWorkingAgents(session);
                    session.Items.Add(new ConversationItem
                    {
                        Type = ConversationItemType.Dispatch,
                        Sender = "little-bear",
                        Recipient = sa.AgentName.ToLowerInvariant().Replace(" ", "-"),
                        SenderDisplayName = sa.AgentName,
                        StepId = sa.StepId,
                        Summary = sa.ModelProfile is not null
                            ? $"{sa.Role} *{sa.ModelProfile}*"
                            : sa.Role,
                        Content = sa.Task,
                        Timestamp = DateTimeOffset.UtcNow,
                    });
                    break;
                }

                case AgentEvent.SubAgentThinking sat:
                {
                    var saId = sat.AgentName.ToLowerInvariant().Replace(" ", "-");
                    EnsureDetailCollections(session, saId);
                    var item = new ConversationItem
                    {
                        Type = ConversationItemType.SubAgentThinking,
                        Sender = saId,
                        SenderDisplayName = sat.AgentName,
                        StepId = sat.StepId,
                        Content = sat.Text,
                        Timestamp = DateTimeOffset.UtcNow,
                    };
                    session.DetailEvents[saId].Add(item);
                    TryAttachPendingUsage(inv, saId, item);
                    break;
                }

                case AgentEvent.SubAgentToolCall satc:
                {
                    var saId = satc.AgentName.ToLowerInvariant().Replace(" ", "-");
                    EnsureDetailCollections(session, saId);
                    session.DetailLogEntries[saId].Add(new LogEntryModel
                    {
                        Sender = saId,
                        SenderDisplayName = satc.AgentName,
                        StepId = satc.StepId,
                        Tool = satc.Tool,
                        DisplayCommand = satc.DisplayCommand,
                        Timestamp = DateTimeOffset.UtcNow,
                        Status = LogEntryStatus.Running,
                        Context = satc.Context,
                    });
                    break;
                }

                case AgentEvent.SubAgentToolResult satr:
                {
                    var saId = satr.AgentName.ToLowerInvariant().Replace(" ", "-");
                    EnsureDetailCollections(session, saId);
                    var logEntry = session.DetailLogEntries[saId].LastOrDefault(e => e.StepId == satr.StepId)
                        ?? session.DetailLogEntries[saId].LastOrDefault();
                    if (logEntry is not null)
                    {
                        logEntry.Output = satr.Output;
                        logEntry.ExitCode = satr.ExitCode;
                        logEntry.Status = satr.TimedOut ? LogEntryStatus.TimedOut : LogEntryStatus.Completed;
                    }
                    break;
                }

                case AgentEvent.SubAgentDone sad:
                {
                    var saId = sad.AgentName.ToLowerInvariant().Replace(" ", "-");
                    SetMemberStatus(session, saId, MemberStatus.Idle);
                    UpdateHasWorkingAgents(session);

                    if (!string.IsNullOrWhiteSpace(sad.Report))
                    {
                        var item = new ConversationItem
                        {
                            Type = ConversationItemType.SubAgentMessage,
                            Sender = saId,
                            Recipient = "little-bear",
                            SenderDisplayName = sad.AgentName,
                            StepId = sad.StepId,
                            Content = sad.Report,
                            Evidence = sad.Evidence,
                            Fix = sad.Fix,
                            Timestamp = DateTimeOffset.UtcNow,
                        };
                        session.Items.Add(item);
                        TryAttachPendingUsage(inv, saId, item);
                        RequestSummary(session, item, oneLine: true);
                    }
                    break;
                }

                case AgentEvent.SubAgentFailed saf:
                {
                    var saId = saf.AgentName.ToLowerInvariant().Replace(" ", "-");
                    SetMemberStatus(session, saId, MemberStatus.Idle);
                    UpdateHasWorkingAgents(session);
                    break;
                }

                case AgentEvent.Usage usage:
                {
                    if (!session.UsageByAgent.TryGetValue(usage.AgentName, out var agentUsage))
                    {
                        agentUsage = new AgentUsage();
                        session.UsageByAgent[usage.AgentName] = agentUsage;
                    }
                    agentUsage.InputTokens += usage.InputTokens;
                    agentUsage.OutputTokens += usage.OutputTokens;
                    agentUsage.CacheReadTokens += usage.CacheReadTokens;
                    agentUsage.CacheCreateTokens += usage.CacheCreateTokens;
                    agentUsage.Cost += usage.CostDelta;
                    agentUsage.ModelProfile ??= usage.ModelProfile;
                    if (agentUsage.InputPricePerMToken == 0 && usage.InputPricePerMToken != 0)
                    {
                        agentUsage.InputPricePerMToken = usage.InputPricePerMToken;
                        agentUsage.OutputPricePerMToken = usage.OutputPricePerMToken;
                        agentUsage.CacheReadPricePerMToken = usage.CacheReadPricePerMToken;
                        agentUsage.CacheCreationPricePerMToken = usage.CacheCreationPricePerMToken;
                    }

                    var senderId = usage.AgentName.ToLowerInvariant().Replace(" ", "-");
                    inv.PendingUsage[senderId] = new TurnUsage
                    {
                        InputTokens = usage.InputTokens,
                        OutputTokens = usage.OutputTokens,
                        CacheReadTokens = usage.CacheReadTokens,
                        CacheCreateTokens = usage.CacheCreateTokens,
                        Cost = usage.CostDelta,
                    };
                    break;
                }

                case AgentEvent.Compaction compaction:
                {
                    if (!session.UsageByAgent.TryGetValue(compaction.AgentName, out var agentUsage))
                    {
                        agentUsage = new AgentUsage();
                        session.UsageByAgent[compaction.AgentName] = agentUsage;
                    }
                    agentUsage.InputTokens += compaction.InputTokens;
                    agentUsage.OutputTokens += compaction.OutputTokens;
                    agentUsage.CacheReadTokens += compaction.CacheReadTokens;
                    agentUsage.CacheCreateTokens += compaction.CacheCreateTokens;
                    agentUsage.Cost += compaction.CostDelta;
                    agentUsage.ModelProfile ??= compaction.ModelProfile;
                    if (agentUsage.InputPricePerMToken == 0 && compaction.InputPricePerMToken != 0)
                    {
                        agentUsage.InputPricePerMToken = compaction.InputPricePerMToken;
                        agentUsage.OutputPricePerMToken = compaction.OutputPricePerMToken;
                        agentUsage.CacheReadPricePerMToken = compaction.CacheReadPricePerMToken;
                        agentUsage.CacheCreationPricePerMToken = compaction.CacheCreationPricePerMToken;
                    }

                    session.LogEntries.Add(new LogEntryModel
                    {
                        Sender = compaction.AgentName.ToLowerInvariant().Replace(" ", "-"),
                        SenderDisplayName = compaction.AgentName,
                        StepId = compaction.StepId,
                        Tool = "compaction",
                        DisplayCommand = $"Context compacted: ~{compaction.TokensBefore} \u2192 ~{compaction.TokensAfter} tokens",
                        Timestamp = DateTimeOffset.UtcNow,
                        Status = LogEntryStatus.Completed,
                        Usage = new TurnUsage
                        {
                            InputTokens = compaction.InputTokens,
                            OutputTokens = compaction.OutputTokens,
                            CacheReadTokens = compaction.CacheReadTokens,
                            CacheCreateTokens = compaction.CacheCreateTokens,
                            Cost = compaction.CostDelta,
                            CompactionBefore = compaction.TokensBefore,
                            CompactionAfter = compaction.TokensAfter,
                        },
                    });
                    break;
                }
            }
        }
    }

    // --- Session mutation helpers ---

    private static void SetMemberStatus(ConversationSession session, string id, MemberStatus status)
    {
        var member = session.Members.FirstOrDefault(m => m.Id == id);
        if (member is not null) member.Status = status;
    }

    private static void GetOrAddMember(ConversationSession session, string agentName)
    {
        var id = agentName.ToLowerInvariant().Replace(" ", "-");
        if (session.Members.Any(m => m.Id == id)) return;
        session.Members.Add(new GroupMember(agentName, id, MemberStatus.Working));
    }

    private static void UpdateHasWorkingAgents(ConversationSession session)
    {
        session.HasWorkingAgents = session.Members.Any(m =>
            m.Status == MemberStatus.Working && m.Id is not "all" and not "little-bear");
    }

    private static void EnsureDetailCollections(ConversationSession session, string saId)
    {
        if (!session.DetailEvents.ContainsKey(saId))
            session.DetailEvents[saId] = new List<ConversationItem>();
        if (!session.DetailLogEntries.ContainsKey(saId))
            session.DetailLogEntries[saId] = new List<LogEntryModel>();
    }

    private static void TryAttachPendingUsage(RunningInvestigation inv, string senderId, ConversationItem item)
    {
        if (inv.PendingUsage.Remove(senderId, out var usage))
            item.Usage = usage;
    }

    private static LogEntryModel? FindLogEntryByStepId(IEnumerable<LogEntryModel> entries, string stepId)
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

    private void RequestHeadline(ConversationSession session, ConversationItem item)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var (text, usage) = await _summarizer.SummarizeToHeadlineWithUsageAsync(item.Content, CancellationToken.None);
                item.Summary = text;
                item.SummarizedByAi = true;
                TrackPanelSummarizationCost(session, usage);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate headline for {Type} item", item.Type);
            }
        });
    }

    private void RequestSummary(ConversationSession session, ConversationItem item, bool oneLine)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var (text, usage) = oneLine
                    ? await _summarizer.SummarizeToOneLineWithUsageAsync(item.Content, CancellationToken.None)
                    : await _summarizer.SummarizeToFewLinesWithUsageAsync(item.Content, CancellationToken.None);
                item.Summary = text;
                item.SummarizedByAi = true;
                TrackPanelSummarizationCost(session, usage);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to summarize {Type} item", item.Type);
            }
        });
    }

    private void TrackPanelSummarizationCost(ConversationSession session, UsageInfo? usage)
    {
        if (usage is null) return;
        var opts = _summarizer.SummarizerModelOptions;
        var cost = AgentRunner.ComputeCost(usage, opts.InputPricePerMToken, opts.OutputPricePerMToken,
            opts.CacheReadPricePerMToken, opts.CacheCreationPricePerMToken);
        lock (session.Lock)
        {
            var ps = session.PanelSummarizationUsage;
            ps.InputTokens += usage.InputTokens;
            ps.OutputTokens += usage.OutputTokens;
            ps.CacheReadTokens += usage.CacheReadInputTokens;
            ps.CacheCreateTokens += usage.CacheCreationInputTokens;
            ps.Cost += cost;
        }
    }

    // --- Internal types ---

    internal sealed class RunningInvestigation
    {
        public required InvestigationRoom Room { get; init; }
        public required ConversationSession Session { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public TimeZoneInfo? ClientTimeZone { get; init; }
        public Task RunTask { get; set; } = Task.CompletedTask;
        public ConcurrentDictionary<string, Channel<AgentEvent>> Subscribers { get; } = new();
        public Dictionary<string, TurnUsage> PendingUsage { get; } = new();
    }
}
