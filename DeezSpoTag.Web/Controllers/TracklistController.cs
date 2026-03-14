using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Controllers
{
    public class TracklistController : Controller
    {
        private const string DeezerSource = "deezer";
        private const string AppleSource = "apple";
        private const string PlaylistType = "playlist";
        private const string ArtistType = "artist";
        private const string RecommendationsSource = "recommendations";

        public sealed class TracklistIndexRequest
        {
            public string Id { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public string? Source { get; set; }
            public string? ExternalUrl { get; set; }
            public string? LibraryId { get; set; }
            public string? RadioType { get; set; }
            public string? RadioValue { get; set; }
            public string? RecommendationType { get; set; }
            public string? RecommendationValue { get; set; }
            public string? AppleUrl { get; set; }
            public string? AppleId { get; set; }
            public string? AudioVariant { get; set; }
        }

        private sealed record TracklistIndexContext(
            string ResolvedId,
            string EffectiveType,
            string EffectiveSource,
            string EffectiveLibraryId,
            string NormalizedSource,
            string NormalizedType,
            string? NormalizedAudioVariant);

        private readonly ILogger<TracklistController> _logger;
        private readonly DeezSpoTagSettingsService _settingsService;
        private readonly DeezSpoTag.Services.Download.Shared.DeezSpoTagApp _deezSpoTagApp;
        private readonly LibraryRepository _libraryRepository;
        private readonly AppleMusicCatalogService _appleCatalogService;

        public TracklistController(
            ILogger<TracklistController> logger,
            DeezSpoTagSettingsService settingsService,
            DeezSpoTag.Services.Download.Shared.DeezSpoTagApp deezSpoTagApp,
            LibraryRepository libraryRepository,
            AppleMusicCatalogService appleCatalogService)
        {
            _logger = logger;
            _settingsService = settingsService;
            _deezSpoTagApp = deezSpoTagApp;
            _libraryRepository = libraryRepository;
            _appleCatalogService = appleCatalogService;
        }

        public async Task<IActionResult> Index(
            [FromQuery] TracklistIndexRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!ModelState.IsValid)
            {
                return View();
            }

            var (effectiveSource, effectiveType, effectiveLibraryId) = ApplyRecommendationRouting(request);
            effectiveSource = await InferPlaylistSourceAsync(
                request.Id,
                effectiveType,
                effectiveSource,
                cancellationToken);

            var context = BuildTracklistIndexContext(
                request,
                effectiveSource,
                effectiveType,
                effectiveLibraryId);

            var appleResult = await TryHandleAppleTrackLocalRedirectAsync(context, request, cancellationToken);
            if (appleResult != null)
            {
                return appleResult;
            }

            if (context.NormalizedSource == AppleSource
                && context.NormalizedType == ArtistType)
            {
                return RedirectToAction("Index", "Artist", new { id = context.ResolvedId, source = AppleSource });
            }

            PopulateViewData(context, request);
            return View();
        }

        private static (string? Source, string Type, string? LibraryId) ApplyRecommendationRouting(TracklistIndexRequest request)
        {
            var effectiveSource = request.Source;
            var effectiveType = request.Type;
            var effectiveLibraryId = request.LibraryId;

            if (TryParseRecommendationStationId(request.Id, out var stationLibraryId, out _))
            {
                if (string.IsNullOrWhiteSpace(effectiveSource)
                    || string.Equals(effectiveSource, DeezerSource, StringComparison.OrdinalIgnoreCase))
                {
                    effectiveSource = RecommendationsSource;
                }

                if (string.IsNullOrWhiteSpace(effectiveType)
                    || string.Equals(effectiveType, PlaylistType, StringComparison.OrdinalIgnoreCase))
                {
                    effectiveType = "recommendation";
                }

                if (string.IsNullOrWhiteSpace(effectiveLibraryId))
                {
                    effectiveLibraryId = stationLibraryId.ToString();
                }
            }

            if (string.Equals(effectiveSource, RecommendationsSource, StringComparison.OrdinalIgnoreCase)
                && string.Equals(effectiveType, PlaylistType, StringComparison.OrdinalIgnoreCase))
            {
                effectiveType = "recommendation";
            }

            return (effectiveSource, effectiveType, effectiveLibraryId);
        }

        private async Task<string?> InferPlaylistSourceAsync(
            string id,
            string effectiveType,
            string? effectiveSource,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(id)
                || !_libraryRepository.IsConfigured
                || !string.Equals(effectiveType.Trim(), PlaylistType, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(effectiveSource)
                    && !string.Equals(effectiveSource, DeezerSource, StringComparison.OrdinalIgnoreCase)))
            {
                return effectiveSource;
            }

            try
            {
                var playlistId = id.Trim();
                var boomplayWatchlisted = await _libraryRepository.IsPlaylistWatchlistedAsync(
                    "boomplay",
                    playlistId,
                    cancellationToken);
                return boomplayWatchlisted ? "boomplay" : effectiveSource;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Tracklist source inference failed for playlist id PlaylistId");
                return effectiveSource;
            }
        }

        private static TracklistIndexContext BuildTracklistIndexContext(
            TracklistIndexRequest request,
            string? effectiveSource,
            string effectiveType,
            string? effectiveLibraryId)
        {
            var normalizedSource = (effectiveSource ?? DeezerSource).Trim().ToLowerInvariant();
            var normalizedType = (effectiveType ?? string.Empty).Trim().ToLowerInvariant();
            var normalizedAudioVariant = NormalizeAudioVariant(request.AudioVariant);
            var resolvedId = ResolveRequestedId(
                request.Id,
                request.AppleId,
                request.AppleUrl,
                normalizedSource,
                normalizedType);

            return new TracklistIndexContext(
                resolvedId,
                effectiveType ?? string.Empty,
                effectiveSource ?? DeezerSource,
                effectiveLibraryId ?? string.Empty,
                normalizedSource,
                normalizedType,
                normalizedAudioVariant);
        }

        private async Task<IActionResult?> TryHandleAppleTrackLocalRedirectAsync(
            TracklistIndexContext context,
            TracklistIndexRequest request,
            CancellationToken cancellationToken)
        {
            if (context.NormalizedSource != AppleSource
                || context.NormalizedType != "track"
                || string.IsNullOrWhiteSpace(context.ResolvedId)
                || !_libraryRepository.IsConfigured)
            {
                return null;
            }

            try
            {
                if (await ShouldRenderVariantSpecificViewAsync(context, cancellationToken))
                {
                    PopulateViewData(context, request);
                    return View();
                }

                var localAlbumId = await ResolveLocalAlbumRedirectTargetAsync(
                    context.ResolvedId,
                    request.AppleUrl,
                    cancellationToken);
                if (localAlbumId.HasValue && localAlbumId.Value > 0)
                {
                    return RedirectToAction("Album", "Library", new { id = localAlbumId.Value });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Apple track local redirect lookup failed for source id AppleTrackId");
            }

            return null;
        }

        private async Task<bool> ShouldRenderVariantSpecificViewAsync(
            TracklistIndexContext context,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(context.NormalizedAudioVariant))
            {
                return false;
            }

            var variantExists = await _libraryRepository.ExistsTrackSourceAsync(
                AppleSource,
                context.ResolvedId,
                audioVariant: context.NormalizedAudioVariant,
                cancellationToken: cancellationToken);

            return !variantExists;
        }

        private async Task<long?> ResolveLocalAlbumRedirectTargetAsync(
            string appleTrackId,
            string? appleUrl,
            CancellationToken cancellationToken)
        {
            var localAlbumId = await _libraryRepository.GetLocalAlbumIdByTrackSourceIdAsync(
                AppleSource,
                appleTrackId,
                cancellationToken);
            if (!localAlbumId.HasValue)
            {
                localAlbumId = await _libraryRepository.GetLocalAlbumIdByAlbumSourceIdAsync(
                    AppleSource,
                    appleTrackId,
                    cancellationToken);
            }

            if (!localAlbumId.HasValue)
            {
                localAlbumId = await TryResolveLocalAlbumIdByAppleMetadataAsync(
                    appleTrackId,
                    appleUrl,
                    cancellationToken);
            }

            return localAlbumId;
        }

        private void PopulateViewData(TracklistIndexContext context, TracklistIndexRequest request)
        {
            ViewData["Id"] = context.ResolvedId;
            ViewData["Type"] = context.EffectiveType;
            ViewData["Source"] = context.EffectiveSource;
            ViewData["LibraryId"] = context.EffectiveLibraryId;
            ViewData["RadioType"] = request.RadioType ?? "";
            ViewData["RadioValue"] = request.RadioValue ?? "";
            ViewData["RecommendationType"] = request.RecommendationType ?? "";
            ViewData["RecommendationValue"] = request.RecommendationValue ?? "";
            ViewData["AppleUrl"] = request.AppleUrl ?? "";
            ViewData["AppleId"] = request.AppleId ?? "";
            ViewData["ExternalUrl"] = request.ExternalUrl ?? "";
            ViewData["AudioVariant"] = context.NormalizedAudioVariant ?? "";
        }

        private static string? NormalizeAudioVariant(string? audioVariant)
        {
            var normalized = (audioVariant ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "atmos" => "atmos",
                "stereo" => "stereo",
                _ => null
            };
        }

        private static string ResolveRequestedId(
            string id,
            string? appleId,
            string? appleUrl,
            string normalizedSource,
            string normalizedType)
        {
            if (normalizedSource == AppleSource && normalizedType == "track")
            {
                var fromUrl = TryExtractAppleTrackIdFromUrl(appleUrl);
                if (!string.IsNullOrWhiteSpace(fromUrl))
                {
                    return fromUrl;
                }
            }

            return string.IsNullOrWhiteSpace(id) ? (appleId ?? string.Empty) : id;
        }

        private static string? TryExtractAppleTrackIdFromUrl(string? appleUrl)
        {
            if (string.IsNullOrWhiteSpace(appleUrl))
            {
                return null;
            }

            try
            {
                var uri = new Uri(appleUrl);
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var iParam = query.Get("i");
                if (!string.IsNullOrWhiteSpace(iParam) && long.TryParse(iParam, out _))
                {
                    return iParam;
                }

                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (var i = segments.Length - 1; i >= 0; i--)
                {
                    if (long.TryParse(segments[i], out _))
                    {
                        return segments[i];
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                // ignore parse errors; caller falls back to other sources
            }

            return null;
        }

        private static bool TryParseRecommendationStationId(string? stationId, out long libraryId, out long folderId)
        {
            libraryId = 0;
            folderId = 0;
            if (string.IsNullOrWhiteSpace(stationId))
            {
                return false;
            }

            var value = stationId.Trim();
            if (!value.StartsWith("daily-rotation:l", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
            {
                return false;
            }

            var libraryPart = parts[1];
            var folderPart = parts[2];
            if (!libraryPart.StartsWith("l", StringComparison.OrdinalIgnoreCase)
                || !folderPart.StartsWith("f", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return long.TryParse(libraryPart[1..], out libraryId)
                && libraryId > 0
                && long.TryParse(folderPart[1..], out folderId)
                && folderId > 0;
        }

        private async Task<long?> TryResolveLocalAlbumIdByAppleMetadataAsync(
            string appleTrackId,
            string? appleUrl,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(appleTrackId))
            {
                return null;
            }

            try
            {
                var settings = _settingsService.LoadSettings();
                var resolvedStorefront = await _appleCatalogService.ResolveStorefrontAsync(
                    settings.AppleMusic?.Storefront,
                    settings.AppleMusic?.MediaUserToken,
                    cancellationToken);
                var storefrontCandidates = BuildStorefrontCandidates(
                    resolvedStorefront,
                    settings.AppleMusic?.Storefront,
                    TryExtractAppleStorefrontFromUrl(appleUrl));

                foreach (var storefront in storefrontCandidates)
                {
                    try
                    {
                        using var songDoc = await _appleCatalogService.GetSongAsync(
                            appleTrackId,
                            storefront,
                            "en-US",
                            cancellationToken,
                            settings.AppleMusic?.MediaUserToken);

                        if (!TryReadAppleSongMetadata(songDoc.RootElement, out var artistName, out var trackTitle, out var durationMs))
                        {
                            continue;
                        }

                        var localAlbumId = await _libraryRepository.GetLocalAlbumIdByTrackMetadataAsync(
                            artistName!,
                            trackTitle!,
                            durationMs,
                            cancellationToken);
                        if (localAlbumId.HasValue && localAlbumId.Value > 0)
                        {
                            return localAlbumId;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "Apple track metadata fallback storefront lookup failed for AppleTrackId (Storefront)");
                    }
                }

                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Apple track metadata redirect fallback lookup failed for AppleTrackId");
                return null;
            }
        }

        private static List<string> BuildStorefrontCandidates(
            string? resolvedStorefront,
            string? configuredStorefront,
            string? storefrontFromUrl)
        {
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string? candidate)
            {
                var normalized = NormalizeStorefront(candidate);
                if (normalized is null)
                {
                    return;
                }

                if (seen.Add(normalized))
                {
                    results.Add(normalized);
                }
            }

            AddCandidate(resolvedStorefront);
            AddCandidate(storefrontFromUrl);
            AddCandidate(configuredStorefront);
            AddCandidate("us");

            return results;
        }

        private static string? NormalizeStorefront(string? value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return null;
            }

            if (!trimmed.All(char.IsLetterOrDigit))
            {
                return null;
            }

            return trimmed.ToLowerInvariant();
        }

        private static string? TryExtractAppleStorefrontFromUrl(string? appleUrl)
        {
            if (string.IsNullOrWhiteSpace(appleUrl))
            {
                return null;
            }

            try
            {
                var uri = new Uri(appleUrl);
                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                {
                    return null;
                }

                var firstSegment = segments[0];
                return NormalizeStorefront(firstSegment);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                return null;
            }
        }

        private static bool TryReadAppleSongMetadata(
            JsonElement root,
            out string? artistName,
            out string? trackTitle,
            out int? durationMs)
        {
            artistName = null;
            trackTitle = null;
            durationMs = null;

            if (!root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0)
            {
                return false;
            }

            var song = data[0];
            if (!song.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (attributes.TryGetProperty("artistName", out var artistEl) && artistEl.ValueKind == JsonValueKind.String)
            {
                artistName = artistEl.GetString();
            }

            if (attributes.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                trackTitle = nameEl.GetString();
            }

            if (attributes.TryGetProperty("durationInMillis", out var durationEl) && durationEl.ValueKind == JsonValueKind.Number)
            {
                if (durationEl.TryGetInt32(out var value))
                {
                    durationMs = value;
                }
                else if (durationEl.TryGetInt64(out var value64) && value64 <= int.MaxValue && value64 >= int.MinValue)
                {
                    durationMs = (int)value64;
                }
            }

            return !string.IsNullOrWhiteSpace(artistName) && !string.IsNullOrWhiteSpace(trackTitle);
        }

        /// <summary>
        /// Download album/playlist action (FIXED: Now properly uses settings and download queue)
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Download(string id, string type, int bitrate = 0)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            try
            {
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type))
                {
                    return BadRequest("ID and type are required.");
                }

                var settings = _settingsService.LoadSettings();
                var resolvedBitrate = DownloadSourceOrder.ResolveDeezerBitrate(settings, bitrate);
                var url = type.ToLowerInvariant() switch
                {
                    "track" => $"https://www.deezer.com/track/{id}",
                    "album" => $"https://www.deezer.com/album/{id}",
                    "playlist" => $"https://www.deezer.com/playlist/{id}",
                    ArtistType => $"https://www.deezer.com/artist/{id}",
                    _ => string.Empty
                };

                if (string.IsNullOrWhiteSpace(url))
                {
                    return BadRequest("Unsupported Deezer type.");
                }

                var queued = await _deezSpoTagApp.AddToQueueAsync(new[] { url }, resolvedBitrate);
                if (queued.Count == 0)
                {
                    return Json(new { success = false, message = "Nothing queued." });
                }

                return Json(new { success = true, queued = queued });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error initiating Deezer download for Type:Id");
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Download selected tracks action (FIXED: Now properly uses settings and download queue)
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> DownloadTracks([FromBody] DownloadTracksRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            try
            {
                if (request == null || request.TrackIds.Count == 0)
                {
                    return BadRequest("Track IDs are required.");
                }

                var settings = _settingsService.LoadSettings();
                var resolvedBitrate = DownloadSourceOrder.ResolveDeezerBitrate(settings, request.Bitrate);
                var urls = request.TrackIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => $"https://www.deezer.com/track/{id}")
                    .ToArray();

                var queued = await _deezSpoTagApp.AddToQueueAsync(urls, resolvedBitrate);
                if (queued.Count == 0)
                {
                    return Json(new { success = false, message = "Nothing queued." });
                }

                return Json(new { success = true, queued = queued });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error initiating Deezer track downloads");
                return Json(new { success = false, message = ex.Message });
            }
        }

    }

    public class DownloadTracksRequest
    {
        public List<string> TrackIds { get; set; } = new();
        public int Bitrate { get; set; } = 0;
    }
}
