using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Google.Cloud.Storage.V1;
using Investigator.Contracts;
using Investigator.Models;
using Microsoft.Extensions.Options;

namespace Investigator.Tools;

public sealed class ProwTool : IInvestigatorTool, ISystemPromptContributor
{
    private static readonly JsonElement s_paramSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "action": {
                "type": "string",
                "enum": ["jobs", "job_status", "log", "artifacts", "junit", "tide", "resolve_url"],
                "description": "jobs: list recent ProwJob runs. job_status: single job detail. log: download build log to workspace. artifacts: list/download artifacts. junit: parse JUnit XML results. tide: merge pool status. resolve_url: parse a Prow/Spyglass/GCS URL into coordinates."
            },
            "job_name": { "type": "string", "description": "ProwJob name pattern (supports substring match). For jobs, job_status, log, artifacts, junit." },
            "build_id": { "type": "string", "description": "Build ID (the numeric run identifier). For job_status, log, artifacts, junit." },
            "org": { "type": "string", "description": "GitHub org. For jobs filter." },
            "repo": { "type": "string", "description": "GitHub repo. For jobs, tide filter." },
            "pr": { "type": "integer", "description": "Pull request number. For jobs filter." },
            "state": { "type": "string", "enum": ["triggered", "pending", "success", "failure", "aborted", "error"], "description": "Filter by ProwJob state. For jobs." },
            "job_type": { "type": "string", "enum": ["presubmit", "postsubmit", "periodic", "batch"], "description": "Filter by job type. For jobs." },
            "count": { "type": "integer", "description": "Max results to return (default 20, max 100). For jobs." },
            "url": { "type": "string", "description": "A Prow/Spyglass/GCS URL to parse. For resolve_url." },
            "path": { "type": "string", "description": "Artifact path relative to job root (e.g. 'artifacts/test-name/must-gather/'). For artifacts." },
            "bucket": { "type": "string", "description": "GCS bucket name. Defaults to configured default. For artifacts, log, junit." },
            "storage_path": { "type": "string", "description": "Full GCS storage path (e.g. 'pr-logs/pull/org_repo/1234/job-name/1234567890'). For log, artifacts, junit." }
        },
        "required": ["action"]
    }
    """).RootElement.Clone();

    private static readonly Regex s_spyglassUrlRegex = new(
        @"prow\.ci\.openshift\.org/view/gs/([^/]+)/(.+?)/?$", RegexOptions.Compiled);

    private static readonly Regex s_gcsWebUrlRegex = new(
        @"gcsweb[^/]*\.apps\.[^/]+/gcs/([^/]+)/(.+?)/?$", RegexOptions.Compiled);

    private static readonly Regex s_deckJobUrlRegex = new(
        @"prow\.ci\.openshift\.org/?\?.*job=([^&]+)", RegexOptions.Compiled);

    private static readonly Regex s_htmlLinkRegex = new(
        @"href=""([^""]+)""", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ProwOptions _options;
    private readonly StorageClient? _storageClient;
    private readonly ILogger<ProwTool> _logger;

    public ProwTool(
        IHttpClientFactory httpClientFactory,
        IOptions<ProwOptions> options,
        ServiceCollectionExtensions.GcsClientHolder gcsHolder,
        ILogger<ProwTool> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Prow");
        _options = options.Value;
        _storageClient = gcsHolder.Client;
        _logger = logger;
    }

    public ToolDefinition Definition => new(
        Name: "prow",
        Description: "Query OpenShift CI Prow -- job results, build logs, test artifacts, "
            + "JUnit results, and Tide merge status. Logs are downloaded to the workspace.",
        ParameterSchema: s_paramSchema,
        DefaultTimeout: TimeSpan.FromSeconds(120));

    public string? GetSystemPromptSection()
    {
        var gcsMode = _storageClient is not null ? "GCS SDK (authenticated)" : "GCS Web (anonymous)";
        return $"""
            ## Prow CI tool
            The `prow` tool queries OpenShift CI at {_options.DeckUrl}.
            Artifact access: {gcsMode} via {_options.GcsWebUrl}.
            Actions:
            - jobs: list recent ProwJob runs, filterable by job_name, org, repo, pr, state, job_type, count.
            - job_status: full details for a single job (provide job_name + build_id, or storage_path).
            - log: download build log to workspace. Returns file path — use run_shell to read/grep.
            - artifacts: list or download artifacts from a job run (provide storage_path or job_name + build_id, optional path).
            - junit: parse JUnit XML from a job run, summarize pass/fail/skip.
            - tide: Tide merge pool status for a repo.
            - resolve_url: parse a Prow/Spyglass/GCS URL into structured coordinates for use by other actions.
            When a user shares a Prow link, use resolve_url first, then use the returned coordinates with other actions.
            """;
    }

    public async Task<ToolResult> InvokeAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var action = parameters.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";

        return action switch
        {
            "jobs" => await Jobs(parameters, context, ct),
            "job_status" => await JobStatus(parameters, context, ct),
            "log" => await Log(parameters, context, ct),
            "artifacts" => await Artifacts(parameters, context, ct),
            "junit" => await Junit(parameters, context, ct),
            "tide" => await Tide(parameters, context, ct),
            "resolve_url" => ResolveUrl(parameters, context),
            _ => new ToolResult($"Unknown action: {action}. Use jobs, job_status, log, artifacts, junit, tide, or resolve_url.", ExitCode: 1),
        };
    }

    // ------------------------------------------------------------------ resolve_url
    private ToolResult ResolveUrl(JsonElement p, ToolContext ctx)
    {
        var url = Str(p, "url");
        if (string.IsNullOrWhiteSpace(url))
            return new ToolResult("resolve_url requires a 'url' parameter.", ExitCode: 1);

        ctx.Logger.LogInformation("prow: resolve_url url=\"{Url}\"", url);

        var match = s_spyglassUrlRegex.Match(url);
        if (match.Success)
        {
            var bucket = match.Groups[1].Value;
            var storagePath = match.Groups[2].Value;
            return FormatResolvedUrl("spyglass", bucket, storagePath);
        }

        match = s_gcsWebUrlRegex.Match(url);
        if (match.Success)
        {
            var bucket = match.Groups[1].Value;
            var storagePath = match.Groups[2].Value;
            return FormatResolvedUrl("gcsweb", bucket, storagePath);
        }

        match = s_deckJobUrlRegex.Match(url);
        if (match.Success)
        {
            var jobName = Uri.UnescapeDataString(match.Groups[1].Value);
            return new ToolResult(
                $"Type: deck_search\nJob name: {jobName}\n\nUse action 'jobs' with job_name=\"{jobName}\" to list runs.");
        }

        return new ToolResult($"Could not parse URL: {url}\nSupported formats: Spyglass (/view/gs/...), GCS Web (gcsweb-.../gcs/...), Deck search (?job=...).", ExitCode: 1);
    }

    private static ToolResult FormatResolvedUrl(string source, string bucket, string storagePath)
    {
        var parts = storagePath.Split('/');
        string? jobName = null;
        string? buildId = null;

        // pr-logs/pull/org_repo/1234/job-name/1234567890
        // logs/job-name/1234567890
        if (parts.Length >= 2)
        {
            var lastPart = parts[^1];
            if (long.TryParse(lastPart, out _))
            {
                buildId = lastPart;
                jobName = parts[^2];
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Type: {source}");
        sb.AppendLine($"Bucket: {bucket}");
        sb.AppendLine($"Storage path: {storagePath}");
        if (jobName is not null) sb.AppendLine($"Job name: {jobName}");
        if (buildId is not null) sb.AppendLine($"Build ID: {buildId}");
        sb.AppendLine();
        sb.AppendLine("Use these coordinates with other actions:");
        sb.AppendLine($"  log:       action=log, bucket=\"{bucket}\", storage_path=\"{storagePath}\"");
        sb.AppendLine($"  artifacts: action=artifacts, bucket=\"{bucket}\", storage_path=\"{storagePath}\"");
        sb.AppendLine($"  junit:     action=junit, bucket=\"{bucket}\", storage_path=\"{storagePath}\"");
        sb.AppendLine($"  status:    action=job_status, bucket=\"{bucket}\", storage_path=\"{storagePath}\"");

        return new ToolResult(sb.ToString());
    }

    // ------------------------------------------------------------------ jobs
    private async Task<ToolResult> Jobs(JsonElement p, ToolContext ctx, CancellationToken ct)
    {
        var count = p.TryGetProperty("count", out var c) ? Math.Clamp(c.GetInt32(), 1, 100) : 20;

        ctx.Logger.LogInformation("prow: jobs query");

        var url = $"{_options.DeckUrl}/prowjobs.js?omit=annotations,labels,decoration_config,pod_spec";
        var json = await GetStringAsync(url, ct);
        if (json is null)
            return new ToolResult("Failed to fetch ProwJob data from Deck.", ExitCode: 1);

        // prowjobs.js returns {"items":[...]}
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items))
            return new ToolResult("Unexpected response format: no 'items' array.", ExitCode: 1);

        var jobName = Str(p, "job_name");
        var org = Str(p, "org");
        var repo = Str(p, "repo");
        var prNum = p.TryGetProperty("pr", out var prEl) ? prEl.GetInt32() : 0;
        var stateFilter = Str(p, "state");
        var typeFilter = Str(p, "job_type");

        var results = new List<JsonElement>();
        foreach (var item in items.EnumerateArray())
        {
            if (!MatchesFilters(item, jobName, org, repo, prNum, stateFilter, typeFilter))
                continue;
            results.Add(item);
            if (results.Count >= count)
                break;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# ProwJobs ({results.Count} results)");
        sb.AppendLine();

        if (results.Count == 0)
        {
            sb.AppendLine("No matching jobs found.");
            return new ToolResult(sb.ToString());
        }

        foreach (var item in results)
        {
            FormatJobSummary(sb, item);
        }

        return new ToolResult(sb.ToString());
    }

    private static bool MatchesFilters(
        JsonElement item, string jobName, string org, string repo,
        int prNum, string stateFilter, string typeFilter)
    {
        var spec = item.GetProperty("spec");
        var status = item.GetProperty("status");

        if (!string.IsNullOrEmpty(jobName))
        {
            var name = spec.TryGetProperty("job", out var j) ? j.GetString() ?? "" : "";
            if (!name.Contains(jobName, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrEmpty(stateFilter))
        {
            var state = status.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
            if (!state.Equals(stateFilter, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrEmpty(typeFilter))
        {
            var jt = spec.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (!jt.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrEmpty(org) || !string.IsNullOrEmpty(repo) || prNum > 0)
        {
            if (!spec.TryGetProperty("refs", out var refs))
                return false;

            if (!string.IsNullOrEmpty(org))
            {
                var refOrg = refs.TryGetProperty("org", out var o) ? o.GetString() ?? "" : "";
                if (!refOrg.Equals(org, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (!string.IsNullOrEmpty(repo))
            {
                var refRepo = refs.TryGetProperty("repo", out var r) ? r.GetString() ?? "" : "";
                if (!refRepo.Equals(repo, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (prNum > 0 && refs.TryGetProperty("pulls", out var pulls))
            {
                var found = false;
                foreach (var pull in pulls.EnumerateArray())
                {
                    if (pull.TryGetProperty("number", out var n) && n.GetInt32() == prNum)
                    { found = true; break; }
                }
                if (!found) return false;
            }
        }

        return true;
    }

    private static void FormatJobSummary(StringBuilder sb, JsonElement item)
    {
        var spec = item.GetProperty("spec");
        var status = item.GetProperty("status");

        var name = spec.TryGetProperty("job", out var j) ? j.GetString() ?? "" : "";
        var state = status.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
        var jobType = spec.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        var buildId = status.TryGetProperty("build_id", out var b) ? b.GetString() ?? "" : "";
        var prowUrl = status.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";

        var startTime = status.TryGetProperty("startTime", out var st) ? st.GetString() ?? "" : "";
        var completionTime = status.TryGetProperty("completionTime", out var ct2) ? ct2.GetString() ?? "" : "";

        var duration = "";
        if (DateTimeOffset.TryParse(startTime, out var startDt))
        {
            if (DateTimeOffset.TryParse(completionTime, out var endDt))
                duration = $" ({(endDt - startDt).TotalMinutes:F0}m)";
            else if (state is "pending" or "triggered")
                duration = $" (running {(DateTimeOffset.UtcNow - startDt).TotalMinutes:F0}m)";
        }

        var orgRepo = "";
        if (spec.TryGetProperty("refs", out var refs))
        {
            var org = refs.TryGetProperty("org", out var o) ? o.GetString() ?? "" : "";
            var repo = refs.TryGetProperty("repo", out var r) ? r.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(org)) orgRepo = $"{org}/{repo}";

            if (refs.TryGetProperty("pulls", out var pulls))
            {
                foreach (var pull in pulls.EnumerateArray())
                {
                    if (pull.TryGetProperty("number", out var n))
                    { orgRepo += $" PR#{n.GetInt32()}"; break; }
                }
            }
        }

        sb.AppendLine($"  {StateIcon(state)} [{buildId}] {name}: {state}{duration}");
        if (!string.IsNullOrEmpty(orgRepo))
            sb.AppendLine($"    {jobType}  {orgRepo}");
        if (!string.IsNullOrEmpty(prowUrl))
            sb.AppendLine($"    {prowUrl}");
        sb.AppendLine();
    }

    // ------------------------------------------------------------------ job_status
    private async Task<ToolResult> JobStatus(JsonElement p, ToolContext ctx, CancellationToken ct)
    {
        var (bucket, storagePath, err) = ResolveStoragePath(p);
        if (err is not null)
            return new ToolResult(err, ExitCode: 1);

        ctx.Logger.LogInformation("prow: job_status bucket={Bucket} path={Path}", bucket, storagePath);

        var sb = new StringBuilder();
        sb.AppendLine($"# Job Status: {storagePath}");
        sb.AppendLine();

        var finishedJson = await FetchArtifact(bucket!, storagePath!, "finished.json", ct);
        if (finishedJson is not null)
        {
            sb.AppendLine("## finished.json");
            try
            {
                using var doc = JsonDocument.Parse(finishedJson);
                var root = doc.RootElement;
                sb.AppendLine($"  Result:    {JsonStr(root, "result")}");
                if (root.TryGetProperty("timestamp", out var ts))
                    sb.AppendLine($"  Finished:  {DateTimeOffset.FromUnixTimeSeconds(ts.GetInt64()):u}");
                if (root.TryGetProperty("revision", out var rev))
                    sb.AppendLine($"  Revision:  {rev.GetString()}");
                if (root.TryGetProperty("metadata", out var meta))
                {
                    foreach (var prop in meta.EnumerateObject())
                        sb.AppendLine($"  {prop.Name}: {prop.Value}");
                }
            }
            catch (JsonException)
            {
                sb.AppendLine($"  (raw) {Truncate(finishedJson, 500)}");
            }
            sb.AppendLine();
        }

        var startedJson = await FetchArtifact(bucket!, storagePath!, "started.json", ct);
        if (startedJson is not null)
        {
            sb.AppendLine("## started.json");
            try
            {
                using var doc = JsonDocument.Parse(startedJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("timestamp", out var ts))
                    sb.AppendLine($"  Started:  {DateTimeOffset.FromUnixTimeSeconds(ts.GetInt64()):u}");
            }
            catch (JsonException)
            {
                sb.AppendLine($"  (raw) {Truncate(startedJson, 200)}");
            }
            sb.AppendLine();
        }

        var prowjobJson = await FetchArtifact(bucket!, storagePath!, "prowjob.json", ct);
        if (prowjobJson is not null)
        {
            sb.AppendLine("## prowjob.json (summary)");
            try
            {
                using var doc = JsonDocument.Parse(prowjobJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("spec", out var spec))
                {
                    sb.AppendLine($"  Job:     {JsonStr(spec, "job")}");
                    sb.AppendLine($"  Type:    {JsonStr(spec, "type")}");
                    sb.AppendLine($"  Cluster: {JsonStr(spec, "cluster")}");
                    if (spec.TryGetProperty("refs", out var refs))
                    {
                        sb.AppendLine($"  Org:     {JsonStr(refs, "org")}");
                        sb.AppendLine($"  Repo:    {JsonStr(refs, "repo")}");
                        sb.AppendLine($"  BaseSHA: {JsonStr(refs, "base_sha")}");
                    }
                }
            }
            catch (JsonException)
            {
                sb.AppendLine($"  (parse error)");
            }
            sb.AppendLine();
        }

        if (finishedJson is null && startedJson is null && prowjobJson is null)
            sb.AppendLine("No job metadata found at this path. Verify the storage_path is correct.");

        return new ToolResult(sb.ToString());
    }

    // ------------------------------------------------------------------ log
    private async Task<ToolResult> Log(JsonElement p, ToolContext ctx, CancellationToken ct)
    {
        var (bucket, storagePath, err) = ResolveStoragePath(p);
        if (err is not null)
            return new ToolResult(err, ExitCode: 1);

        ctx.Logger.LogInformation("prow: log download bucket={Bucket} path={Path}", bucket, storagePath);
        ctx.OnOutputLine?.Invoke("Downloading build log...");

        var safeName = SanitizePath(storagePath!);
        var outDir = Path.Combine(ctx.WorkspacePath, "tool_outputs", "prow_logs", safeName);
        Directory.CreateDirectory(outDir);
        var outFile = Path.Combine(outDir, "build-log.txt");

        long bytesWritten = 0;

        if (_storageClient is not null)
        {
            try
            {
                var objectName = $"{storagePath}/build-log.txt";
                await using var outStream = File.Create(outFile);
                await _storageClient.DownloadObjectAsync(bucket, objectName, outStream, cancellationToken: ct);
                bytesWritten = outStream.Length;
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return await FallbackLogDownload(bucket!, storagePath!, outFile, ctx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "prow: GCS SDK download failed, falling back to HTTP");
                return await FallbackLogDownload(bucket!, storagePath!, outFile, ctx, ct);
            }
        }
        else
        {
            return await FallbackLogDownload(bucket!, storagePath!, outFile, ctx, ct);
        }

        return FormatLogResult(outFile, bytesWritten);
    }

    private async Task<ToolResult> FallbackLogDownload(
        string bucket, string storagePath, string outFile, ToolContext ctx, CancellationToken ct)
    {
        var url = $"{_options.GcsWebUrl}/gcs/{bucket}/{storagePath}/build-log.txt";
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadContentAsStringAsync(response.Content, ct);
                return new ToolResult(
                    $"Failed to download build log ({response.StatusCode}). URL: {url}\n{Truncate(body, 300)}",
                    ExitCode: 1);
            }

            await using var srcStream = await response.Content.ReadAsStreamAsync(ct);
            await using var outStream = File.Create(outFile);
            await srcStream.CopyToAsync(outStream, ct);

            return FormatLogResult(outFile, outStream.Length);
        }
        catch (HttpRequestException ex)
        {
            return new ToolResult($"HTTP request failed: {ex.Message}", ExitCode: 1);
        }
    }

    private static ToolResult FormatLogResult(string outFile, long bytes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Build Log Downloaded");
        sb.AppendLine();
        sb.AppendLine($"File: {outFile}");
        sb.AppendLine($"Size: {bytes:N0} bytes ({bytes / 1024.0:F0} KB)");
        sb.AppendLine();
        sb.AppendLine("Use run_shell to search or read the log, for example:");
        sb.AppendLine($"  grep -i 'error\\|fail' \"{outFile}\" | head -50");
        sb.AppendLine($"  tail -100 \"{outFile}\"");

        return new ToolResult(sb.ToString());
    }

    // ------------------------------------------------------------------ artifacts
    private async Task<ToolResult> Artifacts(JsonElement p, ToolContext ctx, CancellationToken ct)
    {
        var (bucket, storagePath, err) = ResolveStoragePath(p);
        if (err is not null)
            return new ToolResult(err, ExitCode: 1);

        var subPath = Str(p, "path").TrimStart('/');
        var fullPath = string.IsNullOrEmpty(subPath) ? storagePath! : $"{storagePath}/{subPath}";

        ctx.Logger.LogInformation("prow: artifacts bucket={Bucket} path={Path}", bucket, fullPath);

        if (_storageClient is not null)
        {
            return await ListArtifactsSdk(bucket!, fullPath, ctx, ct);
        }

        return await ListArtifactsHttp(bucket!, fullPath, ctx, ct);
    }

    private async Task<ToolResult> ListArtifactsSdk(
        string bucket, string prefix, ToolContext ctx, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Artifacts: {prefix}");
        sb.AppendLine();

        try
        {
            var objects = _storageClient!.ListObjectsAsync(bucket, prefix + "/");
            var count = 0;
            await foreach (var obj in objects.WithCancellation(ct))
            {
                var name = obj.Name;
                if (name.Length > prefix.Length)
                    name = name[(prefix.Length + 1)..];
                var size = obj.Size ?? 0;
                sb.AppendLine($"  {size,12:N0}  {name}");
                count++;
                if (count >= 200) { sb.AppendLine("  ... (truncated at 200 entries)"); break; }
            }

            if (count == 0)
                sb.AppendLine("  (no artifacts found at this path)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "prow: GCS SDK list failed for {Bucket}/{Prefix}", bucket, prefix);
            return await ListArtifactsHttp(bucket, prefix, ctx, ct);
        }

        return new ToolResult(sb.ToString());
    }

    private async Task<ToolResult> ListArtifactsHttp(
        string bucket, string prefix, ToolContext ctx, CancellationToken ct)
    {
        var url = $"{_options.GcsWebUrl}/gcs/{bucket}/{prefix}/";
        var html = await GetStringAsync(url, ct);
        if (html is null)
            return new ToolResult($"Failed to list artifacts. URL: {url}", ExitCode: 1);

        var sb = new StringBuilder();
        sb.AppendLine($"# Artifacts: {prefix}");
        sb.AppendLine($"Source: {url}");
        sb.AppendLine();

        var links = ParseHtmlLinks(html);
        if (links.Count == 0)
        {
            sb.AppendLine("  (no artifacts found at this path)");
        }
        else
        {
            foreach (var link in links)
            {
                if (link == "../") continue;
                sb.AppendLine($"  {link}");
            }
        }

        return new ToolResult(sb.ToString());
    }

    // ------------------------------------------------------------------ junit
    private async Task<ToolResult> Junit(JsonElement p, ToolContext ctx, CancellationToken ct)
    {
        var (bucket, storagePath, err) = ResolveStoragePath(p);
        if (err is not null)
            return new ToolResult(err, ExitCode: 1);

        ctx.Logger.LogInformation("prow: junit bucket={Bucket} path={Path}", bucket, storagePath);

        var xmlContent = await FetchArtifact(bucket!, storagePath!, "prowjob_junit.xml", ct);
        if (xmlContent is null)
        {
            var junitPaths = new[]
            {
                "artifacts/junit_operator.xml",
                "artifacts/junit.xml",
            };
            foreach (var jp in junitPaths)
            {
                xmlContent = await FetchArtifact(bucket!, storagePath!, jp, ct);
                if (xmlContent is not null) break;
            }
        }

        if (xmlContent is null)
            return new ToolResult("No JUnit XML found. Tried prowjob_junit.xml and common artifact paths.", ExitCode: 1);

        return ParseJunit(xmlContent);
    }

    private static ToolResult ParseJunit(string xml)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# JUnit Test Results");
        sb.AppendLine();

        try
        {
            var doc = XDocument.Parse(xml);
            var suites = doc.Descendants("testsuite");

            var totalTests = 0;
            var totalFail = 0;
            var totalSkip = 0;
            var failures = new List<(string Suite, string Test, string Message)>();

            foreach (var suite in suites)
            {
                var suiteName = suite.Attribute("name")?.Value ?? "(unnamed)";
                var tests = int.TryParse(suite.Attribute("tests")?.Value, out var t) ? t : 0;
                var fail = int.TryParse(suite.Attribute("failures")?.Value, out var f) ? f : 0;
                var errs = int.TryParse(suite.Attribute("errors")?.Value, out var e) ? e : 0;
                var skip = int.TryParse(suite.Attribute("skipped")?.Value, out var sk) ? sk : 0;

                totalTests += tests;
                totalFail += fail + errs;
                totalSkip += skip;

                var icon = (fail + errs) > 0 ? "[FAIL]" : "[pass]";
                sb.AppendLine($"  {icon} {suiteName}: {tests} tests, {fail} failures, {errs} errors, {skip} skipped");

                foreach (var tc in suite.Elements("testcase"))
                {
                    var failEl = tc.Element("failure") ?? tc.Element("error");
                    if (failEl is not null)
                    {
                        var tcName = tc.Attribute("name")?.Value ?? "(unnamed)";
                        var msg = failEl.Attribute("message")?.Value ?? failEl.Value;
                        failures.Add((suiteName, tcName, Truncate(msg, 200)));
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine($"Total: {totalTests} tests, {totalTests - totalFail - totalSkip} passed, {totalFail} failed, {totalSkip} skipped");

            if (failures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Failed Tests");
                foreach (var (suite, test, msg) in failures.Take(30))
                {
                    sb.AppendLine();
                    sb.AppendLine($"  {suite} / {test}");
                    sb.AppendLine($"    {msg.Replace("\n", "\n    ")}");
                }
                if (failures.Count > 30)
                    sb.AppendLine($"\n  ... and {failures.Count - 30} more failures");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Failed to parse JUnit XML: {ex.Message}");
            sb.AppendLine($"First 500 chars: {Truncate(xml, 500)}");
        }

        return new ToolResult(sb.ToString());
    }

    // ------------------------------------------------------------------ tide
    private async Task<ToolResult> Tide(JsonElement p, ToolContext ctx, CancellationToken ct)
    {
        var repo = Str(p, "repo");
        var org = Str(p, "org");

        ctx.Logger.LogInformation("prow: tide org={Org} repo={Repo}", org, repo);

        var url = $"{_options.DeckUrl}/tide.js";
        var json = await GetStringAsync(url, ct);
        if (json is null)
            return new ToolResult("Failed to fetch Tide data from Deck.", ExitCode: 1);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var sb = new StringBuilder();
        sb.AppendLine("# Tide Status");
        sb.AppendLine();

        if (root.TryGetProperty("Queries", out var queries))
        {
            sb.AppendLine("## Tide Queries");
            var queryIdx = 0;
            foreach (var q in queries.EnumerateArray())
            {
                var repos = q.TryGetProperty("repos", out var r) ? r : default;
                var matchesFilter = string.IsNullOrEmpty(org) && string.IsNullOrEmpty(repo);
                if (!matchesFilter && repos.ValueKind == JsonValueKind.Array)
                {
                    foreach (var repoEntry in repos.EnumerateArray())
                    {
                        var repoStr = repoEntry.GetString() ?? "";
                        if (!string.IsNullOrEmpty(org) && !string.IsNullOrEmpty(repo))
                            matchesFilter = repoStr.Equals($"{org}/{repo}", StringComparison.OrdinalIgnoreCase);
                        else if (!string.IsNullOrEmpty(repo))
                            matchesFilter = repoStr.EndsWith($"/{repo}", StringComparison.OrdinalIgnoreCase);
                        else if (!string.IsNullOrEmpty(org))
                            matchesFilter = repoStr.StartsWith($"{org}/", StringComparison.OrdinalIgnoreCase);
                        if (matchesFilter) break;
                    }
                }

                if (!matchesFilter) continue;

                queryIdx++;
                var labels = q.TryGetProperty("labels", out var l) ? FormatJsonArray(l) : "";
                var missingLabels = q.TryGetProperty("missingLabels", out var ml) ? FormatJsonArray(ml) : "";
                sb.AppendLine($"  Query #{queryIdx}:");
                if (!string.IsNullOrEmpty(labels)) sb.AppendLine($"    Required labels: {labels}");
                if (!string.IsNullOrEmpty(missingLabels)) sb.AppendLine($"    Blocking labels: {missingLabels}");
                sb.AppendLine();
            }
        }

        if (root.TryGetProperty("Pools", out var pools))
        {
            sb.AppendLine("## Merge Pools");
            var poolCount = 0;
            foreach (var pool in pools.EnumerateArray())
            {
                var poolOrg = pool.TryGetProperty("Org", out var po) ? po.GetString() ?? "" : "";
                var poolRepo = pool.TryGetProperty("Repo", out var pr) ? pr.GetString() ?? "" : "";

                if (!string.IsNullOrEmpty(org) && !poolOrg.Equals(org, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(repo) && !poolRepo.Equals(repo, StringComparison.OrdinalIgnoreCase))
                    continue;

                poolCount++;
                var branch = pool.TryGetProperty("Branch", out var pb) ? pb.GetString() ?? "" : "";
                sb.AppendLine($"  {poolOrg}/{poolRepo} ({branch}):");

                AppendTidePoolSection(sb, pool, "SuccessPRs", "Ready to merge");
                AppendTidePoolSection(sb, pool, "PendingPRs", "Tests pending");
                AppendTidePoolSection(sb, pool, "MissingPRs", "Missing requirements");
                AppendTidePoolSection(sb, pool, "BatchPending", "Batch pending");
                sb.AppendLine();
            }

            if (poolCount == 0 && (!string.IsNullOrEmpty(org) || !string.IsNullOrEmpty(repo)))
                sb.AppendLine("  No matching pools found. The repo may not be enrolled in Tide.");
        }

        return new ToolResult(sb.ToString());
    }

    private static void AppendTidePoolSection(StringBuilder sb, JsonElement pool, string key, string label)
    {
        if (!pool.TryGetProperty(key, out var arr) || arr.GetArrayLength() == 0)
            return;

        sb.AppendLine($"    {label} ({arr.GetArrayLength()}):");
        foreach (var pr in arr.EnumerateArray())
        {
            var num = pr.TryGetProperty("Number", out var n) ? n.GetInt32() : 0;
            var title = pr.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "";
            sb.AppendLine($"      PR#{num}: {Truncate(title, 80)}");
        }
    }

    // ------------------------------------------------------------------ helpers: storage path resolution

    private (string? Bucket, string? StoragePath, string? Error) ResolveStoragePath(JsonElement p)
    {
        var bucket = Str(p, "bucket");
        var storagePath = Str(p, "storage_path");

        if (!string.IsNullOrEmpty(storagePath))
        {
            if (string.IsNullOrEmpty(bucket)) bucket = _options.DefaultBucket;
            return (bucket, storagePath, null);
        }

        var jobName = Str(p, "job_name");
        var buildId = Str(p, "build_id");

        if (string.IsNullOrEmpty(jobName) || string.IsNullOrEmpty(buildId))
            return (null, null, "Provide either storage_path, or both job_name and build_id.");

        if (string.IsNullOrEmpty(bucket)) bucket = _options.DefaultBucket;
        storagePath = $"logs/{jobName}/{buildId}";
        return (bucket, storagePath, null);
    }

    // ------------------------------------------------------------------ helpers: artifact fetch

    private async Task<string?> FetchArtifact(string bucket, string storagePath, string fileName, CancellationToken ct)
    {
        if (_storageClient is not null)
        {
            try
            {
                var objectName = $"{storagePath}/{fileName}";
                using var ms = new MemoryStream();
                await _storageClient.DownloadObjectAsync(bucket, objectName, ms, cancellationToken: ct);
                ms.Position = 0;
                return await new StreamReader(ms).ReadToEndAsync(ct);
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return await FetchArtifactHttp(bucket, storagePath, fileName, ct);
            }
            catch
            {
                return await FetchArtifactHttp(bucket, storagePath, fileName, ct);
            }
        }

        return await FetchArtifactHttp(bucket, storagePath, fileName, ct);
    }

    private async Task<string?> FetchArtifactHttp(string bucket, string storagePath, string fileName, CancellationToken ct)
    {
        var url = $"{_options.GcsWebUrl}/gcs/{bucket}/{storagePath}/{fileName}";
        try
        {
            var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            return await ReadContentAsStringAsync(response.Content, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ helpers: HTTP

    private async Task<string?> GetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync(url, ct);
            var body = await ReadContentAsStringAsync(response.Content, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("prow: GET {Url} returned {Status}: {Body}", url, response.StatusCode,
                    body.Length > 200 ? body[..200] : body);
                return null;
            }

            return body;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "prow: GET {Url} failed", url);
            return null;
        }
    }

    private static List<string> ParseHtmlLinks(string html)
    {
        var links = new List<string>();
        foreach (Match match in s_htmlLinkRegex.Matches(html))
        {
            var href = match.Groups[1].Value;
            if (href.StartsWith("http")) continue;
            var name = href.TrimEnd('/').Split('/')[^1];
            if (!string.IsNullOrEmpty(name))
                links.Add(href.EndsWith('/') ? $"{name}/" : name);
        }
        return links;
    }

    // ------------------------------------------------------------------ helpers: resilient content read

    private static async Task<string> ReadContentAsStringAsync(HttpContent content, CancellationToken ct)
    {
        try
        {
            return await content.ReadAsStringAsync(ct);
        }
        catch (InvalidOperationException)
        {
            var bytes = await content.ReadAsByteArrayAsync(ct);
            return Encoding.UTF8.GetString(bytes);
        }
    }

    // ------------------------------------------------------------------ helpers: formatting

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

    private static string JsonStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.ToString() : "";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    private static string SanitizePath(string path) =>
        Regex.Replace(path, @"[^\w\-/.]", "_");

    private static string FormatJsonArray(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array) return "";
        var items = new List<string>();
        foreach (var item in arr.EnumerateArray())
            items.Add(item.GetString() ?? item.ToString());
        return string.Join(", ", items);
    }

    private static string StateIcon(string state) => state switch
    {
        "success" => "[pass]",
        "failure" => "[FAIL]",
        "error" => "[ERR!]",
        "aborted" => "[skip]",
        "pending" => "[....]",
        "triggered" => "[>>>.]",
        _ => "[????]",
    };
}
