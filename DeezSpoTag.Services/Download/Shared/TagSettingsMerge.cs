using DeezSpoTag.Core.Models.Settings;
using System.Reflection;

namespace DeezSpoTag.Services.Download.Shared;

public static class TagSettingsMerge
{
    private static readonly PropertyInfo[] TagProperties = typeof(TagSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance);

    private static readonly HashSet<string> TechnicalProperties = new(StringComparer.Ordinal)
    {
        nameof(TagSettings.SavePlaylistAsCompilation),
        nameof(TagSettings.UseNullSeparator),
        nameof(TagSettings.SaveID3v1),
        nameof(TagSettings.MultiArtistSeparator),
        nameof(TagSettings.SingleAlbumArtist),
        nameof(TagSettings.CoverDescriptionUTF8)
    };

    public static TagSettings OverlayEnabledTags(TagSettings? baseline, TagSettings? profile)
    {
        var merged = Clone(baseline ?? new TagSettings());
        if (profile == null)
        {
            return merged;
        }

        foreach (var property in TagProperties)
        {
            if (!property.CanRead || !property.CanWrite)
            {
                continue;
            }

            if (TechnicalProperties.Contains(property.Name))
            {
                ApplyTechnicalProperty(merged, profile, property);
                continue;
            }

            ApplyBooleanProperty(merged, profile, property);
        }

        return merged;
    }

    public static TagSettings UseProfileOnly(TagSettings? profile)
    {
        return Clone(profile ?? new TagSettings());
    }

    private static TagSettings Clone(TagSettings source)
    {
        var copy = new TagSettings();
        foreach (var property in TagProperties)
        {
            if (!property.CanRead || !property.CanWrite)
            {
                continue;
            }

            property.SetValue(copy, property.GetValue(source));
        }

        return copy;
    }

    private static void ApplyTechnicalProperty(TagSettings merged, TagSettings profile, PropertyInfo property)
    {
        var technicalValue = property.GetValue(profile);
        if (property.PropertyType == typeof(string))
        {
            var text = technicalValue as string;
            if (!string.IsNullOrWhiteSpace(text))
            {
                property.SetValue(merged, text);
            }

            return;
        }

        if (technicalValue != null)
        {
            property.SetValue(merged, technicalValue);
        }
    }

    private static void ApplyBooleanProperty(TagSettings merged, TagSettings profile, PropertyInfo property)
    {
        if (property.PropertyType == typeof(bool) && (bool?)property.GetValue(profile) == true)
        {
            property.SetValue(merged, true);
        }
    }
}
