using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;
public sealed class ArtistMetadataEnrichmentSettingsTest
{
    [Fact]
    public void OldProfilePreservesSavedLanguageAndUnrelatedValues()
    {
        var profile = new TaggingProfile
        {
            AutoTag = new AutoTagSettings { Data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["tags"] = JsonSerializer.SerializeToElement(new[] { "language", "title" }),
                ["gapFillTags"] = JsonSerializer.SerializeToElement(new[] { "language", "title" }),
                ["overwriteTags"] = JsonSerializer.SerializeToElement(new[] { "language" }),
                ["testPreference"] = JsonSerializer.SerializeToElement("retain")
            } }
        };
        var loaded = JsonSerializer.Deserialize<TaggingProfile>(JsonSerializer.Serialize(profile))!;
        TaggingProfileCanonicalizer.Canonicalize(loaded, seedFromTagConfigWhenMissing: true);
        Assert.Equal(TagSource.AutoTagPlatform, loaded.TagConfig.Language);
        Assert.Equal(TagSource.None, loaded.TagConfig.ArtistLanguage);
        Assert.Equal(TagSource.None, loaded.TagConfig.ArtistCountry);
        Assert.Equal("retain", loaded.AutoTag.Data["testPreference"].GetString());
        Assert.Equal(new[] { "language" }, loaded.AutoTag.Data["overwriteTags"].EnumerateArray().Select(x => x.GetString()));
    }

    [Theory]
    [InlineData("ArtistCountry")]
    [InlineData("ArtistCity")]
    [InlineData("ArtistRegion")]
    [InlineData("ArtistLanguage")]
    public void NewFieldsDefaultOffAndRoundTripIndependently(string name)
    {
        var settings = new TagSettings();
        var property = typeof(TagSettings).GetProperty(name);
        Assert.NotNull(property);
        Assert.False((bool)property!.GetValue(settings)!);
        property.SetValue(settings, true);
        var loaded = JsonSerializer.Deserialize<TagSettings>(JsonSerializer.Serialize(settings))!;
        Assert.True((bool)property.GetValue(loaded)!);
        foreach (var other in new[] { "ArtistCountry", "ArtistCity", "ArtistRegion", "ArtistLanguage" }.Where(x => x != name))
            Assert.False((bool)typeof(TagSettings).GetProperty(other)!.GetValue(loaded)!);
        Assert.Equal(TagSource.None, typeof(UnifiedTagConfig).GetProperty(name)!.GetValue(new UnifiedTagConfig()));
        Assert.True(Enum.TryParse<SupportedTag>(name, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothStageBuildersRetainCommonFieldAndOverwriteSelections(bool enhancement)
    {
        var directory = Path.Combine(Path.GetTempPath(), "enrichment-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var service = (AutoTagService)RuntimeHelpers.GetUninitializedObject(typeof(AutoTagService));
            typeof(AutoTagService).GetField("_runtimeConfigDir", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, directory);
            var root = new JsonObject
            {
                ["tags"] = new JsonArray("artistCountry", "artistCity", "artistLanguage", "language"),
                ["gapFillTags"] = new JsonArray("artistCountry", "artistCity", "artistLanguage", "language"),
                ["overwriteTags"] = new JsonArray("artistCity", "language"),
                ["targetFiles"] = new JsonArray(Path.Join(directory, "track.flac"))
            };
            var type = typeof(AutoTagService);
            var capsType = type.GetNestedType("PlatformTagCapabilities", BindingFlags.NonPublic)!;
            var caps = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), capsType))!;
            var cap = Activator.CreateInstance(capsType, new object[] { new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "language" }, false })!;
            caps.GetType().GetMethod("Add")!.Invoke(caps, new[] { "boomplay", cap });
            var contextType = type.GetNestedType(enhancement ? "EnhancementBuildContext" : "EnrichmentBuildContext", BindingFlags.NonPublic)!;
            var context = Activator.CreateInstance(contextType, new object[] { enhancement ? "enhancement_only" : "manual_enrichment", "test" })!;
            var method = type.GetMethod(enhancement ? "TryBuildEnhancementStage" : "TryBuildEnrichmentStages", BindingFlags.NonPublic | BindingFlags.Instance)!;
            object?[] args = { root, caps, new[] { "boomplay" }, context, null, null, null };
            Assert.True((bool)method.Invoke(service, args)!);
            var stages = enhancement ? new[] { args[4]! } : ((IEnumerable)args[4]!).Cast<object>();
            foreach (var stage in stages)
            {
                var path = (string)stage.GetType().GetProperty("ConfigPath")!.GetValue(stage)!;
                var config = JsonNode.Parse(File.ReadAllText(path))!;
                Assert.Contains("artistCountry", config["tags"]!.AsArray().Select(x => x!.GetValue<string>()));
                Assert.Contains("artistLanguage", config["tags"]!.AsArray().Select(x => x!.GetValue<string>()));
                Assert.True(JsonNode.DeepEquals(root["overwriteTags"], config["overwriteTags"]));
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
