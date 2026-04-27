using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Microsoft.Extensions.Options;

namespace Investigator.Tools;

public sealed class CiRepoTool : IInvestigatorTool
{
    private static readonly JsonElement s_paramSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "repo": {
                "type": "string",
                "enum": ["release", "ci-tools"],
                "description": "Which repository: release (openshift/release) or ci-tools (openshift/ci-tools)."
            },
            "action": {
                "type": "string",
                "enum": ["get_path", "pull"],
                "description": "get_path: returns local path (clones on first use). pull: git pull to update."
            }
        },
        "required": ["repo", "action"]
    }
    """).RootElement.Clone();

    private readonly string _gitPath = "git";
    private readonly Dictionary<string, RepoState> _repos = new(StringComparer.OrdinalIgnoreCase);

    public CiRepoTool(IOptions<CiRepoOptions> options)
    {
        var opts = options.Value;
        if (!string.IsNullOrEmpty(opts.Path))
            _gitPath = opts.Path;

        foreach (var (key, cfg) in opts.Repos)
        {
            var localPath = !string.IsNullOrEmpty(cfg.LocalPath)
                ? cfg.LocalPath
                : Path.Combine(Path.GetTempPath(), "investigator", $"{key}-repo");

            _repos[key] = new RepoState(cfg.Url, localPath, cfg.ShallowClone, cfg.MaxAge);
        }
    }

    public ToolDefinition Definition => new(
        Name: "ci_repo",
        Description: "Access local clones of CI repositories (openshift/release or openshift/ci-tools). "
            + "Actions: get_path (returns local path, clones on first use), pull (update to latest). "
            + "After getting the path, use run_shell to read files (cat, grep, find, etc.).",
        ParameterSchema: s_paramSchema,
        DefaultTimeout: TimeSpan.FromSeconds(120));

    public async Task<ToolResult> InvokeAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var repoKey = parameters.GetProperty("repo").GetString() ?? "";
        var action = parameters.GetProperty("action").GetString() ?? "";

        if (!_repos.TryGetValue(repoKey, out var repo))
            return LogAndReturn(context, $"Unknown repo: {repoKey}. Available: {string.Join(", ", _repos.Keys)}.");

        context.Logger.LogInformation("ci_repo: repo={Repo}, action={Action}, localPath={Path}",
            repoKey, action, repo.LocalPath);

        await repo.Lock.WaitAsync(ct);
        try
        {
            return action switch
            {
                "get_path" => await GetPath(repoKey, repo, context, ct),
                "pull" => await Pull(repoKey, repo, context, ct),
                _ => LogAndReturn(context, $"Unknown action: {action}. Use 'get_path' or 'pull'."),
            };
        }
        finally
        {
            repo.Lock.Release();
        }
    }

    private static ToolResult LogAndReturn(ToolContext context, string message)
    {
        context.Logger.LogError("ci_repo: {Message}", message);
        return new ToolResult(message, ExitCode: 1);
    }

    private async Task<ToolResult> GetPath(string repoKey, RepoState repo, ToolContext context, CancellationToken ct)
    {
        if (Directory.Exists(Path.Combine(repo.LocalPath, ".git")))
        {
            var age = GetLastFetchAge(repo.LocalPath);
            if (repo.MaxAge is not null && age > repo.MaxAge)
            {
                context.Logger.LogInformation("ci_repo[{Repo}]: clone is {Age} old (max {Max}), auto-pulling",
                    repoKey, FormatAge(age), FormatAge(repo.MaxAge.Value));
                context.OnOutputLine?.Invoke($"{repoKey} repo is {FormatAge(age)} stale -- pulling...");
                await PullInternal(repoKey, repo, context, ct);
                age = GetLastFetchAge(repo.LocalPath);
            }

            context.Logger.LogDebug("ci_repo[{Repo}]: repo already exists at {Path}", repoKey, repo.LocalPath);
            return new ToolResult($"{repo.LocalPath}\n(last synced {FormatAge(age)} ago)");
        }

        context.Logger.LogInformation("ci_repo[{Repo}]: cloning {Url} into {Path} (shallow={Shallow})",
            repoKey, repo.Url, repo.LocalPath, repo.ShallowClone);
        context.OnOutputLine?.Invoke($"Cloning {repo.Url} into {repo.LocalPath}...");

        var args = new List<string> { "clone" };
        if (repo.ShallowClone) args.Add("--depth=1");
        args.Add(repo.Url);
        args.Add(repo.LocalPath);

        var (output, exitCode) = await RunGit(args, null, context, ct);

        if (exitCode != 0)
        {
            context.Logger.LogError("ci_repo[{Repo}]: git clone failed with exit code {Code}. Output: {Output}",
                repoKey, exitCode, output);
            return new ToolResult($"Clone failed (exit code {exitCode}):\n{output}", ExitCode: exitCode);
        }

        context.Logger.LogInformation("ci_repo[{Repo}]: clone completed successfully", repoKey);
        return new ToolResult($"{repo.LocalPath}\n\n{output}");
    }

    private async Task<ToolResult> Pull(string repoKey, RepoState repo, ToolContext context, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(repo.LocalPath, ".git")))
        {
            context.Logger.LogWarning("ci_repo[{Repo}]: pull requested but repo not cloned at {Path}",
                repoKey, repo.LocalPath);
            return new ToolResult("Repo not cloned yet. Use action 'get_path' first.", ExitCode: 1);
        }

        return await PullInternal(repoKey, repo, context, ct);
    }

    private async Task<ToolResult> PullInternal(string repoKey, RepoState repo, ToolContext context, CancellationToken ct)
    {
        context.Logger.LogInformation("ci_repo[{Repo}]: pulling latest changes in {Path}", repoKey, repo.LocalPath);
        context.OnOutputLine?.Invoke($"Pulling latest {repoKey} changes...");
        var (output, exitCode) = await RunGit(["pull"], repo.LocalPath, context, ct);

        if (exitCode != 0)
            context.Logger.LogWarning("ci_repo[{Repo}]: git pull exited with code {Code}. Output: {Output}",
                repoKey, exitCode, output);
        else
            context.Logger.LogInformation("ci_repo[{Repo}]: pull completed successfully", repoKey);

        return new ToolResult(output, ExitCode: exitCode);
    }

    private static TimeSpan GetLastFetchAge(string localPath)
    {
        var fetchHead = Path.Combine(localPath, ".git", "FETCH_HEAD");
        var head = Path.Combine(localPath, ".git", "HEAD");

        var marker = File.Exists(fetchHead) ? fetchHead : head;
        var lastWrite = File.GetLastWriteTimeUtc(marker);
        return DateTime.UtcNow - lastWrite;
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1)
            return $"{age.TotalDays:F0}d {age.Hours}h";
        if (age.TotalHours >= 1)
            return $"{age.TotalHours:F0}h {age.Minutes}m";
        return $"{age.TotalMinutes:F0}m";
    }

    private async Task<(string Output, int ExitCode)> RunGit(
        IEnumerable<string> args, string? workingDir, ToolContext context, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_gitPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (workingDir is not null) psi.WorkingDirectory = workingDir;
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            context.Logger.LogError("ci_repo: failed to start {Git} process", _gitPath);
            return ($"Failed to start {_gitPath} process", -1);
        }

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (stderr.Length > 0)
            context.Logger.LogDebug("ci_repo: git stderr: {Stderr}", stderr);

        return (stdout + stderr, proc.ExitCode);
    }

    private sealed record RepoState(string Url, string LocalPath, bool ShallowClone, TimeSpan? MaxAge)
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
    }
}
