using System.Threading.Channels;
using Investigator.Models;
using Investigator.Services;
using Investigator.Tools;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace Investigator.Components.Pages;

public partial class Chat : IAsyncDisposable
{
    [Inject] private InvestigationRoom Room { get; set; } = default!;
    [Inject] private WorkspaceManager WorkspaceMgr { get; set; } = default!;
    [Inject] private ConversationStore Store { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private SummarizationService Summarizer { get; set; } = default!;
    [Inject] private ILogger<Chat> Logger { get; set; } = default!;
    [Inject] private AuthSettings AuthSettings { get; set; } = default!;
    [Inject] private CircuitAuthState CircuitAuth { get; set; } = default!;

    [CascadingParameter] private Task<AuthenticationState>? AuthStateTask { get; set; }

    [Parameter] public string ConversationId { get; set; } = "";

    private readonly string _circuitId = Guid.NewGuid().ToString("N");
    private ConversationSession? _session;
    private bool _isOwner;
    private bool _forcedReadonly;
    private bool _shareCopied;
    private CancellationTokenSource? _cts;
    private bool _started;
    private Task? _eventLoopTask;

    private string? _highlightedStepId;
    private string _selectedMemberId = "all";
    private bool _forceScrollOnRender;
    private bool _scrollAfterRender;
    private bool _jsInitialized;
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

    private bool _showFindings => _selectedMemberId is "all" or "user";

    private IEnumerable<ConversationItem> FilteredConversation
    {
        get
        {
            if (_selectedMemberId == "all")
                return _conversationItems.Where(i => s_roomTypes.Contains(i.Type));

            if (_selectedMemberId == "user")
                return _conversationItems.Where(i =>
                    i.Sender == "user" || (i.Sender == "little-bear" && s_roomTypes.Contains(i.Type)));

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

        if (AuthSettings.HasOidc && AuthStateTask is not null)
        {
            var authState = await AuthStateTask;
            if (authState.User.Identity?.IsAuthenticated == true)
            {
                CircuitAuth.IsAuthenticated = true;
                CircuitAuth.UserName = authState.User.Identity.Name;
                CircuitAuth.AuthMethod = AuthMode.Oidc;
            }
        }

        if (AuthSettings.Mode != AuthMode.None && !CircuitAuth.IsAuthenticated)
        {
            Nav.NavigateTo($"/login?returnUrl=/c/{ConversationId}");
            return;
        }

        _isOwner = Store.TryClaim(ConversationId, _circuitId);
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

    private GroupMember? GetOrAddMember(string agentName)
    {
        var id = agentName.ToLowerInvariant().Replace(" ", "-");
        var existing = _members.FirstOrDefault(m => m.Id == id);
        if (existing is not null) return existing;

        var member = new GroupMember(agentName, id, MemberStatus.Working);
        _members.Add(member);
        return member;
    }

    private void SetMemberStatus(string id, MemberStatus status)
    {
        var member = _members.FirstOrDefault(m => m.Id == id);
        if (member is not null) member.Status = status;
    }

    private void UpdateHasWorkingAgents()
    {
        if (_session is null) return;
        _session.HasWorkingAgents = _members.Any(m =>
            m.Status == MemberStatus.Working && m.Id != "all" && m.Id != "user" && m.Id != "little-bear");
    }

    private async Task OnSendAsync(string message)
    {
        if (!_isOwner || _session is null || string.IsNullOrWhiteSpace(message)) return;

        _conversationItems.Add(new ConversationItem
        {
            Type = ConversationItemType.UserMessage,
            Sender = "user",
            Recipient = "little-bear",
            Content = message,
            Timestamp = DateTimeOffset.UtcNow,
        });

        _history.Add(new ChatMessage(ChatRole.User, message, DateTimeOffset.UtcNow));

        if (!_started)
        {
            _session.WorkspacePath ??= WorkspaceMgr.CreateWorkspace(_session.Id);
            _cts = new CancellationTokenSource();
            _eventLoopTask = RunEventLoopAsync(_cts.Token);
            _ = Room.StartAsync(_session.WorkspacePath, _history, _cts.Token);
            _started = true;
        }

        await Room.PostUserMessageAsync(message, _cts!.Token);
        _scrollAfterRender = true;
        await InvokeAsync(StateHasChanged);
    }

    private async Task RunEventLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in Room.Events.ReadAllAsync(ct))
            {
                HandleAgentEvent(evt);
                _scrollAfterRender = true;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _conversationItems.Add(new ConversationItem
            {
                Type = ConversationItemType.Error,
                Sender = "little-bear",
                Content = $"Investigation failed: {ex.Message}",
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
        finally
        {
            if (_session is not null)
            {
                _session.IsInvestigating = false;
                _session.HasWorkingAgents = false;
                await WorkspaceMgr.SaveSessionAsync(_session);
            }
            SetMemberStatus("little-bear", MemberStatus.Idle);
            _scrollAfterRender = true;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void HandleAgentEvent(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentEvent.StatusChanged sc:
                if (_session is not null) _session.IsInvestigating = sc.IsActive;
                SetMemberStatus("little-bear", sc.IsActive ? MemberStatus.Active : MemberStatus.Idle);
                break;

            case AgentEvent.Thinking t:
                _conversationItems.Add(new ConversationItem
                {
                    Type = ConversationItemType.Thinking,
                    Sender = "little-bear",
                    StepId = t.StepId,
                    Content = t.Text,
                    Timestamp = DateTimeOffset.UtcNow,
                });
                break;

            case AgentEvent.ToolCall tc:
                _logEntries.Add(new LogEntryModel
                {
                    Sender = "little-bear",
                    StepId = tc.StepId,
                    Tool = tc.Tool,
                    DisplayCommand = tc.DisplayCommand,
                    Timestamp = DateTimeOffset.UtcNow,
                    Status = LogEntryStatus.Running,
                    Context = AgentRunner.ExtractContext(tc.Tool, tc.Parameters),
                });
                break;

            case AgentEvent.ToolResult tr:
                var entry = _logEntries.LastOrDefault(e => e.StepId == tr.StepId)
                    ?? _logEntries.LastOrDefault();
                if (entry is not null)
                {
                    entry.Output = tr.Output;
                    entry.OutputFile = tr.OutputFile;
                    entry.ExitCode = tr.ExitCode;
                    entry.Status = tr.TimedOut ? LogEntryStatus.TimedOut : LogEntryStatus.Completed;
                }
                break;

            case AgentEvent.Message m:
                if (!string.IsNullOrWhiteSpace(m.Text))
                {
                    _conversationItems.Add(new ConversationItem
                    {
                        Type = ConversationItemType.AssistantMessage,
                        Sender = "little-bear",
                        StepId = m.StepId,
                        Content = m.Text,
                        Timestamp = DateTimeOffset.UtcNow,
                    });
                    _history.Add(new ChatMessage(ChatRole.Assistant, m.Text, DateTimeOffset.UtcNow));
                }
                break;

            case AgentEvent.Conclusion c:
                var conclusionItem = new ConversationItem
                {
                    Type = ConversationItemType.Conclusion,
                    Sender = "little-bear",
                    StepId = c.StepId,
                    Content = c.Summary,
                    Evidence = c.Evidence,
                    Fix = c.Fix,
                    Timestamp = DateTimeOffset.UtcNow,
                };
                _conversationItems.Add(conclusionItem);
                _history.Add(new ChatMessage(ChatRole.Assistant, c.Summary, DateTimeOffset.UtcNow, c.Evidence, c.Fix));
                RequestHeadline(conclusionItem);
                break;

            case AgentEvent.Error e:
                _conversationItems.Add(new ConversationItem
                {
                    Type = ConversationItemType.Error,
                    Sender = "little-bear",
                    StepId = e.StepId,
                    Content = e.ErrorMessage,
                    Timestamp = DateTimeOffset.UtcNow,
                });
                break;

            case AgentEvent.Finding f:
                if (!string.IsNullOrWhiteSpace(f.Title) || !string.IsNullOrWhiteSpace(f.Description))
                {
                    var findingItem = new ConversationItem
                    {
                        Type = ConversationItemType.Finding,
                        Sender = "little-bear",
                        StepId = f.StepId,
                        Content = $"**{f.Title}**\n{f.Description}",
                        Timestamp = DateTimeOffset.UtcNow,
                    };
                    _conversationItems.Add(findingItem);
                    RequestSummary(findingItem, oneLine: true);
                }
                break;

            case AgentEvent.ScoutAsked sq:
            {
                if (!string.IsNullOrWhiteSpace(sq.Question))
                {
                    var saId = sq.AgentName.ToLowerInvariant().Replace(" ", "-");
                    _conversationItems.Add(new ConversationItem
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
                GetOrAddMember(sa.AgentName);
                UpdateHasWorkingAgents();
                _conversationItems.Add(new ConversationItem
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
                EnsureDetailCollections(saId);
                _session!.DetailEvents[saId].Add(new ConversationItem
                {
                    Type = ConversationItemType.SubAgentThinking,
                    Sender = saId,
                    SenderDisplayName = sat.AgentName,
                    StepId = sat.StepId,
                    Content = sat.Text,
                    Timestamp = DateTimeOffset.UtcNow,
                });
                break;
            }

            case AgentEvent.SubAgentToolCall satc:
            {
                var saId = satc.AgentName.ToLowerInvariant().Replace(" ", "-");
                EnsureDetailCollections(saId);
                _session!.DetailLogEntries[saId].Add(new LogEntryModel
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
                EnsureDetailCollections(saId);
                var logEntry = _session!.DetailLogEntries[saId].LastOrDefault(e => e.StepId == satr.StepId)
                    ?? _session.DetailLogEntries[saId].LastOrDefault();
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
                SetMemberStatus(saId, MemberStatus.Idle);
                UpdateHasWorkingAgents();

                if (!string.IsNullOrWhiteSpace(sad.Report))
                {
                    var reportItem = new ConversationItem
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
                    _conversationItems.Add(reportItem);
                    RequestSummary(reportItem, oneLine: true);
                }
                break;
            }

            case AgentEvent.SubAgentFailed saf:
            {
                var saId = saf.AgentName.ToLowerInvariant().Replace(" ", "-");
                SetMemberStatus(saId, MemberStatus.Idle);
                UpdateHasWorkingAgents();
                break;
            }
        }
    }

    private void OnCancel() => _cts?.Cancel();

    private void EnsureDetailCollections(string saId)
    {
        if (_session is null) return;
        if (!_session.DetailEvents.ContainsKey(saId))
            _session.DetailEvents[saId] = new List<ConversationItem>();
        if (!_session.DetailLogEntries.ContainsKey(saId))
            _session.DetailLogEntries[saId] = new List<LogEntryModel>();
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
            catch (JSDisconnectedException) { }
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
        catch (JSDisconnectedException) { }
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

    private void RequestHeadline(ConversationItem item)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                item.Summary = await Summarizer.SummarizeToHeadlineAsync(item.Content, CancellationToken.None);
                await InvokeAsync(StateHasChanged);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to generate headline for {Type} item", item.Type);
            }
        });
    }

    private void RequestSummary(ConversationItem item, bool oneLine)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                item.Summary = oneLine
                    ? await Summarizer.SummarizeToOneLineAsync(item.Content, CancellationToken.None)
                    : await Summarizer.SummarizeToFewLinesAsync(item.Content, CancellationToken.None);
                await InvokeAsync(StateHasChanged);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to summarize {Type} item", item.Type);
            }
        });
    }

    private static string Truncate(string text, int maxLength = 100)
    {
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + "...";
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        if (_isOwner && _session is not null)
        {
            await WorkspaceMgr.SaveSessionAsync(_session);
            Store.Release(_session.Id, _circuitId);
        }
    }
}
