namespace DeezSpoTag.Services.Download.Shared;

public static class DownloadAcquisitionStages
{
    public const string ResolvingQuality = "resolving_quality";
    public const string ResolvingProviderSession = "resolving_provider_session";
    public const string RequestingTicket = "requesting_ticket";
    public const string RequestingStreamUrl = "requesting_stream_url";
    public const string OpeningStream = "opening_stream";
    public const string DownloadingAudio = "downloading_audio";
    public const string ValidatingAudio = "validating_audio";
    public const string Finalizing = "finalizing";
}
