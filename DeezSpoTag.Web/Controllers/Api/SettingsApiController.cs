using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Core.Models.Settings;
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
    public class SettingsApiController : ControllerBase
    {
        private const string NothingCasingValue = "nothing";
        private const string DefaultArtworkFallbackOrder = "apple,deezer,spotify";
        private readonly ILogger<SettingsApiController> _logger;
        private readonly DeezSpoTagSettingsService _settingsService;
        private readonly UserPreferencesStore _userPreferencesStore;
        private readonly TaggingProfileService _taggingProfileService;
        public SettingsApiController(
            ILogger<SettingsApiController> logger,
            DeezSpoTagSettingsService settingsService,
            UserPreferencesStore userPreferencesStore,
            TaggingProfileService taggingProfileService)
        {
            _logger = logger;
            _settingsService = settingsService;
            _userPreferencesStore = userPreferencesStore;
            _taggingProfileService = taggingProfileService;
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
                await OverlayMovedSettingsFromDefaultProfileAsync(settings);
                var defaultSettings = DeezSpoTagSettingsService.GetStaticDefaultSettings();
                var redactedSettings = RedactSecrets(settings);
                var redactedDefaults = RedactSecrets(defaultSettings);

                var response = new
                {
                    settings = redactedSettings,
                    defaultSettings = redactedDefaults,
                    secrets = new
                    {
                        hasArl = !string.IsNullOrWhiteSpace(settings.Arl),
                        hasApiToken = !string.IsNullOrWhiteSpace(settings.ApiToken),
                        hasAuthorizationToken = !string.IsNullOrWhiteSpace(settings.AuthorizationToken),
                        hasAppleMediaUserToken = !string.IsNullOrWhiteSpace(settings.AppleMusic?.MediaUserToken),
                        hasAppleAuthorizationToken = !string.IsNullOrWhiteSpace(settings.AppleMusic?.AuthorizationToken)
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
                var mergedJson = MergeSettingsJson(persisted, settingsJson, options);
                var settings = JsonSerializer.Deserialize<DeezSpoTagSettings>(mergedJson, options);

                if (settings == null)
                {
                    _logger.LogWarning("Settings data is null after deserialization");
                    return Ok(new { result = false });
                }

                PreserveSensitiveFieldsIfRedacted(persisted, settings);
                PreserveCriticalFieldsIfBlank(persisted, settings);
                _settingsService.SaveSettings(settings);
                await SyncMovedSettingsToDefaultProfileAsync(settings);
                await SyncUserPreferencesAsync(settings);

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
            await _userPreferencesStore.SaveAsync(userPrefs);
        }

        private async Task OverlayMovedSettingsFromDefaultProfileAsync(DeezSpoTagSettings settings)
        {
            var profile = await _taggingProfileService.GetDefaultAsync();
            if (profile == null)
            {
                return;
            }

            settings.Tags ??= new TagSettings();
            var technical = profile.Technical ?? new TechnicalTagSettings();
            var folder = profile.FolderStructure ?? new FolderStructureSettings();

            settings.Tags.SavePlaylistAsCompilation = technical.SavePlaylistAsCompilation;
            settings.Tags.UseNullSeparator = technical.UseNullSeparator;
            settings.Tags.SaveID3v1 = technical.SaveID3v1;
            settings.Tags.MultiArtistSeparator = technical.MultiArtistSeparator ?? "default";
            settings.Tags.SingleAlbumArtist = technical.SingleAlbumArtist;
            settings.Tags.CoverDescriptionUTF8 = technical.CoverDescriptionUTF8;

            settings.AlbumVariousArtists = technical.AlbumVariousArtists;
            settings.RemoveDuplicateArtists = technical.RemoveDuplicateArtists;
            settings.RemoveAlbumVersion = technical.RemoveAlbumVersion;
            settings.DateFormat = technical.DateFormat ?? "Y-M-D";
            settings.FeaturedToTitle = technical.FeaturedToTitle ?? "0";
            settings.TitleCasing = technical.TitleCasing ?? NothingCasingValue;
            settings.ArtistCasing = technical.ArtistCasing ?? NothingCasingValue;
            settings.SyncedLyrics = technical.SyncedLyrics;
            settings.SaveLyrics = technical.SaveLyrics;
            settings.LrcType = technical.LrcType ?? "lyrics,syllable-lyrics,unsynced-lyrics";
            settings.LrcFormat = technical.LrcFormat ?? "both";
            settings.LyricsFallbackEnabled = technical.LyricsFallbackEnabled;
            settings.LyricsFallbackOrder = technical.LyricsFallbackOrder ?? "apple,deezer,spotify,lrclib,musixmatch";
            settings.ArtworkFallbackEnabled = technical.ArtworkFallbackEnabled;
            settings.ArtworkFallbackOrder = technical.ArtworkFallbackOrder ?? DefaultArtworkFallbackOrder;
            settings.ArtistArtworkFallbackEnabled = technical.ArtistArtworkFallbackEnabled;
            settings.ArtistArtworkFallbackOrder = technical.ArtistArtworkFallbackOrder ?? DefaultArtworkFallbackOrder;

            settings.CreateArtistFolder = folder.CreateArtistFolder;
            settings.ArtistNameTemplate = folder.ArtistNameTemplate ?? "%artist%";
            settings.CreateAlbumFolder = folder.CreateAlbumFolder;
            settings.AlbumNameTemplate = folder.AlbumNameTemplate ?? "%album%";
            settings.CreateCDFolder = folder.CreateCDFolder;
            settings.CreateStructurePlaylist = folder.CreateStructurePlaylist;
            settings.CreateSingleFolder = folder.CreateSingleFolder;
            settings.CreatePlaylistFolder = folder.CreatePlaylistFolder;
            settings.PlaylistNameTemplate = folder.PlaylistNameTemplate ?? "%playlist%";
            settings.IllegalCharacterReplacer = folder.IllegalCharacterReplacer ?? "_";
        }

        private async Task SyncMovedSettingsToDefaultProfileAsync(DeezSpoTagSettings settings)
        {
            var profile = await _taggingProfileService.GetDefaultAsync();
            if (profile == null)
            {
                return;
            }

            settings.Tags ??= new TagSettings();
            profile.Technical ??= new TechnicalTagSettings();
            profile.FolderStructure ??= new FolderStructureSettings();

            profile.Technical.SavePlaylistAsCompilation = settings.Tags.SavePlaylistAsCompilation;
            profile.Technical.UseNullSeparator = settings.Tags.UseNullSeparator;
            profile.Technical.SaveID3v1 = settings.Tags.SaveID3v1;
            profile.Technical.MultiArtistSeparator = settings.Tags.MultiArtistSeparator ?? "default";
            profile.Technical.SingleAlbumArtist = settings.Tags.SingleAlbumArtist;
            profile.Technical.CoverDescriptionUTF8 = settings.Tags.CoverDescriptionUTF8;
            profile.Technical.AlbumVariousArtists = settings.AlbumVariousArtists;
            profile.Technical.RemoveDuplicateArtists = settings.RemoveDuplicateArtists;
            profile.Technical.RemoveAlbumVersion = settings.RemoveAlbumVersion;
            profile.Technical.DateFormat = settings.DateFormat ?? "Y-M-D";
            profile.Technical.FeaturedToTitle = settings.FeaturedToTitle ?? "0";
            profile.Technical.TitleCasing = settings.TitleCasing ?? NothingCasingValue;
            profile.Technical.ArtistCasing = settings.ArtistCasing ?? NothingCasingValue;
            profile.Technical.SyncedLyrics = settings.SyncedLyrics;
            profile.Technical.SaveLyrics = settings.SaveLyrics;
            profile.Technical.EmbedLyrics = settings.Tags.Lyrics || settings.Tags.SyncedLyrics;
            profile.Technical.LrcType = settings.LrcType ?? "lyrics,syllable-lyrics,unsynced-lyrics";
            profile.Technical.LrcFormat = settings.LrcFormat ?? "both";
            profile.Technical.LyricsFallbackEnabled = settings.LyricsFallbackEnabled;
            profile.Technical.LyricsFallbackOrder = settings.LyricsFallbackOrder ?? "apple,deezer,spotify,lrclib,musixmatch";
            profile.Technical.ArtworkFallbackEnabled = settings.ArtworkFallbackEnabled;
            profile.Technical.ArtworkFallbackOrder = settings.ArtworkFallbackOrder ?? DefaultArtworkFallbackOrder;
            profile.Technical.ArtistArtworkFallbackEnabled = settings.ArtistArtworkFallbackEnabled;
            profile.Technical.ArtistArtworkFallbackOrder = settings.ArtistArtworkFallbackOrder ?? DefaultArtworkFallbackOrder;

            profile.FolderStructure.CreateArtistFolder = settings.CreateArtistFolder;
            profile.FolderStructure.ArtistNameTemplate = settings.ArtistNameTemplate ?? "%artist%";
            profile.FolderStructure.CreateAlbumFolder = settings.CreateAlbumFolder;
            profile.FolderStructure.AlbumNameTemplate = settings.AlbumNameTemplate ?? "%album%";
            profile.FolderStructure.CreateCDFolder = settings.CreateCDFolder;
            profile.FolderStructure.CreateStructurePlaylist = settings.CreateStructurePlaylist;
            profile.FolderStructure.CreateSingleFolder = settings.CreateSingleFolder;
            profile.FolderStructure.CreatePlaylistFolder = settings.CreatePlaylistFolder;
            profile.FolderStructure.PlaylistNameTemplate = settings.PlaylistNameTemplate ?? "%playlist%";
            profile.FolderStructure.IllegalCharacterReplacer = settings.IllegalCharacterReplacer ?? "_";

            await _taggingProfileService.UpsertAsync(profile);
        }

        /// <summary>
        /// Reset settings to defaults - additional endpoint (not in original deezspotag but useful)
        /// POST /api/resetSettings
        /// </summary>
        [HttpPost("resetSettings")]
        [EnableRateLimiting("SensitiveWrites")]
        public async Task<IActionResult> ResetSettings()
        {
            try
            {
                _logger.LogInformation("Resetting settings to defaults");

                var defaultSettings = DeezSpoTagSettingsService.GetStaticDefaultSettings();
                _settingsService.SaveSettings(defaultSettings);
                await SyncMovedSettingsToDefaultProfileAsync(defaultSettings);

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
            return ResetSettings();
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

        private static void PreserveSensitiveFieldsIfRedacted(DeezSpoTagSettings persisted, DeezSpoTagSettings incoming)
        {
            incoming.Arl = KeepIncomingOrPersisted(incoming.Arl, persisted.Arl);
            incoming.ApiToken = KeepIncomingOrPersisted(incoming.ApiToken, persisted.ApiToken);
            incoming.AuthorizationToken = KeepIncomingOrPersisted(incoming.AuthorizationToken, persisted.AuthorizationToken);

            incoming.AppleMusic ??= new AppleMusicSettings();
            var persistedAppleMusic = persisted.AppleMusic ?? new AppleMusicSettings();
            incoming.AppleMusic.MediaUserToken = KeepIncomingOrPersisted(
                incoming.AppleMusic.MediaUserToken,
                persistedAppleMusic.MediaUserToken);
            incoming.AppleMusic.AuthorizationToken = KeepIncomingOrPersisted(
                incoming.AppleMusic.AuthorizationToken,
                persistedAppleMusic.AuthorizationToken);
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

            clone.Arl = string.Empty;
            clone.ApiToken = string.Empty;
            clone.AuthorizationToken = string.Empty;
            clone.AppleMusic ??= new AppleMusicSettings();
            clone.AppleMusic.MediaUserToken = string.Empty;
            clone.AppleMusic.AuthorizationToken = string.Empty;
            return clone;
        }
    }
}
