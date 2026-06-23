using System.Text.Json;
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
    [Inject] private RemediationOrchestrator RemediationOrch { get; set; } = default!;
    [Inject] private WorkspaceManager WorkspaceMgr { get; set; } = default!;
    [Inject] private ConversationStore Store { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<Chat> Logger { get; set; } = default!;
    [Inject] private AuthSettings AuthSettings { get; set; } = default!;
    [Inject] private CircuitAuthState CircuitAuth { get; set; } = default!;
    [Inject] private BrowserTimeZone BrowserTz { get; set; } = default!;
    [Inject] private AuditLog AuditLog { get; set; } = default!;
    [Inject] private IHttpContextAccessor HttpContextAccessor { get; set; } = default!;

    [Parameter] public string ConversationId { get; set; } = "";

    private readonly string _circuitId = Guid.NewGuid().ToString("N");
    private ConversationSession? _session;
    private SessionView _view = new();
    private SessionView? _remView;
    private string _activeRoom = "investigation";
    private bool _isOwner;
    private bool _forcedReadonly;
    private bool _shareCopied;
    private bool _started;
    private bool _remStarted;
    private bool _interactive;
    private bool _sessionNotFound;
    private Task? _eventLoopTask;
    private Task? _remEventLoopTask;

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

    private List<ConversationItem> _filteredItems = [];
    private List<LogEntryModel> _filteredLogItems = [];

    private SessionView ActiveView =>
        _activeRoom == "remediation" && _remView is not null ? _remView : _view;

    private IReadOnlyList<ConversationItem> _conversationItems => ActiveView.Items;
    private IReadOnlyList<LogEntryModel> _logEntries => ActiveView.LogEntries;
    private IReadOnlyList<GroupMember> _invMembers => _view.Members;
    private IReadOnlyList<GroupMember>? _remMembers => _remView?.Members;
    private IReadOnlyList<GroupMember> _activeMembers => ActiveView.Members;
    private bool _isInvestigating => ActiveView.IsInvestigating;
    private bool _hasWorkingAgents => ActiveView.HasWorkingAgents;
    private bool _isRemediation => _activeRoom == "remediation";

    private bool IsLeadAgent(string id) =>
        _activeRoom == "remediation" ? id == "langur" : id == "little-bear";

    private GroupMember? _selectedScout =>
        _selectedMemberId is "all" || IsLeadAgent(_selectedMemberId)
            ? null
            : _activeMembers.FirstOrDefault(m => m.Id == _selectedMemberId);
    private bool _isScoutSelected => _selectedScout is not null;
    private bool _selectedScoutIsWorking => _selectedScout?.Status == MemberStatus.Working;
    private bool _scoutActionInFlight;
    private bool _isAgentSelected => _selectedMemberId is not "all";

    private AgentUsage? GetSelectedAgentUsage()
    {
        if (_selectedMemberId == "all") return null;
        var member = _activeMembers.FirstOrDefault(m => m.Id == _selectedMemberId);
        if (member is null) return null;
        return ActiveView.UsageByAgent.TryGetValue(member.Id, out var usage) ? usage : null;
    }

    private static bool IsRoomVisible(ConversationItem item) => item is
        ConversationItem.UserMessage or ConversationItem.AgentMessage or
        ConversationItem.ScoutReport or ConversationItem.ScoutQuestion or
        ConversationItem.Conclusion or ConversationItem.Error or
        ConversationItem.Dispatch or ConversationItem.Welcome or
        ConversationItem.SignOffItem or ConversationItem.CaseReceived or
        ConversationItem.MemorySaved or ConversationItem.MemoryRecalled;

    private bool _showFindings => _selectedMemberId is "all";

    private void RefreshFilteredItems()
    {
        var items = ActiveView.Items;
        if (_selectedMemberId == "all")
            _filteredItems = items.Where(IsRoomVisible).ToList();
        else
        {
            var id = _selectedMemberId;
            _filteredItems = items
                .Where(i => i.SenderId == id || i.RecipientId == id)
                .OrderBy(i => i.Timestamp)
                .ToList();
        }

        if (_selectedMemberId != "all" && !IsLeadAgent(_selectedMemberId))
        {
            _filteredLogItems = _logEntries
                .Where(e => e.Sender == _selectedMemberId)
                .ToList();
        }
        else if (IsLeadAgent(_selectedMemberId))
        {
            _filteredLogItems = _logEntries
                .Where(e => e.Sender == _selectedMemberId)
                .ToList();
        }
        else
        {
            _filteredLogItems = [];
        }
    }

    private IEnumerable<ConversationItem> FilteredFindings =>
        _conversationItems.Where(i => i is ConversationItem.Finding or ConversationItem.Conclusion);

    protected virtual bool IsReadonly => false;

    protected override async Task OnInitializedAsync()
    {
        _forcedReadonly = IsReadonly;

        _session = Store.TryGetSession(ConversationId);
        if (_session is null)
        {
            _session = await Store.TryGetOrLoadSessionAsync(ConversationId, WorkspaceMgr);
            if (_session is null)
            {
                if (_forcedReadonly)
                {
                    _sessionNotFound = true;
                    return;
                }
                Nav.NavigateTo("/", forceLoad: true);
                return;
            }

            if (!_forcedReadonly)
            {
                Nav.NavigateTo($"/c/{ConversationId}/view", forceLoad: true);
                return;
            }
        }

        _view = _session.Investigation.CurrentView;
        if (_session.Remediation is not null)
            _remView = _session.Remediation.CurrentView;

        RefreshFilteredItems();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _interactive = true;

            if (!_forcedReadonly && _session is not null)
            {
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

                if (_isOwner)
                {
                    var ip = HttpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
                    AuditLog.Record(ConversationId, "claimed", CircuitAuth.UserId, ip);
                }
            }

            if (_forcedReadonly && _session is not null)
            {
                var ip = HttpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
                AuditLog.Record(ConversationId, "viewed", CircuitAuth.UserId, ip);
            }

            if (_session is not null)
            {
                if (Orchestrator.IsRunning(ConversationId))
                {
                    var reader = Orchestrator.Subscribe(ConversationId, _circuitId);
                    if (reader is not null)
                    {
                        _eventLoopTask = RunRoomEventLoopAsync(reader, _session.Investigation);
                        _started = true;
                        _wasWorking = true;
                    }
                }
                else if (_isOwner && !_started && _session.Investigation.HasWorkingAgents)
                {
                    _session.WorkspacePath ??= WorkspaceMgr.CreateWorkspace(_session.Id);
                    var reader = Orchestrator.StartAsync(ConversationId, _session, _circuitId, BrowserTz.TimeZone);
                    _eventLoopTask = RunRoomEventLoopAsync(reader, _session.Investigation);
                    _started = true;
                    _wasWorking = true;
                }

                if (_session.Remediation is not null && RemediationOrch.IsRunning(ConversationId))
                {
                    var reader = RemediationOrch.Subscribe(ConversationId, _circuitId);
                    if (reader is not null)
                    {
                        _remEventLoopTask = RunRoomEventLoopAsync(reader, _session.Remediation);
                        _remStarted = true;
                        _wasWorking = true;
                    }
                }
                else if (_isOwner && !_remStarted
                    && _session.Remediation is not null
                    && _session.Remediation.HasWorkingAgents)
                {
                    _session.WorkspacePath ??= WorkspaceMgr.CreateWorkspace(_session.Id);
                    var reader = RemediationOrch.StartAsync(ConversationId, _session, _circuitId, BrowserTz.TimeZone);
                    _remEventLoopTask = RunRoomEventLoopAsync(reader, _session.Remediation);
                    _remStarted = true;
                    _wasWorking = true;
                }
            }

            StateHasChanged();
        }

        if (!_jsInitialized)
        {
            try
            {
                await JS.InvokeVoidAsync("initAutoScroll", _messagesRef);
                await JS.InvokeVoidAsync("initAutoScroll", _logRef);
                _jsInitialized = await JS.InvokeAsync<bool>("initDividerResize", _dividerRef, "col");
                await JS.InvokeAsync<bool>("initDividerResize", _inputDividerRef, "row");
            }
            catch (JSDisconnectedException) { Logger.LogDebug("JS disconnected during interop initialization"); }
            catch (JSException ex)
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
        catch (JSException ex)
        {
            Logger.LogDebug(ex, "JS scroll interop failed");
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
        if (memberId.StartsWith("rem:"))
        {
            _activeRoom = "remediation";
            _selectedMemberId = memberId[4..];
        }
        else
        {
            _activeRoom = "investigation";
            _selectedMemberId = memberId;
        }
        RefreshFilteredItems();
        _forceScrollOnRender = true;
        StateHasChanged();
    }

    private async Task OnRecallScout(string scoutName)
    {
        _scoutActionInFlight = true;
        try
        {
            if (_isRemediation)
                await RemediationOrch.RecallSubAgentAsync(ConversationId, scoutName);
            else
                await Orchestrator.RecallSubAgentAsync(ConversationId, scoutName);
        }
        finally { _scoutActionInFlight = false; }
    }

    private async Task OnStandDownScout(string scoutName)
    {
        _scoutActionInFlight = true;
        try
        {
            if (_isRemediation)
                await RemediationOrch.StandDownSubAgentAsync(ConversationId, scoutName);
            else
                await Orchestrator.StandDownSubAgentAsync(ConversationId, scoutName);
        }
        finally { _scoutActionInFlight = false; }
    }

    private void TryStartRemediation()
    {
        if (_session?.Remediation is null || _remStarted) return;

        _remView = _session.Remediation.CurrentView;
        _session.WorkspacePath ??= WorkspaceMgr.CreateWorkspace(_session.Id);
        var reader = RemediationOrch.StartAsync(ConversationId, _session, _circuitId, BrowserTz.TimeZone);
        _remEventLoopTask = RunRoomEventLoopAsync(reader, _session.Remediation);
        _remStarted = true;
    }

    private void CommissionRemedyFromUI(ConversationItem.Conclusion conclusion)
    {
        if (_session is null || _session.Remediation is not null) return;

        var input = JsonSerializer.SerializeToElement(new
        {
            case_description = conclusion.Content,
            root_cause = conclusion.Content,
            fix_description = conclusion.Fix?.Description,
            fix_commands = conclusion.Fix?.Commands,
        });

        var syntheticId = $"toolu_ui_{Guid.NewGuid():N}";

        var assistantMsg = new LlmMessage { Role = "assistant", Content = JsonSerializer.SerializeToElement(new object[] {
            new { type = "tool_use", id = syntheticId, name = "commission_remedy", input }
        })};
        var resultMsg = new LlmMessage { Role = "user", Content = JsonSerializer.SerializeToElement(new[] {
            new { type = "tool_result", tool_use_id = syntheticId, content = "Remediation commissioned." }
        })};

        var ctx = new RoomEvent.LlmContext(0, "little-bear", DateTimeOffset.UtcNow, [assistantMsg, resultMsg]);
        _session.InvestigationTranscriptStore?.Append(ctx);
    }

    private async Task OnSendAsync(string message)
    {
        if (!_isOwner || _session is null || string.IsNullOrWhiteSpace(message)) return;
        if (ActiveView.Phase == RoomPhase.Recovering) return;

        if (_activeRoom == "remediation")
        {
            if (!_remStarted)
            {
                _session.WorkspacePath ??= WorkspaceMgr.CreateWorkspace(_session.Id);
                var reader = RemediationOrch.StartAsync(ConversationId, _session, _circuitId, BrowserTz.TimeZone);
                _remEventLoopTask = RunRoomEventLoopAsync(reader, _session.Remediation!);
                _remStarted = true;
            }
            await RemediationOrch.PostUserMessageAsync(ConversationId, message, CancellationToken.None);
        }
        else
        {
            if (!_started)
            {
                _session.WorkspacePath ??= WorkspaceMgr.CreateWorkspace(_session.Id);
                var reader = Orchestrator.StartAsync(ConversationId, _session, _circuitId, BrowserTz.TimeZone);
                _eventLoopTask = RunRoomEventLoopAsync(reader, _session.Investigation);
                _started = true;
            }
            await Orchestrator.PostUserMessageAsync(ConversationId, message, CancellationToken.None);
        }

        _forceScrollOnRender = true;
        await InvokeAsync(StateHasChanged);
    }

    private async Task RunRoomEventLoopAsync(ChannelReader<byte> ticks, RoomState room)
    {
        try
        {
            await foreach (var tick in ticks.ReadAllAsync(CancellationToken.None))
            {
                while (ticks.TryRead(out _)) { }

                UpdateRoomView(room);
                RefreshFilteredItems();
                if (_view.IsInvestigating || _view.HasWorkingAgents
                    || (_remView is not null && (_remView.IsInvestigating || _remView.HasWorkingAgents)))
                    _wasWorking = true;
                TryStartRemediation();
                TryPlayCaseClosedSound();
                _scrollAfterRender = IsRoomActive(room);
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) { Logger.LogDebug("Event loop cancelled for {Room}", room.Name); }
        catch (ChannelClosedException) { Logger.LogDebug("Subscriber channel closed for {Room}", room.Name); }
        finally
        {
            if (room == _session!.Investigation)
                _started = false;
            else
                _remStarted = false;

            TryPlayCaseClosedSound();
            UpdateRoomView(room);
            RefreshFilteredItems();
            if (IsRoomActive(room)) _scrollAfterRender = true;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void UpdateRoomView(RoomState room)
    {
        if (room == _session!.Investigation)
            _view = room.CurrentView;
        else
            _remView = room.CurrentView;
    }

    private bool IsRoomActive(RoomState room) =>
        room == _session!.Investigation
            ? _activeRoom == "investigation"
            : _activeRoom == "remediation";

    private void TryPlayCaseClosedSound()
    {
        if (_view.IsInvestigating || _view.HasWorkingAgents) return;
        if (_remView is not null && (_remView.IsInvestigating || _remView.HasWorkingAgents)) return;
        if (!_wasWorking) return;
        _wasWorking = false;
        try { _ = JS.InvokeVoidAsync("playCaseClosedSound"); }
        catch (JSDisconnectedException) { Logger.LogDebug("JS disconnected during case-closed sound"); }
    }

    private void OnCancel()
    {
        if (_activeRoom == "remediation")
            RemediationOrch.Cancel(ConversationId);
        else
            Orchestrator.Cancel(ConversationId);
    }

    private void ScrollToLogEntry(string stepId)
    {
        _highlightedStepId = stepId;
        StateHasChanged();
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
        RemediationOrch.Unsubscribe(ConversationId, _circuitId);

        if (_eventLoopTask is not null)
        {
            try { await _eventLoopTask; }
            catch (OperationCanceledException) { Logger.LogDebug("Event loop task cancelled during dispose"); }
            catch (ChannelClosedException) { Logger.LogDebug("Event loop channel closed during dispose"); }
        }
        if (_remEventLoopTask is not null)
        {
            try { await _remEventLoopTask; }
            catch (OperationCanceledException) { Logger.LogDebug("Remediation event loop task cancelled during dispose"); }
            catch (ChannelClosedException) { Logger.LogDebug("Remediation event loop channel closed during dispose"); }
        }

        if (_session is not null && _isOwner)
        {
            try { await WorkspaceMgr.SaveSessionAsync(_session); }
            catch (IOException ex) { Logger.LogWarning(ex, "Failed to save session on circuit disconnect"); }
        }

        if (_isOwner && _session is not null)
        {
            var bothIdle = Orchestrator.IsIdle(ConversationId) && RemediationOrch.IsIdle(ConversationId);
            if (bothIdle)
            {
                Orchestrator.TryCleanupIdle(ConversationId);
                RemediationOrch.TryCleanupIdle(ConversationId);
            }
            Store.Release(_session.Id, _circuitId);
            if (bothIdle)
                Store.UnloadSession(_session.Id);
        }
    }
}
