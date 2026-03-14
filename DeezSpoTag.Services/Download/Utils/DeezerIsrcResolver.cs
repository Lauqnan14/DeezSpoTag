using DeezSpoTag.Integrations.Deezer;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Utils;

public sealed class DeezerIsrcResolver
{
    private readonly DeezerApiService _deezerApi;
    private readonly ILogger<DeezerIsrcResolver> _logger;

    public DeezerIsrcResolver(DeezerApiService deezerApi, ILogger<DeezerIsrcResolver> logger)
    {
        _deezerApi = deezerApi;
        _logger = logger;
    }

    public async Task<string?> ResolveByTrackIdAsync(string? deezerTrackId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deezerTrackId))
        {
            return null;
        }

        try
        {
            var track = await _deezerApi.GetTrackAsync(deezerTrackId);
            var isrc = track.Isrc;
            return string.IsNullOrWhiteSpace(isrc) ? null : isrc.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve Deezer ISRC for track ID {DeezerTrackId}", deezerTrackId);
            return null;
        }
    }

    public async Task<string?> ResolveByMetadataAsync(
        string? title,
        string? artist,
        string? album,
        int? durationMs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        try
        {
            var trackId = await _deezerApi.GetTrackIdFromMetadataAsync(
                artist.Trim(),
                title.Trim(),
                album?.Trim() ?? string.Empty,
                durationMs);

            if (string.IsNullOrWhiteSpace(trackId) || trackId == "0")
            {
                return null;
            }

            var track = await _deezerApi.GetTrackAsync(trackId);
            var isrc = track.Isrc;
            return string.IsNullOrWhiteSpace(isrc) ? null : isrc.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve Deezer ISRC for metadata {Artist} - {Title}", artist, title);
            return null;
        }
    }
}
