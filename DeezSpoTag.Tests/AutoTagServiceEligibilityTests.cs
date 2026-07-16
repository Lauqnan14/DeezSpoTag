using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagServiceEligibilityTests
{
    private static MethodInfo ServiceMethod(string name)
    {
        return typeof(AutoTagService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"AutoTagService.{name} not found.");
    }

    private static T InvokeStatic<T>(string methodName, params object?[] args)
    {
        var result = ServiceMethod(methodName).Invoke(null, args);
        if (result == null)
        {
            return default!;
        }

        return (T)result;
    }

    [Fact]
    public void NormalizeRootPath_ReturnsNullForMissingDirectory()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"autotag-missing-{Guid.NewGuid():N}");
        var normalized = InvokeStatic<string?>("NormalizeRootPath", missing);
        Assert.Null(normalized);
    }

    [Fact]
    public void HasEligibleInputFiles_ReturnsTrueWhenConfigJsonCannotBeParsed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"autotag-config-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var result = InvokeStatic<bool>("HasEligibleInputFiles", root, "{not-json");
            Assert.True(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HasShazamReviewCandidate_RejectsUnavailableStatusWithoutCandidate()
    {
        var result = InvokeStatic<bool>(
            "HasShazamReviewCandidate",
            new TaggingStatus
            {
                Status = "review",
                Message = "shazam unavailable",
                SourceTitle = "16 - Gangster",
                SourceArtist = "Unknown"
            });

        Assert.False(result);
    }

    [Fact]
    public void HasShazamReviewCandidate_AcceptsCandidateConflict()
    {
        var result = InvokeStatic<bool>(
            "HasShazamReviewCandidate",
            new TaggingStatus
            {
                Status = "review",
                SourceTitle = "Wrong Song",
                CandidateTitle = "Correct Song"
            });

        Assert.True(result);
    }

    [Fact]
    public void HasEligibleInputFiles_ReturnsTrueWhenTargetFilesContainSupportedInScopeAudioFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"autotag-targets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var candidate = Path.Combine(root, "track.flac");
            var outside = Path.Combine(Path.GetTempPath(), $"autotag-outside-{Guid.NewGuid():N}.flac");
            File.WriteAllText(candidate, "audio");
            File.WriteAllText(outside, "audio");

            var configJson = $$"""
                {
                  "targetFiles": [
                    "{{candidate.Replace("\\", "\\\\")}}",
                    "{{outside.Replace("\\", "\\\\")}}"
                  ]
                }
                """;

            var result = InvokeStatic<bool>("HasEligibleInputFiles", root, configJson);
            Assert.True(result);

            File.Delete(outside);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HasEligibleInputFiles_ReturnsFalseWhenNoSupportedFilesExist()
    {
        var root = Path.Combine(Path.GetTempPath(), $"autotag-no-audio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "notes.txt"), "not audio");
            var configJson = """{"includeSubfolders": true}""";

            var result = InvokeStatic<bool>("HasEligibleInputFiles", root, configJson);

            Assert.False(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EvaluateIdentityReviewGuard_FlagsSharpTitleAndArtistReplacement()
    {
        var diff = new AutoTagTagDiff
        {
            Before = Snapshot("Mema Meni So", ["Obaapa Christy"], ["Obaapa Christy"]),
            After = Snapshot("Oba Ha Mema", ["T M Jayarathna"], ["T M Jayarathna"])
        };

        var reason = InvokeStatic<string?>("EvaluateIdentityReviewGuard", diff);

        Assert.NotNull(reason);
        Assert.Contains("requires user review", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateIdentityReviewGuard_AllowsSameArtistTitleCleanup()
    {
        var diff = new AutoTagTagDiff
        {
            Before = Snapshot("Mema Meni So", ["Obaapa Christy"], ["Obaapa Christy"]),
            After = Snapshot("Mema Meni So - Remastered", ["Obaapa Christy"], ["Obaapa Christy"])
        };

        var reason = InvokeStatic<string?>("EvaluateIdentityReviewGuard", diff);

        Assert.Null(reason);
    }

    [Fact]
    public void EvaluateIdentityReviewGuard_FlagsSameTitleArtistReplacement()
    {
        var diff = new AutoTagTagDiff
        {
            Before = Snapshot("All Over You", ["Deobi"], ["Deobi"]),
            After = Snapshot("All Over You", ["O.B.I"], ["O.B.I"])
        };

        var reason = InvokeStatic<string?>("EvaluateIdentityReviewGuard", diff);

        Assert.NotNull(reason);
        Assert.Contains("artist identity changed sharply", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateIdentityReviewGuard_AllowsArtistCasingCleanup()
    {
        var diff = new AutoTagTagDiff
        {
            Before = Snapshot("Steppas", ["A Boogie wit da Hoodie"], ["A Boogie wit da Hoodie"]),
            After = Snapshot("Steppas", ["A Boogie Wit da Hoodie"], ["A Boogie Wit da Hoodie"])
        };

        var reason = InvokeStatic<string?>("EvaluateIdentityReviewGuard", diff);

        Assert.Null(reason);
    }

    [Fact]
    public void SelectRequestedPlatformDiff_ReturnsOriginalToRequestedCumulativeState()
    {
        var original = SnapshotWithTags("Track", new Dictionary<string, string[]>
        {
            ["ARTIST"] = ["Original Artist"]
        });
        var platform1 = SnapshotWithTags("Track", new Dictionary<string, string[]>
        {
            ["ARTIST"] = ["Original Artist"],
            ["SPOTIFY_URL"] = ["spotify:track:1"]
        });
        var platform2 = SnapshotWithTags("Track", new Dictionary<string, string[]>
        {
            ["ARTIST"] = ["Original Artist"],
            ["SPOTIFY_URL"] = ["spotify:track:1"],
            ["GENRE"] = ["Dance"]
        });
        var platform4 = SnapshotWithTags("Track", new Dictionary<string, string[]>
        {
            ["ARTIST"] = ["Original Artist"],
            ["SPOTIFY_URL"] = ["spotify:track:1"],
            ["GENRE"] = ["Dance"],
            ["LABEL"] = ["Example Label"]
        });
        var stored = new AutoTagTagDiff
        {
            Path = "/music/track.flac",
            Before = original,
            After = platform4,
            LastPlatform = "platform4",
            PlatformDiffs =
            [
                PlatformStep("platform1", original, platform1),
                PlatformStep("platform2", platform1, platform2),
                PlatformStep("platform4", platform2, platform4)
            ]
        };

        var selected = InvokeStatic<AutoTagTagDiff>("SelectRequestedPlatformDiff", stored, "platform2");

        Assert.Same(original, selected.Before);
        Assert.Same(platform2, selected.After);
        Assert.Equal("original", selected.BasePlatform);
        Assert.Equal("platform2", selected.TargetPlatform);
        Assert.Equal("platform2", selected.LastPlatform);
        Assert.False(selected.IsFinalPlatformDiff);
        Assert.Equal(["platform1", "platform2"], selected.PlatformDiffs.Select(step => step.Platform));
        var selectedAfter = Assert.IsType<AutoTagTagSnapshot>(selected.After);
        Assert.Equal(["spotify:track:1"], selectedAfter.Tags["SPOTIFY_URL"]);
        Assert.Equal(["Dance"], selectedAfter.Tags["GENRE"]);
        Assert.False(selectedAfter.Tags.ContainsKey("LABEL"));
    }

    [Fact]
    public void SelectRequestedPlatformDiff_IgnoresSkippedPlatformsWithoutSnapshots()
    {
        var original = SnapshotWithTags("Track", new Dictionary<string, string[]>());
        var platform1 = SnapshotWithTags("Track", new Dictionary<string, string[]>
        {
            ["SPOTIFY_URL"] = ["spotify:track:1"]
        });
        var platform2 = SnapshotWithTags("Track", new Dictionary<string, string[]>
        {
            ["SPOTIFY_URL"] = ["spotify:track:1"],
            ["GENRE"] = ["Dance"]
        });
        var platform4 = SnapshotWithTags("Track", new Dictionary<string, string[]>
        {
            ["SPOTIFY_URL"] = ["spotify:track:1"],
            ["GENRE"] = ["Dance"],
            ["LABEL"] = ["Example Label"]
        });
        var stored = new AutoTagTagDiff
        {
            Before = original,
            After = platform4,
            PlatformDiffs =
            [
                PlatformStep("platform1", original, platform1),
                PlatformStep("platform2", platform1, platform2),
                new AutoTagPlatformDiffSnapshot { Platform = "platform3", Status = "skipped" },
                PlatformStep("platform4", platform2, platform4)
            ]
        };

        var selected = InvokeStatic<AutoTagTagDiff>("SelectRequestedPlatformDiff", stored, "platform4");

        Assert.Same(original, selected.Before);
        Assert.Same(platform4, selected.After);
        Assert.Equal(["platform1", "platform2", "platform4"], selected.PlatformDiffs.Select(step => step.Platform));
        Assert.True(selected.IsFinalPlatformDiff);
    }

    private static AutoTagPlatformDiffSnapshot PlatformStep(
        string platform,
        AutoTagTagSnapshot before,
        AutoTagTagSnapshot after)
    {
        return new AutoTagPlatformDiffSnapshot
        {
            Platform = platform,
            Status = "tagged",
            Before = before,
            After = after
        };
    }

    private static AutoTagTagSnapshot SnapshotWithTags(
        string title,
        Dictionary<string, string[]> tags)
    {
        return new AutoTagTagSnapshot
        {
            Meta = new QuickTagDumpMeta { Title = title },
            Tags = tags.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.ToList(),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static AutoTagTagSnapshot Snapshot(string title, string[] artists, string[] albumArtists)
    {
        return new AutoTagTagSnapshot
        {
            Meta = new QuickTagDumpMeta
            {
                Title = title,
                Artists = artists.ToList(),
                AlbumArtists = albumArtists.ToList()
            }
        };
    }
}
