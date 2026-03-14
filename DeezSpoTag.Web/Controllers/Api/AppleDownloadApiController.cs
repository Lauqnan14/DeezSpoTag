using System.Text.Json;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/apple")]
[Authorize]
public sealed class AppleDownloadApiController : ControllerBase
{
    private const string AppleSource = "apple";
    private const string AtmosQuality = "atmos";
    private static readonly bool AppleDisabled = ReadAppleDisabled();
    private readonly ILogger<AppleDownloadApiController> _logger;
    private readonly DownloadIntentService _intentService;
    private readonly DownloadOrchestrationService _orchestrationService;

    public AppleDownloadApiController(
        ILogger<AppleDownloadApiController> logger,
        DownloadIntentService intentService,
        DownloadOrchestrationService orchestrationService)
    {
        _logger = logger;
        _intentService = intentService;
        _orchestrationService = orchestrationService;
    }

    private static bool ReadAppleDisabled()
    {
        var value = Environment.GetEnvironmentVariable("DEEZSPOTAG_APPLE_DISABLED");
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    [HttpPost("download")]
    public async Task<IActionResult> Download([FromBody] JsonElement payload, CancellationToken cancellationToken)
        => await HandleDownloadAsync(payload, videosOnly: false, cancellationToken);

    [HttpPost("videos/download")]
    public async Task<IActionResult> DownloadVideo([FromBody] JsonElement payload, CancellationToken cancellationToken)
        => await HandleDownloadAsync(payload, videosOnly: true, cancellationToken);

    private async Task<IActionResult> HandleDownloadAsync(
        JsonElement payload,
        bool videosOnly,
        CancellationToken cancellationToken)
    {
        var guardResult = await ValidateRequestAsync(payload, cancellationToken);
        if (guardResult != null)
        {
            return guardResult;
        }

        var tracks = ExtractAppleTracks(payload);
        if (tracks.Count == 0)
        {
            return BadRequest(new
            {
                error = videosOnly
                    ? "No Apple Music video URLs supplied."
                    : "No Apple Music URLs supplied."
            });
        }

        List<AppleTrackRequest> videoTracks;
        List<AppleTrackRequest> audioTracks;
        if (videosOnly)
        {
            videoTracks = tracks;
            audioTracks = [];
        }
        else
        {
            videoTracks = tracks.Where(IsVideoTrack).ToList();
            audioTracks = tracks.Except(videoTracks).ToList();
        }

        if (!videosOnly && videoTracks.Count > 0)
        {
            _logger.LogInformation("Apple download guard: rerouting {Count} video item(s) to video pipeline.", videoTracks.Count);
        }

        var destinationFolderId = ExtractDestinationFolderId(payload);
        var secondaryDestinationFolderId = ExtractSecondaryDestinationFolderId(payload);
        var allowQualityUpgrade = ExtractAllowQualityUpgrade(payload);
        var quality = ExtractQuality(payload);
        var aggregateResult = new QueueAggregateResult();
        var enqueueOptions = new AppleQueueOptions(
            destinationFolderId,
            secondaryDestinationFolderId,
            quality,
            allowQualityUpgrade);

        await EnqueueTracksAsync(
            videoTracks,
            enqueueOptions,
            forceVideoContent: true,
            aggregateResult,
            cancellationToken);
        if (audioTracks.Count > 0)
        {
            await EnqueueTracksAsync(
                audioTracks,
                enqueueOptions,
                forceVideoContent: false,
                aggregateResult,
                cancellationToken);
        }

        if (aggregateResult.Queued.Count == 0)
        {
            _logger.LogInformation(
                "Apple {Pipeline} download request queued nothing; skipped {Skipped}",
                videosOnly ? "video" : "audio",
                aggregateResult.Skipped);
            return BadRequest(new
            {
                success = false,
                message = string.IsNullOrWhiteSpace(aggregateResult.LastError) ? "Nothing queued." : aggregateResult.LastError,
                reasonCodes = aggregateResult.ReasonCodes
            });
        }

        return Ok(new
        {
            success = true,
            queued = aggregateResult.Queued,
            skipped = aggregateResult.Skipped,
            reasonCodes = aggregateResult.ReasonCodes
        });
    }

    private async Task<IActionResult?> ValidateRequestAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var downloadGate = await _orchestrationService.EvaluateDownloadGateAsync(cancellationToken);
        if (!downloadGate.Allowed)
        {
            return StatusCode(409, new
            {
                error = string.IsNullOrWhiteSpace(downloadGate.Message)
                    ? "Downloads paused while AutoTag is running."
                    : downloadGate.Message
            });
        }

        if (AppleDisabled)
        {
            return StatusCode(503, new { error = "Apple Music is disabled." });
        }

        if (payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return BadRequest(new { error = "Payload is required." });
        }

        return null;
    }

