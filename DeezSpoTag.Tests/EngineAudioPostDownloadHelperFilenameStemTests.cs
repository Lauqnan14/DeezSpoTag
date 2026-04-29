using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EngineAudioPostDownloadHelperFilenameStemTests
{
    [Fact]
    public void GeneratePaths_UsesTrackTemplateForAlbumDownloads()
    {
        var settings = CreateSettings();
        settings.TracknameTemplate = "%artist% - %title%";
        settings.AlbumTracknameTemplate = "%tracknumber% - %title%";

        var artist = new DeezSpoTag.Core.Models.Artist("Cardi B");
        var track = new DeezSpoTag.Core.Models.Track
        {
            Title = "I Like It",
            MainArtist = artist,
            TrackNumber = 1,
            Album = new DeezSpoTag.Core.Models.Album("Invasion of Privacy")
            {
                MainArtist = artist,
                TrackTotal = 13
            }
        };

        var processor = new EnhancedPathTemplateProcessor(NullLogger<EnhancedPathTemplateProcessor>.Instance);
        var result = processor.GeneratePaths(track, "album", settings);

        Assert.Equal("Cardi B - I Like It", result.Filename);
    }

    [Fact]
    public void GeneratePaths_DoesNotStackArtistPrefix_WhenTemplateIsReapplied()
    {
        var settings = CreateSettings();
        settings.TracknameTemplate = "%artist% - %title%";

        var artist = new DeezSpoTag.Core.Models.Artist("Cardi B");
        var track = new DeezSpoTag.Core.Models.Track
        {
            Title = "Cardi B - I Like It",
            MainArtist = artist,
            TrackNumber = 1,
            Album = new DeezSpoTag.Core.Models.Album("Invasion of Privacy")
            {
                MainArtist = artist,
                TrackTotal = 13
            }
        };

        var processor = new EnhancedPathTemplateProcessor(NullLogger<EnhancedPathTemplateProcessor>.Instance);
        var result = processor.GeneratePaths(track, "track", settings);

        Assert.Equal("Cardi B - I Like It", result.Filename);
    }

    [Fact]
    public void BuildTrackContext_DoesNotTrimDottedTitleAsFileExtension()
    {
        var settings = CreateSettings();
        var processor = new EnhancedPathTemplateProcessor(NullLogger<EnhancedPathTemplateProcessor>.Instance);
        var payload = new TidalQueueItem
        {
            CollectionType = "track",
            Title = "Mr. Brightside",
            Artist = "The Killers",
            Album = "Hot Fuss",
            TrackNumber = 1,
            Position = 1
        };

        var context = EngineAudioPostDownloadHelper.BuildTrackContext(
            payload,
            settings,
            processor,
            "tidal",
            payload.TidalId);

        Assert.Equal("literal:The Killers - Mr. Brightside", context.FilenameFormat);
    }

    [Fact]
    public void BuildTrackContext_DoesNotTrimFeaturedTokenAfterDot()
    {
        var settings = CreateSettings();
        settings.FeaturedToTitle = "2";
        var processor = new EnhancedPathTemplateProcessor(NullLogger<EnhancedPathTemplateProcessor>.Instance);
        var payload = new TidalQueueItem
        {
            CollectionType = "track",
            Title = "Dah! (feat. Alikiba)",
            Artist = "Nandy",
            Album = "Dah! (feat. Alikiba)",
            TrackNumber = 1,
            Position = 1
        };

        var context = EngineAudioPostDownloadHelper.BuildTrackContext(
            payload,
            settings,
            processor,
            "tidal",
            payload.TidalId);

        Assert.Equal("literal:Nandy - Dah! (feat. Alikiba)", context.FilenameFormat);
    }

    private static DeezSpoTagSettings CreateSettings()
    {
        return new DeezSpoTagSettings
        {
            TracknameTemplate = "%artist% - %title%",
            CreatePlaylistFolder = false,
            CreateArtistFolder = false,
            CreateAlbumFolder = false,
            CreateSingleFolder = false,
            Tags = new TagSettings
            {
                SingleAlbumArtist = true,
                MultiArtistSeparator = "default"
            }
        };
    }
}
