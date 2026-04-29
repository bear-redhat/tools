using System.Threading.Channels;
using Investigator.Models;
using Investigator.Services;
using Investigator.Tools;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Investigator.Components.Pages;

public partial class Chat : IAsyncDisposable
{
    [Inject] private InvestigationOrchestrator Orchestrator { get; set; } = default!;
    [Inject] private WorkspaceManager WorkspaceMgr { get; set; } = default!;
    [Inject] private ConversationStore Store { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<Chat> Logger { get; set; } = default!;
    [Inject] private AuthSettings AuthSettings { get; set; } = default!;
    [Inject] private CircuitAuthState CircuitAuth { get; set; } = default!;

    [Parameter] public string ConversationId { get; set; } = "";

    private readonly string _circuitId = Guid.NewGuid().ToString("N");
    private ConversationSession? _session;
    private bool _isOwner;
    private bool _forcedReadonly;
    private bool _shareCopied;
    private bool _started;
    private Task? _eventLoopTask;

    private string? _highlightedStepId;
    private string _selectedMemberId = "all";
    private bool _forceScrollOnRender;
    private bool _scrollAfterRender;
    private bool _jsInitialized;
    private bool _wasWorking;
    private ElementReference _messagesRef;
    private ElementReference _logRef;
    private ElementReference _dividerRef;
    private ElementReference _inputDividerRef;

    private List<ConversationItem> _conversationItems => _session?.Items ?? [];
    private List<LogEntryModel> _logEntries => _session?.LogEntries ?? [];
    private List<ChatMessage> _history => _session?.History ?? [];
    private List<GroupMember> _members => _session?.Members ?? [];
    private bool _isInvestigating => _session?.IsInvestigating ?? false;
    private bool _hasWorkingAgents => _session?.HasWorkingAgents ?? false;

    private GroupMember? _selectedScout =>
        _selectedMemberId is "all" or "little-bear"
            ? null
            : _members.FirstOrDefault(m => m.Id == _selectedMemberId);
    private bool _isScoutSelected => _selectedScout is not null;
    private bool _selectedScoutIsWorking => _selectedScout?.Status == MemberStatus.Working;
    private bool _scoutActionInFlight;
    private bool _isAgentSelected => _selectedMemberId is not "all";

    private AgentUsage? GetSelectedAgentUsage()
    {
        if (_session is null || _selectedMemberId == "all") return null;
        var member = _members.FirstOrDefault(m => m.Id == _selectedMemberId);
        if (member is null) return null;
        return _session.UsageByAgent.TryGetValue(member.Name, out var usage) ? usage : null;
    }

    private static readonly HashSet<ConversationItemType> s_roomTypes =
    [
        ConversationItemType.UserMessage,
        ConversationItemType.AssistantMessage,
        ConversationItemType.SubAgentMessage,
        ConversationItemType.ScoutQuestion,
        ConversationItemType.Conclusion,
        ConversationItemType.Error,
        ConversationItemType.Dispatch,
        ConversationItemType.Welcome,
    ];

    private bool _showFindings => _selectedMemberId is "all";

    private IEnumerable<ConversationItem> FilteredConversation
    {
        get
        {
            if (_selectedMemberId == "all")
                return _conversationItems.Where(i => s_roomTypes.Contains(i.Type));

            var id = _selectedMemberId;
            var items = _conversationItems.Where(i =>
                i.Sender == id || i.Recipient == id);

            if (_session?.DetailEvents.TryGetValue(id, out var details) == true)
                items = items.Concat(details).OrderBy(i => i.Timestamp);

            return items;
        }
    }

    private IEnumerable<ConversationItem> FilteredFindings =>
        _conversationItems.Where(i => i.Type is ConversationItemType.Finding or ConversationItemType.Conclusion);

    private IEnumerable<LogEntryModel> FilteredLog
    {
        get
        {
            if (_selectedMemberId == "little-bear")
                return _logEntries.Where(e => e.Sender == "little-bear");

            if (_session?.DetailLogEntries.TryGetValue(_selectedMemberId, out var logs) == true)
                return logs;

            return [];
        }
    }

    protected override async Task OnInitializedAsync()
    {
        var isViewRoute = Nav.ToBaseRelativePath(Nav.Uri)
            .TrimEnd('/')
            .EndsWith("/view", StringComparison.OrdinalIgnoreCase);

        _session = Store.TryGetSession(ConversationId);
        if (_session is null)
        {
            _session = await Store.TryGetOrLoadSessionAsync(ConversationId, WorkspaceMgr);
            if (_session is null)
            {
                Nav.NavigateTo("/", forceLoad: true);
                return;
            }

            if (!isViewRoute)
            {
                Nav.NavigateTo($"/c/{ConversationId}/view", forceLoad: true);
                return;
            }
        }

        if (isViewRoute)
        {
            _forcedReadonly = true;
            _isOwner = false;
            return;
        }

        if (AuthSettings.IsEnabled && !CircuitAuth.IsAuthenticated)
        {
            Nav.NavigateTo($"/login?returnUrl=/c/{ConversationId}");
            return;
        }

        var claim = Store.TryClaim(ConversationId, _circuitId, CircuitAuth.UserId);
        if (claim == ClaimResult.WrongUser)
        {
            Nav.NavigateTo($"/c/{ConversationId}/view", forceLoad: true);
            return;
        }
        _isOwner = claim == ClaimResult.Success;

        if (_isOwner && Orchestrator.IsRunning(ConversationId))
        {
            var reader = Orchestrator.Subscribe(ConversationId, _circuitId);
            if (reader is not null)
            {
                _eventLoopTask = RunEventLoopAsync(reader);
                _started = true;
                _wasWorking = true;
            }
        }
    }

    private void Refresh()
    {
        Nav.NavigateTo($"/c/{ConversationId}", forceLoad: true);
    }

    private async Task CopyShareLink()
    {
        var uri = Nav.ToAbsoluteUri($"/c/{ConversationId}/view");
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", uri.ToString());
        _shareCopied = true;
        StateHasChanged();
        await Task.Delay(2000);
        _shareCopied = false;
        StateHasChanged();
    }

    private void OnMemberSelected(string memberId)
    {
        _selectedMemberId = memberId;
        _forceScrollOnRender = true;
        StateHasChanged();
    }

    private async Task OnRecallScout(string scoutName)
    {
        _scoutActionInFlight = true;
        try { await Orchestrator.RecallScoutAsync(ConversationId, scoutName); }
        finally { _scoutActionInFlight = false; }
    }

    private async Task OnStandDownScout(string scoutName)
    {
        _scoutActionInFlight = true;
        try { await Orchestrator.StandDownScoutAsync(ConversationId, scoutName); }
        finally { _scoutActionInFlight = false; }
    }

    private async Task OnSendAsync(string message)
    {
        if (!_isOwner || _session is null || string.IsNullOrWhiteSpace(message)) return;

        lock (_session.Lock)
        {
            _session.Items.Add(new ConversationItem
            {
                Type = ConversationItemType.UserMessage,
                Sender = "user",
                Recipient = "little-bear",
                Content = message,
                Timestamp = DateTimeOffset.UtcNow,
            });

            _session.History.Add(new ChatMessage(ChatRole.User, message, DateTimeOffset.UtcNow));
        }

        _wasWorking = true;

        if (!_started)
        {
            _session.WorkspacePath ??= WorkspaceMgr.CreateWorkspace(_session.Id);
            var reader = Orchestrator.StartAsync(ConversationId, _session, _circuitId);
            _eventLoopTask = RunEventLoopAsync(reader);
            _started = true;
        }

        await Orchestrator.PostUserMessageAsync(ConversationId, message, CancellationToken.None);
        _scrollAfterRender = true;
        await InvokeAsync(StateHasChanged);
    }

    private async Task RunEventLoopAsync(ChannelReader<AgentEvent> events)
    {
        try
        {
            await foreach (var evt in events.ReadAllAsync(CancellationToken.None))
            {
                HandleUiEvent(evt);
                _scrollAfterRender = true;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) { Logger.LogDebug("Event loop cancelled"); }
        catch (ChannelClosedException) { Logger.LogDebug("Subscriber channel closed"); }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Event loop ended with error");
        }
        finally
        {
            if (_wasWorking)
            {
                _wasWorking = false;
                try { await JS.InvokeVoidAsync("playCaseClosedSound"); }
                catch (JSDisconnectedException) { Logger.LogDebug("JS disconnected during case-closed sound (finally)"); }
            }
            _scrollAfterRender = true;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void HandleUiEvent(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentEvent.StatusChanged sc when !sc.IsActive:
                TryPlayCaseClosedSound();
                break;
        }
    }

    private void TryPlayCaseClosedSound()
    {
        if (_isInvestigating || _hasWorkingAgents || !_wasWorking) return;
        _wasWorking = false;
        try { _ = JS.InvokeVoidAsync("playCaseClosedSound"); }
        catch (JSDisconnectedException) { Logger.LogDebug("JS disconnected during case-closed sound"); }
    }

    private void OnCancel() => Orchestrator.Cancel(ConversationId);

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

    private void ScrollToLogEntry(string stepId)
    {
        _highlightedStepId = stepId;
        StateHasChanged();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender || !_jsInitialized)
        {
            try
            {
                await JS.InvokeVoidAsync("initAutoScroll", _messagesRef);
                await JS.InvokeVoidAsync("initAutoScroll", _logRef);
                _jsInitialized = await JS.InvokeAsync<bool>("initDividerResize", _dividerRef, "col");
                await JS.InvokeAsync<bool>("initDividerResize", _inputDividerRef, "row");
            }
            catch (JSDisconnectedException) { Logger.LogDebug("JS disconnected during interop initialization"); }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "JS interop initialization failed (firstRender={FirstRender})", firstRender);
            }
        }

