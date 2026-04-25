using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Services;
using Microsoft.Extensions.Options;

namespace Investigator.Tools;

public sealed class SkillsLibrary : IInvestigatorTool
{
    private static readonly JsonElement s_paramSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "action": {
                "type": "string",
                "enum": ["search", "read", "list"],
                "description": "search: find relevant entries by query. read: get full content of a named entry. list: show all entries in the index."
            },
            "query": { "type": "string", "description": "Search query (for search action)" },
            "name": { "type": "string", "description": "Entry name without .md extension (for read action)" }
        },
        "required": ["action"]
    }
    """).RootElement.Clone();

    private readonly IEmbeddingClient _embedder;
    private readonly ILogger<SkillsLibrary> _logger;
    private string _skillsDir = "skills";
    private readonly List<SkillEntry> _skills = [];
    private bool _indexed;

    public SkillsLibrary(IEmbeddingClient embedder, ILogger<SkillsLibrary> logger, IOptions<SkillsOptions> options)
    {
        _embedder = embedder;
        _logger = logger;
        var path = options.Value.Path;
        if (!string.IsNullOrEmpty(path))
            _skillsDir = path;
    }

    public ToolDefinition Definition => new(
        Name: "skills",
        Description: "Consult the index -- Little Bear's personal reference of operational notes, organised by topic. "
            + "Use 'search' to find relevant entries, 'read' to study the full content, or 'list' to review what is available.",
        ParameterSchema: s_paramSchema,
        DefaultTimeout: TimeSpan.FromSeconds(30));

    public async Task<ToolResult> InvokeAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        await EnsureIndexed(ct);

        var action = parameters.GetProperty("action").GetString() ?? "";

        return action switch
        {
            "search" => await Search(parameters, context, ct),
            "read" => Read(parameters, context),
            "list" => List(context),
            _ => LogError(context, $"Unknown action: {action}. Use 'search', 'read', or 'list'."),
        };
    }

    private async Task EnsureIndexed(CancellationToken ct)
    {
        if (_indexed) return;

        if (!Directory.Exists(_skillsDir))
        {
            _logger.LogWarning("Skills directory not found: {Dir}", _skillsDir);
            _indexed = true;
            return;
        }

        foreach (var file in Directory.GetFiles(_skillsDir, "*.md"))
        {
            try
            {
                var content = await File.ReadAllTextAsync(file, ct);
                var (title, tags, body) = ParseFrontmatter(content);
                var name = Path.GetFileNameWithoutExtension(file);

                var textForEmbedding = $"{title} {string.Join(" ", tags)} {body}";

                float[]? embedding = null;
                try
                {
                    embedding = await _embedder.EmbedAsync(textForEmbedding, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to embed skill {Name}, falling back to keyword search", name);
                }

                _skills.Add(new SkillEntry(name, title, tags, body, content, embedding));
                _logger.LogInformation("Loaded skill: {Name} ({Title}), {TagCount} tags, embedded={HasEmbedding}",
                    name, title, tags.Length, embedding is not null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load skill from {File}", file);
            }
        }

        _logger.LogInformation("Skills library indexed: {Count} skills from {Dir}", _skills.Count, _skillsDir);
        _indexed = true;
    }

    private async Task<ToolResult> Search(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var query = parameters.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(query))
            return LogError(context, "search requires a 'query' parameter");

        if (_skills.Count == 0)
            return new ToolResult("The index is empty.");

        context.Logger.LogInformation("skills: searching for '{Query}' across {Count} skills", query, _skills.Count);

        var hasEmbeddings = _skills.Any(s => s.Embedding is not null);
        List<(SkillEntry Skill, double Score)> results;

        if (hasEmbeddings)
        {
            try
            {
                var queryEmbedding = await _embedder.EmbedAsync(query, ct);
                results = _skills
                    .Where(s => s.Embedding is not null)
                    .Select(s => (s, Score: CosineSimilarity(queryEmbedding, s.Embedding!)))
                    .OrderByDescending(x => x.Score)
                    .Take(3)
                    .ToList();
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning(ex, "Embedding search failed, falling back to keyword search");
                results = KeywordSearch(query);
            }
        }
        else
        {
            results = KeywordSearch(query);
        }

        if (results.Count == 0)
            return new ToolResult("No matching entries found. Use skills(action: 'list') to review the index.");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Found {results.Count} relevant entries:\n");
        foreach (var (skill, score) in results)
        {
            sb.AppendLine($"- **{skill.Title}** (name: `{skill.Name}`, score: {score:F2})");
            sb.AppendLine($"  Tags: {string.Join(", ", skill.Tags)}");
            sb.AppendLine();
        }
        sb.AppendLine("Use skills(action: 'read', name: '...') to study the full entry.");

        return new ToolResult(sb.ToString());
    }

    private ToolResult Read(JsonElement parameters, ToolContext context)
    {
        var name = parameters.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(name))
            return LogError(context, "read requires a 'name' parameter");

        var skill = _skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
        {
            context.Logger.LogWarning("skills: skill '{Name}' not found", name);
            return new ToolResult($"No entry named '{name}' in the index. Use skills(action: 'list') to see what is available.", ExitCode: 1);
        }

        context.Logger.LogInformation("skills: reading skill '{Name}'", name);
        return new ToolResult(skill.FullContent);
    }

    private ToolResult List(ToolContext context)
    {
        if (_skills.Count == 0)
            return new ToolResult("The index is empty.");

        context.Logger.LogInformation("skills: listing {Count} skills", _skills.Count);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"The index ({_skills.Count} entries):\n");
        foreach (var skill in _skills)
        {
            sb.AppendLine($"- **{skill.Title}** (name: `{skill.Name}`)");
            sb.AppendLine($"  Tags: {string.Join(", ", skill.Tags)}");
        }

        return new ToolResult(sb.ToString());
    }

    private List<(SkillEntry, double)> KeywordSearch(string query)
    {
        var tokens = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _skills
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

    private static ToolResult LogError(ToolContext context, string message)
    {
        context.Logger.LogError("skills: {Message}", message);
        return new ToolResult(message, ExitCode: 1);
    }

    private static (string Title, string[] Tags, string Body) ParseFrontmatter(string content)
    {
        var title = "";
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

    private sealed record SkillEntry(string Name, string Title, string[] Tags, string Body, string FullContent, float[]? Embedding);
}
