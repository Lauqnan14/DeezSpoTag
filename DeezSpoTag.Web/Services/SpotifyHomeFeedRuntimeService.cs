using DeezSpoTag.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyHomeFeedRuntimeService
{
    private readonly SpotifyHomeFeedCollaborators _collaborators;
    private readonly IWebHostEnvironment _hostEnvironment;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SpotifyHomeFeedRuntimeService> _logger;

    public SpotifyHomeFeedRuntimeService(
        SpotifyHomeFeedCollaborators collaborators,
        IWebHostEnvironment hostEnvironment,
        ILoggerFactory loggerFactory,
        ILogger<SpotifyHomeFeedRuntimeService> logger)
    {
        _collaborators = collaborators;
        _hostEnvironment = hostEnvironment;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<object>> GetMappedSectionsAsync(
        string? timeZone,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var controller = CreateController();
        var result = await controller.GetHomeFeedSections(timeZone, refresh, cancellationToken);
        return ExtractSections(result);
    }

    public async Task<int> RefreshAsync(string? timeZone, CancellationToken cancellationToken)
    {
        var sections = await GetMappedSectionsAsync(timeZone, refresh: true, cancellationToken);
        if (sections.Count > 0)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Spotify home feed runtime cache refreshed. sections={SectionCount}", sections.Count);
            }
        }
        else
        {
            _logger.LogWarning("Spotify home feed runtime refresh completed with no sections.");
        }

        return sections.Count;
    }

    public async Task<IReadOnlyList<object>> GetBrowseCategoriesAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        var controller = CreateController();
        var result = await controller.GetBrowseCategories(refresh, debug: false, cancellationToken);
        return ExtractNamedList(result, "categories");
    }

    private SpotifyHomeFeedApiController CreateController()
        => new(_collaborators, _loggerFactory.CreateLogger<SpotifyHomeFeedApiController>(), _hostEnvironment);

    private static IReadOnlyList<object> ExtractSections(IActionResult result)
    {
        if (result is not ObjectResult objectResult || objectResult.Value == null)
        {
            return Array.Empty<object>();
        }

        return ExtractNamedList(objectResult.Value, "sections");
    }

    private static IReadOnlyList<object> ExtractNamedList(IActionResult result, string propertyName)
    {
        if (result is not ObjectResult objectResult || objectResult.Value == null)
        {
            return Array.Empty<object>();
        }

        return ExtractNamedList(objectResult.Value, propertyName);
    }

    private static IReadOnlyList<object> ExtractNamedList(object value, string propertyName)
    {
        var sectionsValue = value.GetType()
            .GetProperty(propertyName)
            ?.GetValue(value);

        return sectionsValue switch
        {
            IReadOnlyList<object> list => list,
            IEnumerable<object> enumerable => enumerable.ToList(),
            System.Collections.IEnumerable enumerable => enumerable.Cast<object>().ToList(),
            _ => Array.Empty<object>()
        };
    }
}
