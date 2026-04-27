using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Investigator.Contracts;
using Investigator.Models;
using Microsoft.Extensions.Options;

namespace Investigator.Tools;

public sealed class AwsExecutor : IInvestigatorTool, ISystemPromptContributor
{
    private static readonly JsonElement s_paramSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "cluster": {
                "type": "string",
                "description": "Name of the target cluster (auto-discovery). Mutually exclusive with account."
            },
            "account": {
                "type": "string",
                "description": "Name of a standalone AWS account. Mutually exclusive with cluster."
            },
            "command": {
                "type": "string",
                "description": "The aws subcommand and arguments, e.g. 'ec2 describe-instances'. Do NOT include the 'aws' prefix. Always fetch complete output -- do NOT add grep, awk, or pipes."
            }
        },
        "required": ["command"]
    }
    """).RootElement.Clone();

    private static readonly TimeSpan s_expiryBuffer = TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> s_readOnlyPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "describe-", "get-", "list-", "search-", "lookup-", "check-", "batch-get-", "filter-"
    };

    private static readonly HashSet<string> s_allowedStsCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "sts get-caller-identity",
        "sts get-access-key-info"
    };

    private static readonly Regex s_accountIdRegex = new(@"arn:aws:iam::(\d+):role/", RegexOptions.Compiled);

    private readonly string _awsPath = "aws";
    private readonly OcExecutor _ocExecutor;
    private readonly List<AwsEntry> _accounts = [];
    private readonly Dictionary<string, DiscoveredCluster> _discoveredClusters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedAwsSession> _sessionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly ILogger<AwsExecutor> _logger;
    private readonly Task _probeTask;

    public record AwsTarget(string Name, AwsTargetKind Kind, string? Description);
    public enum AwsTargetKind { Cluster, Account }

    private sealed record DiscoveredCluster(string RoleArn, string Region);

    private sealed record CachedAwsSession(
        string RoleArn, string Region, string AccessKeyId,
        string SecretAccessKey, string SessionToken, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired(TimeSpan buffer) => DateTimeOffset.UtcNow + buffer >= ExpiresAt;
    }

    public AwsExecutor(IOptions<AwsOptions> options, OcExecutor ocExecutor, ILogger<AwsExecutor> logger)
    {
        _ocExecutor = ocExecutor;
        _logger = logger;
        var opts = options.Value;
        if (!string.IsNullOrEmpty(opts.Path))
            _awsPath = opts.Path;

        foreach (var a in opts.Accounts.Where(a => !string.IsNullOrEmpty(a.Name)))
        {
            if (string.IsNullOrEmpty(a.RoleArn) || string.IsNullOrEmpty(a.Region))
            {
                logger.LogWarning("AWS account {Account} skipped: RoleArn and Region are required", a.Name);
                continue;
            }
            if (!string.IsNullOrEmpty(a.IntermediaryRoleArn))
                logger.LogInformation("AWS account {Account}: using intermediary role {IntermediaryRole}",
                    a.Name, a.IntermediaryRoleArn);
            _accounts.Add(a);
        }

        _probeTask = Task.Run(async () =>
        {
            var allClusters = new HashSet<string>(_ocExecutor.ListClusters(), StringComparer.OrdinalIgnoreCase);
            var skipSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var overrides = new Dictionary<string, AwsEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in opts.Clusters.Where(c => !string.IsNullOrEmpty(c.Name)))
            {
                if (entry.RoleArn is "")
                {
                    skipSet.Add(entry.Name);
                    logger.LogInformation("AWS explicitly disabled for cluster {Cluster}", entry.Name);
                }
                else if (!string.IsNullOrEmpty(entry.RoleArn))
                {
                    overrides[entry.Name] = entry;
                }
            }

            var tasks = allClusters
                .Where(c => !skipSet.Contains(c))
                .Select(async cluster =>
                {
                    try
                    {
                        string? region;

                        if (overrides.TryGetValue(cluster, out var entry))
                        {
                            region = entry.Region;
                            if (string.IsNullOrEmpty(region))
                                region = await DiscoverRegion(cluster);
                            if (string.IsNullOrEmpty(region))
                            {
                                logger.LogWarning("AWS cluster {Cluster}: region unavailable, skipping", cluster);
                                return;
                            }
                            lock (_discoveredClusters)
                                _discoveredClusters[cluster] = new(entry.RoleArn!, region);
                            logger.LogInformation("AWS cluster {Cluster}: configured via override (role={RoleArn})",
                                cluster, entry.RoleArn);
                            return;
                        }

                        var platformResult = await RunOcQuiet(cluster,
                            "get infrastructure cluster -o jsonpath='{.status.platformStatus.type}'",
                            callerContext: null);
                        var platform = platformResult.ExitCode == 0
                            ? platformResult.Output.Trim().Trim('\'')
                            : null;

                        if (string.Equals(platform, "GCP", StringComparison.OrdinalIgnoreCase))
                        {
                            logger.LogInformation("GCP cluster detected: {Cluster} (support pending)", cluster);
                            return;
                        }

                        if (!string.Equals(platform, "AWS", StringComparison.OrdinalIgnoreCase))
                            return;

                        var accountId = await DiscoverAwsAccountId(cluster);
                        if (accountId is null)
                        {
                            logger.LogWarning("AWS cluster {Cluster}: could not discover account ID from CCO secret", cluster);
                            return;
                        }

                        region = await DiscoverRegion(cluster);
                        if (region is null)
                        {
                            logger.LogWarning("AWS cluster {Cluster}: could not discover region", cluster);
                            return;
                        }

                        var roleArn = $"arn:aws:iam::{accountId}:role/investigator";
                        lock (_discoveredClusters)
                            _discoveredClusters[cluster] = new(roleArn, region);
                        logger.LogInformation("AWS cluster {Cluster}: discovered (account={AccountId}, region={Region})",
                            cluster, accountId, region);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "AWS probe failed for cluster {Cluster}", cluster);
                    }
                });

            await Task.WhenAll(tasks);

            List<string> discovered;
            lock (_discoveredClusters)
                discovered = [.. _discoveredClusters.Keys];

            logger.LogInformation("AWS-enabled clusters: {Clusters}",
                discovered.Count > 0 ? string.Join(", ", discovered) : "(none)");
        });
    }

    public ToolDefinition Definition => new(
        Name: "run_aws",
        Description: "Issue a read-only aws command against a cluster's AWS account or a standalone account. "
            + "Always fetch complete output -- do NOT add grep, awk, or pipes. "
            + "Output is saved to disk; use run_shell to search or filter afterward.",
        ParameterSchema: s_paramSchema,
        DefaultTimeout: TimeSpan.FromSeconds(30));

    public IReadOnlyList<AwsTarget> ListTargets()
    {
        var targets = new List<AwsTarget>();
        lock (_discoveredClusters)
        {
            foreach (var c in _discoveredClusters.Keys)
                targets.Add(new AwsTarget(c, AwsTargetKind.Cluster, null));
        }
        foreach (var a in _accounts)
            targets.Add(new AwsTarget(a.Name, AwsTargetKind.Account, a.Description));
        return targets;
    }

    public string? GetSystemPromptSection()
    {
        var parts = new List<string>();

        List<string> clusters;
        lock (_discoveredClusters)
            clusters = [.. _discoveredClusters.Keys];

        if (clusters.Count > 0)
            parts.Add($"AWS clusters (use cluster param): {string.Join(", ", clusters)}");

        if (_accounts.Count > 0)
        {
            var entries = _accounts.Select(a =>
                string.IsNullOrEmpty(a.Description) ? a.Name : $"{a.Name} - {a.Description}");
            parts.Add($"AWS accounts (use account param): {string.Join(", ", entries)}");
        }

        return parts.Count > 0 ? string.Join("\n", parts) : null;
    }

    public async Task<ToolResult> InvokeAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var cluster = parameters.TryGetProperty("cluster", out var cProp) ? cProp.GetString() : null;
        var account = parameters.TryGetProperty("account", out var aProp) ? aProp.GetString() : null;
        var command = parameters.TryGetProperty("command", out var cmdProp) ? cmdProp.GetString() : null;

        if (string.IsNullOrWhiteSpace(command))
            return new ToolResult("Error: 'command' parameter is required and was empty.", ExitCode: 1);

        var hasCluster = !string.IsNullOrWhiteSpace(cluster);
        var hasAccount = !string.IsNullOrWhiteSpace(account);

        if (hasCluster == hasAccount)
            return new ToolResult("Error: exactly one of 'cluster' or 'account' must be provided.", ExitCode: 1);

        if (!IsReadOnlyCommand(command))
            return new ToolResult(
                "Error: only read-only AWS commands are allowed (describe-*, get-*, list-*, search-*, lookup-*, check-*, batch-get-*, filter-*, sts get-caller-identity, sts get-access-key-info).",
                ExitCode: 1);

        CachedAwsSession session;
        try
        {
            session = hasCluster
                ? await GetClusterSession(cluster!, context, ct)
                : await GetAccountSession(account!, context, ct);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "run_aws: failed to obtain AWS credentials");
            return new ToolResult($"Error obtaining AWS credentials: {ex.Message}", ExitCode: 1);
        }

        var args = SplitArgs(command).ToList();
        var reproCommand = $"aws {command}";

        context.Logger.LogDebug("run_aws: executing 'aws {Command}' against {Target}",
            command, hasCluster ? $"cluster {cluster}" : $"account {account}");

        var psi = new ProcessStartInfo(_awsPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["AWS_ACCESS_KEY_ID"] = session.AccessKeyId;
        psi.Environment["AWS_SECRET_ACCESS_KEY"] = session.SecretAccessKey;
        psi.Environment["AWS_SESSION_TOKEN"] = session.SessionToken;
        psi.Environment["AWS_DEFAULT_REGION"] = session.Region;
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        var output = new StringBuilder();
        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null)
            {
                context.Logger.LogError("run_aws: failed to start process {Path}", _awsPath);
                return new ToolResult($"Failed to start {_awsPath} process", ExitCode: -1, ReproCommand: reproCommand);
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
                context.Logger.LogDebug("run_aws: stderr ({Length} chars) for '{Command}'", stderr.Length, command);
                output.Append(stderr);
            }

            if (proc.ExitCode != 0)
                context.Logger.LogWarning("run_aws: '{Command}' exited with code {Code}", command, proc.ExitCode);

            return new ToolResult(output.ToString(), ExitCode: proc.ExitCode, ReproCommand: reproCommand);
        }
        catch (OperationCanceledException)
        {
            KillProcess(proc, command, context.Logger);
            context.Logger.LogWarning("run_aws: '{Command}' was cancelled (timeout)", command);
            return new ToolResult(output + "\n[timed out]", ExitCode: -1, TimedOut: true, ReproCommand: reproCommand);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "run_aws: unexpected error executing '{Command}'", command);
            throw;
        }
        finally
        {
            proc?.Dispose();
        }
    }

    private static bool IsReadOnlyCommand(string command)
    {
        var trimmed = command.TrimStart();
        foreach (var allowed in s_allowedStsCommands)
        {
            if (trimmed.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var parts = SplitArgs(trimmed).ToArray();
        if (parts.Length < 2)
            return false;

        var operation = parts[1];
        foreach (var prefix in s_readOnlyPrefixes)
        {
            if (operation.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private async Task<CachedAwsSession> GetClusterSession(string cluster, ToolContext context, CancellationToken ct)
    {
        if (_sessionCache.TryGetValue(cluster, out var cached) && !cached.IsExpired(s_expiryBuffer))
            return cached;

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_sessionCache.TryGetValue(cluster, out cached) && !cached.IsExpired(s_expiryBuffer))
                return cached;

            _sessionCache.Remove(cluster);

            DiscoveredCluster info;
            lock (_discoveredClusters)
            {
                if (!_discoveredClusters.TryGetValue(cluster, out info!))
                    throw new InvalidOperationException(
                        $"Cluster {cluster} is not AWS-enabled. " +
                        $"Available: {string.Join(", ", _discoveredClusters.Keys)}");
            }

            var tokenResult = await RunOcQuiet(cluster,
                "create token investigator -n investigator --audience sts.amazonaws.com --duration 900s",
                context);
            if (tokenResult.ExitCode != 0)
                throw new InvalidOperationException($"Failed to create token on cluster {cluster}: {tokenResult.Output}");
            var webIdentityToken = tokenResult.Output.Trim();

            var session = await AssumeRoleWithWebIdentity(info.RoleArn, webIdentityToken, info.Region, ct);
            _sessionCache[cluster] = session;
            context?.Logger.LogInformation("run_aws: obtained AWS session for cluster {Cluster} (expires {Expiry})",
                cluster, session.ExpiresAt.ToString("u"));
            return session;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task<CachedAwsSession> GetAccountSession(string account, ToolContext context, CancellationToken ct)
    {
        if (_sessionCache.TryGetValue(account, out var cached) && !cached.IsExpired(s_expiryBuffer))
            return cached;

        var entry = _accounts.FirstOrDefault(a => a.Name.Equals(account, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            var available = string.Join(", ", _accounts.Select(a => a.Name));
            throw new InvalidOperationException($"Unknown account: {account}. Available: {available}");
        }

        var parent = !string.IsNullOrEmpty(entry.IntermediaryRoleArn)
            ? await GetIntermediarySession(entry, context, ct)
            : await GetClusterSession("core-ci", context, ct);

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_sessionCache.TryGetValue(account, out cached) && !cached.IsExpired(s_expiryBuffer))
                return cached;

            _sessionCache.Remove(account);

            var session = await AssumeRole(entry.RoleArn!, entry.Region!, parent, ct);
            _sessionCache[account] = session;
            context.Logger.LogInformation("run_aws: obtained AWS session for account {Account} (expires {Expiry})",
                account, session.ExpiresAt.ToString("u"));
            return session;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task<CachedAwsSession> GetIntermediarySession(
        AwsEntry entry, ToolContext context, CancellationToken ct)
    {
        var key = $"intermediary:{entry.IntermediaryRoleArn}";

        if (_sessionCache.TryGetValue(key, out var cached) && !cached.IsExpired(s_expiryBuffer))
            return cached;

        var coreCi = await GetClusterSession("core-ci", context, ct);

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_sessionCache.TryGetValue(key, out cached) && !cached.IsExpired(s_expiryBuffer))
                return cached;

            _sessionCache.Remove(key);
            var region = entry.IntermediaryRegion ?? entry.Region!;
            var session = await AssumeRole(entry.IntermediaryRoleArn!, region, coreCi, ct);
            _sessionCache[key] = session;
            context.Logger.LogInformation(
                "run_aws: obtained intermediary session via {IntermediaryRole} (expires {Expiry})",
                entry.IntermediaryRoleArn, session.ExpiresAt.ToString("u"));
            return session;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task<CachedAwsSession> AssumeRoleWithWebIdentity(
        string roleArn, string webIdentityToken, string region, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_awsPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["AWS_DEFAULT_REGION"] = region;
        foreach (var arg in new[]
        {
            "sts", "assume-role-with-web-identity",
            "--role-arn", roleArn,
            "--role-session-name", "investigator",
            "--web-identity-token", webIdentityToken,
            "--duration-seconds", "900",
            "--output", "json"
        })
            psi.ArgumentList.Add(arg);

        return await RunStsCommand(psi, roleArn, region, ct);
    }

    private async Task<CachedAwsSession> AssumeRole(
        string roleArn, string region, CachedAwsSession parentSession, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_awsPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["AWS_ACCESS_KEY_ID"] = parentSession.AccessKeyId;
        psi.Environment["AWS_SECRET_ACCESS_KEY"] = parentSession.SecretAccessKey;
        psi.Environment["AWS_SESSION_TOKEN"] = parentSession.SessionToken;
        psi.Environment["AWS_DEFAULT_REGION"] = region;
        foreach (var arg in new[]
        {
            "sts", "assume-role",
            "--role-arn", roleArn,
            "--role-session-name", "investigator",
            "--duration-seconds", "900",
            "--output", "json"
        })
            psi.ArgumentList.Add(arg);

        return await RunStsCommand(psi, roleArn, region, ct);
    }

    private async Task<CachedAwsSession> RunStsCommand(
        ProcessStartInfo psi, string roleArn, string region, CancellationToken ct)
    {
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start aws sts process");

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"aws sts failed (exit {proc.ExitCode}): {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var creds = doc.RootElement.GetProperty("Credentials");
        return new CachedAwsSession(
            RoleArn: roleArn,
            Region: region,
            AccessKeyId: creds.GetProperty("AccessKeyId").GetString()!,
            SecretAccessKey: creds.GetProperty("SecretAccessKey").GetString()!,
            SessionToken: creds.GetProperty("SessionToken").GetString()!,
            ExpiresAt: DateTimeOffset.Parse(creds.GetProperty("Expiration").GetString()!));
    }

    private async Task<ToolResult> RunOcQuiet(string cluster, string command, ToolContext? callerContext)
    {
        var json = JsonDocument.Parse(JsonSerializer.Serialize(new { cluster, command })).RootElement;
        var quietContext = new ToolContext(
            Logger: _logger,
            WorkspacePath: string.Empty,
            OnOutputLine: null,
            NextOutputNumber: () => 0,
            CallerId: "aws-executor");
        return await _ocExecutor.InvokeAsync(json, quietContext, CancellationToken.None);
    }

    private async Task<string?> DiscoverAwsAccountId(string cluster)
    {
        var result = await RunOcQuiet(cluster,
            "get secret aws-cloud-credentials -n openshift-machine-api -o jsonpath='{.data.credentials}'",
            callerContext: null);
        if (result.ExitCode != 0)
            return null;

        var credentialsB64 = result.Output.Trim().Trim('\'');
        if (string.IsNullOrEmpty(credentialsB64))
            return null;

        try
        {
            var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(credentialsB64));
            var match = s_accountIdRegex.Match(credentials);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task<string?> DiscoverRegion(string cluster)
    {
        var result = await RunOcQuiet(cluster,
            "get infrastructure cluster -o jsonpath='{.status.platformStatus.aws.region}'",
            callerContext: null);
        if (result.ExitCode != 0)
            return null;
        var region = result.Output.Trim().Trim('\'');
        return string.IsNullOrEmpty(region) ? null : region;
    }

    private static void KillProcess(Process? proc, string command, ILogger logger)
    {
        if (proc is null || proc.HasExited) return;
        try
        {
            proc.Kill(entireProcessTree: true);
            logger.LogDebug("run_aws: killed process tree for '{Command}'", command);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "run_aws: failed to kill process for '{Command}'", command);
        }
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
}
