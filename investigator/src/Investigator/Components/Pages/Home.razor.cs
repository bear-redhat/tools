using System.Net;
using System.Text;
using Investigator.Services;
using Microsoft.AspNetCore.Components;

namespace Investigator.Components.Pages;

public partial class Home
{
    private const string s_homeArt =
        """
        ╭─────── 221B BANYAN ROW ────────╮
        │                                │
        │        ╭──────╮                │
        │        │  🐻  │  Awaiting      │
        │        │  🪑  │  a new case.   │
        │        ╰──────╯                │
        │                                │
        │           ☕  🔎  🐾           │
        ╰────────────────────────────────╯
        """;

    [Inject] private ConversationStore Store { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private AuthSettings AuthSettings { get; set; } = default!;
    [Inject] private CircuitAuthState CircuitAuth { get; set; } = default!;
    [Inject] private BrowserTimeZone BrowserTz { get; set; } = default!;

    private IReadOnlyList<SessionInfo> _investigations = [];

    protected override void OnInitialized()
    {
        var allInv = Store.GetAllSessionInfo();

        if (AuthSettings.IsEnabled && CircuitAuth.IsAuthenticated)
        {
            _investigations = allInv.Where(i =>
                string.Equals(i.OwnerUserId, CircuitAuth.UserId, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else if (AuthSettings.IsEnabled)
        {
            _investigations = [];
        }
        else
        {
            _investigations = allInv;
        }
    }

    private void StartNewInvestigation()
    {
        var session = Store.CreateSession();
        Nav.NavigateTo($"/c/{session.Id}");
    }

    private string FormatRelativeTime(DateTimeOffset started)
    {
        var elapsed = DateTimeOffset.UtcNow - started;
        if (elapsed.TotalSeconds < 60) return "just now";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
        return BrowserTz.ToLocal(started).ToString("yyyy-MM-dd HH:mm");
    }

    private static MarkupString FormatAsciiArt(string content)
    {
        var sb = new StringBuilder();
        foreach (var rune in content.EnumerateRunes())
        {
            var ch = WebUtility.HtmlEncode(rune.ToString());
            if (IsEmoji(rune))
                sb.Append("<span class=\"emoji-cell\">").Append(ch).Append("</span>");
            else
                sb.Append(ch);
        }
        return new MarkupString(sb.ToString());
    }

    private static bool IsEmoji(System.Text.Rune rune)
    {
        int v = rune.Value;
        return (v >= 0x2600 && v <= 0x27BF)
            || (v >= 0x1F300 && v <= 0x1F9FF)
            || (v >= 0x1FA00 && v <= 0x1FAFF);
    }
}
