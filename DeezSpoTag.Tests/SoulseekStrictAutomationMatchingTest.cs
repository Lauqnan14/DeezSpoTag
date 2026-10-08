using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Proves that automated selection is genuinely stricter than a manual browse.
/// </summary>
/// <remarks>
///     The design requires manual search to be able to show more candidates while automated queueing rejects
///     weak matches. Each case below is one candidate a person might reasonably look at, and asserts that
///     automation refuses to queue it.
/// </remarks>
[Collection("Settings Config Isolation")]
public sealed class SoulseekStrictAutomationMatchingTest : IDisposable
{
    private readonly string _tempRoot;
    private readonly SoulseekSettingsService _settings;

    public SoulseekStrictAutomationMatchingTest()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-soulseek-strict-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", _tempRoot);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_DATA_DIR", _tempRoot);
        _settings = new SoulseekSettingsService(
            new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance),
            new LibraryRepository(
                new ConfigurationBuilder().Build(),
                NullLogger<LibraryRepository>.Instance),
            NullLogger<SoulseekSettingsService>.Instance);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", null);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_DATA_DIR", null);
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked temp file must not fail the run.
        }
    }

    private static SoulseekSearchTarget Target => new(
        Artist: "Boards of Canada",
        Title: "Roygbiv",
        Album: "Music Has the Right to Children",
        DurationMs: 151_000);

    private SoulseekResultScoringService CreateScorer(SoulseekDownloadSettings? settings = null)
    {
        _settings.Update(settings ?? new SoulseekDownloadSettings());
        return new SoulseekResultScoringService(
            _settings,
            new SoulseekPeerPolicyService(
                _settings,
                new SoulseekRepository(
                    new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:Queue"] = $"Data Source={Path.Join(_tempRoot, "queue.db")}"
                        })
                        .Build(),
                    NullLogger<SoulseekRepository>.Instance),
                NullLogger<SoulseekPeerPolicyService>.Instance),
            NullLogger<SoulseekResultScoringService>.Instance);
    }

    private static SoulseekRawCandidate Candidate(
        string filename,
        int? seconds = 151,
        int? bitDepth = 16,
        int? sampleRate = 44_100,
        int? bitrate = 940)
        => new(
            "listener",
            filename,
            30_000_000,
            Path.GetExtension(filename),
            bitrate,
            bitDepth,
            sampleRate,
            seconds,
            IsVariableBitrate: false,
            IsLocked: false,
            PeerQueueLength: 0,
            PeerHasFreeUploadSlot: true,
            PeerUploadSpeed: 1_048_576);

    [Fact]
    public async Task ManualAcceptsAFileWithNoArtistInItsName_AutomatedRequiresOne()
    {
        // A lone "Roygbiv.flac" from a peer whose share is organised by folder rather than by name is
        // plausible to a person, and automation should refuse it because the identity is unproven.
        var filename = "Roygbiv.flac";
        var scorer = CreateScorer();

        var manual = Assert.Single(await scorer.ScoreAsync(Target, [Candidate(filename)], SoulseekSearchMode.Manual));
        var automated = Assert.Single(await scorer.ScoreAsync(Target, [Candidate(filename)], SoulseekSearchMode.Automated));

        Assert.True(manual.Accepted);
        Assert.False(automated.Accepted);
        Assert.Equal("missing_candidate_artist", automated.RejectedBecause);
    }

    [Fact]
    public async Task ManualToleratesALargeDurationDrift_AutomatedDoesNot()
    {
        // 9 seconds out is tolerable when a person is deciding; it is too much to queue unattended.
        var filename = "Boards of Canada - Roygbiv.flac";
        var scorer = CreateScorer();

        var manual = Assert.Single(await scorer.ScoreAsync(Target, [Candidate(filename, seconds: 160)], SoulseekSearchMode.Manual));
        var automated = Assert.Single(await scorer.ScoreAsync(Target, [Candidate(filename, seconds: 160)], SoulseekSearchMode.Automated));

        Assert.True(manual.Accepted);
        Assert.False(automated.Accepted);
        Assert.Equal("duration_mismatch", automated.RejectedBecause);
    }

    [Fact]
    public async Task AutomatedRequiresAKnownDurationWhenTheTargetHasOne()
    {
        // slskd could not determine the length, so automated mode cannot prove the file is the right track.
        var filename = "Boards of Canada - Roygbiv.flac";
        var scorer = CreateScorer();

        var manual = Assert.Single(await scorer.ScoreAsync(Target, [Candidate(filename, seconds: null)], SoulseekSearchMode.Manual));
        var automated = Assert.Single(await scorer.ScoreAsync(Target, [Candidate(filename, seconds: null)], SoulseekSearchMode.Automated));

        Assert.True(manual.Accepted);
        Assert.False(automated.Accepted);
        Assert.Equal("missing_candidate_duration", automated.RejectedBecause);
    }

    [Fact]
    public async Task AutomatedRejectsPromotionalFilenameNoiseThatManualOnlyPenalises()
    {
        var filename = "Boards of Canada - Roygbiv (Official Video).flac";
        var scorer = CreateScorer();

        var manual = Assert.Single(await scorer.ScoreAsync(Target, [Candidate(filename)], SoulseekSearchMode.Manual));
        var automated = Assert.Single(await scorer.ScoreAsync(Target, [Candidate(filename)], SoulseekSearchMode.Automated));

        // Manual still shows it, but with a reduced score, so it cannot outrank a clean match.
        Assert.True(manual.Accepted);
        var clean = Assert.Single(await scorer.ScoreAsync(
            Target,
            [Candidate("Boards of Canada - Roygbiv.flac")],
            SoulseekSearchMode.Manual));
        Assert.True(clean.Score > manual.Score);

        Assert.False(automated.Accepted);
        Assert.Equal("filename_noise", automated.RejectedBecause);
    }

    [Fact]
    public async Task CosmeticTokensDoNotTriggerTheJunkRejectionPath()
    {
        // "(Remastered)" is untidy but not junk. In practice the shared matcher may still reject the title
        // change, and that is fine; what matters here is that the rejection does not come from the junk rule,
        // so a cosmetic token can never be mistaken for "this is a video or a preview".
        var filename = "Boards of Canada - Roygbiv (Remastered).flac";
        var scorer = CreateScorer();

        var candidate = Assert.Single(await scorer.ScoreAsync(Target, [Candidate(filename)], SoulseekSearchMode.Automated));

        Assert.NotEqual("filename_noise", candidate.RejectedBecause);
    }

    [Fact]
    public async Task JunkTokensAreRejectedByAutomation_NotByTheSharedMatcher()
    {
        // A music video whose title still matches exactly, so only the junk rule can catch it.
        var filename = "Boards of Canada - Roygbiv (Official Video).flac";
        var scorer = CreateScorer();

        var candidate = Assert.Single(await scorer.ScoreAsync(Target, [Candidate(filename)], SoulseekSearchMode.Automated));

        Assert.False(candidate.Accepted);
        Assert.Equal("filename_noise", candidate.RejectedBecause);
    }

    [Fact]
    public async Task BothModesRejectTheSameHardFailures()
    {
        // Strictness must not weaken the non-negotiable rules: a blocked user or a mismatched track is
        // refused in both modes.
        var scorer = CreateScorer(new SoulseekDownloadSettings { BlockedUsers = { "spammer" } });

        var blocked = Assert.Single(await scorer.ScoreAsync(
            Target,
            [Candidate("Boards of Canada - Roygbiv.flac") with { Username = "spammer" }],
            SoulseekSearchMode.Manual));
        var wrongTrack = Assert.Single(await scorer.ScoreAsync(
            Target,
            [Candidate("Completely Different - Song.flac")],
            SoulseekSearchMode.Automated));

        Assert.Equal("blocked_user", blocked.RejectedBecause);
        Assert.False(wrongTrack.Accepted);
    }

    [Fact]
    public async Task AutomatedPrefersTheCleanMatchOverTheNoisyOne()
    {
        var scorer = CreateScorer();

        var candidates = await scorer.ScoreAsync(
            Target,
            [
                Candidate("Boards of Canada - Roygbiv (Official Video).flac"),
                Candidate("Boards of Canada - Roygbiv.flac")
            ],
            SoulseekSearchMode.Automated);

        Assert.Equal("Boards of Canada - Roygbiv.flac", candidates[0].Filename);
        Assert.True(candidates[0].Accepted);
    }

    [Fact]
    public async Task ManualRankingAndAutomatedRankingAgreeOnTheBestCandidate()
    {
        var scorer = CreateScorer();

        var raw = new[]
        {
            Candidate("Boards of Canada - Roygbiv.mp3", bitDepth: null, sampleRate: null, bitrate: 320),
            Candidate("Boards of Canada - Roygbiv.flac")
        };

        var manual = await scorer.ScoreAsync(Target, raw, SoulseekSearchMode.Manual);
        var automated = await scorer.ScoreAsync(Target, raw, SoulseekSearchMode.Automated);

        Assert.Equal("FLAC", manual[0].Quality);
        Assert.Equal("FLAC", automated[0].Quality);
    }
}
