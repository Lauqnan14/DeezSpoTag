namespace DeezSpoTag.Services.Download.Identity;

public sealed class NullTrackIdentityResolver : ITrackIdentityResolver
{
    public Task<TrackIdentityResolution> ResolveAsync(
        TrackIdentityResolutionRequest request,
        CancellationToken cancellationToken)
        => Task.FromResult(TrackIdentityResolution.Empty(request));
}
