namespace DeezSpoTag.Services.Download;

public static class QualityCatalog
{
    public sealed record QualityOption(string Value, string Label);

    public static IReadOnlyDictionary<string, IReadOnlyList<QualityOption>> GetEngineQualityOptions()
    {
        return new Dictionary<string, IReadOnlyList<QualityOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["apple"] = new[]
            {
                new QualityOption("ATMOS", "Apple Music Atmos"),
                new QualityOption("ALAC", "Apple Music ALAC (lossless)"),
                new QualityOption("AAC", "Apple Music AAC")
            },
            ["deezer"] = new[]
            {
                new QualityOption("9", "Deezer FLAC"),
                new QualityOption("3", "Deezer MP3 320kbps"),
                new QualityOption("1", "Deezer MP3 128kbps")
            },
            ["amazon"] = new[]
            {
                new QualityOption("FLAC", "Amazon FLAC")
            },
            ["qobuz"] = new[]
            {
                new QualityOption("27", "Qobuz Hi-Res (24-bit/96kHz+)"),
                new QualityOption("7", "Qobuz FLAC 24-bit"),
                new QualityOption("6", "Qobuz FLAC 16-bit (CD)")
            },
            ["tidal"] = new[]
            {
                new QualityOption("HI_RES_LOSSLESS", "Tidal Hi-Res Lossless (24-bit/48kHz+)"),
                new QualityOption("LOSSLESS", "Tidal Lossless (16-bit/CD)")
            }
        };
    }

    /// <summary>
     /// Centralized quality options used by Settings "Library Folders" desired quality selector.
     /// Values are engine-specific identifiers currently used throughout the download pipeline.
     /// </summary>
    public static IReadOnlyList<QualityOption> GetLibraryFolderQualityOptions()
    {
        // Order MUST match the project's actual multisource fallback order (best → worst),
        // so the UI stays aligned with runtime behavior. Qobuz options remain listed even though
        // the Qobuz engine is now processed independently of the multisource ladder.
        //
        // Source of truth today: DeezSpoTag.Services.Download.DownloadSourceOrder AutoPriority.
        return new[]
        {
            new QualityOption("ATMOS", "Atmos"),
            new QualityOption("VIDEO", "Video"),
            new QualityOption("PODCAST", "Podcast"),
            new QualityOption("27", "Qobuz Hi-Res (24-bit/96kHz+)"),
            new QualityOption("HI_RES_LOSSLESS", "Tidal Hi-Res Lossless (24-bit/48kHz+)"),
            new QualityOption("ALAC", "Apple Music ALAC (lossless)"),
            new QualityOption("7", "Qobuz FLAC 24-bit"),
            new QualityOption("6", "Qobuz FLAC 16-bit (CD)"),
            new QualityOption("LOSSLESS", "Tidal Lossless (16-bit/CD)"),
            new QualityOption("FLAC", "Amazon FLAC"),
            new QualityOption("9", "Deezer FLAC"),
            new QualityOption("AAC", "Apple Music AAC"),
            new QualityOption("3", "Deezer MP3 320kbps"),
            new QualityOption("1", "Deezer MP3 128kbps"),
        };
    }
}
