using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, IInvestigatorTool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ToolRegistry> _logger;
    private readonly ToolOutputOptions _options;
    private readonly List<Type> _sorted;
    private readonly IServiceProvider _rootSp;
    private readonly OutputSummarizer _summarizer;

    public ToolRegistry(
        IServiceProvider sp,
        IEnumerable<Type> toolTypes,
        ILogger<ToolRegistry> logger,
        IOptions<ToolOutputOptions> toolOutputOptions,
        OutputSummarizer summarizer)
    {
        _logger = logger;
        _options = toolOutputOptions.Value;
        _sorted = TopologicalSort(toolTypes.ToList());
        _rootSp = sp;
        _summarizer = summarizer;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var scope = _rootSp.CreateScope();
        var toolSp = new ToolAwareServiceProvider(scope.ServiceProvider);

        foreach (var type in _sorted)
        {
            var tool = (IInvestigatorTool)ActivatorUtilities.CreateInstance(toolSp, type);

            try
            {
                await tool.RegisterAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tool {Type} skipped (registration failed)", type.Name);
                continue;
            }

            _tools[tool.Definition.Name] = tool;
            toolSp.Register(type, tool);
            _logger.LogInformation("Registered tool: {Name} (timeout={Timeout}s, truncate={Truncate})",
                tool.Definition.Name, tool.Definition.DefaultTimeout.TotalSeconds, tool.Definition.TruncateOutput);
        }
    }

    public IReadOnlyList<ToolDefinition> GetToolDefinitions() =>
        _tools.Values.Select(t => t.Definition).ToList();

    public IReadOnlyList<ToolDefinition> GetToolDefinitions(ToolScope scope) =>
        _tools.Values
            .Where(t => t.Definition.Scope.HasFlag(scope))
            .Select(t => t.Definition).ToList();

    public IReadOnlyList<string> GetSystemPromptContributions() =>
        _tools.Values
            .OfType<ISystemPromptContributor>()
            .Select(c => c.GetSystemPromptSection())
            .Where(s => s is not null)
            .ToList()!;

    public IReadOnlyList<string> GetSystemPromptContributions(ToolScope scope) =>
        _tools.Values
            .Where(t => t.Definition.Scope.HasFlag(scope))
            .OfType<ISystemPromptContributor>()
            .Select(c => c.GetSystemPromptSection())
            .Where(s => s is not null)
            .ToList()!;

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

        string? outputFilePath;
        string truncated;
        int lineCount;

        if (result.OutputFile is not null)
        {
            // Tool already streamed output to disk and pre-truncated
            outputFilePath = result.OutputFile;
            truncated = result.Output;
            lineCount = result.LineCount ?? 1;
        }
        else
        {
            // Legacy path: tool returned full output in memory
            var outputNum = context.NextOutputNumber();
            var fileName = $"{outputNum:D3}-{toolName}.txt";
            var outputDir = Path.Combine(context.WorkspacePath, "tool_outputs");

            outputFilePath = null;
            try
            {
                Directory.CreateDirectory(outputDir);
                var fullPath = Path.Combine(outputDir, fileName);
                await File.WriteAllTextAsync(fullPath, result.Output, ct);
                outputFilePath = $"tool_outputs/{fileName}";
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Failed to write tool output file {File} for tool {Name}", fileName, toolName);
            }

            lineCount = CountLines(result.Output);

            truncated = def.TruncateOutput
                ? TruncateOutput(result, outputFilePath ?? fileName)
                : ApplyHardCap(result.Output, _options.HardCapBytes);

            if (def.TruncateOutput && lineCount > _options.HeadLines + _options.TailLines)
            {
                var summary = await _summarizer.SummarizeAsync(result.Output, ct);
                if (summary is not null)
                    truncated = OutputSummarizer.InsertSummary(truncated, summary);
            }
        }

        _logger.LogInformation("Tool {Name} completed: exit={Exit}, timed_out={TimedOut}, output_lines={Lines}, output_file={File}",
            toolName, result.ExitCode, result.TimedOut, lineCount, outputFilePath);

        return (result, outputFilePath, truncated);
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        var count = 1;
        foreach (var ch in text.AsSpan())
            if (ch == '\n') count++;
        return count;
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

    private string ApplyHardCap(string output, int maxBytes)
    {
        if (output.Length <= maxBytes) return output;
        _logger.LogWarning("Tool output exceeded hard cap ({Length} > {Max}), truncating", output.Length, maxBytes);
        return output[..maxBytes] + $"\n... [truncated at {maxBytes / 1024}KB hard cap]";
    }

    private static List<Type> TopologicalSort(List<Type> toolTypes)
    {
        var toolTypeSet = new HashSet<Type>(toolTypes);
        var deps = new Dictionary<Type, List<Type>>();
        foreach (var type in toolTypes)
        {
            var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            deps[type] = ctor.GetParameters()
                .Select(p => p.ParameterType)
                .Where(toolTypeSet.Contains)
                .ToList();
        }

        var inDegree = deps.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        var queue = new Queue<Type>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var sorted = new List<Type>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(current);
            foreach (var (type, typeDeps) in deps)
            {
                if (!typeDeps.Remove(current)) continue;
                inDegree[type]--;
                if (inDegree[type] == 0)
                    queue.Enqueue(type);
            }
        }

        if (sorted.Count != toolTypes.Count)
            throw new InvalidOperationException(
                $"Circular dependency detected among tool types: {string.Join(", ", toolTypes.Except(sorted).Select(t => t.Name))}");

        return sorted;
    }

    private sealed class ToolAwareServiceProvider(IServiceProvider inner) : IServiceProvider
    {
        private readonly Dictionary<Type, object> _created = new();

        public object? GetService(Type serviceType)
            => _created.GetValueOrDefault(serviceType) ?? inner.GetService(serviceType);

        public void Register(Type type, object instance) => _created[type] = instance;
    }
}
