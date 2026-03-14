using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/scan")]
[ApiController]
[Authorize]
public class LibraryScanApiController : ControllerBase
{
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly LibraryScanRunner _scanRunner;

    public LibraryScanApiController(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        LibraryScanRunner scanRunner)
    {
        _repository = repository;
        _configStore = configStore;
        _scanRunner = scanRunner;
    }

    [HttpPost]
    public IActionResult Scan(
        [FromQuery] bool refreshImages,
        [FromQuery] bool reset,
        [FromQuery] long? folderId,
        [FromQuery] bool skipSpotifyFetch,
        [FromQuery] bool cacheSpotifyImages,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            "Library scan enqueued."));

        _ = _scanRunner.EnqueueAsync(
            refreshImages,
            reset,
            folderId,
            skipSpotifyFetch,
            cacheSpotifyImages);

        return Ok(new { queued = true });
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        var folders = await _repository.GetFoldersAsync(cancellationToken);
        var lastScan = _configStore.GetLastScanInfo();
        var scanStatus = _scanRunner.GetStatus();

        return Ok(new
        {
            lastRunUtc = lastScan.LastRunUtc,
            lastCounts = new
            {
                artists = lastScan.ArtistCount,
                albums = lastScan.AlbumCount,
                tracks = lastScan.TrackCount
            },
            running = scanStatus.IsRunning,
            progress = new
            {
                processedFiles = scanStatus.ProcessedFiles,
                totalFiles = scanStatus.TotalFiles,
                errorCount = scanStatus.ErrorCount,
                currentFile = scanStatus.CurrentFile,
                artistsDetected = scanStatus.ArtistsDetected,
                albumsDetected = scanStatus.AlbumsDetected,
                tracksDetected = scanStatus.TracksDetected
            },
            folderCount = folders.Count,
            dbConfigured = _repository.IsConfigured
        });
    }

    [HttpPost("cancel")]
    public IActionResult Cancel()
    {
        var cancelled = _scanRunner.TryCancel();
        return Ok(new { cancelled });
    }

    [HttpGet("logs")]
    public IActionResult Logs([FromQuery] int? limit)
    {
        var logs = _configStore.GetLogs();
        if (limit.HasValue && limit.Value > 0)
        {
            var take = limit.Value;
            logs = logs.Count > take
                ? logs.Skip(logs.Count - take).ToList()
                : logs.ToList();
        }
        return Ok(logs);
    }

    [HttpPost("logs")]
    public IActionResult AppendLog([FromBody] LibraryLogRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest();
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(request.Level) ? "info" : request.Level.ToLowerInvariant(),
            request.Message));

        return Ok(new { ok = true });
    }

    [HttpPost("logs/clear")]
    public IActionResult ClearLogs()
    {
        _configStore.ClearLogs();
        return Ok(new { ok = true });
    }

    public sealed record LibraryLogRequest(string? Level, string Message);

}
