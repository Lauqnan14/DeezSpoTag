using DeezSpoTag.Services.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Text;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/lrc")]
[ApiController]
[Authorize]
public sealed class LrcEditorApiController : ControllerBase
{
    private const string LibraryDbNotConfiguredMessage = "Library DB not configured.";
    private const string PathOutsideLibraryRootsMessage = "Path is outside library roots.";
    private readonly LibraryRepository _repository;
    private readonly ILogger<LrcEditorApiController> _logger;
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".m4b", ".aac", ".wav", ".ogg", ".opus"
    };

    public LrcEditorApiController(LibraryRepository repository, ILogger<LrcEditorApiController> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string term, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return Ok(Array.Empty<object>());
        }

        if (!_repository.IsConfigured)
        {
            return BadRequest(new { error = LibraryDbNotConfiguredMessage });
        }

        var like = $"%{term.Trim().Replace("%", "\\%").Replace("_", "\\_")}%";
        var results = await _repository.SearchTracksWithIdsAsync(like, cancellationToken);
        return Ok(results.Select(track => new
        {
            trackId = track.TrackId,
            title = track.Title,
            artist = track.ArtistName,
            album = track.AlbumTitle,
            durationMs = track.DurationMs,
            coverUrl = track.CoverPath is null
                ? null
                : $"/api/library/image?path={Uri.EscapeDataString(track.CoverPath)}&size=240"
        }));
    }

    [HttpGet("browse")]
    public async Task<IActionResult> Browse([FromQuery] string? path, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return BadRequest(new { error = LibraryDbNotConfiguredMessage });
        }

        var roots = (await _repository.GetFoldersAsync(cancellationToken))
            .Where(folder => folder.Enabled)
            .Select(folder => folder.RootPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (roots.Count == 0)
        {
            return Ok(new { path = string.Empty, entries = Array.Empty<object>() });
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            var rootEntries = roots
                .Select(root => new
                {
                    name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    path = root,
                    type = "folder"
                })
                .OrderBy(entry => entry.name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Ok(new { path = string.Empty, entries = rootEntries });
        }

        var normalized = NormalizePath(path);
        if (normalized == null || !IsUnderAnyRoot(normalized, roots))
        {
            return BadRequest(new { error = PathOutsideLibraryRootsMessage });
        }

        if (!Directory.Exists(normalized))
        {
            return NotFound(new { error = "Folder not found." });
        }

        var entries = new List<BrowserEntry>();
        foreach (var directory in Directory.EnumerateDirectories(normalized))
        {
            var name = Path.GetFileName(directory);
            entries.Add(new BrowserEntry(name, directory, "folder"));
        }

        foreach (var file in Directory.EnumerateFiles(normalized))
        {
            var extension = Path.GetExtension(file);
            if (!AudioExtensions.Contains(extension) && !string.Equals(extension, ".lrc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            entries.Add(new BrowserEntry(Path.GetFileName(file), file, "file"));
        }

        var sorted = entries
            .OrderBy(entry => entry.Type == "folder" ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new { path = normalized, entries = sorted });
    }

    [HttpGet("file/info")]
    public async Task<IActionResult> GetFileInfo([FromQuery] string path, CancellationToken cancellationToken)
    {
        var (normalized, extension, errorResult) = await ValidateAudioPathAsync(path, "File not found.", cancellationToken);
        if (errorResult != null)
        {
            return errorResult;
        }

        var normalizedPath = normalized!;

        return Ok(new
        {
            filePath = normalizedPath,
            fileName = Path.GetFileNameWithoutExtension(normalizedPath),
            directory = Path.GetDirectoryName(normalizedPath) ?? string.Empty,
            fileType = extension.TrimStart('.').ToUpperInvariant(),
            audioUrl = $"/api/lrc/file/audio?path={Uri.EscapeDataString(normalizedPath)}",
            lrcUrl = $"/api/lrc/file/lrc?path={Uri.EscapeDataString(normalizedPath)}"
        });
    }

    [HttpGet("file/audio")]
    public async Task<IActionResult> GetAudioForFile([FromQuery] string path, CancellationToken cancellationToken)
    {
        var (normalized, _, errorResult) = await ValidateAudioPathAsync(path, "Track file missing.", cancellationToken);
        if (errorResult != null)
        {
            return errorResult;
        }

        var normalizedPath = normalized!;
        var contentType = GetContentType(normalizedPath);
        var stream = System.IO.File.OpenRead(normalizedPath);
        return new FileStreamResult(stream, contentType)
        {
            EnableRangeProcessing = true
        };
    }

    [HttpGet("file/lrc")]
    public async Task<IActionResult> GetLrcForFile([FromQuery] string path, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return BadRequest(new { error = LibraryDbNotConfiguredMessage });
        }

        var normalized = await NormalizeAndValidatePathAsync(path, cancellationToken);
        if (normalized == null)
        {
            return BadRequest(new { error = PathOutsideLibraryRootsMessage });
        }

        var lrcPath = Path.ChangeExtension(normalized, ".lrc");
        if (!System.IO.File.Exists(lrcPath))
        {
            return NotFound(new { error = "LRC file not found." });
        }

        var content = await System.IO.File.ReadAllTextAsync(lrcPath, cancellationToken);
        var lastWrite = System.IO.File.GetLastWriteTimeUtc(lrcPath);
        return Ok(new { content, updatedAtUtc = lastWrite });
    }

    [HttpPost("file/lrc")]
    public async Task<IActionResult> SaveLrcForFile([FromBody] LrcSavePathRequest request, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return BadRequest(new { error = LibraryDbNotConfiguredMessage });
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Path) || request.Content == null)
        {
            return BadRequest(new { error = "Path and content are required." });
        }

        var normalized = await NormalizeAndValidatePathAsync(request.Path, cancellationToken);
        if (normalized == null)
        {
            return BadRequest(new { error = PathOutsideLibraryRootsMessage });
        }

        var lrcPath = Path.ChangeExtension(normalized, ".lrc");
        try
        {
            await System.IO.File.WriteAllTextAsync(lrcPath, request.Content, new UTF8Encoding(false), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to write LRC file at Path");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to save LRC file." });
        }

        var lastWrite = System.IO.File.GetLastWriteTimeUtc(lrcPath);
        return Ok(new { updatedAtUtc = lastWrite });
    }

    [HttpGet("track/{id:long}")]
    public async Task<IActionResult> GetTrack(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return BadRequest(new { error = LibraryDbNotConfiguredMessage });
        }

        var info = await _repository.GetTrackAudioInfoAsync(id, cancellationToken);
        if (info is null)
        {
            return NotFound();
        }

        return Ok(new
        {
            trackId = info.TrackId,
            title = info.Title,
            artist = info.ArtistName,
            album = info.AlbumTitle,
            durationMs = info.DurationMs,
            coverUrl = info.CoverPath is null
                ? null
                : $"/api/library/image?path={Uri.EscapeDataString(info.CoverPath)}&size=240",
            fileType = Path.GetExtension(info.FilePath).TrimStart('.').ToUpperInvariant(),
            audioUrl = $"/api/lrc/track/{info.TrackId}/audio",
            lrcUrl = $"/api/lrc/track/{info.TrackId}/lrc"
        });
    }

    [HttpGet("track/{id:long}/audio")]
    public async Task<IActionResult> GetAudio(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return BadRequest(new { error = LibraryDbNotConfiguredMessage });
        }

        var info = await _repository.GetTrackAudioInfoAsync(id, cancellationToken);
        if (info is null || string.IsNullOrWhiteSpace(info.FilePath))
        {
            return NotFound(new { error = "Track file unavailable." });
        }

        if (!System.IO.File.Exists(info.FilePath))
        {
            return NotFound(new { error = "Track file missing." });
        }

        var contentType = GetContentType(info.FilePath);
        var stream = System.IO.File.OpenRead(info.FilePath);
        return new FileStreamResult(stream, contentType)
        {
            EnableRangeProcessing = true
        };
    }

    [HttpGet("track/{id:long}/lrc")]
    public async Task<IActionResult> GetLrc(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return BadRequest(new { error = LibraryDbNotConfiguredMessage });
        }

        var info = await _repository.GetTrackAudioInfoAsync(id, cancellationToken);
        if (info is null || string.IsNullOrWhiteSpace(info.FilePath))
        {
            return NotFound(new { error = "Track file unavailable." });
        }

        var lrcPath = Path.ChangeExtension(info.FilePath, ".lrc");
        if (!System.IO.File.Exists(lrcPath))
        {
            return NotFound(new { error = "LRC file not found." });
        }

        var content = await System.IO.File.ReadAllTextAsync(lrcPath, cancellationToken);
        var lastWrite = System.IO.File.GetLastWriteTimeUtc(lrcPath);
        return Ok(new { content, updatedAtUtc = lastWrite });
    }

    [HttpPost("track/{id:long}/lrc")]
    public async Task<IActionResult> SaveLrc(long id, [FromBody] LrcSaveRequest request, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return BadRequest(new { error = LibraryDbNotConfiguredMessage });
        }

        if (request == null || request.Content == null)
        {
            return BadRequest(new { error = "Content is required." });
        }

        var info = await _repository.GetTrackAudioInfoAsync(id, cancellationToken);
        if (info is null || string.IsNullOrWhiteSpace(info.FilePath))
        {
            return NotFound(new { error = "Track file unavailable." });
        }

        var lrcPath = Path.ChangeExtension(info.FilePath, ".lrc");
        try
        {
            await System.IO.File.WriteAllTextAsync(lrcPath, request.Content, new UTF8Encoding(false), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to write LRC file at {Path}", lrcPath);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to save LRC file." });
        }

        var lastWrite = System.IO.File.GetLastWriteTimeUtc(lrcPath);
        return Ok(new { updatedAtUtc = lastWrite });
    }

    private async Task<(string? Normalized, string Extension, IActionResult? ErrorResult)> ValidateAudioPathAsync(
        string path,
        string notFoundMessage,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return (null, string.Empty, BadRequest(new { error = LibraryDbNotConfiguredMessage }));
        }

        var normalized = await NormalizeAndValidatePathAsync(path, cancellationToken);
        if (normalized == null)
        {
            return (null, string.Empty, BadRequest(new { error = PathOutsideLibraryRootsMessage }));
        }

        if (!System.IO.File.Exists(normalized))
        {
            return (null, string.Empty, NotFound(new { error = notFoundMessage }));
        }

        var extension = Path.GetExtension(normalized);
        if (!AudioExtensions.Contains(extension))
        {
            return (null, string.Empty, BadRequest(new { error = "Unsupported audio file." }));
        }

        return (normalized, extension, null);
    }

    private static string GetContentType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".m4a" => "audio/mp4",
            ".m4b" => "audio/mp4",
            ".aac" => "audio/aac",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".opus" => "audio/opus",
            _ => "application/octet-stream"
        };
    }

    public sealed record LrcSaveRequest(string? Content);

    public sealed record LrcSavePathRequest(string? Path, string? Content);
    private sealed record BrowserEntry(string Name, string Path, string Type);

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return null;
        }
    }

    private static bool IsUnderAnyRoot(string path, IReadOnlyList<string> roots)
    {
        var normalized = NormalizePath(path);
        if (normalized == null)
        {
            return false;
        }

        return roots
            .Select(NormalizePath)
            .Where(static root => root != null)
            .Select(static root => root!)
            .Any(rootFull =>
                normalized.StartsWith(rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, rootFull, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> NormalizeAndValidatePathAsync(string? path, CancellationToken cancellationToken)
    {
        var normalized = NormalizePath(path);
        if (normalized == null)
        {
            return null;
        }

        var roots = (await _repository.GetFoldersAsync(cancellationToken))
            .Where(folder => folder.Enabled)
            .Select(folder => folder.RootPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (roots.Count == 0)
        {
            return null;
        }

        return IsUnderAnyRoot(normalized, roots) ? normalized : null;
    }
}
