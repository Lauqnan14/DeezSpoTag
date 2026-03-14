namespace DeezSpoTag.Web.Services;

public sealed record SpotifyBlobResult
{
    public required string BlobPath { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

