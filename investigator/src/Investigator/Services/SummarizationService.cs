using System.Text;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public sealed class SummarizationService
{
    private readonly ILlmClient _client;
    private readonly ILogger<SummarizationService> _logger;

    private static readonly IReadOnlyList<ToolDefinition> s_noTools = [];

    public SummarizationService(ILlmClientFactory factory, IOptions<LlmOptions> llmOptions, ILogger<SummarizationService> logger)
    {
        var profileName = llmOptions.Value.Summarizer ?? factory.DefaultProfileName;
        _client = factory.GetClient(profileName);
        _logger = logger;
        logger.LogInformation("SummarizationService using profile '{Profile}'", profileName);
    }

    public Task<string> SummarizeToOneLineAsync(string content, CancellationToken ct) =>
        SummarizeAsync(content, "Summarise the following into a single concise sentence (max ~120 characters). Return ONLY the summary, nothing else.", ct);

    public Task<string> SummarizeToFewLinesAsync(string content, CancellationToken ct) =>
        SummarizeAsync(content, "Summarise the following report into 2-3 concise sentences capturing the key findings. Return ONLY the summary, nothing else.", ct);

    public Task<string> SummarizeToHeadlineAsync(string content, CancellationToken ct) =>
        SummarizeAsync(content, "Summarise the following into a terse headline of 3-8 words identifying the root cause and any key identifiers (resource IDs, hostnames, error codes). Return ONLY the headline, nothing else.", ct);

    private async Task<string> SummarizeAsync(string content, string systemPrompt, CancellationToken ct)
    {
        var messages = new List<LlmMessage>
        {
            new()
            {
                Role = "user",
                Content = JsonSerializer.SerializeToElement(content),
            }
        };

        var sb = new StringBuilder();

        await foreach (var block in _client.StreamMessageAsync(messages, s_noTools, systemPrompt, ct))
        {
            if (block.Type == "text" && block.Text is not null)
                sb.Append(block.Text);
        }

        var result = sb.ToString().Trim();
        _logger.LogDebug("Summarised {InputLen} chars -> {OutputLen} chars", content.Length, result.Length);
        return result;
    }
}
