using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadTagSettingsResolverRuntimeOverrideParsingTests
{
    private static readonly Dictionary<string, string> TagSettingsPropertyMap = new(StringComparer.Ordinal)
    {
        ["Duration"] = nameof(TagSettings.Length),
        ["UnsyncedLyrics"] = nameof(TagSettings.Lyrics)
    };

    private static readonly MethodInfo ExtractDownloadTagSourceMethod =
        typeof(DownloadTagSettingsResolver).GetMethod(
            "ExtractDownloadTagSource",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DownloadTagSettingsResolver.ExtractDownloadTagSource not found.");
    private static readonly MethodInfo ExtractRuntimeOverridesMethod =
        typeof(DownloadTagSettingsResolver).GetMethod(
            "ExtractRuntimeOverrides",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DownloadTagSettingsResolver.ExtractRuntimeOverrides not found.");
    private static readonly MethodInfo IsDownloadTagSelectionEmptyMethod =
        typeof(DownloadTagSettingsResolver).GetMethod(
            "IsDownloadTagSelectionEmpty",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DownloadTagSettingsResolver.IsDownloadTagSelectionEmpty not found.");

    [Fact]
    public void DownloadTagSettingsConverter_MapsEveryUnifiedTagSourceToRuntimeTagSettings()
    {
        var converter = new DownloadTagSettingsConverter();
        var tagSourceProperties = typeof(UnifiedTagConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(TagSource))
            .ToArray();

        foreach (var property in tagSourceProperties)
        {
            var config = new UnifiedTagConfig();
            foreach (var resetProperty in tagSourceProperties)
            {
                resetProperty.SetValue(config, TagSource.AutoTagPlatform);
            }

            property.SetValue(config, TagSource.DownloadSource);

            var settings = converter.ToTagSettings(config, new TechnicalTagSettings { EmbedLyrics = true });
            var settingsPropertyName = TagSettingsPropertyMap.GetValueOrDefault(property.Name, property.Name);
            var settingsProperty = typeof(TagSettings).GetProperty(settingsPropertyName);

            Assert.NotNull(settingsProperty);
            Assert.True((bool)settingsProperty.GetValue(settings)!);
            Assert.False(IsDownloadTagSelectionEmpty(settings));
        }
    }

    [Fact]
    public void ExtractRuntimeOverrides_ParsesCaseInsensitiveValues_WhenExactKeyIsMissing()
    {
        var autoTag = CreateAutoTagSettings(
            ("TRACKNAMETEMPLATE", "  {artist} - {title}  "),
            ("SAVEARTWORK", "true"),
            ("JPEGIMAGEQUALITY", "85"));

        var runtimeOverrides = ExtractRuntimeOverrides(autoTag);

        Assert.NotNull(runtimeOverrides);
        Assert.Equal("{artist} - {title}", runtimeOverrides.TracknameTemplate);
        Assert.True(runtimeOverrides.SaveArtwork);
        Assert.Equal(85, runtimeOverrides.JpegImageQuality);
    }

    [Fact]
    public void ExtractRuntimeOverrides_PrefersExactKeyMatch_WhenBothExactAndCaseInsensitiveKeysExist()
    {
        var autoTag = CreateAutoTagSettings(
            ("saveArtwork", false),
            ("SAVEARTWORK", "true"));

        var runtimeOverrides = ExtractRuntimeOverrides(autoTag);

        Assert.NotNull(runtimeOverrides);
        Assert.False(runtimeOverrides.SaveArtwork);
    }

    [Fact]
    public void ExtractRuntimeOverrides_ReturnsNull_WhenNoRuntimeOverrideHasValidValue()
    {
        var autoTag = CreateAutoTagSettings(
            ("tracknameTemplate", "   "),
            ("saveArtwork", "not-a-bool"),
            ("jpegImageQuality", "NaN"));

        var runtimeOverrides = ExtractRuntimeOverrides(autoTag);

        Assert.Null(runtimeOverrides);
    }

    [Fact]
    public void ExtractDownloadTagSource_DefaultsToDeezer_WhenAutoTagDataIsMissing()
    {
        var autoTag = new AutoTagSettings();
        typeof(AutoTagSettings).GetProperty(nameof(AutoTagSettings.Data))!.SetValue(autoTag, null);

        var source = ExtractDownloadTagSource(autoTag);

        Assert.Equal(DownloadTagSourceHelper.DeezerSource, source);
    }

    [Fact]
    public void ExtractDownloadTagSource_DefaultsToDeezer_WhenDownloadTagSourceKeyIsMissing()
    {
        var source = ExtractDownloadTagSource(CreateAutoTagSettings(("saveArtwork", true)));

        Assert.Equal(DownloadTagSourceHelper.DeezerSource, source);
    }

    private static DownloadProfileRuntimeOverrides? ExtractRuntimeOverrides(AutoTagSettings autoTag)
    {
        return ExtractRuntimeOverridesMethod.Invoke(null, new object?[] { autoTag }) as DownloadProfileRuntimeOverrides;
    }

    private static string? ExtractDownloadTagSource(AutoTagSettings autoTag)
    {
        return ExtractDownloadTagSourceMethod.Invoke(null, new object?[] { autoTag }) as string;
    }

    private static bool IsDownloadTagSelectionEmpty(TagSettings settings)
    {
        return (bool)IsDownloadTagSelectionEmptyMethod.Invoke(null, new object?[] { settings })!;
    }

    private static AutoTagSettings CreateAutoTagSettings(params (string Key, object? Value)[] entries)
    {
        var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            data[key] = ToJsonElement(value);
        }

        return new AutoTagSettings
        {
            Data = data
        };
    }

    private static JsonElement ToJsonElement(object? value)
    {
        var json = JsonSerializer.Serialize(value);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
