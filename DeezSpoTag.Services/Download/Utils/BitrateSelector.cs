using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Download;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Crypto;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Utils;

/// <summary>
/// EXACT PORT of getPreferredBitrate function from deezspotag
/// Handles bitrate selection and URL generation for tracks
/// </summary>
public class BitrateSelector
{
    public const string ErrorMappedButQualityUnavailable = "mapped_but_quality_unavailable";
    public const string ErrorMappedButQualityUnavailable360 = "mapped_but_quality_unavailable_360";
    public const string ErrorMappedButGeoBlocked = "mapped_but_geo_blocked";
    public const string ErrorMappedButLicenseBlocked = "mapped_but_license_blocked";
    public const string ErrorTransientTokenFailure = "transient_token_failure";
    public const string ErrorTransientNetworkFailure = "transient_network_failure";
    private const string Mp3MiscFormat = "MP3_MISC";

    private readonly ILogger<BitrateSelector> _logger;
    private readonly AuthenticatedDeezerService _authenticatedDeezerService;
    private sealed record FailedFormatContext(
        bool ShouldFallback,
        bool Is360Format,
        int PreferredBitrate,
        string FormatName,
        DeezSpoTag.Core.Models.Track Track,
        IDownloadListener? Listener,
        string DownloadUuid,
        BitrateResolutionState ResolutionState,
        AvailabilityProbeState AvailabilityState,
        UrlResult LatestResult);

    // Format mappings (EXACT PORT from deezspotag)
    private static readonly Dictionary<int, string> FormatsNon360 = new()
    {
        { 9, "FLAC" },      // TrackFormats.FLAC
        { 3, "MP3_320" },   // TrackFormats.MP3_320
        { 1, "MP3_128" }    // TrackFormats.MP3_128
    };

    private static readonly Dictionary<int, string> Formats360 = new()
    {
        { 15, "MP4_RA3" },  // TrackFormats.MP4_RA3
        { 14, "MP4_RA2" },  // TrackFormats.MP4_RA2
        { 13, "MP4_RA1" }   // TrackFormats.MP4_RA1
    };

    public BitrateSelector(ILogger<BitrateSelector> logger, AuthenticatedDeezerService authenticatedDeezerService)
    {
        _logger = logger;
        _authenticatedDeezerService = authenticatedDeezerService;
    }

    /// <summary>
    /// EXACT PORT of getPreferredBitrate function from deezspotag
    /// </summary>
    public async Task<int> GetPreferredBitrateAsync(
        DeezSpoTag.Core.Models.Track track,
        int preferredBitrate,
        bool shouldFallback,
        bool feelingLucky,
        string downloadUuid,
        IDownloadListener? listener)
    {
        var deezerClient = await GetAuthenticatedClientOrThrowAsync();
        var availabilityState = new AvailabilityProbeState();

        if (track.IsLocal)
        {
            return await ResolveLocalTrackBitrateAsync(track, feelingLucky, deezerClient);
        }

        var formats = BuildFormatPreferenceMap(preferredBitrate, shouldFallback, out var is360Format);
        var resolutionState = new BitrateResolutionState(track.FallbackID != 0);
        await CheckAndRenewTrackTokenAsync(track, deezerClient);

        foreach (var (formatNumber, formatName) in GetFormatCandidates(formats, preferredBitrate))
        {
            var attempt = await TryResolveFormatAsync(
                track,
                formatName,
                formatNumber,
                feelingLucky,
                deezerClient,
                resolutionState.HasAlternative,
                availabilityState);

            resolutionState.HasAlternative = attempt.HasAlternative;
            if (!string.IsNullOrEmpty(attempt.Url))
            {
                ApplyResolvedFallbackTrack(track, attempt.ResolvedFallbackTrack);
                track.Urls[formatName] = attempt.Url;
                return formatNumber;
            }

            HandleFailedFormatAttempt(
                new FailedFormatContext(
                    shouldFallback,
                    is360Format,
                    preferredBitrate,
                    formatName,
                    track,
                    listener,
                    downloadUuid,
                    resolutionState,
                    availabilityState,
                    attempt.LastResult));
        }

        return await ResolveDefaultBitrateAsync(
            track,
            preferredBitrate,
            feelingLucky,
            deezerClient,
            availabilityState,
            is360Format);
    }

