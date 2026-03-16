
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace DeezSpoTag.Services.Settings;

/// <summary>
/// DeezSpoTagSettingsService - Ported exactly from deezspotag settings.ts
/// </summary>
public class DeezSpoTagSettingsService : ISettingsService
{
    private const string DeezSpoTagFolderName = "deezspotag";
    private const string ConfigFileName = "config.json";
    private const string ContainerDownloadsPath = "/downloads";
    private const string LegacyContainerDownloadsPath = "/data/downloads";
    private const string LegacyAppDataDownloadsPath = "/app/Data/downloads";
    private const string SyllableLyricsType = "syllable-lyrics";
    private const string UnsyncedLyricsType = "unsynced-lyrics";
    private static readonly string[] CanonicalLyricsProviders = { "apple", "deezer", "spotify", "lrclib" };
    private static readonly string[] CanonicalLyricsTypes = { "lyrics", SyllableLyricsType, UnsyncedLyricsType };
    private static readonly string[] CanonicalLyricsFormats = { "both", "lrc", "ttml" };
    private static readonly JsonSerializerOptions SettingsDeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly JsonSerializerOptions SettingsSerializeOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<DeezSpoTagSettingsService> _logger;
    private readonly string _settingsFilePath;
    private readonly string _configFolder;
    private readonly object _settingsSync = new();
    private DateTime? _lastLoggedWriteUtc;
    private string? _lastFixSignature;
    private readonly HashSet<string> _loggedFixFields = new(StringComparer.OrdinalIgnoreCase);

