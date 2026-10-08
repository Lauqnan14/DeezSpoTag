using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Models.Soulseek;
using DeezSpoTag.Integrations.Soulseek;
using DeezSpoTag.Services.Download.Soulseek;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Tests for the unit of the search timeout sent across the slskd boundary.
/// </summary>
/// <remarks>
///     <para>
///         DeezSpoTag stores the timeout in seconds, and the local poll deadline is a
///         <see cref="TimeSpan" />, so seconds are the right unit everywhere inside this application.
///     </para>
///     <para>
///         The one exception is the slskd request. slskd forwards the value to Soulseek.NET's
///         <c>SearchOptions.SearchTimeout</c>, which is documented in milliseconds and defaults to 15000.
///         Sending seconds therefore hands slskd a number a thousand times too small: a configured 30
///         seconds arrived as 30 milliseconds, and the search was closed almost immediately, so almost no
///         peers had a chance to respond. The conversion belongs at that boundary and nowhere else.
///     </para>
/// </remarks>
[Collection("Settings Config Isolation")]
public sealed class SoulseekSearchServiceTest : IDisposable
{
    private const string TempPrefix = "deezspotag-soulseek-timeout-";

    private readonly string _tempRoot;
    private readonly string _dbPath;
    private readonly CapturingSlskdClient _client = new();

