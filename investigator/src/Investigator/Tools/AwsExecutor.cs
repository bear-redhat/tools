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
            },
            "region": {
                "type": "string",
                "description": "AWS region override, e.g. 'us-west-2'. Omit to use the target's default region."
            }
        },
        "required": ["command"]
    }
    """).RootElement.Clone();

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
    private readonly string _configPath;
    private readonly OcExecutor _ocExecutor;
    private readonly List<AwsEntry> _accounts = [];
    private readonly Dictionary<string, DiscoveredCluster> _discoveredClusters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _profileMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<AwsExecutor> _logger;
    private readonly Task _probeTask;

    public record AwsTarget(string Name, AwsTargetKind Kind, string? Description);
    public enum AwsTargetKind { Cluster, Account }

    private sealed record DiscoveredCluster(
        string RoleArn, string Region,
        string? IntermediaryRoleArn, string? IntermediaryRegion);

    public AwsExecutor(IOptions<AwsOptions> options, OcExecutor ocExecutor, ILogger<AwsExecutor> logger)
    {
        _ocExecutor = ocExecutor;
        _logger = logger;
        _configPath = Path.Combine(Path.GetTempPath(), "investigator-aws-config");
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
            var clusterEntries = new Dictionary<string, AwsEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in opts.Clusters.Where(c => !string.IsNullOrEmpty(c.Name)))
            {
                if (entry.RoleArn is "")
                {
                    skipSet.Add(entry.Name);
                    logger.LogInformation("AWS explicitly disabled for cluster {Cluster}", entry.Name);
                }
                else
                {
                    clusterEntries[entry.Name] = entry;
                }
            }

            var tasks = allClusters
                .Where(c => !skipSet.Contains(c))
                .Select(async cluster =>
                {
                    try
                    {
                        clusterEntries.TryGetValue(cluster, out var entry);
                        string? region;

                        if (entry is not null && !string.IsNullOrEmpty(entry.RoleArn))
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
                                _discoveredClusters[cluster] = new(
                                    entry.RoleArn!, region,
                                    entry.IntermediaryRoleArn, entry.IntermediaryRegion);
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
                            _discoveredClusters[cluster] = new(
                                roleArn, region,
                                entry?.IntermediaryRoleArn, entry?.IntermediaryRegion);
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

            GenerateAwsConfig();
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
        var region = parameters.TryGetProperty("region", out var rProp) ? rProp.GetString() : null;

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

        await _probeTask;

        var targetName = hasCluster ? cluster! : account!;
        string? profileName;
        lock (_profileMap)
        {
            if (!_profileMap.TryGetValue(targetName, out profileName))
            {
                var available = string.Join(", ", _profileMap.Keys);
                return new ToolResult(
                    $"Error: no AWS profile for '{targetName}'. Available: {available}", ExitCode: 1);
            }
        }

        var args = SplitArgs(command).ToList();
        var regionSuffix = !string.IsNullOrWhiteSpace(region) ? $" --region {region}" : "";
        var reproCommand = $"aws --profile {profileName}{regionSuffix} {command}";

        context.Logger.LogDebug("run_aws: executing 'aws {Command}' against {Target} (profile={Profile})",
            command, hasCluster ? $"cluster {cluster}" : $"account {account}", profileName);

        var psi = new ProcessStartInfo(_awsPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["AWS_CONFIG_FILE"] = _configPath;
        psi.ArgumentList.Add("--profile");
        psi.ArgumentList.Add(profileName);
        if (!string.IsNullOrWhiteSpace(region))
        {
            psi.ArgumentList.Add("--region");
            psi.ArgumentList.Add(region);
        }
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

    private void GenerateAwsConfig()
    {
        var sb = new StringBuilder();
        var intermediaryProfiles = new Dictionary<string, string>(StringComparer.Ordinal);

        var tokenFile = Environment.GetEnvironmentVariable("AWS_WEB_IDENTITY_TOKEN_FILE")
            ?? "/var/run/secrets/aws/token";
        var roleArn = Environment.GetEnvironmentVariable("AWS_ROLE_ARN");
        if (string.IsNullOrEmpty(roleArn))
        {
            _logger.LogWarning("AWS_ROLE_ARN not set, cannot generate AWS config profiles");
            return;
        }

        sb.AppendLine("[profile home]");
        sb.AppendLine($"web_identity_token_file = {tokenFile}");
        sb.AppendLine($"role_arn = {roleArn}");
        sb.AppendLine("role_session_name = investigator");
        sb.AppendLine();

        string EnsureIntermediaryProfile(string arn, string region)
        {
            if (intermediaryProfiles.TryGetValue(arn, out var existing))
                return existing;
            var parts = arn.Split(':');
            var name = $"intermediary--{(parts.Length > 4 ? parts[4] : arn.GetHashCode().ToString("x"))}";
            intermediaryProfiles[arn] = name;
            sb.AppendLine($"[profile {name}]");
            sb.AppendLine("source_profile = home");
            sb.AppendLine($"role_arn = {arn}");
            sb.AppendLine("role_session_name = investigator");
            sb.AppendLine($"region = {region}");
            sb.AppendLine();
            return name;
        }

        lock (_discoveredClusters)
        {
            foreach (var (cluster, info) in _discoveredClusters)
            {
                if (string.Equals(info.RoleArn, roleArn, StringComparison.OrdinalIgnoreCase))
                {
                    _profileMap[cluster] = "home";
                    _logger.LogInformation("AWS cluster {Cluster}: home account, using home profile directly", cluster);
                    continue;
                }

                var profileName = $"cluster--{cluster}";
                var sourceProfile = !string.IsNullOrEmpty(info.IntermediaryRoleArn)
                    ? EnsureIntermediaryProfile(info.IntermediaryRoleArn, info.IntermediaryRegion ?? info.Region)
                    : "home";
                sb.AppendLine($"[profile {profileName}]");
                sb.AppendLine($"source_profile = {sourceProfile}");
                sb.AppendLine($"role_arn = {info.RoleArn}");
                sb.AppendLine("role_session_name = investigator");
                sb.AppendLine($"region = {info.Region}");
                sb.AppendLine();
                _profileMap[cluster] = profileName;
            }
        }

        foreach (var entry in _accounts)
        {
            var profileName = $"account--{entry.Name}";
            var sourceProfile = !string.IsNullOrEmpty(entry.IntermediaryRoleArn)
                ? EnsureIntermediaryProfile(entry.IntermediaryRoleArn, entry.IntermediaryRegion ?? entry.Region!)
                : "home";
            sb.AppendLine($"[profile {profileName}]");
            sb.AppendLine($"source_profile = {sourceProfile}");
            sb.AppendLine($"role_arn = {entry.RoleArn}");
            sb.AppendLine("role_session_name = investigator");
            sb.AppendLine($"region = {entry.Region}");
            sb.AppendLine();
            _profileMap[entry.Name] = profileName;
        }

        File.WriteAllText(_configPath, sb.ToString());
        _logger.LogInformation("AWS config written to {Path} ({ProfileCount} target profiles, {IntermediaryCount} intermediary profiles)",
            _configPath, _profileMap.Count, intermediaryProfiles.Count);
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
