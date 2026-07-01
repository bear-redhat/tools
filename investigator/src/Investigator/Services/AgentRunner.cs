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
        int MaxToolCalls,
        int MaxRetries,
        string WorkspacePath,
        int? CompactionMaxTokens,
        Func<string, JsonElement, bool>? IsConditionallyTerminal = null,
        int ThinkingBudget = 10000,
        int ContextWindowTokens = 1_000_000,
        string? UserId = null,
        string? ConversationId = null,
        string? ModelProfile = null,
        decimal InputPricePerMToken = 0,
        decimal OutputPricePerMToken = 0,
        decimal CacheReadPricePerMToken = 0,
        decimal CacheCreationPricePerMToken = 0,
        ILlmClient? SummarizerClient = null,
        ModelOptions? SummarizerModelOptions = null,
        IReadOnlySet<string>? TerminalToolNames = null,
        string? TextOnlyNudge = null,
        Func<bool>? ShouldSuppressNextTurn = null);

    public record ToolExecutionResult(
        string Output,
        string? OutputFile = null,
        int ExitCode = 0,
        bool TimedOut = false,
        string? Summary = null);

    private const int MaxTruncationRetries = 2;
    private const int MaxTruncationFallthroughs = 3;

    private readonly ILogger _logger;

    public AgentRunner(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(
        Config config,
        ChannelReader<RoomEvent> inbox,
        Func<RoomEvent.LlmContext, ValueTask> store,
        Func<string, JsonElement, string, CancellationToken, Task<ToolExecutionResult>> executeTool,
        CancellationToken ct,
        List<LlmMessage>? initialMessages = null,
        bool autoResume = false)
    {
        var toolCallCount = 0;
        var messages = initialMessages ?? [];
        var terminalTools = config.TerminalToolNames ?? new HashSet<string> { "conclude" };

        RoomEvent.LlmContext MakeCtx(IReadOnlyList<LlmMessage> msgs, int removed = 0,
            UsageInfo? usage = null, string? thinkingText = null,
            bool isInboxBatch = false, bool isConcluded = false) =>
            new(0, config.Id, DateTimeOffset.UtcNow, msgs, removed, usage, thinkingText,
                config.ModelProfile,
                config.InputPricePerMToken, config.OutputPricePerMToken,
                config.CacheReadPricePerMToken, config.CacheCreationPricePerMToken,
                isInboxBatch, isConcluded);

        _logger.LogInformation("Agent {Name} ({Role}) starting, maxToolCalls={Max}{AutoResume}",
            config.Name, config.Role, config.MaxToolCalls, autoResume ? " [auto-resume]" : "");

        var skipFirstWait = autoResume && messages.Count > 0;

        try
        {
            while (skipFirstWait || await inbox.WaitToReadAsync(ct))
            {
                if (!skipFirstWait)
                {
                    var inboxBatch = new List<LlmMessage>();
                    while (inbox.TryRead(out var evt))
                    {
                        var msg = FormatEventAsLlmMessage(evt);
                        if (msg is not null) { messages.Add(msg); inboxBatch.Add(msg); }
                    }

                    if (inboxBatch.Count > 0 && config.ShouldSuppressNextTurn?.Invoke() == true)
                    {
                        messages.RemoveRange(messages.Count - inboxBatch.Count, inboxBatch.Count);
                        continue;
                    }

                    if (inboxBatch.Count > 0)
                        await store(MakeCtx(inboxBatch, isInboxBatch: true));
                }
                skipFirstWait = false;

                toolCallCount = 0;

                var concluded = false;
                var textOnlyRetries = 0;
                var hasBeenNudged = false;
                var truncationRetries = 0;
                var consecutiveTruncationFallthroughs = 0;
                int? thinkingBudgetOverride = null;
                var promptTooLongRetried = false;
                while (!concluded && !ct.IsCancellationRequested)
                {
                    _logger.LogDebug("Agent {Name} loop iteration, toolCallCount={Count}/{Max}",
                        config.Name, toolCallCount, config.MaxToolCalls);

                    var compactionBudget = config.CompactionMaxTokens
                        ?? (int)(config.ContextWindowTokens * 0.7);
                    await CompactMessagesIfNeededAsync(messages, compactionBudget, config, store, ct);

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
                        if (!promptTooLongRetried)
                        {
                            promptTooLongRetried = true;
                            var emergencyBudget = (int)(config.ContextWindowTokens * 0.5);
                            _logger.LogWarning("Agent {Name} LLM rejected with HTTP 400, emergency compaction to {Budget} tokens",
                                config.Name, emergencyBudget);
                            await CompactMessagesIfNeededAsync(messages, emergencyBudget, config, store, ct);
                            continue;
                        }

                        LogMessageStructure(config.Name, messages);
                        _logger.LogError(ex, "Agent {Name} LLM call rejected (HTTP 400)", config.Name);
                        llmError = $"LLM call rejected: {ex.Message}";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (HttpRequestException ex)
                    {
                        _logger.LogError(ex, "Agent {Name} LLM call failed (HTTP {Status})", config.Name, ex.StatusCode);
                        llmError = $"LLM call failed: {ex.Message}";
                    }
                    catch (IOException ex)
                    {
                        _logger.LogError(ex, "Agent {Name} LLM stream failed (IO error)", config.Name);
                        llmError = $"LLM stream error: {ex.Message}";
                    }

                    if (llmError is not null)
                    {
                        var errorMsg = new LlmMessage { Role = "user", Content = JsonSerializer.SerializeToElement($"[system error] {llmError}") };
                        messages.Add(errorMsg);
                        await store(MakeCtx([errorMsg]));
                        break;
                    }

                    var textParts = new List<string>();
                    var toolUses = new List<ContentBlock>();
                    ContentBlock? terminalCall = null;
                    var thinkingParts = new List<string>();

                    foreach (var block in contentBlocks!)
                    {
                        if (block.Type == "thinking" && !string.IsNullOrEmpty(block.Text))
                            thinkingParts.Add(block.Text);
                        else if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
                            textParts.Add(block.Text);
                        else if (block.Type == "tool_use" && block.Name is not null && terminalTools.Contains(block.Name))
                            terminalCall = block;
                        else if (block.Type == "tool_use")
                            toolUses.Add(block);
                    }

                    var truncatedTools = contentBlocks!.Where(b => b.Type == "tool_use" && b.Truncated).ToList();
                    var thinkingText = thinkingParts.Count > 0 ? string.Join("\n", thinkingParts) : null;

                    _logger.LogDebug("Agent {Name} LLM returned {TextParts} text, {ToolUses} tools, terminal={TerminalTool}, truncated={TruncatedCount}",
                        config.Name, textParts.Count, toolUses.Count, terminalCall?.Name, truncatedTools.Count);

                    // Case 0: output was truncated
                    if (truncatedTools.Count > 0)
                    {
                        var truncatedNames = string.Join(", ", truncatedTools.Select(t => t.Name));
                        _logger.LogWarning("Agent {Name} response truncated, lost tool calls: {Tools}", config.Name, truncatedNames);

                        var successfulTools = toolUses.Where(t => !t.Truncated).ToList();

                        if (successfulTools.Count > 0)
                        {
                            var assistantContent = new List<object>();
                            foreach (var tp in textParts) assistantContent.Add(new { type = "text", text = tp });
                            var placeholderInput = JsonDocument.Parse("{}").RootElement.Clone();
                            foreach (var tu in toolUses)
                                assistantContent.Add(new { type = "tool_use", id = tu.Id, name = tu.Name, input = tu.Truncated ? (object)placeholderInput : (object)(tu.Input ?? placeholderInput) });

                            var assistantMsg = new LlmMessage { Role = "assistant", Content = JsonSerializer.SerializeToElement(assistantContent) };
                            messages.Add(assistantMsg);
                            await store(MakeCtx([assistantMsg], thinkingText: thinkingText, usage: usageInfo));

                            var toolResults = new List<object>();
                            var bufferedInbox = new List<LlmMessage>();

                            foreach (var tu in toolUses)
                            {
                                if (tu.Truncated)
                                {
                                    toolResults.Add(new { type = "tool_result", tool_use_id = tu.Id, content = "[system] Your tool call was cut off before the input was complete. Call this tool again." });
                                    continue;
                                }

                                if (tu.Name is null || tu.Id is null) { _logger.LogError("Tool use block missing name or id, skipping"); continue; }
                                toolCallCount++;
                                var toolName = tu.Name;
                                var toolInput = tu.Input ?? default;

                                _logger.LogInformation("Agent {Agent} executing tool {Tool}: {Command}", config.Name, toolName, FormatDisplayCommand(toolName, toolInput));

                                var result = await executeTool(toolName, toolInput, tu.Id, ct);
                                toolResults.Add(new { type = "tool_result", tool_use_id = tu.Id, content = result.Output });
                                while (inbox.TryRead(out var inboxEvt)) { var m = FormatEventAsLlmMessage(inboxEvt); if (m is not null) bufferedInbox.Add(m); }
                            }

                            var toolResultMsg = new LlmMessage { Role = "user", Content = JsonSerializer.SerializeToElement(toolResults) };
                            messages.Add(toolResultMsg);
                            messages.AddRange(bufferedInbox);
                            var contextBatch = new List<LlmMessage> { toolResultMsg };
                            contextBatch.AddRange(bufferedInbox);
                            await store(MakeCtx(contextBatch));

                            truncationRetries++;
                            consecutiveTruncationFallthroughs = 0;
                            thinkingBudgetOverride = ReducedThinkingBudget(config);
                            continue;
                        }

                        truncationRetries++;
                        if (truncationRetries <= MaxTruncationRetries)
                        {
                            if (textParts.Count > 0)
                            {
                                var savedMsg = new LlmMessage { Role = "assistant", Content = JsonSerializer.SerializeToElement(string.Join("\n", textParts)) };
                                messages.Add(savedMsg);
                                await store(MakeCtx([savedMsg], thinkingText: thinkingText, usage: usageInfo));
                            }

                            var retryMsg = new LlmMessage
                            {
                                Role = "user",
                                Content = JsonSerializer.SerializeToElement(
                                    $"Your response was cut off before the tool call{(truncatedTools.Count > 1 ? "s" : "")} could complete. " +
                                    $"Your text above is saved. Now continue with ONLY the tool call{(truncatedTools.Count > 1 ? "s" : "")} " +
                                    $"({truncatedNames}) — emit the tool_use block{(truncatedTools.Count > 1 ? "s" : "")} with no additional text."),
                            };
                            messages.Add(retryMsg);
                            await store(MakeCtx([retryMsg]));
                            thinkingBudgetOverride = ReducedThinkingBudget(config);
                            continue;
                        }

                        _logger.LogWarning("Agent {Name} exhausted {Max} truncation retries, falling through", config.Name, MaxTruncationRetries);
                        consecutiveTruncationFallthroughs++;

                        if (consecutiveTruncationFallthroughs >= MaxTruncationFallthroughs)
                        {
                            _logger.LogWarning("Agent {Name} hit {Count} consecutive truncation fallthroughs, forcing compaction", config.Name, consecutiveTruncationFallthroughs);
                            await CompactMessagesIfNeededAsync(messages, 0, config, store, ct);
                            truncationRetries = 0;
                            consecutiveTruncationFallthroughs = 0;
                            thinkingBudgetOverride = null;
                            continue;
                        }

                        var emptyInput = JsonDocument.Parse("{}").RootElement.Clone();
                        foreach (var t in truncatedTools) { t.Input = emptyInput; t.Truncated = false; }
                    }

                    if (truncatedTools.Count == 0) { truncationRetries = 0; consecutiveTruncationFallthroughs = 0; thinkingBudgetOverride = null; }

                    // Case 1: terminal tool (conclude, sign_off, etc.)
                    if (terminalCall is not null)
                    {
                        textOnlyRetries = 0;
                        var terminalName = terminalCall.Name!;

                        _logger.LogInformation("Agent {Agent} executing terminal tool {Tool}", config.Name, terminalName);

                        var result = await executeTool(terminalName, terminalCall.Input ?? default, terminalCall.Id!, ct);

                        var terminalContent = new List<object>();
                        foreach (var tp in textParts) terminalContent.Add(new { type = "text", text = tp });
                        terminalContent.Add(new { type = "tool_use", id = terminalCall.Id, name = terminalName, input = terminalCall.Input });

                        var aMsg = new LlmMessage { Role = "assistant", Content = JsonSerializer.SerializeToElement(terminalContent) };
                        var toolResultContent = JsonSerializer.SerializeToElement(new[] { new { type = "tool_result", tool_use_id = terminalCall.Id, content = result.Output } });
                        LlmMessage tMsg = result.Summary is not null
                            ? new LlmToolResultMessage { Role = "user", Content = toolResultContent, ToolMeta = [new ToolCallMeta { ToolUseId = terminalCall.Id!, Summary = result.Summary, ExitCode = result.ExitCode, OutputFile = result.OutputFile, TimedOut = result.TimedOut }] }
                            : new LlmMessage { Role = "user", Content = toolResultContent };
                        messages.Add(aMsg);
                        messages.Add(tMsg);
                        await store(MakeCtx([aMsg, tMsg], thinkingText: thinkingText, usage: usageInfo));

                        concluded = true;
                        break;
                    }

                    // Case 2: tool calls
                    if (toolUses.Count > 0)
                    {
                        textOnlyRetries = 0;
                        hasBeenNudged = false;

                        var assistantContent = new List<object>();
                        foreach (var tp in textParts) assistantContent.Add(new { type = "text", text = tp });
                        foreach (var tu in toolUses) assistantContent.Add(new { type = "tool_use", id = tu.Id, name = tu.Name, input = tu.Input });

                        var assistantMsg = new LlmMessage { Role = "assistant", Content = JsonSerializer.SerializeToElement(assistantContent) };
                        messages.Add(assistantMsg);
                        await store(MakeCtx([assistantMsg], thinkingText: thinkingText, usage: usageInfo));

                        var toolResults = new List<object>();
                        var bufferedInbox = new List<LlmMessage>();
                        var anyToolConcluded = false;

                        foreach (var tu in toolUses)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (tu.Name is null || tu.Id is null) { _logger.LogError("Tool use block missing name or id, skipping"); continue; }
                            toolCallCount++;
                            var toolName = tu.Name;
                            var toolInput = tu.Input ?? default;

                            _logger.LogInformation("Agent {Agent} executing tool {Tool}: {Command}", config.Name, toolName, FormatDisplayCommand(toolName, toolInput));

                            var result = await executeTool(toolName, toolInput, tu.Id, ct);

                            toolResults.Add(new { type = "tool_result", tool_use_id = tu.Id, content = result.Output });
                            if (config.IsConditionallyTerminal?.Invoke(toolName, toolInput) == true)
                                anyToolConcluded = true;
                            while (inbox.TryRead(out var inboxEvt)) { var m = FormatEventAsLlmMessage(inboxEvt); if (m is not null) bufferedInbox.Add(m); }
                        }

                        var toolResultMsg = new LlmMessage { Role = "user", Content = JsonSerializer.SerializeToElement(toolResults) };
                        messages.Add(toolResultMsg);
                        messages.AddRange(bufferedInbox);
                        var ctxBatch = new List<LlmMessage> { toolResultMsg };
                        ctxBatch.AddRange(bufferedInbox);
                        await store(MakeCtx(ctxBatch, isConcluded: anyToolConcluded));

                        if (anyToolConcluded)
                        {
                            concluded = true;
                            break;
                        }

                        if (toolCallCount >= config.MaxToolCalls)
                        {
                            _logger.LogWarning("Agent {Name} max tool calls ({Max}) reached, forcing conclusion", config.Name, config.MaxToolCalls);
                            var forceMsg = new LlmMessage { Role = "user", Content = JsonSerializer.SerializeToElement($"You have used all {config.MaxToolCalls} tool calls. Call the conclude tool now with your best conclusion.") };
                            messages.Add(forceMsg);
                            await store(MakeCtx([forceMsg]));
                        }

                        continue;
                    }

                    // Case 3: text only -- discard, nudge, retry, synthesize
                    textOnlyRetries++;
                    _logger.LogWarning("Agent {Name} text-only response ({Count}, nudged={Nudged}), retrying LLM",
                        config.Name, textOnlyRetries, hasBeenNudged);

                    if (hasBeenNudged)
                    {
                        _logger.LogWarning("Agent {Name} still text-only after nudge, synthesizing message tool call", config.Name);
                        var fallbackText = string.Join("\n", textParts);
                        if (string.IsNullOrWhiteSpace(fallbackText)) fallbackText = "(no response)";

                        var syntheticId = $"toolu_synth_{Guid.NewGuid():N}";
                        var msgInput = JsonSerializer.SerializeToElement(new { to = "user", text = fallbackText });

                        var syntheticAssistant = new LlmMessage { Role = "assistant", Content = JsonSerializer.SerializeToElement(new object[] {
                            new { type = "tool_use", id = syntheticId, name = "message", input = msgInput }
                        })};

                        var result = await executeTool("message", msgInput, syntheticId, ct);

                        var syntheticResult = new LlmMessage { Role = "user", Content = JsonSerializer.SerializeToElement(new[] {
                            new { type = "tool_result", tool_use_id = syntheticId, content = result.Output }
                        })};

                        messages.Add(syntheticAssistant);
                        messages.Add(syntheticResult);
                        await store(MakeCtx([syntheticAssistant, syntheticResult], thinkingText: thinkingText, usage: usageInfo, isConcluded: true));

                        concluded = true;
                        break;
                    }

                    if (thinkingText is not null || usageInfo is not null)
                        await store(MakeCtx([], thinkingText: thinkingText, usage: usageInfo));

                    var nudge = new LlmMessage { Role = "user",
                        Content = JsonSerializer.SerializeToElement(config.TextOnlyNudge
                            ?? "You must use a tool call. Do not respond with text alone.") };
                    messages.Add(nudge);
                    await store(MakeCtx([nudge]));
                    hasBeenNudged = true;
                    continue;
                }

                if (concluded)
                {
                    toolCallCount = 0;
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
                {
                    ct.ThrowIfCancellationRequested();
                    blocks.Add(block);
                }
                return blocks;
            }
            catch (HttpRequestException ex) when (attempt < config.MaxRetries && IsTransientHttpError(ex))
            {
                lastEx = ex;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(ex, "Agent {Name} LLM call failed (attempt {Attempt}/{Max}), retrying in {Delay}s",
                    config.Name, attempt + 1, config.MaxRetries + 1, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
        throw lastEx ?? new InvalidOperationException("LLM call failed with no exception captured");
    }

    private static int ReducedThinkingBudget(Config config) => Math.Max(1024, config.ThinkingBudget / 4);
    private static bool IsTransientHttpError(HttpRequestException ex) { if (ex.StatusCode is null) return true; var code = (int)ex.StatusCode; return code == 429 || code >= 500; }

    private async Task CompactMessagesIfNeededAsync(
        List<LlmMessage> messages, int maxTokenBudget, Config config,
        Func<RoomEvent.LlmContext, ValueTask> store, CancellationToken ct)
    {
        var tokensBefore = EstimateTokenCount(messages);
        if (tokensBefore < maxTokenBudget * 0.8) return;

        var keepRecent = 6;
        if (messages.Count <= keepRecent + 1) return;

        _logger.LogInformation("Compacting: {Count} messages, ~{Tokens} tokens (budget={Budget})", messages.Count, tokensBefore, maxTokenBudget);

        var compactEnd = messages.Count - keepRecent;
        while (compactEnd > 0 && IsToolBoundary(messages, compactEnd)) compactEnd--;
        if (compactEnd <= 1) return;

        string summary;
        UsageInfo? compactionUsage = null;

        if (config.SummarizerClient is not null)
        {
            var historyParts = new List<string>();
            for (var i = 0; i < compactEnd; i++)
            {
                var msg = messages[i];
                var contentStr = msg.Content.ValueKind == JsonValueKind.String ? msg.Content.GetString() ?? msg.Content.GetRawText() : msg.Content.GetRawText();
                historyParts.Add($"[{msg.Role}]: {contentStr}");
            }

            var summarizerMessages = new List<LlmMessage> { new() { Role = "user", Content = JsonSerializer.SerializeToElement(string.Join("\n", historyParts)) } };
            var sb = new System.Text.StringBuilder();
            IReadOnlyList<ToolDefinition> noTools = [];
            await foreach (var block in config.SummarizerClient.StreamMessageAsync(summarizerMessages, noTools,
                "Summarise the following conversation history, preserving key findings, tool results, decisions, and any resource identifiers. Be concise but thorough.", ct))
            {
                if (block.Type == "text" && block.Text is not null) sb.Append(block.Text);
                else if (block.Type == "usage" && block.Usage is not null) compactionUsage = block.Usage;
            }

            summary = $"[Compacted: {compactEnd} earlier messages summarised by AI. Summary:\n{sb}\nFull conversation is preserved in the session transcript.]";
        }
        else
        {
            var summaryParts = new List<string>();
            for (var i = 0; i < compactEnd; i++)
            {
                var msg = messages[i];
                var contentStr = msg.Content.ValueKind == JsonValueKind.String ? msg.Content.GetString() ?? msg.Content.GetRawText() : msg.Content.GetRawText();
                if (contentStr.Length > 200) contentStr = contentStr[..200] + "...";
                summaryParts.Add($"[{msg.Role}]: {contentStr}");
            }
            summary = $"[Compacted: {compactEnd} earlier messages summarised. Key exchanges:\n{string.Join("\n", summaryParts)}\nFull conversation is preserved in the session transcript.]";
        }

        messages.RemoveRange(0, compactEnd);
        var summaryMsg = new LlmMessage { Role = "user", Content = JsonSerializer.SerializeToElement(summary) };
        messages.Insert(0, summaryMsg);

        var tokensAfter = EstimateTokenCount(messages);
        _logger.LogInformation("Compacted {Removed} messages: ~{Before} -> ~{After} tokens", compactEnd, tokensBefore, tokensAfter);

        await store(new RoomEvent.LlmContext(0, config.Id, DateTimeOffset.UtcNow,
            [summaryMsg], Removed: compactEnd, Usage: compactionUsage,
            ModelProfile: config.ModelProfile,
            InputPrice: config.InputPricePerMToken, OutputPrice: config.OutputPricePerMToken,
            CacheReadPrice: config.CacheReadPricePerMToken, CacheCreatePrice: config.CacheCreationPricePerMToken));
    }

    private static bool IsToolBoundary(List<LlmMessage> messages, int index)
    {
        if (index >= messages.Count) return false;
        var msg = messages[index];
        if (msg.Role == "user" && msg.Content.ValueKind == JsonValueKind.Array)
            foreach (var item in msg.Content.EnumerateArray())
                if (item.TryGetProperty("type", out var t) && t.GetString() == "tool_result") return true;
        if (index > 0)
        {
            var prev = messages[index - 1];
            if (prev.Role == "assistant" && prev.Content.ValueKind == JsonValueKind.Array)
                foreach (var item in prev.Content.EnumerateArray())
                    if (item.TryGetProperty("type", out var t) && t.GetString() == "tool_use") return true;
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
            if (blocks[i].Type == "usage" && blocks[i].Usage is { } u) return u;
        return null;
    }

    private static int EstimateTokenCount(List<LlmMessage> messages)
    {
        var totalChars = 0;
        foreach (var msg in messages)
            totalChars += msg.Content.ValueKind == JsonValueKind.String ? msg.Content.GetString()?.Length ?? 0 : msg.Content.GetRawText().Length;
        return totalChars / 4;
    }

    private static LlmMessage? FormatEventAsLlmMessage(RoomEvent evt)
    {
        var (text, from, to) = evt switch
        {
            RoomEvent.TextMessage { From: "user" } tm => (tm.Text, "user", tm.To),
            RoomEvent.TextMessage { From: "system" } tm => (tm.Text, "system", tm.To),
            RoomEvent.TextMessage tm => ($"[{tm.From}]: {tm.Text}", tm.From, tm.To),
            _ => ((string?)null, (string?)null, (string?)null),
        };
        if (text is null) return null;
        return new LlmInboxMessage
        {
            Role = "user",
            Content = JsonSerializer.SerializeToElement(text),
            SourceFrom = from!,
            SourceTo = to,
        };
    }

    internal static string? FormatDisplayCommand(string toolName, JsonElement input)
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
            "message" => $"message {Prop(input, "to")}",
            "dismiss" => $"dismiss {Prop(input, "agent_name")}",
            "recall" => $"recall {Prop(input, "agent_name")}",
            "memory" => FormatMemoryCommand(input),
            _ => FormatGenericTool(toolName, input),
        };
    }

    internal static Dictionary<string, string>? ExtractContext(string toolName, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        Dictionary<string, string>? ctx = null;
        void Add(string key, string? value) { if (!string.IsNullOrEmpty(value)) { ctx ??= new(); ctx[key] = value; } }
        switch (toolName)
        {
            case "run_oc": Add("cluster", Prop(input, "cluster")); break;
            case "run_aws": Add("cluster", Prop(input, "cluster")); Add("account", Prop(input, "account")); break;
            case "delegate": Add("model", Prop(input, "model")); break;
            case "skills": Add("action", Prop(input, "action")); break;
            case "memory": Add("action", Prop(input, "action")); Add("category", Prop(input, "category")); break;
        }
        return ctx;
    }

    private static string FormatMemoryCommand(JsonElement input)
    {
        var action = Prop(input, "action");
        if (action is null) return "memory";
        var detail = action switch
        {
            "save" => Prop(input, "title") is { } t ? $" \"{Truncate(t, 60)}\"" : null,
            "search" => Prop(input, "query") is { } q ? $" \"{Truncate(q, 60)}\"" : null,
            "read" or "delete" => Prop(input, "id") is { } id ? $" {id}" : null,
            _ => null,
        };
        return $"memory {action}{detail}";
    }

    private static string? Prop(JsonElement input, string name) => input.TryGetProperty(name, out var v) ? v.GetString() : null;
    private static string OptProp(JsonElement input, string name, string prefix) => input.TryGetProperty(name, out var v) && !string.IsNullOrEmpty(v.GetString()) ? prefix + v.GetString() : "";
    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..(maxLength - 1)] + "\u2026";

    private static string FormatGenericTool(string toolName, JsonElement input)
    {
        var parts = new List<string> { toolName };
        foreach (var prop in input.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String) { var val = prop.Value.GetString(); if (val is null) continue; if (val.Length > 40) val = val[..39] + "\u2026"; parts.Add($"{prop.Name}={val}"); if (parts.Count >= 4) break; }
        }
        return string.Join(" ", parts);
    }
}
