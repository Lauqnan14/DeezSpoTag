using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Collections.Generic;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AudioTaggerMp4CodecMarkerTest : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"deezspotag-audio-tagger-{Guid.NewGuid():N}");

    public AudioTaggerMp4CodecMarkerTest()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Theory]
    [InlineData("ec-3")]
    [InlineData("ac-3")]
    [InlineData("dec3")]
    [InlineData("dac3")]
    public void HasMp4AudioCodecMarker_DetectsAtmosCompatibleMp4Markers(string marker)
    {
        var path = Path.Combine(_tempDir, $"{marker}.m4a");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes($"ftypisom....moov....stsd....{marker}....mdat"));

        Assert.True(InvokeHasMp4AudioCodecMarker(path, marker));
    }

    [Fact]
    public void HasMp4AudioCodecMarker_ReturnsFalseForAacOnlyContainer()
    {
        var path = Path.Combine(_tempDir, "aac.m4a");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("ftypM4A ....moov....stsd....mp4a....mdat"));

        Assert.False(InvokeHasMp4AudioCodecMarker(path, "ec-3"));
    }

    [Theory]
    [InlineData("tidal", "0000")]
    [InlineData("apple", "0000")]
    [InlineData("amazon", "0000")]
    [InlineData("tidal", "0001")]
    [InlineData("apple", "invalid")]
    public void Mp4Persistence_DoesNotDemandAnUnavailableYear(string source, string year)
    {
        var track = new Track { Source = source, Date = new CustomDate("00", "00", year) };
        var tags = YearOnlyTags();
        var validators = GetValidators(track, tags);
        Assert.Empty(validators);
    }

    [Fact]
    public void Mp4Persistence_RequiresTheSuppliedYearRatherThanAnyPositiveYear()
    {
        var validators = GetValidators(new Track { Date = CustomDate.FromString("2022-04-22") }, YearOnlyTags());
        Assert.Single(validators);
        var file = new ATL.Track(Path.Combine(_tempDir, "year.m4a"));
        file.Year = 2021;
        Assert.False(validators[0](file));
        file.Year = 2022;
        Assert.True(validators[0](file));
    }

    private static TagSettings YearOnlyTags()
    {
        var tags = new TagSettings();
        foreach (var property in typeof(TagSettings).GetProperties())
            if (property.PropertyType == typeof(bool) && property.CanWrite) property.SetValue(tags, false);
        tags.Year = true;
        return tags;
    }

    private static List<Func<ATL.Track, bool>> GetValidators(Track track, TagSettings tags)
        => (List<Func<ATL.Track, bool>>)typeof(AudioTagger)
            .GetMethod("BuildMp4PersistenceValidators", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { track, tags })!;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception)
        {
            // Best effort cleanup for temp files created by the test.
        }
    }

    private static bool InvokeHasMp4AudioCodecMarker(string path, string marker)
    {
        var method = typeof(AudioTagger).GetMethod(
            "HasMp4AudioCodecMarker",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var result = method!.Invoke(null, [path, new[] { Encoding.ASCII.GetBytes(marker) }]);
        Assert.IsType<bool>(result);
        return (bool)result;
    }
}
