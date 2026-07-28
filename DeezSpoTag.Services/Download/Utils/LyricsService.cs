using DeezSpoTag.Core.Models;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using DeezSpoTag.Core.Models.Settings;
using System.Linq;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Security;
using DeezSpoTag.Services.Apple;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;

namespace DeezSpoTag.Services.Download.Utils;

public sealed class LyricsProviderOptions
{
    public LrclibLyricsProviderOptions? Lrclib { get; init; }
}

public sealed class LrclibLyricsProviderOptions
{
    public int? DurationToleranceSeconds { get; init; }
    public bool? UseDurationHint { get; init; }
    public bool? SearchFallback { get; init; }
    public bool? PreferSynced { get; init; }
}

public sealed record LyricsResolutionPlan(
    IReadOnlyList<string> RequestedFormats,
    IReadOnlyList<string> Providers,
    bool PlainFallbackAllowed);

public sealed record LyricsResolutionResult(
    LyricsBase? Lyrics,
    LyricsResolutionPlan Plan,
    IReadOnlyList<string> ProvidersAttempted,
    IReadOnlyList<string> ResolvedFormats,
    IReadOnlyDictionary<string, string> SourcesByFormat,
    string? Error);

public sealed record LyricsSaveResult(IReadOnlyDictionary<string, string> FilesByFormat)
{
    public static LyricsSaveResult Empty { get; } = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Enhanced lyrics service implementing refreezer's dual API approach
/// Provides robust lyrics fetching with Pipe API primary and GW API fallback
/// </summary>
public class LyricsService
{
    private readonly ILogger<LyricsService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly JwtTokenService _jwtTokenService;
    private readonly AuthenticatedDeezerService _authenticatedDeezerService;
    private readonly DeezSpoTag.Services.Apple.AppleLyricsService _appleLyricsService;
    private readonly LrclibLyricsService _lrclibLyricsService;
    private readonly ProtectedCredentialFileStore _spotifyWebPlayerCredentialStore;
    private string? _cachedGwToken;
    private DateTime _cachedGwTokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _spotifyTokenGate = new(1, 1);
    private readonly SemaphoreSlim _musixmatchTokenGate = new(1, 1);
    private string? _cachedSpotifyAccessToken;
    private DateTimeOffset _cachedSpotifyAccessTokenExpiry = DateTimeOffset.MinValue;
    private string? _cachedSpotifyAccessTokenKey;
    private string? _cachedMusixmatchUserToken;
    private string? _cachedMusixmatchSecret;
    private const int GwTokenTtlMinutes = 45;
    private const string DefaultSpotifyWebPlayerUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36";
    private const string AppleProvider = LyricsProviderRegistry.Apple;
    private const string DeezerProvider = LyricsProviderRegistry.Deezer;
    private const string SpotifyProvider = LyricsProviderRegistry.Spotify;
    private const string LrclibProvider = LyricsProviderRegistry.Lrclib;
    private const string MusixmatchProvider = LyricsProviderRegistry.Musixmatch;
    private const string YouLyPlusProvider = LyricsProviderRegistry.YouLyPlus;
    private const string BetterLyricsProvider = LyricsProviderRegistry.BetterLyrics;
    private const string MusixmatchBaseUrl = "https://apic.musixmatch.com/ws/1.1/";
    private const string MusixmatchWebSearchUrl = "https://www.musixmatch.com/search";
    private const string MusixmatchDefaultSecret = "b3dc8788299f5806a70a6a20a0cb0ffc";
    private const string MusixmatchUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string PaxsenixBaseUrl = "https://lyrics.paxsenix.org";
    private const string ApplicationJson = "application/json";
    private const string LyricsClientName = "LyricsService";
    private const string UserAgentHeader = "User-Agent";
    private const string LyricsType = "lyrics";
    private const string UnsyncedLyricsType = "unsynced-lyrics";
    private const string SyllableLyricsType = "syllable-lyrics";
    private const string TtmlLyricsType = "ttml-lyrics";
    private const string MessagePropertyName = "message";
    private const string SpotifyDataDir = "spotify";
    private const string BlobsDir = "blobs";
    private const string HttpsScheme = "https";
    private const string SpotifyOpenHost = "open.spotify.com";
    private static readonly string SpotifyOpenBaseUrl = BuildAuthorityUrl(SpotifyOpenHost);
    private static readonly string SpotifyOpenRootUrl = BuildRootUrl(SpotifyOpenHost);
    private const string SpotifyOpenTokenPath = "/api/token";
    private const string SpotifyOpenFallbackTokenPath = "/get_access_token";
    private static readonly string DeezerPipeApiUrl = BuildUrl("pipe.deezer.com", "/api/");
    private static readonly string DeezerGwUserDataUrl = BuildUrl("www.deezer.com", "/ajax/gw-light.php?method=deezer.getUserData&input=3&api_version=1.0&api_token=null");
    private static readonly IReadOnlyList<string> DefaultLyricsProviderOrder = LyricsProviderRegistry.DefaultOrder;
    private static readonly string[] YouLyPlusServers =
    [
        "https://lyricsplus.prjktla.my.id",
        "https://lyricsplus.atomix.one",
        "https://lyricsplus.binimum.org",
        "https://lyricsplus.prjktla.workers.dev",
        "https://lyricsplus-seven.vercel.app",
        "https://lyrics-plus-backend.vercel.app"
    ];
    private static readonly ConcurrentDictionary<string, CachedLyricsResult> ProviderResultCache =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ProviderNegativeCache =
        new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim ProviderDiskCacheGate = new(1, 1);
    private static readonly TimeSpan ProviderResultCacheLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan ProviderNegativeCacheLifetime = TimeSpan.FromMinutes(15);
    private const string ProviderCacheVersion = "lyrics-v2";
    private sealed record CachedLyricsResult(DateTimeOffset ExpiresAt, LyricsBase Lyrics);
    private sealed record PersistedLyricsCacheEntry(
        string Version,
        DateTimeOffset ExpiresAt,
        bool IsNegative,
        LyricsSource? Lyrics);

    private static string BuildAuthorityUrl(string host)
    {
        return new UriBuilder(HttpsScheme, host).Uri.GetLeftPart(UriPartial.Authority);
    }

    private static string BuildRootUrl(string host)
    {
        return $"{BuildAuthorityUrl(host)}/";
    }

    private static string BuildUrl(string host, string pathAndQuery)
    {
        return $"{BuildAuthorityUrl(host)}{pathAndQuery}";
    }

    private sealed class LyricsResolutionState
    {
        public string? Arl { get; set; }
        public string? TtmlFallback { get; set; }
        public LyricsBase? ResolvedLyrics { get; set; }
        public bool DeezerAttempted { get; set; }
        public bool DeezerMissingAuth { get; set; }
        public List<string> ProvidersAttempted { get; } = new();
        public Dictionary<string, string> SourcesByFormat { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public LyricsService(
        ILogger<LyricsService> logger,
        IHttpClientFactory httpClientFactory,
        JwtTokenService jwtTokenService,
        AuthenticatedDeezerService authenticatedDeezerService,
        DeezSpoTag.Services.Apple.AppleLyricsService appleLyricsService,
        LrclibLyricsService lrclibLyricsService,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _jwtTokenService = jwtTokenService;
        _authenticatedDeezerService = authenticatedDeezerService;
        _appleLyricsService = appleLyricsService;
        _lrclibLyricsService = lrclibLyricsService;
        _spotifyWebPlayerCredentialStore = serviceProvider.GetRequiredService<ProtectedCredentialFileStore>();
    }

    /// <summary>
    /// Resolve lyrics for a track using current settings and authentication.
    /// </summary>
    public Task<LyricsBase?> ResolveLyricsAsync(
        Track track,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken = default)
        => ResolveLyricsAsync(track, settings, providerOptions: null, cancellationToken);

    public async Task<LyricsBase?> ResolveLyricsAsync(
        Track track,
        DeezSpoTagSettings settings,
        LyricsProviderOptions? providerOptions,
        CancellationToken cancellationToken = default)
        => (await ResolveLyricsWithDetailsAsync(track, settings, providerOptions, cancellationToken)).Lyrics;

    public Task<LyricsResolutionResult> ResolveLyricsWithDetailsAsync(
        Track track,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken = default)
        => ResolveLyricsWithDetailsAsync(track, settings, providerOptions: null, cancellationToken);

    public async Task<LyricsResolutionResult> ResolveLyricsWithDetailsAsync(
        Track track,
        DeezSpoTagSettings settings,
        LyricsProviderOptions? providerOptions,
        CancellationToken cancellationToken = default)
    {
        var plan = DescribeResolutionPlan(settings);
        if (track == null)
        {
            _logger.LogWarning("ResolveLyricsAsync called with null track");
            return EmptyResolution(plan, "Track is required");
        }

        var shouldFetch = ShouldHandleLyricsBySettings(settings);
        if (!shouldFetch)
        {
            return EmptyResolution(plan, null);
        }

        var outputRequirements = ResolveOutputRequirements(settings);
        var providers = plan.Providers;

        var state = new LyricsResolutionState();

        foreach (var provider in providers)
        {
            if (!ProviderCanContribute(provider, outputRequirements, state.ResolvedLyrics))
            {
                continue;
            }
            state.ProvidersAttempted.Add(provider);
            var providerLyrics = await TryResolveProviderSafelyAsync(provider, track, settings, providerOptions, state, cancellationToken);
            if (providerLyrics == null || !providerLyrics.IsLoaded())
            {
                continue;
            }

            MergeProviderLyrics(state, providerLyrics, provider);
            if (ShouldReturnResolvedLyrics(state, outputRequirements, requireAllRequestedRichLyrics: true))
            {
                return BuildResolutionResult(state, plan, outputRequirements, null);
            }
        }

        if (state.ResolvedLyrics?.IsLoaded() == true)
        {
            if (!string.IsNullOrWhiteSpace(state.TtmlFallback) && string.IsNullOrWhiteSpace(state.ResolvedLyrics.TtmlLyrics))
            {
                state.ResolvedLyrics.TtmlLyrics = state.TtmlFallback;
                state.ResolvedLyrics.TtmlLyricsSourceFormat = LyricsSourceFormat.DownloadedTtml;
            }

            if (ShouldReturnResolvedLyrics(state, outputRequirements, requireAllRequestedRichLyrics: false))
            {
                return BuildResolutionResult(state, plan, outputRequirements, null);
            }
        }

        if (!string.IsNullOrWhiteSpace(state.TtmlFallback) && outputRequirements.WantsTtmlLyrics)
        {
            var lyrics = new LyricsSource
            {
                TtmlLyrics = state.TtmlFallback,
                TtmlLyricsSourceFormat = LyricsSourceFormat.DownloadedTtml
            };
            state.ResolvedLyrics = lyrics;
            return BuildResolutionResult(state, plan, outputRequirements, null);
        }

        string error;
        if (state.DeezerAttempted && state.DeezerMissingAuth && string.IsNullOrEmpty(state.Arl))
        {
            error = "No ARL available for lyrics fetching";
        }
        else
        {
            error = "No lyrics available from configured providers";
        }

        state.ResolvedLyrics = LyricsNew.CreateError(error);
        return BuildResolutionResult(state, plan, outputRequirements, error);
    }

    public static LyricsResolutionPlan DescribeResolutionPlan(DeezSpoTagSettings settings)
    {
        var requirements = ResolveOutputRequirements(settings);
        var requested = new List<string>(4);
        if (requirements.WantsTtmlLyrics)
        {
            requested.Add("ttml");
        }
        if (requirements.WantsEnhancedSynchronizedLyrics)
        {
            requested.Add("elrc");
        }
        if (requirements.WantsLrcLyrics)
        {
            requested.Add("lrc");
        }
        if (!requirements.WantsRichLyrics && requirements.WantsPlainLyrics)
        {
            requested.Add("txt");
        }

        return new LyricsResolutionPlan(requested, ResolveLyricsProviders(settings), requirements.WantsPlainLyrics);
    }

    private static LyricsResolutionResult EmptyResolution(LyricsResolutionPlan plan, string? error)
        => new(null, plan, Array.Empty<string>(), Array.Empty<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), error);

