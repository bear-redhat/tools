using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Services;
using Microsoft.Extensions.Options;
using Visus.Cuid;

namespace Investigator.Tools;

public sealed class CasebookTool : IInvestigatorTool, ISystemPromptContributor
{
    private static readonly JsonElement s_paramSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "action": {
                "type": "string",
                "enum": ["save", "search", "read", "list", "delete", "index"],
                "description": "save: jot an observation in the notebook. search: consult the casebook for relevant entries. read: study a specific entry in full. list: review what is filed. delete: remove a notebook entry. index: file notebook entries into the commonplace book."
            },
            "tier": {
                "type": "string",
                "enum": ["notebook", "commonplace", "all"],
                "description": "Which volume to consult. Defaults to 'all' for search/list, 'notebook' for save/delete. Optional."
            },
            "content": { "type": "string", "description": "The observation to save (for save action)" },
            "title": { "type": "string", "description": "Short descriptive title (for save action)" },
            "category": { "type": "string", "description": "Category: pattern, workaround, environment, debugging-tip, infrastructure, general (for save action)" },
            "tags": { "type": "array", "items": { "type": "string" }, "description": "Tags (for save action)" },
            "query": { "type": "string", "description": "Search query (for search action)" },
            "id": { "type": "string", "description": "Entry ID or name (for read/delete actions)" },
            "count": { "type": "integer", "description": "Number of entries to return (for list action, default: 20)" }
        },
        "required": ["action"]
    }
    """).RootElement.Clone();

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IEmbeddingClient _embedder;
    private readonly CasebookOptions _options;
    private readonly WorkspaceOptions _workspaceOptions;
    private readonly Services.CasebookIndexer _indexer;
    private readonly ILogger<CasebookTool> _logger;

    private string _notebookDir = "";
    private readonly List<NotebookEntry> _notebookEntries = [];
    private readonly List<CommonplaceEntry> _commonplaceEntries = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _notebookIndexed;
    private bool _commonplaceIndexed;

    public CasebookTool(
        IEmbeddingClient embedder,
        IOptions<CasebookOptions> casebookOptions,
        IOptions<WorkspaceOptions> workspaceOptions,
        Services.CasebookIndexer indexer,
        ILogger<CasebookTool> logger)
    {
        _embedder = embedder;
        _options = casebookOptions.Value;
        _workspaceOptions = workspaceOptions.Value;
        _indexer = indexer;
        _logger = logger;
    }

    public ToolDefinition Definition => new(
        Name: "casebook",
        Description: "The casebook at 221B Banyan Row -- the notebook for case observations and the commonplace book "
            + "for curated operational knowledge. Use 'search' to consult both, 'save' to jot a note, "
            + "'read' to study an entry in full, 'list' to review what is filed away, "
            + "or 'index' to file notebook entries into the commonplace book.",
        ParameterSchema: s_paramSchema,
        DefaultTimeout: TimeSpan.FromSeconds(60));

    public async Task RegisterAsync(CancellationToken ct = default)
    {
        _notebookDir = ResolveNotebookDir();
        Directory.CreateDirectory(_notebookDir);
        _indexer.SetCasebook(this);
        _logger.LogInformation("Casebook notebook directory: {Dir}", _notebookDir);
        await EnsureNotebookIndexed(ct);
        await EnsureCommonplaceIndexed(ct);
    }

    public async Task<ToolResult> InvokeAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        await EnsureNotebookIndexed(ct);
        await EnsureCommonplaceIndexed(ct);

        var action = parameters.GetProperty("action").GetString();
        if (string.IsNullOrEmpty(action))
            return new ToolResult("'action' is required.", ExitCode: 1);

        return action switch
        {
            "save" => await Save(parameters, context, ct),
            "search" => await Search(parameters, ct),
            "read" => Read(parameters),
            "list" => List(parameters),
            "delete" => await Delete(parameters, ct),
            "index" => await Index(ct),
            _ => new ToolResult($"Unknown action: {action}. Use save, search, read, list, delete, or index.", ExitCode: 1),
        };
    }

    public string? GetSystemPromptSection() =>
        """
        THE CASEBOOK:
        You keep two volumes on the shelf at 221B Banyan Row, and they persist across cases.

        The notebook holds your case observations -- facts, environmental details, version numbers, capacity limits, behavioural quirks noted during investigations. These are your field jottings: raw, specific, timestamped. When a Scout reports back and you notice something worth preserving -- an infrastructure detail, a debugging shortcut, a configuration peculiarity -- jot it down at once while the detail is fresh.

        The commonplace book holds curated operational knowledge -- distilled from prior notebook entries or seeded as reference material. Debugging procedures, provisioning patterns, platform-specific playbooks. Where the notebook is hasty, the commonplace book is considered.

        Use the casebook tool to:
        - SEARCH at the start of each investigation. Both volumes are consulted by default. You may have encountered this problem before, or there may be a commonplace entry that bears directly on the matter at hand.
        - SAVE observations to the notebook as you learn them during the investigation. Do not wait until the end -- the casebook is a notebook, not a filing cabinet.
        - READ an entry in full when a search result looks promising.

        Good notebook entries are distilled facts a future investigation would benefit from: "build01's ingress controller is version 2.4 and does not support gRPC backends", "Hive SyncSets on hive-prod apply with a 5-minute backoff after conflict". Bad entries are raw command output, trivial observations, or investigation conclusions (which belong in conclude).

        Categories: pattern, workaround, environment, debugging-tip, infrastructure, general.
        """;

    #region Actions

    private async Task<ToolResult> Save(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var content = parameters.TryGetProperty("content", out var c) ? c.GetString() : null;
        var title = parameters.TryGetProperty("title", out var t) ? t.GetString() : null;

        if (string.IsNullOrWhiteSpace(content))
            return new ToolResult("'content' is required for save.", ExitCode: 1);
        if (string.IsNullOrWhiteSpace(title))
            return new ToolResult("'title' is required for save.", ExitCode: 1);

        var category = parameters.TryGetProperty("category", out var cat) ? cat.GetString() : null;
        var tags = Array.Empty<string>();
        if (parameters.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            tags = tagsEl.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToArray();

        if (_notebookEntries.Count >= _options.MaxEntries)
            return new ToolResult($"The notebook is full ({_options.MaxEntries} entries). Delete old entries or use 'index' to file them into the commonplace book.", ExitCode: 1);

        var id = new Cuid2(10).ToString();
        var fileNum = NextFileNumber();
        var slug = Slugify(title);
        var fileName = $"{fileNum:D3}-{slug}.md";
        var timestamp = DateTimeOffset.UtcNow;

        var mdContent = BuildMarkdown(title, category, tags, content);
        var indexEntry = new NotebookIndexEntry
        {
            Id = id,
            Timestamp = timestamp,
            Category = category,
            Tags = tags,
            Source = context.ConversationId,
            File = fileName,
            Title = title,
        };

        float[]? embedding = null;
        try
        {
            embedding = await _embedder.EmbedAsync($"{title} {string.Join(" ", tags)} {content}", ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to embed notebook entry {Id}, search will use keyword fallback", id);
        }

        await _writeLock.WaitAsync(ct);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_notebookDir, fileName), mdContent, ct);
            var jsonLine = JsonSerializer.Serialize(indexEntry, s_jsonOptions);
            await File.AppendAllTextAsync(IndexPath, jsonLine + "\n", ct);
            _notebookEntries.Add(new NotebookEntry(indexEntry, content, embedding));
        }
        finally
        {
            _writeLock.Release();
        }

        _logger.LogInformation("Noted: {Id}: {Title} ({Category})", id, title, category);
        return new ToolResult($"Noted (id: {id}, title: {title}).");
    }

    private async Task<ToolResult> Search(JsonElement parameters, CancellationToken ct)
    {
        var query = parameters.TryGetProperty("query", out var q) ? q.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
            return new ToolResult("'query' is required for search.", ExitCode: 1);

        var tier = parameters.TryGetProperty("tier", out var tierEl) ? tierEl.GetString() : "all";

        var searchNotebook = tier is "all" or "notebook";
        var searchCommonplace = tier is "all" or "commonplace";

        var notebookResults = new List<(NotebookEntry Entry, double Score)>();
        var commonplaceResults = new List<(CommonplaceEntry Entry, double Score)>();

        float[]? queryEmbedding = null;
        try
        {
            queryEmbedding = await _embedder.EmbedAsync(query, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Embedding query failed, falling back to keyword search");
        }

        if (searchNotebook && _notebookEntries.Count > 0)
            notebookResults = SearchNotebook(query, queryEmbedding);

        if (searchCommonplace && _commonplaceEntries.Count > 0)
            commonplaceResults = SearchCommonplace(query, queryEmbedding);

        var totalResults = notebookResults.Count + commonplaceResults.Count;
        if (totalResults == 0)
            return new ToolResult("Nothing filed on that subject.");

        var sb = new StringBuilder();
        sb.AppendLine($"Found {totalResults} relevant entries:\n");

        if (notebookResults.Count > 0)
        {
            sb.AppendLine("From the notebook:");
            foreach (var (entry, score) in notebookResults)
            {
                sb.AppendLine($"- **{entry.Index.Title}** (id: `{entry.Index.Id}`, score: {score:F2})");
                sb.AppendLine($"  Category: {entry.Index.Category} | Tags: {string.Join(", ", entry.Index.Tags)} | {entry.Index.Timestamp:yyyy-MM-dd}");
                var preview = entry.Body.Length > 200 ? entry.Body[..200] + "..." : entry.Body;
                sb.AppendLine($"  {preview}");
                sb.AppendLine();
            }
        }

        if (commonplaceResults.Count > 0)
        {
            sb.AppendLine("From the commonplace book:");
            foreach (var (entry, score) in commonplaceResults)
            {
                sb.AppendLine($"- **{entry.Title}** (name: `{entry.Name}`, score: {score:F2})");
                sb.AppendLine($"  Tags: {string.Join(", ", entry.Tags)}");
                sb.AppendLine();
            }
        }

        sb.AppendLine("Use casebook(action: 'read', id: '...') to consult the full entry.");

        return new ToolResult(sb.ToString());
    }

    private ToolResult Read(JsonElement parameters)
    {
        var id = parameters.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
            return new ToolResult("'id' is required for read.", ExitCode: 1);

        var notebookEntry = _notebookEntries.FirstOrDefault(e => e.Index.Id == id);
        if (notebookEntry is not null)
        {
            var filePath = Path.Combine(_notebookDir, notebookEntry.Index.File);
            if (!File.Exists(filePath))
                return new ToolResult($"Notebook file missing: {notebookEntry.Index.File}", ExitCode: 1);

            var content = File.ReadAllText(filePath);
            return new ToolResult($"**{notebookEntry.Index.Title}** (notebook, id: {notebookEntry.Index.Id})\n\n{content}");
        }

        var commonplaceEntry = _commonplaceEntries.FirstOrDefault(e =>
            e.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (commonplaceEntry is not null)
        {
            _logger.LogInformation("Casebook: reading commonplace entry '{Name}'", commonplaceEntry.Name);
            return new ToolResult(commonplaceEntry.FullContent);
        }

        return new ToolResult($"No entry with id or name '{id}'. Use casebook(action: 'list') to see what is filed.", ExitCode: 1);
    }

    private ToolResult List(JsonElement parameters)
    {
        var count = parameters.TryGetProperty("count", out var cEl) && cEl.TryGetInt32(out var c) ? c : _options.ListDefault;
        var tier = parameters.TryGetProperty("tier", out var tierEl) ? tierEl.GetString() : "all";
        var categoryFilter = parameters.TryGetProperty("category", out var catEl) ? catEl.GetString() : null;

        var sb = new StringBuilder();
        var any = false;

        if (tier is "all" or "notebook")
        {
            var filtered = _notebookEntries.AsEnumerable();
            if (!string.IsNullOrEmpty(categoryFilter))
                filtered = filtered.Where(e => string.Equals(e.Index.Category, categoryFilter, StringComparison.OrdinalIgnoreCase));

            var items = filtered.OrderByDescending(e => e.Index.Timestamp).Take(count).ToList();
            if (items.Count > 0)
            {
                any = true;
                sb.AppendLine($"The notebook ({items.Count} of {_notebookEntries.Count}):\n");
                foreach (var entry in items)
                {
                    sb.AppendLine($"- **{entry.Index.Title}** (id: `{entry.Index.Id}`)");
                    sb.AppendLine($"  Category: {entry.Index.Category} | Tags: {string.Join(", ", entry.Index.Tags)} | {entry.Index.Timestamp:yyyy-MM-dd}");
                }
                sb.AppendLine();
            }
        }

        if (tier is "all" or "commonplace")
        {
            if (_commonplaceEntries.Count > 0)
            {
                any = true;
                sb.AppendLine($"The commonplace book ({_commonplaceEntries.Count} entries):\n");
                foreach (var entry in _commonplaceEntries)
                {
                    sb.AppendLine($"- **{entry.Title}** (name: `{entry.Name}`)");
                    sb.AppendLine($"  Tags: {string.Join(", ", entry.Tags)}");
                }
            }
        }

        if (!any)
            return new ToolResult("Nothing filed yet.");

        return new ToolResult(sb.ToString());
    }

    private async Task<ToolResult> Delete(JsonElement parameters, CancellationToken ct)
    {
        var id = parameters.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
            return new ToolResult("'id' is required for delete.", ExitCode: 1);

        var entry = _notebookEntries.FirstOrDefault(e => e.Index.Id == id);
        if (entry is null)
            return new ToolResult($"No notebook entry with id '{id}'.", ExitCode: 1);

        await _writeLock.WaitAsync(ct);
        try
        {
            var filePath = Path.Combine(_notebookDir, entry.Index.File);
            if (File.Exists(filePath))
                File.Delete(filePath);

            _notebookEntries.Remove(entry);
            await RewriteIndex(ct);
        }
        finally
        {
            _writeLock.Release();
        }

        _logger.LogInformation("Deleted notebook entry {Id}: {Title}", id, entry.Index.Title);
        return new ToolResult($"Deleted (id: {id}, title: {entry.Index.Title}).");
    }

    private async Task<ToolResult> Index(CancellationToken ct)
    {
        var result = await _indexer.RunAsync(ct);
        return new ToolResult(result);
    }

    #endregion

    #region Internal methods for CasebookIndexer

    internal IReadOnlyList<NotebookEntry> GetAllNotebookEntries() => _notebookEntries.AsReadOnly();

    internal async Task DeleteNotebookEntries(IEnumerable<string> ids, CancellationToken ct)
    {
        var idSet = ids.ToHashSet();
        await _writeLock.WaitAsync(ct);
        try
        {
            var toRemove = _notebookEntries.Where(e => idSet.Contains(e.Index.Id)).ToList();
            foreach (var entry in toRemove)
            {
                var filePath = Path.Combine(_notebookDir, entry.Index.File);
                if (File.Exists(filePath))
                    File.Delete(filePath);
                _notebookEntries.Remove(entry);
            }
            await RewriteIndex(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Invalidates the commonplace book index so it is reloaded on next access.
    /// Called by CasebookIndexer after writing new commonplace entries.
    /// </summary>
    internal void InvalidateCommonplace()
    {
        _commonplaceIndexed = false;
        _commonplaceEntries.Clear();
        _logger.LogInformation("Commonplace book invalidated, will re-scan on next access");
    }

    #endregion

    #region Notebook index management

    private string IndexPath => Path.Combine(_notebookDir, "index.jsonl");

    private async Task EnsureNotebookIndexed(CancellationToken ct)
    {
        if (_notebookIndexed) return;

        if (!Directory.Exists(_notebookDir))
        {
            _notebookIndexed = true;
            return;
        }

        var indexFile = IndexPath;
        if (!File.Exists(indexFile))
        {
            _notebookIndexed = true;
            return;
        }

        _notebookEntries.Clear();
        foreach (var line in await File.ReadAllLinesAsync(indexFile, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var indexEntry = JsonSerializer.Deserialize<NotebookIndexEntry>(line, s_jsonOptions);
                if (indexEntry is null) continue;

                var filePath = Path.Combine(_notebookDir, indexEntry.File);
                if (!File.Exists(filePath))
                {
                    _logger.LogWarning("Notebook file missing, skipping: {File}", indexEntry.File);
                    continue;
                }

                var rawContent = await File.ReadAllTextAsync(filePath, ct);
                var body = ParseBody(rawContent);

                float[]? embedding = null;
                try
                {
                    embedding = await _embedder.EmbedAsync($"{indexEntry.Title} {string.Join(" ", indexEntry.Tags)} {body}", ct);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Failed to embed notebook entry {Id}, keyword fallback will be used", indexEntry.Id);
                }

                _notebookEntries.Add(new NotebookEntry(indexEntry, body, embedding));
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse notebook index line: {Line}", line);
            }
        }

        _logger.LogInformation("Notebook indexed: {Count} entries from {Dir}", _notebookEntries.Count, _notebookDir);
        _notebookIndexed = true;
    }

    private async Task RewriteIndex(CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var entry in _notebookEntries)
            sb.AppendLine(JsonSerializer.Serialize(entry.Index, s_jsonOptions));

        var tmp = IndexPath + ".tmp";
        await File.WriteAllTextAsync(tmp, sb.ToString(), ct);
        File.Move(tmp, IndexPath, overwrite: true);
    }

    private int NextFileNumber()
    {
        var max = 0;
        if (Directory.Exists(_notebookDir))
        {
            foreach (var file in Directory.GetFiles(_notebookDir, "*.md"))
            {
                var name = Path.GetFileName(file);
                var dashIdx = name.IndexOf('-');
                if (dashIdx > 0 && int.TryParse(name[..dashIdx], out var num) && num > max)
                    max = num;
            }
        }
        return max + 1;
    }

    #endregion

    #region Commonplace book index management

    private async Task EnsureCommonplaceIndexed(CancellationToken ct)
    {
        if (_commonplaceIndexed) return;

        _commonplaceEntries.Clear();

        await IndexCommonplaceDirectory(_options.SeedPath, ct);

        var commonplacePath = _options.CommonplacePath;
        if (!string.IsNullOrEmpty(commonplacePath) && Directory.Exists(commonplacePath))
            await IndexCommonplaceDirectory(commonplacePath, ct);

        _logger.LogInformation("Commonplace book indexed: {Count} entries (seed: {Seed}, learned: {Learned})",
            _commonplaceEntries.Count, _options.SeedPath, commonplacePath ?? "(none)");
        _commonplaceIndexed = true;
    }

    private async Task IndexCommonplaceDirectory(string dir, CancellationToken ct)
    {
        if (!Directory.Exists(dir))
        {
            _logger.LogWarning("Commonplace directory not found: {Dir}", dir);
            return;
        }

        foreach (var file in Directory.GetFiles(dir, "*.md"))
        {
            try
            {
                var content = await File.ReadAllTextAsync(file, ct);
                var (title, tags, body) = ParseFrontmatter(content);
                var name = Path.GetFileNameWithoutExtension(file);

                if (_commonplaceEntries.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                float[]? embedding = null;
                try
                {
                    embedding = await _embedder.EmbedAsync($"{title} {string.Join(" ", tags)} {body}", ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to embed commonplace entry {Name}, falling back to keyword search", name);
                }

                _commonplaceEntries.Add(new CommonplaceEntry(name, title, tags, body, content, embedding));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load commonplace entry from {File}", file);
            }
        }
    }

    #endregion

    #region Search helpers

    private List<(NotebookEntry Entry, double Score)> SearchNotebook(string query, float[]? queryEmbedding)
    {
        if (queryEmbedding is not null && _notebookEntries.Any(e => e.Embedding is not null))
        {
            return _notebookEntries
                .Where(e => e.Embedding is not null)
                .Select(e => (e, Score: CosineSimilarity(queryEmbedding, e.Embedding!)))
                .OrderByDescending(x => x.Score)
                .Take(_options.SearchResults)
                .ToList();
        }

        var tokens = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _notebookEntries
            .Select(e =>
            {
                var searchable = $"{e.Index.Title} {string.Join(" ", e.Index.Tags)} {e.Body}".ToLowerInvariant();
                var score = tokens.Count(t => searchable.Contains(t, StringComparison.OrdinalIgnoreCase));
                return (e, Score: (double)score / tokens.Length);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(_options.SearchResults)
            .ToList();
    }

    private List<(CommonplaceEntry Entry, double Score)> SearchCommonplace(string query, float[]? queryEmbedding)
    {
        if (queryEmbedding is not null && _commonplaceEntries.Any(e => e.Embedding is not null))
        {
            return _commonplaceEntries
                .Where(e => e.Embedding is not null)
                .Select(e => (e, Score: CosineSimilarity(queryEmbedding, e.Embedding!)))
                .OrderByDescending(x => x.Score)
                .Take(3)
                .ToList();
        }

        var tokens = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _commonplaceEntries
            .Select(s =>
            {
                var searchable = $"{s.Title} {string.Join(" ", s.Tags)} {s.Body}".ToLowerInvariant();
                var score = tokens.Count(t => searchable.Contains(t, StringComparison.OrdinalIgnoreCase));
                return (s, Score: (double)score / tokens.Length);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(3)
            .ToList();
    }

    #endregion

    #region Helpers

    private string ResolveNotebookDir()
    {
        if (!string.IsNullOrEmpty(_options.Path))
            return _options.Path;

        var root = _workspaceOptions.RootPath;
        if (!string.IsNullOrEmpty(root))
            return Path.Combine(Path.GetDirectoryName(root) ?? root, "casebook");

        return Path.Combine(Path.GetTempPath(), "investigator", "casebook");
    }

    private static string BuildMarkdown(string title, string? category, string[] tags, string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"title: {title}");
        if (category is not null)
            sb.AppendLine($"category: {category}");
        sb.AppendLine($"tags: [{string.Join(", ", tags)}]");
        sb.AppendLine("---");
        sb.AppendLine(content);
        return sb.ToString();
    }

    private static string ParseBody(string rawContent)
    {
        if (!rawContent.StartsWith("---")) return rawContent;
        var endIdx = rawContent.IndexOf("---", 3, StringComparison.Ordinal);
        return endIdx > 0 ? rawContent[(endIdx + 3)..].TrimStart() : rawContent;
    }

    private static (string Title, string[] Tags, string Body) ParseFrontmatter(string content)
    {
        string? title = null;
        var tags = Array.Empty<string>();
        var body = content;

        if (content.StartsWith("---"))
        {
            var endIdx = content.IndexOf("---", 3, StringComparison.Ordinal);
            if (endIdx > 0)
            {
                var frontmatter = content[3..endIdx];
                body = content[(endIdx + 3)..].TrimStart();

                foreach (var line in frontmatter.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("title:"))
                        title = trimmed["title:".Length..].Trim().Trim('"', '\'');
                    else if (trimmed.StartsWith("tags:"))
                    {
                        var tagStr = trimmed["tags:".Length..].Trim().Trim('[', ']');
                        tags = tagStr.Split(',').Select(t => t.Trim().Trim('"', '\'')).Where(t => t.Length > 0).ToArray();
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(title))
        {
            var firstLine = body.Split('\n').FirstOrDefault(l => l.StartsWith('#'))?.TrimStart('#', ' ');
            title = firstLine ?? "Untitled";
        }

        return (title, tags, body);
    }

    private static string Slugify(string text)
    {
        var slug = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) slug.Append(ch);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }
        var result = slug.ToString().Trim('-');
        return result.Length > 60 ? result[..60].TrimEnd('-') : result;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        var dot = 0.0;
        var normA = 0.0;
        var normB = 0.0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom > 0 ? dot / denom : 0;
    }

    #endregion

    #region Models

    internal sealed class NotebookIndexEntry
    {
        public string? Id { get; set; }
        public DateTimeOffset Timestamp { get; set; }
        public string? Category { get; set; }
        public string[] Tags { get; set; } = [];
        public string? Source { get; set; }
        public string? File { get; set; }
        public string? Title { get; set; }
    }

    internal sealed record NotebookEntry(NotebookIndexEntry Index, string Body, float[]? Embedding);

    internal sealed record CommonplaceEntry(string Name, string Title, string[] Tags, string Body, string FullContent, float[]? Embedding);

    #endregion
}
