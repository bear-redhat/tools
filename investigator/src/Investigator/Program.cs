using Investigator;
using Investigator.Components;
using Investigator.Models;
using Investigator.Services;
using Investigator.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;

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

builder.Services.AddScoped<BrowserTimeZone>();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<InvestigationOrchestrator>();
builder.Services.AddSingleton<RemediationOrchestrator>();

var app = builder.Build();

await app.Services.GetRequiredService<ToolRegistry>().InitializeAsync();

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

app.UseAntiforgery();

app.MapHealthChecks("/health");
app.MapWorkspaceFiles();

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
