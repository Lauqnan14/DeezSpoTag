namespace DeezSpoTag.Services.Download.Amazon;

public interface IAmazonDownloadService
{
    Task<bool> HasPublicDownloadSessionAsync(CancellationToken cancellationToken);
    Task<string?> BeginPublicDownloadVerificationAsync(CancellationToken cancellationToken);
    Task CompletePublicDownloadVerificationAsync(string grant, CancellationToken cancellationToken);

    Task<string> DownloadAsync(
        AmazonDownloadRequest request,
        bool embedMaxQualityCover,
        DeezSpoTag.Core.Models.Settings.TagSettings? tagSettings,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken);
}
