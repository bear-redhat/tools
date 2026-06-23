using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Services;
using Microsoft.Extensions.Options;
using Visus.Cuid;

namespace Investigator.Tools;

public sealed class MemoryTool : IInvestigatorTool, ISystemPromptContributor
{
    private static readonly JsonElement s_paramSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "action": {
                "type": "string",
                "enum": ["save", "search", "read", "list", "delete", "dream"],
                "description": "save: store a new memory. search: find relevant memories by query. read: get full content of a memory by id. list: show recent memories. delete: remove a memory by id. dream: trigger memory consolidation into skills."
            },
            "content": { "type": "string", "description": "The memory content to save (for save action)" },
            "title": { "type": "string", "description": "Short descriptive title for the memory (for save action)" },
            "category": { "type": "string", "description": "Category: pattern, workaround, environment, debugging-tip, infrastructure, general (for save action, default: general)" },
            "tags": { "type": "array", "items": { "type": "string" }, "description": "Tags for the memory (for save action)" },
            "query": { "type": "string", "description": "Search query (for search action)" },
            "id": { "type": "string", "description": "Memory ID (for read/delete actions)" },
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
    private readonly MemoryOptions _options;
    private readonly WorkspaceOptions _workspaceOptions;
    private readonly MemoryConsolidator _consolidator;
    private readonly ILogger<MemoryTool> _logger;

    private string _memoryDir = "";
    private readonly List<MemoryEntry> _entries = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _indexed;

    public MemoryTool(
        IEmbeddingClient embedder,
        IOptions<MemoryOptions> memoryOptions,
        IOptions<WorkspaceOptions> workspaceOptions,
        MemoryConsolidator consolidator,
        ILogger<MemoryTool> logger)
    {
        _embedder = embedder;
        _options = memoryOptions.Value;
        _workspaceOptions = workspaceOptions.Value;
        _consolidator = consolidator;
        _logger = logger;
    }

    public ToolDefinition Definition => new(
        Name: "memory",
        Description: "Long-term memory that persists across investigations. "
            + "Use 'save' to store a learning, 'search' to find relevant memories, "
            + "'read' to get full content, 'list' to browse recent entries, "
            + "'delete' to remove an entry, or 'dream' to consolidate memories into skills.",
        ParameterSchema: s_paramSchema,
        DefaultTimeout: TimeSpan.FromSeconds(60));

    public async Task RegisterAsync(CancellationToken ct = default)
    {
        _memoryDir = ResolveMemoryDir();
        Directory.CreateDirectory(_memoryDir);
        _logger.LogInformation("Memory directory: {Dir}", _memoryDir);
        await EnsureIndexed(ct);
    }

