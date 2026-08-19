using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Services;

namespace Investigator.Tests;

/// <summary>
/// Caching is a prefix match on the exact bytes sent, so these assert the wire shape
/// rather than any intermediate object.
/// </summary>
public class PromptCachingTests
{
    private static ModelOptions Profile(bool caching = true, string? ttl = null) => new()
    {
        Provider = "vertex",
        Model = "claude-opus-4-6",
        MaxTokens = 8000,
        ThinkingBudget = 2000,
        PromptCaching = caching,
        PromptCacheTtl = ttl,
    };

    private static LlmMessage User(string text) => new()
    {
        Role = "user",
        Content = JsonSerializer.SerializeToElement(text),
    };

    private static LlmMessage ToolResult(string toolUseId, string content) => new()
    {
        Role = "user",
        Content = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "tool_result", tool_use_id = toolUseId, content },
        }),
    };

    private static JsonElement Build(
        ModelOptions profile, List<LlmMessage> messages, string? system = "PERSONA AND TOOL BRIEFINGS")
    {
        IReadOnlyList<ToolDefinition> tools =
        [
            new("run_oc", "Run oc", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone(),
                TimeSpan.FromSeconds(30)),
        ];

        var json = AnthropicRequestBuilder.BuildRequestJson(
            profile, messages, tools, system, "vertex-2023-10-16", stream: true);

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void SystemIsSentAsABlockListWithABreakpoint()
    {
        // Render order is tools -> system -> messages, so a breakpoint at the end of
        // system caches the tool schemas with it.
        var root = Build(Profile(), [User("hello")]);

        var system = root.GetProperty("system");
        Assert.Equal(JsonValueKind.Array, system.ValueKind);

        var block = system[0];
        Assert.Equal("text", block.GetProperty("type").GetString());
        Assert.Contains("PERSONA", block.GetProperty("text").GetString());
        Assert.Equal("ephemeral", block.GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public void TheNewestTurnCarriesTheSecondBreakpoint()
    {
        var root = Build(Profile(), [User("first"), ToolResult("toolu_1", "pods listed")]);

        var messages = root.GetProperty("messages");
        var last = messages[messages.GetArrayLength() - 1];
        var lastBlock = last.GetProperty("content")[0];

        Assert.Equal("tool_result", lastBlock.GetProperty("type").GetString());
        Assert.Equal("ephemeral", lastBlock.GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public void EarlierTurnsAreLeftAlone()
    {
        // At most four breakpoints exist; spending them on stale turns would waste them.
        var root = Build(Profile(), [User("first"), User("second"), ToolResult("toolu_1", "out")]);

        var messages = root.GetProperty("messages");
        Assert.DoesNotContain("cache_control", messages[0].GetRawText());
        Assert.DoesNotContain("cache_control", messages[1].GetRawText());
    }

    [Fact]
    public void AStringContentTurn_BecomesATextBlockSoItCanCarryTheMarker()
    {
        var root = Build(Profile(), [User("why did it fail?")]);

        var content = root.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("why did it fail?", content[0].GetProperty("text").GetString());
        Assert.True(content[0].TryGetProperty("cache_control", out _));
    }

    [Fact]
    public void ToolResultStructureSurvivesTheRewrite()
    {
        // The block is rewritten through a node tree; losing tool_use_id would make the
        // API reject the whole turn.
        var root = Build(Profile(), [ToolResult("toolu_42", "NAME READY\nfoo 1/1")]);

        var block = root.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("toolu_42", block.GetProperty("tool_use_id").GetString());
        Assert.Equal("NAME READY\nfoo 1/1", block.GetProperty("content").GetString());
    }

    [Fact]
    public void MultipleToolResultsInOneTurn_OnlyTheLastIsMarked()
    {
        var turn = new LlmMessage
        {
            Role = "user",
            Content = JsonSerializer.SerializeToElement(new object[]
            {
                new { type = "tool_result", tool_use_id = "toolu_1", content = "a" },
                new { type = "tool_result", tool_use_id = "toolu_2", content = "b" },
            }),
        };

        var content = Build(Profile(), [turn]).GetProperty("messages")[0].GetProperty("content");

        Assert.False(content[0].TryGetProperty("cache_control", out _));
        Assert.True(content[1].TryGetProperty("cache_control", out _));
    }

    [Fact]
    public void DisablingCachingEmitsNoMarkersAnywhere()
    {
        var json = Build(Profile(caching: false), [User("hello"), ToolResult("toolu_1", "out")]).GetRawText();

        Assert.DoesNotContain("cache_control", json);
    }

    [Fact]
    public void AConfiguredTtlIsSentOnBothBreakpoints()
    {
        var root = Build(Profile(ttl: "1h"), [ToolResult("toolu_1", "out")]);

        Assert.Equal("1h", root.GetProperty("system")[0].GetProperty("cache_control").GetProperty("ttl").GetString());
        Assert.Equal("1h", root.GetProperty("messages")[0].GetProperty("content")[0]
            .GetProperty("cache_control").GetProperty("ttl").GetString());
    }

    [Fact]
    public void NoSystemPrompt_EmitsNoSystemField()
    {
        var root = Build(Profile(), [User("hello")], system: null);

        Assert.False(root.TryGetProperty("system", out _));
    }

    [Fact]
    public void TheCallersMessagesAreNotMutated()
    {
        // AgentRunner reuses its message list across turns; a marker leaking into stored
        // history would move the breakpoint and invalidate the prefix every call.
        var messages = new List<LlmMessage> { ToolResult("toolu_1", "out") };

        Build(Profile(), messages);

        Assert.DoesNotContain("cache_control", messages[0].Content.GetRawText());
    }
}
