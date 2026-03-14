using System.Collections.Generic;

namespace DeezSpoTag.Services.Download.Shared.Models;

/// <summary>
/// Result of track download operation
/// Matches deezspotag download result structure
/// </summary>
public class TrackDownloadResult
{
    /// <summary>
    /// Full path to downloaded file
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Relative filename from extras path
    /// </summary>
    public string Filename { get; set; } = "";

    /// <summary>
    /// Track metadata used for download
    /// </summary>
    public Dictionary<string, object> ItemData { get; set; } = new();

    /// <summary>
    /// Whether track was found via search fallback
    /// </summary>
    public bool Searched { get; set; } = false;

    /// <summary>
    /// Whether file already existed and was not downloaded
    /// </summary>
    public bool AlreadyDownloaded { get; set; } = false;

    /// <summary>
    /// Error information if download failed
    /// </summary>
    public TrackDownloadError? Error { get; set; }

    /// <summary>
    /// Whether download resulted in error
    /// </summary>
    public bool HasError => Error != null;

    // Artwork-related properties (for Phase 2 integration)
    
    /// <summary>
    /// Path where album artwork should be saved
    /// </summary>
    public string? AlbumPath { get; set; }

    /// <summary>
    /// Filename for album artwork (without extension)
    /// </summary>
    public string? AlbumFilename { get; set; }

    /// <summary>
    /// URLs for album artwork in different formats
    /// </summary>
    public List<ArtworkUrl>? AlbumURLs { get; set; }

    /// <summary>
    /// Path where artist artwork should be saved
    /// </summary>
    public string? ArtistPath { get; set; }

    /// <summary>
    /// Filename for artist artwork (without extension)
    /// </summary>
    public string? ArtistFilename { get; set; }

    /// <summary>
    /// URLs for artist artwork in different formats
    /// </summary>
    public List<ArtworkUrl>? ArtistURLs { get; set; }
}

/// <summary>
/// Error information for failed track download
/// </summary>
public class TrackDownloadError
{
    /// <summary>
    /// Error message
    /// </summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// Stack trace
    /// </summary>
    public string? Stack { get; set; }

    /// <summary>
    /// Error type (track, post, etc.)
    /// </summary>
    public string Type { get; set; } = "track";

    /// <summary>
    /// Additional error data
    /// </summary>
    public Dictionary<string, object> Data { get; set; } = new();
}

/// <summary>
/// Artwork URL with format information
/// </summary>
public class ArtworkUrl
{
    /// <summary>
    /// URL to artwork image
    /// </summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// File extension (jpg, png, etc.)
    /// </summary>
    public string Ext { get; set; } = "";
}

/// <summary>
/// Path generation result from PathTemplateProcessor
/// </summary>
public class PathGenerationResult
{
    /// <summary>
    /// Directory where file should be saved
    /// </summary>
    public string FilePath { get; set; } = "";

    /// <summary>
    /// Filename without extension
    /// </summary>
    public string Filename { get; set; } = "";

    /// <summary>
    /// Base extras path for relative filename calculation
    /// </summary>
    public string ExtrasPath { get; set; } = "";

    /// <summary>
    /// Full write path (FilePath + Filename + extension)
    /// </summary>
    public string WritePath { get; set; } = "";

    /// <summary>
    /// Path for album artwork (if applicable)
    /// </summary>
    public string? CoverPath { get; set; }

    /// <summary>
    /// Path for artist artwork (if applicable)
    /// </summary>
    public string? ArtistPath { get; set; }
}