using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/meloday/artwork")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
[Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryToken]
public sealed class MelodayArtworkApiController : ControllerBase
{
    private readonly MelodayArtworkPool _pool;
    private readonly MelodayArtworkAssignments _assignments;

    public MelodayArtworkApiController(MelodayArtworkPool pool, MelodayArtworkAssignments assignments)
    {
        _pool = pool;
        _assignments = assignments;
    }

    public sealed record ArtworkSummary(
        int Count,
        IReadOnlyList<string> Images,
        IReadOnlyList<MelodayArtworkAssignment> Assignments);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var images = _pool.ListImages();
        var assignments = await _assignments.GetAllAsync(cancellationToken);
        var validAssignments = assignments
            .Where(assignment => images.Contains(assignment.ImageId, StringComparer.OrdinalIgnoreCase))
            .ToList();
        return Ok(new ArtworkSummary(images.Count, images, validAssignments));
    }

    [HttpPost]
    public async Task<IActionResult> Upload(CancellationToken cancellationToken)
    {
        var files = Request.Form.Files;
        if (files.Count == 0)
        {
            return BadRequest("No artwork files were uploaded.");
        }

        var acceptedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
        var existing = _pool.ListImages().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        Directory.CreateDirectory(_pool.SourceDirectory);
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(fileName)
                || !acceptedExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = Path.Join(_pool.SourceDirectory, fileName);
            if (existing.Contains(fileName))
            {
                continue;
            }

            await using var stream = System.IO.File.Create(target);
            await file.CopyToAsync(stream, cancellationToken);
            existing.Add(fileName);
            added++;
        }

        return Ok(new { added, count = existing.Count });
    }
}
