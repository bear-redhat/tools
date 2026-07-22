using System.Text;
using System.Text.Json;
using Investigator.Contracts;

namespace Investigator.Tools;

public sealed class ReadOutputTool : IInvestigatorTool
{
    private static readonly JsonElement s_paramSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "file": {
                "type": "string",
                "description": "Relative path to the tool output file, e.g. 'tool_outputs/003-run_oc.txt'. Use the path from the [full: ...] header of any truncated result."
            },
            "start_line": {
                "type": "integer",
                "description": "First line to return (1-indexed). Defaults to 1."
            },
            "end_line": {
                "type": "integer",
                "description": "Last line to return (inclusive). Defaults to end of file."
            }
        },
        "required": ["file"]
    }
    """).RootElement.Clone();

    private const int HardCapBytes = 32768;

    public ToolDefinition Definition => new(
        Name: "read_output",
        Description: "Read the full content of a tool output file by line range. "
            + "Use the file path from the [full: ...] header of any truncated tool result. "
            + "Specify start_line and end_line to read a specific range.",
        ParameterSchema: s_paramSchema,
        DefaultTimeout: TimeSpan.FromSeconds(5),
        TruncateOutput: false);

    public Task RegisterAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<ToolResult> InvokeAsync(JsonElement parameters, ToolContext context, CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("file", out var fileProp) || string.IsNullOrWhiteSpace(fileProp.GetString()))
            return new ToolResult("Parameter 'file' is required.", ExitCode: 1);

        var relativePath = fileProp.GetString()!;

        if (relativePath.Contains("..") || Path.IsPathRooted(relativePath))
            return new ToolResult("Invalid file path -- must be a relative path within the workspace.", ExitCode: 1);

        var fullPath = Path.Combine(context.WorkspacePath, relativePath);
        if (!File.Exists(fullPath))
            return new ToolResult($"File not found: {relativePath}", ExitCode: 1);

        var startLine = parameters.TryGetProperty("start_line", out var sp) && sp.TryGetInt32(out var s) ? s : 1;
        var endLine = parameters.TryGetProperty("end_line", out var ep) && ep.TryGetInt32(out var e) ? e : int.MaxValue;

        if (startLine < 1) startLine = 1;
        if (endLine < startLine) endLine = startLine;

        var sb = new StringBuilder();
        var lineNum = 0;
        using var reader = new StreamReader(fullPath, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNum++;
            if (lineNum < startLine) continue;
            if (lineNum > endLine) break;
            sb.AppendLine(line);
            if (!context.RawOutput && sb.Length >= HardCapBytes)
            {
                sb.AppendLine($"... [truncated at {HardCapBytes / 1024}KB hard cap, line {lineNum}]");
                break;
            }
        }

        if (sb.Length == 0)
            return new ToolResult($"No content in range {startLine}-{endLine} (file has {lineNum} lines).", ExitCode: 0);

        var header = $"[{relativePath} | lines {startLine}-{Math.Min(lineNum, endLine)} of {lineNum}]\n\n";
        return new ToolResult(header + sb.ToString(), ExitCode: 0);
    }
}
