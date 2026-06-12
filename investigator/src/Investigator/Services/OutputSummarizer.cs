using System.Text;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;

namespace Investigator.Services;

public sealed class OutputSummarizer
{
    private readonly ILlmClient? _client;
    private readonly ILogger<OutputSummarizer> _logger;

    private const int MaxInputChars = 4000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public OutputSummarizer(ILlmClientFactory llmFactory, ILogger<OutputSummarizer> logger)
    {
        _logger = logger;
        _client = llmFactory.GetClient(
            llmFactory.Models.ContainsKey("summarizer") ? "summarizer" : llmFactory.DefaultProfileName);
    }

    public async Task<string?> SummarizeAsync(string fullOutput, CancellationToken ct)
    {
        if (_client is null || fullOutput.Length == 0) return null;

        var input = fullOutput.Length > MaxInputChars ? fullOutput[..MaxInputChars] : fullOutput;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            var messages = new List<LlmMessage> { new() { Role = "user", Content = JsonSerializer.SerializeToElement(input) } };
            IReadOnlyList<ToolDefinition> noTools = [];
            var sb = new StringBuilder();
            await foreach (var block in _client.StreamMessageAsync(messages, noTools,
                "Summarise the key information from this tool output in 2-3 sentences. Focus on facts, errors, and notable values. Output only the summary.",
                cts.Token))
            {
                if (block.Type == "text" && block.Text is not null) sb.Append(block.Text);
            }
            return sb.Length > 0 ? sb.ToString().Trim() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Output summarisation skipped");
            return null;
        }
    }

    public static string InsertSummary(string truncatedOutput, string summary)
    {
        var newlineIdx = truncatedOutput.IndexOf('\n');
        if (newlineIdx < 0)
            return truncatedOutput + $"\n\n[summary: {summary}]";
        return truncatedOutput[..(newlineIdx + 1)] + $"\n[summary: {summary}]\n" + truncatedOutput[(newlineIdx + 1)..];
    }
}