    private static LyricsResolutionResult BuildResolutionResult(
        LyricsResolutionState state,
        LyricsResolutionPlan plan,
        LyricsOutputRequirements requirements,
        string? error)
    {
        var resolved = new List<string>(4);
        var lyrics = state.ResolvedLyrics;
        var hasTtml = lyrics != null && AppleLyricsService.IsWordSyncedTtml(lyrics.TtmlLyrics);
        var hasEnhanced = lyrics?.HasEnhancedSynchronizedLyrics() == true;
        var hasLrc = lyrics?.CanSaveLrcSidecar() == true;
        if (requirements.WantsTtmlLyrics && hasTtml)
        {
            resolved.Add("ttml");
        }
        if (requirements.WantsEnhancedSynchronizedLyrics && hasEnhanced)
        {
            resolved.Add("elrc");
        }
        if (requirements.WantsLrcLyrics && hasLrc)
        {
            resolved.Add("lrc");
        }
        if (resolved.Count == 0 && requirements.WantsPlainLyrics && !string.IsNullOrWhiteSpace(lyrics?.UnsyncedLyrics))
        {
            resolved.Add("txt");
        }

        var sources = state.SourcesByFormat
            .Where(pair => resolved.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return new LyricsResolutionResult(
            lyrics,
            plan,
            state.ProvidersAttempted.ToArray(),
            resolved,
            sources,
            error);
    }

    private async Task<LyricsBase?> TryResolveProviderSafelyAsync(
        string provider,
        Track track,
        DeezSpoTagSettings settings,
        LyricsProviderOptions? providerOptions,
        LyricsResolutionState state,
        CancellationToken cancellationToken)
    {
        try
        {
            var lyrics = await TryResolveProviderLyricsAsync(
                provider,
                track,
                settings,
                providerOptions,
                state,
                cancellationToken);
            if (lyrics != null)
            {
                lyrics.ProviderId = provider;
                lyrics.NativeSourceFormat ??= ResolveNativeSourceFormat(lyrics);
            }
            return lyrics;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(
                ex,
                "Lyrics provider {Provider} timed out for track {TrackId}, advancing to next provider",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(provider),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(track.Id));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Lyrics provider {Provider} threw an exception for track {TrackId}, advancing to next provider",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(provider),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(track.Id));
            return null;
        }
    }

    private static string ResolveNativeSourceFormat(LyricsBase lyrics)
    {
        if (!string.IsNullOrWhiteSpace(lyrics.TtmlLyrics))
        {
            return "ttml";
        }
        if (lyrics.HasEnhancedSynchronizedLyrics())
        {
            return "word-synchronized";
        }
        if (lyrics.IsSynced())
        {
            return "line-synchronized";
        }
        return !string.IsNullOrWhiteSpace(lyrics.UnsyncedLyrics) ? "plain" : "unknown";
    }

    private readonly record struct LyricsOutputRequirements(
        bool WantsLrcLyrics,
        bool WantsEnhancedSynchronizedLyrics,
        bool WantsTtmlLyrics,
        bool WantsPlainLyrics)
    {
        public bool WantsRichLyrics => WantsLrcLyrics || WantsEnhancedSynchronizedLyrics || WantsTtmlLyrics;
    }

    private static bool ProviderCanContribute(
        string provider,
        LyricsOutputRequirements requirements,
        LyricsBase? resolved)
    {
        if (!LyricsProviderRegistry.TryGet(provider, out var descriptor))
        {
            return false;
        }

        var needsTtml = requirements.WantsTtmlLyrics
            && !AppleLyricsService.IsWordSyncedTtml(resolved?.TtmlLyrics);
        var needsEnhanced = requirements.WantsEnhancedSynchronizedLyrics
            && resolved?.HasEnhancedSynchronizedLyrics() != true;
        var needsLrc = requirements.WantsLrcLyrics
            && resolved?.CanSaveLrcSidecar() != true;
        var needsPlain = requirements.WantsPlainLyrics
            && string.IsNullOrWhiteSpace(resolved?.UnsyncedLyrics);

        return (needsTtml && (descriptor.SupportsNativeTtml || descriptor.SupportsWordSynchronized))
            || (needsEnhanced && descriptor.SupportsWordSynchronized)
            || (needsLrc && descriptor.SupportsLineSynchronized)
            || (needsPlain && descriptor.SupportsPlain);
    }

    private static bool ShouldReturnResolvedLyrics(
        LyricsResolutionState state,
        LyricsOutputRequirements requirements,
        bool requireAllRequestedRichLyrics)
    {
        var lyrics = state.ResolvedLyrics;
        if (lyrics == null)
        {
            return false;
        }

        var hasTtml = DeezSpoTag.Services.Apple.AppleLyricsService.IsWordSyncedTtml(lyrics.TtmlLyrics);
        var hasEnhanced = lyrics.HasEnhancedSynchronizedLyrics();
        var hasLrc = lyrics.CanSaveLrcSidecar();
        var hasDirectLrc = hasLrc
            && lyrics.SyncedLyricsSourceFormat != LyricsSourceFormat.ConvertedFromTtml;
        if (requireAllRequestedRichLyrics && requirements.WantsTtmlLyrics && !hasTtml)
        {
            return false;
        }

        if (requireAllRequestedRichLyrics && requirements.WantsEnhancedSynchronizedLyrics && !hasEnhanced)
        {
            return false;
        }

        if (requireAllRequestedRichLyrics && requirements.WantsLrcLyrics && !hasDirectLrc)
        {
            return false;
        }

        if (requirements.WantsRichLyrics)
        {
            return (requirements.WantsTtmlLyrics && hasTtml)
                || (requirements.WantsEnhancedSynchronizedLyrics && hasEnhanced)
                || (requirements.WantsLrcLyrics && hasLrc);
        }

        if (requirements.WantsPlainLyrics && string.IsNullOrWhiteSpace(lyrics.UnsyncedLyrics))
        {
            return false;
        }

        return true;
    }

    private async Task<LyricsBase?> TryResolveProviderLyricsAsync(
        string provider,
        Track track,
        DeezSpoTagSettings settings,
        LyricsProviderOptions? providerOptions,
        LyricsResolutionState state,
        CancellationToken cancellationToken)
    {
        return provider switch
        {
            AppleProvider => await ResolveCachedProviderLyricsAsync(
                AppleProvider,
                track,
                settings,
                () => ResolveAppleProviderLyricsAsync(track, settings, cancellationToken)),
            DeezerProvider => await ResolveCachedProviderLyricsAsync(
                DeezerProvider,
                track,
                settings,
                () => TryResolveDeezerProviderLyricsAsync(track, settings, state, cancellationToken)),
            SpotifyProvider => await ResolveCachedProviderLyricsAsync(
                SpotifyProvider,
                track,
                settings,
                () => ResolveLoadedLyricsOrNullAsync(
                    () => ResolveSpotifyLyricsAsync(track, settings, cancellationToken))),
            LrclibProvider => await ResolveCachedProviderLyricsAsync(
                LrclibProvider,
                track,
                settings,
                () => ResolveLoadedLyricsOrNullAsync(
                    () => _lrclibLyricsService.ResolveLyricsAsync(
                        track,
                        BuildLrclibRequestOptions(providerOptions?.Lrclib),
                        cancellationToken))),
            MusixmatchProvider => await ResolveCachedProviderLyricsAsync(
                MusixmatchProvider,
                track,
                settings,
                () => ResolveLoadedLyricsOrNullAsync(
                    () => ResolveMusixmatchLyricsAsync(track, cancellationToken))),
            YouLyPlusProvider => await ResolveCachedProviderLyricsAsync(
                YouLyPlusProvider,
                track,
                settings,
                () => ResolveYouLyPlusLyricsAsync(track, settings, cancellationToken)),
            BetterLyricsProvider => await ResolveCachedProviderLyricsAsync(
                BetterLyricsProvider,
                track,
                settings,
                () => ResolveBetterLyricsAsync(track, settings, cancellationToken)),
            _ => LogUnknownLyricsProvider(provider)
        };
    }

    private static async Task<LyricsBase?> ResolveLoadedLyricsOrNullAsync(Func<Task<LyricsBase>> resolver)
    {
        LyricsBase? lyrics = await resolver();
        if (lyrics is null)
        {
            return null;
        }

        return lyrics.IsLoaded() ? lyrics : null;
    }

    private LyricsBase? LogUnknownLyricsProvider(string provider)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Unknown lyrics provider {Provider} configured in fallback order",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(provider));
        }
        return null;
    }

    private async Task<LyricsBase?> TryResolveDeezerProviderLyricsAsync(
        Track track,
        DeezSpoTagSettings settings,
        LyricsResolutionState state,
        CancellationToken cancellationToken)
    {
        state.DeezerAttempted = true;
        var deezerTrackId = ResolveDeezerLyricsTrackId(track);
        if (string.IsNullOrWhiteSpace(deezerTrackId))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Skipping Deezer lyrics lookup because no Deezer track id could be resolved for track {TrackId}",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(track.Id));
            }
            return null;
        }

        state.Arl ??= await _authenticatedDeezerService.GetArlAsync();
        if (string.IsNullOrEmpty(state.Arl))
        {
            state.DeezerMissingAuth = true;
            _logger.LogWarning(
                "No ARL available for Deezer lyrics fetch for Deezer track id {DeezerTrackId}",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(deezerTrackId));
            return null;
        }

