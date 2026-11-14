using System;
using System.Collections.Generic;
using System.IO;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Models;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LyricsArtifactStateTest
{
    [Fact]
    public async Task ApplyProgress_ResolvedRichLyricsHaveNoPhantomPlainRequirement()
    {
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = true, SaveLyrics = true, LrcFormat = "lrc",
            LrcType = "lyrics,syllable-lyrics,unsynced-lyrics", LyricsFallbackOrder = "musixmatch,lrclib"
        };
        var (result, progress) = await LyricsServicePrivateHelpersTest.ResolveWithCachedLyricsAsync(settings,
            new Dictionary<string, LyricsBase>
            {
                ["musixmatch"] = new LyricsSource
                {
                    SyncedLyrics = [new SynchronizedLyric("Word lyrics", "[00:01.00]", 1000)
                    {
                        Words = [new SynchronizedLyricWord("Word", 1000, 1500), new SynchronizedLyricWord(" lyrics", 1500, 2000)]
                    }],
                    SyncedLyricsSourceFormat = LyricsSourceFormat.ProviderSyncedJson
                },
                ["lrclib"] = new LyricsSource { UnsyncedLyrics = "Unneeded plain" }
            });
        var artifacts = LyricsArtifactState.Fetching(result.Plan);
        foreach (var tick in progress)
        {
            artifacts.ApplyProgress(tick);
            Assert.DoesNotContain("plain-text", artifacts.RemainingOutputs);
        }
        artifacts.ApplyResolution(result);

        Assert.Empty(artifacts.RemainingOutputs);
        Assert.Null(artifacts.IncompleteReason);
        Assert.Equal("resolved", artifacts.Status);
        Assert.Equal("word", artifacts.LrcTiming);
        Assert.Equal(["lrc"], artifacts.ResolvedFormats);
    }

    [Theory]
    [InlineData("lrc", "lrc")]
    [InlineData("ttml", "ttml")]
    [InlineData("both", "ttml,lrc")]
    [InlineData("richlyrics", "ttml,lrc")]
    public void DescribeResolutionPlan_UsesRequestedRichFormats(string format, string expected)
    {
        var plan = LyricsService.DescribeResolutionPlan(new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = true,
            LrcType = "lyrics,syllable-lyrics,ttml-lyrics,unsynced-lyrics",
            LrcFormat = format,
            LyricsFallbackEnabled = true,
            LyricsFallbackOrder = "lrclib,apple"
        });

        Assert.Equal(expected, string.Join(',', plan.RequestedFormats));
        Assert.True(plan.PlainFallbackAllowed);
        Assert.Equal(["lrclib", "apple"], plan.Providers);
    }

    [Fact]
    public void DescribeResolutionPlan_FallbackDisabled_UsesOnlyPreferredProvider()
    {
        var plan = LyricsService.DescribeResolutionPlan(new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            LrcType = "lyrics",
            LrcFormat = "lrc",
            LyricsFallbackEnabled = false,
            LyricsFallbackOrder = "spotify,lrclib,apple"
        });

        Assert.Equal(["spotify"], plan.Providers);
    }

    [Fact]
    public void ApplyResolution_SuppressesTxtWhenAnyRichFormatResolved()
    {
        var plan = new LyricsResolutionPlan(["ttml", "lrc"], ["apple", "lrclib"], true);
        var state = LyricsArtifactState.Fetching(plan);
        var result = new LyricsResolutionResult(
            null,
            plan,
            ["apple", "lrclib"],
            ["lrc", "txt"],
            new Dictionary<string, string>
            {
                ["lrc"] = "lrclib",
                ["txt"] = "apple"
            },
            null);

        state.ApplyResolution(result);

        Assert.Equal(["lrc"], state.ResolvedFormats);
        Assert.False(state.SourcesByFormat.ContainsKey("txt"));
        Assert.Equal("resolved", state.Status);
    }

    [Fact]
    public void ApplyResolution_AllowsTxtOnlyWhenNoRichFormatResolved()
    {
        var plan = new LyricsResolutionPlan(["ttml", "lrc"], ["apple"], true);
        var state = LyricsArtifactState.Fetching(plan);
        var result = new LyricsResolutionResult(
            null,
            plan,
            ["apple"],
            ["txt"],
            new Dictionary<string, string> { ["txt"] = "apple" },
            null);

        state.ApplyResolution(result);

        Assert.Equal(["txt"], state.ResolvedFormats);
        Assert.Equal("resolved", state.Status);
    }

    [Fact]
    public void ApplyResolution_OperationallyDegradedRichLyricsRemainIncomplete()
    {
        var plan = new LyricsResolutionPlan(["ttml", "lrc"], ["apple", "spotify"], true);
        var state = LyricsArtifactState.Fetching(plan);
        var outcomes = new[]
        {
            new LyricsProviderOutcome("apple", "transient-failure", "timeout"),
            new LyricsProviderOutcome("spotify", "resolved")
        };
        var result = new LyricsResolutionResult(
            null,
            plan,
            ["apple", "spotify"],
            ["lrc"],
            new Dictionary<string, string> { ["lrc"] = "spotify" },
            "Requested lyrics formats were incomplete because a provider failed operationally.",
            outcomes,
            Incomplete: true);

        state.ApplyResolution(result);
        state.ApplyDownloadedFiles(new Dictionary<string, string>
        {
            ["lrc"] = "/music/track.lrc"
        });

        Assert.Equal("incomplete", state.Status);
        Assert.Equal(outcomes, state.ProviderOutcomes);
        Assert.Equal(["lrc"], state.ResolvedFormats);
        Assert.Equal(["lrc"], state.DownloadedFormats);
    }

    [Fact]
    public void ApplyProgress_TracksProviderAndTimingAwareRemainingOutputs()
    {
        var state = LyricsArtifactState.Fetching(
            new LyricsResolutionPlan(["ttml", "lrc"], ["apple", "lrclib"], false));
        var revision = state.Revision;

        state.ApplyProgress(new LyricsResolutionProgress(
            "provider-started",
            "apple",
            null,
            ["word-ttml", "enhanced-lrc"],
            ["enhanced-lrc"],
            ["word-ttml"]));

        Assert.True(state.Revision > revision);
        Assert.Equal("apple", state.CurrentProvider);
        Assert.Equal(["word-ttml"], state.RemainingOutputs);
        Assert.Equal("fetching", state.Status);
    }

    [Fact]
    public void ApplyDownloadedFiles_RecordsWrittenSidecarsAndSuppressesTxt()
    {
        var state = LyricsArtifactState.Fetching(new LyricsResolutionPlan(["lrc"], ["lrclib"], true));

        state.ApplyDownloadedFiles(new Dictionary<string, string>
        {
            ["lrc"] = "/music/track.lrc",
            ["txt"] = "/music/track.txt"
        });

        Assert.Equal(["lrc"], state.DownloadedFormats);
        Assert.Equal("completed", state.Status);
        Assert.False(state.FilesByFormat.ContainsKey("txt"));
    }

    [Theory]
    [InlineData("[00:01.00]<00:01.000>Oh <00:01.400>yeah", "word")]
    [InlineData("[00:01.00]Oh yeah", "line")]
    public void ApplyDownloadedFiles_RecordsLrcTimingPrecision(string lrcBody, string expectedTiming)
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-lrc-timing-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        try
        {
            var lrcPath = Path.Join(tempRoot, "track.lrc");
            File.WriteAllText(lrcPath, lrcBody);

            var state = LyricsArtifactState.Fetching(new LyricsResolutionPlan(["lrc"], ["musixmatch"], false));
            state.ApplyDownloadedFiles(new Dictionary<string, string> { ["lrc"] = lrcPath });

            Assert.Equal(expectedTiming, state.LrcTiming);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ApplyDownloadedFiles_ClearsLrcTimingWhenNoLrcWasWritten()
    {
        var state = LyricsArtifactState.Fetching(new LyricsResolutionPlan(["ttml"], ["apple"], false));
        state.ApplyDownloadedFiles(new Dictionary<string, string> { ["ttml"] = "/music/track.ttml" });

        Assert.Null(state.LrcTiming);
    }

    [Fact]
    public void ApplyDownloadedFiles_RecordsEveryRichLyricsFormat()
    {
        var state = LyricsArtifactState.Fetching(new LyricsResolutionPlan(["ttml", "lrc"], ["musixmatch"], false));

        state.ApplyDownloadedFiles(new Dictionary<string, string>
        {
            ["ttml"] = "/music/track.ttml",
            ["lrc"] = "/music/track.lrc"
        });

        Assert.Equal(["ttml", "lrc"], state.DownloadedFormats);
        Assert.Equal("/music/track.lrc", state.FilesByFormat["lrc"]);
        Assert.Equal("completed", state.Status);
    }

    [Fact]
    public void QueueSettingsSnapshot_PreservesLyricsPreferencesForRetries()
    {
        var snapshot = QueueSourceSettingsSnapshot.Capture(new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = true,
            LrcType = "lyrics,unsynced-lyrics",
            LyricsTypeOrder = "lyrics,unsynced-lyrics,ttml-lyrics,syllable-lyrics",
            LrcFormat = "ttml",
            SynthesizeLrcFromTtml = true,
            LyricsFallbackEnabled = false,
            LyricsFallbackOrder = "lrclib,apple"
        });

        var effective = snapshot.ApplyTo(new DeezSpoTagSettings());

        Assert.True(effective.SyncedLyrics);
        Assert.True(effective.SaveLyrics);
        Assert.Equal("lyrics,unsynced-lyrics", effective.LrcType);
        Assert.Equal("lyrics,ttml-lyrics,syllable-lyrics,unsynced-lyrics", effective.LyricsTypeOrder);
        Assert.Equal("ttml", effective.LrcFormat);
        Assert.True(effective.SynthesizeLrcFromTtml);
        Assert.False(effective.LyricsFallbackEnabled);
        Assert.Equal("lrclib,apple", effective.LyricsFallbackOrder);
    }

    [Fact]
    public void QueueSettingsSnapshot_LegacySnapshotWithoutTypeOrder_RetainsLegacyPriority()
    {
        // A queued job captured before the field existed must not inherit a later
        // reordered global preference; it keeps the legacy default priority.
        var snapshot = QueueSourceSettingsSnapshot.Capture(new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = true,
            LrcType = "lyrics,syllable-lyrics,ttml-lyrics,unsynced-lyrics"
        });
        snapshot.LyricsTypeOrder = null;

        var reorderedGlobal = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = true,
            LyricsTypeOrder = "lyrics,ttml-lyrics,syllable-lyrics"
        };

        var effective = snapshot.ApplyTo(reorderedGlobal);

        Assert.Equal("ttml-lyrics,syllable-lyrics,lyrics", effective.LyricsTypeOrder);
    }

    [Fact]
    public void QueueSettingsSnapshot_JsonRead_ReadsTypeOrderInBothCasingConventions()
    {
        var pascal = QueueSourceSettingsSnapshot.ReadFromPayload(JsonNode.Parse(
            """{"SourceSettingsSnapshot":{"SyncedLyrics":true,"LyricsTypeOrder":"lyrics,ttml-lyrics"}}""")!.AsObject());
        Assert.Equal("lyrics,ttml-lyrics", pascal?.LyricsTypeOrder);

        var camel = QueueSourceSettingsSnapshot.ReadFromPayload(JsonNode.Parse(
            """{"sourceSettingsSnapshot":{"syncedLyrics":true,"lyricsTypeOrder":"unsynced-lyrics,lyrics"}}""")!.AsObject());
        Assert.Equal("unsynced-lyrics,lyrics", camel?.LyricsTypeOrder);
    }

    // ---- lyrics type priority: ordered plan fingerprints ----

    [Fact]
    public void Fetching_PlanFingerprintDistinguishesTypeOrder()
    {
        var providers = new[] { "apple", "lrclib" };
        var ttmlFirst = new LyricsResolutionPlan(["ttml", "lrc"], providers, true)
        {
            TypeOrder = "ttml-lyrics,lyrics"
        };
        var lineFirst = new LyricsResolutionPlan(["ttml", "lrc"], providers, true)
        {
            TypeOrder = "lyrics,ttml-lyrics"
        };
        var lineFirstAgain = new LyricsResolutionPlan(["ttml", "lrc"], providers, true)
        {
            TypeOrder = "lyrics,ttml-lyrics"
        };

        var ttmlFirstFingerprint = LyricsArtifactState.Fetching(ttmlFirst).PlanFingerprint;
        var lineFirstFingerprint = LyricsArtifactState.Fetching(lineFirst).PlanFingerprint;
        var lineFirstAgainFingerprint = LyricsArtifactState.Fetching(lineFirstAgain).PlanFingerprint;

        Assert.NotEqual(ttmlFirstFingerprint, lineFirstFingerprint);
        Assert.Equal(lineFirstFingerprint, lineFirstAgainFingerprint);
    }

    [Fact]
    public void Fetching_ReorderedPlanCannotSatisfyCompletedArtifactOfOtherOrder()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-lyrics-artifacts-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        try
        {
            var ttmlPath = Path.Join(tempRoot, "track.ttml");
            File.WriteAllText(ttmlPath, "<tt><body><p>lyrics</p></body></tt>");
            var providers = new[] { "apple", "lrclib" };
            var completed = new LyricsArtifactState
            {
                Revision = 50,
                Status = "completed",
                ResolvedFormats = ["ttml"],
                DownloadedFormats = ["ttml"],
                FilesByFormat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ttml"] = ttmlPath
                }
            };
            completed.PlanFingerprint = LyricsArtifactState.Fetching(
                new LyricsResolutionPlan(["ttml", "lrc"], providers, true) { TypeOrder = "ttml-lyrics,lyrics" }).PlanFingerprint;

            var reordered = LyricsArtifactState.Fetching(
                new LyricsResolutionPlan(["ttml", "lrc"], providers, true) { TypeOrder = "lyrics,ttml-lyrics" },
                completed);

            // The stale completed artifact of the other order cannot satisfy the
            // reordered plan silently; its verified files are not carried over.
            Assert.Equal("fetching", reordered.Status);
            Assert.Empty(reordered.DownloadedFormats);
            Assert.NotEqual(completed.PlanFingerprint, reordered.PlanFingerprint);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Fetching_DefaultTypeOrder_MatchesLegacyFingerprint()
    {
        // A plan whose resolved order is the legacy default (or absent, for legacy
        // callers) must hash exactly like the pre-feature fingerprint so existing
        // verified checkpoints keep their reuse eligibility.
        var providers = new[] { "apple", "lrclib" };
        var legacy = new LyricsResolutionPlan(["ttml", "lrc"], providers, true);
        var defaultOrder = new LyricsResolutionPlan(["ttml", "lrc"], providers, true)
        {
            TypeOrder = "ttml-lyrics,syllable-lyrics,lyrics"
        };
        var blankOrder = new LyricsResolutionPlan(["ttml", "lrc"], providers, true)
        {
            TypeOrder = ""
        };

        var legacyFingerprint = LyricsArtifactState.Fetching(legacy).PlanFingerprint;

        Assert.Equal(legacyFingerprint, LyricsArtifactState.Fetching(defaultOrder).PlanFingerprint);
        Assert.Equal(legacyFingerprint, LyricsArtifactState.Fetching(blankOrder).PlanFingerprint);

        // The UI appends unsynced-lyrics whenever plain lyrics are enabled. Plain is
        // pinned last in every plan and its availability is already encoded by
        // PlainFallbackAllowed, so the plain suffix adds no ordering information and
        // must not break legacy checkpoint reuse.
        var uiDefaultWithPlain = new LyricsResolutionPlan(["ttml", "lrc"], providers, true)
        {
            TypeOrder = "ttml-lyrics,syllable-lyrics,lyrics,unsynced-lyrics"
        };
        var plainFirstDefault = new LyricsResolutionPlan(["ttml", "lrc"], providers, true)
        {
            TypeOrder = "unsynced-lyrics,ttml-lyrics,syllable-lyrics,lyrics"
        };
        Assert.Equal(legacyFingerprint, LyricsArtifactState.Fetching(uiDefaultWithPlain).PlanFingerprint);
        Assert.Equal(legacyFingerprint, LyricsArtifactState.Fetching(plainFirstDefault).PlanFingerprint);

        // Byte compatibility with checkpoints written before the field existed.
        var legacyFormula = "ttml,lrc\u001fapple,lrclib\u001fTrue";
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(legacyFormula))),
            legacyFingerprint);
    }

    [Fact]
    public void Fetching_CustomOrderPlainSuffix_IsCanonicalAcrossPlainAvailability()
    {
        // A customized rich order fingerprints identically whether or not the caller
        // appended the plain token: the rich order is the only ordering information.
        var providers = new[] { "apple", "lrclib" };
        var withoutPlain = new LyricsResolutionPlan(["ttml", "lrc"], providers, true)
        {
            TypeOrder = "lyrics,ttml-lyrics,syllable-lyrics"
        };
        var withPlain = new LyricsResolutionPlan(["ttml", "lrc"], providers, true)
        {
            TypeOrder = "lyrics,unsynced-lyrics,ttml-lyrics,syllable-lyrics"
        };

        Assert.Equal(
            LyricsArtifactState.Fetching(withoutPlain).PlanFingerprint,
            LyricsArtifactState.Fetching(withPlain).PlanFingerprint);
    }

    [Fact]
    public void Fetching_LegacyCheckpointKeepsReuseEligibilityUnderUnchangedPreferences()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-lyrics-reuse-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        try
        {
            var ttmlPath = Path.Join(tempRoot, "track.ttml");
            File.WriteAllText(ttmlPath, "<tt><body><p>lyrics</p></body></tt>");
            var providers = new[] { "apple", "lrclib" };
            var legacyPlan = new LyricsResolutionPlan(["ttml", "lrc"], providers, true);

            // A completed checkpoint written before the type order existed: verified
            // TTML file plus the legacy (pre-feature) fingerprint.
            var legacyCheckpoint = LyricsArtifactState.Fetching(legacyPlan);
            legacyCheckpoint.Status = "completed";
            legacyCheckpoint.ResolvedFormats = ["ttml"];
            legacyCheckpoint.DownloadedFormats = ["ttml"];
            legacyCheckpoint.FilesByFormat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ttml"] = ttmlPath
            };

            // The same preferences after the update: the resolved order is still the
            // legacy default, so the verified file must remain reusable. The second
            // plan is exactly what the AutoTag UI saves when plain lyrics are enabled.
            var samePreferences = new LyricsResolutionPlan(["ttml", "lrc"], providers, true)
            {
                TypeOrder = "ttml-lyrics,syllable-lyrics,lyrics"
            };
            var uiSavedWithPlain = new LyricsResolutionPlan(["ttml", "lrc"], providers, true)
            {
                TypeOrder = "ttml-lyrics,syllable-lyrics,lyrics,unsynced-lyrics"
            };
            var resumed = LyricsArtifactState.Fetching(samePreferences, legacyCheckpoint);

            Assert.Equal("fetching", resumed.Status);
            Assert.Equal(["ttml"], resumed.DownloadedFormats);
            Assert.Equal(ttmlPath, resumed.FilesByFormat["ttml"]);

            var resumedFromUiOrder = LyricsArtifactState.Fetching(uiSavedWithPlain, legacyCheckpoint);

            Assert.Equal(["ttml"], resumedFromUiOrder.DownloadedFormats);
            Assert.Equal(ttmlPath, resumedFromUiOrder.FilesByFormat["ttml"]);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Fetching_PreservesCompletedArtifactsWithoutStartingAnotherLyricsPath()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-lyrics-artifacts-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        var ttmlPath = Path.Join(tempRoot, "track.ttml");
        var lrcPath = Path.Join(tempRoot, "track.lrc");
        File.WriteAllText(ttmlPath, "<tt><body><p>lyrics</p></body></tt>");
        File.WriteAllText(lrcPath, "[00:01.00]lyrics");
        var plan = new LyricsResolutionPlan(["ttml", "lrc"], ["apple", "lrclib"], true);
        var previous = new LyricsArtifactState
        {
            Revision = 50,
            Status = "completed",
            ResolvedFormats = ["ttml", "lrc"],
            DownloadedFormats = ["ttml", "lrc"],
            FilesByFormat = new Dictionary<string, string>
            {
                ["ttml"] = ttmlPath,
                ["lrc"] = lrcPath
            }
        };

        try
        {
            var next = LyricsArtifactState.Fetching(plan, previous);

            Assert.True(next.Satisfies(plan));
            Assert.Equal("completed", next.Status);
            Assert.True(next.Revision > previous.Revision);
            Assert.False(string.IsNullOrWhiteSpace(next.AttemptId));
            Assert.False(string.IsNullOrWhiteSpace(next.PlanFingerprint));
            Assert.Equal(64, next.FileHashesByFormat["ttml"].Length);
            Assert.Equal(64, next.FileHashesByFormat["lrc"].Length);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Fetching_InvalidatesMissingArtifactsSoRetryRefetchesLyrics()
    {
        var plan = new LyricsResolutionPlan(["lrc"], ["musixmatch"], false);
        var previous = new LyricsArtifactState
        {
            Status = "completed",
            ResolvedFormats = ["lrc"],
            DownloadedFormats = ["lrc"],
            FilesByFormat = new Dictionary<string, string>
            {
                ["lrc"] = Path.Join(Path.GetTempPath(), Path.GetRandomFileName() + ".lrc")
            }
        };

        var next = LyricsArtifactState.Fetching(plan, previous);

        Assert.False(next.Satisfies(plan));
        Assert.Equal("fetching", next.Status);
        Assert.Empty(next.DownloadedFormats);
        Assert.Empty(next.FilesByFormat);
    }

    [Fact]
    public void Fetching_InvalidatesArtifactsWhenResolutionPlanChanges()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-lyrics-plan-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        var path = Path.Join(tempRoot, "track.lrc");
        File.WriteAllText(path, "[00:01.00]lyrics");
        try
        {
            var original = LyricsArtifactState.Fetching(
                new LyricsResolutionPlan(["lrc"], ["lrclib"], false));
            original.ApplyDownloadedFiles(new Dictionary<string, string> { ["lrc"] = path });

            var changed = LyricsArtifactState.Fetching(
                new LyricsResolutionPlan(["ttml"], ["musixmatch"], false),
                original);

            Assert.Empty(changed.FilesByFormat);
            Assert.Equal("fetching", changed.Status);
            Assert.NotEqual(original.PlanFingerprint, changed.PlanFingerprint);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Fetching_InvalidatesArtifactWhoseContentsChanged()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-lyrics-hash-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        var path = Path.Join(tempRoot, "track.lrc");
        File.WriteAllText(path, "[00:01.00]first");
        var plan = new LyricsResolutionPlan(["lrc"], ["lrclib"], false);
        try
        {
            var original = LyricsArtifactState.Fetching(plan);
            original.ApplyDownloadedFiles(new Dictionary<string, string> { ["lrc"] = path });
            File.WriteAllText(path, "[00:01.00]changed");

            var retried = LyricsArtifactState.Fetching(plan, original);

            Assert.Empty(retried.FilesByFormat);
            Assert.False(retried.Satisfies(plan));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
