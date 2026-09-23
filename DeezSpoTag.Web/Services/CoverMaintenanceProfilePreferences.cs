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

        ApplyInt(configRoot, "embeddedArtworkSize", value => settings.EmbeddedArtworkSize = value);
        ApplyInt(configRoot, "localArtworkSize", value => settings.LocalArtworkSize = value);
        ApplyInt(configRoot, "appleArtworkSize", value => settings.AppleArtworkSize = value);
        if (configRoot?["appleArtworkSizeText"]?.GetValue<string>() is { } appleSizeText
            && !string.IsNullOrWhiteSpace(appleSizeText))
        {
            settings.AppleArtworkSizeText = appleSizeText.Trim();
        }
        if (configRoot?["embedMaxQualityCover"]?.GetValue<bool?>() is bool maxQuality)
        {
            settings.EmbedMaxQualityCover = maxQuality;
        }
        ApplyInt(configRoot, "jpegImageQuality", value => settings.JpegImageQuality = value, 1, 100);
        settings.LocalArtworkFormat = DeezSpoTag.Services.Download.Shared.ArtworkFormatPolicy.Normalize(settings.LocalArtworkFormat);
    }

    private static void ApplyInt(
        JsonObject? root,
        string key,
        Action<int> apply,
        int minimum = 100,
        int maximum = 5000)
    {
        if (root?[key]?.GetValue<int?>() is int value)
        {
            apply(Math.Clamp(value, minimum, maximum));
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
