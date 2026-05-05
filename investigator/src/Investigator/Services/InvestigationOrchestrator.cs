using System.Collections.Concurrent;
using System.Text.Json;
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

public sealed class InvestigationOrchestrator : RoomOrchestrator<InvestigationRoom>
{
    private readonly ConcurrentDictionary<string, Dictionary<int, RoomEvent.ToolRequest>> _pendingRequests = new();

    public InvestigationOrchestrator(
        ILlmClientFactory llmFactory,
        ToolRegistry toolRegistry,
        WorkspaceManager workspaceManager,
        IOptions<AgentOptions> agentOptions,
        ILoggerFactory loggerFactory)
        : base(llmFactory, toolRegistry, workspaceManager, agentOptions, loggerFactory,
            loggerFactory.CreateLogger<InvestigationOrchestrator>())
    {
    }

    protected override string LeadId => "little-bear";

    protected override InvestigationRoom CreateRoom(RoomEventPipeline pipeline, TranscriptStore store) =>
        new(_llmFactory, _toolRegistry, _agentOptions, pipeline, store,
            _loggerFactory.CreateLogger<InvestigationRoom>());

    protected override void WireSession(ConversationSession session, RoomEventPipeline pipeline, TranscriptStore store)
    {
        session.InvestigationPipeline = pipeline;
        session.InvestigationTranscriptStore = store;
    }

    protected override RoomState GetRoomState(ConversationSession session) => session.Investigation;

    protected override IReadOnlyList<RoomEvent>? GetEventLog(ConversationSession session) =>
        session.LoadedInvestigationEvents;

    protected override Task StartRoomAsync(InvestigationRoom room, RunningRoom<InvestigationRoom> run, CancellationToken ct)
    {
        _pendingRequests[run.Session.Id] = new Dictionary<int, RoomEvent.ToolRequest>();
        return room.StartAsync(
            run.Session.WorkspacePath!,
            ct,
            eventLog: run.EventLog,
            userId: run.Session.OwnerUserId,
            conversationId: run.Session.Id,
            clientTimeZone: run.ClientTimeZone);
    }

    protected override void OnFanOutEvent(RunningRoom<InvestigationRoom> run, RoomEvent evt, RoomState room)
    {
        if (!_pendingRequests.TryGetValue(run.Session.Id, out var pending))
            return;

        if (evt is RoomEvent.ToolRequest tr)
            pending[tr.Seq] = tr;

        if (evt is RoomEvent.ToolResponse { Tool: "commission_remedy" } cr
            && pending.TryGetValue(cr.RequestSeq, out var req))
        {
            TriggerRemediation(run, req.Input, room);
        }
    }

    public IReadOnlyList<ActiveInvestigationInfo> GetActiveInvestigations()
    {
        var list = new List<ActiveInvestigationInfo>();
        foreach (var (convId, run) in _running)
        {
            var session = run.Session;
            var room = session.Investigation;
            var firstMsg = room.Items.OfType<ConversationItem.UserMessage>().FirstOrDefault();
            var summary = firstMsg?.Content ?? "";
            if (summary.Length > 120)
                summary = summary[..120] + "...";

            var agentCount = room.Members.Count(m =>
                m.Id is not "all" and not "little-bear");

            list.Add(new ActiveInvestigationInfo(
                convId,
                run.StartedAt,
                session.OwnerUserId,
                summary,
                agentCount,
                room.HasWorkingAgents,
                session.Remediation is not null));
        }
        return list;
    }

    private void TriggerRemediation(RunningRoom<InvestigationRoom> run, JsonElement input, RoomState room)
    {
        if (run.Session.Remediation is not null) return;

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
            ParentConversationId: run.Session.Id,
            CaseStatement: caseDesc,
            Findings: findings,
            Summary: rootCause,
            Evidence: null,
            Fix: fix);

        run.Session.AddRemediationRoom(caseFile);
        _logger.LogInformation("Remediation commissioned for investigation {Id}", run.Session.Id);
    }
}
