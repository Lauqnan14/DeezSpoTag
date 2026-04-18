using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadTagSettingsResolverRuntimeOverrideParsingTests
{
    private static readonly MethodInfo ExtractRuntimeOverridesMethod =
        typeof(DownloadTagSettingsResolver).GetMethod(
            "ExtractRuntimeOverrides",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DownloadTagSettingsResolver.ExtractRuntimeOverrides not found.");

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

    private static DownloadProfileRuntimeOverrides? ExtractRuntimeOverrides(AutoTagSettings autoTag)
    {
        return ExtractRuntimeOverridesMethod.Invoke(null, new object?[] { autoTag }) as DownloadProfileRuntimeOverrides;
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
