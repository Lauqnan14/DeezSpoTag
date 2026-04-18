using System.Collections.Concurrent;
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
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using TagLib;
using IOFile = System.IO.File;
using DownloadLyricsService = DeezSpoTag.Services.Download.Utils.LyricsService;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class LocalAutoTagRunner : IAutoTagRunner
{
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
    private static readonly Regex NoisyCoreTagRegex = new(
        @"\b(?:official|audio|video|lyrics?|visualizer|final|finished|master|unknown)\b|(?:\.mp3|\.wav|\.m4a|\.aac)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);
    private static readonly TimeSpan MatchCacheTtl = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly char[] LyricsLineSeparators = ['\r', '\n'];
    private const int MaxCacheEntriesPerJob = 6000;
    private const string FlacExtension = ".flac";
    private const string TtmlExtension = ".ttml";
    private const string ShazamPlatform = "shazam";
    private const string UnknownArtist = "Unknown Artist";
    private const string MultiArtistSeparatorDefault = "default";
    private const string MultiArtistSeparatorNothing = "nothing";
    private const string AlbumArtTag = "albumArt";
    private const string SyncedLyricsTag = "syncedLyrics";
    private const string UnsyncedLyricsTag = "unsyncedLyrics";
    private const string TtmlLyricsTag = "ttmlLyrics";
    private const string ItunesPlatform = "itunes";
    private const string SpotifyPlatform = "spotify";
    private const string LyricsTag = "lyrics";
    private const string SyllableLyricsType = "syllable-lyrics";
    private const string UnsyncedLyricsType = "unsynced-lyrics";
    private const string AlbumArtistTag = "albumArtist";
    private const string TrackTotalTag = "trackTotal";
    private const string AlbumTag = "album";
    private const string CatalogNumberTag = "catalogNumber";
    private const string ReleaseIdTag = "releaseId";
    private const string TrackNumberTag = "trackNumber";
    private const string LabelTag = "label";
    private const string Mp4GenreTag = "GENRE";
    private const string DeezerPlatform = "deezer";
    private const string DeezerTrackIdTag = "DEEZER_TRACK_ID";
    private const string SpotifyTrackIdTag = "SPOTIFY_TRACK_ID";
    private const string WwwAudioFileTag = "WWWAUDIOFILE";
    private const string TaggedDateTag = "1T_TAGGEDDATE";
    private const string TitleTag = "title";
    private const string ArtistTag = "artist";
    private const string DiscNumberTag = "discNumber";
    private const string GenreTag = "genre";
    private const string ExplicitTag = "explicit";
    private const string ItunesAdvisoryTag = "ITUNESADVISORY";
    private const string TrackTotalRawTag = "TRACKTOTAL";
    private const string DurationTag = "duration";
    private const string ReleaseDateTag = "releaseDate";
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
    private const string StyleTag = "style";
    private const string PublishDateTag = "publishDate";
    private const string TrackIdTag = "trackId";
    private const string CatalogNumberUpperTag = "CATALOGNUMBER";
    private const string LengthUpperTag = "LENGTH";
    private const string RemixerTag = "remixer";
    private const string RemixerUpperTag = "REMIXER";
    private const string OtherTagsTag = "otherTags";
    private const string MetaTagsTag = "metaTags";
    private const string StyleUpperTag = "STYLE";
    private const string VorbisFormat = "vorbis";
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
    private readonly JunoDownloadMatcher _junoDownloadMatcher;
    private readonly BandcampMatcher _bandcampMatcher;
    private readonly BeatsourceMatcher _beatsourceMatcher;
    private readonly BpmSupremeMatcher _bpmSupremeMatcher;
    private readonly ItunesMatcher _itunesMatcher;
    private readonly SpotifyMatcher _spotifyMatcher;
    private readonly DeezerMatcher _deezerMatcher;
    private readonly LastFmMatcher _lastFmMatcher;
    private readonly BoomplayMatcher _boomplayMatcher;
    private readonly MusixmatchMatcher _musixmatchMatcher;
    private readonly LrclibMatcher _lrclibMatcher;
    private readonly ShazamMatcher _shazamMatcher;
    private readonly ShazamRecognitionService _shazamRecognitionService;
    private readonly AppleLyricsService _appleLyricsService;
    private readonly AppleMusicCatalogService _appleMusicCatalogService;
    private readonly DownloadLyricsService _downloadLyricsService;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly PlatformCapabilitiesStore _capabilitiesStore;
    private readonly bool _shazamRecognitionAvailable;
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
        _junoDownloadMatcher = collaborators.JunoDownloadMatcher;
        _bandcampMatcher = collaborators.BandcampMatcher;
        _beatsourceMatcher = collaborators.BeatsourceMatcher;
        _bpmSupremeMatcher = collaborators.BpmSupremeMatcher;
        _itunesMatcher = collaborators.ItunesMatcher;
        _spotifyMatcher = collaborators.SpotifyMatcher;
        _deezerMatcher = collaborators.DeezerMatcher;
        _lastFmMatcher = collaborators.LastFmMatcher;
        _boomplayMatcher = collaborators.BoomplayMatcher;
        _musixmatchMatcher = collaborators.MusixmatchMatcher;
        _lrclibMatcher = collaborators.LrclibMatcher;
        _shazamMatcher = collaborators.ShazamMatcher;
        _shazamRecognitionService = collaborators.ShazamRecognitionService;
        _appleLyricsService = collaborators.AppleLyricsService;
        _appleMusicCatalogService = collaborators.AppleMusicCatalogService;
        _downloadLyricsService = collaborators.DownloadLyricsService;
        _settingsService = collaborators.SettingsService;
        _capabilitiesStore = collaborators.CapabilitiesStore;
        _shazamRecognitionAvailable = _shazamRecognitionService.IsAvailable;
    }

    public async Task<AutoTagRunResult> RunAsync(
        string jobId,
        string rootPath,
        string configPath,
        Action<TaggingStatusWrap> statusCallback,
        Action<string> logCallback,
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
            var (runPlan, failure) = await PrepareAutoTagRunPlanAsync(rootPath, configPath, token);
            if (failure != null)
            {
                return failure;
            }

            var plan = runPlan!;
            LogShazamAvailability(plan, logCallback);
            await ExecutePlatformPassesAsync(plan, jobMatchCache, statusCallback, logCallback, resumeCursor, token);
            await ApplyPostLoopFallbackAsync(plan, token);

            return new AutoTagRunResult(true, null);
        }
        catch (OperationCanceledException)
        {
            return new AutoTagRunResult(false, "stopped");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Local AutoTag run failed.");
            return new AutoTagRunResult(false, ex.ToString());
        }
        finally
        {
            jobMatchCache.LastAccessUtc = DateTimeOffset.UtcNow;
            _jobTokens.TryRemove(jobId, out _);
        }
    }

    private async Task<(AutoTagRunPlan? Plan, AutoTagRunResult? Failure)> PrepareAutoTagRunPlanAsync(
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
            MultipleMatches = config.MultipleMatches
        };
        var settings = LoadRuntimeSettings(config.Technical, config);
        var shazamBehavior = ResolveShazamEnrichmentBehavior(config);
        var plan = new AutoTagRunPlan
        {
            Config = config,
            TargetPath = targetPath,
            MatchingConfig = matchingConfig,
            EffectivePlatforms = BuildEffectivePlatforms(config),
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

        return (plan, null);
    }

    private void LogShazamAvailability(AutoTagRunPlan plan, Action<string> logCallback)
    {
        if ((plan.EnableShazamFallback
             || plan.ForceShazamMatch
             || plan.ShazamConflictResolution
             || plan.EffectivePlatforms.Contains(ShazamPlatform, StringComparer.OrdinalIgnoreCase))
            && !_shazamRecognitionAvailable)
        {
            logCallback("onetagger_autotag: shazam unavailable");
        }
    }

    private async Task ExecutePlatformPassesAsync(
        AutoTagRunPlan plan,
        JobMatchCacheState jobMatchCache,
        Action<TaggingStatusWrap> statusCallback,
        Action<string> logCallback,
        AutoTagResumeCursor? resumeCursor,
        CancellationToken token)
    {
        var (startPlatformIndex, startFileIndex) = ResolveResumeStartIndices(plan, resumeCursor);
        for (var platformIndex = startPlatformIndex; platformIndex < plan.PlatformCount; platformIndex++)
        {
            token.ThrowIfCancellationRequested();
            var platform = plan.EffectivePlatforms[platformIndex];
            logCallback($"onetagger_autotag: starting {platform}");

            var fileStart = platformIndex == startPlatformIndex ? startFileIndex : 0;
            for (var fileIndex = fileStart; fileIndex < plan.FileCount; fileIndex++)
            {
                token.ThrowIfCancellationRequested();
                var context = new AutoTagFileRunContext
                {
                    Plan = plan,
                    JobMatchCache = jobMatchCache,
                    Platform = platform,
                    PlatformIndex = platformIndex,
                    FileIndex = fileIndex,
                    File = plan.Files[fileIndex],
                    Progress = ComputeOverallProgress(platformIndex, fileIndex, plan.PlatformCount, plan.FileCount),
                    StatusCallback = statusCallback,
                    LogCallback = logCallback,
                    Token = token
                };
                await ProcessPlatformFileAsync(context);
            }
        }
    }

    private static (int PlatformIndex, int FileIndex) ResolveResumeStartIndices(
        AutoTagRunPlan plan,
        AutoTagResumeCursor? resumeCursor)
    {
        if (plan.PlatformCount == 0 || plan.FileCount == 0 || resumeCursor == null)
        {
            return (0, 0);
        }

        var platformIndex = Math.Clamp(resumeCursor.PlatformIndex, 0, plan.PlatformCount - 1);
        var fileIndex = Math.Clamp(resumeCursor.FileIndex, 0, plan.FileCount);
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

    private async Task ProcessPlatformFileAsync(AutoTagFileRunContext context)
    {
        if (TryHandlePreSkippedFile(context))
        {
            return;
        }

        var info = BuildAudioInfo(
            context.File,
            context.Plan.TargetPath,
            context.Plan.Config.ParseFilename,
            context.Plan.Config.FilenameTemplate,
            context.Plan.Config.TitleRegex);
        var shazamResult = TryApplyShazam(
            context.File,
            info,
            context.Plan.EnableShazamFallback,
            context.Plan.ForceShazamMatch,
            context.Plan.ShazamCache,
            context.LogCallback,
            context.Token);
        var usedShazamForStatus = shazamResult.UsedShazam
            || string.Equals(context.Platform, ShazamPlatform, StringComparison.OrdinalIgnoreCase);

        if (shazamResult.IsFatal)
        {
            EmitSkippedStatus(
                context,
                shazamResult.Error ?? "shazam identify failed",
                shazamResult.UsedShazam);
            return;
        }

        var match = await ResolvePlatformMatchAsync(context, info, usedShazamForStatus);
        if (match == null)
        {
            EmitSkippedStatus(context, "no match", usedShazamForStatus);
            return;
        }

        await ApplyResolvedMatchAsync(context, info, match, usedShazamForStatus);
    }

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
        AutoTagAudioInfo info,
        bool usedShazamForStatus)
    {
        var useMatchCache = CanUseMatchCache(info);
        var matchCacheKey = useMatchCache
            ? BuildMatchCacheKey(context.Platform, info, context.Plan.Config, context.Plan.MatchingConfig)
            : string.Empty;
        if (useMatchCache && TryGetCachedMatch(context.JobMatchCache, matchCacheKey, out var cachedMatch))
        {
            return cachedMatch;
        }

        AutoTagMatchResult? match;
        try
        {
            match = await MatchPlatformAsync(
                context.Platform,
                context.File,
                info,
                context.Plan.Config,
                context.Plan.MatchingConfig,
                context.Plan.ShazamCache,
                context.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag platform {Platform} failed for {File}", context.Platform, context.File);
            EmitErrorStatus(context, ex.Message, usedShazamForStatus);
            return null;
        }

        if (useMatchCache)
        {
            StoreCachedMatch(context.JobMatchCache, matchCacheKey, match);
        }

        return match;
    }

    private async Task ApplyResolvedMatchAsync(
        AutoTagFileRunContext context,
        AutoTagAudioInfo info,
        AutoTagMatchResult match,
        bool usedShazamForStatus)
    {
        EmitTaggingStatus(context, match.Accuracy, usedShazamForStatus);

        try
        {
            PreserveRicherArtistCreditsFromSource(info, match.Track, context.Plan.Settings);
            await EnsureArtworkFallbackAsync(context, info, match.Track);
            await PopulatePlatformLyricsAsync(
                context.Platform,
                context.File,
                match.Track,
                context.Plan.Config,
                context.Plan.Settings,
                context.Token);
            await PopulateAppleExtrasAsync(
                context.File,
                match.Track,
                context.Plan.Config,
                context.Plan.Settings,
                context.Token);
            await TagFileAsync(
                context.File,
                match.Track,
                context.Plan.TagSettings,
                context.Plan.Config,
                context.Plan.Settings,
                context.Platform,
                context.Token);
            context.Plan.TaggedByAnyPlatform.Add(context.File);
            EmitTaggedStatus(context, match.Accuracy, usedShazamForStatus);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AutoTag failed for {File} on {Platform}", context.File, context.Platform);
            EmitErrorStatus(context, ex.Message, usedShazamForStatus);
        }
    }

    private async Task EnsureArtworkFallbackAsync(
        AutoTagFileRunContext context,
        AutoTagAudioInfo info,
        AutoTagTrack track)
    {
        if (!HasAnyTags(context.Plan.Config, AlbumArtTag)
            || !string.IsNullOrWhiteSpace(track.Art))
        {
            return;
        }

        var providerOrder = ArtworkFallbackHelper.ResolveOrder(context.Plan.Settings);
        if (providerOrder.Count == 0)
        {
            return;
        }

        foreach (var provider in providerOrder)
        {
            var platform = ResolveArtworkFallbackPlatform(provider);
            if (string.IsNullOrWhiteSpace(platform)
                || string.Equals(platform, context.Platform, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AutoTagMatchResult? fallbackMatch;
            try
            {
                fallbackMatch = await MatchPlatformAsync(
                    platform,
                    context.File,
                    info,
                    context.Plan.Config,
                    context.Plan.MatchingConfig,
                    context.Plan.ShazamCache,
                    context.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogArtworkFallbackMatchFailure(ex, context.File, platform);
                continue;
            }

            var fallbackArt = fallbackMatch?.Track?.Art;
            if (string.IsNullOrWhiteSpace(fallbackArt))
            {
                continue;
            }

            track.Art = fallbackArt;
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Artwork fallback resolved for {File} via {Platform}.", context.File, platform);
            }

            return;
        }
    }

    private void LogArtworkFallbackMatchFailure(Exception ex, string filePath, string platform)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(ex, "Artwork fallback match failed for {File} using {Platform}.", filePath, platform);
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

    private static void EmitSkippedStatus(AutoTagFileRunContext context, string message, bool usedShazam = false)
    {
        EmitStatus(context, "skipped", message, null, usedShazam);
    }

    private static void EmitErrorStatus(AutoTagFileRunContext context, string message, bool usedShazam)
    {
        EmitStatus(context, "error", message, null, usedShazam);
    }

    private static void EmitTaggingStatus(AutoTagFileRunContext context, double? accuracy, bool usedShazam)
    {
        EmitStatus(context, "tagging", null, accuracy, usedShazam);
    }

    private static void EmitTaggedStatus(AutoTagFileRunContext context, double? accuracy, bool usedShazam)
    {
        EmitStatus(context, "tagged", null, accuracy, usedShazam);
    }

    private static void EmitStatus(
        AutoTagFileRunContext context,
        string status,
        string? message,
        double? accuracy,
        bool usedShazam)
    {
        var nextPlatformIndex = context.PlatformIndex;
        var nextFileIndex = context.FileIndex + 1;
        if (nextFileIndex >= context.Plan.FileCount)
        {
            nextFileIndex = 0;
            nextPlatformIndex += 1;
        }

        context.StatusCallback(new TaggingStatusWrap
        {
            Platform = context.Platform,
            Progress = context.Progress,
            PlatformIndex = context.PlatformIndex,
            PlatformCount = context.Plan.PlatformCount,
            FileIndex = context.FileIndex,
            FileCount = context.Plan.FileCount,
            NextPlatformIndex = nextPlatformIndex,
            NextFileIndex = nextFileIndex,
            Status = new TaggingStatus
            {
                Status = status,
                Path = context.File,
                Message = message,
                Accuracy = accuracy,
                UsedShazam = usedShazam
            }
        });
    }

    private static async Task ApplyPostLoopFallbackAsync(AutoTagRunPlan plan, CancellationToken token)
    {
        if (plan.ShazamConflictResolution)
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
        if (!string.IsNullOrWhiteSpace(config.TracknameTemplate))
        {
            settings.TracknameTemplate = config.TracknameTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.AlbumTracknameTemplate))
        {
            settings.AlbumTracknameTemplate = config.AlbumTracknameTemplate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.PlaylistTracknameTemplate))
        {
            settings.PlaylistTracknameTemplate = config.PlaylistTracknameTemplate.Trim();
        }

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

        if (!string.IsNullOrWhiteSpace(config.CoverImageTemplate))
        {
            settings.CoverImageTemplate = config.CoverImageTemplate.Trim();
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

        if (config.JpegImageQuality.HasValue)
        {
            settings.JpegImageQuality = Math.Clamp(config.JpegImageQuality.Value, 1, 100);
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
        string filePath,
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        CancellationToken token)
    {
        var itunesConfig = LoadConfig(config.Custom, ItunesPlatform, new ItunesMatchConfig());
        var saveAnimatedArtwork = itunesConfig.AnimatedArtwork ?? settings.SaveAnimatedArtwork;
        var wantsAnimatedArtwork = saveAnimatedArtwork && HasAnyTags(config, AlbumArtTag);
        var wantsAppleLyrics = ShouldRequestAnyLyrics(config, settings);
        if (!wantsAnimatedArtwork && !wantsAppleLyrics)
        {
            return;
        }

        if (wantsAppleLyrics)
        {
            await PopulateAppleLyricsAsync(filePath, track, config, settings, token);
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

        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront)
            ? "us"
            : settings.AppleMusic.Storefront;
        var maxResolution = settings.Video?.AppleMusicVideoMaxResolution ?? 2160;
        var baseFileName = BuildAlbumArtworkBaseFileName(track, settings);
        var appleCatalogTrackId = await ResolveAppleCatalogTrackIdForExtrasAsync(track, storefront, token);

        await TryPopulateAppleAnimatedArtworkAsync(
            track,
            outputDir,
            storefront,
            maxResolution,
            baseFileName,
            appleCatalogTrackId,
            token);
    }

    private async Task TryPopulateAppleAnimatedArtworkAsync(
        AutoTagTrack track,
        string outputDir,
        string storefront,
        int maxResolution,
        string baseFileName,
        string? appleCatalogTrackId,
        CancellationToken token)
    {
        var artist = track.Artists.FirstOrDefault();

        try
        {
            var savedAnimated = await AppleQueueHelpers.SaveAnimatedArtworkAsync(
                _appleMusicCatalogService,
                _httpClientFactory,
                new AppleQueueHelpers.AnimatedArtworkSaveRequest
                {
                    AppleId = appleCatalogTrackId,
                    Title = track.Title,
                    Artist = artist,
                    Album = track.Album,
                    BaseFileName = baseFileName,
                    Storefront = storefront,
                    MaxResolution = maxResolution,
                    OutputDir = outputDir,
                    Logger = _logger
                },
                token);

            if (!savedAnimated)
            {
                savedAnimated = await AppleQueueHelpers.SaveAnimatedArtworkAsync(
                    _appleMusicCatalogService,
                    _httpClientFactory,
                    new AppleQueueHelpers.AnimatedArtworkSaveRequest
                    {
                        Title = track.Title,
                        Artist = artist,
                        Album = track.Album,
                        BaseFileName = baseFileName,
                        Storefront = storefront,
                        MaxResolution = maxResolution,
                        OutputDir = outputDir,
                        Logger = _logger
                    },
                    token);
            }

            if (savedAnimated)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("AutoTag Apple animated artwork saved for {Title} in {OutputDir}", track.Title, outputDir);
                }
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("AutoTag Apple animated artwork unavailable for {Title}", track.Title);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple animated artwork resolution failed for {Title}.", track.Title);
            }
        }
    }

    private async Task PopulatePlatformLyricsAsync(
        string platform,
        string filePath,
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        CancellationToken token)
    {
        var request = BuildLyricsPopulationRequest(filePath, track, config, settings);
        if (!request.ShouldFetch)
        {
            return;
        }

        var platformId = platform.Trim().ToLowerInvariant();
        if (!string.Equals(platformId, SpotifyPlatform, StringComparison.Ordinal))
        {
            return;
        }

        if (request.HasAllRequestedLyrics())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(track.TrackId) &&
            string.IsNullOrWhiteSpace(track.Url) &&
            string.IsNullOrWhiteSpace(track.Isrc))
        {
            return;
        }

        var lookupTrack = BuildSpotifyLyricsLookupTrack(track);
        var lookupSettings = BuildSpotifyLyricsSettings(settings, request.WantsSynced, request.WantsUnsynced, request.WantsTtml);
        LyricsBase? lyrics = null;
        try
        {
            lyrics = await _downloadLyricsService.ResolveLyricsAsync(lookupTrack, lookupSettings, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Spotify lyrics resolution failed for {Title}.", track.Title);
            }
            return;
        }

        if (lyrics == null || !lyrics.IsLoaded())
        {
            return;
        }

        ApplyResolvedLyrics(track, lyrics, request);
    }

    private async Task<string?> ResolveAppleCatalogTrackIdForExtrasAsync(
        AutoTagTrack track,
        string storefront,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(track.Isrc))
        {
            return null;
        }

        try
        {
            using var doc = await _appleMusicCatalogService.GetSongByIsrcAsync(track.Isrc, storefront, "en-US", token);
            return TryExtractFirstAppleCatalogId(doc.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple catalog ISRC lookup failed for animated artwork {Isrc}.", track.Isrc);
            }
            return null;
        }
    }

    private static string? TryExtractFirstAppleCatalogId(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array ||
            data.GetArrayLength() == 0)
        {
            return null;
        }

        var first = data[0];
        if (!first.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var id = idEl.GetString();
        return string.IsNullOrWhiteSpace(id) ? null : id;
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

    private static Track BuildSpotifyLyricsLookupTrack(AutoTagTrack track)
    {
        var lookupTrack = new Track
        {
            Id = track.TrackId ?? string.Empty,
            Source = SpotifyPlatform,
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
            lookupTrack.Urls[SpotifyPlatform] = track.Url;
        }

        return lookupTrack;
    }

    private static DeezSpoTagSettings BuildSpotifyLyricsSettings(
        DeezSpoTagSettings baseSettings,
        bool wantsSynced,
        bool wantsUnsynced,
        bool wantsTtml)
    {
        var shouldFetchUnsyncedPayload = wantsUnsynced || wantsTtml;
        return new DeezSpoTagSettings
        {
            Arl = baseSettings.Arl,
            DeezerCountry = baseSettings.DeezerCountry,
            AppleMusic = baseSettings.AppleMusic,
            Video = baseSettings.Video,
            SyncedLyrics = baseSettings.SyncedLyrics && wantsSynced,
            SaveLyrics = baseSettings.SaveLyrics && shouldFetchUnsyncedPayload,
            LyricsFallbackEnabled = baseSettings.LyricsFallbackEnabled,
            LyricsFallbackOrder = string.IsNullOrWhiteSpace(baseSettings.LyricsFallbackOrder)
                ? "apple,deezer,spotify,lrclib,musixmatch"
                : baseSettings.LyricsFallbackOrder,
            LrcFormat = NormalizeLyricsFormat(baseSettings.LrcFormat),
            LrcType = string.IsNullOrWhiteSpace(baseSettings.LrcType)
                ? "lyrics,syllable-lyrics,unsynced-lyrics"
                : baseSettings.LrcType,
            Tags = new TagSettings
            {
                Lyrics = baseSettings.SaveLyrics && shouldFetchUnsyncedPayload,
                SyncedLyrics = baseSettings.SyncedLyrics && wantsSynced
            }
        };
    }

    private async Task PopulateAppleLyricsAsync(
        string filePath,
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        CancellationToken token)
    {
        var request = BuildLyricsPopulationRequest(filePath, track, config, settings);
        if (!request.ShouldFetch)
        {
            return;
        }

        if (request.HasAllRequestedLyrics())
        {
            return;
        }

        LyricsBase? lyrics;
        try
        {
            lyrics = await ResolveAppleLyricsAsync(track, settings, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple lyrics resolution failed for {Title}.", track.Title);
            }
            return;
        }

        if (lyrics == null || !lyrics.IsLoaded())
        {
            return;
        }

        ApplyResolvedLyrics(track, lyrics, request);
    }

    private static LyricsPopulationRequest BuildLyricsPopulationRequest(
        string filePath,
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings)
    {
        var wantsSynced = HasAnyTags(config, SyncedLyricsTag);
        var wantsUnsynced = HasAnyTags(config, UnsyncedLyricsTag);
        var wantsTtml = HasAnyTags(config, TtmlLyricsTag);
        ApplyLyricsPreferenceGate(settings, ref wantsSynced, ref wantsUnsynced, ref wantsTtml);

        var sidecarState = GetLyricsSidecarState(filePath);
        if (sidecarState.HasLrc)
        {
            wantsSynced = false;
            wantsUnsynced = false;
        }

        if (sidecarState.HasTtml)
        {
            wantsTtml = false;
        }

        return new LyricsPopulationRequest(
            wantsSynced,
            wantsUnsynced,
            wantsTtml,
            track.Other.TryGetValue(SyncedLyricsTag, out var existingSynced) && existingSynced.Count > 0,
            (track.Other.TryGetValue(UnsyncedLyricsTag, out var existingUnsynced) && existingUnsynced.Count > 0)
                || (track.Other.TryGetValue(LyricsTag, out var existingLyrics) && existingLyrics.Count > 0),
            track.Other.TryGetValue(TtmlLyricsTag, out var existingTtml)
                && existingTtml.Any(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static void ApplyResolvedLyrics(AutoTagTrack track, LyricsBase lyrics, LyricsPopulationRequest request)
    {
        ApplySyncedLyrics(track, lyrics, request);
        ApplyUnsyncedLyrics(track, lyrics, request);
        ApplyTtmlLyrics(track, lyrics, request);
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

    private static void ApplyTtmlLyrics(AutoTagTrack track, LyricsBase lyrics, LyricsPopulationRequest request)
    {
        if (!request.WantsTtml || request.HasTtml || string.IsNullOrWhiteSpace(lyrics.TtmlLyrics))
        {
            return;
        }

        track.Other[TtmlLyricsTag] = new List<string> { lyrics.TtmlLyrics };
    }

    private static void SetLyrics(AutoTagTrack track, string tag, List<string> lines)
    {
        track.Other[tag] = lines;
        if (!track.Other.TryGetValue(LyricsTag, out var existingLyricsLines) || existingLyricsLines.Count == 0)
        {
            track.Other[LyricsTag] = lines;
        }
    }

    private async Task<LyricsBase?> ResolveAppleLyricsAsync(
        AutoTagTrack track,
        DeezSpoTagSettings settings,
        CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(track.TrackId))
        {
            var byId = await _appleLyricsService.ResolveLyricsAsync(track.TrackId, settings, token);
            if (byId.IsLoaded())
            {
                return byId;
            }
        }

        var lookupTrack = BuildAppleLyricsLookupTrack(track);
        var byLookup = await _appleLyricsService.ResolveLyricsForTrackAsync(lookupTrack, settings, token);
        return byLookup.IsLoaded() ? byLookup : null;
    }

    private static Track BuildAppleLyricsLookupTrack(AutoTagTrack track)
    {
        var lookupTrack = new Track
        {
            Title = track.Title ?? string.Empty,
            Album = new Album(track.Album ?? string.Empty),
            ISRC = track.Isrc ?? string.Empty
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

        return lookupTrack;
    }

    private static void ApplyLyricsPreferenceGate(
        DeezSpoTagSettings settings,
        ref bool wantsSynced,
        ref bool wantsUnsynced,
        ref bool wantsTtml)
    {
        var allowsSyncedByToggle = settings.SyncedLyrics || settings.Tags?.SyncedLyrics == true;
        var allowsUnsyncedByToggle = settings.SaveLyrics || settings.Tags?.Lyrics == true;
        if (!allowsSyncedByToggle && !allowsUnsyncedByToggle)
        {
            wantsSynced = false;
            wantsUnsynced = false;
            wantsTtml = false;
            return;
        }

        var selectedTypes = ParseLyricsTypeSelection(settings.LrcType);
        var allowsSyncedTypes = selectedTypes.Contains(LyricsTag) || selectedTypes.Contains(SyllableLyricsType);
        var allowsUnsyncedTypes = selectedTypes.Contains(UnsyncedLyricsType);
        var normalizedFormat = NormalizeLyricsFormat(settings.LrcFormat);

        wantsSynced &= allowsSyncedByToggle && allowsSyncedTypes;
        wantsUnsynced &= allowsUnsyncedByToggle && allowsUnsyncedTypes;
        wantsTtml &= allowsSyncedByToggle && allowsSyncedTypes && (normalizedFormat == "both" || normalizedFormat == "ttml");
    }

    private static bool ShouldRequestAnyLyrics(AutoTagRunnerConfig config, DeezSpoTagSettings settings)
    {
        var wantsSynced = HasAnyTags(config, SyncedLyricsTag);
        var wantsUnsynced = HasAnyTags(config, UnsyncedLyricsTag);
        var wantsTtml = HasAnyTags(config, TtmlLyricsTag);
        ApplyLyricsPreferenceGate(settings, ref wantsSynced, ref wantsUnsynced, ref wantsTtml);
        return wantsSynced || wantsUnsynced || wantsTtml;
    }

    private static HashSet<string> ParseLyricsTypeSelection(string? raw)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            selected.Add(LyricsTag);
            selected.Add(SyllableLyricsType);
            selected.Add(UnsyncedLyricsType);
            return selected;
        }

        foreach (var value in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = value.Trim().ToLowerInvariant();
            if (normalized == "synced-lyrics")
            {
                normalized = LyricsTag;
            }
            else if (normalized == "time-synced-lyrics" || normalized == "timesynced-lyrics" || normalized == "time_synced_lyrics")
            {
                normalized = SyllableLyricsType;
            }
            else if (normalized == "unsyncedlyrics" || normalized == "unsynced")
            {
                normalized = UnsyncedLyricsType;
            }

            selected.Add(normalized);
        }

        if (selected.Count == 0)
        {
            selected.Add(LyricsTag);
            selected.Add(SyllableLyricsType);
            selected.Add(UnsyncedLyricsType);
        }

        return selected;
    }

    private static string NormalizeLyricsFormat(string? raw)
    {
        var normalized = raw?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "lrc" => "lrc",
            "ttml" => "ttml",
            _ => "both"
        };
    }

    private static IEnumerable<string> EnumerateAudioFiles(string rootPath, bool includeSubfolders)
    {
        var option = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(rootPath, "*.*", option)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)) && !IsAnimatedArtworkFile(path));
    }

    private static IEnumerable<string> ResolveTargetFiles(string rootPath, AutoTagRunnerConfig config)
    {
        if (config.TargetFiles == null)
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
                || IsAnimatedArtworkFile(normalizedPath))
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

    private static bool IsAnimatedArtworkFile(string path)
    {
        if (!Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var filename = Path.GetFileNameWithoutExtension(path);
        return filename.Equals("square_animated_artwork", StringComparison.OrdinalIgnoreCase)
            || filename.Equals("tall_animated_artwork", StringComparison.OrdinalIgnoreCase)
            || filename.EndsWith(" - square_animated_artwork", StringComparison.OrdinalIgnoreCase)
            || filename.EndsWith(" - tall_animated_artwork", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> BuildEffectivePlatforms(AutoTagRunnerConfig config)
    {
        return config.Platforms
            .Select(platform => platform?.Trim())
            .Where(platform => !string.IsNullOrWhiteSpace(platform))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(platform => platform!)
            .ToList();
    }

    private async Task<AutoTagMatchResult?> MatchPlatformAsync(
        string platform,
        string filePath,
        AutoTagAudioInfo info,
        AutoTagRunnerConfig config,
        AutoTagMatchingConfig matchingConfig,
        IDictionary<string, ShazamRecognitionInfo?> shazamCache,
        CancellationToken token)
    {
        var enableLyrics = HasAnyTags(config, UnsyncedLyricsTag, SyncedLyricsTag, TtmlLyricsTag);
        var hasLyricsSidecar = enableLyrics && GetLyricsSidecarState(filePath).HasAny;
        var beatportReleaseMeta = HasAnyTags(config, AlbumArtistTag, TrackTotalTag);
        var traxsourceExtend = HasAnyTags(config, AlbumArtTag, AlbumTag, CatalogNumberTag, ReleaseIdTag, AlbumArtistTag, TrackNumberTag, TrackTotalTag);
        var traxsourceAlbumMeta = HasAnyTags(config, CatalogNumberTag, TrackNumberTag, AlbumArtTag, TrackTotalTag, AlbumArtistTag);
        var discogsNeedsLabelCatalog = HasAnyTags(config, LabelTag, CatalogNumberTag);

        switch (platform.Trim().ToLowerInvariant())
        {
            case "musicbrainz":
                return await _musicBrainzMatcher.MatchAsync(info, matchingConfig, LoadConfig(config.Custom, "musicbrainz", new MusicBrainzMatchConfig()), token);
            case "beatport":
                return await _beatportMatcher.MatchAsync(info, matchingConfig, LoadConfig(config.Custom, "beatport", new BeatportMatchConfig()), beatportReleaseMeta, config.MatchById, token);
            case "discogs":
                return await _discogsMatcher.MatchAsync(info, matchingConfig, LoadConfig(config.Custom, "discogs", new DiscogsConfig()), config.MatchById, discogsNeedsLabelCatalog, token);
            case "traxsource":
                return await _traxsourceMatcher.MatchAsync(info, matchingConfig, traxsourceExtend, traxsourceAlbumMeta, token);
            case "junodownload":
                return await _junoDownloadMatcher.MatchAsync(info, matchingConfig, token);
            case "bandcamp":
                return await _bandcampMatcher.MatchAsync(info, matchingConfig, token);
            case "beatsource":
                return await _beatsourceMatcher.MatchAsync(info, matchingConfig, LoadConfig(config.Custom, "beatsource", new BeatsourceMatchConfig()), token);
            case "bpmsupreme":
                return await _bpmSupremeMatcher.MatchAsync(info, matchingConfig, LoadConfig(config.Custom, "bpmsupreme", new BpmSupremeConfig()), token);
            case ItunesPlatform:
                return await _itunesMatcher.MatchAsync(info, matchingConfig, LoadConfig(config.Custom, ItunesPlatform, new ItunesMatchConfig()), token);
            case SpotifyPlatform:
                return await _spotifyMatcher.MatchAsync(info, matchingConfig, token);
            case DeezerPlatform:
                return await _deezerMatcher.MatchAsync(info, matchingConfig, LoadConfig(config.Custom, DeezerPlatform, new DeezerConfig()), token);
            case "boomplay":
                return await _boomplayMatcher.MatchAsync(info, matchingConfig, LoadConfig(config.Custom, "boomplay", new BoomplayConfig()), token);
            case "lastfm":
                return await _lastFmMatcher.MatchAsync(info, LoadConfig(config.Custom, "lastfm", new LastFmConfig()), token);
            case ShazamPlatform:
                return await MatchShazamAsync(filePath, info, config, matchingConfig, shazamCache, token);
            case "musixmatch":
                return enableLyrics && !hasLyricsSidecar ? await _musixmatchMatcher.MatchAsync(info, token) : null;
            case "lrclib":
                return enableLyrics && !hasLyricsSidecar ? await _lrclibMatcher.MatchAsync(info, matchingConfig, LoadConfig(config.Custom, "lrclib", new LrclibConfig()), token) : null;
            default:
                return null;
        }
    }

    private async Task<AutoTagMatchResult?> MatchShazamAsync(
        string filePath,
        AutoTagAudioInfo info,
        AutoTagRunnerConfig config,
        AutoTagMatchingConfig matchingConfig,
        IDictionary<string, ShazamRecognitionInfo?> shazamCache,
        CancellationToken token)
    {
        var shazamConfig = LoadConfig(config.Custom, ShazamPlatform, new ShazamMatchConfig());
        if (shazamConfig.IdFirst)
        {
            var idFirstMatch = await TryMatchShazamByIdsAsync(info, config, matchingConfig, token);
            if (idFirstMatch != null)
            {
                return idFirstMatch;
            }
        }

        if (!shazamConfig.FingerprintFallback)
        {
            return null;
        }

        return await _shazamMatcher.MatchAsync(filePath, info, shazamConfig, shazamCache, token);
    }

    private async Task<AutoTagMatchResult?> TryMatchShazamByIdsAsync(
        AutoTagAudioInfo info,
        AutoTagRunnerConfig config,
        AutoTagMatchingConfig matchingConfig,
        CancellationToken token)
    {
        var effectiveInfo = BuildShazamIdFirstInfo(info);

        var hasDeezerId = HasTagValue(effectiveInfo, DeezerTrackIdTag, "DEEZERID", "DEEZER_ID");
        var hasSpotifyId = HasTagValue(effectiveInfo, SpotifyTrackIdTag, "SPOTIFY_TRACKID", "SPOTIFYID", "SPOTIFY_ID");
        var hasIsrc = !string.IsNullOrWhiteSpace(effectiveInfo.Isrc);

        if (hasDeezerId)
        {
            var deezerConfig = LoadConfig(config.Custom, DeezerPlatform, new DeezerConfig());
            deezerConfig.MatchById = true;
            var byDeezerId = await _deezerMatcher.MatchAsync(effectiveInfo, matchingConfig, deezerConfig, token);
            if (byDeezerId != null)
            {
                return PrepareShazamIdFirstMatch(byDeezerId, DeezerPlatform, info);
            }
        }

        if (hasSpotifyId)
        {
            var bySpotifyId = await _spotifyMatcher.MatchAsync(effectiveInfo, matchingConfig, token);
            if (bySpotifyId != null)
            {
                return PrepareShazamIdFirstMatch(bySpotifyId, SpotifyPlatform, info);
            }
        }

        if (hasIsrc)
        {
            var deezerConfig = LoadConfig(config.Custom, DeezerPlatform, new DeezerConfig());
            deezerConfig.MatchById = true;
            var byDeezerIsrc = await _deezerMatcher.MatchAsync(effectiveInfo, matchingConfig, deezerConfig, token);
            if (byDeezerIsrc != null)
            {
                return PrepareShazamIdFirstMatch(byDeezerIsrc, DeezerPlatform, info);
            }

            var bySpotifyIsrc = await _spotifyMatcher.MatchAsync(effectiveInfo, matchingConfig, token);
            if (bySpotifyIsrc != null)
            {
                return PrepareShazamIdFirstMatch(bySpotifyIsrc, SpotifyPlatform, info);
            }
        }

        return null;
    }

    private static AutoTagAudioInfo BuildShazamIdFirstInfo(AutoTagAudioInfo source)
    {
        var cloned = new AutoTagAudioInfo
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

        var spotifyId = ExtractSpotifyTrackIdFromTags(cloned.Tags);
        if (!string.IsNullOrWhiteSpace(spotifyId))
        {
            cloned.Tags[SpotifyTrackIdTag] = new List<string> { spotifyId };
        }

        return cloned;
    }

    private static AutoTagMatchResult PrepareShazamIdFirstMatch(
        AutoTagMatchResult match,
        string provider,
        AutoTagAudioInfo sourceInfo)
    {
        if (match.Track.Other == null)
        {
            match.Track.Other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        match.Track.Other["SHAZAM_MATCH_STRATEGY"] = new List<string> { "ID_FIRST" };
        match.Track.Other["SHAZAM_MATCH_PROVIDER"] = new List<string> { provider.ToUpperInvariant() };

        if (provider.Equals(DeezerPlatform, StringComparison.OrdinalIgnoreCase))
        {
            AddOtherIfMissing(match.Track, DeezerTrackIdTag, match.Track.TrackId);
            AddOtherIfMissing(match.Track, "DEEZER_RELEASE_ID", match.Track.ReleaseId);
        }
        else if (provider.Equals(SpotifyPlatform, StringComparison.OrdinalIgnoreCase))
        {
            AddOtherIfMissing(match.Track, SpotifyTrackIdTag, match.Track.TrackId);
            AddOtherIfMissing(match.Track, "SPOTIFY_RELEASE_ID", match.Track.ReleaseId);
        }

        var shazamTrackId = ReadFirstTagValue(sourceInfo.Tags, "SHAZAM_TRACK_ID", "SHAZAM_TRACK_KEY");
        if (string.IsNullOrWhiteSpace(shazamTrackId))
        {
            match.Track.TrackId = null;
            match.Track.ReleaseId = null;
        }
        else
        {
            match.Track.TrackId = shazamTrackId;
            match.Track.ReleaseId = shazamTrackId;
        }

        return match;
    }

    private static void AddOtherIfMissing(AutoTagTrack track, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (track.Other.ContainsKey(key))
        {
            return;
        }

        track.Other[key] = new List<string> { value.Trim() };
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
            "SPOTIFY_TRACKID",
            "SPOTIFYID",
            "SPOTIFY_ID",
            "SPOTIFY_URL",
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
        builder.Append("matchById=").Append(config.MatchById).Append(';');
        builder.Append("enableLyrics=").Append(normalizedTags.Any(tag => tag is "unsyncedlyrics" or "syncedlyrics" or "ttmllyrics")).Append(';');
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

        var configured = new HashSet<string>(config.Tags, StringComparer.OrdinalIgnoreCase);
        return tags.Any(configured.Contains);
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
        bool enableShazamFallback,
        bool forceShazamMatch,
        Dictionary<string, ShazamRecognitionInfo?> cache,
        Action<string> logCallback,
        CancellationToken token)
    {
        if (!ShouldAttemptShazam(info, enableShazamFallback, forceShazamMatch))
        {
            return new ShazamEnrichmentResult(false, null, false);
        }

        if (!_shazamRecognitionAvailable)
        {
            return forceShazamMatch
                ? new ShazamEnrichmentResult(false, "shazam unavailable", true)
                : new ShazamEnrichmentResult(false, "shazam unavailable", false);
        }

        token.ThrowIfCancellationRequested();

        var fromCache = cache.TryGetValue(filePath, out var recognized);
        if (!fromCache)
        {
            recognized = RecognizeWithShazam(filePath, token);
            cache[filePath] = recognized;
        }

        if (recognized == null)
        {
            if (forceShazamMatch)
            {
                return new ShazamEnrichmentResult(false, "shazam could not identify track", true);
            }

            return new ShazamEnrichmentResult(false, null, false);
        }

        if (!fromCache)
        {
            logCallback($"onetagger_autotag: shazam identified {Path.GetFileName(filePath)}");
        }

        var preferShazamCore = forceShazamMatch || IsLikelyNoisyCoreMetadata(info);
        ApplyShazamRecognition(info, recognized, preferShazamCore);
        return new ShazamEnrichmentResult(true, null, false);
    }

    private static bool ShouldAttemptShazam(AutoTagAudioInfo info, bool enableShazamFallback, bool forceShazamMatch)
    {
        if (forceShazamMatch)
        {
            return true;
        }

        // Always attempt Shazam for raw files with no embedded core metadata.
        if (IsRawCoreMetadata(info))
        {
            return true;
        }

        if (!enableShazamFallback)
        {
            return false;
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
    {
        return IsLikelyNoisyCoreValue(info.Title) || IsLikelyNoisyCoreValue(info.Artist);
    }

    private static bool IsLikelyNoisyCoreValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim();
        if (normalized.Length < 2)
        {
            return true;
        }

        return NoisyCoreTagRegex.IsMatch(normalized);
    }

    private static (bool EnableFallback, bool ForceMatch) ResolveShazamEnrichmentBehavior(AutoTagRunnerConfig config)
    {
        var shazamConfig = LoadConfig(config.Custom, ShazamPlatform, new ShazamMatchConfig());

        var hasShazamConfig = config.Custom != null
            && config.Custom.TryGetPropertyValue(ShazamPlatform, out var shazamNode)
            && shazamNode is JsonObject;

        if (hasShazamConfig)
        {
            return (shazamConfig.FallbackMissingCoreTags, shazamConfig.ForceMatch);
        }

        // Legacy fallback for older profiles/configs.
        return (config.EnableShazam, config.ForceShazam);
    }

    private static bool IsShazamConflictResolution(AutoTagRunnerConfig config)
    {
        return string.Equals(config.ConflictResolution, ShazamPlatform, StringComparison.OrdinalIgnoreCase);
    }

    private static AutoTagRunnerConfig NormalizeConfig(AutoTagRunnerConfig? raw)
    {
        raw ??= new AutoTagRunnerConfig();
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
            AlbumArtFile = raw.AlbumArtFile,
            Camelot = raw.Camelot,
            ShortTitle = raw.ShortTitle,
            Strictness = raw.Strictness,
            MatchDuration = raw.MatchDuration,
            MaxDurationDifference = raw.MaxDurationDifference,
            MatchById = raw.MatchById,
            EnableShazam = raw.EnableShazam,
            ForceShazam = raw.ForceShazam,
            ConflictResolution = raw.ConflictResolution,
            SkipTagged = raw.SkipTagged,
            IncludeSubfolders = raw.IncludeSubfolders,
            Multiplatform = raw.Multiplatform,
            ParseFilename = raw.ParseFilename,
            FilenameTemplate = raw.FilenameTemplate,
            OnlyYear = raw.OnlyYear,
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
            WriteLrc = raw.WriteLrc,
            CapitalizeGenres = raw.CapitalizeGenres,
            TracknameTemplate = raw.TracknameTemplate,
            AlbumTracknameTemplate = raw.AlbumTracknameTemplate,
            PlaylistTracknameTemplate = raw.PlaylistTracknameTemplate,
            SaveArtwork = raw.SaveArtwork,
            DlAlbumcoverForPlaylist = raw.DlAlbumcoverForPlaylist,
            SaveArtworkArtist = raw.SaveArtworkArtist,
            CoverImageTemplate = raw.CoverImageTemplate,
            ArtistImageTemplate = raw.ArtistImageTemplate,
            LocalArtworkFormat = raw.LocalArtworkFormat,
            EmbedMaxQualityCover = raw.EmbedMaxQualityCover,
            JpegImageQuality = raw.JpegImageQuality,
            Technical = raw.Technical,
            ProfileId = raw.ProfileId,
            ProfileName = raw.ProfileName
        };
    }

    private ShazamRecognitionInfo? RecognizeWithShazam(string filePath, CancellationToken token)
    {
        try
        {
            return _shazamRecognitionService.Recognize(filePath, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Shazam recognize failed for {File}", filePath);
            }
            return null;
        }
    }

    private static void ApplyShazamRecognition(AutoTagAudioInfo info, ShazamRecognitionInfo payload, bool forceShazam)
    {
        var shazamArtists = ResolveShazamArtists(payload);
        ApplyShazamCoreValues(info, payload, shazamArtists, forceShazam);
        ApplyShazamDurationAndTrackNumber(info, payload);
        ApplyShazamBaseTags(info, payload);
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
        bool forceShazam)
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

        if ((forceShazam || string.IsNullOrWhiteSpace(info.Album))
            && !string.IsNullOrWhiteSpace(payload.Album))
        {
            info.Album = payload.Album.Trim();
        }

        if (string.IsNullOrWhiteSpace(info.Isrc) && !string.IsNullOrWhiteSpace(payload.Isrc))
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

    private static void ApplyShazamBaseTags(AutoTagAudioInfo info, ShazamRecognitionInfo payload)
    {
        SetShazamTag(info, "SHAZAM_TRACK_ID", payload.TrackId);
        SetShazamTag(info, "SHAZAM_TRACK_KEY", payload.TrackId);
        SetShazamTag(info, "SHAZAM_URL", payload.Url);
        SetShazamTag(info, "SHAZAM_TITLE", payload.Title);
        SetShazamTag(info, "SHAZAM_ARTIST", payload.Artist);
        SetShazamTag(info, "SHAZAM_GENRE", payload.Genre);
        SetShazamTag(info, "SHAZAM_ALBUM", payload.Album);
        SetShazamTag(info, "SHAZAM_LABEL", payload.Label);
        SetShazamTag(info, "SHAZAM_RELEASE_DATE", payload.ReleaseDate);
        SetShazamTag(info, "SHAZAM_ARTWORK", payload.ArtworkUrl);
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

    private AutoTagAudioInfo BuildAudioInfo(string filePath, string rootPath, bool parseFilename, string? filenameTemplate, string? titleRegex)
    {
        var extension = Path.GetExtension(filePath);
        try
        {
            using var file = TagLib.File.Create(filePath);
            var draft = BuildAudioInfoDraft(file, filePath);

            PopulateAudioInfoTagMap(file, extension, draft.Tags);
            ApplyDraftTagFallbacks(draft);
            ApplyFilenameTemplateFallbacks(draft, filePath, parseFilename, filenameTemplate);
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
            _logger.LogWarning(ex, "Failed reading tags for {File}", filePath);
            return BuildAudioInfoFallback(filePath, rootPath, parseFilename, filenameTemplate, titleRegex);
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
        var title = file.Tag.Title ?? string.Empty;
        return new AudioInfoDraft
        {
            Title = title,
            Artist = artists.FirstOrDefault() ?? file.Tag.FirstPerformer ?? string.Empty,
            Artists = artists,
            Album = string.IsNullOrWhiteSpace(file.Tag.Album)
                ? InferAlbumFromPath(filePath)
                : file.Tag.Album,
            Isrc = file.Tag.ISRC,
            TrackNumber = file.Tag.Track > 0 ? (int?)file.Tag.Track : null,
            HasEmbeddedTitle = !string.IsNullOrWhiteSpace(title),
            HasEmbeddedArtist = artists.Count > 0 || !string.IsNullOrWhiteSpace(file.Tag.FirstPerformer),
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static void PopulateAudioInfoTagMap(TagLib.File file, string extension, Dictionary<string, List<string>> tags)
    {
        AddTagIfAny(tags, "BEATPORT_TRACK_ID", ReadRawTagValues(file, extension, "BEATPORT_TRACK_ID"));
        AddTagIfAny(tags, "DISCOGS_RELEASE_ID", ReadRawTagValues(file, extension, "DISCOGS_RELEASE_ID"));
        AddTagIfAny(tags, "ITUNES_TRACK_ID", ReadRawTagValuesAny(file, extension, "ITUNES_TRACK_ID", "ITUNESCATALOGID", "ITUNES_TRACKID"));
        AddTagIfAny(tags, "ITUNES_RELEASE_ID", ReadRawTagValuesAny(file, extension, "ITUNES_RELEASE_ID", "ITUNESALBUMID", "ITUNES_ALBUM_ID"));
        AddTagIfAny(tags, "ITUNES_ARTIST_ID", ReadRawTagValuesAny(file, extension, "ITUNES_ARTIST_ID", "ITUNESARTISTID"));
        AddTagIfAny(tags, DeezerTrackIdTag, ReadRawTagValuesAny(file, extension, DeezerTrackIdTag, "DEEZERID", "DEEZER_ID"));
        AddTagIfAny(tags, "DEEZER_RELEASE_ID", ReadRawTagValuesAny(file, extension, "DEEZER_RELEASE_ID"));
        AddTagIfAny(tags, SpotifyTrackIdTag, ReadRawTagValuesAny(file, extension, SpotifyTrackIdTag, "SPOTIFY_TRACKID", "SPOTIFYID", "SPOTIFY_ID"));
        AddTagIfAny(tags, "SPOTIFY_URL", ReadRawTagValuesAny(file, extension, "SPOTIFY_URL", "SPOTIFYURI", "SPOTIFY_URI", "URL", WwwAudioFileTag));
        AddTagIfAny(tags, "MUSICBRAINZ_RECORDING_ID", ReadRawTagValuesAny(file, extension, "MUSICBRAINZ_RECORDING_ID", "MUSICBRAINZ_RECORDINGID", "MUSICBRAINZ_TRACK_ID", "MUSICBRAINZ_TRACKID"));

        foreach (var shazamTag in ShazamRawTagHints)
        {
            AddTagIfAny(tags, shazamTag, ReadRawTagValuesAny(file, extension, shazamTag));
        }
    }

    private static void ApplyDraftTagFallbacks(AudioInfoDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Isrc))
        {
            draft.Isrc = ReadFirstTagValue(draft.Tags, "SHAZAM_ISRC", "ISRC");
        }

        if (string.IsNullOrWhiteSpace(draft.Album))
        {
            draft.Album = ReadFirstTagValue(draft.Tags, "SHAZAM_ALBUM", "ALBUM");
        }

        if (!draft.TrackNumber.HasValue)
        {
            draft.TrackNumber = ParsePositiveInt(ReadFirstTagValue(draft.Tags, "SHAZAM_TRACK_NUMBER", "TRACKNUMBER"));
        }
    }

    private static void ApplyFilenameTemplateFallbacks(
        AudioInfoDraft draft,
        string filePath,
        bool parseFilename,
        string? filenameTemplate)
    {
        if (!parseFilename || (!string.IsNullOrWhiteSpace(draft.Title) && !string.IsNullOrWhiteSpace(draft.Artist)))
        {
            return;
        }

        var template = OneTaggerMatching.ParseFilenameTemplate(filenameTemplate);
        if (!TryParseFilename(Path.GetFileName(filePath), template, out var parsedArtist, out var parsedTitle))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.Artist))
        {
            draft.Artist = parsedArtist;
        }

        if (string.IsNullOrWhiteSpace(draft.Title))
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
        if (draft.Artists.Count == 0 && !string.IsNullOrWhiteSpace(draft.Artist))
        {
            draft.Artists = SplitArtistCredits(new[] { draft.Artist });
            draft.Artist = draft.Artists.FirstOrDefault() ?? draft.Artist;
        }

        if (!string.IsNullOrWhiteSpace(draft.Artist))
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
        var resolved = string.IsNullOrWhiteSpace(title)
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
        return new AutoTagAudioInfo
        {
            Title = draft.Title,
            Artist = draft.Artist,
            Artists = draft.Artists.Count == 0 && !string.IsNullOrWhiteSpace(draft.Artist)
                ? new List<string> { draft.Artist }
                : draft.Artists,
            Album = string.IsNullOrWhiteSpace(draft.Album) ? null : draft.Album,
            DurationSeconds = NormalizeDurationSeconds(filePath, extension, durationSeconds),
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
        string? filenameTemplate,
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

        ApplyFilenameTemplateFallbacks(draft, filePath, parseFilename, filenameTemplate);
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
            Lyrics = false,
            SyncedLyrics = false,
            SavePlaylistAsCompilation = runtimeSettings.Tags?.SavePlaylistAsCompilation ?? false,
            UseNullSeparator = runtimeSettings.Tags?.UseNullSeparator ?? false,
            SaveID3v1 = runtimeSettings.Tags?.SaveID3v1 ?? true,
            MultiArtistSeparator = runtimeSettings.Tags?.MultiArtistSeparator ?? MultiArtistSeparatorDefault,
            SingleAlbumArtist = runtimeSettings.Tags?.SingleAlbumArtist ?? true,
            CoverDescriptionUTF8 = runtimeSettings.Tags?.CoverDescriptionUTF8 ?? true
        };

        foreach (var tag in config.Tags)
        {
            switch (tag.Trim())
            {
                case TitleTag:
                    settings.Title = true;
                    break;
                case ArtistTag:
                    settings.Artist = true;
                    break;
                case "artists":
                    settings.Artists = true;
                    break;
                case AlbumTag:
                    settings.Album = true;
                    break;
                case AlbumArtistTag:
                    settings.AlbumArtist = true;
                    break;
                case TrackNumberTag:
                    settings.TrackNumber = true;
                    break;
                case TrackTotalTag:
                    settings.TrackTotal = true;
                    break;
                case DiscNumberTag:
                    settings.DiscNumber = true;
                    break;
                case GenreTag:
                    settings.Genre = true;
                    break;
                case LabelTag:
                    settings.Label = true;
                    break;
                case "bpm":
                    settings.Bpm = true;
                    break;
                case "isrc":
                    settings.Isrc = true;
                    break;
                case ExplicitTag:
                    settings.Explicit = true;
                    break;
                case DurationTag:
                    settings.Length = true;
                    break;
                case ReleaseDateTag:
                    settings.Date = true;
                    settings.Year = true;
                    break;
                case "year":
                    settings.Year = true;
                    break;
                case AlbumArtTag:
                    settings.Cover = true;
                    break;
                case UnsyncedLyricsTag:
                    settings.Lyrics = true;
                    break;
                case SyncedLyricsTag:
                    settings.SyncedLyrics = true;
                    break;
            }
        }

        return settings;
    }

    private async Task TagFileAsync(
        string filePath,
        AutoTagTrack track,
        TagSettings tagSettings,
        AutoTagRunnerConfig config,
        DeezSpoTagSettings settings,
        string platformId,
        CancellationToken token)
    {
        UpdateAutoTagCapabilities(platformId, track);
        var separator = ResolveSeparatorForFormat(config, Path.GetExtension(filePath));
        var effectiveTagSettings = ApplyOverwriteRules(filePath, tagSettings, config, platformId, track, settings);
        NormalizeTrackArtistsForTagging(track, effectiveTagSettings.SingleAlbumArtist);
        var coreTrack = BuildCoreTrack(track, separator, effectiveTagSettings.SingleAlbumArtist, settings);
        string? tempCoverPath = null;

        if (effectiveTagSettings.Cover && !string.IsNullOrWhiteSpace(track.Art))
        {
            tempCoverPath = await DownloadCoverAsync(track.Art, token);
        }

        if (effectiveTagSettings.Cover &&
            string.IsNullOrWhiteSpace(tempCoverPath) &&
            !TrackHasEmbeddedArtwork(filePath, config, platformId))
        {
            tempCoverPath = TryResolveFolderArtworkPath(filePath);
        }

        await WriteTagsOnetaggerStyleAsync(
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
        await ApplyCustomTagsAsync(
            filePath,
            track,
            config,
            platformId,
            effectiveTagSettings.UseNullSeparator);

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

    private async Task WriteTagsOnetaggerStyleAsync(
        TagWriteRequest request,
        CancellationToken token)
    {
        var context = BuildTagWriteExecutionContext(request);
        var chapterSnapshot = AtlTagHelper.CaptureChapters(context.FilePath, context.Extension, _logger);

        using (var file = TagLib.File.Create(context.FilePath))
        {
            PrepareId3Version(file, context);

            var tagWriteContext = new TagWriteContext(
                file,
                context.Extension,
                context.Config,
                context.Separator,
                context.PlatformId,
                context.EffectiveTagSettings.UseNullSeparator,
                context.GenreAliasMap,
                context.SplitCompositeGenres);
            ApplyPrimaryTagWrites(tagWriteContext, context);
            ApplyAudioFeatureTagWrites(tagWriteContext, context);
            ApplyGenreAndStyleTagWrites(file, tagWriteContext, context);
            ApplyReleaseAndMetadataTagWrites(file, tagWriteContext, context);
            ApplyTrackAndLyricsTagWrites(file, tagWriteContext, context);
            ApplyAlbumArtTagWrite(file, context);
            file.Save();
            RemoveId3v1TagIfDisabled(file, context);
        }

        AtlTagHelper.RestoreChapters(context.FilePath, chapterSnapshot, _logger);

        var sidecarWriteResult = await WriteLyricsSidecarsAsync(context, token);
        CleanupUpgradedTxtSidecar(context, sidecarWriteResult);
    }

    private static TagWriteExecutionContext BuildTagWriteExecutionContext(TagWriteRequest request)
    {
        var extension = Path.GetExtension(request.FilePath);
        var enabledTags = new HashSet<string>(request.Config.Tags.Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);
        var normalizeGenreTags = request.Settings.NormalizeGenreTags;
        var genreAliasMap = normalizeGenreTags
            ? GenreTagAliasNormalizer.BuildAliasMap(request.Settings.GenreTagAliasRules)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var allowsSyncedByToggle = request.Settings.SyncedLyrics || request.Settings.Tags?.SyncedLyrics == true;
        var allowsUnsyncedByToggle = request.Settings.SaveLyrics || request.Settings.Tags?.Lyrics == true;
        var allowsLyricsBySettings = allowsSyncedByToggle || allowsUnsyncedByToggle;
        var selectedLyricsTypes = ParseLyricsTypeSelection(request.Settings.LrcType);
        var allowsSyncedType = allowsSyncedByToggle
            && (selectedLyricsTypes.Contains(LyricsTag) || selectedLyricsTypes.Contains(SyllableLyricsType));
        var allowsUnsyncedType = allowsUnsyncedByToggle && selectedLyricsTypes.Contains(UnsyncedLyricsType);
        var allowsTtmlByFormat = allowsSyncedByToggle && NormalizeLyricsFormat(request.Settings.LrcFormat) is "both" or "ttml";
        var sidecarState = GetLyricsSidecarState(request.FilePath);

        return new TagWriteExecutionContext
        {
            FilePath = request.FilePath,
            SourceTrack = request.SourceTrack,
            CoreTrack = request.CoreTrack,
            EffectiveTagSettings = request.EffectiveTagSettings,
            Config = request.Config,
            PlatformId = request.PlatformId,
            Separator = request.Separator,
            TempCoverPath = request.TempCoverPath,
            Extension = extension,
            EnabledTags = enabledTags,
            GenreAliasMap = genreAliasMap,
            SplitCompositeGenres = normalizeGenreTags,
            AllowsLyricsBySettings = allowsLyricsBySettings,
            AllowsSyncedType = allowsSyncedType,
            AllowsUnsyncedType = allowsUnsyncedType,
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

        SetField(tagWriteContext, new TagFieldBinding("TIT2", "TITLE", "©nam", SupportedTag.Title), new List<string> { titleValue });
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

        SetField(tagWriteContext, new TagFieldBinding("TPE1", "ARTIST", "©ART", SupportedTag.Artist), artistValues);
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
            new TagFieldBinding("TPE2", "ALBUMARTIST", "aART", SupportedTag.AlbumArtist),
            albumArtistValues);
    }

    private static void WriteAlbumTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(AlbumTag) || !context.EffectiveTagSettings.Album || context.CoreTrack.Album == null)
        {
            return;
        }

        SetField(tagWriteContext, new TagFieldBinding("TALB", "ALBUM", "©alb", SupportedTag.Album), new List<string> { context.CoreTrack.Album.Title });
    }

    private static void WriteKeyTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("key") || string.IsNullOrWhiteSpace(context.SourceTrack.Key))
        {
            return;
        }

        var keyValue = context.Config.Camelot ? ToCamelot(context.SourceTrack.Key) : context.SourceTrack.Key;
        SetField(tagWriteContext, new TagFieldBinding("TKEY", "INITIALKEY", "initialkey", SupportedTag.Key), new List<string> { keyValue });
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
        var genres = SanitizeGenres(context.CoreTrack.Album?.Genre ?? new List<string>(), context.GenreAliasMap, context.SplitCompositeGenres);
        var styles = context.SourceTrack.Styles.ToList();
        (genres, styles) = ApplyStylesOptions(genres, styles, context.Config.StylesOptions);

        if (context.EnabledTags.Contains(GenreTag) && context.EffectiveTagSettings.Genre && genres.Count > 0)
        {
            if (context.Config.MergeGenres)
            {
                var existing = SanitizeGenres(ReadExistingGenre(context.FilePath), context.GenreAliasMap, context.SplitCompositeGenres);
                var genreSet = new HashSet<string>(genres, StringComparer.OrdinalIgnoreCase);
                genres.AddRange(existing.Where(genreSet.Add));
            }

            genres = SanitizeGenres(genres, context.GenreAliasMap, context.SplitCompositeGenres);
            if (context.Config.CapitalizeGenres)
            {
                genres = genres.Select(CapitalizeGenre).ToList();
            }

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
            var existingStyles = ReadExistingRawTag(file, context.Extension, styleTagName);
            var existingStyleSet = new HashSet<string>(existingStyles, StringComparer.OrdinalIgnoreCase);
            existingStyles.AddRange(styleValues.Where(existingStyleSet.Add));
            styleValues = existingStyles;
        }

        var rawName = context.Config.StylesOptions.Equals("customTag", StringComparison.OrdinalIgnoreCase)
            ? styleTagName
            : ResolveFieldRawName(SupportedTag.Style, ResolveFormatName(context.Extension), context.Config);
        SetRaw(tagWriteContext, rawName, SupportedTag.Style, styleValues);
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
        WriteCatalogNumberTag(tagWriteContext, context);
        WriteDurationTag(tagWriteContext, context);
        WriteRemixerTag(tagWriteContext, context);
        WriteIsrcTag(tagWriteContext, context);
        WriteMoodTag(tagWriteContext, context);
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
    }

    private static void WriteUrlTag(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains("url") || string.IsNullOrWhiteSpace(context.SourceTrack.Url))
        {
            return;
        }

        SetRaw(tagWriteContext, WwwAudioFileTag, SupportedTag.URL, new List<string> { context.SourceTrack.Url });
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

        var totalSeconds = ((int)context.SourceTrack.Duration.Value.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        SetField(
            tagWriteContext,
            new TagFieldBinding("TLEN", LengthUpperTag, LengthUpperTag, SupportedTag.Duration),
            new List<string> { totalSeconds });
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

    private static void ApplyTrackAndLyricsTagWrites(
        TagLib.File file,
        TagWriteContext tagWriteContext,
        TagWriteExecutionContext context)
    {
        WriteDiscNumberTag(file, context);
        WriteTrackNumberTag(file, context);
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
            null,
            SupportedTag.DiscNumber,
            isDisc: true);
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
    }

    private static void WriteSyncedLyrics(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!ShouldWriteSyncedLyrics(context))
        {
            return;
        }

        WriteLyrics(file, context.Extension, context.SourceTrack, true, context.Config);
    }

    private static void WriteUnsyncedLyrics(TagLib.File file, TagWriteExecutionContext context)
    {
        if (!ShouldWriteUnsyncedLyrics(context))
        {
            return;
        }

        WriteLyrics(file, context.Extension, context.SourceTrack, false, context.Config);
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
            new List<string> { context.SourceTrack.Explicit.Value ? "1" : "2" });
    }

    private static void WriteOtherTags(TagWriteContext tagWriteContext, TagWriteExecutionContext context)
    {
        if (!context.EnabledTags.Contains(OtherTagsTag) || context.SourceTrack.Other.Count == 0)
        {
            return;
        }

        foreach (var kvp in context.SourceTrack.Other)
        {
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

            SetRaw(tagWriteContext, kvp.Key, SupportedTag.OtherTags, kvp.Value.ToList());
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

        if (!context.Config.AlbumArtFile)
        {
            return;
        }

        var coverPath = Path.Join(Path.GetDirectoryName(context.FilePath) ?? "", "cover.jpg");
        if (!IOFile.Exists(coverPath))
        {
            IOFile.Copy(tempCoverPath, coverPath, overwrite: false);
        }
    }

    private static async Task<LyricsSidecarWriteResult> WriteLyricsSidecarsAsync(
        TagWriteExecutionContext context,
        CancellationToken token)
    {
        var wroteLrcSidecar = false;
        var wroteTtmlSidecar = false;
        var sidecarLrcLines = ResolveLrcSidecarLines(context.SourceTrack, context.FilePath);
        if (context.Config.WriteLrc
            && context.AllowsLyricsBySettings
            && (context.AllowsSyncedType || context.AllowsUnsyncedType)
            && sidecarLrcLines.Count > 0)
        {
            var lrcPath = Path.ChangeExtension(context.FilePath, ".lrc");
            if (!IOFile.Exists(lrcPath))
            {
                await IOFile.WriteAllLinesAsync(lrcPath, sidecarLrcLines, token);
                wroteLrcSidecar = true;
            }
        }

        var sidecarTtml = ResolveTtmlSidecarPayload(context.SourceTrack, sidecarLrcLines, context.FilePath);
        if (context.EnabledTags.Contains(TtmlLyricsTag)
            && context.AllowsLyricsBySettings
            && context.AllowsTtmlByFormat
            && !string.IsNullOrWhiteSpace(sidecarTtml))
        {
            var ttmlPath = Path.ChangeExtension(context.FilePath, TtmlExtension);
            if (!IOFile.Exists(ttmlPath))
            {
                await IOFile.WriteAllTextAsync(ttmlPath, sidecarTtml, token);
                wroteTtmlSidecar = true;
            }
        }

        return new LyricsSidecarWriteResult(wroteLrcSidecar, wroteTtmlSidecar);
    }

    private void CleanupUpgradedTxtSidecar(TagWriteExecutionContext context, LyricsSidecarWriteResult sidecarWriteResult)
    {
        if (!context.SidecarState.HasTxt
            || (!context.SidecarState.HasLrc
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
                _logger.LogDebug(ex, "Failed to remove upgraded TXT lyrics sidecar {Path}", context.SidecarState.TxtPath);
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
            || key.Equals(TtmlLyricsTag, StringComparison.OrdinalIgnoreCase);
    }

    private static (bool HasAny, bool HasLrc, bool HasTtml, bool HasTxt, string TxtPath) GetLyricsSidecarState(string filePath)
    {
        var lrcPath = Path.ChangeExtension(filePath, ".lrc");
        var ttmlPath = Path.ChangeExtension(filePath, TtmlExtension);
        var txtPath = Path.ChangeExtension(filePath, ".txt");
        var hasLrc = IOFile.Exists(lrcPath);
        var hasTtml = IOFile.Exists(ttmlPath);
        var hasTxt = IOFile.Exists(txtPath);
        return (hasLrc || hasTtml || hasTxt, hasLrc, hasTtml, hasTxt, txtPath);
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
            "cover",
            "folder",
            "front",
            AlbumTag,
            "albumart",
            "artwork"
        };
        var preferredExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };

        foreach (var name in preferredNames)
        {
            foreach (var ext in preferredExtensions)
            {
                var candidate = Path.Combine(directory, name + ext);
                if (IOFile.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private async Task<string?> DownloadCoverAsync(string url, CancellationToken token)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(url, token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var tempPath = Path.Join(Path.GetTempPath(), $"autotag-cover-{Guid.NewGuid():N}.jpg");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            await using var fileStream = IOFile.Create(tempPath);
            await stream.CopyToAsync(fileStream, token);
            return tempPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to download cover art.");
            return null;
        }
    }

    private Task ApplyCustomTagsAsync(
        string filePath,
        AutoTagTrack track,
        AutoTagRunnerConfig config,
        string platformId,
        bool useNullSeparator)
    {
        if (config.Tags.Count == 0)
        {
            return Task.CompletedTask;
        }

        try
        {
            var extension = Path.GetExtension(filePath);
            var chapterSnapshot = AtlTagHelper.CaptureChapters(filePath, extension, _logger);
            using var file = TagLib.File.Create(filePath);
            var enabledTags = new HashSet<string>(config.Tags.Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);
            var separator = ResolveArtistSeparator(config, filePath);
            var writes = BuildCustomTagWrites(track, config, platformId, extension, file);

            if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
                ApplyId3CustomTags(id3, writes, config, separator, useNullSeparator, enabledTags);
            }
            else if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
            {
                var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
                ApplyVorbisCustomTags(vorbis, writes, config, separator, enabledTags);
            }
            else if (IsMp4Family(extension))
            {
                var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
                ApplyAppleCustomTags(apple, writes, config, separator, enabledTags);
            }

            file.Save();
            AtlTagHelper.RestoreChapters(filePath, chapterSnapshot, _logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed applying custom tags for {File}", filePath);
        }

        return Task.CompletedTask;
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
            [AlbumArtistTag] = SupportedTag.AlbumArtist,
            [AlbumTag] = SupportedTag.Album,
            [AlbumArtTag] = SupportedTag.AlbumArt,
            [VersionTag] = SupportedTag.Version,
            [RemixerTag] = SupportedTag.Remixer,
            [GenreTag] = SupportedTag.Genre,
            [StyleTag] = SupportedTag.Style,
            [LabelTag] = SupportedTag.Label,
            [ReleaseIdTag] = SupportedTag.ReleaseId,
            [TrackIdTag] = SupportedTag.TrackId
        };

        SupportedTagFeatureMappings.AddAudioFeatureTags(map);

        map[CatalogNumberTag] = SupportedTag.CatalogNumber;
        map[TrackNumberTag] = SupportedTag.TrackNumber;
        map[DiscNumberTag] = SupportedTag.DiscNumber;
        map[DurationTag] = SupportedTag.Duration;
        map[TrackTotalTag] = SupportedTag.TrackTotal;
        map["isrc"] = SupportedTag.ISRC;
        map[PublishDateTag] = SupportedTag.PublishDate;
        map[ReleaseDateTag] = SupportedTag.ReleaseDate;
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

    private void UpdateAutoTagCapabilities(string platformId, AutoTagTrack track)
    {
        if (string.IsNullOrWhiteSpace(platformId))
        {
            return;
        }

        try
        {
            var tags = CollectAutoTagTags(track);
            if (tags.Count == 0)
            {
                return;
            }
            _capabilitiesStore.RecordAutoTagTags(platformId, tags);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed updating AutoTag capabilities.");
        }
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

        Add(TitleTag, !string.IsNullOrWhiteSpace(track.Title));
        Add(ArtistTag, track.Artists.Count > 0);
        Add(AlbumArtistTag, track.AlbumArtists.Count > 0);
        Add(AlbumTag, !string.IsNullOrWhiteSpace(track.Album));
        Add(AlbumArtTag, !string.IsNullOrWhiteSpace(track.Art));
        Add(VersionTag, !string.IsNullOrWhiteSpace(track.Version));
        Add(RemixerTag, track.Remixers.Count > 0);
        Add(GenreTag, track.Genres.Count > 0);
        Add(StyleTag, track.Styles.Count > 0);
        Add(LabelTag, !string.IsNullOrWhiteSpace(track.Label));
        Add(ReleaseIdTag, !string.IsNullOrWhiteSpace(track.ReleaseId));
        Add(TrackIdTag, !string.IsNullOrWhiteSpace(track.TrackId));
        Add("bpm", track.Bpm.HasValue && track.Bpm.Value > 0);
        Add("danceability", track.Danceability.HasValue);
        Add("energy", track.Energy.HasValue);
        Add("valence", track.Valence.HasValue);
        Add("acousticness", track.Acousticness.HasValue);
        Add("instrumentalness", track.Instrumentalness.HasValue);
        Add("speechiness", track.Speechiness.HasValue);
        Add("loudness", track.Loudness.HasValue);
        Add("tempo", track.Tempo.HasValue);
        Add("timeSignature", track.TimeSignature.HasValue);
        Add("liveness", track.Liveness.HasValue);
        Add("key", !string.IsNullOrWhiteSpace(track.Key));
        Add("mood", !string.IsNullOrWhiteSpace(track.Mood));
        Add(CatalogNumberTag, !string.IsNullOrWhiteSpace(track.CatalogNumber));
        Add(TrackNumberTag, track.TrackNumber.HasValue && track.TrackNumber.Value > 0);
        Add(TrackTotalTag, track.TrackTotal.HasValue && track.TrackTotal.Value > 0);
        Add(DiscNumberTag, track.DiscNumber.HasValue && track.DiscNumber.Value > 0);
        Add(DurationTag, track.Duration.HasValue && track.Duration.Value.TotalSeconds > 0);
        Add("isrc", !string.IsNullOrWhiteSpace(track.Isrc));
        Add(PublishDateTag, track.PublishDate.HasValue);
        Add(ReleaseDateTag, track.ReleaseDate.HasValue);
        Add("url", !string.IsNullOrWhiteSpace(track.Url));
        Add(ExplicitTag, track.Explicit.HasValue);

        var otherKeys = track.Other.Keys.ToList();
        var hasSyncedLyrics = otherKeys.Any(k => k.Equals(SyncedLyricsTag, StringComparison.OrdinalIgnoreCase));
        var hasUnsyncedLyrics = otherKeys.Any(k => k.Equals(UnsyncedLyricsTag, StringComparison.OrdinalIgnoreCase) ||
                                                   k.Equals(LyricsTag, StringComparison.OrdinalIgnoreCase));
        var hasTtmlLyrics = otherKeys.Any(k => k.Equals(TtmlLyricsTag, StringComparison.OrdinalIgnoreCase));
        Add(SyncedLyricsTag, hasSyncedLyrics);
        Add(UnsyncedLyricsTag, hasUnsyncedLyrics);
        Add(TtmlLyricsTag, hasTtmlLyrics);

        var otherTagKeys = otherKeys
            .Where(k => !k.Equals(SyncedLyricsTag, StringComparison.OrdinalIgnoreCase) &&
                        !k.Equals(UnsyncedLyricsTag, StringComparison.OrdinalIgnoreCase) &&
                        !k.Equals(LyricsTag, StringComparison.OrdinalIgnoreCase) &&
                        !k.Equals(TtmlLyricsTag, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Add(OtherTagsTag, otherTagKeys.Count > 0);

        return tags;
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
            SupportedTag.ISRC => TagRawProbe.HasId3Raw(tag, "TSRC"),
            SupportedTag.CatalogNumber => TagRawProbe.HasId3Raw(tag, CatalogNumberUpperTag),
            SupportedTag.Version => TagRawProbe.HasId3Raw(tag, "TIT3"),
            SupportedTag.TrackNumber => tag.Track > 0,
            SupportedTag.TrackTotal => tag.TrackCount > 0,
            SupportedTag.DiscNumber => tag.Disc > 0,
            SupportedTag.Duration => TagRawProbe.HasId3Raw(tag, "TLEN"),
            SupportedTag.Remixer => TagRawProbe.HasId3Raw(tag, "TPE4"),
            SupportedTag.Mood => TagRawProbe.HasId3Raw(tag, "TMOO"),
            SupportedTag.ReleaseDate => TagRawProbe.HasId3Raw(tag, config.Id3v24 ? "TDRC" : "TYER"),
            SupportedTag.PublishDate => TagRawProbe.HasId3Raw(tag, "TDRL"),
            SupportedTag.URL => TagRawProbe.HasId3Raw(tag, WwwAudioFileTag),
            SupportedTag.TrackId => TagRawProbe.HasId3Raw(tag, $"{platformId.ToUpperInvariant()}_TRACK_ID"),
            SupportedTag.ReleaseId => TagRawProbe.HasId3Raw(tag, $"{platformId.ToUpperInvariant()}_RELEASE_ID"),
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
            SupportedTag.Title => tag.GetField("TITLE").Length > 0,
            SupportedTag.Artist => tag.GetField("ARTIST").Length > 0,
            SupportedTag.AlbumArtist => tag.GetField("ALBUMARTIST").Length > 0,
            SupportedTag.Album => tag.GetField("ALBUM").Length > 0,
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
            SupportedTag.ISRC => tag.GetField("ISRC").Length > 0,
            SupportedTag.CatalogNumber => tag.GetField(CatalogNumberUpperTag).Length > 0,
            SupportedTag.Version => tag.GetField("SUBTITLE").Length > 0,
            SupportedTag.TrackNumber => tag.GetField("TRACKNUMBER").Length > 0,
            SupportedTag.TrackTotal => tag.GetField(TrackTotalRawTag).Length > 0,
            SupportedTag.DiscNumber => tag.GetField("DISCNUMBER").Length > 0,
            SupportedTag.Duration => tag.GetField(LengthUpperTag).Length > 0,
            SupportedTag.Remixer => tag.GetField(RemixerUpperTag).Length > 0,
            SupportedTag.Mood => tag.GetField("MOOD").Length > 0,
            SupportedTag.ReleaseDate => tag.GetField("DATE").Length > 0,
            SupportedTag.PublishDate => tag.GetField("ORIGINALDATE").Length > 0,
            SupportedTag.URL => tag.GetField(WwwAudioFileTag).Length > 0,
            SupportedTag.TrackId => tag.GetField($"{platformId.ToUpperInvariant()}_TRACK_ID").Length > 0,
            SupportedTag.ReleaseId => tag.GetField($"{platformId.ToUpperInvariant()}_RELEASE_ID").Length > 0,
            SupportedTag.MetaTags => tag.GetField(TaggedDateTag).Length > 0,
            SupportedTag.UnsyncedLyrics => tag.GetField("LYRICS").Any(value => !string.IsNullOrWhiteSpace(value)),
            SupportedTag.SyncedLyrics =>
                tag.GetField("LYRICS_SYNCED").Any(value => !string.IsNullOrWhiteSpace(value))
                || HasTimestampedLyricsPayload(tag.GetField("LYRICS")),
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
            SupportedTag.ISRC => Mp4TagHelper.HasRaw(file, "ISRC"),
            SupportedTag.CatalogNumber => Mp4TagHelper.HasRaw(file, CatalogNumberUpperTag),
            SupportedTag.Version => Mp4TagHelper.HasRaw(file, "desc"),
            SupportedTag.TrackNumber => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.TrackTotal => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.DiscNumber => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.Duration => Mp4TagHelper.HasRaw(file, LengthUpperTag),
            SupportedTag.Remixer => Mp4TagHelper.HasRaw(file, RemixerUpperTag),
            SupportedTag.Mood => Mp4TagHelper.HasRaw(file, "MOOD"),
            SupportedTag.Key => Mp4TagHelper.HasRaw(file, "initialkey"),
            SupportedTag.ReleaseDate => Mp4TagHelper.HasRaw(file, "©day"),
            SupportedTag.PublishDate => false,
            SupportedTag.URL => Mp4TagHelper.HasRaw(file, WwwAudioFileTag),
            SupportedTag.TrackId => Mp4TagHelper.HasRaw(file, $"{platformId.ToUpperInvariant()}_TRACK_ID"),
            SupportedTag.ReleaseId => Mp4TagHelper.HasRaw(file, $"{platformId.ToUpperInvariant()}_RELEASE_ID"),
            SupportedTag.MetaTags => Mp4TagHelper.HasRaw(file, TaggedDateTag),
            SupportedTag.UnsyncedLyrics => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.SyncedLyrics =>
                Mp4TagHelper.HasRaw(file, "LYRICS_SYNCED")
                || ContainsTimestampedLyrics(file.Tag.Lyrics),
            SupportedTag.AlbumArt => Mp4TagHelper.HasField(file, supportedTag),
            SupportedTag.Explicit => Mp4TagHelper.HasRaw(file, ItunesAdvisoryTag),
            _ => false
        };
    }

    private static bool HasTimestampedLyricsPayload(IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (ContainsTimestampedLyrics(value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsTimestampedLyrics(string? rawLyrics)
    {
        if (string.IsNullOrWhiteSpace(rawLyrics))
        {
            return false;
        }

        foreach (var line in rawLyrics
                     .Split(LyricsLineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseLrcLine(line, out _, out _))
            {
                return true;
            }
        }

        return false;
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
            var styleValues = track.Styles.ToList();
            if (config.MergeGenres)
            {
                var existing = ReadExistingRawTag(file, extension, styleTagName);
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
        foreach (var kvp in otherTags.Where(kvp => kvp.Value.Count > 0))
        {
            writes.Add(new CustomTagWrite(OtherTagsTag, SupportedTag.OtherTags, kvp.Key, kvp.Value.ToList()));
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
                _ => "initialkey"
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
        public required string PlatformId { get; init; }
        public required string Separator { get; init; }
        public string? TempCoverPath { get; init; }
        public required string Extension { get; init; }
        public required HashSet<string> EnabledTags { get; init; }
        public required IReadOnlyDictionary<string, string> GenreAliasMap { get; init; }
        public required bool SplitCompositeGenres { get; init; }
        public required bool AllowsLyricsBySettings { get; init; }
        public required bool AllowsSyncedType { get; init; }
        public required bool AllowsUnsyncedType { get; init; }
        public required bool AllowsTtmlByFormat { get; init; }
        public required (bool HasAny, bool HasLrc, bool HasTtml, bool HasTxt, string TxtPath) SidecarState { get; init; }
        public required bool ShouldSkipEmbeddedLyrics { get; init; }
    }

    public sealed class LocalAutoTagRunnerCollaborators
    {
        public required ILogger<LocalAutoTagRunner> Logger { get; init; }
        public required IHttpClientFactory HttpClientFactory { get; init; }
        public required MusicBrainzMatcher MusicBrainzMatcher { get; init; }
        public required BeatportMatcher BeatportMatcher { get; init; }
        public required DiscogsMatcher DiscogsMatcher { get; init; }
        public required TraxsourceMatcher TraxsourceMatcher { get; init; }
        public required JunoDownloadMatcher JunoDownloadMatcher { get; init; }
        public required BandcampMatcher BandcampMatcher { get; init; }
        public required BeatsourceMatcher BeatsourceMatcher { get; init; }
        public required BpmSupremeMatcher BpmSupremeMatcher { get; init; }
        public required ItunesMatcher ItunesMatcher { get; init; }
        public required SpotifyMatcher SpotifyMatcher { get; init; }
        public required DeezerMatcher DeezerMatcher { get; init; }
        public required LastFmMatcher LastFmMatcher { get; init; }
        public required BoomplayMatcher BoomplayMatcher { get; init; }
        public required MusixmatchMatcher MusixmatchMatcher { get; init; }
        public required LrclibMatcher LrclibMatcher { get; init; }
        public required ShazamMatcher ShazamMatcher { get; init; }
        public required ShazamRecognitionService ShazamRecognitionService { get; init; }
        public required AppleLyricsService AppleLyricsService { get; init; }
        public required AppleMusicCatalogService AppleMusicCatalogService { get; init; }
        public required DownloadLyricsService DownloadLyricsService { get; init; }
        public required DeezSpoTagSettingsService SettingsService { get; init; }
        public required PlatformCapabilitiesStore CapabilitiesStore { get; init; }
    }

    private readonly record struct TagWriteContext(
        TagLib.File File,
        string Extension,
        AutoTagRunnerConfig Config,
        string Separator,
        string PlatformId,
        bool UseNullSeparator,
        IReadOnlyDictionary<string, string> GenreAliasMap,
        bool SplitCompositeGenres);

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
            values = SanitizeGenres(values, context.GenreAliasMap, context.SplitCompositeGenres);
        }

        if (IsMp4Family(context.Extension))
        {
            Mp4TagHelper.SetMp4Field(
                context.File,
                binding.Tag,
                values,
                context.Config,
                context.PlatformId,
                context.GenreAliasMap,
                context.SplitCompositeGenres);
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
            values = SanitizeGenres(values, context.GenreAliasMap, context.SplitCompositeGenres);
        }

        if (!force && !ShouldOverwriteTag(context.Config, tag))
        {
            if (tag == SupportedTag.OtherTags)
            {
                if (HasRawTag(context.File, context.Extension, rawName))
                {
                    return;
                }
            }
            else if (HasTag(context.File, context.Extension, tag, context.Config, context.PlatformId))
            {
                return;
            }
        }

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
                context.SplitCompositeGenres);
        }
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
        var payload = new DateWritePayload(
            Date: date,
            UseYearOnly: config.OnlyYear,
            Year: date.Year.ToString(CultureInfo.InvariantCulture),
            DateString: config.OnlyYear
                ? date.Year.ToString(CultureInfo.InvariantCulture)
                : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

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
        var field = kind == ReleaseDateTag ? "DATE" : "ORIGINALDATE";
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
        if (!IsMp4Family(extension) || kind != ReleaseDateTag)
        {
            return;
        }

        if (!ShouldOverwriteTag(config, tag) && Mp4TagHelper.HasRaw(file, "©day"))
        {
            return;
        }

        Mp4TagHelper.SetDate(file, dateString);
    }

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

    private static void WriteVorbisTrackNumber(
        TagLib.File file,
        string numberText,
        int? total,
        SupportedTag tag,
        AutoTagRunnerConfig config,
        bool isDisc)
    {
        var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
        var field = isDisc ? "DISCNUMBER" : "TRACKNUMBER";
        if (!ShouldOverwriteTag(config, tag) && TagRawProbe.HasVorbisRaw(vorbis, field))
        {
            return;
        }

        SetVorbisRaw(vorbis, field, new List<string> { numberText }, "");
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

    private static void WriteLyrics(TagLib.File file, string extension, AutoTagTrack track, bool synced, AutoTagRunnerConfig config)
    {
        if (!TryResolveLyricsLines(track, synced, out var lyricsLines))
        {
            return;
        }

        var lyricsText = string.Join(Environment.NewLine, lyricsLines);
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            WriteId3Lyrics(file, synced, config, lyricsLines, lyricsText);
            return;
        }

        if (extension.Equals(FlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            WriteVorbisLyrics(file, synced, config, lyricsText);
            return;
        }

        WriteGenericLyrics(file, synced, config, lyricsText);
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

    private static void WriteId3Lyrics(
        TagLib.File file,
        bool synced,
        AutoTagRunnerConfig config,
        IReadOnlyList<string> lyricsLines,
        string lyricsText)
    {
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
        if (synced)
        {
            WriteId3SyncedLyrics(id3, config, lyricsLines);
            return;
        }

        WriteId3UnsyncedLyrics(id3, config, lyricsText);
    }

    private static void WriteId3SyncedLyrics(TagLib.Id3v2.Tag id3, AutoTagRunnerConfig config, IReadOnlyList<string> lyricsLines)
    {
        if (!ShouldOverwriteTag(config, SupportedTag.SyncedLyrics)
            && id3.GetFrames<TagLib.Id3v2.SynchronisedLyricsFrame>("SYLT").Any())
        {
            return;
        }

        if (!lyricsLines.Any(line => line.StartsWith('[')))
        {
            return;
        }

        var lang = string.IsNullOrWhiteSpace(config.Id3CommLang) ? "eng" : config.Id3CommLang;
        var frame = new TagLib.Id3v2.SynchronisedLyricsFrame(string.Empty, lang, TagLib.Id3v2.SynchedTextType.Lyrics)
        {
            Format = TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds
        };

        frame.Text = BuildSyncedLyricsItems(lyricsLines).ToArray();
        id3.AddFrame(frame);
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

    private static void WriteId3UnsyncedLyrics(TagLib.Id3v2.Tag id3, AutoTagRunnerConfig config, string lyricsText)
    {
        if (!ShouldOverwriteTag(config, SupportedTag.UnsyncedLyrics)
            && id3.GetFrames<TagLib.Id3v2.UnsynchronisedLyricsFrame>("USLT").Any())
        {
            return;
        }

        var lang = string.IsNullOrWhiteSpace(config.Id3CommLang) ? "eng" : config.Id3CommLang;
        var frame = TagLib.Id3v2.UnsynchronisedLyricsFrame.Get(id3, string.Empty, lang, true);
        frame.Text = lyricsText;
    }

    private static void WriteVorbisLyrics(TagLib.File file, bool synced, AutoTagRunnerConfig config, string lyricsText)
    {
        var vorbis = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
        var supportedTag = synced ? SupportedTag.SyncedLyrics : SupportedTag.UnsyncedLyrics;
        if (!ShouldOverwriteTag(config, supportedTag) && TagRawProbe.HasVorbisRaw(vorbis, "LYRICS"))
        {
            return;
        }

        vorbis.SetField("LYRICS", lyricsText);
    }

    private static void WriteGenericLyrics(TagLib.File file, bool synced, AutoTagRunnerConfig config, string lyricsText)
    {
        var supportedTag = synced ? SupportedTag.SyncedLyrics : SupportedTag.UnsyncedLyrics;
        if (!ShouldOverwriteTag(config, supportedTag) && !string.IsNullOrWhiteSpace(file.Tag.Lyrics))
        {
            return;
        }

        file.Tag.Lyrics = lyricsText;
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

    private static IReadOnlyList<string> ResolveLrcSidecarLines(AutoTagTrack sourceTrack, string filePath)
    {
        var existingLrc = ResolveExistingLrcSidecar(filePath);
        if (existingLrc.Count > 0)
        {
            return existingLrc;
        }

        var syncedPayload = ResolveLyricsPayloadLines(sourceTrack, SyncedLyricsTag);
        if (syncedPayload.Count > 0)
        {
            return syncedPayload;
        }

        var genericPayload = ResolveLyricsPayloadLines(sourceTrack, LyricsTag);
        if (genericPayload.Count > 0)
        {
            return genericPayload;
        }

        var ttmlPayload = ResolveLrcFromTtmlPayload(sourceTrack);
        if (ttmlPayload.Count > 0)
        {
            return ttmlPayload;
        }

        return ResolveLrcFromExistingTtmlSidecar(filePath);
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
        catch
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

    private static IReadOnlyList<string> ResolveLrcFromTtmlPayload(AutoTagTrack sourceTrack)
    {
        if (!sourceTrack.Other.TryGetValue(TtmlLyricsTag, out var ttmlPayload) || ttmlPayload.Count == 0)
        {
            return Array.Empty<string>();
        }

        return ConvertTtmlToLrcLines(ComposeTtmlPayload(ttmlPayload));
    }

    private static IReadOnlyList<string> ResolveLrcFromExistingTtmlSidecar(string filePath)
    {
        var existingTtmlPath = Path.ChangeExtension(filePath, TtmlExtension);
        if (!IOFile.Exists(existingTtmlPath))
        {
            return Array.Empty<string>();
        }

        try
        {
            return ConvertTtmlToLrcLines(IOFile.ReadAllText(existingTtmlPath));
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ConvertTtmlToLrcLines(string? ttml)
    {
        if (string.IsNullOrWhiteSpace(ttml))
        {
            return Array.Empty<string>();
        }

        try
        {
            var lrcFromTtml = AppleLyricsService.ConvertTtmlToLrcPublic(ttml);
            if (string.IsNullOrWhiteSpace(lrcFromTtml))
            {
                return Array.Empty<string>();
            }

            return NormalizeLyricsLines(
                lrcFromTtml.Split(LyricsLineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                requireTimestamp: true);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? ResolveTtmlSidecarPayload(AutoTagTrack sourceTrack, IReadOnlyList<string> sidecarLrcLines, string filePath)
    {
        var existingTtmlPath = Path.ChangeExtension(filePath, TtmlExtension);
        if (IOFile.Exists(existingTtmlPath))
        {
            return null;
        }

        var existingLrcPath = Path.ChangeExtension(filePath, ".lrc");
        if (IOFile.Exists(existingLrcPath) && sidecarLrcLines.Count == 0)
        {
            try
            {
                var existingLrcLines = NormalizeLyricsLines(IOFile.ReadAllLines(existingLrcPath), requireTimestamp: true);
                if (existingLrcLines.Count > 0)
                {
                    return BuildTtmlFromLrcLines(existingLrcLines);
                }
            }
            catch
            {
                // Ignore malformed sidecar and continue.
            }
        }

        if (sourceTrack.Other.TryGetValue(TtmlLyricsTag, out var ttmlPayload) && ttmlPayload.Count > 0)
        {
            var existing = ComposeTtmlPayload(ttmlPayload);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }
        }

        if (sidecarLrcLines.Count == 0)
        {
            return null;
        }

        return BuildTtmlFromLrcLines(sidecarLrcLines);
    }

    private static List<string> NormalizeLyricsLines(IEnumerable<string> lines, bool requireTimestamp)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var trimmed = line?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (requireTimestamp && !trimmed.StartsWith('['))
            {
                continue;
            }

            if (seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized;
    }

    private static string? ComposeTtmlPayload(IEnumerable<string> payloadLines)
    {
        var ttml = string.Join(Environment.NewLine, payloadLines.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(ttml) ? null : ttml;
    }

    private static string? BuildTtmlFromLrcLines(IEnumerable<string> lrcLines)
    {
        var parsed = new List<(TimeSpan Start, string Text)>();
        foreach (var line in lrcLines)
        {
            if (!TryParseLrcLine(line, out var timestamp, out var text) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            parsed.Add((timestamp, text.Trim()));
        }

        if (parsed.Count == 0)
        {
            return null;
        }

        parsed = parsed
            .OrderBy(entry => entry.Start)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.AppendLine("<tt xmlns=\"http://www.w3.org/ns/ttml\">");
        builder.AppendLine("  <body>");
        builder.AppendLine("    <div>");

        for (var i = 0; i < parsed.Count; i++)
        {
            var current = parsed[i];
            var beginMs = Math.Max(0, (int)Math.Round(current.Start.TotalMilliseconds));
            var endMs = beginMs + 4000;
            if (i + 1 < parsed.Count)
            {
                var nextMs = Math.Max(beginMs + 1, (int)Math.Round(parsed[i + 1].Start.TotalMilliseconds));
                endMs = nextMs;
            }

            var encodedText = WebUtility.HtmlEncode(current.Text);
            if (string.IsNullOrWhiteSpace(encodedText))
            {
                continue;
            }

            builder.AppendLine($"      <p begin=\"{FormatTtmlSidecarTimestamp(beginMs)}\" end=\"{FormatTtmlSidecarTimestamp(endMs)}\">{encodedText}</p>");
        }

        builder.AppendLine("    </div>");
        builder.AppendLine("  </body>");
        builder.AppendLine("</tt>");
        return builder.ToString();
    }

    private static string FormatTtmlSidecarTimestamp(int milliseconds)
    {
        var clamped = Math.Max(0, milliseconds);
        var ts = TimeSpan.FromMilliseconds(clamped);
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
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
            MimeType = "image/jpeg",
            Description = "Cover"
        };

        var extension = Path.GetExtension(file.Name);
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true);
            id3.RemoveFrames("APIC");
#pragma warning disable CS0618
            var apic = new TagLib.Id3v2.AttachedPictureFrame(picture)
            {
                TextEncoding = coverDescriptionUtf8 ? TagLib.StringType.UTF8 : TagLib.StringType.Latin1
            };
#pragma warning restore CS0618
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
                SupportedTag.UnsyncedLyrics => !string.IsNullOrWhiteSpace(file.Tag.Lyrics),
                SupportedTag.AlbumArt => file.Tag.Pictures?.Length > 0,
                _ => false
            };
        }

        public static void SetMp4Field(
            TagLib.File file,
            SupportedTag tag,
            List<string> values,
            AutoTagRunnerConfig config,
            string platformId,
            IReadOnlyDictionary<string, string> genreAliasMap,
            bool splitCompositeGenres)
        {
            if (!ShouldOverwriteTag(config, tag) && HasTag(file, ".mp4", tag, config, platformId))
            {
                return;
            }

            switch (tag)
            {
                case SupportedTag.Title:
                    file.Tag.Title = values.FirstOrDefault() ?? "";
                    break;
                case SupportedTag.Artist:
                    file.Tag.Performers = values.ToArray();
                    break;
                case SupportedTag.AlbumArtist:
                    file.Tag.AlbumArtists = values.ToArray();
                    break;
                case SupportedTag.Album:
                    file.Tag.Album = values.FirstOrDefault() ?? "";
                    break;
                case SupportedTag.Genre:
                    file.Tag.Genres = SanitizeGenres(values, genreAliasMap, splitCompositeGenres).ToArray();
                    break;
                case SupportedTag.BPM:
                    if (int.TryParse(values.FirstOrDefault(), out var bpm))
                    {
                        file.Tag.BeatsPerMinute = (uint)bpm;
                    }
                    break;
                case SupportedTag.TrackNumber:
                    if (int.TryParse(values.FirstOrDefault(), out var track))
                    {
                        file.Tag.Track = (uint)track;
                    }
                    break;
                case SupportedTag.TrackTotal:
                    if (int.TryParse(values.FirstOrDefault(), out var total))
                    {
                        file.Tag.TrackCount = (uint)total;
                    }
                    break;
                case SupportedTag.DiscNumber:
                    if (int.TryParse(values.FirstOrDefault(), out var disc))
                    {
                        file.Tag.Disc = (uint)disc;
                    }
                    break;
            }
        }

        public static void SetMp4Raw(
            TagLib.File file,
            string rawName,
            string[] values,
            IReadOnlyDictionary<string, string> genreAliasMap,
            bool splitCompositeGenres)
        {
            var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true);
            var normalized = Mp4RawTagNameNormalizer.Normalize(rawName);
            var output = IsGenreRawTag(normalized) || IsGenreRawTag(rawName)
                ? SanitizeGenres(values, genreAliasMap, splitCompositeGenres).ToArray()
                : values;
            TrySetAppleDashBox(apple, normalized, output);
        }

        public static bool HasRaw(TagLib.File file, string rawName)
        {
            var apple = (TagLib.Mpeg4.AppleTag?)file.GetTag(TagTypes.Apple, false);
            return apple != null && TagRawProbe.HasAppleDashBox(apple, Mp4RawTagNameNormalizer.Normalize(rawName));
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
        bool splitComposite = false)
    {
        return GenreTagAliasNormalizer.NormalizeAndExpandValues(values, genreAliasMap, splitComposite)
            .Where(value => !BlockedGenres.Contains(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            if (apple == null) return new List<string>();
            return readAppleValues(apple, Mp4RawTagNameNormalizer.Normalize(name));
        }

        return new List<string>();
    }

    private static void ApplyId3CustomTags(
        TagLib.Id3v2.Tag tag,
        List<CustomTagWrite> writes,
        AutoTagRunnerConfig config,
        string separator,
        bool useNullSeparator,
        HashSet<string> enabledTags)
    {
        foreach (var write in writes)
        {
            if (!enabledTags.Contains(write.TagKey) || write.Values.Count == 0)
            {
                continue;
            }

            if (!ShouldOverwriteTag(config, write.SupportedTag) && TagRawProbe.HasId3Raw(tag, write.RawTagName))
            {
                continue;
            }

            SetId3Raw(tag, write.RawTagName, write.Values, separator, useNullSeparator);
        }
    }

    private static void ApplyVorbisCustomTags(TagLib.Ogg.XiphComment tag, List<CustomTagWrite> writes, AutoTagRunnerConfig config, string separator, HashSet<string> enabledTags)
    {
        foreach (var write in writes)
        {
            if (!enabledTags.Contains(write.TagKey) || write.Values.Count == 0)
            {
                continue;
            }

            if (!ShouldOverwriteTag(config, write.SupportedTag) && TagRawProbe.HasVorbisRaw(tag, write.RawTagName))
            {
                continue;
            }

            SetVorbisRaw(tag, write.RawTagName, write.Values, separator);
        }
    }

    private static void ApplyAppleCustomTags(TagLib.Mpeg4.AppleTag tag, List<CustomTagWrite> writes, AutoTagRunnerConfig config, string separator, HashSet<string> enabledTags)
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
                continue;
            }

            TrySetAppleDashBox(tag, rawName, ApplySeparator(write.Values, separator));
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
            var enabled = new HashSet<string>(config.Tags.Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);
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
            ApplyPreferenceAwareOverwriteGuards(copy, sourceTrack, runtimeSettings, file);
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
            MultiArtistSeparator = baseSettings.MultiArtistSeparator,
            SingleAlbumArtist = baseSettings.SingleAlbumArtist,
            CoverDescriptionUTF8 = baseSettings.CoverDescriptionUTF8
        };
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
        TagLib.File file)
    {
        if (sourceTrack == null
            || runtimeSettings == null
            || (!effectiveTagSettings.Artist && !effectiveTagSettings.AlbumArtist && !effectiveTagSettings.Title))
        {
            return;
        }

        var existingArtistCredits = file.Tag.Performers?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList() ?? new List<string>();
        if (!string.IsNullOrWhiteSpace(file.Tag.FirstPerformer))
        {
            existingArtistCredits.Add(file.Tag.FirstPerformer!);
        }

        var existingAlbumArtistCredits = file.Tag.AlbumArtists?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList() ?? new List<string>();

        ApplyPreferenceAwareArtistGuards(
            effectiveTagSettings,
            sourceTrack,
            runtimeSettings,
            existingArtistCredits,
            existingAlbumArtistCredits,
            file.Tag.Title);
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

        return config.Separators.Id3 ?? "";
    }

    private static List<string> ReadAppleDashBox(TagLib.Mpeg4.AppleTag tag, string name)
    {
        return AppleDashBoxReflectionHelper.ReadValues(tag, name);
    }

    private static void TrySetAppleDashBox(TagLib.Mpeg4.AppleTag? tag, string name, string[] values)
    {
        AppleDashBoxReflectionHelper.TrySetValues(tag, name, values);
    }

    private sealed class AutoTagRunPlan
    {
        public required AutoTagRunnerConfig Config { get; init; }
        public required string TargetPath { get; init; }
        public required AutoTagMatchingConfig MatchingConfig { get; init; }
        public required List<string> EffectivePlatforms { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required TagSettings TagSettings { get; init; }
        public required List<string> Files { get; init; }
        public required Dictionary<string, ShazamRecognitionInfo?> ShazamCache { get; init; }
        public required bool EnableShazamFallback { get; init; }
        public required bool ForceShazamMatch { get; init; }
        public required bool ShazamConflictResolution { get; init; }
        public HashSet<string> PreSkippedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TaggedByAnyPlatform { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int PlatformCount => EffectivePlatforms.Count;
        public int FileCount => Files.Count;
    }

    private sealed class AutoTagFileRunContext
    {
        public required AutoTagRunPlan Plan { get; init; }
        public required JobMatchCacheState JobMatchCache { get; init; }
        public required string Platform { get; init; }
        public required int PlatformIndex { get; init; }
        public required int FileIndex { get; init; }
        public required string File { get; init; }
        public required double Progress { get; init; }
        public required Action<TaggingStatusWrap> StatusCallback { get; init; }
        public required Action<string> LogCallback { get; init; }
        public required CancellationToken Token { get; init; }
    }

    private sealed class JobMatchCacheState
    {
        public object SyncRoot { get; } = new();
        public DateTimeOffset LastAccessUtc { get; set; } = DateTimeOffset.UtcNow;
        public Dictionary<string, MatchCacheEntry> Entries { get; } = new(StringComparer.Ordinal);
    }
    private sealed record MatchCacheEntry(AutoTagMatchResult? Match);
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
        public bool AlbumArtFile { get; set; }
        public bool Camelot { get; set; }
        public bool ShortTitle { get; set; }
        public double Strictness { get; set; } = 0.7;
        public bool MatchDuration { get; set; }
        public int MaxDurationDifference { get; set; } = 30;
        public bool MatchById { get; set; }
        public bool EnableShazam { get; set; } = true;
        public bool ForceShazam { get; set; }
        public string? ConflictResolution { get; set; }
        public bool SkipTagged { get; set; }
        public bool IncludeSubfolders { get; set; } = true;
        public bool Multiplatform { get; set; }
        public bool ParseFilename { get; set; }
        public string? FilenameTemplate { get; set; } = "%artists% - %title%";
        public bool OnlyYear { get; set; }
        public bool Id3v24 { get; set; } = true;
        public int TrackNumberLeadingZeroes { get; set; }
        public string StylesOptions { get; set; } = "default";
        public MultipleMatchesSort MultipleMatches { get; set; } = MultipleMatchesSort.Default;
        public string? TitleRegex { get; set; }
        public JsonObject? Custom { get; set; }
        public AutoTagStylesCustomTag? StylesCustomTag { get; set; }
        public string? Id3CommLang { get; set; }
        public bool WriteLrc { get; set; } = true;
        public bool CapitalizeGenres { get; set; }
        public string? TracknameTemplate { get; set; }
        public string? AlbumTracknameTemplate { get; set; }
        public string? PlaylistTracknameTemplate { get; set; }
        public bool? SaveArtwork { get; set; }
        public bool? DlAlbumcoverForPlaylist { get; set; }
        public bool? SaveArtworkArtist { get; set; }
        public string? CoverImageTemplate { get; set; }
        public string? ArtistImageTemplate { get; set; }
        public string? LocalArtworkFormat { get; set; }
        public bool? EmbedMaxQualityCover { get; set; }
        public int? JpegImageQuality { get; set; }
        public TechnicalTagSettings? Technical { get; set; }
        public string? ProfileId { get; set; }
        public string? ProfileName { get; set; }
    }

    private sealed record ShazamEnrichmentResult(bool UsedShazam, string? Error, bool IsFatal);

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
