using System.Text.Json;
using Investigator.Models;
using Microsoft.Extensions.Options;

namespace Investigator.Tools;

public sealed class WorkspaceManager
{
    private readonly WorkspaceOptions _options;
    private readonly ILogger<WorkspaceManager> _logger;

    public WorkspaceManager(IOptions<WorkspaceOptions> workspaceOptions, ILogger<WorkspaceManager> logger)
    {
        _options = workspaceOptions.Value;
        _logger = logger;
    }

    public string CreateWorkspace()
    {
        var root = _options.RootPath;
        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(Path.GetTempPath(), "investigator", "workspaces");
            _logger.LogInformation("Workspace:RootPath not configured, using temp directory: {Path}", root);
        }

        var id = Guid.NewGuid().ToString("N")[..8];
        var dirName = $"conv-{DateTime.UtcNow:yyyy-MM-dd}-{id}";
        var path = Path.Combine(root, dirName);

        try
        {
            Directory.CreateDirectory(path);
            Directory.CreateDirectory(Path.Combine(path, "tool_outputs"));
            _logger.LogInformation("Created workspace: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create workspace directory at {Path}", path);
            throw;
        }

        return path;
    }

    public async Task AppendTranscriptAsync(string workspacePath, object entry)
    {
        var file = Path.Combine(workspacePath, "transcript.jsonl");
        try
        {
            var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = false });
            await File.AppendAllTextAsync(file, json + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write transcript entry to {File}", file);
        }
    }
}