    public DeezSpoTagSettingsService(IConfiguration configuration, ILogger<DeezSpoTagSettingsService> logger)
    {
        _logger = logger;
        var configRoot = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        }
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = configuration["DataDirectory"] ?? "Data";
        }
        var configFolder = ResolveConfigFolder(configRoot);
        if (File.Exists(configFolder))
        {
            throw new InvalidOperationException($"Settings directory path '{configFolder}' is a file.");
        }

        _configFolder = configFolder;
        _settingsFilePath = Path.Join(_configFolder, ConfigFileName);

        if (!Directory.Exists(_configFolder))
        {
            Directory.CreateDirectory(_configFolder);
        }

        var dataRoot = Directory.GetParent(_configFolder)?.FullName ?? _configFolder;
        ConsolidateDuplicateConfigFiles(dataRoot, _settingsFilePath);
    }

    private static string ResolveConfigFolder(string configRoot)
    {
        var normalized = (configRoot ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Path.Join("Data", "deezspotag");
        }

        normalized = Path.GetFullPath(normalized);
        while (true)
        {
            var currentLeaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(normalized));
            var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(normalized))?.FullName;
            if (!currentLeaf.Equals(DeezSpoTagFolderName, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(parent))
            {
                break;
            }

            var parentLeaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(parent));
            if (!parentLeaf.Equals(DeezSpoTagFolderName, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            normalized = parent;
        }

        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(normalized));
        if (leaf.Equals(DeezSpoTagFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return Path.Join(normalized, DeezSpoTagFolderName);
    }

    private void ConsolidateDuplicateConfigFiles(string dataRoot, string canonicalSettingsPath)
    {
        try
        {
            var canonicalPath = Path.GetFullPath(canonicalSettingsPath);
            var canonicalFolder = Path.GetDirectoryName(canonicalPath) ?? _configFolder;
            Directory.CreateDirectory(canonicalFolder);

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFullPath(Path.Join(dataRoot, ConfigFileName)),
                Path.GetFullPath(Path.Join(dataRoot, DeezSpoTagFolderName, ConfigFileName)),
                Path.GetFullPath(Path.Join(dataRoot, DeezSpoTagFolderName, DeezSpoTagFolderName, ConfigFileName)),
                Path.GetFullPath(Path.Join(canonicalFolder, DeezSpoTagFolderName, ConfigFileName)),
                Path.GetFullPath(Path.Join(canonicalFolder, DeezSpoTagFolderName, DeezSpoTagFolderName, ConfigFileName))
            };

            candidates.Remove(canonicalPath);

            var existingCandidates = candidates
                .Where(File.Exists)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .ToList();

            if (!File.Exists(canonicalPath) && existingCandidates.Count > 0)
            {
                var source = existingCandidates[0];
                File.Copy(source, canonicalPath, overwrite: false);
                _logger.LogWarning(
                    "Recovered settings from duplicate config path {SourcePath} -> {CanonicalPath}",
                    source,
                    canonicalPath);
            }

            foreach (var duplicatePath in existingCandidates)
            {
                if (string.Equals(duplicatePath, canonicalPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Delete(duplicatePath);
                _logger.LogWarning("Deleted duplicate config path: {DuplicatePath}", duplicatePath);
                DeleteEmptyDuplicateParents(duplicatePath, canonicalFolder, dataRoot);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to consolidate duplicate config files under {DataRoot}", dataRoot);
        }
    }

    private static void DeleteEmptyDuplicateParents(string duplicatePath, string canonicalFolder, string dataRoot)
    {
        var current = Path.GetDirectoryName(duplicatePath);
        var canonicalNormalized = Path.GetFullPath(canonicalFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootNormalized = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        while (!string.IsNullOrWhiteSpace(current))
        {
            var fullCurrent = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullCurrent, canonicalNormalized, StringComparison.OrdinalIgnoreCase)
                || !fullCurrent.StartsWith(rootNormalized, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var leaf = Path.GetFileName(fullCurrent);
            if (!string.Equals(leaf, DeezSpoTagFolderName, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!Directory.Exists(fullCurrent) || Directory.EnumerateFileSystemEntries(fullCurrent).Any())
            {
                break;
            }

            Directory.Delete(fullCurrent);
            current = Path.GetDirectoryName(fullCurrent);
        }
    }

    /// <summary>
    /// Load settings exactly like deezspotag loadSettings function
    /// </summary>
    public DeezSpoTagSettings LoadSettings()
    {
        lock (_settingsSync)
        {
            return LoadSettingsLocked();
        }
    }

    private DeezSpoTagSettings LoadSettingsLocked()
    {
        var logLoad = ShouldLogSettingsLoad();
        if (logLoad)
        {
            _logger.LogInformation("Loading settings from: {FilePath}", _settingsFilePath);
        }

        EnsureConfigDirectoryExists();

        if (!System.IO.File.Exists(_settingsFilePath))
        {
            return CreateDefaultSettings(logLoad);
        }

        var settings = ReadSettingsFromFile();
        PersistFixedSettings(settings);

        if (logLoad)
        {
            _logger.LogInformation("Settings loaded successfully");
        }

        return settings;
    }

    private void EnsureConfigDirectoryExists()
    {
        if (!Directory.Exists(_configFolder))
        {
            Directory.CreateDirectory(_configFolder);
        }
    }

    private DeezSpoTagSettings CreateDefaultSettings(bool logLoad)
    {
        if (logLoad)
        {
            _logger.LogInformation("Settings file not found, creating with defaults");
        }

        var defaultSettings = GetStaticDefaultSettings();
        CheckAndFixSettings(defaultSettings, out _);
        SaveSettingsLocked(defaultSettings);
        return defaultSettings;
    }

    private DeezSpoTagSettings ReadSettingsFromFile()
    {
        try
        {
            var json = File.ReadAllText(_settingsFilePath);
            return JsonSerializer.Deserialize<DeezSpoTagSettings>(json, SettingsDeserializeOptions) ?? GetStaticDefaultSettings();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error parsing settings file, resetting to defaults");
            var defaultSettings = GetStaticDefaultSettings();
            SaveSettingsLocked(defaultSettings);
            return defaultSettings;
        }
    }

    private void PersistFixedSettings(DeezSpoTagSettings settings)
    {
        var changes = CheckAndFixSettings(settings, out var fixedFields);
        if (changes <= 0)
        {
            return;
        }

        var signature = string.Join("|", fixedFields.OrderBy(field => field, StringComparer.OrdinalIgnoreCase));
        if (string.Equals(_lastFixSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        LogFixedSettings(changes, fixedFields);
        SaveSettingsLocked(settings);
        _lastFixSignature = signature;
    }

    private void LogFixedSettings(int changes, List<string> fixedFields)
    {
        var newFields = fixedFields.Where(field => !_loggedFixFields.Contains(field)).ToList();
        if (newFields.Count > 0)
        {
            _logger.LogInformation(
                "Fixed {Changes} settings: {Fields}",
                changes,
                string.Join(", ", newFields));
            foreach (var field in newFields)
            {
                _loggedFixFields.Add(field);
            }

            return;
        }

        _logger.LogInformation("Fixed {Changes} settings", changes);
    }

    /// <summary>
    /// Save settings exactly like deezspotag saveSettings function
    /// </summary>
    public void SaveSettings(DeezSpoTagSettings settings)
    {
        lock (_settingsSync)
        {
            CheckAndFixSettings(settings, out _);
            SaveSettingsLocked(settings);
        }
    }

    private void SaveSettingsLocked(DeezSpoTagSettings settings)
    {
        _logger.LogInformation("Saving settings to: {FilePath}", _settingsFilePath);

        if (!Directory.Exists(_configFolder))
        {
            Directory.CreateDirectory(_configFolder);
        }

        var json = JsonSerializer.Serialize(settings, SettingsSerializeOptions);
        var tmpPath = _settingsFilePath + ".tmp";
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, _settingsFilePath, overwrite: true);
        _lastLoggedWriteUtc = File.GetLastWriteTimeUtc(_settingsFilePath);
        
        _logger.LogInformation("Settings saved successfully");
    }

    private bool ShouldLogSettingsLoad()
    {
        if (!File.Exists(_settingsFilePath))
        {
            if (_lastLoggedWriteUtc != null)
            {
                return false;
            }
            return true;
        }

        var writeUtc = File.GetLastWriteTimeUtc(_settingsFilePath);
        if (_lastLoggedWriteUtc == writeUtc)
        {
            return false;
        }

        _lastLoggedWriteUtc = writeUtc;
        return true;
    }

    /// <summary>
    /// Check and fix settings exactly like deezspotag check function
    /// </summary>
    private static int CheckAndFixSettings(DeezSpoTagSettings settings, out List<string> fixedFields)
    {
        var defaultSettings = GetStaticDefaultSettings();
        fixedFields = new List<string>();
        var fixes = new SettingsFixTracker(fixedFields);

        EnsureDownloadLocation(settings, defaultSettings, fixes);
        EnsureTemplates(settings, defaultSettings, fixes);
        EnsureTagsAndMetadata(settings, defaultSettings, fixes);
        EnsureNestedSettings(settings, fixes);
        NormalizeSpotifySettings(settings, defaultSettings, fixes);
        NormalizeDownloadExecutionSettings(settings, defaultSettings, fixes);
        NormalizeWatchSettings(settings, defaultSettings, fixes);
        NormalizeRegionalAndLyricsSettings(settings, defaultSettings, fixes);
        EnsureApiToken(settings, fixes);
        NormalizeGenreTagAliasRules(settings, defaultSettings, fixes);
        NormalizeShazamSettings(settings, defaultSettings, fixes);

        return fixes.Changes;
    }

    private static void EnsureDownloadLocation(
        DeezSpoTagSettings settings,
        DeezSpoTagSettings defaultSettings,
        SettingsFixTracker fixes)
    {
        if (string.IsNullOrEmpty(settings.DownloadLocation))
        {
            settings.DownloadLocation = defaultSettings.DownloadLocation;
            fixes.Mark(nameof(settings.DownloadLocation));
            return;
        }

        var normalizedDownloadLocation = NormalizeDownloadLocation(settings.DownloadLocation, defaultSettings.DownloadLocation);
        if (!string.Equals(normalizedDownloadLocation, settings.DownloadLocation, StringComparison.Ordinal))
        {
            settings.DownloadLocation = normalizedDownloadLocation;
            fixes.Mark(nameof(settings.DownloadLocation));
        }
    }

    private static void EnsureTemplates(
        DeezSpoTagSettings settings,
        DeezSpoTagSettings defaultSettings,
        SettingsFixTracker fixes)
    {
        var templates = new[]
        {
            nameof(settings.TracknameTemplate),
            nameof(settings.AlbumTracknameTemplate),
            nameof(settings.PlaylistTracknameTemplate),
            nameof(settings.PlaylistNameTemplate),
            nameof(settings.ArtistNameTemplate),
            nameof(settings.AlbumNameTemplate),
            nameof(settings.PlaylistFilenameTemplate),
            nameof(settings.CoverImageTemplate),
            nameof(settings.ArtistImageTemplate)
        };

        foreach (var templateName in templates)
        {
            var property = typeof(DeezSpoTagSettings).GetProperty(templateName);
            if (property == null)
            {
                continue;
            }

            var currentValue = property.GetValue(settings) as string;
            if (!string.IsNullOrEmpty(currentValue))
            {
                continue;
            }

            var defaultValue = property.GetValue(defaultSettings) as string;
            property.SetValue(settings, defaultValue);
            fixes.Mark(templateName);
        }
    }

    private static void EnsureTagsAndMetadata(
        DeezSpoTagSettings settings,
        DeezSpoTagSettings defaultSettings,
        SettingsFixTracker fixes)
    {
        if (settings.Tags == null)
        {
            settings.Tags = new TagSettings();
            fixes.Mark(nameof(settings.Tags));
        }

        ApplyFixIf(
            string.IsNullOrWhiteSpace(settings.Service),
            () => settings.Service = defaultSettings.Service,
            fixes,
            nameof(settings.Service));

        ApplyFixIf(
            string.Equals(settings.Service, "auto", StringComparison.OrdinalIgnoreCase) && !settings.FallbackBitrate,
            () => settings.FallbackBitrate = true,
            fixes,
            nameof(settings.FallbackBitrate));

        ApplyFixIf(
            string.IsNullOrWhiteSpace(settings.MetadataSource) || !IsSupportedMetadataSource(settings.MetadataSource),
            () => settings.MetadataSource = defaultSettings.MetadataSource,
            fixes,
            nameof(settings.MetadataSource));
    }

    private static void EnsureNestedSettings(DeezSpoTagSettings settings, SettingsFixTracker fixes)
    {
        ApplyFixIf(
            settings.AppleMusic == null,
            () => settings.AppleMusic = new DeezSpoTag.Core.Models.Settings.AppleMusicSettings(),
            fixes,
            nameof(settings.AppleMusic));

        ApplyFixIf(
            settings.Video == null,
            () => settings.Video = new DeezSpoTag.Core.Models.Settings.VideoSettings(),
            fixes,
            nameof(settings.Video));

        ApplyFixIf(
            settings.Podcast == null,
            () => settings.Podcast = new DeezSpoTag.Core.Models.Settings.PodcastSettings(),
            fixes,
            nameof(settings.Podcast));

        ApplyFixIf(
            settings.MultiQuality == null,
            () => settings.MultiQuality = new DeezSpoTag.Core.Models.Settings.MultiQualityDownloadSettings(),
            fixes,
            nameof(settings.MultiQuality));
    }

    private static void NormalizeSpotifySettings(
        DeezSpoTagSettings settings,
        DeezSpoTagSettings defaultSettings,
        SettingsFixTracker fixes)
    {
        ApplyFixIf(
            settings.LibrespotConcurrency <= 0,
            () => settings.LibrespotConcurrency = defaultSettings.LibrespotConcurrency,
            fixes,
            nameof(settings.LibrespotConcurrency));

        ApplyFixIf(
            settings.SpotifyResolveConcurrency <= 0,
            () => settings.SpotifyResolveConcurrency = defaultSettings.SpotifyResolveConcurrency,
            fixes,
            nameof(settings.SpotifyResolveConcurrency));

        ApplyFixIf(
            settings.SpotifyMatchConcurrency <= 0,
            () => settings.SpotifyMatchConcurrency = defaultSettings.SpotifyMatchConcurrency,
            fixes,
            nameof(settings.SpotifyMatchConcurrency));

        ApplyFixIf(
            settings.SpotifyIsrcHydrationConcurrency <= 0,
            () => settings.SpotifyIsrcHydrationConcurrency = defaultSettings.SpotifyIsrcHydrationConcurrency,
            fixes,
            nameof(settings.SpotifyIsrcHydrationConcurrency));

        if (settings.SpotifyArtistMetadataFetchBatchSize < 1)
        {
            settings.SpotifyArtistMetadataFetchBatchSize = defaultSettings.SpotifyArtistMetadataFetchBatchSize;
            fixes.Mark(nameof(settings.SpotifyArtistMetadataFetchBatchSize));
        }
        else if (settings.SpotifyArtistMetadataFetchBatchSize > 500)
        {
            settings.SpotifyArtistMetadataFetchBatchSize = 500;
            fixes.Mark(nameof(settings.SpotifyArtistMetadataFetchBatchSize));
        }

        if (string.Equals(settings.SpotifyPlaylistTrackSource, "spotiflac", StringComparison.OrdinalIgnoreCase))
        {
            settings.SpotifyPlaylistTrackSource = "pathfinder";
            fixes.Mark(nameof(settings.SpotifyPlaylistTrackSource));
            return;
        }

        if (!string.Equals(settings.SpotifyPlaylistTrackSource, "pathfinder", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(settings.SpotifyPlaylistTrackSource, "librespot", StringComparison.OrdinalIgnoreCase))
        {
            settings.SpotifyPlaylistTrackSource = defaultSettings.SpotifyPlaylistTrackSource;
            fixes.Mark(nameof(settings.SpotifyPlaylistTrackSource));
        }
    }

    private static void NormalizeDownloadExecutionSettings(
        DeezSpoTagSettings settings,
        DeezSpoTagSettings defaultSettings,
        SettingsFixTracker fixes)
    {
        ApplyFixIf(
            settings.MaxConcurrentDownloads <= 0,
            () => settings.MaxConcurrentDownloads = defaultSettings.MaxConcurrentDownloads,
            fixes,
            nameof(settings.MaxConcurrentDownloads));

        ApplyFixIf(
            settings.RealTimeMultiplier < 1 || settings.RealTimeMultiplier > 10,
            () => settings.RealTimeMultiplier = defaultSettings.RealTimeMultiplier,
            fixes,
            nameof(settings.RealTimeMultiplier));

        ApplyFixIf(
            string.IsNullOrWhiteSpace(settings.Bitrate),
            () => settings.Bitrate = defaultSettings.Bitrate,
            fixes,
            nameof(settings.Bitrate));

        ApplyFixIf(
            settings.MaxRetries < 0,
            () => settings.MaxRetries = defaultSettings.MaxRetries,
            fixes,
            nameof(settings.MaxRetries));

        ApplyFixIf(
            settings.RetryDelaySeconds < 1,
            () => settings.RetryDelaySeconds = defaultSettings.RetryDelaySeconds,
            fixes,
            nameof(settings.RetryDelaySeconds));

        ApplyFixIf(
            settings.RetryDelayIncrease < 0,
            () => settings.RetryDelayIncrease = defaultSettings.RetryDelayIncrease,
            fixes,
            nameof(settings.RetryDelayIncrease));
    }

    private static void NormalizeWatchSettings(
        DeezSpoTagSettings settings,
        DeezSpoTagSettings defaultSettings,
        SettingsFixTracker fixes)
    {
        ApplyFixIf(
            settings.WatchPollIntervalSeconds < 1,
            () => settings.WatchPollIntervalSeconds = defaultSettings.WatchPollIntervalSeconds,
            fixes,
            nameof(settings.WatchPollIntervalSeconds));

        ApplyFixIf(
            settings.WatchMaxItemsPerRun < 1 || settings.WatchMaxItemsPerRun > 50,
            () => settings.WatchMaxItemsPerRun = defaultSettings.WatchMaxItemsPerRun,
            fixes,
            nameof(settings.WatchMaxItemsPerRun));

        ApplyFixIf(
            settings.WatchDelayBetweenPlaylistsSeconds < 1,
            () => settings.WatchDelayBetweenPlaylistsSeconds = defaultSettings.WatchDelayBetweenPlaylistsSeconds,
            fixes,
            nameof(settings.WatchDelayBetweenPlaylistsSeconds));

        ApplyFixIf(
            settings.WatchDelayBetweenArtistsSeconds < 1,
            () => settings.WatchDelayBetweenArtistsSeconds = defaultSettings.WatchDelayBetweenArtistsSeconds,
            fixes,
            nameof(settings.WatchDelayBetweenArtistsSeconds));

        ApplyFixIf(
            settings.WatchedArtistAlbumGroup == null || settings.WatchedArtistAlbumGroup.Count == 0,
            () => settings.WatchedArtistAlbumGroup = new List<string>(defaultSettings.WatchedArtistAlbumGroup),
            fixes,
            nameof(settings.WatchedArtistAlbumGroup));
    }

    private static void NormalizeRegionalAndLyricsSettings(
        DeezSpoTagSettings settings,
        DeezSpoTagSettings defaultSettings,
        SettingsFixTracker fixes)
    {
        ApplyFixIf(
            string.IsNullOrWhiteSpace(settings.DeezerLanguage),
            () => settings.DeezerLanguage = defaultSettings.DeezerLanguage,
            fixes,
            nameof(settings.DeezerLanguage));

        ApplyFixIf(
            string.IsNullOrWhiteSpace(settings.DeezerCountry),
            () => settings.DeezerCountry = defaultSettings.DeezerCountry,
            fixes,
            nameof(settings.DeezerCountry));

        NormalizeLyricsSetting(
            settings.LrcType,
            NormalizeLyricsTypeSelection,
            normalized => settings.LrcType = normalized,
            fixes,
            nameof(settings.LrcType));

        NormalizeLyricsSetting(
            settings.LrcFormat,
            NormalizeLyricsFormatSelection,
            normalized => settings.LrcFormat = normalized,
            fixes,
            nameof(settings.LrcFormat));

        NormalizeLyricsSetting(
            settings.LyricsFallbackOrder,
            NormalizeLyricsFallbackOrder,
            normalized => settings.LyricsFallbackOrder = normalized,
            fixes,
            nameof(settings.LyricsFallbackOrder));
    }

    private static void EnsureApiToken(DeezSpoTagSettings settings, SettingsFixTracker fixes)
    {
        ApplyFixIf(
            string.IsNullOrWhiteSpace(settings.ApiToken),
            () => settings.ApiToken = GenerateApiToken(),
            fixes,
            nameof(settings.ApiToken));
    }

    private static void NormalizeGenreTagAliasRules(
        DeezSpoTagSettings settings,
        DeezSpoTagSettings defaultSettings,
        SettingsFixTracker fixes)
    {
        if (settings.GenreTagAliasRules == null)
        {
            settings.GenreTagAliasRules = CloneGenreTagAliasRules(defaultSettings.GenreTagAliasRules);
            fixes.Mark(nameof(settings.GenreTagAliasRules));
            return;
        }

        var normalizedAliasRules = GenreTagAliasNormalizer.NormalizeRules(settings.GenreTagAliasRules);
        var normalizedDefaultAliasRules = GenreTagAliasNormalizer.NormalizeRules(defaultSettings.GenreTagAliasRules);
        var aliasKeys = new HashSet<string>(
            normalizedAliasRules.Select(rule => GenreTagAliasNormalizer.ToLookupKey(rule.Alias)),
            StringComparer.Ordinal);

        foreach (var defaultRule in normalizedDefaultAliasRules)
        {
            var aliasKey = GenreTagAliasNormalizer.ToLookupKey(defaultRule.Alias);
            if (!string.IsNullOrWhiteSpace(aliasKey) && aliasKeys.Add(aliasKey))
            {
                normalizedAliasRules.Add(new GenreTagAliasRule
                {
                    Alias = defaultRule.Alias,
                    Canonical = defaultRule.Canonical
                });
            }
        }

        if (!AreGenreTagAliasRulesEqual(settings.GenreTagAliasRules, normalizedAliasRules))
        {
            settings.GenreTagAliasRules = normalizedAliasRules;
            fixes.Mark(nameof(settings.GenreTagAliasRules));
        }
    }

    private static void NormalizeShazamSettings(
        DeezSpoTagSettings settings,
        DeezSpoTagSettings defaultSettings,
        SettingsFixTracker fixes)
    {
        ApplyFixIf(
            settings.ShazamCaptureDurationSeconds < 3 || settings.ShazamCaptureDurationSeconds > 20,
            () => settings.ShazamCaptureDurationSeconds = defaultSettings.ShazamCaptureDurationSeconds,
            fixes,
            nameof(settings.ShazamCaptureDurationSeconds));
    }

    private static void NormalizeLyricsSetting(
        string? currentValue,
        Func<string?, string> normalize,
        Action<string> apply,
        SettingsFixTracker fixes,
        string fieldName)
    {
        var normalized = normalize(currentValue);
        if (!string.Equals(currentValue, normalized, StringComparison.Ordinal))
        {
            apply(normalized);
            fixes.Mark(fieldName);
        }
    }

    private static List<GenreTagAliasRule> CloneGenreTagAliasRules(IEnumerable<GenreTagAliasRule> source)
    {
        return source
            .Select(rule => new GenreTagAliasRule
            {
                Alias = rule.Alias,
                Canonical = rule.Canonical
            })
            .ToList();
    }

    private static void ApplyFixIf(bool condition, Action applyFix, SettingsFixTracker fixes, string fieldName)
    {
        if (!condition)
        {
            return;
        }

        applyFix();
        fixes.Mark(fieldName);
    }

    private sealed class SettingsFixTracker
    {
        private readonly List<string> _fixedFields;

        public SettingsFixTracker(List<string> fixedFields)
        {
            _fixedFields = fixedFields;
        }

        public int Changes { get; private set; }

        public void Mark(string fieldName)
        {
            Changes++;
            _fixedFields.Add(fieldName);
        }
    }

    private static bool AreGenreTagAliasRulesEqual(
        IReadOnlyList<GenreTagAliasRule> current,
        IReadOnlyList<GenreTagAliasRule> normalized)
    {
        if (ReferenceEquals(current, normalized))
        {
            return true;
        }

        if (current.Count != normalized.Count)
        {
            return false;
        }

        for (var i = 0; i < current.Count; i++)
        {
            var currentRule = current[i];
            var normalizedRule = normalized[i];
            if (currentRule == null && normalizedRule == null)
            {
                continue;
            }

            if (currentRule == null || normalizedRule == null)
            {
                return false;
            }

            if (!string.Equals(currentRule.Alias?.Trim(), normalizedRule.Alias, StringComparison.Ordinal)
                || !string.Equals(currentRule.Canonical?.Trim(), normalizedRule.Canonical, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeDownloadLocation(string downloadLocation, string defaultDownloadLocation)
    {
        if (string.IsNullOrWhiteSpace(downloadLocation))
        {
            return defaultDownloadLocation;
        }

        var normalized = RewriteLegacyHomeUserPath(downloadLocation.Trim());
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return defaultDownloadLocation;
        }

        if (IsLegacyContainerDownloadLocation(normalized))
        {
            normalized = IsRunningInContainer()
                ? ContainerDownloadsPath
                : defaultDownloadLocation;
        }

        if (IsRunningInContainer())
        {
            return NormalizeContainerDownloadLocation(normalized);
        }

        if (DownloadPathResolver.IsSmbPath(normalized))
        {
            return normalized;
        }

        try
        {
            var ioPath = DownloadPathResolver.ResolveIoPath(normalized);
            if (string.IsNullOrWhiteSpace(ioPath))
            {
                return normalized;
            }

            Directory.CreateDirectory(ioPath);
            return normalized;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return normalized;
        }
    }

    private static string NormalizeContainerDownloadLocation(string requestedPath)
    {
        var normalizedPath = string.IsNullOrWhiteSpace(requestedPath)
            ? ContainerDownloadsPath
            : requestedPath.Trim();

        // One-way migration for legacy container path accidentally coupled to app data.
        if (IsLegacyContainerDownloadLocation(normalizedPath))
        {
            normalizedPath = ContainerDownloadsPath;
        }

        var fullPath = NormalizeContainerPath(normalizedPath);
        if (IsPathUnderRoot(fullPath, "/data") || IsPathUnderRoot(fullPath, "/app/Data"))
        {
            throw new InvalidOperationException(
                $"Container download path '{fullPath}' is invalid. Downloads cannot live under app data paths (/data or /app/Data). " +
                "Use /downloads or another dedicated mounted path.");
        }

        return ResolveContainerDownloadLocation(fullPath);
    }

    private static string ResolveContainerDownloadLocation(string path)
    {
        var normalizedPath = NormalizeContainerPath(path);
        if (!IsPathBackedByMountedVolume(normalizedPath))
        {
            throw new InvalidOperationException(
                $"Container download path '{normalizedPath}' is not on a mounted volume. " +
                "Mount it in Docker and then select that mounted path in Settings.");
        }

        try
        {
            Directory.CreateDirectory(normalizedPath);
            if (IsDirectoryWritable(normalizedPath))
            {
                return normalizedPath;
            }
        }
        catch
        {
            // Create/write probes can fail on read-only or permission-restricted mounts.
            // Surface the normalized "not writable" error below instead of leaking OS-specific exceptions.
        }

        throw new InvalidOperationException(
            $"Container download path '{normalizedPath}' is not writable. Mount a writable downloads volume and set Settings > Download location to that mounted path.");
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

    private static bool IsDirectoryWritable(string path)
    {
        try
        {
            var probePath = Path.Combine(path, $".write-test-{Guid.NewGuid():N}");
            using var probeStream = File.Create(probePath);

            File.Delete(probePath);
            return true;
        }
        catch
        {
            // Writable checks are best-effort; callers interpret false as "path cannot be used".
            return false;
        }
    }

    private static string NormalizeContainerPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ContainerDownloadsPath;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private static bool IsLegacyContainerDownloadLocation(string path)
    {
        var normalized = path
            .Trim()
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(normalized, LegacyContainerDownloadsPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, LegacyAppDataDownloadsPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathBackedByMountedVolume(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        var fullPath = NormalizeContainerPath(path);
        var hasNonRootMount = false;
        foreach (var mountPoint in EnumerateLinuxMountPoints())
        {
            if (string.Equals(mountPoint, "/", StringComparison.Ordinal))
            {
                continue;
            }

            hasNonRootMount = true;
            if (IsPathUnderRoot(fullPath, mountPoint))
            {
                return true;
            }
        }

        // If we cannot discover mount points, defer to writable check.
        return !hasNonRootMount;
    }

    private static IEnumerable<string> EnumerateLinuxMountPoints()
    {
        const string mountInfoPath = "/proc/self/mountinfo";
        if (!File.Exists(mountInfoPath))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(mountInfoPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var separatorIndex = line.IndexOf(" - ", StringComparison.Ordinal);
            var head = separatorIndex >= 0 ? line[..separatorIndex] : line;
            var parts = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
            {
                continue;
            }

            var mountPoint = UnescapeMountInfoPath(parts[4]);
            if (string.IsNullOrWhiteSpace(mountPoint))
            {
                continue;
            }

            string normalized;
            try
            {
                normalized = Path.GetFullPath(mountPoint);
            }
            catch
            {
                normalized = mountPoint;
            }

            if (seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalizedPath) || string.IsNullOrWhiteSpace(normalizedRoot))
        {
            return false;
        }

        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.Ordinal))
        {
            return true;
        }

        var rootedPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootedPrefix, StringComparison.Ordinal);
    }

    private static string UnescapeMountInfoPath(string value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var index = 0;
        while (index < value.Length)
        {
            if (TryDecodeMountInfoEscape(value, index, out var decodedChar))
            {
                builder.Append(decodedChar);
                index += 4;
                continue;
            }

            builder.Append(value[index]);
            index++;
        }

        return builder.ToString();
    }

    private static bool TryDecodeMountInfoEscape(string value, int index, out char decodedChar)
    {
        decodedChar = default;
        if (value[index] != '\\'
            || index + 3 >= value.Length
            || !IsOctalDigit(value[index + 1])
            || !IsOctalDigit(value[index + 2])
            || !IsOctalDigit(value[index + 3]))
        {
            return false;
        }

        var octal = value.Substring(index + 1, 3);
        decodedChar = (char)Convert.ToInt32(octal, 8);
        return true;
    }

    private static bool IsOctalDigit(char value)
    {
        return value is >= '0' and <= '7';
    }

    private static string RewriteLegacyHomeUserPath(string path)
    {
        const string legacyPrefix = "/home/user";
        if (!path.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)?.Trim();
        if (string.IsNullOrWhiteSpace(userProfile)
            || string.Equals(userProfile, legacyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var suffix = path.Length > legacyPrefix.Length
            ? path[legacyPrefix.Length..].TrimStart('/', '\\')
            : string.Empty;
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return userProfile;
        }

        return Path.Join(userProfile, suffix.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
    }

    public DeezSpoTagSettings ResetToDefaults()
    {
        var defaultSettings = GetStaticDefaultSettings();
        CheckAndFixSettings(defaultSettings, out _);
        SaveSettings(defaultSettings);
        return defaultSettings;
    }

    /// <summary>
    /// Get default settings exactly like deezspotag DEFAULT_SETTINGS
    /// </summary>
    public static DeezSpoTagSettings GetStaticDefaultSettings()
    {
        return new DeezSpoTagSettings();
    }

    public static string GenerateApiToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public void UpdateSetting(string settingName, object value)
    {
        var settings = LoadSettings();
        var property = typeof(DeezSpoTagSettings).GetProperty(settingName);
        if (property != null)
        {
            property.SetValue(settings, value);
            SaveSettings(settings);
        }
    }

    public T? GetSetting<T>(string settingName)
    {
        var settings = LoadSettings();
        var property = typeof(DeezSpoTagSettings).GetProperty(settingName);
        if (property != null)
        {
            var value = property.GetValue(settings);
            if (value is T typedValue)
            {
                return typedValue;
            }
        }
        return default!;
    }

    public static object GetTemplateVariables()
    {
        return new
        {
            track = new[]
            {
                "%title%", "%artist%", "%artists%", "%allartists%", "%mainartists%", "%featartists%",
                "%album%", "%albumartist%", "%tracknumber%", "%tracktotal%", "%discnumber%", "%disctotal%",
                "%genre%", "%year%", "%date%", "%bpm%", "%label%", "%isrc%", "%upc%", "%explicit%",
                "%track_id%", "%album_id%", "%artist_id%", "%playlist_id%", "%position%"
            },
            album = new[]
            {
                "%album_id%", "%genre%", "%album%", "%artist%", "%artist_id%", "%root_artist%",
                "%root_artist_id%", "%tracktotal%", "%disctotal%", "%type%", "%upc%", "%explicit%",
                "%label%", "%year%", "%date%", "%bitrate%"
            },
            playlist = new[]
            {
                "%playlist%", "%playlist_id%", "%owner%", "%owner_id%", "%year%", "%date%", "%explicit%"
            }
        };
    }

    public static bool IsValidTemplate(string template)
    {
        return !string.IsNullOrWhiteSpace(template);
    }

    private static string NormalizeLyricsFallbackOrder(string? fallbackOrder)
    {
        var parsed = (fallbackOrder ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .Where(p => CanonicalLyricsProviders.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (parsed.Count == 0)
        {
            parsed.AddRange(CanonicalLyricsProviders);
        }

        return string.Join(",", parsed);
    }

    private static string NormalizeLyricsTypeSelection(string? value)
    {
        var parsed = (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeLyricsTypeToken)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (parsed.Count == 0)
        {
            parsed.AddRange(CanonicalLyricsTypes);
        }

        return string.Join(",", parsed);
    }

    private static string NormalizeLyricsTypeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        return token.Trim().ToLowerInvariant() switch
        {
            var known when CanonicalLyricsTypes.Contains(known, StringComparer.OrdinalIgnoreCase) => known,
            "synced-lyrics" => "lyrics",
            "time-synced-lyrics" => SyllableLyricsType,
            "timesynced-lyrics" => SyllableLyricsType,
            "time_synced_lyrics" => SyllableLyricsType,
            "syllablelyrics" => SyllableLyricsType,
            "unsunsynced-lyrics" => UnsyncedLyricsType,
            "unsyncedlyrics" => UnsyncedLyricsType,
            "unsynced" => UnsyncedLyricsType,
            "unsynchronized-lyrics" => UnsyncedLyricsType,
            "unsynchronised-lyrics" => UnsyncedLyricsType,
            _ => string.Empty
        };
    }

    private static string NormalizeLyricsFormatSelection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "both";
        }

        return value.Trim().ToLowerInvariant() switch
        {
            var known when CanonicalLyricsFormats.Contains(known, StringComparer.OrdinalIgnoreCase) => known,
            "lyrics" => "both",
            "lrc+ttml" => "both",
            "ttml+lrc" => "both",
            _ => "both"
        };
    }

    private static bool IsSupportedMetadataSource(string? source)
    {
        return string.Equals(source, "spotify", StringComparison.OrdinalIgnoreCase)
               || string.Equals(source, "deezer", StringComparison.OrdinalIgnoreCase);
    }
}
