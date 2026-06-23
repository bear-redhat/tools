using System.Text;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Tools;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public sealed class MemoryConsolidator
{
    private readonly ILlmClientFactory _llmFactory;
    private readonly IEmbeddingClient _embedder;
    private readonly SkillsOptions _skillsOptions;
    private readonly MemoryOptions _memoryOptions;
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger<MemoryConsolidator> _logger;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public MemoryConsolidator(
        ILlmClientFactory llmFactory,
        IEmbeddingClient embedder,
        IOptions<SkillsOptions> skillsOptions,
        IOptions<MemoryOptions> memoryOptions,
        ToolRegistry toolRegistry,
        ILogger<MemoryConsolidator> logger)
    {
        _llmFactory = llmFactory;
        _embedder = embedder;
        _skillsOptions = skillsOptions.Value;
        _memoryOptions = memoryOptions.Value;
        _toolRegistry = toolRegistry;
        _logger = logger;
    }

    public async Task<string> RunAsync(CancellationToken ct)
    {
        var memoryTool = GetMemoryTool();
        if (memoryTool is null)
            return "Memory tool not available.";

        var entries = memoryTool.GetAllEntries();
        if (entries.Count == 0)
            return "No memories to consolidate.";

        var dreamingConfig = _memoryOptions.Dreaming;
        if (entries.Count < dreamingConfig.MinMemories)
            return $"Only {entries.Count} memories -- need at least {dreamingConfig.MinMemories} before consolidation.";

        _logger.LogInformation("Dreaming: starting consolidation of {Count} memories", entries.Count);

        var candidates = entries.ToList();
        if (candidates.Count > dreamingConfig.ContextBatchSize)
        {
            _logger.LogInformation("Dreaming: pre-filtering {Count} memories to {BatchSize} by embedding interconnectedness",
                candidates.Count, dreamingConfig.ContextBatchSize);
            candidates = PreFilterByEmbedding(candidates, dreamingConfig.ContextBatchSize);
        }

        var memoriesPrompt = BuildMemoriesBlock(candidates);
        var skillsSummary = BuildSkillsSummary();
        var existingSkillContents = LoadExistingSkillContents();

        var profileName = dreamingConfig.ModelProfile ?? _llmFactory.PrimaryProfileName;
        var client = _llmFactory.GetClient(profileName);

        var systemPrompt = BuildDreamingSystemPrompt(existingSkillContents.Keys);
        var userMessage = $"""
            ## Memories to Process

            {memoriesPrompt}

            ## Existing Skills (titles and tags)

            {skillsSummary}
            """;

        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = JsonSerializer.SerializeToElement(userMessage) }
        };

        _logger.LogInformation("Dreaming: calling LLM with {MemCount} memories and {SkillCount} existing skills",
            candidates.Count, existingSkillContents.Count);

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
            _logger.LogError(ex, "Dreaming: LLM call failed");
            return $"Consolidation failed: {ex.Message}";
        }

        var rawResponse = sb.ToString();
        _logger.LogDebug("Dreaming: raw LLM response length={Length}", rawResponse.Length);

        DreamingResponse? response;
        try
        {
            response = ParseResponse(rawResponse);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Dreaming: failed to parse LLM response");
            return $"Consolidation failed: could not parse LLM response.";
        }

        if (response is null || response.Skills.Count == 0)
            return "No skills produced by consolidation -- all memories may be too thin. Will retry next cycle.";

        var learnedDir = _skillsOptions.LearnedPath;
        if (string.IsNullOrEmpty(learnedDir))
        {
            _logger.LogError("Dreaming: LearnedPath not configured, cannot write skill files");
            return "Consolidation failed: LearnedPath not configured.";
        }

        Directory.CreateDirectory(learnedDir);

        var consumedIds = new HashSet<string>();
        var skillsWritten = new List<string>();

        foreach (var skill in response.Skills)
        {
            try
            {
                var filePath = Path.Combine(learnedDir, skill.Filename);

                if (skill.Action == "update" && existingSkillContents.ContainsKey(skill.Filename))
                    _logger.LogInformation("Dreaming: updating existing skill {File}", skill.Filename);
                else
                    _logger.LogInformation("Dreaming: creating new skill {File}", skill.Filename);

                await File.WriteAllTextAsync(filePath, skill.Content, ct);
                skillsWritten.Add(skill.Filename);

                foreach (var id in skill.ConsumedMemoryIds)
                    consumedIds.Add(id);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Dreaming: failed to write skill {File}", skill.Filename);
            }
        }

        if (consumedIds.Count > 0)
        {
            await memoryTool.DeleteEntries(consumedIds, ct);
            _logger.LogInformation("Dreaming: deleted {Count} consumed memories", consumedIds.Count);
        }

        var skillsLibrary = GetSkillsLibrary();
        skillsLibrary?.InvalidateIndex();

        var summary = $"Consolidated {consumedIds.Count} memories into {skillsWritten.Count} skill(s): {string.Join(", ", skillsWritten)}.";
        if (response.KeptMemoryIds.Count > 0)
            summary += $" {response.KeptMemoryIds.Count} memories kept for next cycle.";

        _logger.LogInformation("Dreaming: {Summary}", summary);
        return summary;
    }

    #region Prompt building

    private static string BuildDreamingSystemPrompt(IEnumerable<string> existingSkillFiles) =>
        $$"""
        You are a knowledge curator. You receive a set of raw memories from infrastructure investigations and a list of existing curated skills.

        Your task:
        1. Read all memories. Identify which are related, which are standalone, and which are too thin or vague to consolidate yet.
        2. For each group of related memories: decide whether to CREATE a new skill or UPDATE an existing skill.
        3. Produce each skill in this format:
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
        - Only consolidate memories that genuinely belong together. Do not force unrelated memories into one skill.
        - Memories that are too thin (isolated observations with no pattern) should be kept for the next cycle -- list them in kept_memory_ids.
        - When updating an existing skill, produce the COMPLETE updated content, not a diff.
        - Filenames should be lowercase kebab-case with .md extension.
        - Tags should be lowercase, single words or hyphenated terms.
        - The body should be practical operational knowledge, not a summary of the memories themselves.
        - Write in British English.
        - Return ONLY the JSON object, no markdown fences, no extra text.

        Existing skill files: {{string.Join(", ", existingSkillFiles.DefaultIfEmpty("(none)"))}}
        """;

    private static string BuildMemoriesBlock(IReadOnlyList<MemoryTool.MemoryEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            sb.AppendLine($"### Memory: {entry.Index.Title} (id: {entry.Index.Id})");
            sb.AppendLine($"Category: {entry.Index.Category} | Tags: {string.Join(", ", entry.Index.Tags)} | Date: {entry.Index.Timestamp:yyyy-MM-dd}");
            sb.AppendLine();
            sb.AppendLine(entry.Body);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private string BuildSkillsSummary()
    {
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(_skillsOptions.LearnedPath) && Directory.Exists(_skillsOptions.LearnedPath))
            dirs.Add(_skillsOptions.LearnedPath);
        if (Directory.Exists(_skillsOptions.Path))
            dirs.Add(_skillsOptions.Path);

        if (dirs.Count == 0)
            return "(no existing skills)";

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
        return sb.Length > 0 ? sb.ToString() : "(no existing skills)";
    }

    private Dictionary<string, string> LoadExistingSkillContents()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var learnedDir = _skillsOptions.LearnedPath;
        if (string.IsNullOrEmpty(learnedDir) || !Directory.Exists(learnedDir))
            return result;

        foreach (var file in Directory.GetFiles(learnedDir, "*.md"))
        {
            try
            {
                result[Path.GetFileName(file)] = File.ReadAllText(file);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to read existing skill {File}", file);
            }
        }
        return result;
    }

    #endregion

    #region Pre-filtering

    private List<MemoryTool.MemoryEntry> PreFilterByEmbedding(
        IReadOnlyList<MemoryTool.MemoryEntry> entries, int targetCount)
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

    private static DreamingResponse? ParseResponse(string rawResponse)
    {
        var json = rawResponse.Trim();
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline > 0) json = json[(firstNewline + 1)..];
            if (json.EndsWith("```")) json = json[..^3];
            json = json.Trim();
        }

        return JsonSerializer.Deserialize<DreamingResponse>(json, s_jsonOptions);
    }

    #endregion

    #region Tool resolution

    private MemoryTool? GetMemoryTool()
    {
        var defs = _toolRegistry.GetToolDefinitions();
        var memDef = defs.FirstOrDefault(d => d.Name == "memory");
        if (memDef is null) return null;

        var field = _toolRegistry.GetType()
            .GetField("_tools", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field?.GetValue(_toolRegistry) is Dictionary<string, IInvestigatorTool> tools
            && tools.TryGetValue("memory", out var tool))
            return tool as MemoryTool;

        return null;
    }

    private SkillsLibrary? GetSkillsLibrary()
    {
        var field = _toolRegistry.GetType()
            .GetField("_tools", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field?.GetValue(_toolRegistry) is Dictionary<string, IInvestigatorTool> tools
            && tools.TryGetValue("skills", out var tool))
            return tool as SkillsLibrary;

        return null;
    }

    #endregion

    #region Models

    private sealed class DreamingResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("skills")]
        public List<SkillOutput> Skills { get; set; } = [];

        [System.Text.Json.Serialization.JsonPropertyName("kept_memory_ids")]
        public List<string> KeptMemoryIds { get; set; } = [];
    }

    private sealed class SkillOutput
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
