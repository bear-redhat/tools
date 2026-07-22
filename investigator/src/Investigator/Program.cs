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
builder.Services.AddScoped<McpSessionContext>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "investigator", Version = "1.0.0" };
        options.ServerInstructions =
            """
            This server provides two levels of tools:

            CAPABILITY TOOLS (preferred): investigate, follow_up, get_status, get_findings, list_investigations, search_knowledge.
            These delegate work to the investigator's AI agents who autonomously use infrastructure tools, coordinate sub-agents, and produce structured findings. Use these for any diagnostic or investigative task.

            RAW TOOLS (raw_ prefix): Direct access to infrastructure -- OpenShift clusters, AWS, Prow, GitHub, Prometheus, etc.
            Only use raw_ tools when you need to run a specific command yourself and the capability tools are not appropriate.
            For example, use raw_run_oc to check a specific pod's status, or raw_prow to look up a specific job's logs.

            Typical workflow: call investigate to start, poll with get_status, read results with get_findings.
            """;
    })
    .WithHttpTransport()
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
