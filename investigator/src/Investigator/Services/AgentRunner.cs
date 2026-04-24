using System.Text.Json;
using System.Threading.Channels;
using Investigator.Contracts;
using Investigator.Models;

namespace Investigator.Services;

public sealed class AgentRunner
{
    public record Config(
        string Id,
        string Name,
        string Role,
        string SystemPrompt,
        ILlmClient LlmClient,
        IReadOnlyList<ToolDefinition> Tools,
        List<LlmMessage>? InitialMessages,
        int MaxToolCalls,
        int MaxRetries,
        string WorkspacePath,
        int? CompactionMaxTokens);

    public record ToolExecutionResult(
        string Output,
        string? OutputFile = null,
        int ExitCode = 0,
        bool TimedOut = false,
        bool Concluded = false);

    private const int MaxTruncationRetries = 2;
    private const int MaxTruncationFallthroughs = 3;

    private readonly ILogger _logger;

    public AgentRunner(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(
        Config config,
        ChannelReader<RoomMessage> inbox,
        Func<AgentEvent, ValueTask> emit,
        Func<string, JsonElement, CancellationToken, Task<ToolExecutionResult>> executeTool,
        CancellationToken ct)
    {
        var stepId = 0;
        var toolCallCount = 0;
        var messages = config.InitialMessages ?? [];

        _logger.LogInformation("Agent {Name} ({Role}) starting, maxToolCalls={Max}", config.Name, config.Role, config.MaxToolCalls);

        try
        {
            while (await inbox.WaitToReadAsync(ct))
            {
                while (inbox.TryRead(out var msg))
                    messages.Add(FormatInboxMessage(msg));

                var currentStepId = $"step-{++stepId}";
                await emit(new AgentEvent.StatusChanged(currentStepId, true));

                var concluded = false;
                var truncationRetries = 0;
                var consecutiveTruncationFallthroughs = 0;
                while (!concluded && !ct.IsCancellationRequested)
                {
                    currentStepId = $"step-{++stepId}";

                    _logger.LogDebug("Agent {Name} loop iteration {Step}, toolCallCount={Count}/{Max}",
                        config.Name, stepId, toolCallCount, config.MaxToolCalls);

                    if (config.CompactionMaxTokens is not null)
                        CompactMessagesIfNeeded(messages, config.CompactionMaxTokens.Value, config.WorkspacePath);

                    List<ContentBlock>? contentBlocks = null;
                    string? llmError = null;
                    try
                    {
                        contentBlocks = await CallLlmWithRetry(config, messages, ct);
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        LogMessageStructure(config.Name, messages);
                        _logger.LogError(ex, "Agent {Name} LLM call rejected (HTTP 400) at step {Step}",
                            config.Name, currentStepId);
                        llmError = $"LLM call rejected: {ex.Message}";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Agent {Name} LLM call failed after {Retries} retries at step {Step}",
                            config.Name, config.MaxRetries, currentStepId);
                        llmError = $"LLM call failed: {ex.Message}";
                    }

                    if (llmError is not null)
                    {
                        await emit(new AgentEvent.Error(currentStepId, llmError));
                        await emit(new AgentEvent.StatusChanged(currentStepId, false));
                        return;
                    }

                    var textParts = new List<string>();
                    var toolUses = new List<ContentBlock>();
                    ContentBlock? concludeCall = null;

                    var thinkingParts = new List<string>();

                    foreach (var block in contentBlocks!)
                    {
                        if (block.Type == "thinking" && !string.IsNullOrEmpty(block.Text))
                            thinkingParts.Add(block.Text);
                        else if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
                            textParts.Add(block.Text);
                        else if (block.Type == "tool_use" && block.Name == "conclude")
                            concludeCall = block;
                        else if (block.Type == "tool_use")
                            toolUses.Add(block);
                    }

                    var truncatedTools = contentBlocks!.Where(b => b.Type == "tool_use" && b.Truncated).ToList();

                    _logger.LogDebug("Agent {Name} LLM returned {TextParts} text, {ToolUses} tools, conclude={HasConclude}, truncated={TruncatedCount} at step {Step}",
                        config.Name, textParts.Count, toolUses.Count, concludeCall is not null, truncatedTools.Count, currentStepId);

                    if (thinkingParts.Count > 0)
                        await emit(new AgentEvent.Thinking(currentStepId, string.Join("\n", thinkingParts)));

                    // Case 0: output was truncated -- tool_use blocks lost their JSON input.
                    // Strategy: save the text the model already wrote as the assistant turn,
                    // then ask it to continue with just the tool calls (no new text).
                    if (truncatedTools.Count > 0)
                    {
                        var truncatedNames = string.Join(", ", truncatedTools.Select(t => t.Name ?? "unknown"));
                        _logger.LogWarning("Agent {Name} response truncated at output token limit, lost tool calls: {Tools}",
                            config.Name, truncatedNames);

                        truncationRetries++;
                        if (truncationRetries <= MaxTruncationRetries)
                        {
                            if (textParts.Count > 0)
                            {
                                await emit(new AgentEvent.Message(currentStepId, string.Join("\n", textParts), IsIntermediate: true));
                                messages.Add(new LlmMessage
                                {
                                    Role = "assistant",
                                    Content = JsonSerializer.SerializeToElement(string.Join("\n", textParts)),
                                });
                            }

                            messages.Add(new LlmMessage
                            {
                                Role = "user",
                                Content = JsonSerializer.SerializeToElement(
                                    $"Your response was cut off before the tool call{(truncatedTools.Count > 1 ? "s" : "")} could complete. " +
                                    $"Your text above is saved. Now continue with ONLY the tool call{(truncatedTools.Count > 1 ? "s" : "")} " +
                                    $"({truncatedNames}) — emit the tool_use block{(truncatedTools.Count > 1 ? "s" : "")} with no additional text."),
                            });
                            continue;
                        }

                        _logger.LogWarning("Agent {Name} exhausted {Max} truncation retries, falling through",
                            config.Name, MaxTruncationRetries);

                        consecutiveTruncationFallthroughs++;

                        if (consecutiveTruncationFallthroughs >= MaxTruncationFallthroughs)
                        {
                            _logger.LogWarning(
                                "Agent {Name} hit {Count} consecutive truncation fallthroughs, forcing compaction",
                                config.Name, consecutiveTruncationFallthroughs);

                            CompactMessagesIfNeeded(messages, 0, config.WorkspacePath);
                            truncationRetries = 0;
                            consecutiveTruncationFallthroughs = 0;
                            continue;
                        }

                        var emptyInput = JsonDocument.Parse("{}").RootElement.Clone();
                        foreach (var t in truncatedTools)
                        {
                            t.Input = emptyInput;
                            t.Truncated = false;
                            _logger.LogInformation("Agent {Name} recovering truncated no-input tool {Tool} with empty input",
                                config.Name, t.Name);
                        }
                    }

                    if (truncatedTools.Count == 0)
                    {
                        truncationRetries = 0;
                        consecutiveTruncationFallthroughs = 0;
                    }

                    // Case 1: conclude
                    if (concludeCall is not null)
                    {
                        var result = await executeTool("conclude", concludeCall.Input ?? default, ct);

                        if (!result.Concluded)
                        {
                            _logger.LogWarning("Agent {Name} conclude blocked: {Reason}", config.Name, result.Output);

                            var assistantContent = new List<object>();
                            foreach (var tp in textParts)
                                assistantContent.Add(new { type = "text", text = tp });
                            assistantContent.Add(new { type = "tool_use", id = concludeCall.Id, name = "conclude", input = concludeCall.Input });

                            messages.Add(new LlmMessage
                            {
                                Role = "assistant",
                                Content = JsonSerializer.SerializeToElement(assistantContent),
                            });
                            messages.Add(new LlmMessage
                            {
                                Role = "user",
                                Content = JsonSerializer.SerializeToElement(new[]
                                {
                                    new { type = "tool_result", tool_use_id = concludeCall.Id, content = result.Output }
                                }),
                            });
                            continue;
                        }

                        concluded = true;
                        break;
                    }

                    // Case 2: tool calls
                    if (toolUses.Count > 0)
                    {
                        if (textParts.Count > 0)
                        {
                            await emit(new AgentEvent.Message(currentStepId, string.Join("\n", textParts), IsIntermediate: true));
                        }

                        var assistantContent = new List<object>();
                        foreach (var tp in textParts)
                            assistantContent.Add(new { type = "text", text = tp });
                        foreach (var tu in toolUses)
                            assistantContent.Add(new { type = "tool_use", id = tu.Id, name = tu.Name, input = tu.Input });

                        messages.Add(new LlmMessage
                        {
                            Role = "assistant",
                            Content = JsonSerializer.SerializeToElement(assistantContent),
                        });

                        var toolResults = new List<object>();
                        var bufferedInbox = new List<LlmMessage>();

                        foreach (var tu in toolUses)
                        {
                            toolCallCount++;
                            var toolStepId = $"step-{++stepId}";
                            var toolName = tu.Name ?? "unknown";
                            var toolInput = tu.Input ?? default;
                            var displayCmd = FormatDisplayCommand(toolName, toolInput);

                            _logger.LogInformation("Agent {Agent} executing tool {Tool} at step {Step}: {Command}",
                                config.Name, toolName, toolStepId, displayCmd);

                            await emit(new AgentEvent.ToolCall(toolStepId, toolName, displayCmd, toolInput));

                            var result = await executeTool(toolName, toolInput, ct);

                            await emit(new AgentEvent.ToolResult(toolStepId, toolName, result.Output, result.OutputFile, result.ExitCode, result.TimedOut));

                            toolResults.Add(new
                            {
                                type = "tool_result",
                                tool_use_id = tu.Id,
                                content = result.Output,
                            });

                            while (inbox.TryRead(out var msg))
                                bufferedInbox.Add(FormatInboxMessage(msg));
                        }

                        messages.Add(new LlmMessage
                        {
                            Role = "user",
                            Content = JsonSerializer.SerializeToElement(toolResults),
                        });

                        messages.AddRange(bufferedInbox);

                        if (toolCallCount >= config.MaxToolCalls)
                        {
                            _logger.LogWarning("Agent {Name} max tool calls ({Max}) reached, forcing conclusion",
                                config.Name, config.MaxToolCalls);
                            messages.Add(new LlmMessage
                            {
                                Role = "user",
                                Content = JsonSerializer.SerializeToElement(
                                    $"You have used all {config.MaxToolCalls} tool calls. Call the conclude tool now with your best conclusion."),
                            });
                        }

                        continue;
                    }

                    // Case 3: text only -- agent speaks, then goes idle
                    var messageText = string.Join("\n", textParts);

                    if (!string.IsNullOrWhiteSpace(messageText))
                    {
                        await emit(new AgentEvent.Message(currentStepId, messageText));

                        messages.Add(new LlmMessage
                        {
                            Role = "assistant",
                            Content = JsonSerializer.SerializeToElement(messageText),
                        });
                    }

                    _logger.LogInformation("Agent {Name} paused at step {Step}, waiting for next message", config.Name, currentStepId);
                    await emit(new AgentEvent.StatusChanged(currentStepId, false));
                    break;
                }

                if (concluded)
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Agent {Name} cancelled", config.Name);
        }

        _logger.LogInformation("Agent {Name} loop exited", config.Name);
    }

    private async Task<List<ContentBlock>> CallLlmWithRetry(Config config, List<LlmMessage> messages, CancellationToken ct)
    {
        Exception? lastEx = null;

        for (var attempt = 0; attempt <= config.MaxRetries; attempt++)
        {
            try
            {
                var blocks = new List<ContentBlock>();
                await foreach (var block in config.LlmClient.StreamMessageAsync(messages, config.Tools, config.SystemPrompt, ct))
                    blocks.Add(block);

                _logger.LogDebug("Agent {Name} LLM returned {Count} content blocks on attempt {Attempt}",
                    config.Name, blocks.Count, attempt + 1);
                return blocks;
            }
            catch (HttpRequestException ex) when (attempt < config.MaxRetries && IsTransientHttpError(ex))
            {
                lastEx = ex;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(ex, "Agent {Name} LLM call failed with transient error (attempt {Attempt}/{Max}), retrying in {Delay}s",
                    config.Name, attempt + 1, config.MaxRetries + 1, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }

        throw lastEx ?? new InvalidOperationException("LLM call failed with no exception captured");
    }

    private static bool IsTransientHttpError(HttpRequestException ex)
    {
        if (ex.StatusCode is null) return true;
        var code = (int)ex.StatusCode;
        return code == 429 || code >= 500;
    }

    private void CompactMessagesIfNeeded(List<LlmMessage> messages, int maxTokenBudget, string workspacePath)
    {
        var estimatedTokens = EstimateTokenCount(messages);

        if (estimatedTokens < maxTokenBudget * 0.8)
            return;

        var keepRecent = 6;
        if (messages.Count <= keepRecent + 1)
            return;

        _logger.LogInformation("Compacting message history: {Count} messages, ~{Tokens} estimated tokens (budget={Budget})",
            messages.Count, estimatedTokens, maxTokenBudget);

        var compactEnd = messages.Count - keepRecent;

        // Walk the boundary forward so we never split a tool_use/tool_result pair:
        // if compactEnd lands on a user message whose content is a tool_result array,
        // or on an assistant message containing tool_use blocks, back up to include
        // the full pair in the kept portion.
        while (compactEnd > 0 && IsToolBoundary(messages, compactEnd))
            compactEnd--;

        if (compactEnd <= 1)
            return;

        var summaryParts = new List<string>();

        for (var i = 0; i < compactEnd; i++)
        {
            var msg = messages[i];
            var contentStr = msg.Content.ValueKind == JsonValueKind.String
                ? msg.Content.GetString() ?? ""
                : msg.Content.GetRawText();

            if (contentStr.Length > 200)
                contentStr = contentStr[..200] + "...";

            summaryParts.Add($"[{msg.Role}]: {contentStr}");
        }

        var summary = $"[Compacted: {compactEnd} earlier messages summarised. Key exchanges:\n"
            + string.Join("\n", summaryParts)
            + "\nFull conversation is preserved in transcript.jsonl in the workspace.]";

        messages.RemoveRange(0, compactEnd);
        messages.Insert(0, new LlmMessage
        {
            Role = "user",
            Content = JsonSerializer.SerializeToElement(summary),
        });

        _logger.LogInformation("Compacted {Removed} messages into summary, {Remaining} messages remain",
            compactEnd, messages.Count);
    }

    private static bool IsToolBoundary(List<LlmMessage> messages, int index)
    {
        if (index >= messages.Count) return false;

        var msg = messages[index];

        // Don't start the kept portion with a tool_result message (its tool_use is being compacted)
        if (msg.Role == "user" && msg.Content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in msg.Content.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var t) && t.GetString() == "tool_result")
                    return true;
            }
        }

        // Don't start the kept portion right after an assistant tool_use (its tool_result would be next)
        if (index > 0)
        {
            var prev = messages[index - 1];
            if (prev.Role == "assistant" && prev.Content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in prev.Content.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var t) && t.GetString() == "tool_use")
                        return true;
                }
            }
        }

        return false;
    }

    private void LogMessageStructure(string agentName, List<LlmMessage> messages)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Message structure for {agentName} ({messages.Count} messages):");
        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            var types = "string";
            if (msg.Content.ValueKind == JsonValueKind.Array)
            {
                var blockTypes = new List<string>();
                foreach (var item in msg.Content.EnumerateArray())
                {
                    var t = item.TryGetProperty("type", out var tp) ? tp.GetString() ?? "?" : "?";
                    var id = item.TryGetProperty("id", out var idp) ? idp.GetString()
                           : item.TryGetProperty("tool_use_id", out var tuidp) ? tuidp.GetString()
                           : null;
                    blockTypes.Add(id is not null ? $"{t}({id[^8..]})" : t);
                }
                types = string.Join(", ", blockTypes);
            }
            sb.AppendLine($"  [{i}] {msg.Role}: [{types}]");
        }
        _logger.LogError("{Structure}", sb.ToString());
    }

    private static int EstimateTokenCount(List<LlmMessage> messages)
    {
        var totalChars = 0;
        foreach (var msg in messages)
        {
            totalChars += msg.Content.ValueKind == JsonValueKind.String
                ? msg.Content.GetString()?.Length ?? 0
                : msg.Content.GetRawText().Length;
        }
        return totalChars / 4;
    }

    private static LlmMessage FormatInboxMessage(RoomMessage msg)
    {
        var text = msg.Sender == "user"
            ? msg.Text
            : $"[{msg.Sender}]: {msg.Text}";

        return new LlmMessage
        {
            Role = "user",
            Content = JsonSerializer.SerializeToElement(text),
        };
    }

    internal static string FormatDisplayCommand(string toolName, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return toolName;
        return toolName switch
        {
            "run_oc" => "oc " + Prop(input, "command"),
            "run_shell" => Prop(input, "command"),
            "release_repo" => $"release_repo({Prop(input, "action")})",
            "skills" => $"skills {Prop(input, "action")}" + OptProp(input, "query", " ") + OptProp(input, "name", " "),
            "delegate" => $"delegate {Prop(input, "role")}",
            "conclude" => Truncate($"conclude: {Prop(input, "summary")}", 80),
            "present_finding" => $"finding: {Prop(input, "title")}",
            "reply_to" => $"reply_to {Prop(input, "agent_name")}",
            _ => FormatGenericTool(toolName, input),
        };
    }

    internal static Dictionary<string, string>? ExtractContext(string toolName, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        Dictionary<string, string>? ctx = null;

        void Add(string key, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            ctx ??= new();
            ctx[key] = value;
        }

        switch (toolName)
        {
            case "run_oc":
                Add("cluster", Prop(input, "cluster"));
                break;
            case "delegate":
                Add("model", Prop(input, "model"));
                break;
            case "skills":
                Add("action", Prop(input, "action"));
                break;
        }

        return ctx;
    }

    private static string Prop(JsonElement input, string name) =>
        input.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    private static string OptProp(JsonElement input, string name, string prefix) =>
        input.TryGetProperty(name, out var v) && !string.IsNullOrEmpty(v.GetString())
            ? prefix + v.GetString()
            : "";

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "\u2026";

    private static string FormatGenericTool(string toolName, JsonElement input)
    {
        var parts = new List<string> { toolName };
        foreach (var prop in input.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var val = prop.Value.GetString() ?? "";
                if (val.Length > 40) val = val[..39] + "\u2026";
                parts.Add($"{prop.Name}={val}");
                if (parts.Count >= 4) break;
            }
        }
        return string.Join(" ", parts);
    }
}