    /// <summary>
    /// EXACT PORT of getCorrectURL from deezspotag
    /// </summary>
    private async Task<UrlResult> GetCorrectUrlAsync(DeezSpoTag.Core.Models.Track track, string formatName, int formatNumber, bool feelingLucky, DeezerClient deezerClient, int refreshAttempt = 0)
    {
        var wrongLicense = HasLicenseRestriction(deezerClient, formatName);
        var mediaAttempt = await TryResolveMediaUrlAsync(track, formatName, deezerClient, refreshAttempt);
        if (mediaAttempt.ShouldRetry)
        {
            return await GetCorrectUrlAsync(
                track,
                formatName,
                formatNumber,
                feelingLucky,
                deezerClient,
                refreshAttempt + 1);
        }

        wrongLicense |= mediaAttempt.WrongLicense;
        var isGeolocked = mediaAttempt.IsGeolocked;
        var trackTokenExpired = mediaAttempt.TrackTokenExpired;
        var transientNetworkFailure = mediaAttempt.TransientNetworkFailure;
        var url = mediaAttempt.Url;

        if (string.IsNullOrEmpty(url))
        {
            url = await TryResolveCryptedFallbackUrlAsync(
                track,
                formatName,
                formatNumber,
                feelingLucky,
                wrongLicense,
                isGeolocked);
        }

        return new UrlResult
        {
            Url = url,
            WrongLicense = wrongLicense,
            IsGeolocked = isGeolocked,
            TrackTokenExpired = trackTokenExpired,
            TransientNetworkFailure = transientNetworkFailure,
            QualityUnavailable = string.IsNullOrWhiteSpace(url)
        };
    }

    private async Task<DeezerClient> GetAuthenticatedClientOrThrowAsync()
    {
        var deezerClient = await _authenticatedDeezerService.GetAuthenticatedClientAsync();
        if (deezerClient == null || !deezerClient.LoggedIn || deezerClient.CurrentUser == null)
        {
            throw new BitrateException("User not logged in", "PreferredBitrateNotFound");
        }

        return deezerClient;
    }

    private async Task<int> ResolveLocalTrackBitrateAsync(
        DeezSpoTag.Core.Models.Track track,
        bool feelingLucky,
        DeezerClient deezerClient)
    {
        var url = await GetCorrectUrlAsync(track, Mp3MiscFormat, 0, feelingLucky, deezerClient);
        if (string.IsNullOrEmpty(url.Url))
        {
            throw new BitrateException("Local track URL not available", "PreferredBitrateNotFound");
        }

        track.Urls[Mp3MiscFormat] = url.Url;
        return 0;
    }

    private static Dictionary<int, string> BuildFormatPreferenceMap(
        int preferredBitrate,
        bool shouldFallback,
        out bool is360Format)
    {
        is360Format = Formats360.ContainsKey(preferredBitrate);
        if (shouldFallback && !is360Format)
        {
            return new Dictionary<int, string>(FormatsNon360);
        }

        var formats = new Dictionary<int, string>(Formats360);
        foreach (var (key, value) in FormatsNon360)
        {
            formats[key] = value;
        }

        return formats;
    }

    private static IEnumerable<(int Number, string Name)> GetFormatCandidates(
        IReadOnlyDictionary<int, string> formats,
        int preferredBitrate)
    {
        return formats
            .Where(kvp => kvp.Key <= preferredBitrate)
            .OrderByDescending(kvp => kvp.Key)
            .Select(kvp => (kvp.Key, kvp.Value));
    }

