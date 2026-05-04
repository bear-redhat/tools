using System.Collections.Concurrent;
using System.Text.Json;
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
    bool HasWorkingAgents,
    bool HasRemediation);

public sealed class InvestigationOrchestrator
{
    private readonly ConcurrentDictionary<string, RunningInvestigation> _running = new();

    private readonly ILlmClientFactory _llmFactory;
    private readonly ToolRegistry _toolRegistry;
    private readonly WorkspaceManager _workspaceManager;
    private readonly AgentOptions _agentOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<InvestigationOrchestrator> _logger;

    public InvestigationOrchestrator(
        ILlmClientFactory llmFactory,
        ToolRegistry toolRegistry,
        WorkspaceManager workspaceManager,
        IOptions<AgentOptions> agentOptions,
        ILoggerFactory loggerFactory)
    {
        _llmFactory = llmFactory;
        _toolRegistry = toolRegistry;
        _workspaceManager = workspaceManager;
        _agentOptions = agentOptions.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<InvestigationOrchestrator>();
    }

    public bool IsRunning(string conversationId) => _running.ContainsKey(conversationId);

    public ChannelReader<byte> StartAsync(
        string conversationId,
        ConversationSession session,
        string subscriberId,
        TimeZoneInfo? clientTimeZone = null)
    {
        if (_running.TryGetValue(conversationId, out var stale)
            && (stale.Cts.IsCancellationRequested || stale.RunTask.IsCompleted))
            _running.TryRemove(conversationId, out _);

        var inv = _running.GetOrAdd(conversationId, _ =>
        {
            var bus = new RoomEventBus();
            var initialLog = session.LoadedInvestigationEvents;
            var pipeline = new RoomEventPipeline(bus, [new ToolEffectEnricher("little-bear")]);
            var transcriptStore = new TranscriptStore();

            var room = new InvestigationRoom(
                _llmFactory, _toolRegistry,
                _agentOptions, pipeline, transcriptStore,
                _loggerFactory.CreateLogger<InvestigationRoom>());

            session.InvestigationPipeline = pipeline;
            session.InvestigationTranscriptStore = transcriptStore;

            var cts = new CancellationTokenSource();
            session.StartedAt = DateTimeOffset.UtcNow;

            var created = new RunningInvestigation
            {
                Room = room,
                Session = session,
                Cts = cts,
                StartedAt = session.StartedAt,
                ClientTimeZone = clientTimeZone,
                EventLog = initialLog,
            };

            created.RunTask = RunInvestigationAsync(created, cts.Token);
            return created;
        });

        var sub = Channel.CreateUnbounded<byte>();
        inv.Subscribers[subscriberId] = sub;
        return sub.Reader;
    }

    public ChannelReader<byte>? Subscribe(string conversationId, string subscriberId)
    {
        if (!_running.TryGetValue(conversationId, out var inv))
            return null;

        var sub = Channel.CreateUnbounded<byte>();
        inv.Subscribers[subscriberId] = sub;
        return sub.Reader;
    }

