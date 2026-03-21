using DeezSpoTag.Services.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
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
    private static readonly HashSet<string> LrcBrowserExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lrc", ".txt"
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

        var roots = await GetActiveLibraryRootsAsync(cancellationToken);

        if (roots.Count == 0)
        {
            return Ok(new { path = string.Empty, displayPath = "Library roots", parentPath = string.Empty, entries = Array.Empty<object>() });
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            var rootEntries = roots
                .Select(root => new
                {
                    name = root.DisplayName,
                    path = BuildReference(root, root.RootPath),
                    type = "folder"
                })
                .OrderBy(entry => entry.name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Ok(new { path = string.Empty, displayPath = "Library roots", parentPath = string.Empty, entries = rootEntries });
        }

        var resolved = TryResolveReference(path, roots);
        if (resolved == null || !Directory.Exists(resolved.FullPath))
        {
            return BadRequest(new { error = PathOutsideLibraryRootsMessage });
        }

        var entries = new List<BrowserEntry>();
        foreach (var directory in Directory.EnumerateDirectories(resolved.FullPath))
        {
            var name = Path.GetFileName(directory);
            entries.Add(new BrowserEntry(name, BuildReference(resolved.Root, directory), "folder"));
        }

        foreach (var file in Directory.EnumerateFiles(resolved.FullPath))
        {
            var extension = Path.GetExtension(file);
            if (!AudioExtensions.Contains(extension) && !LrcBrowserExtensions.Contains(extension))
            {
                continue;
            }
            entries.Add(new BrowserEntry(Path.GetFileName(file), BuildReference(resolved.Root, file), "file"));
        }

        var sorted = entries
            .OrderBy(entry => entry.Type == "folder" ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var parentPath = string.Equals(resolved.FullPath, resolved.Root.RootPath, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : BuildReference(resolved.Root, Path.GetDirectoryName(resolved.FullPath)!);

        return Ok(new
        {
            path = path,
            displayPath = BuildDisplayPath(resolved.Root, resolved.FullPath),
            parentPath,
            entries = sorted
        });
    }

    [HttpGet("file/info")]
    public async Task<IActionResult> GetFileInfo([FromQuery] string path, CancellationToken cancellationToken)
    {
        var (resolved, extension, errorResult) = await ValidateAudioPathAsync(path, "File not found.", cancellationToken);
        if (errorResult != null)
        {
            return errorResult;
        }

        var resolvedPath = resolved!;
        var normalizedPath = resolvedPath.FullPath;

        return Ok(new
        {
            fileRef = resolvedPath.Reference,
            fileName = Path.GetFileNameWithoutExtension(normalizedPath),
            directory = BuildDisplayPath(resolvedPath.Root, Path.GetDirectoryName(normalizedPath) ?? resolvedPath.Root.RootPath),
            fileType = extension.TrimStart('.').ToUpperInvariant(),
            audioUrl = $"/api/lrc/file/audio?path={Uri.EscapeDataString(resolvedPath.Reference)}",
            lrcUrl = $"/api/lrc/file/lrc?path={Uri.EscapeDataString(resolvedPath.Reference)}",
            txtUrl = $"/api/lrc/file/txt?path={Uri.EscapeDataString(resolvedPath.Reference)}"
        });
    }

    [HttpGet("file/audio")]
    public async Task<IActionResult> GetAudioForFile([FromQuery] string path, CancellationToken cancellationToken)
    {
        var (resolved, _, errorResult) = await ValidateAudioPathAsync(path, "Track file missing.", cancellationToken);
        if (errorResult != null)
        {
            return errorResult;
        }

        var normalizedPath = resolved!.FullPath;
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

        var resolved = await ResolveLibraryPathAsync(path, cancellationToken);
        if (resolved == null)
        {
            return BadRequest(new { error = PathOutsideLibraryRootsMessage });
        }

        var lrcPath = Path.ChangeExtension(resolved.FullPath, ".lrc");
        if (!System.IO.File.Exists(lrcPath))
        {
            return NotFound(new { error = "LRC file not found." });
        }

        var content = await System.IO.File.ReadAllTextAsync(lrcPath, cancellationToken);
        var lastWrite = System.IO.File.GetLastWriteTimeUtc(lrcPath);
        return Ok(new { content, updatedAtUtc = lastWrite });
    }

    [HttpGet("file/txt")]
    public async Task<IActionResult> GetTxtForFile([FromQuery] string path, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return BadRequest(new { error = LibraryDbNotConfiguredMessage });
        }

        var resolved = await ResolveLibraryPathAsync(path, cancellationToken);
        if (resolved == null)
        {
            return BadRequest(new { error = PathOutsideLibraryRootsMessage });
        }

        var txtPath = Path.ChangeExtension(resolved.FullPath, ".txt");
        if (!System.IO.File.Exists(txtPath))
        {
            return NotFound(new { error = "TXT file not found." });
        }

        var content = await System.IO.File.ReadAllTextAsync(txtPath, cancellationToken);
        var lastWrite = System.IO.File.GetLastWriteTimeUtc(txtPath);
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

        var resolved = await ResolveLibraryPathAsync(request.Path, cancellationToken);
        if (resolved == null)
        {
            return BadRequest(new { error = PathOutsideLibraryRootsMessage });
        }

        var lrcPath = Path.ChangeExtension(resolved.FullPath, ".lrc");
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

        var resolved = await ResolveTrackLibraryPathAsync(info.FilePath, cancellationToken);
        if (resolved == null)
        {
            return NotFound(new { error = "Track file unavailable on this system." });
        }

        return Ok(new
        {
            trackId = info.TrackId,
            title = info.Title,
            artist = info.ArtistName,
            album = info.AlbumTitle,
            durationMs = info.DurationMs,
            fileRef = resolved.Reference,
            directory = BuildDisplayPath(resolved.Root, Path.GetDirectoryName(resolved.FullPath) ?? resolved.Root.RootPath),
            coverUrl = info.CoverPath is null
                ? null
                : $"/api/library/image?path={Uri.EscapeDataString(info.CoverPath)}&size=240",
            fileType = Path.GetExtension(resolved.FullPath).TrimStart('.').ToUpperInvariant(),
            audioUrl = $"/api/lrc/track/{info.TrackId}/audio",
            lrcUrl = $"/api/lrc/track/{info.TrackId}/lrc",
            txtUrl = $"/api/lrc/track/{info.TrackId}/txt"
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

        var resolved = await ResolveTrackLibraryPathAsync(info.FilePath, cancellationToken);
        if (resolved == null || !System.IO.File.Exists(resolved.FullPath))
        {
            return NotFound(new { error = "Track file missing." });
        }

        var contentType = GetContentType(resolved.FullPath);
        var stream = System.IO.File.OpenRead(resolved.FullPath);
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

        var resolved = await ResolveTrackLibraryPathAsync(info.FilePath, cancellationToken);
        if (resolved == null)
        {
            return NotFound(new { error = "Track file unavailable on this system." });
        }

        var lrcPath = Path.ChangeExtension(resolved.FullPath, ".lrc");
        if (!System.IO.File.Exists(lrcPath))
        {
            return NotFound(new { error = "LRC file not found." });
        }

        var content = await System.IO.File.ReadAllTextAsync(lrcPath, cancellationToken);
        var lastWrite = System.IO.File.GetLastWriteTimeUtc(lrcPath);
        return Ok(new { content, updatedAtUtc = lastWrite });
    }

    [HttpGet("track/{id:long}/txt")]
    public async Task<IActionResult> GetTxt(long id, CancellationToken cancellationToken)
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

        var resolved = await ResolveTrackLibraryPathAsync(info.FilePath, cancellationToken);
        if (resolved == null)
        {
            return NotFound(new { error = "Track file unavailable on this system." });
        }

        var txtPath = Path.ChangeExtension(resolved.FullPath, ".txt");
        if (!System.IO.File.Exists(txtPath))
        {
            return NotFound(new { error = "TXT file not found." });
        }

        var content = await System.IO.File.ReadAllTextAsync(txtPath, cancellationToken);
        var lastWrite = System.IO.File.GetLastWriteTimeUtc(txtPath);
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

        var resolved = await ResolveTrackLibraryPathAsync(info.FilePath, cancellationToken);
        if (resolved == null)
        {
            return NotFound(new { error = "Track file unavailable on this system." });
        }

        var lrcPath = Path.ChangeExtension(resolved.FullPath, ".lrc");
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

    private async Task<(ResolvedLibraryPath? Resolved, string Extension, IActionResult? ErrorResult)> ValidateAudioPathAsync(
        string path,
        string notFoundMessage,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return (null, string.Empty, BadRequest(new { error = LibraryDbNotConfiguredMessage }));
        }

        var resolved = await ResolveLibraryPathAsync(path, cancellationToken);
        if (resolved == null)
        {
            return (null, string.Empty, BadRequest(new { error = PathOutsideLibraryRootsMessage }));
        }

        if (!System.IO.File.Exists(resolved.FullPath))
        {
            return (null, string.Empty, NotFound(new { error = notFoundMessage }));
        }

        var extension = Path.GetExtension(resolved.FullPath);
        if (!AudioExtensions.Contains(extension))
        {
            return (null, string.Empty, BadRequest(new { error = "Unsupported audio file." }));
        }

        return (resolved, extension, null);
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
    private sealed record LibraryRootScope(string DisplayName, string RootPath, string Token);
    private sealed record ResolvedLibraryPath(LibraryRootScope Root, string FullPath, string Reference);

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

    private async Task<List<LibraryRootScope>> GetActiveLibraryRootsAsync(CancellationToken cancellationToken)
    {
        return (await _repository.GetFoldersAsync(cancellationToken))
            .Where(folder => folder.Enabled)
            .Select(folder =>
            {
                var normalizedRoot = NormalizePath(folder.RootPath);
                return new { folder.DisplayName, RootPath = normalizedRoot };
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.RootPath) && Directory.Exists(entry.RootPath!))
            .GroupBy(entry => entry.RootPath!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var rootPath = group.Key;
                var displayName = group
                    .Select(item => item.DisplayName?.Trim())
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }

                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = "Library";
                }

                return new LibraryRootScope(displayName, rootPath, EncodePathSegment(rootPath));
            })
            .OrderBy(root => root.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<ResolvedLibraryPath?> ResolveLibraryPathAsync(string? reference, CancellationToken cancellationToken)
    {
        var roots = await GetActiveLibraryRootsAsync(cancellationToken);
        if (roots.Count == 0)
        {
            return null;
        }

        return TryResolveReference(reference, roots);
    }

    private async Task<ResolvedLibraryPath?> ResolveTrackLibraryPathAsync(string? path, CancellationToken cancellationToken)
    {
        var roots = await GetActiveLibraryRootsAsync(cancellationToken);
        if (roots.Count == 0)
        {
            return null;
        }

        var normalized = NormalizePath(path);
        if (normalized == null)
        {
            return null;
        }

        var root = roots.FirstOrDefault(candidate =>
            normalized.StartsWith(candidate.RootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, candidate.RootPath, StringComparison.OrdinalIgnoreCase));

        return root == null ? null : new ResolvedLibraryPath(root, normalized, BuildReference(root, normalized));
    }

    private static ResolvedLibraryPath? TryResolveReference(string? reference, IReadOnlyList<LibraryRootScope> roots)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var separatorIndex = reference.IndexOf(':');
        if (separatorIndex <= 0)
        {
            return null;
        }

        var rootToken = reference[..separatorIndex];
        var relativeToken = reference[(separatorIndex + 1)..];
        var root = roots.FirstOrDefault(candidate => string.Equals(candidate.Token, rootToken, StringComparison.Ordinal));
        if (root == null)
        {
            return null;
        }

        string relativePath;
        try
        {
            relativePath = DecodePathSegment(relativeToken);
        }
        catch
        {
            return null;
        }

        var fullPath = string.IsNullOrWhiteSpace(relativePath)
            ? root.RootPath
            : Path.GetFullPath(Path.Combine(root.RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!string.Equals(fullPath, root.RootPath, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(root.RootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ResolvedLibraryPath(root, fullPath, BuildReference(root, fullPath));
    }

    private static string BuildReference(LibraryRootScope root, string fullPath)
    {
        var relativePath = Path.GetRelativePath(root.RootPath, fullPath);
        if (string.Equals(relativePath, ".", StringComparison.Ordinal))
        {
            relativePath = string.Empty;
        }

        return $"{root.Token}:{EncodePathSegment(relativePath.Replace(Path.DirectorySeparatorChar, '/'))}";
    }

    private static string BuildDisplayPath(LibraryRootScope root, string fullPath)
    {
        var relativePath = Path.GetRelativePath(root.RootPath, fullPath);
        if (string.Equals(relativePath, ".", StringComparison.Ordinal))
        {
            return root.DisplayName;
        }

        return $"{root.DisplayName}/{relativePath.Replace(Path.DirectorySeparatorChar, '/')}";
    }

    private static string EncodePathSegment(string value)
        => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(value ?? string.Empty));

    private static string DecodePathSegment(string value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(value));
}
