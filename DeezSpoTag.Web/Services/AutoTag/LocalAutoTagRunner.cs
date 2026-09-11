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
{    private static readonly TimeSpan ArtworkFallbackTimeout = TimeSpan.FromSeconds(20);
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
    private readonly AudiomackMatcher _audiomackMatcher;
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
        _albumIdentityStorePath = collaborators.AlbumIdentityStorePath;
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
        _audiomackMatcher = collaborators.AudiomackMatcher;
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

    private static readonly string[] AlbumIdentitySeedExtensions =
        [".flac", ".mp3", ".m4a", ".mp4", ".aac", ".alac", ".ogg", ".opus", ".wav"];

    private static readonly string[] AlbumIdentityDateRawNames = ["DATE", "TDRC", "TDRL", "TYER"];
    private static readonly string[] AlbumIdentityAlbumIdRawNames =
        [AlbumIdRawTag, "MUSICBRAINZ_ALBUMID", "MUSICBRAINZ_ALBUM_ID", "MUSICBRAINZ_RELEASE_ID"];
    private static readonly string[] AlbumIdentityAlbumArtistIdRawNames =
        [AlbumArtistIdRawTag, "MUSICBRAINZ_ALBUMARTISTID", "MUSICBRAINZ_ALBUM_ARTIST_ID"];
    private static readonly string[] AlbumIdentityReleaseGroupIdRawNames =
        [ReleaseGroupIdRawTag, "MUSICBRAINZ_RELEASEGROUPID", "MUSICBRAINZ_RELEASE_GROUP_ID"];
    private static readonly string[] PlatformReleaseIdRawNames =
        ["DEEZER_RELEASE_ID", "SPOTIFY_RELEASE_ID", "ITUNES_RELEASE_ID", "APPLE_RELEASE_ID", "APPLE_ALBUM_ID"];

    private AlbumIdentityStore? _albumIdentityStore;
    private readonly string? _albumIdentityStorePath;

    private static readonly string[] AlbumIdAlbumTagNames =
        ["MUSICBRAINZ_ALBUMID", "MUSICBRAINZ_ALBUM_ID", "ALBUMID", "MB_ALBUM_ID"];
    private static readonly string[] AlbumArtistIdAlbumTagNames =
        ["MUSICBRAINZ_ALBUMARTISTID", "MUSICBRAINZ_ALBUM_ARTIST_ID", "ALBUMARTISTID", "MB_ALBUM_ARTIST_ID"];

    private static readonly Dictionary<string, SupportedTag> SupportedTagMap = CreateSupportedTagMap();

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
}
