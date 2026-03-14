namespace DeezSpoTag.Services.Download.Amazon;

public interface IAmazonDownloadService
{
    Task<string> DownloadAsync(
        AmazonDownloadRequest request,
        bool embedMaxQualityCover,
        DeezSpoTag.Core.Models.Settings.TagSettings? tagSettings,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken);
}
