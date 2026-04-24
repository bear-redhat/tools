using System.Diagnostics;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Microsoft.Extensions.Options;

namespace Investigator.Tools;

public sealed class ReleaseRepoTool : IInvestigatorTool
{
    private static readonly JsonElement s_paramSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "action": {
                "type": "string",
                "enum": ["get_path", "pull"],
                "description": "get_path: returns local path (clones on first use). pull: git pull to update."
            }
        },
        "required": ["action"]
    }
    """).RootElement.Clone();

    private readonly string _gitPath = "git";
    private readonly string _repoUrl;
    private readonly string _localPath;
    private readonly bool _shallowClone;
    private readonly TimeSpan? _maxAge;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ReleaseRepoTool(IOptions<ReleaseRepoOptions> options)
    {
        var opts = options.Value;
        if (!string.IsNullOrEmpty(opts.Path))
            _gitPath = opts.Path;

        _repoUrl = opts.Url;
        _localPath = !string.IsNullOrEmpty(opts.LocalPath)
            ? opts.LocalPath
            : Path.Combine(Path.GetTempPath(), "investigator", "release-repo");
        _shallowClone = opts.ShallowClone;
        _maxAge = opts.MaxAge;
    }

    public ToolDefinition Definition => new(
        Name: "release_repo",
        Description: "Manage the local clone of openshift/release. "
            + "Actions: get_path (returns local path, clones on first use), pull (update to latest). "
            + "After getting the path, use run_shell to read files (cat, grep, find, etc.).",
        ParameterSchema: s_paramSchema,
        DefaultTimeout: TimeSpan.FromSeconds(120));

    public async Task<ToolResult> InvokeAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var action = parameters.GetProperty("action").GetString() ?? "";

        context.Logger.LogInformation("release_repo: action={Action}, localPath={Path}", action, _localPath);

        await _lock.WaitAsync(ct);
        try
        {
            return action switch
            {
                "get_path" => await GetPath(context, ct),
                "pull" => await Pull(context, ct),
                _ => LogAndReturn(context, $"Unknown action: {action}. Use 'get_path' or 'pull'."),
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    private static ToolResult LogAndReturn(ToolContext context, string message)
    {
        context.Logger.LogError("release_repo: {Message}", message);
        return new ToolResult(message, ExitCode: 1);
    }

    private async Task<ToolResult> GetPath(ToolContext context, CancellationToken ct)
    {
        if (Directory.Exists(Path.Combine(_localPath, ".git")))
        {
            var age = GetLastFetchAge();
            if (_maxAge is not null && age > _maxAge)
            {
                context.Logger.LogInformation("release_repo: clone is {Age} old (max {Max}), auto-pulling",
                    FormatAge(age), FormatAge(_maxAge.Value));
                context.OnOutputLine?.Invoke($"Release repo is {FormatAge(age)} stale -- pulling...");
                await PullInternal(context, ct);
                age = GetLastFetchAge();
            }

            context.Logger.LogDebug("release_repo: repo already exists at {Path}", _localPath);
            return new ToolResult($"{_localPath}\n(last synced {FormatAge(age)} ago)");
        }

        context.Logger.LogInformation("release_repo: cloning {Url} into {Path} (shallow={Shallow})", _repoUrl, _localPath, _shallowClone);
        context.OnOutputLine?.Invoke($"Cloning {_repoUrl} into {_localPath}...");

        var args = new List<string> { "clone" };
        if (_shallowClone) args.Add("--depth=1");
        args.Add(_repoUrl);
        args.Add(_localPath);

        var (output, exitCode) = await RunGit(args, null, context, ct);

        if (exitCode != 0)
        {
            context.Logger.LogError("release_repo: git clone failed with exit code {Code}. Output: {Output}", exitCode, output);
            return new ToolResult($"Clone failed (exit code {exitCode}):\n{output}", ExitCode: exitCode);
        }

        context.Logger.LogInformation("release_repo: clone completed successfully");
        return new ToolResult($"{_localPath}\n\n{output}");
    }

    private async Task<ToolResult> Pull(ToolContext context, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(_localPath, ".git")))
        {
            context.Logger.LogWarning("release_repo: pull requested but repo not cloned at {Path}", _localPath);
            return new ToolResult("Repo not cloned yet. Use action 'get_path' first.", ExitCode: 1);
        }

        return await PullInternal(context, ct);
    }

    private async Task<ToolResult> PullInternal(ToolContext context, CancellationToken ct)
    {
        context.Logger.LogInformation("release_repo: pulling latest changes in {Path}", _localPath);
        context.OnOutputLine?.Invoke("Pulling latest changes...");
        var (output, exitCode) = await RunGit(["pull"], _localPath, context, ct);

        if (exitCode != 0)
            context.Logger.LogWarning("release_repo: git pull exited with code {Code}. Output: {Output}", exitCode, output);
        else
            context.Logger.LogInformation("release_repo: pull completed successfully");

        return new ToolResult(output, ExitCode: exitCode);
    }

    private TimeSpan GetLastFetchAge()
    {
        var fetchHead = Path.Combine(_localPath, ".git", "FETCH_HEAD");
        var head = Path.Combine(_localPath, ".git", "HEAD");

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
            context.Logger.LogError("release_repo: failed to start {Git} process", _gitPath);
            return ($"Failed to start {_gitPath} process", -1);
        }

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (stderr.Length > 0)
            context.Logger.LogDebug("release_repo: git stderr: {Stderr}", stderr);

        return (stdout + stderr, proc.ExitCode);
    }
}
