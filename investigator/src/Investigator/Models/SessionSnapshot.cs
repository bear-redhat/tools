using Investigator.Services;

namespace Investigator.Models;

public sealed class SessionSnapshot
{
    public required string Id { get; init; }
    public string? OwnerUserId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public List<RoomEvent> Events { get; init; } = [];
    public List<RoomEvent> RemediationEvents { get; init; } = [];
    public RoomPhase InvestigationPhase { get; init; } = RoomPhase.Idle;
    public RoomPhase RemediationPhase { get; init; } = RoomPhase.Idle;
    public CaseFile? CaseFile { get; init; }

    public static SessionSnapshot FromSession(ConversationSession session)
    {
        return new()
        {
            Id = session.Id,
            OwnerUserId = session.OwnerUserId,
            StartedAt = session.StartedAt,
            Events = session.InvestigationTranscriptStore?.Events.ToList()
                ?? session.LoadedInvestigationEvents?.ToList()
                ?? [],
            RemediationEvents = session.RemediationTranscriptStore?.Events.ToList()
                ?? session.LoadedRemediationEvents?.ToList()
                ?? [],
            InvestigationPhase = session.Investigation.Phase,
            RemediationPhase = session.Remediation?.Phase ?? RoomPhase.Idle,
            CaseFile = session.Remediation?.CaseFile,
        };
    }

    public ConversationSession ToSession()
    {
        var session = new ConversationSession(Id);
        session.OwnerUserId = OwnerUserId;
        session.StartedAt = StartedAt;

        ReplayIntoRoom(Events, "little-bear", session.Investigation);
        session.Investigation.Phase = InvestigationPhase;

        if (CaseFile is not null)
        {
            session.AddRemediationRoom(CaseFile);
            ReplayIntoRoom(RemediationEvents, "langur", session.Remediation!);
            session.Remediation!.Phase = RemediationPhase;
        }

        session.LoadedInvestigationEvents = Events;
        session.LoadedRemediationEvents = RemediationEvents;
        return session;
    }

    private static void ReplayIntoRoom(IEnumerable<RoomEvent> events, string leadId, RoomState room)
    {
        var bus = new RoomEventBus();
        var pipeline = new RoomEventPipeline(bus, [new ToolEffectEnricher(leadId)]);
        var uxProjector = new UxProjector(leadId);
        var mutator = new RoomState.Mutator(room);

        var applier = bus.Subscribe("replay-applier");
        var projector = new TranscriptProjector(leadId, evt => pipeline.EmitAsync(evt));

        var projectionTask = projector.ReplayAsync(events);
        projectionTask.GetAwaiter().GetResult();

        while (applier.TryRead(out var evt))
        {
            foreach (var ux in uxProjector.Project(evt))
                mutator.Apply(ux);
        }

        mutator.PublishView();
    }
}
