using Investigator.Models;
using Investigator.Tools;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

namespace Investigator.Services;

public static class WorkspaceFileEndpoints
{
    private static readonly FileExtensionContentTypeProvider s_contentTypes = new();

    private static readonly Dictionary<string, string> s_extraContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".patch"] = "text/plain; charset=utf-8",
        [".diff"] = "text/plain; charset=utf-8",
        [".log"] = "text/plain; charset=utf-8",
        [".yaml"] = "text/plain; charset=utf-8",
        [".yml"] = "text/plain; charset=utf-8",
        [".sh"] = "text/plain; charset=utf-8",
        [".py"] = "text/plain; charset=utf-8",
        [".go"] = "text/plain; charset=utf-8",
        [".rs"] = "text/plain; charset=utf-8",
        [".toml"] = "text/plain; charset=utf-8",
        [".cfg"] = "text/plain; charset=utf-8",
        [".ini"] = "text/plain; charset=utf-8",
        [".md"] = "text/plain; charset=utf-8",
    };

    public static IEndpointRouteBuilder MapWorkspaceFiles(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/conversations/{conversationId}/files");

        group.MapGet("", ListFiles);
        group.MapGet("/{**filePath}", ServeFile);

        return endpoints;
    }

    private static IResult ListFiles(
        string conversationId,
        WorkspaceManager workspaceManager,
        AuthSettings authSettings,
        IOptions<AuthOptions> authOptions,
        HttpContext httpContext,
        ILoggerFactory loggerFactory,
        string? path = null)
    {
        var logger = loggerFactory.CreateLogger("Investigator.WorkspaceFileEndpoints");

        if (!CheckAuth(httpContext, authSettings, authOptions.Value))
        {
            logger.LogWarning("Unauthorized file list attempt for conversation {ConversationId}", conversationId);
            return Results.Json(new { error = "unauthorized" }, statusCode: 401);
        }

        var workspace = workspaceManager.FindWorkspacePath(conversationId);
        if (workspace is null)
        {
            logger.LogWarning("Workspace not found for conversation {ConversationId}", conversationId);
            return Results.Json(new { error = "workspace_not_found", conversationId }, statusCode: 404);
        }

        var relativePath = path ?? "tool_outputs";
        if (!TryResolveSafePath(workspace, relativePath, out var fullPath))
        {
            logger.LogWarning("Forbidden path {Path} for conversation {ConversationId}", relativePath, conversationId);
            return Results.Json(new { error = "path_forbidden", path = relativePath }, statusCode: 403);
        }

        if (!Directory.Exists(fullPath))
        {
            logger.LogWarning("Directory not found: {FullPath} for conversation {ConversationId}", fullPath, conversationId);
            return Results.Json(new { error = "directory_not_found", path = relativePath }, statusCode: 404);
        }

        var entries = new List<object>();

        foreach (var dir in Directory.GetDirectories(fullPath))
        {
            var name = Path.GetFileName(dir);
            entries.Add(new { name, type = "directory" });
        }

        foreach (var file in Directory.GetFiles(fullPath))
        {
            var name = Path.GetFileName(file);
            var info = new FileInfo(file);
            entries.Add(new { name, type = "file", size = info.Length, modified = info.LastWriteTimeUtc });
        }

        return Results.Json(new { path = relativePath, entries });
    }

    private static async Task<IResult> ServeFile(
        string conversationId,
        string filePath,
        WorkspaceManager workspaceManager,
        AuthSettings authSettings,
        IOptions<AuthOptions> authOptions,
        HttpContext httpContext,
        ILoggerFactory loggerFactory,
        bool download = false)
    {
        var logger = loggerFactory.CreateLogger("Investigator.WorkspaceFileEndpoints");

        if (!CheckAuth(httpContext, authSettings, authOptions.Value))
        {
            logger.LogWarning("Unauthorized file access for conversation {ConversationId}, path {FilePath}", conversationId, filePath);
            return Results.Json(new { error = "unauthorized" }, statusCode: 401);
        }

        var workspace = workspaceManager.FindWorkspacePath(conversationId);
        if (workspace is null)
        {
            logger.LogWarning("Workspace not found for conversation {ConversationId}", conversationId);
            return Results.Json(new { error = "workspace_not_found", conversationId }, statusCode: 404);
        }

        if (!TryResolveSafePath(workspace, filePath, out var fullPath))
        {
            logger.LogWarning("Forbidden path {FilePath} in workspace for conversation {ConversationId}", filePath, conversationId);
            return Results.Json(new { error = "path_forbidden", path = filePath }, statusCode: 403);
        }

        if (!File.Exists(fullPath))
        {
            logger.LogWarning("File not found: {FullPath} for conversation {ConversationId}", fullPath, conversationId);
            return Results.Json(new { error = "file_not_found", path = filePath }, statusCode: 404);
        }

        var contentType = ResolveContentType(fullPath);
        var fileName = Path.GetFileName(fullPath);

        if (IsSharedTokenOnly(httpContext, authSettings))
        {
            var raw = await File.ReadAllTextAsync(fullPath);
            var sanitized = LogSanitizer.MaskSuspected(LogSanitizer.Redact(raw));
            return Results.Text(sanitized, contentType);
        }

        return download
            ? Results.File(fullPath, contentType, fileName)
            : Results.File(fullPath, contentType);
    }

    private static bool CheckAuth(HttpContext httpContext, AuthSettings authSettings, AuthOptions authOptions)
    {
        if (!authSettings.IsEnabled)
            return true;

        if (httpContext.User.Identity?.IsAuthenticated == true)
            return true;

        var token = httpContext.Request.Query["token"].FirstOrDefault();
        if (!string.IsNullOrEmpty(authOptions.SharedToken)
            && string.Equals(token, authOptions.SharedToken, StringComparison.Ordinal))
            return true;

        return false;
    }

    private static bool IsSharedTokenOnly(HttpContext httpContext, AuthSettings authSettings)
    {
        if (!authSettings.IsEnabled)
            return false;

        if (httpContext.User.Identity?.IsAuthenticated == true)
            return false;

        return true;
    }

    /// <summary>
    /// Resolves a relative path under the workspace, guarding against traversal.
    /// Only allows paths under tool_outputs/.
    /// </summary>
    private static bool TryResolveSafePath(string workspace, string relativePath, out string fullPath)
    {
        fullPath = "";

        relativePath = relativePath.Replace('\\', '/');
        if (relativePath.StartsWith('/'))
            relativePath = relativePath.TrimStart('/');

        if (!relativePath.StartsWith("tool_outputs", StringComparison.OrdinalIgnoreCase))
            return false;

        var combined = Path.GetFullPath(Path.Combine(workspace, relativePath));
        var workspaceNormalized = Path.GetFullPath(workspace);

        if (!combined.StartsWith(workspaceNormalized, StringComparison.OrdinalIgnoreCase))
            return false;

        fullPath = combined;
        return true;
    }

    private static string ResolveContentType(string filePath)
    {
        var ext = Path.GetExtension(filePath);

        if (s_extraContentTypes.TryGetValue(ext, out var extra))
            return extra;

        if (s_contentTypes.TryGetContentType(filePath, out var contentType))
            return contentType;

        return "application/octet-stream";
    }
}
