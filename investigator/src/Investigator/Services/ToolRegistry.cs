using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, IInvestigatorTool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ToolRegistry> _logger;
    private readonly ToolOutputOptions _options;

    public ToolRegistry(ILogger<ToolRegistry> logger, IOptions<ToolOutputOptions> toolOutputOptions)
    {
        _logger = logger;
        _options = toolOutputOptions.Value;
    }

    public void Register(IInvestigatorTool tool)
    {
        _tools[tool.Definition.Name] = tool;
        _logger.LogInformation("Registered tool: {Name} (timeout={Timeout}s, truncate={Truncate})",
            tool.Definition.Name, tool.Definition.DefaultTimeout.TotalSeconds, tool.Definition.TruncateOutput);
    }

    public IReadOnlyList<ToolDefinition> GetToolDefinitions() =>
        _tools.Values.Select(t => t.Definition).ToList();

    public async Task<(ToolResult Result, string? OutputFile, string TruncatedOutput)> InvokeAsync(
        string toolName,
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        if (!_tools.TryGetValue(toolName, out var tool))
        {
            _logger.LogError("Unknown tool requested: {Name}. Available: {Available}",
                toolName, string.Join(", ", _tools.Keys));
            return (new ToolResult($"Unknown tool: {toolName}", ExitCode: 1), null, $"Unknown tool: {toolName}");
        }

        var def = tool.Definition;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(def.DefaultTimeout);

        ToolResult result;
        try
        {
            result = await tool.InvokeAsync(parameters, context, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Tool {Name} timed out after {Timeout}s", toolName, def.DefaultTimeout.TotalSeconds);
            result = new ToolResult($"[Timed out after {def.DefaultTimeout.TotalSeconds}s]", ExitCode: -1, TimedOut: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Tool {Name} threw an unexpected exception", toolName);
            result = new ToolResult($"Tool error: {ex.Message}", ExitCode: -1);
        }

        var outputNum = context.NextOutputNumber();
        var fileName = $"{outputNum:D3}-{toolName}.txt";
        var outputDir = Path.Combine(context.WorkspacePath, "tool_outputs");

        string? outputFilePath = null;
        try
        {
            Directory.CreateDirectory(outputDir);
            var fullPath = Path.Combine(outputDir, fileName);
            await File.WriteAllTextAsync(fullPath, result.Output, ct);
            outputFilePath = $"tool_outputs/{fileName}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write tool output file {File} for tool {Name}", fileName, toolName);
        }

        var truncated = def.TruncateOutput
            ? TruncateOutput(result, outputFilePath ?? fileName)
            : ApplyHardCap(result.Output);

        _logger.LogInformation("Tool {Name} completed: exit={Exit}, timed_out={TimedOut}, output_lines={Lines}, output_file={File}",
            toolName, result.ExitCode, result.TimedOut, result.Output.Split('\n').Length, outputFilePath);

        return (result, outputFilePath, truncated);
    }

    private string TruncateOutput(ToolResult result, string relativePath)
    {
        var headLines = _options.HeadLines;
        var tailLines = _options.TailLines;
        var lines = result.Output.Split('\n');

        if (lines.Length <= headLines + tailLines)
            return FormatHeader(result, lines.Length, relativePath) + result.Output;

        var head = string.Join('\n', lines.Take(headLines));
        var tail = string.Join('\n', lines.Skip(lines.Length - tailLines));
        var omitted = lines.Length - headLines - tailLines;

        _logger.LogDebug("Truncated output for tool: {Lines} lines -> head {Head} + tail {Tail}, omitted {Omitted}",
            lines.Length, headLines, tailLines, omitted);

        return FormatHeader(result, lines.Length, relativePath)
            + head + $"\n... ({omitted} lines omitted) ...\n" + tail;
    }

    private static string FormatHeader(ToolResult result, int lineCount, string relativePath) =>
        $"[exit_code: {result.ExitCode} | {lineCount} lines | full: {relativePath}]\n\n";

    private string ApplyHardCap(string output, int maxBytes = 65536)
    {
        if (output.Length <= maxBytes) return output;
        _logger.LogWarning("Tool output exceeded hard cap ({Length} > {Max}), truncating", output.Length, maxBytes);
        return output[..maxBytes] + $"\n... [truncated at {maxBytes / 1024}KB hard cap]";
    }
}
