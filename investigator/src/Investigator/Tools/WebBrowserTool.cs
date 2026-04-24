using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace Investigator.Tools;

public sealed class WebBrowserTool : IInvestigatorTool, IAsyncDisposable
{
    private static readonly JsonElement s_paramSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "action": {
                "type": "string",
                "enum": ["navigate", "click", "type", "back", "get_text"],
                "description": "navigate: open a URL. click: click element by number. type: type text into an element. back: go to previous page. get_text: re-read current page text."
            },
            "url": {
                "type": "string",
                "description": "URL to navigate to (required for 'navigate' action)."
            },
            "element": {
                "type": "integer",
                "description": "Element number from the interactive elements list (required for 'click' and 'type' actions)."
            },
            "text": {
                "type": "string",
                "description": "Text to type into the element (required for 'type' action)."
            },
            "offset": {
                "type": "integer",
                "description": "Character offset to start reading from (optional for 'get_text' action, default 0)."
            }
        },
        "required": ["action"]
    }
    """).RootElement.Clone();

    private readonly WebBrowserOptions _options;
    private readonly ILogger<WebBrowserTool> _logger;
    private readonly ConcurrentDictionary<string, BrowseSession> _sessions = new();
    private readonly Timer _cleanupTimer;

    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;

    public WebBrowserTool(IOptions<WebBrowserOptions> options, ILogger<WebBrowserTool> logger)
    {
        _options = options.Value;
        _logger = logger;

        _playwright = Playwright.CreateAsync().GetAwaiter().GetResult();
        _browser = _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _options.Headless,
            Args = ["--no-sandbox", "--disable-setuid-sandbox"]
        }).GetAwaiter().GetResult();

        logger.LogInformation("web_browse: Chromium {Version} ready", _browser.Version);
        _cleanupTimer = new Timer(CleanupIdleSessions, null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public ToolDefinition Definition => new(
        Name: "web_browse",
        Description: "Browse the web using a real browser. Actions: "
            + "navigate (open a URL), click (click element by number), type (type into an input), "
            + "back (go to previous page), get_text (re-read current page). "
            + "After each action, returns the page text and a numbered list of interactive elements you can click or type into.",
        ParameterSchema: s_paramSchema,
        DefaultTimeout: TimeSpan.FromSeconds(60));

    public async Task<ToolResult> InvokeAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var action = parameters.GetProperty("action").GetString() ?? "";
        var sessionKey = $"{context.WorkspacePath}::{context.CallerId}";

        _logger.LogInformation("web_browse: action={Action}, session={Session}", action, sessionKey);

        try
        {
            return action switch
            {
                "navigate" => await HandleNavigate(sessionKey, parameters, context, ct),
                "click" => await HandleClick(sessionKey, parameters, context, ct),
                "type" => await HandleType(sessionKey, parameters, context, ct),
                "back" => await HandleBack(sessionKey, context, ct),
                "get_text" => await HandleGetText(sessionKey, parameters, context),
                _ => new ToolResult($"Unknown action: {action}. Use navigate, click, type, back, or get_text.", ExitCode: 1),
            };
        }
        catch (PlaywrightException ex)
        {
            _logger.LogError(ex, "web_browse: Playwright error during {Action}", action);
            EvictSession(sessionKey);
            return new ToolResult($"Browser error: {ex.Message}", ExitCode: 1);
        }
    }

    private async Task<ToolResult> HandleNavigate(string sessionKey, JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("url", out var urlElem) || string.IsNullOrWhiteSpace(urlElem.GetString()))
            return new ToolResult("Error: 'url' parameter is required for navigate action.", ExitCode: 1);

        var url = urlElem.GetString()!;
        var session = await GetOrCreateSession(sessionKey, ct);

        await session.Lock.WaitAsync(ct);
        try
        {
            await session.Page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30_000
            });
            session.TouchLastUsed();
            return await ExtractPageContent(session, context);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    private async Task<ToolResult> HandleClick(string sessionKey, JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("element", out var elemNum))
            return new ToolResult("Error: 'element' parameter is required for click action.", ExitCode: 1);

        var index = elemNum.GetInt32();
        var session = GetExistingSession(sessionKey);
        if (session is null)
            return new ToolResult("Error: no active browser session. Use navigate first.", ExitCode: 1);

        await session.Lock.WaitAsync(ct);
        try
        {
            if (index < 1 || index > session.Elements.Count)
                return new ToolResult($"Element [{index}] not found. Valid range: 1-{session.Elements.Count}. Use get_text to see current elements.", ExitCode: 1);

            var element = session.Elements[index - 1];
            await element.Handle.ClickAsync(new LocatorClickOptions { Timeout = 10_000 });

            try { await session.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 10_000 }); }
            catch (TimeoutException) { /* page may not navigate */ }

            session.TouchLastUsed();
            return await ExtractPageContent(session, context);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    private async Task<ToolResult> HandleType(string sessionKey, JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("element", out var elemNum))
            return new ToolResult("Error: 'element' parameter is required for type action.", ExitCode: 1);
        if (!parameters.TryGetProperty("text", out var textElem) || string.IsNullOrEmpty(textElem.GetString()))
            return new ToolResult("Error: 'text' parameter is required for type action.", ExitCode: 1);

        var index = elemNum.GetInt32();
        var text = textElem.GetString()!;
        var session = GetExistingSession(sessionKey);
        if (session is null)
            return new ToolResult("Error: no active browser session. Use navigate first.", ExitCode: 1);

        await session.Lock.WaitAsync(ct);
        try
        {
            if (index < 1 || index > session.Elements.Count)
                return new ToolResult($"Element [{index}] not found. Valid range: 1-{session.Elements.Count}.", ExitCode: 1);

            var element = session.Elements[index - 1];
            await element.Handle.FillAsync(text, new LocatorFillOptions { Timeout = 10_000 });

            session.TouchLastUsed();
            return await ExtractPageContent(session, context);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    private async Task<ToolResult> HandleBack(string sessionKey, ToolContext context, CancellationToken ct)
    {
        var session = GetExistingSession(sessionKey);
        if (session is null)
            return new ToolResult("Error: no active browser session. Use navigate first.", ExitCode: 1);

        await session.Lock.WaitAsync(ct);
        try
        {
            await session.Page.GoBackAsync(new PageGoBackOptions { Timeout = 30_000 });

            try { await session.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 10_000 }); }
            catch (TimeoutException) { /* best effort */ }

            session.TouchLastUsed();
            return await ExtractPageContent(session, context);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    private async Task<ToolResult> HandleGetText(string sessionKey, JsonElement parameters, ToolContext context)
    {
        var session = GetExistingSession(sessionKey);
        if (session is null)
            return new ToolResult("Error: no active browser session. Use navigate first.", ExitCode: 1);

        var offset = parameters.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;

        await session.Lock.WaitAsync();
        try
        {
            session.TouchLastUsed();
            return await ExtractPageContent(session, context, offset);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    private async Task<ToolResult> ExtractPageContent(BrowseSession session, ToolContext context, int offset = 0)
    {
        var title = await session.Page.TitleAsync();
        var url = session.Page.Url;
        var text = await session.Page.InnerTextAsync("body");

        var sb = new StringBuilder();
        sb.AppendLine($"=== Page: {title} ({url}) ===");
        sb.AppendLine();

        if (offset > 0 && offset < text.Length)
            text = text[offset..];
        else if (offset >= text.Length)
            text = "(offset beyond end of page content)";

        var maxChars = _options.MaxContentChars;
        var truncated = text.Length > maxChars;
        if (truncated)
            text = text[..maxChars];

        sb.AppendLine(text);

        if (truncated)
            sb.AppendLine($"\n[Content truncated at {maxChars} chars. Use get_text with offset={offset + maxChars} to read more.]");

        var elements = await ExtractInteractiveElements(session);
        session.Elements = elements;

        if (elements.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--- Interactive elements ---");
            var limit = Math.Min(elements.Count, _options.MaxElements);
            for (var i = 0; i < limit; i++)
            {
                var e = elements[i];
                sb.AppendLine($"[{i + 1}] {e.Description}");
            }
            if (elements.Count > limit)
                sb.AppendLine($"... and {elements.Count - limit} more elements");
        }

        return new ToolResult(sb.ToString());
    }

    private async Task<List<PageElement>> ExtractInteractiveElements(BrowseSession session)
    {
        var elements = new List<PageElement>();
        var locators = session.Page.Locator("a, button, input, textarea, select");
        var count = await locators.CountAsync();

        for (var i = 0; i < count && elements.Count < _options.MaxElements + 20; i++)
        {
            try
            {
                var loc = locators.Nth(i);
                if (!await loc.IsVisibleAsync())
                    continue;

                var tag = (await loc.EvaluateAsync<string>("e => e.tagName")).ToLowerInvariant();
                var description = tag switch
                {
                    "a" => await DescribeLink(loc),
                    "button" => await DescribeButton(loc),
                    "input" => await DescribeInput(loc),
                    "textarea" => await DescribeTextarea(loc),
                    "select" => await DescribeSelect(loc),
                    _ => $"{tag}"
                };

                elements.Add(new PageElement(loc, description));
            }
            catch
            {
                // Element may have gone stale; skip it
            }
        }

        return elements;
    }

    private static async Task<string> DescribeLink(ILocator loc)
    {
        var text = (await loc.InnerTextAsync()).Trim();
        var href = await loc.GetAttributeAsync("href") ?? "";
        if (text.Length > 60) text = text[..57] + "...";
        return $"link \"{text}\" -> {href}";
    }

    private static async Task<string> DescribeButton(ILocator loc)
    {
        var text = (await loc.InnerTextAsync()).Trim();
        if (string.IsNullOrEmpty(text))
            text = await loc.GetAttributeAsync("aria-label") ?? await loc.GetAttributeAsync("title") ?? "(unlabelled)";
        if (text.Length > 60) text = text[..57] + "...";
        return $"button \"{text}\"";
    }

    private static async Task<string> DescribeInput(ILocator loc)
    {
        var type = await loc.GetAttributeAsync("type") ?? "text";
        var placeholder = await loc.GetAttributeAsync("placeholder") ?? "";
        var name = await loc.GetAttributeAsync("name") ?? "";
        var label = !string.IsNullOrEmpty(placeholder) ? placeholder : name;
        return $"input[type={type}] \"{label}\"";
    }

    private static async Task<string> DescribeTextarea(ILocator loc)
    {
        var placeholder = await loc.GetAttributeAsync("placeholder") ?? "";
        var name = await loc.GetAttributeAsync("name") ?? "";
        var label = !string.IsNullOrEmpty(placeholder) ? placeholder : name;
        return $"textarea \"{label}\"";
    }

    private static async Task<string> DescribeSelect(ILocator loc)
    {
        var name = await loc.GetAttributeAsync("name") ?? "";
        var label = await loc.GetAttributeAsync("aria-label") ?? name;
        return $"select \"{label}\"";
    }

    private async Task<BrowseSession> GetOrCreateSession(string sessionKey, CancellationToken ct)
    {
        if (_sessions.TryGetValue(sessionKey, out var existing))
            return existing;

        var browserContext = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Investigator/1.0 (bot)"
        });
        var page = await browserContext.NewPageAsync();

        var session = new BrowseSession(browserContext, page);
        _sessions[sessionKey] = session;

        _logger.LogInformation("web_browse: created new session for {Key}", sessionKey);
        return session;
    }

    private BrowseSession? GetExistingSession(string sessionKey)
    {
        _sessions.TryGetValue(sessionKey, out var session);
        return session;
    }

    private void EvictSession(string sessionKey)
    {
        if (_sessions.TryRemove(sessionKey, out var session))
        {
            _logger.LogInformation("web_browse: evicting session {Key}", sessionKey);
            _ = DisposeSessionAsync(session);
        }
    }

    private void CleanupIdleSessions(object? state)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-_options.SessionIdleMinutes);

        foreach (var (key, session) in _sessions)
        {
            if (session.LastUsedUtc < cutoff)
            {
                _logger.LogInformation("web_browse: cleaning up idle session {Key} (idle since {LastUsed})", key, session.LastUsedUtc);
                EvictSession(key);
            }
        }
    }

    private static async Task DisposeSessionAsync(BrowseSession session)
    {
        try { await session.Page.CloseAsync(); } catch { /* best effort */ }
        try { await session.Context.CloseAsync(); } catch { /* best effort */ }
        session.Lock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _cleanupTimer.Dispose();

        foreach (var (key, session) in _sessions)
        {
            _sessions.TryRemove(key, out _);
            await DisposeSessionAsync(session);
        }

        await _browser.CloseAsync();
        _playwright.Dispose();
    }

    private sealed class BrowseSession(IBrowserContext context, IPage page)
    {
        public IBrowserContext Context { get; } = context;
        public IPage Page { get; } = page;
        public List<PageElement> Elements { get; set; } = [];
        public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;
        public SemaphoreSlim Lock { get; } = new(1, 1);

        public void TouchLastUsed() => LastUsedUtc = DateTime.UtcNow;
    }

    private sealed record PageElement(ILocator Handle, string Description);
}
