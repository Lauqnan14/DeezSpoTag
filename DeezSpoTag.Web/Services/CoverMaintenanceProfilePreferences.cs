using System.Text.Json.Nodes;
using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Web.Services;

internal static class CoverMaintenanceProfilePreferences
{
    public static bool WantsArtworkSidecar(JsonObject? configRoot, DeezSpoTagSettings settings)
    {
        if (configRoot?["saveArtwork"]?.GetValue<bool?>() is bool saveArtwork)
        {
            return saveArtwork;
        }

        return settings.SaveArtwork;
    }

    public static bool WantsEmbeddedCover(JsonObject? configRoot, DeezSpoTagSettings settings)
    {
        if (configRoot?["tags"] is JsonArray tagList)
        {
            var hasAnyTags = false;
            foreach (var entry in tagList)
            {
                var value = entry?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                hasAnyTags = true;
                if (string.Equals(value, "cover", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "albumArt", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (hasAnyTags)
            {
                return false;
            }
        }

        if (TryReadTagPreference(configRoot, "cover", out var cover))
        {
            return cover;
        }

        if (TryReadTagPreference(configRoot, "albumArt", out var albumArt))
        {
            return albumArt;
        }

        return settings.Tags?.Cover != false;
    }

    public static void ApplyToSettings(JsonObject? configRoot, DeezSpoTagSettings settings)
    {
        settings.SaveArtwork = WantsArtworkSidecar(configRoot, settings);
        settings.Tags ??= new TagSettings();
        settings.Tags.Cover = WantsEmbeddedCover(configRoot, settings);

        var coverImageTemplate = configRoot?["coverImageTemplate"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(coverImageTemplate))
        {
            settings.CoverImageTemplate = coverImageTemplate.Trim();
        }

        var squareName = configRoot?["animatedArtworkSquareFileName"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(squareName))
        {
            settings.AnimatedArtworkSquareFileName = squareName.Trim();
        }

        var tallName = configRoot?["animatedArtworkTallFileName"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(tallName))
        {
            settings.AnimatedArtworkTallFileName = tallName.Trim();
        }

        var localArtworkFormat = configRoot?["localArtworkFormat"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(localArtworkFormat))
        {
            settings.LocalArtworkFormat = localArtworkFormat.Trim();
        }

        var animatedFormats = configRoot?["animatedArtworkFormats"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(animatedFormats))
        {
            settings.AnimatedArtworkFormats = animatedFormats.Trim();
        }

        if (configRoot?["saveArtworkArtist"]?.GetValue<bool?>() is bool saveArtworkArtist)
        {
            settings.SaveArtworkArtist = saveArtworkArtist;
        }
    }

    private static bool TryReadTagPreference(JsonObject? configRoot, string tagName, out bool enabled)
    {
        enabled = false;
        var tagsNode = configRoot?["tags"];
        if (tagsNode is JsonArray tagList)
        {
            var hasAnyTags = false;
            foreach (var entry in tagList)
            {
                var value = entry?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                hasAnyTags = true;
                if (string.Equals(value, tagName, StringComparison.OrdinalIgnoreCase))
                {
                    enabled = true;
                    return true;
                }
            }

            if (hasAnyTags)
            {
                enabled = false;
                return true;
            }

            return false;
        }

        if (tagsNode is JsonObject tags
            && tags[tagName]?.GetValue<bool?>() is bool flagged)
        {
            enabled = flagged;
            return true;
        }

        return false;
    }
}
