using Investigator;
using Investigator.Components;
using Investigator.Mcp;
using Investigator.Models;
using Investigator.Services;
using Investigator.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHealthChecks();

var workspaceRoot = builder.Configuration.GetValue<string>("Workspace:RootPath");
if (!string.IsNullOrEmpty(workspaceRoot))
{
    var keyDir = new DirectoryInfo(Path.Combine(workspaceRoot, "dp-keys"));
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(keyDir);
}

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = ".Investigator.Antiforgery";
});

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));

builder.Services.AddInvestigatorLlm(builder.Configuration);
builder.Services.AddInvestigatorTools(builder.Configuration);
builder.Services.AddInvestigatorAuth(builder.Configuration, builder.Environment);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<BrowserTimeZone>();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<InvestigationOrchestrator>();
builder.Services.AddSingleton<RemediationOrchestrator>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<McpSessionContext>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "investigator", Version = "1.0.0" };
        options.ServerInstructions =
            """
            Investigate CI infrastructure failures on OpenShift, Prow and AWS.

            TWO WAYS TO WORK

            1. Delegate. `investigate` starts a team of AI agents that diagnose the problem
               autonomously. It returns a conversationId immediately and does not block.
               Follow the work with `poll`; it is the primary loop:

                 investigate(message)              -> { conversationId }
                 poll(conversationId, sinceSeq: 0) -> { events, nextSeq, running,
                                                        pendingQuestion, truncated, gap }
                 poll(conversationId, sinceSeq: <nextSeq>)   ... repeat

               Every event is one step: an agent message, a tool call with its arguments,
               or a tool result with exit code and summary. Pass the returned nextSeq back
               as sinceSeq. `truncated` means more is ready now -- call again immediately
               rather than waiting. `running: false` means it has finished; stop polling
               and call `get_findings`.

               `pendingQuestion` non-null means the investigator asked you something and
               has stopped until you answer. Reply with `follow_up`.

               Steer it with `steer`: nudge the lead, recall a scout for interim findings,
               stand a scout down to abort its in-flight tool, or cancel the whole run.
               You can `follow_up` at any time while it works; the message is delivered
               after the current tool call, never mid-call.

               Tool output in the transcript is clipped. When an event carries outputFile,
               read the rest with `get_output` by line range instead of pulling a whole
               build log into context.

               `get_transcript` is the same data without waiting -- use it to catch up on
               an investigation you were not watching, and to filter by agent or kind.
               Reconnecting later: `list_investigations` -> `get_transcript(sinceSeq: 0)`.

            2. Do it yourself. The raw_ tools run one infrastructure command directly, with
               no agents involved -- raw_run_oc, raw_prow, raw_run_aws, raw_prometheus,
               raw_github and others. Use these when you know exactly what to look at.
               Their large output is written to a file; page it with raw_read_output.

            When the investigator commissions a fix, it runs in a second room. Pass
            room: "remediation" to poll, get_transcript, get_status, get_findings or steer
            to follow or control it; omit the argument for the investigation itself.

            `get_status` is a cheap liveness and cost check. It does not return any of the
            investigation's content -- use poll or get_transcript for that.
            """;
    })
    // Pinned explicitly: the default flips to stateless in the 2.x SDK, which would
    // silently drop Mcp-Session-Id, the SSE GET and DELETE with no compile error.
    .WithHttpTransport(o => o.Stateless = false)
    .WithTools<InvestigatorMcpTools>()
    .WithResources<InvestigatorMcpResources>()
    .WithResources<InvestigatorSkillResources>();

var app = builder.Build();

var toolRegistry = app.Services.GetRequiredService<ToolRegistry>();
await toolRegistry.InitializeAsync();

var rawTools = DynamicToolHandler.BuildToolsFromRegistry(toolRegistry, app.Services);
var mcpOptions = app.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;
mcpOptions.ToolCollection ??= [];
foreach (var tool in rawTools)
    mcpOptions.ToolCollection.Add(tool);

// The nine ISystemPromptContributor tools describe their own usage (cluster lists, prow
// actions, AWS accounts). Those briefings previously reached in-process agents only --
// AgentRoom was the sole consumer of GetSystemPromptContributions() -- so an MCP client
// had to guess at the raw_* tools. Append them to the server instructions.
var toolBriefings = toolRegistry.GetSystemPromptContributions();
if (toolBriefings.Count > 0)
{
    mcpOptions.ServerInstructions = mcpOptions.ServerInstructions
        + "\n\nRAW TOOL REFERENCE\n"
        + "The following briefings describe the raw_ tools. The raw_ prefix is not part of\n"
        + "the command syntax shown below.\n\n"
        + string.Join("\n\n", toolBriefings);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var feature = context.Features.Get<IStatusCodePagesFeature>();
        if (feature is not null) feature.Enabled = false;
    }
    await next();
});

app.UseHttpsRedirection();

var authSettings = app.Services.GetRequiredService<AuthSettings>();
if (authSettings.HasOidc)
{
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/login/oidc", (string? returnUrl) => Results.Challenge(
        new AuthenticationProperties { RedirectUri = returnUrl ?? "/" },
        [OpenIdConnectDefaults.AuthenticationScheme]));

    app.MapGet("/logout", async (HttpContext ctx) =>
    {
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme);
        return Results.Redirect("/");
    });
}

app.UseWhenMcp("/mcp");
app.UseAntiforgery();

app.MapHealthChecks("/health");
app.MapWorkspaceFiles();
app.MapInvestigateApi();

app.MapMcp("/mcp");

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    var browserTool = app.Services.GetService<WebBrowserTool>();
    browserTool?.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

app.Run();
