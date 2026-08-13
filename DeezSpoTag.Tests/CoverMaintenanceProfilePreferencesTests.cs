using System.Text.Json.Nodes;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class CoverMaintenanceProfilePreferencesTests
{
    [Fact]
    public void WantsArtworkSidecar_UsesProfileSaveArtworkFlag()
    {
        var settings = new DeezSpoTagSettings { SaveArtwork = true };
        var disabled = JsonNode.Parse("""{"saveArtwork": false}""")!.AsObject();
        var enabled = JsonNode.Parse("""{"saveArtwork": true}""")!.AsObject();

        Assert.False(CoverMaintenanceProfilePreferences.WantsArtworkSidecar(disabled, settings));
        Assert.True(CoverMaintenanceProfilePreferences.WantsArtworkSidecar(enabled, settings));
        Assert.True(CoverMaintenanceProfilePreferences.WantsArtworkSidecar(new JsonObject(), settings));
    }

    [Fact]
    public void WantsEmbeddedCover_UsesAlbumArtTagList()
    {
        var settings = new DeezSpoTagSettings { Tags = new TagSettings { Cover = true } };
        var withArt = JsonNode.Parse("""{"tags": ["title", "albumArt"]}""")!.AsObject();
        var withoutArt = JsonNode.Parse("""{"tags": ["title", "artist"]}""")!.AsObject();

        Assert.True(CoverMaintenanceProfilePreferences.WantsEmbeddedCover(withArt, settings));
        Assert.False(CoverMaintenanceProfilePreferences.WantsEmbeddedCover(withoutArt, settings));
    }

    [Fact]
    public void ApplyToSettings_CopiesArtworkPreferencesOntoSettings()
    {
        var settings = new DeezSpoTagSettings
        {
            SaveArtwork = true,
            Tags = new TagSettings { Cover = true },
            CoverImageTemplate = "cover",
            LocalArtworkFormat = "jpg"
        };
        var config = JsonNode.Parse("""
            {
              "saveArtwork": false,
              "tags": ["title"],
              "coverImageTemplate": "%album%",
              "animatedArtworkSquareFileName": "motion",
              "animatedArtworkTallFileName": "motion_portrait",
              "localArtworkFormat": "png"
            }
            """)!.AsObject();

        CoverMaintenanceProfilePreferences.ApplyToSettings(config, settings);

        Assert.False(settings.SaveArtwork);
        Assert.False(settings.Tags.Cover);
        Assert.Equal("%album%", settings.CoverImageTemplate);
        Assert.Equal("motion", settings.AnimatedArtworkSquareFileName);
        Assert.Equal("motion_portrait", settings.AnimatedArtworkTallFileName);
        Assert.Equal("png", settings.LocalArtworkFormat);
    }
}
