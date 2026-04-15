using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Enums;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Shared.Settings;

/// <summary>
/// Settings validator and normalizer for deezspotag settings
/// Ported from: check function in deezspotag settings.ts
/// </summary>
public class DeezSpoTagSettingsValidator
{
    private readonly ILogger<DeezSpoTagSettingsValidator> _logger;

    public DeezSpoTagSettingsValidator(ILogger<DeezSpoTagSettingsValidator> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Validate and fix settings, returning number of changes made
    /// Ported from: check function in deezspotag settings.ts
    /// </summary>
    public int ValidateAndFixSettings(DeezSpoTagSettings settings)
    {
        try
        {
            _logger.LogDebug("Validating deezspotag settings");

            var changes = 0;
            var defaultSettings = GetDefaultSettings();

            // Check main settings properties
            changes += ValidateProperty(settings, defaultSettings, nameof(settings.DownloadLocation),
                () => settings.DownloadLocation, (v) => settings.DownloadLocation = v);
            changes += ValidateProperty(settings, defaultSettings, nameof(settings.TracknameTemplate),
                () => settings.TracknameTemplate, (v) => settings.TracknameTemplate = v);
            changes += ValidateProperty(settings, defaultSettings, nameof(settings.AlbumTracknameTemplate),
                () => settings.AlbumTracknameTemplate, (v) => settings.AlbumTracknameTemplate = v);
            changes += ValidateProperty(settings, defaultSettings, nameof(settings.PlaylistTracknameTemplate),
                () => settings.PlaylistTracknameTemplate, (v) => settings.PlaylistTracknameTemplate = v);
            changes += ValidateProperty(settings, defaultSettings, nameof(settings.PlaylistNameTemplate),
                () => settings.PlaylistNameTemplate, (v) => settings.PlaylistNameTemplate = v);
            changes += ValidateProperty(settings, defaultSettings, nameof(settings.ArtistNameTemplate),
                () => settings.ArtistNameTemplate, (v) => settings.ArtistNameTemplate = v);
            changes += ValidateProperty(settings, defaultSettings, nameof(settings.AlbumNameTemplate),
                () => settings.AlbumNameTemplate, (v) => settings.AlbumNameTemplate = v);
            changes += ValidateProperty(settings, defaultSettings, nameof(settings.PlaylistFilenameTemplate),
                () => settings.PlaylistFilenameTemplate, (v) => settings.PlaylistFilenameTemplate = v);
            changes += ValidateProperty(settings, defaultSettings, nameof(settings.CoverImageTemplate),
                () => settings.CoverImageTemplate, (v) => settings.CoverImageTemplate = v);
            changes += ValidateProperty(settings, defaultSettings, nameof(settings.ArtistImageTemplate),
                () => settings.ArtistImageTemplate, (v) => settings.ArtistImageTemplate = v);
            changes += ValidateProperty(settings, defaultSettings, nameof(settings.AuthorizationToken),
                () => settings.AuthorizationToken, (v) => settings.AuthorizationToken = v);
            changes += ValidateProperty(settings, defaultSettings, nameof(settings.LrcType),
                () => settings.LrcType, (v) => settings.LrcType = v);
            changes += ValidateProperty(settings, defaultSettings, nameof(settings.LrcFormat),
                () => settings.LrcFormat, (v) => settings.LrcFormat = v);
            changes += ValidateProperty(settings, defaultSettings, nameof(settings.ConvertFormat),
                () => settings.ConvertFormat, (v) => settings.ConvertFormat = v);
            changes += ValidateProperty(settings, defaultSettings, nameof(settings.ConvertExtraArgs),
                () => settings.ConvertExtraArgs, (v) => settings.ConvertExtraArgs = v);
            changes += ValidateProperty(settings, defaultSettings, nameof(settings.MvFileFormat),
                () => settings.MvFileFormat, (v) => settings.MvFileFormat = v);

            // Check boolean properties
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.CreatePlaylistFolder),
                () => settings.CreatePlaylistFolder, (v) => settings.CreatePlaylistFolder = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.CreateArtistFolder),
                () => settings.CreateArtistFolder, (v) => settings.CreateArtistFolder = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.CreateAlbumFolder),
                () => settings.CreateAlbumFolder, (v) => settings.CreateAlbumFolder = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.CreateCDFolder),
                () => settings.CreateCDFolder, (v) => settings.CreateCDFolder = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.CreateStructurePlaylist),
                () => settings.CreateStructurePlaylist, (v) => settings.CreateStructurePlaylist = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.CreateSingleFolder),
                () => settings.CreateSingleFolder, (v) => settings.CreateSingleFolder = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.PadTracks),
                () => settings.PadTracks, (v) => settings.PadTracks = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.PadSingleDigit),
                () => settings.PadSingleDigit, (v) => settings.PadSingleDigit = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.FeelingLucky),
                () => settings.FeelingLucky, (v) => settings.FeelingLucky = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.FallbackBitrate),
                () => settings.FallbackBitrate, (v) => settings.FallbackBitrate = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.FallbackSearch),
                () => settings.FallbackSearch, (v) => settings.FallbackSearch = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.FallbackISRC),
                () => settings.FallbackISRC, (v) => settings.FallbackISRC = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.LogErrors),
                () => settings.LogErrors, (v) => settings.LogErrors = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.LogSearched),
                () => settings.LogSearched, (v) => settings.LogSearched = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.CreateM3U8File),
                () => settings.CreateM3U8File, (v) => settings.CreateM3U8File = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.SyncedLyrics),
                () => settings.SyncedLyrics, (v) => settings.SyncedLyrics = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.LyricsFallbackEnabled),
                () => settings.LyricsFallbackEnabled, (v) => settings.LyricsFallbackEnabled = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.ArtworkFallbackEnabled),
                () => settings.ArtworkFallbackEnabled, (v) => settings.ArtworkFallbackEnabled = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.ArtistArtworkFallbackEnabled),
                () => settings.ArtistArtworkFallbackEnabled, (v) => settings.ArtistArtworkFallbackEnabled = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.SaveArtwork),
                () => settings.SaveArtwork, (v) => settings.SaveArtwork = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.SaveArtworkArtist),
                () => settings.SaveArtworkArtist, (v) => settings.SaveArtworkArtist = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.SaveAnimatedArtwork),
                () => settings.SaveAnimatedArtwork, (v) => settings.SaveAnimatedArtwork = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.AlbumVariousArtists),
                () => settings.AlbumVariousArtists, (v) => settings.AlbumVariousArtists = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.RemoveAlbumVersion),
                () => settings.RemoveAlbumVersion, (v) => settings.RemoveAlbumVersion = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.RemoveDuplicateArtists),
                () => settings.RemoveDuplicateArtists, (v) => settings.RemoveDuplicateArtists = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.DlAlbumcoverForPlaylist),
                () => settings.DlAlbumcoverForPlaylist, (v) => settings.DlAlbumcoverForPlaylist = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.GetM3u8FromDevice),
                () => settings.GetM3u8FromDevice, (v) => settings.GetM3u8FromDevice = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.ConvertAfterDownload),
                () => settings.ConvertAfterDownload, (v) => settings.ConvertAfterDownload = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.ConvertKeepOriginal),
                () => settings.ConvertKeepOriginal, (v) => settings.ConvertKeepOriginal = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.ConvertSkipIfSourceMatches),
                () => settings.ConvertSkipIfSourceMatches, (v) => settings.ConvertSkipIfSourceMatches = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.ConvertWarnLossyToLossless),
                () => settings.ConvertWarnLossyToLossless, (v) => settings.ConvertWarnLossyToLossless = v);
            changes += ValidateBooleanProperty(settings, defaultSettings, nameof(settings.ConvertSkipLossyToLossless),
                () => settings.ConvertSkipLossyToLossless, (v) => settings.ConvertSkipLossyToLossless = v);

            // Check integer properties
            changes += ValidateIntegerProperty(settings, defaultSettings, nameof(settings.PaddingSize),
                () => settings.PaddingSize, (v) => settings.PaddingSize = v);
            changes += ValidateIntegerProperty(settings, defaultSettings, nameof(settings.MaxBitrate),
                () => settings.MaxBitrate, (v) => settings.MaxBitrate = v);
            changes += ValidateIntegerProperty(settings, defaultSettings, nameof(settings.EmbeddedArtworkSize),
                () => settings.EmbeddedArtworkSize, (v) => settings.EmbeddedArtworkSize = v);
            changes += ValidateIntegerProperty(settings, defaultSettings, nameof(settings.LocalArtworkSize),
                () => settings.LocalArtworkSize, (v) => settings.LocalArtworkSize = v);
            changes += ValidateIntegerProperty(settings, defaultSettings, nameof(settings.AppleArtworkSize),
                () => settings.AppleArtworkSize, (v) => settings.AppleArtworkSize = v);
            changes += ValidateIntegerProperty(settings, defaultSettings, nameof(settings.JpegImageQuality),
                () => settings.JpegImageQuality, (v) => settings.JpegImageQuality = v);
            changes += ValidateIntegerProperty(settings, defaultSettings, nameof(settings.LimitMax),
                () => settings.LimitMax, (v) => settings.LimitMax = v);

            // Check string properties with specific validation
            changes += ValidateStringProperty(settings, defaultSettings, nameof(settings.IllegalCharacterReplacer),
                () => settings.IllegalCharacterReplacer, (v) => settings.IllegalCharacterReplacer = v);
            changes += ValidateStringProperty(settings, defaultSettings, nameof(settings.FeaturedToTitle),
                () => settings.FeaturedToTitle, (v) => settings.FeaturedToTitle = v);
            changes += ValidateStringProperty(settings, defaultSettings, nameof(settings.OverwriteFile),
                () => settings.OverwriteFile, (v) => settings.OverwriteFile = v);
            changes += ValidateStringProperty(settings, defaultSettings, nameof(settings.LocalArtworkFormat),
                () => settings.LocalArtworkFormat, (v) => settings.LocalArtworkFormat = v);
            changes += ValidateStringProperty(settings, defaultSettings, nameof(settings.AppleArtworkSizeText),
                () => settings.AppleArtworkSizeText, (v) => settings.AppleArtworkSizeText = v);
            changes += ValidateStringProperty(settings, defaultSettings, nameof(settings.DateFormat),
                () => settings.DateFormat, (v) => settings.DateFormat = v);
            changes += ValidateStringProperty(settings, defaultSettings, nameof(settings.TitleCasing),
                () => settings.TitleCasing, (v) => settings.TitleCasing = v);
            changes += ValidateStringProperty(settings, defaultSettings, nameof(settings.ArtistCasing),
                () => settings.ArtistCasing, (v) => settings.ArtistCasing = v);
            changes += ValidateStringProperty(settings, defaultSettings, nameof(settings.ExecuteCommand),
                () => settings.ExecuteCommand, (v) => settings.ExecuteCommand = v);
            changes += ValidateStringProperty(settings, defaultSettings, nameof(settings.LyricsFallbackOrder),
                () => settings.LyricsFallbackOrder, (v) => settings.LyricsFallbackOrder = v);
            changes += ValidateStringProperty(settings, defaultSettings, nameof(settings.ArtworkFallbackOrder),
                () => settings.ArtworkFallbackOrder, (v) => settings.ArtworkFallbackOrder = v);
            changes += ValidateStringProperty(settings, defaultSettings, nameof(settings.ArtistArtworkFallbackOrder),
                () => settings.ArtistArtworkFallbackOrder, (v) => settings.ArtistArtworkFallbackOrder = v);

            // Validate tags settings
            if (settings.Tags == null)
            {
                settings.Tags = defaultSettings.Tags;
                changes++;
            }
            else
            {
                changes += ValidateTagsSettings(settings.Tags, defaultSettings.Tags);
            }

            // Special validation for empty download location
            if (string.IsNullOrEmpty(settings.DownloadLocation))
            {
                settings.DownloadLocation = GetDefaultDownloadLocation();
                changes++;
            }

            // Special validation for empty templates
            var templates = new (string, Func<string>, Action<string>)[]
            {
                (nameof(settings.TracknameTemplate), () => settings.TracknameTemplate, (string v) => settings.TracknameTemplate = v),
                (nameof(settings.AlbumTracknameTemplate), () => settings.AlbumTracknameTemplate, (string v) => settings.AlbumTracknameTemplate = v),
                (nameof(settings.PlaylistTracknameTemplate), () => settings.PlaylistTracknameTemplate, (string v) => settings.PlaylistTracknameTemplate = v),
                (nameof(settings.PlaylistNameTemplate), () => settings.PlaylistNameTemplate, (string v) => settings.PlaylistNameTemplate = v),
                (nameof(settings.ArtistNameTemplate), () => settings.ArtistNameTemplate, (string v) => settings.ArtistNameTemplate = v),
                (nameof(settings.AlbumNameTemplate), () => settings.AlbumNameTemplate, (string v) => settings.AlbumNameTemplate = v),
                (nameof(settings.PlaylistFilenameTemplate), () => settings.PlaylistFilenameTemplate, (string v) => settings.PlaylistFilenameTemplate = v),
                (nameof(settings.CoverImageTemplate), () => settings.CoverImageTemplate, (string v) => settings.CoverImageTemplate = v),
                (nameof(settings.ArtistImageTemplate), () => settings.ArtistImageTemplate, (string v) => settings.ArtistImageTemplate = v),
                (nameof(settings.MvFileFormat), () => settings.MvFileFormat, (string v) => settings.MvFileFormat = v)
            };

            foreach ((string name, Func<string> getter, Action<string> setter) in templates)
            {
                if (string.IsNullOrEmpty(getter()))
                {
                    var defaultValue = GetDefaultTemplateValue(name, defaultSettings);
                    setter(defaultValue);
                    changes++;
                }
            }

            if (changes > 0 && _logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Fixed {ChangeCount} invalid settings", changes);
            }

            return changes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error validating settings");
            return 0;
        }
    }

    /// <summary>
    /// Validate tags settings
    /// </summary>
    private static int ValidateTagsSettings(object tags, object defaultTags)
    {
        var changes = 0;

        // Use reflection to validate all tag properties
        var tagProperties = tags.GetType().GetProperties();
        var defaultTagProperties = defaultTags.GetType().GetProperties();

        foreach (var prop in tagProperties)
        {
            var defaultProp = defaultTagProperties.FirstOrDefault(p => p.Name == prop.Name);
            if (defaultProp != null)
            {
                var currentValue = prop.GetValue(tags);
                var defaultValue = defaultProp.GetValue(defaultTags);

                if (currentValue == null || currentValue.GetType() != defaultValue?.GetType())
                {
                    prop.SetValue(tags, defaultValue);
                    changes++;
                }
            }
        }

        return changes;
    }

    /// <summary>
    /// Validate a generic property
    /// </summary>
    /// <summary>
    /// Validate a boolean property
    /// </summary>
    private static int ValidateProperty<T>(DeezSpoTagSettings settings, DeezSpoTagSettings defaultSettings, string propertyName,
        Func<T> getter, Action<T> setter)
    {
        _ = settings;
        try
        {
            var currentValue = getter();
            var defaultValue = GetDefaultPropertyValue<T>(propertyName, defaultSettings);

            if (currentValue is null)
            {
                setter(defaultValue);
                return 1;
            }

            if (defaultValue is not null && currentValue.GetType() != defaultValue.GetType())
            {
                setter(defaultValue);
                return 1;
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Validate a boolean property
    /// </summary>
    private static int ValidateBooleanProperty(DeezSpoTagSettings settings, DeezSpoTagSettings defaultSettings, string propertyName,
        Func<bool> getter, Action<bool> setter)
    {
        _ = settings;
        _ = defaultSettings;
        _ = propertyName;
        _ = getter;
        _ = setter;
        // Boolean properties don't need validation as they can't be invalid
        return 0;
    }

    /// <summary>
    /// Validate an integer property
    /// </summary>
    private static int ValidateIntegerProperty(DeezSpoTagSettings settings, DeezSpoTagSettings defaultSettings, string propertyName,
        Func<int> getter, Action<int> setter)
    {
        _ = settings;
        var currentValue = getter();

        // Validate specific integer constraints
        switch (propertyName)
        {
            case nameof(settings.EmbeddedArtworkSize):
            case nameof(settings.LocalArtworkSize):
            case nameof(settings.AppleArtworkSize):
                if (currentValue < 56 || currentValue > 5000)
                {
                    var defaultValue = GetDefaultPropertyValue<int>(propertyName, defaultSettings);
                    setter(defaultValue);
                    return 1;
                }
                break;
            case nameof(settings.JpegImageQuality):
                if (currentValue < 1 || currentValue > 100)
                {
                    setter(defaultSettings.JpegImageQuality);
                    return 1;
                }
                break;
        }

        return 0;
    }

    /// <summary>
    /// Validate a string property
    /// </summary>
    private static int ValidateStringProperty(DeezSpoTagSettings settings, DeezSpoTagSettings defaultSettings, string propertyName,
        Func<string> getter, Action<string> setter)
    {
        _ = settings;
        var currentValue = getter();

        // Validate specific string constraints
        switch (propertyName)
        {
            case nameof(settings.LocalArtworkFormat):
                var validFormats = new[] { "jpg", "png", "jpg,png" };
                if (!validFormats.Contains(currentValue))
                {
                    setter(defaultSettings.LocalArtworkFormat);
                    return 1;
                }
                break;
            case nameof(settings.AppleArtworkSizeText):
                if (!IsValidArtworkSizeText(currentValue))
                {
                    setter(defaultSettings.AppleArtworkSizeText);
                    return 1;
                }
                break;
            case nameof(settings.TitleCasing):
            case nameof(settings.ArtistCasing):
                var validCasing = new[] { "nothing", "lower", "upper", "title", "sentence" };
                if (!validCasing.Contains(currentValue))
                {
                    var defaultValue = GetDefaultPropertyValue<string>(propertyName, defaultSettings);
                    setter(defaultValue);
                    return 1;
                }
                break;
        }

        return 0;
    }

    private static bool IsValidArtworkSizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height))
        {
            return false;
        }

        return width > 0 && height > 0;
    }

    /// <summary>
    /// Get default property value using reflection
    /// </summary>
    private static T GetDefaultPropertyValue<T>(string propertyName, DeezSpoTagSettings defaultSettings)
    {
        var property = typeof(DeezSpoTagSettings).GetProperty(propertyName);
        if (property != null)
        {
            var value = property.GetValue(defaultSettings);
            if (value is T typedValue)
            {
                return typedValue;
            }
        }
        return default(T)!;
    }

    /// <summary>
    /// Get default template value
    /// </summary>
    private static string GetDefaultTemplateValue(string templateName, DeezSpoTagSettings defaultSettings)
    {
        return templateName switch
        {
            nameof(defaultSettings.TracknameTemplate) => defaultSettings.TracknameTemplate,
            nameof(defaultSettings.AlbumTracknameTemplate) => defaultSettings.AlbumTracknameTemplate,
            nameof(defaultSettings.PlaylistTracknameTemplate) => defaultSettings.PlaylistTracknameTemplate,
            nameof(defaultSettings.PlaylistNameTemplate) => defaultSettings.PlaylistNameTemplate,
            nameof(defaultSettings.ArtistNameTemplate) => defaultSettings.ArtistNameTemplate,
            nameof(defaultSettings.AlbumNameTemplate) => defaultSettings.AlbumNameTemplate,
            nameof(defaultSettings.PlaylistFilenameTemplate) => defaultSettings.PlaylistFilenameTemplate,
            nameof(defaultSettings.CoverImageTemplate) => defaultSettings.CoverImageTemplate,
            nameof(defaultSettings.ArtistImageTemplate) => defaultSettings.ArtistImageTemplate,
            nameof(defaultSettings.MvFileFormat) => defaultSettings.MvFileFormat,
            _ => ""
        };
    }

    /// <summary>
    /// Get default download location
    /// </summary>
    private static string GetDefaultDownloadLocation()
    {
        // Try to get user's Music folder, fallback to Documents/Music
        var musicFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (string.IsNullOrEmpty(musicFolder))
        {
            var documentsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            musicFolder = Path.Join(documentsFolder, "Music");
        }

        return musicFolder;
    }

    /// <summary>
    /// Get default settings instance
    /// Ported from: DEFAULT_SETTINGS in deezspotag settings.ts
    /// </summary>
    private static DeezSpoTagSettings GetDefaultSettings()
    {
        return new DeezSpoTagSettings
        {
            DownloadLocation = GetDefaultDownloadLocation(),
            TracknameTemplate = "%artist% - %title%",
            AlbumTracknameTemplate = "%tracknumber% - %title%",
            PlaylistTracknameTemplate = "%artist% - %title%",
            CreatePlaylistFolder = true,
            PlaylistNameTemplate = "%playlist%",
            CreateArtistFolder = false,
            ArtistNameTemplate = "%artist%",
            CreateAlbumFolder = true,
            AlbumNameTemplate = "%artist% - %album%",
            CreateCDFolder = true,
            CreateStructurePlaylist = false,
            CreateSingleFolder = false,
            PadTracks = true,
            PadSingleDigit = true,
            PaddingSize = 0,
            IllegalCharacterReplacer = "_",
            MaxBitrate = 1, // TrackFormats.MP3_128
            FeelingLucky = false,
            FallbackBitrate = false,
            FallbackSearch = false,
            FallbackISRC = false,
            LogErrors = true,
            LogSearched = false,
            OverwriteFile = "n",
            CreateM3U8File = false,
            PlaylistFilenameTemplate = "playlist",
            SyncedLyrics = true,
            LyricsFallbackEnabled = true,
            LyricsFallbackOrder = "apple,deezer,spotify,lrclib",
            ArtworkFallbackEnabled = true,
            ArtworkFallbackOrder = "apple,deezer,spotify",
            ArtistArtworkFallbackEnabled = true,
            ArtistArtworkFallbackOrder = "apple,deezer,spotify",
            EmbeddedArtworkSize = 800,
            LocalArtworkSize = 1200,
            AppleArtworkSize = 1200,
            AppleArtworkSizeText = "5000x5000",
            LocalArtworkFormat = "jpg",
            SaveArtwork = true,
            CoverImageTemplate = "cover",
            SaveArtworkArtist = false,
            ArtistImageTemplate = "folder",
            JpegImageQuality = 90,
            DateFormat = "Y-M-D",
            AlbumVariousArtists = true,
            RemoveAlbumVersion = false,
            RemoveDuplicateArtists = true,
            FeaturedToTitle = "0",
            TitleCasing = "nothing",
            ArtistCasing = "nothing",
            ExecuteCommand = "",
            AuthorizationToken = "",
            LrcType = "lyrics,syllable-lyrics,unsynced-lyrics",
            LrcFormat = "both",
            SaveAnimatedArtwork = true,
            LimitMax = 200,
            DlAlbumcoverForPlaylist = true,
            GetM3u8FromDevice = true,
            ConvertAfterDownload = true,
            ConvertFormat = "",
            ConvertKeepOriginal = true,
            ConvertSkipIfSourceMatches = true,
            ConvertExtraArgs = "",
            ConvertWarnLossyToLossless = false,
            ConvertSkipLossyToLossless = false,
            MvFileFormat = "{ArtistName} - {VideoName} [{ReleaseYear}]",
            Tags = new TagSettings()
        };
    }
}
