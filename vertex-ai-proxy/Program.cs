using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateSlimBuilder(args);
var app = builder.Build();

var upstream = (app.Configuration["Upstream"]
    ?? Environment.GetEnvironmentVariable("UPSTREAM_URL")
    ?? "").TrimEnd('/');

if (string.IsNullOrEmpty(upstream))
{
    Console.Error.WriteLine("ERROR: No upstream configured.");
    Console.Error.WriteLine("  Set 'Upstream' in appsettings.json, pass --Upstream <url>, or set UPSTREAM_URL env var.");
    Console.Error.WriteLine("  Example: https://us-east5-aiplatform.googleapis.com");
    return 1;
}

var handler = new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    MaxConnectionsPerServer = 100,
    EnableMultipleHttp2Connections = true,
    AutomaticDecompression = DecompressionMethods.All,
    AllowAutoRedirect = false,
};

using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

Console.WriteLine($"vertex-ai-proxy");
Console.WriteLine($"  upstream : {upstream}");
Console.WriteLine($"  model    : claude-opus-4-8 -> claude-opus-4-6");
Console.WriteLine($"  effort   : output_config.effort xhigh -> max");
Console.WriteLine();

app.MapGet("/healthz", () => Results.Ok("ok"));

app.Run(async (HttpContext ctx) =>
{
    var path = ctx.Request.Path.Value ?? "";
    var query = ctx.Request.QueryString.Value ?? "";
    var rewrite = path.Contains("/models/claude-opus-4-8");

    if (rewrite)
    {
        path = path.Replace("/models/claude-opus-4-8", "/models/claude-opus-4-6");
        Log("REWRITE", $"model -> claude-opus-4-6");
    }
    else
    {
        Log("PROXY", $"{ctx.Request.Method} {path}");
    }

    // --- request body ---
    using var bodyBuf = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(bodyBuf, ctx.RequestAborted);
    var body = bodyBuf.ToArray();

    if (rewrite && body.Length > 0)
        body = PatchRequestBody(body);

    // --- build upstream request ---
    var targetUrl = $"{upstream}{path}{query}";
    using var upstreamReq = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), targetUrl);

    foreach (var (key, values) in ctx.Request.Headers)
    {
        if (IsSkipped(key)) continue;

        if (rewrite && key.Equals("anthropic-beta", StringComparison.OrdinalIgnoreCase))
        {
            var filtered = values.ToString()!
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(v => !v.Contains("mid-conversation-system"))
                .ToArray();
            if (filtered.Length > 0)
                upstreamReq.Headers.TryAddWithoutValidation(key, string.Join(", ", filtered));
            else
                Log("REWRITE", "stripped anthropic-beta header (empty after filter)");
            continue;
        }

        upstreamReq.Headers.TryAddWithoutValidation(key, values.ToArray());
    }

    if (body.Length > 0 || HttpMethods.IsPost(ctx.Request.Method) || HttpMethods.IsPut(ctx.Request.Method))
    {
        upstreamReq.Content = new ByteArrayContent(body);
        if (ctx.Request.ContentType is { } ct)
            upstreamReq.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
    }

    // --- send & stream response ---
    HttpResponseMessage upstreamResp;
    try
    {
        upstreamResp = await httpClient.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Log("ERROR", ex.Message);
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsync($"{{\"error\":\"upstream request failed: {ex.Message}\"}}", ctx.RequestAborted);
        return;
    }

    using (upstreamResp)
    {
        ctx.Response.StatusCode = (int)upstreamResp.StatusCode;

        foreach (var (key, values) in upstreamResp.Headers)
        {
            if (IsSkipped(key)) continue;
            ctx.Response.Headers[key] = values.ToArray();
        }
        foreach (var (key, values) in upstreamResp.Content.Headers)
        {
            if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            ctx.Response.Headers[key] = values.ToArray();
        }

        if (!upstreamResp.IsSuccessStatusCode)
        {
            var errorBody = await upstreamResp.Content.ReadAsStringAsync(ctx.RequestAborted);
            Log("UPSTREAM", $"{(int)upstreamResp.StatusCode} {errorBody[..Math.Min(errorBody.Length, 500)]}");
            await ctx.Response.WriteAsync(errorBody, ctx.RequestAborted);
        }
        else
        {
            await using var respStream = await upstreamResp.Content.ReadAsStreamAsync(ctx.RequestAborted);
            await respStream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
    }
});

app.Run();
return 0;

// ──────────────────────────────────────────────
// Helpers
// ──────────────────────────────────────────────

static byte[] PatchRequestBody(byte[] body)
{
    try
    {
        var node = JsonNode.Parse(body);
        if (node is not JsonObject root) return body;

        var changed = false;

        // Patch output_config.effort: xhigh -> max
        if (root["output_config"] is JsonObject outputConfig
            && outputConfig["effort"] is JsonValue ov
            && ov.TryGetValue<string>(out var effort)
            && string.Equals(effort, "xhigh", StringComparison.OrdinalIgnoreCase))
        {
            outputConfig["effort"] = "max";
            changed = true;
            Log("REWRITE", "output_config.effort: xhigh -> max");
        }

        // Move "role":"system" messages out of messages[] into top-level system
        if (root["messages"] is JsonArray messages)
        {
            var systemParts = new List<string>();
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i] is not JsonObject msg) continue;
                if (msg["role"] is not JsonValue rv || !rv.TryGetValue<string>(out var role)) continue;
                if (role != "system") continue;

                if (msg["content"] is JsonValue cv && cv.TryGetValue<string>(out var text))
                    systemParts.Insert(0, text);
                else if (msg["content"] is JsonArray contentArr)
                    foreach (var block in contentArr)
                        if (block is JsonObject b && b["text"] is JsonValue tv && tv.TryGetValue<string>(out var t))
                            systemParts.Insert(0, t);

                messages.RemoveAt(i);
            }

            if (systemParts.Count > 0)
            {
                var combined = string.Join("\n\n", systemParts);

                // Preserve existing top-level system if present
                string existing = "";
                if (root["system"] is JsonValue sv && sv.TryGetValue<string>(out var s))
                    existing = s;
                else if (root["system"] is JsonArray sysArr)
                    foreach (var block in sysArr)
                        if (block is JsonObject b && b["text"] is JsonValue tv && tv.TryGetValue<string>(out var t))
                            existing = string.IsNullOrEmpty(existing) ? t : $"{existing}\n\n{t}";

                root["system"] = string.IsNullOrEmpty(existing)
                    ? combined
                    : $"{existing}\n\n{combined}";
                changed = true;
                Log("REWRITE", $"moved {systemParts.Count} system message(s) to top-level system param");
            }
        }

        return changed ? Encoding.UTF8.GetBytes(node!.ToJsonString()) : body;
    }
    catch
    {
        return body;
    }
}

static bool IsSkipped(string header) =>
    header.Equals("Host", StringComparison.OrdinalIgnoreCase)
    || header.Equals("Connection", StringComparison.OrdinalIgnoreCase)
    || header.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
    || header.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
    || header.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
    || header.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);

static void Log(string tag, string message) =>
    Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [{tag}] {message}");
