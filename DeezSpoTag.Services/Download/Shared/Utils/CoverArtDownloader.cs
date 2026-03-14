namespace DeezSpoTag.Services.Download.Shared.Utils;

internal static class CoverArtDownloader
{
    public static async Task<byte[]?> TryDownloadAsync(
        HttpClient client,
        string coverUrl,
        bool embedMaxQualityCover,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            return null;
        }

        var url = embedMaxQualityCover ? coverUrl.Replace("cover", "cover") : coverUrl;
        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }
}
