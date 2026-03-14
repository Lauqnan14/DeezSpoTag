using System;
using System.Linq;
using System.Text.Json;

namespace DeezSpoTag.Services.Download.Apple;

public sealed record AppleCatalogVideoAttributes
{
    public string Name { get; init; } = string.Empty;
    public string ArtistName { get; init; } = string.Empty;
    public string AlbumName { get; init; } = string.Empty;
    public string ReleaseDate { get; init; } = string.Empty;
    public string Isrc { get; init; } = string.Empty;
    public string ArtworkUrl { get; init; } = string.Empty;
    public int DurationSeconds { get; init; }
    public bool HasAtmos { get; init; }
    public bool Has4K { get; init; }
    public bool HasHdr { get; init; }
}

public static class AppleCatalogVideoAttributeParser
{
    private const string DataProperty = "data";
    private const string ArtworkProperty = "artwork";
    private const string ArtworkUrlProperty = "url";
    private const string DurationProperty = "durationInMillis";
    private const string AtmosQuality = "atmos";

    public static bool TryParse(JsonElement root, string attributesPropertyName, out AppleCatalogVideoAttributes attributes)
    {
        attributes = new AppleCatalogVideoAttributes();
        if (!root.TryGetProperty(DataProperty, out var dataArray)
            || dataArray.ValueKind != JsonValueKind.Array
            || dataArray.GetArrayLength() == 0)
        {
            return false;
        }

        var data = dataArray[0];
        if (!data.TryGetProperty(attributesPropertyName, out var attributeElement)
            || attributeElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        attributes = new AppleCatalogVideoAttributes
        {
            Name = GetText(attributeElement, "name"),
            ArtistName = GetText(attributeElement, "artistName"),
            AlbumName = GetText(attributeElement, "albumName"),
            ReleaseDate = GetText(attributeElement, "releaseDate"),
            Isrc = GetText(attributeElement, "isrc"),
            ArtworkUrl = ResolveArtwork(attributeElement, 1200),
            DurationSeconds = GetDurationSeconds(attributeElement),
            HasAtmos = HasAtmosTrait(attributeElement),
            Has4K = GetBool(attributeElement, "has4K"),
            HasHdr = GetBool(attributeElement, "hasHDR")
        };

        return true;
    }

    public static string ResolveArtwork(JsonElement attributes, int size)
    {
        if (!attributes.TryGetProperty(ArtworkProperty, out var artwork) || artwork.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (!artwork.TryGetProperty(ArtworkUrlProperty, out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var template = urlElement.GetString() ?? string.Empty;
        return template
            .Replace("{w}", size.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{h}", size.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetText(JsonElement attributes, string propertyName)
    {
        return attributes.TryGetProperty(propertyName, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static int GetDurationSeconds(JsonElement attributes)
    {
        return attributes.TryGetProperty(DurationProperty, out var durationElement)
            ? (int)Math.Round(durationElement.GetInt32() / 1000d)
            : 0;
    }

    private static bool HasAtmosTrait(JsonElement attributes)
    {
        return attributes.TryGetProperty("audioTraits", out var traits)
               && traits.ValueKind == JsonValueKind.Array
               && traits.EnumerateArray().Any(static trait =>
                   trait.ValueKind == JsonValueKind.String
                   && trait.GetString()?.IndexOf(AtmosQuality, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static bool GetBool(JsonElement attributes, string propertyName)
    {
        if (!attributes.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out var intValue) && intValue != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false
        };
    }
}
