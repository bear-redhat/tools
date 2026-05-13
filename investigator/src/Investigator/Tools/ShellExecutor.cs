using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Investigator.Contracts;
using Investigator.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Investigator.Tools;

public sealed class ShellExecutor : IInvestigatorTool, ISystemPromptContributor
{
    private string _shellPath = null!;
    private bool _isPowerShell;
    private bool _useRunUser;
    private ToolDefinition _definition = null!;
    private readonly ShellOptions _options;
    private readonly int _hardCapBytes;

    public bool IsPowerShell => _isPowerShell;

    public string? GetSystemPromptSection()
    {
        if (_isPowerShell)
            return """
                SHELL ENVIRONMENT:
                Your shell is PowerShell on Windows. Observe the following constraints:
                - No heredocs (<< 'EOF'), no 2>/dev/null, no $(...) subshells, no single-quote escaping rules from bash.
                - No Linux coreutils: 'find -type f', 'grep -r', 'base64 -d', 'sort', 'xargs', 'wc', 'head', 'tail' will fail or behave differently.
                - Use PowerShell cmdlets: Get-ChildItem (instead of find), Select-String (instead of grep), Get-Content (instead of cat), [Convert]::FromBase64String (instead of base64 -d).
                - For complex logic, prefer python -c one-liners or write a short .py script.
                - When piping, use PowerShell pipeline syntax: Get-Content file.txt | Select-String "pattern"
                """;

        return """
            SHELL ENVIRONMENT:
            Your shell is bash on Linux. Standard coreutils are available (grep, awk, sed, jq, curl, openssl, python3, etc.).
            """;
    }

    public ShellExecutor(IOptions<ShellOptions> options, IOptions<ToolOutputOptions> toolOutputOptions)
    {
        _options = options.Value;
        _hardCapBytes = toolOutputOptions.Value.HardCapBytes;
    }

    public Task RegisterAsync(CancellationToken ct = default)
    {
        var configuredPath = _options.Path;

        if (!string.IsNullOrEmpty(configuredPath))
        {
            _shellPath = configuredPath;
            _isPowerShell = IsPowerShellExecutable(configuredPath);
            _useRunUser = false;
        }
        else if (OperatingSystem.IsLinux())
        {
            _shellPath = "bash";
            _isPowerShell = false;
            _useRunUser = _options.UseRunUser ?? Environment.IsPrivilegedProcess;
        }
        else if (OperatingSystem.IsWindows())
        {
            _shellPath = "powershell";
            _isPowerShell = true;
            _useRunUser = false;
        }
        else
        {
            _shellPath = "bash";
            _isPowerShell = false;
            _useRunUser = false;
        }

        _definition = BuildDefinition();
        return Task.CompletedTask;
    }

    public ToolDefinition Definition => _definition;

    private ToolDefinition BuildDefinition()
    {
        string cmdDesc;
        string toolDesc;

        if (_isPowerShell)
        {
            cmdDesc = "PowerShell command to run in the conversation workspace. "
                + "This is PowerShell -- do NOT use bash/Linux syntax (no heredocs, no 2>/dev/null, no 'find -type f', no 'grep -r', no 'base64 -d'). "
                + "Use PowerShell cmdlets (Get-ChildItem, Select-String, Get-Content) or Python one-liners. "
                + "Do NOT use for oc/kubectl -- use run_oc instead.";
            toolDesc = "Execute a PowerShell command in the workspace. "
                + "IMPORTANT: This shell is PowerShell on Windows -- bash syntax will fail. "
                + "Use PowerShell cmdlets (Select-String instead of grep, Get-ChildItem instead of find, "
                + "[Convert]::FromBase64String instead of base64 -d) or python -c one-liners. "
                + "Do NOT use heredocs, 2>/dev/null, or other bash-isms. "
                + "Do NOT use this for oc/kubectl commands -- use run_oc instead.";
        }
        else
        {
            cmdDesc = "Shell command to run in the conversation workspace. "
                + "Use for data processing (grep, awk, jq), Python scripts, network diagnostics (curl, dig), "
                + "cert inspection (openssl). Do NOT use for oc/kubectl -- use run_oc instead.";
            toolDesc = "Execute a command in the workspace shell. Use for data processing, "
                + "text filtering (grep, awk, jq), Python scripts, network diagnostics (curl, dig), "
                + "cert inspection (openssl), or any general-purpose computation. "
                + "Do NOT use this for oc/kubectl commands -- use run_oc instead.";
        }

        var paramSchema = JsonDocument.Parse($$"""
        {
            "type": "object",
            "properties": {
                "command": {
                    "type": "string",
                    "description": "{{cmdDesc}}"
                }
            },
            "required": ["command"]
        }
        """).RootElement.Clone();

        return new ToolDefinition(
            Name: "run_shell",
            Description: toolDesc,
            ParameterSchema: paramSchema,
            DefaultTimeout: TimeSpan.FromSeconds(60),
            TruncateOutput: false);
    }

