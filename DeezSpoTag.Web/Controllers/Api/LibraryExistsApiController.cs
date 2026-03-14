using DeezSpoTag.Services.Library;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/library/exists")]
[Authorize]
public class LibraryExistsApiController : ControllerBase
{
    private readonly LibraryRepository _repository;

    public LibraryExistsApiController(LibraryRepository repository)
    {
        _repository = repository;
    }

    public sealed record LibraryExistenceRequest(
        string Id,
        string? Isrc,
        string? TrackTitle,
        string? ArtistName,
        int? DurationMs);

    [HttpPost]
    public async Task<IActionResult> Check([FromBody] IReadOnlyList<LibraryExistenceRequest> requests, CancellationToken cancellationToken)
    {
        if (requests == null || requests.Count == 0)
        {
            return Ok(Array.Empty<object>());
        }

        if (!_repository.IsConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        var inputs = requests
            .Select(item => new LibraryRepository.LibraryExistenceInput(
                item.Isrc,
                item.TrackTitle,
                item.ArtistName,
                item.DurationMs))
            .ToList();

        var results = await _repository.ExistsInLibraryAsync(inputs, cancellationToken);
        var response = new object[requests.Count];
        for (var i = 0; i < requests.Count; i++)
        {
            response[i] = new { id = requests[i].Id, exists = results[i] };
        }

        return Ok(response);
    }
}
