using System.Net;
using System.Text;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Microsoft.Extensions.Options;

namespace Investigator.Tools;

public sealed class PrometheusTool : IInvestigatorTool, ISystemPromptContributor
{
    private static readonly JsonElement s_paramSchema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "action": {
                "type": "string",
                "enum": ["query", "query_range", "alerts", "list_servicemonitors", "list_podmonitors", "list_prometheusrules", "list_alertmanagerconfigs", "get_resource"],
                "description": "The action to perform."
            },
            "query": {
                "type": "string",
                "description": "PromQL expression (required for query/query_range)."
            },
            "time": {
                "type": "string",
                "description": "Evaluation timestamp for instant query (RFC3339 or Unix). Optional."
            },
            "start": {
                "type": "string",
                "description": "Range start (RFC3339 or Unix). Required for query_range."
            },
            "end": {
                "type": "string",
                "description": "Range end (RFC3339 or Unix). Required for query_range."
            },
            "step": {
                "type": "string",
                "description": "Query step (e.g. '60s', '5m'). Required for query_range."
            },
            "cluster": {
                "type": "string",
                "description": "Target cluster for alerts (optional label filter), CR listing (required), or get_resource (required)."
            },
            "namespace": {
                "type": "string",
                "description": "Namespace filter for CR listing. Omit to query all namespaces."
            },
            "kind": {
                "type": "string",
                "description": "CR kind for get_resource (servicemonitor, podmonitor, prometheusrule, alertmanagerconfig)."
            },
            "name": {
                "type": "string",
                "description": "Resource name for get_resource."
            }
        },
        "required": ["action"]
    }
    """).RootElement.Clone();

    private readonly PrometheusOptions _options;
    private readonly OcExecutor _ocExecutor;
    private readonly HttpClient _httpClient;
    private readonly ILogger<PrometheusTool> _logger;

    public PrometheusTool(
        OcExecutor ocExecutor,
        IHttpClientFactory httpClientFactory,
        IOptions<PrometheusOptions> options,
        ILogger<PrometheusTool> logger)
    {
        _ocExecutor = ocExecutor;
        _httpClient = httpClientFactory.CreateClient("Prometheus");
        _options = options.Value;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(_options.ThanosUrl.TrimEnd('/'));
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public ToolDefinition Definition => new(
        Name: "prometheus",
        Description: "Query Prometheus metrics via centralized Thanos and list monitoring custom resources (ServiceMonitor, PodMonitor, PrometheusRule, AlertmanagerConfig) on clusters.",
        ParameterSchema: s_paramSchema,
        DefaultTimeout: TimeSpan.FromSeconds(60));

    public string? GetSystemPromptSection() => """
        ## Prometheus / Thanos tool
        The `prometheus` tool queries a centralized Thanos instance that aggregates metrics from all CI clusters.
        Use PromQL label selectors like {cluster="build01"} to scope queries to a specific cluster.

        WARNING: The centralized Thanos has a known issue -- metrics are NOT complete. Some clusters have
        failed remote-writes, so Thanos may be missing data. Do NOT assume Thanos results are authoritative.
        When accuracy matters, cross-check with `run_oc` on the specific cluster (e.g. oc get pods, oc get machines).

        Actions:
        - query: instant PromQL query (requires 'query', optional 'time')
        - query_range: range PromQL query (requires 'query', 'start', 'end', 'step')
        - alerts: firing/pending alerts via the ALERTS metric (optional 'cluster' filter)
        - list_servicemonitors: list ServiceMonitor CRs (requires 'cluster', optional 'namespace')
        - list_podmonitors: list PodMonitor CRs (requires 'cluster', optional 'namespace')
        - list_prometheusrules: list PrometheusRule CRs (requires 'cluster', optional 'namespace')
        - list_alertmanagerconfigs: list AlertmanagerConfig CRs (requires 'cluster', optional 'namespace')
        - get_resource: get a specific monitoring CR (requires 'cluster', 'kind', 'name', 'namespace')
        """;

    public async Task<ToolResult> InvokeAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var action = Prop(parameters, "action");
        if (string.IsNullOrEmpty(action))
            return new ToolResult("Error: 'action' parameter is required.", ExitCode: 1);

        return action switch
        {
            "query" => await QueryAsync(parameters, context, ct),
            "query_range" => await QueryRangeAsync(parameters, context, ct),
            "alerts" => await AlertsAsync(parameters, context, ct),
            "list_servicemonitors" => await ListCrAsync("servicemonitors", parameters, context, ct),
            "list_podmonitors" => await ListCrAsync("podmonitors", parameters, context, ct),
            "list_prometheusrules" => await ListCrAsync("prometheusrules", parameters, context, ct),
            "list_alertmanagerconfigs" => await ListCrAsync("alertmanagerconfigs", parameters, context, ct),
            "get_resource" => await GetResourceAsync(parameters, context, ct),
            _ => new ToolResult($"Unknown action: {action}. Valid: query, query_range, alerts, list_servicemonitors, list_podmonitors, list_prometheusrules, list_alertmanagerconfigs, get_resource", ExitCode: 1),
        };
    }

    private async Task<ToolResult> QueryAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var query = Prop(parameters, "query");
        if (string.IsNullOrEmpty(query))
            return new ToolResult("Error: 'query' parameter is required for action=query.", ExitCode: 1);

        var url = $"/api/v1/query?query={Uri.EscapeDataString(query)}";
        var time = Prop(parameters, "time");
        if (!string.IsNullOrEmpty(time))
            url += $"&time={Uri.EscapeDataString(time)}";

        var (json, error) = await ThanosGetAsync(url, ct);
        if (error is not null) return error;

        return FormatQueryResult(json!.Value, query);
    }

    private async Task<ToolResult> QueryRangeAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var query = Prop(parameters, "query");
        var start = Prop(parameters, "start");
        var end = Prop(parameters, "end");
        var step = Prop(parameters, "step");

        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(start)
            || string.IsNullOrEmpty(end) || string.IsNullOrEmpty(step))
            return new ToolResult("Error: 'query', 'start', 'end', and 'step' are all required for query_range.", ExitCode: 1);

        if (TryParseDataPointCount(start, end, step, out var count) && count > _options.MaxDataPoints)
            return new ToolResult(
                $"Error: query would produce ~{count} data points (max {_options.MaxDataPoints}). Widen the step or narrow the time range.",
                ExitCode: 1);

        var url = $"/api/v1/query_range?query={Uri.EscapeDataString(query)}"
            + $"&start={Uri.EscapeDataString(start)}&end={Uri.EscapeDataString(end)}&step={Uri.EscapeDataString(step)}";

        var (json, error) = await ThanosGetAsync(url, ct);
        if (error is not null) return error;

        return FormatQueryResult(json!.Value, query);
    }

    private async Task<ToolResult> AlertsAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var cluster = Prop(parameters, "cluster");
        var promql = string.IsNullOrEmpty(cluster)
            ? "ALERTS"
            : $"ALERTS{{cluster=\"{cluster}\"}}";

        var url = $"/api/v1/query?query={Uri.EscapeDataString(promql)}";
        var (json, error) = await ThanosGetAsync(url, ct);
        if (error is not null) return error;

        return FormatAlertsResult(json!.Value);
    }

    private async Task<ToolResult> ListCrAsync(string kind, JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var cluster = Prop(parameters, "cluster");
        if (string.IsNullOrEmpty(cluster))
            return new ToolResult($"Error: 'cluster' parameter is required for list_{kind}.", ExitCode: 1);

        var ns = Prop(parameters, "namespace");
        var nsArg = string.IsNullOrEmpty(ns) ? "-A" : $"-n {ns}";
        var command = $"get {kind} {nsArg} -o name";

        var result = await RunOcQuiet(cluster, command, ct);
        if (result.ExitCode != 0)
            return new ToolResult($"Error listing {kind} on {cluster}: {result.Output}", ExitCode: result.ExitCode);

        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.AppendLine(line.Trim());
            context.OnOutputLine?.Invoke(line.Trim());
        }
        sb.AppendLine("---");
        sb.AppendLine($"{lines.Length} {kind} on {cluster}" + (string.IsNullOrEmpty(ns) ? " (all namespaces)" : $" in {ns}"));
        return new ToolResult(sb.ToString());
    }

    private async Task<ToolResult> GetResourceAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var cluster = Prop(parameters, "cluster");
        var kind = Prop(parameters, "kind");
        var name = Prop(parameters, "name");
        var ns = Prop(parameters, "namespace");

        if (string.IsNullOrEmpty(cluster) || string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(ns))
            return new ToolResult("Error: 'cluster', 'kind', 'name', and 'namespace' are all required for get_resource.", ExitCode: 1);

        var validKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "servicemonitor", "podmonitor", "prometheusrule", "alertmanagerconfig" };
        if (!validKinds.Contains(kind))
            return new ToolResult($"Error: invalid kind '{kind}'. Valid: servicemonitor, podmonitor, prometheusrule, alertmanagerconfig", ExitCode: 1);

        var command = $"get {kind} {name} -n {ns} -o yaml";
        var result = await RunOcQuiet(cluster, command, ct);
        if (result.ExitCode != 0)
            return new ToolResult($"Error getting {kind}/{name} in {ns} on {cluster}: {result.Output}", ExitCode: result.ExitCode);

        return new ToolResult(result.Output);
    }

    private async Task<(JsonElement? Json, ToolResult? Error)> ThanosGetAsync(string path, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync(path, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Thanos returned {Status} for {Path}: {Body}",
                    response.StatusCode, path, body.Length > 500 ? body[..500] : body);
                return (null, new ToolResult(
                    $"Thanos API error ({(int)response.StatusCode} {response.StatusCode}): {(body.Length > 500 ? body[..500] : body)}",
                    ExitCode: 1));
            }

            var doc = JsonDocument.Parse(body);
            return (doc.RootElement, null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to Thanos at {Url}", _options.ThanosUrl);
            return (null, new ToolResult(
                $"Failed to connect to Thanos at {_options.ThanosUrl}: {ex.Message}",
                ExitCode: 1));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, new ToolResult(
                $"Thanos request timed out after {_options.TimeoutSeconds}s for {path}",
                ExitCode: 1, TimedOut: true));
        }
    }

    private ToolResult FormatQueryResult(JsonElement root, string query)
    {
        if (root.TryGetProperty("status", out var status) && status.GetString() == "error")
        {
            var errType = root.TryGetProperty("errorType", out var et) ? et.GetString() : "unknown";
            var errMsg = root.TryGetProperty("error", out var em) ? em.GetString() : "unknown error";
            return new ToolResult($"PromQL error ({errType}): {errMsg}", ExitCode: 1);
        }

        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("result", out var resultArr))
            return new ToolResult("Unexpected Thanos response format (no data.result).", ExitCode: 1);

        var resultType = data.TryGetProperty("resultType", out var rt) ? rt.GetString() : "unknown";
        var results = resultArr.EnumerateArray().ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"# {resultType} | query: {query}");

        var shown = 0;
        foreach (var item in results)
        {
            if (shown >= _options.MaxSeries)
            {
                sb.AppendLine($"... ({results.Count - shown} more series truncated, MaxSeries={_options.MaxSeries})");
                break;
            }

            var metric = item.TryGetProperty("metric", out var m) ? FormatLabels(m) : "{}";

            if (resultType == "matrix" && item.TryGetProperty("values", out var vals))
            {
                var pointCount = vals.GetArrayLength();
                var first = pointCount > 0 ? vals[0] : default;
                var last = pointCount > 1 ? vals[pointCount - 1] : first;
                var firstVal = first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 1 ? first[1].GetString() : "?";
                var lastVal = last.ValueKind == JsonValueKind.Array && last.GetArrayLength() > 1 ? last[1].GetString() : "?";
                sb.AppendLine($"{metric}  points={pointCount}  first={firstVal}  last={lastVal}");
            }
            else if (item.TryGetProperty("value", out var val)
                     && val.ValueKind == JsonValueKind.Array && val.GetArrayLength() > 1)
            {
                sb.AppendLine($"{metric} = {val[1].GetString()}");
            }
            else
            {
                sb.AppendLine(metric);
            }

            shown++;
        }

        sb.AppendLine("---");
        sb.AppendLine($"{results.Count} result(s)");
        return new ToolResult(sb.ToString());
    }

    private ToolResult FormatAlertsResult(JsonElement root)
    {
        if (root.TryGetProperty("status", out var status) && status.GetString() == "error")
        {
            var errType = root.TryGetProperty("errorType", out var et) ? et.GetString() : "unknown";
            var errMsg = root.TryGetProperty("error", out var em) ? em.GetString() : "unknown error";
            return new ToolResult($"PromQL error ({errType}): {errMsg}", ExitCode: 1);
        }

        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("result", out var resultArr))
            return new ToolResult("Unexpected Thanos response format (no data.result).", ExitCode: 1);

        var results = resultArr.EnumerateArray().ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# alerts (from ALERTS metric)");

        int firing = 0, pending = 0;
        foreach (var item in results)
        {
            if (!item.TryGetProperty("metric", out var metric)) continue;

            var alertState = metric.TryGetProperty("alertstate", out var asv) ? asv.GetString() : "unknown";
            var alertName = metric.TryGetProperty("alertname", out var anv) ? anv.GetString() : "?";

            if (alertState == "firing") firing++;
            else pending++;

            var labels = new StringBuilder();
            foreach (var prop in metric.EnumerateObject())
            {
                if (prop.Name is "alertstate" or "alertname" or "__name__") continue;
                if (labels.Length > 0) labels.Append(' ');
                labels.Append($"{prop.Name}={prop.Value.GetString()}");
            }

            sb.AppendLine($"{alertState?.ToUpperInvariant(),-8} {alertName}  {labels}");
        }

        sb.AppendLine("---");
        sb.AppendLine($"{results.Count} alert(s) ({firing} firing, {pending} pending)");
        return new ToolResult(sb.ToString());
    }

    private static string FormatLabels(JsonElement metric)
    {
        var parts = new List<string>();
        string? name = null;
        foreach (var prop in metric.EnumerateObject())
        {
            if (prop.Name == "__name__")
            {
                name = prop.Value.GetString();
                continue;
            }
            parts.Add($"{prop.Name}=\"{prop.Value.GetString()}\"");
        }
        var labelStr = string.Join(", ", parts);
        return name is not null ? $"{name}{{{labelStr}}}" : $"{{{labelStr}}}";
    }

    private static bool TryParseDataPointCount(string start, string end, string step, out long count)
    {
        count = 0;
        if (!TryParseTimestamp(start, out var startTs) || !TryParseTimestamp(end, out var endTs))
            return false;
        var stepSeconds = ParseDuration(step);
        if (stepSeconds <= 0) return false;
        count = (long)((endTs - startTs).TotalSeconds / stepSeconds);
        return true;
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset result)
    {
        if (DateTimeOffset.TryParse(value, out result))
            return true;
        if (double.TryParse(value, out var unix))
        {
            result = DateTimeOffset.FromUnixTimeSeconds((long)unix);
            return true;
        }
        return false;
    }

    private static double ParseDuration(string s)
    {
        if (double.TryParse(s, out var secs)) return secs;
        if (s.EndsWith('s') && double.TryParse(s[..^1], out var sv)) return sv;
        if (s.EndsWith('m') && double.TryParse(s[..^1], out var mv)) return mv * 60;
        if (s.EndsWith('h') && double.TryParse(s[..^1], out var hv)) return hv * 3600;
        if (s.EndsWith('d') && double.TryParse(s[..^1], out var dv)) return dv * 86400;
        return 0;
    }

    private async Task<ToolResult> RunOcQuiet(string cluster, string command, CancellationToken ct)
    {
        var json = JsonDocument.Parse(JsonSerializer.Serialize(new { cluster, command })).RootElement;
        var quietContext = new ToolContext(
            Logger: _logger,
            WorkspacePath: string.Empty,
            OnOutputLine: null,
            NextOutputNumber: () => 0,
            CallerId: "prometheus-tool");
        return await _ocExecutor.InvokeAsync(json, quietContext, ct);
    }

    private static string? Prop(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
