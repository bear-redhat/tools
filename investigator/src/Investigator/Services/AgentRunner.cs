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
        int? CompactionMaxTokens,
        int ThinkingBudget = 10000,
        int ContextWindowTokens = 1_000_000,
        string? UserId = null,
        string? ConversationId = null,
        decimal InputPricePerMToken = 0,
        decimal OutputPricePerMToken = 0,
        decimal CacheReadPricePerMToken = 0,
        decimal CacheCreationPricePerMToken = 0,
        ILlmClient? SummarizerClient = null,
        ModelOptions? SummarizerModelOptions = null);

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
        Func<string, JsonElement, string, CancellationToken, Task<ToolExecutionResult>> executeTool,
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

                toolCallCount = 0;

                var currentStepId = $"step-{++stepId}";
                await emit(new AgentEvent.StatusChanged(currentStepId, true));

                var concluded = false;
                var truncationRetries = 0;
                var consecutiveTruncationFallthroughs = 0;
                int? thinkingBudgetOverride = null;
                var promptTooLongRetried = false;
                while (!concluded && !ct.IsCancellationRequested)
                {
                    currentStepId = $"step-{++stepId}";

                    _logger.LogDebug("Agent {Name} loop iteration {Step}, toolCallCount={Count}/{Max}",
                        config.Name, stepId, toolCallCount, config.MaxToolCalls);

                    var compactionBudget = config.CompactionMaxTokens
                        ?? (int)(config.ContextWindowTokens * 0.7);
                    await CompactMessagesIfNeededAsync(messages, compactionBudget, config, currentStepId, emit, ct);

                    List<ContentBlock>? contentBlocks = null;
                    UsageInfo? usageInfo = null;
                    string? llmError = null;
                    try
                    {
                        var llmContext = (config.UserId is not null || config.ConversationId is not null)
                            ? new LlmRequestContext(config.UserId, config.ConversationId)
                            : null;
                        contentBlocks = await CallLlmWithRetry(config, messages, ct, thinkingBudgetOverride, llmContext);
                        promptTooLongRetried = false;
                        usageInfo = ExtractUsage(contentBlocks);
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        if (!promptTooLongRetried && ex.Message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase))
                        {
                            promptTooLongRetried = true;
                            var emergencyBudget = (int)(config.ContextWindowTokens * 0.5);
                            _logger.LogWarning("Agent {Name} prompt too long at step {Step}, emergency compaction to {Budget} tokens",
                                config.Name, currentStepId, emergencyBudget);
                            await CompactMessagesIfNeededAsync(messages, emergencyBudget, config, currentStepId, emit, ct);
                            continue;
                        }

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

                    if (usageInfo is not null)
                    {
                        var cost = ComputeCost(usageInfo, config.InputPricePerMToken, config.OutputPricePerMToken,
                            config.CacheReadPricePerMToken, config.CacheCreationPricePerMToken);
                        await emit(new AgentEvent.Usage(currentStepId, config.Name,
                            usageInfo.InputTokens, usageInfo.OutputTokens,
                            usageInfo.CacheReadInputTokens, usageInfo.CacheCreationInputTokens, cost));
                    }

                    // Case 0: output was truncated -- some tool_use blocks lost their JSON input.
                    // Strategy: execute the successful tools, save them properly, and provide
                    // a synthetic error result for truncated ones so the model re-emits them.
                    if (truncatedTools.Count > 0)
                    {
                        var truncatedNames = string.Join(", ", truncatedTools.Select(t => t.Name ?? "unknown"));
                        _logger.LogWarning("Agent {Name} response truncated, lost tool calls: {Tools}",
                            config.Name, truncatedNames);

                        var successfulTools = toolUses.Where(t => !t.Truncated).ToList();

                        if (successfulTools.Count > 0)
                        {
                            // Execute the successful tools and ask the model to re-emit the truncated ones.
                            if (textParts.Count > 0)
                                await emit(new AgentEvent.Message(currentStepId, string.Join("\n", textParts), IsIntermediate: true));

                            var assistantContent = new List<object>();
                            foreach (var tp in textParts)
                                assistantContent.Add(new { type = "text", text = tp });

                            var placeholderInput = JsonDocument.Parse("{}").RootElement.Clone();
                            foreach (var tu in toolUses)
                                assistantContent.Add(new
                                {
                                    type = "tool_use",
                                    id = tu.Id,
                                    name = tu.Name,
                                    input = tu.Truncated ? (object)placeholderInput : (object)(tu.Input ?? placeholderInput),
                                });

                            messages.Add(new LlmMessage
                            {
                                Role = "assistant",
                                Content = JsonSerializer.SerializeToElement(assistantContent),
                            });

                            var toolResults = new List<object>();
                            var bufferedInbox = new List<LlmMessage>();

                            foreach (var tu in toolUses)
                            {
                                if (tu.Truncated)
                                {
                                    toolResults.Add(new
                                    {
                                        type = "tool_result",
                                        tool_use_id = tu.Id,
                                        content = "[system] Your tool call was cut off before the input was complete. Call this tool again.",
                                    });
                                    continue;
                                }

                                toolCallCount++;
                                var toolStepId = $"step-{++stepId}";
                                var toolName = tu.Name ?? "unknown";
                                var toolInput = tu.Input ?? default;
                                var displayCmd = FormatDisplayCommand(toolName, toolInput);

                                _logger.LogInformation("Agent {Agent} executing tool {Tool} at step {Step}: {Command}",
                                    config.Name, toolName, toolStepId, displayCmd);

                                await emit(new AgentEvent.ToolCall(toolStepId, toolName, displayCmd, toolInput));
                                var result = await executeTool(toolName, toolInput, toolStepId, ct);
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

                            truncationRetries++;
                            consecutiveTruncationFallthroughs = 0;
                            thinkingBudgetOverride = ReducedThinkingBudget(config);
                            continue;
                        }

                        // No successful tools to preserve -- retry with text-only save.
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
                            thinkingBudgetOverride = ReducedThinkingBudget(config);
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

                            await CompactMessagesIfNeededAsync(messages, 0, config, currentStepId, emit, ct);
                            truncationRetries = 0;
                            consecutiveTruncationFallthroughs = 0;
                            thinkingBudgetOverride = null;
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
                        thinkingBudgetOverride = null;
                    }

                    // Case 1: conclude
                    if (concludeCall is not null)
                    {
                        var result = await executeTool("conclude", concludeCall.Input ?? default, currentStepId, ct);

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

                        var concludeContent = new List<object>();
                        foreach (var tp in textParts)
                            concludeContent.Add(new { type = "text", text = tp });
                        concludeContent.Add(new { type = "tool_use", id = concludeCall.Id, name = "conclude", input = concludeCall.Input });

                        messages.Add(new LlmMessage
                        {
                            Role = "assistant",
                            Content = JsonSerializer.SerializeToElement(concludeContent),
                        });
                        messages.Add(new LlmMessage
                        {
                            Role = "user",
                            Content = JsonSerializer.SerializeToElement(new[]
                            {
                                new { type = "tool_result", tool_use_id = concludeCall.Id, content = result.Output }
                            }),
                        });

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

                            var result = await executeTool(toolName, toolInput, toolStepId, ct);

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
                {
                    toolCallCount = 0;
                    await emit(new AgentEvent.StatusChanged($"step-{++stepId}", false));
                    continue;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Agent {Name} cancelled", config.Name);
        }

        _logger.LogInformation("Agent {Name} loop exited", config.Name);
    }

    private async Task<List<ContentBlock>> CallLlmWithRetry(
        Config config, List<LlmMessage> messages, CancellationToken ct,
        int? thinkingBudgetOverride = null, LlmRequestContext? context = null)
    {
        Exception? lastEx = null;

        for (var attempt = 0; attempt <= config.MaxRetries; attempt++)
        {
            try
            {
                var blocks = new List<ContentBlock>();
                await foreach (var block in config.LlmClient.StreamMessageAsync(
                    messages, config.Tools, config.SystemPrompt, ct, thinkingBudgetOverride, context))
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

    private static int ReducedThinkingBudget(Config config) =>
        Math.Max(1024, config.ThinkingBudget / 4);

    private static bool IsTransientHttpError(HttpRequestException ex)
    {
        if (ex.StatusCode is null) return true;
        var code = (int)ex.StatusCode;
        return code == 429 || code >= 500;
    }

    private async Task CompactMessagesIfNeededAsync(
        List<LlmMessage> messages, int maxTokenBudget, Config config,
        string stepId, Func<AgentEvent, ValueTask> emit, CancellationToken ct)
    {
        var tokensBefore = EstimateTokenCount(messages);

        if (tokensBefore < maxTokenBudget * 0.8)
            return;

        var keepRecent = 6;
        if (messages.Count <= keepRecent + 1)
            return;

        _logger.LogInformation("Compacting message history: {Count} messages, ~{Tokens} estimated tokens (budget={Budget})",
            messages.Count, tokensBefore, maxTokenBudget);

        var compactEnd = messages.Count - keepRecent;

        while (compactEnd > 0 && IsToolBoundary(messages, compactEnd))
            compactEnd--;

        if (compactEnd <= 1)
            return;

        string summary;
        UsageInfo? compactionUsage = null;

        if (config.SummarizerClient is not null)
        {
            var historyParts = new List<string>();
            for (var i = 0; i < compactEnd; i++)
            {
                var msg = messages[i];
                var contentStr = msg.Content.ValueKind == JsonValueKind.String
                    ? msg.Content.GetString() ?? ""
                    : msg.Content.GetRawText();
                historyParts.Add($"[{msg.Role}]: {contentStr}");
            }
            var historyText = string.Join("\n", historyParts);

            var summarizerMessages = new List<LlmMessage>
            {
                new()
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement(historyText),
                }
            };

            var sb = new System.Text.StringBuilder();
            IReadOnlyList<ToolDefinition> noTools = [];
            await foreach (var block in config.SummarizerClient.StreamMessageAsync(
                summarizerMessages, noTools,
                "Summarise the following conversation history, preserving key findings, tool results, decisions, and any resource identifiers. Be concise but thorough.",
                ct))
            {
                if (block.Type == "text" && block.Text is not null)
                    sb.Append(block.Text);
                else if (block.Type == "usage" && block.Usage is not null)
                    compactionUsage = block.Usage;
            }

            summary = $"[Compacted: {compactEnd} earlier messages summarised by AI. Summary:\n{sb}\nFull conversation is preserved in transcript.jsonl in the workspace.]";
        }
        else
        {
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
            summary = $"[Compacted: {compactEnd} earlier messages summarised. Key exchanges:\n"
                + string.Join("\n", summaryParts)
                + "\nFull conversation is preserved in transcript.jsonl in the workspace.]";
        }

        messages.RemoveRange(0, compactEnd);
        messages.Insert(0, new LlmMessage
        {
            Role = "user",
            Content = JsonSerializer.SerializeToElement(summary),
        });

        var tokensAfter = EstimateTokenCount(messages);

        _logger.LogInformation("Compacted {Removed} messages: ~{Before} -> ~{After} tokens, {Remaining} messages remain",
            compactEnd, tokensBefore, tokensAfter, messages.Count);

        decimal costDelta = 0;
        if (compactionUsage is not null && config.SummarizerModelOptions is { } smo)
        {
            costDelta = ComputeCost(compactionUsage, smo.InputPricePerMToken, smo.OutputPricePerMToken,
                smo.CacheReadPricePerMToken, smo.CacheCreationPricePerMToken);
        }

        await emit(new AgentEvent.Compaction(stepId, config.Name, tokensBefore, tokensAfter,
            compactionUsage?.InputTokens ?? 0, compactionUsage?.OutputTokens ?? 0,
            compactionUsage?.CacheReadInputTokens ?? 0, compactionUsage?.CacheCreationInputTokens ?? 0,
            costDelta));
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

    internal static decimal ComputeCost(UsageInfo usage, decimal inputPrice, decimal outputPrice,
        decimal cacheReadPrice, decimal cacheCreationPrice)
    {
        return (usage.InputTokens * inputPrice
            + usage.OutputTokens * outputPrice
            + usage.CacheReadInputTokens * cacheReadPrice
            + usage.CacheCreationInputTokens * cacheCreationPrice) / 1_000_000m;
    }

    private static UsageInfo? ExtractUsage(List<ContentBlock> blocks)
    {
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            if (blocks[i].Type == "usage" && blocks[i].Usage is { } u)
                return u;
        }
        return null;
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
            "run_aws" => "aws " + Prop(input, "command"),
            "run_shell" => Prop(input, "command"),
            "ci_repo" => $"ci_repo {Prop(input, "repo")}({Prop(input, "action")})",
            "skills" => $"skills {Prop(input, "action")}" + OptProp(input, "query", " ") + OptProp(input, "name", " "),
            "delegate" => $"delegate {Prop(input, "role")}",
            "conclude" => Truncate($"conclude: {Prop(input, "summary")}", 80),
            "present_finding" => $"finding: {Prop(input, "title")}",
            "reply_to" => $"reply_to {Prop(input, "agent_name")}",
            "dismiss_scout" => $"dismiss_scout {Prop(input, "agent_name")}",
            "recall_scout" => $"recall_scout {Prop(input, "agent_name")}",
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
            case "run_aws":
                Add("cluster", Prop(input, "cluster"));
                Add("account", Prop(input, "account"));
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
