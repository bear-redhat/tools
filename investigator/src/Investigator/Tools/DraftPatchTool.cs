using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Services;

namespace Investigator.Tools;

public sealed partial class DraftPatchTool : IInvestigatorTool, ISystemPromptContributor
{
    private static readonly JsonElement s_paramSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "repo_path": {
                "type": "string",
                "description": "Path to the git repository containing the changes (absolute, or relative to workspace)."
            },
            "description": {
                "type": "string",
                "description": "Brief description of what this patch does."
            }
        },
        "required": ["repo_path", "description"]
    }
    """).RootElement.Clone();

    private readonly BrowserTimeZone _browserTz;
    private readonly ILogger<DraftPatchTool> _logger;

    public DraftPatchTool(BrowserTimeZone browserTz, ILogger<DraftPatchTool> logger)
    {
        _browserTz = browserTz;
        _logger = logger;
    }

    public Task RegisterAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ToolDefinition Definition => new(
        Name: "draft_patch",
        Description: "Capture git changes in a repository as a downloadable .patch file. "
            + "Make your edits first (via run_shell), then call this with the repo path and a description. "
            + "The Client will download and apply the patch through their own review process.",
        ParameterSchema: s_paramSchema,
        DefaultTimeout: TimeSpan.FromSeconds(30),
        TruncateOutput: false,
        Scope: ToolScope.Remediation);

    public string? GetSystemPromptSection() =>
        """
        PATCH DRAFTING:
        Use draft_patch to capture git changes as a downloadable .patch file. Make your edits first (via run_shell), then call draft_patch with the repo path and a description. The Client will download and apply the patch through their own review process. You do not push, commit to remote, or create pull requests.
        """;

    public async Task<ToolResult> InvokeAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var repoPath = parameters.TryGetProperty("repo_path", out var rp) ? rp.GetString() ?? "" : "";
        var description = parameters.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(repoPath))
            return new ToolResult("repo_path is required.", ExitCode: 1);

        if (!Path.IsPathRooted(repoPath))
            repoPath = Path.Combine(context.WorkspacePath, repoPath);

        if (!Directory.Exists(Path.Combine(repoPath, ".git")))
            return new ToolResult($"Not a git repository: {repoPath}", ExitCode: 1);

        var remote = await RunGit(repoPath, "remote get-url origin", ct);
        var branch = (await RunGit(repoPath, "rev-parse --abbrev-ref HEAD", ct)).Trim();
        var baseCommit = (await RunGit(repoPath, "rev-parse HEAD", ct)).Trim();
        var baseShort = baseCommit.Length >= 12 ? baseCommit[..12] : baseCommit;
        var commitDate = (await RunGit(repoPath, $"log -1 --format=%aI {baseCommit}", ct)).Trim();

        var orgRepo = ParseOrgRepo(remote.Trim());

        var diff = await RunGit(repoPath, "diff HEAD", ct);
        var untrackedFiles = (await RunGit(repoPath, "ls-files --others --exclude-standard", ct)).Trim();
        if (!string.IsNullOrEmpty(untrackedFiles))
        {
            foreach (var file in untrackedFiles.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fileDiff = await RunGit(repoPath, $"diff --no-index /dev/null {file}", ct);
                diff += "\n" + fileDiff;
            }
        }

        if (string.IsNullOrWhiteSpace(diff))
            return new ToolResult("No changes detected in the repository.", ExitCode: 1);

        var diffstat = (await RunGit(repoPath, "diff --stat HEAD", ct)).Trim();

        var tz = _browserTz.TimeZone;
        var now = tz is not null
            ? TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz)
            : DateTimeOffset.UtcNow;
        var nowStr = $"{now:O}";

        var slug = SlugRegex().Replace(description.ToLowerInvariant(), "-");
        if (slug.Length > 60) slug = slug[..60];
        slug = slug.Trim('-');
        var outputNum = context.NextOutputNumber();
        var fileName = $"{outputNum:D3}-{slug}.patch";

        var patchDir = Path.Combine(context.WorkspacePath, "tool_outputs", "patches");
        Directory.CreateDirectory(patchDir);
        var patchPath = Path.Combine(patchDir, fileName);

        var header = new StringBuilder();
        header.AppendLine($"# Patch: {description}");
        header.AppendLine($"# Repo: {orgRepo}");
        header.AppendLine($"# Branch: {branch}");
        header.AppendLine($"# Base: {baseShort} ({commitDate})");
        header.AppendLine($"# Generated: {nowStr}");
        header.AppendLine("#");
        header.AppendLine($"# Apply with:");
        header.AppendLine($"#   cd <{orgRepo} clone>");
        header.AppendLine($"#   git checkout {baseShort}");
        header.AppendLine($"#   git apply /path/to/{fileName}");
        header.AppendLine();

        await File.WriteAllTextAsync(patchPath, header.ToString() + diff, ct);

        var relativePath = $"tool_outputs/patches/{fileName}";
        _logger.LogInformation("Patch written to {Path}: {DiffStat}", relativePath, diffstat);

        var result = new StringBuilder();
        result.AppendLine($"Patch saved: {relativePath}");
        result.AppendLine($"Repo: {orgRepo}");
        result.AppendLine($"Branch: {branch}");
        result.AppendLine($"Base commit: {baseShort}");
        result.AppendLine($"Diffstat: {diffstat}");
        return new ToolResult(result.ToString());
    }

    private static async Task<string> RunGit(string workingDir, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var output = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return output;
    }

    private static string ParseOrgRepo(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return "unknown/unknown";

        var match = OrgRepoRegex().Match(remoteUrl);
        return match.Success ? $"{match.Groups[1].Value}/{match.Groups[2].Value}" : remoteUrl;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"[/:]([^/]+)/([^/.]+?)(?:\.git)?$")]
    private static partial Regex OrgRepoRegex();
}