    private async Task<FormatAttemptResult> TryResolveFormatAsync(
        DeezSpoTag.Core.Models.Track originalTrack,
        string formatName,
        int formatNumber,
        bool feelingLucky,
        DeezerClient deezerClient,
        bool hasAlternative,
        AvailabilityProbeState availabilityState)
    {
        var currentTrack = originalTrack;
        var latestResult = await GetCorrectUrlAsync(currentTrack, formatName, formatNumber, feelingLucky, deezerClient);
        availabilityState.Observe(latestResult);

        var url = latestResult.Url;
        DeezSpoTag.Core.Models.Track? fallbackTrack = null;
        var canContinueWithAlternative = hasAlternative;

        while (string.IsNullOrEmpty(url) && canContinueWithAlternative)
        {
            fallbackTrack = await TryResolveFallbackTrackAsync(currentTrack, deezerClient);
            if (fallbackTrack == null)
            {
                canContinueWithAlternative = false;
                break;
            }

            currentTrack = fallbackTrack;
            canContinueWithAlternative = currentTrack.FallbackID != 0;
            latestResult = await GetCorrectUrlAsync(currentTrack, formatName, formatNumber, feelingLucky, deezerClient);
            availabilityState.Observe(latestResult);
            url = latestResult.Url;
        }

        return new FormatAttemptResult
        {
            Url = url,
            LastResult = latestResult,
            ResolvedFallbackTrack = fallbackTrack,
            HasAlternative = canContinueWithAlternative
        };
    }

    private async Task<DeezSpoTag.Core.Models.Track?> TryResolveFallbackTrackAsync(
        DeezSpoTag.Core.Models.Track currentTrack,
        DeezerClient deezerClient)
    {
        try
        {
            var fallbackId = currentTrack.FallbackID.ToString();
            var newTrackData = await deezerClient.GetTrackWithFallbackAsync(fallbackId);
            if (newTrackData == null)
            {
                return null;
            }

            var fallbackTrack = new DeezSpoTag.Core.Models.Track
            {
                Id = newTrackData.SngId.ToString(),
                TrackToken = newTrackData.TrackToken,
                TrackTokenExpiration = newTrackData.TrackTokenExpire,
                TrackTokenExpire = newTrackData.TrackTokenExpire,
                MD5 = newTrackData.Md5Origin,
                MediaVersion = newTrackData.MediaVersion.ToString(),
                FallbackID = 0,
                FileSizes = new Dictionary<string, int>()
            };

            TrySetFileSize(fallbackTrack.FileSizes, "mp3_128", newTrackData.FilesizeMp3128);
            TrySetFileSize(fallbackTrack.FileSizes, "mp3_320", newTrackData.FilesizeMp3320);
            TrySetFileSize(fallbackTrack.FileSizes, "flac", newTrackData.FilesizeFlac);
            TrySetFileSize(fallbackTrack.FileSizes, "mp4_ra1", newTrackData.FilesizeMp4Ra1);
            TrySetFileSize(fallbackTrack.FileSizes, "mp4_ra2", newTrackData.FilesizeMp4Ra2);
            TrySetFileSize(fallbackTrack.FileSizes, "mp4_ra3", newTrackData.FilesizeMp4Ra3);

            return fallbackTrack;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to get fallback track for {TrackId}", currentTrack.FallbackID);
            return null;
        }
    }

    private static void TrySetFileSize(IDictionary<string, int> fileSizes, string key, int? value)
    {
        if (value.HasValue && value.Value > 0)
        {
            fileSizes[key] = value.Value;
        }
    }

    private static void ApplyResolvedFallbackTrack(
        DeezSpoTag.Core.Models.Track originalTrack,
        DeezSpoTag.Core.Models.Track? fallbackTrack)
    {
        if (fallbackTrack == null)
        {
            return;
        }

        originalTrack.TrackToken = fallbackTrack.TrackToken;
        originalTrack.TrackTokenExpiration = fallbackTrack.TrackTokenExpiration;
        originalTrack.TrackTokenExpire = fallbackTrack.TrackTokenExpire;
        originalTrack.MD5 = fallbackTrack.MD5;
        originalTrack.MediaVersion = fallbackTrack.MediaVersion;
    }

    private static void HandleFailedFormatAttempt(FailedFormatContext context)
    {
        if (!context.ShouldFallback)
        {
            ThrowAvailabilityFailure(
                context.LatestResult,
                context.AvailabilityState,
                context.Is360Format,
                context.PreferredBitrate,
                context.FormatName);
        }

        if (context.ResolutionState.FallbackNotified || context.Listener == null || string.IsNullOrEmpty(context.DownloadUuid))
        {
            return;
        }

        context.ResolutionState.FallbackNotified = true;
        context.Listener.OnDownloadInfo(
            new SingleDownloadObject { Track = context.Track },
            "Falling back to lower quality",
            "bitrateFallback");
    }

