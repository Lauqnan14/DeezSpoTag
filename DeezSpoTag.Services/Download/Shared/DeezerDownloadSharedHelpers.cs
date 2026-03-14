using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace DeezSpoTag.Services.Download.Shared;

internal static class DeezerDownloadSharedHelpers
{
    public static async Task<string?> ResolveEpisodeStreamUrlFromShowAsync(
        IServiceProvider serviceProvider,
        string? showId,
        string episodeId,
        Action<Exception, string>? onError = null)
    {
        if (string.IsNullOrWhiteSpace(showId))
        {
            return null;
        }

        try
        {
            using var scope = serviceProvider.CreateScope();
            var gatewayService = scope.ServiceProvider.GetRequiredService<DeezerGatewayService>();
            var showPage = await gatewayService.GetShowPageAsync(showId);
            return DeezerEpisodeStreamResolver.ResolveStreamUrl(
                showPage,
                episodeId,
                includeLinkFallback: false,
                rejectDeezerEpisodePages: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onError?.Invoke(ex, episodeId);
            return null;
        }
    }

    public static Dictionary<string, object> GetDictObject(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value == null)
        {
            return new Dictionary<string, object>();
        }

        if (value is Dictionary<string, object> typed)
        {
            return typed;
        }

        if (value is Newtonsoft.Json.Linq.JObject jobject)
        {
            return jobject.ToObject<Dictionary<string, object>>() ?? new Dictionary<string, object>();
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText()) ?? new Dictionary<string, object>();
        }

        return new Dictionary<string, object>();
    }
}
