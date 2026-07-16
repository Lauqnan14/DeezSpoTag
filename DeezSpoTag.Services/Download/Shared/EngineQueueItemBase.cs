using System.Text.Json.Serialization;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Core.Models;

namespace DeezSpoTag.Services.Download.Shared;

public abstract class EngineQueueItemBase : MusicKeyAudioFeaturesBase
{
    public string Id { get; set; } = "";
    public string Engine { get; set; } = "";
    public string QueueOrigin { get; set; } = "";
    public string SourceService { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string CollectionName { get; set; } = "";
    public string CollectionType { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string Isrc { get; set; } = "";
    public List<string> Genres { get; set; } = new();
    public string Label { get; set; } = "";
    public string Copyright { get; set; } = "";
    public bool? Explicit { get; set; }
    public string Composer { get; set; } = "";
    public string Url { get; set; } = "";
    public string Barcode { get; set; } = "";
    public string DeezerId { get; set; } = "";
    public string DeezerArtistId { get; set; } = "";
    public string AppleId { get; set; } = "";
    public string AppleArtistId { get; set; } = "";
    public string AppleAlbumId { get; set; } = "";
    public string AppleAlbumName { get; set; } = "";
    public string AppleArtistName { get; set; } = "";
    public string AppleIsrc { get; set; } = "";
    public int? AppleDurationMs { get; set; }
    public string QobuzId { get; set; } = "";
    public string TidalId { get; set; } = "";
    public string AmazonId { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string WatchlistSource { get; set; } = "";
    public string WatchlistPlaylistId { get; set; } = "";
    public string WatchlistTrackId { get; set; } = "";
    public string WatchlistOrigin { get; set; } = "";
    public string WatchlistUnavailableSettingsFingerprint { get; set; } = "";
    public string Cover { get; set; } = "";
    public QueueSourceSettingsSnapshot SourceSettingsSnapshot { get; set; } = new();
    public int AutoIndex { get; set; }
    public string Quality { get; set; } = "";
    public string RequestedQuality { get; set; } = "";
    public string DeliveredQuality { get; set; } = "";
    public LyricsArtifactState LyricsArtifacts { get; set; } = new();
    public List<Dictionary<string, object>> Files { get; set; } = new();
    public int DurationSeconds { get; set; }
    public string ReleaseDate { get; set; } = "";
    public int Position { get; set; }
    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; }
    public int TrackTotal { get; set; }
    public int DiscTotal { get; set; }
    public int SpotifyTrackNumber { get; set; }
    public int SpotifyDiscNumber { get; set; }
    public int SpotifyTotalTracks { get; set; }
    public bool UseAlbumTrackNumber { get; set; }
    public string SpotifyId { get; set; } = "";
    public string SpotifyArtistId { get; set; } = "";
    public string ArtistArtworkProvider { get; set; } = "";
    public string ArtistArtworkSourceUrl { get; set; } = "";
    public string ArtistArtworkResolutionMethod { get; set; } = "";
    public int? ArtistArtworkWidth { get; set; }
    public int? ArtistArtworkHeight { get; set; }
    public bool ArtistArtworkExistingRetained { get; set; }
    public int Size { get; set; } = 1;
    public int Downloaded { get; set; }
    public int Failed { get; set; }
    public double Progress { get; set; }
    public double TotalSize { get; set; }
    public double Speed { get; set; }
    public long StartTime { get; set; }
    public long EndTime { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string ExpectedFinalOutputPath { get; set; } = "";
    public string Profile { get; set; } = "";
    public string QualityBucket { get; set; } = "";
    [JsonPropertyName("finalDestinations")]
    public Dictionary<string, string> FinalDestinations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public long? DestinationFolderId { get; set; }
    public List<DeezSpoTag.Services.Download.Fallback.FallbackPlanStep> FallbackPlan { get; set; } = new();
    public List<DeezSpoTag.Services.Download.Fallback.FallbackAttempt> FallbackHistory { get; set; } = new();
    public string ResolutionStatus { get; set; } = "";
    public DateTimeOffset? ResolvedAtUtc { get; set; }
    public string ResolvedEngine { get; set; } = "";
    public string ResolvedSourceUrl { get; set; } = "";
    public string ResolvedQuality { get; set; } = "";
    public int? ResolvedAutoIndex { get; set; }
    public string ResolutionError { get; set; } = "";

    protected Dictionary<string, object> BuildQueuePayload(string mappedStatus, Dictionary<string, object?>? extra = null)
    {
        var extras = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["autoIndex"] = AutoIndex,
            ["fallbackPlan"] = FallbackPlan,
            ["fallbackHistory"] = FallbackHistory,
            ["sourceSettingsSnapshot"] = SourceSettingsSnapshot,
            ["resolutionStatus"] = ResolutionStatus,
            ["resolvedAtUtc"] = ResolvedAtUtc,
            ["resolvedEngine"] = ResolvedEngine,
            ["resolvedSourceUrl"] = ResolvedSourceUrl,
            ["resolvedQuality"] = ResolvedQuality,
            ["resolvedAutoIndex"] = ResolvedAutoIndex,
            ["resolutionError"] = ResolutionError,
            ["requestedQuality"] = RequestedQuality,
            ["deliveredQuality"] = DeliveredQuality,
            ["appleId"] = AppleId,
            ["appleArtistId"] = AppleArtistId,
            ["deezerArtistId"] = DeezerArtistId,
            ["spotifyArtistId"] = SpotifyArtistId,
            ["appleAlbumId"] = AppleAlbumId,
            ["appleAlbumName"] = AppleAlbumName,
            ["appleArtistName"] = AppleArtistName,
            ["appleIsrc"] = AppleIsrc,
            ["appleDurationMs"] = AppleDurationMs,
            ["qobuzId"] = QobuzId,
            ["tidalId"] = TidalId,
            ["amazonId"] = AmazonId,
            ["expectedFinalOutputPath"] = ExpectedFinalOutputPath,
            ["watchlistUnavailableSettingsFingerprint"] = WatchlistUnavailableSettingsFingerprint,
            ["lyricsArtifacts"] = LyricsArtifacts,
            ["artistArtworkProvider"] = ArtistArtworkProvider,
            ["artistArtworkSourceUrl"] = ArtistArtworkSourceUrl,
            ["artistArtworkResolutionMethod"] = ArtistArtworkResolutionMethod,
            ["artistArtworkWidth"] = ArtistArtworkWidth,
            ["artistArtworkHeight"] = ArtistArtworkHeight,
            ["artistArtworkExistingRetained"] = ArtistArtworkExistingRetained
        };

        if (extra != null)
        {
            foreach (var entry in extra)
            {
                extras[entry.Key] = entry.Value;
            }
        }

        return QueuePayloadBuilder.BuildBasePayload(new QueuePayloadBuilder.QueuePayloadInput
        {
            Id = Id,
            Title = Title,
            Artist = Artist,
            Album = Album,
            Isrc = Isrc,
            Cover = Cover,
            Quality = Quality,
            Size = Size,
            Downloaded = Downloaded,
            Failed = Failed,
            Progress = Progress,
            Status = mappedStatus,
            Engine = Engine,
            ContentType = ContentType,
            Files = Files,
            Profile = Profile,
            FinalDestinations = FinalDestinations,
            DestinationFolderId = DestinationFolderId,
            Extras = extras
        });
    }
}
