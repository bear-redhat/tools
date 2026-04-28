using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Investigator.Services;
using Microsoft.Extensions.Options;

namespace Investigator.Tools;

public sealed class GitHubTool : IInvestigatorTool, ISystemPromptContributor
{
    private static readonly JsonElement s_paramSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "action": {
                "type": "string",
                "enum": ["pr_status", "pr_files", "pr_comments", "workflow_runs", "workflow_logs", "search", "get_file", "list_directory", "get_tree", "clone_repo"],
                "description": "pr_status: PR metadata + check runs + commit statuses. pr_files: changed files. pr_comments: review and issue comments. workflow_runs: list recent Actions runs. workflow_logs: download full logs for a run. search: search issues/PRs. get_file: download a file to workspace. list_directory: list entries in a directory. get_tree: recursively list the repo tree. clone_repo: shallow-clone a repo to workspace."
            },
            "owner": { "type": "string", "description": "Repository owner (org or user)." },
            "repo": { "type": "string", "description": "Repository name." },
            "number": { "type": "integer", "description": "PR or issue number. Required for pr_status, pr_files, pr_comments. Optional for workflow_runs (derives head branch)." },
            "query": { "type": "string", "description": "Search query with GitHub qualifiers (e.g. 'is:pr repo:openshift/release label:approved'). For search action." },
            "run_id": { "type": "integer", "description": "Workflow run ID. Required for workflow_logs." },
            "workflow": { "type": "string", "description": "Workflow filename (e.g. ci.yaml) or ID. For workflow_runs only." },
            "branch": { "type": "string", "description": "Filter workflow runs by branch. For workflow_runs only." },
            "status": { "type": "string", "description": "Filter workflow runs by status (queued, in_progress, completed). For workflow_runs only." },
            "count": { "type": "integer", "description": "Number of results to return (default 10, max 50). For workflow_runs only." },
            "path": { "type": "string", "description": "File or directory path within the repo. Required for get_file, list_directory. Optional for get_tree (defaults to root)." },
            "ref": { "type": "string", "description": "Branch, tag, or commit SHA. Optional for get_file, list_directory, get_tree, clone_repo (defaults to repo default branch)." },
            "depth": { "type": "integer", "description": "Clone depth (default 1 = shallow). Set to 0 for full clone. For clone_repo only." }
        },
        "required": ["action"]
    }
    """).RootElement.Clone();

    private const string ApiBase = "https://api.github.com";

    private readonly HttpClient _httpClient;
    private readonly GitHubAppAuth _auth;
    private readonly GitHubOptions _options;
    private readonly ILogger<GitHubTool> _logger;

    public GitHubTool(IHttpClientFactory httpClientFactory, GitHubAppAuth auth,
        IOptions<GitHubOptions> options, ILogger<GitHubTool> logger)
    {
        _httpClient = httpClientFactory.CreateClient("GitHub");
        _auth = auth;
        _options = options.Value;
        _logger = logger;

        if (_auth.IsConfigured)
            _logger.LogInformation("github: initialised in authenticated mode (GitHub App)");
        else
            _logger.LogInformation("github: initialised in unauthenticated mode (public repos only, 60 req/hr)");
    }

    public ToolDefinition Definition => new(
        Name: "github",
        Description: "Query the GitHub API -- pull requests, checks, comments, workflow runs/logs, search, "
            + "file/directory contents, repo tree, and shallow clone.",
        ParameterSchema: s_paramSchema,
        DefaultTimeout: TimeSpan.FromSeconds(120));

    public string? GetSystemPromptSection()
    {
        var mode = _auth.IsConfigured ? "authenticated (GitHub App)" : "unauthenticated (public repos only, 60 req/hr)";
        return $"""
            ## GitHub tool
            The `github` tool queries the GitHub REST API ({mode}).
            Actions: pr_status, pr_files, pr_comments, workflow_runs, workflow_logs, search, get_file, list_directory, get_tree, clone_repo.
            For PR actions, provide owner, repo, and number.
            For workflow_runs, provide owner and repo; optionally workflow, branch, status, count, or number (to filter by PR branch).
            For workflow_logs, provide owner, repo, and run_id. Logs are downloaded to the workspace — use run_shell to search them.
            For search, provide a query using GitHub search qualifiers.
            For get_file, provide owner, repo, path, and optionally ref. The file is saved to disk — use run_shell to read it.
            For list_directory, provide owner, repo, path (or omit path for repo root), and optionally ref.
            For get_tree, provide owner, repo, and optionally ref. Returns a recursive listing of all files.
            For clone_repo, provide owner and repo, optionally ref and depth. Clones the repo to the workspace for local browsing (repos over {_options.MaxCloneSizeKb / 1024} MB are refused — use get_file instead).
            Do NOT use run_shell with curl to access github.com or api.github.com — use this tool instead.
            """;
    }

    public async Task<ToolResult> InvokeAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var action = parameters.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";

        return action switch
        {
            "pr_status" => await PrStatus(parameters, context, ct),
            "pr_files" => await PrFiles(parameters, context, ct),
            "pr_comments" => await PrComments(parameters, context, ct),
            "workflow_runs" => await WorkflowRuns(parameters, context, ct),
            "workflow_logs" => await WorkflowLogs(parameters, context, ct),
            "search" => await Search(parameters, context, ct),
            "get_file" => await GetFile(parameters, context, ct),
            "list_directory" => await ListDirectory(parameters, context, ct),
            "get_tree" => await GetTree(parameters, context, ct),
            "clone_repo" => await CloneRepo(parameters, context, ct),
            _ => new ToolResult($"Unknown action: {action}.", ExitCode: 1),
        };
    }

    // ------------------------------------------------------------------ pr_status
    private async Task<ToolResult> PrStatus(JsonElement p, ToolContext ctx, CancellationToken ct)
    {
        if (!TryGetPrParams(p, out var owner, out var repo, out var number, out var error))
            return new ToolResult(error, ExitCode: 1);

        ctx.Logger.LogInformation("github: pr_status {Owner}/{Repo}#{Number}", owner, repo, number);

        var prJson = await GetAsync($"{ApiBase}/repos/{owner}/{repo}/pulls/{number}", ct);
        if (prJson is null)
            return new ToolResult($"Failed to fetch PR {owner}/{repo}#{number}", ExitCode: 1);

        using var prDoc = JsonDocument.Parse(prJson);
        var pr = prDoc.RootElement;

        var sha = pr.GetProperty("head").GetProperty("sha").GetString() ?? "";
        var sb = new StringBuilder();

        sb.AppendLine($"# PR {owner}/{repo}#{number}");
        sb.AppendLine();
        sb.AppendLine($"Title:     {Str(pr, "title")}");
        sb.AppendLine($"State:     {Str(pr, "state")}");
        sb.AppendLine($"Author:    {pr.GetProperty("user").GetProperty("login").GetString()}");
        sb.AppendLine($"Branch:    {pr.GetProperty("head").GetProperty("ref").GetString()} -> {pr.GetProperty("base").GetProperty("ref").GetString()}");
        sb.AppendLine($"Mergeable: {(pr.TryGetProperty("mergeable", out var m) ? m.ToString() : "unknown")}");
        sb.AppendLine($"Draft:     {pr.TryGetProperty("draft", out var d) && d.GetBoolean()}");
        sb.AppendLine($"URL:       {Str(pr, "html_url")}");
        sb.AppendLine($"Head SHA:  {sha}");

        if (pr.TryGetProperty("labels", out var labels))
        {
            var labelNames = new List<string>();
            foreach (var lbl in labels.EnumerateArray())
                labelNames.Add(lbl.GetProperty("name").GetString() ?? "");
            if (labelNames.Count > 0)
                sb.AppendLine($"Labels:    {string.Join(", ", labelNames)}");
        }

        sb.AppendLine();

        if (!string.IsNullOrEmpty(sha))
        {
            await AppendCheckRuns(sb, owner, repo, sha, ct);
            await AppendCommitStatuses(sb, owner, repo, sha, ct);
        }

        return new ToolResult(sb.ToString());
    }

    private async Task AppendCheckRuns(StringBuilder sb, string owner, string repo, string sha, CancellationToken ct)
    {
        var json = await GetAsync($"{ApiBase}/repos/{owner}/{repo}/commits/{sha}/check-runs?per_page=100", ct);
        if (json is null) return;

        using var doc = JsonDocument.Parse(json);
        var runs = doc.RootElement.GetProperty("check_runs");
        var count = runs.GetArrayLength();

        sb.AppendLine($"## Check Runs ({count})");
        if (count == 0) { sb.AppendLine("  (none)"); sb.AppendLine(); return; }

        foreach (var run in runs.EnumerateArray())
        {
            var name = Str(run, "name");
            var status = Str(run, "status");
            var conclusion = Str(run, "conclusion");
            var display = status == "completed" ? conclusion : status;
            sb.AppendLine($"  {StatusIcon(conclusion, status)} {name}: {display}");
        }
        sb.AppendLine();
    }

    private async Task AppendCommitStatuses(StringBuilder sb, string owner, string repo, string sha, CancellationToken ct)
    {
        var json = await GetAsync($"{ApiBase}/repos/{owner}/{repo}/commits/{sha}/status", ct);
        if (json is null) return;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var state = Str(root, "state");
        var statuses = root.GetProperty("statuses");
        var count = statuses.GetArrayLength();

        sb.AppendLine($"## Commit Statuses (combined: {state}, {count} contexts)");
        if (count == 0) { sb.AppendLine("  (none)"); sb.AppendLine(); return; }

        foreach (var s in statuses.EnumerateArray())
        {
            var context = Str(s, "context");
            var sState = Str(s, "state");
            var desc = Str(s, "description");
            sb.AppendLine($"  {StatusIcon(sState, sState)} {context}: {sState} — {desc}");
        }
        sb.AppendLine();
    }

    // ------------------------------------------------------------------ pr_files
    private async Task<ToolResult> PrFiles(JsonElement p, ToolContext ctx, CancellationToken ct)
    {
        if (!TryGetPrParams(p, out var owner, out var repo, out var number, out var error))
            return new ToolResult(error, ExitCode: 1);

        ctx.Logger.LogInformation("github: pr_files {Owner}/{Repo}#{Number}", owner, repo, number);

        var json = await GetAsync($"{ApiBase}/repos/{owner}/{repo}/pulls/{number}/files?per_page=100", ct);
        if (json is null)
            return new ToolResult($"Failed to fetch files for {owner}/{repo}#{number}", ExitCode: 1);

        using var doc = JsonDocument.Parse(json);
        var files = doc.RootElement;
        var sb = new StringBuilder();
        sb.AppendLine($"# Changed files in {owner}/{repo}#{number} ({files.GetArrayLength()} files)");
        sb.AppendLine();

        foreach (var file in files.EnumerateArray())
        {
            var filename = Str(file, "filename");
            var status = Str(file, "status");
            var additions = file.TryGetProperty("additions", out var add) ? add.GetInt32() : 0;
            var deletions = file.TryGetProperty("deletions", out var del) ? del.GetInt32() : 0;
            sb.AppendLine($"  {status,-10} +{additions,-4} -{deletions,-4} {filename}");
        }

        return new ToolResult(sb.ToString());
    }

    // ------------------------------------------------------------------ pr_comments
    private async Task<ToolResult> PrComments(JsonElement p, ToolContext ctx, CancellationToken ct)
    {
        if (!TryGetPrParams(p, out var owner, out var repo, out var number, out var error))
            return new ToolResult(error, ExitCode: 1);

        ctx.Logger.LogInformation("github: pr_comments {Owner}/{Repo}#{Number}", owner, repo, number);

        var sb = new StringBuilder();
        sb.AppendLine($"# Comments on {owner}/{repo}#{number}");
        sb.AppendLine();

        var issueJson = await GetAsync($"{ApiBase}/repos/{owner}/{repo}/issues/{number}/comments?per_page=100", ct);
        if (issueJson is not null)
        {
            using var doc = JsonDocument.Parse(issueJson);
            var comments = doc.RootElement;
            sb.AppendLine($"## Issue Comments ({comments.GetArrayLength()})");
            foreach (var c in comments.EnumerateArray())
            {
                var author = c.GetProperty("user").GetProperty("login").GetString() ?? "";
                var created = Str(c, "created_at");
                var body = Str(c, "body");
                if (body.Length > 500) body = body[..500] + "...";
                sb.AppendLine($"  [{created}] {author}:");
                sb.AppendLine($"    {body.Replace("\n", "\n    ")}");
                sb.AppendLine();
            }
        }

        var reviewJson = await GetAsync($"{ApiBase}/repos/{owner}/{repo}/pulls/{number}/comments?per_page=100", ct);
        if (reviewJson is not null)
        {
            using var doc = JsonDocument.Parse(reviewJson);
            var comments = doc.RootElement;
            sb.AppendLine($"## Review Comments ({comments.GetArrayLength()})");
            foreach (var c in comments.EnumerateArray())
            {
                var author = c.GetProperty("user").GetProperty("login").GetString() ?? "";
                var path = Str(c, "path");
                var body = Str(c, "body");
                if (body.Length > 500) body = body[..500] + "...";
                sb.AppendLine($"  {author} on {path}:");
                sb.AppendLine($"    {body.Replace("\n", "\n    ")}");
                sb.AppendLine();
            }
        }

        return new ToolResult(sb.ToString());
    }

    // ------------------------------------------------------------------ workflow_runs
    private async Task<ToolResult> WorkflowRuns(JsonElement p, ToolContext ctx, CancellationToken ct)
    {
        if (!TryGetRepoParams(p, out var owner, out var repo, out var error))
            return new ToolResult(error, ExitCode: 1);

        var count = p.TryGetProperty("count", out var c) ? Math.Clamp(c.GetInt32(), 1, 50) : 10;
        var workflow = p.TryGetProperty("workflow", out var w) ? w.GetString() : null;
        var branch = p.TryGetProperty("branch", out var b) ? b.GetString() : null;
        var status = p.TryGetProperty("status", out var s) ? s.GetString() : null;

        if (p.TryGetProperty("number", out var numEl) && branch is null)
        {
            var prNum = numEl.GetInt32();
            ctx.Logger.LogInformation("github: workflow_runs deriving branch from PR {Owner}/{Repo}#{Number}", owner, repo, prNum);
            var prJson = await GetAsync($"{ApiBase}/repos/{owner}/{repo}/pulls/{prNum}", ct);
            if (prJson is not null)
            {
                using var prDoc = JsonDocument.Parse(prJson);
                branch = prDoc.RootElement.GetProperty("head").GetProperty("ref").GetString();
            }
        }

        string url;
        if (!string.IsNullOrEmpty(workflow))
            url = $"{ApiBase}/repos/{owner}/{repo}/actions/workflows/{Uri.EscapeDataString(workflow)}/runs?per_page={count}";
        else
            url = $"{ApiBase}/repos/{owner}/{repo}/actions/runs?per_page={count}";

        if (!string.IsNullOrEmpty(branch)) url += $"&branch={Uri.EscapeDataString(branch)}";
        if (!string.IsNullOrEmpty(status)) url += $"&status={Uri.EscapeDataString(status)}";

        ctx.Logger.LogInformation("github: workflow_runs {Owner}/{Repo} workflow={Workflow} branch={Branch} status={Status}",
            owner, repo, workflow ?? "(all)", branch ?? "(all)", status ?? "(all)");

        var json = await GetAsync(url, ct);
        if (json is null)
            return new ToolResult($"Failed to fetch workflow runs for {owner}/{repo}", ExitCode: 1);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var total = root.GetProperty("total_count").GetInt32();
        var runs = root.GetProperty("workflow_runs");

        var sb = new StringBuilder();
        sb.AppendLine($"# Workflow Runs for {owner}/{repo} ({runs.GetArrayLength()} of {total} total)");
        sb.AppendLine();

        foreach (var run in runs.EnumerateArray())
        {
            var runId = run.GetProperty("id").GetInt64();
            var name = Str(run, "name");
            var runStatus = Str(run, "status");
            var conclusion = Str(run, "conclusion");
            var display = runStatus == "completed" ? conclusion : runStatus;
            var headBranch = Str(run, "head_branch");
            var ev = Str(run, "event");
            var htmlUrl = Str(run, "html_url");

            var duration = "";
            if (run.TryGetProperty("created_at", out var createdEl) && run.TryGetProperty("updated_at", out var updatedEl))
            {
                if (DateTimeOffset.TryParse(createdEl.GetString(), out var created) &&
                    DateTimeOffset.TryParse(updatedEl.GetString(), out var updated))
                    duration = $" ({(updated - created).TotalMinutes:F0}m)";
            }

            sb.AppendLine($"  {StatusIcon(conclusion, runStatus)} [{runId}] {name}: {display}{duration}");
            sb.AppendLine($"    branch={headBranch}  event={ev}  {htmlUrl}");
        }

        return new ToolResult(sb.ToString());
    }

    // ------------------------------------------------------------------ workflow_logs
    private async Task<ToolResult> WorkflowLogs(JsonElement p, ToolContext ctx, CancellationToken ct)
    {
        if (!TryGetRepoParams(p, out var owner, out var repo, out var error))
            return new ToolResult(error, ExitCode: 1);

        if (!p.TryGetProperty("run_id", out var runIdEl))
            return new ToolResult("workflow_logs requires 'run_id'.", ExitCode: 1);

        var runId = runIdEl.GetInt64();
        ctx.Logger.LogInformation("github: workflow_logs {Owner}/{Repo} run={RunId}", owner, repo, runId);

        var url = $"{ApiBase}/repos/{owner}/{repo}/actions/runs/{runId}/logs";
        byte[] zipBytes;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            await ApplyAuth(request, ct);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return new ToolResult($"Failed to download logs ({response.StatusCode}): {body}", ExitCode: 1);
            }

            zipBytes = await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            return new ToolResult($"HTTP request failed: {ex.Message}", ExitCode: 1);
        }

        var outDir = Path.Combine(ctx.WorkspacePath, "tool_outputs", "workflow_logs", runId.ToString());
        Directory.CreateDirectory(outDir);

        var files = new List<(string Name, long Size)>();
        using (var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var dest = Path.Combine(outDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                var dir = Path.GetDirectoryName(dest);
                if (dir is not null) Directory.CreateDirectory(dir);

                using var src = entry.Open();
                using var dst = File.Create(dest);
                await src.CopyToAsync(dst, ct);

                files.Add((entry.FullName, entry.Length));
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Workflow Logs for run {runId} ({owner}/{repo})");
        sb.AppendLine($"Extracted {files.Count} log files to: {outDir}");
        sb.AppendLine();

        foreach (var (name, size) in files.OrderBy(f => f.Name))
        {
            sb.AppendLine($"  {size,8:N0} bytes  {name}");
        }

        sb.AppendLine();
        sb.AppendLine("Use run_shell to search or read specific log files.");

        return new ToolResult(sb.ToString());
    }

    // ------------------------------------------------------------------ search
    private async Task<ToolResult> Search(JsonElement p, ToolContext ctx, CancellationToken ct)
    {
        var query = p.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(query))
            return new ToolResult("search requires a 'query' parameter.", ExitCode: 1);

        ctx.Logger.LogInformation("github: search query=\"{Query}\"", query);

        var url = $"{ApiBase}/search/issues?q={Uri.EscapeDataString(query)}&per_page=20";
        var json = await GetAsync(url, ct);
        if (json is null)
            return new ToolResult("Search request failed.", ExitCode: 1);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var total = root.GetProperty("total_count").GetInt32();
        var items = root.GetProperty("items");

        var sb = new StringBuilder();
        sb.AppendLine($"# Search results ({items.GetArrayLength()} of {total} total)");
        sb.AppendLine($"Query: {query}");
        sb.AppendLine();

        foreach (var item in items.EnumerateArray())
        {
            var title = Str(item, "title");
            var htmlUrl = Str(item, "html_url");
            var state = Str(item, "state");
            var user = item.GetProperty("user").GetProperty("login").GetString() ?? "";
            var isPr = item.TryGetProperty("pull_request", out _);

            sb.AppendLine($"  [{(isPr ? "PR" : "Issue")}] {title}");
            sb.AppendLine($"    state={state}  author={user}  {htmlUrl}");
        }

        return new ToolResult(sb.ToString());
    }

    // ------------------------------------------------------------------ get_file
    private async Task<ToolResult> GetFile(JsonElement p, ToolContext ctx, CancellationToken ct)
    {
        if (!TryGetRepoParams(p, out var owner, out var repo, out var error))
            return new ToolResult(error, ExitCode: 1);

        var path = p.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(path))
            return new ToolResult("get_file requires 'path'.", ExitCode: 1);

        var gitRef = p.TryGetProperty("ref", out var refEl) ? refEl.GetString() : null;
        ctx.Logger.LogInformation("github: get_file {Owner}/{Repo}/{Path} ref={Ref}", owner, repo, path, gitRef ?? "(default)");

        var url = $"{ApiBase}/repos/{owner}/{repo}/contents/{path.TrimStart('/')}";
        if (!string.IsNullOrEmpty(gitRef)) url += $"?ref={Uri.EscapeDataString(gitRef)}";

        var json = await GetAsync(url, ct);
        if (json is null)
            return new ToolResult($"Failed to fetch {owner}/{repo}/{path}", ExitCode: 1);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
            return new ToolResult($"Path '{path}' is a directory, not a file. Use list_directory instead.", ExitCode: 1);

        var type = Str(root, "type");
        if (type != "file")
            return new ToolResult($"Path '{path}' is a {type}, not a file.", ExitCode: 1);

        var sha = Str(root, "sha");
        var size = root.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0;
        var refLabel = gitRef ?? sha[..Math.Min(sha.Length, 12)];

        byte[] content;
        var encoding = Str(root, "encoding");
        if (encoding == "base64")
        {
            var base64 = Str(root, "content").Replace("\n", "");
            content = Convert.FromBase64String(base64);
        }
        else
        {
            var rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{refLabel}/{path.TrimStart('/')}";
            ctx.Logger.LogInformation("github: get_file falling back to raw download for {Owner}/{Repo}/{Path}", owner, repo, path);
            var rawBytes = await GetBytesAsync(rawUrl, ct);
            if (rawBytes is null)
                return new ToolResult($"Failed to download raw content for {owner}/{repo}/{path}", ExitCode: 1);
            content = rawBytes;
        }

        var outDir = Path.Combine(ctx.WorkspacePath, "tool_outputs", "github_files", owner, repo, refLabel);
        var outPath = Path.Combine(outDir, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(outPath);
        if (dir is not null) Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(outPath, content, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"# File saved: {owner}/{repo}/{path}");
        sb.AppendLine($"Ref:   {refLabel}");
        sb.AppendLine($"SHA:   {sha}");
        sb.AppendLine($"Size:  {size:N0} bytes");
        sb.AppendLine($"Saved: {outPath}");
        sb.AppendLine();
        sb.AppendLine("Use run_shell to read or search this file.");

        return new ToolResult(sb.ToString());
    }

    // ------------------------------------------------------------------ list_directory
    private async Task<ToolResult> ListDirectory(JsonElement p, ToolContext ctx, CancellationToken ct)
    {
        if (!TryGetRepoParams(p, out var owner, out var repo, out var error))
            return new ToolResult(error, ExitCode: 1);

        var path = p.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? "" : "";
        var gitRef = p.TryGetProperty("ref", out var refEl) ? refEl.GetString() : null;
        ctx.Logger.LogInformation("github: list_directory {Owner}/{Repo}/{Path} ref={Ref}", owner, repo, path, gitRef ?? "(default)");

        var url = $"{ApiBase}/repos/{owner}/{repo}/contents/{path.TrimStart('/')}";
        if (!string.IsNullOrEmpty(gitRef)) url += $"?ref={Uri.EscapeDataString(gitRef)}";

        var json = await GetAsync(url, ct);
        if (json is null)
            return new ToolResult($"Failed to fetch directory listing for {owner}/{repo}/{path}", ExitCode: 1);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
            return new ToolResult($"Path '{path}' is a file, not a directory. Use get_file instead.", ExitCode: 1);

        var sb = new StringBuilder();
        var displayPath = string.IsNullOrEmpty(path) ? "/" : $"/{path.TrimStart('/')}";
        sb.AppendLine($"# Directory listing: {owner}/{repo}{displayPath}");
        if (!string.IsNullOrEmpty(gitRef)) sb.AppendLine($"Ref: {gitRef}");
        sb.AppendLine($"Entries: {root.GetArrayLength()}");
        sb.AppendLine();

        foreach (var entry in root.EnumerateArray())
        {
            var entryType = Str(entry, "type");
            var entryName = Str(entry, "name");
            var entrySize = entry.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
            var icon = entryType == "dir" ? "dir " : "file";
            sb.AppendLine($"  {icon}  {entrySize,8:N0}  {entryName}");
        }

        return new ToolResult(sb.ToString());
    }

    // ------------------------------------------------------------------ get_tree
    private async Task<ToolResult> GetTree(JsonElement p, ToolContext ctx, CancellationToken ct)
    {
        if (!TryGetRepoParams(p, out var owner, out var repo, out var error))
            return new ToolResult(error, ExitCode: 1);

        var gitRef = p.TryGetProperty("ref", out var refEl) ? refEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(gitRef))
            gitRef = "HEAD";

        ctx.Logger.LogInformation("github: get_tree {Owner}/{Repo} ref={Ref}", owner, repo, gitRef);

        var url = $"{ApiBase}/repos/{owner}/{repo}/git/trees/{Uri.EscapeDataString(gitRef)}?recursive=1";
        var json = await GetAsync(url, ct);
        if (json is null)
            return new ToolResult($"Failed to fetch tree for {owner}/{repo} at {gitRef}", ExitCode: 1);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tree = root.GetProperty("tree");
        var truncated = root.TryGetProperty("truncated", out var trunc) && trunc.GetBoolean();

        var sb = new StringBuilder();
        sb.AppendLine($"# Tree: {owner}/{repo} @ {gitRef}");
        sb.AppendLine($"Entries: {tree.GetArrayLength()}{(truncated ? " (TRUNCATED by GitHub -- tree too large)" : "")}");
        sb.AppendLine();

        foreach (var entry in tree.EnumerateArray())
        {
            var entryPath = Str(entry, "path");
            var entryType = Str(entry, "type");
            var entrySize = entry.TryGetProperty("size", out var sz) ? sz.GetInt64() : -1;

            if (entryType == "tree")
                sb.AppendLine($"  dir   {"",8}  {entryPath}/");
            else
                sb.AppendLine($"  blob  {entrySize,8:N0}  {entryPath}");
        }

        return new ToolResult(sb.ToString());
    }

    // ------------------------------------------------------------------ clone_repo
    private async Task<ToolResult> CloneRepo(JsonElement p, ToolContext ctx, CancellationToken ct)
    {
        if (!TryGetRepoParams(p, out var owner, out var repo, out var error))
            return new ToolResult(error, ExitCode: 1);

        var gitRef = p.TryGetProperty("ref", out var refEl) ? refEl.GetString() : null;
        var depth = p.TryGetProperty("depth", out var depthEl) ? depthEl.GetInt32() : 1;

        ctx.Logger.LogInformation("github: clone_repo {Owner}/{Repo} ref={Ref} depth={Depth}", owner, repo, gitRef ?? "(default)", depth);

        var cloneDir = Path.Combine(ctx.WorkspacePath, "tool_outputs", "github_clones", owner, repo);
        if (Directory.Exists(cloneDir) && Directory.Exists(Path.Combine(cloneDir, ".git")))
        {
            ctx.Logger.LogInformation("github: clone_repo reusing existing clone at {Path}", cloneDir);
            return new ToolResult(
                $"# Clone already exists: {owner}/{repo}\nPath: {cloneDir}\n\nUse run_shell to browse, grep, or read files.");
        }

        var repoJson = await GetAsync($"{ApiBase}/repos/{owner}/{repo}", ct);
        if (repoJson is null)
            return new ToolResult($"Failed to query repo metadata for {owner}/{repo}", ExitCode: 1);

        using var repoDoc = JsonDocument.Parse(repoJson);
        var repoRoot = repoDoc.RootElement;
        var sizeKb = repoRoot.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0;
        var maxKb = _options.MaxCloneSizeKb;

        if (sizeKb > maxKb)
        {
            return new ToolResult(
                $"Repository {owner}/{repo} is too large to clone ({sizeKb / 1024} MB reported, limit is {maxKb / 1024} MB). "
                + "Use get_file, list_directory, or get_tree to inspect specific paths instead.",
                ExitCode: 1);
        }

        Directory.CreateDirectory(cloneDir);

        var cloneUrl = $"https://github.com/{owner}/{repo}.git";
        var token = await _auth.GetTokenAsync(ct);
        if (token is not null)
            cloneUrl = $"https://x-access-token:{token}@github.com/{owner}/{repo}.git";

        var args = new List<string> { "clone" };
        if (depth > 0)
        {
            args.Add($"--depth={depth}");
            args.Add("--single-branch");
        }
        if (!string.IsNullOrEmpty(gitRef))
            args.Add($"--branch={gitRef}");
        args.Add(cloneUrl);
        args.Add(cloneDir);

        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = ctx.WorkspacePath,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var output = new StringBuilder();
        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null)
                return new ToolResult("Failed to start git process.", ExitCode: -1);

            var readOut = proc.StandardOutput.ReadToEndAsync(ct);
            var readErr = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            output.Append(await readOut);
            output.Append(await readErr);

            if (proc.ExitCode != 0)
            {
                TryCleanupDir(cloneDir);
                return new ToolResult($"git clone failed (exit {proc.ExitCode}):\n{output}", ExitCode: proc.ExitCode);
            }
        }
        catch (OperationCanceledException)
        {
            if (proc is not null && !proc.HasExited)
                try { proc.Kill(entireProcessTree: true); } catch { }
            TryCleanupDir(cloneDir);
            return new ToolResult("git clone timed out.", ExitCode: -1, TimedOut: true);
        }
        finally
        {
            proc?.Dispose();
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Cloned: {owner}/{repo}");
        sb.AppendLine($"Repo size (reported): {sizeKb:N0} KB");
        sb.AppendLine($"Depth: {(depth > 0 ? depth.ToString() : "full")}");
        if (!string.IsNullOrEmpty(gitRef)) sb.AppendLine($"Ref: {gitRef}");
        sb.AppendLine($"Path: {cloneDir}");
        sb.AppendLine();
        sb.AppendLine("Use run_shell to browse, grep, or read files in the clone.");

        return new ToolResult(sb.ToString());
    }

    private static void TryCleanupDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    // ------------------------------------------------------------------ HTTP helpers

    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            await ApplyAuth(request, ct);

            var response = await _httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("github: GET {Url} returned {Status}: {Body}", url, response.StatusCode,
                    body.Length > 200 ? body[..200] : body);
                return null;
            }

            return body;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "github: GET {Url} failed", url);
            return null;
        }
    }

    private async Task<byte[]?> GetBytesAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            await ApplyAuth(request, ct);

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("github: GET {Url} returned {Status}", url, response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "github: GET bytes {Url} failed", url);
            return null;
        }
    }

    private async Task ApplyAuth(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await _auth.GetTokenAsync(ct);
        if (token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    // ------------------------------------------------------------------ parameter helpers

    private static bool TryGetPrParams(JsonElement p, out string owner, out string repo, out int number, out string error)
    {
        owner = p.TryGetProperty("owner", out var o) ? o.GetString() ?? "" : "";
        repo = p.TryGetProperty("repo", out var r) ? r.GetString() ?? "" : "";
        number = p.TryGetProperty("number", out var n) ? n.GetInt32() : 0;
        error = "";

        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        { error = "owner and repo are required."; return false; }
        if (number <= 0)
        { error = "number (PR number) is required and must be positive."; return false; }
        return true;
    }

    private static bool TryGetRepoParams(JsonElement p, out string owner, out string repo, out string error)
    {
        owner = p.TryGetProperty("owner", out var o) ? o.GetString() ?? "" : "";
        repo = p.TryGetProperty("repo", out var r) ? r.GetString() ?? "" : "";
        error = "";

        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        { error = "owner and repo are required."; return false; }
        return true;
    }

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

    private static string StatusIcon(string conclusion, string status) => conclusion switch
    {
        "success" => "[pass]",
        "failure" => "[FAIL]",
        "cancelled" => "[skip]",
        "skipped" => "[skip]",
        "neutral" => "[----]",
        "timed_out" => "[TIME]",
        "action_required" => "[ACT!]",
        _ => status switch
        {
            "queued" => "[....]",
            "in_progress" => "[>>>.]",
            "pending" => "[....]",
            _ => "[????]",
        }
    };
}
