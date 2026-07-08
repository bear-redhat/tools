using System.Collections.Concurrent;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Tools;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public sealed class RemediationOrchestrator : RoomOrchestrator<RemediationRoom>
{
    private readonly ConcurrentDictionary<string, Dictionary<int, RoomEvent.ToolRequest>> _pendingRequests = new();

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

    protected override Task StartRoomAsync(RemediationRoom room, RunningRoom<RemediationRoom> run, CancellationToken ct)
    {
        _pendingRequests[run.Session.Id] = new Dictionary<int, RoomEvent.ToolRequest>();
        return room.StartAsync(
            run.Session.WorkspacePath!,
            run.Session.Remediation!.CaseFile!,
            ct,
            eventLog: run.EventLog,
            userId: run.Session.OwnerUserId,
            conversationId: run.Session.Id,
            clientTimeZone: run.ClientTimeZone);
    }

    protected override void OnFanOutEvent(RunningRoom<RemediationRoom> run, RoomEvent evt, RoomState room)
    {
        if (!_pendingRequests.TryGetValue(run.Session.Id, out var pending))
            return;

        if (evt is RoomEvent.ToolRequest tr)
            pending[tr.Seq] = tr;

        if (evt is RoomEvent.ToolResponse { Tool: "refer_back" } rb
            && pending.TryGetValue(rb.RequestSeq, out var req))
        {
            TriggerReferBack(run, req.Input);
        }
    }

    public new bool TryCleanupIdle(string conversationId)
    {
        var result = base.TryCleanupIdle(conversationId);
        if (result)
            _pendingRequests.TryRemove(conversationId, out _);
        return result;
    }

    private void TriggerReferBack(RunningRoom<RemediationRoom> run, JsonElement input)
    {
        var reason = input.TryGetProperty("reason", out var r) ? r.GetString() : null;
        var suggestedDirection = input.TryGetProperty("suggested_direction", out var sd) ? sd.GetString() : null;

        EvidenceChain? disprovalEvidence = null;
        if (input.TryGetProperty("evidence", out var evidenceArray) && evidenceArray.ValueKind == JsonValueKind.Array)
        {
            var steps = new List<EvidenceStep>();
            foreach (var item in evidenceArray.EnumerateArray())
            {
                steps.Add(new EvidenceStep(
                    Step: item.TryGetProperty("step", out var st) ? st.GetInt32() : steps.Count + 1,
                    Reasoning: item.TryGetProperty("reasoning", out var rs) ? rs.GetString() : null,
                    Finding: item.TryGetProperty("finding", out var f) ? f.GetString() : null,
                    Cluster: null,
                    Proof: item.TryGetProperty("proof", out var prf) ? prf.GetString() : null));
            }
            disprovalEvidence = new EvidenceChain(steps.OrderBy(s => s.Step).ToList());
        }

        var referral = new CaseReferral(reason, disprovalEvidence, suggestedDirection);
        run.Session.PendingReferral = referral;
        _logger.LogInformation("Case referred back to Little Bear for investigation {Id}: {Reason}", run.Session.Id, reason);
    }
}
