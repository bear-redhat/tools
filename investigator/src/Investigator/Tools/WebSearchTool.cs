using System.Text;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Microsoft.Extensions.Options;

namespace Investigator.Tools;

public sealed class WebSearchTool : IInvestigatorTool
{
    private static readonly JsonElement s_paramSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "query": {
                "type": "string",
                "description": "The search query to send to Google."
            }
        },
        "required": ["query"]
    }
    """).RootElement.Clone();

    private readonly HttpClient _httpClient;
    private readonly WebSearchOptions _options;

    public WebSearchTool(IHttpClientFactory httpClientFactory, IOptions<WebSearchOptions> options)
    {
        var opts = options.Value;
        if (string.IsNullOrEmpty(opts.GoogleApiKey) || string.IsNullOrEmpty(opts.GoogleSearchEngineId))
            throw new InvalidOperationException(
                "web_search: GoogleApiKey and GoogleSearchEngineId must be configured");

        _httpClient = httpClientFactory.CreateClient("WebSearch");
        _options = opts;
    }

    public ToolDefinition Definition => new(
        Name: "web_search",
        Description: "Search the web using Google. Returns a list of results with title, URL, and snippet. "
            + "Use this to find documentation, error messages, release notes, blog posts, or any publicly available information.",
        ParameterSchema: s_paramSchema,
        DefaultTimeout: TimeSpan.FromSeconds(15));

    public async Task<ToolResult> InvokeAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var query = parameters.GetProperty("query").GetString() ?? "";

        if (string.IsNullOrWhiteSpace(query))
            return new ToolResult("Error: 'query' parameter is required and was empty.", ExitCode: 1);

        var url = $"https://customsearch.googleapis.com/customsearch/v1"
            + $"?key={Uri.EscapeDataString(_options.GoogleApiKey!)}"
            + $"&cx={Uri.EscapeDataString(_options.GoogleSearchEngineId!)}"
            + $"&q={Uri.EscapeDataString(query)}";

        context.Logger.LogInformation("web_search: query=\"{Query}\"", query);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(url, ct);
        }
        catch (HttpRequestException ex)
        {
            context.Logger.LogError(ex, "web_search: HTTP request failed for query \"{Query}\"", query);
            return new ToolResult($"Search request failed: {ex.Message}", ExitCode: 1);
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            context.Logger.LogWarning("web_search: API returned {Status} for query \"{Query}\"", response.StatusCode, query);
            return new ToolResult($"Google API error ({response.StatusCode}): {body}", ExitCode: 1);
        }

        return FormatResults(body, query, context);
    }

    private static ToolResult FormatResults(string json, string query, ToolContext context)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var sb = new StringBuilder();
        sb.AppendLine($"Search results for: {query}");
        sb.AppendLine();

        if (!root.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
        {
            sb.AppendLine("No results found.");
            return new ToolResult(sb.ToString());
        }

        var index = 1;
        foreach (var item in items.EnumerateArray())
        {
            var title = item.GetProperty("title").GetString() ?? "(no title)";
            var link = item.GetProperty("link").GetString() ?? "";
            var snippet = item.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : "";

            sb.AppendLine($"[{index}] {title}");
            sb.AppendLine($"    {link}");
            if (!string.IsNullOrWhiteSpace(snippet))
                sb.AppendLine($"    {snippet.Replace("\n", " ").Trim()}");
            sb.AppendLine();
            index++;
        }

        context.Logger.LogInformation("web_search: returned {Count} results for \"{Query}\"", index - 1, query);
        return new ToolResult(sb.ToString());
    }
}
