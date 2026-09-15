using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Behavioural coverage of the mashup decision in the missing-metadata audit: a candidate that the
/// fingerprint → platform chain confidently identifies is repaired like any other file, and only a
/// candidate that stays unidentified is counted unofficial and left alone.
/// </summary>
public sealed class MashupIdentityPartitionTest
{
    private static readonly MissingCoreMetadataFileDto MashupCandidate = new(
        TrackId: 1,
        AudioFileId: 1,
        FolderId: 1,
        FilePath: "/music/Artist/Album/Song A vs Song B.flac",
        MissingFields: ["Title"],
        RepairScore: 10,
        Title: "Song A vs Song B",
        Artist: "Artist");

    private static readonly MissingCoreMetadataFileDto OrdinaryFile = new(
        TrackId: 2,
        AudioFileId: 2,
        FolderId: 1,
        FilePath: "/music/Artist/Album/Ordinary Song.flac",
        MissingFields: ["Title"],
        RepairScore: 10,
        Title: "Ordinary Song",
        Artist: "Artist");

    [Fact]
    public async Task TaggableMashup_IsIdentified_AndRepaired()
    {
        var (repairable, unofficial) = await AutoTagService.PartitionUnofficialMashupsAsync(
            [MashupCandidate, OrdinaryFile],
            (_, _) => Task.FromResult(true),
            CancellationToken.None);

        Assert.Equal(0, unofficial);
        Assert.Equal(2, repairable.Count);
        Assert.Contains(repairable, file => file.FilePath == MashupCandidate.FilePath);
    }

    [Fact]
    public async Task UnidentifiableMashup_IsCountedUnofficial_AndLeftAlone()
    {
        var (repairable, unofficial) = await AutoTagService.PartitionUnofficialMashupsAsync(
            [MashupCandidate, OrdinaryFile],
            (_, _) => Task.FromResult(false),
            CancellationToken.None);

        Assert.Equal(1, unofficial);
        Assert.Single(repairable);
        Assert.Equal(OrdinaryFile.FilePath, repairable[0].FilePath);
        Assert.DoesNotContain(repairable, file => file.FilePath == MashupCandidate.FilePath);
    }

    [Fact]
    public async Task MashupTitleInTheTag_IsClassifiedEvenWhenTheFileNameIsPlain()
    {
        var tagged = MashupCandidate with { FilePath = "/music/Artist/Album/01 Track.flac" };
        var (repairable, unofficial) = await AutoTagService.PartitionUnofficialMashupsAsync(
            [tagged],
            (_, _) => Task.FromResult(false),
            CancellationToken.None);

        Assert.Equal(1, unofficial);
        Assert.Empty(repairable);
    }

    [Fact]
    public async Task NonMashupFiles_NeverReachTheIdentificationProbe()
    {
        var probed = false;
        var (repairable, unofficial) = await AutoTagService.PartitionUnofficialMashupsAsync(
            [OrdinaryFile],
            (_, _) =>
            {
                probed = true;
                return Task.FromResult(false);
            },
            CancellationToken.None);

        Assert.False(probed);
        Assert.Equal(0, unofficial);
        Assert.Single(repairable);
    }
}
