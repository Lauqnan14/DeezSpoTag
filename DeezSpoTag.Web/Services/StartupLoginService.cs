using DeezSpoTag.Services.Authentication;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Integrations.Deezer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services
{
    /// <summary>
    /// EXACT PORT: Startup service that handles automatic login on application start
    /// Uses DeezerClient directly for persistent session like deezspotag sessionDZ pattern
    /// </summary>
    public class StartupLoginService : BackgroundService
    {
        private static readonly TimeSpan StartupLoginTimeout = TimeSpan.FromSeconds(15);
        private readonly ILogger<StartupLoginService> _logger;
        private readonly DeezerClient _deezerClient;
        private readonly DeezerLoginCoordinator _loginCoordinator;
        private readonly ILoginStorageService _loginStorage;
        private readonly DeezSpoTagSettingsService _settingsService;
        private readonly IHostApplicationLifetime _applicationLifetime;

        public StartupLoginService(
            ILogger<StartupLoginService> logger,
            DeezerClient deezerClient,
            DeezerLoginCoordinator loginCoordinator,
            ILoginStorageService loginStorage,
            DeezSpoTagSettingsService settingsService,
            IHostApplicationLifetime applicationLifetime)
        {
            _logger = logger;
            _deezerClient = deezerClient;
            _loginCoordinator = loginCoordinator;
            _loginStorage = loginStorage;
            _settingsService = settingsService;
            _applicationLifetime = applicationLifetime;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await WaitForApplicationStartedAsync(stoppingToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(StartupLoginTimeout);
            await RunStartupLoginAsync(timeout.Token);
        }

        private async Task RunStartupLoginAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting automatic login check...");

            try
            {
                var normalizedArl = await TryLoadNormalizedArlAsync();
                if (string.IsNullOrEmpty(normalizedArl))
                {
                    _logger.LogInformation("No saved ARL found, skipping automatic login");
                    return;
                }

                if (!DeezSpoTag.Services.Utils.DeezerAuthUtils.IsValidArlLength(normalizedArl))
                {
                    _logger.LogWarning("Saved ARL has invalid length; clearing stored credentials");
                    await _loginStorage.ResetLoginCredentialsAsync();
                    return;
                }

                _logger.LogInformation("Found saved ARL, attempting automatic login...");
                await AttemptAutomaticLoginAsync(normalizedArl, cancellationToken);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Startup login timed out; continuing without automatic Deezer login.");
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex, "Startup login timed out after {TimeoutSeconds}s; continuing without automatic Deezer login.", StartupLoginTimeout.TotalSeconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in startup login service");
            }
        }

        private async Task WaitForApplicationStartedAsync(CancellationToken cancellationToken)
        {
            if (_applicationLifetime.ApplicationStarted.IsCancellationRequested)
            {
                return;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = cancellationToken.Register(static state =>
            {
                ((TaskCompletionSource)state!).TrySetCanceled();
            }, completion);
            using var startedRegistration = _applicationLifetime.ApplicationStarted.Register(static state =>
            {
                ((TaskCompletionSource)state!).TrySetResult();
            }, completion);

            await completion.Task;
        }

        private async Task<string?> TryLoadNormalizedArlAsync()
        {
            // EXACT PORT: Load saved credentials like deezspotag getLoginCredentials
            var credentials = await _loginStorage.LoadLoginCredentialsAsync();
            return DeezSpoTag.Services.Utils.DeezerAuthUtils.NormalizeArl(credentials?.Arl);
        }

        private async Task AttemptAutomaticLoginAsync(string normalizedArl, CancellationToken cancellationToken)
        {
            try
            {
                // EXACT PORT: Attempt to login with saved ARL like deezspotag connect.ts
                DeezSpoTag.Web.Controllers.Api.DeezerStreamApiController.ClearPlaybackContextCache();
                var loginResult = await _loginCoordinator.LoginViaArlAsync(normalizedArl, cancellationToken: cancellationToken);
                if (!loginResult.Success || _deezerClient.CurrentUser == null)
                {
                    if (string.Equals(loginResult.FailureReason, "invalid_user", StringComparison.Ordinal))
                    {
                        _logger.LogWarning("Automatic login failed because the saved ARL is invalid; clearing stored credentials.");
                        await _loginStorage.ResetLoginCredentialsAsync();
                        await _deezerClient.LogoutAsync();
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Automatic login was unavailable ({FailureReason}); keeping saved Deezer credentials for the next attempt.",
                            loginResult.FailureReason ?? "login_failed");
                    }
                    return;
                }

                await HandleSuccessfulLoginAsync(normalizedArl, _deezerClient.CurrentUser);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Automatic login timed out; keeping saved Deezer credentials for the next attempt.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Automatic login was unavailable; keeping saved Deezer credentials for the next attempt.");
            }
        }

        private async Task HandleSuccessfulLoginAsync(string normalizedArl, DeezSpoTag.Core.Models.Deezer.DeezerUser currentUser)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Automatic login successful for user: {UserName} (ID: {UserId})",
                    currentUser.Name,
                    currentUser.Id);
            }
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "User capabilities - Lossless: {Lossless}, HQ: {HQ}",
                    currentUser.CanStreamLossless,
                    currentUser.CanStreamHq);
            }

            // EXACT PORT: Update stored user data like deezspotag does after successful login
            await UpdateStoredUserDataAsync(normalizedArl, currentUser);
            DeezerAccountCapabilityService.UpdateMaxBitrateForUser(currentUser, _settingsService, _logger);
        }

        /// <summary>
        /// Update stored user data after successful login - EXACT PORT from deezspotag pattern
        /// This ensures the login file always has the latest user info from Deezer
        /// </summary>
        private async Task UpdateStoredUserDataAsync(string arl, DeezSpoTag.Core.Models.Deezer.DeezerUser currentUser)
        {
            try
            {
                var updatedLoginData = new LoginData
                {
                    Arl = arl,
                    AccessToken = null, // ARL login doesn't provide access token
                    User = new UserData
                    {
                        Id = currentUser.Id?.ToString() ?? "0",
                        Name = currentUser.Name ?? "",
                        Picture = currentUser.Picture ?? "",
                        Country = currentUser.Country ?? "",
                        CanStreamLossless = currentUser.CanStreamLossless == true,
                        CanStreamHq = currentUser.CanStreamHq == true
                    }
                };

                await _loginStorage.SaveLoginCredentialsAsync(updatedLoginData);
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Updated stored user data for user: {UserName}", currentUser.Name);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error updating stored user data");
            }
        }
    }
}
