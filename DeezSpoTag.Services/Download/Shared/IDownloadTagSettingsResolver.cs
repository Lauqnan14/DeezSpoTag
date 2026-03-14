using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Services.Download.Shared;

public sealed record DownloadTagProfileSettings(
    TagSettings? TagSettings,
    string? DownloadTagSource,
    FolderStructureSettings? FolderStructure,
    TechnicalTagSettings? Technical);

public interface IDownloadTagSettingsResolver
{
    Task<TagSettings?> ResolveAsync(long? destinationFolderId, CancellationToken cancellationToken);
    Task<DownloadTagProfileSettings?> ResolveProfileAsync(long? destinationFolderId, CancellationToken cancellationToken);
}

public sealed class NullDownloadTagSettingsResolver : IDownloadTagSettingsResolver
{
    public Task<TagSettings?> ResolveAsync(long? destinationFolderId, CancellationToken cancellationToken)
    {
        return Task.FromResult<TagSettings?>(null);
    }

    public Task<DownloadTagProfileSettings?> ResolveProfileAsync(long? destinationFolderId, CancellationToken cancellationToken)
    {
        return Task.FromResult<DownloadTagProfileSettings?>(null);
    }
}
