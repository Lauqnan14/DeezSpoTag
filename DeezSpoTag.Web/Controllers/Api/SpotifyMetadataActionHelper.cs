using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

internal static class SpotifyMetadataActionHelper
{
    internal sealed record SpotifyMetadataFetchRequest(
        string Id,
        string IdParameterName,
        string SpotifyType,
        string FailureLogMessage,
        string FailureResponseMessage);

    public static async Task<IActionResult> FetchByUrlAsync(
        ControllerBase controller,
        SpotifyPathfinderMetadataClient pathfinder,
        ILogger logger,
        SpotifyMetadataFetchRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            return controller.BadRequest($"{request.IdParameterName} is required");
        }

        try
        {
            var url = $"https://open.spotify.com/{request.SpotifyType}/{request.Id}";
            var metadata = await pathfinder.FetchByUrlAsync(url, cancellationToken);
            if (metadata is null)
            {
                return controller.Ok(new { available = false });
            }

            return controller.Ok(new { available = true, metadata });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{FailureLogMessage}", request.FailureLogMessage);
            return controller.StatusCode(502, new { error = request.FailureResponseMessage });
        }
    }
}
