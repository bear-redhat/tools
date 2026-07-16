using System.Text;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Tools;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public sealed class CasebookIndexer
{
    private readonly ILlmClientFactory _llmFactory;
    private readonly CasebookOptions _options;
    private readonly ILogger<CasebookIndexer> _logger;

    private CasebookTool? _casebook;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public CasebookIndexer(
        ILlmClientFactory llmFactory,
        IOptions<CasebookOptions> casebookOptions,
        ILogger<CasebookIndexer> logger)
    {
        _llmFactory = llmFactory;
        _options = casebookOptions.Value;
        _logger = logger;
    }

    internal void SetCasebook(CasebookTool casebook) => _casebook = casebook;

    public async Task<string> RunAsync(CancellationToken ct)
    {
        if (_casebook is null)
            return "Casebook not available.";

        var entries = _casebook.GetAllNotebookEntries();
        if (entries.Count == 0)
            return "The notebook is empty -- nothing to index.";

        var indexingConfig = _options.Indexing;
        if (entries.Count < indexingConfig.MinEntries)
            return $"Only {entries.Count} notebook entries -- need at least {indexingConfig.MinEntries} before indexing.";

        _logger.LogInformation("Indexing: starting consolidation of {Count} notebook entries", entries.Count);

        var candidates = entries.ToList();
        if (candidates.Count > indexingConfig.ContextBatchSize)
        {
            _logger.LogInformation("Indexing: pre-filtering {Count} entries to {BatchSize} by embedding interconnectedness",
                candidates.Count, indexingConfig.ContextBatchSize);
            candidates = PreFilterByEmbedding(candidates, indexingConfig.ContextBatchSize);
        }

        var entriesPrompt = BuildEntriesBlock(candidates);
        var commonplaceSummary = BuildCommonplaceSummary();
        var existingContents = LoadExistingCommonplaceContents();

        var profileName = indexingConfig.ModelProfile ?? _llmFactory.PrimaryProfileName;
        var client = _llmFactory.GetClient(profileName);

        var systemPrompt = BuildIndexingSystemPrompt(existingContents.Keys);
        var userMessage = $"""
            ## Notebook Entries to Process

            {entriesPrompt}

            ## Existing Commonplace Book Entries (titles and tags)

            {commonplaceSummary}
            """;

        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = JsonSerializer.SerializeToElement(userMessage) }
        };

        _logger.LogInformation("Indexing: calling LLM with {EntryCount} notebook entries and {CommonplaceCount} existing commonplace entries",
            candidates.Count, existingContents.Count);

        IReadOnlyList<ToolDefinition> noTools = [];
        var sb = new StringBuilder();
        try
        {
            await foreach (var block in client.StreamMessageAsync(messages, noTools, systemPrompt, ct))
            {
                if (block.Type == "text" && block.Text is not null)
                    sb.Append(block.Text);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Indexing: LLM call failed");
            return $"Indexing failed: {ex.Message}";
        }

        var rawResponse = sb.ToString();
        _logger.LogDebug("Indexing: raw LLM response length={Length}", rawResponse.Length);

        IndexingResponse? response;
        try
        {
            response = ParseResponse(rawResponse);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Indexing: failed to parse LLM response");
            return "Indexing failed: could not parse LLM response.";
        }

        if (response is null || response.Skills.Count == 0)
            return "Nothing distilled -- notebook entries may be too thin. Will retry next cycle.";

        var commonplaceDir = _options.CommonplacePath;
        if (string.IsNullOrEmpty(commonplaceDir))
        {
            _logger.LogError("Indexing: CommonplacePath not configured, cannot write commonplace entries");
            return "Indexing failed: CommonplacePath not configured.";
        }

        Directory.CreateDirectory(commonplaceDir);

        var consumedIds = new HashSet<string>();
        var filesWritten = new List<string>();

        foreach (var skill in response.Skills)
        {
            try
            {
                var filePath = Path.Combine(commonplaceDir, skill.Filename);

                if (skill.Action == "update" && existingContents.ContainsKey(skill.Filename))
                    _logger.LogInformation("Indexing: updating commonplace entry {File}", skill.Filename);
                else
                    _logger.LogInformation("Indexing: creating commonplace entry {File}", skill.Filename);

                await File.WriteAllTextAsync(filePath, skill.Content, ct);
                filesWritten.Add(skill.Filename);

                foreach (var id in skill.ConsumedMemoryIds)
                    consumedIds.Add(id);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Indexing: failed to write {File}", skill.Filename);
            }
        }

        if (consumedIds.Count > 0)
        {
            await _casebook.DeleteNotebookEntries(consumedIds, ct);
            _logger.LogInformation("Indexing: deleted {Count} consumed notebook entries", consumedIds.Count);
        }

        _casebook.InvalidateCommonplace();

        var summary = $"Filed {consumedIds.Count} notebook entries into {filesWritten.Count} commonplace entry/entries: {string.Join(", ", filesWritten)}.";
        if (response.KeptMemoryIds.Count > 0)
            summary += $" {response.KeptMemoryIds.Count} entries kept for next cycle.";

        _logger.LogInformation("Indexing: {Summary}", summary);
        return summary;
    }

    #region Prompt building

    private static string BuildIndexingSystemPrompt(IEnumerable<string> existingFiles) =>
        $$"""
        You are a knowledge curator. You receive a set of raw notebook entries from infrastructure investigations and a list of existing curated commonplace book entries.

        Your task:
        1. Read all notebook entries. Identify which are related, which are standalone, and which are too thin or vague to consolidate yet.
        2. For each group of related entries: decide whether to CREATE a new commonplace entry or UPDATE an existing one.
        3. Produce each entry in this format:
           ---
           title: <concise title>
           tags: [tag1, tag2, tag3]
           ---
           <markdown body with the distilled operational knowledge>
        4. Return your response as a JSON object (and nothing else) with this schema:
           {
             "skills": [
               {
                 "filename": "slug-name.md",
                 "action": "create" or "update",
                 "content": "---\ntitle: ...\ntags: [...]\n---\n...",
                 "consumed_memory_ids": ["id1", "id2"]
               }
             ],
             "kept_memory_ids": ["id3"]
           }

        Rules:
        - Only consolidate entries that genuinely belong together. Do not force unrelated entries into one.
        - Entries that are too thin (isolated observations with no pattern) should be kept for the next cycle -- list them in kept_memory_ids.
        - When updating an existing entry, produce the COMPLETE updated content, not a diff.
        - Filenames should be lowercase kebab-case with .md extension.
        - Tags should be lowercase, single words or hyphenated terms.
        - The body should be practical operational knowledge, not a summary of the entries themselves.
        - Write in British English.
        - Return ONLY the JSON object, no markdown fences, no extra text.

        Existing commonplace files: {{string.Join(", ", existingFiles.DefaultIfEmpty("(none)"))}}
        """;

    private static string BuildEntriesBlock(IReadOnlyList<CasebookTool.NotebookEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            sb.AppendLine($"### Entry: {entry.Index.Title} (id: {entry.Index.Id})");
            sb.AppendLine($"Category: {entry.Index.Category} | Tags: {string.Join(", ", entry.Index.Tags)} | Date: {entry.Index.Timestamp:yyyy-MM-dd}");
            sb.AppendLine();
            sb.AppendLine(entry.Body);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private string BuildCommonplaceSummary()
    {
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(_options.CommonplacePath) && Directory.Exists(_options.CommonplacePath))
            dirs.Add(_options.CommonplacePath);
        if (Directory.Exists(_options.SeedPath))
            dirs.Add(_options.SeedPath);

        if (dirs.Count == 0)
            return "(no existing commonplace entries)";

        var sb = new StringBuilder();
        foreach (var dir in dirs)
        {
            foreach (var file in Directory.GetFiles(dir, "*.md"))
            {
                var name = Path.GetFileName(file);
                try
                {
                    var firstLines = File.ReadLines(file).Take(10).ToList();
                    var title = firstLines.FirstOrDefault(l => l.StartsWith("title:"))
                        ?["title:".Length..]?.Trim().Trim('"', '\'') ?? name;
                    var tagsLine = firstLines.FirstOrDefault(l => l.StartsWith("tags:"));
                    var tags = tagsLine?["tags:".Length..]?.Trim();
                    sb.AppendLine($"- {name}: {title}{(tags is not null ? $" (tags: {tags})" : "")}");
                }
                catch
                {
                    sb.AppendLine($"- {name}");
                }
            }
        }
        return sb.Length > 0 ? sb.ToString() : "(no existing commonplace entries)";
    }

    private Dictionary<string, string> LoadExistingCommonplaceContents()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var commonplaceDir = _options.CommonplacePath;
        if (string.IsNullOrEmpty(commonplaceDir) || !Directory.Exists(commonplaceDir))
            return result;

        foreach (var file in Directory.GetFiles(commonplaceDir, "*.md"))
        {
            try
            {
                result[Path.GetFileName(file)] = File.ReadAllText(file);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to read existing commonplace entry {File}", file);
            }
        }
        return result;
    }

    #endregion

    #region Pre-filtering

    private List<CasebookTool.NotebookEntry> PreFilterByEmbedding(
        IReadOnlyList<CasebookTool.NotebookEntry> entries, int targetCount)
    {
        var withEmbeddings = entries.Where(e => e.Embedding is not null).ToList();
        if (withEmbeddings.Count <= targetCount)
            return entries.ToList();

        var scores = new Dictionary<int, double>();
        for (var i = 0; i < withEmbeddings.Count; i++)
        {
            var totalSim = 0.0;
            for (var j = 0; j < withEmbeddings.Count; j++)
            {
                if (i == j) continue;
                totalSim += CosineSimilarity(withEmbeddings[i].Embedding!, withEmbeddings[j].Embedding!);
            }
            scores[i] = totalSim;
        }

        var topIndices = scores.OrderByDescending(kv => kv.Value).Take(targetCount).Select(kv => kv.Key).ToHashSet();
        return topIndices.Select(i => withEmbeddings[i]).ToList();
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

    #region Response parsing

    private static IndexingResponse? ParseResponse(string rawResponse)
    {
        var json = rawResponse.Trim();
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline > 0) json = json[(firstNewline + 1)..];
            if (json.EndsWith("```")) json = json[..^3];
            json = json.Trim();
        }

        return JsonSerializer.Deserialize<IndexingResponse>(json, s_jsonOptions);
    }

    #endregion

    #region Models

    private sealed class IndexingResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("skills")]
        public List<CommonplaceOutput> Skills { get; set; } = [];

        [System.Text.Json.Serialization.JsonPropertyName("kept_memory_ids")]
        public List<string> KeptMemoryIds { get; set; } = [];
    }

    private sealed class CommonplaceOutput
    {
        [System.Text.Json.Serialization.JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("action")]
        public string Action { get; set; } = "create";

        [System.Text.Json.Serialization.JsonPropertyName("content")]
        public string? Content { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("consumed_memory_ids")]
        public List<string> ConsumedMemoryIds { get; set; } = [];
    }

    #endregion
}
