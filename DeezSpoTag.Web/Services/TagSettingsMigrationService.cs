using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class TagSettingsMigrationService
{
    private readonly TaggingProfileService _profileService;
    private readonly LibraryRepository _repository;
    private readonly ILogger<TagSettingsMigrationService> _logger;
    private readonly IWebHostEnvironment _environment;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TagSettingsMigrationService(
        TaggingProfileService profileService,
        LibraryRepository repository,
        IWebHostEnvironment environment,
        ILogger<TagSettingsMigrationService> logger)
    {
        _profileService = profileService;
        _repository = repository;
        _environment = environment;
        _logger = logger;
    }

    public async Task MigrateAsync()
    {
        await _profileService.EnsureSingleStoreAsync();

        if (!_repository.IsConfigured)
        {
            _logger.LogInformation("Tagging settings migration skipped: library DB not configured.");
            return;
        }

        if (await _profileService.HasAnyProfilesAsync())
        {
            _logger.LogInformation("Tagging profiles already exist, skipping migration.");
            return;
        }

        _logger.LogDebug("Starting tagging settings migration...");

        var dataRoot = AppDataPaths.GetDataRoot(_environment);
        var settingsPath = Path.Join(dataRoot, "settings.json");
        var lastAutoTagConfigPath = Path.Join(dataRoot, "autotag", "last-config.json");

        TagSettings? oldTagSettings = null;
        DeezSpoTagSettings? oldSettings = null;
        if (File.Exists(settingsPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(settingsPath);
                oldSettings = JsonSerializer.Deserialize<DeezSpoTagSettings>(json, _jsonOptions);
                oldTagSettings = oldSettings?.Tags;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to load existing settings.");
            }
        }

        AutoTagSettings? autoTagSettings = null;
        if (File.Exists(lastAutoTagConfigPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(lastAutoTagConfigPath);
                autoTagSettings = JsonSerializer.Deserialize<AutoTagSettings>(json, _jsonOptions);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to load last AutoTag config.");
            }
        }

        var defaultProfile = new TaggingProfile
        {
            Name = "Default",
            IsDefault = true,
            TagConfig = ConvertTagSettings(oldTagSettings),
            AutoTag = autoTagSettings ?? new AutoTagSettings(),
            Technical = ConvertTechnicalSettings(oldTagSettings, oldSettings)
        };

        await _profileService.UpsertAsync(defaultProfile);
        _logger.LogInformation("Created default tagging profile from existing settings.");

        var folders = await _repository.GetFoldersAsync();
        foreach (var folderId in folders.Select(static folder => folder.Id))
        {
            await _repository.UpdateFolderProfileAsync(folderId, defaultProfile.Id);
            await _repository.UpdateFolderAutoTagEnabledAsync(folderId, true);
        }

        _logger.LogInformation("Associated {Count} folders with default profile.", folders.Count);
    }

    private static UnifiedTagConfig ConvertTagSettings(TagSettings? old)
    {
        if (old is null)
        {
            return new UnifiedTagConfig();
        }

        return new UnifiedTagConfig
        {
            Title = ResolveLegacyTagSource(old.Title),
            Artist = ResolveLegacyTagSource(old.Artist),
            Artists = ResolveLegacyTagSource(old.Artists),
            Album = ResolveLegacyTagSource(old.Album),
            AlbumArtist = ResolveLegacyTagSource(old.AlbumArtist),
            Cover = ResolveLegacyTagSource(old.Cover),
            TrackNumber = ResolveLegacyTagSource(old.TrackNumber),
            TrackTotal = ResolveLegacyTagSource(old.TrackTotal),
            DiscNumber = ResolveLegacyTagSource(old.DiscNumber),
            DiscTotal = ResolveLegacyTagSource(old.DiscTotal),
            Genre = ResolveLegacyTagSource(old.Genre),
            Year = ResolveLegacyTagSource(old.Year),
            Date = ResolveLegacyTagSource(old.Date),
            Isrc = ResolveLegacyTagSource(old.Isrc),
            Barcode = ResolveLegacyTagSource(old.Barcode),
            Bpm = ResolveLegacyTagSource(old.Bpm),
            Key = ResolveLegacyTagSource(old.Key),
            Duration = ResolveLegacyTagSource(old.Length),
            ReplayGain = ResolveLegacyTagSource(old.ReplayGain),
            Danceability = ResolveLegacyTagSource(old.Danceability),
            Energy = ResolveLegacyTagSource(old.Energy),
            Valence = ResolveLegacyTagSource(old.Valence),
            Acousticness = ResolveLegacyTagSource(old.Acousticness),
            Instrumentalness = ResolveLegacyTagSource(old.Instrumentalness),
            Speechiness = ResolveLegacyTagSource(old.Speechiness),
            Loudness = ResolveLegacyTagSource(old.Loudness),
            Tempo = ResolveLegacyTagSource(old.Tempo),
            TimeSignature = ResolveLegacyTagSource(old.TimeSignature),
            Liveness = ResolveLegacyTagSource(old.Liveness),
            Label = ResolveLegacyTagSource(old.Label),
            Copyright = ResolveLegacyTagSource(old.Copyright),
            UnsyncedLyrics = ResolveLegacyTagSource(old.Lyrics),
            SyncedLyrics = ResolveLegacyTagSource(old.SyncedLyrics),
            Composer = ResolveLegacyTagSource(old.Composer),
            InvolvedPeople = ResolveLegacyTagSource(old.InvolvedPeople),
            Source = ResolveLegacyTagSource(old.Source),
            Explicit = ResolveLegacyTagSource(old.Explicit),
            Rating = ResolveLegacyTagSource(old.Rating),
            Style = TagSource.AutoTagPlatform,
            ReleaseDate = TagSource.AutoTagPlatform,
            PublishDate = TagSource.AutoTagPlatform,
            ReleaseId = TagSource.AutoTagPlatform,
            TrackId = TagSource.AutoTagPlatform,
            CatalogNumber = TagSource.AutoTagPlatform,
            Remixer = TagSource.AutoTagPlatform,
            Version = TagSource.AutoTagPlatform,
            Mood = TagSource.AutoTagPlatform,
            Url = TagSource.AutoTagPlatform,
            OtherTags = TagSource.AutoTagPlatform,
            MetaTags = TagSource.AutoTagPlatform
        };
    }

    private static TagSource ResolveLegacyTagSource(bool enabled)
        => enabled ? TagSource.DownloadSource : TagSource.None;

    private static TechnicalTagSettings ConvertTechnicalSettings(TagSettings? tagSettings, DeezSpoTagSettings? settings)
    {
        var result = new TechnicalTagSettings();

        if (tagSettings != null)
        {
            result.UseNullSeparator = tagSettings.UseNullSeparator;
            result.SaveID3v1 = tagSettings.SaveID3v1;
            result.MultiArtistSeparator = tagSettings.MultiArtistSeparator;
            result.SingleAlbumArtist = tagSettings.SingleAlbumArtist;
            result.CoverDescriptionUTF8 = tagSettings.CoverDescriptionUTF8;
            result.SavePlaylistAsCompilation = tagSettings.SavePlaylistAsCompilation;
        }

        if (settings != null)
        {
            result.AlbumVariousArtists = settings.AlbumVariousArtists;
            result.RemoveDuplicateArtists = settings.RemoveDuplicateArtists;
            result.RemoveAlbumVersion = settings.RemoveAlbumVersion;
            result.DateFormat = settings.DateFormat;
            result.FeaturedToTitle = settings.FeaturedToTitle;
            result.TitleCasing = settings.TitleCasing;
            result.ArtistCasing = settings.ArtistCasing;
            result.SyncedLyrics = settings.SyncedLyrics;
            result.SaveLyrics = settings.SaveLyrics;
            result.EmbedLyrics = (tagSettings?.Lyrics ?? false) || (tagSettings?.SyncedLyrics ?? false);
            result.LrcType = settings.LrcType;
            result.LrcFormat = settings.LrcFormat;
            result.LyricsFallbackEnabled = settings.LyricsFallbackEnabled;
            result.LyricsFallbackOrder = settings.LyricsFallbackOrder;
            result.ArtworkFallbackEnabled = settings.ArtworkFallbackEnabled;
            result.ArtworkFallbackOrder = settings.ArtworkFallbackOrder;
            result.ArtistArtworkFallbackEnabled = settings.ArtistArtworkFallbackEnabled;
            result.ArtistArtworkFallbackOrder = settings.ArtistArtworkFallbackOrder;
        }

        return result;
    }
}
