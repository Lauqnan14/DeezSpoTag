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

public sealed partial class LocalAutoTagRunner : IAutoTagRunner
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
    private const string AudiomackPlatform = "audiomack";
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
    /* IsMp4Family moved to LocalAutoTagRunner.RunEntry.cs */

    /* _jobTokens moved to LocalAutoTagRunner.RunEntry.cs */

    /* _jobMatchCaches moved to LocalAutoTagRunner.RunEntry.cs */

    /* _logger moved to LocalAutoTagRunner.RunEntry.cs */

    /* _httpClientFactory moved to LocalAutoTagRunner.RunEntry.cs */

    /* _musicBrainzMatcher moved to LocalAutoTagRunner.RunEntry.cs */

    /* _beatportMatcher moved to LocalAutoTagRunner.RunEntry.cs */

    /* _discogsMatcher moved to LocalAutoTagRunner.RunEntry.cs */

    /* _traxsourceMatcher moved to LocalAutoTagRunner.RunEntry.cs */

    /* _bandcampMatcher moved to LocalAutoTagRunner.RunEntry.cs */

    /* _bpmSupremeMatcher moved to LocalAutoTagRunner.RunEntry.cs */

    /* _itunesMatcher moved to LocalAutoTagRunner.RunEntry.cs */

    /* _spotifyMatcher moved to LocalAutoTagRunner.RunEntry.cs */

    /* _deezerMatcher moved to LocalAutoTagRunner.RunEntry.cs */

    /* _lastFmMatcher moved to LocalAutoTagRunner.RunEntry.cs */

    /* _boomplayMatcher moved to LocalAutoTagRunner.RunEntry.cs */

    /* _audiomackMatcher moved to LocalAutoTagRunner.RunEntry.cs */

    /* _shazamMatcher moved to LocalAutoTagRunner.RunEntry.cs */

    /* _shazamRecognitionService moved to LocalAutoTagRunner.RunEntry.cs */

    /* _appleLyricsService moved to LocalAutoTagRunner.RunEntry.cs */

    /* _appleMusicCatalogService moved to LocalAutoTagRunner.RunEntry.cs */

    /* _downloadLyricsService moved to LocalAutoTagRunner.RunEntry.cs */

    /* _settingsService moved to LocalAutoTagRunner.RunEntry.cs */

    /* _serviceScopeFactory moved to LocalAutoTagRunner.RunEntry.cs */

    /* _trackIdentityResolver moved to LocalAutoTagRunner.RunEntry.cs */

    /* _platformRegistry moved to LocalAutoTagRunner.RunEntry.cs */

    /* _jsonOptions moved to LocalAutoTagRunner.RunEntry.cs */

    /* .ctor#1 moved to LocalAutoTagRunner.RunEntry.cs */

    /* RunAsync moved to LocalAutoTagRunner.RunEntry.cs */

    /* PrepareAutoTagRunPlanAsync moved to LocalAutoTagRunner.RunEntry.cs */

    /* LogShazamAvailability moved to LocalAutoTagRunner.RunEntry.cs */

    /* ExecutePlatformPassesAsync moved to LocalAutoTagRunner.RunEntry.cs */

    /* ExecutePlainPlatformPassAsync moved to LocalAutoTagRunner.RunEntry.cs */

    /* EnhancementPickupScheduler moved to LocalAutoTagRunner.RunEntry.cs */

    /* ExecuteLibraryWideEnhancementBatchesAsync moved to LocalAutoTagRunner.RunEntry.cs */

    /* ResolveResumeStartIndices moved to LocalAutoTagRunner.RunEntry.cs */

    /* IsLibraryWideEnhancementBatchingEnabled moved to LocalAutoTagRunner.RunEntry.cs */

    /* IsManualEnrichment moved to LocalAutoTagRunner.RunEntry.cs */

    /* WantsArtworkFromSettings moved to LocalAutoTagRunner.RunEntry.cs */

    /* BuildNormalizedPathSet moved to LocalAutoTagRunner.RunEntry.cs */

    /* NormalizeOrderPath moved to LocalAutoTagRunner.RunEntry.cs */

    /* ArtistSortMeta moved to LocalAutoTagRunner.RunEntry.cs */

    /* ReadArtistSortMeta moved to LocalAutoTagRunner.RunEntry.cs */

    /* OrderFilesForEnhancementRun moved to LocalAutoTagRunner.RunEntry.cs */

    /* GetAlbumSortKey moved to LocalAutoTagRunner.RunEntry.cs */

    /* SameAlbumDirectory moved to LocalAutoTagRunner.RunEntry.cs */

    /* BuildLibraryWideEnhancementBatchRanges moved to LocalAutoTagRunner.RunEntry.cs */

    /* BuildLibraryWideEnhancementBatchRanges moved to LocalAutoTagRunner.RunEntry.cs */

    /* GetResumeCheckpointMismatchReason moved to LocalAutoTagRunner.RunEntry.cs */

    /* BuildProviderTagPlan moved to LocalAutoTagRunner.RunEntry.cs */

    /* CapturePresentTags moved to LocalAutoTagRunner.RunEntry.cs */

    /* ResolveReturnedEligibleTags moved to LocalAutoTagRunner.RunEntry.cs */

    /* VerifyPersistedTags moved to LocalAutoTagRunner.RunEntry.cs */

    /* VerifyOtherTagsPersisted moved to LocalAutoTagRunner.RunEntry.cs */

    /* ToTagKey moved to LocalAutoTagRunner.RunEntry.cs */

    /* ProcessPlatformFileAsync moved to LocalAutoTagRunner.RunEntry.cs */

    /* ApplyCentralIdentityForManualEnrichmentAsync moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* AddResolvedIdentity moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* HasValue moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* IsLastPlatform moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* WasTaggedByAnyPlatform moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* TryHandlePreSkippedFile moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* ResolvePlatformMatchAsync moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* RunPlatformMatchWithTimeoutAsync moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* IsProviderNotConfigured moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* ApplyResolvedMatchAsync moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* EnsureManualArtistArtworkAsync moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* HandleRejectedManualRelease moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* AlbumsReferToSameRelease moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* RunBoundedOptionalStepAsync moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* ObserveBackgroundTask moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* EvaluateGlobalMismatchGuard moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* EvaluateGlobalMismatchGuard moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* IsAuthoritativeIdMatch moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* EvaluateBoomplayReliabilityGuard moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* AreArtistIdentitiesCompatibleForOverwrite moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* HasDottedInitialArtistCollapse moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* IsDottedInitialArtist moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* NormalizeArtistIdentity moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* CreateCatalogLookupInfo moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* CreateCatalogLookupInfo moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* PreserveAtmosFileIsrc moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* PreserveAtmosFileIsrc moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* HasMatchingIsrc moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* HasDurationMismatch moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* EnsureArtworkFallbackAsync moved to LocalAutoTagRunner.IdentityAndArtwork.cs */

    /* LogArtworkFallbackMatchFailure moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* ResolveArtworkFallbackPlatform moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* EmitSkippedStatus moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* EmitErrorStatus moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* EmitReviewStatus moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* EmitTaggingStatus moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* EmitTaggedStatus moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* EmitStatus moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* ResolveRecognitionStrategy moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* ApplyPostLoopFallbackAsync moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* ComputeOverallProgress moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* ComputeBatchOverallProgress moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* ComputeNextPlatformIndex moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* ComputeNextFileIndex moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* GetOrCreateMatchCache moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* PruneExpiredMatchCaches moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* TryGetCachedMatch moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* StoreCachedMatch moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* IsPlatformUnavailable moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* MarkPlatformUnavailable moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* StopAsync moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* LoadRuntimeSettings moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* ApplyTechnicalOverrides moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* ApplyRuntimeConfigOverrides moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* ApplyFolderStructureOverrides moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* NormalizeLocalArtworkFormat moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* PopulateAppleExtrasAsync moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* PopulateAppleCatalogMetadataAsync moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* ApplyAppleCatalogMetadata moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* IsLocalAtmosFile moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* TryGetJsonString moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* TryPopulateAppleAnimatedArtworkAsync moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* PopulatePlatformLyricsAsync moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* RestrictLyricsRequestToProvider moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* ResolveAppleIdentityForExtrasAsync moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* BuildAlbumArtworkBaseFileName moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* BuildLyricsLookupTrack moved to LocalAutoTagRunner.ArtworkAndLyrics.cs */

    /* NormalizeLyricsLookupSource moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* TryGetFirstOtherValue moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* AddLookupUrl moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* BuildLyricsLookupSettings moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* BuildLyricsProviderOptions moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* BuildLyricsPopulationRequest moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* ApplyResolvedLyrics moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* ApplySyncedLyrics moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* ApplyUnsyncedLyrics moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* ApplyTtmlLyrics moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* SetLyrics moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* ApplyLyricsPreferenceGate moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* LyricsSidecarsSatisfyPreference moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* ReadFileOrEmpty moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* ResolveLyricsTimingBadges moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* ResolveAnimatedArtworkBadges moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* ResolveLyricsRowCoverUrl moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* ShouldRequestAnyLyrics moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* ParseLyricsTypeSelection moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* NormalizeLyricsFormat moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* ParseLyricsFormatSelection moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* NormalizeLyricsFormatToken moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* EnumerateAudioFiles moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* ResolveTargetFiles moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* NormalizeScopePath moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* IsPathWithinScope moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* BuildEffectivePlatforms moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* ResolveLyricsProviderOrder moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* IsLyricsOnlyPlatform moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* BuildPlatformSupportedTags moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* PlatformMatchContext moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* MatchPlatformAsync moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* MatchLyricsProviderAsync moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* BuildLyricsOnlyAutoTagTrack moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* MatchShazamAsync moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* TryMatchShazamByIdsAsync moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* ResolveDeezerMatchConfig moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* BuildShazamIdFirstInfo moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* CloneAudioInfo moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* FirstNonEmpty moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* HasUsableMatchIdentity moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* PrepareShazamIdFirstMatch moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* HasTagValue moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* ExtractSpotifyTrackIdFromTags moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* IsSpotifyTrackId moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* CanUseMatchCache moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* BuildMatchCacheKey moved to LocalAutoTagRunner.LyricsLookup.cs */

    /* NormalizeCacheToken moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* ComputeCacheHash moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* HasAnyTags moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* BuildConfiguredTagSet moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* NormalizeConfiguredTagKey moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* LoadConfig moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* TryApplyShazam moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* ShouldAttemptShazam moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* IsRawCoreMetadata moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* IsLikelyNoisyCoreMetadata moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* IsTrustedSourceIdentity moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* ResolveShazamEnrichmentBehavior moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* IsShazamRecognitionAvailable moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* IsShazamConflictResolution moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* IsShazamPlatformEnabled moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* NormalizeConfig moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* NormalizeManualReleasePreference moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* RecognizeWithShazamAttempt moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* ApplyShazamRecognition moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* ResolveShazamArtists moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* ApplyShazamCoreValues moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* ApplyShazamDurationAndTrackNumber moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* ApplyShazamBaseTags moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* ApplyShazamOptionalScalarTags moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* ApplyShazamCollectionTags moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* SetShazamTag moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* SetShazamTagValues moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* BuildAudioInfo moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* BuildAudioInfoDraft moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* PopulateAudioInfoTagMap moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* NormalizeSpotifyTrackUrls moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* NormalizeSpotifyTrackUrl moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* ApplyDraftTagFallbacks moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* ApplyTracknameTemplateFallbacks moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* EnsureArtistFallbacks moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* ResolveTitleWithFallback moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* ApplyTitleRegexFilter moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* CreateAudioInfoFromDraft moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* BuildAudioInfoFallback moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* AudioInfoDraft moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* InferArtistFromPath moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* InferAlbumFromPath moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* IsSpecificFolderArtist moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* IsWeakMetadataValue moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* IsVariousArtistsValue moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* InferTitleFromFilename moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* NormalizeDurationSeconds moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* ResolveDurationSecondsFromTags moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* ResolveDurationSecondsWithFfprobe moved to LocalAutoTagRunner.MatchCacheAndDuration.cs */

    /* TryKillProcess moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* SplitArtistCredits moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* ReadFirstTagValue moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* ParsePositiveInt moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* EnsureCoreTagsFromPathAsync moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* TrySetMissingTitle moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* TrySetMissingPerformers moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* TrySetMissingAlbumArtists moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* TrySetMissingAlbum moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* TryParseFilename moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* AddTagIfAny moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* ReadRawTagValuesAny moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* HasExistingTags moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* BuildTagSettings moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* AlbumIdentitySeedExtensions moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* AlbumIdentityDateRawNames moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* AlbumIdentityAlbumIdRawNames moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* AlbumIdentityAlbumArtistIdRawNames moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* AlbumIdentityReleaseGroupIdRawNames moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* PlatformReleaseIdRawNames moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* _albumIdentityStore moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* _albumIdentityStorePath moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* LoadPersistedAlbumIdentities moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* SeedPlanAlbumIdentities moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* PersistAlbumIdentities moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* FolderAlbumIdentity moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* ApplyAlbumIdentityConsensus moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* ApplyFolderAlbumIdentity moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* BuildAlbumIdentityCandidate moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* BuildPlatformReleaseIds moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* AddPlatformReleaseId moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* PlatformReleaseIdRawName moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* ApplyEstablishedAlbumIdentity moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* SetOtherValue moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* ResolveAlbumFolderKey moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* TryResolveProspectiveAlbumDirectory moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* TryReadAlbumIdentityFromSiblings moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* EnumerateAlbumIdentitySeedDirectories moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* ReadAlbumIdentityFromDirectory moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* BuildMajorityAlbumIdentity moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* BuildMajorityPlatformReleaseIds moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* SelectMajority moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* ReadPlatformReleaseIds moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* TagFileAsync moved to LocalAutoTagRunner.AlbumIdentityAndTagging.cs */

    /* BuildCoreTrack moved to LocalAutoTagRunner.TagWrites.cs */

    /* PreserveAlbumEditionIdentity moved to LocalAutoTagRunner.TagWrites.cs */

    /* AlbumIdAlbumTagNames moved to LocalAutoTagRunner.TagWrites.cs */

    /* AlbumArtistIdAlbumTagNames moved to LocalAutoTagRunner.TagWrites.cs */

    /* ReadFirstRawTagValue moved to LocalAutoTagRunner.TagWrites.cs */

    /* PreserveSourceTitleWording moved to LocalAutoTagRunner.TagWrites.cs */

    /* PreserveRicherArtistCreditsFromSource moved to LocalAutoTagRunner.TagWrites.cs */

    /* ApplyFolderContextGuards moved to LocalAutoTagRunner.TagWrites.cs */

    /* ShouldPreferSourceArtistCredits moved to LocalAutoTagRunner.TagWrites.cs */

    /* ApplyArtistAliasPreference moved to LocalAutoTagRunner.TagWrites.cs */

    /* RewriteCreditList moved to LocalAutoTagRunner.TagWrites.cs */

    /* NormalizeTrackArtistsForTagging moved to LocalAutoTagRunner.TagWrites.cs */

    /* WriteTagsOnetaggerStyleAsync moved to LocalAutoTagRunner.TagWrites.cs */

    /* BuildAtlDashFieldName moved to LocalAutoTagRunner.TagWrites.cs */

    /* BuildTagWriteExecutionContext moved to LocalAutoTagRunner.TagWrites.cs */

    /* PrepareId3Version moved to LocalAutoTagRunner.TagWrites.cs */

    /* RemoveId3v1TagIfDisabled moved to LocalAutoTagRunner.TagWrites.cs */

    /* ApplyPrimaryTagWrites moved to LocalAutoTagRunner.TagWrites.cs */

    /* ResolveArtistValues moved to LocalAutoTagRunner.TagWrites.cs */

    /* ResolveAlbumArtistValues moved to LocalAutoTagRunner.TagWrites.cs */

    /* WriteTitleTag moved to LocalAutoTagRunner.TagWrites.cs */

    /* WriteVersionTag moved to LocalAutoTagRunner.TagWrites.cs */

    /* WriteArtistTag moved to LocalAutoTagRunner.TagWrites.cs */

    /* WriteArtistsTag moved to LocalAutoTagRunner.TagWrites.cs */

    /* WriteAlbumArtistTag moved to LocalAutoTagRunner.TagWrites.cs */

    /* WriteAlbumTag moved to LocalAutoTagRunner.TagWrites.cs */

    /* WriteKeyTag moved to LocalAutoTagRunner.TagWrites.cs */

    /* WriteBpmTag moved to LocalAutoTagRunner.TagWrites.cs */

    /* WriteLabelTag moved to LocalAutoTagRunner.TagWrites.cs */

    /* ApplyAudioFeatureTagWrites moved to LocalAutoTagRunner.TagWrites.cs */

    /* WriteAudioFeatureTag moved to LocalAutoTagRunner.TagWrites.cs */

    /* ApplyGenreAndStyleTagWrites moved to LocalAutoTagRunner.TagWrites.cs */

    /* GenreWriteAddsNothing moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* NormalizeStyleValues moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* ApplyStylesOptions moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* ApplyReleaseAndMetadataTagWrites moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteReleaseDateTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WritePublishDateTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteUrlTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteTrackIdTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteReleaseIdTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* IsPlatformReleaseIdShapeValid moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteSourceIdentityTags moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* ToMusicBrainzShapedId moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteSingleRawTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteCatalogNumberTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteDurationTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteRemixerTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteIsrcTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteMoodTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteActivityTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* ApplyTrackAndLyricsTagWrites moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteDiscNumberTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteDiscTotalTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteTrackNumberTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteBarcodeTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteReplayGainTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteCopyrightTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteComposerTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteLyricistTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteInvolvedPeopleTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WritePublisherTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteDescriptionTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteSourceTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteRatingTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteLanguageTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteSyncedLyrics moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteUnsyncedLyrics moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteExplicitTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteOtherTags moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* WriteMetaTag moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* ShouldWriteSyncedLyrics moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* ShouldWriteUnsyncedLyrics moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* ApplyAlbumArtTagWrite moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* MarkAttemptedIfPresent moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* BuildTemplatePathInfo moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* MaterializeFileToTemplatePath moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* ResolveTemplateMaterializationDestination moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* ShouldOverwriteMaterializedFile moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* MoveAdjacentSidecars moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* PathsReferToSameFile moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* PersistManualMaterializedTargetPath moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* ReplaceTargetPathInRuntimeConfig moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* ShouldWriteArtworkSidecar moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* ShouldPrepareTemplateArtworkSidecar moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* TryResolveExistingCoverSidecar moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* EnsureTemplateFoldersAndArtworkSidecarAsync moved to LocalAutoTagRunner.ArtworkSidecars.cs */

    /* ResolveLocalArtworkFormats moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* WriteLyricsSidecarsAsync moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* ShouldUpgradeTtmlSidecarToWordTiming moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* ShouldUpgradeLrcSidecarToWordTiming moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* BuildLyricsSidecarPath moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* CleanupUpgradedTxtSidecar moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* ShouldAllowLyricsOtherTagKey moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* IsLyricsPayloadKey moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* GetLyricsSidecarState moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* HasTimedTtmlSidecar moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* TrackHasEmbeddedArtwork moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* TryResolveFolderArtworkPath moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* DownloadCoverAsync moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* ApplyCustomTagsAsync moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* ResolveStylesTagName moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* SupportedTagMap moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* CreateSupportedTagMap moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* ShouldOverwriteTag moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* ResolveSeparatorForFormat moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* CollectAutoTagTags moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* AddAutoTagMetadataTags moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* AddAutoTagFeatureTags moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* AddAutoTagNumericAndDateTags moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* AddAutoTagOtherMappedTags moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* AddAutoTagLyricsAndOtherTags moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* HasOtherKey moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* HasAnyOtherKey moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* HasNonLyricsOtherTag moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* HasOtherTagValues moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* IsFirstClassOtherRawKey moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* IsRuntimeMatchMetadataKey moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* IsNonPersistedOtherRawKey moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* ShouldPersistOtherRawKey moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* HasReleaseTypeTagEnabled moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* EnsureReleaseCategory moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* ApplySeparator moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* FormatAudioFeature moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* HasTag moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* HasId3Tag moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* HasVorbisTag moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* HasMp4Tag moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* HasTimestampedLyricsPayload moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* ContainsTimestampedLyrics moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* ReadExistingGenre moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* BuildCustomTagWrites moved to LocalAutoTagRunner.ArtworkSidecarsAndLyrics.cs */

    /* AddSingleValueCustomTagWrite moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* AddOtherTagWrites moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* AddMetaTagWrite moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* ResolveFieldRawName moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* TagWriteRequest moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* TagWriteExecutionContext moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* TagFileWriteResult moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* LocalAutoTagRunnerCollaborators moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* TagWriteContext moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* TagFieldBinding moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* DateWritePayload moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* LyricsSidecarWriteResult moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* OverwriteRuleContext moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* CustomTagWrite moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* ResolveFormatName moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* SetField moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* SetRaw moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* HasRawTag moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* WriteDate moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* WriteId3Date moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* ShouldSkipId3ReleaseDate moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* WriteVorbisDate moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* WriteMp4Date moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* IsYearOnlyDateFormat moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* SetTrackNumber moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* WriteId3TrackNumber moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* SetDiscTotal moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* WriteVorbisTrackNumber moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* WriteMp4TrackNumber moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* WriteLyrics moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* TryResolveLyricsLines moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* WriteId3Lyrics moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* WriteId3SyncedLyrics moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* BuildSyncedLyricsItems moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* WriteId3UnsyncedLyrics moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* WriteVorbisLyrics moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* WriteGenericLyrics moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* TryParseLrcLine moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* ResolveLrcSidecarLines moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* HasLrcSidecarSourceFormat moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* ResolveExistingLrcSidecar moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* ResolveLyricsPayloadLines moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* ResolveTtmlSidecarPayload moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* NormalizeLyricsLines moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* ComposeTtmlPayload moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* ApplyAlbumArt moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* Mp4TagHelper moved to LocalAutoTagRunner.TagWritesAdvanced.cs */

    /* CapitalizeGenre moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* IsGenreRawTag moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* SanitizeGenres moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* PreserveGenreOrderWhenSetEqual moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ToCamelot moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* CamelotNotes moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* SetId3Raw moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* SetVorbisRaw moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ReadExistingRawTag moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ReadRawTagValues moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ReadRawTagValuesCore moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ReadMp4AtlRawValues moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* AddMp4AtlNativeRawValues moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* AddMp4AtlDateValue moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* AddMp4AtlPositiveNumberValue moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* AddMp4AtlLyricsValues moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ResolveAtlAdditionalValue moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* AddIfPresent moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ApplyId3CustomTags moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ApplyVorbisCustomTags moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ApplyAppleCustomTags moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ApplyOverwriteRules moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* CloneTagSettings moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* SetRawIfAllowed moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* WriteRawTagValues moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* RemoveRawTagValues moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ShouldOverwriteRawTag moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ResolveOtherValues moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ResolveFirstClassOrOtherValues moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* SplitCompositeRawValues moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ResolveFirstPositiveInt moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ResolveComposerRawName moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ResolveLyricistRawName moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ApplyOverwriteRule moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ApplyReleaseDateOverwriteRule moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ApplyTrackNumberOverwriteRule moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ApplyTrackTotalOverwriteRule moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ApplyPreferenceAwareOverwriteGuards moved to LocalAutoTagRunner.OverwriteGuards.cs */

    /* ArtistAliasOverwriteDecision moved to LocalAutoTagRunner.RunMetadata.cs */

    /* ApplyPreferredArtistAliasToExistingCredits moved to LocalAutoTagRunner.RunMetadata.cs */

    /* PreserveRicherCreditsWithoutBlockingPreferredWrite moved to LocalAutoTagRunner.RunMetadata.cs */

    /* ApplyAlbumLossyOverwriteGuard moved to LocalAutoTagRunner.RunMetadata.cs */

    /* ApplyPlatformOverwriteGuards moved to LocalAutoTagRunner.RunMetadata.cs */

    /* ApplyPreferenceAwareArtistGuards moved to LocalAutoTagRunner.RunMetadata.cs */

    /* ApplyAlbumArtistGuards moved to LocalAutoTagRunner.RunMetadata.cs */

    /* ApplySingleAlbumArtistGuard moved to LocalAutoTagRunner.RunMetadata.cs */

    /* ApplyTitleFeaturedGuard moved to LocalAutoTagRunner.RunMetadata.cs */

    /* ApplyTitleLossyOverwriteGuard moved to LocalAutoTagRunner.RunMetadata.cs */

    /* ShouldKeepExistingTitle moved to LocalAutoTagRunner.RunMetadata.cs */

    /* HasDetailedTitleMarkers moved to LocalAutoTagRunner.RunMetadata.cs */

    /* IsNearMissAlternativeTitle moved to LocalAutoTagRunner.RunMetadata.cs */

    /* NormalizeLooseTitle moved to LocalAutoTagRunner.RunMetadata.cs */

    /* AreArtistCreditsEquivalent moved to LocalAutoTagRunner.RunMetadata.cs */

    /* AreArtistPrimaryCompatible moved to LocalAutoTagRunner.RunMetadata.cs */

    /* HasFeaturedMarker moved to LocalAutoTagRunner.RunMetadata.cs */

    /* ResolveArtistSeparator moved to LocalAutoTagRunner.RunMetadata.cs */

    /* ReadAppleDashBox moved to LocalAutoTagRunner.RunMetadata.cs */

    /* TrySetAppleDashBox moved to LocalAutoTagRunner.RunMetadata.cs */

    /* AutoTagRunPlan moved to LocalAutoTagRunner.RunMetadata.cs */

    /* ManualReleaseIdentity moved to LocalAutoTagRunner.RunMetadata.cs */

    /* AutoTagFileRunContext moved to LocalAutoTagRunner.RunMetadata.cs */

    /* JobMatchCacheState moved to LocalAutoTagRunner.RunMetadata.cs */

    /* MatchCacheEntry moved to LocalAutoTagRunner.RunMetadata.cs */

    /* ProviderTagPlan moved to LocalAutoTagRunner.RunMetadata.cs */

    /* LyricsPopulationRequest moved to LocalAutoTagRunner.RunMetadata.cs */

    /* LyricsRequestFlags moved to LocalAutoTagRunner.RunMetadata.cs */

    /* SanitizeLogValue moved to LocalAutoTagRunner.RunMetadata.cs */

    /* AutoTagRunnerConfig moved to LocalAutoTagRunner.RunMetadata.cs */

    /* ShazamEnrichmentResult moved to LocalAutoTagRunner.RunMetadata.cs */

    /* ShazamFailureKind moved to LocalAutoTagRunner.RunMetadata.cs */

    /* AutoTagReviewMetadata moved to LocalAutoTagRunner.RunMetadata.cs */

    /* AutoTagSeparators moved to LocalAutoTagRunner.RunMetadata.cs */

    /* AutoTagStylesCustomTag moved to LocalAutoTagRunner.RunMetadata.cs */

}
