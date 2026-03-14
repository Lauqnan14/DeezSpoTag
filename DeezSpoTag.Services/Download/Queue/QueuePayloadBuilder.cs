namespace DeezSpoTag.Services.Download.Queue;

internal static class QueuePayloadBuilder
{
    private const string DefaultCoverPath = "/images/default-cover.png";
    private const string InQueueStatus = "inQueue";
    private const string DownloadingStatus = "downloading";
    private const string CompletedStatus = "completed";
    private const string FailedStatus = "failed";
    private const string CancelledStatus = "cancelled";

    public sealed class QueuePayloadInput
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required string Artist { get; init; }
        public required string Album { get; init; }
        public required string Isrc { get; init; }
        public required string Cover { get; init; }
        public required string Quality { get; init; }
        public required int Size { get; init; }
        public required int Downloaded { get; init; }
        public required int Failed { get; init; }
        public required double Progress { get; init; }
        public required string Status { get; init; }
        public required string Engine { get; init; }
        public required string ContentType { get; init; }
        public required List<Dictionary<string, object>> Files { get; init; }
        public required string LyricsStatus { get; init; }
        public required string Profile { get; init; }
        public required Dictionary<string, string> FinalDestinations { get; init; }
        public required long? DestinationFolderId { get; init; }
        public IReadOnlyDictionary<string, object?>? Extras { get; init; }
    }

    public static Dictionary<string, object> BuildBasePayload(QueuePayloadInput input)
    {
        var payload = new Dictionary<string, object>
        {
            ["uuid"] = input.Id,
            ["title"] = input.Title,
            ["artist"] = input.Artist,
            ["album"] = input.Album,
            ["isrc"] = input.Isrc,
            ["cover"] = string.IsNullOrWhiteSpace(input.Cover) ? DefaultCoverPath : input.Cover,
            ["quality"] = input.Quality,
            ["size"] = input.Size,
            ["downloaded"] = input.Downloaded,
            ["failed"] = input.Failed,
            ["progress"] = input.Progress,
            ["status"] = input.Status,
            ["engine"] = input.Engine,
            ["contentType"] = input.ContentType,
            ["files"] = input.Files,
            ["lyrics_status"] = input.LyricsStatus,
            ["profile"] = input.Profile,
            ["finalDestinations"] = input.FinalDestinations,
            ["destinationFolderId"] = input.DestinationFolderId!
        };

        if (input.Extras == null)
        {
            return payload;
        }

        foreach (var extra in input.Extras)
        {
            payload[extra.Key] = extra.Value!;
        }

        return payload;
    }

    public static string MapStatusForUi(string statusName)
    {
        return statusName switch
        {
            "Queued" => InQueueStatus,
            "Downloading" => DownloadingStatus,
            "Completed" => CompletedStatus,
            "Failed" => FailedStatus,
            "Skipped" => CancelledStatus,
            _ => InQueueStatus
        };
    }
}