    private static void ThrowAvailabilityFailure(
        UrlResult latestResult,
        AvailabilityProbeState availabilityState,
        bool is360Format,
        int preferredBitrate,
        string formatName)
    {
        if (latestResult.WrongLicense)
        {
            throw new BitrateException(
                $"User does not have permission to stream {formatName}",
                ErrorMappedButLicenseBlocked);
        }

        if (latestResult.IsGeolocked)
        {
            throw new BitrateException("Track not available in country", ErrorMappedButGeoBlocked);
        }

        var availabilityError = ResolveAvailabilityErrorCode(availabilityState, is360Format);
        throw new BitrateException(
            BuildAvailabilityErrorMessage(availabilityError, preferredBitrate, formatName),
            availabilityError);
    }

    private async Task<int> ResolveDefaultBitrateAsync(
        DeezSpoTag.Core.Models.Track track,
        int preferredBitrate,
        bool feelingLucky,
        DeezerClient deezerClient,
        AvailabilityProbeState availabilityState,
        bool is360Format)
    {
        if (is360Format)
        {
            var availabilityError = ResolveAvailabilityErrorCode(availabilityState, is360Request: true);
            throw new BitrateException(
                BuildAvailabilityErrorMessage(availabilityError, preferredBitrate, "MP4_RA"),
                availabilityError);
        }

        var defaultUrlResult = await GetCorrectUrlAsync(track, Mp3MiscFormat, 8, feelingLucky, deezerClient);
        availabilityState.Observe(defaultUrlResult);
        track.Urls[Mp3MiscFormat] = defaultUrlResult.Url ?? "";
        if (string.IsNullOrWhiteSpace(defaultUrlResult.Url))
        {
            var availabilityError = ResolveAvailabilityErrorCode(availabilityState, is360Request: false);
            throw new BitrateException(
                BuildAvailabilityErrorMessage(availabilityError, preferredBitrate, Mp3MiscFormat),
                availabilityError);
        }

        return 8;
    }

    private static bool HasLicenseRestriction(DeezerClient deezerClient, string formatName)
    {
        var user = deezerClient.CurrentUser!;
        var requiresLossless = formatName == "FLAC" || formatName.StartsWith("MP4_RA", StringComparison.Ordinal);
        var requiresHq = formatName == "MP3_320";
        var lacksLossless = requiresLossless && user.CanStreamLossless != true;
        var lacksHq = requiresHq && user.CanStreamHq != true;
        return lacksLossless || lacksHq;
    }

    private static bool HasValidFileSizeForFormat(DeezSpoTag.Core.Models.Track track, string formatName)
    {
        var fileSizeKey = formatName.ToLower();
        return track.FileSizes?.ContainsKey(fileSizeKey) == true && track.FileSizes[fileSizeKey] > 0;
    }

