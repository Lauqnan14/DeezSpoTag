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
                // EXACT PORT: Load saved credentials like deezspotag getLoginCredentials
                var credentials = await _loginStorage.LoadLoginCredentialsAsync();

                var normalizedArl = DeezSpoTag.Services.Utils.DeezerAuthUtils.NormalizeArl(credentials?.Arl);

                if (!string.IsNullOrEmpty(normalizedArl))
                {
                    if (!DeezSpoTag.Services.Utils.DeezerAuthUtils.IsValidArlLength(normalizedArl))
                    {
                        _logger.LogWarning("Saved ARL has invalid length; clearing stored credentials");
                        await _loginStorage.ResetLoginCredentialsAsync();
                        return;
                    }

                    _logger.LogInformation("Found saved ARL, attempting automatic login...");

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

                        if (success && _deezerClient.CurrentUser != null)
                        {
                            _logger.LogInformation("Automatic login successful for user: {UserName} (ID: {UserId})", 
                                _deezerClient.CurrentUser.Name, _deezerClient.CurrentUser.Id);
                            _logger.LogInformation("User capabilities - Lossless: {Lossless}, HQ: {HQ}", 
                                _deezerClient.CurrentUser.CanStreamLossless, _deezerClient.CurrentUser.CanStreamHq);

                            // EXACT PORT: Update stored user data like deezspotag does after successful login
                            await UpdateStoredUserDataAsync(normalizedArl, _deezerClient.CurrentUser);
                            DeezerAccountCapabilityService.UpdateMaxBitrateForUser(_deezerClient.CurrentUser, _settingsService, _logger);
                        }
                        else
                        {
                            _logger.LogWarning("Automatic login failed - invalid ARL or user data");
                            // EXACT PORT: Clear invalid credentials like deezspotag
                            await _loginStorage.ResetLoginCredentialsAsync();
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Error during automatic login");
                        // EXACT PORT: Clear invalid credentials on error like deezspotag
                        await _loginStorage.ResetLoginCredentialsAsync();
                    }
                }
                else
                {
                    _logger.LogInformation("No saved ARL found, skipping automatic login");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in startup login service");
            }
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
                _logger.LogDebug("Updated stored user data for user: {UserName}", currentUser.Name);
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
