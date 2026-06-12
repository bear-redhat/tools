using System.Collections.Concurrent;
using System.Threading.Channels;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Tools;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public sealed class RunningRoom<TRoom> where TRoom : AgentRoom
{
    public required TRoom Room { get; init; }
    public required ConversationSession Session { get; init; }
    public required CancellationTokenSource Cts { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public TimeZoneInfo? ClientTimeZone { get; init; }
    public IReadOnlyList<RoomEvent>? EventLog { get; init; }
    public Task RunTask { get; set; } = Task.CompletedTask;
    public ConcurrentDictionary<string, Channel<byte>> Subscribers { get; } = new();
}

public abstract class RoomOrchestrator<TRoom> where TRoom : AgentRoom
{
    protected readonly ConcurrentDictionary<string, RunningRoom<TRoom>> _running = new();

    protected readonly ILlmClientFactory _llmFactory;
    protected readonly ToolRegistry _toolRegistry;
    protected readonly WorkspaceManager _workspaceManager;
    protected readonly AgentOptions _agentOptions;
    protected readonly ILoggerFactory _loggerFactory;
    protected readonly ILogger _logger;

    protected RoomOrchestrator(
        ILlmClientFactory llmFactory,
        ToolRegistry toolRegistry,
        WorkspaceManager workspaceManager,
        IOptions<AgentOptions> agentOptions,
        ILoggerFactory loggerFactory,
        ILogger logger)
    {
        _llmFactory = llmFactory;
        _toolRegistry = toolRegistry;
        _workspaceManager = workspaceManager;
        _agentOptions = agentOptions.Value;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected abstract string LeadId { get; }
    protected abstract TRoom CreateRoom(RoomEventPipeline pipeline, TranscriptStore store);
    protected abstract void WireSession(ConversationSession session, RoomEventPipeline pipeline, TranscriptStore store);
    protected abstract RoomState GetRoomState(ConversationSession session);
    protected abstract IReadOnlyList<RoomEvent>? GetEventLog(ConversationSession session);
    protected abstract Task StartRoomAsync(TRoom room, RunningRoom<TRoom> run, CancellationToken ct);
    protected virtual void OnFanOutEvent(RunningRoom<TRoom> run, RoomEvent evt, RoomState room) { }

    public bool IsRunning(string conversationId) => _running.ContainsKey(conversationId);

    public bool IsIdle(string conversationId)
    {
        if (!_running.TryGetValue(conversationId, out var run))
            return true;
        return run.RunTask.IsCompleted;
    }

    public bool TryCleanupIdle(string conversationId)
    {
        if (!_running.TryGetValue(conversationId, out var run))
            return true;

        if (!run.RunTask.IsCompleted || !run.Subscribers.IsEmpty)
            return false;

        if (_running.TryRemove(conversationId, out var removed))
        {
            removed.Cts.Dispose();
            return true;
        }
        return false;
    }

    public ChannelReader<byte> StartAsync(
        string conversationId,
        ConversationSession session,
        string subscriberId,
        TimeZoneInfo? clientTimeZone = null)
    {
        if (_running.TryGetValue(conversationId, out var stale)
            && (stale.Cts.IsCancellationRequested || stale.RunTask.IsCompleted))
            _running.TryRemove(conversationId, out _);

        var run = _running.GetOrAdd(conversationId, _ =>
        {
            var bus = new RoomEventBus();
            var initialLog = GetEventLog(session);
            var pipeline = new RoomEventPipeline(bus, [new ToolEffectEnricher(LeadId)]);
            var transcriptStore = new TranscriptStore();
            if (initialLog is not null)
                transcriptStore.SeedHistory(initialLog);

            var room = CreateRoom(pipeline, transcriptStore);
            WireSession(session, pipeline, transcriptStore);

            var cts = new CancellationTokenSource();
            session.StartedAt = DateTimeOffset.UtcNow;

            var created = new RunningRoom<TRoom>
            {
                Room = room,
                Session = session,
                Cts = cts,
                StartedAt = session.StartedAt,
                ClientTimeZone = clientTimeZone,
                EventLog = initialLog,
            };

            created.RunTask = RunRoomAsync(created, cts.Token);
            return created;
        });

        var sub = Channel.CreateUnbounded<byte>();
        run.Subscribers[subscriberId] = sub;
        return sub.Reader;
    }

    public ChannelReader<byte>? Subscribe(string conversationId, string subscriberId)
    {
        if (!_running.TryGetValue(conversationId, out var run))
            return null;

        var sub = Channel.CreateUnbounded<byte>();
        run.Subscribers[subscriberId] = sub;
        return sub.Reader;
    }

    public void Unsubscribe(string conversationId, string subscriberId)
    {
        if (!_running.TryGetValue(conversationId, out var run))
            return;

        if (run.Subscribers.TryRemove(subscriberId, out var sub))
            sub.Writer.TryComplete();
    }

    public ValueTask PostUserMessageAsync(string conversationId, string message, CancellationToken ct)
    {
        if (_running.TryGetValue(conversationId, out var run))
            return run.Room.PostUserMessageAsync(message, ct);
        return ValueTask.CompletedTask;
    }

    public void Cancel(string conversationId)
    {
        if (_running.TryGetValue(conversationId, out var run))
            run.Cts.Cancel();
    }

    public Task RecallSubAgentAsync(string conversationId, string agentName)
    {
        if (_running.TryGetValue(conversationId, out var run))
            return run.Room.RecallSubAgentAsync(agentName);
        return Task.CompletedTask;
    }

    public Task StandDownSubAgentAsync(string conversationId, string agentName)
    {
        if (_running.TryGetValue(conversationId, out var run))
            return run.Room.StandDownSubAgentAsync(agentName);
        return Task.CompletedTask;
    }

    private async Task RunRoomAsync(RunningRoom<TRoom> run, CancellationToken ct)
    {
        var applierReader = run.Room.Bus.Subscribe("state-applier");
        var fanOutTask = Task.Run(() => ConsumeAndFanOutAsync(run, applierReader));

        var projector = new TranscriptProjector(LeadId, evt => run.Room.Pipeline.EmitAsync(evt));
        var projectionTask = Task.Run(() => projector.RunLiveAsync(run.Room.TranscriptStore.Reader, ct));

        run.Room.RoomStateRef = GetRoomState(run.Session);

        try
        {
            await StartRoomAsync(run.Room, run, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("{RoomType} {Id} cancelled", typeof(TRoom).Name, run.Session.Id);
        }
        finally
        {
            run.Room.TranscriptStore.Append(
                new RoomEvent.SessionEnded(0, "system", DateTimeOffset.UtcNow));
            run.Room.TranscriptStore.Complete();

            try { await projectionTask; } catch (OperationCanceledException) { _logger.LogDebug("Projection task cancelled for {Id}", run.Session.Id); }

            run.Room.Bus.Complete();

            try { await fanOutTask; } catch (OperationCanceledException) { _logger.LogDebug("Fan-out task cancelled for {Id}", run.Session.Id); }

            await _workspaceManager.SaveSessionAsync(run.Session);
        }
    }

    private async Task ConsumeAndFanOutAsync(
        RunningRoom<TRoom> run, ChannelReader<RoomEvent> applierReader)
    {
        var room = GetRoomState(run.Session);
        var projector = new UxProjector(LeadId);
        var mutator = new RoomState.Mutator(room);
        var lastSave = DateTimeOffset.UtcNow;
        try
        {
            await foreach (var evt in applierReader.ReadAllAsync())
            {
                lock (room.Lock)
                {
                    foreach (var ux in projector.Project(evt))
                        mutator.Apply(ux);
                    mutator.PublishView();
                }

                OnFanOutEvent(run, evt, room);

                foreach (var (_, sub) in run.Subscribers)
                    sub.Writer.TryWrite(0);

                if (DateTimeOffset.UtcNow - lastSave > TimeSpan.FromSeconds(30))
                {
                    await _workspaceManager.SaveSessionAsync(run.Session);
                    lastSave = DateTimeOffset.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) { _logger.LogDebug("{RoomType} {Id} consumer loop cancelled", typeof(TRoom).Name, run.Session.Id); }
        finally
        {
            foreach (var (_, sub) in run.Subscribers)
                sub.Writer.TryComplete();

            _logger.LogInformation("{RoomType} {Id} fan-out completed", typeof(TRoom).Name, run.Session.Id);
        }
    }
}
