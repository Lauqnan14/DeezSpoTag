using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;

namespace DeezSpoTag.Services.Download.Apple;

public static class AppleRequestBuilder
{
    public static AppleDownloadRequest BuildRequest(AppleQueueItem item, DeezSpoTagSettings settings, Func<double, double, Task>? progressCallback = null)
    {
        var preferredProfile = string.IsNullOrWhiteSpace(item.Quality)
            ? settings.AppleMusic.PreferredAudioProfile
            : item.Quality;

        var isVideo = AppleVideoClassifier.IsVideo(item.SourceUrl, item.CollectionType, item.ContentType);

        // Mark explicit per-item Atmos requests as strict so the downloader does not silently pick stereo.
        var videoAudioType = !string.IsNullOrWhiteSpace(item.Quality)
            && item.Quality.Contains("atmos", StringComparison.OrdinalIgnoreCase)
            ? "atmos-strict"
            : settings.Video.AppleMusicVideoAudioType;

        var request = RequestBuilderCommon.CreateCommonRequest<AppleDownloadRequest>(item, settings);
        request.IsVideo = isVideo;
        request.AppleId = item.AppleId;
        request.Storefront = settings.AppleMusic.Storefront;
        request.MediaUserToken = settings.AppleMusic.MediaUserToken;
        request.AuthorizationToken = string.IsNullOrWhiteSpace(settings.AppleMusic.AuthorizationToken)
            ? settings.AuthorizationToken
            : settings.AppleMusic.AuthorizationToken;
        request.PreferredProfile = preferredProfile;
        request.GetM3u8Mode = settings.AppleMusic.GetM3u8Mode;
        request.AacType = settings.AppleMusic.AacType;
        request.AlacMax = settings.AppleMusic.AlacMax;
        request.AtmosMax = settings.AppleMusic.AtmosMax;
        request.DecryptM3u8Port = settings.AppleMusic.DecryptM3u8Port;
        request.GetM3u8Port = settings.AppleMusic.GetM3u8Port;
        request.GetM3u8FromDevice = settings.AppleMusic.GetM3u8FromDevice;
        request.VideoAudioType = videoAudioType;
        request.VideoMaxResolution = settings.Video.AppleMusicVideoMaxResolution;
        request.VideoCodecPreference = settings.Video.AppleMusicVideoCodecPreference;
        request.ProgressCallback = progressCallback;
        request.ServiceUrl = item.SourceUrl ?? string.Empty;
        return request;
    }
}

public sealed class AppleDownloadRequest : EngineDownloadRequestBase
{
    public bool IsVideo { get; set; }
    public string AppleId { get; set; } = "";
    public string Storefront { get; set; } = "us";
    public string MediaUserToken { get; set; } = "";
    public string AuthorizationToken { get; set; } = "";
    public string PreferredProfile { get; set; } = "atmos";
    public string GetM3u8Mode { get; set; } = "hires";
    public string AacType { get; set; } = "aac-lc";
    public int AlacMax { get; set; } = 192000;
    public int AtmosMax { get; set; } = 2768;
    public string DecryptM3u8Port { get; set; } = "127.0.0.1:10020";
    public string GetM3u8Port { get; set; } = "127.0.0.1:20020";
    public bool GetM3u8FromDevice { get; set; } = true;
    public string VideoAudioType { get; set; } = "atmos";
    public int VideoMaxResolution { get; set; } = 2160;
    public string VideoCodecPreference { get; set; } = "prefer-hevc";
    public string VideoOutputRoot { get; set; } = "";
    public Func<double, double, Task>? ProgressCallback { get; set; }
}