        try
        {
            if (_forceScrollOnRender)
            {
                _forceScrollOnRender = false;
                await JS.InvokeVoidAsync("forceScrollToBottom", _messagesRef);
                await JS.InvokeVoidAsync("forceScrollToBottom", _logRef);
            }
            else if (_scrollAfterRender)
            {
                _scrollAfterRender = false;
                await JS.InvokeVoidAsync("scrollToBottom", _messagesRef);
                await JS.InvokeVoidAsync("scrollToBottom", _logRef);
            }
        }
        catch (JSDisconnectedException) { Logger.LogDebug("JS disconnected during scroll"); }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "JS scroll interop failed");
        }
    }

    private readonly HashSet<ConversationItem> _expandedFindings = [];

    private void ToggleFinding(ConversationItem item)
    {
        if (!_expandedFindings.Remove(item))
            _expandedFindings.Add(item);
    }

    private bool IsFindingExpanded(ConversationItem item) => _expandedFindings.Contains(item);

    private static string Truncate(string text, int maxLength = 100)
    {
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + "...";
    }

    public async ValueTask DisposeAsync()
    {
        Orchestrator.Unsubscribe(ConversationId, _circuitId);

        if (_eventLoopTask is not null)
        {
            try { await _eventLoopTask; }
            catch (Exception ex) { Logger.LogDebug(ex, "Event loop task completed with error during dispose"); }
        }

        if (_isOwner && _session is not null)
        {
            Store.Release(_session.Id, _circuitId);
        }
    }
}