    public void Unsubscribe(string conversationId, string subscriberId)
    {
        if (!_running.TryGetValue(conversationId, out var inv))
            return;

        if (inv.Subscribers.TryRemove(subscriberId, out var sub))
            sub.Writer.TryComplete();

        if (inv.Subscribers.IsEmpty)
        {
            inv.Cts.Cancel();
            _running.TryRemove(conversationId, out _);
            inv.Cts.Dispose();
        }
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
            var room = session.Investigation;
            var firstMsg = room.Items.OfType<ConversationItem.UserMessage>().FirstOrDefault();
            var summary = firstMsg?.Content ?? "";
            if (summary.Length > 120)
                summary = summary[..120] + "...";

            var agentCount = room.Members.Count(m =>
                m.Id is not "all" and not "little-bear");

            list.Add(new ActiveInvestigationInfo(
                convId,
                inv.StartedAt,
                session.OwnerUserId,
                summary,
                agentCount,
                room.HasWorkingAgents,
                session.Remediation is not null));
        }
        return list;
    }

    private async Task RunInvestigationAsync(RunningInvestigation inv, CancellationToken ct)
    {
        var applierReader = inv.Room.Bus.Subscribe("state-applier");
        var fanOutTask = Task.Run(() => ConsumeAndFanOutAsync(inv, applierReader));

        var projector = new TranscriptProjector("little-bear", evt => inv.Room.Pipeline.EmitAsync(evt));
        var projectionTask = Task.Run(() => projector.RunLiveAsync(inv.Room.TranscriptStore.Reader, ct));

        try
        {
            await inv.Room.StartAsync(
                inv.Session.WorkspacePath!,
                ct,
                eventLog: inv.EventLog,
                userId: inv.Session.OwnerUserId,
                conversationId: inv.Session.Id,
                clientTimeZone: inv.ClientTimeZone);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Investigation {Id} cancelled", inv.Session.Id);
        }
        finally
        {
            inv.Room.TranscriptStore.Append(
                new RoomEvent.SessionEnded(0, "system", DateTimeOffset.UtcNow));
            inv.Room.TranscriptStore.Complete();

            try { await projectionTask; } catch (OperationCanceledException) { }

            inv.Room.Bus.Complete();

            try { await fanOutTask; } catch (OperationCanceledException) { }

            await _workspaceManager.SaveSessionAsync(inv.Session);
        }
    }

    private async Task ConsumeAndFanOutAsync(
        RunningInvestigation inv, ChannelReader<RoomEvent> applierReader)
    {
        var room = inv.Session.Investigation;
        var projector = new UxProjector("little-bear");
        var mutator = new RoomState.Mutator(room);
        var lastSave = DateTimeOffset.UtcNow;
        try
        {
            var pendingRequests = new Dictionary<int, RoomEvent.ToolRequest>();
            await foreach (var evt in applierReader.ReadAllAsync())
            {
                if (evt is RoomEvent.ToolRequest tr)
                    pendingRequests[tr.Seq] = tr;

                lock (room.Lock)
                {
                    foreach (var ux in projector.Project(evt))
                        mutator.Apply(ux);
                    mutator.PublishView();
                }

                if (evt is RoomEvent.ToolResponse { Tool: "commission_remedy" } cr
                    && pendingRequests.TryGetValue(cr.RequestSeq, out var req))
                {
                    TriggerRemediation(inv, req.Input, room);
                }

                foreach (var (_, sub) in inv.Subscribers)
                    sub.Writer.TryWrite(0);

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
            foreach (var (_, sub) in inv.Subscribers)
                sub.Writer.TryComplete();

            _logger.LogInformation("Investigation {Id} fan-out completed", inv.Session.Id);
        }
    }

    // --- Internal types ---

    private void TriggerRemediation(RunningInvestigation inv, JsonElement input, RoomState room)
    {
        if (inv.Session.Remediation is not null) return;

        var caseDesc = input.TryGetProperty("case_description", out var cd) ? cd.GetString() ?? "" : "";
        var rootCause = input.TryGetProperty("root_cause", out var rc) ? rc.GetString() ?? "" : "";
        var fixDesc = input.TryGetProperty("fix_description", out var fd) ? fd.GetString() : null;
        var fixCmds = input.TryGetProperty("fix_commands", out var fc) && fc.ValueKind == JsonValueKind.Array
            ? fc.EnumerateArray().Select(c => c.GetString() ?? "").ToList()
            : new List<string>();

        var findings = room.Items.OfType<ConversationItem.Finding>()
            .Select(f => new CaseFinding(f.Title, f.Description)).ToList();

        FixSuggestion? fix = !string.IsNullOrWhiteSpace(fixDesc)
            ? new FixSuggestion(fixDesc, fixCmds)
            : null;

        var caseFile = new CaseFile(
            ParentConversationId: inv.Session.Id,
            CaseStatement: caseDesc,
            Findings: findings,
            Summary: rootCause,
            Evidence: null,
            Fix: fix);

        inv.Session.AddRemediationRoom(caseFile);
        _logger.LogInformation("Remediation commissioned for investigation {Id}", inv.Session.Id);
    }

    internal sealed class RunningInvestigation
    {
        public required InvestigationRoom Room { get; init; }
        public required ConversationSession Session { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public TimeZoneInfo? ClientTimeZone { get; init; }
        public IReadOnlyList<RoomEvent>? EventLog { get; init; }
        public Task RunTask { get; set; } = Task.CompletedTask;
        public ConcurrentDictionary<string, Channel<byte>> Subscribers { get; } = new();
    }
}
