using System;
using System.IO;
using DeezSpoTag.Core.Enums;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Services.Download.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadUtilsTests
{
    [Theory]
    [InlineData(OverwriteOption.DontOverwrite)]
    [InlineData(OverwriteOption.DontCheckExt)]
    [InlineData(OverwriteOption.OnlyLowerBitrates)]
    public void CheckShouldDownload_DoesNotBlock_WhenStagingFileAlreadyExists(OverwriteOption overwriteOption)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var filename = "track";
            var filepath = root;
            var writepath = Path.Combine(root, "track.mp3");
            File.WriteAllText(Path.Combine(root, "track.flac"), "existing-staging-file");

            var track = new Track
            {
                Id = "1",
                Title = "Track",
                Bitrate = 3,
                Duration = 180
            };

            var shouldDownload = DownloadUtils.CheckShouldDownload(
                filename,
                filepath,
                ".mp3",
                writepath,
                overwriteOption,
                track);

            Assert.True(shouldDownload);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