    public async Task<ToolResult> InvokeAsync(JsonElement parameters, ToolContext context, CancellationToken ct)
    {
        var command = parameters.GetProperty("command").GetString() ?? "";

        if (string.IsNullOrWhiteSpace(command))
        {
            context.Logger.LogError("run_shell called with empty command parameter");
            return new ToolResult("Error: 'command' parameter is required and was empty.", ExitCode: 1);
        }

        ProcessStartInfo psi;

        if (_useRunUser)
        {
            context.Logger.LogDebug("run_shell: executing via runuser -> {Shell}, cwd={Cwd}", _shellPath, context.WorkspacePath);
            psi = new ProcessStartInfo("runuser")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = context.WorkspacePath,
            };
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add("agent");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(_shellPath);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            context.Logger.LogDebug("run_shell: executing via {Shell} (powershell={IsPowerShell}), cwd={Cwd}",
                _shellPath, _isPowerShell, context.WorkspacePath);
            psi = new ProcessStartInfo(_shellPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = context.WorkspacePath,
            };

            if (_isPowerShell)
            {
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(command);
            }
            else
            {
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
            }
        }

        var (outputPath, outputRelative) = context.AllocateOutputFile("run_shell");
        var headTail = new HeadTailBuffer(headLines: 20, tailLines: 10);
        StreamWriter? fileWriter = null;
        Process? proc = null;
        try
        {
            fileWriter = new StreamWriter(outputPath, append: false, Encoding.UTF8) { AutoFlush = false };

            proc = Process.Start(psi);
            if (proc is null)
            {
                context.Logger.LogError("run_shell: failed to start process {Path}", _shellPath);
                return new ToolResult($"Failed to start {_shellPath} process", ExitCode: -1, ReproCommand: command);
            }

            var readOut = Task.Run(async () =>
            {
                while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    await fileWriter.WriteLineAsync(line);
                    headTail.Add(line);
                    context.OnOutputLine?.Invoke(line);
                }
            }, ct);

            var stderr = new StringBuilder();
            var readErr = Task.Run(async () =>
            {
                while (await proc.StandardError.ReadLineAsync(ct) is { } line)
                    stderr.AppendLine(line);
            }, ct);

            await proc.WaitForExitAsync(ct);
            await Task.WhenAll(readOut, readErr);

            if (stderr.Length > 0)
            {
                context.Logger.LogDebug("run_shell: stderr ({Length} chars) for command '{Command}'", stderr.Length, command);
                await fileWriter.WriteAsync(stderr.ToString());
            }

            await fileWriter.FlushAsync();

            if (proc.ExitCode != 0)
                context.Logger.LogWarning("run_shell: command '{Command}' exited with code {Code}", command, proc.ExitCode);

            var truncated = headTail.BuildRaw();
            if (truncated.Length > _hardCapBytes)
            {
                context.Logger.LogWarning("Tool output exceeded hard cap ({Length} > {Max}), truncating",
                    truncated.Length, _hardCapBytes);
                truncated = truncated[.._hardCapBytes] + $"\n... [truncated at {_hardCapBytes / 1024}KB hard cap]";
            }
            return new ToolResult(truncated, ExitCode: proc.ExitCode, ReproCommand: command,
                LineCount: headTail.LineCount, OutputFile: outputRelative);
        }
        catch (OperationCanceledException)
        {
            KillProcess(proc, command, context.Logger);
            context.Logger.LogWarning("run_shell: command '{Command}' was cancelled (timeout)", command);
            if (fileWriter is not null)
            {
                await fileWriter.WriteLineAsync("[timed out]");
                await fileWriter.FlushAsync();
            }
            headTail.Add("[timed out]");
            var truncated = headTail.BuildRaw();
            return new ToolResult(truncated, ExitCode: -1, TimedOut: true, ReproCommand: command,
                LineCount: headTail.LineCount, OutputFile: outputRelative);
        }
        finally
        {
            if (fileWriter is not null)
                await fileWriter.DisposeAsync();
            proc?.Dispose();
        }
    }

    private static void KillProcess(Process? proc, string command, ILogger logger)
    {
        if (proc is null || proc.HasExited) return;
        try
        {
            proc.Kill(entireProcessTree: true);
            logger.LogDebug("run_shell: killed process tree for command '{Command}'", command);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "run_shell: failed to kill process for command '{Command}'", command);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            logger.LogWarning(ex, "run_shell: failed to kill process for command '{Command}'", command);
        }
    }

    private static bool IsPowerShellExecutable(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return name is "powershell" or "pwsh";
    }
}
