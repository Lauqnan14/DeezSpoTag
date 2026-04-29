using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeezSpoTag.Web.Services;

public partial class AutoTagService
{
    private const string ShazamPlatformId = "shazam";
    private static readonly string[] ManualShazamBootstrapTags =
    {
        "title",
        AutoTagLiterals.ArtistTag,
        "album",
        "albumArt",
        "duration",
        "isrc",
        "trackId",
        "source",
        "url",
        AutoTagLiterals.ReleaseDateTag
    };

    private enum EnrichmentRunMode
    {
        Manual,
        AutomaticDownload
    }

    private sealed record EnrichmentStagePlan(
        IReadOnlyList<string> RequestedTags,
        IReadOnlyList<string> Platforms,
        string? ExcludedPlatform);

    private bool TryBuildEnrichmentStages(
        JsonObject baseRoot,
        Dictionary<string, PlatformTagCapabilities> platformCaps,
        IReadOnlyList<string> eligiblePlatforms,
        EnrichmentBuildContext context,
        out List<AutoTagStageConfig> stages,
        out string skipReason,
        out List<string> strippedKeys)
    {
        stages = new List<AutoTagStageConfig>();
        var mode = ResolveEnrichmentRunMode(context.RunIntent);
        if (mode == EnrichmentRunMode.AutomaticDownload)
        {
            if (!TryBuildAutomaticDownloadEnrichmentStage(baseRoot, platformCaps, eligiblePlatforms, context, out var downloadStage, out skipReason, out strippedKeys))
            {
                return false;
            }

            stages.Add(downloadStage);
            return true;
        }

        return TryBuildManualEnrichmentStages(baseRoot, platformCaps, eligiblePlatforms, context, out stages, out skipReason, out strippedKeys);
    }

