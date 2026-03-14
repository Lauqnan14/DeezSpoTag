using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Services.Download.Shared.Utils;

/// <summary>
/// Command execution service for post-download commands
/// Ported from: Command execution logic in deezspotag downloader.ts
/// </summary>
public class CommandExecutionService
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly ILogger<CommandExecutionService> _logger;

    public CommandExecutionService(ILogger<CommandExecutionService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Execute post-download command
    /// Ported from: executeCommand logic in deezspotag downloader.ts afterDownloadSingle/afterDownloadCollection
    /// </summary>
    public async Task<CommandExecutionResult> ExecutePostDownloadCommandAsync(
        DeezSpoTagSettings settings, 
        string? extrasPath = null, 
        string? filename = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settings.ExecuteCommand))
            {
                return new CommandExecutionResult { Success = true, Message = "No command configured" };
            }

            _logger.LogDebug("Executing post-download command: {Command}", settings.ExecuteCommand);

            var command = PrepareCommand(settings.ExecuteCommand, extrasPath, filename);
            
            if (string.IsNullOrWhiteSpace(command))
            {
                return new CommandExecutionResult 
                { 
                    Success = false, 
                    Message = "Command is empty after variable substitution" 
                };
            }

            var result = await ExecuteCommandAsync(command, cancellationToken);
            
            if (result.Success)
            {
                _logger.LogInformation("Post-download command executed successfully");
            }
            else
            {
                _logger.LogWarning("Post-download command failed: {ErrorMessage}", result.ErrorMessage);
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error executing post-download command");
            return new CommandExecutionResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Prepare command by replacing variables
    /// Ported from: Variable replacement logic in deezspotag downloader.ts
    /// </summary>
    private string PrepareCommand(string command, string? extrasPath, string? filename)
    {
        try
        {
            var preparedCommand = command;

            // Replace folder variable
            preparedCommand = preparedCommand.Replace(
                "%folder%",
                !string.IsNullOrEmpty(extrasPath) ? ShellEscape(extrasPath) : string.Empty);

            // Replace filename variable
            preparedCommand = preparedCommand.Replace(
                "%filename%",
                !string.IsNullOrEmpty(filename) ? ShellEscape(filename) : string.Empty);

            // Clean up extra spaces
            preparedCommand = Regex.Replace(preparedCommand, @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();

            _logger.LogDebug("Prepared command: {PreparedCommand}", preparedCommand);
            return preparedCommand;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error preparing command");
            return command;
        }
    }

    /// <summary>
    /// Execute command asynchronously
    /// </summary>
    private async Task<CommandExecutionResult> ExecuteCommandAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            var result = new CommandExecutionResult();
            var startTime = DateTime.UtcNow;

            // Determine shell and command based on OS
            var (shell, shellArgs) = GetShellInfo();
            var fullCommand = $"{shellArgs} \"{command}\"";

            var processStartInfo = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = fullCommand,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory
            };

            _logger.LogDebug("Starting process: {Shell} {Args}", shell, fullCommand);

            using var process = new Process { StartInfo = processStartInfo };
            
            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    outputBuilder.AppendLine(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    errorBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait for process to complete with cancellation support
            var processTask = Task.Run(() => process.WaitForExit(), cancellationToken);
            
            try
            {
                await processTask;
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex, "Command execution was cancelled");
                
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception killEx) when (killEx is not OperationCanceledException)
                    {
                        _logger.LogWarning(killEx, "Failed to kill process after cancellation");
                    }
                }
                
                return new CommandExecutionResult
                {
                    Success = false,
                    ErrorMessage = "Command execution was cancelled"
                };
            }

            result.ExitCode = process.ExitCode;
            result.StandardOutput = outputBuilder.ToString().Trim();
            result.StandardError = errorBuilder.ToString().Trim();
            result.ExecutionTime = DateTime.UtcNow - startTime;
            result.Success = process.ExitCode == 0;

            if (!result.Success)
            {
                result.ErrorMessage = $"Command failed with exit code {process.ExitCode}";
            }

            _logger.LogDebug("Command completed with exit code: {ExitCode}, execution time: {ExecutionTime}", 
                result.ExitCode, result.ExecutionTime);

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error executing command");
            return new CommandExecutionResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Get shell information based on operating system
    /// </summary>
    private static (string shell, string args) GetShellInfo()
    {
        if (OperatingSystem.IsWindows())
        {
            return ("cmd.exe", "/c");
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return ("/bin/bash", "-c");
        }
        else
        {
            // Fallback to bash
            return ("/bin/bash", "-c");
        }
    }

    /// <summary>
    /// Escape shell arguments
    /// Ported from: shellEscape function in deezspotag core.ts
    /// </summary>
    private static string ShellEscape(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        // If argument doesn't contain special characters, return as-is
        if (!Regex.IsMatch(argument, @"[^\w\-\./]", RegexOptions.None, RegexTimeout))
        {
            return argument;
        }

        return OperatingSystem.IsWindows()
            ? "\"" + argument.Replace("\"", "\"\"") + "\""
            : "'" + argument.Replace("'", "'\"'\"'") + "'";
    }

    /// <summary>
    /// Validate command before execution
    /// </summary>
    public static CommandValidationResult ValidateCommand(string command)
    {
        try
        {
            var result = new CommandValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(command))
            {
                result.IsValid = false;
                result.ErrorMessage = "Command cannot be empty";
                return result;
            }

            // Check for potentially dangerous commands
            var dangerousPatterns = new[]
            {
                @"\brm\s+-rf\s+/",  // rm -rf /
                @"\bformat\s+c:",   // format c:
                @"\bdel\s+/s\s+/q", // del /s /q
                @">\s*/dev/null",   // Redirect to /dev/null (could hide malicious output)
            };

            var matchedDangerousPattern = Array.Find(
                dangerousPatterns,
                pattern => Regex.IsMatch(command, pattern, RegexOptions.IgnoreCase, RegexTimeout));
            if (!string.IsNullOrEmpty(matchedDangerousPattern))
            {
                result.IsValid = false;
                result.ErrorMessage = $"Command contains potentially dangerous pattern: {matchedDangerousPattern}";
                result.IsDangerous = true;
                return result;
            }

            // Check command length
            if (command.Length > 2048)
            {
                result.IsValid = false;
                result.ErrorMessage = "Command is too long (maximum 2048 characters)";
                return result;
            }

            // Extract and validate variables
            var variables = Regex.Matches(command, @"%(\w+)%", RegexOptions.None, RegexTimeout)
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            var validVariables = new[] { "folder", "filename" };
            var invalidVariables = variables.Except(validVariables).ToList();

            if (invalidVariables.Count > 0)
            {
                result.IsValid = false;
                result.ErrorMessage = $"Invalid variables: {string.Join(", ", invalidVariables)}. Valid variables: {string.Join(", ", validVariables)}";
                return result;
            }

            result.Variables = variables;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CommandValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Error validating command: {ex.Message}"
            };
        }
    }
}

/// <summary>
/// Result of command execution
/// </summary>
public class CommandExecutionResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = "";
    public string StandardError { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public TimeSpan ExecutionTime { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// Result of command validation
/// </summary>
public class CommandValidationResult
{
    public bool IsValid { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsDangerous { get; set; }
    public List<string> Variables { get; set; } = new();
}
