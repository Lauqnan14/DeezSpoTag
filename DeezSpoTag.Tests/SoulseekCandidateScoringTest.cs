using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Scoring, peer policy and filename parsing, exercised without touching a database.
/// </summary>
/// <remarks>
///     The peer policy's cooldown path needs a repository, so these tests inject a real repository over a
///     temporary database and rely on the settings service reading a temporary config root. The scoring rules
///     themselves are pure and are the main subject here.
/// </remarks>
[Collection("Settings Config Isolation")]
public sealed class SoulseekCandidateScoringTest : IDisposable
{
    private readonly string _tempRoot;
    private readonly SoulseekSettingsService _settings;
    private readonly DeezSpoTagSettingsService _appSettings;

    public SoulseekCandidateScoringTest()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-soulseek-scoring-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", _tempRoot);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_DATA_DIR", _tempRoot);
        _appSettings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        _settings = new SoulseekSettingsService(
            _appSettings,
            new DeezSpoTag.Services.Library.LibraryRepository(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                NullLogger<DeezSpoTag.Services.Library.LibraryRepository>.Instance),
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

    private SoulseekRepository CreateRepository()
    {
        var dbPath = Path.Join(_tempRoot, "queue.db");
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = $"Data Source={dbPath}"
            })
            .Build();

        return new SoulseekRepository(configuration, NullLogger<SoulseekRepository>.Instance);
    }

    private SoulseekResultScoringService CreateScorer(SoulseekDownloadSettings? settings = null)
        => CreateScorer(settings, new AlwaysAllowPeerPolicy());

    private SoulseekResultScoringService CreateScorer(
        SoulseekDownloadSettings? settings,
        ISoulseekPeerPolicyService peerPolicy)
    {
        _settings.Update(settings ?? new SoulseekDownloadSettings());
        return new SoulseekResultScoringService(
            _settings,
            peerPolicy,
            NullLogger<SoulseekResultScoringService>.Instance);
    }

    private void EnableOnlySoulseekQualities(params string[] codes)
    {
        var settings = _appSettings.LoadSettings();
        settings.Service = "custom";
        settings.DownloadEngineOrder = DownloadEngineOrderSettings.CreateDefault();
        settings.DownloadEngineOrder.Enabled = true;
        foreach (var engine in settings.DownloadEngineOrder.Engines)
        {
            engine.Enabled = engine.Engine == "soulseek";
            foreach (var quality in engine.Qualities)
            {
                quality.Enabled = engine.Enabled && codes.Contains(quality.Quality, StringComparer.OrdinalIgnoreCase);
            }
        }

        _appSettings.SaveSettings(settings);
    }

    /// <summary>
    ///     A scoring pass reads the cooldowns once, not once per candidate.
    /// </summary>
    /// <remarks>
    ///     The search loop scores everything it has accumulated on each poll, and a peer offering ten files
    ///     is ten candidates, so resolving each candidate's cooldown individually multiplied a single database
    ///     read by the candidate count and then by the number of polls. The snapshot is read per pass instead.
    /// </remarks>
    [Fact]
    public async Task CooldownsAreReadOncePerScoringPassRatherThanOncePerCandidate()
    {
        var policy = new CountingPeerPolicy();
        var scorer = CreateScorer(new SoulseekDownloadSettings(), policy);

        var candidates = new List<SoulseekRawCandidate>();
        for (var i = 0; i < 10; i++)
        {
            candidates.Add(Good(filename: $"Boards of Canada - Roygbiv {i}.flac"));
        }

        var scored = await scorer.ScoreAsync(Target, candidates);

        Assert.Equal(10, scored.Count);
        Assert.Equal(1, policy.CooldownReads);
    }

    /// <summary>
    ///     The snapshot must reach the policy unchanged, so a peer named as being in cooldown is still rejected.
    /// </summary>
    [Fact]
    public async Task APeerInTheCooldownSnapshotIsStillRejected()
    {
        var policy = new CountingPeerPolicy { InCooldown = "listener" };
        var scorer = CreateScorer(new SoulseekDownloadSettings(), policy);

        var scored = await scorer.ScoreAsync(Target, [Good()]);

        Assert.Equal("peer_in_cooldown", Assert.Single(scored).RejectedBecause);
    }

    private static SoulseekSearchTarget Target => new(
        Artist: "Boards of Canada",
        Title: "Roygbiv",
        Album: "Music Has the Right to Children",
        DurationMs: 151_000);

    /// <summary>A free-text term, which is what the search page sends when it has no track to search for.</summary>
    private static SoulseekSearchTarget Query(string term) => new(
        Artist: string.Empty,
        Title: term,
        Album: string.Empty);

    /// <summary>
    ///     The quality of the step being attempted decides what a search may accept.
    /// </summary>
    /// <remarks>
    ///     The queue walks the ladder one quality at a time, and each step asked the search for "the best
    ///     candidate" without saying which quality it was for. The scorer then used the user's global preference,
    ///     which is usually empty, so a FLAC attempt could take an MP3 off a peer that had one - and the item
    ///     reported a lossless request satisfied by a lossy file.
    /// </remarks>
    [Fact]
    public async Task AFlacStepDoesNotAcceptAnMp3Candidate()
    {
        var scorer = CreateScorer();

        var scored = await scorer.ScoreAsync(
            Target,
            [Good(filename: "Boards of Canada - Roygbiv.mp3", bitrate: 320, bitDepth: null, sampleRate: null)],
            SoulseekSearchMode.Automated,
            requiredQualityCode: "FLAC");

        var candidate = Assert.Single(scored);
        Assert.False(candidate.Accepted);
        Assert.Equal("below_requested_quality", candidate.RejectedBecause);
    }

    [Fact]
    public async Task ALowerRankedFlacStillBeatsAHigherScoringMp3DuringAFlacStep()
    {
        // The FLAC file here matches the track less well by name than the MP3 does, and still wins, because the
        // step asked for lossless. Without the constraint the MP3 would have been taken.
        var scorer = CreateScorer();

        var scored = await scorer.ScoreAsync(
            Target,
            [
                Good(filename: "Boards of Canada - Roygbiv.mp3", bitrate: 320, bitDepth: null, sampleRate: null),
                Good(filename: "Roygbiv.flac", bitrate: 940)
            ],
            SoulseekSearchMode.Manual,
            requiredQualityCode: "FLAC");

        var accepted = Assert.Single(scored.Where(candidate => candidate.Accepted));
        Assert.EndsWith(".flac", accepted.Filename, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameMp3IsAcceptedWhenTheStepAsksForMp3()
    {
        // The ladder must not be broken by the constraint: a later MP3 step has to be able to take the file the
        // FLAC step refused.
        var scorer = CreateScorer();

        var scored = await scorer.ScoreAsync(
            Target,
            [Good(filename: "Boards of Canada - Roygbiv.mp3", bitrate: 320, bitDepth: null, sampleRate: null)],
            SoulseekSearchMode.Automated,
            requiredQualityCode: "MP3_320");

        Assert.True(Assert.Single(scored).Accepted);
    }

    [Fact]
    public async Task AFlacStepStillAcceptsHiResFlac()
    {
        // The equivalence rule the constraint must not break: FLAC is a family of three codes, and a 24-bit
        // FLAC file is a better answer to a FLAC request, not a different one. Restricting the step to the
        // literal code would have rejected it and made the ladder fail with the file sitting right there.
        var scorer = CreateScorer();

        var scored = await scorer.ScoreAsync(
            Target,
            [Good(filename: "Boards of Canada - Roygbiv.flac", bitrate: 940, bitDepth: 24, sampleRate: 96_000)],
            SoulseekSearchMode.Automated,
            requiredQualityCode: "FLAC");

        var candidate = Assert.Single(scored);
        Assert.True(candidate.Accepted);
        Assert.Equal("FLAC_HI_RES", candidate.Quality);
    }

    [Fact]
    public async Task AnMp3StepAcceptsALosslessFileAsAnUpgrade()
    {
        // The other half of the same documented rule.
        var scorer = CreateScorer();

        var scored = await scorer.ScoreAsync(
            Target,
            [Good(filename: "Boards of Canada - Roygbiv.flac", bitrate: 940)],
            SoulseekSearchMode.Automated,
            requiredQualityCode: "MP3_320");

        Assert.True(Assert.Single(scored).Accepted);
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("  FLAC  ")]
    public async Task TheStepQualityIsNormalizedBeforeItIsApplied(string requested)
    {
        // A step quality arrives from the queue, so it can be any casing and carry stray spaces. An unnormalized
        // value would be read as UNKNOWN and behave as a different request, so this is a real distinction.
        var scorer = CreateScorer();

        var scored = await scorer.ScoreAsync(
            Target,
            [Good(filename: "Boards of Canada - Roygbiv.flac", bitrate: 940)],
            SoulseekSearchMode.Automated,
            requiredQualityCode: requested);

        Assert.True(Assert.Single(scored).Accepted);
    }

    [Fact]
    public async Task NoStepQualityLeavesTheExistingBehaviourAlone()
    {
        // A manual search with no step quality keeps accepting what it always accepted.
        var scorer = CreateScorer();

        var scored = await scorer.ScoreAsync(
            Target,
            [Good(filename: "Boards of Canada - Roygbiv.mp3", bitrate: 320, bitDepth: null, sampleRate: null)],
            SoulseekSearchMode.Manual);

        Assert.True(Assert.Single(scored).Accepted);
    }

    [Fact]
    public async Task AStepQualityDoesNotOpenTheUnknownQualityPolicy()
    {
        // Unknown quality is still rejected unless the user opted in; a step quality is not an opt-in.
        var scorer = CreateScorer();

        var scored = await scorer.ScoreAsync(
            Target,
            [Good(filename: "Boards of Canada - Roygbiv.bin", bitrate: null, bitDepth: null, sampleRate: null)],
            SoulseekSearchMode.Automated,
            requiredQualityCode: "FLAC");

        Assert.False(Assert.Single(scored).Accepted);
    }

    /// <summary>
    ///     The observed failure, end to end through the scorer.
    /// </summary>
    /// <remarks>
    ///     A correctly tagged Sauti Sol file was refused because the parser read "Live and Die in Afrika" as the
    ///     title. The matcher is strict on purpose, so the fix belongs in the parser and not in the thresholds;
    ///     this asserts the track is accepted from the shape a real ripper produces, with the quality and peer
    ///     constraints untouched.
    /// </remarks>
    [Theory]
    [InlineData("Sauti Sol - Live and Die in Afrika - 04 - Isabella.flac", "FLAC")]
    [InlineData("Sauti Sol_Live and Die in Afrika_04_Isabella.flac", "FLAC")]
    [InlineData("Sauti Sol - Live and Die in Afrika - 04 - Isabella.mp3", "MP3_320")]
    public async Task ARealSautiSolFileIsAcceptedOnceItsNameIsReadCorrectly(string filename, string requiredQuality)
    {
        var scorer = CreateScorer();
        var candidate = Good(
            filename: filename,
            size: requiredQuality == "FLAC" ? 40_000_000 : 13_764_977,
            bitrate: requiredQuality == "FLAC" ? 940 : 320,
            bitDepth: requiredQuality == "FLAC" ? 16 : null,
            sampleRate: requiredQuality == "FLAC" ? 44_100 : null);

        var scored = await scorer.ScoreAsync(
            new SoulseekSearchTarget("Sauti Sol", "Isabella", "Live and Die in Afrika"),
            [candidate],
            SoulseekSearchMode.Automated,
            requiredQualityCode: requiredQuality);

        var result = Assert.Single(scored);
        Assert.True(result.Accepted, $"rejected as {result.RejectedBecause}");
    }

    private static SoulseekRawCandidate Good(
        string username = "listener",
        string filename = "Boards of Canada - Roygbiv.flac",
        long size = 30_000_000,
        int? bitrate = 940,
        int? bitDepth = 16,
        int? sampleRate = 44_100,
        int? seconds = 151,
        long queue = 0,
        bool freeSlot = true,
        int uploadSpeed = 1_048_576)
        => new(
            username,
            filename,
            size,
            Path.GetExtension(filename),
            bitrate,
            bitDepth,
            sampleRate,
            seconds,
            IsVariableBitrate: false,
            IsLocked: false,
            queue,
            freeSlot,
            uploadSpeed);

    [Fact]
    public async Task CleanLosslessMatch_IsAcceptedAndScoredHigh()
    {
        var scorer = CreateScorer();

        var candidates = await scorer.ScoreAsync(Target, [Good()], SoulseekSearchMode.Automated);
        var candidate = Assert.Single(candidates);

        Assert.True(candidate.Accepted);
        Assert.Null(candidate.RejectedBecause);
        Assert.Equal("FLAC", candidate.Quality);
        Assert.Equal("flac", candidate.TierValue);
        Assert.InRange(candidate.Score, 0.7, 0.95);
    }

    [Fact]
    public async Task ResultsAreOrderedBestFirst()
    {
        var scorer = CreateScorer();

        var candidates = await scorer.ScoreAsync(
            Target,
            [
                Good(filename: "Boards of Canada - Roygbiv.mp3", bitrate: 128, bitDepth: null, sampleRate: null),
                Good(filename: "Boards of Canada - Roygbiv.flac", bitrate: 940)
            ],
            SoulseekSearchMode.Manual);

        Assert.Equal(2, candidates.Count);
        Assert.True(candidates[0].Score >= candidates[1].Score);
        Assert.Equal("FLAC", candidates[0].Quality);
    }

    [Theory]
    [InlineData("Some Other Artist - Roygbiv.flac", "artist_mismatch")]
    [InlineData("Boards of Canada - A Completely Different Song.flac", "title_mismatch")]
    public async Task IdentityMismatch_IsRejectedWithAReason(string filename, string expectedReason)
    {
        var scorer = CreateScorer();

        var candidate = Assert.Single(await scorer.ScoreAsync(Target, [Good(filename: filename)], SoulseekSearchMode.Manual));

        Assert.False(candidate.Accepted);
        Assert.Equal(expectedReason, candidate.RejectedBecause);
    }

    [Theory]
    [InlineData("Boards of Canada - Roygbiv (Live).flac")]
    [InlineData("Boards of Canada - Roygbiv (Remix).flac")]
    public async Task AlternateVersions_AreRejected(string filename)
    {
        var scorer = CreateScorer();

        var candidate = Assert.Single(await scorer.ScoreAsync(Target, [Good(filename: filename)], SoulseekSearchMode.Manual));

        // The shared matcher reports a version-tagged filename as a title mismatch on the metadata path; it
        // only distinguishes version_drift when an ISRC is involved. The outcome is what matters here: a
        // different version of the track is never queued as if it were the requested one.
        Assert.False(candidate.Accepted);
        Assert.Equal("title_mismatch", candidate.RejectedBecause);
    }

    [Fact]
    public async Task DurationMismatch_IsRejected()
    {
        var scorer = CreateScorer();

        var candidate = Assert.Single(await scorer.ScoreAsync(
            Target,
            [Good(seconds: 90)],
            SoulseekSearchMode.Manual));

        Assert.False(candidate.Accepted);
        Assert.Equal("duration_mismatch", candidate.RejectedBecause);
    }

    /// <summary>
    ///     A manual search of free text is a query, not a track request.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The page has no artist to search by, so the whole term used to be compared as if it were a
    ///         title. Searching for an artist then rejected every file by that artist with a title mismatch,
    ///         which is what the real database was full of: candidate after candidate, all
    ///         <c>title_mismatch</c>, all scoring nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task AnArtistQueryAcceptsTheFilesByThatArtist()
    {
        var scorer = CreateScorer();

        var candidate = Assert.Single(await scorer.ScoreAsync(
            Query("mejja"),
            [Good(filename: "Mejja - Thank Me Later - 12 - Cece.mp3", bitrate: 320, bitDepth: null, sampleRate: null, seconds: 220)],
            SoulseekSearchMode.Manual));

        Assert.True(candidate.Accepted, $"rejected as {candidate.RejectedBecause}");
        Assert.Null(candidate.RejectedBecause);
        Assert.True(candidate.Score > 0, "A matching query must leave a candidate with a score.");
    }

    [Fact]
    public async Task AQueryThatDescribesNothingIsRejectedAsAQueryMismatch()
    {
        var scorer = CreateScorer();

        var candidate = Assert.Single(await scorer.ScoreAsync(
            Query("kavinsky"),
            [Good(filename: "Mejja - Thank Me Later - 12 - Cece.mp3")],
            SoulseekSearchMode.Manual));

        Assert.False(candidate.Accepted);
        Assert.Equal("query_mismatch", candidate.RejectedBecause);
    }

    [Fact]
    public async Task TheFileTheQueryNamesOutranksTheFileItOnlyContains()
    {
        var scorer = CreateScorer();

        var scored = await scorer.ScoreAsync(
            Query("mejja"),
            [
                Good(filename: "Someone Else - Thank Me Later.mp3", username: "listener"),
                Good(filename: "Mejja - Thank Me Later.mp3", username: "listener")
            ],
            SoulseekSearchMode.Manual);

        var named = Assert.Single(scored, candidate => candidate.Accepted);
        Assert.Equal("Mejja - Thank Me Later.mp3", named.Filename);
        Assert.Equal("query_mismatch", Assert.Single(scored, candidate => !candidate.Accepted).RejectedBecause);
    }

    /// <summary>
    ///     The query grade is a manual-search tool and must not become a hole in automated selection, where a
    ///     real track is known and the strict rules are the only thing standing between the ladder and a
    ///     wrong file.
    /// </summary>
    [Fact]
    public async Task AutomatedSelectionNeverUsesTheLooserQueryGrade()
    {
        var scorer = CreateScorer();

        var candidate = Assert.Single(await scorer.ScoreAsync(
            Query("mejja"),
            [Good(filename: "Mejja - Thank Me Later.mp3")],
            SoulseekSearchMode.Automated));

        // No artist on the target means there is nothing to match against, which automated mode has always
        // refused outright; the query grade must not turn that into an acceptance.
        Assert.False(candidate.Accepted);
        Assert.NotEqual("query_match", candidate.RejectedBecause);
    }

    [Fact]
    public async Task UnknownQuality_IsRejectedByDefault()
    {
        var scorer = CreateScorer();

        var candidate = Assert.Single(await scorer.ScoreAsync(
            Target,
            [Good(filename: "Boards of Canada - Roygbiv.opus", bitrate: 128, bitDepth: null, sampleRate: null)],
            SoulseekSearchMode.Automated));

        Assert.False(candidate.Accepted);
        Assert.Equal("unknown_quality", candidate.RejectedBecause);
    }

    [Fact]
    public async Task UnknownQuality_IsAcceptedOnlyWhenExplicitlyAllowed()
    {
        var scorer = CreateScorer(new SoulseekDownloadSettings { AllowUnknownQuality = true });

        var candidate = Assert.Single(await scorer.ScoreAsync(
            Target,
            [Good(filename: "Boards of Canada - Roygbiv.opus", bitrate: 128, bitDepth: null, sampleRate: null)],
            SoulseekSearchMode.Manual));

        // Accepted as a candidate, but it must never be treated as a high quality match.
        Assert.True(candidate.Accepted);
        Assert.Null(candidate.CanonicalRank);
        Assert.True(candidate.Score <= 0.5, $"unknown quality scored {candidate.Score}");
    }

    [Fact]
    public async Task BlockedUser_IsRejectedBeforeAnythingElse()
    {
        var scorer = CreateScorer(new SoulseekDownloadSettings { BlockedUsers = { "spammer" } });

        var candidate = Assert.Single(await scorer.ScoreAsync(
            Target,
            [Good(username: "Spammer", filename: "Boards of Canada - Wrong Song.flac")],
            SoulseekSearchMode.Manual));

        Assert.False(candidate.Accepted);
        Assert.Equal("blocked_user", candidate.RejectedBecause);
    }

    [Fact]
    public async Task BlockedFilenamePattern_IsRejected()
    {
        // The blocklist lives in the peer policy, so this uses the real service rather than a stub. Otherwise
        // the test would be asserting against the stub's behaviour instead of the rule.
        _settings.Update(new SoulseekDownloadSettings { BlockedFilenamePatterns = { "unreleased" } });
        var scorer = new SoulseekResultScoringService(
            _settings,
            new SoulseekPeerPolicyService(
                _settings,
                CreateRepository(),
                NullLogger<SoulseekPeerPolicyService>.Instance),
            NullLogger<SoulseekResultScoringService>.Instance);

        var candidate = Assert.Single(await scorer.ScoreAsync(
            Target,
            [Good(filename: "Boards of Canada - Roygbiv (UNRELEASED).flac")],
            SoulseekSearchMode.Manual));

        Assert.False(candidate.Accepted);
        Assert.Equal("blocked_filename_pattern", candidate.RejectedBecause);
    }

    [Fact]
    public async Task BlockedFilenamePattern_SupportsSimpleWildcards()
    {
        _settings.Update(new SoulseekDownloadSettings { BlockedFilenamePatterns = { "*/live/*" } });
        var policy = new SoulseekPeerPolicyService(
            _settings,
            CreateRepository(),
            NullLogger<SoulseekPeerPolicyService>.Instance);

        Assert.True(policy.IsBlockedFilename("@@u\\share\\live\\Roygbiv.flac"));
        Assert.False(policy.IsBlockedFilename("@@u\\share\\studio\\Roygbiv.flac"));
    }

    [Fact]
    public async Task NonAudioFile_IsRejected()
    {
        var scorer = CreateScorer();

        var candidate = Assert.Single(await scorer.ScoreAsync(
            Target,
            [Good(filename: "Boards of Canada - Roygbiv - folder.jpg", bitrate: null, bitDepth: null, sampleRate: null)],
            SoulseekSearchMode.Manual));

        Assert.False(candidate.Accepted);
        Assert.Equal("filename_noise", candidate.RejectedBecause);
    }

    [Fact]
    public async Task SourceEnabledMp3IsAcceptedWithoutAnExtensionWhitelist()
    {
        EnableOnlySoulseekQualities("MP3_320");
        var scorer = CreateScorer();

        var candidate = Assert.Single(await scorer.ScoreAsync(
            Target,
            [Good(filename: "Boards of Canada - Roygbiv.mp3", bitrate: 320, bitDepth: null, sampleRate: null)],
            SoulseekSearchMode.Manual));

        Assert.True(candidate.Accepted);
    }

    [Fact]
    public async Task QualityOutsideTheSourceSelection_IsRejected()
    {
        EnableOnlySoulseekQualities("FLAC");
        var scorer = CreateScorer();

        var candidate = Assert.Single(await scorer.ScoreAsync(
            Target,
            [Good(filename: "Boards of Canada - Roygbiv.mp3", bitrate: 320, bitDepth: null, sampleRate: null)],
            SoulseekSearchMode.Manual));

        Assert.False(candidate.Accepted);
        Assert.Equal("quality_not_allowed", candidate.RejectedBecause);
    }

    [Fact]
    public async Task LosslessStep_RejectsLossyCandidates()
    {
        var scorer = CreateScorer();

        var candidate = Assert.Single(await scorer.ScoreAsync(
            Target,
            [Good(filename: "Boards of Canada - Roygbiv.mp3", bitrate: 320, bitDepth: null, sampleRate: null)],
            SoulseekSearchMode.Manual,
            requiredQualityCode: "FLAC"));

        Assert.False(candidate.Accepted);
        Assert.Equal("below_requested_quality", candidate.RejectedBecause);
    }

    [Fact]
    public async Task LossyStep_RejectsCandidatesBelowTheRequestedBitrate()
    {
        var scorer = CreateScorer();

        var low = Assert.Single(await scorer.ScoreAsync(
            Target,
            [Good(filename: "Boards of Canada - Roygbiv.mp3", bitrate: 128, bitDepth: null, sampleRate: null)],
            SoulseekSearchMode.Manual,
            requiredQualityCode: "MP3_320"));
        var high = Assert.Single(await scorer.ScoreAsync(
            Target,
            [Good(filename: "Boards of Canada - Roygbiv.mp3", bitrate: 320, bitDepth: null, sampleRate: null)],
            SoulseekSearchMode.Manual,
            requiredQualityCode: "MP3_320"));

        Assert.False(low.Accepted);
        Assert.Equal("below_requested_quality", low.RejectedBecause);
        Assert.True(high.Accepted);
    }

    [Fact]
    public async Task LossyStep_AcceptsLosslessAsAnUpgrade()
    {
        var scorer = CreateScorer();

        var candidate = Assert.Single(await scorer.ScoreAsync(Target, [Good()], SoulseekSearchMode.Manual, requiredQualityCode: "MP3_320"));

        Assert.True(candidate.Accepted);
    }

    [Fact]
    public async Task AHealthyPeerOutranksABusySlotlessOneAtTheSameQuality()
    {
        var scorer = CreateScorer();

        var candidates = await scorer.ScoreAsync(
            Target,
            [
                Good(username: "busy", queue: 9, freeSlot: false, uploadSpeed: 1024),
                Good(username: "healthy", queue: 0, freeSlot: true, uploadSpeed: 2_097_152)
            ],
            SoulseekSearchMode.Manual);

        Assert.Equal("healthy", candidates[0].Username);
        Assert.True(candidates[0].Score > candidates[1].Score);
    }

    [Fact]
    public async Task AHighQualityFileOutranksALowQualityOneAtTheSamePeer()
    {
        var scorer = CreateScorer();

        var candidates = await scorer.ScoreAsync(
            Target,
            [
                Good(filename: "Boards of Canada - Roygbiv.mp3", bitrate: 128, bitDepth: null, sampleRate: null),
                Good(filename: "Boards of Canada - Roygbiv.flac", bitrate: 1400, bitDepth: 24, sampleRate: 96_000)
            ],
            SoulseekSearchMode.Manual);

        // A 24-bit/96kHz FLAC reports the hi-res band, which is what puts it ahead of the 128 kbps MP3.
        Assert.Equal("FLAC_HI_RES", candidates[0].Quality);
    }

    [Fact]
    public async Task HigherBitDepthAndSampleRateScoreHigherThanPlainFlac()
    {
        var scorer = CreateScorer();

        var candidates = await scorer.ScoreAsync(
            Target,
            [
                Good(filename: "Boards of Canada - Roygbiv.flac", bitDepth: 16, sampleRate: 44_100),
                Good(filename: "Boards of Canada - Roygbiv (24bit).flac", bitDepth: 24, sampleRate: 192_000)
            ],
            SoulseekSearchMode.Manual);

        // A FLAC with no hi-res evidence maps onto the shared flac tier; 24/192 maps onto Max Hi-Res.
        Assert.Equal(120, candidates[0].CanonicalRank);
        Assert.Equal(70, candidates[1].CanonicalRank);
        Assert.True(candidates[0].Score > candidates[1].Score);
    }

    [Fact]
    public async Task SelectBest_OnlyConsidersAcceptedCandidates()
    {
        var scorer = CreateScorer();

        var candidates = await scorer.ScoreAsync(
            Target,
            [Good(username: "bad", filename: "Wrong Artist - Wrong Song.flac")],
            SoulseekSearchMode.Manual);

        Assert.All(candidates, candidate => Assert.False(candidate.Accepted));
        Assert.Null(scorer.SelectBest(candidates));
    }

    [Fact]
    public void SelectBest_PrefersAdvertisedSpeedBeforeMatchScore()
    {
        var scorer = CreateScorer();
        var slowerHigherScore = Accepted("slow", uploadSpeed: 100_000, score: 0.99, queue: 0);
        var fasterLowerScore = Accepted("fast", uploadSpeed: 5_000_000, score: 0.81, queue: 4);

        Assert.Equal("fast", scorer.SelectBest([slowerHigherScore, fasterLowerScore])?.Username);
    }

    [Fact]
    public void SelectBest_UsesScoreThenQueueAndStableInputOrderWhenSpeedsTie()
    {
        var scorer = CreateScorer();
        var unknown = Accepted("unknown", uploadSpeed: 0, score: 0.99, queue: 0);
        var lowerScore = Accepted("lower-score", uploadSpeed: 1_000_000, score: 0.80, queue: 0);
        var busy = Accepted("busy", uploadSpeed: 1_000_000, score: 0.90, queue: 3);
        var first = Accepted("first", uploadSpeed: 1_000_000, score: 0.90, queue: 1);
        var same = Accepted("same", uploadSpeed: 1_000_000, score: 0.90, queue: 1);

        Assert.Equal("first", scorer.SelectBest([unknown, lowerScore, busy, first, same])?.Username);
        Assert.Equal("unknown", scorer.SelectBest([unknown])?.Username);
    }

    [Fact]
    public void SelectBest_NeverLetsRejectedSpeedBypassEligibility()
    {
        var scorer = CreateScorer();
        var rejected = Accepted("rejected-fast", uploadSpeed: int.MaxValue, score: 1, queue: 0) with
        {
            Accepted = false,
            RejectedBecause = "blocked_user"
        };
        var accepted = Accepted("accepted", uploadSpeed: 10, score: 0.5, queue: 2);

        Assert.Equal("accepted", scorer.SelectBest([rejected, accepted])?.Username);
    }

    private static SoulseekCandidate Accepted(string username, int uploadSpeed, double score, long queue)
        => new(
            Good(username: username, uploadSpeed: uploadSpeed, queue: queue),
            "FLAC",
            "FLAC",
            TierValue: "flac",
            CanonicalRank: 70,
            Score: score,
            IdentityConfidence: score,
            Accepted: true);

    [Fact]
    public async Task CandidateMapsOntoTheSharedQualityTierSoDedupeCanRankIt()
    {
        var scorer = CreateScorer();

        var candidate = Assert.Single(await scorer.ScoreAsync(Target, [Good()], SoulseekSearchMode.Automated));

        Assert.Equal("flac", candidate.TierValue);
        Assert.Equal(DeezSpoTag.Services.Download.QualityCatalog.GetLibraryFolderCanonicalRank("flac"), candidate.CanonicalRank);
    }

    [Fact]
    public void FilenameParser_ReadsTheCommonReleaseShapes()
    {
        var dashed = SoulseekFilenameParser.Parse("@@user\\share\\Boards of Canada - Roygbiv.flac", 151);
        Assert.Equal("Roygbiv", dashed.Title);
        Assert.Equal("Boards of Canada", dashed.Artist);
        Assert.Equal(151_000, dashed.DurationMs);

        var numbered = SoulseekFilenameParser.Parse("03 - Roygbiv.flac", 151);
        Assert.Equal("Roygbiv", numbered.Title);
        Assert.Equal(3, numbered.TrackNumber);

        var threePart = SoulseekFilenameParser.Parse("Boards of Canada - Roygbiv - Music Has the Right to Children.flac");
        Assert.Equal("Roygbiv", threePart.Title);
        Assert.Equal("Boards of Canada", threePart.Artist);
        Assert.Equal("Music Has the Right to Children", threePart.Album);
    }

    [Fact]
    public void FilenameParser_StripsDurationAndYearMarkers()
    {
        var facts = SoulseekFilenameParser.Parse("Roygbiv (2011) [3:04].flac");

        Assert.DoesNotContain("2011", facts.Title);
        Assert.DoesNotContain("3:04", facts.Title);
    }

    [Fact]
    public void FilenameParser_FlagsJunkButNotMerelyUntidyNames()
    {
        Assert.True(SoulseekFilenameParser.Parse("Roygbiv - cover.jpg").IsNoise);
        Assert.True(SoulseekFilenameParser.Parse("A1B2C3D4E5F6A7B8.flac").IsNoise);
        Assert.True(SoulseekFilenameParser.Parse(string.Empty).IsNoise);

        // Promotional and bracketing noise is recorded, but is a score penalty rather than a hard reject.
        var promo = SoulseekFilenameParser.Parse("Boards of Canada - Roygbiv (Official Video).flac");
        Assert.True(promo.IsNoise);
        Assert.DoesNotContain("non_audio_file", promo.NoiseReasons!);
    }

    /// <summary>
    ///     The search text carries words, not punctuation.
    /// </summary>
    /// <remarks>
    ///     Soulseek matches a token string, and a peer indexes its files as plain words. Injecting " - " into the
    ///     query therefore asks for a phrase that almost no peer stores: the observed
    ///     <c>Nick Drake - Time Has Told Me</c> search returned nothing while <c>Nick Drake Time Has Told Me</c>
    ///     returns the file sitting in front of the reader. A separator is a formatting nicety that costs a
    ///     search.
    /// </remarks>
    [Fact]
    public void SearchText_IsArtistAndTitleOnly()
    {
        Assert.Equal("Boards of Canada Roygbiv", SoulseekSearchService.BuildSearchText(Target));

        Assert.Equal("Roygbiv", SoulseekSearchService.BuildSearchText(Target with { Artist = string.Empty }));

        // Whitespace inside the artist is collapsed, because peers match against a normalized token string.
        Assert.Equal(
            "Boards of Canada Roygbiv",
            SoulseekSearchService.BuildSearchText(Target with { Artist = "  Boards   of   Canada " }));
    }

    [Fact]
    public void TheSearchTextCarriesNoSeparatorToken()
    {
        var text = SoulseekSearchService.BuildSearchText(Target);

        Assert.DoesNotContain("-", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheQuerySequenceIsArtistThenTitle()
    {
        Assert.Equal(
            ["Boards of Canada Roygbiv", "Roygbiv"],
            SoulseekSearchService.BuildSearchTexts(Target));
    }

    [Theory]
    [InlineData("", "Roygbiv")]
    [InlineData("Roygbiv", "Roygbiv")]
    [InlineData("   ", "Roygbiv")]
    public void AQuerySequenceWithNothingToRelaxIsOneQuery(string artist, string expected)
    {
        // The second query is only ever the title. If there is no artist, or the artist is the title, there is
        // nothing to relax and the sequence must collapse to a single query rather than repeat itself.
        var texts = SoulseekSearchService.BuildSearchTexts(Target with { Artist = artist });

        Assert.Equal([expected], texts);
    }

    [Fact]
    public void TheQuerySequenceNeverFallsBackToArtistOnly()
    {
        // An artist-only query on a network this size returns thousands of unrelated files, which is worse than
        // no result: the downloader would then have to pick one, and it would pick the wrong one.
        var texts = SoulseekSearchService.BuildSearchTexts(Target);

        Assert.DoesNotContain("Boards of Canada", texts);
        Assert.All(texts, text => Assert.Contains("Roygbiv", text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Counts how often the cooldowns were read, so the per-pass snapshot can be asserted.
    /// </summary>
    private sealed class CountingPeerPolicy : ISoulseekPeerPolicyService
    {
        public int CooldownReads { get; private set; }

        public string? InCooldown { get; init; }

        public Task<SoulseekPeerDecision> EvaluateAsync(SoulseekRawCandidate candidate, CancellationToken cancellationToken = default)
        {
            CooldownReads++;
            return Task.FromResult(new SoulseekPeerDecision(true));
        }

        public Task<SoulseekPeerDecision> EvaluateAsync(
            SoulseekRawCandidate candidate,
            IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)> cooldownsInEffect,
            CancellationToken cancellationToken = default)
            => Task.FromResult(
                InCooldown is not null
                && string.Equals(InCooldown, candidate.Username, StringComparison.OrdinalIgnoreCase)
                    ? new SoulseekPeerDecision(false, "peer_in_cooldown")
                    : new SoulseekPeerDecision(true));

        public bool IsBlockedFilename(string? filename) => false;

        public Task RecordFailureAsync(string username, string reason, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RecordSuccessAsync(string username, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)>> GetCooldownsAsync(
            CancellationToken cancellationToken = default)
        {
            CooldownReads++;
            return Task.FromResult<IReadOnlyList<(string, DateTimeOffset)>>(
                InCooldown is null
                    ? []
                    : [(InCooldown, DateTimeOffset.UtcNow.AddMinutes(5))]);
        }

        public Task<int> CleanupExpiredCooldownsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }

    private sealed class AlwaysAllowPeerPolicy : ISoulseekPeerPolicyService
    {
        public Task<SoulseekPeerDecision> EvaluateAsync(SoulseekRawCandidate candidate, CancellationToken cancellationToken = default)
            => Task.FromResult(new SoulseekPeerDecision(true));

        public Task<SoulseekPeerDecision> EvaluateAsync(
            SoulseekRawCandidate candidate,
            IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)> cooldownsInEffect,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SoulseekPeerDecision(true));

        public bool IsBlockedFilename(string? filename) => false;

        public Task RecordFailureAsync(string username, string reason, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RecordSuccessAsync(string username, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)>> GetCooldownsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<(string, DateTimeOffset)>>([]);

        public Task<int> CleanupExpiredCooldownsAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
