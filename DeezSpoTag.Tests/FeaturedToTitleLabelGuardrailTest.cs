using System;
using System.Collections.Generic;
using System.IO;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Locks the "Featured To Title" dropdown wording to what the backend actually does, so the two
/// cannot drift apart again.
///
/// The labels were previously the wrong way round, which is why the same setting appeared to
/// behave inconsistently:
///   value "1" keeps the featured artist in the artist tag AND puts it in the title  -> "Copy to title"
///   value "2" takes the featured artist out of the artist tag and puts it in the title -> "Move to title"
/// </summary>
public sealed class FeaturedToTitleLabelGuardrailTest
{
    private static string ReadAutoTagView() => File.ReadAllText(Path.Join(
        TestSourcePaths.RepositoryRoot,
        "DeezSpoTag.Web",
        "Views",
        "AutoTag",
        "Index.cshtml"));

    private static Track MakeTrack(string title)
        => new()
        {
            Title = title,
            MainArtist = new Artist("2Baba"),
            Album = new Album("album-1", title),
            Artist = new Dictionary<string, List<string>>
            {
                ["Main"] = new List<string> { "2Baba" },
                ["Featured"] = new List<string> { "Falz" }
            },
            Artists = new List<string> { "2Baba", "Falz" }
        };

    private static DeezSpoTagSettings Settings(string mode) => new()
    {
        FeaturedToTitle = mode,
        Tags = new TagSettings { MultiArtistSeparator = "default" }
    };

    [Fact]
    public void TheDropdownOffersCopyForValueOneAndMoveForValueTwo()
    {
        var source = ReadAutoTagView();

        Assert.Contains("<option value=\"1\">Copy to title</option>", source, StringComparison.Ordinal);
        Assert.Contains("<option value=\"2\">Move to title</option>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ValueTwoIsLabelledMoveToTitleBecauseItDropsTheFeaturedArtistFromTheArtistTag()
    {
        var track = MakeTrack("Rise Up");

        track.ApplySettings(Settings("2"));

        // The credit moved into the title...
        Assert.Equal("Rise Up (feat. Falz)", track.Title);
        // ...and left the artist tag, which is what makes "Move" the truthful word.
        Assert.Equal("2Baba", track.ArtistsString);
        Assert.DoesNotContain("Falz", track.ArtistsString, StringComparison.Ordinal);
    }

    [Fact]
    public void ValueOneIsLabelledCopyToTitleBecauseTheCreditStaysInTheArtistTag()
    {
        var track = MakeTrack("Rise Up (feat. Falz)");

        track.ApplySettings(Settings("1"));

        // Title is cleaned...
        Assert.Equal("Rise Up", track.Title);
        // ...while the artist tag still carries every artist, so nothing was moved.
        Assert.Contains("2Baba", track.ArtistsString, StringComparison.Ordinal);
        Assert.Contains("Falz", track.ArtistsString, StringComparison.Ordinal);
    }

    [Fact]
    public void ValueZeroLeavesTheArtistTagIntact()
    {
        var track = MakeTrack("Rise Up (feat. Falz)");

        track.ApplySettings(Settings("0"));

        Assert.Equal("Rise Up", track.Title);
        Assert.Contains("Falz", track.ArtistsString, StringComparison.Ordinal);
    }
}
