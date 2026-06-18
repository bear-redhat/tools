using Investigator.Models;
using Investigator.Tools;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public static class InvestigateEndpoints
{
    public static IEndpointRouteBuilder MapInvestigateApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/investigate", HandleInvestigate);
        return endpoints;
    }

    private static async Task<IResult> HandleInvestigate(
        HttpContext httpContext,
        ConversationStore store,
        InvestigationOrchestrator orchestrator,
        WorkspaceManager workspaceManager,
        AuditLog auditLog,
        AuthSettings authSettings,
        IOptions<AuthOptions> authOptions,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Investigator.InvestigateEndpoints");

        if (!CheckAuth(httpContext, authSettings, authOptions.Value))
        {
            logger.LogWarning("Unauthorized investigate attempt from {IP}", httpContext.Connection.RemoteIpAddress);
            return Results.Json(new { error = "unauthorized" }, statusCode: 401);
        }

        InvestigateRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<InvestigateRequest>(ct);
        }
        catch
        {
            return Results.Json(new { error = "invalid_body" }, statusCode: 400);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Message))
            return Results.Json(new { error = "message_required" }, statusCode: 400);

        var session = store.CreateSession();
        session.WorkspacePath = workspaceManager.CreateWorkspace(session.Id);

        const string subscriberId = "__api__";
        orchestrator.StartAsync(session.Id, session, subscriberId);
        await orchestrator.PostUserMessageAsync(session.Id, request.Message, ct);
        orchestrator.Unsubscribe(session.Id, subscriberId);

        var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
        var viewUrl = $"{baseUrl}/c/{session.Id}/view";

        var ip = httpContext.Connection.RemoteIpAddress?.ToString();
        var msgPreview = request.Message.Length > 120 ? request.Message[..120] + "..." : request.Message;
        auditLog.Record(session.Id, "api_start", null, ip, new Dictionary<string, string> { ["message"] = msgPreview });

        logger.LogInformation("Investigation started via API: {ConversationId}", session.Id);

        return Results.Json(new { conversationId = session.Id, url = viewUrl }, statusCode: 202);
    }

    private static bool CheckAuth(HttpContext httpContext, AuthSettings authSettings, AuthOptions authOptions)
    {
        if (!authSettings.IsEnabled)
            return true;

        if (string.IsNullOrEmpty(authOptions.SharedToken))
            return false;

        var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader is not null
            && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && string.Equals(authHeader["Bearer ".Length..], authOptions.SharedToken, StringComparison.Ordinal))
            return true;

        var queryToken = httpContext.Request.Query["token"].FirstOrDefault();
        if (string.Equals(queryToken, authOptions.SharedToken, StringComparison.Ordinal))
            return true;

        return false;
    }

    private sealed record InvestigateRequest(string? Message);
}
