using DeezSpoTag.Web.Services;
using DeezSpoTag.Services.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/quicktag")]
[Authorize]
public sealed class QuickTagApiController : ControllerBase
{
    private readonly QuickTagService _quickTag;

    public QuickTagApiController(QuickTagService quickTag)
    {
        _quickTag = quickTag;
    }

    [HttpGet("folder")]
    public IActionResult Folder([FromQuery] string? path, [FromQuery] string? subdir)
    {
        try
        {
            return Ok(_quickTag.GetFolder(path, subdir));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("load")]
    public IActionResult Load([FromBody] QuickTagLoadRequest request)
    {
        try
        {
            var result = _quickTag.Load(request ?? new QuickTagLoadRequest());
            return Ok(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("save")]
    public IActionResult Save([FromBody] QuickTagSaveRequest request)
    {
        try
        {
            var file = _quickTag.Save(request);
            return Ok(new { file });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("thumb")]
    public IActionResult Thumb([FromQuery] string? path, [FromQuery] int? size, [FromQuery] bool crop = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(new { error = "Path is required." });
        }

        try
        {
            var bytes = _quickTag.LoadThumbnail(path, size ?? 50, crop);
            return File(bytes, "image/jpeg");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return NotFound();
        }
    }

    [HttpGet("audio")]
    public IActionResult Audio([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(new { error = "Path is required." });
        }

        try
        {
            var audio = _quickTag.OpenAudio(path);
            var fileName = Path.GetFileName(audio.Path);
            return File(audio.Stream, audio.ContentType, fileName, enableRangeProcessing: true);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("dump")]
    public IActionResult Dump([FromQuery] string? path, [FromQuery] bool includeArtworkData = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(new { error = "Path is required." });
        }

        try
        {
            var payload = _quickTag.Dump(path, includeArtworkData);
            return Ok(payload);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

}
