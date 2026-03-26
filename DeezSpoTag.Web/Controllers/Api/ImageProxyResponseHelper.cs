using Microsoft.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

internal static class ImageProxyResponseHelper
{
    public static async Task<IActionResult> CreateImageResultAsync(
        ControllerBase controller,
        HttpResponseMessage response,
        Action<CacheControlHeaderValue>? configureCache,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return new StatusCodeResult((int)response.StatusCode);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
        var cacheControl = new CacheControlHeaderValue();
        configureCache?.Invoke(cacheControl);
        controller.Response.GetTypedHeaders().CacheControl = cacheControl;
        return controller.File(bytes, contentType);
    }
}
