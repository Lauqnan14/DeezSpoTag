using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Library;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Shared;

public interface IFolderConversionSettingsOverlay
{
    Task ApplyAsync(DeezSpoTagSettings settings, long? destinationFolderId, CancellationToken cancellationToken);
}

public sealed class FolderConversionSettingsOverlay : IFolderConversionSettingsOverlay
{
    private readonly LibraryRepository _libraryRepository;
    private readonly ILogger<FolderConversionSettingsOverlay> _logger;

    public FolderConversionSettingsOverlay(
        LibraryRepository libraryRepository,
        ILogger<FolderConversionSettingsOverlay> logger)
    {
        _libraryRepository = libraryRepository;
        _logger = logger;
    }

    public async Task ApplyAsync(DeezSpoTagSettings settings, long? destinationFolderId, CancellationToken cancellationToken)
    {
        if (settings == null || destinationFolderId is null || destinationFolderId.Value <= 0 || !_libraryRepository.IsConfigured)
        {
            return;
        }

        try
        {
            var folders = await _libraryRepository.GetFoldersAsync(cancellationToken);
            var folder = folders.FirstOrDefault(item => item.Id == destinationFolderId.Value);
            if (folder == null || !folder.ConvertEnabled)
            {
                return;
            }

            var normalizedFormat = NormalizeOptional(folder.ConvertFormat);
            var normalizedBitrate = NormalizeOptional(folder.ConvertBitrate);
            var currentFormat = NormalizeOptional(settings.ConvertFormat) ?? NormalizeOptional(settings.ConvertTo);
            var effectiveFormat = normalizedFormat ?? currentFormat;

            if (!string.IsNullOrWhiteSpace(effectiveFormat))
            {
                settings.ConvertAfterDownload = true;
                settings.ConvertFormat = effectiveFormat;
                settings.ConvertTo = effectiveFormat;
            }

            if (!string.IsNullOrWhiteSpace(normalizedBitrate))
            {
                settings.Bitrate = normalizedBitrate;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to apply folder conversion override for folder {FolderId}", destinationFolderId);            }
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
