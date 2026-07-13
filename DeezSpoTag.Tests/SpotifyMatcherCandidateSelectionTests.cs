using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyMatcherCandidateSelectionTests
{
    [Fact]
    public void SelectBestCandidate_DoesNotTrustFirstIsrcResult_WhenArtistDoesNotMatch()
    {
        var source = SourceInfo();
        var wrongFirst = new SpotifyTrackInfo
        {
            Title = "All Over You",
            Artists = new List<string> { "O.B.I" },
            Duration = TimeSpan.FromSeconds(145),
            TrackId = "1111111111111111111111"
        };
        var correctSecond = new SpotifyTrackInfo
        {
            Title = "All Over You",
            Artists = new List<string> { "Deobi" },
            Duration = TimeSpan.FromSeconds(145),
            TrackId = "6EzsUkZqhkXet4vVTc7kKv"
        };

        var selected = InvokeSelectBestCandidate(source, new[] { wrongFirst, correctSecond });

        Assert.Same(correctSecond, selected);
    }

    [Fact]
    public void SelectBestCandidate_ReturnsNull_WhenIsrcResultOnlyMatchesTitleButNotArtist()
    {
        var source = SourceInfo();
        var wrong = new SpotifyTrackInfo
        {
            Title = "All Over You",
            Artists = new List<string> { "O.B.I" },
            Duration = TimeSpan.FromSeconds(145),
            TrackId = "1111111111111111111111"
        };

        var selected = InvokeSelectBestCandidate(source, new[] { wrong });

        Assert.Null(selected);
    }

    [Fact]
    public void SelectBestCandidate_ReturnsNull_WhenIsrcResultsAreEmpty()
    {
        var selected = InvokeSelectBestCandidate(SourceInfo(), Array.Empty<SpotifyTrackInfo>());

        Assert.Null(selected);
    }

    [Fact]
    public void SelectBestCandidate_UsesRequestedSingleRelease()
    {
        var album = new SpotifyTrackInfo
        {
            Title = "All Over You",
            Artists = new List<string> { "Deobi" },
            Duration = TimeSpan.FromSeconds(145),
            TrackId = "1111111111111111111111",
            ReleaseType = "album",
            TrackTotal = 12
        };
        var single = new SpotifyTrackInfo
        {
            Title = "All Over You",
            Artists = new List<string> { "Deobi" },
            Duration = TimeSpan.FromSeconds(145),
            TrackId = "2222222222222222222222",
            ReleaseType = "single",
            TrackTotal = 1
        };

        var selected = InvokeSelectBestCandidate(SourceInfo(), new[] { album, single }, "single");

        Assert.Same(single, selected);
    }

    [Fact]
    public void SelectBestCandidate_DoesNotUseCompilationForRequestedAlbum()
    {
        var compilation = new SpotifyTrackInfo
        {
            Title = "All Over You",
            Artists = new List<string> { "Deobi" },
            Duration = TimeSpan.FromSeconds(145),
            TrackId = "1111111111111111111111",
            ReleaseType = "compilation",
            TrackTotal = 30
        };

        var selected = InvokeSelectBestCandidate(SourceInfo(), new[] { compilation }, "album");

        Assert.Null(selected);
    }

    private static AutoTagAudioInfo SourceInfo()
    {
        return new AutoTagAudioInfo
        {
            Title = "All Over You",
            Artist = "Deobi",
            Artists = new List<string> { "Deobi" },
            DurationSeconds = 145,
            Isrc = "QZTAW2669495"
        };
    }

    private static SpotifyTrackInfo? InvokeSelectBestCandidate(
        AutoTagAudioInfo source,
        IReadOnlyList<SpotifyTrackInfo> candidates,
        string? preferredReleaseType = null)
    {
        var candidateType = typeof(SpotifyMatcher).GetNestedType("SpotifyCandidate", BindingFlags.NonPublic);
        Assert.NotNull(candidateType);
        var candidateListType = typeof(List<>).MakeGenericType(candidateType!);
        var wrappedCandidates = (IList)Activator.CreateInstance(candidateListType)!;
        foreach (var candidate in candidates)
        {
            wrappedCandidates.Add(Activator.CreateInstance(candidateType!, candidate, 1));
        }

        var method = typeof(SpotifyMatcher).GetMethod(
            "SelectBestCandidate",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(AutoTagAudioInfo), typeof(IReadOnlyList<>).MakeGenericType(candidateType!), typeof(AutoTagMatchingConfig) },
            modifiers: null);
        Assert.NotNull(method);

        var selection = method!.Invoke(null, new object[]
        {
            source,
            wrappedCandidates,
            new AutoTagMatchingConfig
            {
                MatchDuration = true,
                MaxDurationDifferenceSeconds = 5,
                Strictness = 0.7,
                PreferredReleaseType = preferredReleaseType
            }
        });
        if (selection == null)
        {
            return null;
        }

        var selectedCandidate = selection.GetType().GetProperty("Track")?.GetValue(selection);
        Assert.NotNull(selectedCandidate);
        return selectedCandidate!.GetType().GetProperty("Track")?.GetValue(selectedCandidate) as SpotifyTrackInfo;
    }
}