        var sid = await _authenticatedDeezerService.GetSidAsync();
        var deezerLyrics = await GetLyricsAsync(deezerTrackId, state.Arl, sid, cancellationToken);
        return deezerLyrics.IsLoaded() ? deezerLyrics : null;
    }

    private static void MergeProviderLyrics(LyricsResolutionState state, LyricsBase providerLyrics, string provider)
    {
        if (AppleLyricsService.IsWordSyncedTtml(providerLyrics.TtmlLyrics))
        {
            state.SourcesByFormat.TryAdd("ttml", provider);
        }
        if (providerLyrics.HasEnhancedSynchronizedLyrics())
        {
            state.SourcesByFormat.TryAdd("elrc", provider);
        }
        if (providerLyrics.CanSaveLrcSidecar())
        {
            if (providerLyrics.SyncedLyricsSourceFormat == LyricsSourceFormat.ConvertedFromTtml)
            {
                state.SourcesByFormat.TryAdd("lrc", provider);
            }
            else
            {
                state.SourcesByFormat["lrc"] = provider;
            }
        }
        if (!string.IsNullOrWhiteSpace(providerLyrics.UnsyncedLyrics))
        {
            state.SourcesByFormat.TryAdd("txt", provider);
        }

        if (DeezSpoTag.Services.Apple.AppleLyricsService.IsWordSyncedTtml(providerLyrics.TtmlLyrics))
        {
            state.TtmlFallback = providerLyrics.TtmlLyrics;
        }

        if (!string.IsNullOrWhiteSpace(state.TtmlFallback) && string.IsNullOrWhiteSpace(providerLyrics.TtmlLyrics))
        {
            providerLyrics.TtmlLyrics = state.TtmlFallback;
            providerLyrics.TtmlLyricsSourceFormat = LyricsSourceFormat.DownloadedTtml;
        }

        if (state.ResolvedLyrics == null)
        {
            state.ResolvedLyrics = providerLyrics;
            return;
        }

        MergeLyricsData(state.ResolvedLyrics, providerLyrics);
    }

    private static LyricsOutputRequirements ResolveOutputRequirements(DeezSpoTagSettings settings)
    {
        var selectedTypes = ParseSelectedLyricsTypes(settings);
        var wantsTimedLyrics = settings.SyncedLyrics
            && (selectedTypes.Contains(LyricsType)
                || selectedTypes.Contains(SyllableLyricsType)
                || selectedTypes.Contains(TtmlLyricsType));
        var outputFormats = ParseLyricsOutputFormats(settings.LrcFormat);
        var wantsLrcLyrics = wantsTimedLyrics && outputFormats.Contains("lrc");
        var wantsEnhancedSynchronizedLyrics = wantsTimedLyrics && outputFormats.Contains("elrc");
        var wantsTtmlLyrics = settings.SyncedLyrics
            && selectedTypes.Contains(TtmlLyricsType)
            && outputFormats.Contains("ttml");
        var wantsPlainLyrics = settings.SaveLyrics
            && selectedTypes.Contains(UnsyncedLyricsType);
        return new LyricsOutputRequirements(wantsLrcLyrics, wantsEnhancedSynchronizedLyrics, wantsTtmlLyrics, wantsPlainLyrics);
    }

    private static LrclibLyricsService.LrclibRequestOptions? BuildLrclibRequestOptions(
        LrclibLyricsProviderOptions? options)
    {
        if (options == null)
        {
            return null;
        }

        return new LrclibLyricsService.LrclibRequestOptions
        {
            DurationToleranceSeconds = options.DurationToleranceSeconds ?? 10,
            UseDurationHint = options.UseDurationHint ?? true,
            SearchFallback = options.SearchFallback ?? true,
            PreferSynced = options.PreferSynced ?? true
        };
    }

    private static void MergeLyricsData(LyricsBase target, LyricsBase candidate)
    {
        if (target == null || candidate == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(target.TtmlLyrics) && !string.IsNullOrWhiteSpace(candidate.TtmlLyrics))
        {
            target.TtmlLyrics = candidate.TtmlLyrics;
            target.TtmlLyricsSourceFormat = candidate.TtmlLyricsSourceFormat;
        }

        if ((!HasLyricsLines(target.SyncedLyrics)
             || (target.SyncedLyricsSourceFormat == LyricsSourceFormat.ConvertedFromTtml
                 && candidate.SyncedLyricsSourceFormat != LyricsSourceFormat.ConvertedFromTtml))
            && HasLyricsLines(candidate.SyncedLyrics))
        {
            target.SyncedLyrics = candidate.SyncedLyrics;
            target.SyncedLyricsSourceFormat = candidate.SyncedLyricsSourceFormat;
        }
        else if (!target.HasEnhancedSynchronizedLyrics() && candidate.HasEnhancedSynchronizedLyrics())
        {
            target.SyncedLyrics = candidate.SyncedLyrics;
            target.SyncedLyricsSourceFormat = candidate.SyncedLyricsSourceFormat;
        }

        if (string.IsNullOrWhiteSpace(target.UnsyncedLyrics) && !string.IsNullOrWhiteSpace(candidate.UnsyncedLyrics))
        {
            target.UnsyncedLyrics = candidate.UnsyncedLyrics;
            target.UnsyncedLyricsSourceFormat = candidate.UnsyncedLyricsSourceFormat;
        }

        if (string.IsNullOrWhiteSpace(target.Writers) && !string.IsNullOrWhiteSpace(candidate.Writers))
        {
            target.Writers = candidate.Writers;
        }

        if (string.IsNullOrWhiteSpace(target.Copyright) && !string.IsNullOrWhiteSpace(candidate.Copyright))
        {
            target.Copyright = candidate.Copyright;
        }
    }

    private static bool HasLyricsLines(List<SynchronizedLyric>? lyricsLines)
    {
        return lyricsLines != null && lyricsLines.Count > 0;
    }

    private static List<string> ResolveLyricsProviders(DeezSpoTagSettings settings)
    {
        return ProviderOrderResolver.Resolve(
            settings.LyricsFallbackEnabled,
            settings.LyricsFallbackOrder,
            DefaultLyricsProviderOrder,
            NormalizeLyricsProviderToken);
    }

    private static string NormalizeLyricsProviderToken(string? provider)
        => LyricsProviderRegistry.TryNormalize(provider, out var normalized)
            ? normalized
            : (provider ?? string.Empty).Trim().ToLowerInvariant();

    private async Task<LyricsBase?> ResolveAppleProviderLyricsAsync(
        Track track,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var appleId = ResolveAppleLyricsTrackId(track);
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return null;
        }

        LyricsBase? appleLyrics = null;
        try
        {
            appleLyrics = await ResolveLoadedLyricsOrNullAsync(
                () => _appleLyricsService.ResolveLyricsAsync(appleId, settings, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Authenticated Apple lyrics lookup failed for track {TrackId}, trying public fallback",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(track.Id));
        }

        var requirements = ResolveOutputRequirements(settings);
        var appleHasRequestedWordTtml = AppleLyricsService.IsWordSyncedTtml(appleLyrics?.TtmlLyrics);
        if (appleLyrics?.IsLoaded() == true
            && (!requirements.WantsTtmlLyrics || appleHasRequestedWordTtml))
        {
            return appleLyrics;
        }

        var fallbackLyrics = await ResolvePaxsenixAppleLyricsByIdAsync(track, settings, cancellationToken);
        if (fallbackLyrics?.IsLoaded() == true)
        {
            if (appleLyrics?.IsLoaded() == true)
            {
                MergeLyricsData(appleLyrics, fallbackLyrics);
                return appleLyrics;
            }
            return fallbackLyrics;
        }

        return appleLyrics;
    }

    private static string? ResolveAppleLyricsTrackId(Track track)
    {
        var candidate = FirstNonEmpty(
            TryGetTrackUrl(track, "apple_track_id"),
            TryGetTrackUrl(track, "apple_id"),
            TryGetTrackUrl(track, AppleProvider),
            string.Equals(track.Source, AppleProvider, StringComparison.OrdinalIgnoreCase) ? track.SourceId : null);
        return AppleIdParser.Resolve(candidate, candidate);
    }

    private async Task<LyricsBase?> ResolvePaxsenixAppleLyricsByIdAsync(
        Track track,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var appleId = ResolvePaxsenixAppleTrackId(track);
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return null;
        }

        var url = $"{PaxsenixBaseUrl}/apple-music/lyrics?id={Uri.EscapeDataString(appleId)}&ttml=true";
        var body = await FetchPaxsenixLyricsBodyAsync(url, AppleProvider, "Apple Music", cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        if (LooksLikeRawTtml(body))
        {
            return BuildLyricsFromAppleTtml(body, settings);
        }

        if (DeezSpoTag.Services.Apple.AppleLyricsService.TryExtractPlainLyrics(body, out var plainBody))
        {
            return new LyricsSource
            {
                UnsyncedLyrics = plainBody,
                UnsyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedPlainText
            };
        }

        try
        {
            using var payload = JsonDocument.Parse(body);
            var lyrics = ParsePaxsenixLyricsPayload(payload.RootElement, settings);
            return lyrics.IsLoaded() ? lyrics : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ResolvePaxsenixAppleTrackId(Track track)
    {
        if (string.Equals(track.Source, AppleProvider, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(track.SourceId)
            && long.TryParse(track.SourceId.Trim(), out _))
        {
            return track.SourceId.Trim();
        }

        foreach (var value in EnumerateAppleIdentityCandidates(track))
        {
            var resolved = AppleIdParser.Resolve(value, value);
            if (!string.IsNullOrWhiteSpace(resolved)
                && long.TryParse(resolved, out _))
            {
                return resolved;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateAppleIdentityCandidates(Track track)
    {
        if (!string.IsNullOrWhiteSpace(track.DownloadURL))
        {
            yield return track.DownloadURL;
        }

        if (track.Urls is not { Count: > 0 })
        {
            yield break;
        }

        foreach (var key in new[] { "apple_track_id", "apple_id", "appleid", "apple", "apple_url", "source_url" })
        {
            if (track.Urls.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    private async Task<string?> FetchPaxsenixLyricsBodyAsync(
        string url,
        string source,
        string sourceName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(LyricsClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(UserAgentHeader, DefaultSpotifyWebPlayerUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", $"{ApplicationJson}, text/plain, application/xml, text/xml");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Paxsenix {Source} lyrics fallback request failed with status {StatusCode}", sourceName, response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Paxsenix {Source} lyrics fallback request failed.", sourceName);
            return null;
        }
    }

    private static LyricsBase ParsePaxsenixLyricsPayload(
        JsonElement root,
        DeezSpoTagSettings? settings = null)
    {
        var lyrics = new LyricsSource();
        if (TryFindStringByName(root, IsTtmlPropertyName, out var ttml)
            && LooksLikeTtml(ttml))
        {
            MergeLyricsData(lyrics, BuildLyricsFromAppleTtml(ttml, settings));
        }
        else if (!string.IsNullOrWhiteSpace(ttml)
                 && DeezSpoTag.Services.Apple.AppleLyricsService.TryExtractPlainLyrics(ttml, out var plainTtml))
        {
            lyrics.UnsyncedLyrics = plainTtml;
            lyrics.UnsyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedPlainText;
        }

        if (TryFindStringByName(root, IsSyncedLyricsPropertyName, out var syncedText))
        {
            lyrics.SyncedLyrics = ParseLrcLines(syncedText);
            if (HasLyricsLines(lyrics.SyncedLyrics))
            {
                lyrics.SyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedLrc;
            }
        }

        if (TryFindStringByName(root, IsPlainLyricsPropertyName, out var plainText))
        {
            lyrics.UnsyncedLyrics = plainText;
            lyrics.UnsyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedPlainText;
        }

        if (string.IsNullOrWhiteSpace(lyrics.UnsyncedLyrics)
            && TryFindStringArrayByName(root, IsPlainLyricsPropertyName, out var plainLines))
        {
            lyrics.UnsyncedLyrics = string.Join('\n', plainLines);
            lyrics.UnsyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedPlainText;
        }

        return lyrics;
    }

    private static LyricsBase BuildLyricsFromAppleTtml(
        string ttml,
        DeezSpoTagSettings? settings)
    {
        var lyrics = new LyricsSource();
        var kind = AppleLyricsService.ClassifyTtml(ttml);
        if (kind == AppleTtmlTimingKind.Word)
        {
            lyrics.TtmlLyrics = ttml;
            lyrics.TtmlLyricsSourceFormat = LyricsSourceFormat.DownloadedTtml;
        }

        if ((kind == AppleTtmlTimingKind.Word || settings?.SynthesizeLrcFromTtml == true)
            && kind is AppleTtmlTimingKind.Line or AppleTtmlTimingKind.Word
            && AppleLyricsService.TryConvertTtmlToSynchronizedLyrics(ttml, out var synchronizedLyrics))
        {
            lyrics.SyncedLyrics = synchronizedLyrics;
            lyrics.SyncedLyricsSourceFormat = LyricsSourceFormat.ConvertedFromTtml;
        }

        if (kind == AppleTtmlTimingKind.Untimed
            && AppleLyricsService.TryExtractPlainLyrics(ttml, out var plainLyrics))
        {
            lyrics.UnsyncedLyrics = plainLyrics;
            lyrics.UnsyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedPlainText;
        }

        return lyrics;
    }

    private static bool TryFindStringByName(JsonElement element, Func<string, bool> namePredicate, out string value)
    {
        value = string.Empty;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (namePredicate(property.Name) && property.Value.ValueKind == JsonValueKind.String)
                    {
                        value = property.Value.GetString() ?? string.Empty;
                        return !string.IsNullOrWhiteSpace(value);
                    }
                    if (TryFindStringByName(property.Value, namePredicate, out value))
                    {
                        return true;
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindStringByName(item, namePredicate, out value))
                    {
                        return true;
                    }
                }
                break;
        }

        return false;
    }

    private static bool TryFindStringArrayByName(JsonElement element, Func<string, bool> namePredicate, out List<string> values)
    {
        values = new List<string>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (namePredicate(property.Name) && property.Value.ValueKind == JsonValueKind.Array)
                {
                    var lines = property.Value.EnumerateArray()
                        .Where(static item => item.ValueKind == JsonValueKind.String)
                        .Select(static item => item.GetString())
                        .Where(static line => !string.IsNullOrWhiteSpace(line))
                        .Cast<string>()
                        .ToList();
                    if (lines.Count > 0)
                    {
                        values = lines;
                        return true;
                    }
                }
                if (TryFindStringArrayByName(property.Value, namePredicate, out values))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindStringArrayByName(item, namePredicate, out values))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsTtmlPropertyName(string name)
    {
        var normalized = NormalizeJsonName(name);
        return normalized is "ttml" or "ttmllyrics" or "ttmlpayload" or "applettml" or "content";
    }

    private static bool IsSyncedLyricsPropertyName(string name)
    {
        var normalized = NormalizeJsonName(name);
        return normalized is "syncedlyrics" or "synced" or "lrc" or "lrclib" or "richsync";
    }

    private static bool IsPlainLyricsPropertyName(string name)
    {
        var normalized = NormalizeJsonName(name);
        return normalized is "lyrics" or "plainlyrics" or "unsyncedlyrics" or "unsynced" or "text";
    }

    private static string NormalizeJsonName(string name)
        => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool LooksLikeTtml(string value)
        => DeezSpoTag.Services.Apple.AppleLyricsService.IsTimedTtml(value);

    private static bool LooksLikeRawTtml(string value)
    {
        var trimmed = value.TrimStart();
        return LooksLikeTtml(value)
               && (trimmed.StartsWith("<", StringComparison.Ordinal)
                   || trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase));
    }

    private static List<SynchronizedLyric> ParseLrcLines(string value)
    {
        var lines = new List<SynchronizedLyric>();
        foreach (var rawLine in value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Regex.Match(rawLine, @"^\[(?<min>\d{1,2}):(?<sec>\d{2})(?:[.:](?<frac>\d{1,3}))?\](?<text>.*)$", RegexOptions.None, TimeSpan.FromMilliseconds(200));
            if (!match.Success)
            {
                continue;
            }

            var minutes = int.Parse(match.Groups["min"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var seconds = int.Parse(match.Groups["sec"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var fraction = match.Groups["frac"].Success ? match.Groups["frac"].Value : "0";
            var milliseconds = fraction.Length switch
            {
                1 => int.Parse(fraction, System.Globalization.CultureInfo.InvariantCulture) * 100,
                2 => int.Parse(fraction, System.Globalization.CultureInfo.InvariantCulture) * 10,
                _ => int.Parse(fraction[..Math.Min(3, fraction.Length)], System.Globalization.CultureInfo.InvariantCulture)
            };
            var offsetMs = (minutes * 60 + seconds) * 1000 + milliseconds;
            var text = match.Groups["text"].Value.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            lines.Add(new SynchronizedLyric(text, SynchronizedLyric.BuildLrcTimestamp(offsetMs), offsetMs));
        }

        return lines;
    }

    private async Task<LyricsBase?> ResolveCachedProviderLyricsAsync(
        string provider,
        Track track,
        DeezSpoTagSettings settings,
        Func<Task<LyricsBase?>> resolver)
    {
        var cacheKey = BuildProviderCacheKey(provider, track, settings);
        if (ProviderResultCache.TryGetValue(cacheKey, out var cached)
            && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return CloneLyrics(cached.Lyrics);
        }

        ProviderResultCache.TryRemove(cacheKey, out _);
        var allowsNegativeCaching = provider is YouLyPlusProvider or BetterLyricsProvider;
        if (allowsNegativeCaching
            && ProviderNegativeCache.TryGetValue(cacheKey, out var negativeExpiry)
            && negativeExpiry > DateTimeOffset.UtcNow)
        {
            return null;
        }
        ProviderNegativeCache.TryRemove(cacheKey, out _);
        var persisted = await TryReadPersistedLyricsCacheAsync(cacheKey);
        if (persisted?.Version == ProviderCacheVersion
            && persisted.ExpiresAt > DateTimeOffset.UtcNow
            && persisted.IsNegative
            && allowsNegativeCaching)
        {
            ProviderNegativeCache[cacheKey] = persisted.ExpiresAt;
            return null;
        }
        if (persisted?.Version == ProviderCacheVersion
            && persisted.ExpiresAt > DateTimeOffset.UtcNow
            && persisted.Lyrics?.IsLoaded() == true)
        {
            ProviderResultCache[cacheKey] = new CachedLyricsResult(
                persisted.ExpiresAt,
                CloneLyrics(persisted.Lyrics));
            ProviderNegativeCache.TryRemove(cacheKey, out _);
            return CloneLyrics(persisted.Lyrics);
        }

        var lyrics = await resolver();
        if (lyrics?.IsLoaded() == true)
        {
            var expiresAt = DateTimeOffset.UtcNow.Add(ProviderResultCacheLifetime);
            var cachedLyrics = CloneLyrics(lyrics);
            ProviderResultCache[cacheKey] = new CachedLyricsResult(expiresAt, cachedLyrics);
            ProviderNegativeCache.TryRemove(cacheKey, out _);
            await TryWritePersistedLyricsCacheAsync(
                cacheKey,
                new PersistedLyricsCacheEntry(
                    ProviderCacheVersion,
                    expiresAt,
                    false,
                    (LyricsSource)CloneLyrics(cachedLyrics)));
        }
        else if (allowsNegativeCaching)
        {
            var expiresAt = DateTimeOffset.UtcNow.Add(ProviderNegativeCacheLifetime);
            ProviderNegativeCache[cacheKey] = expiresAt;
            await TryWritePersistedLyricsCacheAsync(
                cacheKey,
                new PersistedLyricsCacheEntry(ProviderCacheVersion, expiresAt, true, null));
        }
        return lyrics;
    }

    private static async Task<PersistedLyricsCacheEntry?> TryReadPersistedLyricsCacheAsync(string cacheKey)
    {
        var path = TryResolveProviderCachePath(cacheKey);
        if (path == null || !File.Exists(path))
        {
            return null;
        }

        await ProviderDiskCacheGate.WaitAsync();
        try
        {
            await using var stream = File.OpenRead(path);
            var entry = await JsonSerializer.DeserializeAsync<PersistedLyricsCacheEntry>(stream);
            if (entry?.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                File.Delete(path);
                return null;
            }
            return entry;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return null;
        }
        finally
        {
            ProviderDiskCacheGate.Release();
        }
    }

    private static async Task TryWritePersistedLyricsCacheAsync(
        string cacheKey,
        PersistedLyricsCacheEntry entry)
    {
        var path = TryResolveProviderCachePath(cacheKey);
        if (path == null)
        {
            return;
        }

        await ProviderDiskCacheGate.WaitAsync();
        var temporaryPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, entry);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Cache writes are best effort and must not fail lyrics lookup.
            }
        }
        finally
        {
            ProviderDiskCacheGate.Release();
        }
    }

    private static string? TryResolveProviderCachePath(string cacheKey)
    {
        try
        {
            var root = AppDataPathResolver.GetDefaultWorkersDataDir();
            return Path.Join(root, "cache", "lyrics", $"{cacheKey}.json");
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return null;
        }
    }

    private static string BuildProviderCacheKey(
        string provider,
        Track track,
        DeezSpoTagSettings settings)
    {
        var identity = string.Join(
            '\u001f',
            provider,
            track.ISRC?.Trim().ToUpperInvariant(),
            track.Title?.Trim().ToUpperInvariant(),
            ResolveMusixmatchArtist(track).Trim().ToUpperInvariant(),
            track.Album?.Title?.Trim().ToUpperInvariant(),
            track.Duration.ToString(CultureInfo.InvariantCulture),
            ProviderCacheVersion,
            string.Join(",", DescribeResolutionPlan(settings).RequestedFormats),
            settings.SynthesizeLrcFromTtml.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static LyricsBase CloneLyrics(LyricsBase source)
    {
        return new LyricsSource
        {
            Id = source.Id,
            Writers = source.Writers,
            Copyright = source.Copyright,
            UnsyncedLyrics = source.UnsyncedLyrics,
            UnsyncedLyricsSourceFormat = source.UnsyncedLyricsSourceFormat,
            SyncedLyrics = source.SyncedLyrics?
                .Select(line => new SynchronizedLyric(
                    line.Text,
                    line.LrcTimestamp,
                    line.Milliseconds,
                    line.Duration)
                {
                    Agent = line.Agent,
                    IsBackground = line.IsBackground,
                    Translation = line.Translation,
                    Romanization = line.Romanization,
                    BackgroundVocals = line.BackgroundVocals,
                    Words = line.Words?
                        .Select(word => new SynchronizedLyricWord(
                            word.Text,
                            word.StartMilliseconds,
                            word.EndMilliseconds)
                        {
                            IsBackground = word.IsBackground
                        })
                        .ToList()
                })
                .ToList(),
            SyncedLyricsSourceFormat = source.SyncedLyricsSourceFormat,
            TtmlLyrics = source.TtmlLyrics,
            TtmlLyricsSourceFormat = source.TtmlLyricsSourceFormat,
            ProviderId = source.ProviderId,
            NativeSourceFormat = source.NativeSourceFormat,
            SourcePayloadHash = source.SourcePayloadHash,
            IsExplicit = source.IsExplicit
        };
    }

    private async Task<LyricsBase?> ResolveBetterLyricsAsync(
        Track track,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var artist = ResolveMusixmatchArtist(track);
        if (string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildLyricsProviderUri(
                "https://lyrics-api.boidu.dev/getLyrics",
                track,
                artist,
                ("s", track.Title),
                ("a", artist),
                ("d", track.Duration > 0 ? track.Duration.ToString(CultureInfo.InvariantCulture) : null),
                ("al", track.Album?.Title)));
        using var response = await _httpClientFactory.CreateClient(LyricsClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        if (!TryFindStringByName(document.RootElement, IsTtmlPropertyName, out var ttml)
            || !LooksLikeRawTtml(ttml))
        {
            return null;
        }

        var lyrics = BuildLyricsFromAppleTtml(ttml, settings);
        lyrics.SourcePayloadHash = ComputePayloadHash(body);
        return lyrics;
    }

    private async Task<LyricsBase?> ResolveYouLyPlusLyricsAsync(
        Track track,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var artist = ResolveMusixmatchArtist(track);
        if (string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCancellation.CancelAfter(TimeSpan.FromSeconds(10));
        var tasks = YouLyPlusServers
            .Select(server => ResolveYouLyPlusMirrorAsync(server, track, artist, settings, linkedCancellation.Token))
            .ToList();

        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks);
            tasks.Remove(completed);
            LyricsBase? result = null;
            try
            {
                result = await completed;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Another mirror may still produce a valid result.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "YouLy+ mirror request failed");
            }

            if (result?.IsLoaded() == true)
            {
                await linkedCancellation.CancelAsync();
                return result;
            }
        }

        return null;
    }

    private async Task<LyricsBase?> ResolveYouLyPlusMirrorAsync(
        string server,
        Track track,
        string artist,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var uri = BuildLyricsProviderUri(
            $"{server}/v2/lyrics/get",
            track,
            artist,
            ("title", track.Title),
            ("artist", artist),
            ("duration", track.Duration > 0 ? track.Duration.ToString(CultureInfo.InvariantCulture) : null),
            ("album", track.Album?.Title),
            ("id", string.IsNullOrWhiteSpace(track.Id) ? null : track.Id),
            ("isrc", string.IsNullOrWhiteSpace(track.ISRC) ? null : track.ISRC));
        using var response = await _httpClientFactory.CreateClient(LyricsClientName)
            .GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var lyrics = ParseYouLyPlusPayload(document.RootElement, track, artist, settings);
        if (lyrics != null)
        {
            lyrics.SourcePayloadHash = ComputePayloadHash(body);
        }
        return lyrics;
    }

    private static Uri BuildLyricsProviderUri(
        string baseUrl,
        Track track,
        string artist,
        params (string Name, string? Value)[] parameters)
    {
        var query = string.Join(
            "&",
            parameters
                .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
                .Select(parameter =>
                    $"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(parameter.Value!)}"));
        return new Uri($"{baseUrl}?{query}", UriKind.Absolute);
    }

    private static LyricsBase? ParseYouLyPlusPayload(
        JsonElement root,
        Track track,
        string artist,
        DeezSpoTagSettings settings)
    {
        if (!YouLyPlusIdentityMatches(root, track, artist))
        {
            return null;
        }

        var result = new LyricsSource();
        if (root.TryGetProperty("lyrics", out var lyricsElement)
            && lyricsElement.ValueKind == JsonValueKind.Array)
        {
            result.SyncedLyrics = ParseYouLyPlusLines(lyricsElement);
            if (result.SyncedLyrics.Count > 0)
            {
                result.SyncedLyricsSourceFormat = LyricsSourceFormat.ProviderSyncedJson;
                if (result.HasEnhancedSynchronizedLyrics()
                    && settings.SyncedLyrics
                    && ParseLyricsOutputFormats(settings.LrcFormat).Contains("ttml"))
                {
                    result.TtmlLyrics = BuildWordSynchronizedTtml(result.SyncedLyrics);
                    result.TtmlLyricsSourceFormat = LyricsSourceFormat.DownloadedTtml;
                }
            }
        }

        if (!result.IsSynced()
            && root.TryGetProperty("syncedLyrics", out var synced)
            && synced.ValueKind == JsonValueKind.String)
        {
            result.SyncedLyrics = ParseLrcLines(synced.GetString() ?? string.Empty);
            result.SyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedLrc;
        }
        if (root.TryGetProperty("plainLyrics", out var plain)
            && plain.ValueKind == JsonValueKind.String)
        {
            result.UnsyncedLyrics = plain.GetString();
            result.UnsyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedPlainText;
        }
        return result.IsLoaded() ? result : null;
    }

    private static bool YouLyPlusIdentityMatches(JsonElement root, Track track, string artist)
    {
        var titleMatches = root.TryGetProperty("trackName", out var returnedTitle)
            && returnedTitle.ValueKind == JsonValueKind.String
            && MetadataTextMatches(track.Title, returnedTitle.GetString());
        var artistMatches = root.TryGetProperty("artistName", out var returnedArtist)
            && returnedArtist.ValueKind == JsonValueKind.String
            && MetadataTextMatches(artist, returnedArtist.GetString());
        var durationMatches = !root.TryGetProperty("duration", out var returnedDuration)
            || !returnedDuration.TryGetDouble(out var duration)
            || track.Duration <= 0
            || Math.Abs(duration - track.Duration) <= 10;
        return titleMatches && artistMatches && durationMatches;
    }

    private static bool MetadataTextMatches(string? expected, string? actual)
    {
        static string Normalize(string? value)
            => Regex.Replace(
                (value ?? string.Empty).ToLowerInvariant(),
                @"[^\p{L}\p{N}]+",
                " ",
                RegexOptions.None,
                TimeSpan.FromMilliseconds(200)).Trim();
        var left = Normalize(expected);
        var right = Normalize(actual);
        return left.Length > 0 && right.Length > 0
            && string.Equals(left, right, StringComparison.Ordinal);
    }

    private static List<SynchronizedLyric> ParseYouLyPlusLines(JsonElement lyricsElement)
    {
        var lines = new List<SynchronizedLyric>();
        foreach (var item in lyricsElement.EnumerateArray())
        {
            if (!item.TryGetProperty("time", out var timeElement)
                || !timeElement.TryGetInt64(out var time)
                || time < 0
                || time > int.MaxValue)
            {
                continue;
            }

            var duration = item.TryGetProperty("duration", out var durationElement)
                && durationElement.TryGetInt32(out var parsedDuration)
                ? Math.Max(0, parsedDuration)
                : 0;
            var words = new List<SynchronizedLyricWord>();
            if (item.TryGetProperty("syllabus", out var syllables)
                && syllables.ValueKind == JsonValueKind.Array)
            {
                foreach (var syllable in syllables.EnumerateArray())
                {
                    var text = syllable.TryGetProperty("text", out var wordText)
                        ? wordText.GetString()
                        : null;
                    if (!syllable.TryGetProperty("time", out var wordTime)
                        || !wordTime.TryGetInt32(out var start)
                        || !syllable.TryGetProperty("duration", out var wordDuration)
                        || !wordDuration.TryGetInt32(out var length)
                        || string.IsNullOrEmpty(text)
                        || length <= 0)
                    {
                        continue;
                    }
                    words.Add(new SynchronizedLyricWord(text, start, checked(start + length))
                    {
                        IsBackground = syllable.TryGetProperty("isBackground", out var background)
                            && background.ValueKind == JsonValueKind.True
                    });
                }
            }

            var lineText = item.TryGetProperty("text", out var textElement)
                ? textElement.GetString()
                : null;
            lineText ??= string.Concat(words.Select(word => word.Text));
            if (string.IsNullOrWhiteSpace(lineText))
            {
                continue;
            }
            lines.Add(new SynchronizedLyric(
                lineText,
                SynchronizedLyric.BuildLrcTimestamp((int)time),
                (int)time,
                duration)
            {
                IsBackground = words.Any(word => word.IsBackground),
                Words = words.Count > 0 ? words : null
            });
        }
        return lines;
    }

    private static string BuildWordSynchronizedTtml(IReadOnlyList<SynchronizedLyric> lines)
    {
        var builder = new StringBuilder(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div>");
        foreach (var line in lines.Where(line => line.Words?.Any(word => word.IsValid()) == true))
        {
            var end = line.Duration > 0
                ? line.Milliseconds + line.Duration
                : line.Words!.Max(word => word.EndMilliseconds);
            builder.Append("<p begin=\"")
                .Append(FormatTtmlTime(line.Milliseconds))
                .Append("\" end=\"")
                .Append(FormatTtmlTime(end))
                .Append("\">");
            foreach (var word in line.Words!.Where(word => word.IsValid()))
            {
                builder.Append("<span begin=\"")
                    .Append(FormatTtmlTime(word.StartMilliseconds))
                    .Append("\" end=\"")
                    .Append(FormatTtmlTime(word.EndMilliseconds))
                    .Append("\">")
                    .Append(XmlEscape(word.Text!))
                    .Append("</span>");
            }
            builder.Append("</p>");
        }
        builder.Append("</div></body></tt>");
        return builder.ToString();
    }

    private static string FormatTtmlTime(int milliseconds)
        => TimeSpan.FromMilliseconds(Math.Max(0, milliseconds))
            .ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    private static string XmlEscape(string value)
        => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private static string ComputePayloadHash(string payload)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private async Task<LyricsBase> ResolveMusixmatchLyricsAsync(Track track, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Title))
        {
            return LyricsNew.CreateError("Track title is required for Musixmatch lyrics");
        }

        var artist = ResolveMusixmatchArtist(track);
        if (string.IsNullOrWhiteSpace(artist))
        {
            return LyricsNew.CreateError("Track artist is required for Musixmatch lyrics");
        }

        var body = await FetchMusixmatchLyricsPayloadAsync(track, track.Title, artist, cancellationToken);
        if (body == null)
        {
            return LyricsNew.CreateError("No Musixmatch lyrics payload");
        }

        var validation = ValidateMusixmatchPayload(track, body);
        if (!validation.IsMatch)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Rejected Musixmatch lyrics candidate for track {TrackId}: {Reason}",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(track.Id),
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(validation.Reason));
            }

            return LyricsNew.CreateError($"Musixmatch lyrics identity rejected: {validation.Reason}");
        }

        var output = new LyricsSource();
        if (TryReadMusixmatchRichsync(body, out var richsyncLines) && richsyncLines.Count > 0)
        {
            output.SyncedLyrics = richsyncLines;
            output.SyncedLyricsSourceFormat = LyricsSourceFormat.ProviderSyncedJson;
            return output;
        }

        if (TryReadMusixmatchSubtitles(body, out var subtitleLines) && subtitleLines.Count > 0)
        {
            output.SyncedLyrics = subtitleLines;
            output.SyncedLyricsSourceFormat = LyricsSourceFormat.ProviderSyncedJson;
            return output;
        }

        if (TryReadMusixmatchUnsynced(body, out var unsyncedLyrics))
        {
            output.UnsyncedLyrics = unsyncedLyrics;
            output.UnsyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedPlainText;
            return output;
        }

        return LyricsNew.CreateError("No lyrics available from Musixmatch");
    }

    private static string ResolveMusixmatchArtist(Track track)
    {
        if (track.MainArtist is { Name: { Length: > 0 } mainArtistName } && !string.IsNullOrWhiteSpace(mainArtistName))
        {
            return mainArtistName;
        }

        if (track.Artists?.Count > 0)
        {
            return string.Join(", ", track.Artists.Where(static name => !string.IsNullOrWhiteSpace(name)));
        }

        if (track.Artist.TryGetValue("Main", out var mainArtists) && mainArtists.Count > 0)
        {
            return string.Join(", ", mainArtists.Where(static name => !string.IsNullOrWhiteSpace(name)));
        }

        return track.ArtistString;
    }

    private async Task<MusixmatchLyricsPayload?> FetchMusixmatchLyricsPayloadAsync(
        Track expected,
        string title,
        string artist,
        CancellationToken cancellationToken,
        bool tokenRetryUsed = false)
    {
        var token = await EnsureMusixmatchTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        using var searchDocument = await GetMusixmatchSignedJsonAsync(
            "track.search",
            new List<KeyValuePair<string, string>>
            {
                new("q_track", title),
                new("q_artist", artist),
                new("f_has_lyrics", "true"),
                new("page_size", "10"),
                new("usertoken", token)
            },
            cancellationToken);

        if (searchDocument == null)
        {
            return null;
        }

        if (IsMusixmatchAuthRejected(searchDocument.RootElement))
        {
            ClearMusixmatchAuthCache();
            return tokenRetryUsed
                ? null
                : await FetchMusixmatchLyricsPayloadAsync(expected, title, artist, cancellationToken, tokenRetryUsed: true);
        }

        var selectedTrack = SelectMusixmatchTrack(expected, ParseMusixmatchSearchTracks(searchDocument.RootElement));
        if (selectedTrack == null || selectedTrack.TrackId == null)
        {
            return null;
        }

        var trackId = selectedTrack.TrackId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var duration = ResolveMusixmatchDuration(expected, selectedTrack);
        var payload = new MusixmatchLyricsPayload { Track = selectedTrack };

        var richsyncDocument = await GetMusixmatchSignedJsonAsync(
            "track.richsync.get",
            new List<KeyValuePair<string, string>>
            {
                new("track_id", trackId),
                new("usertoken", token),
                new("f_richsync_length", duration),
                new("f_richsync_length_max_deviation", "10")
            },
            cancellationToken);
        if (richsyncDocument != null && IsMusixmatchAuthRejected(richsyncDocument.RootElement))
        {
            ClearMusixmatchAuthCache();
            return tokenRetryUsed
                ? payload
                : await FetchMusixmatchLyricsPayloadAsync(expected, title, artist, cancellationToken, tokenRetryUsed: true);
        }

        payload.RichsyncBody = TryReadMusixmatchRichsyncBody(richsyncDocument?.RootElement);

        var subtitleDocument = await GetMusixmatchSignedJsonAsync(
            "track.subtitle.get",
            new List<KeyValuePair<string, string>>
            {
                new("track_id", trackId),
                new("usertoken", token),
                new("f_subtitle_length", duration),
                new("f_subtitle_length_max_deviation", "10")
            },
            cancellationToken);
        if (subtitleDocument != null && IsMusixmatchAuthRejected(subtitleDocument.RootElement))
        {
            ClearMusixmatchAuthCache();
            return tokenRetryUsed
                ? payload
                : await FetchMusixmatchLyricsPayloadAsync(expected, title, artist, cancellationToken, tokenRetryUsed: true);
        }

        payload.SubtitleBody = TryReadMusixmatchSubtitleBody(subtitleDocument?.RootElement);

        var lyricsDocument = await GetMusixmatchSignedJsonAsync(
            "track.lyrics.get",
            new List<KeyValuePair<string, string>>
            {
                new("track_id", trackId),
                new("usertoken", token)
            },
            cancellationToken);
        if (lyricsDocument != null && IsMusixmatchAuthRejected(lyricsDocument.RootElement))
        {
            ClearMusixmatchAuthCache();
            return tokenRetryUsed
                ? payload
                : await FetchMusixmatchLyricsPayloadAsync(expected, title, artist, cancellationToken, tokenRetryUsed: true);
        }

        payload.LyricsBody = TryReadMusixmatchLyricsBody(lyricsDocument?.RootElement);
        return payload;
    }

    private async Task<JsonDocument?> GetMusixmatchSignedJsonAsync(
        string action,
        IReadOnlyList<KeyValuePair<string, string>> query,
        CancellationToken cancellationToken)
    {
        var secret = await GetMusixmatchSecretAsync(cancellationToken);
        var url = BuildMusixmatchSignedUrl(action, query, secret);

        using var client = _httpClientFactory.CreateClient(LyricsClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(UserAgentHeader, MusixmatchUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.musixmatch.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.musixmatch.com/");

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Musixmatch request {Action} failed with status {StatusCode}", action, response.StatusCode);
            }
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private async Task<string> GetMusixmatchSecretAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedMusixmatchSecret))
        {
            return _cachedMusixmatchSecret!;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient(LyricsClientName);
            using var searchRequest = new HttpRequestMessage(HttpMethod.Get, MusixmatchWebSearchUrl);
            searchRequest.Headers.TryAddWithoutValidation(UserAgentHeader, MusixmatchUserAgent);
            searchRequest.Headers.TryAddWithoutValidation("Cookie", "mxm_bab=AB");
            searchRequest.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            searchRequest.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            using var searchResponse = await client.SendAsync(searchRequest, cancellationToken);
            searchResponse.EnsureSuccessStatusCode();
            var searchPage = await searchResponse.Content.ReadAsStringAsync(cancellationToken);
            var appJsMatch = Regex.Match(
                searchPage,
                "src=\"(?<url>[^\"]*/_next/static/chunks/pages/_app-[^\"]+\\.js)\"",
                RegexOptions.None,
                TimeSpan.FromMilliseconds(500));
            if (!appJsMatch.Success)
            {
                return CacheMusixmatchSecret(MusixmatchDefaultSecret);
            }

            using var jsRequest = new HttpRequestMessage(HttpMethod.Get, appJsMatch.Groups["url"].Value);
            jsRequest.Headers.TryAddWithoutValidation(UserAgentHeader, MusixmatchUserAgent);
            jsRequest.Headers.TryAddWithoutValidation("Accept", "*/*");
            jsRequest.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            using var jsResponse = await client.SendAsync(jsRequest, cancellationToken);
            jsResponse.EnsureSuccessStatusCode();
            var jsContent = await jsResponse.Content.ReadAsStringAsync(cancellationToken);
            var secretMatch = Regex.Match(
                jsContent,
                "from\\(\\s*\"(?<secret>.*?)\"\\s*\\.split",
                RegexOptions.None,
                TimeSpan.FromMilliseconds(500));
            if (!secretMatch.Success)
            {
                return CacheMusixmatchSecret(MusixmatchDefaultSecret);
            }

            var encodedSecret = secretMatch.Groups["secret"].Value;
            var reversed = new string(encodedSecret.Reverse().ToArray());
            var decoded = Convert.FromBase64String(reversed);
            return CacheMusixmatchSecret(Encoding.UTF8.GetString(decoded));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Musixmatch web secret lookup failed; using bundled signing secret.");
            }
            return CacheMusixmatchSecret(MusixmatchDefaultSecret);
        }
    }

    private string CacheMusixmatchSecret(string secret)
    {
        _cachedMusixmatchSecret = string.IsNullOrWhiteSpace(secret) ? MusixmatchDefaultSecret : secret;
        return _cachedMusixmatchSecret;
    }

    private async Task<string?> EnsureMusixmatchTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedMusixmatchUserToken))
        {
            return _cachedMusixmatchUserToken;
        }

        await _musixmatchTokenGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedMusixmatchUserToken))
            {
                return _cachedMusixmatchUserToken;
            }

            using var tokenDocument = await GetMusixmatchSignedJsonAsync(
                "token.get",
                Array.Empty<KeyValuePair<string, string>>(),
                cancellationToken);
            if (tokenDocument == null || IsMusixmatchAuthRejected(tokenDocument.RootElement))
            {
                ClearMusixmatchAuthCache();
                return null;
            }

            if (!TryGetMusixmatchBody(tokenDocument.RootElement, out var body)
                || !body.TryGetProperty("user_token", out var tokenElement))
            {
                return null;
            }

            _cachedMusixmatchUserToken = tokenElement.GetString();
            return _cachedMusixmatchUserToken;
        }
        finally
        {
            _musixmatchTokenGate.Release();
        }
    }

    private void ClearMusixmatchAuthCache()
    {
        _cachedMusixmatchUserToken = null;
        _cachedMusixmatchSecret = null;
    }

    private static string BuildMusixmatchSignedUrl(string action, IReadOnlyList<KeyValuePair<string, string>> query, string secret)
    {
        var parameters = new List<KeyValuePair<string, string>>(query.Count + 2)
        {
            new("app_id", "web-desktop-app-v1.0"),
            new("format", "json")
        };
        parameters.AddRange(query.Where(static pair => !string.IsNullOrWhiteSpace(pair.Key)));

        var queryString = string.Join("&", parameters.Select(static pair =>
            pair.Key + "=" + Uri.EscapeDataString(pair.Value ?? string.Empty)));
        var unsignedUrl = MusixmatchBaseUrl + action + "?" + queryString;
        return SignMusixmatchUrl(unsignedUrl, secret);
    }

    private static string SignMusixmatchUrl(string unsignedUrl, string secret)
    {
        var normalizedUrl = unsignedUrl.Replace("%20", "+", StringComparison.Ordinal).Replace(" ", "+", StringComparison.Ordinal);
        var date = DateTimeOffset.UtcNow.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var message = normalizedUrl + date;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));
        return normalizedUrl
            + "&signature=" + Uri.EscapeDataString(signature)
            + "&signature_protocol=sha256";
    }

    private static bool IsMusixmatchAuthRejected(JsonElement root)
    {
        return TryReadMusixmatchRootStatus(root, out var statusCode)
            && (statusCode == (int)HttpStatusCode.Unauthorized || statusCode == (int)HttpStatusCode.PaymentRequired);
    }

    private static bool TryReadMusixmatchRootStatus(JsonElement root, out int statusCode)
    {
        statusCode = default;
        if (!root.TryGetProperty(MessagePropertyName, out var message)
            || !message.TryGetProperty("header", out var header)
            || !header.TryGetProperty("status_code", out var statusCodeElement)
            || statusCodeElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return statusCodeElement.TryGetInt32(out statusCode);
    }

    private static bool TryGetMusixmatchBody(JsonElement root, out JsonElement body)
    {
        body = default;
        return root.TryGetProperty(MessagePropertyName, out var message)
            && message.TryGetProperty("body", out body)
            && body.ValueKind == JsonValueKind.Object;
    }

    private static IReadOnlyList<MusixmatchTrack> ParseMusixmatchSearchTracks(JsonElement root)
    {
        if (!TryGetMusixmatchBody(root, out var body)
            || !body.TryGetProperty("track_list", out var trackList)
            || trackList.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<MusixmatchTrack>();
        }

        var tracks = new List<MusixmatchTrack>();
        foreach (var item in trackList.EnumerateArray())
        {
            if (!item.TryGetProperty("track", out var trackElement) || trackElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            try
            {
                var track = trackElement.Deserialize<MusixmatchTrack>();
                if (track != null)
                {
                    tracks.Add(track);
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return tracks;
    }

    private static MusixmatchTrack? SelectMusixmatchTrack(Track expected, IReadOnlyList<MusixmatchTrack> candidates)
    {
        return candidates
            .Select(candidate => new
            {
                Track = candidate,
                Validation = ValidateMusixmatchTrack(expected, candidate),
                DurationDelta = ResolveMusixmatchDurationDelta(expected, candidate)
            })
            .Where(candidate => candidate.Validation.IsMatch)
            .OrderByDescending(candidate => candidate.Validation.Score)
            .ThenBy(candidate => candidate.DurationDelta)
            .Select(candidate => candidate.Track)
            .FirstOrDefault();
    }

    private static LyricsIdentityValidationResult ValidateMusixmatchTrack(Track expected, MusixmatchTrack track)
    {
        return LyricsIdentityValidator.ValidateSearchCandidate(
            expected,
            new LyricsCandidateIdentity(
                MusixmatchProvider,
                track.TrackId?.ToString() ?? track.CommonTrackId?.ToString(),
                track.TrackName,
                track.ArtistName,
                track.AlbumName,
                track.TrackLength.HasValue ? (int)Math.Round(track.TrackLength.Value) : null,
                track.TrackIsrc),
            durationToleranceSeconds: 10,
            requireArtist: true);
    }

    private static int ResolveMusixmatchDurationDelta(Track expected, MusixmatchTrack track)
    {
        if (expected.Duration <= 0 || !track.TrackLength.HasValue || track.TrackLength.Value <= 0)
        {
            return int.MaxValue;
        }

        return Math.Abs(expected.Duration - (int)Math.Round(track.TrackLength.Value));
    }

    private static string ResolveMusixmatchDuration(Track expected, MusixmatchTrack track)
    {
        var duration = track.TrackLength.HasValue && track.TrackLength.Value > 0
            ? (int)Math.Round(track.TrackLength.Value)
            : expected.Duration;
        return Math.Max(0, duration).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? TryReadMusixmatchLyricsBody(JsonElement? root)
    {
        if (root == null
            || !TryGetMusixmatchBody(root.Value, out var body)
            || !body.TryGetProperty("lyrics", out var lyrics)
            || lyrics.ValueKind != JsonValueKind.Object
            || !lyrics.TryGetProperty("lyrics_body", out var lyricsBodyElement)
            || lyricsBodyElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var lyricsBody = lyricsBodyElement.GetString();
        return string.IsNullOrWhiteSpace(lyricsBody) ? null : lyricsBody;
    }

    private static string? TryReadMusixmatchSubtitleBody(JsonElement? root)
    {
        if (root == null
            || !TryGetMusixmatchBody(root.Value, out var body)
            || !body.TryGetProperty("subtitle", out var subtitle)
            || subtitle.ValueKind != JsonValueKind.Object
            || !subtitle.TryGetProperty("subtitle_body", out var subtitleBodyElement)
            || subtitleBodyElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var subtitleBody = subtitleBodyElement.GetString();
        return string.IsNullOrWhiteSpace(subtitleBody) ? null : subtitleBody;
    }

    private static string? TryReadMusixmatchRichsyncBody(JsonElement? root)
    {
        if (root == null
            || !TryGetMusixmatchBody(root.Value, out var body)
            || !body.TryGetProperty("richsync", out var richsync)
            || richsync.ValueKind != JsonValueKind.Object
            || !richsync.TryGetProperty("richsync_body", out var richsyncBodyElement)
            || richsyncBodyElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var richsyncBody = richsyncBodyElement.GetString();
        return string.IsNullOrWhiteSpace(richsyncBody) ? null : richsyncBody;
    }

    private static bool TryReadMusixmatchUnsynced(MusixmatchLyricsPayload body, out string? unsyncedLyrics)
    {
        unsyncedLyrics = null;
        var lyricsBody = body.LyricsBody;
        if (string.IsNullOrWhiteSpace(lyricsBody))
        {
            return false;
        }

        if (string.Equals(lyricsBody.Trim(), "instrumental", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        unsyncedLyrics = lyricsBody;
        return true;
    }

    private static bool TryReadMusixmatchSubtitles(MusixmatchLyricsPayload body, out List<SynchronizedLyric> lines)
    {
        lines = new List<SynchronizedLyric>();
        var subtitleBody = body.SubtitleBody;
        if (string.IsNullOrWhiteSpace(subtitleBody))
        {
            return false;
        }

        foreach (var line in subtitleBody.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length < 11 || line[0] != '[')
            {
                continue;
            }

            var closingBracketIndex = line.IndexOf(']');
            if (closingBracketIndex <= 0)
            {
                continue;
            }

            var timestampRaw = line[1..closingBracketIndex];
            if (!TryParseLrcTimestampMilliseconds(timestampRaw, out var milliseconds))
            {
                continue;
            }

            var text = line[(closingBracketIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            lines.Add(new SynchronizedLyric(text, SynchronizedLyric.BuildLrcTimestamp(milliseconds), milliseconds));
        }

        return lines.Count > 0;
    }

    private static bool TryReadMusixmatchRichsync(MusixmatchLyricsPayload body, out List<SynchronizedLyric> lines)
    {
        lines = new List<SynchronizedLyric>();
        var richsyncBody = body.RichsyncBody;
        if (string.IsNullOrWhiteSpace(richsyncBody))
        {
            return false;
        }

        List<MusixmatchRichsyncLine>? richsyncLines;
        try
        {
            richsyncLines = JsonSerializer.Deserialize<List<MusixmatchRichsyncLine>>(richsyncBody);
        }
        catch (JsonException)
        {
            return false;
        }

        if (richsyncLines == null || richsyncLines.Count == 0)
        {
            return false;
        }

        foreach (var richsyncLine in richsyncLines)
        {
            if (richsyncLine.Ts < 0 || string.IsNullOrWhiteSpace(richsyncLine.Text))
            {
                continue;
            }

            var milliseconds = ConvertMusixmatchSecondsToMilliseconds(richsyncLine.Ts);
            var endMilliseconds = richsyncLine.Te > richsyncLine.Ts
                ? ConvertMusixmatchSecondsToMilliseconds(richsyncLine.Te)
                : milliseconds;
            var words = BuildMusixmatchTimedWords(richsyncLine, milliseconds, endMilliseconds);
            lines.Add(new SynchronizedLyric(
                richsyncLine.Text.Trim(),
                SynchronizedLyric.BuildLrcTimestamp(milliseconds),
                milliseconds,
                Math.Max(0, endMilliseconds - milliseconds))
            {
                Words = words.Count > 0 ? words : null
            });
        }

        return lines.Count > 0;
    }

    private static int ConvertMusixmatchSecondsToMilliseconds(double seconds)
        => (int)Math.Round(Math.Max(0d, seconds) * 1000d);

    private static List<SynchronizedLyricWord> BuildMusixmatchTimedWords(
        MusixmatchRichsyncLine richsyncLine,
        int lineStartMilliseconds,
        int lineEndMilliseconds)
    {
        var sourceWords = richsyncLine.Words?
            .Where(static word => !string.IsNullOrEmpty(word.Text) && word.Offset >= 0)
            .OrderBy(static word => word.Offset)
            .ToList();
        if (sourceWords == null || sourceWords.Count == 0)
        {
            return new List<SynchronizedLyricWord>();
        }

        var words = new List<SynchronizedLyricWord>(sourceWords.Count);
        for (var i = 0; i < sourceWords.Count; i++)
        {
            var sourceWord = sourceWords[i];
            var startMilliseconds = lineStartMilliseconds + ConvertMusixmatchSecondsToMilliseconds(sourceWord.Offset);
            var nextStartMilliseconds = i + 1 < sourceWords.Count
                ? lineStartMilliseconds + ConvertMusixmatchSecondsToMilliseconds(sourceWords[i + 1].Offset)
                : lineEndMilliseconds;
            var endMilliseconds = Math.Max(startMilliseconds + 1, nextStartMilliseconds);
            words.Add(new SynchronizedLyricWord(sourceWord.Text, startMilliseconds, endMilliseconds));
        }

        return words
            .Where(static word => !string.IsNullOrEmpty(word.Text)
                && (string.IsNullOrWhiteSpace(word.Text) || word.IsValid()))
            .ToList();
    }

    private static bool TryParseLrcTimestampMilliseconds(string value, out int milliseconds)
    {
        milliseconds = 0;
        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var minutes))
        {
            return false;
        }

        var secondsParts = parts[1].Split('.', StringSplitOptions.TrimEntries);
        if (secondsParts.Length != 2
            || !int.TryParse(secondsParts[0], out var seconds)
            || !int.TryParse(secondsParts[1], out var fraction))
        {
            return false;
        }

        var millisecondsFraction = secondsParts[1].Length switch
        {
            1 => fraction * 100,
            2 => fraction * 10,
            _ => int.Parse(secondsParts[1][..Math.Min(3, secondsParts[1].Length)], System.Globalization.CultureInfo.InvariantCulture)
        };

        milliseconds = (minutes * 60 * 1000) + (seconds * 1000) + millisecondsFraction;
        return true;
    }

    private static LyricsIdentityValidationResult ValidateMusixmatchPayload(
        Track expected,
        MusixmatchLyricsPayload body)
    {
        return ValidateMusixmatchTrack(expected, body.Track);
    }

    private sealed class MusixmatchLyricsPayload
    {
        public required MusixmatchTrack Track { get; init; }
        public string? RichsyncBody { get; set; }
        public string? SubtitleBody { get; set; }
        public string? LyricsBody { get; set; }
    }

    private sealed class MusixmatchTrack
    {
        [JsonPropertyName("track_id")]
        public long? TrackId { get; set; }

        [JsonPropertyName("commontrack_id")]
        public long? CommonTrackId { get; set; }

        [JsonPropertyName("track_name")]
        public string? TrackName { get; set; }

        [JsonPropertyName("artist_name")]
        public string? ArtistName { get; set; }

        [JsonPropertyName("album_name")]
        public string? AlbumName { get; set; }

        [JsonPropertyName("track_length")]
        public double? TrackLength { get; set; }

        [JsonPropertyName("track_isrc")]
        public string? TrackIsrc { get; set; }
    }

    private sealed class MusixmatchRichsyncLine
    {
        [JsonPropertyName("ts")]
        public double Ts { get; set; }

        [JsonPropertyName("te")]
        public double Te { get; set; }

        [JsonPropertyName("x")]
        public string? Text { get; set; }

        [JsonPropertyName("l")]
        public List<MusixmatchRichsyncWord>? Words { get; set; }
    }

    private sealed class MusixmatchRichsyncWord
    {
        [JsonPropertyName("c")]
        public string? Text { get; set; }

        [JsonPropertyName("o")]
        public double Offset { get; set; }
    }

    private static string? ResolveDeezerLyricsTrackId(Track track)
    {
        if (TryResolveDeezerTrackIdFromTrack(track, out var deezerTrackId))
        {
            return deezerTrackId;
        }

        return null;
    }

    private static bool TryResolveDeezerTrackIdFromTrack(Track track, out string? deezerTrackId)
    {
        return TrackIdNormalization.TryResolveDeezerTrackId(track, out deezerTrackId, track.LyricsId);
    }

    private async Task<LyricsBase> ResolveSpotifyLyricsAsync(
        Track track,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var spotifyTrackId = ResolveSpotifyLyricsTrackId(track);
        if (string.IsNullOrWhiteSpace(spotifyTrackId))
        {
            return CreateLyricsError("Unable to resolve Spotify track ID for lyrics.");
        }

        var authContext = await ResolveSpotifyAuthContextAsync(cancellationToken);
        if (authContext is null)
        {
            return CreateLyricsError("Spotify auth is not available for lyrics.");
        }

        var accessToken = await ResolveSpotifyWebPlayerAccessTokenAsync(authContext, cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return CreateLyricsError("Unable to obtain Spotify web player token for lyrics.");
        }

        foreach (var url in BuildSpotifyLyricsUrls(spotifyTrackId, settings))
        {
            var payload = await TryFetchSpotifyLyricsPayloadAsync(url, accessToken, authContext.UserAgent, cancellationToken);
            if (payload is null)
            {
                continue;
            }

            var parsed = ParseSpotifyLyricsPayload(payload.Value);
            if (parsed.IsLoaded())
            {
                return parsed;
            }
        }

        return CreateLyricsError($"Spotify lyrics not available for track {spotifyTrackId}.");
    }

    private static string? ResolveSpotifyLyricsTrackId(Track track)
    {
        if (TryResolveSpotifyTrackIdFromTrack(track, out var spotifyTrackId))
        {
            return spotifyTrackId;
        }

        return null;
    }

    private static bool TryResolveSpotifyTrackIdFromTrack(Track track, out string? spotifyTrackId)
    {
        return TrackIdNormalization.TryResolveSpotifyTrackId(track, out spotifyTrackId);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? TryGetTrackUrl(Track track, string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || track.Urls == null)
        {
            return null;
        }

        return track.Urls.TryGetValue(key, out var value) ? value : null;
    }

    private async Task<SpotifyAuthContext?> ResolveSpotifyAuthContextAsync(CancellationToken cancellationToken)
    {
        var state = await TryLoadSpotifyAuthStateAsync(cancellationToken);
        if (state is null)
        {
            return null;
        }

        var defaultUserAgent = string.IsNullOrWhiteSpace(state.UserAgent)
            ? DefaultSpotifyWebPlayerUserAgent
            : state.UserAgent;

        if (!string.IsNullOrWhiteSpace(state.SpDc))
        {
            return new SpotifyAuthContext(state.SpDc, defaultUserAgent);
        }

        foreach (var rawBlobPath in state.BlobPaths)
        {
            var fromBlob = await TryExtractSpotifyAuthContextFromBlobAsync(rawBlobPath, defaultUserAgent, cancellationToken);
            if (fromBlob is not null)
            {
                return fromBlob;
            }
        }

        return null;
    }

    private async Task<SpotifyAuthState?> TryLoadSpotifyAuthStateAsync(CancellationToken cancellationToken)
    {
        var dataRoot = ResolveSpotifyDataRoot();
        var statePath = Path.Join(dataRoot, "autotag", "spotify.json");
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(statePath, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var spotify = doc.RootElement;
            if (spotify.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var spDc = TryReadJsonString(spotify, "webPlayerSpDc");
            var userAgent = TryReadJsonString(spotify, "webPlayerUserAgent");
            var activeAccount = TryReadJsonString(spotify, "activeAccount");
            var blobPaths = ReadSpotifyBlobPaths(spotify, activeAccount);

            return new SpotifyAuthState(spDc, userAgent, blobPaths);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read Spotify auth state from {Path}", statePath);            }
            return null;
        }
    }

    private static List<string> ReadSpotifyBlobPaths(JsonElement spotify, string? activeAccount)
    {
        var blobPaths = new List<string>();
        if (!spotify.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
        {
            return blobPaths;
        }

        AppendActiveAccountBlobPaths(accounts, activeAccount, blobPaths);
        AppendRemainingBlobPaths(accounts, blobPaths);
        return blobPaths;
    }

    private static void AppendActiveAccountBlobPaths(JsonElement accounts, string? activeAccount, List<string> blobPaths)
    {
        if (string.IsNullOrWhiteSpace(activeAccount))
        {
            return;
        }

        foreach (var account in accounts.EnumerateArray())
        {
            var accountName = TryReadJsonString(account, "name");
            if (!string.Equals(accountName, activeAccount, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AppendSpotifyBlobPath(account, "webPlayerBlobPath", blobPaths);
            AppendSpotifyBlobPath(account, "blobPath", blobPaths);
        }
    }

    private static void AppendRemainingBlobPaths(JsonElement accounts, List<string> blobPaths)
    {
        foreach (var account in accounts.EnumerateArray())
        {
            AppendSpotifyBlobPath(account, "webPlayerBlobPath", blobPaths);
            AppendSpotifyBlobPath(account, "blobPath", blobPaths);
        }
    }

    private static void AppendSpotifyBlobPath(JsonElement account, string propertyName, List<string> blobPaths)
    {
        var blobPath = TryReadJsonString(account, propertyName);
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return;
        }

        if (blobPaths.Any(existing => string.Equals(existing, blobPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        blobPaths.Add(blobPath);
    }

    private async Task<SpotifyAuthContext?> TryExtractSpotifyAuthContextFromBlobAsync(
        string rawBlobPath,
        string fallbackUserAgent,
        CancellationToken cancellationToken)
    {
        var blobPath = ResolveSpotifyBlobPath(rawBlobPath);
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
        {
            return null;
        }

        try
        {
            var json = await _spotifyWebPlayerCredentialStore.ReadTextAndMigrateAsync(blobPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("auth_type", out _) && root.TryGetProperty("auth_data", out _))
            {
                return null;
            }

            if (!root.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var spDc = ReadSpotifyCookies(cookies);

            if (string.IsNullOrWhiteSpace(spDc))
            {
                return null;
            }

            var userAgent = TryReadJsonString(root, "userAgent");
            if (string.IsNullOrWhiteSpace(userAgent))
            {
                userAgent = fallbackUserAgent;
            }

            return new SpotifyAuthContext(spDc, userAgent!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to parse Spotify blob payload at {Path}", blobPath);            }
            return null;
        }
    }

    private static string? ReadSpotifyCookies(JsonElement cookies)
    {
        string? spDc = null;
        foreach (var cookie in cookies.EnumerateArray())
        {
            var name = TryReadJsonString(cookie, "name");
            var value = TryReadJsonString(cookie, "value");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (name.Equals("sp_dc", StringComparison.OrdinalIgnoreCase))
            {
                spDc = value;
            }
        }

        return spDc;
    }

    private async Task<string?> ResolveSpotifyWebPlayerAccessTokenAsync(
        SpotifyAuthContext context,
        CancellationToken cancellationToken)
    {
        var cacheKey = context.SpDc;
        if (string.Equals(cacheKey, _cachedSpotifyAccessTokenKey, StringComparison.Ordinal)
            && DateTimeOffset.UtcNow < _cachedSpotifyAccessTokenExpiry
            && !string.IsNullOrWhiteSpace(_cachedSpotifyAccessToken))
        {
            return _cachedSpotifyAccessToken;
        }

        await _spotifyTokenGate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(cacheKey, _cachedSpotifyAccessTokenKey, StringComparison.Ordinal)
                && DateTimeOffset.UtcNow < _cachedSpotifyAccessTokenExpiry
                && !string.IsNullOrWhiteSpace(_cachedSpotifyAccessToken))
            {
                return _cachedSpotifyAccessToken;
            }

            var response = await FetchSpotifyWebPlayerTokenAsync(context, cancellationToken);
            if (response is null || string.IsNullOrWhiteSpace(response.AccessToken))
            {
                return null;
            }

            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(45);
            if (response.ExpiresAtUnixMs.HasValue && response.ExpiresAtUnixMs.Value > 0)
            {
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(response.ExpiresAtUnixMs.Value).AddMinutes(-2);
            }

            _cachedSpotifyAccessToken = response.AccessToken;
            _cachedSpotifyAccessTokenExpiry = expiresAt;
            _cachedSpotifyAccessTokenKey = cacheKey;
            return response.AccessToken;
        }
        finally
        {
            _spotifyTokenGate.Release();
        }
    }

    private async Task<SpotifyTokenResponse?> FetchSpotifyWebPlayerTokenAsync(
        SpotifyAuthContext context,
        CancellationToken cancellationToken)
    {
        var cookieContainer = new CookieContainer();
        cookieContainer.Add(new Cookie("sp_dc", context.SpDc, "/", ".spotify.com")
        {
            Secure = true,
            HttpOnly = true
        });

        using var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(context.UserAgent)
            ? DefaultSpotifyWebPlayerUserAgent
            : context.UserAgent);

        await WarmSpotifyWebPlayerSessionAsync(client, cancellationToken);

        var (totp, version) = SpotifyWebPlayerTotp.Generate();
        if (!string.IsNullOrWhiteSpace(totp))
        {
            var apiTokenUrl =
                $"{SpotifyOpenBaseUrl}{SpotifyOpenTokenPath}?reason=init&productType=web-player&totp={totp}&totpVer={version}&totpServer={totp}";
            var primary = await RequestSpotifyWebPlayerTokenAsync(client, apiTokenUrl, cancellationToken);
            if (!string.IsNullOrWhiteSpace(primary?.AccessToken))
            {
                return primary;
            }
        }

        var fallbackUrl = $"{SpotifyOpenBaseUrl}{SpotifyOpenFallbackTokenPath}?reason=transport&productType=web_player";
        var fallback = await RequestSpotifyWebPlayerTokenAsync(client, fallbackUrl, cancellationToken);
        return !string.IsNullOrWhiteSpace(fallback?.AccessToken) ? fallback : null;
    }

    private async Task<SpotifyTokenResponse?> RequestSpotifyWebPlayerTokenAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd(ApplicationJson);
            request.Headers.Referrer = new Uri(SpotifyOpenRootUrl);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var accessToken = TryReadJsonString(root, "accessToken");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return null;
            }

            long? expiresAt = null;
            if (root.TryGetProperty("accessTokenExpirationTimestampMs", out var expiry) &&
                expiry.TryGetInt64(out var expiryMs))
            {
                expiresAt = expiryMs;
            }

            bool? isAnonymous = null;
            if (root.TryGetProperty("isAnonymous", out var anon))
            {
                isAnonymous = anon.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                };
            }

            var country = TryReadJsonString(root, "country");
            return new SpotifyTokenResponse(accessToken, expiresAt, country, isAnonymous);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Spotify web player token request failed for {Url}", url);            }
            return null;
        }
    }

    private static async Task WarmSpotifyWebPlayerSessionAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SpotifyOpenBaseUrl);
            request.Headers.Accept.ParseAdd("text/html");
            request.Headers.Referrer = new Uri(SpotifyOpenRootUrl);
            using var response = await client.SendAsync(request, cancellationToken);
            _ = response.Content;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort warmup only.
        }
    }

    private async Task<JsonElement?> TryFetchSpotifyLyricsPayloadAsync(
        string url,
        string accessToken,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(LyricsClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.ParseAdd(ApplicationJson);
            request.Headers.Referrer = new Uri(SpotifyOpenRootUrl);
            request.Headers.TryAddWithoutValidation("app-platform", "WebPlayer");
            request.Headers.TryAddWithoutValidation("origin", SpotifyOpenBaseUrl);
            if (!string.IsNullOrWhiteSpace(userAgent))
            {
                request.Headers.TryAddWithoutValidation(UserAgentHeader, userAgent);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Spotify lyrics request failed for {Url}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(url));            }
            return null;
        }
    }

    private static LyricsSource ParseSpotifyLyricsPayload(JsonElement payload)
    {
        var lyrics = new LyricsSource();
        var source = payload;
        if (payload.TryGetProperty(LyricsType, out var nestedLyrics) &&
            nestedLyrics.ValueKind == JsonValueKind.Object)
        {
            source = nestedLyrics;
        }

        var syncType = TryReadJsonString(source, "syncType") ?? TryReadJsonString(payload, "syncType");
        var isSynced = string.Equals(syncType, "LINE_SYNCED", StringComparison.OrdinalIgnoreCase);

        if (source.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            var unsyncedLines = new List<string>();
            foreach (var line in lines.EnumerateArray())
            {
                ProcessSpotifyLyricsLine(lyrics, line, isSynced, unsyncedLines);
            }

            if (unsyncedLines.Count > 0)
            {
                lyrics.UnsyncedLyrics = string.Join('\n', unsyncedLines);
                lyrics.UnsyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedPlainText;
            }
        }

        if (lyrics.SyncedLyrics?.Count > 0)
        {
            lyrics.SyncedLyricsSourceFormat = LyricsSourceFormat.ProviderSyncedJson;
        }

        if (!lyrics.IsLoaded())
        {
            var plain = TryReadJsonString(source, "text");
            if (!string.IsNullOrWhiteSpace(plain))
            {
                lyrics.UnsyncedLyrics = plain;
                lyrics.UnsyncedLyricsSourceFormat = LyricsSourceFormat.DownloadedPlainText;
            }
        }

        if (!lyrics.IsLoaded())
        {
            lyrics.SetErrorMessage("Spotify lyrics payload contained no usable lines.");
        }

        return lyrics;
    }

    private static void ProcessSpotifyLyricsLine(
        LyricsSource lyrics,
        JsonElement line,
        bool isSynced,
        List<string> unsyncedLines)
    {
        if (line.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var words = ReadSpotifyLineWords(line);
        if (string.IsNullOrWhiteSpace(words))
        {
            return;
        }

        unsyncedLines.Add(words);
        if (!isSynced)
        {
            return;
        }

        var syncedLyric = BuildSyncedSpotifyLine(line, words);
        if (syncedLyric != null)
        {
            lyrics.SyncedLyrics?.Add(syncedLyric);
        }
    }

    private static string ReadSpotifyLineWords(JsonElement line)
    {
        var words = TryReadJsonString(line, "words")
            ?? TryReadJsonString(line, "text")
            ?? string.Empty;
        return words.Replace("\r", string.Empty).TrimEnd();
    }

    private static SynchronizedLyric? BuildSyncedSpotifyLine(JsonElement line, string words)
    {
        var startMs = TryParseMilliseconds(line, "startTimeMs");
        if (!startMs.HasValue || startMs.Value < 0)
        {
            return null;
        }

        var endMs = TryParseMilliseconds(line, "endTimeMs");
        var duration = endMs.HasValue && endMs.Value > startMs.Value
            ? endMs.Value - startMs.Value
            : 0;
        return new SynchronizedLyric(
            words,
            SynchronizedLyric.BuildLrcTimestamp(startMs.Value),
            startMs.Value,
            duration);
    }

    private static int? TryParseMilliseconds(JsonElement line, string property)
    {
        if (!line.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            return numeric;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static IEnumerable<string> BuildSpotifyLyricsUrls(string spotifyTrackId, DeezSpoTagSettings settings)
    {
        var markets = new List<string> { "from_token" };
        if (!string.IsNullOrWhiteSpace(settings.DeezerCountry))
        {
            var country = settings.DeezerCountry.Trim().ToUpperInvariant();
            if (country.Length == 2 && !markets.Contains(country, StringComparer.OrdinalIgnoreCase))
            {
                markets.Add(country);
            }
        }

        foreach (var market in markets)
        {
            yield return $"https://spclient.wg.spotify.com/color-lyrics/v2/track/{spotifyTrackId}?format=json&market={market}";
            yield return $"https://spclient.wg.spotify.com/lyrics/v1/track/{spotifyTrackId}?format=json&market={market}";
        }
    }

    private static string ResolveSpotifyDataRoot()
    {
        return AppDataPathResolver.ResolveDataRootOrDefault(AppDataPathResolver.GetDefaultWorkersDataDir());
    }

    private static string? ResolveSpotifyBlobPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var trimmed = rawPath.Trim();
        if (File.Exists(trimmed))
        {
            return Path.GetFullPath(trimmed);
        }

        var dataRoot = ResolveSpotifyDataRoot();
        var candidates = new List<string>();
        if (Path.IsPathRooted(trimmed))
        {
            var fileName = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                candidates.Add(Path.Join(dataRoot, SpotifyDataDir, BlobsDir, fileName));
            }
        }
        else
        {
            candidates.Add(Path.Join(dataRoot, trimmed));
            candidates.Add(Path.Join(dataRoot, SpotifyDataDir, BlobsDir, trimmed));
            var fileName = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                candidates.Add(Path.Join(dataRoot, SpotifyDataDir, BlobsDir, fileName));
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? TryReadJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static LyricsSource CreateLyricsError(string message)
    {
        var lyrics = new LyricsSource();
        lyrics.SetErrorMessage(message);
        return lyrics;
    }

    private sealed record SpotifyAuthState(string? SpDc, string? UserAgent, List<string> BlobPaths);
    private sealed record SpotifyAuthContext(string SpDc, string UserAgent);
    private sealed record SpotifyTokenResponse(string AccessToken, long? ExpiresAtUnixMs, string? Country, bool? IsAnonymous);

    /// <summary>
    /// Get lyrics using refreezer's dual API approach
    /// Primary: Pipe API with GraphQL, Fallback: Legacy GW API
    /// </summary>
    public async Task<LyricsBase> GetLyricsAsync(string trackId, string arl, string? sid = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(trackId))
        {
            _logger.LogWarning("Track ID is null or empty");
            return LyricsNew.CreateError("Track ID is required");
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Fetching lyrics for track {TrackId}", trackId);        }

        // Primary: Try Pipe API with GraphQL
        LyricsBase lyricsFromPipe;
        try
        {
            lyricsFromPipe = await GetLyricsFromPipeApiAsync(trackId, arl, sid, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Pipe API failed for track {TrackId}; falling back to GW API", trackId);
            lyricsFromPipe = LyricsNew.CreateError($"Pipe API failed: {ex.Message}");
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Pipe API timed out for track {TrackId}; falling back to GW API", trackId);
            lyricsFromPipe = LyricsNew.CreateError("Pipe API timed out.");
        }

        if (lyricsFromPipe.IsLoaded())
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Successfully fetched lyrics from Pipe API for track {TrackId}", trackId);            }
            return lyricsFromPipe;
        }

        if (!string.IsNullOrEmpty(lyricsFromPipe.ErrorMessage))
        {
            _logger.LogWarning("Pipe API failed for track {TrackId}: {Error}", trackId, lyricsFromPipe.ErrorMessage);
        }

        // Fallback: Try legacy GW API
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Falling back to GW API for track {TrackId}", trackId);        }
        var lyricsFromGw = await GetLyricsFromGwApiAsync(trackId, arl, sid, cancellationToken);

        if (lyricsFromGw.IsLoaded())
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Successfully fetched lyrics from GW API for track {TrackId}", trackId);            }
            return lyricsFromGw;
        }

        if (!string.IsNullOrEmpty(lyricsFromGw.ErrorMessage))
        {
            _logger.LogWarning("GW API also failed for track {TrackId}: {Error}", trackId, lyricsFromGw.ErrorMessage);
        }

        // Both APIs failed, return the Pipe API error (usually more informative)
        _logger.LogError("Both Pipe API and GW API failed for track {TrackId}", trackId);
        return lyricsFromPipe;
    }

    /// <summary>
    /// Get lyrics from modern Pipe API using GraphQL
    /// </summary>
    private async Task<LyricsNew> GetLyricsFromPipeApiAsync(string trackId, string arl, string? sid, CancellationToken cancellationToken)
    {
        try
        {
            // Get JWT token for authentication
            var jwtToken = await _jwtTokenService.GetJsonWebTokenAsync(arl, sid, cancellationToken);
            if (string.IsNullOrEmpty(jwtToken))
            {
                return LyricsNew.CreateError("Failed to obtain JWT token for Pipe API");
            }

            // Create GraphQL query
            var queryString = """
                query SynchronizedTrackLyrics($trackId: String!) {
                  track(trackId: $trackId) {
                    id
                    isExplicit
                    lyrics {
                      id
                      copyright
                      text
                      writers
                      synchronizedLines {
                        lrcTimestamp
                        line
                        milliseconds
                        duration
                      }
                    }
                  }
                }
                """;

            var requestBody = new
            {
                operationName = "SynchronizedTrackLyrics",
                variables = new { trackId },
                query = queryString
            };

            using var httpClient = _httpClientFactory.CreateClient(LyricsClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, DeezerPipeApiUrl);

            // Set headers
            request.Headers.Add(UserAgentHeader, "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/96.0.4664.110 Safari/537.36");
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            request.Headers.Add("Authorization", $"Bearer {jwtToken}");

            // Set cookies
            var cookieValue = $"arl={arl}";
            if (!string.IsNullOrEmpty(sid))
            {
                cookieValue += $"; sid={sid}";
            }
            request.Headers.Add("Cookie", cookieValue);

            // Set content
            var jsonContent = JsonSerializer.Serialize(requestBody);
            request.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, ApplicationJson);

            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return LyricsNew.CreateError($"Pipe API request failed with status: {response.StatusCode}");
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (string.IsNullOrEmpty(responseContent))
            {
                return LyricsNew.CreateError("Empty response from Pipe API");
            }

            // Parse response
            using var jsonDoc = JsonDocument.Parse(responseContent);
            return new LyricsNew(jsonDoc.RootElement);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Pipe API response for track {TrackId}", trackId);
            return LyricsNew.CreateError($"Failed to parse Pipe API response: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request failed for Pipe API track {TrackId}", trackId);
            return LyricsNew.CreateError($"Pipe API request failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error in Pipe API for track {TrackId}", trackId);
            return LyricsNew.CreateError($"Unexpected Pipe API error: {ex.Message}");
        }
    }

    /// <summary>
    /// Get lyrics from legacy GW API as fallback
    /// </summary>
    private async Task<LyricsClassic> GetLyricsFromGwApiAsync(string trackId, string arl, string? sid, CancellationToken cancellationToken)
    {
        try
        {
            // First get track data to access lyrics - EXACT PORT: Use SNG_ID like deemix
            var trackData = await CallGwApiAsync("deezer.pageTrack", $"{{\"SNG_ID\": \"{trackId}\"}}", arl, sid, cancellationToken);

            if (trackData == null)
            {
                return LyricsClassic.CreateError("Failed to get track data from GW API");
            }

            // Check if track data has lyrics
            if (trackData.HasValue && trackData.Value.TryGetProperty("results", out var resultsElement) &&
                resultsElement.TryGetProperty("LYRICS", out var lyricsElement))
            {
                return new LyricsClassic(lyricsElement);
            }

            // Try direct lyrics API call - EXACT PORT: Use SNG_ID like deemix
            var lyricsData = await CallGwApiAsync("song.getLyrics", $"{{\"SNG_ID\": \"{trackId}\"}}", arl, sid, cancellationToken);

            if (lyricsData == null)
            {
                return LyricsClassic.CreateError("No lyrics data from GW API");
            }

            if (lyricsData.HasValue && lyricsData.Value.TryGetProperty("results", out var lyricsResultsElement))
            {
                return new LyricsClassic(lyricsResultsElement);
            }

            return LyricsClassic.CreateError("No lyrics found in GW API response");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error in GW API lyrics fetch for track {TrackId}", trackId);
            return LyricsClassic.CreateError($"GW API error: {ex.Message}");
        }
    }

    /// <summary>
    /// Call Deezer GW API
    /// </summary>
    private async Task<JsonElement?> CallGwApiAsync(string method, string body, string arl, string? sid, CancellationToken cancellationToken)
    {
        try
        {
            var apiToken = await GetGwTokenAsync(arl, sid, cancellationToken);
            if (RequiresGwApiToken(method) && string.IsNullOrWhiteSpace(apiToken))
            {
                _logger.LogWarning("Unable to obtain GW token for method {Method}", method);
                return null;
            }

            var url = $"https://www.deezer.com/ajax/gw-light.php?method={method}&input=3&api_version=1.0&api_token={apiToken ?? "null"}";
            using var httpClient = _httpClientFactory.CreateClient(LyricsClientName);
            using var request = BuildGwApiRequest(url, body, arl, sid);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var root = await ParseGwApiResponseAsync(response, cancellationToken);
            if (!root.HasValue)
            {
                return null;
            }

            var retried = await TryRetryGwApiCallOnInvalidTokenAsync(root.Value, method, body, arl, sid, cancellationToken);
            return retried ?? root;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error calling GW API method {Method}", method);
            return null;
        }
    }

    private static bool RequiresGwApiToken(string method)
    {
        return !string.Equals(method, "deezer.getUserData", StringComparison.Ordinal);
    }

    private static HttpRequestMessage BuildGwApiRequest(string url, string body, string arl, string? sid)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add(UserAgentHeader, "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/96.0.4664.110 Safari/537.36");
        request.Headers.Add("Accept", "*/*");
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
        request.Headers.Add("Cookie", BuildGwCookie(arl, sid));
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, ApplicationJson);
        return request;
    }

    private static string BuildGwCookie(string arl, string? sid)
    {
        return string.IsNullOrEmpty(sid)
            ? $"arl={arl}"
            : $"arl={arl}; sid={sid}";
    }

    private async Task<JsonElement?> ParseGwApiResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("GW API request failed with status: {StatusCode}", response.StatusCode);
            return null;
        }

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrEmpty(responseContent))
        {
            return null;
        }

        using var jsonDoc = JsonDocument.Parse(responseContent);
        return jsonDoc.RootElement.Clone();
    }

    private async Task<JsonElement?> TryRetryGwApiCallOnInvalidTokenAsync(
        JsonElement root,
        string method,
        string body,
        string arl,
        string? sid,
        CancellationToken cancellationToken)
    {
        if (!IsInvalidGwTokenError(root))
        {
            return null;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("GW token invalid, refreshing token for {Method}", method);
        }

        _cachedGwToken = null;
        _cachedGwTokenExpiry = DateTime.MinValue;
        var refreshed = await GetGwTokenAsync(arl, sid, cancellationToken, forceRefresh: true);
        if (string.IsNullOrWhiteSpace(refreshed))
        {
            return null;
        }

        return await CallGwApiAsync(method, body, arl, sid, cancellationToken);
    }

    private static bool IsInvalidGwTokenError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var errorElement) || errorElement.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        var errorText = errorElement.ToString();
        return !string.IsNullOrWhiteSpace(errorText)
               && errorText.Contains("VALID_TOKEN_REQUIRED", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> GetGwTokenAsync(string arl, string? sid, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        if (!forceRefresh && !string.IsNullOrWhiteSpace(_cachedGwToken) && DateTime.UtcNow < _cachedGwTokenExpiry)
        {
            return _cachedGwToken;
        }

        try
        {
            using var httpClient = _httpClientFactory.CreateClient(LyricsClientName);
            var url = DeezerGwUserDataUrl;
            using var request = new HttpRequestMessage(HttpMethod.Post, url);

            request.Headers.Add(UserAgentHeader, "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/96.0.4664.110 Safari/537.36");
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            var cookieValue = $"arl={arl}";
            if (!string.IsNullOrEmpty(sid))
            {
                cookieValue += $"; sid={sid}";
            }
            request.Headers.Add("Cookie", cookieValue);
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, ApplicationJson);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GW token bootstrap failed: {StatusCode}", response.StatusCode);
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(responseContent))
            {
                return null;
            }

            using var jsonDoc = JsonDocument.Parse(responseContent);
            var root = jsonDoc.RootElement;
            if (root.TryGetProperty("results", out var results) &&
                results.TryGetProperty("checkForm", out var tokenElement))
            {
                var token = tokenElement.GetString();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    _cachedGwToken = token;
                    _cachedGwTokenExpiry = DateTime.UtcNow.AddMinutes(GwTokenTtlMinutes);
                    return token;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to obtain GW token");
        }

        return null;
    }

    /// <summary>
    /// Validate and prepare lyrics for .lrc file creation
    /// Only creates .lrc files for synchronized lyrics
    /// </summary>
    public bool ShouldCreateLrcFile(LyricsBase lyrics)
    {
        if (lyrics == null)
        {
            _logger.LogDebug("No lyrics provided for LRC validation");
            return false;
        }

        if (!string.IsNullOrEmpty(lyrics.ErrorMessage))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Lyrics have error message, skipping LRC creation: {Error}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(lyrics.ErrorMessage));            }
            return false;
        }

        if (!lyrics.IsSynced())
        {
            _logger.LogDebug("Lyrics are not synchronized, skipping LRC creation");
            return false;
        }

        if (!lyrics.CanSaveLrcSidecar())
        {
            _logger.LogDebug(
                "Synchronized lyrics source format {SourceFormat} cannot be saved as LRC, skipping LRC creation",
                lyrics.SyncedLyricsSourceFormat);
            return false;
        }

        if (!HasLyricsLines(lyrics.SyncedLyrics))
        {
            _logger.LogDebug("No synchronized lyrics lines found, skipping LRC creation");
            return false;
        }

        var syncedLyrics = lyrics.SyncedLyrics!;
        var validLines = syncedLyrics.Count(l => l.IsValid());
        if (validLines < 1)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Insufficient valid synchronized lyrics lines ({Count}), skipping LRC creation", validLines);            }
            return false;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Lyrics validation passed, LRC file can be created with {Count} lines", validLines);        }
        return true;
    }

    /// <summary>
    /// Generate LRC content from lyrics with metadata
    /// </summary>
    public string GenerateLrcContent(LyricsBase lyrics, string? title = null, string? artist = null, string? album = null)
    {
        if (!ShouldCreateLrcFile(lyrics))
        {
            return string.Empty;
        }

        return lyrics.GenerateLrcContent(title, artist, album);
    }

    public string GenerateEnhancedLrcContent(LyricsBase lyrics, string? title = null, string? artist = null, string? album = null)
    {
        if (!ShouldCreateLrcFile(lyrics) || !lyrics.HasEnhancedSynchronizedLyrics())
        {
            return string.Empty;
        }

        return lyrics.GenerateEnhancedLrcContent(title, artist, album);
    }

    /// <summary>
    /// Save lyrics to file using priority implementation
    /// Priority: .lrc for synchronized lyrics, .txt for unsynchronized lyrics as fallback
    /// </summary>
    public async Task<LyricsSaveResult> SaveLyricsAsync(
        Track track,
        (string FilePath, string Filename, string ExtrasPath, string CoverPath, string ArtistPath) paths,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("SaveLyricsAsync called for track {TrackId}, SaveLyrics: {SaveLyrics}, SyncedLyrics: {SyncedLyrics}",
                track.Id, settings.SaveLyrics, settings.SyncedLyrics);        }

        // Check if lyrics saving is enabled (either general lyrics or synced lyrics)
        if (!ShouldHandleLyricsBySettings(settings))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Lyrics saving disabled for track {TrackId}", track.Id);            }
            return LyricsSaveResult.Empty;
        }

        try
        {
            var lyrics = await ResolveLyricsAsync(track, settings, cancellationToken);
            if (lyrics == null)
            {
            _logger.LogWarning("Lyrics resolution returned null for track {TrackId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(track.Id));
                return LyricsSaveResult.Empty;
            }

            return await SaveLyricsAsync(lyrics, track, paths, settings, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error saving lyrics for track {TrackId}", track.Id);
        }

        return LyricsSaveResult.Empty;
    }

    /// <summary>
    /// Save lyrics to file using already-fetched lyrics data.
    /// </summary>
    public async Task<LyricsSaveResult> SaveLyricsAsync(
        LyricsBase lyrics,
        Track track,
        (string FilePath, string Filename, string ExtrasPath, string CoverPath, string ArtistPath) paths,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("SaveLyricsAsync (prefetched) called for track {TrackId}, SaveLyrics: {SaveLyrics}, SyncedLyrics: {SyncedLyrics}",
                track.Id, settings.SaveLyrics, settings.SyncedLyrics);        }

        if (!ShouldHandleLyricsBySettings(settings))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Lyrics saving disabled for track {TrackId}", track.Id);            }
            return LyricsSaveResult.Empty;
        }

        if (lyrics == null || !string.IsNullOrEmpty(lyrics.ErrorMessage))
        {
            _logger.LogWarning("Failed to fetch lyrics for track {TrackId}: {Error}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(track.Id), DeezSpoTag.Core.Security.LogSanitizer.OneLine(lyrics?.ErrorMessage ?? "Unknown error"));
            return LyricsSaveResult.Empty;
        }

        var saveState = new LyricsSaveState(paths, settings);
        var overwriteSidecar = ShouldOverwriteLyricsSidecar(settings);
        saveState.HadExistingLrc = System.IO.File.Exists(saveState.LrcPath);
        saveState.HadExistingElrc = System.IO.File.Exists(saveState.ElrcPath);
        saveState.HadExistingTtml = System.IO.File.Exists(saveState.TtmlPath);
        saveState.HadExistingTxt = System.IO.File.Exists(saveState.TxtPath);

        await TrySaveSyncedLrcAsync(lyrics, track, settings, overwriteSidecar, saveState, cancellationToken);
        await TrySaveEnhancedSynchronizedLyricsAsync(lyrics, track, settings, overwriteSidecar, saveState, cancellationToken);
        await TrySaveTtmlAsync(lyrics, settings, overwriteSidecar, saveState, cancellationToken);
        await TrySaveUnsyncedTxtAsync(lyrics, track, settings, overwriteSidecar, saveState, cancellationToken);
        RemoveTxtWhenRichLyricsExist(saveState);

        if (!saveState.SavedLyrics)
        {
            _logger.LogWarning("No lyrics saved for track {TrackId} - SaveLyrics: {SaveLyrics}, SyncedLyrics: {SyncedLyrics}, HasSynced: {HasSynced}, HasUnsynced: {HasUnsynced}",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(track.Id), settings.SaveLyrics, settings.SyncedLyrics, lyrics.IsSynced(), !string.IsNullOrEmpty(lyrics.UnsyncedLyrics));
        }

        return CreateLyricsSaveResult(saveState);
    }

    private static LyricsSaveResult CreateLyricsSaveResult(LyricsSaveState state)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddWrittenLyricsFile(files, "lrc", state.LrcPath);
        AddWrittenLyricsFile(files, "elrc", state.ElrcPath);
        if (IsWordSynchronizedTtmlFile(state.TtmlPath))
        {
            AddWrittenLyricsFile(files, "ttml", state.TtmlPath);
        }
        AddWrittenLyricsFile(files, "txt", state.TxtPath);
        return files.Count == 0 ? LyricsSaveResult.Empty : new LyricsSaveResult(files);
    }

    private static void AddWrittenLyricsFile(IDictionary<string, string> files, string format, string path)
    {
        if (System.IO.File.Exists(path))
        {
            files[format] = DownloadPathResolver.NormalizeDisplayPath(path);
        }
    }

    private static bool IsWordSynchronizedTtmlFile(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return false;
        }

        try
        {
            return AppleLyricsService.IsWordSyncedTtml(System.IO.File.ReadAllText(path));
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private sealed class LyricsSaveState((string FilePath, string Filename, string ExtrasPath, string CoverPath, string ArtistPath) paths, DeezSpoTagSettings settings)
    {
        public string LrcPath { get; } = Path.Join(paths.FilePath, $"{paths.Filename}.lrc");
        public string ElrcPath { get; } = Path.Join(paths.FilePath, $"{paths.Filename}.elrc");
        public string TtmlPath { get; } = Path.Join(paths.FilePath, $"{paths.Filename}.ttml");
        public string TxtPath { get; } = Path.Join(paths.FilePath, $"{paths.Filename}.txt");
        public bool RichOutputRequested { get; } = ShouldSaveSyncedLrc(settings) || ShouldSaveEnhancedSynchronizedLyrics(settings) || ShouldOutputTtmlBySettings(settings);
        public bool HadExistingLrc { get; set; }
        public bool HadExistingElrc { get; set; }
        public bool HadExistingTtml { get; set; }
        public bool HadExistingTxt { get; set; }
        public bool SavedLyrics { get; set; }
        public bool SavedLrc { get; set; }
        public bool SavedElrc { get; set; }
        public bool SavedTtml { get; set; }
    }

    private async Task TrySaveSyncedLrcAsync(
        LyricsBase lyrics,
        Track track,
        DeezSpoTagSettings settings,
        bool overwriteSidecar,
        LyricsSaveState state,
        CancellationToken cancellationToken)
    {
        if (!ShouldSaveSyncedLrc(settings))
        {
            return;
        }
        if (!lyrics.IsSynced())
        {
            return;
        }

        try
        {
            var lrcContent = GenerateLrcContent(lyrics, track.Title, track.MainArtist?.Name, track.Album?.Title);
            if (string.IsNullOrEmpty(lrcContent))
            {
                _logger.LogWarning("Generated LRC content is empty for track {TrackId}", track.Id);
                return;
            }

            if (!overwriteSidecar && state.HadExistingLrc)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Keeping existing LRC sidecar at {LrcPath}", state.LrcPath);                }
            }
            else
            {
                await System.IO.File.WriteAllTextAsync(state.LrcPath, lrcContent, cancellationToken);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Successfully saved synchronized lyrics to {LrcPath}", state.LrcPath);                }
            }
            state.SavedLyrics = true;
            state.SavedLrc = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error downloading synchronized lyrics.");
        }
    }

    private async Task TrySaveEnhancedSynchronizedLyricsAsync(
        LyricsBase lyrics,
        Track track,
        DeezSpoTagSettings settings,
        bool overwriteSidecar,
        LyricsSaveState state,
        CancellationToken cancellationToken)
    {
        if (!ShouldSaveEnhancedSynchronizedLyrics(settings) || !lyrics.HasEnhancedSynchronizedLyrics())
        {
            return;
        }

        try
        {
            var elrcContent = GenerateEnhancedLrcContent(lyrics, track.Title, track.MainArtist?.Name, track.Album?.Title);
            if (string.IsNullOrEmpty(elrcContent))
            {
                _logger.LogWarning("Generated enhanced synchronized lyrics content is empty for track {TrackId}", track.Id);
                return;
            }

            if (!overwriteSidecar && state.HadExistingElrc)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Keeping existing enhanced synchronized lyrics sidecar at {ElrcPath}", state.ElrcPath);
                }
            }
            else
            {
                await System.IO.File.WriteAllTextAsync(state.ElrcPath, elrcContent, cancellationToken);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Successfully saved enhanced synchronized lyrics to {ElrcPath}", state.ElrcPath);
                }
            }

            state.SavedLyrics = true;
            state.SavedElrc = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error downloading enhanced synchronized lyrics.");
        }
    }

    private async Task TrySaveTtmlAsync(
        LyricsBase lyrics,
        DeezSpoTagSettings settings,
        bool overwriteSidecar,
        LyricsSaveState state,
        CancellationToken cancellationToken)
    {
        if (!ShouldSaveTtml(settings, lyrics))
        {
            return;
        }

        try
        {
            if (!overwriteSidecar && state.HadExistingTtml)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Keeping existing TTML sidecar at {TtmlPath}", state.TtmlPath);                }
            }
            else
            {
                await System.IO.File.WriteAllTextAsync(state.TtmlPath, lyrics.TtmlLyrics!, cancellationToken);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Successfully saved TTML lyrics to {TtmlPath}", state.TtmlPath);                }
            }
            state.SavedLyrics = true;
            state.SavedTtml = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error downloading TTML lyrics.");
        }
    }

    private async Task TrySaveUnsyncedTxtAsync(
        LyricsBase lyrics,
        Track track,
        DeezSpoTagSettings settings,
        bool overwriteSidecar,
        LyricsSaveState state,
        CancellationToken cancellationToken)
    {
        if (!ShouldSavePlainLyrics(settings) || string.IsNullOrEmpty(lyrics.UnsyncedLyrics))
        {
            return;
        }

        if (ShouldSkipUnsyncedTxtWrite(state))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Skipping unsynchronized lyrics for track {TrackId} because LRC or TTML exists.", track.Id);            }
            return;
        }

        await TryWriteUnsyncedTxtAsync(lyrics.UnsyncedLyrics, overwriteSidecar, state, cancellationToken);
    }

    private static bool ShouldSkipUnsyncedTxtWrite(LyricsSaveState state)
    {
        var hasExistingRichLyrics = state.SavedLrc
            || state.SavedElrc
            || state.SavedTtml
            || state.HadExistingLrc
            || state.HadExistingElrc
            || state.HadExistingTtml
            || System.IO.File.Exists(state.LrcPath)
            || System.IO.File.Exists(state.ElrcPath)
            || System.IO.File.Exists(state.TtmlPath);
        return state.RichOutputRequested && (state.SavedLrc || state.SavedElrc || state.SavedTtml || hasExistingRichLyrics);
    }

    private async Task TryWriteUnsyncedTxtAsync(
        string unsyncedLyrics,
        bool overwriteSidecar,
        LyricsSaveState state,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!overwriteSidecar && state.HadExistingTxt)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Keeping existing TXT lyrics sidecar at {TxtPath}", state.TxtPath);                }
            }
            else
            {
                await System.IO.File.WriteAllTextAsync(state.TxtPath, unsyncedLyrics, cancellationToken);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Successfully saved unsynchronized lyrics to {TxtPath}", state.TxtPath);                }
            }
            state.SavedLyrics = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error downloading unsynchronized lyrics.");
        }
    }

    private void RemoveTxtWhenRichLyricsExist(LyricsSaveState state)
    {
        if (!state.RichOutputRequested
            || !(state.SavedLrc || state.SavedElrc || state.SavedTtml || state.HadExistingLrc || state.HadExistingElrc || state.HadExistingTtml)
            || !System.IO.File.Exists(state.TxtPath))
        {
            return;
        }

        try
        {
            System.IO.File.Delete(state.TxtPath);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Removed TXT lyrics sidecar after rich-lyrics upgrade at {TxtPath}", state.TxtPath);            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed removing TXT lyrics sidecar after upgrade at {TxtPath}", state.TxtPath);            }
        }
    }

    private static bool ShouldOverwriteLyricsSidecar(DeezSpoTagSettings settings)
    {
        var overwritePolicy = string.IsNullOrWhiteSpace(settings.OverwriteFile)
            ? "y"
            : settings.OverwriteFile.Trim().ToLowerInvariant();
        return overwritePolicy is "y" or "t";
    }

    private static bool ShouldSavePlainLyrics(DeezSpoTagSettings settings)
    {
        return IsLyricsGateEnabled(settings)
            && IsLyricsTypeSelected(settings, UnsyncedLyricsType)
            && settings.SaveLyrics;
    }

    private static bool ShouldSaveSyncedLrc(DeezSpoTagSettings settings)
    {
        var outputFormats = ParseLyricsOutputFormats(settings.LrcFormat);
        return settings.SyncedLyrics
            && IsLyricsGateEnabled(settings)
            && outputFormats.Contains("lrc")
            && (IsLyricsTypeSelected(settings, LyricsType)
                || IsLyricsTypeSelected(settings, SyllableLyricsType)
                || (settings.SynthesizeLrcFromTtml
                    && IsLyricsTypeSelected(settings, TtmlLyricsType)));
    }

    private static bool ShouldSaveEnhancedSynchronizedLyrics(DeezSpoTagSettings settings)
    {
        var outputFormats = ParseLyricsOutputFormats(settings.LrcFormat);
        return settings.SyncedLyrics
            && IsLyricsGateEnabled(settings)
            && outputFormats.Contains("elrc")
            && (IsLyricsTypeSelected(settings, LyricsType)
                || IsLyricsTypeSelected(settings, SyllableLyricsType));
    }

    private static bool ShouldSaveTtml(DeezSpoTagSettings settings, LyricsBase lyrics)
    {
        if (!ShouldOutputTtmlBySettings(settings))
        {
            return false;
        }

        return DeezSpoTag.Services.Apple.AppleLyricsService.IsWordSyncedTtml(lyrics?.TtmlLyrics);
    }

    private static bool ShouldOutputTtmlBySettings(DeezSpoTagSettings settings)
    {
        if (!settings.SyncedLyrics || !IsLyricsGateEnabled(settings))
        {
            return false;
        }

        return IsLyricsTypeSelected(settings, TtmlLyricsType)
            && ParseLyricsOutputFormats(settings.LrcFormat).Contains("ttml");
    }

    private static HashSet<string> ParseLyricsOutputFormats(string? value)
    {
        var formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var normalized in NormalizeLyricsOutputFormatToken(token))
            {
                formats.Add(normalized);
            }
        }

        if (formats.Count == 0)
        {
            formats.Add("lrc");
            formats.Add("elrc");
            formats.Add("ttml");
        }

        return formats;
    }

    private static string NormalizeLyricsOutputFormat(string? value)
    {
        var formats = ParseLyricsOutputFormats(value);
        if (formats.Contains("lrc") && formats.Contains("elrc") && formats.Contains("ttml"))
        {
            return "richlyrics";
        }

        if (formats.Contains("lrc") && formats.Contains("ttml"))
        {
            return "both";
        }

        if (formats.Contains("ttml"))
        {
            return "ttml";
        }

        if (formats.Contains("elrc"))
        {
            return "elrc";
        }

        return "lrc";
    }

    private static IReadOnlyList<string> NormalizeLyricsOutputFormatToken(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "lrc" => ["lrc"],
            "standard-lrc" => ["lrc"],
            "synced" => ["lrc"],
            "synced-lyrics" => ["lrc"],
            "elrc" => ["elrc"],
            "enhanced-lrc" => ["elrc"],
            "enhanced-synchronized-lyrics" => ["elrc"],
            "enhanced-synchronised-lyrics" => ["elrc"],
            "ttml" => ["ttml"],
            "both" => ["lrc", "elrc", "ttml"],
            "richlyrics" => ["lrc", "elrc", "ttml"],
            "rich-lyrics" => ["lrc", "elrc", "ttml"],
            LyricsType => ["lrc", "elrc", "ttml"],
            "lrc+ttml" => ["lrc", "ttml"],
            "ttml+lrc" => ["lrc", "ttml"],
            "all" => ["lrc", "elrc", "ttml"],
            _ => []
        };

    private static bool ShouldHandleLyricsBySettings(DeezSpoTagSettings settings)
    {
        return LyricsSettingsPolicy.CanFetchLyrics(settings);
    }

    private static bool IsLyricsGateEnabled(DeezSpoTagSettings settings)
    {
        return LyricsSettingsPolicy.IsLyricsGateEnabled(settings);
    }

    private static bool IsLyricsTypeSelected(DeezSpoTagSettings settings, string type)
    {
        return ParseSelectedLyricsTypes(settings).Contains(type);
    }

    private static HashSet<string> ParseSelectedLyricsTypes(DeezSpoTagSettings settings)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var normalized in (settings.LrcType ?? string.Empty)
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(NormalizeLyricsTypeToken)
                     .Where(static token => !string.IsNullOrWhiteSpace(token)))
        {
            selected.Add(normalized);
        }

        if (selected.Count == 0)
        {
            selected.Add(LyricsType);
            selected.Add(SyllableLyricsType);
            selected.Add(TtmlLyricsType);
            selected.Add(UnsyncedLyricsType);
        }

        return selected;
    }

    private static string NormalizeLyricsTypeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        return token.Trim().ToLowerInvariant() switch
        {
            LyricsType => LyricsType,
            "synced-lyrics" => LyricsType,
            SyllableLyricsType => SyllableLyricsType,
            "time-synced-lyrics" => SyllableLyricsType,
            "timesynced-lyrics" => SyllableLyricsType,
            "time_synced_lyrics" => SyllableLyricsType,
            "syllablelyrics" => SyllableLyricsType,
            TtmlLyricsType => TtmlLyricsType,
            "ttml" => TtmlLyricsType,
            "ttmllyrics" => TtmlLyricsType,
            "ttml_lyrics" => TtmlLyricsType,
            UnsyncedLyricsType => UnsyncedLyricsType,
            "unsyncedlyrics" => UnsyncedLyricsType,
            "unsynced" => UnsyncedLyricsType,
            "unsynchronized-lyrics" => UnsyncedLyricsType,
            "unsynchronised-lyrics" => UnsyncedLyricsType,
            _ => string.Empty
        };
    }
}
