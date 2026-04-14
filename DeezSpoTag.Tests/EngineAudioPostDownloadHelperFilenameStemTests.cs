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
