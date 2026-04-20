using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EngineAudioPostDownloadHelperLyricsTests
{
    private static MethodInfo GetStaticMethod(string name)
    {
        return typeof(EngineAudioPostDownloadHelper).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"EngineAudioPostDownloadHelper.{name} not found.");
    }

    private static T InvokeStatic<T>(string methodName, params object?[] args)
    {
        return (T)(GetStaticMethod(methodName).Invoke(null, args)
            ?? throw new InvalidOperationException($"EngineAudioPostDownloadHelper.{methodName} returned null."));
    }

    private static T? InvokeStaticNullable<T>(string methodName, params object?[] args) where T : class
    {
        return GetStaticMethod(methodName).Invoke(null, args) as T;
    }

    [Fact]
    public void NormalizeLrcLines_FiltersInvalidAndDuplicateLines()
    {
        var lines = new[]
        {
            "[00:01.00]First line",
            "plain text",
            "   [00:01.00]First line  ",
            "[00:02.50]Second line",
            ""
        };

        var normalized = InvokeStatic<List<string>>("NormalizeLrcLines", (object)lines);

        Assert.Equal(2, normalized.Count);
        Assert.Equal("[00:01.00]First line", normalized[0]);
        Assert.Equal("[00:02.50]Second line", normalized[1]);
    }

    [Fact]
    public void ToSyncLyricOrNull_ParsesTimestampAndText()
    {
        var line = "[03:05.25]  Hello world  ";

        var syncLyric = InvokeStatic<SyncLyric?>("ToSyncLyricOrNull", line);

        Assert.NotNull(syncLyric);
        Assert.Equal((3 * 60 * 1000) + (5 * 1000) + 250, syncLyric!.Timestamp);
        Assert.Equal("Hello world", syncLyric.Text);
    }

    [Fact]
    public void ToSyncLyricOrNull_ReturnsNullForInvalidLine()
    {
        var syncLyric = InvokeStaticNullable<SyncLyric>("ToSyncLyricOrNull", "not-an-lrc-line");
        Assert.Null(syncLyric);
    }

    [Fact]
    public void ResolveSyncedLinesFromSidecars_UsesLrcWhenPresent()
    {
        using var tmp = new TemporaryDirectory();
        var lrcPath = Path.Join(tmp.Path, "track.lrc");
        var ttmlPath = Path.Join(tmp.Path, "track.ttml");
        File.WriteAllText(lrcPath, "[00:01.00]Line A\n[00:02.00]Line B\n");
        File.WriteAllText(ttmlPath, "<tt><body><div><p begin=\"00:00:03.000\">Ignored</p></div></body></tt>");

        var lines = InvokeStatic<List<string>>("ResolveSyncedLinesFromSidecars", lrcPath, ttmlPath);

        Assert.Equal(2, lines.Count);
        Assert.Equal("[00:01.00]Line A", lines[0]);
        Assert.Equal("[00:02.00]Line B", lines[1]);
    }

    [Fact]
    public void ResolveUnsyncedTextFromSidecars_PrefersTxtThenFallsBackToSyncedText()
    {
        using var tmp = new TemporaryDirectory();
        var txtPath = Path.Join(tmp.Path, "track.txt");
        var lrcPath = Path.Join(tmp.Path, "track.lrc");
        var ttmlPath = Path.Join(tmp.Path, "track.ttml");

        File.WriteAllText(lrcPath, "[00:01.00]Line A\n[00:02.00]Line B\n");
        File.WriteAllText(txtPath, " Unsynced from txt ");

        var fromTxt = InvokeStatic<string>("ResolveUnsyncedTextFromSidecars", txtPath, lrcPath, ttmlPath);
        Assert.Equal("Unsynced from txt", fromTxt);

        File.Delete(txtPath);

        var fromSynced = InvokeStatic<string>("ResolveUnsyncedTextFromSidecars", txtPath, lrcPath, ttmlPath);
        Assert.Equal($"Line A{Environment.NewLine}Line B", fromSynced);
    }

    [Fact]
    public void ConvertSyncedLyricsToUnsynced_JoinsNonEmptyLines()
    {
        var synced = new List<SyncLyric>
        {
            new() { Timestamp = 1000, Text = "First" },
            new() { Timestamp = 2000, Text = " " },
            new() { Timestamp = 3000, Text = "Second" }
        };

        var unsynced = InvokeStatic<string>("ConvertSyncedLyricsToUnsynced", synced);

        Assert.Equal($"First{Environment.NewLine}Second", unsynced);
    }

    [Fact]
    public void ApplyResolvedLyricsForTagging_FillsUnsyncedAndSyncedWhenMissing()
    {
        var track = new Track
        {
            Title = "Song",
            MainArtist = new Artist("1", "Artist"),
            Album = new Album("1", "Album"),
            Lyrics = new Lyrics("1")
        };
        var tagSettings = new TagSettings
        {
            Lyrics = true,
            SyncedLyrics = true
        };
        var lyrics = new LyricsSource
        {
            UnsyncedLyrics = "Plain lyrics",
            SyncedLyrics =
            [
                new SynchronizedLyric("Line 1", "[00:01.00]", 1000),
                new SynchronizedLyric("Line 2", "[00:02.00]", 2000)
            ]
        };

        GetStaticMethod("ApplyResolvedLyricsForTagging").Invoke(null, [track, tagSettings, lyrics]);

        Assert.Equal("Plain lyrics", track.Lyrics.Unsync);
        Assert.NotEmpty(track.Lyrics.Sync);
        Assert.Equal(2, track.Lyrics.SyncID3.Count);
        Assert.Equal("Line 1", track.Lyrics.SyncID3[0].Text);
        Assert.Equal(1000, track.Lyrics.SyncID3[0].Timestamp);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deezspotag-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup for test temp folders.
            }
        }
    }
}
