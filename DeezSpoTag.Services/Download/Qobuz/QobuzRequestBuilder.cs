using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Services.Download.Qobuz;

public static class QobuzRequestBuilder
{
    public static QobuzDownloadRequest BuildRequest(QobuzQueueItem item, DeezSpoTagSettings settings)
    {
        var request = RequestBuilderCommon.CreateCommonRequest<QobuzDownloadRequest>(item, settings);
        request.Quality = RequestBuilderCommon.ResolvePreferredQuality(
            item.Quality,
            settings.QobuzQuality,
            "6");
        if (string.IsNullOrWhiteSpace(request.TrackUrl) && !string.IsNullOrWhiteSpace(request.QobuzId))
        {
            request.TrackUrl = $"https://play.qobuz.com/track/{Uri.EscapeDataString(request.QobuzId)}";
        }

        request.AllowQualityFallback = settings.FallbackBitrate
            && !string.Equals(settings.Service, "auto", StringComparison.OrdinalIgnoreCase);
        return request;
    }
}

public sealed class QobuzDownloadRequest : EngineDownloadRequestBase
{
    public string Quality { get; set; } = "";
    public string? TrackUrl { get; set; }
    public bool EmbedMaxQualityCover { get; set; }
    public bool AllowQualityFallback { get; set; } = true;
    public Func<string, Task>? SelectedQualityCallback { get; set; }
    public TagSettings? TagSettings { get; set; }
    public Func<double, double, Task>? ProgressCallback { get; set; }
}
