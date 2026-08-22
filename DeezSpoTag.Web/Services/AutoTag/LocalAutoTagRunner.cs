using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Security;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Identity;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using TagLib;
using IOFile = System.IO.File;
using DownloadLyricsService = DeezSpoTag.Services.Download.Utils.LyricsService;
using LyricsProviderRegistry = DeezSpoTag.Services.Download.Utils.LyricsProviderRegistry;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class LocalAutoTagRunner : IAutoTagRunner
{
    private static readonly TimeSpan ArtworkFallbackTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LyricsResolutionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AppleExtrasTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PlatformMatchTimeout = TimeSpan.FromSeconds(45);
    private const int DefaultLibraryWideEnhancementBatchSize = 40;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        FlacExtension,
        ".wav",
        ".aiff",
        ".aif",
        ".alac",
        ".m4a",
        ".m4b",
        ".mp4",
        ".aac",
        ".mp3",
        ".wma",
        ".ogg",
        ".opus",
        ".oga",
        ".ape",
        ".wv",
        ".mp2",
        ".mp1",
        ".tta",
        ".dsf",
        ".dff",
        ".mka"
    };
    private static readonly Regex LeadingTrackNumberRegex = new(
        @"^\s*(?:\d+\s*[-._)\]]\s*)+",
        RegexOptions.Compiled,
        RegexTimeout);
    private static readonly Regex TitleQualifierRegex = new(
        @"\b(?:feat(?:uring)?|ft\.?|remix|mix|edit|version|live|acoustic|demo|radio|extended|dub|instrumental|remaster(?:ed)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);
    private static readonly Regex BracketedTitleDetailRegex = new(
        @"[\(\[\{][^\)\]\}]{2,}[\)\]\}]",
        RegexOptions.Compiled,
        RegexTimeout);
    private static readonly Regex LooseTitleNormalizationRegex = new(
        @"[^a-z0-9]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);
    private static readonly Regex VariantSuffixRegex = new(
        @"(?:\b(?:pt\.?|part|vol\.?|volume)\s*\d+\b|\b(?:ii|iii|iv|v|vi|vii|viii|ix|x)\b|\b\d{1,2}\b)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);
    private static readonly HashSet<string> WeakMetadataValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown",
        "unknown artist",
        "unknown album artist",
        "unknown album",
        "untitled",
        "track",
        "audio"
    };
    private static readonly TimeSpan MatchCacheTtl = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly char[] LyricsLineSeparators = ['\r', '\n'];
    private const int MaxCacheEntriesPerJob = 6000;
    private const string FlacExtension = ".flac";
    private const string TtmlExtension = ".ttml";
    private const string ShazamPlatform = "shazam";
    private const string LyricsPlatform = "lyrics";
    private const string UnknownArtist = "Unknown Artist";
    private const string MultiArtistSeparatorDefault = "default";
    private const string MultiArtistSeparatorNothing = "nothing";
    private const string AlbumArtTag = "albumArt";
    private const string SyncedLyricsTag = "syncedLyrics";
    private const string UnsyncedLyricsTag = "unsyncedLyrics";
    private const string TtmlLyricsTag = "ttmlLyrics";
    private const string SyncedLyricsSourceFormatTag = "syncedLyricsSourceFormat";
    private const string ItunesPlatform = "itunes";
    private const string AppleProvider = "apple";
    private const string SpotifyPlatform = "spotify";
    private const string LyricsTag = "lyrics";
    private const string SyllableLyricsType = "syllable-lyrics";
    private const string TtmlLyricsType = "ttml-lyrics";
    private const string UnsyncedLyricsType = "unsynced-lyrics";
    private const string AlbumArtistTag = "albumArtist";
    private const string TrackTotalTag = "trackTotal";
    private const string ReleaseTypeTag = "releaseType";
    private const string AlbumTag = "album";
    private const string CatalogNumberTag = "catalogNumber";
    private const string ReleaseIdTag = "releaseId";
    private const string TrackNumberTag = "trackNumber";
    private const string LabelTag = "label";
    private const string Mp4GenreTag = "GENRE";
    private const string DeezerPlatform = "deezer";
    private const string DeezerTrackIdTag = "DEEZER_TRACK_ID";
    private const string SpotifyTrackIdTag = "SPOTIFY_TRACK_ID";
    private const string SpotifyTrackIdLegacyTag = "SPOTIFY_TRACKID";
    private const string SpotifyIdLegacyTag = "SPOTIFYID";
    private const string SpotifyIdUnderscoreLegacyTag = "SPOTIFY_ID";
    private const string SpotifyUrlTag = "SPOTIFY_URL";
    private const string LrclibProvider = "lrclib";
    private const string LyricsUpperTag = "LYRICS";
    private const string LyricsSyncedTag = "LYRICS_SYNCED";
    private const string WwwAudioFileTag = "WWWAUDIOFILE";
    private const string TaggedDateTag = "1T_TAGGEDDATE";
    private const string TitleTag = "title";
    private const string ArtistTag = "artist";
    private const string BoomplayPlatform = "boomplay";
    private const string DiscNumberTag = "discNumber";
    private const string DiscTotalTag = "discTotal";
    private const string GenreTag = "genre";
    private const string ExplicitTag = "explicit";
    private const string ItunesAdvisoryTag = "ITUNESADVISORY";
    private const string TrackTotalRawTag = "TRACKTOTAL";
    private const string ReleaseTypeRawTag = "RELEASETYPE";
    private const string DiscTotalRawTag = "DISCTOTAL";
    private const string TitleUpperTag = "TITLE";
    private const string ArtistUpperTag = "ARTIST";
    private const string AlbumArtistUpperTag = "ALBUMARTIST";
    private const string AlbumUpperTag = "ALBUM";
    private const string TrackNumberUpperTag = "TRACKNUMBER";
    private const string OriginalDateUpperTag = "ORIGINALDATE";
    private const string ComposerUpperTag = "COMPOSER";
    private const string InitialKeyRawTag = "initialkey";
    private const string IsoDateFormat = "yyyy-MM-dd";
    private const string DurationTag = "duration";
    private const string LengthTag = "length";
    private const string ReleaseDateTag = "releaseDate";
    private const string YearTag = "year";
    private const string DateTag = "date";
    private const string CoverTag = "cover";
    private const string VersionTag = "version";
    private const string DanceabilityTag = "DANCEABILITY";
    private const string EnergyTag = "ENERGY";
    private const string ValenceTag = "VALENCE";
    private const string AcousticnessTag = "ACOUSTICNESS";
    private const string InstrumentalnessTag = "INSTRUMENTALNESS";
    private const string SpeechinessTag = "SPEECHINESS";
    private const string LoudnessTag = "LOUDNESS";
    private const string TempoTag = "TEMPO";
    private const string TimeSignatureTag = "TIME_SIGNATURE";
    private const string LivenessTag = "LIVENESS";
    private const string LabelUpperTag = "LABEL";
    private const string BarcodeTag = "barcode";
    private const string BarcodeRawTag = "BARCODE";
    private const string ReplayGainTag = "replayGain";
    private const string ReplayGainRawTag = "REPLAYGAIN_TRACK_GAIN";
    private const string CopyrightTag = "copyright";
    private const string CopyrightRawTag = "COPYRIGHT";
    private const string ComposerTag = "composer";
    private const string LyricistTag = "lyricist";
    private const string LyricistRawTag = "LYRICIST";
    private const string InvolvedPeopleTag = "involvedPeople";
    private const string InvolvedPeopleRawTag = "INVOLVEDPEOPLE";
    private const string PublisherTag = "publisher";
    private const string PublisherRawTag = "PUBLISHER";
    private const string DescriptionTag = "description";
    private const string DescriptionRawTag = "DESCRIPTION";
    private const string CommentRawTag = "COMMENT";
    private const string SourceTag = "source";
    private const string SourceRawTag = "SOURCE";
    private const string SourceIdRawTag = "SOURCEID";
    private const string RecordingIdRawTag = "RECORDINGID";
    private const string ArtistIdRawTag = "ARTISTID";
    private const string AlbumArtistIdRawTag = "ALBUMARTISTID";
    private const string ReleaseGroupIdRawTag = "RELEASEGROUPID";
    private const string AlbumIdRawTag = "ALBUMID";
    private const string ReleaseStatusRawTag = "RELEASESTATUS";
    private const string ReleaseCountryRawTag = "RELEASECOUNTRY";
    private const string MediaRawTag = "MEDIA";
    private const string RatingTag = "rating";
    private const string RatingRawTag = "RATING";
    private const string LanguageTag = "language";
    private const string LanguageRawTag = "LANGUAGE";
    private const string StyleTag = "style";
    private const string PublishDateTag = "publishDate";
    private const string TrackIdTag = "trackId";
    private const string RecordingIdTag = "recordingId";
    private const string ArtistIdTag = "artistId";
    private const string AlbumArtistIdTag = "albumArtistId";
    private const string ReleaseGroupIdTag = "releaseGroupId";
    private const string AlbumIdTag = "albumId";
    private const string ReleaseStatusTag = "releaseStatus";
    private const string ReleaseCountryTag = "releaseCountry";
    private const string MediaTag = "media";
    private const string ArtistsTag = "artists";
    private const string BpmTag = "bpm";
    private const string IsrcTag = "isrc";
    private const string UrlTag = "url";
    private const string CatalogNumberUpperTag = "CATALOGNUMBER";
    private const string LengthUpperTag = "LENGTH";
    private const string RemixerTag = "remixer";
    private const string RemixerUpperTag = "REMIXER";
    private const string OtherTagsTag = "otherTags";
    private const string MetaTagsTag = "metaTags";
    private const string StyleUpperTag = "STYLE";
    private const string VorbisFormat = "vorbis";
    private static readonly HashSet<string> FirstClassRawOtherTags = new(StringComparer.OrdinalIgnoreCase)
    {
        RecordingIdTag,
        RecordingIdRawTag,
        "MUSICBRAINZ_RECORDINGID",
        "MUSICBRAINZ_RECORDING_ID",
        ArtistIdTag,
        ArtistIdRawTag,
        "MUSICBRAINZ_ARTISTID",
        AlbumArtistIdTag,
        AlbumArtistIdRawTag,
        "MUSICBRAINZ_ALBUMARTISTID",
        ReleaseGroupIdTag,
        ReleaseGroupIdRawTag,
        "MUSICBRAINZ_RELEASEGROUPID",
        AlbumIdTag,
        AlbumIdRawTag,
        "MUSICBRAINZ_ALBUMID",
        ReleaseStatusTag,
        ReleaseStatusRawTag,
        ReleaseCountryTag,
        ReleaseCountryRawTag,
        BarcodeTag,
        BarcodeRawTag,
        "upc",
        MediaTag,
        MediaRawTag,
        SourceTag,
        SourceRawTag,
        "sourceId",
        "SOURCE_ID",
        SourceIdRawTag,
        ReplayGainTag,
        ReplayGainRawTag,
        "gain",
        CopyrightTag,
        CopyrightRawTag,
        ComposerTag,
        ComposerUpperTag,
        "TCOM",
        LyricistTag,
        LyricistRawTag,
        "TEXT",
        InvolvedPeopleTag,
        InvolvedPeopleRawTag,
        PublisherTag,
        PublisherRawTag,
        DescriptionTag,
        DescriptionRawTag,
        CommentRawTag,
        RatingTag,
        RatingRawTag,
        LanguageTag,
        LanguageRawTag,
        DiscTotalTag,
        DiscTotalRawTag
    };
    private static readonly Dictionary<string, Action<TagSettings>> TagSettingsAppliers = new(StringComparer.OrdinalIgnoreCase)
    {
        [TitleTag] = settings => settings.Title = true,
        [ArtistTag] = settings => settings.Artist = true,
        [ArtistsTag] = settings => settings.Artists = true,
        [AlbumTag] = settings => settings.Album = true,
        [AlbumArtistTag] = settings => settings.AlbumArtist = true,
        [TrackNumberTag] = settings => settings.TrackNumber = true,
        [TrackTotalTag] = settings => settings.TrackTotal = true,
        [DiscNumberTag] = settings => settings.DiscNumber = true,
        [DiscTotalTag] = settings => settings.DiscTotal = true,
        [GenreTag] = settings => settings.Genre = true,
        [LabelTag] = settings => settings.Label = true,
        [BpmTag] = settings => settings.Bpm = true,
        [IsrcTag] = settings => settings.Isrc = true,
        [ExplicitTag] = settings => settings.Explicit = true,
        [DurationTag] = settings => settings.Length = true,
        [LengthTag] = settings => settings.Length = true,
        [ReleaseDateTag] = settings =>
        {
            settings.Date = true;
            settings.Year = true;
        },
        [YearTag] = settings =>
        {
            settings.Date = true;
            settings.Year = true;
        },
        [DateTag] = settings =>
        {
            settings.Date = true;
            settings.Year = true;
        },
        [AlbumArtTag] = settings => settings.Cover = true,
        [CoverTag] = settings => settings.Cover = true,
        [BarcodeTag] = settings => settings.Barcode = true,
        [ReplayGainTag] = settings => settings.ReplayGain = true,
        [CopyrightTag] = settings => settings.Copyright = true,
        [ComposerTag] = settings => settings.Composer = true,
        [LyricistTag] = settings => settings.Lyricist = true,
        [InvolvedPeopleTag] = settings => settings.InvolvedPeople = true,
        [PublisherTag] = settings => settings.Publisher = true,
        [DescriptionTag] = settings => settings.Description = true,
        [SourceTag] = settings => settings.Source = true,
        [UrlTag] = settings => settings.Url = true,
        [TrackIdTag] = settings => settings.TrackId = true,
        [ReleaseIdTag] = settings => settings.ReleaseId = true,
        [RecordingIdTag] = settings => settings.TrackId = true,
        [ArtistIdTag] = settings => settings.Source = true,
        [AlbumArtistIdTag] = settings => settings.Source = true,
        [ReleaseGroupIdTag] = settings => settings.ReleaseId = true,
        [AlbumIdTag] = settings => settings.ReleaseId = true,
        [ReleaseStatusTag] = settings => settings.ReleaseId = true,
        [ReleaseCountryTag] = settings => settings.ReleaseId = true,
        [MediaTag] = settings => settings.ReleaseId = true,
        [RatingTag] = settings => settings.Rating = true,
        [UnsyncedLyricsTag] = settings => settings.Lyrics = true,
        [LyricsTag] = settings => settings.Lyrics = true,
        [SyncedLyricsTag] = settings => settings.SyncedLyrics = true
    };
    private static readonly string[] ShazamRawTagHints =
    [
        "SHAZAM_TRACK_ID",
        "SHAZAM_TRACK_KEY",
        "SHAZAM_KEY",
        "SHAZAM_MUSICAL_KEY",
        "SHAZAM_URL",
        "SHAZAM_TITLE",
        "SHAZAM_ARTIST",
        "SHAZAM_ARTIST_IDS",
        "SHAZAM_ARTIST_ADAM_IDS",
        "SHAZAM_ISRC",
        "SHAZAM_DURATION_MS",
        "SHAZAM_GENRE",
        "SHAZAM_ALBUM",
        "SHAZAM_LABEL",
        "SHAZAM_RELEASE_DATE",
        "SHAZAM_ARTWORK",
        "SHAZAM_ARTWORK_HQ",
        "SHAZAM_ARTWORK_BG",
        "SHAZAM_LANGUAGE",
        "SHAZAM_COMPOSER",
        "SHAZAM_LYRICIST",
        "SHAZAM_PUBLISHER",
        "SHAZAM_TRACK_NUMBER",
        "SHAZAM_DISC_NUMBER",
        "SHAZAM_EXPLICIT",
        "SHAZAM_ALBUM_ADAM_ID",
        "SHAZAM_APPLE_MUSIC_URL",
        "SHAZAM_SPOTIFY_URL",
        "SHAZAM_YOUTUBE_URL",
        "SHAZAM_META_ALBUM",
        "SHAZAM_META_LABEL",
        "SHAZAM_META_RELEASED",
        "SHAZAM_META_RELEASE_DATE",
        "SHAZAM_META_RELEASE",
        "SHAZAM_META_YEAR",
        "SHAZAM_META_GENRE",
        "SHAZAM_META_ISRC",
        "SHAZAM_META_LANGUAGE",
        "SHAZAM_META_COMPOSER",
        "SHAZAM_META_SONGWRITER",
        "SHAZAM_META_SONGWRITER_S",
        "SHAZAM_META_WRITTEN_BY",
        "SHAZAM_META_LYRICIST",
        "SHAZAM_META_PUBLISHER",
        "SHAZAM_META_TRACK",
        "SHAZAM_META_TRACK_NUMBER",
        "SHAZAM_META_DISC",
        "SHAZAM_META_DISC_NUMBER",
        "SHAZAM_META_DURATION",
        "SHAZAM_META_TIME",
        "SHAZAM_META_LENGTH",
        "SHAZAM_META_EXPLICIT",
        "SHAZAM_META_CONTENT_RATING",
        "SHAZAM_META_KEY"
    ];
    private static readonly HashSet<string> BlockedGenres = new(StringComparer.OrdinalIgnoreCase)
    {
        "other",
        "others"
    };

    private static bool IsMp4Family(string extension)
    {
        return AtlTagHelper.IsMp4Family(extension);
    }
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobTokens = new();
    private readonly ConcurrentDictionary<string, JobMatchCacheState> _jobMatchCaches = new();
    private readonly ILogger<LocalAutoTagRunner> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MusicBrainzMatcher _musicBrainzMatcher;
    private readonly BeatportMatcher _beatportMatcher;
    private readonly DiscogsMatcher _discogsMatcher;
    private readonly TraxsourceMatcher _traxsourceMatcher;
    private readonly BandcampMatcher _bandcampMatcher;
    private readonly BpmSupremeMatcher _bpmSupremeMatcher;
    private readonly ItunesMatcher _itunesMatcher;
    private readonly SpotifyMatcher _spotifyMatcher;
    private readonly DeezerMatcher _deezerMatcher;
    private readonly LastFmMatcher _lastFmMatcher;
    private readonly BoomplayMatcher _boomplayMatcher;
    private readonly ShazamMatcher _shazamMatcher;
    private readonly ShazamRecognitionService _shazamRecognitionService;
    private readonly AppleLyricsService _appleLyricsService;
    private readonly AppleMusicCatalogService _appleMusicCatalogService;
    private readonly DownloadLyricsService _downloadLyricsService;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ITrackIdentityResolver _trackIdentityResolver;
    private readonly PortedPlatformRegistry? _platformRegistry;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            new MultipleMatchesSortConverter()
        }
    };

    public LocalAutoTagRunner(LocalAutoTagRunnerCollaborators collaborators)
    {
        _logger = collaborators.Logger;
        _httpClientFactory = collaborators.HttpClientFactory;
        _musicBrainzMatcher = collaborators.MusicBrainzMatcher;
        _beatportMatcher = collaborators.BeatportMatcher;
        _discogsMatcher = collaborators.DiscogsMatcher;
        _traxsourceMatcher = collaborators.TraxsourceMatcher;
        _bandcampMatcher = collaborators.BandcampMatcher;
        _bpmSupremeMatcher = collaborators.BpmSupremeMatcher;
        _itunesMatcher = collaborators.ItunesMatcher;
        _spotifyMatcher = collaborators.SpotifyMatcher;
        _deezerMatcher = collaborators.DeezerMatcher;
        _lastFmMatcher = collaborators.LastFmMatcher;
        _boomplayMatcher = collaborators.BoomplayMatcher;
        _shazamMatcher = collaborators.ShazamMatcher;
        _shazamRecognitionService = collaborators.ShazamRecognitionService;
        _appleLyricsService = collaborators.AppleLyricsService;
        _appleMusicCatalogService = collaborators.AppleMusicCatalogService;
        _downloadLyricsService = collaborators.DownloadLyricsService;
        _settingsService = collaborators.SettingsService;
        _serviceScopeFactory = collaborators.ServiceScopeFactory;
        _trackIdentityResolver = collaborators.TrackIdentityResolver;
        _platformRegistry = collaborators.PlatformRegistry;
    }

    public async Task<AutoTagRunResult> RunAsync(
        string jobId,
        string rootPath,
        string configPath,
        Action<TaggingStatusWrap> statusCallback,
        Action<string> logCallback,
        Func<IReadOnlyList<string>, CancellationToken, Task<bool>>? batchCompletedCallback,
        AutoTagResumeCursor? resumeCursor,
        CancellationToken cancellationToken)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _jobTokens[jobId] = linkedCts;
        var token = linkedCts.Token;
        PruneExpiredMatchCaches();
        var jobMatchCache = GetOrCreateMatchCache(jobId);

        try
        {
            var (runPlan, failure) = await PrepareAutoTagRunPlanAsync(jobId, rootPath, configPath, token);
            if (failure != null)
            {
                return failure;
            }

            var plan = runPlan!;
            LogShazamAvailability(plan, logCallback);
            await ExecutePlatformPassesAsync(
                plan,
                jobMatchCache,
                statusCallback,
                logCallback,
                batchCompletedCallback,
                resumeCursor,
                token);
            await ApplyPostLoopFallbackAsync(plan, token);

            return new AutoTagRunResult(true, null);
        }
        catch (OperationCanceledException)
        {
            return new AutoTagRunResult(false, "stopped");
        }
        catch (AutoTagRunPausedException ex)
        {
            return new AutoTagRunResult(false, $"paused: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Local AutoTag run failed.");
            return new AutoTagRunResult(false, ex.ToString());
        }
        finally
        {
            _jobMatchCaches.TryRemove(jobId, out _);
            _jobTokens.TryRemove(jobId, out _);
        }
    }

    private async Task<(AutoTagRunPlan? Plan, AutoTagRunResult? Failure)> PrepareAutoTagRunPlanAsync(
        string jobId,
        string rootPath,
        string configPath,
        CancellationToken token)
    {
        if (!IOFile.Exists(configPath))
        {
            return (null, new AutoTagRunResult(false, "Config not found."));
        }

        var configJson = await IOFile.ReadAllTextAsync(configPath, token);
        var config = NormalizeConfig(JsonSerializer.Deserialize<AutoTagRunnerConfig>(configJson, _jsonOptions));
        var targetPath = string.IsNullOrWhiteSpace(rootPath) ? config.Path : rootPath;
        if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
        {
            return (null, new AutoTagRunResult(false, "Target path not found."));
        }

        var matchingConfig = new AutoTagMatchingConfig
        {
            MatchDuration = config.MatchDuration,
            MaxDurationDifferenceSeconds = config.MaxDurationDifference,
            Strictness = config.Strictness,
            MultipleMatches = config.MultipleMatches,
            PreferredReleaseType = IsManualEnrichment(config)
                ? config.ManualReleasePreference
                : null
        };
        var settings = LoadRuntimeSettings(config.Technical, config);
        settings.DownloadLocation = targetPath;
        var shazamBehavior = ResolveShazamEnrichmentBehavior(config);
        var plan = new AutoTagRunPlan
        {
            JobId = jobId,
            ConfigPath = configPath,
            Config = config,
            TargetPath = targetPath,
            MatchingConfig = matchingConfig,
            EffectivePlatforms = BuildEffectivePlatforms(config, settings),
            PlatformSupportedTags = BuildPlatformSupportedTags(),
            Settings = settings,
            TagSettings = BuildTagSettings(config, settings),
            Files = ResolveTargetFiles(targetPath, config).ToList(),
            ShazamCache = new Dictionary<string, ShazamRecognitionInfo?>(StringComparer.OrdinalIgnoreCase),
            EnableShazamFallback = shazamBehavior.EnableFallback,
            ForceShazamMatch = shazamBehavior.ForceMatch,
            ShazamConflictResolution = IsShazamConflictResolution(config)
        };

        if (config.SkipTagged)
        {
            plan.PreSkippedFiles.UnionWith(plan.Files.Where(HasExistingTags));
        }

        if (IsLibraryWideEnhancementBatchingEnabled(config))
        {
            plan.Files.Sort(CompareLibraryWideEnhancementFiles);
        }

        return (plan, null);
    }

    private void LogShazamAvailability(AutoTagRunPlan plan, Action<string> logCallback)
    {
        var shazamRecognitionAvailable = IsShazamRecognitionAvailable();
        if ((plan.EnableShazamFallback
             || plan.ForceShazamMatch
             || plan.ShazamConflictResolution
             || plan.EffectivePlatforms.Contains(ShazamPlatform, StringComparer.OrdinalIgnoreCase))
            && !shazamRecognitionAvailable)
        {
            logCallback("onetagger_autotag: shazam unavailable");
        }
    }

    private async Task ExecutePlatformPassesAsync(
        AutoTagRunPlan plan,
        JobMatchCacheState jobMatchCache,
        Action<TaggingStatusWrap> statusCallback,
        Action<string> logCallback,
        Func<IReadOnlyList<string>, CancellationToken, Task<bool>>? batchCompletedCallback,
        AutoTagResumeCursor? resumeCursor,
        CancellationToken token)
    {
        var resumeMismatchReason = GetResumeCheckpointMismatchReason(plan, resumeCursor);
        if (!string.IsNullOrWhiteSpace(resumeMismatchReason))
        {
            logCallback($"onetagger_autotag: resume checkpoint adjusted ({resumeMismatchReason})");
        }

        var (startPlatformIndex, startFileIndex) = ResolveResumeStartIndices(
            plan,
            resumeCursor,
            preferPathAnchor: !string.IsNullOrWhiteSpace(resumeMismatchReason));
        if (IsLibraryWideEnhancementBatchingEnabled(plan.Config))
        {
            await ExecuteLibraryWideEnhancementBatchesAsync(
                plan,
                jobMatchCache,
                statusCallback,
                logCallback,
                batchCompletedCallback,
                startPlatformIndex,
                startFileIndex,
                token);
            return;
        }

        for (var platformIndex = startPlatformIndex; platformIndex < plan.PlatformCount; platformIndex++)
        {
            token.ThrowIfCancellationRequested();
            var platform = plan.EffectivePlatforms[platformIndex];
            logCallback($"onetagger_autotag: starting {platform}");

            var fileStart = platformIndex == startPlatformIndex ? startFileIndex : 0;
            for (var fileIndex = fileStart; fileIndex < plan.FileCount; fileIndex++)
            {
                token.ThrowIfCancellationRequested();
                if (plan.ReviewedFiles.Contains(plan.Files[fileIndex]))
                {
                    continue;
                }

                var context = new AutoTagFileRunContext
                {
                    Plan = plan,
                    JobMatchCache = jobMatchCache,
                    Platform = platform,
                    PlatformIndex = platformIndex,
                    FileIndex = fileIndex,
                    File = plan.Files[fileIndex],
                    Progress = ComputeOverallProgress(platformIndex, fileIndex, plan.PlatformCount, plan.FileCount),
                    NextPlatformIndex = ComputeNextPlatformIndex(platformIndex, fileIndex, plan.PlatformCount, plan.FileCount),
                    NextFileIndex = ComputeNextFileIndex(fileIndex, plan.FileCount),
                    StatusCallback = statusCallback,
                    LogCallback = logCallback,
                    Token = token
                };
                await ProcessPlatformFileAsync(context);
            }
        }
    }

    private async Task ExecuteLibraryWideEnhancementBatchesAsync(
        AutoTagRunPlan plan,
        JobMatchCacheState jobMatchCache,
        Action<TaggingStatusWrap> statusCallback,
        Action<string> logCallback,
        Func<IReadOnlyList<string>, CancellationToken, Task<bool>>? batchCompletedCallback,
        int startPlatformIndex,
        int startFileIndex,
        CancellationToken token)
    {
        if (startPlatformIndex >= plan.PlatformCount)
        {
            return;
        }

        var batchSize = Math.Max(1, plan.Config.LibraryWideEnhancementBatchSize ?? DefaultLibraryWideEnhancementBatchSize);
        var resumeBatchStart = startFileIndex - (startFileIndex % batchSize);
        var batchStart = resumeBatchStart;
        for (; batchStart < plan.FileCount; batchStart += batchSize)
        {
            var batchEnd = Math.Min(batchStart + batchSize, plan.FileCount);
            var firstPlatformIndex = batchStart == resumeBatchStart
                ? startPlatformIndex
                : 0;

            for (var platformIndex = firstPlatformIndex; platformIndex < plan.PlatformCount; platformIndex++)
            {
                token.ThrowIfCancellationRequested();
                var platform = plan.EffectivePlatforms[platformIndex];
                logCallback($"onetagger_autotag: starting {platform}");

                var fileStart = batchStart == resumeBatchStart
                    && platformIndex == startPlatformIndex
                        ? startFileIndex
                        : batchStart;
                for (var fileIndex = fileStart; fileIndex < batchEnd; fileIndex++)
                {
                    token.ThrowIfCancellationRequested();
                    if (plan.ReviewedFiles.Contains(plan.Files[fileIndex]))
                    {
                        continue;
                    }

                    var nextPlatformIndex = platformIndex;
                    var nextFileIndex = fileIndex + 1;
                    if (nextFileIndex >= batchEnd)
                    {
                        nextFileIndex = batchStart;
                        nextPlatformIndex += 1;
                    }

                    if (nextPlatformIndex >= plan.PlatformCount)
                    {
                        if (batchEnd >= plan.FileCount)
                        {
                            nextFileIndex = 0;
                            nextPlatformIndex = plan.PlatformCount;
                        }
                        else
                        {
                            nextFileIndex = batchEnd;
                            nextPlatformIndex = 0;
                        }
                    }

                    var context = new AutoTagFileRunContext
                    {
                        Plan = plan,
                        JobMatchCache = jobMatchCache,
                        Platform = platform,
                        PlatformIndex = platformIndex,
                        FileIndex = fileIndex,
                        File = plan.Files[fileIndex],
                        Progress = ComputeBatchOverallProgress(batchStart, batchEnd, platformIndex, fileIndex, plan.PlatformCount, plan.FileCount),
                        NextPlatformIndex = nextPlatformIndex,
                        NextFileIndex = nextFileIndex,
                        StatusCallback = statusCallback,
                        LogCallback = logCallback,
                        Token = token
                    };
                    await ProcessPlatformFileAsync(context);
                }
            }

            if (batchCompletedCallback != null)
            {
                if (await batchCompletedCallback(plan.Files.GetRange(batchStart, batchEnd - batchStart), token))
                {
                    return;
                }
            }
        }
    }

    private static (int PlatformIndex, int FileIndex) ResolveResumeStartIndices(
        AutoTagRunPlan plan,
        AutoTagResumeCursor? resumeCursor,
        bool preferPathAnchor = false)
    {
        if (plan.PlatformCount == 0 || plan.FileCount == 0 || resumeCursor == null)
        {
            return (0, 0);
        }

        var platformIndex = Math.Clamp(resumeCursor.PlatformIndex, 0, plan.PlatformCount - 1);
        var fileIndex = Math.Clamp(resumeCursor.FileIndex, 0, plan.FileCount);
        if (preferPathAnchor
            && !string.IsNullOrWhiteSpace(resumeCursor.LastPath))
        {
            var anchoredFileIndex = plan.Files.FindIndex(file =>
                string.Equals(file, resumeCursor.LastPath, StringComparison.OrdinalIgnoreCase));
            if (anchoredFileIndex >= 0)
            {
                fileIndex = anchoredFileIndex + 1;
            }
        }

        if (fileIndex >= plan.FileCount)
        {
            fileIndex = 0;
            platformIndex += 1;
        }

        if (platformIndex >= plan.PlatformCount)
        {
            return (plan.PlatformCount, 0);
        }

        return (platformIndex, fileIndex);
    }

    private static bool IsLibraryWideEnhancementBatchingEnabled(AutoTagRunnerConfig config)
        => (config.LibraryWideEnhancementBatchSize ?? 0) > 0;

    private static bool IsManualEnrichment(AutoTagRunnerConfig config)
        => !string.IsNullOrWhiteSpace(config.ManualReleasePreference)
           && config.ManualDestinationFolderId is > 0;

    private static bool WantsArtworkFromSettings(AutoTagRunnerConfig config, DeezSpoTagSettings settings)
        => HasAnyTags(config, AlbumArtTag)
           || settings.SaveArtwork
           || settings.EmbedMaxQualityCover;

    private static int CompareLibraryWideEnhancementFiles(string? left, string? right)
    {
        var leftTimestamp = GetLibraryWideEnhancementSortTimestamp(left);
        var rightTimestamp = GetLibraryWideEnhancementSortTimestamp(right);
        var timestampComparison = leftTimestamp.CompareTo(rightTimestamp);
        return timestampComparison != 0
            ? timestampComparison
            : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset GetLibraryWideEnhancementSortTimestamp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DateTimeOffset.MaxValue;
        }

        try
        {
            var creationTime = IOFile.GetCreationTimeUtc(path);
            var writeTime = IOFile.GetLastWriteTimeUtc(path);
            var timestamp = creationTime <= writeTime ? creationTime : writeTime;
            return timestamp == DateTime.MinValue
                ? DateTimeOffset.MaxValue
                : new DateTimeOffset(timestamp, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return DateTimeOffset.MaxValue;
        }
    }

    private static string? GetResumeCheckpointMismatchReason(AutoTagRunPlan plan, AutoTagResumeCursor? resumeCursor)
    {
        if (resumeCursor == null)
        {
            return null;
        }

        if (resumeCursor.PlatformCount is > 0 and var checkpointPlatformCount
            && checkpointPlatformCount != plan.PlatformCount)
        {
            return $"platform count changed (checkpoint={checkpointPlatformCount}, current={plan.PlatformCount})";
        }

        if (resumeCursor.FileCount is > 0 and var checkpointFileCount
            && checkpointFileCount != plan.FileCount)
        {
            return $"file count changed (checkpoint={checkpointFileCount}, current={plan.FileCount})";
        }

        return null;
    }

    private static ProviderTagPlan BuildProviderTagPlan(AutoTagFileRunContext context)
    {
        var configured = context.Plan.Config.Tags
            .Select(tag => tag?.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => SupportedTagMap.TryGetValue(tag!, out var mapped) ? (SupportedTag?)mapped : null)
            .Where(tag => tag.HasValue)
            .Select(tag => tag!.Value)
            .ToHashSet();

        if (context.Plan.PlatformSupportedTags.TryGetValue(context.Platform, out var supported))
        {
            configured.IntersectWith(supported);
        }

        var retained = new HashSet<SupportedTag>();
        var eligible = new HashSet<SupportedTag>();
        try
        {
            using var file = TagLib.File.Create(context.File);
            var extension = Path.GetExtension(context.File);
            foreach (var tag in configured)
            {
                if (!ShouldOverwriteTag(context.Plan.Config, tag)
                    && HasTag(file, extension, tag, context.Plan.Config, context.Platform))
                {
                    retained.Add(tag);
                }
                else
                {
                    eligible.Add(tag);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            eligible.UnionWith(configured);
        }

        return new ProviderTagPlan(configured, eligible, retained);
    }

    private static HashSet<SupportedTag> CapturePresentTags(
        string filePath,
        AutoTagRunnerConfig config,
        string platformId,
        IEnumerable<SupportedTag> tags)
    {
        var present = new HashSet<SupportedTag>();
        using var file = TagLib.File.Create(filePath);
        var extension = Path.GetExtension(filePath);
        foreach (var tag in tags)
        {
            if (HasTag(file, extension, tag, config, platformId))
            {
                present.Add(tag);
            }
        }

        return present;
    }

    private static HashSet<SupportedTag> ResolveReturnedEligibleTags(AutoTagTrack track, ProviderTagPlan plan)
    {
        return CollectAutoTagTags(track)
            .Select(tag => SupportedTagMap.TryGetValue(tag, out var mapped) ? (SupportedTag?)mapped : null)
            .Where(tag => tag.HasValue && plan.Eligible.Contains(tag.Value))
            .Select(tag => tag!.Value)
            .ToHashSet();
    }

    private static HashSet<SupportedTag> VerifyPersistedTags(
        string filePath,
        AutoTagRunnerConfig config,
        string platformId,
        AutoTagTrack track,
        IEnumerable<SupportedTag> expectedTags)
    {
        var missing = new HashSet<SupportedTag>();
        var expected = expectedTags.ToHashSet();
        using var file = TagLib.File.Create(filePath);
        var extension = Path.GetExtension(filePath);
        if (expected.Contains(SupportedTag.Artist)
            && BuildConfiguredTagSet(config.Tags).Contains(ArtistsTag)
            && !string.Equals(
                config.Technical?.MultiArtistSeparator ?? MultiArtistSeparatorDefault,
                MultiArtistSeparatorDefault,
                StringComparison.OrdinalIgnoreCase)
            && track.Artists.Count > 0
            && !HasRawTag(file, extension, "ARTISTS"))
        {
            missing.Add(SupportedTag.Artist);
        }

        foreach (var tag in expected)
        {
            var persisted = tag switch
            {
                SupportedTag.OtherTags => VerifyOtherTagsPersisted(file, extension, track),
                SupportedTag.TtmlLyrics => IOFile.Exists(Path.ChangeExtension(filePath, TtmlExtension)),
                _ => HasTag(file, extension, tag, config, platformId)
            };
            if (!persisted)
            {
                missing.Add(tag);
            }
        }

        return missing;
    }

    private static bool VerifyOtherTagsPersisted(TagLib.File file, string extension, AutoTagTrack track)
    {
        var expectedRawTags = track.Other
            .Where(pair => pair.Value.Count > 0)
            .Where(pair => ShouldPersistOtherRawKey(pair.Key))
            .Select(pair => pair.Key)
            .ToList();
        return expectedRawTags.Count == 0
            || expectedRawTags.All(rawTag => HasRawTag(file, extension, rawTag));
    }

    private static string ToTagKey(SupportedTag tag)
    {
        return tag switch
        {
            SupportedTag.AlbumArt => AlbumArtTag,
            SupportedTag.BPM => BpmTag,
            SupportedTag.ISRC => IsrcTag,
            SupportedTag.URL => UrlTag,
            SupportedTag.TtmlLyrics => TtmlLyricsTag,
            _ => char.ToLowerInvariant(tag.ToString()[0]) + tag.ToString()[1..]
        };
    }

    private async Task ProcessPlatformFileAsync(AutoTagFileRunContext context)
    {
        if (TryHandlePreSkippedFile(context))
        {
            return;
        }

        var tagPlan = BuildProviderTagPlan(context);
        if (tagPlan.Eligible.Count == 0)
        {
            EmitSkippedStatus(
                context,
                "provider has no eligible configured fields for this file",
                outcome: "no_eligible_tags",
                tagPlan: tagPlan);
            return;
        }

        var isManualEnrichment = IsManualEnrichment(context.Plan.Config);
        AutoTagAudioInfo? cachedManualInfo = null;
        var firstManualPass = !isManualEnrichment
            || !context.Plan.ResolvedManualInfo.TryGetValue(context.FileIndex, out cachedManualInfo);
        var validationInfo = firstManualPass
            ? BuildAudioInfo(
                context.File,
                context.Plan.TargetPath,
                context.Plan.Config.ParseFilename,
                context.Plan.Config.TracknameTemplate,
                context.Plan.Config.TitleRegex)
            : CloneAudioInfo(context.Plan.OriginalManualInfo[context.FileIndex]);
        var info = firstManualPass
            ? CloneAudioInfo(validationInfo)
            : CloneAudioInfo(cachedManualInfo!);
        var shazamResult = firstManualPass
            ? TryApplyShazam(
                context.File,
                info,
                context.Plan.Config,
                context.Plan.EnableShazamFallback,
                context.Plan.ForceShazamMatch,
                context.Plan.ShazamCache,
                context.LogCallback,
                context.Token)
            : new ShazamEnrichmentResult(
                context.Plan.ShazamIdentifiedFiles.Contains(context.FileIndex),
                null,
                false);
        var usedShazamForStatus = shazamResult.UsedShazam
            || string.Equals(context.Platform, ShazamPlatform, StringComparison.OrdinalIgnoreCase);

        if (shazamResult.IsFatal)
        {
            throw new AutoTagRunPausedException(shazamResult.Error ?? "Shazam is unavailable.");
        }

        if (isManualEnrichment && shazamResult.FailureKind == ShazamFailureKind.NoMatch)
        {
            EmitReviewStatus(
                context,
                "Shazam could not identify the staged audio file.",
                usedShazamForStatus,
                AutoTagReviewMetadata.FromSourceOnly(validationInfo));
            context.Plan.ReviewedFiles.Add(context.File);
            return;
        }

        if (shazamResult.FailureKind == ShazamFailureKind.NoMatch
            && !string.Equals(context.Platform, ShazamPlatform, StringComparison.OrdinalIgnoreCase))
        {
            context.LogCallback(
                $"onetagger_autotag: shazam could not identify {Path.GetFileName(context.File)}; continuing with {context.Platform}");
        }

        if (isManualEnrichment && firstManualPass)
        {
            context.Plan.OriginalManualInfo[context.FileIndex] = CloneAudioInfo(validationInfo);
            await ApplyCentralIdentityForManualEnrichmentAsync(info, context.Plan.Config, context.LogCallback, context.Token);
            context.Plan.ResolvedManualInfo[context.FileIndex] = CloneAudioInfo(info);
            if (shazamResult.UsedShazam)
            {
                context.Plan.ShazamIdentifiedFiles.Add(context.FileIndex);
            }
        }

        var identityIsTrusted = IsTrustedSourceIdentity(validationInfo, context.File, context.Plan.Config);
        var matchInfo = string.Equals(context.Platform, ShazamPlatform, StringComparison.OrdinalIgnoreCase)
            && identityIsTrusted
            ? validationInfo
            : info;
        var match = await ResolvePlatformMatchAsync(context, matchInfo);
        if (match == null)
        {
            if (string.Equals(context.MatchFailureOutcome, "provider_error", StringComparison.Ordinal))
            {
                EmitErrorStatus(
                    context,
                    context.MatchFailureMessage ?? "provider request failed",
                    usedShazamForStatus,
                    "provider_error",
                    tagPlan);
                return;
            }

            if (isManualEnrichment
                && IsLastPlatform(context)
                && !WasTaggedByAnyPlatform(context))
            {
                EmitReviewStatus(
                    context,
                    $"No {context.Plan.Config.ManualReleasePreference} release could be resolved.",
                    usedShazamForStatus,
                    AutoTagReviewMetadata.FromSourceOnly(validationInfo),
                    context.MatchFailureOutcome ?? "not_in_catalog",
                    tagPlan);
                context.Plan.ReviewedFiles.Add(context.File);
            }
            else
            {
                EmitSkippedStatus(
                    context,
                    context.MatchFailureMessage ?? "no match",
                    usedShazamForStatus,
                    context.MatchFailureOutcome ?? "not_in_catalog",
                    tagPlan);
            }
            return;
        }

        await ApplyResolvedMatchAsync(context, info, validationInfo, match, usedShazamForStatus, tagPlan);
    }

    private async Task ApplyCentralIdentityForManualEnrichmentAsync(
        AutoTagAudioInfo info,
        AutoTagRunnerConfig config,
        Action<string> logCallback,
        CancellationToken token)
    {
        var resolution = await _trackIdentityResolver.ResolveAsync(
            new TrackIdentityResolutionRequest(
                SourcePlatform: ShazamPlatform,
                SourceUrl: AutoTagTagValueReader.ReadFirstTagValue(info, "URL", WwwAudioFileTag),
                Title: info.Title,
                Artist: info.Artists.FirstOrDefault() ?? info.Artist,
                Album: info.Album,
                Isrc: info.Isrc,
                DurationMs: info.DurationSeconds is > 0 ? info.DurationSeconds.Value * 1000 : null,
                TargetPlatforms: ["spotify", "deezer", "apple", "qobuz", "tidal", "amazon"],
                PreferredReleaseType: config.ManualReleasePreference),
            token);

        info.Title = FirstNonEmpty(resolution.Title, info.Title) ?? info.Title;
        info.Artist = FirstNonEmpty(resolution.Artist, info.Artist) ?? info.Artist;
        info.Album = FirstNonEmpty(resolution.Album, info.Album);
        info.Isrc = FirstNonEmpty(resolution.Isrc, info.Isrc);
        var spotifyUrl = FirstNonEmpty(
            NormalizeSpotifyTrackUrl(resolution.SpotifyUrl),
            NormalizeSpotifyTrackUrl(AutoTagTagValueReader.ReadFirstTagValue(info, "SHAZAM_SPOTIFY_URL")),
            NormalizeSpotifyTrackUrl(resolution.SpotifyId));
        var spotifyId = FirstNonEmpty(resolution.SpotifyId, ExtractSpotifyTrackIdFromTags(info.Tags));
        AddResolvedIdentity(info, SpotifyTrackIdTag, spotifyId);
        AddResolvedIdentity(info, SpotifyUrlTag, spotifyUrl);
        AddResolvedIdentity(info, DeezerTrackIdTag, resolution.DeezerId);
        AddResolvedIdentity(info, "DEEZER_URL", resolution.DeezerUrl);
        AddResolvedIdentity(info, "ITUNES_TRACK_ID", resolution.AppleId);
        AddResolvedIdentity(info, "APPLE_MUSIC_TRACK_ID", resolution.AppleId);
        AddResolvedIdentity(info, "APPLE_MUSIC_URL", resolution.AppleUrl);
        AddResolvedIdentity(info, "QOBUZ_TRACK_ID", resolution.QobuzId);
        AddResolvedIdentity(info, "TIDAL_TRACK_ID", resolution.TidalId);
        AddResolvedIdentity(info, "AMAZON_TRACK_ID", resolution.AmazonId);
        logCallback(
            $"onetagger_autotag: central identity resolved spotify={HasValue(resolution.SpotifyId)}, deezer={HasValue(resolution.DeezerId)}, apple={HasValue(resolution.AppleId)}, qobuz={HasValue(resolution.QobuzId)}, tidal={HasValue(resolution.TidalId)}, amazon={HasValue(resolution.AmazonId)}");
    }

    private static void AddResolvedIdentity(AutoTagAudioInfo info, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            info.Tags[key] = [value.Trim()];
        }
    }

    private static string HasValue(string? value) => string.IsNullOrWhiteSpace(value) ? "no" : "yes";

    private static bool IsLastPlatform(AutoTagFileRunContext context)
        => context.PlatformIndex == context.Plan.PlatformCount - 1;

    private static bool WasTaggedByAnyPlatform(AutoTagFileRunContext context)
        => context.Plan.TaggedFileIndices.Contains(context.FileIndex);

    private static bool TryHandlePreSkippedFile(AutoTagFileRunContext context)
    {
        if (!context.Plan.PreSkippedFiles.Contains(context.File))
        {
            return false;
        }

        if (context.PlatformIndex == 0)
        {
            EmitSkippedStatus(context, "already tagged");
        }

        return true;
    }

    private async Task<AutoTagMatchResult?> ResolvePlatformMatchAsync(
        AutoTagFileRunContext context,
        AutoTagAudioInfo info)
    {
        var useMatchCache = CanUseMatchCache(info);
        var matchCacheKey = useMatchCache
            ? BuildMatchCacheKey(context.Platform, info, context.Plan.Config, context.Plan.Settings, context.Plan.MatchingConfig)
            : string.Empty;
        if (IsPlatformUnavailable(context.JobMatchCache, context.Platform))
        {
            context.MatchFailureOutcome = "provider_unavailable";
            context.MatchFailureMessage = $"{context.Platform} skipped after earlier match timeout";
            context.LogCallback(
                $"onetagger_autotag: {context.Platform} skipped; platform unavailable after earlier match timeout");
            return null;
        }

        if (useMatchCache && TryGetCachedMatch(context.JobMatchCache, matchCacheKey, out var cachedMatch))
        {
            PreserveAtmosFileIsrc(context.File, info, cachedMatch?.Track);
            return cachedMatch;
        }

        AutoTagMatchResult? match;
        context.MatchFailureOutcome = null;
        context.MatchFailureMessage = null;
        try
        {
            match = await RunPlatformMatchWithTimeoutAsync(
                context,
                CreateCatalogLookupInfo(context.File, info),
                new PlatformMatchContext
                {
                    FilePath = context.File,
                    Config = context.Plan.Config,
                    Settings = context.Plan.Settings,
                    MatchingConfig = context.Plan.MatchingConfig,
                    ShazamCache = context.Plan.ShazamCache,
                    IsManualEnrichment = IsManualEnrichment(context.Plan.Config)
                });
            if (match == null)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag platform {Platform} failed for {File}", SanitizeLogValue(context.Platform), SanitizeLogValue(context.File));
            context.MatchFailureOutcome = IsProviderNotConfigured(ex) ? "not_configured" : "provider_error";
            context.MatchFailureMessage = ex.Message;
            return null;
        }

        PreserveAtmosFileIsrc(context.File, info, match.Track);
        if (useMatchCache)
        {
            StoreCachedMatch(context.JobMatchCache, matchCacheKey, match);
        }

        return match;
    }

    private async Task<AutoTagMatchResult?> RunPlatformMatchWithTimeoutAsync(
        AutoTagFileRunContext context,
        AutoTagAudioInfo info,
        PlatformMatchContext matchContext)
    {
        context.LogCallback($"onetagger_autotag: {context.Platform} match starting");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(context.Token);
        var matchTask = MatchPlatformAsync(
            context.Platform,
            info,
            matchContext,
            timeoutSource.Token);

        try
        {
            var match = await matchTask.WaitAsync(PlatformMatchTimeout, context.Token);
            context.LogCallback($"onetagger_autotag: {context.Platform} match completed");
            return match;
        }
        catch (TimeoutException)
        {
            timeoutSource.Cancel();
            ObserveBackgroundTask(matchTask);
            MarkPlatformUnavailable(context.JobMatchCache, context.Platform);
            context.MatchFailureOutcome = "provider_error";
            context.MatchFailureMessage = $"provider timed out after {PlatformMatchTimeout.TotalSeconds:0}s";
            context.LogCallback(
                $"onetagger_autotag: {context.Platform} match timed out after {PlatformMatchTimeout.TotalSeconds:0}s; skipping remaining {context.Platform} matches in this run");
            return null;
        }
    }

    private static bool IsProviderNotConfigured(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is not InvalidOperationException)
            {
                continue;
            }

            var message = current.Message;
            if (message.Contains("not configured", StringComparison.OrdinalIgnoreCase)
                || message.Contains("must be connected", StringComparison.OrdinalIgnoreCase)
                || message.Contains("credentials are required", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task ApplyResolvedMatchAsync(
        AutoTagFileRunContext context,
        AutoTagAudioInfo info,
        AutoTagAudioInfo validationInfo,
        AutoTagMatchResult match,
        bool usedShazamForStatus,
        ProviderTagPlan tagPlan)
    {
        var isManualEnrichment = IsManualEnrichment(context.Plan.Config);
        if (string.Equals(context.Platform, BoomplayPlatform, StringComparison.OrdinalIgnoreCase))
        {
            var boomplayGuardReason = EvaluateBoomplayReliabilityGuard(validationInfo, match, context.Plan.MatchingConfig);
            if (!string.IsNullOrWhiteSpace(boomplayGuardReason))
            {
                EmitSkippedStatus(context, boomplayGuardReason, usedShazamForStatus, "rejected", tagPlan);
                return;
            }
        }

        if (isManualEnrichment
            && !AutoTagReleaseCategory.MatchesPreference(
                match.Track.ReleaseType,
                match.Track.TrackTotal,
                context.Plan.Config.ManualReleasePreference))
        {
            HandleRejectedManualRelease(
                context,
                info,
                match.Track,
                $"Provider returned a {match.Track.ReleaseType ?? "different"} release instead of the requested {context.Plan.Config.ManualReleasePreference} release.",
                usedShazamForStatus,
                tagPlan,
                match);
            return;
        }

        context.Plan.FrozenManualReleases.TryGetValue(context.FileIndex, out var frozenRelease);
        if (isManualEnrichment && frozenRelease != null)
        {
            var frozenMismatch = EvaluateGlobalMismatchGuard(
                frozenRelease.ToAudioInfo(),
                match,
                context.Plan.MatchingConfig);
            if (!string.IsNullOrWhiteSpace(frozenMismatch)
                || !AlbumsReferToSameRelease(frozenRelease.Album, match.Track.Album))
            {
                EmitSkippedStatus(
                    context,
                    frozenMismatch ?? "provider release conflicts with the frozen manual-enrichment release",
                    usedShazamForStatus,
                    outcome: "rejected",
                    tagPlan: tagPlan);
                return;
            }

            frozenRelease.ApplyTo(match.Track);
        }

        var identityIsTrusted = IsTrustedSourceIdentity(validationInfo, context.File, context.Plan.Config);
        var validationBasis = isManualEnrichment
            || (usedShazamForStatus
                && !string.Equals(context.Platform, ShazamPlatform, StringComparison.OrdinalIgnoreCase))
            ? info
            : validationInfo;
        var mismatchReason = EvaluateGlobalMismatchGuard(
            validationBasis,
            match,
            context.Plan.MatchingConfig,
            context.File,
            treatSourceAsUntrusted: !identityIsTrusted);
        if (!string.IsNullOrWhiteSpace(mismatchReason))
        {
            if (string.Equals(context.Platform, ShazamPlatform, StringComparison.OrdinalIgnoreCase))
            {
                EmitReviewStatus(
                    context,
                    mismatchReason,
                    usedShazamForStatus,
                    AutoTagReviewMetadata.FromMatch(validationInfo, match.Track),
                    "rejected",
                    tagPlan,
                    match);
                context.Plan.ReviewedFiles.Add(context.File);
                return;
            }

            EmitSkippedStatus(context, mismatchReason, usedShazamForStatus, "rejected", tagPlan);
            return;
        }

        EmitTaggingStatus(context, match.Accuracy, usedShazamForStatus);

        try
        {
            var originalFile = context.File;
            PreserveRicherArtistCreditsFromSource(info, match.Track, context.Plan.Settings);
            ApplyFolderContextGuards(context.File, context.Plan.TargetPath, match.Track);
            ApplyAlbumIdentityConsensus(context, match.Track);
            if (isManualEnrichment && frozenRelease == null)
            {
                frozenRelease = ManualReleaseIdentity.FromTrack(match.Track);
                context.Plan.FrozenManualReleases[context.FileIndex] = frozenRelease;
            }
            if (!isManualEnrichment
                || !context.Plan.MaterializedManualPaths.TryGetValue(context.FileIndex, out var materializedPath))
            {
                materializedPath = MaterializeFileToTemplatePath(
                    context.File,
                    match.Track,
                    context.Plan.Config,
                    context.Plan.Settings,
                    context.Plan.TagSettings);
                if (isManualEnrichment)
                {
                    context.Plan.MaterializedManualPaths[context.FileIndex] = materializedPath;
                    PersistManualMaterializedTargetPath(
                        context.Plan,
                        context.File,
                        materializedPath);
                }
            }
            context.File = materializedPath;
            context.Plan.Files[context.FileIndex] = context.File;
            var presentBefore = CapturePresentTags(context.File, context.Plan.Config, context.Platform, tagPlan.Eligible);
            await RunBoundedOptionalStepAsync(
                context,
                "artwork fallback",
                ArtworkFallbackTimeout,
                stepToken => EnsureArtworkFallbackAsync(context, info, match.Track, stepToken));
            if (isManualEnrichment && frozenRelease != null)
            {
                frozenRelease.Art ??= match.Track.Art;
                frozenRelease.ApplyTo(match.Track);
            }
            if (ShouldRequestAnyLyrics(context.Plan.Config, context.Plan.Settings))
            {
                await RunBoundedOptionalStepAsync(
                    context,
                    "lyrics",
                    LyricsResolutionTimeout,
                    stepToken => PopulatePlatformLyricsAsync(
                        context.Platform,
                        context.File,
                        match.Track,
                        context.Plan.Config,
                        context.Plan.Settings,
                        stepToken));
            }
            if (context.Plan.AttemptedAppleExtras.Add(context.FileIndex))
            {
                await RunBoundedOptionalStepAsync(
                    context,
                    "Apple extras",
                    AppleExtrasTimeout,
                    stepToken => PopulateAppleExtrasAsync(
                        context.Platform,
                        context.File,
                        match.Track,
                        context.Plan.Config,
                        context.Plan.Settings,
                        stepToken));
            }
            var writeResult = await TagFileAsync(
                context.File,
                match.Track,
                context.Plan.TagSettings,
                context.Plan.Config,
                context.Plan.Settings,
                context.Platform,
                context.Token);
            var returnedTags = ResolveReturnedEligibleTags(match.Track, tagPlan);
            returnedTags.IntersectWith(writeResult.AttemptedTags);
            var persistenceFailures = VerifyPersistedTags(
                context.File,
                context.Plan.Config,
                context.Platform,
                match.Track,
                returnedTags);
            if (persistenceFailures.Remove(SupportedTag.AlbumArt))
            {
                returnedTags.Remove(SupportedTag.AlbumArt);
                context.LogCallback(
                    $"onetagger_autotag: {context.Platform} artwork was not persisted; retaining provider metadata and reporting artwork as missing");
            }
            if (persistenceFailures.Remove(SupportedTag.OtherTags))
            {
                returnedTags.Remove(SupportedTag.OtherTags);
                context.LogCallback(
                    $"onetagger_autotag: {context.Platform} extra tags were not persisted; identity tags were kept");
            }
            if (persistenceFailures.Count > 0)
            {
                throw new IOException($"Metadata persistence verification failed for: {string.Join(", ", persistenceFailures.Select(ToTagKey))}.");
            }
            if (isManualEnrichment)
            {
                await EnsureManualArtistArtworkAsync(context, info, match.Track);
            }
            context.Plan.TaggedByAnyPlatform.Add(originalFile);
            context.Plan.TaggedByAnyPlatform.Add(context.File);
            context.Plan.TaggedFileIndices.Add(context.FileIndex);
            var writtenTags = returnedTags
                .Where(tag => ShouldOverwriteTag(context.Plan.Config, tag) || !presentBefore.Contains(tag))
                .ToHashSet();
            var missingTags = tagPlan.Eligible
                .Where(tag => !returnedTags.Contains(tag))
                .ToHashSet();
            var outcome = writtenTags.Count > 0 ? "tagged" : "matched_no_changes";
            EmitTaggedStatus(
                context,
                match.Accuracy,
                usedShazamForStatus,
                outcome,
                tagPlan,
                match,
                returnedTags,
                writtenTags,
                missingTags);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag failed for {File} on {Platform}", SanitizeLogValue(context.File), SanitizeLogValue(context.Platform));
            EmitErrorStatus(context, ex.Message, usedShazamForStatus, "provider_error", tagPlan, match);
        }
    }

    private async Task EnsureManualArtistArtworkAsync(
        AutoTagFileRunContext context,
        AutoTagAudioInfo identity,
        AutoTagTrack track)
    {
        if (!context.Plan.Settings.SaveArtworkArtist)
        {
            return;
        }

        var coreTrack = BuildCoreTrack(
            track,
            ResolveArtistSeparator(context.Plan.Config, context.File),
            context.Plan.TagSettings.SingleAlbumArtist,
            context.Plan.Settings);
        var artistPath = BuildTemplatePathInfo(coreTrack, context.Plan.Settings).ArtistPath;
        if (string.IsNullOrWhiteSpace(artistPath))
        {
            return;
        }

        var artworkKey = Path.GetFullPath(artistPath);
        if (!context.Plan.AttemptedArtistArtworkPaths.Add(artworkKey))
        {
            return;
        }

        using var scope = _serviceScopeFactory.CreateScope();
        var provider = scope.ServiceProvider;
        var artist = track.Artists.FirstOrDefault();
        var artistArtwork = await DownloadEngineArtworkHelper.ResolveArtistArtworkAsync(
            new DownloadEngineArtworkHelper.ArtistImageResolveRequest(
                _appleMusicCatalogService,
                _httpClientFactory,
                context.Plan.Settings,
                provider.GetService<DeezSpoTag.Integrations.Deezer.DeezerClient>(),
                provider.GetService<DeezSpoTag.Services.Download.ISpotifyArtworkResolver>(),
                provider.GetService<DeezSpoTag.Services.Download.ILastFmArtistImageResolver>(),
                AutoTagIdentityTags.ReadAppleTrackId(identity),
                AutoTagTagValueReader.ReadFirstTagValue(identity, DeezerTrackIdTag),
                AutoTagTagValueReader.ReadFirstTagValue(identity, SpotifyTrackIdTag),
                artist,
                _logger)
            {
                AppleArtistId = AutoTagIdentityTags.ReadAppleArtistId(identity)
                    ?? (context.Platform is "itunes" or "apple" or "applemusic" ? track.ArtistId : null),
                DeezerArtistId = string.Equals(context.Platform, "deezer", StringComparison.OrdinalIgnoreCase)
                    ? track.ArtistId
                    : null,
                SpotifyArtistId = string.Equals(context.Platform, "spotify", StringComparison.OrdinalIgnoreCase)
                    ? track.ArtistId
                    : null
            },
            context.Token);
        if (artistArtwork == null)
        {
            return;
        }

        _logger.LogInformation(
            "Artist artwork resolved from {Provider} using {ResolutionMethod} for {Artist}",
            artistArtwork.Provider,
            artistArtwork.ResolutionMethod,
            artist);

        _ = await DownloadEngineArtworkHelper.SaveArtistArtworkAsync(
            new DownloadEngineArtworkHelper.SaveArtistArtworkRequest(
                provider.GetRequiredService<ImageDownloader>(),
                provider.GetRequiredService<EnhancedPathTemplateProcessor>(),
                artistPath,
                artistArtwork.Url,
                context.Plan.Settings,
                coreTrack,
                AppleQueueHelpers.GetAppleArtworkSize(context.Plan.Settings),
                context.Plan.Settings.EmbedMaxQualityCover,
                _logger),
            context.Token);
    }

    private static void HandleRejectedManualRelease(
        AutoTagFileRunContext context,
        AutoTagAudioInfo source,
        AutoTagTrack candidate,
        string reason,
        bool usedShazam,
        ProviderTagPlan tagPlan,
        AutoTagMatchResult match)
    {
        if (IsLastPlatform(context) && !WasTaggedByAnyPlatform(context))
        {
            EmitReviewStatus(
                context,
                reason,
                usedShazam,
                AutoTagReviewMetadata.FromMatch(source, candidate),
                "rejected",
                tagPlan,
                match);
            context.Plan.ReviewedFiles.Add(context.File);
            return;
        }

        EmitSkippedStatus(context, reason, usedShazam, "rejected", tagPlan, match);
    }

    private static bool AlbumsReferToSameRelease(string? frozen, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(frozen) || string.IsNullOrWhiteSpace(candidate))
        {
            return true;
        }

        return string.Equals(
            AutoTagSimilarity.NormalizeText(frozen),
            AutoTagSimilarity.NormalizeText(candidate),
            StringComparison.Ordinal);
    }

    private async Task RunBoundedOptionalStepAsync(
        AutoTagFileRunContext context,
        string stepName,
        TimeSpan timeout,
        Func<CancellationToken, Task> action)
    {
        context.LogCallback($"onetagger_autotag: {context.Platform} {stepName} starting");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(context.Token);
        var stepTask = action(timeoutSource.Token);
        try
        {
            await stepTask.WaitAsync(timeout, context.Token);
            context.LogCallback($"onetagger_autotag: {context.Platform} {stepName} completed");
        }
        catch (TimeoutException)
        {
            timeoutSource.Cancel();
            ObserveBackgroundTask(stepTask);
            context.LogCallback(
                $"onetagger_autotag: {context.Platform} {stepName} timed out after {timeout.TotalSeconds:0}s; continuing");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Optional AutoTag step {Step} failed for {File} on {Platform}; continuing with provider metadata.",
                SanitizeLogValue(stepName),
                SanitizeLogValue(context.File),
                SanitizeLogValue(context.Platform));
            context.LogCallback(
                $"onetagger_autotag: {context.Platform} optional {stepName} failed; continuing with provider metadata");
        }
    }

    private static void ObserveBackgroundTask(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string? EvaluateGlobalMismatchGuard(
        AutoTagAudioInfo info,
        AutoTagMatchResult match,
        AutoTagMatchingConfig matchingConfig)
        => EvaluateGlobalMismatchGuard(info, match, matchingConfig, filePath: null, treatSourceAsUntrusted: false);

    private static string? EvaluateGlobalMismatchGuard(
        AutoTagAudioInfo info,
        AutoTagMatchResult match,
        AutoTagMatchingConfig matchingConfig,
        string? filePath,
        bool treatSourceAsUntrusted)
    {
        if (match.Track == null)
        {
            return null;
        }

        if (treatSourceAsUntrusted
            || TrackIdentityTrust.IsUntrustedIdentity(info.Title, info.Artist, filePath))
        {
            return null;
        }

        var incomingFullTitle = OneTaggerMatching.FullTitle(match.Track.Title, match.Track.Version);
        if (TrackTitleMatcher.HasVersionDrift(info.Title, incomingFullTitle))
        {
            return "match rejected by quality guard (version drift)";
        }

        if (IsAuthoritativeIdMatch(match.MatchStrategy))
        {
            return null;
        }

        if (HasMatchingIsrc(info.Isrc, match.Track.Isrc))
        {
            return null;
        }

        if (!TrackTitleMatcher.HasCompatibleTitleIdentity(info.Title, incomingFullTitle))
        {
            return "match rejected by quality guard (title identity)";
        }

        List<string> sourceArtists;
        if (info.Artists.Count > 0)
        {
            sourceArtists = info.Artists;
        }
        else if (string.IsNullOrWhiteSpace(info.Artist))
        {
            sourceArtists = [];
        }
        else
        {
            sourceArtists = [info.Artist];
        }
        var incomingArtists = match.Track.Artists ?? new List<string>();

        var artistStrictness = Math.Clamp(matchingConfig.Strictness - 0.05d, 0.45d, 0.95d);
        var artistCompatible = sourceArtists.Count == 0
            || incomingArtists.Count == 0
            || AreArtistIdentitiesCompatibleForOverwrite(sourceArtists, incomingArtists, artistStrictness);

        if (!artistCompatible)
        {
            return "match rejected by quality guard (artist mismatch)";
        }

        var sourceTitle = AutoTagSimilarity.NormalizeText(OneTaggerMatching.CleanTitleMatching(info.Title));
        var incomingTitle = AutoTagSimilarity.NormalizeText(
            OneTaggerMatching.CleanTitleMatching(
                OneTaggerMatching.FullTitle(match.Track.Title, match.Track.Version)));
        var titleSimilarity = AutoTagSimilarity.ComputeScore(sourceTitle, incomingTitle);

        var durationMismatch = HasDurationMismatch(info.DurationSeconds, match.Track.Duration, matchingConfig.MaxDurationDifferenceSeconds);
        var minTitleSimilarity = Math.Clamp(matchingConfig.Strictness - 0.08d, 0.62d, 0.95d);

        if (titleSimilarity < minTitleSimilarity)
        {
            return $"match rejected by quality guard (title similarity {titleSimilarity:0.000} < {minTitleSimilarity:0.000})";
        }

        if (durationMismatch && titleSimilarity < 0.90d)
        {
            return "match rejected by quality guard (duration mismatch)";
        }

        return null;
    }

    private static bool IsAuthoritativeIdMatch(string? matchStrategy)
        => (matchStrategy ?? string.Empty).Trim().ToLowerInvariant() is "id" or "id_first";

    private static string? EvaluateBoomplayReliabilityGuard(
        AutoTagAudioInfo info,
        AutoTagMatchResult match,
        AutoTagMatchingConfig matchingConfig)
    {
        if (match.Track == null)
        {
            return "match rejected by Boomplay guard (missing track payload)";
        }

        List<string> sourceArtists;
        if (info.Artists.Count > 0)
        {
            sourceArtists = info.Artists;
        }
        else if (string.IsNullOrWhiteSpace(info.Artist))
        {
            sourceArtists = [];
        }
        else
        {
            sourceArtists = [info.Artist];
        }

        var incomingArtists = match.Track.Artists ?? new List<string>();
        var artistStrictness = Math.Clamp(matchingConfig.Strictness + 0.12d, 0.80d, 0.98d);
        var artistCompatible = sourceArtists.Count > 0
            && incomingArtists.Count > 0
            && AreArtistIdentitiesCompatibleForOverwrite(sourceArtists, incomingArtists, artistStrictness);
        if (!artistCompatible)
        {
            return "match rejected by Boomplay guard (artist mismatch)";
        }

        var hasMatchingIsrc = HasMatchingIsrc(info.Isrc, match.Track.Isrc);
        var minAccuracy = Math.Clamp(matchingConfig.Strictness + 0.10d, 0.80d, 0.99d);
        if (!hasMatchingIsrc && match.Accuracy < minAccuracy)
        {
            return $"match rejected by Boomplay guard (accuracy {match.Accuracy:0.000} < {minAccuracy:0.000})";
        }

        var incomingFullTitle = OneTaggerMatching.FullTitle(match.Track.Title, match.Track.Version);
        if (TrackTitleMatcher.HasVersionDrift(info.Title, incomingFullTitle))
        {
            return "match rejected by Boomplay guard (version drift)";
        }

        if (!hasMatchingIsrc && !TrackTitleMatcher.HasCompatibleTitleIdentity(info.Title, incomingFullTitle))
        {
            return "match rejected by Boomplay guard (title identity)";
        }

        var sourceTitle = AutoTagSimilarity.NormalizeText(OneTaggerMatching.CleanTitleMatching(info.Title));
        var incomingTitle = AutoTagSimilarity.NormalizeText(
            OneTaggerMatching.CleanTitleMatching(incomingFullTitle));
        if (string.IsNullOrWhiteSpace(sourceTitle) || string.IsNullOrWhiteSpace(incomingTitle))
        {
            return hasMatchingIsrc ? null : "match rejected by Boomplay guard (insufficient title evidence)";
        }

        var titleSimilarity = AutoTagSimilarity.ComputeScore(sourceTitle, incomingTitle);
        var minTitleSimilarity = Math.Clamp(matchingConfig.Strictness + 0.10d, 0.82d, 0.98d);
        if (titleSimilarity < minTitleSimilarity)
        {
            return $"match rejected by Boomplay guard (title similarity {titleSimilarity:0.000} < {minTitleSimilarity:0.000})";
        }

        if (HasDurationMismatch(info.DurationSeconds, match.Track.Duration, matchingConfig.MaxDurationDifferenceSeconds))
        {
            return "match rejected by Boomplay guard (duration mismatch)";
        }

        return null;
    }

    private static bool AreArtistIdentitiesCompatibleForOverwrite(
        IReadOnlyList<string> sourceArtists,
        IReadOnlyList<string> incomingArtists,
        double strictness)
    {
        var normalizedSource = SplitArtistCredits(sourceArtists)
            .Select(NormalizeArtistIdentity)
            .Where(artist => !string.IsNullOrWhiteSpace(artist))
            .ToList();
        var normalizedIncoming = SplitArtistCredits(incomingArtists)
            .Select(NormalizeArtistIdentity)
            .Where(artist => !string.IsNullOrWhiteSpace(artist))
            .ToList();

        if (normalizedSource.Count == 0 || normalizedIncoming.Count == 0)
        {
            return true;
        }

        if (normalizedSource.Any(source => normalizedIncoming.Contains(source, StringComparer.Ordinal)))
        {
            return true;
        }

        var sourceJoined = string.Join(" ", normalizedSource);
        var incomingJoined = string.Join(" ", normalizedIncoming);
        var similarity = AutoTagSimilarity.ComputeScore(sourceJoined, incomingJoined);
        return similarity >= Math.Clamp(strictness + 0.15d, 0.80d, 0.98d);
    }

    private static string NormalizeArtistIdentity(string value)
    {
        return AutoTagSimilarity.NormalizeText(value);
    }

    private static AutoTagAudioInfo CreateCatalogLookupInfo(string filePath, AutoTagAudioInfo source)
        => CreateCatalogLookupInfo(IsLocalAtmosFile(filePath), source);

    private static AutoTagAudioInfo CreateCatalogLookupInfo(bool localFileIsAtmos, AutoTagAudioInfo source)
    {
        if (!localFileIsAtmos)
        {
            return source;
        }

        var lookup = CloneAudioInfo(source);
        lookup.Isrc = ReadFirstTagValue(lookup.Tags, "SHAZAM_ISRC");
        return lookup;
    }

    private static void PreserveAtmosFileIsrc(string filePath, AutoTagAudioInfo source, AutoTagTrack? incoming)
        => PreserveAtmosFileIsrc(IsLocalAtmosFile(filePath), source, incoming);

    private static void PreserveAtmosFileIsrc(bool localFileIsAtmos, AutoTagAudioInfo source, AutoTagTrack? incoming)
    {
        if (incoming == null || !localFileIsAtmos)
        {
            return;
        }

        incoming.Isrc = source.Isrc;
    }

    private static bool HasMatchingIsrc(string? sourceIsrc, string? incomingIsrc)
    {
        if (string.IsNullOrWhiteSpace(sourceIsrc) || string.IsNullOrWhiteSpace(incomingIsrc))
        {
            return false;
        }

        return string.Equals(sourceIsrc.Trim(), incomingIsrc.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasDurationMismatch(int? sourceDurationSeconds, TimeSpan? incomingDuration, int maxDifferenceSeconds)
    {
        if (!sourceDurationSeconds.HasValue || !incomingDuration.HasValue || sourceDurationSeconds.Value <= 0 || incomingDuration.Value <= TimeSpan.Zero)
        {
            return false;
        }

        var incomingSeconds = (int)Math.Round(incomingDuration.Value.TotalSeconds);
        return Math.Abs(sourceDurationSeconds.Value - incomingSeconds) > Math.Max(1, maxDifferenceSeconds);
    }

    private async Task EnsureArtworkFallbackAsync(
        AutoTagFileRunContext context,
        AutoTagAudioInfo info,
        AutoTagTrack track,
        CancellationToken token)
    {
        if (!WantsArtworkFromSettings(context.Plan.Config, context.Plan.Settings)
            || !string.IsNullOrWhiteSpace(track.Art))
        {
            return;
        }

        var providerOrder = ArtworkFallbackHelper.ResolveOrder(context.Plan.Settings);
        if (providerOrder.Count == 0)
        {
            return;
        }

        foreach (var platform in providerOrder
            .Select(ResolveArtworkFallbackPlatform)
            .Where(platform => !string.IsNullOrWhiteSpace(platform)
                && !string.Equals(platform, context.Platform, StringComparison.OrdinalIgnoreCase)))
        {
            AutoTagMatchResult? fallbackMatch;
            try
            {
                fallbackMatch = await MatchPlatformAsync(
                    platform!,
                    info,
                    new PlatformMatchContext
                    {
                        FilePath = context.File,
                        Config = context.Plan.Config,
                        Settings = context.Plan.Settings,
                        MatchingConfig = context.Plan.MatchingConfig,
                        ShazamCache = context.Plan.ShazamCache,
                        IsManualEnrichment = IsManualEnrichment(context.Plan.Config)
                    },
                    token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogArtworkFallbackMatchFailure(ex, context.File, platform!);
                continue;
            }

            var fallbackArt = fallbackMatch?.Track?.Art;
            if (string.IsNullOrWhiteSpace(fallbackArt))
            {
                continue;
            }
            if (IsManualEnrichment(context.Plan.Config)
                && (!AutoTagReleaseCategory.MatchesPreference(
                        fallbackMatch!.Track.ReleaseType,
                        fallbackMatch.Track.TrackTotal,
                        context.Plan.Config.ManualReleasePreference)
                    || (context.Plan.FrozenManualReleases.TryGetValue(context.FileIndex, out var frozenRelease)
                        && !AlbumsReferToSameRelease(frozenRelease.Album, fallbackMatch.Track.Album))))
            {
                continue;
            }

            track.Art = fallbackArt;
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Artwork fallback resolved for {File} via {Platform}.", SanitizeLogValue(context.File), SanitizeLogValue(platform));
            }

            return;
        }
    }

    private void LogArtworkFallbackMatchFailure(Exception ex, string filePath, string platform)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(ex, "Artwork fallback match failed for {File} using {Platform}.", SanitizeLogValue(filePath), SanitizeLogValue(platform));
        }
    }

    private static string? ResolveArtworkFallbackPlatform(string provider)
    {
        var normalized = provider?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "apple" => ItunesPlatform,
            "deezer" => DeezerPlatform,
            SpotifyPlatform => SpotifyPlatform,
            _ => null
        };
    }

    private static void EmitSkippedStatus(
        AutoTagFileRunContext context,
        string message,
        bool usedShazam = false,
        string? outcome = null,
        ProviderTagPlan? tagPlan = null,
        AutoTagMatchResult? match = null)
    {
        EmitStatus(context, "skipped", message, null, usedShazam, outcome: outcome, tagPlan: tagPlan, match: match);
    }

    private static void EmitErrorStatus(
        AutoTagFileRunContext context,
        string message,
        bool usedShazam,
        string? outcome = null,
        ProviderTagPlan? tagPlan = null,
        AutoTagMatchResult? match = null)
    {
        EmitStatus(context, "error", message, null, usedShazam, outcome: outcome, tagPlan: tagPlan, match: match);
    }

    private static void EmitReviewStatus(
        AutoTagFileRunContext context,
        string message,
        bool usedShazam,
        AutoTagReviewMetadata? review,
        string? outcome = null,
        ProviderTagPlan? tagPlan = null,
        AutoTagMatchResult? match = null)
    {
        EmitStatus(context, "review", message, null, usedShazam, review, outcome, tagPlan, match);
    }

    private static void EmitTaggingStatus(AutoTagFileRunContext context, double? accuracy, bool usedShazam)
    {
        EmitStatus(context, "tagging", null, accuracy, usedShazam);
    }

    private static void EmitTaggedStatus(
        AutoTagFileRunContext context,
        double? accuracy,
        bool usedShazam,
        string? outcome = null,
        ProviderTagPlan? tagPlan = null,
        AutoTagMatchResult? match = null,
        IReadOnlyCollection<SupportedTag>? returnedTags = null,
        IReadOnlyCollection<SupportedTag>? writtenTags = null,
        IReadOnlyCollection<SupportedTag>? missingTags = null)
    {
        EmitStatus(
            context,
            "tagged",
            outcome == "matched_no_changes" ? "provider matched but supplied no new eligible values" : null,
            accuracy,
            usedShazam,
            outcome: outcome,
            tagPlan: tagPlan,
            match: match,
            returnedTags: returnedTags,
            writtenTags: writtenTags,
            missingTags: missingTags);
    }

    private static void EmitStatus(
        AutoTagFileRunContext context,
        string status,
        string? message,
        double? accuracy,
        bool usedShazam,
        AutoTagReviewMetadata? review = null,
        string? outcome = null,
        ProviderTagPlan? tagPlan = null,
        AutoTagMatchResult? match = null,
        IReadOnlyCollection<SupportedTag>? returnedTags = null,
        IReadOnlyCollection<SupportedTag>? writtenTags = null,
        IReadOnlyCollection<SupportedTag>? missingTags = null)
    {
        var isLyricsPlatform = string.Equals(context.Platform, LyricsPlatform, StringComparison.OrdinalIgnoreCase);
        context.StatusCallback(new TaggingStatusWrap
        {
            Platform = context.Platform,
            Progress = context.Progress,
            PlatformIndex = context.PlatformIndex,
            PlatformCount = context.Plan.PlatformCount,
            FileIndex = context.FileIndex,
            FileCount = context.Plan.FileCount,
            NextPlatformIndex = context.NextPlatformIndex,
            NextFileIndex = context.NextFileIndex,
            Status = new TaggingStatus
            {
                Status = status,
                Path = context.File,
                Message = message,
                Accuracy = accuracy,
                UsedShazam = usedShazam,
                Outcome = outcome,
                RecognitionStrategy = ResolveRecognitionStrategy(match),
                RequestedTags = (tagPlan?.Requested.AsEnumerable() ?? Enumerable.Empty<SupportedTag>()).Select(ToTagKey).OrderBy(tag => tag, StringComparer.Ordinal).ToList(),
                ReturnedTags = (returnedTags ?? Array.Empty<SupportedTag>()).Select(ToTagKey).OrderBy(tag => tag, StringComparer.Ordinal).ToList(),
                WrittenTags = (writtenTags ?? Array.Empty<SupportedTag>()).Select(ToTagKey).OrderBy(tag => tag, StringComparer.Ordinal).ToList(),
                RetainedTags = (tagPlan?.Retained.AsEnumerable() ?? Enumerable.Empty<SupportedTag>()).Select(ToTagKey).OrderBy(tag => tag, StringComparer.Ordinal).ToList(),
                MissingTags = (missingTags?.AsEnumerable() ?? tagPlan?.Eligible.AsEnumerable() ?? Enumerable.Empty<SupportedTag>()).Select(ToTagKey).OrderBy(tag => tag, StringComparer.Ordinal).ToList(),
                ReviewReason = review?.Reason ?? message,
                LyricsBadges = isLyricsPlatform
                    ? ResolveLyricsTimingBadges(context.File, context.Plan.Config, context.Plan.Settings)
                    : new List<string>(),
                ArtworkBadges = ResolveAnimatedArtworkBadges(context.File, context.Plan.Settings),
                LyricsCoverUrl = isLyricsPlatform ? ResolveLyricsRowCoverUrl(context.File) : null,
                SourceTitle = isLyricsPlatform
                    ? (match?.Track.Title ?? review?.SourceTitle)
                    : review?.SourceTitle,
                SourceArtist = isLyricsPlatform
                    ? (match?.Track.Artists.FirstOrDefault() ?? review?.SourceArtist)
                    : review?.SourceArtist,
                SourceIsrc = review?.SourceIsrc,
                SourceDurationSeconds = review?.SourceDurationSeconds,
                CandidateTitle = review?.CandidateTitle,
                CandidateArtist = review?.CandidateArtist,
                CandidateIsrc = review?.CandidateIsrc,
                CandidateDurationSeconds = review?.CandidateDurationSeconds
            }
        });
    }

    private static string? ResolveRecognitionStrategy(AutoTagMatchResult? match)
    {
        if (!string.IsNullOrWhiteSpace(match?.MatchStrategy))
        {
            return match.MatchStrategy;
        }

        return match?.Track.Other.TryGetValue("SHAZAM_MATCH_STRATEGY", out var strategies) == true
            ? strategies.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim().ToLowerInvariant()
            : null;
    }

    private static async Task ApplyPostLoopFallbackAsync(AutoTagRunPlan plan, CancellationToken token)
    {
        if (plan.ShazamConflictResolution || !plan.Config.ParseFilename)
        {
            return;
        }

        foreach (var file in plan.Files.Where(file => !plan.PreSkippedFiles.Contains(file) && !plan.TaggedByAnyPlatform.Contains(file)))
        {
            await EnsureCoreTagsFromPathAsync(
                file,
                plan.TargetPath,
                plan.Settings.Tags?.SingleAlbumArtist ?? true,
                token);
        }
    }

    private static double ComputeOverallProgress(int platformIndex, int fileIndex, int platformCount, int fileCount)
    {
        var fileProgress = fileCount == 0
            ? 1.0
            : (fileIndex + 1) / (double)fileCount;

        return platformCount == 0
            ? 1.0
            : (platformIndex / (double)platformCount) + (fileProgress / platformCount);
    }

    private static double ComputeBatchOverallProgress(
        int batchStart,
        int batchEnd,
        int platformIndex,
        int fileIndex,
        int platformCount,
        int fileCount)
    {
        if (platformCount == 0 || fileCount == 0)
        {
            return 1.0;
        }

        var completedFilesBeforeBatch = batchStart;
        var batchFileCount = Math.Max(1, batchEnd - batchStart);
        var batchProgress = (platformIndex / (double)platformCount)
            + (((fileIndex - batchStart) + 1) / (double)batchFileCount / platformCount);
        return Math.Min(1.0, (completedFilesBeforeBatch + (batchProgress * batchFileCount)) / fileCount);
    }

    private static int ComputeNextPlatformIndex(int platformIndex, int fileIndex, int platformCount, int fileCount)
    {
        var nextFileIndex = fileIndex + 1;
        return nextFileIndex >= fileCount ? platformIndex + 1 : platformIndex;
    }

    private static int ComputeNextFileIndex(int fileIndex, int fileCount)
    {
        var nextFileIndex = fileIndex + 1;
        return nextFileIndex >= fileCount ? 0 : nextFileIndex;
    }

    private JobMatchCacheState GetOrCreateMatchCache(string jobId)
    {
        var cache = _jobMatchCaches.GetOrAdd(jobId, static _ => new JobMatchCacheState());
        cache.LastAccessUtc = DateTimeOffset.UtcNow;
        return cache;
    }

    private void PruneExpiredMatchCaches()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (jobId, cache) in _jobMatchCaches)
        {
            if (now - cache.LastAccessUtc > MatchCacheTtl)
            {
                _jobMatchCaches.TryRemove(jobId, out _);
            }
        }
    }

    private static bool TryGetCachedMatch(JobMatchCacheState cache, string key, out AutoTagMatchResult? match)
    {
        lock (cache.SyncRoot)
        {
            cache.LastAccessUtc = DateTimeOffset.UtcNow;
            if (cache.Entries.TryGetValue(key, out var entry))
            {
                match = entry.Match;
                return true;
            }
        }

        match = null;
        return false;
    }

    private static void StoreCachedMatch(JobMatchCacheState cache, string key, AutoTagMatchResult? match)
    {
        lock (cache.SyncRoot)
        {
            cache.LastAccessUtc = DateTimeOffset.UtcNow;
            cache.Entries[key] = new MatchCacheEntry(match);
            if (cache.Entries.Count > MaxCacheEntriesPerJob)
            {
                var keysToRemove = cache.Entries.Keys
                    .Take(cache.Entries.Count - MaxCacheEntriesPerJob)
                    .ToList();
                foreach (var staleKey in keysToRemove)
                {
                    cache.Entries.Remove(staleKey);
                }
            }
        }
    }

    private static bool IsPlatformUnavailable(JobMatchCacheState cache, string platform)
    {
        lock (cache.SyncRoot)
        {
            return cache.UnavailablePlatforms.Contains(platform);
        }
    }

    private static void MarkPlatformUnavailable(JobMatchCacheState cache, string platform)
    {
        lock (cache.SyncRoot)
        {
            cache.LastAccessUtc = DateTimeOffset.UtcNow;
            cache.UnavailablePlatforms.Add(platform);
        }
    }

    public Task<bool> StopAsync(string jobId, CancellationToken cancellationToken)
    {
        if (_jobTokens.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    private DeezSpoTagSettings LoadRuntimeSettings(TechnicalTagSettings? technical, AutoTagRunnerConfig config)
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            ApplyTechnicalOverrides(settings, technical);
            ApplyRuntimeConfigOverrides(settings, config);
            return settings;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load runtime settings for AutoTag.");
            var fallback = DeezSpoTagSettingsService.GetStaticDefaultSettings();
            ApplyTechnicalOverrides(fallback, technical);
            ApplyRuntimeConfigOverrides(fallback, config);
            return fallback;
        }
    }

    private static void ApplyTechnicalOverrides(DeezSpoTagSettings settings, TechnicalTagSettings? technical)
    {
        if (technical == null)
        {
            return;
        }

        TechnicalLyricsSettingsApplier.Apply(settings, technical);
        settings.Tags ??= new TagSettings();

        settings.DateFormat = technical.DateFormat;
        settings.AlbumVariousArtists = technical.AlbumVariousArtists;
        settings.RemoveAlbumVersion = technical.RemoveAlbumVersion;
        settings.RemoveDuplicateArtists = technical.RemoveDuplicateArtists;
        settings.FeaturedToTitle = technical.FeaturedToTitle;
        settings.TitleCasing = technical.TitleCasing;
        settings.ArtistCasing = technical.ArtistCasing;

        settings.Tags.SavePlaylistAsCompilation = technical.SavePlaylistAsCompilation;
        settings.Tags.UseNullSeparator = technical.UseNullSeparator;
        settings.Tags.SaveID3v1 = technical.SaveID3v1;
        settings.Tags.MultiArtistSeparator = technical.MultiArtistSeparator;
        settings.Tags.SingleAlbumArtist = technical.SingleAlbumArtist;
        settings.Tags.CoverDescriptionUTF8 = technical.CoverDescriptionUTF8;
    }

    private static void ApplyRuntimeConfigOverrides(DeezSpoTagSettings settings, AutoTagRunnerConfig config)
    {
        ApplyFolderStructureOverrides(settings, config.FolderStructure);

        if (config.SaveArtwork.HasValue)
        {
            settings.SaveArtwork = config.SaveArtwork.Value;
        }

        if (config.DlAlbumcoverForPlaylist.HasValue)
        {
            settings.DlAlbumcoverForPlaylist = config.DlAlbumcoverForPlaylist.Value;
        }

        if (config.SaveArtworkArtist.HasValue)
        {
            settings.SaveArtworkArtist = config.SaveArtworkArtist.Value;
        }

        if (config.SaveAnimatedArtwork.HasValue)
        {
            settings.SaveAnimatedArtwork = config.SaveAnimatedArtwork.Value;
        }

        if (!string.IsNullOrWhiteSpace(config.AnimatedArtworkFormats))
        {
            settings.AnimatedArtworkFormats = config.AnimatedArtworkFormats.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.CoverImageTemplate))
        {
            settings.CoverImageTemplate = config.CoverImageTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.AnimatedArtworkSquareFileName))
        {
            settings.AnimatedArtworkSquareFileName = config.AnimatedArtworkSquareFileName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.AnimatedArtworkTallFileName))
        {
            settings.AnimatedArtworkTallFileName = config.AnimatedArtworkTallFileName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.ArtistImageTemplate))
        {
            settings.ArtistImageTemplate = config.ArtistImageTemplate.Trim();
        }

        var normalizedArtworkFormat = NormalizeLocalArtworkFormat(config.LocalArtworkFormat);
        if (!string.IsNullOrWhiteSpace(normalizedArtworkFormat))
        {
            settings.LocalArtworkFormat = normalizedArtworkFormat;
        }

        if (config.EmbedMaxQualityCover.HasValue)
        {
            settings.EmbedMaxQualityCover = config.EmbedMaxQualityCover.Value;
        }

        if (config.AnimatedArtworkMaxSizeMb.HasValue)
        {
            settings.AnimatedArtworkMaxSizeMb = Math.Clamp(config.AnimatedArtworkMaxSizeMb.Value, 1, 200);
        }

        if (config.JpegImageQuality.HasValue)
        {
            settings.JpegImageQuality = Math.Clamp(config.JpegImageQuality.Value, 1, 100);
        }
    }

    private static void ApplyFolderStructureOverrides(DeezSpoTagSettings settings, FolderStructureSettings? folderStructure)
    {
        if (folderStructure == null)
        {
            return;
        }

        settings.CreateArtistFolder = folderStructure.CreateArtistFolder;
        settings.CreateAlbumFolder = folderStructure.CreateAlbumFolder;
        settings.CreateCDFolder = folderStructure.CreateCDFolder;
        settings.CreateStructurePlaylist = folderStructure.CreateStructurePlaylist;
        settings.CreateSingleFolder = folderStructure.CreateSingleFolder;
        settings.CreatePlaylistFolder = folderStructure.CreatePlaylistFolder;

        if (!string.IsNullOrWhiteSpace(folderStructure.ArtistNameTemplate))
        {
            settings.ArtistNameTemplate = folderStructure.ArtistNameTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(folderStructure.AlbumNameTemplate))
        {
            settings.AlbumNameTemplate = folderStructure.AlbumNameTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(folderStructure.PlaylistNameTemplate))
        {
            settings.PlaylistNameTemplate = folderStructure.PlaylistNameTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(folderStructure.IllegalCharacterReplacer))
        {
            settings.IllegalCharacterReplacer = folderStructure.IllegalCharacterReplacer.Trim();
        }
    }

    private static string? NormalizeLocalArtworkFormat(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "jpg",
            "png"
        };

        var normalized = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => allowed.Contains(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(value => value.ToLowerInvariant())
            .ToList();

        return normalized.Count == 0 ? null : string.Join(",", normalized);
    }

    private async Task PopulateAppleExtrasAsync(
        string platform,
        string filePath,
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        CancellationToken token)
    {
        var itunesConfig = LoadConfig(config.Custom, ItunesPlatform, new ItunesMatchConfig());
        var saveAnimatedArtwork = itunesConfig.AnimatedArtwork ?? settings.SaveAnimatedArtwork;
        var wantsAnimatedArtwork = saveAnimatedArtwork
            && WantsArtworkFromSettings(config, settings);
        var wantsAppleLyrics = ShouldRequestAnyLyrics(config, settings);
        var wantsCatalogMetadata = string.Equals(platform, ItunesPlatform, StringComparison.OrdinalIgnoreCase)
            && HasAnyTags(
                config,
                GenreTag,
                IsrcTag,
                LabelTag,
                CopyrightTag,
                ComposerTag,
                InvolvedPeopleTag,
                OtherTagsTag,
                ExplicitTag);
        if (!wantsAnimatedArtwork && !wantsAppleLyrics && !wantsCatalogMetadata)
        {
            return;
        }

        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront)
            ? "us"
            : settings.AppleMusic.Storefront;
        var appleIdentity = await ResolveAppleIdentityForExtrasAsync(track, storefront, settings, token);

        if (wantsCatalogMetadata && !string.IsNullOrWhiteSpace(appleIdentity?.AppleId))
        {
            await PopulateAppleCatalogMetadataAsync(
                track,
                config,
                IsLocalAtmosFile(filePath),
                appleIdentity.AppleId,
                storefront,
                settings.AppleMusic?.MediaUserToken,
                token);
        }

        if (wantsAppleLyrics)
        {
            await PopulatePlatformLyricsAsync(
                AppleProvider,
                filePath,
                track,
                config,
                settings,
                token,
                appleIdentity?.AppleId);
        }

        if (!wantsAnimatedArtwork)
        {
            return;
        }

        var outputDir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return;
        }

        var maxResolution = settings.Video?.AppleMusicVideoMaxResolution ?? 2160;
        var baseFileName = BuildAlbumArtworkBaseFileName(track, settings);

        await TryPopulateAppleAnimatedArtworkAsync(
            track,
            outputDir,
            storefront,
            maxResolution,
            baseFileName,
            appleIdentity,
            settings,
            token);
    }

    private async Task PopulateAppleCatalogMetadataAsync(
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        bool localFileIsAtmos,
        string appleTrackId,
        string storefront,
        string? mediaUserToken,
        CancellationToken token)
    {
        using var payload = await _appleMusicCatalogService.GetSongAsync(
            appleTrackId,
            storefront,
            "en-US",
            token,
            mediaUserToken);
        if (!payload.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0
            || !data[0].TryGetProperty("attributes", out var attributes))
        {
            return;
        }

        ApplyAppleCatalogMetadata(track, config, attributes, localFileIsAtmos);
    }

    private static void ApplyAppleCatalogMetadata(
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        JsonElement attributes,
        bool localFileIsAtmos)
    {
        if (HasAnyTags(config, GenreTag)
            && attributes.TryGetProperty("genreNames", out var genreNames)
            && genreNames.ValueKind == JsonValueKind.Array)
        {
            var genres = genreNames.EnumerateArray()
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Where(value => !string.Equals(value, "Music", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (genres.Count > 0)
            {
                track.Genres = track.Genres
                    .Concat(genres)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        var isrc = TryGetJsonString(attributes, "isrc");
        if (HasAnyTags(config, IsrcTag) && !string.IsNullOrWhiteSpace(isrc) && !localFileIsAtmos)
        {
            track.Isrc = isrc;
        }

        var recordLabel = TryGetJsonString(attributes, "recordLabel");
        if (HasAnyTags(config, LabelTag) && !string.IsNullOrWhiteSpace(recordLabel))
        {
            track.Label = recordLabel;
        }

        var composer = TryGetJsonString(attributes, "composerName");
        if (HasAnyTags(config, ComposerTag) && !string.IsNullOrWhiteSpace(composer))
        {
            track.Other[ComposerTag] = SplitCompositeRawValues(composer).ToList();
        }
        if (HasAnyTags(config, InvolvedPeopleTag) && !string.IsNullOrWhiteSpace(composer))
        {
            track.Other[InvolvedPeopleTag] = [.. SplitCompositeRawValues(composer).Select(name => $"Composer: {name}")];
        }

        var copyright = TryGetJsonString(attributes, "copyright");
        if (HasAnyTags(config, CopyrightTag) && !string.IsNullOrWhiteSpace(copyright))
        {
            track.Other[CopyrightTag] = [copyright];
        }

        if (HasAnyTags(config, ExplicitTag)
            && attributes.TryGetProperty("contentRating", out var rating)
            && rating.ValueKind == JsonValueKind.String)
        {
            track.Explicit = string.Equals(rating.GetString(), "explicit", StringComparison.OrdinalIgnoreCase);
        }

        if (!HasAnyTags(config, OtherTagsTag))
        {
            return;
        }

        track.RawTagsToRemove.Add("APPLE_AUDIO_TRAITS");
        track.RawTagsToRemove.Add("APPLE_IS_ATMOS");
        if (!localFileIsAtmos)
        {
            return;
        }

        track.Other["APPLE_AUDIO_TRAITS"] = ["atmos"];
        track.Other["APPLE_IS_ATMOS"] = ["1"];
    }

    private static bool IsLocalAtmosFile(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var codec = file.Properties.Codecs == null
                ? string.Empty
                : string.Join(' ', file.Properties.Codecs.Select(value => value.Description ?? string.Empty));
            return AudioVariantResolver.IsAtmosVariant(
                file.Properties.AudioChannels,
                codec,
                Path.GetExtension(filePath),
                filePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static string? TryGetJsonString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private async Task TryPopulateAppleAnimatedArtworkAsync(
        AutoTagTrack track,
        string outputDir,
        string storefront,
        int maxResolution,
        string baseFileName,
        TrackIdentityResolution? appleIdentity,
        DeezSpoTagSettings settings,
        CancellationToken token)
    {
        var artist = track.Artists.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(appleIdentity?.AppleId))
        {
            return;
        }

        try
        {
            var animatedResult = await AppleQueueHelpers.SaveAnimatedArtworkAsync(
                _appleMusicCatalogService,
                _httpClientFactory,
                new AppleQueueHelpers.AnimatedArtworkSaveRequest
                {
                    AppleId = appleIdentity?.AppleId,
                    Artist = appleIdentity?.AppleArtistName ?? artist,
                    Album = appleIdentity?.AppleAlbumName ?? track.Album,
                    SquareFileName = settings.AnimatedArtworkSquareFileName,
                    TallFileName = settings.AnimatedArtworkTallFileName,
                    Storefront = storefront,
                    MaxResolution = maxResolution,
                    OutputDir = outputDir,
                    Logger = _logger,
                    CollectionType = string.IsNullOrWhiteSpace(appleIdentity?.AppleAlbumId) ? null : "album",
                    CollectionId = appleIdentity?.AppleAlbumId,
                    OutputFormats = AppleQueueHelpers.ResolveAnimatedArtworkFormats(settings),
                    MaxSizeMb = AppleQueueHelpers.ResolveAnimatedArtworkMaxSizeMb(settings)
                },
                token);

            if (animatedResult.Paths.Count > 0)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("AutoTag Apple animated artwork saved for {Title} in {OutputDir}", SanitizeLogValue(track.Title), SanitizeLogValue(outputDir));
                }
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "AutoTag Apple animated artwork {Status} for {Title}: {Message}",
                        animatedResult.Status,
                        SanitizeLogValue(track.Title),
                        animatedResult.Message);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple animated artwork resolution failed for {Title}.", SanitizeLogValue(track.Title));
            }
        }
    }

    private async Task PopulatePlatformLyricsAsync(
        string platform,
        string filePath,
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        CancellationToken token,
        string? providerTrackId = null)
    {
        var provider = NormalizeLyricsLookupSource(platform.Trim().ToLowerInvariant());
        if (!LyricsProviderRegistry.IsRegistered(provider))
        {
            return;
        }

        var request = RestrictLyricsRequestToProvider(
            BuildLyricsPopulationRequest(filePath, track, config, settings),
            provider);
        if (!request.ShouldFetch)
        {
            return;
        }

        if (request.HasAllRequestedLyrics())
        {
            return;
        }

        if (provider is not LyricsProviderRegistry.YouLyPlus and not LyricsProviderRegistry.BetterLyrics
            && string.IsNullOrWhiteSpace(providerTrackId)
            && string.IsNullOrWhiteSpace(track.TrackId) &&
            string.IsNullOrWhiteSpace(track.Url) &&
            string.IsNullOrWhiteSpace(track.Isrc))
        {
            return;
        }

        var lookupTrack = BuildLyricsLookupTrack(track, provider);
        if (!string.IsNullOrWhiteSpace(providerTrackId))
        {
            lookupTrack.Id = providerTrackId;
            lookupTrack.SourceId = providerTrackId;
            AddLookupUrl(lookupTrack.Urls, $"{provider}_track_id", providerTrackId);
        }
        var lookupSettings = BuildLyricsLookupSettings(
            settings,
            request.WantsSynced,
            request.WantsUnsynced,
            request.WantsTtml);
        lookupSettings.LyricsFallbackEnabled = true;
        lookupSettings.LyricsFallbackOrder = provider;
        var providerOptions = BuildLyricsProviderOptions(config.Custom);
        LyricsBase? lyrics = null;
        try
        {
            lyrics = await _downloadLyricsService.ResolveLyricsAsync(
                lookupTrack,
                lookupSettings,
                providerOptions,
                token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Lyrics resolution failed for platform {Platform} and track {Title}.", SanitizeLogValue(provider), SanitizeLogValue(track.Title));
            }
            return;
        }

        if (lyrics == null || !lyrics.IsLoaded())
        {
            return;
        }

        ApplyResolvedLyrics(track, lyrics, request, settings);
    }

    private static LyricsPopulationRequest RestrictLyricsRequestToProvider(
        LyricsPopulationRequest request,
        string provider)
    {
        var supportsTtml = LyricsProviderRegistry.TryGet(provider, out var descriptor)
            && (descriptor.SupportsNativeTtml || descriptor.SupportsWordSynchronized);
        return request with { WantsTtml = request.WantsTtml && supportsTtml };
    }

    private async Task<TrackIdentityResolution?> ResolveAppleIdentityForExtrasAsync(
        AutoTagTrack track,
        string storefront,
        DeezSpoTagSettings settings,
        CancellationToken token)
    {
        try
        {
            var artist = track.Artists.FirstOrDefault();
            var persistedAppleId = TryGetFirstOtherValue(track.Other, AutoTagIdentityTags.AppleTrackIdAliases)
                ?? (track.Other.ContainsKey(AutoTagIdentityTags.AppleTrackId) ? track.TrackId : null);
            var identity = await _trackIdentityResolver.ResolveAsync(
                new TrackIdentityResolutionRequest(
                    SourcePlatform: null,
                    SourceUrl: track.Url,
                    Title: track.Title,
                    Artist: artist,
                    Album: track.Album,
                    Isrc: track.Isrc,
                    DurationMs: track.Duration.HasValue ? (int)Math.Round(track.Duration.Value.TotalMilliseconds) : null,
                    AppleId: persistedAppleId,
                    TargetPlatforms: new[] { "apple" },
                    Storefront: storefront,
                    Language: "en-US",
                    MediaUserToken: settings.AppleMusic?.MediaUserToken),
                token);
            return string.IsNullOrWhiteSpace(identity.AppleId) ? null : identity;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Central Apple identity lookup failed for animated artwork {Isrc}.", SanitizeLogValue(track.Isrc));
            }
            return null;
        }
    }

    private static string BuildAlbumArtworkBaseFileName(AutoTagTrack track, DeezSpoTagSettings settings)
    {
        var albumTitle = string.IsNullOrWhiteSpace(track.Album) ? "Unknown Album" : track.Album.Trim();
        var primaryArtist = track.Artists.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(primaryArtist))
        {
            primaryArtist = UnknownArtist;
        }

        var albumModel = new DeezSpoTag.Core.Models.Album(albumTitle)
        {
            MainArtist = new DeezSpoTag.Core.Models.Artist(primaryArtist),
            Artists = new List<string> { primaryArtist }
        };

        return PathTemplateGenerator.GenerateAlbumName(
            settings.CoverImageTemplate,
            albumModel,
            settings,
            playlist: null);
    }

    private static Track BuildLyricsLookupTrack(AutoTagTrack track, string platformId)
    {
        var normalizedPlatform = NormalizeLyricsLookupSource(platformId);
        var lookupTrack = new Track
        {
            Id = track.TrackId ?? string.Empty,
            Source = normalizedPlatform,
            SourceId = track.TrackId,
            Title = track.Title ?? string.Empty,
            Album = new Album(track.Album ?? string.Empty),
            ISRC = track.Isrc ?? string.Empty,
            DownloadURL = track.Url ?? string.Empty,
            Duration = track.Duration.HasValue ? (int)Math.Max(0, track.Duration.Value.TotalSeconds) : 0
        };

        var primaryArtist = track.Artists.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(primaryArtist))
        {
            lookupTrack.MainArtist = new DeezSpoTag.Core.Models.Artist
            {
                Id = "0",
                Name = primaryArtist,
                Role = "Main"
            };
            lookupTrack.Artists = new List<string> { primaryArtist };
            lookupTrack.Artist["Main"] = new List<string> { primaryArtist };
        }

        if (!string.IsNullOrWhiteSpace(track.Url))
        {
            lookupTrack.Urls[normalizedPlatform] = track.Url;
        }

        if (string.Equals(normalizedPlatform, DeezerPlatform, StringComparison.OrdinalIgnoreCase))
        {
            AddLookupUrl(lookupTrack.Urls, "deezer_track_id", track.TrackId);
        }
        else if (string.Equals(normalizedPlatform, SpotifyPlatform, StringComparison.OrdinalIgnoreCase))
        {
            AddLookupUrl(lookupTrack.Urls, "spotify_track_id", track.TrackId);
        }
        else if (string.Equals(normalizedPlatform, AppleProvider, StringComparison.OrdinalIgnoreCase))
        {
            AddLookupUrl(lookupTrack.Urls, "apple_track_id", track.TrackId);
        }

        var other = track.Other ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        AddLookupUrl(lookupTrack.Urls, "deezer_track_id", TryGetFirstOtherValue(other, DeezerTrackIdTag, "DEEZERID", "DEEZER_ID"));
        AddLookupUrl(lookupTrack.Urls, "spotify_track_id", TryGetFirstOtherValue(other, SpotifyTrackIdTag, SpotifyTrackIdLegacyTag, SpotifyIdLegacyTag, SpotifyIdUnderscoreLegacyTag));
        AddLookupUrl(lookupTrack.Urls, "apple_track_id", TryGetFirstOtherValue(other, "APPLE_TRACK_ID", "APPLEID", "ITUNES_TRACK_ID", "ITUNESCATALOGID"));

        AddLookupUrl(lookupTrack.Urls, DeezerPlatform, TryGetFirstOtherValue(other, "DEEZER_URL"));
        AddLookupUrl(lookupTrack.Urls, SpotifyPlatform, TryGetFirstOtherValue(other, SpotifyUrlTag));
        AddLookupUrl(lookupTrack.Urls, AppleProvider, TryGetFirstOtherValue(other, "APPLE_URL", "ITUNES_URL"));

        if (string.IsNullOrWhiteSpace(lookupTrack.DownloadURL))
        {
            lookupTrack.DownloadURL = TryGetFirstOtherValue(other, "source_url", "URL", WwwAudioFileTag)
                ?? TryGetFirstOtherValue(other, "DEEZER_URL", SpotifyUrlTag, "APPLE_URL", "ITUNES_URL")
                ?? string.Empty;
        }

        return lookupTrack;
    }

    private static string NormalizeLyricsLookupSource(string platformId)
    {
        return platformId switch
        {
            ItunesPlatform => AppleProvider,
            _ => string.IsNullOrWhiteSpace(platformId) ? string.Empty : platformId
        };
    }

    private static string? TryGetFirstOtherValue(Dictionary<string, List<string>> other, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!other.TryGetValue(key, out var values) || values == null)
            {
                continue;
            }

            var value = values.FirstOrDefault(static raw => !string.IsNullOrWhiteSpace(raw))?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static void AddLookupUrl(Dictionary<string, string> urls, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
        {
            urls[key] = value.Trim();
        }
    }

    private static DeezSpoTagSettings BuildLyricsLookupSettings(
        DeezSpoTagSettings baseSettings,
        bool wantsSynced,
        bool wantsUnsynced,
        bool wantsTtml)
    {
        var allowsSyncedBySettings = baseSettings.SyncedLyrics;
        var allowsUnsyncedBySettings = baseSettings.SaveLyrics;
        var shouldFetchUnsyncedPayload = wantsUnsynced || wantsTtml;
        return new DeezSpoTagSettings
        {
            DeezerCountry = baseSettings.DeezerCountry,
            AppleMusic = baseSettings.AppleMusic,
            Video = baseSettings.Video,
            SyncedLyrics = allowsSyncedBySettings && (wantsSynced || wantsTtml),
            SaveLyrics = allowsUnsyncedBySettings && shouldFetchUnsyncedPayload,
            SynthesizeLrcFromTtml = baseSettings.SynthesizeLrcFromTtml,
            SynthesizeTtmlFromLrc = baseSettings.SynthesizeTtmlFromLrc,
            PreferEnhancedLrc = baseSettings.PreferEnhancedLrc,
            LyricsFallbackEnabled = baseSettings.LyricsFallbackEnabled,
            LyricsFallbackOrder = string.IsNullOrWhiteSpace(baseSettings.LyricsFallbackOrder)
                ? string.Join(",", LyricsProviderRegistry.DefaultOrder)
                : baseSettings.LyricsFallbackOrder,
            LrcFormat = NormalizeLyricsFormat(baseSettings.LrcFormat),
            LrcType = string.IsNullOrWhiteSpace(baseSettings.LrcType)
                ? "lyrics,syllable-lyrics,ttml-lyrics,unsynced-lyrics"
                : baseSettings.LrcType,
            Tags = new TagSettings
            {
                Lyrics = allowsUnsyncedBySettings && shouldFetchUnsyncedPayload,
                SyncedLyrics = allowsSyncedBySettings && (wantsSynced || wantsTtml)
            }
        };
    }

    private static LyricsProviderOptions? BuildLyricsProviderOptions(JsonObject? custom)
    {
        if (custom == null
            || !custom.TryGetPropertyValue(LrclibProvider, out var lrclibNode)
            || lrclibNode is not JsonObject)
        {
            return null;
        }

        var lrclibConfig = LoadConfig(custom, LrclibProvider, new LrclibConfig());
        return new LyricsProviderOptions
        {
            Lrclib = new LrclibLyricsProviderOptions
            {
                DurationToleranceSeconds = lrclibConfig.DurationToleranceSeconds,
                UseDurationHint = lrclibConfig.UseDurationHint,
                SearchFallback = lrclibConfig.SearchFallback,
                PreferSynced = lrclibConfig.PreferSynced
            }
        };
    }

    private static LyricsPopulationRequest BuildLyricsPopulationRequest(
        string filePath,
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings)
    {
        var requestFlags = ApplyLyricsPreferenceGate(
            settings,
            new LyricsRequestFlags(
                HasAnyTags(config, SyncedLyricsTag),
                HasAnyTags(config, UnsyncedLyricsTag),
                HasAnyTags(config, TtmlLyricsTag)));

        var sidecarState = GetLyricsSidecarState(filePath);
        var timingPreference = LrcTimingModes.Normalize(settings.LrcTimingPreference, settings.PreferEnhancedLrc);
        var existingLrcIsWord = sidecarState.HasLrc
            && LrcContent.IsWordSynchronized(ReadFileOrEmpty(Path.ChangeExtension(filePath, ".lrc")));
        var lrcSatisfies = sidecarState.HasLrc
            && (timingPreference == LrcTimingModes.Line || existingLrcIsWord);
        if (lrcSatisfies)
        {
            requestFlags = requestFlags with { WantsSynced = false, WantsUnsynced = false };
        }

        if (sidecarState.HasTtml)
        {
            requestFlags = requestFlags with { WantsTtml = false };
        }

        return new LyricsPopulationRequest(
            requestFlags.WantsSynced,
            requestFlags.WantsUnsynced,
            requestFlags.WantsTtml,
            track.Other.TryGetValue(SyncedLyricsTag, out var existingSynced) && existingSynced.Count > 0,
            (track.Other.TryGetValue(UnsyncedLyricsTag, out var existingUnsynced) && existingUnsynced.Count > 0)
                || (track.Other.TryGetValue(LyricsTag, out var existingLyrics) && existingLyrics.Count > 0),
            track.Other.TryGetValue(TtmlLyricsTag, out var existingTtml)
                && existingTtml.Any(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static void ApplyResolvedLyrics(
        AutoTagTrack track,
        LyricsBase lyrics,
        LyricsPopulationRequest request,
        DeezSpoTagSettings settings)
    {
        ApplySyncedLyrics(track, lyrics, request);
        ApplyUnsyncedLyrics(track, lyrics, request);
        ApplyTtmlLyrics(track, lyrics, request, settings);
    }

    private static void ApplySyncedLyrics(AutoTagTrack track, LyricsBase lyrics, LyricsPopulationRequest request)
    {
        if (!request.WantsSynced || request.HasSynced)
        {
            return;
        }

        var syncedLines = lyrics.SyncedLyrics?
            .Where(line => line.IsValid())
            .Select(line => line.ToString())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (syncedLines is not { Count: > 0 })
        {
            return;
        }

        SetLyrics(track, SyncedLyricsTag, syncedLines);
        if (lyrics.CanSaveLrcSidecar())
        {
            track.Other[SyncedLyricsSourceFormatTag] = new List<string> { lyrics.SyncedLyricsSourceFormat.ToString() };
        }
    }

    private static void ApplyUnsyncedLyrics(AutoTagTrack track, LyricsBase lyrics, LyricsPopulationRequest request)
    {
        if (!request.WantsUnsynced || request.HasUnsynced || string.IsNullOrWhiteSpace(lyrics.UnsyncedLyrics))
        {
            return;
        }

        var unsyncedLines = lyrics.UnsyncedLyrics
            .Split(LyricsLineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (unsyncedLines.Count == 0)
        {
            return;
        }

        SetLyrics(track, UnsyncedLyricsTag, unsyncedLines);
    }

    private static void ApplyTtmlLyrics(
        AutoTagTrack track,
        LyricsBase lyrics,
        LyricsPopulationRequest request,
        DeezSpoTagSettings settings)
    {
        if (!request.WantsTtml || request.HasTtml)
        {
            return;
        }

        if (!AppleLyricsService.IsWordSyncedTtml(lyrics.TtmlLyrics))
        {
            DownloadLyricsService.TryApplySynthesizedWordTtml(lyrics, settings);
        }

        if (!AppleLyricsService.IsWordSyncedTtml(lyrics.TtmlLyrics))
        {
            return;
        }

        track.Other[TtmlLyricsTag] = new List<string> { lyrics.TtmlLyrics! };
    }

    private static void SetLyrics(AutoTagTrack track, string tag, List<string> lines)
    {
        track.Other[tag] = lines;
        if (!track.Other.TryGetValue(LyricsTag, out var existingLyricsLines) || existingLyricsLines.Count == 0)
        {
            track.Other[LyricsTag] = lines;
        }
    }

    private static LyricsRequestFlags ApplyLyricsPreferenceGate(
        DeezSpoTagSettings settings,
        LyricsRequestFlags requestFlags)
    {
        var allowsSyncedByToggle = settings.SyncedLyrics;
        var allowsUnsyncedByToggle = settings.SaveLyrics;
        if (!allowsSyncedByToggle && !allowsUnsyncedByToggle)
        {
            return requestFlags with { WantsSynced = false, WantsUnsynced = false, WantsTtml = false };
        }

        var selectedTypes = ParseLyricsTypeSelection(settings.LrcType);
        var allowsSyncedTypes = selectedTypes.Contains(LyricsTag) || selectedTypes.Contains(SyllableLyricsType);
        var allowsUnsyncedTypes = selectedTypes.Contains(UnsyncedLyricsType);
        var allowsTtmlTypes = selectedTypes.Contains(TtmlLyricsType);
        var selectedFormats = ParseLyricsFormatSelection(settings.LrcFormat);

        return requestFlags with
        {
            WantsSynced = requestFlags.WantsSynced && allowsSyncedByToggle && allowsSyncedTypes,
            WantsUnsynced = requestFlags.WantsUnsynced && allowsUnsyncedByToggle && allowsUnsyncedTypes,
            WantsTtml = requestFlags.WantsTtml
                && allowsSyncedByToggle
                && allowsTtmlTypes
                && selectedFormats.Contains("ttml")
        };
    }

    private static bool LyricsSidecarsSatisfyPreference(
        string filePath,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings)
    {
        var flags = ApplyLyricsPreferenceGate(
            settings,
            new LyricsRequestFlags(
                HasAnyTags(config, SyncedLyricsTag),
                HasAnyTags(config, UnsyncedLyricsTag),
                HasAnyTags(config, TtmlLyricsTag)));

        if (flags.WantsTtml)
        {
            var ttmlPath = Path.ChangeExtension(filePath, TtmlExtension);
            if (!IOFile.Exists(ttmlPath) || !AppleLyricsService.IsWordSyncedTtml(ReadFileOrEmpty(ttmlPath)))
            {
                return false;
            }
        }

        if (flags.WantsSynced)
        {
            var lrcPath = Path.ChangeExtension(filePath, ".lrc");
            if (!IOFile.Exists(lrcPath))
            {
                return false;
            }
            if (LrcTimingModes.ImpliesEnhanced(LrcTimingModes.Normalize(settings.LrcTimingPreference, settings.PreferEnhancedLrc))
                && !LrcContent.IsWordSynchronized(ReadFileOrEmpty(lrcPath)))
            {
                return false;
            }
        }

        if (flags.WantsUnsynced && !flags.WantsSynced && !flags.WantsTtml
            && !IOFile.Exists(Path.ChangeExtension(filePath, ".txt")))
        {
            return false;
        }

        return flags.WantsSynced || flags.WantsUnsynced || flags.WantsTtml;
    }

    private static string ReadFileOrEmpty(string path)
    {
        try
        {
            return IOFile.Exists(path) ? IOFile.ReadAllText(path) : string.Empty;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return string.Empty;
        }
    }

    private static List<string> ResolveLyricsTimingBadges(string filePath, AutoTagRunnerConfig config, DeezSpoTagSettings settings)
        => LyricsSidecarTimingBadges.FromAudioPath(filePath).ToList();

    private static List<string> ResolveAnimatedArtworkBadges(string filePath, DeezSpoTagSettings settings)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new List<string>();
        }

        return Directory.EnumerateFiles(directory)
            .Any(path => AnimatedArtworkNaming.IsAlbumAnimatedArtworkSidecar(
                path,
                settings.AnimatedArtworkSquareFileName,
                settings.AnimatedArtworkTallFileName))
            ? new List<string> { "animated-artwork" }
            : new List<string>();
    }

    private static string? ResolveLyricsRowCoverUrl(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        foreach (var name in new[] { "cover.jpg", "cover.png", "folder.jpg", "folder.png" })
        {
            var candidate = Path.Join(directory, name);
            if (IOFile.Exists(candidate))
            {
                return $"/api/library/image?path={Uri.EscapeDataString(candidate)}&size=240";
            }
        }

        return null;
    }

    private static bool ShouldRequestAnyLyrics(AutoTagRunnerConfig config, DeezSpoTagSettings settings)
    {
        var requestFlags = ApplyLyricsPreferenceGate(
            settings,
            new LyricsRequestFlags(
                HasAnyTags(config, SyncedLyricsTag),
                HasAnyTags(config, UnsyncedLyricsTag),
                HasAnyTags(config, TtmlLyricsTag)));
        return requestFlags.WantsSynced || requestFlags.WantsUnsynced || requestFlags.WantsTtml;
    }

    private static HashSet<string> ParseLyricsTypeSelection(string? raw)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            selected.Add(LyricsTag);
            selected.Add(SyllableLyricsType);
            selected.Add(TtmlLyricsType);
            selected.Add(UnsyncedLyricsType);
            return selected;
        }

        foreach (var normalized in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value =>
            {
                var normalized = value.Trim().ToLowerInvariant();
                return normalized switch
                {
                    "synced-lyrics" => LyricsTag,
                    "time-synced-lyrics" or "timesynced-lyrics" or "time_synced_lyrics" => SyllableLyricsType,
                    "ttml" or "ttmllyrics" or "ttml_lyrics" => TtmlLyricsType,
                    "unsyncedlyrics" or "unsynced" => UnsyncedLyricsType,
                    _ => normalized
                };
            }))
        {
            selected.Add(normalized);
        }

        if (selected.Count == 0)
        {
            selected.Add(LyricsTag);
            selected.Add(SyllableLyricsType);
            selected.Add(TtmlLyricsType);
            selected.Add(UnsyncedLyricsType);
        }

        return selected;
    }

    private static string NormalizeLyricsFormat(string? raw)
    {
        var formats = ParseLyricsFormatSelection(raw);
        if (formats.Contains("lrc") && formats.Contains("ttml"))
        {
            return "both";
        }

        if (formats.Contains("ttml"))
        {
            return "ttml";
        }

        return "lrc";
    }

    private static HashSet<string> ParseLyricsFormatSelection(string? raw)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var normalized in NormalizeLyricsFormatToken(token))
            {
                selected.Add(normalized);
            }
        }

        if (selected.Count == 0)
        {
            selected.Add("lrc");
            selected.Add("ttml");
        }

        return selected;
    }

    private static IReadOnlyList<string> NormalizeLyricsFormatToken(string? token)
        => token?.Trim().ToLowerInvariant() switch
        {
            "lrc" => ["lrc"],
            "standard-lrc" => ["lrc"],
            "synced" => ["lrc"],
            "synced-lyrics" => ["lrc"],
            "elrc" => ["lrc"],
            "enhanced-lrc" => ["lrc"],
            "enhanced-synchronized-lyrics" => ["lrc"],
            "ttml" => ["ttml"],
            "both" => ["lrc", "ttml"],
            "richlyrics" => ["lrc", "ttml"],
            "rich-lyrics" => ["lrc", "ttml"],
            "lyrics" => ["lrc", "ttml"],
            "lrc+ttml" => ["lrc", "ttml"],
            "ttml+lrc" => ["lrc", "ttml"],
            "all" => ["lrc", "ttml"],
            _ => []
        };

    private static IEnumerable<string> EnumerateAudioFiles(string rootPath, bool includeSubfolders)
    {
        var option = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(rootPath, "*.*", option)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path))
                && !AnimatedArtworkFileNaming.IsAnimatedArtworkSidecar(path));
    }

    private static IEnumerable<string> ResolveTargetFiles(string rootPath, AutoTagRunnerConfig config)
    {
        if (config.TargetFiles == null || config.TargetFiles.Count == 0)
        {
            return EnumerateAudioFiles(rootPath, config.IncludeSubfolders);
        }

        var normalizedRoot = NormalizeScopePath(DownloadPathResolver.ResolveIoPath(rootPath));
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in config.TargetFiles)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            var ioPath = DownloadPathResolver.ResolveIoPath(rawPath.Trim());
            if (string.IsNullOrWhiteSpace(ioPath))
            {
                continue;
            }

            var normalizedPath = NormalizeScopePath(ioPath);
            if (string.IsNullOrWhiteSpace(normalizedPath)
                || !IsPathWithinScope(normalizedPath, normalizedRoot)
                || !IOFile.Exists(normalizedPath)
                || !SupportedExtensions.Contains(Path.GetExtension(normalizedPath))
                || AnimatedArtworkFileNaming.IsAnimatedArtworkSidecar(normalizedPath))
            {
                continue;
            }

            selected.Add(normalizedPath);
        }

        return selected;
    }

    private static string NormalizeScopePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static bool IsPathWithinScope(string candidatePath, string scopePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(scopePath))
        {
            return false;
        }

        if (string.Equals(candidatePath, scopePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var scopeWithSeparator = scopePath.EndsWith(Path.DirectorySeparatorChar)
            || scopePath.EndsWith(Path.AltDirectorySeparatorChar)
            ? scopePath
            : scopePath + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(scopeWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> BuildEffectivePlatforms(AutoTagRunnerConfig config, DeezSpoTagSettings? settings = null)
    {
        var platforms = config.Platforms
            .Select(platform => platform?.Trim())
            .Where(platform => !string.IsNullOrWhiteSpace(platform))
            .Select(platform => platform!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tagPlatforms = platforms.Where(platform => !IsLyricsOnlyPlatform(platform)).ToList();
        if (platforms.Any(IsLyricsOnlyPlatform) && ShouldRequestAnyLyrics(config, settings ?? new DeezSpoTagSettings()))
        {
            tagPlatforms.Add(LyricsPlatform);
        }

        return tagPlatforms;
    }

    private static List<string> ResolveLyricsProviderOrder(AutoTagRunnerConfig config)
        => config.Platforms
            .Select(platform => platform?.Trim())
            .Where(platform => !string.IsNullOrWhiteSpace(platform) && IsLyricsOnlyPlatform(platform!))
            .Select(platform => platform!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsLyricsOnlyPlatform(string platform)
        => LyricsProviderRegistry.TryGet(platform, out var provider)
           && provider.IsLyricsOnly;

    private Dictionary<string, HashSet<SupportedTag>> BuildPlatformSupportedTags()
    {
        var map = (_platformRegistry?.DescribeAll() ?? Array.Empty<AutoTagPlatformDescriptor>())
            .Where(descriptor => !string.IsNullOrWhiteSpace(descriptor.Id))
            .GroupBy(descriptor => descriptor.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(descriptor => descriptor.SupportedTags).ToHashSet(),
                StringComparer.OrdinalIgnoreCase);
        map[LyricsPlatform] = new HashSet<SupportedTag>
        {
            SupportedTag.SyncedLyrics,
            SupportedTag.UnsyncedLyrics,
            SupportedTag.TtmlLyrics
        };
        return map;
    }

    private sealed class PlatformMatchContext
    {
        public required string FilePath { get; init; }
        public required AutoTagRunnerConfig Config { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required AutoTagMatchingConfig MatchingConfig { get; init; }
        public required IDictionary<string, ShazamRecognitionInfo?> ShazamCache { get; init; }
        public required bool IsManualEnrichment { get; init; }
    }

    private async Task<AutoTagMatchResult?> MatchPlatformAsync(
        string platform,
        AutoTagAudioInfo info,
        PlatformMatchContext context,
        CancellationToken token)
    {
        var enableLyrics = ShouldRequestAnyLyrics(context.Config, context.Settings);
        var hasLyricsSidecar = enableLyrics && LyricsSidecarsSatisfyPreference(context.FilePath, context.Config, context.Settings);
        var beatportReleaseMeta = HasAnyTags(context.Config, AlbumArtistTag, TrackTotalTag);
        var traxsourceExtend = HasAnyTags(context.Config, AlbumArtTag, AlbumTag, CatalogNumberTag, ReleaseIdTag, AlbumArtistTag, TrackNumberTag, TrackTotalTag);
        var traxsourceAlbumMeta = HasAnyTags(context.Config, CatalogNumberTag, TrackNumberTag, AlbumArtTag, TrackTotalTag, AlbumArtistTag);
        var discogsNeedsLabelCatalog = HasAnyTags(context.Config, LabelTag, CatalogNumberTag);
        if (string.Equals(platform, LyricsPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return await MatchLyricsProviderAsync(
                string.Join(",", ResolveLyricsProviderOrder(context.Config)),
                info,
                context,
                enableLyrics,
                hasLyricsSidecar,
                token);
        }

        switch (platform.Trim().ToLowerInvariant())
        {
            case "musicbrainz":
                return await _musicBrainzMatcher.MatchAsync(info, context.MatchingConfig, LoadConfig(context.Config.Custom, "musicbrainz", new MusicBrainzMatchConfig()), token);
            case "beatport":
                return await _beatportMatcher.MatchAsync(info, context.MatchingConfig, LoadConfig(context.Config.Custom, "beatport", new BeatportMatchConfig()), beatportReleaseMeta, context.Config.MatchById, token);
            case "discogs":
                return await _discogsMatcher.MatchAsync(info, context.MatchingConfig, LoadConfig(context.Config.Custom, "discogs", new DiscogsConfig()), context.Config.MatchById, discogsNeedsLabelCatalog, token);
            case "traxsource":
                return await _traxsourceMatcher.MatchAsync(info, context.MatchingConfig, traxsourceExtend, traxsourceAlbumMeta, token);
            case "bandcamp":
                return await _bandcampMatcher.MatchAsync(info, context.MatchingConfig, token);
            case "bpmsupreme":
                return await _bpmSupremeMatcher.MatchAsync(info, context.MatchingConfig, LoadConfig(context.Config.Custom, "bpmsupreme", new BpmSupremeConfig()), token);
            case ItunesPlatform:
                return await _itunesMatcher.MatchAsync(info, context.MatchingConfig, LoadConfig(context.Config.Custom, ItunesPlatform, new ItunesMatchConfig()), token);
            case SpotifyPlatform:
                return await _spotifyMatcher.MatchAsync(info, context.MatchingConfig, token);
            case DeezerPlatform:
                var deezerConfig = ResolveDeezerMatchConfig(context.Config);
                return await _deezerMatcher.MatchAsync(info, context.MatchingConfig, deezerConfig, token);
            case BoomplayPlatform:
                return await _boomplayMatcher.MatchAsync(
                    info,
                    context.MatchingConfig,
                    LoadConfig(context.Config.Custom, BoomplayPlatform, new BoomplayConfig()),
                    token);
            case "lastfm":
                return await _lastFmMatcher.MatchAsync(info, LoadConfig(context.Config.Custom, "lastfm", new LastFmConfig()), token);
            case ShazamPlatform:
                return await MatchShazamAsync(context.FilePath, info, context.Config, context.Settings, context.MatchingConfig, context.ShazamCache, token);
            default:
                return null;
        }
    }

    private async Task<AutoTagMatchResult?> MatchLyricsProviderAsync(
        string provider,
        AutoTagAudioInfo info,
        PlatformMatchContext context,
        bool enableLyrics,
        bool hasLyricsSidecar,
        CancellationToken token)
    {
        if (!enableLyrics || hasLyricsSidecar)
        {
            return null;
        }

        var track = BuildLyricsOnlyAutoTagTrack(info);
        var request = BuildLyricsPopulationRequest(context.FilePath, track, context.Config, context.Settings);
        if (!request.ShouldFetch || request.HasAllRequestedLyrics())
        {
            return null;
        }

        var lookupSettings = BuildLyricsLookupSettings(
            context.Settings,
            request.WantsSynced,
            request.WantsUnsynced,
            request.WantsTtml);
        lookupSettings.LyricsFallbackEnabled = true;
        lookupSettings.LyricsFallbackOrder = provider;

        var lyrics = await _downloadLyricsService.ResolveLyricsAsync(
            BuildLyricsLookupTrack(track, provider),
            lookupSettings,
            BuildLyricsProviderOptions(context.Config.Custom),
            token);
        if (lyrics == null || !lyrics.IsLoaded())
        {
            return null;
        }

        ApplyResolvedLyrics(track, lyrics, request, context.Settings);
        return new AutoTagMatchResult
        {
            Accuracy = 1.0,
            Track = track
        };
    }

    private static AutoTagTrack BuildLyricsOnlyAutoTagTrack(AutoTagAudioInfo info)
    {
        var artists = info.Artists
            .Where(static artist => !string.IsNullOrWhiteSpace(artist))
            .Select(static artist => artist.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (artists.Count == 0 && !string.IsNullOrWhiteSpace(info.Artist))
        {
            artists.Add(info.Artist.Trim());
        }

        return new AutoTagTrack
        {
            Title = info.Title ?? string.Empty,
            Artists = artists,
            Album = info.Album,
            Duration = info.DurationSeconds is > 0 ? TimeSpan.FromSeconds(info.DurationSeconds.Value) : null,
            Isrc = info.Isrc,
            TrackNumber = info.TrackNumber,
            Other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private async Task<AutoTagMatchResult?> MatchShazamAsync(
        string filePath,
        AutoTagAudioInfo info,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        AutoTagMatchingConfig matchingConfig,
        IDictionary<string, ShazamRecognitionInfo?> shazamCache,
        CancellationToken token)
    {
        var shazamConfig = LoadConfig(config.Custom, ShazamPlatform, new ShazamMatchConfig());
        var identityIsTrusted = IsTrustedSourceIdentity(info, filePath, config);
        if (shazamConfig.IdFirst && identityIsTrusted)
        {
            var idFirstMatch = await TryMatchShazamByIdsAsync(
                info,
                config,
                matchingConfig,
                token);
            if (idFirstMatch != null)
            {
                return idFirstMatch;
            }
        }

        if (!shazamConfig.FingerprintFallback)
        {
            return null;
        }

        return await _shazamMatcher.MatchAsync(
            filePath,
            info,
            matchingConfig,
            shazamConfig,
            shazamCache,
            token,
            trustSourceIdentity: identityIsTrusted);
    }

    private async Task<AutoTagMatchResult?> TryMatchShazamByIdsAsync(
        AutoTagAudioInfo info,
        AutoTagRunnerConfig config,
        AutoTagMatchingConfig matchingConfig,
        CancellationToken token)
    {
        var effectiveInfo = BuildShazamIdFirstInfo(info);

        var hasDeezerId = HasTagValue(effectiveInfo, DeezerTrackIdTag, "DEEZERID", "DEEZER_ID");
        var hasSpotifyId = HasTagValue(effectiveInfo, SpotifyTrackIdTag, SpotifyTrackIdLegacyTag, SpotifyIdLegacyTag, SpotifyIdUnderscoreLegacyTag);
        var hasIsrc = !string.IsNullOrWhiteSpace(effectiveInfo.Isrc);

        if (hasDeezerId)
        {
            var deezerConfig = ResolveDeezerMatchConfig(config);
            deezerConfig.MatchById = true;
            var byDeezerId = await _deezerMatcher.MatchAsync(effectiveInfo, matchingConfig, deezerConfig, token);
            if (HasUsableMatchIdentity(byDeezerId))
            {
                return PrepareShazamIdFirstMatch(byDeezerId!, DeezerPlatform, info);
            }
        }

        if (hasSpotifyId)
        {
            var bySpotifyId = await _spotifyMatcher.MatchAsync(effectiveInfo, matchingConfig, token);
            if (HasUsableMatchIdentity(bySpotifyId))
            {
                return PrepareShazamIdFirstMatch(bySpotifyId!, SpotifyPlatform, info);
            }
        }

        if (hasIsrc)
        {
            var deezerConfig = ResolveDeezerMatchConfig(config);
            deezerConfig.MatchById = true;
            var byDeezerIsrc = await _deezerMatcher.MatchAsync(effectiveInfo, matchingConfig, deezerConfig, token);
            if (HasUsableMatchIdentity(byDeezerIsrc))
            {
                return PrepareShazamIdFirstMatch(byDeezerIsrc!, DeezerPlatform, info);
            }

            var bySpotifyIsrc = await _spotifyMatcher.MatchAsync(effectiveInfo, matchingConfig, token);
            if (HasUsableMatchIdentity(bySpotifyIsrc))
            {
                return PrepareShazamIdFirstMatch(bySpotifyIsrc!, SpotifyPlatform, info);
            }
        }

        return null;
    }

    private static DeezerConfig ResolveDeezerMatchConfig(AutoTagRunnerConfig config)
        => LoadConfig(config.Custom, DeezerPlatform, new DeezerConfig());

    private static AutoTagAudioInfo BuildShazamIdFirstInfo(AutoTagAudioInfo source)
    {
        var cloned = CloneAudioInfo(source);

        var spotifyId = ExtractSpotifyTrackIdFromTags(cloned.Tags);
        if (!string.IsNullOrWhiteSpace(spotifyId))
        {
            cloned.Tags[SpotifyTrackIdTag] = new List<string> { spotifyId };
        }

        return cloned;
    }

    private static AutoTagAudioInfo CloneAudioInfo(AutoTagAudioInfo source)
    {
        return new AutoTagAudioInfo
        {
            Title = source.Title,
            Artist = source.Artist,
            Artists = source.Artists.ToList(),
            Album = source.Album,
            DurationSeconds = source.DurationSeconds,
            Isrc = source.Isrc,
            TrackNumber = source.TrackNumber,
            Tags = source.Tags.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase),
            HasEmbeddedTitle = source.HasEmbeddedTitle,
            HasEmbeddedArtist = source.HasEmbeddedArtist
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values
            .Select(value => value?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static bool HasUsableMatchIdentity(AutoTagMatchResult? match)
    {
        if (match?.Track == null)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(match.Track.Title)
            && match.Track.Artists.Exists(static artist => !string.IsNullOrWhiteSpace(artist));
    }

    private static AutoTagMatchResult PrepareShazamIdFirstMatch(
        AutoTagMatchResult match,
        string provider,
        AutoTagAudioInfo sourceInfo)
    {
        match.Track.Other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        match.Track.Other["SHAZAM_MATCH_STRATEGY"] = new List<string> { "ID_FIRST" };
        match.Track.Other["SHAZAM_MATCH_PROVIDER"] = new List<string> { provider.ToUpperInvariant() };
        match.MatchStrategy = "id_first";

        var shazamTrackId = ReadFirstTagValue(sourceInfo.Tags, "SHAZAM_TRACK_ID", "SHAZAM_TRACK_KEY");
        var shazamUrl = ReadFirstTagValue(sourceInfo.Tags, "SHAZAM_URL");
        match.Track.Url = string.IsNullOrWhiteSpace(shazamUrl) ? null : shazamUrl.Trim();
        match.Track.ReleaseId = null;
        if (string.IsNullOrWhiteSpace(shazamTrackId))
        {
            match.Track.TrackId = null;
        }
        else
        {
            match.Track.TrackId = shazamTrackId.Trim();
            match.Track.Other["SHAZAM_TRACK_ID"] = [match.Track.TrackId];
        }

        return match;
    }

    private static bool HasTagValue(AutoTagAudioInfo info, params string[] keys)
    {
        return !string.IsNullOrWhiteSpace(ReadFirstTagValue(info.Tags, keys));
    }

    private static string? ExtractSpotifyTrackIdFromTags(Dictionary<string, List<string>> tags)
    {
        var candidates = new[]
        {
            SpotifyTrackIdTag,
            SpotifyTrackIdLegacyTag,
            SpotifyIdLegacyTag,
            SpotifyIdUnderscoreLegacyTag,
            SpotifyUrlTag,
            "SHAZAM_SPOTIFY_URL",
            "SPOTIFY_URI",
            "SPOTIFYURI",
            "URL",
            WwwAudioFileTag
        };

        foreach (var key in candidates)
        {
            if (!tags.TryGetValue(key, out var values) || values == null || values.Count == 0)
            {
                continue;
            }

            foreach (var raw in values.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (SpotifyMetadataService.TryParseSpotifyUrl(raw.Trim(), out var type, out var parsedId)
                    && type.Equals("track", StringComparison.OrdinalIgnoreCase)
                    && IsSpotifyTrackId(parsedId))
                {
                    return parsedId;
                }

                var trimmed = raw.Trim();
                if (IsSpotifyTrackId(trimmed))
                {
                    return trimmed;
                }
            }
        }

        return null;
    }

    private static bool IsSpotifyTrackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 22)
        {
            return false;
        }

        return value.All(char.IsLetterOrDigit);
    }

    private static bool CanUseMatchCache(AutoTagAudioInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.Isrc))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(info.Title))
        {
            return false;
        }

        var hasArtist = !string.IsNullOrWhiteSpace(info.Artist)
            || info.Artists.Any(artist => !string.IsNullOrWhiteSpace(artist));
        if (!hasArtist)
        {
            return false;
        }

        return info.DurationSeconds.HasValue && info.DurationSeconds.Value > 0;
    }

    private static string BuildMatchCacheKey(
        string platform,
        AutoTagAudioInfo info,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        AutoTagMatchingConfig matchingConfig)
    {
        var platformKey = NormalizeCacheToken(platform);
        JsonNode? customNode = null;
        if (config.Custom != null)
        {
            config.Custom.TryGetPropertyValue(platformKey, out customNode);
        }

        var normalizedTags = config.Tags
            .Select(NormalizeCacheToken)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToList();
        var normalizedArtists = info.Artists
            .Select(NormalizeCacheToken)
            .Where(artist => !string.IsNullOrWhiteSpace(artist))
            .ToList();

        var builder = new StringBuilder();
        builder.Append("platform=").Append(platformKey).Append(';');
        builder.Append("title=").Append(NormalizeCacheToken(info.Title)).Append(';');
        builder.Append("artist=").Append(NormalizeCacheToken(info.Artist)).Append(';');
        builder.Append("artists=").Append(string.Join(',', normalizedArtists)).Append(';');
        builder.Append("album=").Append(NormalizeCacheToken(info.Album)).Append(';');
        builder.Append("isrc=").Append(NormalizeCacheToken(info.Isrc)).Append(';');
        builder.Append("duration=").Append(info.DurationSeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(';');
        builder.Append("track=").Append(info.TrackNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(';');
        builder.Append("matchDuration=").Append(matchingConfig.MatchDuration).Append(';');
        builder.Append("maxDiff=").Append(matchingConfig.MaxDurationDifferenceSeconds.ToString(CultureInfo.InvariantCulture)).Append(';');
        builder.Append("strictness=").Append(matchingConfig.Strictness.ToString("0.###", CultureInfo.InvariantCulture)).Append(';');
        builder.Append("multiple=").Append(matchingConfig.MultipleMatches).Append(';');
        builder.Append("preferredRelease=").Append(NormalizeCacheToken(matchingConfig.PreferredReleaseType)).Append(';');
        builder.Append("matchById=").Append(config.MatchById).Append(';');
        builder.Append("enableLyrics=").Append(ShouldRequestAnyLyrics(config, settings)).Append(';');
        builder.Append("lyricsSyncedToggle=").Append(settings.SyncedLyrics).Append(';');
        builder.Append("lyricsUnsyncedToggle=").Append(settings.SaveLyrics).Append(';');
        builder.Append("lyricsType=").Append(NormalizeCacheToken(settings.LrcType)).Append(';');
        builder.Append("lyricsFormat=").Append(NormalizeCacheToken(settings.LrcFormat)).Append(';');
        builder.Append("lyricsSynthesizeLrcFromTtml=").Append(settings.SynthesizeLrcFromTtml).Append(';');
        builder.Append("lyricsSynthesizeTtmlFromLrc=").Append(settings.SynthesizeTtmlFromLrc).Append(';');
        builder.Append("lyricsPreferEnhancedLrc=").Append(settings.PreferEnhancedLrc).Append(';');
        builder.Append("beatportReleaseMeta=").Append(normalizedTags.Any(tag => tag is "albumartist" or "tracktotal")).Append(';');
        builder.Append("traxsourceExtend=").Append(normalizedTags.Any(tag => tag is "albumart" or AlbumTag or "catalognumber" or "releaseid" or "albumartist" or "tracknumber" or "tracktotal")).Append(';');
        builder.Append("traxsourceAlbumMeta=").Append(normalizedTags.Any(tag => tag is "catalognumber" or "tracknumber" or "albumart" or "tracktotal" or "albumartist")).Append(';');
        builder.Append("discogsLabelCatalog=").Append(normalizedTags.Any(tag => tag is LabelTag or "catalognumber")).Append(';');
        builder.Append("custom=").Append(customNode?.ToJsonString() ?? string.Empty).Append(';');

        var fingerprint = ComputeCacheHash(builder.ToString());
        return $"{platformKey}:{fingerprint}";
    }

    private static string NormalizeCacheToken(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    private static string ComputeCacheHash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static bool HasAnyTags(AutoTagRunnerConfig config, params string[] tags)
    {
        if (config.Tags == null || config.Tags.Count == 0)
        {
            return false;
        }

        var configured = BuildConfiguredTagSet(config.Tags);
        return tags.Any(configured.Contains);
    }

    private static HashSet<string> BuildConfiguredTagSet(IEnumerable<string>? tags)
    {
        var configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tags == null)
        {
            return configured;
        }

        foreach (var trimmed in tags
            .Select(static rawTag => rawTag?.Trim())
            .Where(static trimmed => !string.IsNullOrWhiteSpace(trimmed)))
        {
            configured.Add(trimmed!);
            var normalized = NormalizeConfiguredTagKey(trimmed!);
            if (!string.Equals(normalized, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                configured.Add(normalized);
            }
        }

        return configured;
    }

    private static string NormalizeConfiguredTagKey(string tag)
    {
        return tag.Trim().ToLowerInvariant() switch
        {
            YearTag => ReleaseDateTag,
            DateTag => ReleaseDateTag,
            LengthTag => DurationTag,
            LyricsTag => UnsyncedLyricsTag,
            CoverTag => AlbumArtTag,
            _ => tag.Trim()
        };
    }

    private static T LoadConfig<T>(JsonObject? custom, string key, T fallback) where T : class, new()
    {
        if (custom == null || !custom.TryGetPropertyValue(key, out var node) || node == null)
        {
            return fallback;
        }

        try
        {
            var parsed = node.Deserialize<T>(CaseInsensitiveJsonOptions);
            return parsed ?? fallback;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return fallback;
        }
    }

    private ShazamEnrichmentResult TryApplyShazam(
        string filePath,
        AutoTagAudioInfo info,
        AutoTagRunnerConfig config,
        bool enableShazamFallback,
        bool forceShazamMatch,
        Dictionary<string, ShazamRecognitionInfo?> cache,
        Action<string> logCallback,
        CancellationToken token)
    {
        var identityIsTrusted = IsTrustedSourceIdentity(info, filePath, config);
        if (!ShouldAttemptShazam(info, enableShazamFallback, forceShazamMatch, identityIsTrusted))
        {
            return new ShazamEnrichmentResult(false, null, false);
        }

        if (!IsShazamRecognitionAvailable())
        {
            if (forceShazamMatch)
            {
                return new ShazamEnrichmentResult(
                    false,
                    "shazam unavailable",
                    true,
                    ShazamFailureKind.Infrastructure);
            }

            // Degrade gracefully when optional Shazam fallback is unavailable.
            return new ShazamEnrichmentResult(false, "shazam unavailable", false);
        }

        token.ThrowIfCancellationRequested();

        var fromCache = cache.TryGetValue(filePath, out var recognized);
        ShazamRecognitionAttempt? attempt = null;
        if (!fromCache)
        {
            attempt = RecognizeWithShazamAttempt(filePath, token);
            recognized = attempt?.Recognition;
            cache[filePath] = recognized;
        }

        if (recognized == null)
        {
            var outcome = attempt?.Outcome ?? ShazamRecognitionOutcome.NoMatch;
            if (outcome is ShazamRecognitionOutcome.RecognizerError or ShazamRecognitionOutcome.RecognizerUnavailable)
            {
                return new ShazamEnrichmentResult(
                    false,
                    attempt?.Error ?? "shazam unavailable",
                    true,
                    ShazamFailureKind.Infrastructure);
            }

            return forceShazamMatch
                ? new ShazamEnrichmentResult(false, "shazam could not identify track", false, ShazamFailureKind.NoMatch)
                : new ShazamEnrichmentResult(false, null, false);
        }

        if (!fromCache)
        {
            logCallback($"onetagger_autotag: shazam identified {Path.GetFileName(filePath)}");
        }

        var preferShazamCore = forceShazamMatch || !identityIsTrusted || IsLikelyNoisyCoreMetadata(info);
        var shazamConfig = LoadConfig(config.Custom, ShazamPlatform, new ShazamMatchConfig());
        ApplyShazamRecognition(info, recognized, preferShazamCore, shazamConfig);
        return new ShazamEnrichmentResult(true, null, false);
    }

    private static bool ShouldAttemptShazam(
        AutoTagAudioInfo info,
        bool enableShazamFallback,
        bool forceShazamMatch,
        bool identityIsTrusted)
    {
        if (forceShazamMatch || !identityIsTrusted)
        {
            return true;
        }

        if (!enableShazamFallback)
        {
            return false;
        }

        // Always attempt Shazam for raw files with no embedded core metadata.
        if (IsRawCoreMetadata(info))
        {
            return true;
        }

        // Shazam fallback is needed when core tags are missing or clearly noisy.
        return !info.HasEmbeddedTitle || !info.HasEmbeddedArtist || IsLikelyNoisyCoreMetadata(info);
    }

    private static bool IsRawCoreMetadata(AutoTagAudioInfo info)
    {
        return !info.HasEmbeddedTitle
            && !info.HasEmbeddedArtist
            && string.IsNullOrWhiteSpace(info.Isrc);
    }

    private static bool IsLikelyNoisyCoreMetadata(AutoTagAudioInfo info)
        => TrackIdentityTrust.IsWeakMetadataValue(info.Title)
            || TrackIdentityTrust.IsWeakMetadataValue(info.Artist);

    private static bool IsTrustedSourceIdentity(AutoTagAudioInfo info, string filePath, AutoTagRunnerConfig config)
    {
        if (config.EnhancementUntrustedTargets)
        {
            return false;
        }

        if (!info.HasEmbeddedTitle || !info.HasEmbeddedArtist)
        {
            return false;
        }

        return !TrackIdentityTrust.IsUntrustedIdentity(info.Title, info.Artist, filePath);
    }

    private static (bool EnableFallback, bool ForceMatch) ResolveShazamEnrichmentBehavior(AutoTagRunnerConfig config)
    {
        var shazamEnabled = IsShazamPlatformEnabled(config);
        var shazamConfig = LoadConfig(config.Custom, ShazamPlatform, new ShazamMatchConfig());

        var hasShazamConfig = config.Custom != null
            && config.Custom.TryGetPropertyValue(ShazamPlatform, out var shazamNode)
            && shazamNode is JsonObject;

        if (config.ForceShazam || (hasShazamConfig && shazamConfig.ForceMatch))
        {
            return (true, true);
        }

        if (!shazamEnabled)
        {
            return (false, false);
        }

        if (hasShazamConfig)
        {
            return (shazamConfig.FallbackMissingCoreTags, shazamConfig.ForceMatch);
        }

        // Legacy fallback for older profiles/configs. At this point Shazam is enabled,
        // no explicit Shazam platform block exists, and ForceShazam was already handled.
        return (true, false);
    }

    private bool IsShazamRecognitionAvailable()
    {
        return _shazamRecognitionService.IsAvailable;
    }

    private static bool IsShazamConflictResolution(AutoTagRunnerConfig config)
    {
        return IsShazamPlatformEnabled(config)
            && string.Equals(config.ConflictResolution, ShazamPlatform, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsShazamPlatformEnabled(AutoTagRunnerConfig config)
    {
        if (config.Platforms.Count == 0)
        {
            return false;
        }

        return config.Platforms.Any(platform => string.Equals(platform?.Trim(), ShazamPlatform, StringComparison.OrdinalIgnoreCase));
    }

    private static AutoTagRunnerConfig NormalizeConfig(AutoTagRunnerConfig? raw)
    {
        raw ??= new AutoTagRunnerConfig();
        var effectiveSaveArtwork = raw.SaveArtwork ?? false;
        return new AutoTagRunnerConfig
        {
            Platforms = raw.Platforms ?? new List<string>(),
            DownloadTagSource = raw.DownloadTagSource,
            Path = raw.Path,
            TargetFiles = raw.TargetFiles?.Where(path => !string.IsNullOrWhiteSpace(path)).ToList(),
            Tags = raw.Tags ?? new List<string>(),
            OverwriteTags = raw.OverwriteTags ?? new List<string>(),
            Separators = raw.Separators == null
                ? null
                : new AutoTagSeparators
                {
                    Id3 = raw.Separators.Id3,
                    Vorbis = raw.Separators.Vorbis,
                    Mp4 = raw.Separators.Mp4
            },
            Overwrite = raw.Overwrite,
            MergeGenres = raw.MergeGenres,
            Camelot = raw.Camelot,
            ShortTitle = raw.ShortTitle,
            Strictness = raw.Strictness,
            MatchDuration = raw.MatchDuration,
            MaxDurationDifference = raw.MaxDurationDifference,
            MatchById = raw.MatchById,
            EnableShazam = raw.EnableShazam,
            ForceShazam = raw.ForceShazam,
            EnhancementUntrustedTargets = raw.EnhancementUntrustedTargets,
            ConflictResolution = raw.ConflictResolution,
            SkipTagged = raw.SkipTagged,
            IncludeSubfolders = raw.IncludeSubfolders,
            Multiplatform = raw.Multiplatform,
            ParseFilename = raw.ParseFilename,
            Id3v24 = raw.Id3v24,
            TrackNumberLeadingZeroes = raw.TrackNumberLeadingZeroes,
            StylesOptions = raw.StylesOptions,
            MultipleMatches = raw.MultipleMatches,
            TitleRegex = raw.TitleRegex,
            Custom = raw.Custom,
            StylesCustomTag = raw.StylesCustomTag == null
                ? null
                : new AutoTagStylesCustomTag
                {
                    Id3 = raw.StylesCustomTag.Id3,
                    Vorbis = raw.StylesCustomTag.Vorbis,
                    Mp4 = raw.StylesCustomTag.Mp4
                },
            Id3CommLang = raw.Id3CommLang,
            CapitalizeGenres = raw.CapitalizeGenres,
            TracknameTemplate = raw.TracknameTemplate,
            FolderStructure = raw.FolderStructure,
            SaveArtwork = effectiveSaveArtwork,
            DlAlbumcoverForPlaylist = raw.DlAlbumcoverForPlaylist,
            SaveArtworkArtist = raw.SaveArtworkArtist,
            SaveAnimatedArtwork = raw.SaveAnimatedArtwork,
            AnimatedArtworkFormats = raw.AnimatedArtworkFormats,
            CoverImageTemplate = raw.CoverImageTemplate,
            AnimatedArtworkSquareFileName = raw.AnimatedArtworkSquareFileName,
            AnimatedArtworkTallFileName = raw.AnimatedArtworkTallFileName,
            ArtistImageTemplate = raw.ArtistImageTemplate,
            LocalArtworkFormat = raw.LocalArtworkFormat,
            MaterializeToTemplatePath = raw.MaterializeToTemplatePath,
            OrganizeSidecarsIntoTemplateFolders = raw.OrganizeSidecarsIntoTemplateFolders,
            EmbedMaxQualityCover = raw.EmbedMaxQualityCover,
            JpegImageQuality = raw.JpegImageQuality,
            AnimatedArtworkMaxSizeMb = raw.AnimatedArtworkMaxSizeMb,
            Technical = raw.Technical,
            ProfileId = raw.ProfileId,
            ProfileName = raw.ProfileName,
            LibraryWideEnhancementBatchSize = raw.LibraryWideEnhancementBatchSize,
            ManualReleasePreference = NormalizeManualReleasePreference(raw.ManualReleasePreference),
            ManualDestinationFolderId = raw.ManualDestinationFolderId
        };
    }

    private static string? NormalizeManualReleasePreference(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            AutoTagReleaseCategory.Album => AutoTagReleaseCategory.Album,
            AutoTagReleaseCategory.Single => AutoTagReleaseCategory.Single,
            _ => null
        };

    private ShazamRecognitionAttempt? RecognizeWithShazamAttempt(string filePath, CancellationToken token)
    {
        try
        {
            return _shazamRecognitionService.RecognizeWithDetails(filePath, cancellationToken: token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Shazam recognize failed for {File}", SanitizeLogValue(filePath));
            }
            return new ShazamRecognitionAttempt
            {
                Outcome = ShazamRecognitionOutcome.RecognizerError,
                Error = ex.Message
            };
        }
    }

    private static void ApplyShazamRecognition(
        AutoTagAudioInfo info,
        ShazamRecognitionInfo payload,
        bool forceShazam,
        ShazamMatchConfig config)
    {
        var shazamArtists = ResolveShazamArtists(payload);
        ApplyShazamCoreValues(info, payload, shazamArtists, forceShazam, config);
        ApplyShazamDurationAndTrackNumber(info, payload);
        ApplyShazamBaseTags(info, payload, config);
        ApplyShazamOptionalScalarTags(info, payload);
        ApplyShazamCollectionTags(info, payload);
    }

    private static List<string> ResolveShazamArtists(ShazamRecognitionInfo payload)
    {
        var shazamArtists = payload.Artists
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (shazamArtists.Count == 0 && !string.IsNullOrWhiteSpace(payload.Artist))
        {
            shazamArtists.Add(payload.Artist.Trim());
        }

        return shazamArtists;
    }

    private static void ApplyShazamCoreValues(
        AutoTagAudioInfo info,
        ShazamRecognitionInfo payload,
        List<string> shazamArtists,
        bool forceShazam,
        ShazamMatchConfig config)
    {
        if ((forceShazam || !info.HasEmbeddedTitle || string.IsNullOrWhiteSpace(info.Title))
            && !string.IsNullOrWhiteSpace(payload.Title))
        {
            info.Title = payload.Title.Trim();
        }

        if ((forceShazam || !info.HasEmbeddedArtist || string.IsNullOrWhiteSpace(info.Artist)) && shazamArtists.Count > 0)
        {
            info.Artists = shazamArtists.ToList();
            info.Artist = shazamArtists[0];
        }

        if (config.IncludeAlbum
            && (forceShazam || string.IsNullOrWhiteSpace(info.Album))
            && !string.IsNullOrWhiteSpace(payload.Album))
        {
            info.Album = payload.Album.Trim();
        }

        if ((forceShazam || string.IsNullOrWhiteSpace(info.Isrc)) && !string.IsNullOrWhiteSpace(payload.Isrc))
        {
            info.Isrc = payload.Isrc.Trim();
        }
    }

    private static void ApplyShazamDurationAndTrackNumber(AutoTagAudioInfo info, ShazamRecognitionInfo payload)
    {
        if (!info.DurationSeconds.HasValue && payload.DurationMs.HasValue)
        {
            var seconds = (int)Math.Round(payload.DurationMs.Value / 1000d);
            if (seconds > 0)
            {
                info.DurationSeconds = seconds;
            }
        }

        if (!info.TrackNumber.HasValue && payload.TrackNumber.HasValue && payload.TrackNumber.Value > 0)
        {
            info.TrackNumber = payload.TrackNumber.Value;
        }
    }

    private static void ApplyShazamBaseTags(AutoTagAudioInfo info, ShazamRecognitionInfo payload, ShazamMatchConfig config)
    {
        var preferredArtwork = config.PreferHqArtwork
            ? FirstNonEmpty(payload.ArtworkHqUrl, payload.ArtworkUrl)
            : FirstNonEmpty(payload.ArtworkUrl, payload.ArtworkHqUrl);
        SetShazamTag(info, "SHAZAM_TRACK_ID", payload.TrackId);
        SetShazamTag(info, "SHAZAM_TRACK_KEY", payload.TrackId);
        SetShazamTag(info, "SHAZAM_URL", payload.Url);
        SetShazamTag(info, "SHAZAM_TITLE", payload.Title);
        SetShazamTag(info, "SHAZAM_ARTIST", payload.Artist);
        if (config.IncludeGenre)
        {
            SetShazamTag(info, "SHAZAM_GENRE", payload.Genre);
        }

        if (config.IncludeAlbum)
        {
            SetShazamTag(info, "SHAZAM_ALBUM", payload.Album);
        }

        if (config.IncludeLabel)
        {
            SetShazamTag(info, "SHAZAM_LABEL", payload.Label);
        }

        if (config.IncludeReleaseDate)
        {
            SetShazamTag(info, "SHAZAM_RELEASE_DATE", payload.ReleaseDate);
        }

        SetShazamTag(info, "SHAZAM_ARTWORK", preferredArtwork);
        SetShazamTag(info, "SHAZAM_ARTWORK_HQ", payload.ArtworkHqUrl);
        SetShazamTag(info, "SHAZAM_ISRC", payload.Isrc);
        SetShazamTag(info, "SHAZAM_KEY", payload.Key);
        SetShazamTag(info, "SHAZAM_ALBUM_ADAM_ID", payload.AlbumAdamId);
        SetShazamTag(info, "SHAZAM_APPLE_MUSIC_URL", payload.AppleMusicUrl);
        SetShazamTag(info, "SHAZAM_SPOTIFY_URL", payload.SpotifyUrl);
        SetShazamTag(info, "SHAZAM_YOUTUBE_URL", payload.YoutubeUrl);
        SetShazamTag(info, "SHAZAM_LANGUAGE", payload.Language);
        SetShazamTag(info, "SHAZAM_COMPOSER", payload.Composer);
        SetShazamTag(info, "SHAZAM_LYRICIST", payload.Lyricist);
        SetShazamTag(info, "SHAZAM_PUBLISHER", payload.Publisher);
    }

    private static void ApplyShazamOptionalScalarTags(AutoTagAudioInfo info, ShazamRecognitionInfo payload)
    {
        if (payload.DurationMs.HasValue && payload.DurationMs.Value > 0)
        {
            SetShazamTag(info, "SHAZAM_DURATION_MS", payload.DurationMs.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (payload.TrackNumber.HasValue && payload.TrackNumber.Value > 0)
        {
            SetShazamTag(info, "SHAZAM_TRACK_NUMBER", payload.TrackNumber.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (payload.DiscNumber.HasValue && payload.DiscNumber.Value > 0)
        {
            SetShazamTag(info, "SHAZAM_DISC_NUMBER", payload.DiscNumber.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (payload.Explicit.HasValue)
        {
            SetShazamTag(info, "SHAZAM_EXPLICIT", payload.Explicit.Value ? "true" : "false");
        }
    }

    private static void ApplyShazamCollectionTags(AutoTagAudioInfo info, ShazamRecognitionInfo payload)
    {
        SetShazamTagValues(info, "SHAZAM_ARTIST_IDS", payload.ArtistIds);
        SetShazamTagValues(info, "SHAZAM_ARTIST_ADAM_IDS", payload.ArtistAdamIds);

        foreach (var (tagKey, tagValues) in payload.Tags)
        {
            SetShazamTagValues(info, tagKey, tagValues);
        }
    }

    private static void SetShazamTag(AutoTagAudioInfo info, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        info.Tags[key] = new List<string> { value.Trim() };
    }

    private static void SetShazamTagValues(AutoTagAudioInfo info, string key, IEnumerable<string>? values)
    {
        if (string.IsNullOrWhiteSpace(key) || values == null)
        {
            return;
        }

        var normalized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.Count == 0)
        {
            return;
        }

        info.Tags[key.Trim()] = normalized;
    }

    private AutoTagAudioInfo BuildAudioInfo(string filePath, string rootPath, bool parseFilename, string? tracknameTemplate, string? titleRegex)
    {
        var extension = Path.GetExtension(filePath);
        try
        {
            using var file = TagLib.File.Create(filePath);
            var draft = BuildAudioInfoDraft(file, filePath);

            PopulateAudioInfoTagMap(file, extension, draft.Tags);
            ApplyDraftTagFallbacks(draft);
            ApplyTracknameTemplateFallbacks(draft, filePath, parseFilename, tracknameTemplate);
            EnsureArtistFallbacks(draft, filePath, rootPath);
            draft.Title = ResolveTitleWithFallback(draft.Title, filePath, titleRegex);

            return CreateAudioInfoFromDraft(
                draft,
                filePath,
                extension,
                (int?)file.Properties.Duration.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed reading tags for {File}", SanitizeLogValue(filePath));
            return BuildAudioInfoFallback(filePath, rootPath, parseFilename, tracknameTemplate, titleRegex);
        }
    }

    private static AudioInfoDraft BuildAudioInfoDraft(TagLib.File file, string filePath)
    {
        var performerCredits = file.Tag.Performers?
            .Where(credit => !string.IsNullOrWhiteSpace(credit))
            .ToList()
            ?? new List<string>();
        if (!string.IsNullOrWhiteSpace(file.Tag.FirstPerformer))
        {
            performerCredits.Add(file.Tag.FirstPerformer!);
        }

        var artists = SplitArtistCredits(performerCredits);
        if (artists.Count > 0 && artists.All(IsWeakMetadataValue))
        {
            artists.Clear();
        }

        var firstPerformer = IsWeakMetadataValue(file.Tag.FirstPerformer)
            ? string.Empty
            : file.Tag.FirstPerformer ?? string.Empty;
        var title = IsWeakMetadataValue(file.Tag.Title) ? string.Empty : file.Tag.Title ?? string.Empty;
        var album = IsWeakMetadataValue(file.Tag.Album)
            ? InferAlbumFromPath(filePath)
            : file.Tag.Album;
        return new AudioInfoDraft
        {
            Title = title,
            Artist = artists.FirstOrDefault() ?? firstPerformer,
            Artists = artists,
            Album = string.IsNullOrWhiteSpace(album)
                ? InferAlbumFromPath(filePath)
                : album,
            Isrc = file.Tag.ISRC,
            TrackNumber = file.Tag.Track > 0 ? (int?)file.Tag.Track : null,
            HasEmbeddedTitle = !string.IsNullOrWhiteSpace(title),
            HasEmbeddedArtist = artists.Count > 0,
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static void PopulateAudioInfoTagMap(TagLib.File file, string extension, Dictionary<string, List<string>> tags)
    {
        AddTagIfAny(tags, "BEATPORT_TRACK_ID", ReadRawTagValues(file, extension, "BEATPORT_TRACK_ID"));
        AddTagIfAny(tags, "DISCOGS_RELEASE_ID", ReadRawTagValues(file, extension, "DISCOGS_RELEASE_ID"));
        AddTagIfAny(tags, AutoTagIdentityTags.ItunesTrackId, ReadRawTagValuesAny(file, extension, AutoTagIdentityTags.AppleTrackIdAliases));
        AddTagIfAny(tags, AutoTagIdentityTags.AppleTrackId, ReadRawTagValuesAny(file, extension, AutoTagIdentityTags.AppleTrackIdAliases));
        AddTagIfAny(tags, "ITUNES_RELEASE_ID", ReadRawTagValuesAny(file, extension, AutoTagIdentityTags.AppleReleaseIdAliases));
        AddTagIfAny(tags, "ITUNES_ARTIST_ID", ReadRawTagValuesAny(file, extension, AutoTagIdentityTags.AppleArtistIdAliases));
        AddTagIfAny(tags, DeezerTrackIdTag, ReadRawTagValuesAny(file, extension, DeezerTrackIdTag, "DEEZERID", "DEEZER_ID"));
        AddTagIfAny(tags, "DEEZER_RELEASE_ID", ReadRawTagValuesAny(file, extension, "DEEZER_RELEASE_ID"));
        AddTagIfAny(tags, SpotifyTrackIdTag, ReadRawTagValuesAny(file, extension, SpotifyTrackIdTag, SpotifyTrackIdLegacyTag, SpotifyIdLegacyTag, SpotifyIdUnderscoreLegacyTag));
        AddTagIfAny(tags, SpotifyUrlTag, NormalizeSpotifyTrackUrls(ReadRawTagValuesAny(file, extension, SpotifyUrlTag, "SPOTIFYURI", "SPOTIFY_URI")));
        AddTagIfAny(tags, "URL", ReadRawTagValuesAny(file, extension, "URL"));
        AddTagIfAny(tags, WwwAudioFileTag, ReadRawTagValuesAny(file, extension, WwwAudioFileTag));
        AddTagIfAny(tags, "MUSICBRAINZ_RECORDING_ID", ReadRawTagValuesAny(file, extension, "MUSICBRAINZ_RECORDING_ID", "MUSICBRAINZ_RECORDINGID", "MUSICBRAINZ_TRACK_ID", "MUSICBRAINZ_TRACKID"));
        AddTagIfAny(tags, RecordingIdRawTag, ReadRawTagValuesAny(file, extension, RecordingIdRawTag));
        AddTagIfAny(tags, ArtistIdRawTag, ReadRawTagValuesAny(file, extension, ArtistIdRawTag));
        AddTagIfAny(tags, AlbumArtistIdRawTag, ReadRawTagValuesAny(file, extension, AlbumArtistIdRawTag));
        AddTagIfAny(tags, ReleaseGroupIdRawTag, ReadRawTagValuesAny(file, extension, ReleaseGroupIdRawTag));
        AddTagIfAny(tags, AlbumIdRawTag, ReadRawTagValuesAny(file, extension, AlbumIdRawTag));
        AddTagIfAny(tags, ReleaseStatusRawTag, ReadRawTagValuesAny(file, extension, ReleaseStatusRawTag));
        AddTagIfAny(tags, ReleaseCountryRawTag, ReadRawTagValuesAny(file, extension, ReleaseCountryRawTag));
        AddTagIfAny(tags, MediaRawTag, ReadRawTagValuesAny(file, extension, MediaRawTag));

        foreach (var shazamTag in ShazamRawTagHints)
        {
            AddTagIfAny(tags, shazamTag, ReadRawTagValuesAny(file, extension, shazamTag));
        }
    }

    private static List<string> NormalizeSpotifyTrackUrls(IEnumerable<string> values)
    {
        return values
            .Select(NormalizeSpotifyTrackUrl)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? NormalizeSpotifyTrackUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        string? trackId = null;
        if (SpotifyMetadataService.TryParseSpotifyUrl(trimmed, out var type, out var parsedId)
            && type.Equals("track", StringComparison.OrdinalIgnoreCase))
        {
            trackId = parsedId;
        }
        else if (IsSpotifyTrackId(trimmed))
        {
            trackId = trimmed;
        }

        return IsSpotifyTrackId(trackId)
            ? $"https://open.spotify.com/track/{trackId}"
            : null;
    }

    private static void ApplyDraftTagFallbacks(AudioInfoDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Isrc))
        {
            draft.Isrc = ReadFirstTagValue(draft.Tags, "SHAZAM_ISRC", "ISRC");
        }

        if (IsWeakMetadataValue(draft.Album))
        {
            draft.Album = ReadFirstTagValue(draft.Tags, "SHAZAM_ALBUM", AlbumUpperTag);
        }

        if (!draft.TrackNumber.HasValue)
        {
            draft.TrackNumber = ParsePositiveInt(ReadFirstTagValue(draft.Tags, "SHAZAM_TRACK_NUMBER", TrackNumberUpperTag));
        }
    }

    private static void ApplyTracknameTemplateFallbacks(
        AudioInfoDraft draft,
        string filePath,
        bool parseFilename,
        string? tracknameTemplate)
    {
        if (!parseFilename || (!IsWeakMetadataValue(draft.Title) && !IsWeakMetadataValue(draft.Artist)))
        {
            return;
        }

        var template = OneTaggerMatching.ParseFilenameTemplate(tracknameTemplate);
        if (!TryParseFilename(Path.GetFileName(filePath), template, out var parsedArtist, out var parsedTitle))
        {
            return;
        }

        if (IsWeakMetadataValue(draft.Artist))
        {
            draft.Artist = parsedArtist;
        }

        if (IsWeakMetadataValue(draft.Title))
        {
            draft.Title = parsedTitle;
        }

        if (draft.Artists.Count == 0 && !string.IsNullOrWhiteSpace(parsedArtist))
        {
            draft.Artists = SplitArtistCredits(new[] { parsedArtist });
        }
    }

    private static void EnsureArtistFallbacks(AudioInfoDraft draft, string filePath, string rootPath)
    {
        if (draft.Artists.Count > 0 && draft.Artists.All(IsWeakMetadataValue))
        {
            draft.Artists.Clear();
        }

        if (draft.Artists.Count == 0 && !IsWeakMetadataValue(draft.Artist))
        {
            draft.Artists = SplitArtistCredits(new[] { draft.Artist });
            draft.Artist = draft.Artists.FirstOrDefault() ?? draft.Artist;
        }

        if (!IsWeakMetadataValue(draft.Artist))
        {
            return;
        }

        draft.Artist = InferArtistFromPath(filePath, rootPath);
        if (draft.Artists.Count == 0 && !string.IsNullOrWhiteSpace(draft.Artist))
        {
            draft.Artists = SplitArtistCredits(new[] { draft.Artist });
            draft.Artist = draft.Artists.FirstOrDefault() ?? draft.Artist;
        }
    }

    private static string ResolveTitleWithFallback(string title, string filePath, string? titleRegex)
    {
        var resolved = IsWeakMetadataValue(title)
            ? InferTitleFromFilename(filePath)
            : title;

        return ApplyTitleRegexFilter(resolved, titleRegex);
    }

    private static string ApplyTitleRegexFilter(string title, string? titleRegex)
    {
        if (string.IsNullOrWhiteSpace(titleRegex) || string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        try
        {
            var regex = new Regex(titleRegex, RegexOptions.IgnoreCase, RegexTimeout);
            return regex.Replace(title, string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return title;
        }
    }

    private static AutoTagAudioInfo CreateAudioInfoFromDraft(
        AudioInfoDraft draft,
        string filePath,
        string extension,
        int? durationSeconds)
    {
        var normalizedDurationSeconds = NormalizeDurationSeconds(filePath, extension, durationSeconds)
            ?? ResolveDurationSecondsFromTags(draft.Tags)
            ?? ResolveDurationSecondsWithFfprobe(filePath, extension);

        return new AutoTagAudioInfo
        {
            Title = draft.Title,
            Artist = draft.Artist,
            Artists = draft.Artists.Count == 0 && !string.IsNullOrWhiteSpace(draft.Artist)
                ? new List<string> { draft.Artist }
                : draft.Artists,
            Album = string.IsNullOrWhiteSpace(draft.Album) ? null : draft.Album,
            DurationSeconds = normalizedDurationSeconds,
            Isrc = string.IsNullOrWhiteSpace(draft.Isrc) ? null : draft.Isrc,
            TrackNumber = draft.TrackNumber,
            Tags = draft.Tags,
            HasEmbeddedTitle = draft.HasEmbeddedTitle,
            HasEmbeddedArtist = draft.HasEmbeddedArtist
        };
    }

    private static AutoTagAudioInfo BuildAudioInfoFallback(
        string filePath,
        string rootPath,
        bool parseFilename,
        string? tracknameTemplate,
        string? titleRegex)
    {
        var draft = new AudioInfoDraft
        {
            Title = InferTitleFromFilename(filePath),
            Artist = InferArtistFromPath(filePath, rootPath),
            Album = InferAlbumFromPath(filePath),
            Artists = new List<string>(),
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        };
        if (!string.IsNullOrWhiteSpace(draft.Artist))
        {
            draft.Artists = SplitArtistCredits(new[] { draft.Artist });
        }

        ApplyTracknameTemplateFallbacks(draft, filePath, parseFilename, tracknameTemplate);
        draft.Title = ApplyTitleRegexFilter(draft.Title, titleRegex);

        return new AutoTagAudioInfo
        {
            Title = draft.Title,
            Artist = draft.Artist,
            Artists = draft.Artists,
            Album = string.IsNullOrWhiteSpace(draft.Album) ? null : draft.Album,
            HasEmbeddedTitle = false,
            HasEmbeddedArtist = false
        };
    }

    private sealed class AudioInfoDraft
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public List<string> Artists { get; set; } = new();
        public string? Album { get; set; }
        public string? Isrc { get; set; }
        public int? TrackNumber { get; set; }
        public bool HasEmbeddedTitle { get; set; }
        public bool HasEmbeddedArtist { get; set; }
        public Dictionary<string, List<string>> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string InferArtistFromPath(string filePath, string rootPath)
    {
        try
        {
            var fileDir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (string.IsNullOrWhiteSpace(fileDir))
            {
                return string.Empty;
            }

            var rootFull = Path.GetFullPath(rootPath);
            var relativeDir = Path.GetRelativePath(rootFull, fileDir);
            if (!string.IsNullOrWhiteSpace(relativeDir) &&
                !relativeDir.StartsWith("..", StringComparison.Ordinal) &&
                !Path.IsPathRooted(relativeDir))
            {
                var parts = relativeDir.Split(
                        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                        StringSplitOptions.RemoveEmptyEntries)
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .ToArray();
                if (parts.Length >= 2)
                {
                    return parts[0].Trim();
                }
            }

            var parent = Directory.GetParent(fileDir);
            return parent?.Name?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string InferAlbumFromPath(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            return string.IsNullOrWhiteSpace(dir) ? string.Empty : Path.GetFileName(dir).Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static bool IsSpecificFolderArtist(string? value)
        => !IsWeakMetadataValue(value) && !IsVariousArtistsValue(value);

    private static bool IsWeakMetadataValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim().Trim('[', ']');
        return WeakMetadataValues.Contains(normalized);
    }

    private static bool IsVariousArtistsValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().Trim('[', ']').ToLowerInvariant();
        return normalized is "various artists" or "various" or "va" or "v/a";
    }

    private static string InferTitleFromFilename(string filePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return string.Empty;
        }

        var cleaned = LeadingTrackNumberRegex.Replace(baseName, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? baseName.Trim() : cleaned;
    }

    private static int? NormalizeDurationSeconds(string filePath, string extension, int? durationSeconds)
    {
        if (!durationSeconds.HasValue || durationSeconds.Value <= 0)
        {
            return null;
        }

        if (!IsMp4Family(extension))
        {
            return durationSeconds;
        }

        if (durationSeconds.Value >= 20)
        {
            return durationSeconds;
        }

        try
        {
            var lengthBytes = new FileInfo(filePath).Length;
            if (lengthBytes >= 8L * 1024L * 1024L)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // best effort
        }

        return durationSeconds;
    }

    private static int? ResolveDurationSecondsFromTags(Dictionary<string, List<string>> tags)
    {
        var raw = ReadFirstTagValue(tags, LengthUpperTag, "TLEN", "SHAZAM_DURATION_MS", "SHAZAM_META_DURATION", "SHAZAM_META_TIME", "SHAZAM_META_LENGTH");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Trim();
        if (normalized.Contains(':', StringComparison.Ordinal) && TimeSpan.TryParse(normalized, CultureInfo.InvariantCulture, out var parsedTime))
        {
            return parsedTime.TotalSeconds > 0
                ? (int)Math.Round(parsedTime.TotalSeconds)
                : null;
        }

        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            return null;
        }

        var seconds = value >= 10000d ? value / 1000d : value;
        return seconds > 0 ? (int)Math.Round(seconds) : null;
    }

    private static int? ResolveDurationSecondsWithFfprobe(string filePath, string extension)
    {
        if (!IsMp4Family(extension))
        {
            return null;
        }

        try
        {
            var ffprobePath = ExternalToolResolver.ResolveFfprobePath();
            if (string.IsNullOrWhiteSpace(ffprobePath))
            {
                return null;
            }

            var startInfo = ExternalToolProcessStartInfo.CreateRedirected(ffprobePath);
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("format=duration");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
            startInfo.ArgumentList.Add(filePath);

            using var process = Process.Start(startInfo);

            if (process == null)
            {
                return null;
            }

            if (!process.WaitForExit(3000))
            {
                TryKillProcess(process);
                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            return double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? (int)Math.Round(seconds)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static void TryKillProcess(Process process)
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
            // best effort timeout cleanup
        }
    }

    private static List<string> SplitArtistCredits(IEnumerable<string> rawCredits)
    {
        return ArtistNameNormalizer.ExpandArtistNames(rawCredits);
    }

    private static string? ReadFirstTagValue(Dictionary<string, List<string>> tags, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key) || !tags.TryGetValue(key, out var values) || values.Count == 0)
            {
                continue;
            }

            var value = values.FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry));
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static int? ParsePositiveInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        return int.TryParse(trimmed, out var value) && value > 0 ? value : null;
    }

    private static Task EnsureCoreTagsFromPathAsync(
        string filePath,
        string rootPath,
        bool singleAlbumArtist,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var artist = InferArtistFromPath(filePath, rootPath);
        var artistCredits = string.IsNullOrWhiteSpace(artist)
            ? new List<string>()
            : SplitArtistCredits(new[] { artist });
        var album = InferAlbumFromPath(filePath);
        var title = InferTitleFromFilename(filePath);

        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(album) && string.IsNullOrWhiteSpace(title))
        {
            return Task.CompletedTask;
        }

        try
        {
            var extension = Path.GetExtension(filePath);
            var chapterSnapshot = AtlTagHelper.CaptureChapters(filePath, extension);
            using var file = TagLib.File.Create(filePath);
            var changed = false;
            changed |= TrySetMissingTitle(file.Tag, title);
            changed |= TrySetMissingPerformers(file.Tag, artistCredits);
            changed |= TrySetMissingAlbumArtists(file.Tag, artistCredits, singleAlbumArtist);
            changed |= TrySetMissingAlbum(file.Tag, album);

            if (changed)
            {
                file.Save();
                AtlTagHelper.RestoreChapters(filePath, chapterSnapshot);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // best effort only
        }

        return Task.CompletedTask;
    }

    private static bool TrySetMissingTitle(TagLib.Tag tag, string? title)
    {
        if (!string.IsNullOrWhiteSpace(tag.Title) || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        tag.Title = title;
        return true;
    }

    private static bool TrySetMissingPerformers(TagLib.Tag tag, List<string> artistCredits)
    {
        var hasPerformer = tag.Performers != null && tag.Performers.Any(value => !string.IsNullOrWhiteSpace(value));
        if (hasPerformer || artistCredits.Count == 0)
        {
            return false;
        }

        tag.Performers = artistCredits.ToArray();
        return true;
    }

    private static bool TrySetMissingAlbumArtists(
        TagLib.Tag tag,
        List<string> artistCredits,
        bool singleAlbumArtist)
    {
        var hasAlbumArtist = tag.AlbumArtists != null && tag.AlbumArtists.Any(value => !string.IsNullOrWhiteSpace(value));
        if (hasAlbumArtist || artistCredits.Count == 0)
        {
            return false;
        }

        tag.AlbumArtists = singleAlbumArtist
            ? new[] { artistCredits[0] }
            : artistCredits.ToArray();
        return true;
    }

    private static bool TrySetMissingAlbum(TagLib.Tag tag, string? album)
    {
        if (!string.IsNullOrWhiteSpace(tag.Album) || string.IsNullOrWhiteSpace(album))
        {
            return false;
        }

        tag.Album = album;
        return true;
    }

    private static bool TryParseFilename(string filename, Regex? template, out string artist, out string title)
    {
        artist = "";
        title = "";
        if (template != null)
        {
            var match = template.Match(filename);
            if (match.Success)
            {
                var titleGroup = match.Groups[TitleTag];
                if (titleGroup.Success)
                {
                    title = titleGroup.Value.Trim();
                }
                var artistGroup = match.Groups["artists"];
                if (artistGroup.Success)
                {
                    artist = artistGroup.Value.Trim();
                }
                return !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(artist);
            }
        }

        return false;
    }

    private static void AddTagIfAny(Dictionary<string, List<string>> tags, string key, List<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        tags[key] = values;
    }

    private static List<string> ReadRawTagValuesAny(TagLib.File file, string extension, params string[] rawNames)
    {
        var values = new List<string>();
        foreach (var value in rawNames
                     .SelectMany(rawName => ReadRawTagValues(file, extension, rawName))
                     .Where(value => !values.Contains(value, StringComparer.OrdinalIgnoreCase)))
        {
            values.Add(value);
        }

        return values;
    }

    private static bool HasExistingTags(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var extension = Path.GetExtension(filePath);
            if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                var id3 = (TagLib.Id3v2.Tag?)file.GetTag(TagTypes.Id3v2, false);
                if (id3 == null) return false;
                return TagRawProbe.HasId3Raw(id3, TaggedDateTag);
            }

            if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
            {
                var vorbis = (TagLib.Ogg.XiphComment?)file.GetTag(TagTypes.Xiph, false);
                return vorbis != null && TagRawProbe.HasVorbisRaw(vorbis, TaggedDateTag);
            }

            if (IsMp4Family(extension))
            {
                return Mp4TagHelper.HasRaw(file, TaggedDateTag);
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static TagSettings BuildTagSettings(AutoTagRunnerConfig config, DeezSpoTagSettings runtimeSettings)
    {
        var settings = new TagSettings
        {
            Title = false,
            Artist = false,
            Artists = false,
            Album = false,
            AlbumArtist = false,
            TrackNumber = false,
            TrackTotal = false,
            DiscNumber = false,
            DiscTotal = false,
            Genre = false,
            Label = false,
            Bpm = false,
            Isrc = false,
            Explicit = false,
            Length = false,
            Date = false,
            Year = false,
            Cover = false,
            Barcode = false,
            ReplayGain = false,
            Copyright = false,
            Lyrics = false,
            SyncedLyrics = false,
            Composer = false,
            InvolvedPeople = false,
            Source = false,
            Url = false,
            TrackId = false,
            ReleaseId = false,
            Rating = false,
            SavePlaylistAsCompilation = runtimeSettings.Tags?.SavePlaylistAsCompilation ?? false,
            UseNullSeparator = runtimeSettings.Tags?.UseNullSeparator ?? false,
            SaveID3v1 = runtimeSettings.Tags?.SaveID3v1 ?? true,
            MultiArtistSeparator = runtimeSettings.Tags?.MultiArtistSeparator ?? MultiArtistSeparatorDefault,
            SingleAlbumArtist = runtimeSettings.Tags?.SingleAlbumArtist ?? true,
            CoverDescriptionUTF8 = runtimeSettings.Tags?.CoverDescriptionUTF8 ?? true
        };

        foreach (var tag in config.Tags.Where(tag => TagSettingsAppliers.ContainsKey(tag.Trim())))
        {
            TagSettingsAppliers[tag.Trim()](settings);
        }
        if (WantsArtworkFromSettings(config, runtimeSettings))
        {
            settings.Cover = true;
        }

        return settings;
    }

    private static readonly string[] AlbumIdentitySeedExtensions =
        [".flac", ".mp3", ".m4a", ".mp4", ".aac", ".alac", ".ogg", ".opus", ".wav"];

    private static readonly string[] AlbumIdentityDateRawNames = ["DATE", "TDRC", "TDRL", "TYER"];

    private static void ApplyAlbumIdentityConsensus(AutoTagFileRunContext context, AutoTagTrack track)
    {
        var albumArtist = track.AlbumArtists.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? track.Artists.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        var key = AlbumIdentity.BuildKey(albumArtist, track.Album);
        if (key is null)
        {
            return;
        }

        AlbumIdentity? seed = null;
        if (context.Plan.SeededAlbumIdentityKeys.Add(key))
        {
            seed = TryReadAlbumIdentityFromSiblings(
                context.File,
                TryResolveProspectiveAlbumDirectory(context, track));
        }

        var candidate = new AlbumIdentity(
            AlbumIdentity.FormatReleaseDate(track.ReleaseDate),
            track.AlbumId,
            track.AlbumArtistId);
        var established = context.Plan.AlbumIdentities.Establish(key, candidate, seed);
        if (established.IsEmpty)
        {
            return;
        }

        var establishedDate = AlbumIdentity.ParseReleaseDate(established.ReleaseDate);
        if (establishedDate.HasValue)
        {
            track.ReleaseDate = establishedDate;
        }

        if (!string.IsNullOrWhiteSpace(established.AlbumId))
        {
            track.AlbumId = established.AlbumId;
        }

        if (!string.IsNullOrWhiteSpace(established.AlbumArtistId))
        {
            track.AlbumArtistId = established.AlbumArtistId;
        }
    }

    private static string? TryResolveProspectiveAlbumDirectory(AutoTagFileRunContext context, AutoTagTrack track)
    {
        if (context.Plan.Config.MaterializeToTemplatePath != true)
        {
            return null;
        }

        try
        {
            var separator = ResolveArtistSeparator(context.Plan.Config, context.File);
            var coreTrack = BuildCoreTrack(
                track,
                separator,
                context.Plan.TagSettings.SingleAlbumArtist,
                context.Plan.Settings);
            var pathInfo = BuildTemplatePathInfo(coreTrack, context.Plan.Settings);
            return string.IsNullOrWhiteSpace(pathInfo.FilePath) ? null : pathInfo.FilePath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static AlbumIdentity? TryReadAlbumIdentityFromSiblings(string filePath, string? destinationDirectory)
    {
        var identity = AlbumIdentity.Empty;
        foreach (var directory in EnumerateAlbumIdentitySeedDirectories(filePath, destinationDirectory))
        {
            identity = identity.CoalesceWith(ReadAlbumIdentityFromDirectory(directory, filePath));
            if (!identity.IsEmpty)
            {
                break;
            }
        }

        return identity.IsEmpty ? null : identity;
    }

    private static IEnumerable<string> EnumerateAlbumIdentitySeedDirectories(string filePath, string? destinationDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[] { destinationDirectory, Path.GetDirectoryName(filePath) })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
            {
                continue;
            }

            if (seen.Add(Path.GetFullPath(candidate)))
            {
                yield return candidate;
            }
        }
    }

    private static AlbumIdentity ReadAlbumIdentityFromDirectory(string directory, string filePath)
    {
        var identity = AlbumIdentity.Empty;
        IEnumerable<string> siblings;
        try
        {
            siblings = Directory.EnumerateFiles(directory).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return identity;
        }

        foreach (var sibling in siblings)
        {
            if (PathsReferToSameFile(sibling, filePath))
            {
                continue;
            }

            var extension = Path.GetExtension(sibling);
            if (!AlbumIdentitySeedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var file = TagLib.File.Create(sibling);
                identity = identity.CoalesceWith(new AlbumIdentity(
                    ReadRawTagValuesAny(file, extension, AlbumIdentityDateRawNames).FirstOrDefault(),
                    ReadRawTagValuesAny(file, extension, AlbumIdRawTag).FirstOrDefault(),
                    ReadRawTagValuesAny(file, extension, AlbumArtistIdRawTag).FirstOrDefault()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            if (!identity.IsEmpty)
            {
                break;
            }
        }

        return identity;
    }

    private async Task<TagFileWriteResult> TagFileAsync(
        string filePath,
        AutoTagTrack track,
        TagSettings tagSettings,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        string platformId,
        CancellationToken token)
    {
        EnsureReleaseCategory(track);
        var separator = ResolveSeparatorForFormat(config, Path.GetExtension(filePath));
        var effectiveTagSettings = ApplyOverwriteRules(filePath, tagSettings, config, platformId, track, settings);
        NormalizeTrackArtistsForTagging(track, effectiveTagSettings.SingleAlbumArtist);
        var coreTrack = BuildCoreTrack(track, separator, effectiveTagSettings.SingleAlbumArtist, settings);
        string? tempCoverPath = null;
        var shouldPrepareTemplateArtworkSidecar = ShouldPrepareTemplateArtworkSidecar(config);

        if ((effectiveTagSettings.Cover || shouldPrepareTemplateArtworkSidecar) && !string.IsNullOrWhiteSpace(track.Art))
        {
            tempCoverPath = TryResolveExistingCoverSidecar(filePath, track, coreTrack, config, settings)
                ?? await DownloadCoverAsync(track.Art, token);
        }

        if (effectiveTagSettings.Cover &&
            string.IsNullOrWhiteSpace(tempCoverPath) &&
            !TrackHasEmbeddedArtwork(filePath, config, platformId))
        {
            tempCoverPath = TryResolveFolderArtworkPath(filePath);
        }

        var writeResult = await WriteTagsOnetaggerStyleAsync(
            new TagWriteRequest
            {
                FilePath = filePath,
                SourceTrack = track,
                CoreTrack = coreTrack,
                EffectiveTagSettings = effectiveTagSettings,
                Config = config,
                Settings = settings,
                PlatformId = platformId,
                Separator = separator,
                TempCoverPath = tempCoverPath
            },
            token);
        await EnsureTemplateFoldersAndArtworkSidecarAsync(
            track,
            coreTrack,
            config,
            settings,
            filePath,
            tempCoverPath,
            token);
        if (!IsMp4Family(Path.GetExtension(filePath)))
        {
            writeResult.AttemptedTags.UnionWith(await ApplyCustomTagsAsync(
                filePath,
                track,
                config,
                platformId,
                effectiveTagSettings.UseNullSeparator));
        }

        if (!string.IsNullOrWhiteSpace(tempCoverPath) && !string.Equals(Path.GetDirectoryName(tempCoverPath), Path.GetDirectoryName(filePath), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                IOFile.Delete(tempCoverPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // best effort
            }
        }

        return writeResult;
    }

    private static Track BuildCoreTrack(
        AutoTagTrack track,
        string? separator,
        bool singleAlbumArtist,
        DeezSpoTagSettings settings)
    {
        var artists = track.Artists.Count == 0 ? new List<string> { UnknownArtist } : track.Artists;
        var albumArtists = track.AlbumArtists.Count == 0 ? artists : track.AlbumArtists;
        var album = new Album(track.Album ?? "")
        {
            TrackTotal = track.TrackTotal ?? 0,
            DiscTotal = null,
            Genre = track.Genres.ToList(),
            Label = track.Label,
            ReleaseDate = track.ReleaseDate
        };

        var primaryAlbumArtist = albumArtists
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?.Trim();
        if (string.IsNullOrWhiteSpace(primaryAlbumArtist))
        {
            primaryAlbumArtist = artists[0];
        }

        var albumMainArtists = singleAlbumArtist
            ? new List<string> { primaryAlbumArtist }
            : albumArtists.ToList();

        album.MainArtist = new DeezSpoTag.Core.Models.Artist(primaryAlbumArtist);
        album.Artists = albumMainArtists.ToList();
        album.Artist["Main"] = albumMainArtists.ToList();

        var coreTrack = new Track
        {
            Title = track.Title,
            Artists = artists.ToList(),
            MainArtist = new DeezSpoTag.Core.Models.Artist(artists[0]),
            Album = album,
            TrackNumber = track.TrackNumber ?? 0,
            DiscNumber = track.DiscNumber ?? 0,
            Bpm = track.Bpm ?? 0,
            Explicit = track.Explicit ?? false,
            ISRC = track.Isrc ?? "",
            Duration = (int?)track.Duration?.TotalSeconds ?? 0
        };

        if (singleAlbumArtist && artists.Count > 1)
        {
            coreTrack.Artist["Main"] = new List<string> { artists[0] };
            coreTrack.Artist["Featured"] = artists.Skip(1).ToList();
            coreTrack.MainArtist = new DeezSpoTag.Core.Models.Artist(artists[0]);
        }
        else
        {
            coreTrack.Artist["Main"] = artists.ToList();
        }

        coreTrack.GenerateMainFeatStrings();
        coreTrack.ArtistString = coreTrack.MainArtist?.Name ?? artists[0];
        coreTrack.ArtistsString = string.IsNullOrWhiteSpace(separator) ? string.Join(", ", artists) : string.Join(separator, artists);

        if (track.ReleaseDate.HasValue)
        {
            coreTrack.Date = CustomDate.FromDateTime(track.ReleaseDate.Value);
            coreTrack.DateString = coreTrack.Date.Format("ymd");
        }

        settings.Tags ??= new TagSettings();
        coreTrack.ApplySettings(settings);

        return coreTrack;
    }

    private static void PreserveRicherArtistCreditsFromSource(
        AutoTagAudioInfo sourceInfo,
        AutoTagTrack track,
        DeezSpoTagSettings settings)
    {
        IEnumerable<string> sourceArtistValues = sourceInfo.Artists.Count > 0
            ? sourceInfo.Artists
            : Array.Empty<string>();
        if (sourceInfo.Artists.Count == 0 && !string.IsNullOrWhiteSpace(sourceInfo.Artist))
        {
            sourceArtistValues = new[] { sourceInfo.Artist };
        }

        var sourceArtists = SplitArtistCredits(sourceArtistValues);
        var matchedArtists = SplitArtistCredits(track.Artists);

        if (ShouldPreferSourceArtistCredits(sourceArtists, matchedArtists))
        {
            track.Artists = sourceArtists;
        }
        else if (matchedArtists.Count > 0)
        {
            track.Artists = matchedArtists;
        }

        var normalizedAlbumArtists = SplitArtistCredits(track.AlbumArtists);
        if (normalizedAlbumArtists.Count == 0 && track.Artists.Count > 0)
        {
            normalizedAlbumArtists = track.Artists.ToList();
        }

        var singleAlbumArtist = settings.Tags?.SingleAlbumArtist ?? true;
        if (singleAlbumArtist && normalizedAlbumArtists.Count > 1)
        {
            normalizedAlbumArtists = new List<string> { normalizedAlbumArtists[0] };
        }

        track.AlbumArtists = normalizedAlbumArtists;
    }

    private static void ApplyFolderContextGuards(string filePath, string rootPath, AutoTagTrack track)
    {
        var folderArtist = InferArtistFromPath(filePath, rootPath);
        var folderAlbum = InferAlbumFromPath(filePath);
        var hasSpecificFolderArtist = IsSpecificFolderArtist(folderArtist);
        if (!string.IsNullOrWhiteSpace(folderAlbum) && IsWeakMetadataValue(track.Album))
        {
            track.Album = folderAlbum;
        }

        if (!hasSpecificFolderArtist)
        {
            return;
        }

        var normalizedArtists = SplitArtistCredits(track.Artists);
        if (normalizedArtists.Count == 0
            || normalizedArtists.All(IsWeakMetadataValue)
            || normalizedArtists.All(IsVariousArtistsValue))
        {
            track.Artists = new List<string> { folderArtist };
        }

        var normalizedAlbumArtists = SplitArtistCredits(track.AlbumArtists);
        if (normalizedAlbumArtists.Count == 0
            || normalizedAlbumArtists.All(IsWeakMetadataValue)
            || normalizedAlbumArtists.All(IsVariousArtistsValue))
        {
            track.AlbumArtists = new List<string> { folderArtist };
        }
    }

    private static bool ShouldPreferSourceArtistCredits(List<string> sourceArtists, List<string> matchedArtists)
    {
        if (sourceArtists.Count == 0)
        {
            return false;
        }

        if (matchedArtists.Count == 0)
        {
            return true;
        }

        if (sourceArtists.Count <= matchedArtists.Count)
        {
            return false;
        }

        var sourcePrimary = sourceArtists[0];
        var matchedPrimary = matchedArtists[0];
        if (string.IsNullOrWhiteSpace(sourcePrimary) || string.IsNullOrWhiteSpace(matchedPrimary))
        {
            return true;
        }

        if (string.Equals(sourcePrimary, matchedPrimary, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return sourcePrimary.Contains(matchedPrimary, StringComparison.OrdinalIgnoreCase)
            || matchedPrimary.Contains(sourcePrimary, StringComparison.OrdinalIgnoreCase);
    }

    private static void NormalizeTrackArtistsForTagging(AutoTagTrack track, bool singleAlbumArtist)
    {
        var normalizedArtists = SplitArtistCredits(track.Artists);
        var normalizedAlbumArtists = SplitArtistCredits(track.AlbumArtists);
        if (normalizedArtists.Count > 0 && normalizedArtists.All(IsWeakMetadataValue))
        {
            normalizedArtists.Clear();
        }

        if (normalizedAlbumArtists.Count > 0 && normalizedAlbumArtists.All(IsWeakMetadataValue))
        {
            normalizedAlbumArtists.Clear();
        }

        if (normalizedArtists.Count == 0)
        {
            normalizedArtists = normalizedAlbumArtists.ToList();
        }

        if (normalizedArtists.Count == 0)
        {
            normalizedArtists.Add(UnknownArtist);
        }

        if (normalizedAlbumArtists.Count == 0)
        {
            normalizedAlbumArtists = normalizedArtists.ToList();
        }

        if (singleAlbumArtist && normalizedAlbumArtists.Count > 1)
        {
            normalizedAlbumArtists = new List<string> { normalizedAlbumArtists[0] };
        }

        track.Artists = normalizedArtists;
        track.AlbumArtists = normalizedAlbumArtists;
    }

    private async Task<TagFileWriteResult> WriteTagsOnetaggerStyleAsync(
        TagWriteRequest request,
        CancellationToken token)
    {
        var context = BuildTagWriteExecutionContext(request);
        var chapterSnapshot = AtlTagHelper.CaptureChapters(context.FilePath, context.Extension, _logger);

        using var file = TagLib.File.Create(context.FilePath);
        PrepareId3Version(file, context);

        var tagWriteContext = new TagWriteContext(
            file,
            context.Extension,
            context.Config,
            context.Separator,
            context.PlatformId,
            context.EffectiveTagSettings.UseNullSeparator,
            context.GenreAliasMap,
            context.GenreBlockList,
            context.SplitCompositeGenres,
            context.AttemptedTags);
        ApplyPrimaryTagWrites(tagWriteContext, context);
        ApplyAudioFeatureTagWrites(tagWriteContext, context);
        ApplyGenreAndStyleTagWrites(file, tagWriteContext, context);
        ApplyReleaseAndMetadataTagWrites(file, tagWriteContext, context);
        ApplyTrackAndLyricsTagWrites(file, tagWriteContext, context);
        ApplyAlbumArtTagWrite(file, context);
        file.Save();
        RemoveId3v1TagIfDisabled(file, context);

        AtlTagHelper.RestoreChapters(context.FilePath, chapterSnapshot, _logger);

        var sidecarWriteResult = await WriteLyricsSidecarsAsync(context, token);
        CleanupUpgradedTxtSidecar(context, sidecarWriteResult);

        if (sidecarWriteResult.WroteTtmlSidecar)
        {
            context.AttemptedTags.Add(SupportedTag.TtmlLyrics);
        }

        return new TagFileWriteResult(context.AttemptedTags);
    }

    private static string BuildAtlDashFieldName(string name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? string.Empty
            : $"----:com.apple.iTunes:{name.Trim()}";
    }

    private static TagWriteExecutionContext BuildTagWriteExecutionContext(TagWriteRequest request)
    {
        var extension = Path.GetExtension(request.FilePath);
        var enabledTags = BuildConfiguredTagSet(request.Config.Tags);
        var normalizeGenreTags = request.Settings.NormalizeGenreTags;
        var genreAliasMap = normalizeGenreTags
            ? GenreTagAliasNormalizer.BuildAliasMap(request.Settings.GenreTagAliasRules)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var genreBlockList = GenreTagAliasNormalizer.NormalizeBlockedValues(request.Settings.GenreTagBlockList);
        var allowsSyncedByToggle = request.Settings.SyncedLyrics;
        var allowsUnsyncedByToggle = request.Settings.SaveLyrics;
        var allowsLyricsBySettings = allowsSyncedByToggle || allowsUnsyncedByToggle;
        var selectedLyricsTypes = ParseLyricsTypeSelection(request.Settings.LrcType);
        var allowsSyncedType = allowsSyncedByToggle
            && (selectedLyricsTypes.Contains(LyricsTag) || selectedLyricsTypes.Contains(SyllableLyricsType));
        var allowsUnsyncedType = allowsUnsyncedByToggle && selectedLyricsTypes.Contains(UnsyncedLyricsType);
        var allowsTtmlByFormat = allowsSyncedByToggle
            && selectedLyricsTypes.Contains(TtmlLyricsType)
            && ParseLyricsFormatSelection(request.Settings.LrcFormat).Contains("ttml");
        var allowsLrcByFormat = allowsSyncedByToggle
            && ParseLyricsFormatSelection(request.Settings.LrcFormat).Contains("lrc");
        var sidecarState = GetLyricsSidecarState(request.FilePath);

        return new TagWriteExecutionContext
        {
            FilePath = request.FilePath,
            SourceTrack = request.SourceTrack,
            CoreTrack = request.CoreTrack,
            EffectiveTagSettings = request.EffectiveTagSettings,
            Config = request.Config,
            Settings = request.Settings,
            PlatformId = request.PlatformId,
            Separator = request.Separator,
            TempCoverPath = request.TempCoverPath,
            Extension = extension,
            EnabledTags = enabledTags,
            GenreAliasMap = genreAliasMap,
            GenreBlockList = genreBlockList,
            SplitCompositeGenres = normalizeGenreTags,
            AllowsLyricsBySettings = allowsLyricsBySettings,
            AllowsSyncedType = allowsSyncedType,
            AllowsUnsyncedType = allowsUnsyncedType,
            AllowsLrcByFormat = allowsLrcByFormat,
            AllowsTtmlByFormat = allowsTtmlByFormat,
            SidecarState = sidecarState,
            ShouldSkipEmbeddedLyrics = sidecarState.HasAny
        };
    }

    private static void PrepareId3Version(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
        id3.Version = context.Config.Id3v24 ? (byte)4 : (byte)3;
    }

    private static void RemoveId3v1TagIfDisabled(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || context.EffectiveTagSettings.SaveID3v1)
        {
            return;
        }

        file.RemoveTags(TagTypes.Id3v1);
        file.Save();
    }

    private static void ApplyPrimaryTagWrites(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        WriteTitleTag(tagWriteContext, context);
        WriteVersionTag(tagWriteContext, context);
        WriteArtistTag(tagWriteContext, context);
        WriteArtistsTag(tagWriteContext, context);
        WriteAlbumArtistTag(tagWriteContext, context);
        WriteAlbumTag(tagWriteContext, context);
        WriteKeyTag(tagWriteContext, context);
        WriteBpmTag(tagWriteContext, context);
        WriteLabelTag(tagWriteContext, context);
    }

    private static List<string> ResolveArtistValues(Track coreTrack, TagSettings tagSettings)
    {
        var artists = coreTrack.Artists
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (artists.Count == 0)
        {
            return new List<string>();
        }

        if (tagSettings.SingleAlbumArtist)
        {
            var primary = coreTrack.MainArtist?.Name;
            return string.IsNullOrWhiteSpace(primary)
                ? new List<string> { artists[0] }
                : new List<string> { primary.Trim() };
        }

        if (string.Equals(tagSettings.MultiArtistSeparator, MultiArtistSeparatorDefault, StringComparison.OrdinalIgnoreCase))
        {
            return artists;
        }

        if (string.Equals(tagSettings.MultiArtistSeparator, MultiArtistSeparatorNothing, StringComparison.OrdinalIgnoreCase))
        {
            var primary = coreTrack.MainArtist?.Name;
            return string.IsNullOrWhiteSpace(primary)
                ? new List<string> { artists[0] }
                : new List<string> { primary.Trim() };
        }

        var joined = string.IsNullOrWhiteSpace(coreTrack.ArtistsString)
            ? string.Join(", ", artists)
            : coreTrack.ArtistsString;
        return new List<string> { joined };
    }

    private static List<string> ResolveAlbumArtistValues(Track coreTrack)
    {
        var primary = coreTrack.Album?.MainArtist?.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return new List<string> { primary };
        }

        var mainArtists = coreTrack.Artist.GetValueOrDefault("Main", new List<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (mainArtists.Count > 0)
        {
            return new List<string> { mainArtists[0] };
        }

        var artists = coreTrack.Artists
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (artists.Count > 0)
        {
            return new List<string> { artists[0] };
        }

        return new List<string> { UnknownArtist };
    }

    private static void WriteTitleTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(TitleTag) || !context.EffectiveTagSettings.Title)
        {
            return;
        }

        var titleValue = context.CoreTrack.Title;
        if (!context.Config.ShortTitle
            && !string.IsNullOrWhiteSpace(context.SourceTrack.Version)
            && !titleValue.Contains(context.SourceTrack.Version, StringComparison.OrdinalIgnoreCase))
        {
            titleValue = $"{titleValue} ({context.SourceTrack.Version})";
        }

        SetField(tagWriteContext, new TagFieldBinding("TIT2", TitleUpperTag, "©nam", SupportedTag.Title), new List<string> { titleValue });
    }

    private static void WriteVersionTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(VersionTag) || string.IsNullOrWhiteSpace(context.SourceTrack.Version))
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TIT3", "SUBTITLE", "desc", SupportedTag.Version), new List<string> { context.SourceTrack.Version });
    }

    private static void WriteArtistTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ArtistTag) || !context.EffectiveTagSettings.Artist)
        {
            return;
        }

        var artistValues = ResolveArtistValues(context.CoreTrack, context.EffectiveTagSettings);
        if (artistValues.Count == 0)
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TPE1", ArtistUpperTag, "©ART", SupportedTag.Artist), artistValues);
    }

    private static void WriteArtistsTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ArtistsTag) || !context.EffectiveTagSettings.Artists)
        {
            return;
        }

        if (string.Equals(
                context.EffectiveTagSettings.MultiArtistSeparator,
                MultiArtistSeparatorDefault,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var artists = context.CoreTrack.Artists
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (artists.Count == 0)
        {
            return;
        }

        var value = context.EffectiveTagSettings.MultiArtistSeparator switch
        {
            MultiArtistSeparatorNothing => context.CoreTrack.MainArtist?.Name ?? artists[0],
            MultiArtistSeparatorDefault => string.Join(", ", artists),
            _ when !string.IsNullOrWhiteSpace(context.CoreTrack.ArtistsString) => context.CoreTrack.ArtistsString,
            _ => string.Join(", ", artists)
        };
        SetRawIfAllowed(tagWriteContext, ArtistsTag, "ARTISTS", new List<string> { value });
    }

    private static void WriteAlbumArtistTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(AlbumArtistTag) || !context.EffectiveTagSettings.AlbumArtist)
        {
            return;
        }

        var albumArtistValues = ResolveAlbumArtistValues(context.CoreTrack);
        SetField(
            tagWriteContext,
            new TagFieldBinding("TPE2", AlbumArtistUpperTag, "aART", SupportedTag.AlbumArtist),
            albumArtistValues);
    }

    private static void WriteAlbumTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(AlbumTag) || !context.EffectiveTagSettings.Album || context.CoreTrack.Album == null)
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TALB", AlbumUpperTag, "©alb", SupportedTag.Album), new List<string> { context.CoreTrack.Album.Title });
    }

    private static void WriteKeyTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("key") || string.IsNullOrWhiteSpace(context.SourceTrack.Key))
        {
            return;
        }

        var keyValue = context.Config.Camelot ? ToCamelot(context.SourceTrack.Key) : context.SourceTrack.Key;
        SetField(tagWriteContext, new TagFieldBinding("TKEY", "INITIALKEY", InitialKeyRawTag, SupportedTag.Key), new List<string> { keyValue });
    }

    private static void WriteBpmTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("bpm") || !context.SourceTrack.Bpm.HasValue)
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TBPM", "BPM", "tmpo", SupportedTag.BPM), new List<string> { context.SourceTrack.Bpm.Value.ToString() });
    }

    private static void WriteLabelTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(LabelTag) || string.IsNullOrWhiteSpace(context.SourceTrack.Label))
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TPUB", LabelUpperTag, LabelUpperTag, SupportedTag.Label), new List<string> { context.SourceTrack.Label });
    }

    private static void ApplyAudioFeatureTagWrites(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        WriteAudioFeatureTag(tagWriteContext, context, "danceability", DanceabilityTag, SupportedTag.Danceability, context.SourceTrack.Danceability);
        WriteAudioFeatureTag(tagWriteContext, context, "energy", EnergyTag, SupportedTag.Energy, context.SourceTrack.Energy);
        WriteAudioFeatureTag(tagWriteContext, context, "valence", ValenceTag, SupportedTag.Valence, context.SourceTrack.Valence);
        WriteAudioFeatureTag(tagWriteContext, context, "acousticness", AcousticnessTag, SupportedTag.Acousticness, context.SourceTrack.Acousticness);
        WriteAudioFeatureTag(tagWriteContext, context, "instrumentalness", InstrumentalnessTag, SupportedTag.Instrumentalness, context.SourceTrack.Instrumentalness);
        WriteAudioFeatureTag(tagWriteContext, context, "speechiness", SpeechinessTag, SupportedTag.Speechiness, context.SourceTrack.Speechiness);
        WriteAudioFeatureTag(tagWriteContext, context, "loudness", LoudnessTag, SupportedTag.Loudness, context.SourceTrack.Loudness);
        WriteAudioFeatureTag(tagWriteContext, context, "tempo", TempoTag, SupportedTag.Tempo, context.SourceTrack.Tempo);
        WriteAudioFeatureTag(tagWriteContext, context, "liveness", LivenessTag, SupportedTag.Liveness, context.SourceTrack.Liveness);

        if (context.EnabledTags.Contains("timeSignature") && context.SourceTrack.TimeSignature.HasValue)
        {
            SetRaw(
                tagWriteContext,
                TimeSignatureTag,
                SupportedTag.TimeSignature,
                new List<string> { context.SourceTrack.TimeSignature.Value.ToString(CultureInfo.InvariantCulture) });
        }
    }

    private static void WriteAudioFeatureTag(
        TagWriteContext tagWriteContext,
        TagWriteExecutionContext context,
        string enabledTag,
        string rawTag,
        SupportedTag supportedTag,
        double? value)
    {
        if (!context.EnabledTags.Contains(enabledTag) || !value.HasValue)
        {
            return;
        }

        SetRaw(tagWriteContext, rawTag, supportedTag, new List<string> { FormatAudioFeature(value.Value) });
    }

    private static void ApplyGenreAndStyleTagWrites(
        TagLib.File file,
        TagWriteContext tagWriteContext,
        TagWriteExecutionContext context)
    {
        var genres = SanitizeGenres(context.CoreTrack.Album?.Genre ?? new List<string>(), context.GenreAliasMap, context.GenreBlockList, context.SplitCompositeGenres);
        var styles = NormalizeStyleValues(context.SourceTrack.Styles, context.Separator);
        (genres, styles) = ApplyStylesOptions(genres, styles, context.Config.StylesOptions);

        if (context.EnabledTags.Contains(GenreTag) && context.EffectiveTagSettings.Genre && genres.Count > 0)
        {
            if (context.Config.MergeGenres)
            {
                var existing = SanitizeGenres(ReadExistingGenre(context.FilePath), context.GenreAliasMap, context.GenreBlockList, context.SplitCompositeGenres);
                var genreSet = new HashSet<string>(genres, StringComparer.OrdinalIgnoreCase);
                genres.AddRange(existing.Where(genreSet.Add));
            }

            genres = SanitizeGenres(genres, context.GenreAliasMap, context.GenreBlockList, context.SplitCompositeGenres);
            if (context.Config.CapitalizeGenres)
            {
                genres = genres.Select(CapitalizeGenre).ToList();
            }
            genres = GenreTagAliasNormalizer.DedupeValues(genres, context.GenreBlockList);

            SetField(tagWriteContext, new TagFieldBinding("TCON", Mp4GenreTag, "©gen", SupportedTag.Genre), genres);
        }

        if (!context.EnabledTags.Contains(StyleTag) || styles.Count == 0)
        {
            return;
        }

        var styleTagName = ResolveStylesTagName(context.Config, context.Extension);
        var styleValues = styles;
        if (context.Config.MergeGenres)
        {
            var existingStyles = NormalizeStyleValues(
                ReadExistingRawTag(file, context.Extension, styleTagName),
                context.Separator);
            var existingStyleSet = new HashSet<string>(existingStyles, StringComparer.OrdinalIgnoreCase);
            existingStyles.AddRange(styleValues.Where(existingStyleSet.Add));
            styleValues = existingStyles;
        }

        var rawName = context.Config.StylesOptions.Equals("customTag", StringComparison.OrdinalIgnoreCase)
            ? styleTagName
            : ResolveFieldRawName(SupportedTag.Style, ResolveFormatName(context.Extension), context.Config);
        SetRaw(tagWriteContext, rawName, SupportedTag.Style, styleValues);
    }

    private static List<string> NormalizeStyleValues(IEnumerable<string> values, string separator)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var effectiveSeparator = string.IsNullOrEmpty(separator) ? "," : separator;
            var parts = value.Split(
                effectiveSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0 && seen.Add(trimmed))
                {
                    normalized.Add(trimmed);
                }
            }
        }

        return normalized;
    }

    private static (List<string> Genres, List<string> Styles) ApplyStylesOptions(
        List<string> genres,
        List<string> styles,
        string stylesOption)
    {
        switch (stylesOption.ToLowerInvariant())
        {
            case "onlygenres":
                styles = new List<string>();
                break;
            case "onlystyles":
                genres = new List<string>();
                break;
            case "mergetogenres":
                var genreSet = new HashSet<string>(genres, StringComparer.OrdinalIgnoreCase);
                genres.AddRange(styles.Where(genreSet.Add));
                break;
            case "mergetostyles":
                var styleSet = new HashSet<string>(styles, StringComparer.OrdinalIgnoreCase);
                styles.AddRange(genres.Where(styleSet.Add));
                break;
            case "stylestogenre":
                genres = styles.ToList();
                break;
            case "genrestostyle":
                styles = genres.ToList();
                break;
        }

        return (genres, styles);
    }

    private static void ApplyReleaseAndMetadataTagWrites(
        TagLib.File file,
        TagWriteContext tagWriteContext,
        TagWriteExecutionContext context)
    {
        WriteReleaseDateTag(file, context);
        WritePublishDateTag(file, context);
        WriteUrlTag(tagWriteContext, context);
        WriteTrackIdTag(tagWriteContext, context);
        WriteReleaseIdTag(tagWriteContext, context);
        WriteSourceIdentityTags(tagWriteContext, context);
        WriteCatalogNumberTag(tagWriteContext, context);
        WriteDurationTag(tagWriteContext, context);
        WriteRemixerTag(tagWriteContext, context);
        WriteIsrcTag(tagWriteContext, context);
        WriteMoodTag(tagWriteContext, context);
        WriteActivityTag(tagWriteContext, context);
    }

    private static void WriteReleaseDateTag(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ReleaseDateTag) || !context.SourceTrack.ReleaseDate.HasValue)
        {
            return;
        }

        WriteDate(
            file,
            context.Extension,
            ReleaseDateTag,
            context.SourceTrack.ReleaseDate.Value,
            SupportedTag.ReleaseDate,
            context.Config,
            context.EffectiveTagSettings.UseNullSeparator);
        MarkAttemptedIfPresent(context, file, SupportedTag.ReleaseDate);
    }

    private static void WritePublishDateTag(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(PublishDateTag) || !context.SourceTrack.PublishDate.HasValue)
        {
            return;
        }

        WriteDate(
            file,
            context.Extension,
            PublishDateTag,
            context.SourceTrack.PublishDate.Value,
            SupportedTag.PublishDate,
            context.Config,
            context.EffectiveTagSettings.UseNullSeparator);
        MarkAttemptedIfPresent(context, file, SupportedTag.PublishDate);
    }

    private static void WriteUrlTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("url") || string.IsNullOrWhiteSpace(context.SourceTrack.Url))
        {
            return;
        }

        var url = context.SourceTrack.Url;
        if (string.Equals(context.PlatformId, SpotifyPlatform, StringComparison.OrdinalIgnoreCase))
        {
            url = NormalizeSpotifyTrackUrl(url);
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }
        }

        SetRaw(tagWriteContext, WwwAudioFileTag, SupportedTag.URL, new List<string> { url });

        if (!string.Equals(context.PlatformId, SpotifyPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var existingSpotifyUrls = ReadRawTagValues(tagWriteContext.File, context.Extension, SpotifyUrlTag);
        var shouldWriteSpotifyUrl = ShouldOverwriteTag(context.Config, SupportedTag.URL)
            || existingSpotifyUrls.Count == 0
            || existingSpotifyUrls.Any(existing => NormalizeSpotifyTrackUrl(existing) == null);
        if (shouldWriteSpotifyUrl)
        {
            SetRaw(tagWriteContext, SpotifyUrlTag, SupportedTag.URL, new List<string> { url }, force: true);
        }
    }

    private static void WriteTrackIdTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(TrackIdTag) || string.IsNullOrWhiteSpace(context.SourceTrack.TrackId))
        {
            return;
        }

        SetRaw(
            tagWriteContext,
            $"{context.PlatformId.ToUpperInvariant()}_TRACK_ID",
            SupportedTag.TrackId,
            new List<string> { context.SourceTrack.TrackId });
    }

    private static void WriteReleaseIdTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ReleaseIdTag) || string.IsNullOrWhiteSpace(context.SourceTrack.ReleaseId))
        {
            return;
        }

        SetRaw(
            tagWriteContext,
            $"{context.PlatformId.ToUpperInvariant()}_RELEASE_ID",
            SupportedTag.ReleaseId,
            new List<string> { context.SourceTrack.ReleaseId });
    }

    private static void WriteSourceIdentityTags(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        WriteSingleRawTag(tagWriteContext, context, RecordingIdTag, SupportedTag.RecordingId, RecordingIdRawTag, context.SourceTrack.RecordingId);
        WriteSingleRawTag(tagWriteContext, context, ArtistIdTag, SupportedTag.ArtistId, ArtistIdRawTag, context.SourceTrack.ArtistId);
        WriteSingleRawTag(tagWriteContext, context, AlbumArtistIdTag, SupportedTag.AlbumArtistId, AlbumArtistIdRawTag, context.SourceTrack.AlbumArtistId);
        WriteSingleRawTag(tagWriteContext, context, ReleaseGroupIdTag, SupportedTag.ReleaseGroupId, ReleaseGroupIdRawTag, context.SourceTrack.ReleaseGroupId);
        WriteSingleRawTag(tagWriteContext, context, AlbumIdTag, SupportedTag.AlbumId, AlbumIdRawTag, context.SourceTrack.AlbumId);
        WriteSingleRawTag(tagWriteContext, context, ReleaseStatusTag, SupportedTag.ReleaseStatus, ReleaseStatusRawTag, context.SourceTrack.ReleaseStatus);
        WriteSingleRawTag(tagWriteContext, context, ReleaseCountryTag, SupportedTag.ReleaseCountry, ReleaseCountryRawTag, context.SourceTrack.ReleaseCountry);
        WriteSingleRawTag(tagWriteContext, context, BarcodeTag, SupportedTag.Barcode, BarcodeRawTag, context.SourceTrack.Barcode);
        if (context.EnabledTags.Contains(MediaTag) && context.SourceTrack.Media.Count > 0)
        {
            SetRaw(tagWriteContext, MediaRawTag, SupportedTag.Media, context.SourceTrack.Media);
        }
    }

    private static void WriteSingleRawTag(
        TagWriteContext tagWriteContext,
        TagWriteExecutionContext context,
        string tagKey,
        SupportedTag supportedTag,
        string rawTagName,
        string? value)
    {
        if (!context.EnabledTags.Contains(tagKey) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        SetRaw(tagWriteContext, rawTagName, supportedTag, new List<string> { value });
    }

    private static void WriteCatalogNumberTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(CatalogNumberTag) || string.IsNullOrWhiteSpace(context.SourceTrack.CatalogNumber))
        {
            return;
        }

        SetField(
            tagWriteContext,
            new TagFieldBinding(CatalogNumberUpperTag, CatalogNumberUpperTag, CatalogNumberUpperTag, SupportedTag.CatalogNumber),
            new List<string> { context.SourceTrack.CatalogNumber });
    }

    private static void WriteDurationTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(DurationTag) || !context.SourceTrack.Duration.HasValue)
        {
            return;
        }

        var totalMilliseconds = ((int)Math.Round(context.SourceTrack.Duration.Value.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture);
        SetField(
            tagWriteContext,
            new TagFieldBinding("TLEN", LengthUpperTag, LengthUpperTag, SupportedTag.Duration),
            new List<string> { totalMilliseconds });
    }

    private static void WriteRemixerTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(RemixerTag) || context.SourceTrack.Remixers.Count == 0)
        {
            return;
        }

        SetField(
            tagWriteContext,
            new TagFieldBinding("TPE4", RemixerUpperTag, RemixerUpperTag, SupportedTag.Remixer),
            context.SourceTrack.Remixers.ToList());
    }

    private static void WriteIsrcTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("isrc") || string.IsNullOrWhiteSpace(context.SourceTrack.Isrc))
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TSRC", "ISRC", "ISRC", SupportedTag.ISRC), new List<string> { context.SourceTrack.Isrc });
    }

    private static void WriteMoodTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("mood") || string.IsNullOrWhiteSpace(context.SourceTrack.Mood))
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TMOO", "MOOD", "MOOD", SupportedTag.Mood), new List<string> { context.SourceTrack.Mood });
    }

    private static void WriteActivityTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("activity") || string.IsNullOrWhiteSpace(context.SourceTrack.Activity))
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("ACTIVITY", "ACTIVITY", "ACTIVITY", SupportedTag.Activity), new List<string> { context.SourceTrack.Activity });
    }

    private static void ApplyTrackAndLyricsTagWrites(
        TagLib.File file,
        TagWriteContext tagWriteContext,
        TagWriteExecutionContext context)
    {
        WriteDiscNumberTag(file, context);
        WriteDiscTotalTag(file, context);
        WriteTrackNumberTag(file, context);
        WriteBarcodeTag(tagWriteContext, context);
        WriteReplayGainTag(tagWriteContext, context);
        WriteCopyrightTag(tagWriteContext, context);
        WriteComposerTag(tagWriteContext, context);
        WriteLyricistTag(tagWriteContext, context);
        WriteInvolvedPeopleTag(tagWriteContext, context);
        WritePublisherTag(tagWriteContext, context);
        WriteDescriptionTag(tagWriteContext, context);
        WriteSourceTag(tagWriteContext, context);
        WriteRatingTag(tagWriteContext, context);
        WriteLanguageTag(tagWriteContext, context);
        WriteSyncedLyrics(file, context);
        WriteUnsyncedLyrics(file, context);
        WriteExplicitTag(tagWriteContext, context);
        WriteOtherTags(tagWriteContext, context);
        WriteMetaTag(tagWriteContext, context);
    }

    private static void WriteDiscNumberTag(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(DiscNumberTag)
            || !context.EffectiveTagSettings.DiscNumber
            || !context.SourceTrack.DiscNumber.HasValue)
        {
            return;
        }

        SetTrackNumber(
            file,
            context,
            context.SourceTrack.DiscNumber.Value,
            ResolveFirstPositiveInt(context.SourceTrack, DiscTotalTag, DiscTotalRawTag),
            SupportedTag.DiscNumber,
            isDisc: true);
        MarkAttemptedIfPresent(context, file, SupportedTag.DiscNumber);
        MarkAttemptedIfPresent(context, file, SupportedTag.DiscTotal);
    }

    private static void WriteDiscTotalTag(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(DiscTotalTag)
            || !context.EffectiveTagSettings.DiscTotal)
        {
            return;
        }

        var total = context.SourceTrack.DiscTotal is > 0
            ? context.SourceTrack.DiscTotal
            : ResolveFirstPositiveInt(context.SourceTrack, DiscTotalTag, DiscTotalRawTag);
        if (!total.HasValue)
        {
            return;
        }

        SetDiscTotal(file, context, total.Value);
        MarkAttemptedIfPresent(context, file, SupportedTag.DiscTotal);
    }

    private static void WriteTrackNumberTag(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(TrackNumberTag)
            || !context.EffectiveTagSettings.TrackNumber
            || !context.SourceTrack.TrackNumber.HasValue)
        {
            return;
        }

        var total = context.EnabledTags.Contains(TrackTotalTag)
            && context.EffectiveTagSettings.TrackTotal
            && context.SourceTrack.TrackTotal is > 0
            ? context.SourceTrack.TrackTotal
            : null;
        SetTrackNumber(
            file,
            context,
            context.SourceTrack.TrackNumber.Value,
            total,
            SupportedTag.TrackNumber,
            isDisc: false);
        MarkAttemptedIfPresent(context, file, SupportedTag.TrackNumber);
        MarkAttemptedIfPresent(context, file, SupportedTag.TrackTotal);
    }

    private static void WriteBarcodeTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(BarcodeTag) || !context.EffectiveTagSettings.Barcode)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, BarcodeTag, "upc", BarcodeRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, BarcodeTag, BarcodeRawTag, values);
    }

    private static void WriteReplayGainTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ReplayGainTag) || !context.EffectiveTagSettings.ReplayGain)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, ReplayGainTag, ReplayGainRawTag, "gain");
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, ReplayGainTag, ReplayGainRawTag, values);
    }

    private static void WriteCopyrightTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(CopyrightTag) || !context.EffectiveTagSettings.Copyright)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, CopyrightTag, CopyrightRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, CopyrightTag, CopyrightRawTag, values);
    }

    private static void WriteComposerTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ComposerTag) || !context.EffectiveTagSettings.Composer)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, ComposerTag, ComposerUpperTag, "TCOM");
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, ComposerTag, ResolveComposerRawName(context.Extension), values);
    }

    private static void WriteLyricistTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(LyricistTag) || !context.EffectiveTagSettings.Lyricist)
        {
            return;
        }

        var values = ResolveFirstClassOrOtherValues(context.SourceTrack.Lyricist, context.SourceTrack, LyricistTag, LyricistRawTag, "TEXT");
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, LyricistTag, ResolveLyricistRawName(context.Extension), values);
    }

    private static void WriteInvolvedPeopleTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(InvolvedPeopleTag) || !context.EffectiveTagSettings.InvolvedPeople)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, InvolvedPeopleTag, InvolvedPeopleRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, InvolvedPeopleTag, InvolvedPeopleRawTag, values);
    }

    private static void WritePublisherTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(PublisherTag) || !context.EffectiveTagSettings.Publisher)
        {
            return;
        }

        var values = ResolveFirstClassOrOtherValues(context.SourceTrack.Publisher, context.SourceTrack, PublisherTag, PublisherRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, PublisherTag, PublisherRawTag, values);
    }

    private static void WriteDescriptionTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(DescriptionTag) || !context.EffectiveTagSettings.Description)
        {
            return;
        }

        var values = ResolveFirstClassOrOtherValues(context.SourceTrack.Description, context.SourceTrack, DescriptionTag, DescriptionRawTag, CommentRawTag);
        if (values.Count == 0)
        {
            return;
        }

        var rawName = IsMp4Family(context.Extension) ? "ldes" : DescriptionRawTag;
        SetRawIfAllowed(tagWriteContext, DescriptionTag, rawName, values);
    }

    private static void WriteSourceTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(SourceTag) || !context.EffectiveTagSettings.Source)
        {
            return;
        }

        if (string.Equals(context.PlatformId, ShazamPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var sourceValues = ResolveOtherValues(context.SourceTrack, SourceTag);
        if (sourceValues.Count == 0)
        {
            sourceValues.Add(context.PlatformId.ToUpperInvariant());
        }

        SetRawIfAllowed(tagWriteContext, SourceTag, SourceRawTag, sourceValues);

        var sourceIdValues = ResolveOtherValues(context.SourceTrack, "sourceId", "SOURCE_ID", SourceIdRawTag);
        if (sourceIdValues.Count == 0 && !string.IsNullOrWhiteSpace(context.SourceTrack.TrackId))
        {
            sourceIdValues.Add(context.SourceTrack.TrackId);
        }
        if (sourceIdValues.Count > 0)
        {
            SetRawIfAllowed(tagWriteContext, SourceTag, SourceIdRawTag, sourceIdValues);
        }
    }

    private static void WriteRatingTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(RatingTag) || !context.EffectiveTagSettings.Rating)
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, RatingTag, RatingRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, RatingTag, RatingRawTag, values);
    }

    private static void WriteLanguageTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(LanguageTag))
        {
            return;
        }

        var values = ResolveOtherValues(context.SourceTrack, LanguageTag, LanguageRawTag);
        if (values.Count == 0)
        {
            return;
        }

        SetRawIfAllowed(tagWriteContext, LanguageTag, LanguageRawTag, values);
    }

    private static void WriteSyncedLyrics(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!ShouldWriteSyncedLyrics(context))
        {
            return;
        }

        if (WriteLyrics(file, context.Extension, context.SourceTrack, true, context.Config))
        {
            context.AttemptedTags.Add(SupportedTag.SyncedLyrics);
        }
    }

    private static void WriteUnsyncedLyrics(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!ShouldWriteUnsyncedLyrics(context))
        {
            return;
        }

        if (WriteLyrics(file, context.Extension, context.SourceTrack, false, context.Config))
        {
            context.AttemptedTags.Add(SupportedTag.UnsyncedLyrics);
        }
    }

    private static void WriteExplicitTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(ExplicitTag) || !context.SourceTrack.Explicit.HasValue)
        {
            return;
        }

        SetRaw(
            tagWriteContext,
            ItunesAdvisoryTag,
            SupportedTag.Explicit,
            new List<string> { context.SourceTrack.Explicit.Value ? "1" : "0" });
    }

    private static void WriteOtherTags(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(OtherTagsTag)
            && !context.EnabledTags.Contains(ReleaseTypeTag))
        {
            return;
        }

        if (context.EnabledTags.Contains(OtherTagsTag))
        {
            foreach (var rawName in context.SourceTrack.RawTagsToRemove)
            {
                RemoveRawTagValues(tagWriteContext, rawName);
            }
        }

        if (context.SourceTrack.Other.Count == 0)
        {
            return;
        }

        foreach (var kvp in context.SourceTrack.Other)
        {
            if (IsNonPersistedOtherRawKey(kvp.Key))
            {
                continue;
            }

            var isReleaseType = kvp.Key.Equals(ReleaseTypeRawTag, StringComparison.OrdinalIgnoreCase);
            if (isReleaseType && !HasReleaseTypeTagEnabled(context.EnabledTags))
            {
                continue;
            }

            if (!isReleaseType && !context.EnabledTags.Contains(OtherTagsTag))
            {
                continue;
            }

            if (!ShouldAllowLyricsOtherTagKey(
                    kvp.Key,
                    context.AllowsLyricsBySettings,
                    context.AllowsSyncedType,
                    context.AllowsUnsyncedType,
                    context.AllowsTtmlByFormat,
                    !context.ShouldSkipEmbeddedLyrics))
            {
                continue;
            }

            SetRaw(tagWriteContext, kvp.Key, isReleaseType ? SupportedTag.ReleaseType : SupportedTag.OtherTags, kvp.Value.ToList());
        }
    }

    private static void WriteMetaTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(MetaTagsTag))
        {
            return;
        }

        SetRaw(tagWriteContext, TaggedDateTag, SupportedTag.MetaTags, new List<string> { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}_AT" });
    }

    private static bool ShouldWriteSyncedLyrics(TagWriteExecutionContext context)
    {
        return context.EnabledTags.Contains(SyncedLyricsTag)
            && context.EffectiveTagSettings.SyncedLyrics
            && context.AllowsLyricsBySettings
            && context.AllowsSyncedType
            && !context.ShouldSkipEmbeddedLyrics;
    }

    private static bool ShouldWriteUnsyncedLyrics(TagWriteExecutionContext context)
    {
        return context.EnabledTags.Contains(UnsyncedLyricsTag)
            && context.EffectiveTagSettings.Lyrics
            && context.AllowsLyricsBySettings
            && context.AllowsUnsyncedType
            && !context.ShouldSkipEmbeddedLyrics;
    }

    private static void ApplyAlbumArtTagWrite(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(AlbumArtTag) || !context.EffectiveTagSettings.Cover || string.IsNullOrWhiteSpace(context.TempCoverPath))
        {
            return;
        }

        var tempCoverPath = context.TempCoverPath;
        if (ShouldOverwriteTag(context.Config, SupportedTag.AlbumArt)
            || !HasTag(file, context.Extension, SupportedTag.AlbumArt, context.Config, context.PlatformId))
        {
            ApplyAlbumArt(file, tempCoverPath, context.EffectiveTagSettings.CoverDescriptionUTF8);
        }

        MarkAttemptedIfPresent(context, file, SupportedTag.AlbumArt);
    }

    private static void MarkAttemptedIfPresent(TagWriteExecutionContext context, TagLib.File file, SupportedTag tag)
    {
        if (HasTag(file, context.Extension, tag, context.Config, context.PlatformId))
        {
            context.AttemptedTags.Add(tag);
        }
    }

    private static TrackPathInfo BuildTemplatePathInfo(
        Track coreTrack,
        DeezSpoTagSettings settings)
    {
        var downloadType = string.IsNullOrWhiteSpace(coreTrack.Album?.Title) ? "track" : "album";
        var pathInfo = PathTemplateGenerator.GeneratePath(coreTrack, downloadType, settings);
        if (!string.IsNullOrWhiteSpace(pathInfo.CoverPath)
            || !settings.CreateAlbumFolder
            || string.IsNullOrWhiteSpace(coreTrack.Album?.Title))
        {
            return pathInfo;
        }

        var albumParentPath = !string.IsNullOrWhiteSpace(pathInfo.ArtistPath)
            ? pathInfo.ArtistPath
            : settings.DownloadLocation ?? ".";
        var albumName = PathTemplateGenerator.GenerateAlbumName(
            settings.AlbumNameTemplate,
            coreTrack.Album,
            settings,
            coreTrack.Playlist);
        if (string.IsNullOrWhiteSpace(albumName))
        {
            return pathInfo;
        }

        pathInfo.CoverPath = Path.Join(albumParentPath, albumName);
        return pathInfo;
    }

    private static string MaterializeFileToTemplatePath(
        string sourcePath,
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        TagSettings tagSettings)
    {
        if (config.MaterializeToTemplatePath != true)
        {
            return sourcePath;
        }

        var separator = ResolveArtistSeparator(config, sourcePath);
        var coreTrack = BuildCoreTrack(track, separator, tagSettings.SingleAlbumArtist, settings);
        var pathInfo = BuildTemplatePathInfo(coreTrack, settings);
        if (string.IsNullOrWhiteSpace(pathInfo.FilePath)
            || string.IsNullOrWhiteSpace(pathInfo.Filename))
        {
            return sourcePath;
        }

        var destinationPath = Path.Join(pathInfo.FilePath, $"{pathInfo.Filename}{Path.GetExtension(sourcePath)}");
        if (PathsReferToSameFile(sourcePath, destinationPath))
        {
            return sourcePath;
        }

        Directory.CreateDirectory(pathInfo.FilePath);
        destinationPath = ResolveTemplateMaterializationDestination(sourcePath, destinationPath, settings);
        FileMoveFallbackHelper.MoveWithFallback(sourcePath, destinationPath);
        MoveAdjacentSidecars(sourcePath, destinationPath);
        return destinationPath;
    }

    private static string ResolveTemplateMaterializationDestination(
        string sourcePath,
        string destinationPath,
        DeezSpoTagSettings settings)
    {
        if (!IOFile.Exists(destinationPath) || ShouldOverwriteMaterializedFile(settings))
        {
            return destinationPath;
        }

        var directory = Path.GetDirectoryName(destinationPath) ?? "";
        var filename = Path.GetFileNameWithoutExtension(destinationPath);
        var extension = Path.GetExtension(destinationPath);
        for (var index = 1; index < 10_000; index++)
        {
            var candidate = Path.Join(directory, $"{filename} ({index}){extension}");
            if (!IOFile.Exists(candidate) && !PathsReferToSameFile(sourcePath, candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not allocate a unique manual enrichment destination for {destinationPath}.");
    }

    private static bool ShouldOverwriteMaterializedFile(DeezSpoTagSettings settings)
        => string.Equals(settings.OverwriteFile, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(settings.OverwriteFile, "overwrite", StringComparison.OrdinalIgnoreCase);

    private static void MoveAdjacentSidecars(string sourcePath, string destinationPath)
    {
        foreach (var extension in new[] { ".lrc", ".elrc", TtmlExtension, ".txt" })
        {
            var sourceSidecar = Path.ChangeExtension(sourcePath, extension);
            if (!IOFile.Exists(sourceSidecar))
            {
                continue;
            }

            var destinationSidecar = Path.ChangeExtension(destinationPath, extension);
            if (PathsReferToSameFile(sourceSidecar, destinationSidecar) || IOFile.Exists(destinationSidecar))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationSidecar) ?? "");
            FileMoveFallbackHelper.MoveWithFallback(sourceSidecar, destinationSidecar);
        }
    }

    private static bool PathsReferToSameFile(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void PersistManualMaterializedTargetPath(
        AutoTagRunPlan plan,
        string previousPath,
        string materializedPath)
    {
        if (PathsReferToSameFile(previousPath, materializedPath))
        {
            return;
        }

        var runtimeDirectory = Path.GetDirectoryName(plan.ConfigPath);
        if (string.IsNullOrWhiteSpace(runtimeDirectory) || !Directory.Exists(runtimeDirectory))
        {
            return;
        }

        var configPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { plan.ConfigPath };
        if (!string.IsNullOrWhiteSpace(plan.JobId))
        {
            foreach (var path in Directory.EnumerateFiles(runtimeDirectory, $"autotag-{plan.JobId}-*.json"))
            {
                configPaths.Add(path);
            }
        }

        foreach (var configPath in configPaths)
        {
            ReplaceTargetPathInRuntimeConfig(configPath, previousPath, materializedPath);
        }
    }

    private static void ReplaceTargetPathInRuntimeConfig(
        string configPath,
        string previousPath,
        string materializedPath)
    {
        try
        {
            var root = JsonNode.Parse(IOFile.ReadAllText(configPath)) as JsonObject;
            if (root?["targetFiles"] is not JsonArray targets)
            {
                return;
            }

            var changed = false;
            for (var index = 0; index < targets.Count; index++)
            {
                var existing = targets[index]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(existing) || !PathsReferToSameFile(existing, previousPath))
                {
                    continue;
                }

                targets[index] = materializedPath;
                changed = true;
            }

            if (changed)
            {
                IOFile.WriteAllText(configPath, root.ToJsonString(CaseInsensitiveJsonOptions), new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new IOException($"Failed to persist manual enrichment staging path '{materializedPath}'.", ex);
        }
    }

    private static bool ShouldWriteArtworkSidecar(AutoTagRunnerConfig config)
        => config.SaveArtwork ?? false;

    private static bool ShouldPrepareTemplateArtworkSidecar(AutoTagRunnerConfig config)
        => config.OrganizeSidecarsIntoTemplateFolders == true && ShouldWriteArtworkSidecar(config);

    private static string? TryResolveExistingCoverSidecar(
        string filePath,
        AutoTagTrack track,
        Track coreTrack,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings)
    {
        if (!ShouldWriteArtworkSidecar(config))
        {
            return null;
        }

        var outputDirectory = config.OrganizeSidecarsIntoTemplateFolders == true
            ? BuildTemplatePathInfo(coreTrack, settings).CoverPath
            : Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return null;
        }

        var baseFileName = BuildAlbumArtworkBaseFileName(track, settings);
        if (string.IsNullOrWhiteSpace(baseFileName))
        {
            baseFileName = CoverTag;
        }

        return ResolveLocalArtworkFormats(settings.LocalArtworkFormat)
            .Select(format => Path.Join(outputDirectory, $"{baseFileName}.{format}"))
            .FirstOrDefault(IOFile.Exists);
    }

    private static async Task EnsureTemplateFoldersAndArtworkSidecarAsync(
        AutoTagTrack sourceTrack,
        Track coreTrack,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        string filePath,
        string? tempCoverPath,
        CancellationToken token)
    {
        var pathInfo = config.OrganizeSidecarsIntoTemplateFolders == true
            ? BuildTemplatePathInfo(coreTrack, settings)
            : new TrackPathInfo
            {
                ArtistPath = Path.GetDirectoryName(filePath),
                CoverPath = Path.GetDirectoryName(filePath)
            };
        if (!string.IsNullOrWhiteSpace(pathInfo.ArtistPath))
        {
            Directory.CreateDirectory(pathInfo.ArtistPath);
        }

        if (!string.IsNullOrWhiteSpace(pathInfo.CoverPath))
        {
            Directory.CreateDirectory(pathInfo.CoverPath);
        }

        if (!ShouldWriteArtworkSidecar(config)
            || string.IsNullOrWhiteSpace(pathInfo.CoverPath)
            || string.IsNullOrWhiteSpace(tempCoverPath)
            || !IOFile.Exists(tempCoverPath))
        {
            return;
        }

        var baseFileName = BuildAlbumArtworkBaseFileName(sourceTrack, settings);
        if (string.IsNullOrWhiteSpace(baseFileName))
        {
            baseFileName = CoverTag;
        }

        var formats = ResolveLocalArtworkFormats(settings.LocalArtworkFormat);
        using var image = await Image.LoadAsync(tempCoverPath, token);
        foreach (var format in formats)
        {
            var coverPath = Path.Join(pathInfo.CoverPath, $"{baseFileName}.{format}");
            if (IOFile.Exists(coverPath))
            {
                continue;
            }

            if (format == "png")
            {
                await image.SaveAsPngAsync(coverPath, new PngEncoder(), token);
            }
            else
            {
                await image.SaveAsJpegAsync(
                    coverPath,
                    new JpegEncoder { Quality = Math.Clamp(settings.JpegImageQuality, 1, 100) },
                    token);
            }
        }
    }

    private static IReadOnlyList<string> ResolveLocalArtworkFormats(string? configured)
    {
        var formats = (configured ?? "jpg")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.TrimStart('.').ToLowerInvariant())
            .Where(value => value is "jpg" or "jpeg" or "png")
            .Select(value => value == "jpeg" ? "jpg" : value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return formats.Count == 0 ? ["jpg"] : formats;
    }

    private static async Task<LyricsSidecarWriteResult> WriteLyricsSidecarsAsync(
        TagWriteExecutionContext context,
        CancellationToken token)
    {
        var wroteLrcSidecar = false;
        var wroteTtmlSidecar = false;
        var sidecarLrcLines = ResolveLrcSidecarLines(context.SourceTrack, context.FilePath, context.Settings);
        if (context.AllowsLyricsBySettings
            && context.AllowsLrcByFormat
            && (context.AllowsSyncedType || context.AllowsUnsyncedType)
            && sidecarLrcLines.Count > 0)
        {
            var lrcPath = BuildLyricsSidecarPath(context, ".lrc");
            if (!IOFile.Exists(lrcPath) || ShouldUpgradeLrcSidecarToWordTiming(context, lrcPath, sidecarLrcLines))
            {
                await IOFile.WriteAllLinesAsync(lrcPath, sidecarLrcLines, token);
                wroteLrcSidecar = true;
            }
        }

        var sidecarTtml = ResolveTtmlSidecarPayload(context.SourceTrack, context.FilePath);
        if (context.EnabledTags.Contains(TtmlLyricsTag)
            && context.AllowsLyricsBySettings
            && context.AllowsTtmlByFormat
            && AppleLyricsService.IsWordSyncedTtml(sidecarTtml))
        {
            var ttmlPath = BuildLyricsSidecarPath(context, TtmlExtension);
            if (!IOFile.Exists(ttmlPath) || ShouldUpgradeTtmlSidecarToWordTiming(ttmlPath, sidecarTtml))
            {
                await IOFile.WriteAllTextAsync(ttmlPath, sidecarTtml, token);
                wroteTtmlSidecar = true;
            }
        }

        return new LyricsSidecarWriteResult(wroteLrcSidecar, wroteTtmlSidecar);
    }

    private static bool ShouldUpgradeTtmlSidecarToWordTiming(string ttmlPath, string? incomingTtml)
    {
        try
        {
            var existing = IOFile.ReadAllText(ttmlPath);
            if (!AppleLyricsService.IsWordSyncedTtml(existing))
            {
                return true;
            }

            return AppleLyricsService.IsAppleNativeTtml(incomingTtml)
                && !AppleLyricsService.IsAppleNativeTtml(existing);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private static bool ShouldUpgradeLrcSidecarToWordTiming(
        TagWriteExecutionContext context,
        string lrcPath,
        IReadOnlyList<string> sidecarLrcLines)
    {
        if (!context.Settings.PreferEnhancedLrc || !LrcContent.IsWordSynchronized(sidecarLrcLines))
        {
            return false;
        }

        try
        {
            return !LrcContent.IsWordSynchronized(IOFile.ReadAllLines(lrcPath));
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private static string BuildLyricsSidecarPath(TagWriteExecutionContext context, string extension)
    {
        if (context.Config.OrganizeSidecarsIntoTemplateFolders == true)
        {
            var pathInfo = BuildTemplatePathInfo(context.CoreTrack, context.Settings);
            if (!string.IsNullOrWhiteSpace(pathInfo.FilePath)
                && !string.IsNullOrWhiteSpace(pathInfo.Filename))
            {
                Directory.CreateDirectory(pathInfo.FilePath);
                return Path.Join(pathInfo.FilePath, $"{pathInfo.Filename}{extension}");
            }
        }

        return Path.ChangeExtension(context.FilePath, extension);
    }

    private void CleanupUpgradedTxtSidecar(TagWriteExecutionContext context, LyricsSidecarWriteResult sidecarWriteResult)
    {
        if (!context.SidecarState.HasTxt
            || (!context.SidecarState.HasLrc
                && !context.SidecarState.HasElrc
                && !context.SidecarState.HasTtml
                && !sidecarWriteResult.WroteLrcSidecar
                && !sidecarWriteResult.WroteTtmlSidecar))
        {
            return;
        }

        try
        {
            IOFile.Delete(context.SidecarState.TxtPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to remove upgraded TXT lyrics sidecar {Path}", SanitizeLogValue(context.SidecarState.TxtPath));
            }
        }
    }

    private static bool ShouldAllowLyricsOtherTagKey(
        string key,
        bool allowsLyricsBySettings,
        bool allowsSyncedType,
        bool allowsUnsyncedType,
        bool allowsTtmlByFormat,
        bool allowLyricsPayloadWrites)
    {
        if (!IsLyricsPayloadKey(key))
        {
            return true;
        }

        if (!allowsLyricsBySettings || !allowLyricsPayloadWrites)
        {
            return false;
        }

        if (key.Equals(SyncedLyricsTag, StringComparison.OrdinalIgnoreCase))
        {
            return allowsSyncedType;
        }

        if (key.Equals(UnsyncedLyricsTag, StringComparison.OrdinalIgnoreCase))
        {
            return allowsUnsyncedType;
        }

        if (key.Equals(TtmlLyricsTag, StringComparison.OrdinalIgnoreCase))
        {
            return allowsSyncedType && allowsTtmlByFormat;
        }

        return allowsSyncedType || allowsUnsyncedType;
    }

    private static bool IsLyricsPayloadKey(string key)
    {
        return key.Equals(LyricsTag, StringComparison.OrdinalIgnoreCase)
            || key.Equals(SyncedLyricsTag, StringComparison.OrdinalIgnoreCase)
            || key.Equals(UnsyncedLyricsTag, StringComparison.OrdinalIgnoreCase)
            || key.Equals(TtmlLyricsTag, StringComparison.OrdinalIgnoreCase)
            || key.Equals(SyncedLyricsSourceFormatTag, StringComparison.OrdinalIgnoreCase);
    }

    private static (bool HasAny, bool HasLrc, bool HasElrc, bool HasTtml, bool HasTxt, string TxtPath) GetLyricsSidecarState(string filePath)
    {
        var lrcPath = Path.ChangeExtension(filePath, ".lrc");
        var elrcPath = Path.ChangeExtension(filePath, ".elrc");
        var ttmlPath = Path.ChangeExtension(filePath, TtmlExtension);
        var txtPath = Path.ChangeExtension(filePath, ".txt");
        var hasLrc = IOFile.Exists(lrcPath);
        var hasElrc = IOFile.Exists(elrcPath);
        var hasTtml = HasTimedTtmlSidecar(ttmlPath);
        var hasTxt = IOFile.Exists(txtPath);
        return (hasLrc || hasElrc || hasTtml || hasTxt, hasLrc, hasElrc, hasTtml, hasTxt, txtPath);
    }

    private static bool HasTimedTtmlSidecar(string path)
    {
        if (!IOFile.Exists(path))
        {
            return false;
        }

        try
        {
            return AppleLyricsService.IsWordSyncedTtml(IOFile.ReadAllText(path));
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private static bool TrackHasEmbeddedArtwork(string filePath, AutoTagRunnerConfig config, string platformId)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var extension = Path.GetExtension(filePath);
            return HasTag(file, extension, SupportedTag.AlbumArt, config, platformId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static string? TryResolveFolderArtworkPath(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var preferredNames = new[]
        {
            CoverTag,
            "folder",
            "front",
            AlbumTag,
            "albumart",
            "artwork"
        };
        var preferredExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };

        return preferredNames
            .SelectMany(name => preferredExtensions.Select(ext => Path.Join(directory, name + ext)))
            .FirstOrDefault(IOFile.Exists);
    }

    private async Task<string?> DownloadCoverAsync(string url, CancellationToken token)
    {
        try
        {
            var tempPath = Path.Join(Path.GetTempPath(), $"autotag-cover-{Guid.NewGuid():N}.jpg");
            using var scope = _serviceScopeFactory.CreateScope();
            var imageDownloader = scope.ServiceProvider.GetRequiredService<ImageDownloader>();
            return await imageDownloader.DownloadImageAsync(
                url,
                tempPath,
                overwrite: "y",
                preferMaxQuality: true,
                cancellationToken: token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to download cover art.");
            return null;
        }
    }

    private Task<HashSet<SupportedTag>> ApplyCustomTagsAsync(
        string filePath,
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        string platformId,
        bool useNullSeparator)
    {
        if (config.Tags.Count == 0)
        {
            return Task.FromResult(new HashSet<SupportedTag>());
        }

        var attemptedTags = new HashSet<SupportedTag>();
        try
        {
            var extension = Path.GetExtension(filePath);
            var chapterSnapshot = AtlTagHelper.CaptureChapters(filePath, extension, _logger);
            using var file = TagLib.File.Create(filePath);
            var enabledTags = BuildConfiguredTagSet(config.Tags);
            var separator = ResolveArtistSeparator(config, filePath);
            var writes = BuildCustomTagWrites(track, config, platformId, extension, file);

            if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
                ApplyId3CustomTags(id3, writes, config, separator, useNullSeparator, enabledTags, attemptedTags);
            }
            else if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
            {
                var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
                ApplyVorbisCustomTags(vorbis, writes, config, separator, enabledTags, attemptedTags);
            }
            else if (IsMp4Family(extension))
            {
                var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
                ApplyAppleCustomTags(apple, writes, config, separator, enabledTags, attemptedTags);
            }

            file.Save();
            AtlTagHelper.RestoreChapters(filePath, chapterSnapshot, _logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed applying custom tags for {File}", SanitizeLogValue(filePath));
        }

        return Task.FromResult(attemptedTags);
    }

    private static string ResolveStylesTagName(AutoTagRunnerConfig config, string extension)
    {
        if (config.StylesCustomTag == null)
        {
            return StyleUpperTag;
        }

        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(config.StylesCustomTag.Id3) ? StyleUpperTag : config.StylesCustomTag.Id3;
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(config.StylesCustomTag.Vorbis) ? StyleUpperTag : config.StylesCustomTag.Vorbis;
        }

        if (IsMp4Family(extension))
        {
            return string.IsNullOrWhiteSpace(config.StylesCustomTag.Mp4) ? StyleUpperTag : config.StylesCustomTag.Mp4;
        }

        return StyleUpperTag;
    }

    private static readonly Dictionary<string, SupportedTag> SupportedTagMap = CreateSupportedTagMap();

    private static Dictionary<string, SupportedTag> CreateSupportedTagMap()
    {
        var map = new Dictionary<string, SupportedTag>(StringComparer.OrdinalIgnoreCase)
        {
            [TitleTag] = SupportedTag.Title,
            [ArtistTag] = SupportedTag.Artist,
            [ArtistsTag] = SupportedTag.Artist,
            [AlbumArtistTag] = SupportedTag.AlbumArtist,
            [AlbumTag] = SupportedTag.Album,
            [AlbumArtTag] = SupportedTag.AlbumArt,
            [VersionTag] = SupportedTag.Version,
            [RemixerTag] = SupportedTag.Remixer,
            [GenreTag] = SupportedTag.Genre,
            [StyleTag] = SupportedTag.Style,
            [LabelTag] = SupportedTag.Label,
            [ReleaseIdTag] = SupportedTag.ReleaseId,
            [TrackIdTag] = SupportedTag.TrackId,
            [RecordingIdTag] = SupportedTag.RecordingId,
            [ArtistIdTag] = SupportedTag.ArtistId,
            [AlbumArtistIdTag] = SupportedTag.AlbumArtistId,
            [ReleaseGroupIdTag] = SupportedTag.ReleaseGroupId,
            [AlbumIdTag] = SupportedTag.AlbumId,
            [ReleaseStatusTag] = SupportedTag.ReleaseStatus,
            [ReleaseCountryTag] = SupportedTag.ReleaseCountry,
            [BarcodeTag] = SupportedTag.Barcode,
            [MediaTag] = SupportedTag.Media,
            [CopyrightTag] = SupportedTag.Copyright,
            [ComposerTag] = SupportedTag.Composer,
            [LyricistTag] = SupportedTag.Lyricist,
            [InvolvedPeopleTag] = SupportedTag.InvolvedPeople,
            [PublisherTag] = SupportedTag.Publisher,
            [DescriptionTag] = SupportedTag.Description,
            [ReplayGainTag] = SupportedTag.ReplayGain,
            [SourceTag] = SupportedTag.Source,
            [RatingTag] = SupportedTag.Rating,
            [LanguageTag] = SupportedTag.Language
        };

        SupportedTagFeatureMappings.AddAudioFeatureTags(map);

        map[CatalogNumberTag] = SupportedTag.CatalogNumber;
        map[TrackNumberTag] = SupportedTag.TrackNumber;
        map[DiscNumberTag] = SupportedTag.DiscNumber;
        map[DurationTag] = SupportedTag.Duration;
        map[TrackTotalTag] = SupportedTag.TrackTotal;
        map[ReleaseTypeTag] = SupportedTag.ReleaseType;
        map[DiscTotalTag] = SupportedTag.DiscTotal;
        map["isrc"] = SupportedTag.ISRC;
        map[PublishDateTag] = SupportedTag.PublishDate;
        map[ReleaseDateTag] = SupportedTag.ReleaseDate;
        map[YearTag] = SupportedTag.ReleaseDate;
        map[DateTag] = SupportedTag.ReleaseDate;
        map[LengthTag] = SupportedTag.Duration;
        map[CoverTag] = SupportedTag.AlbumArt;
        map[LyricsTag] = SupportedTag.UnsyncedLyrics;
        map["url"] = SupportedTag.URL;
        map[OtherTagsTag] = SupportedTag.OtherTags;
        map[MetaTagsTag] = SupportedTag.MetaTags;
        map[UnsyncedLyricsTag] = SupportedTag.UnsyncedLyrics;
        map[SyncedLyricsTag] = SupportedTag.SyncedLyrics;
        map[TtmlLyricsTag] = SupportedTag.TtmlLyrics;
        map[ExplicitTag] = SupportedTag.Explicit;
        return map;
    }

    private static bool ShouldOverwriteTag(AutoTagRunnerConfig config, SupportedTag tag)
    {
        if (config.Overwrite)
        {
            return true;
        }

        return config.OverwriteTags.Any(t => SupportedTagMap.TryGetValue(t.Trim(), out var mapped) && mapped == tag);
    }

    private static string ResolveSeparatorForFormat(AutoTagRunnerConfig config, string extension)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return config.Separators?.Id3 ?? ", ";
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            return config.Separators?.Vorbis ?? "";
        }

        if (IsMp4Family(extension))
        {
            return config.Separators?.Mp4 ?? ", ";
        }

        return ", ";
    }

    private static List<string> CollectAutoTagTags(AutoTagTrack track)
    {
        var tags = new List<string>();
        void Add(string tag, bool condition)
        {
            if (!condition || tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
            tags.Add(tag);
        }

        AddAutoTagMetadataTags(track, Add);
        AddAutoTagFeatureTags(track, Add);
        AddAutoTagNumericAndDateTags(track, Add);
        AddAutoTagOtherMappedTags(track, Add);
        AddAutoTagLyricsAndOtherTags(track, Add);

        return tags;
    }

    private static void AddAutoTagMetadataTags(AutoTagTrack track, Action<string, bool> add)
    {
        add(TitleTag, !string.IsNullOrWhiteSpace(track.Title));
        add(ArtistTag, track.Artists.Count > 0);
        add(AlbumArtistTag, track.AlbumArtists.Count > 0);
        add(AlbumTag, !string.IsNullOrWhiteSpace(track.Album));
        add(AlbumArtTag, !string.IsNullOrWhiteSpace(track.Art));
        add(VersionTag, !string.IsNullOrWhiteSpace(track.Version));
        add(RemixerTag, track.Remixers.Count > 0);
        add(GenreTag, track.Genres.Count > 0);
        add(StyleTag, track.Styles.Count > 0);
        add(LabelTag, !string.IsNullOrWhiteSpace(track.Label));
        add(ReleaseIdTag, !string.IsNullOrWhiteSpace(track.ReleaseId));
        add(TrackIdTag, !string.IsNullOrWhiteSpace(track.TrackId));
        add(RecordingIdTag, !string.IsNullOrWhiteSpace(track.RecordingId));
        add(ArtistIdTag, !string.IsNullOrWhiteSpace(track.ArtistId));
        add(AlbumArtistIdTag, !string.IsNullOrWhiteSpace(track.AlbumArtistId));
        add(ReleaseGroupIdTag, !string.IsNullOrWhiteSpace(track.ReleaseGroupId));
        add(AlbumIdTag, !string.IsNullOrWhiteSpace(track.AlbumId));
        add(ReleaseStatusTag, !string.IsNullOrWhiteSpace(track.ReleaseStatus));
        add(ReleaseCountryTag, !string.IsNullOrWhiteSpace(track.ReleaseCountry));
        add(BarcodeTag, !string.IsNullOrWhiteSpace(track.Barcode));
        add(MediaTag, track.Media.Count > 0);
        add(LyricistTag, !string.IsNullOrWhiteSpace(track.Lyricist));
        add(PublisherTag, !string.IsNullOrWhiteSpace(track.Publisher));
        add(DescriptionTag, !string.IsNullOrWhiteSpace(track.Description));
    }

    private static void AddAutoTagFeatureTags(AutoTagTrack track, Action<string, bool> add)
    {
        add(BpmTag, track.Bpm.HasValue && track.Bpm.Value > 0);
        add("danceability", track.Danceability.HasValue);
        add("energy", track.Energy.HasValue);
        add("valence", track.Valence.HasValue);
        add("acousticness", track.Acousticness.HasValue);
        add("instrumentalness", track.Instrumentalness.HasValue);
        add("speechiness", track.Speechiness.HasValue);
        add("loudness", track.Loudness.HasValue);
        add("tempo", track.Tempo.HasValue);
        add("timeSignature", track.TimeSignature.HasValue);
        add("liveness", track.Liveness.HasValue);
        add("key", !string.IsNullOrWhiteSpace(track.Key));
        add("mood", !string.IsNullOrWhiteSpace(track.Mood));
        add("activity", !string.IsNullOrWhiteSpace(track.Activity));
    }

    private static void AddAutoTagNumericAndDateTags(AutoTagTrack track, Action<string, bool> add)
    {
        add(CatalogNumberTag, !string.IsNullOrWhiteSpace(track.CatalogNumber));
        add(TrackNumberTag, track.TrackNumber.HasValue && track.TrackNumber.Value > 0);
        add(TrackTotalTag, track.TrackTotal.HasValue && track.TrackTotal.Value > 0);
        add(ReleaseTypeTag, !string.IsNullOrWhiteSpace(AutoTagReleaseCategory.Resolve(track.ReleaseType, track.TrackTotal)));
        add(DiscTotalTag, track.DiscTotal.HasValue && track.DiscTotal.Value > 0 || HasOtherTagValues(track, DiscTotalTag));
        add(DiscNumberTag, track.DiscNumber.HasValue && track.DiscNumber.Value > 0);
        add(DurationTag, track.Duration.HasValue && track.Duration.Value.TotalSeconds > 0);
        add(IsrcTag, !string.IsNullOrWhiteSpace(track.Isrc));
        add(PublishDateTag, track.PublishDate.HasValue);
        add(ReleaseDateTag, track.ReleaseDate.HasValue);
        add(UrlTag, !string.IsNullOrWhiteSpace(track.Url));
        add(ExplicitTag, track.Explicit.HasValue);
    }

    private static void AddAutoTagOtherMappedTags(AutoTagTrack track, Action<string, bool> add)
    {
        add(BarcodeTag, HasOtherTagValues(track, BarcodeTag));
        add(ReplayGainTag, HasOtherTagValues(track, ReplayGainTag));
        add(CopyrightTag, HasOtherTagValues(track, CopyrightTag));
        add(ComposerTag, HasOtherTagValues(track, ComposerTag));
        add(LyricistTag, HasOtherTagValues(track, LyricistTag) || HasOtherTagValues(track, LyricistRawTag) || HasOtherTagValues(track, "TEXT"));
        add(InvolvedPeopleTag, HasOtherTagValues(track, InvolvedPeopleTag));
        add(PublisherTag, HasOtherTagValues(track, PublisherTag) || HasOtherTagValues(track, PublisherRawTag));
        add(DescriptionTag, HasOtherTagValues(track, DescriptionTag) || HasOtherTagValues(track, DescriptionRawTag) || HasOtherTagValues(track, CommentRawTag));
        add(SourceTag, HasOtherTagValues(track, SourceTag));
        add(RatingTag, HasOtherTagValues(track, RatingTag));
        add(LanguageTag, HasOtherTagValues(track, LanguageTag));
    }

    private static void AddAutoTagLyricsAndOtherTags(AutoTagTrack track, Action<string, bool> add)
    {
        var otherKeys = track.Other.Keys.ToList();
        var hasSyncedLyrics = HasOtherKey(otherKeys, SyncedLyricsTag);
        var hasUnsyncedLyrics = HasAnyOtherKey(otherKeys, UnsyncedLyricsTag, LyricsTag);
        var hasTtmlLyrics = HasOtherKey(otherKeys, TtmlLyricsTag);
        add(SyncedLyricsTag, hasSyncedLyrics);
        add(UnsyncedLyricsTag, hasUnsyncedLyrics);
        add(TtmlLyricsTag, hasTtmlLyrics);

        var hasOtherTags = HasNonLyricsOtherTag(otherKeys);
        add(OtherTagsTag, hasOtherTags);
    }

    private static bool HasOtherKey(IEnumerable<string> keys, string target)
        => keys.Any(key => key.Equals(target, StringComparison.OrdinalIgnoreCase));

    private static bool HasAnyOtherKey(IEnumerable<string> keys, string first, string second)
        => HasOtherKey(keys, first) || HasOtherKey(keys, second);

    private static bool HasNonLyricsOtherTag(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (key.Equals(SyncedLyricsTag, StringComparison.OrdinalIgnoreCase)
                || key.Equals(UnsyncedLyricsTag, StringComparison.OrdinalIgnoreCase)
                || key.Equals(LyricsTag, StringComparison.OrdinalIgnoreCase)
                || key.Equals(TtmlLyricsTag, StringComparison.OrdinalIgnoreCase)
                || key.Equals(SyncedLyricsSourceFormatTag, StringComparison.OrdinalIgnoreCase)
                || key.Equals(ReleaseTypeRawTag, StringComparison.OrdinalIgnoreCase)
                || IsNonPersistedOtherRawKey(key))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool HasOtherTagValues(AutoTagTrack track, string key)
    {
        return track.Other.TryGetValue(key, out var values) && values.Count > 0;
    }

    private static bool IsFirstClassOtherRawKey(string key)
    {
        if (FirstClassRawOtherTags.Contains(key))
        {
            return true;
        }

        return key.EndsWith("_TRACK_ID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_RELEASE_ID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_ALBUM_ID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_ARTIST_ID", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_URL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRuntimeMatchMetadataKey(string key)
    {
        return key.StartsWith("SHAZAM_MATCH_", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_SIMILARITY", StringComparison.OrdinalIgnoreCase)
            || key.Equals("SHAZAM_DURATION_DIFF_SECONDS", StringComparison.OrdinalIgnoreCase)
            || key.Equals("SHAZAM_TITLE_SIMILARITY", StringComparison.OrdinalIgnoreCase)
            || key.Equals("SHAZAM_ARTIST_SIMILARITY", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNonPersistedOtherRawKey(string key)
        => IsFirstClassOtherRawKey(key) || IsRuntimeMatchMetadataKey(key);

    private static bool ShouldPersistOtherRawKey(string key)
    {
        return !string.IsNullOrWhiteSpace(key)
            && !IsNonPersistedOtherRawKey(key)
            && !key.Equals(ReleaseTypeRawTag, StringComparison.OrdinalIgnoreCase)
            && !IsLyricsPayloadKey(key);
    }

    private static bool HasReleaseTypeTagEnabled(HashSet<string> enabledTags)
        => enabledTags.Contains(ReleaseTypeTag) || enabledTags.Contains(OtherTagsTag);

    private static void EnsureReleaseCategory(AutoTagTrack track)
    {
        var releaseType = AutoTagReleaseCategory.Resolve(track.ReleaseType, track.TrackTotal);
        if (string.IsNullOrWhiteSpace(releaseType))
        {
            return;
        }

        track.ReleaseType = releaseType;
        track.Other[ReleaseTypeRawTag] = new List<string> { releaseType };
    }

    private static string[] ApplySeparator(List<string> values, string separator, bool useNullSeparator = false)
    {
        if (values.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (useNullSeparator)
        {
            return values.ToArray();
        }

        if (string.IsNullOrEmpty(separator))
        {
            return values.ToArray();
        }

        return new[] { string.Join(separator, values) };
    }

    private static string FormatAudioFeature(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static bool HasTag(TagLib.File file, string extension, SupportedTag tag, AutoTagRunnerConfig config, string platformId)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag?)file.GetTag(TagTypes.Id3v2, false);
            if (id3 == null) return false;
            return HasId3Tag(id3, tag, config, platformId);
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = (TagLib.Ogg.XiphComment?)file.GetTag(TagTypes.Xiph, false);
            if (vorbis == null) return false;
            return HasVorbisTag(vorbis, tag, config, platformId);
        }

        if (IsMp4Family(extension))
        {
            return HasMp4Tag(file, tag, config, platformId);
        }

        return false;
    }

    private static bool HasId3Tag(TagLib.Id3v2.Tag tag, SupportedTag supportedTag, AutoTagRunnerConfig config, string platformId)
    {
        return supportedTag switch
        {
            SupportedTag.Title => !string.IsNullOrWhiteSpace(tag.Title),
            SupportedTag.Artist => tag.Performers?.Length > 0,
            SupportedTag.AlbumArtist => tag.AlbumArtists?.Length > 0,
            SupportedTag.Album => !string.IsNullOrWhiteSpace(tag.Album),
            SupportedTag.Key => TagRawProbe.HasId3Raw(tag, "TKEY"),
            SupportedTag.BPM => TagRawProbe.HasId3Raw(tag, "TBPM"),
            SupportedTag.Danceability => TagRawProbe.HasId3Raw(tag, DanceabilityTag),
            SupportedTag.Energy => TagRawProbe.HasId3Raw(tag, EnergyTag),
            SupportedTag.Valence => TagRawProbe.HasId3Raw(tag, ValenceTag),
            SupportedTag.Acousticness => TagRawProbe.HasId3Raw(tag, AcousticnessTag),
            SupportedTag.Instrumentalness => TagRawProbe.HasId3Raw(tag, InstrumentalnessTag),
            SupportedTag.Speechiness => TagRawProbe.HasId3Raw(tag, SpeechinessTag),
            SupportedTag.Loudness => TagRawProbe.HasId3Raw(tag, LoudnessTag),
            SupportedTag.Tempo => TagRawProbe.HasId3Raw(tag, TempoTag),
            SupportedTag.TimeSignature => TagRawProbe.HasId3Raw(tag, TimeSignatureTag),
            SupportedTag.Liveness => TagRawProbe.HasId3Raw(tag, LivenessTag),
            SupportedTag.Genre => tag.Genres?.Length > 0,
            SupportedTag.Style => TagRawProbe.HasId3Raw(tag, ResolveStylesTagName(config, ".mp3")),
            SupportedTag.Label => TagRawProbe.HasId3Raw(tag, "TPUB"),
            SupportedTag.Copyright => TagRawProbe.HasId3Raw(tag, CopyrightRawTag),
            SupportedTag.Composer => TagRawProbe.HasId3Raw(tag, "TCOM"),
            SupportedTag.Lyricist => TagRawProbe.HasId3Raw(tag, "TEXT") || TagRawProbe.HasId3Raw(tag, LyricistRawTag),
            SupportedTag.InvolvedPeople => TagRawProbe.HasId3Raw(tag, InvolvedPeopleRawTag),
            SupportedTag.Publisher => TagRawProbe.HasId3Raw(tag, PublisherRawTag),
            SupportedTag.Description => TagRawProbe.HasId3Raw(tag, DescriptionRawTag) || TagRawProbe.HasId3Raw(tag, CommentRawTag) || !string.IsNullOrWhiteSpace(tag.Comment),
            SupportedTag.ReplayGain => TagRawProbe.HasId3Raw(tag, ReplayGainRawTag),
            SupportedTag.Source => TagRawProbe.HasId3Raw(tag, SourceRawTag),
            SupportedTag.Rating => TagRawProbe.HasId3Raw(tag, RatingRawTag),
            SupportedTag.Language => TagRawProbe.HasId3Raw(tag, LanguageRawTag),
            SupportedTag.ISRC => TagRawProbe.HasId3Raw(tag, "TSRC"),
            SupportedTag.CatalogNumber => TagRawProbe.HasId3Raw(tag, CatalogNumberUpperTag),
            SupportedTag.Version => TagRawProbe.HasId3Raw(tag, "TIT3"),
            SupportedTag.TrackNumber => tag.Track > 0,
            SupportedTag.TrackTotal => tag.TrackCount > 0,
            SupportedTag.ReleaseType => TagRawProbe.HasId3Raw(tag, ReleaseTypeRawTag),
            SupportedTag.DiscNumber => tag.Disc > 0,
            SupportedTag.DiscTotal => tag.DiscCount > 0,
            SupportedTag.Duration => TagRawProbe.HasId3Raw(tag, "TLEN"),
            SupportedTag.Remixer => TagRawProbe.HasId3Raw(tag, "TPE4"),
            SupportedTag.Mood => TagRawProbe.HasId3Raw(tag, "TMOO"),
            SupportedTag.Activity => TagRawProbe.HasId3Raw(tag, "ACTIVITY"),
            SupportedTag.ReleaseDate => TagRawProbe.HasId3Raw(tag, config.Id3v24 ? "TDRC" : "TYER"),
            SupportedTag.PublishDate => TagRawProbe.HasId3Raw(tag, "TDRL"),
            SupportedTag.URL => TagRawProbe.HasId3Raw(tag, WwwAudioFileTag),
            SupportedTag.TrackId => TagRawProbe.HasId3Raw(tag, $"{platformId.ToUpperInvariant()}_TRACK_ID"),
            SupportedTag.ReleaseId => TagRawProbe.HasId3Raw(tag, $"{platformId.ToUpperInvariant()}_RELEASE_ID"),
            SupportedTag.RecordingId => TagRawProbe.HasId3Raw(tag, RecordingIdRawTag),
            SupportedTag.ArtistId => TagRawProbe.HasId3Raw(tag, ArtistIdRawTag),
            SupportedTag.AlbumArtistId => TagRawProbe.HasId3Raw(tag, AlbumArtistIdRawTag),
            SupportedTag.ReleaseGroupId => TagRawProbe.HasId3Raw(tag, ReleaseGroupIdRawTag),
            SupportedTag.AlbumId => TagRawProbe.HasId3Raw(tag, AlbumIdRawTag),
            SupportedTag.ReleaseStatus => TagRawProbe.HasId3Raw(tag, ReleaseStatusRawTag),
            SupportedTag.ReleaseCountry => TagRawProbe.HasId3Raw(tag, ReleaseCountryRawTag),
            SupportedTag.Barcode => TagRawProbe.HasId3Raw(tag, BarcodeRawTag),
            SupportedTag.Media => TagRawProbe.HasId3Raw(tag, MediaRawTag),
            SupportedTag.OtherTags => false,
            SupportedTag.MetaTags => TagRawProbe.HasId3Raw(tag, TaggedDateTag),
            SupportedTag.SyncedLyrics => tag.GetFrames<TagLib.Id3v2.SynchronisedLyricsFrame>("SYLT").Any(),
            SupportedTag.UnsyncedLyrics => !string.IsNullOrWhiteSpace(tag.Lyrics),
            SupportedTag.AlbumArt => tag.Pictures?.Length > 0,
            SupportedTag.Explicit => TagRawProbe.HasId3Raw(tag, ItunesAdvisoryTag),
            _ => false
        };
    }

    private static bool HasVorbisTag(TagLib.Ogg.XiphComment tag, SupportedTag supportedTag, AutoTagRunnerConfig config, string platformId)
    {
        return supportedTag switch
        {
            SupportedTag.Title => tag.GetField(TitleUpperTag).Length > 0,
            SupportedTag.Artist => tag.GetField(ArtistUpperTag).Length > 0,
            SupportedTag.AlbumArtist => tag.GetField(AlbumArtistUpperTag).Length > 0,
            SupportedTag.Album => tag.GetField(AlbumUpperTag).Length > 0,
            SupportedTag.Key => tag.GetField("INITIALKEY").Length > 0,
            SupportedTag.BPM => tag.GetField("BPM").Length > 0,
            SupportedTag.Danceability => TagRawProbe.HasVorbisRaw(tag, DanceabilityTag),
            SupportedTag.Energy => TagRawProbe.HasVorbisRaw(tag, EnergyTag),
            SupportedTag.Valence => TagRawProbe.HasVorbisRaw(tag, ValenceTag),
            SupportedTag.Acousticness => TagRawProbe.HasVorbisRaw(tag, AcousticnessTag),
            SupportedTag.Instrumentalness => TagRawProbe.HasVorbisRaw(tag, InstrumentalnessTag),
            SupportedTag.Speechiness => TagRawProbe.HasVorbisRaw(tag, SpeechinessTag),
            SupportedTag.Loudness => TagRawProbe.HasVorbisRaw(tag, LoudnessTag),
            SupportedTag.Tempo => TagRawProbe.HasVorbisRaw(tag, TempoTag),
            SupportedTag.TimeSignature => TagRawProbe.HasVorbisRaw(tag, TimeSignatureTag),
            SupportedTag.Liveness => TagRawProbe.HasVorbisRaw(tag, LivenessTag),
            SupportedTag.Genre => tag.GetField(Mp4GenreTag).Length > 0,
            SupportedTag.Style => tag.GetField(ResolveStylesTagName(config, FlacExtension)).Length > 0,
            SupportedTag.Label => tag.GetField(LabelUpperTag).Length > 0,
            SupportedTag.Copyright => tag.GetField(CopyrightRawTag).Length > 0,
            SupportedTag.Composer => tag.GetField(ComposerUpperTag).Length > 0,
            SupportedTag.Lyricist => tag.GetField(LyricistRawTag).Length > 0,
            SupportedTag.InvolvedPeople => tag.GetField(InvolvedPeopleRawTag).Length > 0,
            SupportedTag.Publisher => tag.GetField(PublisherRawTag).Length > 0,
            SupportedTag.Description => tag.GetField(DescriptionRawTag).Length > 0 || tag.GetField(CommentRawTag).Length > 0,
            SupportedTag.ReplayGain => tag.GetField(ReplayGainRawTag).Length > 0,
            SupportedTag.Source => tag.GetField(SourceRawTag).Length > 0,
            SupportedTag.Rating => tag.GetField(RatingRawTag).Length > 0,
            SupportedTag.Language => tag.GetField(LanguageRawTag).Length > 0,
            SupportedTag.ISRC => tag.GetField("ISRC").Length > 0,
            SupportedTag.CatalogNumber => tag.GetField(CatalogNumberUpperTag).Length > 0,
            SupportedTag.Version => tag.GetField("SUBTITLE").Length > 0,
            SupportedTag.TrackNumber => tag.GetField(TrackNumberUpperTag).Length > 0,
            SupportedTag.TrackTotal => tag.GetField(TrackTotalRawTag).Length > 0,
            SupportedTag.ReleaseType => tag.GetField(ReleaseTypeRawTag).Length > 0,
            SupportedTag.DiscNumber => tag.GetField("DISCNUMBER").Length > 0,
            SupportedTag.DiscTotal => tag.GetField(DiscTotalRawTag).Length > 0,
            SupportedTag.Duration => tag.GetField(LengthUpperTag).Length > 0,
            SupportedTag.Remixer => tag.GetField(RemixerUpperTag).Length > 0,
            SupportedTag.Mood => tag.GetField("MOOD").Length > 0,
            SupportedTag.Activity => tag.GetField("ACTIVITY").Length > 0,
            SupportedTag.ReleaseDate => tag.GetField("DATE").Length > 0,
            SupportedTag.PublishDate => tag.GetField(OriginalDateUpperTag).Length > 0,
            SupportedTag.URL => tag.GetField(WwwAudioFileTag).Length > 0,
            SupportedTag.TrackId => tag.GetField($"{platformId.ToUpperInvariant()}_TRACK_ID").Length > 0,
            SupportedTag.ReleaseId => tag.GetField($"{platformId.ToUpperInvariant()}_RELEASE_ID").Length > 0,
            SupportedTag.RecordingId => tag.GetField(RecordingIdRawTag).Length > 0,
            SupportedTag.ArtistId => tag.GetField(ArtistIdRawTag).Length > 0,
            SupportedTag.AlbumArtistId => tag.GetField(AlbumArtistIdRawTag).Length > 0,
            SupportedTag.ReleaseGroupId => tag.GetField(ReleaseGroupIdRawTag).Length > 0,
            SupportedTag.AlbumId => tag.GetField(AlbumIdRawTag).Length > 0,
            SupportedTag.ReleaseStatus => tag.GetField(ReleaseStatusRawTag).Length > 0,
            SupportedTag.ReleaseCountry => tag.GetField(ReleaseCountryRawTag).Length > 0,
            SupportedTag.Barcode => tag.GetField(BarcodeRawTag).Length > 0,
            SupportedTag.Media => tag.GetField(MediaRawTag).Length > 0,
            SupportedTag.MetaTags => tag.GetField(TaggedDateTag).Length > 0,
            SupportedTag.UnsyncedLyrics => tag.GetField(LyricsUpperTag).Any(value => !string.IsNullOrWhiteSpace(value)),
            SupportedTag.SyncedLyrics =>
                tag.GetField(LyricsSyncedTag).Any(value => !string.IsNullOrWhiteSpace(value))
                || HasTimestampedLyricsPayload(tag.GetField(LyricsUpperTag)),
            SupportedTag.AlbumArt => tag.Pictures?.Length > 0,
            SupportedTag.Explicit => tag.GetField(ItunesAdvisoryTag).Length > 0
                || tag.GetField("COMMENT").Any(v => string.Equals(v, "Explicit", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    private static bool HasMp4Tag(TagLib.File file, SupportedTag supportedTag, AutoTagRunnerConfig config, string platformId)
    {
        return supportedTag switch
        {
            SupportedTag.Title => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.Artist => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.AlbumArtist => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.Album => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.BPM => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.Genre => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.Style => Mp4TagHelper.HasRaw(file, ResolveStylesTagName(config, ".mp4")),
            SupportedTag.Danceability => Mp4TagHelper.HasRaw(file, DanceabilityTag),
            SupportedTag.Energy => Mp4TagHelper.HasRaw(file, EnergyTag),
            SupportedTag.Valence => Mp4TagHelper.HasRaw(file, ValenceTag),
            SupportedTag.Acousticness => Mp4TagHelper.HasRaw(file, AcousticnessTag),
            SupportedTag.Instrumentalness => Mp4TagHelper.HasRaw(file, InstrumentalnessTag),
            SupportedTag.Speechiness => Mp4TagHelper.HasRaw(file, SpeechinessTag),
            SupportedTag.Loudness => Mp4TagHelper.HasRaw(file, LoudnessTag),
            SupportedTag.Tempo => Mp4TagHelper.HasRaw(file, TempoTag),
            SupportedTag.TimeSignature => Mp4TagHelper.HasRaw(file, TimeSignatureTag),
            SupportedTag.Liveness => Mp4TagHelper.HasRaw(file, LivenessTag),
            SupportedTag.Label => Mp4TagHelper.HasRaw(file, LabelUpperTag),
            SupportedTag.Copyright => Mp4TagHelper.HasRaw(file, CopyrightRawTag),
            SupportedTag.Composer => Mp4TagHelper.HasRaw(file, "©wrt"),
            SupportedTag.Lyricist => Mp4TagHelper.HasRaw(file, LyricistRawTag),
            SupportedTag.InvolvedPeople => Mp4TagHelper.HasRaw(file, InvolvedPeopleRawTag),
            SupportedTag.Publisher => Mp4TagHelper.HasRaw(file, PublisherRawTag),
            SupportedTag.Description => Mp4TagHelper.HasRaw(file, "ldes") || Mp4TagHelper.HasRaw(file, DescriptionRawTag),
            SupportedTag.ReplayGain => Mp4TagHelper.HasRaw(file, ReplayGainRawTag),
            SupportedTag.Source => Mp4TagHelper.HasRaw(file, SourceRawTag),
            SupportedTag.Rating => Mp4TagHelper.HasRaw(file, RatingRawTag),
            SupportedTag.Language => Mp4TagHelper.HasRaw(file, LanguageRawTag),
            SupportedTag.ISRC => Mp4TagHelper.HasRaw(file, "ISRC"),
            SupportedTag.CatalogNumber => Mp4TagHelper.HasRaw(file, CatalogNumberUpperTag),
            SupportedTag.Version => Mp4TagHelper.HasRaw(file, "desc"),
            SupportedTag.TrackNumber => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.TrackTotal => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.ReleaseType => Mp4TagHelper.HasRaw(file, ReleaseTypeRawTag),
            SupportedTag.DiscNumber => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.DiscTotal => file.Tag.DiscCount > 0,
            SupportedTag.Duration => Mp4TagHelper.HasRaw(file, LengthUpperTag),
            SupportedTag.Remixer => Mp4TagHelper.HasRaw(file, RemixerUpperTag),
            SupportedTag.Mood => Mp4TagHelper.HasRaw(file, "MOOD"),
            SupportedTag.Activity => Mp4TagHelper.HasRaw(file, "ACTIVITY"),
            SupportedTag.Key => Mp4TagHelper.HasRaw(file, InitialKeyRawTag),
            SupportedTag.ReleaseDate =>
                Mp4TagHelper.HasRaw(file, "©day")
                || Mp4TagHelper.HasRaw(file, "DATE"),
            SupportedTag.PublishDate => Mp4TagHelper.HasRaw(file, "ORIGINALDATE"),
            SupportedTag.URL => Mp4TagHelper.HasRaw(file, WwwAudioFileTag),
            SupportedTag.TrackId => Mp4TagHelper.HasRaw(file, $"{platformId.ToUpperInvariant()}_TRACK_ID"),
            SupportedTag.ReleaseId => Mp4TagHelper.HasRaw(file, $"{platformId.ToUpperInvariant()}_RELEASE_ID"),
            SupportedTag.RecordingId => Mp4TagHelper.HasRaw(file, RecordingIdRawTag),
            SupportedTag.ArtistId => Mp4TagHelper.HasRaw(file, ArtistIdRawTag),
            SupportedTag.AlbumArtistId => Mp4TagHelper.HasRaw(file, AlbumArtistIdRawTag),
            SupportedTag.ReleaseGroupId => Mp4TagHelper.HasRaw(file, ReleaseGroupIdRawTag),
            SupportedTag.AlbumId => Mp4TagHelper.HasRaw(file, AlbumIdRawTag),
            SupportedTag.ReleaseStatus => Mp4TagHelper.HasRaw(file, ReleaseStatusRawTag),
            SupportedTag.ReleaseCountry => Mp4TagHelper.HasRaw(file, ReleaseCountryRawTag),
            SupportedTag.Barcode => Mp4TagHelper.HasRaw(file, BarcodeRawTag),
            SupportedTag.Media => Mp4TagHelper.HasRaw(file, MediaRawTag),
            SupportedTag.MetaTags => Mp4TagHelper.HasRaw(file, TaggedDateTag),
            SupportedTag.UnsyncedLyrics => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.SyncedLyrics =>
                Mp4TagHelper.HasRaw(file, LyricsSyncedTag)
                || ContainsTimestampedLyrics(file.Tag.Lyrics),
            SupportedTag.AlbumArt => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.Explicit => Mp4TagHelper.HasRaw(file, ItunesAdvisoryTag),
            _ => false
        };
    }

    private static bool HasTimestampedLyricsPayload(IEnumerable<string> values)
    {
        return values.Any(ContainsTimestampedLyrics);
    }

    private static bool ContainsTimestampedLyrics(string? rawLyrics)
    {
        if (string.IsNullOrWhiteSpace(rawLyrics))
        {
            return false;
        }

        return rawLyrics
            .Split(LyricsLineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => TryParseLrcLine(line, out _, out _));
    }

    private static List<string> ReadExistingGenre(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            return SanitizeGenres(file.Tag.Genres ?? Array.Empty<string>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new List<string>();
        }
    }

    private static List<CustomTagWrite> BuildCustomTagWrites(AutoTagTrack track, AutoTagRunnerConfig config, string platformId, string extension, TagLib.File file)
    {
        var writes = new List<CustomTagWrite>();
        var styleTagName = ResolveStylesTagName(config, extension);
        var format = ResolveFormatName(extension);

        if (track.Styles.Count > 0)
        {
            var separator = ResolveSeparatorForFormat(config, extension);
            var styleValues = NormalizeStyleValues(track.Styles, separator);
            if (config.MergeGenres)
            {
                var existing = NormalizeStyleValues(
                    ReadExistingRawTag(file, extension, styleTagName),
                    separator);
                var existingStyleSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
                existing.AddRange(styleValues.Where(existingStyleSet.Add));
                styleValues = existing;
            }

            var styleRaw = config.StylesOptions.Equals("customTag", StringComparison.OrdinalIgnoreCase)
                ? styleTagName
                : ResolveFieldRawName(SupportedTag.Style, format, config);

            writes.Add(new CustomTagWrite(StyleTag, SupportedTag.Style, styleRaw, styleValues));
        }

        AddSingleValueCustomTagWrite(
            writes,
            "mood",
            SupportedTag.Mood,
            ResolveFieldRawName(SupportedTag.Mood, format, config),
            track.Mood);
        AddSingleValueCustomTagWrite(
            writes,
            "key",
            SupportedTag.Key,
            ResolveFieldRawName(SupportedTag.Key, format, config),
            track.Key);
        AddSingleValueCustomTagWrite(
            writes,
            VersionTag,
            SupportedTag.Version,
            ResolveFieldRawName(SupportedTag.Version, format, config),
            track.Version);

        if (track.Remixers.Count > 0)
        {
            writes.Add(new CustomTagWrite(RemixerTag, SupportedTag.Remixer, ResolveFieldRawName(SupportedTag.Remixer, format, config), track.Remixers.ToList()));
        }

        AddSingleValueCustomTagWrite(writes, "url", SupportedTag.URL, WwwAudioFileTag, track.Url);
        AddSingleValueCustomTagWrite(
            writes,
            CatalogNumberTag,
            SupportedTag.CatalogNumber,
            ResolveFieldRawName(SupportedTag.CatalogNumber, format, config),
            track.CatalogNumber);
        var platformKey = platformId.ToUpperInvariant();
        AddSingleValueCustomTagWrite(
            writes,
            TrackIdTag,
            SupportedTag.TrackId,
            $"{platformKey}_TRACK_ID",
            track.TrackId);
        AddSingleValueCustomTagWrite(
            writes,
            ReleaseIdTag,
            SupportedTag.ReleaseId,
            $"{platformKey}_RELEASE_ID",
            track.ReleaseId);
        AddSingleValueCustomTagWrite(writes, RecordingIdTag, SupportedTag.RecordingId, RecordingIdRawTag, track.RecordingId);
        AddSingleValueCustomTagWrite(writes, ArtistIdTag, SupportedTag.ArtistId, ArtistIdRawTag, track.ArtistId);
        AddSingleValueCustomTagWrite(writes, AlbumArtistIdTag, SupportedTag.AlbumArtistId, AlbumArtistIdRawTag, track.AlbumArtistId);
        AddSingleValueCustomTagWrite(writes, ReleaseGroupIdTag, SupportedTag.ReleaseGroupId, ReleaseGroupIdRawTag, track.ReleaseGroupId);
        AddSingleValueCustomTagWrite(writes, AlbumIdTag, SupportedTag.AlbumId, AlbumIdRawTag, track.AlbumId);
        AddSingleValueCustomTagWrite(writes, ReleaseStatusTag, SupportedTag.ReleaseStatus, ReleaseStatusRawTag, track.ReleaseStatus);
        AddSingleValueCustomTagWrite(writes, ReleaseCountryTag, SupportedTag.ReleaseCountry, ReleaseCountryRawTag, track.ReleaseCountry);
        AddSingleValueCustomTagWrite(writes, BarcodeTag, SupportedTag.Barcode, BarcodeRawTag, track.Barcode);
        AddSingleValueCustomTagWrite(writes, LyricistTag, SupportedTag.Lyricist, ResolveFieldRawName(SupportedTag.Lyricist, format, config), track.Lyricist);
        AddSingleValueCustomTagWrite(writes, PublisherTag, SupportedTag.Publisher, ResolveFieldRawName(SupportedTag.Publisher, format, config), track.Publisher);
        AddSingleValueCustomTagWrite(writes, DescriptionTag, SupportedTag.Description, ResolveFieldRawName(SupportedTag.Description, format, config), track.Description);
        if (track.Media.Count > 0)
        {
            writes.Add(new CustomTagWrite(MediaTag, SupportedTag.Media, MediaRawTag, track.Media.ToList()));
        }
        AddOtherTagWrites(writes, track.Other);
        AddMetaTagWrite(writes, config);

        return writes;
    }

    private static void AddSingleValueCustomTagWrite(
        List<CustomTagWrite> writes,
        string tagKey,
        SupportedTag supportedTag,
        string rawTagName,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        writes.Add(new CustomTagWrite(tagKey, supportedTag, rawTagName, new List<string> { value }));
    }

    private static void AddOtherTagWrites(List<CustomTagWrite> writes, IReadOnlyDictionary<string, List<string>> otherTags)
    {
        foreach (var kvp in otherTags.Where(kvp => kvp.Value.Count > 0 && !IsNonPersistedOtherRawKey(kvp.Key)))
        {
            var isReleaseType = kvp.Key.Equals(ReleaseTypeRawTag, StringComparison.OrdinalIgnoreCase);
            writes.Add(new CustomTagWrite(
                isReleaseType ? ReleaseTypeTag : OtherTagsTag,
                isReleaseType ? SupportedTag.ReleaseType : SupportedTag.OtherTags,
                kvp.Key,
                kvp.Value.ToList()));
        }
    }

    private static void AddMetaTagWrite(List<CustomTagWrite> writes, AutoTagRunnerConfig config)
    {
        if (!config.Tags.Any(tag => string.Equals(tag, MetaTagsTag, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        writes.Add(new CustomTagWrite(
            MetaTagsTag,
            SupportedTag.MetaTags,
            TaggedDateTag,
            new List<string> { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}_AT" }));
    }

    private static string ResolveFieldRawName(SupportedTag tag, string format, AutoTagRunnerConfig config)
    {
        return tag switch
        {
            SupportedTag.Key => format switch
            {
                "id3" => "TKEY",
                VorbisFormat => "INITIALKEY",
                _ => InitialKeyRawTag
            },
            SupportedTag.Style => format switch
            {
                "id3" => ResolveStylesTagName(config, ".mp3"),
                VorbisFormat => ResolveStylesTagName(config, FlacExtension),
                _ => ResolveStylesTagName(config, ".mp4")
            },
            SupportedTag.Version => format switch
            {
                "id3" => "TIT3",
                VorbisFormat => "SUBTITLE",
                _ => "desc"
            },
            SupportedTag.Remixer => format switch
            {
                "id3" => "TPE4",
                VorbisFormat => RemixerUpperTag,
                _ => RemixerUpperTag
            },
            SupportedTag.Mood => format switch
            {
                "id3" => "TMOO",
                VorbisFormat => "MOOD",
                _ => "MOOD"
            },
            SupportedTag.Lyricist => format == "id3" ? "TEXT" : LyricistRawTag,
            SupportedTag.Publisher => PublisherRawTag,
            SupportedTag.Description => format == "mp4" ? "ldes" : DescriptionRawTag,
            SupportedTag.Activity => "ACTIVITY",
            SupportedTag.CatalogNumber => CatalogNumberUpperTag,
            _ => tag.ToString()
        };
    }

    private sealed class TagWriteRequest
    {
        public required string FilePath { get; init; }
        public required AutoTagTrack SourceTrack { get; init; }
        public required Track CoreTrack { get; init; }
        public required TagSettings EffectiveTagSettings { get; init; }
        public required AutoTagRunnerConfig Config { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required string PlatformId { get; init; }
        public required string Separator { get; init; }
        public string? TempCoverPath { get; init; }
    }

    private sealed class TagWriteExecutionContext
    {
        public required string FilePath { get; init; }
        public required AutoTagTrack SourceTrack { get; init; }
        public required Track CoreTrack { get; init; }
        public required TagSettings EffectiveTagSettings { get; init; }
        public required AutoTagRunnerConfig Config { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required string PlatformId { get; init; }
        public required string Separator { get; init; }
        public string? TempCoverPath { get; init; }
        public required string Extension { get; init; }
        public required HashSet<string> EnabledTags { get; init; }
        public required IReadOnlyDictionary<string, string> GenreAliasMap { get; init; }
        public required IReadOnlyList<string> GenreBlockList { get; init; }
        public required bool SplitCompositeGenres { get; init; }
        public required bool AllowsLyricsBySettings { get; init; }
        public required bool AllowsSyncedType { get; init; }
        public required bool AllowsUnsyncedType { get; init; }
        public required bool AllowsLrcByFormat { get; init; }
        public required bool AllowsTtmlByFormat { get; init; }
        public required (bool HasAny, bool HasLrc, bool HasElrc, bool HasTtml, bool HasTxt, string TxtPath) SidecarState { get; init; }
        public required bool ShouldSkipEmbeddedLyrics { get; init; }
        public HashSet<SupportedTag> AttemptedTags { get; } = new();
    }

    private sealed record TagFileWriteResult(HashSet<SupportedTag> AttemptedTags);

    public sealed class LocalAutoTagRunnerCollaborators
    {
        public required ILogger<LocalAutoTagRunner> Logger { get; init; }
        public required IHttpClientFactory HttpClientFactory { get; init; }
        public required MusicBrainzMatcher MusicBrainzMatcher { get; init; }
        public required BeatportMatcher BeatportMatcher { get; init; }
        public required DiscogsMatcher DiscogsMatcher { get; init; }
        public required TraxsourceMatcher TraxsourceMatcher { get; init; }
        public required BandcampMatcher BandcampMatcher { get; init; }
        public required BpmSupremeMatcher BpmSupremeMatcher { get; init; }
        public required ItunesMatcher ItunesMatcher { get; init; }
        public required SpotifyMatcher SpotifyMatcher { get; init; }
        public required DeezerMatcher DeezerMatcher { get; init; }
        public required LastFmMatcher LastFmMatcher { get; init; }
        public required BoomplayMatcher BoomplayMatcher { get; init; }
        public required ShazamMatcher ShazamMatcher { get; init; }
        public required ShazamRecognitionService ShazamRecognitionService { get; init; }
        public required AppleLyricsService AppleLyricsService { get; init; }
        public required AppleMusicCatalogService AppleMusicCatalogService { get; init; }
        public required DownloadLyricsService DownloadLyricsService { get; init; }
        public required DeezSpoTagSettingsService SettingsService { get; init; }
        public required IServiceScopeFactory ServiceScopeFactory { get; init; }
        public required ITrackIdentityResolver TrackIdentityResolver { get; init; }
        public PortedPlatformRegistry? PlatformRegistry { get; init; }
    }

    private readonly record struct TagWriteContext(
        TagLib.File File,
        string Extension,
        AutoTagRunnerConfig Config,
        string Separator,
        string PlatformId,
        bool UseNullSeparator,
        IReadOnlyDictionary<string, string> GenreAliasMap,
        IReadOnlyList<string> GenreBlockList,
        bool SplitCompositeGenres,
        HashSet<SupportedTag> AttemptedTags);

    private readonly record struct TagFieldBinding(
        string Id3Frame,
        string VorbisField,
        string Mp4Field,
        SupportedTag Tag);

    private readonly record struct DateWritePayload(
        DateTime Date,
        bool UseYearOnly,
        string Year,
        string DateString);

    private readonly record struct LyricsSidecarWriteResult(
        bool WroteLrcSidecar,
        bool WroteTtmlSidecar);

    private readonly record struct OverwriteRuleContext(
        HashSet<string> EnabledTags,
        AutoTagRunnerConfig Config,
        TagLib.File File,
        string Extension,
        string PlatformId);

    private sealed record CustomTagWrite(string TagKey, SupportedTag SupportedTag, string RawTagName, List<string> Values);

    private static string ResolveFormatName(string extension)
    {
        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase)) return VorbisFormat;
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)) return "id3";
        return "mp4";
    }

    private static void SetField(TagWriteContext context, TagFieldBinding binding, List<string> values)
    {
        if (binding.Tag == SupportedTag.Genre)
        {
            values = SanitizeGenres(values, context.GenreAliasMap, context.GenreBlockList, context.SplitCompositeGenres);
        }

        if (values.Count == 0)
        {
            return;
        }

        if (IsMp4Family(context.Extension))
        {
            if (Mp4TagHelper.TrySetMp4Field(
                context,
                binding.Tag,
                values))
            {
                context.AttemptedTags.Add(binding.Tag);
                return;
            }

            SetRaw(context, binding.Mp4Field, binding.Tag, values);
            return;
        }

        var raw = ResolveFormatName(context.Extension) switch
        {
            "id3" => binding.Id3Frame,
            VorbisFormat => binding.VorbisField,
            _ => binding.Mp4Field
        };
        SetRaw(context, raw, binding.Tag, values);
    }

    private static void SetRaw(TagWriteContext context, string rawName, SupportedTag tag, List<string> values, bool force = false)
    {
        if (tag == SupportedTag.Genre || IsGenreRawTag(rawName))
        {
            values = SanitizeGenres(values, context.GenreAliasMap, context.GenreBlockList, context.SplitCompositeGenres);
            if (values.Count == 0)
            {
                return;
            }
        }
        else if (rawName.Equals(SpotifyUrlTag, StringComparison.OrdinalIgnoreCase))
        {
            values = NormalizeSpotifyTrackUrls(values);
            if (values.Count == 0)
            {
                return;
            }

            var existingValues = ReadRawTagValues(context.File, context.Extension, SpotifyUrlTag);
            force |= existingValues.Any(value => NormalizeSpotifyTrackUrl(value) == null);
        }

        if (!force && !ShouldOverwriteTag(context.Config, tag))
        {
            if (tag == SupportedTag.OtherTags)
            {
                if (HasRawTag(context.File, context.Extension, rawName))
                {
                    context.AttemptedTags.Add(tag);
                    return;
                }
            }
            else if (HasTag(context.File, context.Extension, tag, context.Config, context.PlatformId))
            {
                context.AttemptedTags.Add(tag);
                return;
            }
        }

        WriteRawTagValues(context, rawName, values);
        context.AttemptedTags.Add(tag);
    }

    private static bool HasRawTag(TagLib.File file, string extension, string rawName)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag?)file.GetTag(TagTypes.Id3v2, false);
            return id3 != null && TagRawProbe.HasId3Raw(id3, rawName);
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = (TagLib.Ogg.XiphComment?)file.GetTag(TagTypes.Xiph, false);
            return vorbis != null && TagRawProbe.HasVorbisRaw(vorbis, rawName);
        }

        if (IsMp4Family(extension))
        {
            return Mp4TagHelper.HasRaw(file, rawName);
        }

        return false;
    }

    private static void WriteDate(
        TagLib.File file,
        string extension,
        string kind,
        DateTime date,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        bool useNullSeparator)
    {
        var useYearOnly = IsYearOnlyDateFormat(config.Technical?.DateFormat);
        var payload = new DateWritePayload(
            Date: date,
            UseYearOnly: useYearOnly,
            Year: date.Year.ToString(CultureInfo.InvariantCulture),
            DateString: useYearOnly
                ? date.Year.ToString(CultureInfo.InvariantCulture)
                : date.ToString(IsoDateFormat, CultureInfo.InvariantCulture));

        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            WriteId3Date(file, kind, tag, config, payload, useNullSeparator);
            return;
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            WriteVorbisDate(file, kind, tag, config, payload.DateString);
            return;
        }

        WriteMp4Date(file, extension, kind, tag, config, payload.DateString);
    }

    private static void WriteId3Date(
        TagLib.File file,
        string kind,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        DateWritePayload payload,
        bool useNullSeparator)
    {
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
        if (kind == ReleaseDateTag)
        {
            if (ShouldSkipId3ReleaseDate(config, tag, id3, payload.UseYearOnly))
            {
                return;
            }

            if (config.Id3v24)
            {
                SetId3Raw(id3, "TDRC", new List<string> { payload.DateString }, ", ", useNullSeparator);
                return;
            }

            SetId3Raw(id3, "TYER", new List<string> { payload.Year }, ", ", useNullSeparator);
            if (!payload.UseYearOnly)
            {
                SetId3Raw(id3, "TDAT", new List<string> { payload.Date.ToString("ddMM", CultureInfo.InvariantCulture) }, ", ", useNullSeparator);
            }
            return;
        }

        if (!ShouldOverwriteTag(config, tag) && TagRawProbe.HasId3Raw(id3, "TDRL"))
        {
            return;
        }

        SetId3Raw(id3, "TDRL", new List<string> { payload.DateString }, ", ", useNullSeparator);
    }

    private static bool ShouldSkipId3ReleaseDate(
        AutoTagRunnerConfig config,
        SupportedTag tag,
        TagLib.Id3v2.Tag id3,
        bool useYearOnly)
    {
        if (ShouldOverwriteTag(config, tag))
        {
            return false;
        }

        if (config.Id3v24 && TagRawProbe.HasId3Raw(id3, "TDRC"))
        {
            return true;
        }

        return !config.Id3v24
            && (TagRawProbe.HasId3Raw(id3, "TYER")
                || (!useYearOnly && TagRawProbe.HasId3Raw(id3, "TDAT")));
    }

    private static void WriteVorbisDate(
        TagLib.File file,
        string kind,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        string dateString)
    {
        var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
        var field = kind == ReleaseDateTag ? "DATE" : OriginalDateUpperTag;
        if (!ShouldOverwriteTag(config, tag) && TagRawProbe.HasVorbisRaw(vorbis, field))
        {
            return;
        }

        SetVorbisRaw(vorbis, field, new List<string> { dateString }, "");
    }

    private static void WriteMp4Date(
        TagLib.File file,
        string extension,
        string kind,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        string dateString)
    {
        if (!IsMp4Family(extension))
        {
            return;
        }

        if (kind == ReleaseDateTag)
        {
            if (!ShouldOverwriteTag(config, tag)
                && (Mp4TagHelper.HasRaw(file, "©day")
                    || Mp4TagHelper.HasRaw(file, "DATE")))
            {
                return;
            }

            Mp4TagHelper.SetDate(file, dateString);
            var appleRelease = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
            TrySetAppleDashBox(appleRelease, "DATE", new[] { dateString });
            return;
        }

        if (kind != PublishDateTag)
        {
            return;
        }

        if (!ShouldOverwriteTag(config, tag) && Mp4TagHelper.HasRaw(file, OriginalDateUpperTag))
        {
            return;
        }

        var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
        TrySetAppleDashBox(apple, "ORIGINALDATE", new[] { dateString });
    }

    private static bool IsYearOnlyDateFormat(string? dateFormat)
        => string.Equals(dateFormat?.Trim(), "Y", StringComparison.OrdinalIgnoreCase);

    private static void SetTrackNumber(
        TagLib.File file,
        TagWriteExecutionContext context,
        int number,
        int? total,
        SupportedTag tag,
        bool isDisc)
    {
        var numberText = context.Config.TrackNumberLeadingZeroes > 0
            ? number.ToString($"D{context.Config.TrackNumberLeadingZeroes}", CultureInfo.InvariantCulture)
            : number.ToString(CultureInfo.InvariantCulture);

        if (context.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            WriteId3TrackNumber(file, numberText, total, tag, context.Config, context.EffectiveTagSettings.UseNullSeparator, isDisc);
            return;
        }

        if (context.Extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            WriteVorbisTrackNumber(file, numberText, total, tag, context.Config, isDisc);
            return;
        }

        WriteMp4TrackNumber(file, number, total, tag, context.Config, isDisc, context.Extension);
    }

    private static void WriteId3TrackNumber(
        TagLib.File file,
        string numberText,
        int? total,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        bool useNullSeparator,
        bool isDisc)
    {
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
        if (!ShouldOverwriteTag(config, tag) && (isDisc ? id3.Disc > 0 : id3.Track > 0))
        {
            return;
        }

        var value = total.HasValue ? $"{numberText}/{total.Value}" : numberText;
        var frame = TagLib.Id3v2.TextInformationFrame.Get(id3, isDisc ? "TPOS" : "TRCK", true);
        if (useNullSeparator)
        {
            frame.TextEncoding = TagLib.StringType.UTF16;
        }
        frame.Text = new[] { value };
    }

    private static void SetDiscTotal(TagLib.File file, TagWriteExecutionContext context, int total)
    {
        if (context.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
            if (!ShouldOverwriteTag(context.Config, SupportedTag.DiscTotal) && id3.DiscCount > 0)
            {
                return;
            }

            var discNumber = file.Tag.Disc > 0
                ? file.Tag.Disc
                : context.SourceTrack.DiscNumber is > 0
                    ? (uint)context.SourceTrack.DiscNumber.Value
                    : 0;
            var frame = TagLib.Id3v2.TextInformationFrame.Get(id3, "TPOS", true);
            frame.Text = new[] { discNumber > 0 ? $"{discNumber}/{total}" : $"0/{total}" };
            return;
        }

        if (context.Extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
            if (!ShouldOverwriteTag(context.Config, SupportedTag.DiscTotal)
                && TagRawProbe.HasVorbisRaw(vorbis, DiscTotalRawTag))
            {
                return;
            }

            SetVorbisRaw(vorbis, DiscTotalRawTag, new List<string> { total.ToString(CultureInfo.InvariantCulture) }, "");
            return;
        }

        if (IsMp4Family(context.Extension))
        {
            if (!ShouldOverwriteTag(context.Config, SupportedTag.DiscTotal) && file.Tag.DiscCount > 0)
            {
                return;
            }

            file.Tag.DiscCount = (uint)total;
        }
    }

    private static void WriteVorbisTrackNumber(
        TagLib.File file,
        string numberText,
        int? total,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        bool isDisc)
    {
        var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
        var field = isDisc ? "DISCNUMBER" : TrackNumberUpperTag;
        if (!ShouldOverwriteTag(config, tag) && TagRawProbe.HasVorbisRaw(vorbis, field))
        {
            return;
        }

        SetVorbisRaw(vorbis, field, new List<string> { numberText }, "");
        if (isDisc
            && total.HasValue
            && (ShouldOverwriteTag(config, SupportedTag.DiscTotal) || !TagRawProbe.HasVorbisRaw(vorbis, DiscTotalRawTag)))
        {
            SetVorbisRaw(vorbis, DiscTotalRawTag, new List<string> { total.Value.ToString(CultureInfo.InvariantCulture) }, "");
        }
        if (!isDisc
            && total.HasValue
            && (ShouldOverwriteTag(config, SupportedTag.TrackTotal) || !TagRawProbe.HasVorbisRaw(vorbis, TrackTotalRawTag)))
        {
            SetVorbisRaw(vorbis, TrackTotalRawTag, new List<string> { total.Value.ToString(CultureInfo.InvariantCulture) }, "");
        }
    }

    private static void WriteMp4TrackNumber(
        TagLib.File file,
        int number,
        int? total,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        bool isDisc,
        string extension)
    {
        if (!IsMp4Family(extension))
        {
            return;
        }

        if (!ShouldOverwriteTag(config, tag) && (isDisc ? file.Tag.Disc > 0 : file.Tag.Track > 0))
        {
            return;
        }

        if (!isDisc)
        {
            file.Tag.Track = (uint)number;
            if (total.HasValue
                && (ShouldOverwriteTag(config, SupportedTag.TrackTotal) || file.Tag.TrackCount == 0))
            {
                file.Tag.TrackCount = (uint)total.Value;
            }
            return;
        }

        file.Tag.Disc = (uint)number;
        if (total.HasValue)
        {
            file.Tag.DiscCount = (uint)total.Value;
        }
    }

    private static bool WriteLyrics(TagLib.File file, string extension, AutoTagTrack track, bool synced, AutoTagRunnerConfig config)
    {
        if (!TryResolveLyricsLines(track, synced, out var lyricsLines))
        {
            return false;
        }

        var lyricsText = string.Join(Environment.NewLine, lyricsLines);
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return WriteId3Lyrics(file, synced, config, lyricsLines, lyricsText);
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            return WriteVorbisLyrics(file, synced, config, lyricsText);
        }

        return WriteGenericLyrics(file, synced, config, lyricsText);
    }

    private static bool TryResolveLyricsLines(AutoTagTrack track, bool synced, out List<string> lyricsLines)
    {
        var key = synced ? SyncedLyricsTag : UnsyncedLyricsTag;
        if (track.Other.TryGetValue(key, out var preferred) && preferred is { Count: > 0 })
        {
            lyricsLines = preferred;
            return true;
        }

        if (track.Other.TryGetValue(LyricsTag, out var fallback) && fallback is { Count: > 0 })
        {
            lyricsLines = fallback;
            return true;
        }

        lyricsLines = new List<string>();
        return false;
    }

    private static bool WriteId3Lyrics(
        TagLib.File file,
        bool synced,
        AutoTagRunnerConfig config,
        IReadOnlyList<string> lyricsLines,
        string lyricsText)
    {
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
        if (synced)
        {
            return WriteId3SyncedLyrics(id3, config, lyricsLines);
        }

        return WriteId3UnsyncedLyrics(id3, config, lyricsText);
    }

    private static bool WriteId3SyncedLyrics(TagLib.Id3v2.Tag id3, AutoTagRunnerConfig config, IReadOnlyList<string> lyricsLines)
    {
        if (!ShouldOverwriteTag(config, SupportedTag.SyncedLyrics)
            && id3.GetFrames<TagLib.Id3v2.SynchronisedLyricsFrame>("SYLT").Any())
        {
            return true;
        }

        if (!lyricsLines.Any(line => line.StartsWith('[')))
        {
            return false;
        }

        var lang = string.IsNullOrWhiteSpace(config.Id3CommLang) ? "eng" : config.Id3CommLang;
        var frame = new TagLib.Id3v2.SynchronisedLyricsFrame(string.Empty, lang, TagLib.Id3v2.SynchedTextType.Lyrics)
        {
            Format = TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds
        };

        frame.Text = BuildSyncedLyricsItems(lyricsLines).ToArray();
        id3.AddFrame(frame);
        return true;
    }

    private static List<TagLib.Id3v2.SynchedText> BuildSyncedLyricsItems(IReadOnlyList<string> lyricsLines)
    {
        var items = new List<TagLib.Id3v2.SynchedText>();
        foreach (var line in lyricsLines)
        {
            if (!TryParseLrcLine(line, out var timestamp, out var text))
            {
                continue;
            }

            items.Add(new TagLib.Id3v2.SynchedText((long)timestamp.TotalMilliseconds, text));
        }

        return items;
    }

    private static bool WriteId3UnsyncedLyrics(TagLib.Id3v2.Tag id3, AutoTagRunnerConfig config, string lyricsText)
    {
        if (!ShouldOverwriteTag(config, SupportedTag.UnsyncedLyrics)
            && id3.GetFrames<TagLib.Id3v2.UnsynchronisedLyricsFrame>("USLT").Any())
        {
            return true;
        }

        var lang = string.IsNullOrWhiteSpace(config.Id3CommLang) ? "eng" : config.Id3CommLang;
        var frame = TagLib.Id3v2.UnsynchronisedLyricsFrame.Get(id3, string.Empty, lang, true);
        frame.Text = lyricsText;
        return true;
    }

    private static bool WriteVorbisLyrics(TagLib.File file, bool synced, AutoTagRunnerConfig config, string lyricsText)
    {
        var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
        var supportedTag = synced ? SupportedTag.SyncedLyrics : SupportedTag.UnsyncedLyrics;
        if (!ShouldOverwriteTag(config, supportedTag) && TagRawProbe.HasVorbisRaw(vorbis, LyricsUpperTag))
        {
            return true;
        }

        vorbis.SetField(LyricsUpperTag, lyricsText);
        return true;
    }

    private static bool WriteGenericLyrics(TagLib.File file, bool synced, AutoTagRunnerConfig config, string lyricsText)
    {
        var supportedTag = synced ? SupportedTag.SyncedLyrics : SupportedTag.UnsyncedLyrics;
        if (!ShouldOverwriteTag(config, supportedTag) && !string.IsNullOrWhiteSpace(file.Tag.Lyrics))
        {
            return true;
        }

        file.Tag.Lyrics = lyricsText;
        return true;
    }

    private static bool TryParseLrcLine(string line, out TimeSpan timestamp, out string text)
    {
        timestamp = TimeSpan.Zero;
        text = "";
        if (line.Length < 6 || line[0] != '[')
        {
            return false;
        }

        var end = line.IndexOf(']');
        if (end <= 0)
        {
            return false;
        }

        var ts = line[1..end];
        var parts = ts.Split(':');
        if (parts.Length != 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var minutes))
        {
            return false;
        }

        if (!double.TryParse(parts[1], out var seconds))
        {
            return false;
        }

        timestamp = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        text = line[(end + 1)..].Trim();
        return true;
    }

    private static IReadOnlyList<string> ResolveLrcSidecarLines(AutoTagTrack sourceTrack, string filePath, DeezSpoTagSettings settings)
    {
        var syncedPayload = ResolveLyricsPayloadLines(sourceTrack, SyncedLyricsTag);
        var payloadUsable = syncedPayload.Count > 0 && HasLrcSidecarSourceFormat(sourceTrack);
        var timingPreference = LrcTimingModes.Normalize(settings.LrcTimingPreference, settings.PreferEnhancedLrc);
        var payloadIsWord = payloadUsable && LrcContent.IsWordSynchronized(syncedPayload);

        var existingLrc = ResolveExistingLrcSidecar(filePath);
        if (existingLrc.Count > 0)
        {
            var existingIsWord = LrcContent.IsWordSynchronized(existingLrc);
            if (LrcTimingModes.ImpliesEnhanced(timingPreference)
                && payloadIsWord
                && !existingIsWord)
            {
                return syncedPayload;
            }

            return existingLrc;
        }

        if (timingPreference == LrcTimingModes.WordEnhanced)
        {
            return payloadIsWord ? syncedPayload : Array.Empty<string>();
        }

        return payloadUsable ? syncedPayload : Array.Empty<string>();
    }

    private static bool HasLrcSidecarSourceFormat(AutoTagTrack sourceTrack)
    {
        return sourceTrack.Other.TryGetValue(SyncedLyricsSourceFormatTag, out var values)
            && values.Any(value =>
                string.Equals(value, LyricsSourceFormat.DownloadedLrc.ToString(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, LyricsSourceFormat.ProviderSyncedJson.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ResolveExistingLrcSidecar(string filePath)
    {
        var existingLrcPath = Path.ChangeExtension(filePath, ".lrc");
        if (!IOFile.Exists(existingLrcPath))
        {
            return Array.Empty<string>();
        }

        try
        {
            return NormalizeLyricsLines(IOFile.ReadAllLines(existingLrcPath), requireTimestamp: true);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ResolveLyricsPayloadLines(AutoTagTrack sourceTrack, string key)
    {
        if (!sourceTrack.Other.TryGetValue(key, out var payload) || payload.Count == 0)
        {
            return Array.Empty<string>();
        }

        return NormalizeLyricsLines(payload, requireTimestamp: true);
    }

    private static string? ResolveTtmlSidecarPayload(AutoTagTrack sourceTrack, string filePath)
    {
        var existingTtmlPath = Path.ChangeExtension(filePath, TtmlExtension);
        if (IOFile.Exists(existingTtmlPath)
            && AppleLyricsService.IsWordSyncedTtml(ReadFileOrEmpty(existingTtmlPath)))
        {
            return null;
        }

        if (sourceTrack.Other.TryGetValue(TtmlLyricsTag, out var ttmlPayload) && ttmlPayload.Count > 0)
        {
            var existing = ComposeTtmlPayload(ttmlPayload);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }
        }

        return null;
    }

    private static List<string> NormalizeLyricsLines(IEnumerable<string> lines, bool requireTimestamp)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trimmed in lines
            .Select(static line => line?.Trim())
            .Where(static trimmed => !string.IsNullOrWhiteSpace(trimmed)))
        {
            if (requireTimestamp && !trimmed!.StartsWith('['))
            {
                continue;
            }

            if (seen.Add(trimmed!))
            {
                normalized.Add(trimmed!);
            }
        }

        return normalized;
    }

    private static string? ComposeTtmlPayload(IEnumerable<string> payloadLines)
    {
        var ttml = string.Join(Environment.NewLine, payloadLines.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(ttml) ? null : ttml;
    }

    private static void ApplyAlbumArt(TagLib.File file, string imagePath, bool coverDescriptionUtf8)
    {
        if (!IOFile.Exists(imagePath))
        {
            return;
        }

        var data = IOFile.ReadAllBytes(imagePath);
        var picture = new TagLib.Picture
        {
            Data = data,
            Type = TagLib.PictureType.FrontCover,
            MimeType = CoverArtMimeTypeResolver.Resolve(imagePath, data),
            Description = "Cover"
        };

        var extension = Path.GetExtension(file.Name);
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
            id3.RemoveFrames("APIC");
            var apic = new TagLib.Id3v2.AttachmentFrame(picture)
            {
                TextEncoding = coverDescriptionUtf8 ? TagLib.StringType.UTF8 : TagLib.StringType.Latin1
            };
            id3.AddFrame(apic);
        }

        file.Tag.Pictures = new[] { picture };
    }

    private static class Mp4TagHelper
    {
        public static bool HasField(TagLib.File file, SupportedTag tag)
        {
            return tag switch
            {
                SupportedTag.Title => !string.IsNullOrWhiteSpace(file.Tag.Title),
                SupportedTag.Artist => file.Tag.Performers?.Length > 0,
                SupportedTag.AlbumArtist => file.Tag.AlbumArtists?.Length > 0,
                SupportedTag.Album => !string.IsNullOrWhiteSpace(file.Tag.Album),
                SupportedTag.Genre => file.Tag.Genres?.Length > 0,
                SupportedTag.BPM => file.Tag.BeatsPerMinute > 0,
                SupportedTag.TrackNumber => file.Tag.Track > 0,
                SupportedTag.TrackTotal => file.Tag.TrackCount > 0,
                SupportedTag.DiscNumber => file.Tag.Disc > 0,
                SupportedTag.DiscTotal => file.Tag.DiscCount > 0,
                SupportedTag.UnsyncedLyrics => !string.IsNullOrWhiteSpace(file.Tag.Lyrics),
                SupportedTag.AlbumArt => file.Tag.Pictures?.Length > 0,
                _ => false
            };
        }

        public static bool TrySetMp4Field(
            TagWriteContext context,
            SupportedTag tag,
            List<string> values)
        {
            if (!ShouldOverwriteTag(context.Config, tag) && HasTag(context.File, ".mp4", tag, context.Config, context.PlatformId))
            {
                return true;
            }

            switch (tag)
            {
                case SupportedTag.Title:
                    context.File.Tag.Title = values.FirstOrDefault() ?? "";
                    return true;
                case SupportedTag.Artist:
                    context.File.Tag.Performers = values.ToArray();
                    return true;
                case SupportedTag.AlbumArtist:
                    context.File.Tag.AlbumArtists = values.ToArray();
                    return true;
                case SupportedTag.Album:
                    context.File.Tag.Album = values.FirstOrDefault() ?? "";
                    return true;
                case SupportedTag.Genre:
                    context.File.Tag.Genres = SanitizeGenres(
                        values,
                        context.GenreAliasMap,
                        context.GenreBlockList,
                        context.SplitCompositeGenres).ToArray();
                    return true;
                case SupportedTag.BPM:
                    if (int.TryParse(values.FirstOrDefault(), out var bpm))
                    {
                        context.File.Tag.BeatsPerMinute = (uint)bpm;
                    }
                    return true;
                case SupportedTag.TrackNumber:
                    if (int.TryParse(values.FirstOrDefault(), out var track))
                    {
                        context.File.Tag.Track = (uint)track;
                    }
                    return true;
                case SupportedTag.TrackTotal:
                    if (int.TryParse(values.FirstOrDefault(), out var total))
                    {
                        context.File.Tag.TrackCount = (uint)total;
                    }
                    return true;
                case SupportedTag.DiscNumber:
                    if (int.TryParse(values.FirstOrDefault(), out var disc))
                    {
                        context.File.Tag.Disc = (uint)disc;
                    }
                    return true;
                case SupportedTag.DiscTotal:
                    if (int.TryParse(values.FirstOrDefault(), out var discTotal))
                    {
                        context.File.Tag.DiscCount = (uint)discTotal;
                    }
                    return true;
                default:
                    return false;
            }
        }

        public static void SetMp4Raw(
            TagLib.File file,
            string rawName,
            string[] values,
            IReadOnlyDictionary<string, string> genreAliasMap,
            IReadOnlyList<string> genreBlockList,
            bool splitCompositeGenres)
        {
            var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
            var normalized = Mp4RawTagNameNormalizer.Normalize(rawName);
            var output = IsGenreRawTag(normalized) || IsGenreRawTag(rawName)
                ? SanitizeGenres(values, genreAliasMap, genreBlockList, splitCompositeGenres).ToArray()
                : values;
            TrySetAppleDashBox(apple, normalized, output);
        }

        public static bool HasRaw(TagLib.File file, string rawName)
        {
            return HasMp4RawValue(file, rawName);
        }

        private static bool HasMp4RawValue(TagLib.File file, string rawName)
        {
            var normalized = Mp4RawTagNameNormalizer.Normalize(rawName);
            var apple = (TagLib.Mpeg4.AppleTag?)file.GetTag(TagTypes.Apple, false);
            if (apple != null && TagRawProbe.HasAppleDashBox(apple, normalized))
            {
                return true;
            }

            if (ReadMp4AtlRawValues(file.Name, normalized).Count > 0)
            {
                return true;
            }

            return normalized.ToUpperInvariant() switch
            {
                "©NAM" or TitleUpperTag => !string.IsNullOrWhiteSpace(file.Tag.Title),
                "©ART" or ArtistUpperTag or "ARTISTS" => file.Tag.Performers?.Any(value => !string.IsNullOrWhiteSpace(value)) == true,
                "AART" or AlbumArtistUpperTag => file.Tag.AlbumArtists?.Any(value => !string.IsNullOrWhiteSpace(value)) == true,
                "©ALB" or AlbumUpperTag => !string.IsNullOrWhiteSpace(file.Tag.Album),
                "ISRC" => !string.IsNullOrWhiteSpace(file.Tag.ISRC),
                "©GEN" or Mp4GenreTag => file.Tag.Genres?.Any(value => !string.IsNullOrWhiteSpace(value)) == true,
                "TRACK" or "TRKN" => file.Tag.Track > 0 || file.Tag.TrackCount > 0,
                "DISC" or "DISK" => file.Tag.Disc > 0 || file.Tag.DiscCount > 0,
                "LYRICS" or "©LYR" => !string.IsNullOrWhiteSpace(file.Tag.Lyrics),
                _ => false
            };
        }

        public static void SetDate(TagLib.File file, string dateString)
        {
            var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
            TrySetAppleDashBox(apple, "©day", new[] { dateString });
        }
    }

    private static string CapitalizeGenre(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            words[i] = word.Length > 1
                ? char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()
                : word.ToUpperInvariant();
        }
        return string.Join(' ', words);
    }

    private static bool IsGenreRawTag(string rawName)
    {
        var normalized = rawName.Trim();
        var mp4Normalized = Mp4RawTagNameNormalizer.Normalize(normalized);
        return normalized.Equals("TCON", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(Mp4GenreTag, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("©gen", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals($"----:com.apple.iTunes:{Mp4GenreTag}", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals($"iTunes:{Mp4GenreTag}", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals($"com.apple.iTunes:{Mp4GenreTag}", StringComparison.OrdinalIgnoreCase)
            || mp4Normalized.Equals(Mp4GenreTag, StringComparison.OrdinalIgnoreCase)
            || mp4Normalized.Equals("©gen", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SanitizeGenres(
        IEnumerable<string> values,
        IReadOnlyDictionary<string, string>? genreAliasMap = null,
        IEnumerable<string>? genreBlockList = null,
        bool splitComposite = false)
    {
        return GenreTagAliasNormalizer.NormalizeExpandFilterAndDedupeValues(
            values,
            genreAliasMap,
            splitComposite,
            genreBlockList ?? BlockedGenres);
    }

    private static string ToCamelot(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        foreach (var (original, camelot) in CamelotNotes)
        {
            if (string.Equals(original, key, StringComparison.OrdinalIgnoreCase))
            {
                return camelot;
            }
        }
        return key;
    }

    private static readonly (string Original, string Camelot)[] CamelotNotes =
    {
        ("Abm", "1A"),
        ("G#m", "1A"),
        ("B", "1B"),
        ("D#m", "2A"),
        ("Ebm", "2A"),
        ("Gb", "2B"),
        ("F#", "2B"),
        ("A#m", "3A"),
        ("Bbm", "3A"),
        ("C#", "3B"),
        ("Db", "3B"),
        ("Dd", "3B"),
        ("Fm", "4A"),
        ("G#", "4B"),
        ("Ab", "4B"),
        ("Cm", "5A"),
        ("D#", "5B"),
        ("Eb", "5B"),
        ("Gm", "6A"),
        ("A#", "6B"),
        ("Bb", "6B"),
        ("Dm", "7A"),
        ("F", "7B"),
        ("Am", "8A"),
        ("C", "8B"),
        ("Em", "9A"),
        ("G", "9B"),
        ("Bm", "10A"),
        ("D", "10B"),
        ("Gbm", "11A"),
        ("F#m", "11A"),
        ("A", "11B"),
        ("C#m", "12A"),
        ("Dbm", "12A"),
        ("E", "12B")
    };

    private static void SetId3Raw(TagLib.Id3v2.Tag tag, string name, List<string> values, string separator, bool useNullSeparator = false)
    {
        var output = ApplySeparator(values, separator, useNullSeparator);
        if (name.Length == 4)
        {
            var frame = TagLib.Id3v2.TextInformationFrame.Get(tag, name, true);
            if (useNullSeparator)
            {
                frame.TextEncoding = TagLib.StringType.UTF16;
            }
            frame.Text = output;
            return;
        }

        var user = TagLib.Id3v2.UserTextInformationFrame.Get(tag, name, true);
        if (useNullSeparator)
        {
            user.TextEncoding = TagLib.StringType.UTF16;
        }
        user.Text = output;
    }

    private static void SetVorbisRaw(TagLib.Ogg.XiphComment tag, string name, List<string> values, string separator)
    {
        var output = ApplySeparator(values, separator);
        tag.SetField(name, output);
    }

    private static List<string> ReadExistingRawTag(TagLib.File file, string extension, string name)
    {
        return ReadRawTagValuesCore(
            file,
            extension,
            name,
            static (apple, rawName) => TagRawProbe.HasAppleDashBox(apple, rawName)
                ? new List<string> { rawName }
                : new List<string>());
    }

    private static List<string> ReadRawTagValues(TagLib.File file, string extension, string name)
    {
        return ReadRawTagValuesCore(file, extension, name, ReadAppleDashBox);
    }

    private static List<string> ReadRawTagValuesCore(
        TagLib.File file,
        string extension,
        string name,
        Func<TagLib.Mpeg4.AppleTag, string, List<string>> readAppleValues)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag?)file.GetTag(TagTypes.Id3v2, false);
            if (id3 == null) return new List<string>();
            if (name.Length == 4)
            {
                var frame = TagLib.Id3v2.TextInformationFrame.Get(id3, name, false);
                return frame?.Text?.ToList() ?? new List<string>();
            }

            var user = TagLib.Id3v2.UserTextInformationFrame.Get(id3, name, false);
            return user?.Text?.ToList() ?? new List<string>();
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = (TagLib.Ogg.XiphComment?)file.GetTag(TagTypes.Xiph, false);
            return vorbis?.GetField(name).ToList() ?? new List<string>();
        }

        if (IsMp4Family(extension))
        {
            var apple = (TagLib.Mpeg4.AppleTag?)file.GetTag(TagTypes.Apple, false);
            var normalizedName = Mp4RawTagNameNormalizer.Normalize(name);
            if (apple != null)
            {
                var dashValues = readAppleValues(apple, normalizedName);
                if (dashValues.Count > 0)
                {
                    return dashValues;
                }
            }

            return ReadMp4AtlRawValues(file.Name, normalizedName);
        }

        return new List<string>();
    }

    private static List<string> ReadMp4AtlRawValues(string filePath, string rawName)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !IOFile.Exists(filePath))
        {
            return new List<string>();
        }

        try
        {
            var atlTrack = new ATL.Track(filePath);
            var normalized = Mp4RawTagNameNormalizer.Normalize(rawName);
            var values = new List<string>();

            AddMp4AtlNativeRawValues(values, atlTrack, normalized);

            if (atlTrack.AdditionalFields != null)
            {
                var additional = new Dictionary<string, string>(atlTrack.AdditionalFields, StringComparer.OrdinalIgnoreCase);
                AddIfPresent(values, ResolveAtlAdditionalValue(additional, normalized));
                AddIfPresent(values, ResolveAtlAdditionalValue(additional, BuildAtlDashFieldName(normalized)));
            }

            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return new List<string>();
        }
    }

    private static void AddMp4AtlNativeRawValues(List<string> values, ATL.Track atlTrack, string normalized)
    {
        switch (normalized.ToUpperInvariant())
        {
            case "©NAM":
            case TitleUpperTag:
                AddIfPresent(values, atlTrack.Title);
                break;
            case "©ART":
            case ArtistUpperTag:
            case "ARTISTS":
                AddIfPresent(values, atlTrack.Artist);
                break;
            case "©ALB":
            case AlbumUpperTag:
                AddIfPresent(values, atlTrack.Album);
                break;
            case "AART":
            case AlbumArtistUpperTag:
            case "ALBUM ARTIST":
                AddIfPresent(values, atlTrack.AlbumArtist);
                break;
            case "©WRT":
            case ComposerUpperTag:
                AddIfPresent(values, atlTrack.Composer);
                break;
            case "©GEN":
            case Mp4GenreTag:
                AddIfPresent(values, atlTrack.Genre);
                break;
            case "ISRC":
                AddIfPresent(values, atlTrack.ISRC);
                break;
            case "DATE":
            case "YEAR":
            case "©DAY":
                AddMp4AtlDateValue(values, atlTrack);
                break;
            case "BPM":
            case "TMPO":
                AddMp4AtlPositiveNumberValue(values, atlTrack.BPM);
                break;
            case "TRACK":
            case "TRKN":
                AddMp4AtlPositiveNumberValue(values, atlTrack.TrackNumber);
                break;
            case "DISC":
            case "DISK":
                AddMp4AtlPositiveNumberValue(values, atlTrack.DiscNumber);
                break;
            case "LYRICS":
            case "©LYR":
                AddMp4AtlLyricsValues(values, atlTrack);
                break;
        }
    }

    private static void AddMp4AtlDateValue(List<string> values, ATL.Track atlTrack)
    {
        if (atlTrack.Date.HasValue)
        {
            AddIfPresent(values, atlTrack.Date.Value.ToString(IsoDateFormat));
        }
    }

    private static void AddMp4AtlPositiveNumberValue(List<string> values, double? value)
    {
        if (value is > 0)
        {
            AddIfPresent(values, value.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AddMp4AtlLyricsValues(List<string> values, ATL.Track atlTrack)
    {
        if (atlTrack.Lyrics == null || atlTrack.Lyrics.Count == 0)
        {
            return;
        }

        foreach (var line in atlTrack.Lyrics)
        {
            AddIfPresent(values, line?.UnsynchronizedLyrics);
        }
    }

    private static string ResolveAtlAdditionalValue(Dictionary<string, string> additional, string key)
    {
        return additional.TryGetValue(key, out var value)
            ? value
            : string.Empty;
    }

    private static void AddIfPresent(List<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value);
        }
    }

    private static void ApplyId3CustomTags(
        TagLib.Id3v2.Tag tag,
        List<CustomTagWrite> writes,
        AutoTagRunnerConfig config,
        string separator,
        bool useNullSeparator,
        HashSet<string> enabledTags,
        HashSet<SupportedTag> attemptedTags)
    {
        foreach (var write in writes)
        {
            if (!enabledTags.Contains(write.TagKey) || write.Values.Count == 0)
            {
                continue;
            }

            if (!ShouldOverwriteTag(config, write.SupportedTag) && TagRawProbe.HasId3Raw(tag, write.RawTagName))
            {
                attemptedTags.Add(write.SupportedTag);
                continue;
            }

            SetId3Raw(tag, write.RawTagName, write.Values, separator, useNullSeparator);
            attemptedTags.Add(write.SupportedTag);
        }
    }

    private static void ApplyVorbisCustomTags(TagLib.Ogg.XiphComment tag, List<CustomTagWrite> writes, AutoTagRunnerConfig config, string separator, HashSet<string> enabledTags, HashSet<SupportedTag> attemptedTags)
    {
        foreach (var write in writes)
        {
            if (!enabledTags.Contains(write.TagKey) || write.Values.Count == 0)
            {
                continue;
            }

            if (!ShouldOverwriteTag(config, write.SupportedTag) && TagRawProbe.HasVorbisRaw(tag, write.RawTagName))
            {
                attemptedTags.Add(write.SupportedTag);
                continue;
            }

            SetVorbisRaw(tag, write.RawTagName, write.Values, separator);
            attemptedTags.Add(write.SupportedTag);
        }
    }

    private static void ApplyAppleCustomTags(TagLib.Mpeg4.AppleTag tag, List<CustomTagWrite> writes, AutoTagRunnerConfig config, string separator, HashSet<string> enabledTags, HashSet<SupportedTag> attemptedTags)
    {
        foreach (var write in writes)
        {
            if (!enabledTags.Contains(write.TagKey) || write.Values.Count == 0)
            {
                continue;
            }

            var rawName = Mp4RawTagNameNormalizer.Normalize(write.RawTagName);
            if (!ShouldOverwriteTag(config, write.SupportedTag) && TagRawProbe.HasAppleDashBox(tag, rawName))
            {
                attemptedTags.Add(write.SupportedTag);
                continue;
            }

            TrySetAppleDashBox(tag, rawName, ApplySeparator(write.Values, separator));
            attemptedTags.Add(write.SupportedTag);
        }
    }

    private static TagSettings ApplyOverwriteRules(
        string filePath,
        TagSettings baseSettings,
        AutoTagRunnerConfig config,
        string platformId,
        AutoTagTrack? sourceTrack = null,
        DeezSpoTagSettings? runtimeSettings = null)
    {
        var copy = CloneTagSettings(baseSettings);
        if (config.Tags.Count == 0)
        {
            return copy;
        }

        try
        {
            using var file = TagLib.File.Create(filePath);
            var extension = Path.GetExtension(filePath);
            var enabled = BuildConfiguredTagSet(config.Tags);
            var context = new OverwriteRuleContext(enabled, config, file, extension, platformId);

            ApplyOverwriteRule(copy, context, TitleTag, SupportedTag.Title, static c => c.Title = false);
            ApplyOverwriteRule(copy, context, ArtistTag, SupportedTag.Artist, static c => c.Artist = false);
            ApplyOverwriteRule(copy, context, AlbumArtistTag, SupportedTag.AlbumArtist, static c => c.AlbumArtist = false);
            ApplyOverwriteRule(copy, context, AlbumTag, SupportedTag.Album, static c => c.Album = false);
            ApplyOverwriteRule(copy, context, GenreTag, SupportedTag.Genre, static c => c.Genre = false);
            ApplyOverwriteRule(copy, context, LabelTag, SupportedTag.Label, static c => c.Label = false);
            ApplyOverwriteRule(copy, context, "bpm", SupportedTag.BPM, static c => c.Bpm = false);
            ApplyOverwriteRule(copy, context, "isrc", SupportedTag.ISRC, static c => c.Isrc = false);
            ApplyOverwriteRule(copy, context, DurationTag, SupportedTag.Duration, static c => c.Length = false);
            ApplyOverwriteRule(copy, context, DiscNumberTag, SupportedTag.DiscNumber, static c => c.DiscNumber = false);
            ApplyOverwriteRule(copy, context, AlbumArtTag, SupportedTag.AlbumArt, static c => c.Cover = false);
            ApplyOverwriteRule(copy, context, UnsyncedLyricsTag, SupportedTag.UnsyncedLyrics, static c => c.Lyrics = false);
            ApplyOverwriteRule(copy, context, SyncedLyricsTag, SupportedTag.SyncedLyrics, static c => c.SyncedLyrics = false);

            ApplyReleaseDateOverwriteRule(copy, context);
            ApplyTrackNumberOverwriteRule(copy, context);
            ApplyTrackTotalOverwriteRule(copy, context);
            ApplyPreferenceAwareOverwriteGuards(copy, sourceTrack, runtimeSettings, file, platformId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return copy;
        }

        return copy;
    }

    private static TagSettings CloneTagSettings(TagSettings baseSettings)
    {
        return new TagSettings
        {
            Title = baseSettings.Title,
            Artist = baseSettings.Artist,
            Artists = baseSettings.Artists,
            Album = baseSettings.Album,
            Cover = baseSettings.Cover,
            TrackNumber = baseSettings.TrackNumber,
            TrackTotal = baseSettings.TrackTotal,
            DiscNumber = baseSettings.DiscNumber,
            DiscTotal = baseSettings.DiscTotal,
            AlbumArtist = baseSettings.AlbumArtist,
            Genre = baseSettings.Genre,
            Year = baseSettings.Year,
            Date = baseSettings.Date,
            Explicit = baseSettings.Explicit,
            Isrc = baseSettings.Isrc,
            Barcode = baseSettings.Barcode,
            Length = baseSettings.Length,
            Bpm = baseSettings.Bpm,
            ReplayGain = baseSettings.ReplayGain,
            Label = baseSettings.Label,
            Copyright = baseSettings.Copyright,
            Lyrics = baseSettings.Lyrics,
            SyncedLyrics = baseSettings.SyncedLyrics,
            Composer = baseSettings.Composer,
            InvolvedPeople = baseSettings.InvolvedPeople,
            Source = baseSettings.Source,
            Rating = baseSettings.Rating,
            SavePlaylistAsCompilation = baseSettings.SavePlaylistAsCompilation,
            UseNullSeparator = baseSettings.UseNullSeparator,
            SaveID3v1 = baseSettings.SaveID3v1,
            Url = baseSettings.Url,
            TrackId = baseSettings.TrackId,
            ReleaseId = baseSettings.ReleaseId,
            MultiArtistSeparator = baseSettings.MultiArtistSeparator,
            SingleAlbumArtist = baseSettings.SingleAlbumArtist,
            CoverDescriptionUTF8 = baseSettings.CoverDescriptionUTF8
        };
    }

    private static void SetRawIfAllowed(
        TagWriteContext context,
        string configTagKey,
        string rawName,
        List<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        if (!ShouldOverwriteRawTag(context.File, context.Extension, context.Config, configTagKey, rawName))
        {
            if (SupportedTagMap.TryGetValue(configTagKey, out var retainedTag))
            {
                context.AttemptedTags.Add(retainedTag);
            }
            return;
        }

        WriteRawTagValues(context, rawName, values);
        if (SupportedTagMap.TryGetValue(configTagKey, out var supportedTag))
        {
            context.AttemptedTags.Add(supportedTag);
        }
    }

    private static void WriteRawTagValues(TagWriteContext context, string rawName, List<string> values)
    {
        if (context.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag)context.File.GetTag(TagTypes.Id3v2, true);
            SetId3Raw(id3, rawName, values, context.Separator, context.UseNullSeparator);
            return;
        }

        if (context.Extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = (TagLib.Ogg.XiphComment)context.File.GetTag(TagTypes.Xiph, true);
            SetVorbisRaw(vorbis, rawName, values, context.Separator);
            return;
        }

        if (IsMp4Family(context.Extension))
        {
            Mp4TagHelper.SetMp4Raw(
                context.File,
                rawName,
                ApplySeparator(values, context.Separator),
                context.GenreAliasMap,
                context.GenreBlockList,
                context.SplitCompositeGenres);
        }
    }

    private static void RemoveRawTagValues(TagWriteContext context, string rawName)
    {
        if (context.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag?)context.File.GetTag(TagTypes.Id3v2, false);
            if (id3 == null)
            {
                return;
            }

            if (rawName.Length == 4)
            {
                id3.RemoveFrames(rawName);
                return;
            }

            foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>("TXXX")
                         .Where(frame => string.Equals(frame.Description, rawName, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                id3.RemoveFrame(frame);
            }
            return;
        }

        if (context.Extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = (TagLib.Ogg.XiphComment?)context.File.GetTag(TagTypes.Xiph, false);
            vorbis?.RemoveField(rawName);
            return;
        }

        if (IsMp4Family(context.Extension))
        {
            var apple = (TagLib.Mpeg4.AppleTag?)context.File.GetTag(TagTypes.Apple, false);
            AppleDashBoxReflectionHelper.TryClearValues(apple, Mp4RawTagNameNormalizer.Normalize(rawName));
        }
    }

    private static bool ShouldOverwriteRawTag(
        TagLib.File file,
        string extension,
        AutoTagRunnerConfig config,
        string configTagKey,
        string rawName)
    {
        if (config.Overwrite || config.OverwriteTags.Any(tag => string.Equals(tag?.Trim(), configTagKey, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return !HasRawTag(file, extension, rawName);
    }

    private static List<string> ResolveOtherValues(AutoTagTrack track, params string[] keys)
    {
        var values = new List<string>();
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key) || !track.Other.TryGetValue(key, out var keyValues))
            {
                continue;
            }

            values = values
                .Concat(keyValues.SelectMany(SplitCompositeRawValues))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return values;
    }

    private static List<string> ResolveFirstClassOrOtherValues(string? firstClassValue, AutoTagTrack track, params string[] keys)
    {
        var values = ResolveOtherValues(track, keys);
        if (!string.IsNullOrWhiteSpace(firstClassValue))
        {
            values.Insert(0, firstClassValue.Trim());
        }

        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> SplitCompositeRawValues(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw.Split([';', '\0'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int? ResolveFirstPositiveInt(AutoTagTrack track, params string[] keys)
    {
        return ResolveOtherValues(track, keys)
            .Select(raw => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null)
            .FirstOrDefault(parsed => parsed > 0);
    }

    private static string ResolveComposerRawName(string extension)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return "TCOM";
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            return ComposerUpperTag;
        }

        return "©wrt";
    }

    private static string ResolveLyricistRawName(string extension)
    {
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return "TEXT";
        }

        return LyricistRawTag;
    }

    private static void ApplyOverwriteRule(
        TagSettings settings,
        OverwriteRuleContext context,
        string tagKey,
        SupportedTag supportedTag,
        Action<TagSettings> disableAction)
    {
        if (!context.EnabledTags.Contains(tagKey))
        {
            return;
        }

        if (ShouldOverwriteTag(context.Config, supportedTag))
        {
            return;
        }

        if (!HasTag(context.File, context.Extension, supportedTag, context.Config, context.PlatformId))
        {
            return;
        }

        disableAction(settings);
    }

    private static void ApplyReleaseDateOverwriteRule(TagSettings settings, OverwriteRuleContext context)
    {
        if (!context.EnabledTags.Contains(ReleaseDateTag))
        {
            return;
        }

        if (ShouldOverwriteTag(context.Config, SupportedTag.ReleaseDate))
        {
            return;
        }

        if (!HasTag(context.File, context.Extension, SupportedTag.ReleaseDate, context.Config, context.PlatformId))
        {
            return;
        }

        settings.Date = false;
        settings.Year = false;
    }

    private static void ApplyTrackNumberOverwriteRule(TagSettings settings, OverwriteRuleContext context)
    {
        if (!context.EnabledTags.Contains(TrackNumberTag))
        {
            return;
        }

        if (ShouldOverwriteTag(context.Config, SupportedTag.TrackNumber))
        {
            return;
        }

        if (!HasTag(context.File, context.Extension, SupportedTag.TrackNumber, context.Config, context.PlatformId))
        {
            return;
        }

        settings.TrackNumber = false;
        settings.TrackTotal = false;
    }

    private static void ApplyTrackTotalOverwriteRule(TagSettings settings, OverwriteRuleContext context)
    {
        if (context.EnabledTags.Contains(TrackTotalTag) && !settings.TrackNumber)
        {
            settings.TrackTotal = false;
        }
    }

    private static void ApplyPreferenceAwareOverwriteGuards(
        TagSettings effectiveTagSettings,
        AutoTagTrack? sourceTrack,
        DeezSpoTagSettings? runtimeSettings,
        TagLib.File file,
        string platformId)
    {
        if (sourceTrack == null
            || runtimeSettings == null
            || (!effectiveTagSettings.Artist && !effectiveTagSettings.AlbumArtist && !effectiveTagSettings.Title))
        {
            return;
        }

        ApplyTitleLossyOverwriteGuard(effectiveTagSettings, sourceTrack, file.Tag.Title, platformId);

        var existingArtistCredits = file.Tag.Performers?
            .Where(value => !IsWeakMetadataValue(value) && !IsVariousArtistsValue(value))
            .ToList() ?? new List<string>();
        if (!IsWeakMetadataValue(file.Tag.FirstPerformer) && !IsVariousArtistsValue(file.Tag.FirstPerformer))
        {
            existingArtistCredits.Add(file.Tag.FirstPerformer!);
        }

        var existingAlbumArtistCredits = file.Tag.AlbumArtists?
            .Where(value => !IsWeakMetadataValue(value) && !IsVariousArtistsValue(value))
            .ToList() ?? new List<string>();

        ApplyPreferenceAwareArtistGuards(
            effectiveTagSettings,
            sourceTrack,
            runtimeSettings,
            existingArtistCredits,
            existingAlbumArtistCredits,
            file.Tag.Title);
        ApplyAlbumLossyOverwriteGuard(effectiveTagSettings, sourceTrack, file.Tag.Album);
        ApplyPlatformOverwriteGuards(effectiveTagSettings, sourceTrack, file, platformId);
    }

    private static void ApplyAlbumLossyOverwriteGuard(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        string? existingAlbum)
    {
        var incomingAlbum = sourceTrack.Album?.Trim();
        var currentAlbum = existingAlbum?.Trim();
        if (!effectiveTagSettings.Album
            || string.IsNullOrWhiteSpace(currentAlbum)
            || string.IsNullOrWhiteSpace(incomingAlbum))
        {
            return;
        }

        var similarity = AutoTagSimilarity.ComputeScore(
            AutoTagSimilarity.NormalizeText(currentAlbum),
            AutoTagSimilarity.NormalizeText(incomingAlbum));
        if (similarity >= 0.90d)
        {
            return;
        }

        sourceTrack.Album = currentAlbum;
        effectiveTagSettings.Album = false;
    }

    private static void ApplyPlatformOverwriteGuards(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        TagLib.File file,
        string platformId)
    {
        if (!string.Equals(platformId, BoomplayPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var existingTitle = file.Tag.Title?.Trim();
        if (effectiveTagSettings.Title
            && !string.IsNullOrWhiteSpace(existingTitle)
            && !string.IsNullOrWhiteSpace(sourceTrack.Title))
        {
            var normalizedExistingTitle = AutoTagSimilarity.NormalizeText(existingTitle);
            var normalizedIncomingTitle = AutoTagSimilarity.NormalizeText(sourceTrack.Title);
            var titleSimilarity = AutoTagSimilarity.ComputeScore(normalizedExistingTitle, normalizedIncomingTitle);
            if (!TrackTitleMatcher.HasCompatibleTitleIdentity(existingTitle, sourceTrack.Title)
                || titleSimilarity < 0.90d)
            {
                sourceTrack.Title = existingTitle;
                effectiveTagSettings.Title = false;
            }
        }

        var existingArtists = SplitArtistCredits(file.Tag.Performers?.Where(value => !IsWeakMetadataValue(value) && !IsVariousArtistsValue(value)).ToList()
            ?? new List<string>());
        var incomingArtists = SplitArtistCredits(sourceTrack.Artists);
        if (effectiveTagSettings.Artist
            && existingArtists.Count > 0
            && incomingArtists.Count > 0
            && !AreArtistCreditsEquivalent(existingArtists, incomingArtists))
        {
            sourceTrack.Artists = existingArtists;
            effectiveTagSettings.Artist = false;
        }

        var existingAlbumArtists = SplitArtistCredits(file.Tag.AlbumArtists?.Where(value => !IsWeakMetadataValue(value) && !IsVariousArtistsValue(value)).ToList()
            ?? new List<string>());
        var incomingAlbumArtists = SplitArtistCredits(sourceTrack.AlbumArtists);
        if (effectiveTagSettings.AlbumArtist
            && existingAlbumArtists.Count > 0
            && incomingAlbumArtists.Count > 0
            && !AreArtistCreditsEquivalent(existingAlbumArtists, incomingAlbumArtists))
        {
            sourceTrack.AlbumArtists = existingAlbumArtists;
            effectiveTagSettings.AlbumArtist = false;
        }

        var existingAlbum = file.Tag.Album?.Trim();
        var incomingAlbum = sourceTrack.Album?.Trim();
        if (effectiveTagSettings.Album
            && !string.IsNullOrWhiteSpace(existingAlbum)
            && !string.IsNullOrWhiteSpace(incomingAlbum))
        {
            var normalizedExistingAlbum = AutoTagSimilarity.NormalizeText(existingAlbum);
            var normalizedIncomingAlbum = AutoTagSimilarity.NormalizeText(incomingAlbum);
            var albumSimilarity = AutoTagSimilarity.ComputeScore(normalizedExistingAlbum, normalizedIncomingAlbum);
            if (albumSimilarity < 0.90d)
            {
                sourceTrack.Album = existingAlbum;
                effectiveTagSettings.Album = false;
            }
        }
    }

    private static void ApplyPreferenceAwareArtistGuards(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        DeezSpoTagSettings runtimeSettings,
        List<string> existingArtists,
        List<string> existingAlbumArtists,
        string? existingTitle)
    {
        if ((!effectiveTagSettings.Artist && !effectiveTagSettings.AlbumArtist && !effectiveTagSettings.Title)
            || existingArtists.Count == 0)
        {
            return;
        }

        var normalizedExistingArtists = SplitArtistCredits(existingArtists);
        if (normalizedExistingArtists.Count == 0)
        {
            return;
        }

        var normalizedIncomingArtists = SplitArtistCredits(sourceTrack.Artists);
        if (normalizedIncomingArtists.Count == 0)
        {
            normalizedIncomingArtists = SplitArtistCredits(sourceTrack.AlbumArtists);
        }

        if (normalizedIncomingArtists.Count == 0)
        {
            return;
        }

        var multiArtistSeparator = runtimeSettings.Tags?.MultiArtistSeparator ?? MultiArtistSeparatorDefault;
        var keepSingleArtistOnly = string.Equals(multiArtistSeparator, MultiArtistSeparatorNothing, StringComparison.OrdinalIgnoreCase);
        var artistsMatchOrPreferred = AreArtistCreditsEquivalent(normalizedExistingArtists, normalizedIncomingArtists)
            || (!keepSingleArtistOnly && ShouldPreferSourceArtistCredits(normalizedExistingArtists, normalizedIncomingArtists));
        if (!artistsMatchOrPreferred)
        {
            effectiveTagSettings.Artist = false;
            effectiveTagSettings.AlbumArtist = false;
            return;
        }

        sourceTrack.Artists = normalizedExistingArtists.ToList();
        if (effectiveTagSettings.Artist)
        {
            effectiveTagSettings.Artist = false;
        }

        ApplyAlbumArtistGuards(
            effectiveTagSettings,
            sourceTrack,
            runtimeSettings,
            normalizedExistingArtists,
            existingAlbumArtists,
            keepSingleArtistOnly);
        ApplyTitleFeaturedGuard(effectiveTagSettings, sourceTrack, runtimeSettings, normalizedExistingArtists, existingTitle);
    }

    private static void ApplyAlbumArtistGuards(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        DeezSpoTagSettings runtimeSettings,
        List<string> normalizedExistingArtists,
        List<string> existingAlbumArtists,
        bool keepSingleArtistOnly)
    {
        var singleAlbumArtist = runtimeSettings.Tags?.SingleAlbumArtist ?? true;
        var normalizedExistingAlbumArtists = SplitArtistCredits(existingAlbumArtists);
        var normalizedIncomingAlbumArtists = SplitArtistCredits(sourceTrack.AlbumArtists);
        if (singleAlbumArtist)
        {
            ApplySingleAlbumArtistGuard(
                effectiveTagSettings,
                sourceTrack,
                normalizedExistingArtists,
                normalizedExistingAlbumArtists);
            return;
        }

        var albumArtistsMatchOrPreferred = normalizedExistingAlbumArtists.Count > 0
            && (AreArtistCreditsEquivalent(normalizedExistingAlbumArtists, normalizedIncomingAlbumArtists)
                || (!keepSingleArtistOnly
                    && ShouldPreferSourceArtistCredits(normalizedExistingAlbumArtists, normalizedIncomingAlbumArtists)));
        if (!albumArtistsMatchOrPreferred)
        {
            return;
        }

        sourceTrack.AlbumArtists = normalizedExistingAlbumArtists.ToList();
        if (effectiveTagSettings.AlbumArtist)
        {
            effectiveTagSettings.AlbumArtist = false;
        }
    }

    private static void ApplySingleAlbumArtistGuard(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        List<string> normalizedExistingArtists,
        List<string> normalizedExistingAlbumArtists)
    {
        string? preferredAlbumArtist = null;
        for (var i = 0; i < normalizedExistingAlbumArtists.Count; i++)
        {
            var candidate = normalizedExistingAlbumArtists[i];
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                preferredAlbumArtist = candidate;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(preferredAlbumArtist) && normalizedExistingArtists.Count > 0)
        {
            preferredAlbumArtist = normalizedExistingArtists[0];
        }
        if (string.IsNullOrWhiteSpace(preferredAlbumArtist))
        {
            return;
        }

        sourceTrack.AlbumArtists = new List<string> { preferredAlbumArtist };
        if (effectiveTagSettings.AlbumArtist
            && normalizedExistingAlbumArtists.Count > 0
            && AreArtistPrimaryCompatible(normalizedExistingAlbumArtists[0], preferredAlbumArtist))
        {
            effectiveTagSettings.AlbumArtist = false;
        }
    }

    private static void ApplyTitleFeaturedGuard(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        DeezSpoTagSettings runtimeSettings,
        List<string> normalizedExistingArtists,
        string? existingTitle)
    {
        if (!effectiveTagSettings.Title
            || !string.Equals(runtimeSettings.FeaturedToTitle, "2", StringComparison.OrdinalIgnoreCase)
            || normalizedExistingArtists.Count <= 1
            || string.IsNullOrWhiteSpace(existingTitle)
            || !HasFeaturedMarker(existingTitle))
        {
            return;
        }

        sourceTrack.Title = existingTitle.Trim();
        effectiveTagSettings.Title = false;
    }

    private static void ApplyTitleLossyOverwriteGuard(
        TagSettings effectiveTagSettings,
        AutoTagTrack sourceTrack,
        string? existingTitle,
        string platformId)
    {
        _ = platformId;
        if (!effectiveTagSettings.Title
            || string.IsNullOrWhiteSpace(existingTitle)
            || string.IsNullOrWhiteSpace(sourceTrack.Title))
        {
            return;
        }

        var existing = existingTitle.Trim();
        var incoming = sourceTrack.Title.Trim();
        if (string.Equals(existing, incoming, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!ShouldKeepExistingTitle(existing, incoming))
        {
            return;
        }

        sourceTrack.Title = existing;
        effectiveTagSettings.Title = false;
    }

    private static bool ShouldKeepExistingTitle(string existingTitle, string incomingTitle)
    {
        if (TrackIdentityTrust.IsWeakMetadataValue(existingTitle))
        {
            return false;
        }

        if (IsNearMissAlternativeTitle(existingTitle, incomingTitle))
        {
            return true;
        }

        var existingNormalized = NormalizeLooseTitle(existingTitle);
        var incomingNormalized = NormalizeLooseTitle(incomingTitle);
        if (string.IsNullOrWhiteSpace(existingNormalized) || string.IsNullOrWhiteSpace(incomingNormalized))
        {
            return false;
        }

        if (string.Equals(existingNormalized, incomingNormalized, StringComparison.Ordinal))
        {
            return false;
        }

        var existingHasDetails = HasDetailedTitleMarkers(existingTitle);
        if (!existingHasDetails)
        {
            return false;
        }

        if (HasDetailedTitleMarkers(incomingTitle))
        {
            return false;
        }

        if (existingTitle.Length <= incomingTitle.Length + 2)
        {
            return false;
        }

        return existingNormalized.Contains(incomingNormalized, StringComparison.Ordinal);
    }

    private static bool HasDetailedTitleMarkers(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        return HasFeaturedMarker(title)
            || TitleQualifierRegex.IsMatch(title)
            || BracketedTitleDetailRegex.IsMatch(title)
            || VariantSuffixRegex.IsMatch(title.Trim());
    }

    private static bool IsNearMissAlternativeTitle(string existingTitle, string incomingTitle)
    {
        if (TrackTitleMatcher.HasCompatibleTitleIdentity(existingTitle, incomingTitle))
        {
            return false;
        }

        var existingNormalized = AutoTagSimilarity.NormalizeText(OneTaggerMatching.CleanTitleMatching(existingTitle));
        var incomingNormalized = AutoTagSimilarity.NormalizeText(OneTaggerMatching.CleanTitleMatching(incomingTitle));
        if (string.IsNullOrWhiteSpace(existingNormalized) || string.IsNullOrWhiteSpace(incomingNormalized))
        {
            return false;
        }

        // Old MusicBrainz/OneTagger fuzzy thresholds treated scores around 0.86 as the same work.
        return AutoTagSimilarity.ComputeScore(existingNormalized, incomingNormalized) >= 0.80d;
    }

    private static string NormalizeLooseTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return LooseTitleNormalizationRegex.Replace(value.ToLowerInvariant(), string.Empty);
    }

    private static bool AreArtistCreditsEquivalent(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var normalizedLeft = SplitArtistCredits(left);
        var normalizedRight = SplitArtistCredits(right);
        if (normalizedLeft.Count != normalizedRight.Count)
        {
            return false;
        }

        for (var i = 0; i < normalizedLeft.Count; i++)
        {
            if (!string.Equals(normalizedLeft[i], normalizedRight[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreArtistPrimaryCompatible(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var leftTrimmed = left.Trim();
        var rightTrimmed = right.Trim();
        if (string.Equals(leftTrimmed, rightTrimmed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return leftTrimmed.Contains(rightTrimmed, StringComparison.OrdinalIgnoreCase)
            || rightTrimmed.Contains(leftTrimmed, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasFeaturedMarker(string title)
    {
        return title.Contains("(feat", StringComparison.OrdinalIgnoreCase)
            || title.Contains(" feat.", StringComparison.OrdinalIgnoreCase)
            || title.Contains(" ft.", StringComparison.OrdinalIgnoreCase)
            || title.Contains(" featuring ", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveArtistSeparator(AutoTagRunnerConfig config, string filePath)
    {
        if (config.Separators == null)
        {
            return "";
        }

        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return config.Separators.Id3 ?? "";
        }

        if (IsMp4Family(extension))
        {
            return config.Separators.Mp4 ?? "";
        }

        return config.Separators.Vorbis ?? "";
    }

    private static List<string> ReadAppleDashBox(TagLib.Mpeg4.AppleTag tag, string name)
    {
        return AppleDashBoxReflectionHelper.ReadValues(tag, name);
    }

    private static void TrySetAppleDashBox(TagLib.Mpeg4.AppleTag? tag, string name, string[] values)
    {
        if (!AppleDashBoxReflectionHelper.TrySetValues(tag, name, values))
        {
            throw new InvalidOperationException($"Failed to set MP4 dash box {name}.");
        }
    }

    private sealed class AutoTagRunPlan
    {
        public required string JobId { get; init; }
        public required string ConfigPath { get; init; }
        public required AutoTagRunnerConfig Config { get; init; }
        public required string TargetPath { get; init; }
        public required AutoTagMatchingConfig MatchingConfig { get; init; }
        public required List<string> EffectivePlatforms { get; init; }
        public required Dictionary<string, HashSet<SupportedTag>> PlatformSupportedTags { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required TagSettings TagSettings { get; init; }
        public required List<string> Files { get; init; }
        public required Dictionary<string, ShazamRecognitionInfo?> ShazamCache { get; init; }
        public required bool EnableShazamFallback { get; init; }
        public required bool ForceShazamMatch { get; init; }
        public required bool ShazamConflictResolution { get; init; }
        public HashSet<string> PreSkippedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TaggedByAnyPlatform { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReviewedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> TaggedFileIndices { get; } = new();
        public HashSet<int> ShazamIdentifiedFiles { get; } = new();
        public Dictionary<int, AutoTagAudioInfo> OriginalManualInfo { get; } = new();
        public Dictionary<int, AutoTagAudioInfo> ResolvedManualInfo { get; } = new();
        public Dictionary<int, ManualReleaseIdentity> FrozenManualReleases { get; } = new();
        public AlbumIdentityRegistry AlbumIdentities { get; } = new();
        public HashSet<string> SeededAlbumIdentityKeys { get; } = new(StringComparer.Ordinal);
        public Dictionary<int, string> MaterializedManualPaths { get; } = new();
        public HashSet<string> AttemptedArtistArtworkPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> AttemptedAppleExtras { get; } = new();
        public int PlatformCount => EffectivePlatforms.Count;
        public int FileCount => Files.Count;
    }

    private sealed class ManualReleaseIdentity
    {
        public string Title { get; init; } = string.Empty;
        public List<string> Artists { get; init; } = new();
        public List<string> AlbumArtists { get; init; } = new();
        public string? Album { get; init; }
        public string? Isrc { get; init; }
        public string? ReleaseType { get; init; }
        public int? TrackTotal { get; init; }
        public string? Art { get; set; }

        public static ManualReleaseIdentity FromTrack(AutoTagTrack track)
            => new()
            {
                Title = track.Title,
                Artists = track.Artists.ToList(),
                AlbumArtists = track.AlbumArtists.ToList(),
                Album = track.Album,
                Isrc = track.Isrc,
                ReleaseType = track.ReleaseType,
                TrackTotal = track.TrackTotal,
                Art = track.Art
            };

        public AutoTagAudioInfo ToAudioInfo()
            => new()
            {
                Title = Title,
                Artist = Artists.FirstOrDefault() ?? string.Empty,
                Artists = Artists.ToList(),
                Album = Album,
                Isrc = Isrc
            };

        public void ApplyTo(AutoTagTrack track)
        {
            track.Title = Title;
            track.Artists = Artists.ToList();
            track.AlbumArtists = AlbumArtists.ToList();
            track.Album = Album;
            track.Isrc = Isrc;
            track.ReleaseType = ReleaseType;
            track.TrackTotal = TrackTotal;
            if (!string.IsNullOrWhiteSpace(Art))
            {
                track.Art = Art;
            }
        }
    }

    private sealed class AutoTagFileRunContext
    {
        public required AutoTagRunPlan Plan { get; init; }
        public required JobMatchCacheState JobMatchCache { get; init; }
        public required string Platform { get; init; }
        public required int PlatformIndex { get; init; }
        public required int FileIndex { get; init; }
        public required string File { get; set; }
        public required double Progress { get; init; }
        public required int NextPlatformIndex { get; init; }
        public required int NextFileIndex { get; init; }
        public required Action<TaggingStatusWrap> StatusCallback { get; init; }
        public required Action<string> LogCallback { get; init; }
        public required CancellationToken Token { get; init; }
        public string? MatchFailureOutcome { get; set; }
        public string? MatchFailureMessage { get; set; }
    }

    private sealed class JobMatchCacheState
    {
        public object SyncRoot { get; } = new();
        public DateTimeOffset LastAccessUtc { get; set; } = DateTimeOffset.UtcNow;
        public Dictionary<string, MatchCacheEntry> Entries { get; } = new(StringComparer.Ordinal);
        public HashSet<string> UnavailablePlatforms { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed record MatchCacheEntry(AutoTagMatchResult? Match);
    private sealed record ProviderTagPlan(
        HashSet<SupportedTag> Requested,
        HashSet<SupportedTag> Eligible,
        HashSet<SupportedTag> Retained);
    private sealed record LyricsPopulationRequest(
        bool WantsSynced,
        bool WantsUnsynced,
        bool WantsTtml,
        bool HasSynced,
        bool HasUnsynced,
        bool HasTtml)
    {
        public bool ShouldFetch => WantsSynced || WantsUnsynced || WantsTtml;

        public bool HasAllRequestedLyrics()
        {
            if (WantsSynced && !HasSynced)
            {
                return false;
            }

            if (WantsUnsynced && !HasUnsynced)
            {
                return false;
            }

            return !WantsTtml || HasTtml;
        }
    }

    private readonly record struct LyricsRequestFlags(
        bool WantsSynced,
        bool WantsUnsynced,
        bool WantsTtml);

    private static string SanitizeLogValue(string? value)
    {
        return LogSanitizer.OneLine(value);
    }

    private sealed class AutoTagRunnerConfig
    {
        public List<string> Platforms { get; set; } = new();
        public string? DownloadTagSource { get; set; }
        public string? Path { get; set; }
        public List<string>? TargetFiles { get; set; }
        public List<string> Tags { get; set; } = new();
        public List<string> OverwriteTags { get; set; } = new();
        public AutoTagSeparators? Separators { get; set; }
        public bool Overwrite { get; set; } = false;
        public bool MergeGenres { get; set; } = true;
        public bool Camelot { get; set; }
        public bool ShortTitle { get; set; }
        public double Strictness { get; set; } = 0.7;
        public bool MatchDuration { get; set; }
        public int MaxDurationDifference { get; set; } = 30;
        public bool MatchById { get; set; }
        public bool EnableShazam { get; set; } = true;
        public bool ForceShazam { get; set; }
        public bool EnhancementUntrustedTargets { get; set; }
        public string? ConflictResolution { get; set; }
        public bool SkipTagged { get; set; }
        public bool IncludeSubfolders { get; set; } = true;
        public bool Multiplatform { get; set; }
        public bool ParseFilename { get; set; }
        public bool Id3v24 { get; set; } = true;
        public int TrackNumberLeadingZeroes { get; set; }
        public string StylesOptions { get; set; } = "default";
        public MultipleMatchesSort MultipleMatches { get; set; } = MultipleMatchesSort.Default;
        public string? TitleRegex { get; set; }
        public JsonObject? Custom { get; set; }
        public AutoTagStylesCustomTag? StylesCustomTag { get; set; }
        public string? Id3CommLang { get; set; }
        public bool CapitalizeGenres { get; set; }
        public string? TracknameTemplate { get; set; }
        public FolderStructureSettings? FolderStructure { get; set; }
        public bool? SaveArtwork { get; set; }
        public bool? DlAlbumcoverForPlaylist { get; set; }
        public bool? SaveArtworkArtist { get; set; }
        public bool? SaveAnimatedArtwork { get; set; }
        public string? AnimatedArtworkFormats { get; set; }
        public string? CoverImageTemplate { get; set; }
        public string? AnimatedArtworkSquareFileName { get; set; }
        public string? AnimatedArtworkTallFileName { get; set; }
        public string? ArtistImageTemplate { get; set; }
        public string? LocalArtworkFormat { get; set; }
        public bool? MaterializeToTemplatePath { get; set; }
        public bool? OrganizeSidecarsIntoTemplateFolders { get; set; }
        public bool? EmbedMaxQualityCover { get; set; }
        public int? JpegImageQuality { get; set; }

        public int? AnimatedArtworkMaxSizeMb { get; set; }
        public TechnicalTagSettings? Technical { get; set; }
        public string? ProfileId { get; set; }
        public string? ProfileName { get; set; }
        public int? LibraryWideEnhancementBatchSize { get; set; }
        public string? ManualReleasePreference { get; set; }
        public long? ManualDestinationFolderId { get; set; }
    }

    private sealed record ShazamEnrichmentResult(bool UsedShazam, string? Error, bool IsFatal, ShazamFailureKind FailureKind = ShazamFailureKind.None);

    private enum ShazamFailureKind
    {
        None,
        NoMatch,
        Infrastructure
    }

    private sealed record AutoTagReviewMetadata(
        string? Reason,
        string? SourceTitle,
        string? SourceArtist,
        string? SourceIsrc,
        double? SourceDurationSeconds,
        string? CandidateTitle,
        string? CandidateArtist,
        string? CandidateIsrc,
        double? CandidateDurationSeconds)
    {
        public static AutoTagReviewMetadata FromSourceOnly(AutoTagAudioInfo source)
            => new(
                null,
                source.Title,
                source.Artist,
                source.Isrc,
                source.DurationSeconds,
                null,
                null,
                null,
                null);

        public static AutoTagReviewMetadata FromMatch(AutoTagAudioInfo source, AutoTagTrack? candidate)
            => new(
                null,
                source.Title,
                source.Artist,
                source.Isrc,
                source.DurationSeconds,
                candidate?.Title,
                candidate?.Artists.FirstOrDefault(),
                candidate?.Isrc,
                candidate?.Duration?.TotalSeconds);
    }

    private sealed class AutoTagSeparators
    {
        public string? Id3 { get; set; }
        public string? Vorbis { get; set; }
        public string? Mp4 { get; set; }
    }

    private sealed class AutoTagStylesCustomTag
    {
        public string? Id3 { get; set; }
        public string? Vorbis { get; set; }
        public string? Mp4 { get; set; }
    }
}