    private async Task EnqueueTracksAsync(
        IEnumerable<AppleTrackRequest> tracks,
        AppleQueueOptions options,
        bool forceVideoContent,
        QueueAggregateResult aggregate,
        CancellationToken cancellationToken)
    {
        foreach (var track in tracks)
        {
            var intent = BuildDownloadIntent(
                track,
                options.DestinationFolderId,
                options.SecondaryDestinationFolderId,
                options.Quality,
                options.AllowQualityUpgrade,
                forceVideoContent);

            var result = await _intentService.EnqueueAsync(intent, cancellationToken);
            if (result.Success && result.Queued.Count > 0)
            {
                aggregate.Queued.AddRange(result.Queued);
                continue;
            }

            aggregate.Skipped += Math.Max(result.Skipped, 1);
            if (result.SkipReasonCodes.Count > 0)
            {
                aggregate.ReasonCodes.AddRange(result.SkipReasonCodes);
            }

            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                aggregate.LastError = result.Message;
            }
        }
    }

    private static DownloadIntent BuildDownloadIntent(
        AppleTrackRequest track,
        long? destinationFolderId,
        long? secondaryDestinationFolderId,
        string? quality,
        bool allowQualityUpgrade,
        bool forceVideoContent)
    {
        var hasAtmos = forceVideoContent
            ? track.Metadata?.HasAtmos == true || IsAtmosQuality(quality)
            : track.Metadata?.HasAtmos == true;
        var intentQuality = ResolveIntentQuality(quality, hasAtmos, forceVideoContent);
        var intent = new DownloadIntent
        {
            SourceService = AppleSource,
            SourceUrl = track.Url,
            DestinationFolderId = destinationFolderId,
            SecondaryDestinationFolderId = secondaryDestinationFolderId,
            ContentType = forceVideoContent ? DownloadContentTypes.Video : string.Empty,
            Quality = intentQuality,
            HasAtmos = hasAtmos,
            HasAppleDigitalMaster = !forceVideoContent && track.Metadata?.HasAppleDigitalMaster == true,
            AllowQualityUpgrade = allowQualityUpgrade
        };
        ApplyMetadata(intent, track.Metadata);
        return intent;
    }

    private static string ResolveIntentQuality(string? quality, bool hasAtmos, bool forceVideoContent)
    {
        if (hasAtmos)
        {
            return AtmosQuality;
        }

        if (!string.IsNullOrWhiteSpace(quality))
        {
            return quality;
        }

        return forceVideoContent ? DownloadContentTypes.Video : string.Empty;
    }

    private static void ApplyMetadata(DownloadIntent intent, AppleTrackMetadata? metadata)
    {
        if (metadata is null)
        {
            return;
        }

        intent.Title = metadata.Title;
        intent.Artist = metadata.Artist;
        intent.Album = metadata.Album;
        intent.AlbumArtist = metadata.AlbumArtist;
        intent.Isrc = metadata.Isrc;
        intent.Cover = metadata.Cover;
        intent.DurationMs = metadata.DurationMs;
        intent.Position = metadata.Position;
    }

    private static List<AppleTrackRequest> ExtractAppleTracks(JsonElement payload)
    {
        var tracks = new List<AppleTrackRequest>();
        if (payload.TryGetProperty("tracks", out var tracksElement)
            && tracksElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var track in tracksElement.EnumerateArray()
                         .Where(static track => track.TryGetProperty("appleUrl", out var urlElement)
                             && !string.IsNullOrWhiteSpace(urlElement.GetString())))
            {
                var url = track.GetProperty("appleUrl").GetString()!;
                tracks.Add(new AppleTrackRequest
                {
                    Url = url,
                    IsVideo = ExtractMetadataBool(track, "isVideo"),
                    Metadata = ExtractAppleMetadata(track)
                });
            }
        }

        if (tracks.Count == 0 && payload.TryGetProperty("url", out var urlElementRoot))
        {
            var url = urlElementRoot.GetString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                tracks.Add(new AppleTrackRequest { Url = url, IsVideo = IsAppleMusicVideoUrl(url) });
            }
        }

        if (tracks.Count == 0 && payload.TryGetProperty("appleUrl", out var appleUrlElementRoot))
        {
            var url = appleUrlElementRoot.GetString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                tracks.Add(new AppleTrackRequest { Url = url, IsVideo = IsAppleMusicVideoUrl(url) });
            }
        }

        return tracks;
    }

    private static long? ExtractDestinationFolderId(JsonElement payload)
    {
        if (!payload.TryGetProperty("destinationFolderId", out var destinationElement))
        {
            return null;
        }

        if (destinationElement.ValueKind == JsonValueKind.Number
            && destinationElement.TryGetInt64(out var destinationValue))
        {
            return destinationValue;
        }

        if (destinationElement.ValueKind == JsonValueKind.String
            && long.TryParse(destinationElement.GetString(), out var destinationValueFromString))
        {
            return destinationValueFromString;
        }

        return null;
    }

    private static string? ExtractQuality(JsonElement payload)
    {
        if (payload.TryGetProperty("quality", out var qualityElement))
        {
            var quality = qualityElement.GetString();
            return string.IsNullOrWhiteSpace(quality) ? null : quality;
        }

        return null;
    }

    private static bool IsAtmosQuality(string? quality) =>
        !string.IsNullOrWhiteSpace(quality)
        && quality.Contains(AtmosQuality, StringComparison.OrdinalIgnoreCase);

    private static long? ExtractSecondaryDestinationFolderId(JsonElement payload)
    {
        if (TryExtractLong(payload, "secondaryDestinationFolderId", out var secondaryDestinationFolderId))
        {
            return secondaryDestinationFolderId;
        }

        if (TryExtractLong(payload, "atmosDestinationFolderId", out var atmosDestinationFolderId))
        {
            return atmosDestinationFolderId;
        }

        return null;
    }

    private static bool ExtractAllowQualityUpgrade(JsonElement payload)
    {
        if (!payload.TryGetProperty("allowQualityUpgrade", out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var numeric))
        {
            return numeric != 0;
        }

        if (element.ValueKind == JsonValueKind.String
            && bool.TryParse(element.GetString(), out var parsed))
        {
            return parsed;
        }

        return false;
    }

    private static AppleTrackMetadata? ExtractAppleMetadata(JsonElement track)
    {
        if (!track.TryGetProperty("metadata", out var metadataElement)
            || metadataElement.ValueKind != JsonValueKind.Object)
        {
            return ExtractAppleMetadataFromElement(track);
        }

        return ExtractAppleMetadataFromElement(metadataElement);
    }

    private static AppleTrackMetadata? ExtractAppleMetadataFromElement(JsonElement metadataElement)
    {
        var durationMs = ExtractMetadataInt(metadataElement, "durationMs");
        if (durationMs == 0)
        {
            var durationSeconds = ExtractMetadataInt(metadataElement, "duration");
            if (durationSeconds > 0)
            {
                durationMs = durationSeconds * 1000;
            }
        }

        return new AppleTrackMetadata
        {
            Title = ExtractMetadataString(metadataElement, "title"),
            Artist = ExtractMetadataString(metadataElement, "artist"),
            Album = ExtractMetadataString(metadataElement, "album"),
            AlbumArtist = ExtractMetadataString(metadataElement, "albumArtist"),
            Isrc = ExtractMetadataString(metadataElement, "isrc"),
            Cover = ExtractMetadataString(metadataElement, "cover"),
            DurationMs = durationMs,
            Position = ExtractMetadataInt(metadataElement, "position"),
            HasAtmos = ExtractMetadataBool(metadataElement, "hasAtmos"),
            IsVideo = ExtractMetadataBool(metadataElement, "isVideo"),
            CollectionType = ExtractMetadataString(metadataElement, "collectionType"),
            ContentType = ExtractMetadataString(metadataElement, "contentType"),
            HasAppleDigitalMaster = ExtractMetadataBool(metadataElement, "hasAppleDigitalMaster")
                || ExtractMetadataBool(metadataElement, "isAppleDigitalMaster")
                || ExtractMetadataBool(metadataElement, "isMasteredForItunes")
        };
    }

    private static bool IsVideoTrack(AppleTrackRequest track)
    {
        if (track == null)
        {
            return false;
        }

        if (track.IsVideo)
        {
            return true;
        }

        if (track.Metadata?.IsVideo == true)
        {
            return true;
        }

        if (AppleVideoClassifier.IsVideoCollectionType(track.Metadata?.CollectionType))
        {
            return true;
        }

        if (AppleVideoClassifier.IsVideoContentType(track.Metadata?.ContentType))
        {
            return true;
        }

        return IsAppleMusicVideoUrl(track.Url);
    }

    private static bool IsAppleMusicVideoUrl(string? url) => AppleVideoClassifier.IsVideoUrl(url);

    private static string ExtractMetadataString(JsonElement metadataElement, string propertyName)
    {
        if (metadataElement.TryGetProperty(propertyName, out var valueElement))
        {
            return valueElement.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static int ExtractMetadataInt(JsonElement metadataElement, string propertyName)
    {
        if (metadataElement.TryGetProperty(propertyName, out var valueElement)
            && valueElement.ValueKind == JsonValueKind.Number
            && valueElement.TryGetInt32(out var value))
        {
            return value;
        }

        return 0;
    }

    private static bool ExtractMetadataBool(JsonElement metadataElement, string propertyName)
    {
        if (metadataElement.TryGetProperty(propertyName, out var valueElement))
        {
            if (valueElement.ValueKind == JsonValueKind.True)
            {
                return true;
            }
            if (valueElement.ValueKind == JsonValueKind.False)
            {
                return false;
            }
            if (valueElement.ValueKind == JsonValueKind.String
                && bool.TryParse(valueElement.GetString(), out var value))
            {
                return value;
            }
        }

        return false;
    }

    private static bool TryExtractLong(JsonElement payload, string propertyName, out long value)
    {
        if (payload.TryGetProperty(propertyName, out var element))
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value))
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.String
                && long.TryParse(element.GetString(), out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private sealed class AppleTrackRequest
    {
        public string Url { get; set; } = "";
        public bool IsVideo { get; set; }
        public AppleTrackMetadata? Metadata { get; set; }
    }

    private sealed class AppleTrackMetadata
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public string AlbumArtist { get; set; } = "";
        public string Isrc { get; set; } = "";
        public string Cover { get; set; } = "";
        public int DurationMs { get; set; }
        public int Position { get; set; }
        public bool HasAtmos { get; set; }
        public bool HasAppleDigitalMaster { get; set; }
        public bool IsVideo { get; set; }
        public string CollectionType { get; set; } = "";
        public string ContentType { get; set; } = "";
    }

    private sealed class QueueAggregateResult
    {
        public List<string> Queued { get; } = new();
        public List<string> ReasonCodes { get; } = new();
        public int Skipped { get; set; }
        public string? LastError { get; set; }
    }

    private sealed record AppleQueueOptions(
        long? DestinationFolderId,
        long? SecondaryDestinationFolderId,
        string? Quality,
        bool AllowQualityUpgrade);
}