    private async Task<MediaUrlAttempt> TryResolveMediaUrlAsync(
        DeezSpoTag.Core.Models.Track track,
        string formatName,
        DeezerClient deezerClient,
        int refreshAttempt)
    {
        if (!HasValidFileSizeForFormat(track, formatName) || string.IsNullOrWhiteSpace(track.TrackToken))
        {
            return MediaUrlAttempt.Empty;
        }

        try
        {
            var mediaResult = await deezerClient.GetTrackUrlWithStatusAsync(track.TrackToken, formatName);
            var url = mediaResult.Url;
            var tokenExpired = mediaResult.ErrorCode == 2001 && string.IsNullOrWhiteSpace(url);

            if (tokenExpired && refreshAttempt < 1 && await RefreshTrackTokenAsync(track, deezerClient))
            {
                return new MediaUrlAttempt
                {
                    ShouldRetry = true
                };
            }

            return new MediaUrlAttempt
            {
                Url = url,
                TrackTokenExpired = tokenExpired
            };
        }
        catch (DeezerException ex) when (ex.ErrorCode == "WrongLicense")
        {
            return new MediaUrlAttempt { WrongLicense = true };
        }
        catch (DeezerException ex) when (ex.ErrorCode == "WrongGeolocation")
        {
            return new MediaUrlAttempt { IsGeolocked = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Media API failed for format {Format}", formatName);
            return new MediaUrlAttempt { TransientNetworkFailure = true };
        }
    }

    private async Task<string?> TryResolveCryptedFallbackUrlAsync(
        DeezSpoTag.Core.Models.Track track,
        string formatName,
        int formatNumber,
        bool feelingLucky,
        bool wrongLicense,
        bool isGeolocked)
    {
        if (!CanTryCryptedFallback(track, feelingLucky, wrongLicense, isGeolocked))
        {
            _logger.LogWarning(
                "Cannot try fallback URL for track {TrackId}: feelingLucky={FeelingLucky}, hasMD5={HasMD5}, hasMediaVersion={HasMediaVersion}, wrongLicense={WrongLicense}, isGeolocked={IsGeolocked}",
                track.Id,
                feelingLucky,
                !string.IsNullOrEmpty(track.MD5),
                !string.IsNullOrEmpty(track.MediaVersion),
                wrongLicense,
                isGeolocked);
            return null;
        }

        try
        {
            _logger.LogInformation(
                "Trying fallback crypted URL for format {Format} (feelingLucky={FeelingLucky}) - Track {TrackId}",
                formatName,
                feelingLucky,
                track.Id);

            var url = CryptoService.GenerateCryptedStreamUrl(
                track.Id,
                track.MD5!,
                track.MediaVersion!,
                formatNumber.ToString());

            _logger.LogDebug(
                "Generated crypted URL for track {TrackId}, format {Format}: {Url}",
                track.Id,
                formatName,
                url);

            return !string.IsNullOrEmpty(url) && await TestUrlAsync(track, url, formatName)
                ? url
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to generate fallback URL for format {Format} - Track {TrackId}", formatName, track.Id);
            return null;
        }
    }

    private static bool CanTryCryptedFallback(
        DeezSpoTag.Core.Models.Track track,
        bool feelingLucky,
        bool wrongLicense,
        bool isGeolocked)
    {
        return !string.IsNullOrEmpty(track.MD5)
            && !string.IsNullOrEmpty(track.MediaVersion)
            && (feelingLucky || (!wrongLicense && !isGeolocked));
    }

    private sealed class BitrateResolutionState
    {
        public BitrateResolutionState(bool hasAlternative)
        {
            HasAlternative = hasAlternative;
        }

        public bool HasAlternative { get; set; }
        public bool FallbackNotified { get; set; }
    }

    private sealed class FormatAttemptResult
    {
        public required UrlResult LastResult { get; init; }
        public string? Url { get; init; }
        public DeezSpoTag.Core.Models.Track? ResolvedFallbackTrack { get; init; }
        public bool HasAlternative { get; init; }
    }

    private sealed class MediaUrlAttempt
    {
        public static MediaUrlAttempt Empty { get; } = new();

        public string? Url { get; init; }
        public bool WrongLicense { get; init; }
        public bool IsGeolocked { get; init; }
        public bool TrackTokenExpired { get; init; }
        public bool TransientNetworkFailure { get; init; }
        public bool ShouldRetry { get; init; }
    }

    private static string ResolveAvailabilityErrorCode(AvailabilityProbeState state, bool is360Request)
    {
        if (state.WrongLicense)
        {
            return ErrorMappedButLicenseBlocked;
        }

        if (state.IsGeolocked)
        {
            return ErrorMappedButGeoBlocked;
        }

        if (state.TrackTokenExpired)
        {
            return ErrorTransientTokenFailure;
        }

        if (state.TransientNetworkFailure)
        {
            return ErrorTransientNetworkFailure;
        }

        if (is360Request)
        {
            return ErrorMappedButQualityUnavailable360;
        }

        return ErrorMappedButQualityUnavailable;
    }

    private static string BuildAvailabilityErrorMessage(string errorCode, int preferredBitrate, string formatName)
    {
        return errorCode switch
        {
            ErrorMappedButLicenseBlocked => $"Track mapped, but account cannot stream {formatName}.",
            ErrorMappedButGeoBlocked => "Track mapped, but unavailable in the current country.",
            ErrorTransientTokenFailure => "Track mapped, but track token expired during availability check.",
            ErrorTransientNetworkFailure => "Track mapped, but availability check failed due to a transient network/auth issue.",
            ErrorMappedButQualityUnavailable360 => "Track mapped, but 360 Reality Audio is unavailable for this item.",
            _ => $"Track mapped, but preferred quality ({preferredBitrate}) is unavailable."
        };
    }

    private async Task<bool> RefreshTrackTokenAsync(DeezSpoTag.Core.Models.Track track, DeezerClient deezerClient)
    {
        return await TrackTokenRefreshHelper.RefreshTrackTokenAsync(
            track,
            deezerClient,
            _logger,
            includeFileSizes: true);
    }

    /// <summary>
    /// EXACT PORT of testURL from deezspotag - simple HEAD request with immediate cancellation
    /// </summary>
    private async Task<bool> TestUrlAsync(DeezSpoTag.Core.Models.Track track, string url, string formatName)
    {
        if (string.IsNullOrEmpty(url)) 
            return false;

        try
        {
            var handler = new HttpClientHandler();
            TlsPolicy.ApplyIfAllowed(handler, configuration: null);
            
            using var httpClient = new HttpClient(handler);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.130 Safari/537.36");
            httpClient.Timeout = TimeSpan.FromSeconds(5); // Shorter timeout like deezspotag

            // EXACT PORT: Make request and cancel immediately after getting headers
            using var cts = new CancellationTokenSource();
            var request = httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            
            using var response = await request;
            
            // EXACT PORT: Update file sizes like deezspotag does
            var fileSizeKey = formatName.ToLower();
            
            if (track.FileSizes == null)
                track.FileSizes = new Dictionary<string, int>();

            // EXACT PORT: Set file size based on response
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                track.FileSizes[fileSizeKey] = 0;
                return false;
            }
            else
            {
                var contentLength = response.Content.Headers.ContentLength;
                track.FileSizes[fileSizeKey] = contentLength.HasValue ? (int)contentLength.Value : 1;
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "URL test failed for track {TrackId}, format {Format}", track.Id, formatName);
            
            // EXACT PORT: Set file size to 0 on failure
            if (track.FileSizes == null)
                track.FileSizes = new Dictionary<string, int>();
            track.FileSizes[formatName.ToLower()] = 0;
            
            return false;
        }
    }

