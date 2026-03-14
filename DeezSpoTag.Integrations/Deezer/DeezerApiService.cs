using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Deezer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static DeezSpoTag.Integrations.Deezer.DeezerGatewayService;

namespace DeezSpoTag.Integrations.Deezer;

/// <summary>
/// Deezer API service using centralized session management
/// </summary>
public sealed class DeezerApiService : IDisposable
{
    private const string ClientProxyNotSetMessage = "Deezer client proxy not set";
    private DeezerSessionManager? _sessionManager;
    private DeezerClient? _clientProxy;
    private bool _disposed;

    public DeezerApiService(ILogger<DeezerApiService> logger)
    {
        _ = logger;
    }

    public void SetSessionManager(DeezerSessionManager sessionManager)
    {
        _sessionManager = sessionManager;
        _clientProxy = new DeezerClient(NullLogger<DeezerClient>.Instance, sessionManager);
    }

    private DeezerClient RequireClientProxy()
    {
        return _clientProxy ?? throw new InvalidOperationException(ClientProxyNotSetMessage);
    }

    // -----===== Tracks =====-----

    public Task<ApiTrack> GetTrack(string trackId) => GetTrackAsync(trackId);

    public Task<ApiTrack> GetTrackAsync(string trackId) => RequireClientProxy().GetTrackAsync(trackId);

    public Task<ApiTrack> GetTrackByIsrcAsync(string isrc) => GetTrackAsync($"isrc:{isrc}");

    // -----===== Albums =====-----

    public Task<ApiAlbum> GetAlbum(string albumId) => GetAlbumAsync(albumId);

    public Task<ApiAlbum> GetAlbumAsync(string albumId) => RequireClientProxy().GetAlbumAsync(albumId);

    public Task<ApiAlbum> GetAlbumByUpcAsync(string upc) => GetAlbumAsync($"upc:{upc}");

    // -----===== Artists =====-----

    public Task<ApiArtist> GetArtistAsync(string artistId) => RequireClientProxy().GetArtistAsync(artistId);

    // -----===== Playlists =====-----

    public Task<ApiPlaylist> GetPlaylist(string playlistId) => GetPlaylistAsync(playlistId);

    public Task<ApiPlaylist> GetPlaylistAsync(string playlistId) => RequireClientProxy().GetPlaylistAsync(playlistId);

    // -----===== Search =====-----

    public Task<DeezerSearchResult> SearchAsync(string query, ApiOptions? options = null)
        => RequireClientProxy().SearchAsync(query, options);

    public Task<DeezerSearchResult> SearchTrackAsync(string query, ApiOptions? options = null)
        => RequireClientProxy().SearchTrackAsync(query, options);

    public Task<DeezerSearchResult> SearchTracksAsync(string query, ApiOptions? options = null)
        => RequireClientProxy().SearchTracksAsync(query, options);

    public Task<List<Track>> SearchTracksAsync(string query, int limit, CancellationToken cancellationToken = default)
        => RequireClientProxy().SearchTracksAsync(query, limit, cancellationToken);

    public Task<DeezerSearchResult> SearchAlbumAsync(string query, ApiOptions? options = null)
        => RequireClientProxy().SearchAlbumAsync(query, options);

    public Task<DeezerSearchResult> SearchArtistAsync(string query, ApiOptions? options = null)
        => RequireClientProxy().SearchArtistAsync(query, options);

    public Task<DeezerSearchResult> SearchPlaylistAsync(string query, ApiOptions? options = null)
        => RequireClientProxy().SearchPlaylistAsync(query, options);

    /// <summary>
    /// Advanced search with metadata - EXACT PORT from deezspotag api.ts get_track_id_from_metadata
    /// </summary>
    public Task<string> GetTrackIdFromMetadataAsync(string artist, string track, string album, int? durationMs = null)
        => RequireClientProxy().GetTrackIdFromMetadataAsync(artist, track, album, durationMs);

    // -----===== Gateway API Methods =====-----

    /// <summary>
    /// Get track with fallback using Gateway API
    /// </summary>
    public async Task<GwTrack?> GetTrackWithFallbackAsync(string trackId)
    {
        return await RequireClientProxy().GetTrackWithFallbackAsync(trackId);
    }

    /// <summary>
    /// Get album page with tracks using Gateway API
    /// </summary>
    public async Task<GwAlbumPageResponse?> GetAlbumPageAsync(string albumId)
    {
        return await RequireClientProxy().GetAlbumPageAsync(albumId);
    }

    /// <summary>
    /// Get album tracks using Gateway API
    /// </summary>
    public async Task<List<GwTrack>?> GetAlbumTracksAsync(string albumId)
    {
        return await RequireClientProxy().GetAlbumTracksAsync(albumId);
    }

    /// <summary>
    /// Get playlist tracks using Gateway API
    /// </summary>
    public async Task<List<GwTrack>?> GetPlaylistTracksAsync(string playlistId)
    {
        return await RequireClientProxy().GetPlaylistTracksAsync(playlistId);
    }

    /// <summary>
    /// Get user data using Gateway API
    /// </summary>
    public Task<DeezerUser?> GetUserDataAsync()
        => Task.FromResult(_sessionManager?.CurrentUser);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

// Response models
public class DeezerSearchResult
{
    public object[]? Data { get; set; }
    public int Total { get; set; }
    public string? Next { get; set; }
}

public class GwPlaylistResponse
{
    public List<GwTrack>? Data { get; set; }
}