    public SoulseekSearchServiceTest()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), TempPrefix + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Join(_tempRoot, "queue.db");
        Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", _tempRoot);
        Environment.SetEnvironmentVariable("DEEZSPOTAG_DATA_DIR", _tempRoot);
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

    /// <summary>
    ///     The repository a service under test writes through, and a test reads back with.
    /// </summary>
    /// <remarks>
    ///     Held as a field so a test can read the persisted search after the service has finished with it. Each
    ///     repository instance opens its own connections against the same file, so a separate instance reads what
    ///     was committed - which is exactly what a reload does, and the point of several assertions here.
    /// </remarks>
    private SoulseekRepository CreateRepository()
        => new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Queue"] = $"Data Source={_dbPath}" })
                .Build(),
            NullLogger<SoulseekRepository>.Instance);

    private SoulseekSettingsService CreateSettings(int searchTimeoutSeconds)
    {
        var service = new SoulseekSettingsService(
            new DeezSpoTag.Services.Settings.DeezSpoTagSettingsService(
                NullLogger<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>.Instance),
            new DeezSpoTag.Services.Library.LibraryRepository(
                new ConfigurationBuilder().Build(),
                NullLogger<DeezSpoTag.Services.Library.LibraryRepository>.Instance),
            NullLogger<SoulseekSettingsService>.Instance);

        service.Update(new SoulseekDownloadSettings { SearchTimeoutSeconds = searchTimeoutSeconds });
        return service;
    }

    private SoulseekSearchService CreateService(SoulseekSettingsService settings)
        => CreateService(settings, _client);

    private SoulseekSearchService CreateService(SoulseekSettingsService settings, ISlskdClient client)
        => CreateService(settings, client, new StubScoringService());

    private SoulseekSearchService CreateService(
        SoulseekSettingsService settings,
        ISlskdClient client,
        ISoulseekResultScoringService scoring)
        => CreateService(settings, client, scoring, null);

    /// <summary>
    ///     The service under test, optionally recording what it publishes.
    /// </summary>
    /// <remarks>
    ///     A realtime publisher is needed because a failed search is only distinguishable from a finished one by
    ///     the final event it publishes. The repository is created once and shared with the caller so a test can
    ///     read back what was persisted rather than trusting the returned outcome alone.
    /// </remarks>
    private SoulseekSearchService CreateService(
        SoulseekSettingsService settings,
        ISlskdClient client,
        ISoulseekResultScoringService scoring,
        ISoulseekRealtimePublisher? realtime)
        => new(
            client,
            new StubCredentialProvider(new SlskdCredentials("http://127.0.0.1:5030", string.Empty)),
            new StubConnectionService(),
            scoring,
            settings,
            CreateRepository(),
            NullLogger<SoulseekSearchService>.Instance,
            realtime);

    /// <summary>
    ///     The quality of the step being attempted has to arrive at the scorer.
    /// </summary>
    /// <remarks>
    ///     The queue walks one quality at a time, and each rung asks the search for candidates. If the search does
    ///     not pass the rung's quality down, the scorer answers with the user's standing preference - usually
    ///     none - and a FLAC step will happily take an MP3.
    /// </remarks>
    [Fact]
    public async Task TheStepQualityReachesCandidateScoring()
    {
        var scoring = new StubScoringService();
        var service = CreateService(CreateSettings(30), new CountingTickClient(ticksBeforeComplete: 0), scoring);

        await service.ObserveAsync(
            Target(),
            Guid.NewGuid(),
            "artist - title",
            SoulseekSearchMode.Automated,
            queueUuid: "queue-1",
            CancellationToken.None,
            requiredQualityCode: "FLAC");

        Assert.Equal("FLAC", scoring.LastRequiredQualityCode);
    }

    [Fact]
    public async Task AManualSearchWithNoStepQualityLeavesTheScorerUnconstrained()
    {
        var scoring = new StubScoringService();
        var service = CreateService(CreateSettings(30), new CountingTickClient(ticksBeforeComplete: 0), scoring);

        await service.ObserveAsync(
            Target(),
            Guid.NewGuid(),
            "artist - title",
            SoulseekSearchMode.Manual,
            queueUuid: null,
            CancellationToken.None);

        Assert.Null(scoring.LastRequiredQualityCode);
    }

    /// <summary>
    ///     The responses endpoint is read once per search, however long the search runs.
    /// </summary>
    /// <remarks>
    ///     slskd keeps the SearchResponse objects in memory for the life of the search and writes them into
    ///     the persisted search only when it finalizes, so GET /searches/{id}/responses is empty until then.
    ///     Polling it on every tick re-fetched and re-deserialized an empty list dozens of times per search
    ///     and produced no candidates at all. The live counts come from the status object instead.
    /// </remarks>
    [Fact]
    public async Task ResponsesAreFetchedOnceNoMatterHowManyTimesTheSearchIsPolled()
    {
        var client = new CountingTickClient(ticksBeforeComplete: 25);
        var service = CreateService(CreateSettings(30), client);

        var outcome = await service.ObserveAsync(
            Target(),
            Guid.NewGuid(),
            "artist - title",
            SoulseekSearchMode.Manual,
            queueUuid: null,
            CancellationToken.None);

        Assert.True(client.StatusPolls > 1, "The search was never polled more than once, so nothing was proven.");
        Assert.Equal(1, client.ResponseFetches);
        Assert.Single(outcome.Candidates);
    }

    /// <summary>
    ///     A search whose finalized responses could not be read is left in slskd rather than deleted.
    /// </summary>
    /// <remarks>
    ///     The remote search holds the only copy of the results. Deleting it in a finally block, which is
    ///     what the old cleanup did, threw away the very thing a retry would need.
    /// </remarks>
    [Fact]
    public async Task ASearchWhoseResponsesCannotBeReadIsLeftInSlskd()
    {
        var client = new CountingTickClient(ticksBeforeComplete: 0) { ResponsesThrow = true };
        var service = CreateService(CreateSettings(30), client);

        var outcome = await service.ObserveAsync(
            Target(),
            Guid.NewGuid(),
            "artist - title",
            SoulseekSearchMode.Manual,
            queueUuid: null,
            CancellationToken.None);

        Assert.Empty(outcome.Candidates);
        Assert.Equal(0, client.DeleteCalls);
    }

    /// <summary>
    ///     A search whose responses could not be read is reported as a failure, not as an empty success.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the reproduced defect. The read was caught and logged, and then the search continued as if it
    ///         had worked: candidates were scored from whatever polling happened to collect, the record was written
    ///         as completed, and a final "search finished" event went out. The reader was told a search had returned
    ///         nothing for a search that had peers answering and files it never saw.
    ///     </para>
    ///     <para>
    ///         So the outcome carries a failure code, is neither completed nor timed out, and keeps the peer and file
    ///         counts observed while polling - which is the only record that the network did answer.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ASearchWhoseResponsesCannotBeReadIsReportedAsFailed()
    {
        var client = new CountingTickClient(ticksBeforeComplete: 3) { ResponsesThrow = true };
        var service = CreateService(CreateSettings(30), client);
        var searchId = Guid.NewGuid();

        var outcome = await service.ObserveAsync(
            Target(),
            searchId,
            "artist - title",
            SoulseekSearchMode.Manual,
            queueUuid: "queue-failed",
            CancellationToken.None);

        Assert.True(outcome.Failed);
        Assert.Equal(SoulseekSearchService.ResponseRetrievalFailedCode, outcome.ErrorCode);
        Assert.Equal(SoulseekSearchService.ResponseRetrievalFailedMessage, outcome.Error);

        // Neither of the two words that would read as a finished search.
        Assert.False(outcome.Completed);
        Assert.False(outcome.TimedOut);

        // The counts observed while polling survive, so this is not reclassified as a search nobody answered.
        Assert.True(outcome.ResponseCount > 0, "The observed peer count must survive a read failure.");
        Assert.Empty(outcome.Candidates);

        // And it is still a failure after a reload, which is the part a log line alone never gave.
        var record = await CreateRepository().GetSearchAsync(searchId, CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal(SoulseekSearchService.ResponseRetrievalFailedCode, record!.ErrorCode);
        Assert.False(record.Completed);
        Assert.False(record.TimedOut);
        Assert.Equal(outcome.ResponseCount, record.ResponseCount);
        Assert.Equal(client.StatusPolls * 7, record.FileCount);

        // No candidate rows are written, because nothing was ever scored. A persisted candidate would look like a
        // completed search with results.
        Assert.Empty(await CreateRepository().GetCandidatesAsync(searchId, CancellationToken.None));
    }

    [Fact]
    public async Task RetrievalFailurePreservesPreviouslyRecordedCandidates()
    {
        var repository = CreateRepository();
        var searchId = Guid.NewGuid();
        await repository.RecordSearchStartedAsync(searchId, "artist - title", SoulseekSearchMode.Manual, "queue-retained");
        var candidate = new SoulseekCandidate(new SoulseekRawCandidate("peer", "album/track.flac", 1000, ".flac"),
            "FLAC", "FLAC", Score: 0.9, Accepted: true);
        await repository.ReplaceCandidatesAsync(searchId, [candidate]);
        var client = new CountingTickClient(ticksBeforeComplete: 0) { ResponsesThrow = true };
        var outcome = await CreateService(CreateSettings(30), client).ObserveAsync(
            Target(), searchId, "artist - title", SoulseekSearchMode.Manual, "queue-retained", CancellationToken.None);
        Assert.True(outcome.Failed);
        Assert.Equal(candidate.Filename, Assert.Single(await repository.GetCandidatesAsync(searchId)).Filename);
        Assert.Equal(0, client.DeleteCalls);
    }

    /// <summary>
    ///     A failed search publishes a final failure event rather than a finished one.
    /// </summary>
    /// <remarks>
    ///     The realtime contract is what tells a live panel to stop. Without a code on the final event the client
    ///     merges an empty candidate list and keeps polling a search that is already over.
    /// </remarks>
    [Fact]
    public async Task ASearchWhoseResponsesCannotBeReadPublishesAFailedFinalEvent()
    {
        var client = new CountingTickClient(ticksBeforeComplete: 0) { ResponsesThrow = true };
        var realtime = new RecordingSearchPublisher();
        var service = CreateService(CreateSettings(30), client, new StubScoringService(), realtime);

        await service.ObserveAsync(
            Target(),
            Guid.NewGuid(),
            "artist - title",
            SoulseekSearchMode.Manual,
            queueUuid: "queue-failed",
            CancellationToken.None);

        var final = Assert.Single(realtime.Progresses.Where(progress => progress.Final));
        Assert.Equal(SoulseekSearchStage.Failed, final.Stage);
        Assert.Equal(SoulseekSearchService.ResponseRetrievalFailedCode, final.ErrorCode);
        Assert.False(final.Completed);
        Assert.False(final.TimedOut);

        var result = Assert.Single(realtime.Outcomes);
        Assert.True(result.Failed);
    }

    /// <summary>
    ///     Automation stops at the failed search instead of relaxing the query.
    /// </summary>
    /// <remarks>
    ///     A relaxation is only justified by the network genuinely having nothing. Asking again after a broken read
    ///     repeats the same question, and then the relaxed query's empty result is reported as the first search's
    ///     outcome - so a transport failure ends up described as "nobody has this track".
    /// </remarks>
    [Fact]
    public async Task AutomatedSearchDoesNotRelaxTheQueryAfterAReadFailure()
    {
        var client = new CountingTickClient(ticksBeforeComplete: 0) { ResponsesThrow = true, CountSearches = true };
        var service = CreateService(CreateSettings(30), client);

        await Assert.ThrowsAsync<SoulseekUnavailableException>(() => service.SearchAsync(
            Target(),
            SoulseekSearchMode.Automated,
            "queue-automated",
            CancellationToken.None));

        // Exactly one query was issued. A second one would be the relaxed title-only search.
        Assert.Equal(1, client.StartSearchCalls);
    }

    /// <summary>
    ///     A search whose responses were read is cleaned up, as before.
    /// </summary>
    [Fact]
    public async Task ASearchWhoseResponsesWereReadIsRemovedFromSlskd()
    {
        var client = new CountingTickClient(ticksBeforeComplete: 0);
        var service = CreateService(CreateSettings(30), client);

        await service.ObserveAsync(
            Target(),
            Guid.NewGuid(),
            "artist - title",
            SoulseekSearchMode.Manual,
            queueUuid: null,
            CancellationToken.None);

        Assert.Equal(1, client.DeleteCalls);
    }

    /// <summary>
    ///     A running search is visible to a client, with slskd's real peer count and no candidates yet.
    /// </summary>
    /// <remarks>
    ///     Because the responses are not published until slskd finalizes, the live candidate list is empty for
    ///     the whole search. The entry in the live map is what stops the results endpoint answering 404 for a
    ///     search that is plainly running, and the counts are what stop it reporting zero peers.
    /// </remarks>
    [Fact]
    public async Task ARunningSearchIsVisibleWithItsRealPeerCountAndNoCandidates()
    {
        var client = new CountingTickClient(ticksBeforeComplete: 25);
        var service = CreateService(CreateSettings(30), client);
        var searchId = Guid.NewGuid();

        var observing = service.ObserveAsync(
            Target(),
            searchId,
            "artist - title",
            SoulseekSearchMode.Manual,
            queueUuid: null,
            CancellationToken.None);

        var sawRunning = false;
        for (var i = 0; i < 40 && !observing.IsCompleted; i++)
        {
            if (SoulseekSearchService.LiveResults.TryGetValue(searchId, out var live))
            {
                sawRunning = true;
                Assert.Empty(live);
                Assert.True(
                    SoulseekSearchService.LiveCounts.TryGetValue(searchId, out var counts) && counts.ResponseCount > 0,
                    "A running search reported no peers even though slskd had reported some.");
                break;
            }

            await Task.Delay(25);
        }

        await observing;

        Assert.True(sawRunning, "The running search was never visible to a client watching its progress.");
        Assert.False(SoulseekSearchService.LiveResults.ContainsKey(searchId), "The live set outlived the search.");
        Assert.False(SoulseekSearchService.LiveCounts.ContainsKey(searchId), "The live counts outlived the search.");
    }

    private static SoulseekSearchTarget Target()
        => new("Test Artist", "Test Title", "Test Album", 210_000, "GBTEST1234567", 2019);
    [Fact]
    public async Task DeadlineCancelsRemoteSearchAndWaitsForFinalization()
    {
        var client = new CancelFinalizingClient();
        var service = new SoulseekSearchService(client,
            new StubCredentialProvider(new SlskdCredentials("http://localhost:5030", "")),
            new StubConnectionService(), new StubScoringService(), CreateSettings(30),
            CreateRepository(), NullLogger<SoulseekSearchService>.Instance);
        var method = typeof(SoulseekSearchService).GetMethod("PollUntilCompleteAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var task = (Task<(bool Completed, int FileCount)>)method.Invoke(service,
            new object[] { new SlskdCredentials("http://localhost:5030", ""), Guid.NewGuid(), TimeSpan.Zero, CancellationToken.None })!;
        var result = await task;
        Assert.True(client.Cancelled);
        Assert.True(client.FinalizationObserved);
        Assert.False(result.Completed);
        Assert.Equal(298, result.FileCount);
    }

    [Fact]
    public async Task ObserveReadsFinalResponsesBeforeDeletingSearch()
    {
        var client = new FinalResponsesClient();
        var service = new SoulseekSearchService(client,
            new StubCredentialProvider(new SlskdCredentials("http://localhost:5030", "")),
            new StubConnectionService(), new StubScoringService(), CreateSettings(30),
            CreateRepository(), NullLogger<SoulseekSearchService>.Instance);
        var outcome = await service.ObserveAsync(new SoulseekSearchTarget("", "nora dean"),
            Guid.NewGuid(), "nora dean", SoulseekSearchMode.Manual, null, CancellationToken.None);
        Assert.Equal(1, outcome.ResponseCount);
        Assert.True(client.DeletedAfterFinalRead);
    }

    private sealed class FinalResponsesClient : CapturingSlskdClientBase
    {
        private bool _complete;
        private bool _readFinal;
        public bool DeletedAfterFinalRead { get; private set; }
        public override Task<SlskdSearch?> GetSearchAsync(SlskdCredentials credentials, Guid searchId, bool includeResponses = false, CancellationToken cancellationToken = default)
        { _complete = true; return Task.FromResult<SlskdSearch?>(new SlskdSearch { Id = searchId, IsComplete = true, FileCount = 1 }); }
        public override Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
        {
            _readFinal = _complete;
            return Task.FromResult<IReadOnlyList<SlskdSearchResponse>>(_complete
                ? [new SlskdSearchResponse { Username = "peer", Files = [new SlskdFile { Filename = "nora dean.flac" }] }]
                : []);
        }
        public override Task<bool> DeleteSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
        { DeletedAfterFinalRead = _readFinal; return Task.FromResult(true); }
    }

    private sealed class CancelFinalizingClient : CapturingSlskdClientBase
    {
        public bool Cancelled { get; private set; }
        public bool FinalizationObserved { get; private set; }
        public override Task CancelSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
        { Cancelled = true; return Task.CompletedTask; }
        public override Task<SlskdSearch?> GetSearchAsync(SlskdCredentials credentials, Guid searchId, bool includeResponses = false, CancellationToken cancellationToken = default)
        {
            FinalizationObserved = Cancelled;
            return Task.FromResult<SlskdSearch?>(new SlskdSearch { Id = searchId, FileCount = 298, IsComplete = Cancelled });
        }
    }

    /// <summary>
    ///     The load-bearing assertion: a configured timeout of 30 seconds must reach slskd as 30000.
    /// </summary>
    [Fact]
    public async Task AConfiguredTimeoutIsSentToSlskdInMilliseconds()
    {
        var service = CreateService(CreateSettings(searchTimeoutSeconds: 30));

        await service.SearchAsync(
            new SoulseekSearchTarget("Boards of Canada", "Roygbiv"),
            SoulseekSearchMode.Manual,
            queueUuid: null,
            CancellationToken.None);

        // The first request is the subject here; a search that finds nothing is followed by the bounded
        // title-only query, which carries the same timeout.
        Assert.NotEmpty(_client.StartedSearches);
        Assert.Equal(30000, _client.StartedSearches[0].SearchTimeout);
    }

    /// <summary>
    ///     The local poll deadline must stay in seconds. Only the value crossing the slskd boundary is
    ///     converted, so a 30 second setting still ends the local poll after 30 seconds.
    /// </summary>
    [Fact]
    public async Task TheOtherSearchRequestFieldsAreUnchanged()
    {
        var settings = CreateSettings(searchTimeoutSeconds: 30);
        settings.Update(new SoulseekDownloadSettings
        {
            MaximumPeerQueueLength = 5,
            MinimumPeerUploadSpeedBytesPerSecond = 2048
        });

        var service = CreateService(settings);
        await service.SearchAsync(
            new SoulseekSearchTarget("Boards of Canada", "Roygbiv"),
            SoulseekSearchMode.Manual,
            queueUuid: null,
            CancellationToken.None);

        // Plain words, and no separator: a peer indexes words, so "Boards of Canada - Roygbiv" asks for a
        // phrase almost none of them store.
        Assert.NotEmpty(_client.StartedSearches);
        var request = _client.StartedSearches[0];
        Assert.Equal("Boards of Canada Roygbiv", request.SearchText);
        Assert.Equal(5, request.MaximumPeerQueueLength);
        Assert.Equal(2048, request.MinimumPeerUploadSpeed);
        Assert.True(request.FilterResponses);
    }

    /// <summary>
    ///     The conversion scales with the setting, so the wire value is always a thousand times the
    ///     configured seconds.
    /// </summary>
    /// <remarks>
    ///     Deliberately restricted to in-range values. The settings layer normalises an out-of-range timeout
    ///     to the default before this service ever sees it, so the service's own 5-600 clamp is a defensive
    ///     second bound rather than something settings can reach. That behaviour is covered by the settings
    ///     tests, not here.
    /// </remarks>
    [Theory]
    [InlineData(5, 5000)]
    [InlineData(30, 30000)]
    [InlineData(45, 45000)]
    [InlineData(600, 600000)]
    public async Task TheWireValueScalesWithTheConfiguredSeconds(int configuredSeconds, int expectedMilliseconds)
    {
        var service = CreateService(CreateSettings(configuredSeconds));

        await service.SearchAsync(
            new SoulseekSearchTarget("Boards of Canada", "Roygbiv"),
            SoulseekSearchMode.Manual,
            queueUuid: null,
            CancellationToken.None);

        // The first request is the one that matters: this is about the value on the wire, and a search that finds
        // nothing is followed by the bounded title-only query, which sends the same timeout.
        Assert.NotEmpty(_client.StartedSearches);
        Assert.Equal(expectedMilliseconds, _client.StartedSearches[0].SearchTimeout);
        Assert.All(_client.StartedSearches, request => Assert.Equal(expectedMilliseconds, request.SearchTimeout));
    }

    /// <summary>
    ///     The regression test for the second bug: the local poll deadline must outlast the timeout slskd was
    ///     given.
    /// </summary>
    /// <remarks>
    ///     The local deadline used to equal slskd's own timeout, so the two expired at the same instant.
    ///     The poll gave up, the responses were requested a moment before slskd had attached them, an empty
    ///     collection came back, and the search was deleted in the finally block. A real "nora dean" search
    ///     reported 497 files on the slskd side and produced zero candidates here.
    /// </remarks>
    [Fact]
    public async Task ResponsesAreStillRetrievableWhenSlskdFinalisesAtItsOwnTimeout()
    {
        // 5 seconds is the lowest the settings clamp allows, so this is the cheapest faithful reproduction.
        var client = new SlowFinalizingSlskdClient(fileCount: 497);
        var service = new SoulseekSearchService(
            client,
            new StubCredentialProvider(new SlskdCredentials("http://127.0.0.1:5030", string.Empty)),
            new StubConnectionService(),
            new StubScoringService(),
            CreateSettings(searchTimeoutSeconds: 5),
            CreateRepository(),
            NullLogger<SoulseekSearchService>.Instance);

        var outcome = await service.SearchAsync(
            new SoulseekSearchTarget("Nora Dean", "Tempo"),
            SoulseekSearchMode.Manual,
            queueUuid: null,
            CancellationToken.None);

        // slskd finished at its own timeout; the responses must still be there when we ask.
        Assert.False(outcome.TimedOut, "The search was recorded as timed out even though slskd completed it.");
        Assert.True(outcome.Completed);
        // One peer responded. The point is that it is non-zero: before the fix this was an empty collection.
        Assert.Equal(1, outcome.ResponseCount);
        Assert.Single(outcome.Candidates);
        Assert.Equal("finalizing-peer", outcome.Candidates[0].Username);

        // And the local poll really did keep going past the remote timeout.
        Assert.True(
            client.ResponseRequestsBeforeComplete > 0,
            "The poll never saw the search as open, so this test did not exercise the race.");
    }

    /// <summary>
    ///     A search that reports itself complete before its responses are readable must still yield them.
    /// </summary>
    /// <remarks>
    ///     This is the live failure: slskd reported 497 files, the poll read "complete" on its first pass, the
    ///     responses came back empty, and the UI showed no candidates. Believing a first empty read is what
    ///     turned a successful search into nothing.
    /// </remarks>
    [Fact]
    public async Task ResponsesThatLagTheCompletionSignalAreStillCollected()
    {
        var client = new LateResponsesSlskdClient(responseDelay: TimeSpan.FromSeconds(2));
        var service = new SoulseekSearchService(
            client,
            new StubCredentialProvider(new SlskdCredentials("http://192.168.28.24:5030", string.Empty)),
            new StubConnectionService(),
            new StubScoringService(),
            CreateSettings(searchTimeoutSeconds: 5),
            CreateRepository(),
            NullLogger<SoulseekSearchService>.Instance);

        var outcome = await service.SearchAsync(
            new SoulseekSearchTarget("Nora Dean", "Tempo"),
            SoulseekSearchMode.Manual,
            queueUuid: null,
            CancellationToken.None);

        Assert.True(client.EmptyReadsBeforeResponses > 0, "The empty first read was never exercised.");
        Assert.Equal(1, outcome.ResponseCount);
        Assert.Single(outcome.Candidates);
        Assert.Equal("late-peer", outcome.Candidates[0].Username);
    }

    /// <summary>
    ///     A stage must never reach the UI as a bare number.
    /// </summary>
    [Fact]
    public void HubEventsPublishStagesAsNamesRatherThanEnumNumbers()
    {
        var realtime = ReadRepoFile("DeezSpoTag.Web", "Services", "SoulseekRealtimeService.cs");

        Assert.DoesNotContain("progress.Stage,", realtime, StringComparison.Ordinal);
        Assert.Contains("stage = progress.Stage.ToString().ToLowerInvariant()", realtime, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The DTO's documentation claimed seconds, which is what let the wrong unit ship. Pin the contract
    ///     in the comment itself so the next reader is not misled.
    /// </summary>
    [Fact]
    public void TheDtoDocumentsTheWireUnitAsMilliseconds()
    {
        var dtos = ReadRepoFile("DeezSpoTag.Integrations", "Soulseek", "SlskdDtos.cs");

        Assert.Contains("search timeout in <b>milliseconds</b>", dtos, StringComparison.Ordinal);
        Assert.DoesNotContain("search timeout in seconds", dtos, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Services")))
            {
                return File.ReadAllText(Path.Join(new[] { current.FullName }.Concat(parts).ToArray()));
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>
    ///     Records the requests it is given and reports every search as already complete, so a test never
    ///     waits on a poll loop and never touches the network.
    /// </summary>
    private sealed class CapturingSlskdClient : ISlskdClient
    {
        public List<SlskdSearchRequest> StartedSearches { get; } = new();

        public Task<SlskdServerState> GetServerStateAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => Task.FromResult(new SlskdServerState { IsConnected = true, IsLoggedIn = true, Username = "tester" });

        public Task<SlskdServerState> ConnectAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => Task.FromResult(new SlskdServerState { IsConnected = true, IsLoggedIn = true, Username = "tester" });

        public Task DisconnectAsync(SlskdCredentials credentials, string? message = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<SlskdSearch> StartSearchAsync(SlskdCredentials credentials, SlskdSearchRequest request, CancellationToken cancellationToken = default)
        {
            StartedSearches.Add(request);
            return Task.FromResult(CompletedSearch(request.SearchText));
        }

        public Task<SlskdSearch?> GetSearchAsync(SlskdCredentials credentials, Guid searchId, bool includeResponses = false, CancellationToken cancellationToken = default)
            => Task.FromResult<SlskdSearch?>(CompletedSearch("Roygbiv"));

        public Task<IReadOnlyList<SlskdSearch>> ListSearchesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SlskdSearch>>([CompletedSearch("Roygbiv")]);

        public Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SlskdSearchResponse>>([]);

        public Task CancelSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> DeleteSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<SlskdUserStatus?> GetUserStatusAsync(SlskdCredentials credentials, string username, CancellationToken cancellationToken = default)
            => Task.FromResult<SlskdUserStatus?>(null);

        public Task<IReadOnlyList<SlskdDirectory>> BrowseUserAsync(SlskdCredentials credentials, string username, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SlskdDirectory>>([]);

        public Task<IReadOnlyList<SlskdDirectory>> BrowseUserDirectoryAsync(SlskdCredentials credentials, string username, string? directory, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SlskdDirectory>>([]);

        public Task<IReadOnlyList<SlskdTransfer>> EnqueueDownloadsAsync(SlskdCredentials credentials, string username, IReadOnlyList<SlskdQueueDownload> downloads, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SlskdTransfer>>([]);

        public Task<IReadOnlyList<SlskdTransfer>> ListDownloadsAsync(SlskdCredentials credentials, bool includeRemoved = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SlskdTransfer>>([]);

        public Task<SlskdTransfer?> GetDownloadAsync(SlskdCredentials credentials, string username, Guid transferId, CancellationToken cancellationToken = default)
            => Task.FromResult<SlskdTransfer?>(null);

        public Task<int?> GetDownloadPositionAsync(SlskdCredentials credentials, string username, Guid transferId, CancellationToken cancellationToken = default)
            => Task.FromResult<int?>(null);

        public Task CancelDownloadAsync(SlskdCredentials credentials, string username, Guid transferId, bool remove = false, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ClearCompletedDownloadsAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SlskdShare>> ListSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SlskdShare>>([]);

        public Task<IReadOnlyList<SlskdDirectory>> BrowseSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SlskdDirectory>>([]);

        public Task RescanSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private static SlskdSearch CompletedSearch(string searchText) => new()
        {
            Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),

            // endedAt is what marks a search complete, so the poll loop exits on the first pass.
            SearchText = searchText,

            // IsComplete is a stored property, not computed from EndedAt; the JSON reader is what derives
            // one from the other. Set both so the poll loop exits on its first pass.
            IsComplete = true,
            EndedAt = DateTimeOffset.UtcNow,
            FileCount = 0,
            ResponseCount = 0
        };
    }

    /// <summary>
    ///     Reproduces slskd's behaviour: the search stays open until the timeout it was given, and the
    ///     responses only exist once it has closed. This is the shape that made the real search return
    ///     nothing, because the local poll deadline expired at the same instant slskd was still finalising.
    /// </summary>
    private sealed class SlowFinalizingSlskdClient : ISlskdClient
    {
        private readonly int _fileCount;
        /// <summary>
        ///     How long slskd takes to attach responses and stamp endedAt after its own timeout expires.
        ///     Small, but enough to lose the race when the local deadline is the same value.
        /// </summary>
        private static readonly TimeSpan FinalisationLatency = TimeSpan.FromMilliseconds(400);

        private DateTimeOffset _startedAt = DateTimeOffset.MinValue;
        private TimeSpan _remoteTimeout = TimeSpan.Zero;

        private bool HasFinalised => DateTimeOffset.UtcNow - _startedAt >= _remoteTimeout + FinalisationLatency;

        public SlowFinalizingSlskdClient(int fileCount) => _fileCount = fileCount;

        public int ResponseRequestsBeforeComplete { get; private set; }

        public Task<SlskdServerState> GetServerStateAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => Task.FromResult(new SlskdServerState { IsConnected = true, IsLoggedIn = true, Username = "tester" });

        public Task<SlskdServerState> ConnectAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => Task.FromResult(new SlskdServerState { IsConnected = true, IsLoggedIn = true, Username = "tester" });

        public Task DisconnectAsync(SlskdCredentials credentials, string? message = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<SlskdSearch> StartSearchAsync(SlskdCredentials credentials, SlskdSearchRequest request, CancellationToken cancellationToken = default)
        {
            _remoteTimeout = TimeSpan.FromMilliseconds(request.SearchTimeout ?? 0);
            _startedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(Open());
        }

        public Task<IReadOnlyList<SlskdSearch>> ListSearchesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SlskdSearch>>([Open()]);

        public Task<SlskdSearch?> GetSearchAsync(SlskdCredentials credentials, Guid searchId, bool includeResponses = false, CancellationToken cancellationToken = default)
        {
            if (!HasFinalised)
            {
                ResponseRequestsBeforeComplete++;
                return Task.FromResult<SlskdSearch?>(Open());
            }

            return Task.FromResult<SlskdSearch?>(new SlskdSearch
            {
                Id = SearchId,
                SearchText = "nora dean",
                IsComplete = true,
                EndedAt = DateTimeOffset.UtcNow,
                FileCount = _fileCount,
                ResponseCount = 1
            });
        }

        public Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
            => !HasFinalised
                ? Task.FromResult<IReadOnlyList<SlskdSearchResponse>>([])
                : Task.FromResult<IReadOnlyList<SlskdSearchResponse>>(
                [
                    new SlskdSearchResponse
                    {
                        Username = "finalizing-peer",
                        FileCount = _fileCount,
                        Files =
                        [
                            new SlskdFile
                            {
                                Filename = "@@Music\\nora dean\\tempo.flac",
                                Size = 40_000_000,
                                BitRate = 1411,
                                BitDepth = 24,
                                SampleRate = 96000,
                                Length = 421
                            }
                        ]
                    }
                ]);

        private SlskdSearch Open() => new()
        {
            Id = SearchId,
            SearchText = "nora dean",
            IsComplete = false,
            StartedAt = _startedAt,
            FileCount = 0,
            ResponseCount = 0
        };

        private static Guid SearchId => Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        public Task CancelSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> DeleteSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<SlskdUserStatus?> GetUserStatusAsync(SlskdCredentials credentials, string username, CancellationToken cancellationToken = default) => Task.FromResult<SlskdUserStatus?>(null);

        public Task<IReadOnlyList<SlskdDirectory>> BrowseUserAsync(SlskdCredentials credentials, string username, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SlskdDirectory>>([]);

        public Task<IReadOnlyList<SlskdDirectory>> BrowseUserDirectoryAsync(SlskdCredentials credentials, string username, string? directory, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SlskdDirectory>>([]);

        public Task<IReadOnlyList<SlskdTransfer>> EnqueueDownloadsAsync(SlskdCredentials credentials, string username, IReadOnlyList<SlskdQueueDownload> downloads, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SlskdTransfer>>([]);

        public Task<IReadOnlyList<SlskdTransfer>> ListDownloadsAsync(SlskdCredentials credentials, bool includeRemoved = false, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SlskdTransfer>>([]);

        public Task<SlskdTransfer?> GetDownloadAsync(SlskdCredentials credentials, string username, Guid transferId, CancellationToken cancellationToken = default) => Task.FromResult<SlskdTransfer?>(null);

        public Task<int?> GetDownloadPositionAsync(SlskdCredentials credentials, string username, Guid transferId, CancellationToken cancellationToken = default) => Task.FromResult<int?>(null);

        public Task CancelDownloadAsync(SlskdCredentials credentials, string username, Guid transferId, bool remove = false, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearCompletedDownloadsAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SlskdShare>> ListSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SlskdShare>>([]);

        public Task<IReadOnlyList<SlskdDirectory>> BrowseSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SlskdDirectory>>([]);

        public Task RescanSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    ///     Reports the search complete immediately, but only publishes its responses after a delay. This is
    ///     the shape that produced zero candidates on a live search: the poll believed the search was done
    ///     and read the responses before they were readable.
    /// </summary>
    private sealed class LateResponsesSlskdClient : CapturingSlskdClientBase
    {
        private readonly TimeSpan _responseDelay;
        private DateTimeOffset _startedAt = DateTimeOffset.MinValue;

        public LateResponsesSlskdClient(TimeSpan responseDelay) => _responseDelay = responseDelay;

        public int EmptyReadsBeforeResponses { get; private set; }

        public override Task<SlskdSearch> StartSearchAsync(SlskdCredentials credentials, SlskdSearchRequest request, CancellationToken cancellationToken = default)
        {
            _startedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(new SlskdSearch { Id = Id, SearchText = request.SearchText, IsComplete = true, EndedAt = _startedAt, FileCount = 497, ResponseCount = 5 });
        }

        public override Task<SlskdSearch?> GetSearchAsync(SlskdCredentials credentials, Guid searchId, bool includeResponses = false, CancellationToken cancellationToken = default)
            => Task.FromResult<SlskdSearch?>(new SlskdSearch { Id = Id, SearchText = "nora dean", IsComplete = true, EndedAt = _startedAt, FileCount = 497, ResponseCount = 5 });

        public override Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
        {
            if (DateTimeOffset.UtcNow - _startedAt < _responseDelay)
            {
                EmptyReadsBeforeResponses++;
                return Task.FromResult<IReadOnlyList<SlskdSearchResponse>>([]);
            }

            return Task.FromResult<IReadOnlyList<SlskdSearchResponse>>(
            [
                new SlskdSearchResponse
                {
                    Username = "late-peer",
                    FileCount = 497,
                    Files = [new SlskdFile { Filename = "@@Music\\Nora Dean\\Tempo.flac", Size = 41_000_000, BitRate = 1411, BitDepth = 24, SampleRate = 96000, Length = 421 }]
                }
            ]);
        }

        private static Guid Id => Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
    }

    /// <summary>Shared plumbing so the late-response fake only has to override what it cares about.</summary>
    /// <summary>
    ///     A client whose search stays running for a fixed number of status polls, then completes.
    /// </summary>
    /// <remarks>
    ///     It counts both HTTP calls, because the whole point of the transport fix is that the number of
    ///     status polls is no longer paid for in response fetches.
    /// </remarks>
    private sealed class CountingTickClient : CapturingSlskdClientBase
    {
        private readonly int _ticksBeforeComplete;

        public CountingTickClient(int ticksBeforeComplete) => _ticksBeforeComplete = ticksBeforeComplete;

        public int ResponseFetches { get; private set; }

        public int StatusPolls { get; private set; }

        public int DeleteCalls { get; private set; }

        public bool ResponsesThrow { get; init; }

        /// <summary>Counts how many queries the service issued, so query relaxation is observable.</summary>
        public bool CountSearches { get; init; }

        /// <summary>
        ///     Every search this client was asked to start, in order.
        /// </summary>
        /// <remarks>
        ///     The count is what proves a read failure does not trigger the relaxed title-only query. That query is a
        ///     legitimate second attempt after an empty result and an illegitimate one after a broken read, and the
        ///     only difference between them is whether a second search is issued.
        /// </remarks>
        public List<string> StartSearchTexts { get; } = [];

        /// <summary>How many searches were started at all.</summary>
        public int StartSearchCalls
        {
            get
            {
                if (CountSearches)
                {
                    return StartSearchTexts.Count;
                }

                // Not counting: report one, because a caller that did not ask to count is asserting "a search ran",
                // not "exactly one ran".
                return 1;
            }
        }

        public override Task<SlskdSearch> StartSearchAsync(SlskdCredentials credentials, SlskdSearchRequest request, CancellationToken cancellationToken = default)
        {
            StartSearchTexts.Add(request.SearchText);
            return base.StartSearchAsync(credentials, request, cancellationToken);
        }

        public override Task<SlskdSearch?> GetSearchAsync(SlskdCredentials credentials, Guid searchId, bool includeResponses = false, CancellationToken cancellationToken = default)
        {
            StatusPolls++;
            var complete = StatusPolls > _ticksBeforeComplete;
            return Task.FromResult<SlskdSearch?>(
                new SlskdSearch
                {
                    Id = searchId,
                    IsComplete = complete,
                    ResponseCount = StatusPolls * 3,
                    FileCount = StatusPolls * 7
                });
        }

        public override Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
        {
            ResponseFetches++;
            if (ResponsesThrow)
            {
                throw new InvalidOperationException("the finalized responses could not be read");
            }

            return Task.FromResult<IReadOnlyList<SlskdSearchResponse>>(
            [
                new SlskdSearchResponse
                {
                    Username = "peer-one",
                    Files = [new SlskdFile { Filename = "Artist - Title.flac", Size = 1024, Extension = ".flac" }]
                }
            ]);
        }

        public override Task<bool> DeleteSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return Task.FromResult(true);
        }
    }

    /// <summary>
    ///     Captures what a search published, so a failure can be told from a finished search.
    /// </summary>
    /// <remarks>
    ///     The live panel's only evidence about a finished search is the final event, and a failed one carries no
    ///     candidates. Without the code on that event the two are the same message, which is what let a retrieval
    ///     failure read as "search finished, 0 candidates".
    /// </remarks>
    private sealed class RecordingSearchPublisher : ISoulseekRealtimePublisher
    {
        public List<SoulseekSearchProgress> Progresses { get; } = [];

        public List<SoulseekSearchOutcome> Outcomes { get; } = [];

        public void PublishSearchUpdate(SoulseekSearchProgress progress) => Progresses.Add(progress);

        public void PublishSearchResult(SoulseekSearchOutcome outcome) => Outcomes.Add(outcome);

        public void PublishConnectionState(SoulseekConnectionStatus status) { }

        public void PublishEngineHealth(string state, string? message) { }

        public void PublishDownloadUpdate(SoulseekDownloadProgress progress) { }

        public void PublishShareSyncUpdate(SoulseekShareReconciliation reconciliation) { }

        public void PublishShareScanUpdate(SoulseekShareScanStatus status) { }

        public void PublishImportUpdate(SoulseekImportUpdate update) { }
    }

    public abstract class CapturingSlskdClientBase : ISlskdClient
    {
        public virtual Task<SlskdServerState> GetServerStateAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default) => Task.FromResult(new SlskdServerState { IsConnected = true, IsLoggedIn = true, Username = "tester" });
        public virtual Task<SlskdServerState> ConnectAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default) => Task.FromResult(new SlskdServerState { IsConnected = true, IsLoggedIn = true, Username = "tester" });
        public virtual Task DisconnectAsync(SlskdCredentials credentials, string? message = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public virtual Task<SlskdSearch> StartSearchAsync(SlskdCredentials credentials, SlskdSearchRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new SlskdSearch());
        public virtual Task<SlskdSearch?> GetSearchAsync(SlskdCredentials credentials, Guid searchId, bool includeResponses = false, CancellationToken cancellationToken = default) => Task.FromResult<SlskdSearch?>(null);
        public virtual Task<IReadOnlyList<SlskdSearch>> ListSearchesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SlskdSearch>>([]);
        public virtual Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SlskdSearchResponse>>([]);
        public virtual Task CancelSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public virtual Task<bool> DeleteSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public virtual Task<SlskdUserStatus?> GetUserStatusAsync(SlskdCredentials credentials, string username, CancellationToken cancellationToken = default) => Task.FromResult<SlskdUserStatus?>(null);
        public virtual Task<IReadOnlyList<SlskdDirectory>> BrowseUserAsync(SlskdCredentials credentials, string username, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SlskdDirectory>>([]);
        public virtual Task<IReadOnlyList<SlskdDirectory>> BrowseUserDirectoryAsync(SlskdCredentials credentials, string username, string? directory, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SlskdDirectory>>([]);
        public virtual Task<IReadOnlyList<SlskdTransfer>> EnqueueDownloadsAsync(SlskdCredentials credentials, string username, IReadOnlyList<SlskdQueueDownload> downloads, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SlskdTransfer>>([]);
        public virtual Task<IReadOnlyList<SlskdTransfer>> ListDownloadsAsync(SlskdCredentials credentials, bool includeRemoved = false, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SlskdTransfer>>([]);
        public virtual Task<SlskdTransfer?> GetDownloadAsync(SlskdCredentials credentials, string username, Guid transferId, CancellationToken cancellationToken = default) => Task.FromResult<SlskdTransfer?>(null);
        public virtual Task<int?> GetDownloadPositionAsync(SlskdCredentials credentials, string username, Guid transferId, CancellationToken cancellationToken = default) => Task.FromResult<int?>(null);
        public virtual Task CancelDownloadAsync(SlskdCredentials credentials, string username, Guid transferId, bool remove = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public virtual Task ClearCompletedDownloadsAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public virtual Task<IReadOnlyList<SlskdShare>> ListSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SlskdShare>>([]);
        public virtual Task<IReadOnlyList<SlskdDirectory>> BrowseSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SlskdDirectory>>([]);
        public virtual Task RescanSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    ///     An over-specific query may try once more, and only in the one direction that is safe.
    /// </summary>
    /// <remarks>
    ///     The observed search for "Nick Drake - Time Has Told Me" returned nothing at all while the file was
    ///     sitting on the network, because peers index words and not that punctuation. So the first query is plain
    ///     words, and when it finds nothing accepted the title alone is tried once. There is no third attempt: an
    ///     artist-only query on a network this size returns thousands of unrelated files, and a downloader asked
    ///     to choose from those will choose wrongly.
    /// </remarks>
    [Fact]
    public async Task ZeroCandidatePrimarySearchRetriesWithTitleOnly()
    {
        var client = new SequencedSearchClient();
        var scoring = new RecordingScoringService();
        var service = CreateService(CreateSettings(30), client, scoring);

        var outcome = await service.SearchAsync(
            new SoulseekSearchTarget("Nick Drake", "Time Has Told Me"),
            SoulseekSearchMode.Automated,
            queueUuid: "87c643923d264bd69d4aefbec28b902f",
            CancellationToken.None,
            requiredQualityCode: "FLAC");

        // Two searches, in the order the sequence fixes: the words first, then the title alone.
        Assert.Equal(["Nick Drake Time Has Told Me", "Time Has Told Me"], client.StartedSearchTexts);
        Assert.NotNull(outcome.Best);
        Assert.Equal("Nintendude94", outcome.Best!.Username);
        Assert.Contains("Time Has Told Me", outcome.Best.Filename, StringComparison.Ordinal);

        // The rung the caller asked for applies to every attempt: relaxing the query must not relax the quality.
        Assert.Equal(["FLAC", "FLAC"], scoring.RequiredQualityCodes);
    }

    [Fact]
    public async Task AcceptedPrimarySearchDoesNotRunRelaxedQuery()
    {
        // A second search here would be a duplicate remote search on every successful download.
        var client = new SequencedSearchClient { AnswerFirstSearch = true };
        var service = CreateService(CreateSettings(30), client, new RecordingScoringService());

        var outcome = await service.SearchAsync(
            new SoulseekSearchTarget("Nick Drake", "Time Has Told Me"),
            SoulseekSearchMode.Automated,
            queueUuid: "87c643923d264bd69d4aefbec28b902f",
            CancellationToken.None,
            requiredQualityCode: "FLAC");

        Assert.Single(client.StartedSearchTexts);
        Assert.NotNull(outcome.Best);
    }

    /// <summary>
    ///     A search with nothing to search for is refused rather than sent.
    /// </summary>
    /// <remarks>
    ///     With no artist and no title the query list is empty. An empty query sent to the network would return
    ///     whatever the index holds and then be scored against a target that names no track, which is not a
    ///     search that can succeed - it is the network's whole library arriving as candidates for nothing.
    /// </remarks>
    [Fact]
    public async Task ATargetWithNeitherArtistNorTitleIsRefusedWithoutSearching()
    {
        var client = new SequencedSearchClient();
        var service = CreateService(CreateSettings(30), client, new RecordingScoringService());

        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(
            new SoulseekSearchTarget(string.Empty, string.Empty),
            SoulseekSearchMode.Manual,
            queueUuid: null,
            CancellationToken.None));

        Assert.Empty(client.StartedSearchTexts);
    }

    /// <summary>
    ///     An slskd that answers nothing to the first query and the known file to the second.
    /// </summary>
    private sealed class SequencedSearchClient : CapturingSlskdClientBase
    {
        public List<string> StartedSearchTexts { get; } = [];

        public bool AnswerFirstSearch { get; init; }

        public override Task<SlskdSearch> StartSearchAsync(SlskdCredentials credentials, SlskdSearchRequest request, CancellationToken cancellationToken = default)
        {
            StartedSearchTexts.Add(request.SearchText);
            return Task.FromResult(new SlskdSearch { Id = Guid.NewGuid(), SearchText = request.SearchText });
        }

        public override Task<SlskdSearch?> GetSearchAsync(SlskdCredentials credentials, Guid searchId, bool includeResponses = false, CancellationToken cancellationToken = default)
            => Task.FromResult<SlskdSearch?>(new SlskdSearch
            {
                Id = searchId,
                IsComplete = true,
                ResponseCount = 1,
                FileCount = 1,
                EndedAt = DateTimeOffset.UtcNow
            });

        public override Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
        {
            var answered = StartedSearchTexts.Count == 1 ? AnswerFirstSearch : true;
            return Task.FromResult<IReadOnlyList<SlskdSearchResponse>>(
                answered
                    ?
                    [
                        new SlskdSearchResponse
                        {
                            Username = "Nintendude94",
                            Files =
                            [
                                new SlskdFile
                                {
                                    Filename = "Music\\Nick Drake\\(1969) Five Leaves Left\\01 - Nick Drake - Time Has Told Me.flac",
                                    Size = 24_679_285,
                                    Extension = ".flac",
                                    BitDepth = 16,
                                    SampleRate = 44_100
                                }
                            ]
                        }
                    ]
                    : []);
        }
    }

    /// <summary>Records the quality every scoring pass was asked to enforce.</summary>
    private sealed class RecordingScoringService : ISoulseekResultScoringService
    {
        public List<string?> RequiredQualityCodes { get; } = [];

        public Task<IReadOnlyList<SoulseekCandidate>> ScoreAsync(
            SoulseekSearchTarget target,
            IReadOnlyList<SoulseekRawCandidate> raw,
            SoulseekSearchMode mode,
            IReadOnlyList<string>? allowedQualities = null,
            bool? allowUnknownQuality = null,
            CancellationToken cancellationToken = default,
            string? requiredQualityCode = null)
        {
            RequiredQualityCodes.Add(requiredQualityCode);
            return Task.FromResult<IReadOnlyList<SoulseekCandidate>>(
                raw.Select(candidate => new SoulseekCandidate(
                    candidate,
                    "FLAC",
                    "FLAC",
                    Score: 0.9,
                    Accepted: true))
                    .ToList());
        }

        public SoulseekCandidate? SelectBest(IReadOnlyList<SoulseekCandidate> candidates)
            => candidates.Count == 0 ? null : candidates[0];
    }

    private sealed class StubCredentialProvider(SlskdCredentials? credentials) : ISoulseekCredentialProvider
    {
        public Task<SlskdCredentials?> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(credentials);
    }

    private sealed class StubConnectionService : ISoulseekConnectionService
    {
        public Task<SoulseekConnectionStatus> GetStatusAsync(bool force = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new SoulseekConnectionStatus(SoulseekConnectionState.Connected, "connected", "tester", null, DateTimeOffset.UtcNow, null));

        public Task<SoulseekConnectionStatus> EnsureAvailableAsync(CancellationToken cancellationToken = default)
            => GetStatusAsync(cancellationToken: cancellationToken);

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        /// <inheritdoc />
        public Task<SoulseekConnectionStatus> GetEligibilityAsync(CancellationToken cancellationToken = default)
            => GetStatusAsync(cancellationToken: cancellationToken);

        public void Invalidate()
        {
        }
    }

    private sealed class StubScoringService : ISoulseekResultScoringService
    {
        /// <summary>The last quality the service was asked to enforce, so a test can see what reached scoring.</summary>
        public string? LastRequiredQualityCode { get; private set; }

        public Task<IReadOnlyList<SoulseekCandidate>> ScoreAsync(
            SoulseekSearchTarget target,
            IReadOnlyList<SoulseekRawCandidate> raw,
            SoulseekSearchMode mode,
            IReadOnlyList<string>? allowedQualities = null,
            bool? allowUnknownQuality = null,
            CancellationToken cancellationToken = default,
            string? requiredQualityCode = null)
        {
            LastRequiredQualityCode = requiredQualityCode;
            return Task.FromResult<IReadOnlyList<SoulseekCandidate>>(
                raw.Select(candidate => new SoulseekCandidate(
                    candidate,
                    "FLAC",
                    "FLAC 24-bit/96kHz",
                    Score: 0.9,
                    Accepted: true))
                    .ToList());
        }

        public SoulseekCandidate? SelectBest(IReadOnlyList<SoulseekCandidate> candidates)
            => candidates.Count == 0 ? null : candidates[0];
    }
}
