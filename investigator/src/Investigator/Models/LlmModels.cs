using System.Text.Json;
using System.Text.Json.Serialization;

namespace Investigator.Models;

public class LlmRequest
{
    [JsonPropertyName("anthropic_version")]
    public string AnthropicVersion { get; set; } = "vertex-2023-10-16";

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 8192;

    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Stream { get; set; }

    [JsonPropertyName("thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThinkingConfig? Thinking { get; set; }

    [JsonPropertyName("system")]
    public string? System { get; set; }

    [JsonPropertyName("messages")]
    public List<LlmMessage> Messages { get; set; } = [];

    [JsonPropertyName("tools")]
    public List<LlmTool>? Tools { get; set; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlmRequestMetadata? Metadata { get; set; }
}

public class LlmRequestMetadata
{
    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }
}

public class ThinkingConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "enabled";

    [JsonPropertyName("budget_tokens")]
    public int BudgetTokens { get; set; } = 10000;
}

public class LlmMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public JsonElement Content { get; set; }
}

public class LlmTool
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("input_schema")]
    public JsonElement InputSchema { get; set; }
}

public class ContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("input")]
    public JsonElement? Input { get; set; }

    [JsonIgnore]
    public bool Truncated { get; set; }

    [JsonIgnore]
    public UsageInfo? Usage { get; set; }
}

public class StreamEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("content_block")]
    public ContentBlock? ContentBlock { get; set; }

    [JsonPropertyName("delta")]
    public StreamDelta? Delta { get; set; }

    [JsonPropertyName("message")]
    public StreamMessage? Message { get; set; }

    [JsonPropertyName("usage")]
    public UsageInfo? Usage { get; set; }
}

public class StreamDelta
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("thinking")]
    public string? Thinking { get; set; }

    [JsonPropertyName("partial_json")]
    public string? PartialJson { get; set; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }
}

public class StreamMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("usage")]
    public UsageInfo? Usage { get; set; }
}

public class UsageInfo
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    [JsonPropertyName("cache_creation_input_tokens")]
    public int CacheCreationInputTokens { get; set; }

    [JsonPropertyName("cache_read_input_tokens")]
    public int CacheReadInputTokens { get; set; }
}

public record LlmRequestContext(string? UserId, string? ConversationId);
