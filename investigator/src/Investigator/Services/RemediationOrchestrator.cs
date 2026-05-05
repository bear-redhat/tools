using Investigator.Contracts;
using Investigator.Models;
using Investigator.Tools;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public sealed class RemediationOrchestrator : RoomOrchestrator<RemediationRoom>
{
    public RemediationOrchestrator(
        ILlmClientFactory llmFactory,
        ToolRegistry toolRegistry,
        WorkspaceManager workspaceManager,
        IOptions<AgentOptions> agentOptions,
        ILoggerFactory loggerFactory)
        : base(llmFactory, toolRegistry, workspaceManager, agentOptions, loggerFactory,
            loggerFactory.CreateLogger<RemediationOrchestrator>())
    {
    }

    protected override string LeadId => "langur";

    protected override RemediationRoom CreateRoom(RoomEventPipeline pipeline, TranscriptStore store) =>
        new(_llmFactory, _toolRegistry, _agentOptions, pipeline, store,
            _loggerFactory.CreateLogger<RemediationRoom>());

    protected override void WireSession(ConversationSession session, RoomEventPipeline pipeline, TranscriptStore store)
    {
        session.RemediationPipeline = pipeline;
        session.RemediationTranscriptStore = store;
    }

    protected override RoomState GetRoomState(ConversationSession session) => session.Remediation!;

    protected override IReadOnlyList<RoomEvent>? GetEventLog(ConversationSession session) =>
        session.LoadedRemediationEvents;

    protected override Task StartRoomAsync(RemediationRoom room, RunningRoom<RemediationRoom> run, CancellationToken ct) =>
        room.StartAsync(
            run.Session.WorkspacePath!,
            run.Session.Remediation!.CaseFile!,
            ct,
            eventLog: run.EventLog,
            userId: run.Session.OwnerUserId,
            conversationId: run.Session.Id,
            clientTimeZone: run.ClientTimeZone);
}