    public async Task<ToolResult> InvokeAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        await EnsureIndexed(ct);

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
            "dream" => await Dream(ct),
            _ => new ToolResult($"Unknown action: {action}. Use save, search, read, list, delete, or dream.", ExitCode: 1),
        };
    }

    public string? GetSystemPromptSection() =>
        """
        MEMORY:
        You have a long-term memory that persists across investigations. Use the memory tool to:
        - SAVE facts as you learn them during the investigation -- do not wait until the end. When a Scout reports back and you notice something worth retaining (an infrastructure quirk, a version detail, an environment-specific behaviour, a debugging shortcut, a capacity limit, a configuration pattern), save it promptly. Memory is a notebook for facts, not a filing cabinet for conclusions -- the conclude tool handles that.
        - SEARCH your memory at the start of each investigation for relevant prior knowledge -- you may have seen this problem before.
        - Good memory entries are distilled facts a future investigation would benefit from: "build01's ingress controller is version 2.4 and does not support gRPC backends", "Hive SyncSets on hive-prod apply with a 5-minute backoff after conflict", "the image-registry operator on vsphere clusters restarts weekly due to a known storage driver bug". Bad entries are raw command output, trivial observations, or investigation conclusions (which belong in conclude).

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

        if (_entries.Count >= _options.MaxEntries)
            return new ToolResult($"Memory is full ({_options.MaxEntries} entries). Delete old entries or trigger 'dream' to consolidate.", ExitCode: 1);

        var id = new Cuid2(10).ToString();
        var fileNum = NextFileNumber();
        var slug = Slugify(title);
        var fileName = $"{fileNum:D3}-{slug}.md";
        var timestamp = DateTimeOffset.UtcNow;

        var mdContent = BuildMarkdown(title, category, tags, content);
        var indexEntry = new MemoryIndexEntry
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
            _logger.LogWarning(ex, "Failed to embed memory {Id}, search will use keyword fallback", id);
        }

        await _writeLock.WaitAsync(ct);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_memoryDir, fileName), mdContent, ct);
            var jsonLine = JsonSerializer.Serialize(indexEntry, s_jsonOptions);
            await File.AppendAllTextAsync(IndexPath, jsonLine + "\n", ct);
            _entries.Add(new MemoryEntry(indexEntry, content, embedding));
        }
        finally
        {
            _writeLock.Release();
        }

        _logger.LogInformation("Saved memory {Id}: {Title} ({Category})", id, title, category);
        return new ToolResult($"Memory saved (id: {id}, title: {title}).");
    }

    private async Task<ToolResult> Search(JsonElement parameters, CancellationToken ct)
    {
        var query = parameters.TryGetProperty("query", out var q) ? q.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
            return new ToolResult("'query' is required for search.", ExitCode: 1);

        if (_entries.Count == 0)
            return new ToolResult("Memory is empty.");

        var hasEmbeddings = _entries.Any(e => e.Embedding is not null);
        List<(MemoryEntry Entry, double Score)> results;

        if (hasEmbeddings)
        {
            try
            {
                var queryEmbedding = await _embedder.EmbedAsync(query, ct);
                results = _entries
                    .Where(e => e.Embedding is not null)
                    .Select(e => (e, Score: CosineSimilarity(queryEmbedding, e.Embedding!)))
                    .OrderByDescending(x => x.Score)
                    .Take(_options.SearchResults)
                    .ToList();
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Embedding search failed, falling back to keyword search");
                results = KeywordSearch(query);
            }
        }
        else
        {
            results = KeywordSearch(query);
        }

        if (results.Count == 0)
            return new ToolResult("No matching memories found.");

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} relevant memories:\n");
        foreach (var (entry, score) in results)
        {
            sb.AppendLine($"- **{entry.Index.Title}** (id: `{entry.Index.Id}`, score: {score:F2})");
            sb.AppendLine($"  Category: {entry.Index.Category} | Tags: {string.Join(", ", entry.Index.Tags)} | {entry.Index.Timestamp:yyyy-MM-dd}");
            var preview = entry.Body.Length > 200 ? entry.Body[..200] + "..." : entry.Body;
            sb.AppendLine($"  {preview}");
            sb.AppendLine();
        }
        sb.AppendLine("Use memory(action: 'read', id: '...') to read the full content.");

        return new ToolResult(sb.ToString());
    }

    private ToolResult Read(JsonElement parameters)
    {
        var id = parameters.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
            return new ToolResult("'id' is required for read.", ExitCode: 1);

        var entry = _entries.FirstOrDefault(e => e.Index.Id == id);
        if (entry is null)
            return new ToolResult($"No memory with id '{id}'.", ExitCode: 1);

        var filePath = Path.Combine(_memoryDir, entry.Index.File);
        if (!File.Exists(filePath))
            return new ToolResult($"Memory file missing: {entry.Index.File}", ExitCode: 1);

        var content = File.ReadAllText(filePath);
        return new ToolResult($"**{entry.Index.Title}** (id: {entry.Index.Id})\n\n{content}");
    }

    private ToolResult List(JsonElement parameters)
    {
        var count = parameters.TryGetProperty("count", out var cEl) && cEl.TryGetInt32(out var c) ? c : _options.ListDefault;
        var categoryFilter = parameters.TryGetProperty("category", out var catEl) ? catEl.GetString() : null;

        var filtered = _entries.AsEnumerable();
        if (!string.IsNullOrEmpty(categoryFilter))
            filtered = filtered.Where(e => string.Equals(e.Index.Category, categoryFilter, StringComparison.OrdinalIgnoreCase));

        var items = filtered.OrderByDescending(e => e.Index.Timestamp).Take(count).ToList();

        if (items.Count == 0)
            return new ToolResult(categoryFilter is not null
                ? $"No memories in category '{categoryFilter}'."
                : "Memory is empty.");

        var sb = new StringBuilder();
        sb.AppendLine($"Memories ({items.Count} of {_entries.Count} total):\n");
        foreach (var entry in items)
        {
            sb.AppendLine($"- **{entry.Index.Title}** (id: `{entry.Index.Id}`)");
            sb.AppendLine($"  Category: {entry.Index.Category} | Tags: {string.Join(", ", entry.Index.Tags)} | {entry.Index.Timestamp:yyyy-MM-dd}");
        }

        return new ToolResult(sb.ToString());
    }

    private async Task<ToolResult> Delete(JsonElement parameters, CancellationToken ct)
    {
        var id = parameters.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
            return new ToolResult("'id' is required for delete.", ExitCode: 1);

        var entry = _entries.FirstOrDefault(e => e.Index.Id == id);
        if (entry is null)
            return new ToolResult($"No memory with id '{id}'.", ExitCode: 1);

        await _writeLock.WaitAsync(ct);
        try
        {
            var filePath = Path.Combine(_memoryDir, entry.Index.File);
            if (File.Exists(filePath))
                File.Delete(filePath);

            _entries.Remove(entry);
            await RewriteIndex(ct);
        }
        finally
        {
            _writeLock.Release();
        }

        _logger.LogInformation("Deleted memory {Id}: {Title}", id, entry.Index.Title);
        return new ToolResult($"Memory deleted (id: {id}, title: {entry.Index.Title}).");
    }

    private async Task<ToolResult> Dream(CancellationToken ct)
    {
        var result = await _consolidator.RunAsync(ct);
        return new ToolResult(result);
    }

    #endregion

    #region Internal methods for MemoryConsolidator

    internal IReadOnlyList<MemoryEntry> GetAllEntries() => _entries.AsReadOnly();

    internal async Task DeleteEntries(IEnumerable<string> ids, CancellationToken ct)
    {
        var idSet = ids.ToHashSet();
        await _writeLock.WaitAsync(ct);
        try
        {
            var toRemove = _entries.Where(e => idSet.Contains(e.Index.Id)).ToList();
            foreach (var entry in toRemove)
            {
                var filePath = Path.Combine(_memoryDir, entry.Index.File);
                if (File.Exists(filePath))
                    File.Delete(filePath);
                _entries.Remove(entry);
            }
            await RewriteIndex(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    #endregion

    #region Index management

    private string IndexPath => Path.Combine(_memoryDir, "index.jsonl");

    private async Task EnsureIndexed(CancellationToken ct)
    {
        if (_indexed) return;

        if (!Directory.Exists(_memoryDir))
        {
            _indexed = true;
            return;
        }

        var indexFile = IndexPath;
        if (!File.Exists(indexFile))
        {
            _indexed = true;
            return;
        }

        _entries.Clear();
        foreach (var line in await File.ReadAllLinesAsync(indexFile, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var indexEntry = JsonSerializer.Deserialize<MemoryIndexEntry>(line, s_jsonOptions);
                if (indexEntry is null) continue;

                var filePath = Path.Combine(_memoryDir, indexEntry.File);
                if (!File.Exists(filePath))
                {
                    _logger.LogWarning("Memory file missing, skipping: {File}", indexEntry.File);
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
                    _logger.LogWarning(ex, "Failed to embed memory {Id}, keyword fallback will be used", indexEntry.Id);
                }

                _entries.Add(new MemoryEntry(indexEntry, body, embedding));
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse memory index line: {Line}", line);
            }
        }

        _logger.LogInformation("Memory indexed: {Count} entries from {Dir}", _entries.Count, _memoryDir);
        _indexed = true;
    }

    private async Task RewriteIndex(CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var entry in _entries)
            sb.AppendLine(JsonSerializer.Serialize(entry.Index, s_jsonOptions));

        var tmp = IndexPath + ".tmp";
        await File.WriteAllTextAsync(tmp, sb.ToString(), ct);
        File.Move(tmp, IndexPath, overwrite: true);
    }

    private int NextFileNumber()
    {
        var max = 0;
        if (Directory.Exists(_memoryDir))
        {
            foreach (var file in Directory.GetFiles(_memoryDir, "*.md"))
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

    #region Helpers

    private string ResolveMemoryDir()
    {
        if (!string.IsNullOrEmpty(_options.Path))
            return _options.Path;

        var root = _workspaceOptions.RootPath;
        if (!string.IsNullOrEmpty(root))
            return Path.Combine(Path.GetDirectoryName(root) ?? root, "memory");

        return Path.Combine(Path.GetTempPath(), "investigator", "memory");
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

    private List<(MemoryEntry, double)> KeywordSearch(string query)
    {
        var tokens = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _entries
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

    internal sealed class MemoryIndexEntry
    {
        public string? Id { get; set; }
        public DateTimeOffset Timestamp { get; set; }
        public string? Category { get; set; }
        public string[] Tags { get; set; } = [];
        public string? Source { get; set; }
        public string? File { get; set; }
        public string? Title { get; set; }
    }

    internal sealed record MemoryEntry(MemoryIndexEntry Index, string Body, float[]? Embedding);

    #endregion
}
