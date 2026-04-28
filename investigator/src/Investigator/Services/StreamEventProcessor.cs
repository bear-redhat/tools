using System.Text;
using System.Text.Json;
using Investigator.Models;

namespace Investigator.Services;

internal sealed class StreamEventProcessor
{
    private readonly Dictionary<int, ContentBlock> _blocks = new();
    private readonly Dictionary<int, StringBuilder> _jsonAccumulators = new();
    private readonly ILogger _logger;
    private UsageInfo? _accumulatedUsage;

    public StreamEventProcessor(ILogger logger)
    {
        _logger = logger;
    }

    public IEnumerable<ContentBlock> ProcessEvent(StreamEvent evt)
    {
        switch (evt.Type)
        {
            case "content_block_start" when evt.ContentBlock is not null:
                var idx = evt.Index ?? 0;
                _blocks[idx] = evt.ContentBlock;
                if (evt.ContentBlock.Type == "tool_use")
                    _jsonAccumulators[idx] = new StringBuilder();
                _logger.LogTrace("content_block_start: index={Index}, type={Type}", idx, evt.ContentBlock.Type);
                break;

            case "content_block_delta" when evt.Delta is not null:
                var deltaIdx = evt.Index ?? 0;
                if (_blocks.TryGetValue(deltaIdx, out var block))
                {
                    if (evt.Delta.Type == "text_delta" && evt.Delta.Text is not null)
                        block.Text = (block.Text ?? "") + evt.Delta.Text;
                    else if (evt.Delta.Type == "thinking_delta" && evt.Delta.Thinking is not null)
                        block.Text = (block.Text ?? "") + evt.Delta.Thinking;
                    else if (evt.Delta.Type == "input_json_delta" && evt.Delta.PartialJson is not null)
                        if (_jsonAccumulators.TryGetValue(deltaIdx, out var acc))
                            acc.Append(evt.Delta.PartialJson);
                }
                else
                {
                    _logger.LogWarning("content_block_delta for unknown index {Index}", deltaIdx);
                }
                break;

            case "content_block_stop":
                var stopIdx = evt.Index ?? 0;
                if (_blocks.TryGetValue(stopIdx, out var completed))
                {
                    if (completed.Type == "tool_use" && _jsonAccumulators.TryGetValue(stopIdx, out var jsonAcc))
                    {
                        var rawJson = jsonAcc.ToString();
                        if (string.IsNullOrEmpty(rawJson))
                        {
                            completed.Input ??= JsonDocument.Parse("{}").RootElement.Clone();
                        }
                        else
                        {
                            try
                            {
                                completed.Input = JsonDocument.Parse(rawJson).RootElement.Clone();
                            }
                            catch (JsonException ex)
                            {
                                _logger.LogWarning(ex, "Failed to parse tool input JSON at index {Index}, marking as truncated. Raw length: {RawLength}", stopIdx, rawJson.Length);
                                completed.Truncated = true;
                                completed.Input = null;
                            }
                        }
                    }
                    yield return completed;
                    _blocks.Remove(stopIdx);
                    _jsonAccumulators.Remove(stopIdx);
                }
                else
                {
                    _logger.LogWarning("content_block_stop for unknown index {Index}", stopIdx);
                }
                break;

            case "message_start":
                _logger.LogDebug("message_start: id={Id}", evt.Message?.Id);
                if (evt.Message?.Usage is { } startUsage)
                {
                    _accumulatedUsage ??= new UsageInfo();
                    _accumulatedUsage.InputTokens += startUsage.InputTokens;
                    _accumulatedUsage.CacheCreationInputTokens += startUsage.CacheCreationInputTokens;
                    _accumulatedUsage.CacheReadInputTokens += startUsage.CacheReadInputTokens;
                }
                break;

            case "message_delta":
                var stopReason = evt.Delta?.StopReason;
                var deltaUsage = evt.Usage ?? evt.Message?.Usage;
                if (stopReason == "max_tokens")
                    _logger.LogWarning("message_delta: stop_reason={StopReason} (output truncated), output_tokens={OutputTokens}",
                        stopReason, deltaUsage?.OutputTokens);
                else
                    _logger.LogDebug("message_delta: stop_reason={StopReason}, output_tokens={OutputTokens}",
                        stopReason, deltaUsage?.OutputTokens);

                if (deltaUsage is not null)
                {
                    _accumulatedUsage ??= new UsageInfo();
                    _accumulatedUsage.OutputTokens += deltaUsage.OutputTokens;
                    _accumulatedUsage.CacheCreationInputTokens += deltaUsage.CacheCreationInputTokens;
                    _accumulatedUsage.CacheReadInputTokens += deltaUsage.CacheReadInputTokens;
                }

                if (_accumulatedUsage is not null)
                {
                    yield return new ContentBlock { Type = "usage", Usage = _accumulatedUsage };
                }
                break;

            case "message_stop":
                _logger.LogDebug("message_stop");
                break;

            case "ping":
                break;

            default:
                _logger.LogDebug("Unhandled stream event type: {Type}", evt.Type);
                break;
        }
    }
}
