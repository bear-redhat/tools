using System.Collections.Concurrent;
using System.Threading.Channels;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Tools;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public sealed class RemediationOrchestrator
{
    private readonly ConcurrentDictionary<string, RunningRemediation> _running = new();

    private readonly ILlmClientFactory _llmFactory;
    private readonly ToolRegistry _toolRegistry;
    private readonly WorkspaceManager _workspaceManager;
    private readonly AgentOptions _agentOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RemediationOrchestrator> _logger;

    public RemediationOrchestrator(
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
        _logger = loggerFactory.CreateLogger<RemediationOrchestrator>();
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

        var rem = _running.GetOrAdd(conversationId, _ =>
        {
            var bus = new RoomEventBus();
            var initialLog = session.LoadedRemediationEvents;
            var pipeline = new RoomEventPipeline(bus, [new ToolEffectEnricher("langur")]);
            var transcriptStore = new TranscriptStore();

            var room = new RemediationRoom(
                _llmFactory, _toolRegistry,
                _agentOptions, pipeline, transcriptStore,
                _loggerFactory.CreateLogger<RemediationRoom>());

            session.RemediationPipeline = pipeline;
            session.RemediationTranscriptStore = transcriptStore;

            var cts = new CancellationTokenSource();
            session.StartedAt = DateTimeOffset.UtcNow;

            var created = new RunningRemediation
            {
                Room = room,
                Session = session,
                Cts = cts,
                StartedAt = session.StartedAt,
                ClientTimeZone = clientTimeZone,
                EventLog = initialLog,
            };

            created.RunTask = RunRemediationAsync(created, cts.Token);
            return created;
        });

        var sub = Channel.CreateUnbounded<byte>();
        rem.Subscribers[subscriberId] = sub;
        return sub.Reader;
    }

    public ChannelReader<byte>? Subscribe(string conversationId, string subscriberId)
    {
        if (!_running.TryGetValue(conversationId, out var rem))
            return null;

        var sub = Channel.CreateUnbounded<byte>();
        rem.Subscribers[subscriberId] = sub;
        return sub.Reader;
    }

    public void Unsubscribe(string conversationId, string subscriberId)
    {
        if (!_running.TryGetValue(conversationId, out var rem))
            return;

        if (rem.Subscribers.TryRemove(subscriberId, out var sub))
            sub.Writer.TryComplete();

        if (rem.Subscribers.IsEmpty)
        {
            rem.Cts.Cancel();
            _running.TryRemove(conversationId, out _);
            rem.Cts.Dispose();
        }
    }

    public ValueTask PostUserMessageAsync(string conversationId, string message, CancellationToken ct)
    {
        if (_running.TryGetValue(conversationId, out var rem))
            return rem.Room.PostUserMessageAsync(message, ct);
        return ValueTask.CompletedTask;
    }

    public void Cancel(string conversationId)
    {
        if (_running.TryGetValue(conversationId, out var rem))
            rem.Cts.Cancel();
    }

    public Task RecallRangerAsync(string conversationId, string rangerName)
    {
        if (_running.TryGetValue(conversationId, out var rem))
            return rem.Room.RecallRangerAsync(rangerName);
        return Task.CompletedTask;
    }

    public Task StandDownRangerAsync(string conversationId, string rangerName)
    {
        if (_running.TryGetValue(conversationId, out var rem))
            return rem.Room.StandDownRangerAsync(rangerName);
        return Task.CompletedTask;
    }

    private async Task RunRemediationAsync(RunningRemediation rem, CancellationToken ct)
    {
        var applierReader = rem.Room.Bus.Subscribe("state-applier");
        var fanOutTask = Task.Run(() => ConsumeAndFanOutAsync(rem, applierReader));

        var projector = new TranscriptProjector("langur", evt => rem.Room.Pipeline.EmitAsync(evt));
        var projectionTask = Task.Run(() => projector.RunLiveAsync(rem.Room.TranscriptStore.Reader, ct));

        try
        {
            await rem.Room.StartAsync(
                rem.Session.WorkspacePath!,
                rem.Session.Remediation!.CaseFile!,
                ct,
                eventLog: rem.EventLog,
                userId: rem.Session.OwnerUserId,
                conversationId: rem.Session.Id,
                clientTimeZone: rem.ClientTimeZone);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Remediation {Id} cancelled", rem.Session.Id);
        }
        finally
        {
            rem.Room.TranscriptStore.Append(
                new RoomEvent.SessionEnded(0, "system", DateTimeOffset.UtcNow));
            rem.Room.TranscriptStore.Complete();

            try { await projectionTask; } catch (OperationCanceledException) { }

            rem.Room.Bus.Complete();

            try { await fanOutTask; } catch (OperationCanceledException) { }

            await _workspaceManager.SaveSessionAsync(rem.Session);
        }
    }

    private async Task ConsumeAndFanOutAsync(
        RunningRemediation rem, ChannelReader<RoomEvent> applierReader)
    {
        var room = rem.Session.Remediation!;
        var projector = new UxProjector("langur");
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

                foreach (var (_, sub) in rem.Subscribers)
                    sub.Writer.TryWrite(0);

                if (DateTimeOffset.UtcNow - lastSave > TimeSpan.FromSeconds(30))
                {
                    await _workspaceManager.SaveSessionAsync(rem.Session);
                    lastSave = DateTimeOffset.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var (_, sub) in rem.Subscribers)
                sub.Writer.TryComplete();

            _logger.LogInformation("Remediation {Id} fan-out completed", rem.Session.Id);
        }
    }

    // --- Internal types ---

    internal sealed class RunningRemediation
    {
        public required RemediationRoom Room { get; init; }
        public required ConversationSession Session { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public TimeZoneInfo? ClientTimeZone { get; init; }
        public IReadOnlyList<RoomEvent>? EventLog { get; init; }
        public Task RunTask { get; set; } = Task.CompletedTask;
        public ConcurrentDictionary<string, Channel<byte>> Subscribers { get; } = new();
    }
}
