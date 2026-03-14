using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

public abstract class SearchApiControllerBase : ControllerBase
{
    private readonly DeezSpoTagSearchService _searchService;

    protected SearchApiControllerBase(DeezSpoTagSearchService searchService)
    {
        _searchService = searchService;
    }

    protected async Task<IActionResult> SearchAsync(
        SearchApiRequest request,
        CancellationToken cancellationToken)
    {
        var queryError = SearchApiControllerCommon.ValidateQuery(request.Query);
        if (queryError != null)
        {
            return BadRequest(new { error = queryError });
        }

        var searchRequest = new DeezSpoTagSearchRequest(
            request.Provider,
            request.Query,
            Math.Clamp(request.Limit, 1, 50),
            Math.Max(0, request.Offset),
            Types: null,
            AudioTraits: null,
            Title: request.Title,
            Artist: request.Artist,
            Album: request.Album,
            Isrc: request.Isrc,
            DurationMs: request.DurationMs);
        var result = await _searchService.SearchAsync(searchRequest, cancellationToken);

        return result == null
            ? Ok(SearchApiControllerCommon.BuildUnavailablePayload())
            : Ok(SearchApiControllerCommon.BuildSearchPayload(result));
    }

    protected async Task<IActionResult> SearchByTypeAsync(
        string provider,
        string query,
        string type,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        var queryError = SearchApiControllerCommon.ValidateQuery(query);
        if (queryError != null)
        {
            return BadRequest(new { error = queryError });
        }

        var typeError = SearchApiControllerCommon.ValidateType(type);
        if (typeError != null)
        {
            return BadRequest(new { error = typeError });
        }

        var result = await _searchService.SearchByTypeAsync(
            provider,
            query,
            SearchApiControllerCommon.NormalizeType(type),
            Math.Clamp(limit, 1, 50),
            Math.Max(0, offset),
            cancellationToken);

        return result == null
            ? Ok(SearchApiControllerCommon.BuildUnavailablePayload())
            : Ok(SearchApiControllerCommon.BuildTypedPayload(result));
    }

    protected sealed record SearchApiRequest(
        string Provider,
        string Query,
        int Limit,
        int Offset,
        string? Title = null,
        string? Artist = null,
        string? Album = null,
        string? Isrc = null,
        long? DurationMs = null);
}
