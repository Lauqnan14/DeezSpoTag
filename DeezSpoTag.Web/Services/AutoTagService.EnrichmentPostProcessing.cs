using DeezSpoTag.Web.Services.CoverPort;

namespace DeezSpoTag.Web.Services;

public partial class AutoTagService
{
    private async Task RunManualEnrichmentArtworkMaintenanceAsync(
        AutoTagJob job,
        string rootPath,
        bool includesEnrichmentStage,
        bool includesEnhancementStage,
        AutoTagMoveSummary autoMoveSummary,
        CancellationToken cancellationToken)
    {
        if (!ShouldRunManualEnrichmentArtworkMaintenance(job, includesEnrichmentStage, includesEnhancementStage))
        {
            return;
        }

        var rootPaths = ResolveManualEnrichmentArtworkRoots(rootPath, autoMoveSummary);
        if (rootPaths.Count == 0)
        {
            AppendLog(job, "manual enrichment artwork skipped: no existing organized roots.");
            return;
        }

        var settings = _settingsService.LoadSettings();
        if (!settings.SaveArtwork && !settings.EmbedMaxQualityCover)
        {
            AppendLog(job, "manual enrichment artwork skipped: artwork saving disabled.");
            return;
        }

        var request = new CoverLibraryMaintenanceRequest(
            RootPaths: rootPaths,
            IncludeSubfolders: true,
            WorkerCount: 8,
            UpgradeLowResolutionCovers: false,
            MinResolution: 500,
            TargetResolution: 1200,
            SizeTolerancePercent: 25,
            PreserveSourceFormat: false,
            ReplaceMissingEmbeddedCovers: settings.EmbedMaxQualityCover,
            SyncExternalCovers: settings.SaveArtwork,
            QueueAnimatedArtwork: settings.SaveArtwork && settings.SaveAnimatedArtwork,
            AppleStorefront: string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront,
            AnimatedArtworkMaxResolution: settings.Video?.AppleMusicVideoMaxResolution ?? 2160,
            EnabledSources: null,
            CoverImageTemplate: settings.CoverImageTemplate);

        AppendLog(job, $"manual enrichment artwork starting ({rootPaths.Count} root(s)).");
        var result = await _coverMaintenanceService.RunAsync(request, cancellationToken);
        AppendLog(job, $"manual enrichment artwork finished ({result.Message})");
    }

    private static bool ShouldRunManualEnrichmentArtworkMaintenance(
        AutoTagJob job,
        bool includesEnrichmentStage,
        bool includesEnhancementStage)
    {
        return includesEnrichmentStage
            && !includesEnhancementStage
            && string.Equals(job.Trigger, AutoTagLiterals.ManualTrigger, StringComparison.OrdinalIgnoreCase)
            && string.Equals(job.Status, AutoTagLiterals.CompletedStatus, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                NormalizeRunIntent(job.RunIntent),
                AutoTagLiterals.RunIntentDownloadEnrichment,
                StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ResolveManualEnrichmentArtworkRoots(
        string rootPath,
        AutoTagMoveSummary autoMoveSummary)
    {
        var roots = autoMoveSummary.DestinationRoots.Count > 0
            ? autoMoveSummary.DestinationRoots
            : new List<string> { rootPath };

        return roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
