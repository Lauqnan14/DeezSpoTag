using System.Diagnostics;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using DeezSpoTag.Services.Download.Apple;

namespace DeezSpoTag.Web.Services;

public sealed record AppleMusicWrapperStatusSnapshot(
    string Status,
    string Message,
    string? Email,
    bool NeedsTwoFactor,
    bool WrapperReady)
{
    public static AppleMusicWrapperStatusSnapshot Missing =>
        new("missing", "Wrapper not started.", null, false, false);
}

public sealed record AppleMusicWrapperDiagnostics(
    string? WrapperHost,
    bool PortsOpen,
    bool AccountInfoReachable,
    bool HasDevToken,
    bool HasMusicToken,
    string? StorefrontId,
    string? Message);

public sealed record AppleMusicWrapperHelperResult(
    bool Success,
    int ExitCode,
    string? Output,
    string? Error,
    string? HelperPath);

public sealed record AppleMusicWrapperHealthResult(
    bool AcceptingLoginCommands,
    string? Message,
    string? Host,
    bool PortsOpen,
    bool AccountInfoReachable,
    bool HasDevToken,
    bool HasMusicToken,
    string? StorefrontId,
    AppleMusicWrapperHelperResult HelperStatus);

public sealed class AppleMusicWrapperService : IHostedService, IDisposable, IAppleWrapperStatusProvider
{
    private enum TwoFactorProbeState
    {
        Unknown = 0,
        Waiting = 1,
        NotWaiting = 2
    }

    private const string StatusStarting = "starting";
    private const string StatusWaiting = "waitingForTwoFactor";
    private const string StatusRunning = "running";
    private const string StatusSuccess = "success";
    private const string StatusFailed = "failed";
    private const string StatusStopped = "stopped";
    private const string StatusMissing = "missing";
    private const string ExternalWrapperHostEnv = "DEEZSPOTAG_APPLE_WRAPPER_HOST";
    private const string ExternalWrapperHelperModeEnv = "DEEZSPOTAG_APPLE_WRAPPER_HELPER_MODE";
    private const string ExternalWrapperContainerNameEnv = "DEEZSPOTAG_APPLE_WRAPPER_CONTAINER_NAME";
    private const string ExternalWrapperComposeFileEnv = "DEEZSPOTAG_APPLE_WRAPPER_COMPOSE_FILE";
    private const string LoopbackHost = "127.0.0.1";
    private const string DefaultWrapperContainerName = "apple-wrapper";
    private const string HelperModeAuto = "auto";
    private const string HelperModeDirect = "direct";
    private const string HelperModeCompose = "compose";
    private const string ToolsDirectory = "Tools";
    private const string AppleMusicWrapperDirectory = "AppleMusicWrapper";
    private static readonly string[] HelperLogoutArgs = ["logout"];
    private static readonly string[] HelperRunArgs = ["run"];
    private static readonly string[] HelperStatusArgs = ["status"];
    private static readonly string[] HelperSanitizeArgs = ["sanitize"];
    private static readonly string[] HelperProbeTwoFactorArgs = ["probe-2fa"];
    private readonly IWebHostEnvironment _environment;
    private readonly PlatformAuthService _platformAuthService;
    private readonly ILogger<AppleMusicWrapperService> _logger;
    private readonly object _sync = new();
    private readonly Queue<string> _recentOutput = new();

    private Process? _process;
    private CancellationTokenSource? _processCts;
    private Task? _outputTask;
    private Task? _healthTask;
    private AppleMusicWrapperStatusSnapshot _status = AppleMusicWrapperStatusSnapshot.Missing;
    private AppleMusicWrapperDiagnostics? _diagnostics;
    private string? _email;
    private DateTimeOffset? _startedAt;
    private bool _awaitingTwoFactor;
    private string? _externalStartError;
    private DateTimeOffset? _externalStartErrorAt;
    private bool _authStateReady;
    private bool _authStateBootstrapped;
    private bool _loginInProgress;
    private DateTimeOffset? _twoFactorSubmittedAt;
    private DateTimeOffset? _lastTwoFactorProbeAt;
    private bool _lastTwoFactorProbeKnown;
    private bool _lastTwoFactorProbeResult;
    private static readonly HttpClient ExternalWrapperClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private enum ExternalWrapperContainerState
    {
        Unknown = 0,
        Running = 1,
        Exited = 2
    }

    public AppleMusicWrapperService(
        IWebHostEnvironment environment,
        PlatformAuthService platformAuthService,
        ILogger<AppleMusicWrapperService> logger)
    {
        _environment = environment;
        _platformAuthService = platformAuthService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureAuthStateBootstrapped();
        var shouldAutoStart = false;
        lock (_sync)
        {
            shouldAutoStart = _authStateReady || _loginInProgress || _awaitingTwoFactor;
        }

        if (shouldAutoStart)
        {
            _ = EnsureExternalWrapperRunningAsync(cancellationToken);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopProcess("service_stop");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StopProcess("dispose");
    }

    public AppleMusicWrapperStatusSnapshot GetStatus()
    {
        lock (_sync)
        {
            return GetExternalWrapperStatus();
        }
    }

    public IReadOnlyList<string> GetRecentOutput()
    {
        lock (_sync)
        {
            return _recentOutput.ToList();
        }
    }

    public AppleMusicWrapperDiagnostics? GetDiagnostics()
    {
        lock (_sync)
        {
            return _diagnostics;
        }
    }

    DeezSpoTag.Services.Download.Apple.AppleWrapperStatusSnapshot IAppleWrapperStatusProvider.GetStatus()
    {
        var status = GetStatus();
        return new DeezSpoTag.Services.Download.Apple.AppleWrapperStatusSnapshot(
            status.Status,
            status.Message,
            status.NeedsTwoFactor,
            status.WrapperReady);
    }

    public async Task<AppleMusicWrapperStatusSnapshot> StartLoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return new AppleMusicWrapperStatusSnapshot(StatusFailed, "Email and password are required.", email, false, false);
        }

