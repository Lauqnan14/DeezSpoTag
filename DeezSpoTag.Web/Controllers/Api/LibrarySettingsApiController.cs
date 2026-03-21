using DeezSpoTag.Services.Library;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/settings")]
[ApiController]
[Authorize]
public class LibrarySettingsApiController : ControllerBase
{
    private readonly LibraryRepository _repository;

    public LibrarySettingsApiController(LibraryRepository repository)
    {
        _repository = repository;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        var settings = await GetSettingsWithRetryAsync(cancellationToken);
        return Ok(settings);
    }

    private async Task<LibrarySettingsDto> GetSettingsWithRetryAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(150);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return await _repository.GetSettingsAsync(cancellationToken);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5 && attempt < 4)
            {
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 2000));
            }
        }

        return await _repository.GetSettingsAsync(cancellationToken);
    }

    public sealed record UpdateSettingsRequest(
        decimal? FuzzyThreshold,
        bool? IncludeAllFolders,
        bool? LivePreviewIngest,
        bool? EnableSignalAnalysis);

    [HttpPost]
    public async Task<IActionResult> Update([FromBody] UpdateSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        var existing = await GetSettingsWithRetryAsync(cancellationToken);
        var update = new LibrarySettingsDto(
            request.FuzzyThreshold ?? existing.FuzzyThreshold,
            request.IncludeAllFolders ?? existing.IncludeAllFolders,
            request.LivePreviewIngest ?? existing.LivePreviewIngest,
            request.EnableSignalAnalysis ?? existing.EnableSignalAnalysis);
        var updated = await _repository.UpdateSettingsAsync(update, cancellationToken);
        return Ok(updated);
    }
}
