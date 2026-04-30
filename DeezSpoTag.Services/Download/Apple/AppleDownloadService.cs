using System.Text;
using System.Text.Json;
using System.Threading;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Apple;

public sealed class AppleDownloadService : IAppleDownloadService
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DeviceM3u8Timeout = TimeSpan.FromSeconds(12);
    private static readonly HttpClient WrapperAccountClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };
    private int _widevineParityLogged;
    private readonly AppleMusicCatalogService _catalogService;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppleWebPlaybackClient _webPlaybackClient;
    private readonly AppleHlsDownloader _hlsDownloader;
    private readonly AppleExternalToolRunner _toolRunner;
    private readonly AppleWidevineLicenseClient _licenseClient;
    private readonly AppleWrapperDecryptor _wrapperDecryptor;
    private readonly ILogger<AppleDownloadService> _logger;
    private readonly IMemoryCache _cache;
    private const string DefaultLanguage = "en-US";
    private const string AtmosKeyword = "atmos";
    private const string AttributesKey = "attributes";
    private const string UnknownValue = "unknown";
    private const string AacLcType = "aac-lc";
    private const string AacBinauralType = "aac-binaural";
    private const string AacDownmixType = "aac-downmix";
    private const string AudioStereo256 = "audio-stereo-256";
    private const string AudioStereo = "audio-stereo";
    private const string StereoKeyword = "stereo";

    private sealed class VideoDownloadContext
    {
        public required DeezSpoTagSettings Settings { get; init; }
        public required string AppleId { get; init; }
        public required AppleVideoAttributes Attributes { get; init; }
        public required AppleHlsVariantEntry VideoVariant { get; init; }
        public required List<AppleHlsMediaEntry> AudioCandidates { get; init; }
        public required string OutputPath { get; init; }
        public required HttpClient HttpClient { get; init; }
    }

    private sealed class VideoTempPaths
    {
        public required string TempVideo { get; init; }
        public required string TempAudio { get; init; }
        public required string DecryptedVideo { get; init; }
        public required string DecryptedAudio { get; init; }
        public string TempCover { get; set; } = string.Empty;
    }

    private sealed record VideoCandidateAttemptResult(
        bool Success,
        string? ResolvedVideoKeyUri,
        bool KeyAcquisitionFailed,
        bool MuxFailed,
        bool MuxNoAudioTrack);

    private sealed record VideoCandidateSelectionResult(
        AppleHlsMediaEntry? ResolvedAudio,
        string FailureMessage);

    private enum VideoMuxStatus
    {
        Success,
        Failed,
        MissingAudioTrack
    }

    public AppleDownloadService(
        IServiceProvider serviceProvider,
        ILogger<AppleDownloadService> logger)
    {
        _catalogService = serviceProvider.GetRequiredService<AppleMusicCatalogService>();
        _settingsService = serviceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        _httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        _webPlaybackClient = serviceProvider.GetRequiredService<AppleWebPlaybackClient>();
        _hlsDownloader = serviceProvider.GetRequiredService<AppleHlsDownloader>();
        _toolRunner = serviceProvider.GetRequiredService<AppleExternalToolRunner>();
        _licenseClient = serviceProvider.GetRequiredService<AppleWidevineLicenseClient>();
        _wrapperDecryptor = serviceProvider.GetRequiredService<AppleWrapperDecryptor>();
        _logger = logger;
        _cache = serviceProvider.GetRequiredService<IMemoryCache>();
    }

    public async Task<AppleDownloadResult> DownloadAsync(AppleDownloadRequest request, CancellationToken cancellationToken)
    {
        var sourceUrl = request.ServiceUrl ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return AppleDownloadResult.Fail("Apple download missing source URL.");
        }

        await TryHydrateTokensFromWrapperAsync(request, cancellationToken);

        var isVideo = AppleVideoClassifier.IsVideo(sourceUrl, explicitFlag: request.IsVideo);
        if (isVideo)
        {
            return await DownloadVideoAsync(request, cancellationToken);
        }

        var storefront = await ResolveStorefrontForRequestAsync(request, cancellationToken);
        var language = DefaultLanguage;
        var appleId = ResolveAppleId(sourceUrl, request.AppleId);
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return AppleDownloadResult.Fail("Unable to resolve Apple Music ID.");
        }

        var isStation = IsStationRequest(sourceUrl, appleId);
        if (isStation)
        {
            return await DownloadStationAsync(request, appleId, storefront, language, cancellationToken);
        }

        LogWidevineParity();

        // Check if this is an AAC-LC request - if so, go directly to WebPlayback API
        // This matches the GUI's runv3 behavior: AAC-LC uses WebPlayback, not enhanced HLS
        var isAacLc = IsAacLcRequest(request);
        if (isAacLc)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Apple AAC-LC requested for {AppleId} - using WebPlayback API directly (matching GUI runv3 behavior).",
                    appleId);            }
            return await DownloadAacLcFromWebPlaybackAsync(request, appleId, cancellationToken);
        }

        var manifestResolution = await ResolveManifestWithFallbackAsync(
            request,
            appleId,
            storefront,
            language,
            cancellationToken);
        appleId = manifestResolution.AppleId;
        var manifestUrl = manifestResolution.ManifestUrl;
        var missingManifestResult = await TryHandleMissingManifestAsync(request, appleId, manifestUrl, cancellationToken);
        if (missingManifestResult != null)
        {
            return missingManifestResult;
        }

        LogManifestResolved(appleId);
        var resolvedManifestUrl = manifestUrl!;

        var variantSelection = await SelectManifestVariantAsync(resolvedManifestUrl, request, cancellationToken);
        if (variantSelection == null)
        {
            return AppleDownloadResult.Fail("No matching Apple HLS variant found.");
        }

        var variant = variantSelection.Value.Variant;
        var streamGroup = variantSelection.Value.StreamGroup;
        await ReportSelectedVariantAsync(request, variant.Uri, streamGroup);

        var wrapperResult = await TryRunWrapperDecryptAsync(request, appleId, variant, cancellationToken);
        if (wrapperResult != null)
        {
            return wrapperResult;
        }

        var drmInfo = await ResolveDrmInfoForVariantAsync(variant.Uri, appleId, request, cancellationToken);
        if (drmInfo == null)
        {
            return AppleDownloadResult.Fail("Unable to extract Apple DRM data from playlist.");
        }

        var pssh = !string.IsNullOrWhiteSpace(drmInfo.Value.PsshBase64)
            ? drmInfo.Value.PsshBase64
            : AppleKeyService.BuildPssh(drmInfo.Value.KidBase64 ?? string.Empty, appleId);
        if (string.IsNullOrWhiteSpace(pssh))
        {
            return AppleDownloadResult.Fail("Failed to build Apple PSSH.");
        }

        return await DownloadAndDecryptWidevineAudioAsync(
            request,
            new WidevineDownloadContext(
                appleId,
                variant.Uri,
                streamGroup,
                drmInfo.Value,
                pssh,
                "apple",
                "Widevine key acquisition failed.",
                "mp4decrypt failed."),
            cancellationToken);
    }

    private async Task<AppleDownloadResult?> TryHandleMissingManifestAsync(
        AppleDownloadRequest request,
        string appleId,
        string? manifestUrl,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(manifestUrl))
        {
            return null;
        }

        if (AllowsAacFallback(request)
            && !string.IsNullOrWhiteSpace(request.MediaUserToken)
            && !string.IsNullOrWhiteSpace(request.AuthorizationToken))
        {
            _logger.LogWarning(
                "Apple enhanced HLS unavailable for {AppleId} (requested {Profile}). Device wrapper not available - falling back to AAC-LC via WebPlayback.",
                appleId,
                request.PreferredProfile ?? "default");
            return await DownloadAacLcFromWebPlaybackAsync(request, appleId, cancellationToken);
        }

        return AppleDownloadResult.Fail("Apple manifest URL missing. Enhanced HLS not available and no tokens configured for AAC-LC fallback.");
    }

    private void LogManifestResolved(string appleId)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Apple manifest resolved for {AppleId}", appleId);
        }
    }

    private async Task ReportSelectedVariantAsync(AppleDownloadRequest request, string variantUri, string streamGroup)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Apple variant selected: {Uri} (AudioGroup: {AudioGroup})", variantUri, streamGroup);
        }

        if (request.ProgressCallback != null)
        {
            await request.ProgressCallback(2, 0);
        }
    }

    private async Task<string> ResolveStorefrontForRequestAsync(AppleDownloadRequest request, CancellationToken cancellationToken)
    {
        var storefront = string.IsNullOrWhiteSpace(request.Storefront) ? "us" : request.Storefront;
        if (string.IsNullOrWhiteSpace(request.MediaUserToken))
        {
            return storefront;
        }

        var accountStorefront = await _catalogService.GetAccountStorefrontAsync(request.MediaUserToken, cancellationToken);
        if (string.IsNullOrWhiteSpace(accountStorefront) ||
            string.Equals(accountStorefront, storefront, StringComparison.OrdinalIgnoreCase))
        {
            return storefront;
        }

        if (IsAtmosProfile(request.PreferredProfile))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Apple storefront override skipped for Atmos request: keeping {Configured} instead of account storefront {Account}.",
                    storefront,
                    accountStorefront);            }
            return storefront;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Apple storefront overridden by account token: {Original} -> {Account}",
                storefront,
                accountStorefront);        }
        return accountStorefront;
    }

    private string ResolveAppleId(string sourceUrl, string? requestAppleId)
    {
        var appleId = !string.IsNullOrWhiteSpace(requestAppleId)
            ? requestAppleId
            : AppleIdParser.TryExtractFromUrl(sourceUrl);
        var queryAppleId = AppleIdParser.TryExtractFromUrl(sourceUrl);
        if (!string.IsNullOrWhiteSpace(queryAppleId)
            && !string.Equals(queryAppleId, appleId, StringComparison.OrdinalIgnoreCase))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Apple ID overridden from source URL query parameter: {OriginalId} -> {ResolvedId}",
                    appleId,
                    queryAppleId);            }
            return queryAppleId;
        }

        return appleId ?? string.Empty;
    }

    private async Task<ManifestResolution> ResolveManifestWithFallbackAsync(
        AppleDownloadRequest request,
        string appleId,
        string storefront,
        string language,
        CancellationToken cancellationToken)
    {
        var currentAppleId = appleId;
        var (manifestUrl, resolvedAppleId) = await ResolveEnhancedHlsAsync(currentAppleId, storefront, language, request, cancellationToken);
        currentAppleId = UpdateResolvedAppleId(currentAppleId, resolvedAppleId, null);
        if (!string.IsNullOrWhiteSpace(manifestUrl))
        {
            return new ManifestResolution(manifestUrl, currentAppleId);
        }

        foreach (var fallbackStorefront in GetFallbackStorefronts(storefront))
        {
            _logger.LogWarning(
                "Apple catalog lookup failed for storefront {Storefront}; retrying with {FallbackStorefront}.",
                storefront,
                fallbackStorefront);
            var (fallbackManifest, fallbackResolvedId) = await ResolveEnhancedHlsAsync(
                currentAppleId,
                fallbackStorefront,
                language,
                request,
                cancellationToken);
            currentAppleId = UpdateResolvedAppleId(currentAppleId, fallbackResolvedId, fallbackStorefront);
            if (!string.IsNullOrWhiteSpace(fallbackManifest))
            {
                return new ManifestResolution(fallbackManifest, currentAppleId);
            }
        }

        return new ManifestResolution(string.Empty, currentAppleId);
    }

    private string UpdateResolvedAppleId(string currentAppleId, string? resolvedAppleId, string? storefront)
    {
        if (string.IsNullOrWhiteSpace(resolvedAppleId) || resolvedAppleId == currentAppleId)
        {
            return currentAppleId;
        }

        if (string.IsNullOrWhiteSpace(storefront))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Apple ID resolved: {OriginalId} -> {ResolvedId}",
                    currentAppleId,
                    resolvedAppleId);            }
        }
        else
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Apple ID resolved via fallback storefront {Storefront}: {OriginalId} -> {ResolvedId}",
                    storefront,
                    currentAppleId,
                    resolvedAppleId);            }
        }

        return resolvedAppleId;
    }

    private async Task<VariantSelection?> SelectManifestVariantAsync(
        string manifestUrl,
        AppleDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var masterText = await GetCachedTextAsync(
            $"apple:hls-master:{manifestUrl}",
            TimeSpan.FromMinutes(5),
            () => client.GetStringAsync(manifestUrl, cancellationToken));
        var masterManifest = AppleHlsManifestParser.ParseMaster(masterText, new Uri(manifestUrl));
        var variant = SelectVariant(masterManifest, request);
        if (variant == null || string.IsNullOrWhiteSpace(variant.Uri))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                var atmosGroupCount = BuildAtmosAudioGroupSet(masterManifest.Media).Count;
                _logger.LogWarning(
                    "Apple variant selection failed for manifest {ManifestUrl}. profile={Profile}, variants={VariantCount}, audioEntries={AudioEntries}, atmosGroups={AtmosGroups}, atmosMax={AtmosMax}.",
                    manifestUrl,
                    request.PreferredProfile ?? string.Empty,
                    masterManifest.Variants.Count,
                    masterManifest.Media.Count,
                    atmosGroupCount,
                    request.AtmosMax);
            }

            return null;
        }

        return new VariantSelection(variant, variant.AudioGroup);
    }

    private async Task<AppleDownloadResult?> TryRunWrapperDecryptAsync(
        AppleDownloadRequest request,
        string appleId,
        AppleHlsVariantEntry variant,
        CancellationToken cancellationToken)
    {
        var useWrapperDecrypt = request.GetM3u8FromDevice
            && !string.IsNullOrWhiteSpace(request.DecryptM3u8Port);
        if (!useWrapperDecrypt)
        {
            return null;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Attempting Apple wrapper decrypt for {AppleId} using playlist {PlaylistUrl}.", appleId, variant.Uri);        }
        var outputPath = BuildOutputPath(request, appleId);
        await ReportProgressAsync(request.ProgressCallback, 8);
        var success = await _wrapperDecryptor.TryDecryptAsync(
            variant.Uri,
            outputPath,
            appleId,
            request.DecryptM3u8Port,
            cancellationToken);
        if (success)
        {
            var validation = await _toolRunner.ValidateDecodableAudioAsync(outputPath, cancellationToken);
            if (!validation.Success)
            {
                _logger.LogWarning(
                    "Apple wrapper decrypt produced invalid audio for {AppleId}; continuing with internal decrypt pipeline. {Reason}",
                    appleId,
                    validation.Message);
                TryDelete(outputPath);
                return null;
            }

            await ReportProgressAsync(request.ProgressCallback, 98);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Apple wrapper decrypt succeeded for {AppleId}.", appleId);            }
            return AppleDownloadResult.Ok(outputPath, variant.AudioGroup);
        }

        if (IsAtmosProfile(request.PreferredProfile))
        {
            _logger.LogWarning(
                "Apple wrapper decrypt failed for Atmos stream {AppleId}; falling back to internal decrypt pipeline.",
                appleId);
        }
        else
        {
            _logger.LogWarning(
                "Apple wrapper decrypt failed for {AppleId}; continuing with internal decrypt pipeline.",
                appleId);
        }

        return null;
    }

    private async Task<ExtractedDrmInfo?> ResolveDrmInfoForVariantAsync(
        string variantUri,
        string appleId,
        AppleDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var drmInfo = await TryExtractDrmInfoAsync(variantUri, cancellationToken);
        if (drmInfo != null)
        {
            return drmInfo;
        }

        var playback = await _webPlaybackClient.GetWebPlaybackAsync(
            appleId,
            request.AuthorizationToken,
            request.MediaUserToken,
            cancellationToken);
        if (playback == null)
        {
            return null;
        }

        return await TryExtractDrmInfoAsync(playback.AssetUrl, cancellationToken);
    }

    private async Task<AppleDownloadResult> DownloadStationAsync(
        AppleDownloadRequest request,
        string stationId,
        string storefront,
        string language,
        CancellationToken cancellationToken)
    {
        var tokenValidationError = ValidateStationTokens(request);
        if (!string.IsNullOrWhiteSpace(tokenValidationError))
        {
            return AppleDownloadResult.Fail(tokenValidationError);
        }

        LogWidevineParity();

        await TryPopulateStationMetadataAsync(request, stationId, storefront, language, cancellationToken);

        (string ManifestUrl, string? KeyServerUrl)? stationAsset;
        try
        {
            stationAsset = await TryGetStationAssetAsync(stationId, request.MediaUserToken, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple station assets lookup failed for {StationId}", stationId);
            return AppleDownloadResult.Fail("Apple station assets request failed.");
        }

        if (stationAsset == null || string.IsNullOrWhiteSpace(stationAsset.Value.ManifestUrl))
        {
            return AppleDownloadResult.Fail("Apple station assets unavailable.");
        }

        var manifestUrl = stationAsset.Value.ManifestUrl;
        var keyServerUrl = stationAsset.Value.KeyServerUrl;

        var stationStream = await ResolveStationStreamAsync(manifestUrl, request, stationId, cancellationToken);
        if (!stationStream.Success)
        {
            return AppleDownloadResult.Fail(stationStream.ErrorMessage ?? "No station HLS variant found.");
        }
        if (string.IsNullOrWhiteSpace(stationStream.MediaPlaylistUrl))
        {
            return AppleDownloadResult.Fail("Apple station media playlist unavailable.");
        }

        var drmInfo = await TryExtractDrmInfoAsync(stationStream.MediaPlaylistUrl, cancellationToken);
        if (drmInfo == null)
        {
            return AppleDownloadResult.Fail("Unable to extract station encryption KID.");
        }

        var pssh = !string.IsNullOrWhiteSpace(drmInfo.Value.PsshBase64)
            ? drmInfo.Value.PsshBase64
            : AppleKeyService.BuildPssh(drmInfo.Value.KidBase64 ?? string.Empty, stationId);
        if (string.IsNullOrWhiteSpace(pssh))
        {
            return AppleDownloadResult.Fail("Failed to build station PSSH.");
        }

        return await DownloadAndDecryptWidevineAudioAsync(
            request,
            new WidevineDownloadContext(
                stationId,
                stationStream.MediaPlaylistUrl,
                stationStream.StreamGroup,
                drmInfo.Value,
                pssh,
                "apple-station",
                "Station Widevine key acquisition failed.",
                "mp4decrypt failed for station stream.",
                keyServerUrl),
            cancellationToken);
    }

    private static AppleHlsVariantEntry? SelectVariant(AppleHlsMasterManifest master, AppleDownloadRequest request)
    {
        if (master.Variants.Count == 0)
        {
            return null;
        }

        var profile = request.PreferredProfile?.Trim().ToLowerInvariant() ?? string.Empty;
        var orderedProfiles = ResolveProfileOrder(profile);

        foreach (var profileName in orderedProfiles)
        {
            var candidates = FilterVariantsByProfile(master, profileName, request).ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            return candidates
                .OrderByDescending(v => v.AverageBandwidth > 0 ? v.AverageBandwidth : v.Bandwidth)
                .FirstOrDefault();
        }

        return orderedProfiles.Count == 0
            ? master.Variants
                .OrderByDescending(v => v.AverageBandwidth > 0 ? v.AverageBandwidth : v.Bandwidth)
                .FirstOrDefault()
            : null;
    }

    private static List<string> ResolveProfileOrder(string profile)
    {
        if (profile.Contains(AtmosKeyword, StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { AtmosKeyword };
        }

        if (profile.Contains("alac", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { "alac", "aac" };
        }

        if (profile.Contains("aac", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { "aac" };
        }

        return new List<string> { "alac", "aac" };
    }

    private static bool AllowsAacFallback(AppleDownloadRequest request)
    {
        var profile = request.PreferredProfile?.Trim().ToLowerInvariant() ?? string.Empty;
        if (profile.Contains(AtmosKeyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return profile.Contains("alac", StringComparison.OrdinalIgnoreCase)
               || profile.Contains("aac", StringComparison.OrdinalIgnoreCase)
               || string.IsNullOrWhiteSpace(profile);
    }

    private static bool IsAtmosProfile(string? profile)
        => !string.IsNullOrWhiteSpace(profile)
           && profile.Contains(AtmosKeyword, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<AppleHlsVariantEntry> FilterVariantsByProfile(
        AppleHlsMasterManifest master,
        string profile,
        AppleDownloadRequest request)
    {
        var variants = master.Variants;
        var atmosGroups = BuildAtmosAudioGroupSet(master.Media);

        if (profile.Contains("aac", StringComparison.OrdinalIgnoreCase))
        {
            return variants
                .Where(v => !IsAtmosVariantCandidate(v, atmosGroups))
                .Where(v => v.Codecs.Contains("mp4a", StringComparison.OrdinalIgnoreCase))
                .Where(v => IsMatchingAacGroup(v.AudioGroup, request.AacType));
        }

        if (profile.Contains(AtmosKeyword, StringComparison.OrdinalIgnoreCase))
        {
            return variants
                .Where(v => IsAtmosVariantCandidate(v, atmosGroups))
                .Where(v => IsMatchingAtmosGroup(v.AudioGroup, request.AtmosMax));
        }

        if (profile.Contains("alac", StringComparison.OrdinalIgnoreCase))
        {
            return variants
                .Where(v => !IsAtmosVariantCandidate(v, atmosGroups))
                .Where(v => v.Codecs.Contains("alac", StringComparison.OrdinalIgnoreCase))
                .Where(v => IsMatchingAlacGroup(v.AudioGroup, request.AlacMax));
        }

        return variants;
    }

    private static bool IsAtmosVariantCandidate(AppleHlsVariantEntry variant, HashSet<string>? knownAtmosGroups = null)
    {
        if (variant == null)
        {
            return false;
        }

        var codecs = variant.Codecs ?? string.Empty;
        if (codecs.Contains("ec-3", StringComparison.OrdinalIgnoreCase)
            || codecs.Contains("ec3", StringComparison.OrdinalIgnoreCase)
            || codecs.Contains("ac-3", StringComparison.OrdinalIgnoreCase)
            || codecs.Contains("ac3", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var group = variant.AudioGroup ?? string.Empty;
        if (knownAtmosGroups != null
            && !string.IsNullOrWhiteSpace(group)
            && knownAtmosGroups.Contains(group))
        {
            return true;
        }

        return group.Contains(AtmosKeyword, StringComparison.OrdinalIgnoreCase)
               || group.Contains("joc", StringComparison.OrdinalIgnoreCase)
               || group.Contains("ec3", StringComparison.OrdinalIgnoreCase)
               || group.Contains("ac3", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> BuildAtmosAudioGroupSet(IEnumerable<AppleHlsMediaEntry> mediaEntries)
    {
        return mediaEntries
            .Where(entry => entry.Type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase))
            .Where(IsAtmosAudioEntry)
            .Select(entry => entry.GroupId)
            .Where(groupId => !string.IsNullOrWhiteSpace(groupId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsStationRequest(string sourceUrl, string? appleId)
        => IsAppleStationId(appleId)
           || (!string.IsNullOrWhiteSpace(sourceUrl)
               && sourceUrl.Contains("/station/", StringComparison.OrdinalIgnoreCase));

    private static bool IsAppleStationId(string? appleId)
        => !string.IsNullOrWhiteSpace(appleId)
           && appleId.StartsWith("ra.", StringComparison.OrdinalIgnoreCase);

    private async Task<(string ManifestUrl, string? KeyServerUrl)?> TryGetStationAssetAsync(
        string stationId,
        string mediaUserToken,
        CancellationToken cancellationToken)
    {
        using var doc = await _catalogService.GetStationAssetsAsync(stationId, mediaUserToken, cancellationToken);
        var root = doc.RootElement;
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!results.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() == 0)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var manifestUrl = urlEl.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(manifestUrl))
            {
                continue;
            }

            var keyServerUrl = asset.TryGetProperty("keyServerUrl", out var keyServerEl) && keyServerEl.ValueKind == JsonValueKind.String
                ? keyServerEl.GetString()
                : null;
            return (manifestUrl, keyServerUrl);
        }

        return null;
    }

    private static bool TryExtractStationName(JsonElement root, out string stationName)
    {
        stationName = string.Empty;
        if (!root.TryGetProperty("data", out var dataArr)
            || dataArr.ValueKind != JsonValueKind.Array
            || dataArr.GetArrayLength() == 0)
        {
            return false;
        }

        var station = dataArr[0];
        if (!station.TryGetProperty(AttributesKey, out var attrs) || attrs.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!attrs.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        stationName = nameEl.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(stationName);
    }

    private void LogWidevineParity()
    {
        if (Interlocked.Exchange(ref _widevineParityLogged, 1) != 0)
        {
            return;
        }

        _logger.LogInformation(
            "Apple DRM parity: using Widevine license flow. FairPlay JS-agent decryption path is disabled in this pipeline.");
    }

    private static bool IsAacLcRequest(AppleDownloadRequest request)
    {
        var profile = request.PreferredProfile?.Trim().ToLowerInvariant() ?? string.Empty;
        var aacType = request.AacType?.Trim().ToLowerInvariant() ?? string.Empty;
        var isLc = string.IsNullOrWhiteSpace(aacType) || aacType is "aac" or AacLcType;
        var isAacProfile = profile.Contains("aac", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrWhiteSpace(profile) && isLc);
        return isAacProfile && isLc;
    }

    private static bool IsApplePreviewUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("audiopreview", StringComparison.OrdinalIgnoreCase)
            || url.Contains("audio-preview", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/preview/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("preview.m4a", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".p.m4a", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AppleDownloadResult> DownloadAacLcFromWebPlaybackAsync(
        AppleDownloadRequest request,
        string appleId,
        CancellationToken cancellationToken)
    {
        var tokenError = ValidateAacLcTokens(request);
        if (!string.IsNullOrWhiteSpace(tokenError))
        {
            return AppleDownloadResult.Fail(tokenError);
        }

        var storefront = await ResolveStorefrontForAacAsync(request.MediaUserToken, request.Storefront, cancellationToken);
        if (string.IsNullOrWhiteSpace(storefront))
        {
            return AppleDownloadResult.Fail("Apple Music subscription is not active or could not be verified. Refusing to download Apple preview audio.");
        }

        var resolvedAppleId = await ResolveAacAppleIdAsync(request, appleId, storefront, cancellationToken);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Apple AAC-LC download starting: appleId={AppleId}, storefront={Storefront}, authToken={AuthLen}chars, mediaUserToken={MutLen}chars",
                resolvedAppleId,
                storefront,
                request.AuthorizationToken.Length,
                request.MediaUserToken.Length);        }

        var playback = await _webPlaybackClient.GetWebPlaybackAsync(
            resolvedAppleId,
            request.AuthorizationToken,
            request.MediaUserToken,
            cancellationToken);
        if (playback == null)
        {
            _logger.LogError("Apple AAC-LC download failed: webPlayback returned null for {AppleId} (resolved from {OriginalId}). Check logs above for specific error.",
                resolvedAppleId, appleId);
            return AppleDownloadResult.Fail("Apple web playback failed. Track may not be available through AAC-LC API (try ALAC/Atmos with device wrapper), or there may be regional restrictions. Check logs for details.");
        }

        if (string.IsNullOrWhiteSpace(playback.AssetUrl))
        {
            _logger.LogError("Apple AAC-LC download failed: webPlayback returned but AssetUrl is empty for {AppleId}. The track may not be available in AAC format or your account region.",
                resolvedAppleId);
            return AppleDownloadResult.Fail("Apple web playback returned no AAC asset URL. Track may be unavailable in your region or AAC format not available.");
        }

        if (IsApplePreviewUrl(playback.AssetUrl) || IsApplePreviewUrl(playback.HlsPlaylistUrl))
        {
            _logger.LogWarning(
                "Apple AAC-LC playback returned preview asset for {AppleId}; refusing preview download.",
                resolvedAppleId);
            return AppleDownloadResult.Fail("Apple playback returned a preview asset. Refusing to download previews.");
        }

        var drmInfo = await TryExtractDrmInfoAsync(playback.AssetUrl, cancellationToken);
        if (drmInfo == null)
        {
            return AppleDownloadResult.Fail("Unable to extract Apple DRM data from playlist.");
        }

        var pssh = !string.IsNullOrWhiteSpace(drmInfo.Value.PsshBase64)
            ? drmInfo.Value.PsshBase64
            : AppleKeyService.BuildPssh(drmInfo.Value.KidBase64 ?? string.Empty, resolvedAppleId);
        if (string.IsNullOrWhiteSpace(pssh))
        {
            return AppleDownloadResult.Fail("Failed to build Apple PSSH.");
        }

        var result = await DownloadAndDecryptWidevineAudioAsync(
            request,
            new WidevineDownloadContext(
                resolvedAppleId,
                playback.AssetUrl,
                AudioStereo256,
                drmInfo.Value,
                pssh,
                "apple",
                "Widevine key acquisition failed.",
                "mp4decrypt failed.",
                null,
                new WidevineDownloadFailureHandlers(
                    message => _logger.LogError("Apple AAC-LC download failed: HLS download error: {Message}", message),
                    () => _logger.LogError("Apple AAC-LC download failed: Widevine key acquisition returned empty key."),
                    outputPath => _logger.LogError("Apple AAC-LC download failed: mp4decrypt failed for {OutputPath}.", outputPath))),
            cancellationToken);
        if (result.Success && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Apple AAC-LC download complete: {OutputPath}", result.OutputPath);
        }

        return result;
    }

    private static string? TryExtractAppleIdFromCatalog(JsonDocument? doc)
    {
        if (doc == null)
        {
            return null;
        }

        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array && dataArr.GetArrayLength() > 0)
        {
            return dataArr[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Object &&
            results.TryGetProperty("songs", out var songs) && songs.ValueKind == JsonValueKind.Object &&
            songs.TryGetProperty("data", out var songData) && songData.ValueKind == JsonValueKind.Array &&
            songData.GetArrayLength() > 0)
        {
            return songData[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }

        return null;
    }

    private static string? TryGetEnhancedHls(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array || dataArr.GetArrayLength() == 0)
        {
            return null;
        }

        var item = dataArr[0];
        if (!item.TryGetProperty(AttributesKey, out var attrs) || attrs.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!attrs.TryGetProperty("extendedAssetUrls", out var assets) || assets.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!assets.TryGetProperty("enhancedHls", out var hlsEl) || hlsEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return hlsEl.GetString();
    }

    private static List<string> TryGetAudioTraits(JsonElement root)
    {
        var traits = new List<string>();
        if (!root.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array || dataArr.GetArrayLength() == 0)
        {
            return traits;
        }

        var item = dataArr[0];
        if (!item.TryGetProperty(AttributesKey, out var attrs) || attrs.ValueKind != JsonValueKind.Object)
        {
            return traits;
        }

        if (!attrs.TryGetProperty("audioTraits", out var audioTraits) || audioTraits.ValueKind != JsonValueKind.Array)
        {
            return traits;
        }

        foreach (var value in audioTraits.EnumerateArray()
                     .Where(static trait => trait.ValueKind == JsonValueKind.String)
                     .Select(static trait => trait.GetString())
                     .Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            traits.Add(value!);
        }

        return traits;
    }

    private static bool TrySelectPreferredIsrcCandidate(
        JsonElement root,
        AppleDownloadRequest request,
        bool requireAtmos,
        out string? selectedId,
        out string? selectedEnhancedHls)
    {
        selectedId = null;
        selectedEnhancedHls = null;

        if (!root.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var bestScore = int.MinValue;
        foreach (var item in dataArr.EnumerateArray())
        {
            if (!TryReadIsrcCandidate(item, out var candidateId, out var candidateEnhancedHls, out var attrs, out var hasAtmos))
            {
                continue;
            }

            if (requireAtmos && !hasAtmos)
            {
                continue;
            }

            var score = ScoreIsrcCandidate(request, attrs, candidateId, hasAtmos);

            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            selectedId = candidateId;
            selectedEnhancedHls = candidateEnhancedHls;
        }

        return !string.IsNullOrWhiteSpace(selectedId)
            && !string.IsNullOrWhiteSpace(selectedEnhancedHls);
    }

    private static int ScoreIsrcCandidate(
        AppleDownloadRequest request,
        JsonElement attributes,
        string? candidateId,
        bool hasAtmos)
    {
        var score = hasAtmos ? 100 : 0;
        score += ScoreExactAlbumMatch(request.AlbumName, TryReadString(attributes, "albumName"), 40);
        score += ScoreContainsMatch(request.ArtistName, TryReadString(attributes, "artistName"), 20);
        score += ScoreContainsMatch(request.TrackName, TryReadString(attributes, "name"), 10);
        if (!string.IsNullOrWhiteSpace(request.AppleId)
            && string.Equals(candidateId, request.AppleId, StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        return score;
    }

    private static int ScoreExactAlbumMatch(string? expected, string? actual, int weight)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            return 0;
        }

        return string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase) ? weight : 0;
    }

    private static int ScoreContainsMatch(string? expected, string? actual, int weight)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            return 0;
        }

        return actual.Contains(expected, StringComparison.OrdinalIgnoreCase) ? weight : 0;
    }

    private static string? TryReadString(JsonElement attributes, string propertyName)
    {
        return attributes.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool ShouldProbeDeviceM3u8(string? mode, List<string> audioTraits)
    {
        var normalized = mode?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized == "all")
        {
            return true;
        }

        if (normalized == "hires")
        {
            return audioTraits.Any(t => t.Contains("hi-res-lossless", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static bool IsDeviceFirstMode(string? mode)
    {
        var normalized = mode?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is "all" or "hires";
    }

    private async Task<(string? ManifestUrl, string ResolvedAppleId)> ResolveEnhancedHlsAsync(
        string appleId,
        string storefront,
        string language,
        AppleDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var isAtmosRequest = IsAtmosProfile(request.PreferredProfile);
        var resolvedAppleId = appleId;

        var deviceFirst = await TryResolveDeviceFirstManifestAsync(request, resolvedAppleId, isAtmosRequest, cancellationToken);
        if (!string.IsNullOrWhiteSpace(deviceFirst))
        {
            return (deviceFirst, resolvedAppleId);
        }

        var doc = await ResolveCatalogDocumentAsync(
            resolvedAppleId,
            storefront,
            language,
            request,
            cancellationToken);
        if (doc == null)
            return (null, resolvedAppleId);
        resolvedAppleId = TryUpdateResolvedIdFromCatalog(doc, storefront, resolvedAppleId);

        var preferredAtmos = await TryResolveAtmosPreferredCandidateAsync(
            request,
            storefront,
            language,
            resolvedAppleId,
            isAtmosRequest,
            cancellationToken);
        if (preferredAtmos != null)
        {
            return preferredAtmos.Value;
        }

        var enhancedHls = TryGetEnhancedHls(doc.RootElement);
        var audioTraits = TryGetAudioTraits(doc.RootElement);
        var shouldProbeDevice = !isAtmosRequest
            && request.GetM3u8FromDevice
            && ShouldProbeDeviceM3u8(request.GetM3u8Mode, audioTraits);

        if (shouldProbeDevice)
        {
            var device = await TryGetDeviceEnhancedHlsAsync(request.GetM3u8Port, resolvedAppleId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(device))
            {
                return (device, resolvedAppleId);
            }
        }

        if (!string.IsNullOrWhiteSpace(enhancedHls))
        {
            return (enhancedHls, resolvedAppleId);
        }

        var isrcSecondPass = await ResolveEnhancedFromIsrcSecondPassAsync(
            request,
            storefront,
            language,
            resolvedAppleId,
            cancellationToken);
        if (isrcSecondPass.ManifestUrl != null)
        {
            return (isrcSecondPass.ManifestUrl, isrcSecondPass.ResolvedAppleId);
        }
        resolvedAppleId = isrcSecondPass.ResolvedAppleId;

        if (request.GetM3u8FromDevice && (isAtmosRequest || !shouldProbeDevice))
        {
            var device = await TryGetDeviceEnhancedHlsAsync(request.GetM3u8Port, resolvedAppleId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(device))
            {
                return (device, resolvedAppleId);
            }
        }

        return (null, resolvedAppleId);
    }

    private async Task<JsonDocument?> ResolveCatalogDocumentAsync(
        string resolvedAppleId,
        string storefront,
        string language,
        AppleDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var doc = await TryGetCatalogSongAsync(resolvedAppleId, storefront, language, request.MediaUserToken, cancellationToken);
        if (doc != null || string.IsNullOrWhiteSpace(request.Isrc))
        {
            return doc;
        }

        return await TryGetSongByIsrcAsync(request.Isrc, storefront, language, request.MediaUserToken, cancellationToken);
    }

    private string TryUpdateResolvedIdFromCatalog(JsonDocument? doc, string storefront, string currentResolvedAppleId)
    {
        var resolvedId = TryExtractAppleIdFromCatalog(doc);
        if (string.IsNullOrWhiteSpace(resolvedId) || string.Equals(currentResolvedAppleId, resolvedId, StringComparison.Ordinal))
        {
            return currentResolvedAppleId;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Apple catalog ID resolved via ISRC for storefront {Storefront}: {OriginalId} -> {ResolvedId}",
                storefront,
                currentResolvedAppleId,
                resolvedId);        }
        return resolvedId;
    }

    private async Task<(string? ManifestUrl, string ResolvedAppleId)> ResolveEnhancedFromIsrcSecondPassAsync(
        AppleDownloadRequest request,
        string storefront,
        string language,
        string resolvedAppleId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Isrc))
        {
            return (null, resolvedAppleId);
        }

        var isrcDoc = await TryGetSongByIsrcAsync(request.Isrc, storefront, language, request.MediaUserToken, cancellationToken);
        if (isrcDoc == null)
        {
            return (null, resolvedAppleId);
        }

        var updatedResolvedId = TryUpdateResolvedIdFromCatalog(isrcDoc, storefront, resolvedAppleId);
        var isrcEnhanced = TryGetEnhancedHls(isrcDoc.RootElement);
        if (string.IsNullOrWhiteSpace(isrcEnhanced))
        {
            return (null, updatedResolvedId);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Apple catalog ID resolved via ISRC (second pass) for storefront {Storefront}: {OriginalId} -> {ResolvedId}",
                storefront,
                resolvedAppleId,
                updatedResolvedId);        }
        return (isrcEnhanced, updatedResolvedId);
    }

    private async Task<JsonDocument?> TryGetSongByIsrcAsync(
        string isrc,
        string storefront,
        string language,
        string? mediaUserToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        try
        {
            return await _catalogService.GetSongByIsrcAsync(isrc, storefront, language, cancellationToken, mediaUserToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Apple catalog ISRC lookup failed for {Isrc} storefront {Storefront}.",
                isrc,
                storefront);
            return null;
        }
    }

    private static IEnumerable<string> GetFallbackStorefronts(string primary)
    {
        if (string.IsNullOrWhiteSpace(primary))
        {
            yield return "us";
            yield break;
        }

        if (!string.Equals(primary, "us", StringComparison.OrdinalIgnoreCase))
        {
            yield return "us";
        }
    }

    private async Task<string?> TryGetDeviceEnhancedHlsAsync(string hostAndPort, string appleId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hostAndPort) || string.IsNullOrWhiteSpace(appleId))
        {
            return null;
        }

        var parts = hostAndPort.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
        {
            return null;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DeviceM3u8Timeout);
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(parts[0], port, timeoutCts.Token);

            await using var stream = client.GetStream();
            var idBytes = Encoding.UTF8.GetBytes(appleId);
            if (idBytes.Length > byte.MaxValue)
            {
                return null;
            }

            var idLengthPrefix = new[] { (byte)idBytes.Length };
            await stream.WriteAsync(idLengthPrefix.AsMemory(), timeoutCts.Token);
            await stream.WriteAsync(idBytes.AsMemory(0, idBytes.Length), timeoutCts.Token);

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            var line = await reader.ReadLineAsync(timeoutCts.Token);
            return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Apple device M3U8 probe timed out for {AppleId} via {Endpoint}.",
                appleId,
                hostAndPort);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple device M3U8 probe failed for {AppleId} via {Endpoint}.", appleId, hostAndPort);
            }
            return null;
        }
    }

    private static string BuildOutputPath(AppleDownloadRequest request, string appleId)
    {
        var baseName = string.Empty;
        if (!string.IsNullOrWhiteSpace(request.FilenameFormat) && request.FilenameFormat.StartsWith("literal:", StringComparison.OrdinalIgnoreCase))
        {
            baseName = request.FilenameFormat["literal:".Length..];
        }

        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = string.IsNullOrWhiteSpace(request.TrackName) ? appleId : SanitizeFileName(request.TrackName);
        }

        var fileName = $"{baseName}.m4a";

        return Path.Join(request.OutputDir, fileName);
    }

    private async Task<AppleDownloadResult> DownloadVideoAsync(AppleDownloadRequest request, CancellationToken cancellationToken)
    {
        var preparation = await PrepareVideoDownloadContextAsync(request, cancellationToken);
        if (preparation.Failure != null)
        {
            return preparation.Failure;
        }

        var context = preparation.Context!;
        var tempPaths = CreateVideoTempPaths();
        try
        {
            var keyCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var videoDownload = await DownloadVideoTrackAsync(request, context.VideoVariant, tempPaths.TempVideo, cancellationToken);
            if (!videoDownload.Success)
            {
                return AppleDownloadResult.Fail(videoDownload.Message);
            }

            var tags = await BuildVideoTagsAsync(context, tempPaths, cancellationToken);
            var audioSelection = await SelectVideoAudioCandidateAsync(
                request,
                context,
                videoDownload,
                tempPaths,
                keyCache,
                tags,
                cancellationToken);
            if (audioSelection.ResolvedAudio == null)
            {
                return AppleDownloadResult.Fail(audioSelection.FailureMessage);
            }

            await ReportVideoCompletionAsync(request);
            return BuildVideoSuccessResult(context, audioSelection.ResolvedAudio);
        }
        finally
        {
            CleanupVideoTempArtifacts(tempPaths);
        }
    }

    private async Task<(VideoDownloadContext? Context, AppleDownloadResult? Failure)> PrepareVideoDownloadContextAsync(
        AppleDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        var appleId = ResolveVideoAppleId(request);
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return (null, AppleDownloadResult.Fail("Unable to resolve Apple Music video ID."));
        }

        var requestValidationFailure = ValidateVideoRequest(request, appleId);
        if (requestValidationFailure != null)
        {
            return (null, requestValidationFailure);
        }

        var storefront = string.IsNullOrWhiteSpace(request.Storefront) ? "us" : request.Storefront;
        using var doc = await _catalogService.GetMusicVideoAsync(appleId, storefront, DefaultLanguage, cancellationToken);
        if (!TryExtractVideoAttributes(doc.RootElement, out var attrs))
        {
            return (null, AppleDownloadResult.Fail("Apple video metadata unavailable."));
        }

        var playlistUrl = await _webPlaybackClient.GetWebPlaybackPlaylistAsync(
            appleId,
            request.AuthorizationToken,
            request.MediaUserToken,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(playlistUrl))
        {
            return (null, AppleDownloadResult.Fail("Unable to fetch Apple video playlist."));
        }

        var client = _httpClientFactory.CreateClient();
        var masterText = await GetCachedTextAsync(
            $"apple:video-master:{playlistUrl}",
            TimeSpan.FromMinutes(5),
            () => client.GetStringAsync(playlistUrl, cancellationToken));
        var master = AppleHlsManifestParser.ParseMaster(masterText, new Uri(playlistUrl));

        var targetMaxResolution = ResolveVideoMaxResolution(request.VideoMaxResolution);
        var videoVariant = SelectVideoVariant(master, targetMaxResolution, request.VideoAudioType, request.VideoCodecPreference);
        if (videoVariant == null)
        {
            return (null, AppleDownloadResult.Fail("No suitable Apple video variant found."));
        }

        var audioCandidates = SelectVideoAudioCandidates(master, request.VideoAudioType, videoVariant.AudioGroup);
        if (audioCandidates.Count == 0)
        {
            return (null, AppleDownloadResult.Fail("No suitable Apple video audio stream found."));
        }

        LogVideoCandidateSelection(appleId, videoVariant, request.VideoAudioType, audioCandidates);
        var baseDir = ResolveVideoOutputDirectory(settings, attrs.ArtistName, request.VideoOutputRoot);
        Directory.CreateDirectory(baseDir);
        var outputPath = BuildVideoOutputPath(settings, attrs, baseDir, appleId);

        return (new VideoDownloadContext
        {
            Settings = settings,
            AppleId = appleId,
            Attributes = attrs,
            VideoVariant = videoVariant,
            AudioCandidates = audioCandidates,
            OutputPath = outputPath,
            HttpClient = client
        }, null);
    }

    private static string ResolveVideoAppleId(AppleDownloadRequest request)
    {
        return !string.IsNullOrWhiteSpace(request.AppleId)
            ? request.AppleId
            : AppleIdParser.TryExtractFromUrl(request.ServiceUrl ?? string.Empty) ?? string.Empty;
    }

    private AppleDownloadResult? ValidateVideoRequest(AppleDownloadRequest request, string appleId)
    {
        if (string.IsNullOrWhiteSpace(request.MediaUserToken))
        {
            _logger.LogWarning("Apple Music video download skipped: MediaUserToken is missing for {AppleId}.", appleId);
            return AppleDownloadResult.Fail("Apple Music video download skipped: MediaUserToken is missing.");
        }

        if (request.MediaUserToken.Length <= 50)
        {
            _logger.LogWarning(
                "Apple Music video download skipped: MediaUserToken appears invalid (length={Length}) for {AppleId}.",
                request.MediaUserToken.Length,
                appleId);
            return AppleDownloadResult.Fail("Apple Music video download skipped: MediaUserToken appears invalid.");
        }

        if (!AppleExternalToolRunner.HasMp4Decrypt() || !AppleExternalToolRunner.HasMp4Box())
        {
            _logger.LogWarning("Apple Music video download skipped: mp4decrypt/MP4Box not available for {AppleId}.", appleId);
            return AppleDownloadResult.Fail("Apple Music video download skipped: mp4decrypt/MP4Box not available.");
        }

        return null;
    }

    private void LogVideoCandidateSelection(
        string appleId,
        AppleHlsVariantEntry videoVariant,
        string? requestedAudioType,
        IReadOnlyCollection<AppleHlsMediaEntry> audioCandidates)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Apple MV selection for {AppleId}: video={Resolution} audioGroup={AudioGroup} requestedAudio={RequestedAudio} candidates=[{Candidates}]",
                appleId,
                string.IsNullOrWhiteSpace(videoVariant.Resolution) ? UnknownValue : videoVariant.Resolution,
                string.IsNullOrWhiteSpace(videoVariant.AudioGroup) ? UnknownValue : videoVariant.AudioGroup,
                string.IsNullOrWhiteSpace(requestedAudioType) ? "auto" : requestedAudioType,
                string.Join(", ",
                    audioCandidates
                        .Select(candidate => candidate.GroupId)
                        .Where(group => !string.IsNullOrWhiteSpace(group))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(8)));        }
    }

    private static VideoTempPaths CreateVideoTempPaths()
    {
        return new VideoTempPaths
        {
            TempVideo = Path.Join(Path.GetTempPath(), $"apple-video-{Guid.NewGuid():N}.mp4"),
            TempAudio = Path.Join(Path.GetTempPath(), $"apple-audio-{Guid.NewGuid():N}.mp4"),
            DecryptedVideo = Path.Join(Path.GetTempPath(), $"apple-video-dec-{Guid.NewGuid():N}.mp4"),
            DecryptedAudio = Path.Join(Path.GetTempPath(), $"apple-audio-dec-{Guid.NewGuid():N}.mp4")
        };
    }

    private async Task<AppleHlsDownloadResult> DownloadVideoTrackAsync(
        AppleDownloadRequest request,
        AppleHlsVariantEntry videoVariant,
        string tempVideoPath,
        CancellationToken cancellationToken)
    {
        var videoProgress = CreateScaledProgress(request.ProgressCallback, 0, 42);
        return await _hlsDownloader.DownloadAsync(videoVariant.Uri, tempVideoPath, videoProgress, cancellationToken);
    }

    private static async Task<List<string>> BuildVideoTagsAsync(
        VideoDownloadContext context,
        VideoTempPaths tempPaths,
        CancellationToken cancellationToken)
    {
        var tags = BuildMp4Tags(context.Attributes);
        if (string.IsNullOrWhiteSpace(context.Attributes.ArtworkUrl))
        {
            return tags;
        }

        tempPaths.TempCover = await DownloadTempCoverAsync(context.HttpClient, context.Attributes.ArtworkUrl, cancellationToken);
        if (!string.IsNullOrWhiteSpace(tempPaths.TempCover))
        {
            tags.Add($"cover={tempPaths.TempCover}");
        }

        return tags;
    }

    private async Task<VideoCandidateSelectionResult> SelectVideoAudioCandidateAsync(
        AppleDownloadRequest request,
        VideoDownloadContext context,
        AppleHlsDownloadResult videoDownload,
        VideoTempPaths tempPaths,
        IDictionary<string, string> keyCache,
        List<string> tags,
        CancellationToken cancellationToken)
    {
        AppleHlsMediaEntry? resolvedAudio = null;
        string? resolvedVideoKeyUri = null;
        var keyAcquisitionFailed = false;
        var muxFailed = false;
        var muxNoAudioTrack = false;

        for (var i = 0; i < context.AudioCandidates.Count; i++)
        {
            var candidate = context.AudioCandidates[i];
            var attemptContext = new VideoAudioCandidateAttemptContext
            {
                Request = request,
                DownloadContext = context,
                VideoDownload = videoDownload,
                TempPaths = tempPaths,
                KeyCache = keyCache,
                Tags = tags,
                ResolvedVideoKeyUri = resolvedVideoKeyUri,
                CancellationToken = cancellationToken
            };
            var attempt = await TryProcessVideoAudioCandidateAsync(
                attemptContext,
                candidate,
                i);
            resolvedVideoKeyUri = attempt.ResolvedVideoKeyUri;
            keyAcquisitionFailed |= attempt.KeyAcquisitionFailed;
            muxFailed |= attempt.MuxFailed;
            muxNoAudioTrack |= attempt.MuxNoAudioTrack;
            if (!attempt.Success)
            {
                continue;
            }

            resolvedAudio = candidate;
            if (i > 0 && _logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Apple MV audio fallback succeeded for {AppleId}: selected_group={GroupId} attempt={Attempt}",
                    context.AppleId,
                    candidate.GroupId,
                    i + 1);
            }
            break;
        }

        if (resolvedAudio != null)
        {
            return new VideoCandidateSelectionResult(resolvedAudio, string.Empty);
        }

        return new VideoCandidateSelectionResult(
            null,
            ResolveVideoCandidateFailureMessage(keyAcquisitionFailed, muxFailed, muxNoAudioTrack));
    }

    private async Task<VideoCandidateAttemptResult> TryProcessVideoAudioCandidateAsync(
        VideoAudioCandidateAttemptContext attemptContext,
        AppleHlsMediaEntry candidate,
        int attemptIndex)
    {
        if (string.IsNullOrWhiteSpace(candidate.Uri))
        {
            return new VideoCandidateAttemptResult(false, attemptContext.ResolvedVideoKeyUri, false, false, false);
        }

        LogVideoAudioCandidateAttempt(attemptContext.DownloadContext.AppleId, candidate, attemptIndex + 1, attemptContext.DownloadContext.AudioCandidates.Count);
        ResetVideoAudioAttemptArtifacts(attemptContext.TempPaths, attemptContext.DownloadContext.OutputPath);

        var audioDownload = await DownloadVideoAudioCandidateAsync(
            attemptContext.Request,
            attemptContext.DownloadContext.AppleId,
            candidate,
            attemptContext.TempPaths.TempAudio,
            attemptContext.CancellationToken);
        if (audioDownload == null)
        {
            return new VideoCandidateAttemptResult(false, attemptContext.ResolvedVideoKeyUri, false, false, false);
        }

        var videoKeyUri = ResolveVideoKeyUri(attemptContext.VideoDownload.KeyUri, audioDownload.KeyUri);
        if (string.IsNullOrWhiteSpace(videoKeyUri))
        {
            _logger.LogWarning("Apple MV missing video decryption key URI for {AppleId}.", attemptContext.DownloadContext.AppleId);
            return new VideoCandidateAttemptResult(false, attemptContext.ResolvedVideoKeyUri, false, false, false);
        }

        var videoDecrypt = await EnsureVideoTrackDecryptedAsync(
            attemptContext.Request,
            attemptContext.DownloadContext,
            videoKeyUri,
            attemptContext.ResolvedVideoKeyUri,
            attemptContext.KeyCache,
            attemptContext.TempPaths,
            attemptContext.CancellationToken);
        if (!videoDecrypt.Success)
        {
            return new VideoCandidateAttemptResult(false, attemptContext.ResolvedVideoKeyUri, videoDecrypt.KeyAcquisitionFailed, false, false);
        }

        await ReportProgressAsync(attemptContext.Request.ProgressCallback, 84);

        var audioDecrypt = await DecryptVideoAudioTrackAsync(
            attemptContext,
            candidate,
            audioDownload,
            videoKeyUri);
        if (!audioDecrypt.Success)
        {
            return new VideoCandidateAttemptResult(false, videoKeyUri, audioDecrypt.KeyAcquisitionFailed, false, false);
        }

        await ReportProgressAsync(attemptContext.Request.ProgressCallback, 92);

        var muxStatus = await MuxVideoAndAudioTrackAsync(
            attemptContext.DownloadContext,
            candidate,
            attemptContext.TempPaths,
            attemptContext.Tags,
            attemptContext.CancellationToken);

        if (muxStatus == VideoMuxStatus.Success)
        {
            await ReportProgressAsync(attemptContext.Request.ProgressCallback, 97);
        }

        return muxStatus switch
        {
            VideoMuxStatus.Success => new VideoCandidateAttemptResult(true, videoKeyUri, false, false, false),
            VideoMuxStatus.MissingAudioTrack => new VideoCandidateAttemptResult(false, videoKeyUri, false, false, true),
            _ => new VideoCandidateAttemptResult(false, videoKeyUri, false, true, false)
        };
    }

    private sealed class VideoAudioCandidateAttemptContext
    {
        public required AppleDownloadRequest Request { get; init; }
        public required VideoDownloadContext DownloadContext { get; init; }
        public required AppleHlsDownloadResult VideoDownload { get; init; }
        public required VideoTempPaths TempPaths { get; init; }
        public required IDictionary<string, string> KeyCache { get; init; }
        public required List<string> Tags { get; init; }
        public string? ResolvedVideoKeyUri { get; init; }
        public required CancellationToken CancellationToken { get; init; }
    }

    private void LogVideoAudioCandidateAttempt(
        string appleId,
        AppleHlsMediaEntry candidate,
        int attempt,
        int total)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Apple MV audio candidate attempt {Attempt}/{Total} for {AppleId}: group={GroupId} name={Name}",
                attempt,
                total,
                appleId,
                string.IsNullOrWhiteSpace(candidate.GroupId) ? UnknownValue : candidate.GroupId,
                string.IsNullOrWhiteSpace(candidate.Name) ? UnknownValue : candidate.Name);        }
    }

    private static void ResetVideoAudioAttemptArtifacts(VideoTempPaths tempPaths, string outputPath)
    {
        TryDelete(tempPaths.TempAudio);
        TryDelete(tempPaths.DecryptedAudio);
        TryDelete(outputPath);
    }

    private async Task<AppleHlsDownloadResult?> DownloadVideoAudioCandidateAsync(
        AppleDownloadRequest request,
        string appleId,
        AppleHlsMediaEntry candidate,
        string tempAudioPath,
        CancellationToken cancellationToken)
    {
        var audioProgress = CreateScaledProgress(request.ProgressCallback, 42, 75);
        var audioDownload = await _hlsDownloader.DownloadAsync(candidate.Uri, tempAudioPath, audioProgress, cancellationToken);
        if (audioDownload.Success)
        {
            return audioDownload;
        }

        _logger.LogWarning(
            "Apple MV audio candidate failed to download for {AppleId}: group={GroupId} error={Error}",
            appleId,
            candidate.GroupId,
            audioDownload.Message);
        return null;
    }

    private static string ResolveVideoKeyUri(string? videoKeyUri, string? audioKeyUri)
    {
        return !string.IsNullOrWhiteSpace(videoKeyUri) ? videoKeyUri : audioKeyUri ?? string.Empty;
    }

    private async Task<(bool Success, bool KeyAcquisitionFailed)> EnsureVideoTrackDecryptedAsync(
        AppleDownloadRequest request,
        VideoDownloadContext context,
        string videoKeyUri,
        string? resolvedVideoKeyUri,
        IDictionary<string, string> keyCache,
        VideoTempPaths tempPaths,
        CancellationToken cancellationToken)
    {
        if (string.Equals(resolvedVideoKeyUri, videoKeyUri, StringComparison.OrdinalIgnoreCase)
            && File.Exists(tempPaths.DecryptedVideo))
        {
            return (true, false);
        }

        var videoKey = await ResolveCachedVideoDecryptionKeyAsync(
            keyCache,
            request,
            context.AppleId,
            videoKeyUri,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(videoKey))
        {
            _logger.LogWarning("Apple MV video key acquisition failed for {AppleId}.", context.AppleId);
            return (false, true);
        }

        if (await _toolRunner.RunMp4DecryptAsync(videoKey, tempPaths.TempVideo, tempPaths.DecryptedVideo, cancellationToken))
        {
            return (true, false);
        }

        _logger.LogWarning("Apple MV video decrypt failed for {AppleId}.", context.AppleId);
        return (false, false);
    }

    private async Task<(bool Success, bool KeyAcquisitionFailed)> DecryptVideoAudioTrackAsync(
        VideoAudioCandidateAttemptContext attemptContext,
        AppleHlsMediaEntry candidate,
        AppleHlsDownloadResult audioDownload,
        string videoKeyUri)
    {
        var audioKeyUri = !string.IsNullOrWhiteSpace(audioDownload.KeyUri) ? audioDownload.KeyUri : videoKeyUri;
        var audioKey = await ResolveCachedVideoDecryptionKeyAsync(
            attemptContext.KeyCache,
            attemptContext.Request,
            attemptContext.DownloadContext.AppleId,
            audioKeyUri,
            attemptContext.CancellationToken);
        if (string.IsNullOrWhiteSpace(audioKey))
        {
            _logger.LogWarning(
                "Apple MV audio key acquisition failed for {AppleId}: group={GroupId}",
                attemptContext.DownloadContext.AppleId,
                candidate.GroupId);
            return (false, true);
        }

        if (await _toolRunner.RunMp4DecryptAsync(
                audioKey,
                attemptContext.TempPaths.TempAudio,
                attemptContext.TempPaths.DecryptedAudio,
                attemptContext.CancellationToken))
        {
            return (true, false);
        }

        _logger.LogWarning(
            "Apple MV audio decrypt failed for {AppleId}: group={GroupId}",
            attemptContext.DownloadContext.AppleId,
            candidate.GroupId);
        return (false, false);
    }

    private async Task<VideoMuxStatus> MuxVideoAndAudioTrackAsync(
        VideoDownloadContext context,
        AppleHlsMediaEntry candidate,
        VideoTempPaths tempPaths,
        List<string> tags,
        CancellationToken cancellationToken)
    {
        if (!await _toolRunner.RunMp4BoxMuxAsync(
                tempPaths.DecryptedVideo,
                tempPaths.DecryptedAudio,
                context.OutputPath,
                tags,
                cancellationToken))
        {
            _logger.LogWarning(
                "Apple MV mux failed for {AppleId}: group={GroupId}",
                context.AppleId,
                candidate.GroupId);
            return VideoMuxStatus.Failed;
        }

        if (await AppleExternalToolRunner.HasAudioTrackAsync(context.OutputPath, cancellationToken))
        {
            return VideoMuxStatus.Success;
        }

        _logger.LogWarning(
            "Apple MV mux output has no audio track for {AppleId}: group={GroupId}",
            context.AppleId,
            candidate.GroupId);
        return VideoMuxStatus.MissingAudioTrack;
    }

    private async Task<string> ResolveCachedVideoDecryptionKeyAsync(
        IDictionary<string, string> keyCache,
        AppleDownloadRequest request,
        string appleId,
        string keyUri,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(keyUri))
        {
            return string.Empty;
        }

        if (keyCache.TryGetValue(keyUri, out var cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var resolved = await ResolveVideoDecryptionKeyAsync(request, appleId, keyUri, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            keyCache[keyUri] = resolved;
        }

        return resolved;
    }

    private async Task<string> ResolveVideoDecryptionKeyAsync(
        AppleDownloadRequest request,
        string appleId,
        string keyUri,
        CancellationToken cancellationToken)
    {
        var keyInfo = TryParseKeyUri(keyUri);
        if (keyInfo == null)
        {
            return string.Empty;
        }

        var pssh = !string.IsNullOrWhiteSpace(keyInfo.Value.PsshBase64)
            ? keyInfo.Value.PsshBase64
            : AppleKeyService.BuildPssh(keyInfo.Value.KidBase64 ?? string.Empty, appleId);
        if (string.IsNullOrWhiteSpace(pssh))
        {
            return string.Empty;
        }

        var keyBytes = await AcquireWidevineKeyWithTokenRefreshAsync(
            request,
            appleId,
            keyUri,
            keyInfo.Value.UriPrefix,
            pssh,
            cancellationToken);
        return BuildDecryptKeySpec(keyUri, keyBytes);
    }

    private static string ResolveVideoCandidateFailureMessage(
        bool keyAcquisitionFailed,
        bool muxFailed,
        bool muxNoAudioTrack)
    {
        if (keyAcquisitionFailed && !muxNoAudioTrack && !muxFailed)
        {
            return "Apple video key acquisition failed.";
        }

        if (muxNoAudioTrack)
        {
            return "Apple video mux completed without an audio track.";
        }

        if (muxFailed)
        {
            return "Apple video mux failed.";
        }

        return "Apple video mux completed without an audio track.";
    }

    private static async Task ReportVideoCompletionAsync(AppleDownloadRequest request)
    {
        if (request.ProgressCallback != null)
        {
            await request.ProgressCallback(99, 0);
        }
    }

    private static AppleDownloadResult BuildVideoSuccessResult(
        VideoDownloadContext context,
        AppleHlsMediaEntry resolvedAudio)
    {
        var resolutionTier = ResolveVideoResolutionTier(context.VideoVariant.Resolution, context.Attributes.Has4K);
        var hasHdr = ResolveVideoHdr(context.VideoVariant.VideoRange, context.Attributes.HasHdr);
        var audioProfile = ResolveVideoAudioProfile(resolvedAudio);
        var streamParts = new List<string> { "video" };
        if (!string.IsNullOrWhiteSpace(context.VideoVariant.Resolution))
        {
            streamParts.Add(context.VideoVariant.Resolution);
        }
        if (hasHdr)
        {
            streamParts.Add("hdr");
        }
        if (!string.IsNullOrWhiteSpace(resolvedAudio.GroupId))
        {
            streamParts.Add(resolvedAudio.GroupId);
        }

        var videoStreamGroup = string.Join('-', streamParts);
        return AppleDownloadResult.OkVideo(context.OutputPath, videoStreamGroup, resolutionTier, hasHdr, audioProfile);
    }

    private static void CleanupVideoTempArtifacts(VideoTempPaths tempPaths)
    {
        TryDelete(tempPaths.TempVideo);
        TryDelete(tempPaths.TempAudio);
        TryDelete(tempPaths.DecryptedVideo);
        TryDelete(tempPaths.DecryptedAudio);
        if (!string.IsNullOrWhiteSpace(tempPaths.TempCover))
        {
            TryDelete(tempPaths.TempCover);
        }
    }

    private async Task<byte[]> AcquireWidevineKeyWithTokenRefreshAsync(
        AppleDownloadRequest request,
        string adamId,
        string keyUri,
        string uriPrefix,
        string pssh,
        CancellationToken cancellationToken,
        string? licenseEndpointOverride = null)
    {
        var initialLicenseRequest = new AppleWidevineLicenseClient.AppleWidevineLicenseRequest(
            adamId,
            request.AuthorizationToken,
            request.MediaUserToken,
            keyUri,
            uriPrefix,
            pssh,
            licenseEndpointOverride);
        var keyBytes = await _licenseClient.AcquireKeyAsync(initialLicenseRequest, cancellationToken);
        if (keyBytes.Length > 0)
        {
            return keyBytes;
        }

        try
        {
            var refreshedToken = await _catalogService.GetAuthorizationTokenAsync(cancellationToken);
            var normalized = NormalizeBearerToken(refreshedToken);
            if (!string.IsNullOrWhiteSpace(normalized)
                && !string.Equals(normalized, request.AuthorizationToken, StringComparison.Ordinal))
            {
                request.AuthorizationToken = normalized;
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Retrying Apple Widevine key acquisition with refreshed dev token for {AdamId}.", adamId);                }
                var refreshedLicenseRequest = new AppleWidevineLicenseClient.AppleWidevineLicenseRequest(
                    adamId,
                    request.AuthorizationToken,
                    request.MediaUserToken,
                    keyUri,
                    uriPrefix,
                    pssh,
                    licenseEndpointOverride);
                keyBytes = await _licenseClient.AcquireKeyAsync(refreshedLicenseRequest, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple dev token refresh failed before Widevine retry for {AdamId}.", adamId);
        }

        return keyBytes;
    }

    private async Task<AppleDownloadResult> DownloadAndDecryptWidevineAudioAsync(
        AppleDownloadRequest request,
        WidevineDownloadContext context,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.Join(Path.GetTempPath(), $"{context.TempPrefix}-{Guid.NewGuid():N}.mp4");
        var hlsProgress = CreateScaledProgress(request.ProgressCallback, 0, 82);
        var hlsDownload = await _hlsDownloader.DownloadAsync(context.MediaPlaylistUrl, tempPath, hlsProgress, cancellationToken);
        if (!hlsDownload.Success)
        {
            context.FailureHandlers.OnHlsFailure?.Invoke(hlsDownload.Message);
            return AppleDownloadResult.Fail(hlsDownload.Message);
        }

        try
        {
            var keyBytes = await AcquireWidevineKeyWithTokenRefreshAsync(
                request,
                context.AdamId,
                context.DrmInfo.KeyUri,
                context.DrmInfo.UriPrefix,
                context.Pssh,
                cancellationToken,
                context.LicenseEndpointOverride);

            await ReportProgressAsync(request.ProgressCallback, 90);

            var key = BuildDecryptKeySpec(hlsDownload.KeyUri, keyBytes);
            if (string.IsNullOrWhiteSpace(key))
            {
                context.FailureHandlers.OnKeyFailure?.Invoke();
                return AppleDownloadResult.Fail(context.KeyFailureMessage);
            }

            if (!AppleExternalToolRunner.HasMp4Decrypt())
            {
                const string mp4DecryptMissing = "mp4decrypt executable not found. Install Bento4 mp4decrypt or set DEEZSPOTAG_APPLE_MP4DECRYPT_PATH.";
                _logger.LogWarning("Apple decryption prerequisite missing for {AdamId}: {Message}", context.AdamId, mp4DecryptMissing);
                return AppleDownloadResult.Fail(mp4DecryptMissing);
            }

            var outputPath = BuildOutputPath(request, context.AdamId);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? string.Empty);
            var decrypted = await _toolRunner.RunMp4DecryptAsync(key, hlsDownload.OutputPath, outputPath, cancellationToken);
            if (!decrypted)
            {
                context.FailureHandlers.OnDecryptFailure?.Invoke(outputPath);
                return AppleDownloadResult.Fail(context.DecryptFailureMessage);
            }

            var validation = await _toolRunner.ValidateDecodableAudioAsync(outputPath, cancellationToken);
            if (!validation.Success)
            {
                context.FailureHandlers.OnDecryptFailure?.Invoke(outputPath);
                TryDelete(outputPath);
                return AppleDownloadResult.Fail(validation.Message);
            }

            var durationValidation = await _toolRunner.ValidateExpectedDurationAsync(
                outputPath,
                request.DurationSeconds,
                cancellationToken);
            if (!durationValidation.Success)
            {
                context.FailureHandlers.OnDecryptFailure?.Invoke(outputPath);
                TryDelete(outputPath);
                return AppleDownloadResult.Fail(durationValidation.Message);
            }

            await ReportProgressAsync(request.ProgressCallback, 98);
            return AppleDownloadResult.Ok(outputPath, context.StreamGroup);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private async Task TryHydrateTokensFromWrapperAsync(AppleDownloadRequest request, CancellationToken cancellationToken)
    {
        var authMissing = string.IsNullOrWhiteSpace(request.AuthorizationToken);
        var mediaMissing = string.IsNullOrWhiteSpace(request.MediaUserToken) || request.MediaUserToken.Length < 50;
        var storefrontMissing = string.IsNullOrWhiteSpace(request.Storefront);
        if (!authMissing && !mediaMissing && !storefrontMissing)
        {
            return;
        }

        var wrapperHost = Environment.GetEnvironmentVariable("DEEZSPOTAG_APPLE_WRAPPER_HOST");
        if (string.IsNullOrWhiteSpace(wrapperHost))
            wrapperHost = "127.0.0.1";

        try
        {
            var accountUri = BuildWrapperAccountUri(wrapperHost);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, accountUri);
            using var response = await WrapperAccountClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var hasDevToken = root.TryGetProperty("has_dev_token", out var hasDevTokenEl)
                && hasDevTokenEl.ValueKind == JsonValueKind.True;
            var hasMusicToken = root.TryGetProperty("has_music_token", out var hasMusicTokenEl)
                && hasMusicTokenEl.ValueKind == JsonValueKind.True;

            ApplyWrapperDevToken(root, request);
            ApplyWrapperMusicToken(root, request);
            if (storefrontMissing)
            {
                ApplyWrapperStorefront(root, request);
            }

            LogMissingWrapperTokens(authMissing, mediaMissing, hasDevToken, hasMusicToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to hydrate Apple tokens from wrapper account endpoint.");
        }
    }

    private static Uri BuildWrapperAccountUri(string wrapperHost)
    {
        return new UriBuilder(Uri.UriSchemeHttp, wrapperHost, 30020, "account")
        {
            Query = "include_tokens=1"
        }.Uri;
    }

    private void ApplyWrapperDevToken(JsonElement root, AppleDownloadRequest request)
    {
        if (!root.TryGetProperty("dev_token", out var devTokenEl) || devTokenEl.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var devToken = NormalizeBearerToken(devTokenEl.GetString());
        if (string.IsNullOrWhiteSpace(devToken)
            || string.Equals(devToken, request.AuthorizationToken, StringComparison.Ordinal))
        {
            return;
        }

        request.AuthorizationToken = devToken;
        _logger.LogInformation("Apple download request synchronized dev token from wrapper account endpoint.");
    }

    private void ApplyWrapperMusicToken(JsonElement root, AppleDownloadRequest request)
    {
        var normalizedMusicToken = ReadWrapperMusicToken(root);
        if (string.IsNullOrWhiteSpace(normalizedMusicToken)
            || normalizedMusicToken.Length < 50
            || string.Equals(normalizedMusicToken, request.MediaUserToken, StringComparison.Ordinal))
        {
            return;
        }

        request.MediaUserToken = normalizedMusicToken;
        _logger.LogInformation("Apple download request synchronized media user token from wrapper account endpoint.");
    }

    private static string? ReadWrapperMusicToken(JsonElement root)
    {
        if (root.TryGetProperty("music_user_token", out var mutEl) && mutEl.ValueKind == JsonValueKind.String)
        {
            return mutEl.GetString()?.Trim();
        }

        return root.TryGetProperty("music_token", out var musicTokenEl) && musicTokenEl.ValueKind == JsonValueKind.String
            ? musicTokenEl.GetString()?.Trim()
            : null;
    }

    private static void ApplyWrapperStorefront(JsonElement root, AppleDownloadRequest request)
    {
        if (!root.TryGetProperty("storefront_id", out var storefrontEl) || storefrontEl.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var storefront = storefrontEl.GetString()?.Trim();
        if (!string.IsNullOrWhiteSpace(storefront))
        {
            request.Storefront = storefront;
        }
    }

    private void LogMissingWrapperTokens(bool authMissing, bool mediaMissing, bool hasDevToken, bool hasMusicToken)
    {
        if (authMissing && !hasDevToken)
        {
            _logger.LogDebug("Wrapper account endpoint reachable but dev token is unavailable.");
        }

        if (mediaMissing && !hasMusicToken)
        {
            _logger.LogDebug("Wrapper account endpoint reachable but media user token is unavailable.");
        }
    }

    private static string NormalizeBearerToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        var normalized = token.Trim();
        if (normalized.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["Bearer ".Length..].Trim();
        }

        return normalized;
    }

    private static bool TryExtractVideoAttributes(JsonElement root, out AppleVideoAttributes attrs)
    {
        attrs = new AppleVideoAttributes();
        if (!root.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array || dataArr.GetArrayLength() == 0)
        {
            return false;
        }

        var data = dataArr[0];
        if (!data.TryGetProperty(AttributesKey, out var attributes) || attributes.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var has4K = TryReadBoolean(attributes, "has4K");
        var hasHdr = TryReadBoolean(attributes, "hasHDR");
        var genre = TryReadFirstGenre(attributes);

        attrs = new AppleVideoAttributes
        {
            Name = attributes.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
            ArtistName = attributes.TryGetProperty("artistName", out var artistEl) ? artistEl.GetString() ?? "" : "",
            AlbumName = attributes.TryGetProperty("albumName", out var albumEl) ? albumEl.GetString() ?? "" : "",
            ReleaseDate = attributes.TryGetProperty("releaseDate", out var dateEl) ? dateEl.GetString() ?? "" : "",
            Isrc = attributes.TryGetProperty("isrc", out var isrcEl) ? isrcEl.GetString() ?? "" : "",
            Genre = genre,
            ArtworkUrl = TryGetArtwork(attributes, 1200),
            ContentRating = attributes.TryGetProperty("contentRating", out var ratingEl) ? ratingEl.GetString() ?? "" : "",
            Has4K = has4K,
            HasHdr = hasHdr
        };

        return true;
    }

    private static string TryGetArtwork(JsonElement attributes, int size)
    {
        if (!attributes.TryGetProperty("artwork", out var art) || art.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (!art.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var raw = urlEl.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var sizeStr = $"{size}x{size}";
        return raw.Replace("{w}x{h}", sizeStr).Replace("{w}", size.ToString()).Replace("{h}", size.ToString());
    }

    private static AppleHlsVariantEntry? SelectVideoVariant(
        AppleHlsMasterManifest master,
        int maxResolution,
        string? preferredAudioType = null,
        string? preferredVideoCodec = null)
    {
        if (master.Variants.Count == 0)
        {
            return null;
        }

        var normalizedCodecPreference = NormalizeVideoCodecPreference(preferredVideoCodec);
        var candidates = master.Variants
            .Select(v =>
            {
                var height = ParseResolutionHeight(v.Resolution);
                return (Variant: v, Height: height);
            })
            .Where(x => x.Height == 0 || x.Height <= maxResolution)
            .OrderByDescending(x => x.Height)
            .ThenByDescending(x => ScoreVideoCodecPreference(x.Variant.Codecs, normalizedCodecPreference))
            .ThenByDescending(x => x.Variant.AverageBandwidth > 0 ? x.Variant.AverageBandwidth : x.Variant.Bandwidth)
            .ToList();

        var strictAtmosAudio = IsStrictAtmosAudioPreference(preferredAudioType);
        if (strictAtmosAudio)
        {
            var atmosGroups = master.Media
                .Where(entry => string.Equals(entry.Type, "AUDIO", StringComparison.OrdinalIgnoreCase))
                .Where(IsAtmosAudioEntry)
                .Select(entry => entry.GroupId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            candidates = candidates
                .Where(candidate => IsAtmosVideoVariant(candidate.Variant, atmosGroups))
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }
        }

        if (candidates.Count > 0)
        {
            var preferredGroups = ResolveVideoAudioGroupPreference(preferredAudioType);
            foreach (var preferred in preferredGroups)
            {
                var matchingVariant = candidates
                    .Select(candidate => candidate.Variant)
                    .FirstOrDefault(variant => MatchesAudioGroup(variant.AudioGroup, preferred));
                if (matchingVariant != null)
                {
                    return matchingVariant;
                }
            }
        }

        return candidates.FirstOrDefault().Variant ?? master.Variants
            .OrderByDescending(v => ScoreVideoCodecPreference(v.Codecs, normalizedCodecPreference))
            .ThenByDescending(v => v.AverageBandwidth > 0 ? v.AverageBandwidth : v.Bandwidth)
            .FirstOrDefault();
    }

    private static string NormalizeVideoCodecPreference(string? preference)
    {
        if (string.IsNullOrWhiteSpace(preference))
        {
            return "prefer-hevc";
        }

        var normalized = preference.Trim().ToLowerInvariant();
        if (normalized.Contains("auto", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        if (normalized.Contains("avc", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("h264", StringComparison.OrdinalIgnoreCase))
        {
            return "prefer-avc";
        }

        if (normalized.Contains("hevc", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("h265", StringComparison.OrdinalIgnoreCase))
        {
            return "prefer-hevc";
        }

        return "prefer-hevc";
    }

    private static int ScoreVideoCodecPreference(string codecs, string normalizedPreference)
    {
        if (string.IsNullOrWhiteSpace(codecs) || string.Equals(normalizedPreference, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var normalizedCodecs = codecs.ToLowerInvariant();
        var hasHevc = normalizedCodecs.Contains("hvc1", StringComparison.OrdinalIgnoreCase)
            || normalizedCodecs.Contains("hev1", StringComparison.OrdinalIgnoreCase)
            || normalizedCodecs.Contains("hevc", StringComparison.OrdinalIgnoreCase)
            || normalizedCodecs.Contains("h265", StringComparison.OrdinalIgnoreCase);
        var hasAvc = normalizedCodecs.Contains("avc1", StringComparison.OrdinalIgnoreCase)
            || normalizedCodecs.Contains("avc3", StringComparison.OrdinalIgnoreCase)
            || normalizedCodecs.Contains("h264", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(normalizedPreference, "prefer-avc", StringComparison.OrdinalIgnoreCase))
        {
            if (hasAvc)
            {
                return 2;
            }

            if (hasHevc)
            {
                return 1;
            }

            return 0;
        }

        if (hasHevc)
        {
            return 2;
        }

        if (hasAvc)
        {
            return 1;
        }

        return 0;
    }

    private static int ResolveVideoMaxResolution(int configuredMax)
    {
        var maxResolution = configuredMax > 0 ? configuredMax : 2160;
        return maxResolution;
    }

    private static int ParseResolutionHeight(string resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
        {
            return 0;
        }

        var parts = resolution.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return 0;
        }

        return int.TryParse(parts[1], out var height) ? height : 0;
    }

    private static List<AppleHlsMediaEntry> SelectVideoAudioCandidates(AppleHlsMasterManifest master, string audioType, string? preferredGroupId = null)
    {
        if (master.Media.Count == 0)
        {
            return new List<AppleHlsMediaEntry>();
        }

        var audioEntries = master.Media
            .Where(m => string.Equals(m.Type, "AUDIO", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (audioEntries.Count == 0)
        {
            return new List<AppleHlsMediaEntry>();
        }

        var strictAtmosAudio = IsStrictAtmosAudioPreference(audioType);
        var desiredGroups = ResolveVideoAudioGroupPreference(audioType);

        var ordered = new List<AppleHlsMediaEntry>();
        var seenUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var canPrioritizePreferredGroup = !strictAtmosAudio
            || (AppleAtmosHeuristics.ContainsAtmosToken(preferredGroupId, AtmosKeyword)
                || audioEntries.Any(entry =>
                    string.Equals(entry.GroupId, preferredGroupId, StringComparison.OrdinalIgnoreCase)
                    && IsAtmosAudioEntry(entry)));
        if (!string.IsNullOrWhiteSpace(preferredGroupId) && canPrioritizePreferredGroup)
        {
            var preferred = audioEntries
                .Where(entry => string.Equals(entry.GroupId, preferredGroupId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => ParseGroupRank(entry.Uri))
                .ThenByDescending(entry => entry.Default)
                .ThenByDescending(entry => entry.AutoSelect)
                .ToList();

            AddUniqueAudioEntries(ordered, seenUris, preferred);
        }

        foreach (var group in BuildDesiredAudioGroups(audioEntries, desiredGroups))
        {
            AddUniqueAudioEntries(ordered, seenUris, group);
        }

        if (!strictAtmosAudio)
        {
            AddUniqueAudioEntries(ordered, seenUris, audioEntries.OrderByDescending(m => ParseGroupRank(m.Uri)));
        }

        return ordered;
    }

    private static string? ValidateStationTokens(AppleDownloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AuthorizationToken))
        {
            return "Apple station download missing authorization token.";
        }

        if (string.IsNullOrWhiteSpace(request.MediaUserToken))
        {
            return "Apple station download missing media user token.";
        }

        return null;
    }

    private async Task TryPopulateStationMetadataAsync(
        AppleDownloadRequest request,
        string stationId,
        string storefront,
        string language,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.TrackName))
        {
            return;
        }

        try
        {
            using var stationDoc = await _catalogService.GetStationAsync(stationId, storefront, language, cancellationToken);
            if (!TryExtractStationName(stationDoc.RootElement, out var stationName) || string.IsNullOrWhiteSpace(stationName))
            {
                return;
            }

            request.TrackName = stationName;
            if (string.IsNullOrWhiteSpace(request.AlbumName))
            {
                request.AlbumName = stationName;
            }
            if (string.IsNullOrWhiteSpace(request.ArtistName))
            {
                request.ArtistName = "Apple Music";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple station metadata lookup failed for {StationId}", stationId);            }
        }
    }

    private async Task<(bool Success, string? MediaPlaylistUrl, string StreamGroup, string? ErrorMessage)> ResolveStationStreamAsync(
        string manifestUrl,
        AppleDownloadRequest request,
        string stationId,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var manifestText = await GetCachedTextAsync(
            $"apple:station-manifest:{manifestUrl}",
            TimeSpan.FromMinutes(5),
            () => client.GetStringAsync(manifestUrl, cancellationToken));

        var mediaPlaylistUrl = manifestUrl;
        var streamGroup = string.Empty;
        try
        {
            var master = AppleHlsManifestParser.ParseMaster(manifestText, new Uri(manifestUrl));
            if (master.Variants.Count == 0)
            {
                return (true, mediaPlaylistUrl, streamGroup, null);
            }

            var variant = SelectVariant(master, request)
                ?? master.Variants
                    .OrderByDescending(v => v.AverageBandwidth > 0 ? v.AverageBandwidth : v.Bandwidth)
                    .FirstOrDefault();
            if (variant == null || string.IsNullOrWhiteSpace(variant.Uri))
            {
                return (false, null, string.Empty, "No station HLS variant found.");
            }

            mediaPlaylistUrl = variant.Uri;
            streamGroup = variant.AudioGroup;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple station manifest treated as media playlist for {StationId}", stationId);            }
        }

        return (true, mediaPlaylistUrl, streamGroup, null);
    }

    private string? ValidateAacLcTokens(AppleDownloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AuthorizationToken))
        {
            _logger.LogError("Apple AAC-LC download failed: AuthorizationToken is missing. Configure it in settings or ensure automatic token fetching is working.");
            return "Apple download missing authorization token. Check your Apple Music settings.";
        }

        if (string.IsNullOrWhiteSpace(request.MediaUserToken))
        {
            _logger.LogError("Apple AAC-LC download failed: MediaUserToken is missing. This is required for AAC downloads. Extract it from the 'media-user-token' cookie at music.apple.com.");
            return "Apple AAC download missing media user token. Configure MediaUserToken in Apple Music settings.";
        }

        if (request.MediaUserToken.Length < 50)
        {
            _logger.LogError(
                "Apple AAC-LC download failed: MediaUserToken appears invalid (length={Length}). Valid tokens are typically 100+ characters. Re-extract from music.apple.com cookies.",
                request.MediaUserToken.Length);
            return $"Apple MediaUserToken appears invalid (too short: {request.MediaUserToken.Length} chars). Re-extract from music.apple.com cookies.";
        }

        return null;
    }

    private async Task<string?> ResolveStorefrontForAacAsync(string mediaUserToken, string? configuredStorefront, CancellationToken cancellationToken)
    {
        var accountStorefront = await _catalogService.GetAccountStorefrontAsync(mediaUserToken, cancellationToken);
        if (!string.IsNullOrWhiteSpace(accountStorefront))
        {
            return accountStorefront;
        }

        _logger.LogWarning(
            "Apple AAC-LC download skipped: active subscription storefront could not be verified. configuredStorefront={Storefront}",
            configuredStorefront ?? string.Empty);
        return null;
    }

    private async Task<string> ResolveAacAppleIdAsync(
        AppleDownloadRequest request,
        string originalAppleId,
        string storefront,
        CancellationToken cancellationToken)
    {
        var resolvedAppleId = originalAppleId;
        try
        {
            var doc = await _catalogService.GetSongAsync(originalAppleId, storefront, DefaultLanguage, cancellationToken, request.MediaUserToken);
            var catalogId = TryExtractAppleIdFromCatalog(doc);
            if (!string.IsNullOrWhiteSpace(catalogId) && catalogId != originalAppleId)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Apple AAC-LC: ID resolved via catalog for storefront {Storefront}: {OriginalId} -> {ResolvedId}",
                        storefront,
                        originalAppleId,
                        catalogId);                }
                resolvedAppleId = catalogId;
            }
        }
        catch (HttpRequestException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple AAC-LC: Direct catalog lookup failed for {AppleId}, trying ISRC fallback.", originalAppleId);            }
        }

        if (resolvedAppleId == originalAppleId && !string.IsNullOrWhiteSpace(request.Isrc))
        {
            var isrcDoc = await TryGetSongByIsrcAsync(request.Isrc, storefront, DefaultLanguage, request.MediaUserToken, cancellationToken);
            var isrcResolvedId = TryExtractAppleIdFromCatalog(isrcDoc);
            if (!string.IsNullOrWhiteSpace(isrcResolvedId))
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Apple AAC-LC: ID resolved via ISRC {Isrc} for storefront {Storefront}: {OriginalId} -> {ResolvedId}",
                        request.Isrc,
                        storefront,
                        originalAppleId,
                        isrcResolvedId);                }
                resolvedAppleId = isrcResolvedId;
            }
        }

        return resolvedAppleId;
    }

    private static bool TryReadIsrcCandidate(
        JsonElement item,
        out string? candidateId,
        out string? candidateEnhancedHls,
        out JsonElement attrs,
        out bool hasAtmos)
    {
        candidateId = null;
        candidateEnhancedHls = null;
        attrs = default;
        hasAtmos = false;

        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        candidateId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(candidateId))
        {
            return false;
        }

        if (!item.TryGetProperty(AttributesKey, out attrs) || attrs.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!attrs.TryGetProperty("extendedAssetUrls", out var assets) || assets.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        candidateEnhancedHls = assets.TryGetProperty("enhancedHls", out var hlsEl) && hlsEl.ValueKind == JsonValueKind.String
            ? hlsEl.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(candidateEnhancedHls))
        {
            return false;
        }

        hasAtmos = HasAtmosAudioTrait(attrs);
        return true;
    }

    private static bool HasAtmosAudioTrait(JsonElement attrs)
    {
        if (!attrs.TryGetProperty("audioTraits", out var audioTraits) || audioTraits.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var trait in audioTraits.EnumerateArray())
        {
            if (trait.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var traitValue = trait.GetString();
            if (!string.IsNullOrWhiteSpace(traitValue)
                && traitValue.Contains(AtmosKeyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string?> TryResolveDeviceFirstManifestAsync(
        AppleDownloadRequest request,
        string appleId,
        bool isAtmosRequest,
        CancellationToken cancellationToken)
    {
        if (isAtmosRequest && request.GetM3u8FromDevice && IsDeviceFirstMode(request.GetM3u8Mode))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Apple Atmos request for {AppleId}: skipping device-first manifest probe to prefer catalog enhancedHls.",
                    appleId);            }
            return null;
        }

        if (!(request.GetM3u8FromDevice && IsDeviceFirstMode(request.GetM3u8Mode)))
        {
            return null;
        }

        return await TryGetDeviceEnhancedHlsAsync(request.GetM3u8Port, appleId, cancellationToken);
    }

    private async Task<JsonDocument?> TryGetCatalogSongAsync(
        string appleId,
        string storefront,
        string language,
        string? mediaUserToken,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _catalogService.GetSongAsync(appleId, storefront, language, cancellationToken, mediaUserToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Apple catalog request failed for {AppleId} storefront {Storefront}.",
                appleId,
                storefront);
            return null;
        }
    }

    private async Task<(string? ManifestUrl, string ResolvedAppleId)?> TryResolveAtmosPreferredCandidateAsync(
        AppleDownloadRequest request,
        string storefront,
        string language,
        string resolvedAppleId,
        bool isAtmosRequest,
        CancellationToken cancellationToken)
    {
        if (!isAtmosRequest || string.IsNullOrWhiteSpace(request.Isrc))
        {
            return null;
        }

        var isrcAtmosDoc = await TryGetSongByIsrcAsync(request.Isrc, storefront, language, request.MediaUserToken, cancellationToken);
        if (isrcAtmosDoc == null
            || !TrySelectPreferredIsrcCandidate(
                isrcAtmosDoc.RootElement,
                request,
                requireAtmos: true,
                out var preferredAtmosId,
                out var preferredAtmosEnhancedHls))
        {
            return null;
        }

        if (!string.Equals(preferredAtmosId, resolvedAppleId, StringComparison.OrdinalIgnoreCase)
            && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Apple Atmos candidate selected via ISRC for storefront {Storefront}: {OriginalId} -> {ResolvedId}",
                storefront,
                resolvedAppleId,
                preferredAtmosId);
        }

        resolvedAppleId = preferredAtmosId ?? resolvedAppleId;
        return (preferredAtmosEnhancedHls, resolvedAppleId);
    }

    private static bool TryReadBoolean(JsonElement attributes, string property)
    {
        if (!attributes.TryGetProperty(property, out var value))
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.True
               || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed);
    }

    private static string TryReadFirstGenre(JsonElement attributes)
    {
        if (!attributes.TryGetProperty("genreNames", out var genreEl)
            || genreEl.ValueKind != JsonValueKind.Array
            || genreEl.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        return genreEl[0].GetString() ?? string.Empty;
    }

    private static IEnumerable<List<AppleHlsMediaEntry>> BuildDesiredAudioGroups(
        IEnumerable<AppleHlsMediaEntry> audioEntries,
        IEnumerable<string> desiredGroups)
    {
        return desiredGroups.Select(desired => audioEntries
            .Where(m =>
                (!string.IsNullOrWhiteSpace(m.GroupId) && m.GroupId.Contains(desired, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(m.Name) && m.Name.Contains(desired, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(m => ParseGroupRank(m.Uri))
            .ThenByDescending(m => m.Default)
            .ThenByDescending(m => m.AutoSelect)
            .ToList());
    }

    private static void AddUniqueAudioEntries(
        List<AppleHlsMediaEntry> target,
        HashSet<string> seenUris,
        IEnumerable<AppleHlsMediaEntry> source)
    {
        foreach (var entry in source)
        {
            var uriKey = string.IsNullOrWhiteSpace(entry.Uri) ? $"{entry.GroupId}:{entry.Name}" : entry.Uri;
            if (!seenUris.Add(uriKey))
            {
                continue;
            }

            target.Add(entry);
        }
    }

    private static string[] ResolveVideoAudioGroupPreference(string? audioType)
    {
        var preference = audioType?.Trim().ToLowerInvariant();
        return preference switch
        {
            "atmos-strict" => new[] { "audio-atmos", AtmosKeyword, "joc" },
            AtmosKeyword => new[] { "audio-atmos", AtmosKeyword, "joc", "audio-ac3", "ac3", AudioStereo256, AudioStereo, StereoKeyword },
            "aac" => new[] { AudioStereo256, AudioStereo, StereoKeyword, "aac" },
            StereoKeyword => new[] { AudioStereo256, AudioStereo, StereoKeyword, "aac" },
            "ac3" => new[] { "audio-ac3", "ac3" },
            _ => new[] { "audio-atmos", AtmosKeyword, "audio-ac3", "ac3", AudioStereo256, AudioStereo, StereoKeyword }
        };
    }

    private static bool IsStrictAtmosAudioPreference(string? audioType)
    {
        if (string.IsNullOrWhiteSpace(audioType))
        {
            return false;
        }

        return audioType.Contains("atmos-strict", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAtmosVideoVariant(AppleHlsVariantEntry variant, HashSet<string> atmosGroups)
    {
        if (variant == null)
        {
            return false;
        }

        if (AppleAtmosHeuristics.ContainsAtmosToken(variant.AudioGroup, AtmosKeyword)
            || AppleAtmosHeuristics.ContainsAtmosToken(variant.Codecs, AtmosKeyword))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(variant.AudioGroup) && atmosGroups.Contains(variant.AudioGroup);
    }

    private static bool IsAtmosAudioEntry(AppleHlsMediaEntry entry)
    {
        return AppleAtmosHeuristics.ContainsAtmosToken(entry.GroupId, AtmosKeyword)
            || AppleAtmosHeuristics.ContainsAtmosToken(entry.Name, AtmosKeyword)
            || AppleAtmosHeuristics.ContainsAtmosToken(entry.Uri, AtmosKeyword)
            || AppleAtmosHeuristics.ContainsAtmosToken(entry.Characteristics, AtmosKeyword)
            || AppleAtmosHeuristics.IsAtmosChannels(entry.Channels);
    }

    private static bool MatchesAudioGroup(string? groupId, string token)
    {
        return !string.IsNullOrWhiteSpace(groupId)
            && !string.IsNullOrWhiteSpace(token)
            && groupId.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseGroupRank(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return 0;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            uri,
            "_gr(\\d+)_",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            RegexTimeout);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var rank))
        {
            return rank;
        }

        return 0;
    }

    private static string BuildDecryptKeySpec(string? keyUri, byte[] keyBytes)
    {
        if (keyBytes.Length == 0)
        {
            return string.Empty;
        }

        var keyHex = Convert.ToHexString(keyBytes).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(keyUri))
        {
            var keyInfo = TryParseKeyUri(keyUri);
            if (keyInfo != null && !string.IsNullOrWhiteSpace(keyInfo.Value.KidBase64))
            {
                var kidBytes = Base64UrlDecoder.TryDecode(keyInfo.Value.KidBase64);
                if (kidBytes is { Length: > 0 } && kidBytes.Length <= 32)
                {
                    var kidHex = Convert.ToHexString(kidBytes).ToLowerInvariant();
                    return $"{kidHex}:{keyHex}";
                }
            }
        }

        return $"1:{keyHex}";
    }

    private static string ResolveVideoResolutionTier(string resolution, bool has4K)
    {
        var height = ParseResolutionHeight(resolution);
        if (height >= 2160)
        {
            return "4K";
        }

        if (height >= 1080)
        {
            return "FHD";
        }

        if (height >= 720)
        {
            return "HD";
        }

        if (has4K)
        {
            return "4K";
        }

        return string.Empty;
    }

    private static bool ResolveVideoHdr(string? videoRange, bool hasHdr)
    {
        if (!string.IsNullOrWhiteSpace(videoRange))
        {
            var normalized = videoRange.Trim();
            if (normalized.Equals("SDR", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (normalized.Contains("PQ", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("HLG", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("HDR", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("DOLBY", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return hasHdr;
    }

    private static string ResolveVideoAudioProfile(AppleHlsMediaEntry? audioEntry)
    {
        if (audioEntry == null)
        {
            return string.Empty;
        }

        return IsAtmosAudioEntry(audioEntry) ? "Atmos" : "Stereo";
    }

    private static ParsedKeyUri? TryParseKeyUri(string keyUri)
    {
        if (string.IsNullOrWhiteSpace(keyUri))
        {
            return null;
        }

        if (keyUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var marker = "base64,";
            var markerIndex = keyUri.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex <= 0)
            {
                return null;
            }

            var payloadIndex = markerIndex + marker.Length;
            if (payloadIndex >= keyUri.Length)
            {
                return null;
            }

            var uriPrefix = keyUri[..(payloadIndex - 1)];
            var dataPayload = keyUri[payloadIndex..];
            var dataPayloadBytes = Base64UrlDecoder.TryDecode(dataPayload);
            if (dataPayloadBytes is { Length: > 24 })
            {
                // Large payloads in data URIs are usually full PSSH blobs.
                return new ParsedKeyUri(uriPrefix, null, dataPayload);
            }

            // Short payloads are typically the KID; build a proper PSSH from it later.
            return new ParsedKeyUri(uriPrefix, dataPayload, null);
        }

        var split = keyUri.Split(',', 2, StringSplitOptions.TrimEntries);
        if (split.Length < 2 || string.IsNullOrWhiteSpace(split[0]) || string.IsNullOrWhiteSpace(split[1]))
        {
            return null;
        }

        var payload = split[1];
        var payloadBytes = Base64UrlDecoder.TryDecode(payload);
        if (payloadBytes is { Length: > 24 })
        {
            // Some manifests emit URI="<prefix>,<pssh>" instead of URI="<prefix>,<kid>".
            // Treat longer base64 payloads as PSSH to avoid rebuilding a malformed challenge.
            return new ParsedKeyUri(split[0], null, payload);
        }

        return new ParsedKeyUri(split[0], payload, null);
    }

    private readonly record struct ParsedKeyUri(string UriPrefix, string? KidBase64, string? PsshBase64);

    private static List<string> BuildMp4Tags(AppleVideoAttributes attrs)
    {
        var tags = new List<string>
        {
            "tool=",
            $"artist={attrs.ArtistName}",
            $"title={attrs.Name}",
            $"created={attrs.ReleaseDate}",
            $"ISRC={attrs.Isrc}"
        };

        if (!string.IsNullOrWhiteSpace(attrs.Genre))
        {
            tags.Add($"genre={attrs.Genre}");
        }

        if (!string.IsNullOrWhiteSpace(attrs.AlbumName))
        {
            tags.Add($"album={attrs.AlbumName}");
        }

        var rating = attrs.ContentRating?.ToLowerInvariant() ?? string.Empty;
        if (rating == "explicit")
        {
            tags.Add("rating=1");
        }
        else if (rating == "clean")
        {
            tags.Add("rating=2");
        }
        else
        {
            tags.Add("rating=0");
        }

        return tags;
    }

    private static string ResolveVideoOutputDirectory(
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        string artistName,
        string? explicitOutputRoot)
    {
        string baseDir;
        if (!string.IsNullOrWhiteSpace(explicitOutputRoot))
        {
            baseDir = explicitOutputRoot;
        }
        else
        {
            baseDir = settings.Video.VideoDownloadLocation;
        }

        if (string.IsNullOrWhiteSpace(baseDir))
        {
            throw new InvalidOperationException("Video destination is not configured. Set Settings > Video > Video download location.");
        }

        baseDir = DownloadPathResolver.ResolveIoPath(baseDir);
        var artistTemplate = settings.Video.ArtistFolderTemplate ?? "%artist%";
        var artistFolder = artistTemplate.Replace("%artist%", artistName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        artistFolder = SanitizeFileName(artistFolder);

        if (string.IsNullOrWhiteSpace(artistFolder))
        {
            return baseDir;
        }

        return Path.Join(baseDir, artistFolder);
    }

    private static string BuildVideoOutputPath(DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings, AppleVideoAttributes attrs, string outputDir, string appleId)
    {
        var fileTemplate = string.IsNullOrWhiteSpace(settings.MvFileFormat)
            ? settings.Video.TitleTemplate
            : settings.MvFileFormat;

        if (string.IsNullOrWhiteSpace(fileTemplate))
        {
            fileTemplate = "{ArtistName} - {VideoName}";
        }

        var releaseYear = attrs.ReleaseDate.Length >= 4 ? attrs.ReleaseDate[..4] : string.Empty;
        var fileName = fileTemplate
            .Replace("{ArtistName}", attrs.ArtistName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{VideoName}", attrs.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{VideoID}", appleId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{ReleaseDate}", attrs.ReleaseDate ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{ReleaseYear}", releaseYear, StringComparison.OrdinalIgnoreCase)
            .Replace("%artist%", attrs.ArtistName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("%title%", attrs.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        fileName = SanitizeFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = appleId;
        }

        var extension = string.IsNullOrWhiteSpace(settings.Video.Container) ? "mp4" : settings.Video.Container;
        return Path.Join(outputDir, $"{fileName}.{extension}");
    }

    private static Func<double, double, Task>? CreateScaledProgress(Func<double, double, Task>? callback, double start, double end)
    {
        if (callback == null)
        {
            return null;
        }

        var span = end - start;
        return async (progress, speed) =>
        {
            var scaled = start + (progress / 100d) * span;
            await callback(scaled, speed);
        };
    }

    private static Task ReportProgressAsync(Func<double, double, Task>? callback, double progress, double speed = 0)
    {
        if (callback == null)
        {
            return Task.CompletedTask;
        }

        return callback(Math.Clamp(progress, 0, 100), speed);
    }

    private static async Task<string> DownloadTempCoverAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            var ext = Path.GetExtension(url);
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = ".jpg";
            }

            var temp = Path.Join(Path.GetTempPath(), $"apple-cover-{Guid.NewGuid():N}{ext}");
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(temp);
            await stream.CopyToAsync(output, cancellationToken);
            return temp;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static bool IsMatchingAacGroup(string audioGroup, string aacType)
    {
        if (string.IsNullOrWhiteSpace(audioGroup))
        {
            return true;
        }

        var group = audioGroup.Trim().ToLowerInvariant();
        if (IsAtmosAudioGroup(group))
        {
            return false;
        }

        var normalizedType = NormalizeAacType(aacType);
        var isBinauralGroup = group.Contains("binaural", StringComparison.OrdinalIgnoreCase);
        var isDownmixGroup = group.Contains("downmix", StringComparison.OrdinalIgnoreCase);

        return normalizedType switch
        {
            AacBinauralType => isBinauralGroup,
            AacDownmixType => isDownmixGroup,
            _ => !isBinauralGroup && !isDownmixGroup
        };
    }

    private static string NormalizeAacType(string? aacType)
    {
        var normalized = (aacType ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return AacLcType;
        }

        return normalized switch
        {
            "aac" or AacLcType or "aac_lc" or "aac-stereo" or "aac_stereo" => AacLcType,
            AacBinauralType or "aac_binaural" => AacBinauralType,
            AacDownmixType or "aac_downmix" => AacDownmixType,
            _ when normalized.Contains("binaural", StringComparison.OrdinalIgnoreCase) => AacBinauralType,
            _ when normalized.Contains("downmix", StringComparison.OrdinalIgnoreCase) => AacDownmixType,
            _ => AacLcType
        };
    }

    private static bool IsMatchingAtmosGroup(string audioGroup, int maxBitrate)
    {
        if (string.IsNullOrWhiteSpace(audioGroup))
        {
            return true;
        }

        if (maxBitrate <= 0)
        {
            return true;
        }

        var bitrate = ExtractAtmosBitrateKbps(audioGroup);
        return bitrate == 0 || bitrate <= maxBitrate;
    }

    private static bool IsMatchingAlacGroup(string audioGroup, int maxSampleRate)
    {
        if (string.IsNullOrWhiteSpace(audioGroup))
        {
            return true;
        }

        var normalized = audioGroup.Trim().ToLowerInvariant();
        if (IsAtmosAudioGroup(normalized))
        {
            return false;
        }

        var sampleRate = ExtractSampleRate(normalized);
        return sampleRate == 0 || sampleRate <= maxSampleRate;
    }

    private static bool IsAtmosAudioGroup(string normalizedAudioGroup)
    {
        return normalizedAudioGroup.Contains(AtmosKeyword, StringComparison.OrdinalIgnoreCase)
            || normalizedAudioGroup.Contains("joc", StringComparison.OrdinalIgnoreCase)
            || normalizedAudioGroup.Contains("ec-3", StringComparison.OrdinalIgnoreCase)
            || normalizedAudioGroup.Contains("ec3", StringComparison.OrdinalIgnoreCase)
            || normalizedAudioGroup.Contains("ac-3", StringComparison.OrdinalIgnoreCase)
            || normalizedAudioGroup.Contains("ac3", StringComparison.OrdinalIgnoreCase);
    }

    private static int ExtractTrailingNumber(string? value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            value ?? string.Empty,
            "(\\d+)(?!.*\\d)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            RegexTimeout);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : 0;
    }

    private static int ExtractSampleRate(string audioGroup)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            audioGroup ?? string.Empty,
            "-(\\d+)-(\\d+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            RegexTimeout);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var sampleRate))
        {
            return sampleRate;
        }

        return ExtractTrailingNumber(audioGroup);
    }

    private static int ExtractAtmosBitrateKbps(string? audioGroup)
    {
        if (string.IsNullOrWhiteSpace(audioGroup))
        {
            return 0;
        }

        var tokens = audioGroup
            .Split(['-', '_', '/', '.', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static token => token.Trim().ToLowerInvariant())
            .ToArray();

        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (!IsAtmosToken(tokens[index]))
            {
                continue;
            }

            if (!int.TryParse(tokens[index + 1], out var numericToken))
            {
                continue;
            }

            var normalized = NormalizeAtmosBitrateKbps(numericToken);
            if (normalized > 0)
            {
                return normalized;
            }
        }

        return 0;
    }

    private static bool IsAtmosToken(string token)
    {
        return token.Contains(AtmosKeyword, StringComparison.OrdinalIgnoreCase)
            || token is "joc" or "ec3" or "ec-3" or "ac3" or "ac-3";
    }

    private static int NormalizeAtmosBitrateKbps(int rawBitrate)
    {
        var normalized = rawBitrate;
        if (normalized >= 64000)
        {
            normalized /= 1000;
        }

        return normalized is >= 256 and <= 32000 ? normalized : 0;
    }

    private sealed record AppleVideoAttributes
    {
        public string Name { get; init; } = string.Empty;
        public string ArtistName { get; init; } = string.Empty;
        public string AlbumName { get; init; } = string.Empty;
        public string ReleaseDate { get; init; } = string.Empty;
        public string Isrc { get; init; } = string.Empty;
        public string Genre { get; init; } = string.Empty;
        public string ArtworkUrl { get; init; } = string.Empty;
        public string ContentRating { get; init; } = string.Empty;
        public bool Has4K { get; init; }
        public bool HasHdr { get; init; }
    }

    private static string SanitizeFileName(string value)
    {
        return CjkFilenameSanitizer.SanitizeSegment(
            value,
            fallback: string.Empty,
            replacement: "_",
            collapseWhitespace: true,
            trimTrailingDotsAndSpaces: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _ = ex.HResult;
        }
        catch (UnauthorizedAccessException ex)
        {
            _ = ex.HResult;
        }
        catch (ArgumentException ex)
        {
            _ = ex.HResult;
        }
        catch (NotSupportedException ex)
        {
            _ = ex.HResult;
        }
    }

    private async Task<string> GetCachedTextAsync(string cacheKey, TimeSpan ttl, Func<Task<string>> fetch)
    {
        if (_cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var payload = await fetch();
        _cache.Set(cacheKey, payload, ttl);
        return payload;
    }

    private async Task<ExtractedDrmInfo?> TryExtractDrmInfoAsync(string playlistUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistUrl))
        {
            return null;
        }

        var client = _httpClientFactory.CreateClient();
        var playlistText = await GetCachedTextAsync(
            $"apple:hls-playlist:{playlistUrl}",
            TimeSpan.FromMinutes(5),
            () => client.GetStringAsync(playlistUrl, cancellationToken));
        var media = AppleHlsManifestParser.ParseMedia(playlistText, new Uri(playlistUrl));
        if (string.IsNullOrWhiteSpace(media.KeyUri))
        {
            return null;
        }

        var parsedKeyUri = TryParseKeyUri(media.KeyUri);
        if (parsedKeyUri == null)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.Append(media.InitSegment);
        foreach (var segment in media.Segments)
        {
            builder.Append(';');
            builder.Append(segment.Uri);
        }

        return new ExtractedDrmInfo(
            parsedKeyUri.Value.KidBase64,
            parsedKeyUri.Value.PsshBase64,
            parsedKeyUri.Value.UriPrefix,
            media.KeyUri,
            builder.ToString());
    }

    private readonly record struct ExtractedDrmInfo(string? KidBase64, string? PsshBase64, string UriPrefix, string KeyUri, string InitAndSegments);
    private readonly record struct ManifestResolution(string ManifestUrl, string AppleId);
    private readonly record struct VariantSelection(AppleHlsVariantEntry Variant, string StreamGroup);
    private readonly record struct WidevineDownloadFailureHandlers(
        Action<string>? OnHlsFailure = null,
        Action? OnKeyFailure = null,
        Action<string>? OnDecryptFailure = null);
    private readonly record struct WidevineDownloadContext(
        string AdamId,
        string MediaPlaylistUrl,
        string StreamGroup,
        ExtractedDrmInfo DrmInfo,
        string Pssh,
        string TempPrefix,
        string KeyFailureMessage,
        string DecryptFailureMessage,
        string? LicenseEndpointOverride = null,
        WidevineDownloadFailureHandlers FailureHandlers = default);
}
