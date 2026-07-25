using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EnhancementAlbumBoundaryBatchTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "deezspotag-batch-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void BuildAlbumBoundaryBatches_ExpandsBoundaryToFinishAlbum()
    {
        var files = BuildAlbum("album-a", 37)
            .Concat(BuildAlbum("album-b", 8))
            .Concat(BuildAlbum("album-c", 3))
            .ToList();

        var batches = AutoTagService.BuildAlbumBoundaryBatches(files, static path => path);

        Assert.Equal(2, batches.Count);
        Assert.Equal(45, batches[0].Count);
        Assert.Equal(3, batches[1].Count);
        Assert.DoesNotContain(batches[1], path => path.Contains("album-b", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildAlbumBoundaryBatches_AllowsSingleAlbumToExceedLimit()
    {
        var files = BuildAlbum("large-album", 52).ToList();

        var batches = AutoTagService.BuildAlbumBoundaryBatches(files, static path => path);

        var batch = Assert.Single(batches);
        Assert.Equal(52, batch.Count);
    }

    [Fact]
    public void BuildAlbumBoundaryBatches_DoesNotMergeSameAlbumAcrossItsOriginalPosition()
    {
        var files = BuildAlbum("album-a", 20)
            .Concat(BuildAlbum("album-b", 20))
            .Concat(BuildAlbum("album-c", 5))
            .ToList();

        var batches = AutoTagService.BuildAlbumBoundaryBatches(files, static path => path);

        Assert.Equal(2, batches.Count);
        Assert.Equal(40, batches[0].Count);
        Assert.Equal(5, batches[1].Count);
    }

    private IEnumerable<string> BuildAlbum(string album, int count)
        => Enumerable.Range(1, count)
            .Select(index => Path.Join(_root, album, $"{index:D2}.flac"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
