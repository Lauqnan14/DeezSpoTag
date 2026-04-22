using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DeezSpoTag.Services.Download.Shared;

internal static class DeezerDownloadSharedHelpers
{
    private const int ShowPageCacheMaxEntries = 256;
    private static readonly TimeSpan ShowPageCacheTtl = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, CachedShowPage> ShowPageCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ShowPageGates = new(StringComparer.OrdinalIgnoreCase);

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
            var showPage = await GetCachedShowPageAsync(serviceProvider, showId);
            if (showPage == null)
            {
                return null;
            }

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

    private static async Task<JObject?> GetCachedShowPageAsync(IServiceProvider serviceProvider, string showId)
    {
        var cacheKey = showId.Trim();
        if (TryGetCachedShowPage(cacheKey, out var cached))
        {
            return cached;
        }

        var gate = ShowPageGates.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (TryGetCachedShowPage(cacheKey, out cached))
            {
                return cached;
            }

            using var scope = serviceProvider.CreateScope();
            var gatewayService = scope.ServiceProvider.GetRequiredService<DeezerGatewayService>();
            var showPage = await gatewayService.GetShowPageAsync(cacheKey).ConfigureAwait(false);
            if (showPage == null)
            {
                return null;
            }

            ShowPageCache[cacheKey] = new CachedShowPage(DateTimeOffset.UtcNow, showPage);
            PruneShowPageCacheIfNeeded();
            return (JObject)showPage.DeepClone();
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool TryGetCachedShowPage(string cacheKey, out JObject? showPage)
    {
        showPage = null;
        if (!ShowPageCache.TryGetValue(cacheKey, out var cached))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - cached.CachedAtUtc > ShowPageCacheTtl)
        {
            ShowPageCache.TryRemove(cacheKey, out _);
            return false;
        }

        showPage = (JObject)cached.Page.DeepClone();
        return true;
    }

    private static void PruneShowPageCacheIfNeeded()
    {
        if (ShowPageCache.Count <= ShowPageCacheMaxEntries)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ShowPageCache)
        {
            if (now - entry.Value.CachedAtUtc > ShowPageCacheTtl)
            {
                ShowPageCache.TryRemove(entry.Key, out _);
            }
        }

        if (ShowPageCache.Count <= ShowPageCacheMaxEntries)
        {
            return;
        }

        var overflow = ShowPageCache.Count - ShowPageCacheMaxEntries;
        if (overflow <= 0)
        {
            return;
        }

        foreach (var entry in ShowPageCache
                     .OrderBy(item => item.Value.CachedAtUtc)
                     .Take(overflow))
        {
            ShowPageCache.TryRemove(entry.Key, out _);
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

    private sealed record CachedShowPage(DateTimeOffset CachedAtUtc, JObject Page);
}
