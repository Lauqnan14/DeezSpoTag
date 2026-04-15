using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Settings;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public sealed class AppleCatalogVideoAtmosEnricher
{
    private const string DefaultLanguage = "en-US";
    private const string DataField = "data";
    private const string AttributesField = "attributes";
    private const string AudioTraitsField = "audioTraits";
    private const string AppleIdField = "appleId";
    private const string AppleUrlField = "appleUrl";
    private const string HasAtmosField = "hasAtmos";
    private const string HasAtmosCatalogField = "hasAtmosCatalog";
    private const string HasAtmosDownloadableField = "hasAtmosDownloadable";
    private const string HasAtmosVerifiedField = "hasAtmosVerified";
    private const string AtmosDetectionField = "atmosDetection";
    private const string CatalogDetection = "catalog";
    private const string CatalogDetailsDetection = "catalog-details";
    private const string ManifestDetection = "manifest";
    private const string UnavailableDetection = "unavailable";
    private const string AtmosKeyword = "atmos";
    private static readonly TimeSpan AtmosCatalogLookupBudget = TimeSpan.FromSeconds(5);

    private readonly AppleMusicCatalogService _catalog;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly AppleVideoAtmosCapabilityService _appleVideoAtmosCapabilityService;
    private readonly ILogger<AppleCatalogVideoAtmosEnricher> _logger;

    public AppleCatalogVideoAtmosEnricher(
        AppleMusicCatalogService catalog,
        DeezSpoTagSettingsService settingsService,
        AppleVideoAtmosCapabilityService appleVideoAtmosCapabilityService,
        ILogger<AppleCatalogVideoAtmosEnricher> logger)
    {
        _catalog = catalog;
        _settingsService = settingsService;
        _appleVideoAtmosCapabilityService = appleVideoAtmosCapabilityService;
        _logger = logger;
    }

    public async Task EnrichAsync(
        List<Dictionary<string, object?>> videos,
        string failureLogMessage,
        CancellationToken cancellationToken)
    {
        if (videos.Count == 0)
        {
            return;
        }

        var ids = CollectResolvedIds(videos);
        if (ids.Count == 0)
        {
            return;
        }

        var capabilities = await _appleVideoAtmosCapabilityService.GetAtmosCapabilitiesAsync(ids, cancellationToken);
        var detailLookupIds = CollectDetailLookupIds(videos, capabilities);
        var catalogDetailHints = await ReadCatalogVideoAtmosHintsAsync(detailLookupIds, failureLogMessage, cancellationToken);
        ApplyCapabilityResults(videos, capabilities, catalogDetailHints);
    }

    private static HashSet<string> CollectResolvedIds(IEnumerable<Dictionary<string, object?>> videos)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in videos)
        {
            var resolvedId = ResolveVideoId(video);
            if (!string.IsNullOrWhiteSpace(resolvedId))
            {
                ids.Add(resolvedId);
            }
        }

        return ids;
    }

    private static HashSet<string> CollectDetailLookupIds(
        IEnumerable<Dictionary<string, object?>> videos,
        IReadOnlyDictionary<string, bool?> capabilities)
    {
        var detailLookupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in videos)
        {
            var resolvedId = ResolveVideoId(video);
            if (string.IsNullOrWhiteSpace(resolvedId) || ReadVideoBool(video, HasAtmosCatalogField))
            {
                continue;
            }

            if (!capabilities.TryGetValue(resolvedId, out var hasAtmosDownloadable) || !hasAtmosDownloadable.HasValue)
            {
                detailLookupIds.Add(resolvedId);
            }
        }

        return detailLookupIds;
    }

    private static void ApplyCapabilityResults(
        IEnumerable<Dictionary<string, object?>> videos,
        IReadOnlyDictionary<string, bool?> capabilities,
        IReadOnlyDictionary<string, bool> catalogDetailHints)
    {
        foreach (var video in videos)
        {
            var resolvedId = ResolveVideoId(video);
            if (string.IsNullOrWhiteSpace(resolvedId)
                || !capabilities.TryGetValue(resolvedId, out var hasAtmosDownloadable))
            {
                continue;
            }

            var hasAtmosCatalog = ReadVideoBool(video, HasAtmosCatalogField);
            if (hasAtmosDownloadable.HasValue)
            {
                video[HasAtmosDownloadableField] = hasAtmosDownloadable;
                video[HasAtmosVerifiedField] = true;
                video[HasAtmosField] = hasAtmosDownloadable.Value;
                video[AtmosDetectionField] = ManifestDetection;
                continue;
            }

            if (catalogDetailHints.TryGetValue(resolvedId, out var hasAtmosDetail) && hasAtmosDetail)
            {
                video[HasAtmosDownloadableField] = true;
                video[HasAtmosVerifiedField] = false;
                video[HasAtmosField] = true;
                video[AtmosDetectionField] = CatalogDetailsDetection;
                continue;
            }

            video[HasAtmosDownloadableField] = hasAtmosCatalog ? true : null;
            video[HasAtmosVerifiedField] = false;
            video[HasAtmosField] = hasAtmosCatalog;
            video[AtmosDetectionField] = hasAtmosCatalog ? CatalogDetection : UnavailableDetection;
        }
    }

    private static string ResolveVideoId(Dictionary<string, object?> video)
    {
        var appleId = ReadVideoValue(video, AppleIdField);
        var appleUrl = ReadVideoValue(video, AppleUrlField);
        return AppleVideoAtmosCapabilityService.ResolveAppleId(appleId, appleUrl);
    }

    private async Task<IReadOnlyDictionary<string, bool>> ReadCatalogVideoAtmosHintsAsync(
        IEnumerable<string> appleIds,
        string failureLogMessage,
        CancellationToken cancellationToken)
    {
        var ids = appleIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        var settings = _settingsService.LoadSettings();
        var storefront = await _catalog.ResolveStorefrontAsync(
            settings.AppleMusic?.Storefront,
            settings.AppleMusic?.MediaUserToken,
            cancellationToken);

        var results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using var semaphore = new SemaphoreSlim(6);
        using var lookupBudgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lookupBudgetCts.CancelAfter(AtmosCatalogLookupBudget);
        var lookupToken = lookupBudgetCts.Token;
        var tasks = ids.Select(async id =>
        {
            var acquired = false;
            try
            {
                await semaphore.WaitAsync(lookupToken);
                acquired = true;
                using var doc = await _catalog.GetMusicVideoAsync(id, storefront, DefaultLanguage, lookupToken);
                var hasAtmos = ReadAtmosFromMusicVideoPayload(doc.RootElement);
                lock (results)
                {
                    results[id] = hasAtmos;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _ = lookupToken.IsCancellationRequested;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "{FailureLogMessage}. AppleId={AppleId}", failureLogMessage, id);
                }
            }
            finally
            {
                if (acquired)
                {
                    semaphore.Release();
                }
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _ = lookupToken.IsCancellationRequested;
        }

        return results;
    }

    private static bool ReadAtmosFromMusicVideoPayload(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(DataField, out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty(AttributesField, out var attrs) || attrs.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var traits = AppleCatalogJsonHelper.ReadStringArray(attrs, AudioTraitsField);
            if (traits.Any(trait => trait.Contains(AtmosKeyword, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadVideoValue(Dictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return string.Empty;
        }

        return Convert.ToString(value) ?? string.Empty;
    }

    private static bool ReadVideoBool(Dictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return false;
        }

        return value switch
        {
            bool boolValue => boolValue,
            string text => bool.TryParse(text, out var parsed) && parsed,
            _ => false
        };
    }
}
