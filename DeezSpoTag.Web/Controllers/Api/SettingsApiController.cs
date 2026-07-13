using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Globalization;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.RateLimiting;
using DeezSpoTag.Web.Services;

namespace DeezSpoTag.Web.Controllers.Api
{
    /// <summary>
    /// Settings API controller - EXACT PORT from deezspotag getSettings.ts and saveSettings.ts
    /// Ported from: /deezspotag/webui/src/server/routes/api/get/getSettings.ts
    /// Ported from: /deezspotag/webui/src/server/routes/api/post/saveSettings.ts
    /// </summary>
    [Route("api")]
    [ApiController]
    [LocalApiAuthorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
    public class SettingsApiController : ControllerBase
    {
        private readonly ILogger<SettingsApiController> _logger;
        private readonly DeezSpoTagSettingsService _settingsService;
        private readonly UserPreferencesStore _userPreferencesStore;
        private readonly PlatformAuthService _platformAuthService;
        private readonly WatchlistRunCoordinator? _watchlistCoordinator;
        private readonly WatchlistPostDownloadSyncService? _watchlistSyncService;
        public SettingsApiController(
            ILogger<SettingsApiController> logger,
            DeezSpoTagSettingsService settingsService,
            UserPreferencesStore userPreferencesStore,
            PlatformAuthService platformAuthService,
            WatchlistRunCoordinator? watchlistCoordinator = null,
            WatchlistPostDownloadSyncService? watchlistSyncService = null)
        {
            _logger = logger;
            _settingsService = settingsService;
            _userPreferencesStore = userPreferencesStore;
            _platformAuthService = platformAuthService;
            _watchlistCoordinator = watchlistCoordinator;
            _watchlistSyncService = watchlistSyncService;
        }

        /// <summary>
        /// Get settings - EXACT PORT from deezspotag getSettings.ts
        /// GET /api/getSettings
        /// </summary>
        [HttpGet("getSettings")]
        public async Task<IActionResult> GetSettings()
        {
            try
            {
                var settings = _settingsService.LoadSettings();
                var platformAuth = await _platformAuthService.LoadAsync();
                var defaultSettings = DeezSpoTagSettingsService.GetStaticDefaultSettings();
                var redactedSettings = RedactSecrets(settings);
                var redactedDefaults = RedactSecrets(defaultSettings);
                var appleAuth = platformAuth.AppleMusic;
                var hasAppleMediaUserToken = !string.IsNullOrWhiteSpace(appleAuth?.MediaUserToken)
                    || !string.IsNullOrWhiteSpace(settings.AppleMusic?.MediaUserToken);
                var hasAppleAuthorizationToken = !string.IsNullOrWhiteSpace(appleAuth?.AuthorizationToken)
                    || !string.IsNullOrWhiteSpace(settings.AppleMusic?.AuthorizationToken);

                var response = new
                {
                    settings = redactedSettings,
                    defaultSettings = redactedDefaults,
                    secrets = new
                    {
                        hasApiToken = !string.IsNullOrWhiteSpace(settings.ApiToken),
                        hasAuthorizationToken = !string.IsNullOrWhiteSpace(settings.AuthorizationToken),
                        hasAppleMediaUserToken,
                        hasAppleAuthorizationToken
                    }
                };

                return Ok(response);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in getSettings");
                return StatusCode(500, new { error = "Failed to load settings: " + ex.Message });
            }
        }

        /// <summary>
        /// Alternative endpoint for compatibility
        /// GET /api/settings
        /// </summary>
        [HttpGet("settings")]
        public Task<IActionResult> GetSettingsAlternative()
        {
            return GetSettings();
        }

        [HttpGet("download-sources")]
        public IActionResult GetDownloadSources()
        {
            return Ok(new
            {
                settings = DownloadSourceCatalog.GetSettingsSourceOptions(),
                watchlist = DownloadSourceCatalog.GetWatchlistSourceOptions(),
                defaultDownloadEngineOrder = BuildDefaultDownloadEngineOrder()
            });
        }

        private static List<DefaultDownloadEngineOrderOption> BuildDefaultDownloadEngineOrder()
        {
            var qualityCatalog = QualityCatalog.GetEngineQualityOptions();
            var sourceLabels = DownloadSourceCatalog.GetEngineOptions()
                .ToDictionary(option => option.Value, option => option.Label, StringComparer.OrdinalIgnoreCase);
            return DownloadEngineOrderSettings.CreateDefault().Engines.Select(engine =>
            {
                qualityCatalog.TryGetValue(engine.Engine, out var qualityOptions);
                var qualityLabels = (qualityOptions ?? Array.Empty<QualityCatalog.QualityOption>())
                    .ToDictionary(option => option.Value, option => option.Label, StringComparer.OrdinalIgnoreCase);
                return new DefaultDownloadEngineOrderOption(
                    engine.Engine,
                    sourceLabels.TryGetValue(engine.Engine, out var label) ? label : engine.Engine,
                    engine.Enabled,
                    engine.Qualities
                        .Select(quality => new DefaultDownloadQualityOrderOption(
                            quality.Quality,
                            qualityLabels.TryGetValue(quality.Quality, out var qualityLabel) ? qualityLabel : quality.Quality,
                            quality.Enabled))
                        .ToList());
            }).ToList();
        }

        private sealed record DefaultDownloadEngineOrderOption(
            [property: JsonPropertyName("engine")] string Engine,
            [property: JsonPropertyName("label")] string Label,
            [property: JsonPropertyName("enabled")] bool Enabled,
            [property: JsonPropertyName("qualities")] List<DefaultDownloadQualityOrderOption> Qualities);

        private sealed record DefaultDownloadQualityOrderOption(
            [property: JsonPropertyName("quality")] string Quality,
            [property: JsonPropertyName("label")] string Label,
            [property: JsonPropertyName("enabled")] bool Enabled);

        /// <summary>
        /// Save settings - EXACT PORT from deezspotag saveSettings.ts
        /// POST /api/saveSettings
        /// </summary>
        [HttpPost("saveSettings")]
        [EnableRateLimiting("SensitiveWrites")]
        public async Task<IActionResult> SaveSettings([FromBody] JsonElement settingsJson)
        {
            try
            {
                _logger.LogInformation("Received settings save request.");

                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Converters = { new JsonStringEnumConverter() },
                    PropertyNameCaseInsensitive = true
                };

                var persisted = _settingsService.LoadSettings();
                var platformAuth = await _platformAuthService.LoadAsync();
                var mergedJson = MergeSettingsJson(persisted, settingsJson, options);
                var settings = JsonSerializer.Deserialize<DeezSpoTagSettings>(mergedJson, options);

                if (settings == null)
                {
                    _logger.LogWarning("Settings data is null after deserialization");
                    return Ok(new { result = false });
                }

                PreserveSensitiveFieldsIfRedacted(persisted, settings, platformAuth);
                PreserveCriticalFieldsIfBlank(persisted, settings);
                var engineOrderValidation = DownloadSourceOrder.ValidateDownloadEngineOrderSettings(settings.DownloadEngineOrder);
                if (!engineOrderValidation.IsValid)
                {
                    return Ok(new { result = false, error = engineOrderValidation.Error });
                }

                settings.DownloadEngineOrder = DownloadSourceOrder.NormalizeDownloadEngineOrderSettings(settings.DownloadEngineOrder);
                await PersistAppleMusicPlatformTokensAsync(settings, platformAuth);
                _settingsService.SaveSettings(settings);
                await SyncUserPreferencesAsync(settings);
                if (!persisted.WatchEnabled && settings.WatchEnabled)
                {
                    if (_watchlistSyncService != null)
                    {
                        await _watchlistSyncService.ResumePendingJobsAsync(HttpContext.RequestAborted);
                    }

                    if (_watchlistCoordinator != null)
                    {
                        await _watchlistCoordinator.TriggerRunOnceAsync(HttpContext.RequestAborted);
                    }
                }

                _logger.LogInformation("Settings saved successfully.");

                return Ok(new { result = true });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in saveSettings");
                return Ok(new { result = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Alternative endpoint for compatibility
        /// POST /api/settings
        /// </summary>
        [HttpPost("settings")]
        [EnableRateLimiting("SensitiveWrites")]
        public Task<IActionResult> SaveSettingsAlternative([FromBody] JsonElement settingsJson)
        {
            return SaveSettings(settingsJson);
        }

        private async Task SyncUserPreferencesAsync(DeezSpoTagSettings settings)
        {
            var userPrefs = await _userPreferencesStore.LoadAsync();
            userPrefs.TabsPreferenceEnabled = settings.RememberTabsPreference;
            userPrefs.PreviewVolume = settings.PreviewVolume;
            userPrefs.DownloadDestinationStereoFolderId = FormatFolderId(settings.MultiQuality?.PrimaryDestinationFolderId);
            userPrefs.DownloadDestinationAtmosFolderId = FormatFolderId(settings.MultiQuality?.SecondaryDestinationFolderId);
            await _userPreferencesStore.SaveAsync(userPrefs);
        }

        private static string? FormatFolderId(long? folderId)
        {
            return folderId.HasValue
                ? folderId.Value.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        /// <summary>
        /// Reset settings to defaults - additional endpoint (not in original deezspotag but useful)
        /// POST /api/resetSettings
        /// </summary>
        [HttpPost("resetSettings")]
        [EnableRateLimiting("SensitiveWrites")]
        public IActionResult ResetSettings()
        {
            try
            {
                _logger.LogInformation("Resetting settings to defaults");

                var defaultSettings = DeezSpoTagSettingsService.GetStaticDefaultSettings();
                _settingsService.SaveSettings(defaultSettings);

                var response = new
                {
                    result = true,
                    settings = defaultSettings
                };

                _logger.LogInformation("Settings reset successfully");
                return Ok(response);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error resetting settings");
                return Ok(new { result = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Alternative reset endpoint for compatibility
        /// POST /api/settings/reset
        /// </summary>
        [HttpPost("settings/reset")]
        [EnableRateLimiting("SensitiveWrites")]
        public Task<IActionResult> ResetSettingsAlternative()
        {
            return Task.FromResult(ResetSettings());
        }

        [HttpPost("settings/api-token")]
        [EnableRateLimiting("AuthEndpoints")]
        public IActionResult RegenerateApiToken()
        {
            try
            {
                var settings = _settingsService.LoadSettings();
                settings.ApiToken = DeezSpoTagSettingsService.GenerateApiToken();
                _settingsService.SaveSettings(settings);
                return Ok(new { token = settings.ApiToken });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error regenerating API token");
                return StatusCode(500, new { error = "Failed to regenerate API token." });
            }
        }

        private static readonly Regex MaskRegex = new(
            @"[\*\u2022]{2,}",
            RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(250));

        private static void PreserveSensitiveFieldsIfRedacted(
            DeezSpoTagSettings persisted,
            DeezSpoTagSettings incoming,
            PlatformAuthState platformAuth)
        {
            incoming.ApiToken = KeepIncomingOrPersisted(incoming.ApiToken, persisted.ApiToken);
            incoming.AuthorizationToken = KeepIncomingOrPersisted(incoming.AuthorizationToken, persisted.AuthorizationToken);

            incoming.AppleMusic ??= new AppleMusicSettings();
            var persistedAppleMusic = persisted.AppleMusic ?? new AppleMusicSettings();
            var persistedAppleAuth = platformAuth.AppleMusic ?? new AppleMusicAuth();
            incoming.AppleMusic.MediaUserToken = KeepIncomingOrPersisted(
                incoming.AppleMusic.MediaUserToken,
                FirstNonEmpty(persistedAppleAuth.MediaUserToken, persistedAppleMusic.MediaUserToken));
            incoming.AppleMusic.AuthorizationToken = KeepIncomingOrPersisted(
                incoming.AppleMusic.AuthorizationToken,
                FirstNonEmpty(persistedAppleAuth.AuthorizationToken, persistedAppleMusic.AuthorizationToken));
        }

        private async Task PersistAppleMusicPlatformTokensAsync(
            DeezSpoTagSettings incoming,
            PlatformAuthState currentAuth)
        {
            incoming.AppleMusic ??= new AppleMusicSettings();
            var mediaUserToken = incoming.AppleMusic.MediaUserToken?.Trim() ?? string.Empty;
            var authorizationToken = incoming.AppleMusic.AuthorizationToken?.Trim() ?? string.Empty;
            var existingApple = currentAuth.AppleMusic ?? new AppleMusicAuth();
            var hasChanged =
                !string.Equals(existingApple.MediaUserToken ?? string.Empty, mediaUserToken, StringComparison.Ordinal) ||
                !string.Equals(existingApple.AuthorizationToken ?? string.Empty, authorizationToken, StringComparison.Ordinal);
            if (hasChanged)
            {
                await _platformAuthService.UpdateAsync(state =>
                {
                    state.AppleMusic ??= new AppleMusicAuth();
                    state.AppleMusic.MediaUserToken = mediaUserToken;
                    state.AppleMusic.AuthorizationToken = authorizationToken;
                    return 0;
                });
            }

            // Keep config.json clean: Apple auth tokens now live in platform auth storage.
            incoming.AppleMusic.MediaUserToken = string.Empty;
            incoming.AppleMusic.AuthorizationToken = string.Empty;
        }

        private static string KeepIncomingOrPersisted(string? incoming, string? persisted)
        {
            if (string.IsNullOrWhiteSpace(incoming))
            {
                return persisted ?? string.Empty;
            }

            if (MaskRegex.IsMatch(incoming))
            {
                return persisted ?? string.Empty;
            }

            return incoming.Trim();
        }

        private static string FirstNonEmpty(string? primary, string? fallback)
        {
            if (!string.IsNullOrWhiteSpace(primary))
            {
                return primary.Trim();
            }

            return fallback ?? string.Empty;
        }

        private static void PreserveCriticalFieldsIfBlank(DeezSpoTagSettings persisted, DeezSpoTagSettings incoming)
        {
            // Prevent accidental resets to defaults when auxiliary pages post partial/stale
            // settings blobs with empty strings.
            if (string.IsNullOrWhiteSpace(incoming.DownloadLocation))
            {
                incoming.DownloadLocation = persisted.DownloadLocation;
            }

            incoming.Video ??= new VideoSettings();
            var persistedVideo = persisted.Video ?? new VideoSettings();
            if (string.IsNullOrWhiteSpace(incoming.Video.VideoDownloadLocation))
            {
                incoming.Video.VideoDownloadLocation = persistedVideo.VideoDownloadLocation;
            }

            incoming.Podcast ??= new PodcastSettings();
            var persistedPodcast = persisted.Podcast ?? new PodcastSettings();
            if (string.IsNullOrWhiteSpace(incoming.Podcast.DownloadLocation))
            {
                incoming.Podcast.DownloadLocation = persistedPodcast.DownloadLocation;
            }
        }

        private static string MergeSettingsJson(
            DeezSpoTagSettings persisted,
            JsonElement incoming,
            JsonSerializerOptions options)
        {
            var persistedNode = JsonNode.Parse(JsonSerializer.Serialize(persisted, options))?.AsObject()
                ?? new JsonObject();
            var incomingNode = JsonNode.Parse(incoming.GetRawText()) as JsonObject;
            if (incomingNode == null)
            {
                return persistedNode.ToJsonString();
            }

            NormalizeIncomingAliases(incomingNode);
            MergeObjects(persistedNode, incomingNode);
            return persistedNode.ToJsonString();
        }

        private static void NormalizeIncomingAliases(JsonObject incomingNode)
        {
            if (incomingNode["tags"] is JsonObject tags
                && tags["tagSyncedLyrics"] == null
                && tags["syncedLyrics"] != null)
            {
                tags["tagSyncedLyrics"] = tags["syncedLyrics"]?.DeepClone();
                tags.Remove("syncedLyrics");
            }
        }

        private static void MergeObjects(JsonObject target, JsonObject incoming)
        {
            foreach (var pair in incoming)
            {
                if (pair.Value is JsonObject incomingChild)
                {
                    if (target[pair.Key] is not JsonObject targetChild)
                    {
                        target[pair.Key] = incomingChild.DeepClone();
                        continue;
                    }

                    MergeObjects(targetChild, incomingChild);
                    continue;
                }

                target[pair.Key] = pair.Value?.DeepClone();
            }
        }

        private static DeezSpoTagSettings RedactSecrets(DeezSpoTagSettings source)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };

            var clone = JsonSerializer.Deserialize<DeezSpoTagSettings>(
                JsonSerializer.Serialize(source, options),
                options) ?? new DeezSpoTagSettings();

            clone.ApiToken = string.Empty;
            clone.AuthorizationToken = string.Empty;
            clone.AppleMusic ??= new AppleMusicSettings();
            clone.AppleMusic.MediaUserToken = string.Empty;
            clone.AppleMusic.AuthorizationToken = string.Empty;
            return clone;
        }
    }
}
