using System.Collections.Generic;

namespace DeezSpoTag.Services.Download.Identity;

public sealed record TrackIdentityResolutionRequest(
    string? SourcePlatform,
    string? SourceUrl,
    string? Title,
    string? Artist,
    string? Album,
    string? Isrc,
    int? DurationMs,
    string? SpotifyId = null,
    string? DeezerId = null,
    string? AppleId = null,
    string? QobuzId = null,
    string? TidalId = null,
    string? AmazonId = null,
    IReadOnlyCollection<string>? TargetPlatforms = null,
    string? Storefront = null,
    string? Language = null,
    string? MediaUserToken = null,
    string? PreferredReleaseType = null);

public sealed record PlatformIdentityCandidate(
    string Platform,
    string? Id,
    string? Url,
    string Source,
    bool Accepted,
    string? Reason = null,
    double Score = 0d);

public sealed record TrackIdentityResolution(
    string? Title,
    string? Artist,
    string? Album,
    string? Isrc,
    int? DurationMs,
    string? SpotifyId,
    string? SpotifyUrl,
    string? DeezerId,
    string? DeezerUrl,
    string? AppleId,
    string? AppleUrl,
    string? AppleAlbumId,
    string? AppleAlbumName,
    string? AppleArtistName,
    string? AppleIsrc,
    int? AppleDurationMs,
    string? QobuzId,
    string? QobuzUrl,
    string? TidalId,
    string? TidalUrl,
    string? AmazonId,
    string? AmazonUrl,
    IReadOnlyList<PlatformIdentityCandidate> Candidates)
{
    public static TrackIdentityResolution Empty(TrackIdentityResolutionRequest request)
        => new(
            request.Title,
            request.Artist,
            request.Album,
            request.Isrc,
            request.DurationMs,
            request.SpotifyId,
            null,
            request.DeezerId,
            null,
            request.AppleId,
            null,
            null,
            request.Album,
            null,
            request.Isrc,
            request.DurationMs,
            request.QobuzId,
            null,
            request.TidalId,
            null,
            request.AmazonId,
            null,
            Array.Empty<PlatformIdentityCandidate>());
}

public interface ITrackIdentityResolver
{
    Task<TrackIdentityResolution> ResolveAsync(
        TrackIdentityResolutionRequest request,
        CancellationToken cancellationToken);
}
