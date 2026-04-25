namespace DeezSpoTag.Services.Download.Shared.Utils;

internal static class SongLinkClient
{
    public static async Task<string> ResolvePlatformUrlAsync(
        HttpClient client,
        string spotifyId,
        string platform,
        CancellationToken cancellationToken)
    {
        _ = client;
        _ = cancellationToken;
        await Task.Yield();
        throw new InvalidOperationException(
            $"song.link is deactivated. Native resolver path must be used for platform \"{platform}\" (spotifyId={spotifyId}).");
    }
}
