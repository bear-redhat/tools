using Investigator.Models;
using Investigator.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Investigator.Mcp;

/// <summary>
/// Middleware that authenticates MCP HTTP requests using either:
/// 1. SharedToken (simple Bearer match)
/// 2. OIDC JWT (validated against the configured Authority/Dex)
/// 
/// When neither auth mechanism is configured, all requests are allowed.
/// </summary>
public sealed class McpAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var authSettings = context.RequestServices.GetRequiredService<AuthSettings>();
        var authOptions = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;

        if (!authSettings.IsEnabled)
        {
            await next(context);
            return;
        }

        var bearerToken = ExtractBearerToken(context);

        if (bearerToken is not null && authSettings.HasToken
            && !string.IsNullOrEmpty(authOptions.SharedToken)
            && string.Equals(bearerToken, authOptions.SharedToken, StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        if (authSettings.HasOidc)
        {
            var result = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
            if (result.Succeeded)
            {
                context.User = result.Principal!;
                await next(context);
                return;
            }
        }

        context.Response.StatusCode = 401;
        context.Response.Headers["WWW-Authenticate"] = "Bearer";
        await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
    }

    private static string? ExtractBearerToken(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader is not null
            && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authHeader["Bearer ".Length..];
        }

        return null;
    }
}

public static class McpAuthExtensions
{
    /// <summary>
    /// Applies MCP auth middleware to a specific path prefix.
    /// </summary>
    public static IApplicationBuilder UseWhenMcp(this IApplicationBuilder app, string pathPrefix)
    {
        return app.UseWhen(
            context => context.Request.Path.StartsWithSegments(pathPrefix),
            branch => branch.UseMiddleware<McpAuthMiddleware>());
    }
}
