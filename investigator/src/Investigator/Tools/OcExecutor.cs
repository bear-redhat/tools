using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Microsoft.Extensions.Options;

namespace Investigator.Tools;

public sealed class OcExecutor : IInvestigatorTool, ISystemPromptContributor
{
    private static readonly JsonElement s_paramSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "cluster": {
                "type": "string",
                "description": "Name of the target cluster"
            },
            "command": {
                "type": "string",
                "description": "The oc subcommand and arguments, e.g. 'get pods -n production -o wide'. Do NOT include the 'oc' prefix. Always fetch complete output -- do NOT add grep, awk, or pipes."
            }
        },
        "required": ["cluster", "command"]
    }
    """).RootElement.Clone();

    private static readonly TimeSpan s_expiryBuffer = TimeSpan.FromSeconds(60);

    private readonly string _ocPath = "oc";
    private readonly string _kubeconfigPath = Path.Combine(Path.GetTempPath(), "investigator-kubeconfig");
    private readonly List<ClusterEntry> _clusters = [];
    private readonly HashSet<string> _unavailableClusters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedLogin> _loggedInContexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private readonly ILogger<OcExecutor> _logger;

    public OcExecutor(IOptions<OcOptions> options, ILogger<OcExecutor> logger)
    {
        _logger = logger;
        var opts = options.Value;
        if (!string.IsNullOrEmpty(opts.Path))
            _ocPath = opts.Path;

        foreach (var c in opts.Clusters)
        {
            if (string.IsNullOrEmpty(c.Name) || string.IsNullOrEmpty(c.Type))
                continue;

            _clusters.Add(new ClusterEntry
            {
                Name = c.Name,
                Type = c.Type,
                Kubeconfig = c.Kubeconfig,
                Context = c.Context,
                Server = c.Server,
                TokenFile = c.TokenFile,
                CaFile = c.CaFile,
            });
        }

        ValidateClusterCredentials();
    }

    private void ValidateClusterCredentials()
    {
        foreach (var c in _clusters)
        {
            if (c.Type.Equals("token", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(c.TokenFile))
                {
                    _logger.LogWarning("Cluster {Name}: no token file configured", c.Name);
                    _unavailableClusters.Add(c.Name);
                }
                else if (!File.Exists(c.TokenFile))
                {
                    _logger.LogWarning("Cluster {Name}: token file not found at {Path}", c.Name, c.TokenFile);
                    _unavailableClusters.Add(c.Name);
                }
                else if (new FileInfo(c.TokenFile).Length == 0)
                {
                    _logger.LogWarning("Cluster {Name}: token file is empty at {Path}", c.Name, c.TokenFile);
                    _unavailableClusters.Add(c.Name);
                }
            }
            else if (c.Type.Equals("kubeconfig", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(c.Kubeconfig) && !File.Exists(c.Kubeconfig))
                {
                    _logger.LogWarning("Cluster {Name}: kubeconfig not found at {Path}", c.Name, c.Kubeconfig);
                    _unavailableClusters.Add(c.Name);
                }
            }
        }

        if (_unavailableClusters.Count > 0)
            _logger.LogWarning("Clusters with missing/empty credentials: {Clusters}", string.Join(", ", _unavailableClusters));

        var available = _clusters.Where(c => !_unavailableClusters.Contains(c.Name)).Select(c => c.Name);
        _logger.LogInformation("Available clusters: {Clusters}", string.Join(", ", available));
    }

    public ToolDefinition Definition => new(
        Name: "run_oc",
        Description: "Issue a read-only oc command against a cluster. "
            + "Always fetch complete output -- do NOT add grep, awk, or pipes to filter. "
            + "Output is saved to disk; use run_shell to search or filter afterward.",
        ParameterSchema: s_paramSchema,
        DefaultTimeout: TimeSpan.FromSeconds(30));

    public IReadOnlyList<string> ListClusters() =>
        _clusters.Where(c => !_unavailableClusters.Contains(c.Name)).Select(c => c.Name).ToList();

    public IReadOnlyList<string> ListAllClusters() => _clusters.Select(c => c.Name).ToList();

    public string? GetSystemPromptSection()
    {
        var available = ListClusters();
        var list = available.Count > 0
            ? string.Join(", ", available)
            : "(no clusters configured)";
        return $"Available clusters: {list}";
    }

    public async Task<ToolResult> InvokeAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var cluster = parameters.GetProperty("cluster").GetString() ?? "";
        var command = parameters.GetProperty("command").GetString() ?? "";

        if (string.IsNullOrWhiteSpace(cluster))
        {
            context.Logger.LogError("run_oc called with empty cluster parameter");
            return new ToolResult("Error: 'cluster' parameter is required and was empty.", ExitCode: 1);
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            context.Logger.LogError("run_oc called with empty command parameter");
            return new ToolResult("Error: 'command' parameter is required and was empty.", ExitCode: 1);
        }

        var entry = _clusters.FirstOrDefault(c => c.Name.Equals(cluster, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            var available = string.Join(", ", _clusters.Select(c => c.Name));
            context.Logger.LogError("Unknown cluster requested: {Name}. Available: {Available}", cluster, available);
            return new ToolResult($"Unknown cluster: {cluster}. Available: {available}", ExitCode: 1);
        }

        var args = new List<string>();
        string reproSuffix;

        if (entry.Type.Equals("kubeconfig", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(entry.Kubeconfig))
            {
                if (!File.Exists(entry.Kubeconfig))
                {
                    context.Logger.LogError("Kubeconfig file not found for cluster {Name}: {Path}", entry.Name, entry.Kubeconfig);
                    return new ToolResult($"Kubeconfig file not found: {entry.Kubeconfig}", ExitCode: 1);
                }
                args.AddRange(["--kubeconfig", entry.Kubeconfig]);
            }
            var ctx = entry.Context ?? entry.Name;
            args.AddRange(["--context", ctx]);
            reproSuffix = !string.IsNullOrEmpty(entry.Kubeconfig)
                ? $"--kubeconfig={entry.Kubeconfig} --context={ctx}"
                : $"--context={ctx}";
        }
        else if (entry.Type.Equals("token", StringComparison.OrdinalIgnoreCase))
        {
            var ctx = await EnsureLoggedIn(entry, context, ct);
            if (ctx is null)
                return new ToolResult($"Failed to login to cluster {entry.Name}", ExitCode: 1);
            args.AddRange(["--context", ctx]);
            reproSuffix = $"--context={ctx}";
        }
        else
        {
            context.Logger.LogError("Unknown cluster type '{Type}' for cluster {Name}", entry.Type, entry.Name);
            return new ToolResult($"Unknown cluster type: {entry.Type}", ExitCode: 1);
        }

        foreach (var part in SplitArgs(command))
            args.Add(part);

        var reproCommand = $"oc {reproSuffix} {command}";

        context.Logger.LogDebug("run_oc: executing {Oc} with {ArgCount} args against cluster {Cluster}", _ocPath, args.Count, cluster);

        var psi = new ProcessStartInfo(_ocPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["KUBECONFIG"] = _kubeconfigPath;
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        var output = new StringBuilder();
        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null)
            {
                context.Logger.LogError("run_oc: failed to start process {Path}", _ocPath);
                return new ToolResult($"Failed to start {_ocPath} process", ExitCode: -1, ReproCommand: reproCommand);
            }

            var readOut = Task.Run(async () =>
            {
                while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    output.AppendLine(line);
                    context.OnOutputLine?.Invoke(line);
                }
            }, ct);

            var stderr = new StringBuilder();
            var readErr = Task.Run(async () =>
            {
                while (await proc.StandardError.ReadLineAsync(ct) is { } line)
                    stderr.AppendLine(line);
            }, ct);

            await proc.WaitForExitAsync(ct);
            await Task.WhenAll(readOut, readErr);

            if (stderr.Length > 0)
            {
                context.Logger.LogDebug("run_oc: stderr ({Length} chars) for '{Command}' on {Cluster}", stderr.Length, command, cluster);
                output.Append(stderr);
            }

            if (proc.ExitCode != 0)
                context.Logger.LogWarning("run_oc: '{Command}' on {Cluster} exited with code {Code}", command, cluster, proc.ExitCode);

            return new ToolResult(output.ToString(), ExitCode: proc.ExitCode, ReproCommand: reproCommand);
        }
        catch (OperationCanceledException)
        {
            KillProcess(proc, command, cluster, context.Logger);
            context.Logger.LogWarning("run_oc: '{Command}' on {Cluster} was cancelled (timeout)", command, cluster);
            return new ToolResult(output + "\n[timed out]", ExitCode: -1, TimedOut: true, ReproCommand: reproCommand);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "run_oc: unexpected error executing '{Command}' on {Cluster}", command, cluster);
            throw;
        }
        finally
        {
            proc?.Dispose();
        }
    }

    private static void KillProcess(Process? proc, string command, string cluster, ILogger logger)
    {
        if (proc is null || proc.HasExited) return;
        try
        {
            proc.Kill(entireProcessTree: true);
            logger.LogDebug("run_oc: killed process tree for '{Command}' on {Cluster}", command, cluster);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "run_oc: failed to kill process for '{Command}' on {Cluster}", command, cluster);
        }
    }

    private async Task<string?> EnsureLoggedIn(ClusterEntry entry, ToolContext context, CancellationToken ct)
    {
        if (_loggedInContexts.TryGetValue(entry.Name, out var cached) && !cached.IsExpired(s_expiryBuffer))
            return cached.ContextName;

        await _loginLock.WaitAsync(ct);
        try
        {
            if (_loggedInContexts.TryGetValue(entry.Name, out cached) && !cached.IsExpired(s_expiryBuffer))
                return cached.ContextName;

            _loggedInContexts.Remove(entry.Name);

            if (string.IsNullOrEmpty(entry.Server))
            {
                context.Logger.LogError("Token-based cluster {Name} has no Server configured", entry.Name);
                return null;
            }

            if (string.IsNullOrEmpty(entry.TokenFile) || !File.Exists(entry.TokenFile))
            {
                context.Logger.LogError("Token file missing or not found for cluster {Name}: {Path}", entry.Name, entry.TokenFile);
                return null;
            }

            var token = (await File.ReadAllTextAsync(entry.TokenFile, ct)).Trim();
            if (string.IsNullOrEmpty(token))
            {
                context.Logger.LogError("Token file for cluster {Name} is empty: {Path}", entry.Name, entry.TokenFile);
                return null;
            }

            var loginArgs = new List<string> { "login", entry.Server, $"--token={token}" };
            if (!string.IsNullOrEmpty(entry.CaFile))
            {
                if (!File.Exists(entry.CaFile))
                    context.Logger.LogWarning("CA file not found for cluster {Name}: {Path}", entry.Name, entry.CaFile);
                loginArgs.Add($"--certificate-authority={entry.CaFile}");
            }

            context.Logger.LogInformation("run_oc: logging in to cluster {Name} at {Server}", entry.Name, entry.Server);

            var psi = new ProcessStartInfo(_ocPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.Environment["KUBECONFIG"] = _kubeconfigPath;
            foreach (var arg in loginArgs) psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                context.Logger.LogError("run_oc: failed to start oc login process for cluster {Name}", entry.Name);
                return null;
            }

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
            {
                context.Logger.LogError("run_oc: oc login failed for cluster {Name} (exit {Code}). stdout={Out}, stderr={Err}",
                    entry.Name, proc.ExitCode, stdout, stderr);
                return null;
            }

            var contextName = $"investigator-{entry.Name}";
            var actualContext = await GetCurrentContext(ct);
            if (actualContext is not null && actualContext != contextName)
            {
                if (!await RenameContext(actualContext, contextName, ct))
                {
                    context.Logger.LogWarning("run_oc: failed to rename context {Actual} to {Expected}, using actual",
                        actualContext, contextName);
                    contextName = actualContext;
                }
            }
            else if (actualContext is null)
            {
                context.Logger.LogWarning("run_oc: could not read current-context after login for cluster {Name}", entry.Name);
            }

            var expiry = GetJwtExpiry(token) ?? DateTimeOffset.MaxValue;
            _loggedInContexts[entry.Name] = new CachedLogin(contextName, expiry);
            context.Logger.LogInformation("run_oc: logged in to cluster {Name} with context {Context} (expires {Expiry})",
                entry.Name, contextName, expiry == DateTimeOffset.MaxValue ? "never" : expiry.ToString("u"));
            return contextName;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private static DateTimeOffset? GetJwtExpiry(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
            return null;

        try
        {
            var payload = parts[1];
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            payload = payload.Replace('-', '+').Replace('_', '/');

            var json = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("exp", out var exp))
                return DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64());
        }
        catch
        {
            // Not a valid JWT or missing exp -- treat as non-expiring
        }

        return null;
    }

    private async Task<string?> GetCurrentContext(CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ocPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["KUBECONFIG"] = _kubeconfigPath;
        psi.ArgumentList.Add("config");
        psi.ArgumentList.Add("current-context");

        using var proc = Process.Start(psi);
        if (proc is null) return null;

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode == 0 ? stdout.Trim() : null;
    }

    private async Task<bool> RenameContext(string oldName, string newName, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ocPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["KUBECONFIG"] = _kubeconfigPath;
        psi.ArgumentList.Add("config");
        psi.ArgumentList.Add("rename-context");
        psi.ArgumentList.Add(oldName);
        psi.ArgumentList.Add(newName);

        using var proc = Process.Start(psi);
        if (proc is null) return false;

        await proc.WaitForExitAsync(ct);
        return proc.ExitCode == 0;
    }

    private static IEnumerable<string> SplitArgs(string command)
    {
        var inQuote = false;
        var current = new StringBuilder();
        foreach (var c in command)
        {
            if (c == '"') { inQuote = !inQuote; continue; }
            if (c == ' ' && !inQuote)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private sealed record CachedLogin(string ContextName, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired(TimeSpan buffer) => DateTimeOffset.UtcNow + buffer >= ExpiresAt;
    }

    private sealed class ClusterEntry
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string? Kubeconfig { get; set; }
        public string? Context { get; set; }
        public string? Server { get; set; }
        public string? TokenFile { get; set; }
        public string? CaFile { get; set; }
    }
}