    /// <summary>
    /// EXACT PORT: Check and renew track token if expired
    /// </summary>
    private async Task CheckAndRenewTrackTokenAsync(DeezSpoTag.Core.Models.Track track, DeezerClient deezerClient)
    {
        var tokenExpire = track.TrackTokenExpire > 0 ? track.TrackTokenExpire : track.TrackTokenExpiration ?? 0;
        
        if (tokenExpire <= 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var expiration = DateTimeOffset.FromUnixTimeSeconds(tokenExpire);
        
        if (now > expiration)
        {
            try
            {
                var newTrackData = await deezerClient.GetTrackWithFallbackAsync(track.Id);
                if (newTrackData != null)
                {
                    track.TrackToken = newTrackData.TrackToken;
                    track.TrackTokenExpiration = newTrackData.TrackTokenExpire;
                    track.TrackTokenExpire = newTrackData.TrackTokenExpire;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to renew track token for track {TrackId}", track.Id);
            }
        }
    }
}

/// <summary>
/// Result of URL retrieval operation
/// </summary>
public class UrlResult
{
    public string? Url { get; set; }
    public bool WrongLicense { get; set; }
    public bool IsGeolocked { get; set; }
    public bool TrackTokenExpired { get; set; }
    public bool TransientNetworkFailure { get; set; }
    public bool QualityUnavailable { get; set; }
}

internal sealed class AvailabilityProbeState
{
    public bool WrongLicense { get; private set; }
    public bool IsGeolocked { get; private set; }
    public bool TrackTokenExpired { get; private set; }
    public bool TransientNetworkFailure { get; private set; }
    public bool QualityUnavailable { get; private set; }

    public void Observe(UrlResult result)
    {
        if (result == null)
        {
            return;
        }

        WrongLicense |= result.WrongLicense;
        IsGeolocked |= result.IsGeolocked;
        TrackTokenExpired |= result.TrackTokenExpired;
        TransientNetworkFailure |= result.TransientNetworkFailure;
        QualityUnavailable |= result.QualityUnavailable;
    }
}

/// <summary>
/// Exception thrown when bitrate selection fails
/// </summary>
public class BitrateException : Exception
{
    public string ErrorCode { get; }

    public BitrateException(string message, string errorCode) : base(message)
    {
        ErrorCode = errorCode;
    }

    public BitrateException(string message, Exception innerException, string errorCode) : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}
