using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services.Audiomack;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/analysis")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public sealed class LibraryAnalysisStatusApiController : ControllerBase
{
    private readonly LibraryRepository _repository;
    private readonly TrackAnalysisBackgroundService _analysisService;
    private readonly IAudiomackVibeMetadataService _audiomackVibeMetadataService;
    private readonly LastFmTagService _lastFmTagService;
    private readonly VibeAnalysisSettingsStore _vibeAnalysisSettingsStore;
    private readonly DeezSpoTag.Services.Settings.DeezSpoTagSettingsService _settingsService;

    public LibraryAnalysisStatusApiController(
        LibraryRepository repository,
        TrackAnalysisBackgroundService analysisService,
        IAudiomackVibeMetadataService audiomackVibeMetadataService,
        LastFmTagService lastFmTagService,
        VibeAnalysisSettingsStore vibeAnalysisSettingsStore,
        DeezSpoTag.Services.Settings.DeezSpoTagSettingsService settingsService)
    {
        _audiomackVibeMetadataService = audiomackVibeMetadataService;
        _lastFmTagService = lastFmTagService;
        _vibeAnalysisSettingsStore = vibeAnalysisSettingsStore;
        _settingsService = settingsService;
        _repository = repository;
        _analysisService = analysisService;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        // The progress counter counts exactly the libraries enabled for Vibe
        // analysis (the settings' library order); no selection = all enabled.
        var settings = await _vibeAnalysisSettingsStore.LoadAsync().ConfigureAwait(false);
        var libraryIds = settings.UseLibraryOrder && settings.LibraryOrder.Count > 0
            ? settings.LibraryOrder
            : null;
        var status = await _repository.GetAnalysisStatusAsync(libraryIds, cancellationToken);
        return Ok(status);
    }

    /// <summary>
    /// Online platform tags for one track: Audiomack first, Last.fm fallback. The
    /// platform field names the active supplier so the UI label is dynamic.
    /// </summary>
    [HttpGet("tags")]
    public async Task<IActionResult> GetTags(long trackId, CancellationToken cancellationToken)
    {
        var summaries = await _repository.GetTrackSummariesAsync(new List<long> { trackId }, cancellationToken);
        var summary = summaries.FirstOrDefault();
        if (summary is null)
        {
            return Ok(new { platform = (string?)null, tags = Array.Empty<string>() });
        }

        var settings = _settingsService.LoadSettings();
        var aliasMap = settings.NormalizeGenreTags
            ? DeezSpoTag.Core.Utils.GenreTagAliasNormalizer.BuildAliasMap(settings.GenreTagAliasRules)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var blockList = DeezSpoTag.Core.Utils.GenreTagAliasNormalizer.NormalizeBlockedValues(settings.GenreTagBlockList);

        List<string> ApplyGenrePreferences(IEnumerable<string> values)
        {
            var normalized = DeezSpoTag.Core.Utils.GenreTagAliasNormalizer.DedupeValues(
                DeezSpoTag.Core.Utils.GenreTagAliasNormalizer.NormalizeAndExpandValues(values, aliasMap, settings.NormalizeGenreTags),
                blockList);
            // Capitalization is casing-preserving (R&B, HipHop, EDM survive) and
            // matches the user's capitalizeGenres behavior in AutoTag.
            return normalized.Select(DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner.CapitalizeGenre).ToList();
        }

        try
        {
            var audiomack = await _audiomackVibeMetadataService
                .FindTrackAsync(summary.ArtistName, summary.Title, cancellationToken)
                .ConfigureAwait(false);
            if (audiomack is not null)
            {
                var audiomackTags = ApplyGenrePreferences(
                    audiomack.Subgenres.Concat(audiomack.Moods));
                if (audiomackTags.Count > 0)
                {
                    return Ok(new { platform = "audiomack", tags = audiomackTags });
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Audiomack failure falls through to Last.fm.
        }

        var lastfmTags = await _lastFmTagService.GetTrackTagsAsync(summary.ArtistName, summary.Title, cancellationToken);
        if (lastfmTags is { Count: > 0 })
        {
            return Ok(new { platform = "lastfm", tags = ApplyGenrePreferences(lastfmTags) });
        }

        return Ok(new { platform = (string?)null, tags = Array.Empty<string>() });
    }

    [HttpGet("latest")]
    public async Task<IActionResult> GetLatest(CancellationToken cancellationToken)
    {
        var runtime = _analysisService.GetRuntimeSnapshot();
        var latest = runtime.Latest ?? await _repository.GetLatestTrackAnalysisAsync(cancellationToken);
        if (latest is null)
        {
            return NotFound();
        }

        return Ok(latest);
    }

    [HttpGet("current")]
    public IActionResult GetCurrent()
    {
        return GetCurrentProcessingResult();
    }

    [HttpGet("processing")]
    public IActionResult GetProcessing()
    {
        return GetCurrentProcessingResult();
    }

    private IActionResult GetCurrentProcessingResult()
    {
        var runtime = _analysisService.GetRuntimeSnapshot();
        var processing = runtime.Current;
        if (processing is null)
        {
            return NotFound();
        }

        return Ok(processing);
    }

    [HttpGet("runtime")]
    public IActionResult GetRuntime()
    {
        return Ok(_analysisService.GetRuntimeSnapshot());
    }
}