        if (IsExternalModeEnabled())
        {
            StopProcess("external_login_restart");

            // Always clear any persisted wrapper session before a credentialed login attempt.
            // This prevents stale authenticated state from being misread as a fresh successful login.
            var helperPath = ResolveExternalWrapperHelperPath();
            if (!string.IsNullOrWhiteSpace(helperPath) && File.Exists(helperPath))
            {
                try
                {
                    var resetResult = await RunExternalWrapperHelperAsync(HelperLogoutArgs, cancellationToken);
                    if (!resetResult.Success)
                    {
                        var resetError = resetResult.Error ?? resetResult.Output;
                        if (IsWrapperContainerMissingError(resetError))
                        {
                            _logger.LogInformation("Pre-login wrapper session reset skipped: {Error}", resetError);
                        }
                        else
                        {
                            _logger.LogWarning("Pre-login wrapper session reset failed: {Error}", resetError);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Pre-login wrapper session reset failed.");
                }
            }

            lock (_sync)
            {
                ClearRecentOutputLocked();
                _status = new AppleMusicWrapperStatusSnapshot(StatusStarting, "Starting wrapper login...", email, false, false);
                _email = email;
                _awaitingTwoFactor = false;
                _authStateReady = false;
                _loginInProgress = true;
                _startedAt = DateTimeOffset.UtcNow;
                _externalStartError = null;
                _externalStartErrorAt = null;
                _twoFactorSubmittedAt = null;
                _lastTwoFactorProbeAt = null;
                _lastTwoFactorProbeKnown = false;
                _lastTwoFactorProbeResult = false;
            }
            _ = UpdateAuthStateAsync(false);

            var startResult = await StartExternalWrapperLoginAsync(email, password, cancellationToken);
            if (!startResult.Success)
            {
                lock (_sync)
                {
                    _status = new AppleMusicWrapperStatusSnapshot(StatusFailed, startResult.Error ?? "External wrapper login failed.", email, false, false);
                    _loginInProgress = false;
                    _startedAt = null;
                    _twoFactorSubmittedAt = null;
                }
                return _status;
            }

            lock (_sync)
            {
                _status = new AppleMusicWrapperStatusSnapshot(
                    StatusStarting,
                    "Login request sent. Waiting for wrapper response.",
                    email,
                    false,
                    false);
                _awaitingTwoFactor = false;
                _loginInProgress = true;
                _startedAt = _startedAt ?? DateTimeOffset.UtcNow;
                _twoFactorSubmittedAt = null;
            }

            var externalUpdated = await WaitForWrapperStatusAsync(TimeSpan.FromSeconds(12), cancellationToken);
            return externalUpdated;
        }
        return new AppleMusicWrapperStatusSnapshot(
            StatusFailed,
            "Internal Apple Music wrapper support has been removed. Use the external wrapper container.",
            email,
            false,
            false);
    }

    private async Task<AppleMusicWrapperStatusSnapshot> WaitForWrapperStatusAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            var status = GetStatus();
            if (!string.Equals(status.Status, StatusStarting, StringComparison.OrdinalIgnoreCase))
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }

