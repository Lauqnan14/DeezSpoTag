using DeezSpoTag.Services.Authentication;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Utils;
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
    public class StartupLoginService : IHostedService
    {
        private readonly ILogger<StartupLoginService> _logger;
        private readonly DeezerClient _deezerClient;
        private readonly ILoginStorageService _loginStorage;
        private readonly DeezSpoTagSettingsService _settingsService;
        private readonly DeezerAuthUtils _authUtils;

        public StartupLoginService(
            IServiceProvider serviceProvider,
            ILogger<StartupLoginService> logger,
            DeezerClient deezerClient,
            ILoginStorageService loginStorage,
            DeezSpoTagSettingsService settingsService,
            DeezerAuthUtils authUtils)
        {
            _ = serviceProvider;
            _logger = logger;
            _deezerClient = deezerClient;
            _loginStorage = loginStorage;
            _settingsService = settingsService;
            _authUtils = authUtils;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in startup login service");
            }
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
                if (!await _authUtils.IsDeezerAvailableAsync(cancellationToken))
                {
                    _logger.LogWarning("Deezer is not available; skipping automatic login");
                    return;
                }

                // EXACT PORT: Attempt to login with saved ARL like deezspotag connect.ts
                DeezSpoTag.Web.Controllers.Api.DeezerStreamApiController.ClearPlaybackContextCache();
                var success = await _deezerClient.LoginViaArlAsync(normalizedArl);
                if (!success || _deezerClient.CurrentUser == null)
                {
                    _logger.LogWarning("Automatic login failed - invalid ARL or user data");
                    await _loginStorage.ResetLoginCredentialsAsync();
                    return;
                }

                await HandleSuccessfulLoginAsync(normalizedArl, _deezerClient.CurrentUser);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during automatic login");
                // EXACT PORT: Clear invalid credentials on error like deezspotag
                await _loginStorage.ResetLoginCredentialsAsync();
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

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
