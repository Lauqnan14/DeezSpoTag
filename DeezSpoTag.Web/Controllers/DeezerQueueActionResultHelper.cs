using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers;

internal static class DeezerQueueActionResultHelper
{
    public static IActionResult FromQueued(Controller controller, IReadOnlyCollection<Dictionary<string, object>> queued)
    {
        if (queued.Count == 0)
        {
            return controller.Json(new { success = false, message = "Nothing queued." });
        }

        return controller.Json(new { success = true, queued });
    }

    public static IActionResult FromError(Controller controller, ILogger logger, Exception exception, string logMessage)
    {
        logger.LogError(exception, "{LogMessage}", logMessage);
        return controller.Json(new { success = false, message = exception.Message });
    }
}