        return GetStatus();
    }

    private async Task<AppleMusicWrapperStatusSnapshot> WaitForTwoFactorCompletionAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            var status = GetStatus();
            if (status.WrapperReady)
            {
                return status;
            }

            if (string.Equals(status.Status, StatusFailed, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status.Status, StatusStopped, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status.Status, StatusMissing, StringComparison.OrdinalIgnoreCase))
            {
                return status;
            }

            if (status.NeedsTwoFactor ||
                string.Equals(status.Status, StatusWaiting, StringComparison.OrdinalIgnoreCase))
            {
                return status;
            }

            if (string.Equals(status.Status, StatusStarting, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status.Status, StatusRunning, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                continue;
            }

            return status;
        }

        return GetStatus();
    }

    public async Task<AppleMusicWrapperStatusSnapshot> SubmitTwoFactorAsync(string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return new AppleMusicWrapperStatusSnapshot(StatusFailed, "Verification code is required.", _email, true, false);
        }

        if (IsExternalModeEnabled())
        {
            var currentStatus = GetStatus();
            var wrapperAwaitingTwoFactor = currentStatus.NeedsTwoFactor ||
                                           string.Equals(currentStatus.Status, StatusWaiting, StringComparison.OrdinalIgnoreCase);
            if (!wrapperAwaitingTwoFactor)
            {
                return new AppleMusicWrapperStatusSnapshot(
                    StatusFailed,
                    "Wrapper is not waiting for a two-factor code. Restart Apple Music login and try again.",
                    _email,
                    false,
                    false);
            }

            var twoFactorResult = await WriteExternalTwoFactorAsync(code.Trim(), cancellationToken);
            if (!twoFactorResult.Success)
            {
                var message = string.IsNullOrWhiteSpace(twoFactorResult.Error)
                    ? "Failed to submit verification code to the wrapper."
                    : twoFactorResult.Error;
                lock (_sync)
                {
                    _status = new AppleMusicWrapperStatusSnapshot(
                        StatusFailed,
                        message,
                        _email,
                        true,
                        false);
                    _awaitingTwoFactor = false;
                    _loginInProgress = false;
                    _startedAt = null;
                    _twoFactorSubmittedAt = null;
                }
                return _status;
            }
            lock (_sync)
            {
                _status = new AppleMusicWrapperStatusSnapshot(
                    StatusWaiting,
                    "Verification code submitted. Waiting for wrapper confirmation.",
                    _email,
                    true,
                    false);
                _awaitingTwoFactor = true;
                _loginInProgress = true;
                _startedAt = _startedAt ?? DateTimeOffset.UtcNow;
                _twoFactorSubmittedAt = DateTimeOffset.UtcNow;
                _lastTwoFactorProbeAt = null;
                _lastTwoFactorProbeKnown = false;
                _lastTwoFactorProbeResult = false;
            }
            // Wrapper confirmation can lag when container IO is busy; avoid timing out too aggressively.
            return await WaitForTwoFactorCompletionAsync(TimeSpan.FromSeconds(75), cancellationToken);
        }

        return new AppleMusicWrapperStatusSnapshot(
            StatusFailed,
            "Internal Apple Music wrapper support has been removed. Use the external wrapper container.",
            _email,
            false,
            false);
    }

    private async Task<(bool Success, string? Error)> TryRunExternalWrapperHelperAsync(string[] args, CancellationToken cancellationToken, string? forcedHelperMode = null)
    {
        var helperPath = ResolveExternalWrapperHelperPath();
        if (string.IsNullOrWhiteSpace(helperPath) || !File.Exists(helperPath))
        {
            return (false, "External wrapper helper not found. Set DEEZSPOTAG_APPLE_WRAPPER_HELPER.");
        }

        try
        {
            var startInfo = CreateExternalWrapperHelperStartInfo(helperPath, args, forcedHelperMode);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return (false, "Failed to start external wrapper helper.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(error) ? output : error;
                return (false, string.IsNullOrWhiteSpace(message) ? $"External wrapper helper failed (exit {process.ExitCode})." : message.Trim());
            }

            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "External wrapper helper failed.");
            return (false, ex.Message);
        }
    }

    private async Task EnsureExternalWrapperRunningAsync(CancellationToken cancellationToken)
    {
        EnsureAuthStateBootstrapped();
        var shouldAutoStart = false;
        lock (_sync)
        {
            shouldAutoStart = _authStateReady || _loginInProgress || _awaitingTwoFactor;
        }

        if (!shouldAutoStart)
        {
            return;
        }

        var helperPath = ResolveExternalWrapperHelperPath();
        if (string.IsNullOrWhiteSpace(helperPath) || !File.Exists(helperPath))
        {
            return;
        }

        // Use a generous timeout — the wrapper container may still be booting after a host restart.
        // A false negative here causes force-recreation of a healthy container, which triggers
        // Apple re-authentication and unwanted 2FA codes.
        var hostCandidates = ResolveExternalWrapperHosts();
        var portsOpen = hostCandidates.Any(candidate => AreWrapperPortsOpen(candidate, TimeSpan.FromSeconds(3)));
        if (portsOpen)
        {
            return;
        }

        var result = await RunExternalWrapperHelperAsync(HelperRunArgs, cancellationToken);
        if (!result.Success)
        {
            _logger.LogWarning("Failed to auto-start external wrapper. {Message}", result.Error ?? result.Output);
            lock (_sync)
            {
                _externalStartError = result.Error ?? result.Output ?? "Failed to auto-start external wrapper.";
                _externalStartErrorAt = DateTimeOffset.UtcNow;
            }
        }
    }

    public async Task<AppleMusicWrapperHelperResult> RunExternalWrapperHelperAsync(string[] args, CancellationToken cancellationToken)
    {
        var helperPath = ResolveExternalWrapperHelperPath();
        if (string.IsNullOrWhiteSpace(helperPath) || !File.Exists(helperPath))
        {
            return new AppleMusicWrapperHelperResult(
                false,
                -1,
                null,
                "External wrapper helper not found. Set DEEZSPOTAG_APPLE_WRAPPER_HELPER.",
                helperPath);
        }

        try
        {
            var startInfo = CreateExternalWrapperHelperStartInfo(helperPath, args);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new AppleMusicWrapperHelperResult(false, -1, null, "Failed to start external wrapper helper.", helperPath);
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            var success = process.ExitCode == 0;
            return new AppleMusicWrapperHelperResult(success, process.ExitCode, output, error, helperPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "External wrapper helper failed.");
            return new AppleMusicWrapperHelperResult(false, -1, null, ex.Message, helperPath);
        }
    }

    public Task<AppleMusicWrapperHelperResult> GetExternalWrapperHelperStatusAsync(CancellationToken cancellationToken)
    {
        return RunExternalWrapperHelperAsync(HelperStatusArgs, cancellationToken);
    }

    public async Task<AppleMusicWrapperHelperResult> LogoutExternalWrapperSessionAsync(CancellationToken cancellationToken)
    {
        AppleMusicWrapperHelperResult result;
        try
        {
            result = await RunExternalWrapperHelperAsync(HelperLogoutArgs, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to clear Apple Music wrapper session.");
            result = new AppleMusicWrapperHelperResult(false, -1, null, ex.Message, null);
        }

        lock (_sync)
        {
            _authStateReady = false;
            _awaitingTwoFactor = false;
            _loginInProgress = false;
            _startedAt = null;
            _twoFactorSubmittedAt = null;
            _lastTwoFactorProbeAt = null;
            _lastTwoFactorProbeKnown = false;
            _lastTwoFactorProbeResult = false;
            _email = null;
            _status = new AppleMusicWrapperStatusSnapshot(
                result.Success ? StatusMissing : StatusFailed,
                result.Success
                    ? "Wrapper session cleared. Enter your Apple ID credentials to login."
                    : (result.Error ?? "Failed to clear Apple Music wrapper session."),
                null,
                false,
                false);
        }

        try
        {
            await _platformAuthService.UpdateAsync(state =>
            {
                state.AppleMusic ??= new AppleMusicAuth();
                state.AppleMusic.WrapperReady = false;
                state.AppleMusic.Email = null;
                state.AppleMusic.WrapperLoggedInAt = null;
                return 0;
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to persist Apple Music logout state.");
        }

        return result;
    }

    public async Task<AppleMusicWrapperHealthResult> CheckExternalWrapperHealthAsync(CancellationToken cancellationToken)
    {
        var helper = await GetExternalWrapperHelperStatusAsync(cancellationToken);
        var context = await BuildWrapperHealthContextAsync(helper, cancellationToken);
        var runningHint = IsWrapperRunningHint(helper.Output ?? helper.Error);
        var accepting = helper.Success && (runningHint || context.PortsOpen);
        var message = BuildWrapperHealthMessage(helper, context, runningHint);

        return new AppleMusicWrapperHealthResult(
            accepting,
            message,
            context.Host,
            context.PortsOpen,
            context.AccountInfoReachable,
            context.HasDevToken,
            context.HasMusicToken,
            context.StorefrontId,
            helper);
    }

    private async Task<WrapperHealthContext> BuildWrapperHealthContextAsync(
        AppleMusicWrapperHelperResult helper,
        CancellationToken cancellationToken)
    {
        var context = CreateWrapperHealthContext();
        if (!context.PortsOpen && helper.Success)
        {
            await EnsureExternalWrapperRunningAsync(cancellationToken);
            context = CreateWrapperHealthContext();
        }

        ResolveWrapperHealthAccountInfo(context);
        return context;
    }

    private static WrapperHealthContext CreateWrapperHealthContext()
    {
        var hostCandidates = ResolveExternalWrapperHosts().ToList();
        var host = hostCandidates.FirstOrDefault() ?? LoopbackHost;
        var hostsWithPorts = hostCandidates
            .Where(candidate => AreWrapperPortsOpen(candidate, TimeSpan.FromMilliseconds(200)))
            .ToList();

        if (hostsWithPorts.Count > 0)
        {
            host = hostsWithPorts[0];
        }

        return new WrapperHealthContext(host, hostsWithPorts);
    }

    private static void ResolveWrapperHealthAccountInfo(WrapperHealthContext context)
    {
        if (!context.PortsOpen)
        {
            return;
        }

        foreach (var candidate in context.HostsWithPorts)
        {
            var tokenStateValid = TryFetchExternalAccountInfo(
                candidate,
                out var hasDevToken,
                out var hasMusicToken,
                out var accountInfoReachable,
                out var storefrontId);

            context.Host = candidate;
            context.AccountInfoReachable = accountInfoReachable;
            context.HasDevToken = hasDevToken;
            context.HasMusicToken = hasMusicToken;
            context.StorefrontId = storefrontId;

            if (tokenStateValid || accountInfoReachable)
            {
                break;
            }
        }
    }

    private static bool IsWrapperRunningHint(string? output)
    {
        var value = output ?? string.Empty;
        return value.Contains("up", StringComparison.OrdinalIgnoreCase)
            || value.Contains("running", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildWrapperHealthMessage(
        AppleMusicWrapperHelperResult helper,
        WrapperHealthContext context,
        bool runningHint)
    {
        if (!helper.Success)
        {
            return helper.Error ?? "Wrapper helper failed.";
        }

        if (!runningHint && !context.PortsOpen)
        {
            return "Wrapper container is not running or ports are not reachable.";
        }

        if (!context.PortsOpen)
        {
            return "Wrapper container reported running, but ports 10020/20020 are not reachable.";
        }

        if (!context.AccountInfoReachable)
        {
            return "Wrapper ports are open, but the account endpoint is not reachable yet.";
        }

        return string.IsNullOrWhiteSpace(context.StorefrontId)
            ? "Wrapper container is running and accepting login commands."
            : $"Wrapper container is running and accepting login commands (storefront: {context.StorefrontId}).";
    }

    private string? ResolveExternalWrapperHelperPath()
    {
        var envPath = Environment.GetEnvironmentVariable("DEEZSPOTAG_APPLE_WRAPPER_HELPER");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return envPath;
        }

        var contentRootPath = Path.Join(_environment.ContentRootPath, ToolsDirectory, AppleMusicWrapperDirectory, "docker", "apple-wrapperctl.sh");
        if (File.Exists(contentRootPath))
        {
            EnsureExecutable(contentRootPath);
            return contentRootPath;
        }

        var contentRootSiblingPath = Path.GetFullPath(Path.Join(
            _environment.ContentRootPath,
            "..",
            ToolsDirectory,
            AppleMusicWrapperDirectory,
            "docker",
            "apple-wrapperctl.sh"));
        if (File.Exists(contentRootSiblingPath))
        {
            EnsureExecutable(contentRootSiblingPath);
            return contentRootSiblingPath;
        }

        var repoRoot = ResolveRepoRoot();
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            var repoPath = Path.Join(repoRoot, ToolsDirectory, AppleMusicWrapperDirectory, "docker", "apple-wrapperctl.sh");
            if (File.Exists(repoPath))
            {
                EnsureExecutable(repoPath);
                return repoPath;
            }
        }

        return contentRootPath;
    }

    private async Task<(bool Success, string? Error)> StartExternalWrapperLoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var loginArgs = new[] { "login", $"{email}:{password}" };
        var helperResult = await TryRunExternalWrapperHelperAsync(loginArgs, cancellationToken);
        if (!helperResult.Success && IsWrapperContainerMissingError(helperResult.Error))
        {
            // Host dotnet runs may start without apple-wrapper created yet; compose-mode login bootstraps it.
            helperResult = await TryRunExternalWrapperHelperAsync(loginArgs, cancellationToken, HelperModeCompose);
        }
        if (helperResult.Success)
        {
            return (true, null);
        }

        var hostCandidates = ResolveExternalWrapperHosts();
        var portsOpen = hostCandidates.Any(candidate => AreWrapperPortsOpen(candidate, TimeSpan.FromMilliseconds(200)));
        if (portsOpen)
        {
            return (false, "Wrapper is already running, but login command failed. Configure DEEZSPOTAG_APPLE_WRAPPER_HELPER (apple-wrapperctl.sh) correctly.");
        }

        return (false, helperResult.Error ?? "External wrapper login failed.");
    }

    private async Task<(bool Success, string? Error)> WriteExternalTwoFactorAsync(string code, CancellationToken cancellationToken)
    {
        (bool Success, string? Error) helperResult = (false, null);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            helperResult = await TryRunExternalWrapperHelperAsync(new[] { "2fa", code.Trim() }, cancellationToken);
            if (helperResult.Success)
            {
                return (true, null);
            }

            if (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350d * attempt), cancellationToken);
            }
        }

        var message = string.IsNullOrWhiteSpace(helperResult.Error)
            ? "External wrapper helper failed."
            : helperResult.Error;
        _logger.LogWarning("External wrapper helper failed to submit 2FA. {Message}", message);
        return (false, message);
    }

    private ProcessStartInfo CreateExternalWrapperHelperStartInfo(string helperPath, IEnumerable<string> args, string? forcedHelperMode = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        ApplyExternalWrapperHelperEnvironment(startInfo, forcedHelperMode);
        return startInfo;
    }

    private void ApplyExternalWrapperHelperEnvironment(ProcessStartInfo startInfo, string? forcedHelperMode = null)
    {
        if (!startInfo.Environment.TryGetValue("TMPDIR", out var tmpDir) || string.IsNullOrWhiteSpace(tmpDir))
        {
            startInfo.Environment["TMPDIR"] = "/tmp";
        }

        startInfo.Environment["APPLE_WRAPPER_CONTAINER_NAME"] = ResolveExternalWrapperContainerName();
        var composeFile = ResolveExternalWrapperComposeFile();
        if (!string.IsNullOrWhiteSpace(composeFile))
        {
            startInfo.Environment["APPLE_WRAPPER_COMPOSE_FILE"] = composeFile;
        }

        switch (ResolveExternalWrapperHelperMode(forcedHelperMode))
        {
            case HelperModeCompose:
                startInfo.Environment["APPLE_WRAPPER_DISABLE_COMPOSE"] = "0";
                startInfo.Environment["APPLE_WRAPPER_ALLOW_COMPOSE_IN_CONTAINER"] = "1";
                startInfo.Environment["APPLE_WRAPPER_ENV_FILE"] = "/tmp/apple-wrapper.env";
                break;
            case HelperModeAuto:
                break;
            default:
                // Force direct Docker mode by default to keep host dotnet and containerized dotnet behavior aligned.
                startInfo.Environment["APPLE_WRAPPER_DISABLE_COMPOSE"] = "1";
                startInfo.Environment["APPLE_WRAPPER_ALLOW_COMPOSE_IN_CONTAINER"] = "0";
                break;
        }
    }

    private string ResolveExternalWrapperHelperMode(string? forcedHelperMode = null)
    {
        if (!string.IsNullOrWhiteSpace(forcedHelperMode))
        {
            var forcedNormalized = forcedHelperMode.Trim().ToLowerInvariant();
            if (forcedNormalized is HelperModeDirect or HelperModeCompose or HelperModeAuto)
            {
                return forcedNormalized;
            }
        }

        var configured = Environment.GetEnvironmentVariable(ExternalWrapperHelperModeEnv);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return HelperModeDirect;
        }

        var normalized = configured.Trim().ToLowerInvariant();
        if (normalized is HelperModeDirect or HelperModeCompose or HelperModeAuto)
        {
            return normalized;
        }

        _logger.LogWarning(
            "Invalid {EnvironmentVariable} value '{Value}'. Supported values are: {Direct}, {Compose}, {Auto}. Falling back to {Default}.",
            ExternalWrapperHelperModeEnv,
            configured,
            HelperModeDirect,
            HelperModeCompose,
            HelperModeAuto,
            HelperModeDirect);
        return HelperModeDirect;
    }

    private static bool IsWrapperContainerMissingError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("Wrapper container", StringComparison.OrdinalIgnoreCase)
               && message.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveExternalWrapperContainerName()
    {
        var configured = Environment.GetEnvironmentVariable(ExternalWrapperContainerNameEnv);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return DefaultWrapperContainerName;
    }

    private static string? ResolveExternalWrapperComposeFile()
    {
        var configured = Environment.GetEnvironmentVariable(ExternalWrapperComposeFileEnv);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        return configured.Trim();
    }

    private static string ResolveExternalWrapperHost()
    {
        var env = Environment.GetEnvironmentVariable(ExternalWrapperHostEnv);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        return LoopbackHost;
    }

    private void StopProcess(string reason)
    {
        Process? process;
        CancellationTokenSource? cts;
        Task? outputTask;
        lock (_sync)
        {
            process = _process;
            cts = _processCts;
            outputTask = _outputTask;
            _process = null;
            _processCts = null;
            _outputTask = null;
            _healthTask = null;
            _startedAt = null;
            _status = AppleMusicWrapperStatusSnapshot.Missing;
            _awaitingTwoFactor = false;
            _authStateReady = false;
            _loginInProgress = false;
            _startedAt = null;
            _twoFactorSubmittedAt = null;
            _lastTwoFactorProbeAt = null;
            _lastTwoFactorProbeKnown = false;
            _lastTwoFactorProbeResult = false;
            ClearRecentOutputLocked();
        }

        if (process != null)
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Failed to terminate wrapper process.");
                }
            }
        }

        if (cts != null)
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (outputTask != null)
        {
            _ = outputTask.ContinueWith(_ => { }, TaskScheduler.Default);
        }

        if (_healthTask != null)
        {
            _ = _healthTask.ContinueWith(_ => { }, TaskScheduler.Default);
        }

        _logger.LogInformation("Apple Music wrapper stopped ({Reason}).", reason);
    }

    private void ClearRecentOutputLocked()
    {
        _recentOutput.Clear();
    }

    private async Task UpdateAuthStateAsync(bool wrapperReady)
    {
        try
        {
            await _platformAuthService.UpdateAsync(state =>
            {
                state.AppleMusic ??= new AppleMusicAuth();
                if (!string.IsNullOrWhiteSpace(_email))
                {
                    state.AppleMusic.Email = _email;
                }
                state.AppleMusic.WrapperReady = wrapperReady;
                if (wrapperReady)
                {
                    state.AppleMusic.WrapperLoggedInAt = DateTimeOffset.UtcNow;
                    _ = SanitizeWrapperContainerAsync();
                }
                return 0;
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to update Apple Music wrapper auth state.");
        }
    }

    /// <summary>
    /// After successful authentication, sanitizes the wrapper container by:
    /// 1. Rewriting the env file to remove login credentials (-L/-F flags)
    /// 2. Recreating the container so Docker's restart policy uses clean args
    /// Session data persists on the apple_wrapper_session volume.
    /// </summary>
    private async Task SanitizeWrapperContainerAsync()
    {
        try
        {
            var result = await RunExternalWrapperHelperAsync(HelperSanitizeArgs, CancellationToken.None);
            if (result.Success)
            {
                _logger.LogInformation("Sanitized wrapper container — credentials stripped from env, container recreated with clean args.");
            }
            else
            {
                _logger.LogWarning("Wrapper sanitize failed: {Error}", result.Error ?? result.Output);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to sanitize wrapper container after authentication.");
        }
    }

    private void EnsureAuthStateBootstrapped()
    {
        lock (_sync)
        {
            if (_authStateBootstrapped)
            {
                return;
            }

            _authStateBootstrapped = true;
        }

        try
        {
            var state = _platformAuthService.LoadAsync().GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(state.AppleMusic?.Email))
            {
                lock (_sync)
                {
                    if (string.IsNullOrWhiteSpace(_email))
                    {
                        _email = state.AppleMusic.Email;
                    }
                }
            }

            if (state.AppleMusic?.WrapperReady == true)
            {
                lock (_sync)
                {
                    _authStateReady = true;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to bootstrap Apple Music wrapper auth state.");
        }
    }

    public static bool IsExternalModeEnabled()
    {
        var disabled = Environment.GetEnvironmentVariable("DEEZSPOTAG_APPLE_WRAPPER_EXTERNAL_DISABLED");
        return !string.Equals(disabled, "1", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(disabled, "true", StringComparison.OrdinalIgnoreCase);
    }

    private TwoFactorProbeState ProbeExternalWrapperTwoFactorState()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            if (_lastTwoFactorProbeKnown &&
                _lastTwoFactorProbeAt.HasValue &&
                now - _lastTwoFactorProbeAt.Value < TimeSpan.FromSeconds(2))
            {
                return _lastTwoFactorProbeResult ? TwoFactorProbeState.Waiting : TwoFactorProbeState.NotWaiting;
            }
        }

        var state = TwoFactorProbeState.Unknown;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var result = RunExternalWrapperHelperAsync(HelperProbeTwoFactorArgs, cts.Token)
                .GetAwaiter()
                .GetResult();
            var output = (result.Output ?? string.Empty).Trim().ToLowerInvariant();
            if (result.Success)
            {
                if (string.Equals(output, "waiting_for_2fa", StringComparison.Ordinal))
                {
                    state = TwoFactorProbeState.Waiting;
                }
                else if (string.Equals(output, "not_waiting_for_2fa", StringComparison.Ordinal))
                {
                    state = TwoFactorProbeState.NotWaiting;
                }
                else
                {
                    _logger.LogWarning("Unexpected probe-2fa output from wrapper helper: {Output}", output);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to probe external wrapper 2FA state.");
        }

        lock (_sync)
        {
            if (state != TwoFactorProbeState.Unknown)
            {
                _lastTwoFactorProbeAt = now;
                _lastTwoFactorProbeKnown = true;
                _lastTwoFactorProbeResult = state == TwoFactorProbeState.Waiting;
            }
        }

        return state;
    }

    private AppleMusicWrapperStatusSnapshot GetExternalWrapperStatus()
    {
        EnsureAuthStateBootstrapped();

        if (TryGetRecentExternalStartFailure(out var failedStatus))
        {
            return failedStatus;
        }

        var context = CreateExternalStatusContext();
        SyncTwoFactorProbeState(context);
        DiscoverWrapperPorts(context);
        ResolveAuthenticationState(context);
        RecoverTwoFactorAfterRestart(context);
        RefreshLoginSnapshot(context);

        var transientStatus = TryBuildTransientLoginStatus(context);
        if (transientStatus is not null)
        {
            return transientStatus;
        }

        return BuildStableWrapperStatus(context);
    }

    private bool TryGetRecentExternalStartFailure(out AppleMusicWrapperStatusSnapshot failedStatus)
    {
        failedStatus = AppleMusicWrapperStatusSnapshot.Missing;
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_externalStartError) || !_externalStartErrorAt.HasValue)
            {
                return false;
            }

            var elapsed = DateTimeOffset.UtcNow - _externalStartErrorAt.Value;
            if (elapsed >= TimeSpan.FromSeconds(60) || _authStateReady)
            {
                return false;
            }

            failedStatus = new AppleMusicWrapperStatusSnapshot(
                StatusFailed,
                _externalStartError,
                _email,
                false,
                false);
            return true;
        }
    }

    private static ExternalStatusContext CreateExternalStatusContext()
    {
        var hostCandidates = ResolveExternalWrapperHosts().ToList();
        var host = hostCandidates.FirstOrDefault() ?? LoopbackHost;
        return new ExternalStatusContext(hostCandidates, host);
    }

    private void SyncTwoFactorProbeState(ExternalStatusContext context)
    {
        lock (_sync)
        {
            context.LoginActive = _awaitingTwoFactor || _loginInProgress;
            context.ShouldPromptTwoFactor = _awaitingTwoFactor;
        }

        if (!context.LoginActive)
        {
            return;
        }

        context.HelperTwoFactorState = ProbeExternalWrapperTwoFactorState();
        lock (_sync)
        {
            if (context.HelperTwoFactorState == TwoFactorProbeState.Waiting)
            {
                _awaitingTwoFactor = true;
                _lastTwoFactorProbeResult = true;
                _lastTwoFactorProbeKnown = true;
                _lastTwoFactorProbeAt = DateTimeOffset.UtcNow;
                context.ShouldPromptTwoFactor = true;
            }
            else if (context.HelperTwoFactorState == TwoFactorProbeState.NotWaiting &&
                     _awaitingTwoFactor &&
                     _twoFactorSubmittedAt.HasValue)
            {
                _awaitingTwoFactor = false;
                context.ShouldPromptTwoFactor = false;
            }
        }
    }

    private static void DiscoverWrapperPorts(ExternalStatusContext context)
    {
        foreach (var candidate in context.HostCandidates)
        {
            if (!AreWrapperPortsOpen(candidate, TimeSpan.FromMilliseconds(250)))
            {
                continue;
            }

            context.PortsOpen = true;
            context.HostsWithPorts.Add(candidate);
            if (context.HostsWithPorts.Count == 1)
            {
                context.Host = candidate;
            }
        }
    }

    private void ResolveAuthenticationState(ExternalStatusContext context)
    {
        if (!context.PortsOpen)
        {
            return;
        }

        var tokenStateValid = TryResolveAccountInfo(context);
        if (tokenStateValid)
        {
            PromoteAuthenticatedState(context);
            return;
        }

        context.HasAuthentication = false;
        if (context.AccountInfoReachable)
        {
            DowngradeAuthStateIfNeeded();
        }
    }

    private static bool TryResolveAccountInfo(ExternalStatusContext context)
    {
        foreach (var candidate in context.HostsWithPorts)
        {
            var tokenStateValid = TryFetchExternalAccountInfo(
                candidate,
                out var hasDevToken,
                out var hasMusicToken,
                out var accountInfoReachable,
                out var storefrontId);

            context.Host = candidate;
            context.AccountInfoReachable = accountInfoReachable;
            context.HasDevToken = hasDevToken;
            context.HasMusicToken = hasMusicToken;
            context.StorefrontId = storefrontId;

            if (tokenStateValid || accountInfoReachable)
            {
                return tokenStateValid;
            }
        }

        return false;
    }

    private void PromoteAuthenticatedState(ExternalStatusContext context)
    {
        var wasAuthReady = false;
        var loginAttemptActive = false;
        lock (_sync)
        {
            wasAuthReady = _authStateReady;
            loginAttemptActive = _loginInProgress || _awaitingTwoFactor || _startedAt.HasValue;
            if (!_authStateReady || _awaitingTwoFactor || _loginInProgress || _startedAt.HasValue)
            {
                _authStateReady = true;
                _awaitingTwoFactor = false;
                _loginInProgress = false;
                _startedAt = null;
                _twoFactorSubmittedAt = null;
            }

            context.HasAuthentication = _authStateReady;
        }

        if (!wasAuthReady)
        {
            _ = UpdateAuthStateAsync(true);
        }

        if (loginAttemptActive)
        {
            context.PromotedFromLogin = true;
        }
    }

    private void DowngradeAuthStateIfNeeded()
    {
        var shouldPersistLoggedOut = false;
        lock (_sync)
        {
            if (!_authStateReady)
            {
                return;
            }

            _authStateReady = false;
            _awaitingTwoFactor = false;
            _loginInProgress = false;
            _startedAt = null;
            _twoFactorSubmittedAt = null;
            shouldPersistLoggedOut = true;
        }

        if (shouldPersistLoggedOut)
        {
            _ = UpdateAuthStateAsync(false);
        }
    }

    private void RecoverTwoFactorAfterRestart(ExternalStatusContext context)
    {
        if (!context.PortsOpen || context.HasAuthentication || context.ShouldPromptTwoFactor)
        {
            return;
        }

        if (context.HelperTwoFactorState == TwoFactorProbeState.Unknown)
        {
            context.HelperTwoFactorState = ProbeExternalWrapperTwoFactorState();
        }

        if (context.HelperTwoFactorState != TwoFactorProbeState.Waiting)
        {
            return;
        }

        var shouldPersistLoggedOut = false;
        lock (_sync)
        {
            if (_authStateReady)
            {
                _authStateReady = false;
                shouldPersistLoggedOut = true;
            }

            _awaitingTwoFactor = true;
            _loginInProgress = true;
            _startedAt ??= DateTimeOffset.UtcNow;
            context.ShouldPromptTwoFactor = true;
        }

        if (shouldPersistLoggedOut)
        {
            _ = UpdateAuthStateAsync(false);
        }

        context.HasAuthentication = false;
    }

    private void RefreshLoginSnapshot(ExternalStatusContext context)
    {
        lock (_sync)
        {
            context.LoginActive = _awaitingTwoFactor || _loginInProgress;
            context.LoginInProgress = _loginInProgress;
            context.ShouldPromptTwoFactor = context.ShouldPromptTwoFactor || _awaitingTwoFactor;
            context.StartedAt = _startedAt;
        }
    }

    private AppleMusicWrapperStatusSnapshot? TryBuildTransientLoginStatus(ExternalStatusContext context)
    {
        if (!context.LoginActive)
        {
            return null;
        }

        var timeoutStatus = TryBuildLoginTimeoutStatus(context);
        if (timeoutStatus is not null)
        {
            return timeoutStatus;
        }

        if (context.ShouldPromptTwoFactor)
        {
            return BuildWaitingForTwoFactorStatus(context);
        }

        return !context.HasAuthentication ? BuildLoginInProgressStatus(context) : null;
    }

    private AppleMusicWrapperStatusSnapshot? TryBuildLoginTimeoutStatus(ExternalStatusContext context)
    {
        if (!context.StartedAt.HasValue)
        {
            return null;
        }

        var elapsed = DateTimeOffset.UtcNow - context.StartedAt.Value;
        if (!context.PortsOpen && elapsed > TimeSpan.FromSeconds(4))
        {
            var containerState = ProbeExternalWrapperContainerState();
            if (containerState == ExternalWrapperContainerState.Exited)
            {
                return SetAndReturnLoginFailure("Apple Music login failed. Verify your credentials and try again.");
            }
        }

        if (!context.PortsOpen && elapsed > TimeSpan.FromSeconds(20))
        {
            var modeHint = "External wrapper mode is enabled. Start the wrapper container to continue.";
            return SetAndReturnLoginFailure($"Wrapper did not start or stopped unexpectedly. {modeHint}");
        }

        if (!context.HasAuthentication && elapsed > TimeSpan.FromMinutes(2))
        {
            return SetAndReturnLoginFailure("Login timed out. Check your credentials or 2FA code and try again.");
        }

        return null;
    }

    private AppleMusicWrapperStatusSnapshot SetAndReturnLoginFailure(string message)
    {
        lock (_sync)
        {
            _status = new AppleMusicWrapperStatusSnapshot(
                StatusFailed,
                message,
                _email,
                false,
                false);
            _awaitingTwoFactor = false;
            _loginInProgress = false;
            _startedAt = null;
            _twoFactorSubmittedAt = null;
            return _status;
        }
    }

    private AppleMusicWrapperStatusSnapshot BuildWaitingForTwoFactorStatus(ExternalStatusContext context)
    {
        _diagnostics = new AppleMusicWrapperDiagnostics(
            context.Host,
            context.PortsOpen,
            context.AccountInfoReachable,
            context.HasDevToken,
            context.HasMusicToken,
            context.StorefrontId,
            "Waiting for two-factor verification code.");

        return new AppleMusicWrapperStatusSnapshot(
            StatusWaiting,
            "Waiting for two-factor verification code.",
            _email,
            true,
            false);
    }

    private AppleMusicWrapperStatusSnapshot BuildLoginInProgressStatus(ExternalStatusContext context)
    {
        var message = context.PortsOpen
            ? "Login in progress. Waiting for wrapper response."
            : "Starting wrapper login...";

        _diagnostics = new AppleMusicWrapperDiagnostics(
            context.Host,
            context.PortsOpen,
            context.AccountInfoReachable,
            context.HasDevToken,
            context.HasMusicToken,
            context.StorefrontId,
            message);

        return new AppleMusicWrapperStatusSnapshot(
            StatusStarting,
            message,
            _email,
            false,
            false);
    }

    private AppleMusicWrapperStatusSnapshot BuildStableWrapperStatus(ExternalStatusContext context)
    {
        var diagnosticMessage = BuildExternalWrapperDiagnosticMessage(context);
        _diagnostics = new AppleMusicWrapperDiagnostics(
            context.Host,
            context.PortsOpen,
            context.AccountInfoReachable,
            context.HasDevToken,
            context.HasMusicToken,
            context.StorefrontId,
            diagnosticMessage);

        string message;
        string status;
        if (!context.PortsOpen)
        {
            message = $"External wrapper mode is enabled, but nothing is listening on {context.Host}:10020/20020. " +
                      "Start the wrapper container to continue.";
            status = StatusMissing;
        }
        else if (!context.HasAuthentication)
        {
            message = "Wrapper is running. Enter your Apple ID credentials to login.";
            status = StatusRunning;
        }
        else if (context.PromotedFromLogin)
        {
            message = "Apple Music login succeeded.";
            status = StatusSuccess;
        }
        else
        {
            message = "External wrapper is running and authenticated.";
            status = StatusRunning;
        }

        var isFullyReady = context.PortsOpen &&
                           context.HasAuthentication &&
                           !context.LoginInProgress &&
                           !context.ShouldPromptTwoFactor;

        return new AppleMusicWrapperStatusSnapshot(
            status,
            message,
            _email,
            false,
            isFullyReady);
    }

    private static string BuildExternalWrapperDiagnosticMessage(ExternalStatusContext context)
    {
        if (context.HasAuthentication && context.PortsOpen)
        {
            return string.IsNullOrWhiteSpace(context.StorefrontId)
                ? "Wrapper authenticated."
                : $"Wrapper authenticated (storefront: {context.StorefrontId}).";
        }

        if (!context.PortsOpen)
        {
            return $"External wrapper mode is enabled, but the wrapper is not reachable on {context.Host}:10020/20020.";
        }

        if (!context.AccountInfoReachable)
        {
            return $"Wrapper is running, but account endpoint {context.Host}:30020 is not ready yet. Authentication may still be in progress.";
        }

        if (!context.HasDevToken || !context.HasMusicToken)
        {
            return "Wrapper reachable, but auth tokens are missing. Login not completed.";
        }

        return "Wrapper reachable. Login required.";
    }

    private ExternalWrapperContainerState ProbeExternalWrapperContainerState()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var result = RunExternalWrapperHelperAsync(HelperStatusArgs, cts.Token)
                .GetAwaiter()
                .GetResult();
            var text = $"{result.Output}\n{result.Error}".ToLowerInvariant();
            if (text.Contains(" up "))
            {
                return ExternalWrapperContainerState.Running;
            }

            if (text.Contains("exited") || text.Contains(" dead ") || text.Contains("created"))
            {
                return ExternalWrapperContainerState.Exited;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Unable to determine external wrapper container state");
        }

        return ExternalWrapperContainerState.Unknown;
    }

    private static IEnumerable<string> ResolveExternalWrapperHosts()
    {
        var host = ResolveExternalWrapperHost();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(host) && seen.Add(host))
        {
            yield return host;
        }

        if (!IsRunningInContainer() && !IsLoopbackHost(host) && seen.Add(LoopbackHost))
        {
            yield return LoopbackHost;
        }
    }

    private static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return string.Equals(host, LoopbackHost, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRunningInContainer()
    {
        var env = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        if (string.Equals(env, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return File.Exists("/.dockerenv");
    }

    private static bool TryFetchExternalAccountInfo(
        string host,
        out bool hasDevToken,
        out bool hasMusicToken,
        out bool reachable,
        out string? storefrontId)
    {
        hasDevToken = false;
        hasMusicToken = false;
        reachable = false;
        storefrontId = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!TryReadExternalAccountInfo(host, out var accountInfo, out reachable))
                {
                    return false;
                }

                storefrontId = accountInfo.StorefrontId;
                hasDevToken = accountInfo.HasDevToken;
                hasMusicToken = accountInfo.HasMusicToken;
                return hasDevToken && hasMusicToken;
            }
            catch (Exception) when (attempt < 2)
            {
                global::System.Threading.Thread.Sleep(150);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                return false;
            }
        }

        return false;
    }

    private static bool TryReadExternalAccountInfo(string host, out ExternalAccountInfo accountInfo, out bool reachable)
    {
        accountInfo = ExternalAccountInfo.Empty;
        reachable = false;

        var url = new UriBuilder(Uri.UriSchemeHttp, host, 30020).Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var response = ExternalWrapperClient.Send(request);
        reachable = true;
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return TryParseExternalAccountInfo(json, out accountInfo);
    }

    private static bool TryParseExternalAccountInfo(string? json, out ExternalAccountInfo accountInfo)
    {
        accountInfo = ExternalAccountInfo.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var storefrontId = TryReadStringProperty(root, "storefront_id");
        var hasDevToken = TryReadBooleanProperty(root, "has_dev_token")
            ?? HasNonEmptyStringProperty(root, "dev_token");
        var hasMusicToken = TryReadBooleanProperty(root, "has_music_token")
            ?? HasNonEmptyStringProperty(root, "music_user_token")
            || HasNonEmptyStringProperty(root, "music_token");

        accountInfo = new ExternalAccountInfo(hasDevToken, hasMusicToken, storefrontId);
        return true;
    }

    private static bool? TryReadBooleanProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool HasNonEmptyStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString());
    }

    private static string? TryReadStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool AreWrapperPortsOpen(string host, TimeSpan timeout)
    {
        return CheckPort(host, 10020, timeout)
            && CheckPort(host, 20020, timeout);
    }

    private static bool CheckPort(string host, int port, TimeSpan timeout)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var task = client.ConnectAsync(host, port);
            if (!task.Wait(timeout))
            {
                return false;
            }

            return client.Connected;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return false;
        }
    }

    private static string? ResolveRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        for (var i = 0; i < 8; i++)
        {
            var parent = Directory.GetParent(dir);
            if (parent is null)
            {
                break;
            }

            var candidate = parent.FullName;
            if (Directory.Exists(Path.Join(candidate, ToolsDirectory, AppleMusicWrapperDirectory)) ||
                File.Exists(Path.Join(candidate, "src.sln")))
            {
                return candidate;
            }

            dir = parent.FullName;
        }

        return null;
    }

    private sealed class WrapperHealthContext
    {
        public WrapperHealthContext(string host, List<string> hostsWithPorts)
        {
            Host = host;
            HostsWithPorts = hostsWithPorts;
        }

        public string Host { get; set; }
        public List<string> HostsWithPorts { get; }
        public bool PortsOpen => HostsWithPorts.Count > 0;
        public bool AccountInfoReachable { get; set; }
        public bool HasDevToken { get; set; }
        public bool HasMusicToken { get; set; }
        public string? StorefrontId { get; set; }
    }

    private sealed class ExternalStatusContext
    {
        public ExternalStatusContext(List<string> hostCandidates, string host)
        {
            HostCandidates = hostCandidates;
            Host = host;
        }

        public List<string> HostCandidates { get; }
        public string Host { get; set; }
        public bool PortsOpen { get; set; }
        public bool AccountInfoReachable { get; set; }
        public bool HasDevToken { get; set; }
        public bool HasMusicToken { get; set; }
        public string? StorefrontId { get; set; }
        public List<string> HostsWithPorts { get; } = new();
        public bool PromotedFromLogin { get; set; }
        public bool HasAuthentication { get; set; }
        public bool LoginActive { get; set; }
        public bool LoginInProgress { get; set; }
        public bool ShouldPromptTwoFactor { get; set; }
        public TwoFactorProbeState HelperTwoFactorState { get; set; } = TwoFactorProbeState.Unknown;
        public DateTimeOffset? StartedAt { get; set; }
    }

    private sealed record ExternalAccountInfo(bool HasDevToken, bool HasMusicToken, string? StorefrontId)
    {
        public static ExternalAccountInfo Empty { get; } = new(false, false, null);
    }

    private static void EnsureExecutable(string path)
    {
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                var mode = File.GetUnixFileMode(path);
                mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                File.SetUnixFileMode(path, mode);
            }
        }
        catch (IOException)
        {
            // Best effort only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort only.
        }
        catch (PlatformNotSupportedException)
        {
            // Best effort only.
        }
    }

}
