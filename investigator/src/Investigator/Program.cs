using Investigator;
using Investigator.Components;
using Investigator.Models;
using Investigator.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHealthChecks();

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));

builder.Services.AddInvestigatorLlm(builder.Configuration);
builder.Services.AddInvestigatorTools(builder.Configuration);
builder.Services.AddInvestigatorAuth(builder.Configuration);

builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddScoped<InvestigationRoom>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

var authSettings = app.Services.GetRequiredService<AuthSettings>();
if (authSettings.Mode == AuthMode.Oidc)
{
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/login", () => Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/" },
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

app.MapGet("/", (ConversationStore store) =>
{
    var session = store.CreateSession();
    return Results.Redirect($"/c/{session.Id}");
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