    private bool TryBuildManualEnrichmentStages(
        JsonObject baseRoot,
        Dictionary<string, PlatformTagCapabilities> platformCaps,
        IReadOnlyList<string> eligiblePlatforms,
        EnrichmentBuildContext context,
        out List<AutoTagStageConfig> stages,
        out string skipReason,
        out List<string> strippedKeys)
    {
        stages = new List<AutoTagStageConfig>();
        strippedKeys = new List<string>();

        var plan = BuildManualEnrichmentStagePlan(baseRoot, eligiblePlatforms);
        if (!plan.Platforms.Any(platform => string.Equals(platform, ShazamPlatformId, StringComparison.OrdinalIgnoreCase)))
        {
            skipReason = "manual enrichment requires Shazam to be enabled";
            return false;
        }

        var shazamPlan = plan with
        {
            RequestedTags = plan.RequestedTags
                .Concat(ManualShazamBootstrapTags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Platforms = new[] { ShazamPlatformId }
        };
        if (!TryBuildEnrichmentStageFromPlan(
            baseRoot,
            platformCaps,
            context,
            shazamPlan,
            out var shazamStage,
            out skipReason,
            out var shazamStrippedKeys,
            forceShazamFingerprint: true))
        {
            skipReason = $"manual enrichment Shazam bootstrap failed: {skipReason}";
            return false;
        }

        strippedKeys.AddRange(shazamStrippedKeys);
        stages.Add(shazamStage);

        var remainingPlatforms = plan.Platforms
            .Where(platform => !string.Equals(platform, ShazamPlatformId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (remainingPlatforms.Count == 0)
        {
            skipReason = string.Empty;
            return true;
        }

        var remainingPlan = plan with
        {
            Platforms = remainingPlatforms
        };
        if (!TryBuildEnrichmentStageFromPlan(
            baseRoot,
            platformCaps,
            context,
            remainingPlan,
            out var remainingStage,
            out skipReason,
            out var remainingStrippedKeys))
        {
            skipReason = $"manual enrichment remaining platforms failed: {skipReason}";
            return false;
        }

        strippedKeys.AddRange(remainingStrippedKeys);
        stages.Add(remainingStage);
        skipReason = string.Empty;
        return true;
    }

    private bool TryBuildAutomaticDownloadEnrichmentStage(
        JsonObject baseRoot,
        Dictionary<string, PlatformTagCapabilities> platformCaps,
        IReadOnlyList<string> eligiblePlatforms,
        EnrichmentBuildContext context,
        out AutoTagStageConfig stage,
        out string skipReason,
        out List<string> strippedKeys)
    {
        return TryBuildEnrichmentStageFromPlan(
            baseRoot,
            platformCaps,
            context,
            BuildAutomaticDownloadEnrichmentStagePlan(baseRoot, eligiblePlatforms),
            out stage,
            out skipReason,
            out strippedKeys);
    }

    private static EnrichmentStagePlan BuildManualEnrichmentStagePlan(
        JsonObject baseRoot,
        IReadOnlyList<string> eligiblePlatforms)
    {
        return new EnrichmentStagePlan(
            RequestedTags: ResolveEnrichmentRequestedTags(baseRoot),
            Platforms: eligiblePlatforms.ToList(),
            ExcludedPlatform: null);
    }

    private static EnrichmentStagePlan BuildAutomaticDownloadEnrichmentStagePlan(
        JsonObject baseRoot,
        IReadOnlyList<string> eligiblePlatforms)
    {
        var excludedPlatform = ResolveDownloadSourcePlatform(baseRoot);
        var platforms = string.IsNullOrWhiteSpace(excludedPlatform)
            ? eligiblePlatforms.ToList()
            : eligiblePlatforms
                .Where(platform => !string.Equals(platform, excludedPlatform, StringComparison.OrdinalIgnoreCase))
                .ToList();
        return new EnrichmentStagePlan(
            RequestedTags: ResolveEnrichmentRequestedTags(baseRoot),
            Platforms: platforms,
            ExcludedPlatform: excludedPlatform);
    }

    private bool TryBuildEnrichmentStageFromPlan(
        JsonObject baseRoot,
        Dictionary<string, PlatformTagCapabilities> platformCaps,
        EnrichmentBuildContext context,
        EnrichmentStagePlan plan,
        out AutoTagStageConfig stage,
        out string skipReason,
        out List<string> strippedKeys,
        bool forceShazamFingerprint = false)
    {
        stage = null!;
        skipReason = "tags not configured";
        strippedKeys = new List<string>();

        if (plan.RequestedTags.Count == 0)
        {
            return false;
        }

        if (plan.Platforms.Count == 0)
        {
            skipReason = string.IsNullOrWhiteSpace(plan.ExcludedPlatform)
                ? "no eligible enrichment platforms enabled"
                : $"no enrichment platforms enabled after excluding download source ({plan.ExcludedPlatform})";
            return false;
        }

        var filtered = FilterSupportedTags(plan.RequestedTags, plan.Platforms, platformCaps);
        if (filtered.Count == 0)
        {
            skipReason = "no supported enrichment tags for enabled platforms";
            return false;
        }

        var stageRoot = CloneRoot(baseRoot);
        WriteStringList(stageRoot, "tags", filtered);
        WriteStringList(stageRoot, AutoTagLiterals.PlatformsKey, plan.Platforms);
        var platformCount = ReadStringList(stageRoot, AutoTagLiterals.PlatformsKey).Count;
        stageRoot[AutoTagLiterals.MultiPlatformKey] = platformCount > 1;
        if (forceShazamFingerprint)
        {
            ConfigureShazamFingerprintBootstrap(stageRoot);
        }

        strippedKeys = ApplyStageSchema(stageRoot, EnrichmentStageAllowedKeys);

        var configJson = stageRoot.ToJsonString(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        var configPath = WriteRuntimeConfigFile(context.JobId, AutoTagLiterals.EnrichmentStage, configJson);
        stage = new AutoTagStageConfig(
            AutoTagLiterals.EnrichmentStage,
            configPath,
            filtered.Count,
            ComputeConfigHash(configJson));
        return true;
    }

    private static void ConfigureShazamFingerprintBootstrap(JsonObject stageRoot)
    {
        stageRoot["enableShazam"] = true;
        stageRoot["forceShazam"] = true;

        if (stageRoot["custom"] is not JsonObject custom)
        {
            custom = new JsonObject();
            stageRoot["custom"] = custom;
        }

        custom[ShazamPlatformId] = new JsonObject
        {
            ["id_first"] = false,
            ["fingerprint_fallback"] = true,
            ["fallback_missing_core_tags"] = true,
            ["force_match"] = true,
            ["prefer_hq_artwork"] = true,
            ["include_album"] = true,
            ["include_genre"] = true,
            ["include_label"] = true,
            ["include_release_date"] = true
        };

        stageRoot["organizeSidecarsIntoTemplateFolders"] = true;
    }

    private static EnrichmentRunMode ResolveEnrichmentRunMode(string? runIntent)
    {
        return string.Equals(
            NormalizeRunIntent(runIntent),
            AutoTagLiterals.RunIntentDownloadEnrichment,
            StringComparison.OrdinalIgnoreCase)
                ? EnrichmentRunMode.AutomaticDownload
                : EnrichmentRunMode.Manual;
    }

    private static List<string> ResolveEnrichmentRequestedTags(JsonObject baseRoot)
    {
        return ReadStringList(baseRoot, "tags");
    }
}
