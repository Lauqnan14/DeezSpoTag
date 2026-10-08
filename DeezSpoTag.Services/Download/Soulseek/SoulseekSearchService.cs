using DeezSpoTag.Core.Models.Soulseek;
using DeezSpoTag.Integrations.Soulseek;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     Runs Soulseek searches through slskd and turns the responses into DeezSpoTag candidates.
/// </summary>
/// <remarks>
///     <para>
///         This owns the search lifecycle only: build the query, start it, poll until slskd says it finished
///         or the timeout expires, normalize the responses, and record the outcome. Deciding which candidate
///         wins is <see cref="ISoulseekResultScoringService"/>'s job, and moving a file is
///         <see cref="ISoulseekTransferService"/>'s.
///     </para>
///     <para>
///         The stale search is always deleted, including when polling times out, so a cancelled download does
///         not leave the search running inside slskd forever.
///     </para>
/// </remarks>
public sealed class SoulseekSearchService : ISoulseekSearchService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);

    /// <summary>Bounded allowance for response reads and remote cancellation finalization.</summary>
    public static readonly TimeSpan ResponseCollectionGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Persists and publishes a search that ended because its results could not be read.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     The record is marked failed and nothing else about it is changed: the peer and file counts observed while
    ///     polling stay, so a reloaded search still shows the network answered, and the remote search is left in slskd
    ///     because deleting it would destroy the only copy of the results this attempt could not read.
    /// </para>
    /// <para>
    ///     The final realtime events carry the code and the safe message, so a client watching the live search stops
    ///     polling and says what happened instead of waiting for results that are not coming.
    /// </para>
    /// </remarks>
    private async Task RecordRetrievalFailureAsync(
        Guid searchId,
        string searchText,
        string? queueUuid,
        int fileCount,
        int responseCount,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        await _repository.RecordSearchFailedAsync(
                searchId,
                fileCount,
                responseCount,
                ResponseRetrievalFailedCode,
                ResponseRetrievalFailedMessage,
                cancellationToken)
            .ConfigureAwait(false);

        var elapsed = (DateTimeOffset.UtcNow - startedAt).TotalSeconds;
        _realtime.PublishSearchUpdate(new SoulseekSearchProgress(
            searchId,
            queueUuid,
            searchText,
            SoulseekSearchStage.Failed,
            Completed: false,
            TimedOut: false,
            responseCount,
            elapsed,
            Results: null,
            Final: true,
            ErrorCode: ResponseRetrievalFailedCode,
            Error: ResponseRetrievalFailedMessage));

        _realtime.PublishSearchResult(new SoulseekSearchOutcome(
            searchId,
            searchText,
            [],
            Best: null,
            Completed: false,
            TimedOut: false,
            responseCount,
            queueUuid,
            ResponseRetrievalFailedCode,
            ResponseRetrievalFailedMessage));
    }

    /// <summary>Returns the local poll window for a given slskd timeout.</summary>
    public static TimeSpan ResolvePollTimeout(TimeSpan remoteTimeout) => remoteTimeout + ResponseCollectionGrace;

    /// <summary>
    ///     Cancellation sources for searches currently in flight, so a caller can cancel one without the
    ///     service having to be addressed by anything other than its search id.
    /// </summary>
    public static System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> ActiveSearches { get; } = new();

    /// <summary>
    ///     The ranked set for searches that are still running, keyed by search id.
    /// </summary>
    /// <remarks>
    ///     The repository only receives candidates when a search finishes, so a client polling for progress
    ///     mid-search would otherwise be told the search does not exist. This is the same data, kept live,
    ///     and it is removed as soon as the search ends so the persisted record becomes the single source of
    ///     truth again.
    /// </remarks>
    public static System.Collections.Concurrent.ConcurrentDictionary<Guid, IReadOnlyList<SoulseekCandidate>> LiveResults { get; } = new();

    /// <summary>
    ///     The live peer and file counts for each running search.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         These are the only figures a running search can honestly report, because slskd does not publish a
    ///         search's file responses until it finalizes. They are kept here so a client polling for progress is
    ///         given the real counts and a 200, rather than the candidate count standing in for a peer count.
    ///     </para>
    /// </remarks>
    public static System.Collections.Concurrent.ConcurrentDictionary<Guid, (int ResponseCount, int FileCount)> LiveCounts { get; } = new();

    /// <summary>Cancels an in-flight search. Returns false when it is not active, so the caller can 404.</summary>
    public static bool TryCancelSearch(Guid searchId)
    {
        if (ActiveSearches.TryGetValue(searchId, out var source))
        {
            try
            {
                source.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        return false;
    }

    private async Task<IReadOnlyList<SoulseekCandidate>> RankAsync(
        SoulseekSearchTarget target,
        IEnumerable<SoulseekRawCandidate> raw,
        SoulseekSearchMode mode,
        CancellationToken cancellationToken,
        string? requiredQualityCode = null)
    {
        // The whole accumulated set is scored every tick, so the live ordering is always exactly what the
        // scorer would produce for that set. No merge logic that could disagree with the final ranking.
        var candidates = await _scoring.ScoreAsync(
                target,
                raw.ToList(),
                mode,
                allowedQualities: null,
                allowUnknownQuality: null,
                cancellationToken,
                requiredQualityCode)
            .ConfigureAwait(false);

        return candidates;
    }

    private static bool IsSearchFinished(SlskdSearch? search) => search is null || search.IsComplete;

    private readonly ISlskdClient _client;
    private readonly ISoulseekCredentialProvider _credentials;
    private readonly ISoulseekConnectionService _connection;
    private readonly ISoulseekResultScoringService _scoring;
    private readonly SoulseekSettingsService _settings;
    private readonly SoulseekRepository _repository;
    private readonly ISoulseekRealtimePublisher _realtime;
    private readonly ILogger<SoulseekSearchService> _logger;

    /// <summary>Initializes a new instance of the <see cref="SoulseekSearchService"/> class.</summary>
    public SoulseekSearchService(
        ISlskdClient client,
        ISoulseekCredentialProvider credentials,
        ISoulseekConnectionService connection,
        ISoulseekResultScoringService scoring,
        SoulseekSettingsService settings,
        SoulseekRepository repository,
        ILogger<SoulseekSearchService> logger,
        ISoulseekRealtimePublisher? realtime = null)
    {
        _client = client;
        _credentials = credentials;
        _connection = connection;
        _scoring = scoring;
        _settings = settings;
        _repository = repository;
        _logger = logger;
        _realtime = realtime ?? NullSoulseekRealtimePublisher.Instance;
    }

    /// <inheritdoc />
    public async Task<SoulseekSearchOutcome> SearchAsync(
        SoulseekSearchTarget target,
        SoulseekSearchMode mode = SoulseekSearchMode.Manual,
        string? queueUuid = null,
        CancellationToken cancellationToken = default,
        string? requiredQualityCode = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        // A cached "available" is right for the reader's eye and wrong for granting a new search. The cache
        // exists so a burst of reads does not hammer slskd; new work still probes. Using it here meant a
        // background download could start a search up to twenty seconds after slskd lost its login.
        var eligibility = await _connection.GetEligibilityAsync(cancellationToken).ConfigureAwait(false);
        if (!eligibility.IsUsable)
        {
            throw new SoulseekUnavailableException(eligibility.Message);
        }

        var settings = _settings.GetSettings();
        var credentials = await _credentials.GetCredentialsAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new SoulseekUnavailableException("Soulseek is not configured.");

        // Seconds everywhere inside this application, including the local poll deadline below. slskd is the
        // one exception: it forwards this to Soulseek.NET's SearchOptions.SearchTimeout, which is in
        // milliseconds. Sending seconds hands it a value a thousand times too small, so a 30 second search
        // was closed after 30 milliseconds and almost no peer had time to respond. Convert here, at the
        // boundary, and nowhere else.
        var remoteTimeout = TimeSpan.FromSeconds(Math.Clamp(settings.SearchTimeoutSeconds, 5, 600));

        // slskd's timeout measures inactivity since the last response. The app's elapsed deadline
        // therefore explicitly stops an active search and waits for finalization before reading results.
        ResolvePollTimeout(remoteTimeout);

        // A bounded sequence, not a retry loop. The first query that returns an accepted candidate ends the
        // search, so a successful download costs exactly one remote search; a query that returns nothing may be
        // followed by the title alone, once. Every attempt carries the same mode, the same queue UUID, the same
        // quality the caller asked for and the same cancellation, so relaxing the query cannot relax anything
        // else about it.
        var queries = BuildSearchTexts(target);
        if (queries.Count == 0)
        {
            // Nothing to ask the network: no artist and no title. Refused here rather than searched for as an
            // empty string, which would return the network's whole index and then be scored against nothing.
            throw new ArgumentException("A Soulseek search needs an artist or a title.", nameof(target));
        }

        SoulseekSearchOutcome? last = null;

        for (var index = 0; index < queries.Count; index++)
        {
            if (index > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var search = await _client.StartSearchAsync(
                    credentials,
                    new SlskdSearchRequest
                    {
                        SearchText = queries[index],
                        SearchTimeout = (int)remoteTimeout.TotalMilliseconds,
                        MaximumPeerQueueLength = settings.MaximumPeerQueueLength,
                        MinimumPeerUploadSpeed = (int)Math.Min(settings.MinimumPeerUploadSpeedBytesPerSecond, int.MaxValue),
                        // slskd pre-filters peers; letting it do the cheap rejects keeps the response set small.
                        FilterResponses = true
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (search.Id == Guid.Empty)
            {
                throw new SoulseekUnavailableException("slskd did not return a search identifier.");
            }

            await _repository.RecordSearchStartedAsync(search.Id, queries[index], mode, queueUuid, cancellationToken)
                .ConfigureAwait(false);

            _realtime.PublishSearchUpdate(new SoulseekSearchProgress(
                search.Id,
                queueUuid,
                queries[index],
                SoulseekSearchStage.Searching));

            last = await ObserveAsync(target, search.Id, queries[index], mode, queueUuid, cancellationToken, requiredQualityCode)
                .ConfigureAwait(false);

            // A search whose responses could not be read ends the sequence here. Relaxing the query would mean
            // asking the network again about a track it already answered, and the relaxed query's empty result
            // would then be reported as the outcome of the first search. The remote search is kept, so retrying
            // the same search id still finds those results.
            if (last.Failed)
            {
                throw new SoulseekUnavailableException(last.Error ?? ResponseRetrievalFailedMessage);
            }

            if (last.Best is not null)
            {
                return last;
            }
        }

        // Nothing was accepted by any query. The last outcome is returned so the existing no-match reporting
        // and its diagnostics are unchanged - this loop adds an attempt, it does not add a new way of failing.
        // The query list cannot be empty here: that case is refused above, so every path through the loop has
        // run at least one real search and `last` is its outcome.
        return last!;
    }

    /// <summary>
    ///     The code carried by a search whose finalized responses could not be read.
    /// </summary>
    /// <remarks>
    ///     Its own code rather than <c>no_network_responses</c>, because peers did respond: that classification is
    ///     derived from having no responses at all, and reusing it would report a search that had answers as one
    ///     where the network was silent.
    /// </remarks>
    public const string ResponseRetrievalFailedCode = "response_retrieval_failed";

    /// <summary>
    ///     The sentence shown when a search's results could not be retrieved.
    /// </summary>
    /// <remarks>
    ///     Written for the reader and free of internals: the underlying exception is a transport or slskd detail,
    ///     and this string is rendered in the search panel and persisted beside the record.
    /// </remarks>
    public const string ResponseRetrievalFailedMessage =
        "Soulseek search results could not be retrieved. Try the search again.";

    /// <summary>
    ///     Watches an already-started search, publishing ranked results as they arrive.
    /// </summary>
    /// <remarks>
    ///     Split from <see cref="SearchAsync" /> so a caller that must return before the search ends can start
    ///     the remote search itself and hand the id here. Both paths run exactly this loop, so collection,
    ///     scoring, publication and cleanup have a single implementation.
    /// </remarks>
    public async Task<SoulseekSearchOutcome> ObserveAsync(
        SoulseekSearchTarget target,
        Guid searchId,
        string searchText,
        SoulseekSearchMode mode,
        string? queueUuid,
        CancellationToken cancellationToken,
        string? requiredQualityCode = null)
    {
        var credentials = await RequireCredentialsAsync(cancellationToken).ConfigureAwait(false);
        var startedAt = DateTimeOffset.UtcNow;
        var settings = _settings.GetSettings();
        var remoteTimeout = TimeSpan.FromSeconds(Math.Clamp(settings.SearchTimeoutSeconds, 5, 600));
        var pollTimeout = ResolvePollTimeout(remoteTimeout);

        // Accumulated across the whole search, keyed by username+filename, which is the identity the
        // candidate record already uses. Local to this invocation, so concurrent searches cannot mix.
            var collected = new Dictionary<string, SoulseekRawCandidate>(StringComparer.Ordinal);
            var responseCount = 0;
            var lastFileCount = 0;
            var completed = false;
            var remoteSearchRemovable = false;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ActiveSearches[searchId] = linked;

            // Registered before the first poll so a client that asks for results straight away is served the
            // live view rather than a 404 for a search that is plainly running.
            LiveResults[searchId] = [];
            LiveCounts[searchId] = (0, 0);
            await _repository.RecordSearchStartedAsync(searchId, searchText, mode, queueUuid, cancellationToken).ConfigureAwait(false);

            try
            {
                var deadline = DateTimeOffset.UtcNow + pollTimeout;

                while (true)
                {
                    // While the search runs, only the lightweight status object is read. slskd holds the
                    // SearchResponse objects in memory during the search and writes them into the persisted
                    // search only when it finalizes, so GET /searches/{id}/responses returns an empty list
                    // until then. Polling it every tick therefore re-fetched and re-deserialized an empty set
                    // some forty times per search, and reported nothing. The counts on the status object are
                    // live, so they are what the live view and the arrival histogram are built from.
                    try
                    {
                        var remote = await _client.GetSearchAsync(credentials, searchId, includeResponses: false, linked.Token).ConfigureAwait(false);
                        if (remote is not null)
                        {
                            lastFileCount = remote.FileCount;
                            responseCount = remote.ResponseCount;
                            LiveCounts[searchId] = (remote.ResponseCount, remote.FileCount);

                            if (_logger.IsEnabled(LogLevel.Debug))
                            {
                                _logger.LogDebug(
                                    "Soulseek search {SearchId} progress: {ResponseCount} peer(s), {FileCount} file(s).",
                                    searchId,
                                    remote.ResponseCount,
                                    remote.FileCount);
                            }
                        }

                        // Published on every tick regardless of whether anything new arrived: these events
                        // are what tell the page the search is alive and moving.
                        _realtime.PublishSearchUpdate(new SoulseekSearchProgress(
                            searchId,
                            queueUuid,
                            searchText,
                            linked.IsCancellationRequested ? SoulseekSearchStage.Cancelled : SoulseekSearchStage.Searching,
                            false,
                            false,
                            responseCount,
                            (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
                            LiveResults.TryGetValue(searchId, out var live) ? live : []));

                        if (IsSearchFinished(remote))
                        {
                            completed = true;
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One bad read must not abandon a search that is still returning results.
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug(ex, "Soulseek search {SearchId} could not be read this tick.", searchId);
                        }
                    }

                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        break;
                    }

                    await Task.Delay(PollInterval, linked.Token).ConfigureAwait(false);
                }

                if (!completed)
                {
                    // The local deadline passed first. Cancel the remote search and wait for slskd to bring it
                    // to a terminal state, so the responses are still finalized and readable.
                    var stopped = await PollUntilCompleteAsync(credentials, searchId, TimeSpan.Zero, linked.Token).ConfigureAwait(false);
                    lastFileCount = stopped.FileCount;
                }

                // The single response read of the search. If this throws, the remote search is deliberately
                // left in place: it is the only copy of the results, and deleting it in a finally block would
                // destroy the very thing a retry would need.
                var responsesCaptured = false;
                try
                {
                    var finalResponses = await FetchResponsesAsync(credentials, searchId, linked.Token).ConfigureAwait(false);
                    responsesCaptured = true;

                    // The outcome counts the responses this process actually holds, which is what was scored
                    // and persisted. The live figure slskd reported during the search was already published on
                    // every tick, so it is not re-applied here.
                    responseCount = finalResponses.Count;

                    foreach (var raw in Flatten(finalResponses)) collected[raw.Identity] = raw;
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (_logger.IsEnabled(LogLevel.Error))
                    {
                        _logger.LogError(
                            ex,
                            "Soulseek search {SearchId} could not read its finalized responses; the remote search is kept so the results remain recoverable.",
                            searchId);
                    }
                }

                if (responsesCaptured)
                {
                    remoteSearchRemovable = true;
                }

                // Peers answered and their files were never retrieved. That is not an empty result set, and the
                // difference is the whole point: an empty set is scored, published as a completed search, and the
                // caller relaxes the query looking for something better. Carrying on here would score the
                // candidates collected during polling, publish a final "searched, found nothing" event, and
                // overwrite a real record of peers and files with a successful search that never happened.
                //
                // The observed counts are kept. They are the only record that this was a genuine search with real
                // answers, and losing them is what turns a retrieval failure into "no peers responded" on reload.
                if (!responsesCaptured)
                {
                    await RecordRetrievalFailureAsync(
                            searchId,
                            searchText,
                            queueUuid,
                            lastFileCount,
                            responseCount,
                            startedAt,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    return new SoulseekSearchOutcome(
                        searchId,
                        searchText,
                        [],
                        Best: null,
                        Completed: false,
                        TimedOut: false,
                        responseCount,
                        queueUuid,
                        ResponseRetrievalFailedCode,
                        ResponseRetrievalFailedMessage);
                }

                var candidates = await RankAsync(target, collected.Values, mode, CancellationToken.None, requiredQualityCode)
                    .ConfigureAwait(false);
                var best = _scoring.SelectBest(candidates);

                await _repository.ReplaceCandidatesAsync(searchId, candidates, CancellationToken.None).ConfigureAwait(false);
                await _repository.RecordSearchCompletedAsync(
                        searchId,
                        new SoulseekSearchCompletion(
                            completed,
                            !completed,
                            lastFileCount,
                            responseCount,
                            candidates.Count,
                            best is null ? null : candidates.ToList().IndexOf(best)),
                        CancellationToken.None)
                    .ConfigureAwait(false);

                var outcome = new SoulseekSearchOutcome(
                    searchId,
                    searchText,
                    candidates,
                    best,
                    completed,
                    TimedOut: !completed,
                    responseCount,
                    queueUuid);

                _realtime.PublishSearchUpdate(new SoulseekSearchProgress(
                    searchId,
                    queueUuid,
                    searchText,
                    completed ? SoulseekSearchStage.Completed : SoulseekSearchStage.TimedOut,
                    outcome.Completed,
                    outcome.TimedOut,
                    responseCount,
                    (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
                    candidates,
                    Final: true));
                _realtime.PublishSearchResult(outcome);

                return outcome;
            }
            finally
            {
                ActiveSearches.TryRemove(searchId, out _);
                LiveResults.TryRemove(searchId, out _);
                LiveCounts.TryRemove(searchId, out _);

                // The remote search is removed on completion, timeout or cancel, but only once its responses
                // have actually been read. A cancelled search is the one exception: the user asked to stop, so
                // the results are abandoned deliberately and the search must not be left behind in slskd. When
                // the read merely failed, the search is left where it is, because deleting it would throw away
                // the only copy of the results.
                if (remoteSearchRemovable || linked.IsCancellationRequested)
                {
                    await TryDeleteSearchAsync(credentials, searchId, CancellationToken.None).ConfigureAwait(false);
                }
                else if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        "Soulseek search {SearchId} was left in slskd because its responses were never captured.",
                        searchId);
                }
            }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SlskdDirectory>> BrowseAsync(
        string username,
        string? directory = null,
        CancellationToken cancellationToken = default)
    {
        var credentials = await RequireCredentialsAsync(cancellationToken).ConfigureAwait(false);
        return await _client.BrowseUserDirectoryAsync(credentials, username, directory, cancellationToken).ConfigureAwait(false);
    }


    /// <inheritdoc />
    public async Task<int> CleanupStaleSearchesAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        var credentials = await _credentials.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        var deleted = 0;

        if (credentials is not null)
        {
            var searches = await _client.ListSearchesAsync(credentials, cancellationToken).ConfigureAwait(false);
            var cutoff = DateTimeOffset.UtcNow - olderThan;

            foreach (var search in searches.Where(search => search.EndedAt is { } ended && ended < cutoff))
            {
                if (await _client.DeleteSearchAsync(credentials, search.Id, cancellationToken).ConfigureAwait(false))
                {
                    deleted++;
                }
            }
        }

        deleted += await _repository
            .DeleteSearchesOlderThanAsync(DateTimeOffset.UtcNow - olderThan, cancellationToken)
            .ConfigureAwait(false);

        return deleted;
    }

    /// <summary>
    ///     The Soulseek queries for a track, in the order they may be tried.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Soulseek has no structured query: this is the token string peers match against, and a peer indexes
    ///         its files as plain words. The first query is therefore the artist and the title joined by one
    ///         space, and never with a separator - a query carrying " - " asks for a phrase almost no peer stores,
    ///         which is how "Nick Drake - Time Has Told Me" returned nothing at all while the file was there.
    ///     </para>
    ///     <para>
    ///         The album and the year stay out: they narrow the result set, and on this network that loses more
    ///         good matches than it rejects bad ones.
    ///     </para>
    ///     <para>
    ///         The second query is the title alone, for when the words are indexed in an order the first query
    ///         misses. There is deliberately no third. An artist-only query returns thousands of unrelated files,
    ///         and a downloader left to choose from those chooses wrongly - which is worse than finding nothing,
    ///         because the reader is told they got the track.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<string> BuildSearchTexts(SoulseekSearchTarget target)
    {
        var artist = Collapse(target.Artist);
        var title = Collapse(target.Title);
        var queries = new List<string>(2);

        if (artist.Length > 0
            && title.Length > 0
            && !string.Equals(artist, title, StringComparison.OrdinalIgnoreCase))
        {
            queries.Add($"{artist} {title}");
            queries.Add(title);
        }
        else if (title.Length > 0)
        {
            // No artist, or an artist that is the title: the relaxed query would be the same words, so there is
            // nothing to relax and asking twice would be a duplicate remote search.
            queries.Add(title);
        }
        else if (artist.Length > 0)
        {
            // No title to lose by asking for the artist on its own, and it is the only query there is.
            queries.Add(artist);
        }

        return queries
            .Where(query => query.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    ///     Builds the Soulseek search text for a track: the first query of the bounded sequence.
    /// </summary>
    /// <remarks>
    ///     Kept for callers that only ever run one search. It is the same text, so a caller that uses it is not
    ///     querying differently from one that iterates the sequence.
    /// </remarks>
    public static string BuildSearchText(SoulseekSearchTarget target)
        => BuildSearchTexts(target).FirstOrDefault() ?? string.Empty;

    /// <summary>
    ///     Reads the responses for a finished search, retrying while they come back empty.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A search can report itself complete before its responses are readable. slskd attaches them
    ///         during finalisation, so a read taken the instant <c>endedAt</c> appears can legitimately come
    ///         back empty, and a search that has genuinely found nothing is indistinguishable from that at
    ///         the first read.
    ///     </para>
    ///     <para>
    ///         So an empty result is re-read for a bounded window rather than believed immediately. This is a
    ///         second line of defence behind the poll window: it costs nothing when responses exist, and it
    ///         still ends on time when they never do, because the window is the same short grace.
    ///     </para>
    /// </remarks>
    private async Task<IReadOnlyList<SlskdSearchResponse>> FetchResponsesAsync(
        SlskdCredentials credentials,
        Guid searchId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ResponseCollectionGrace;

        while (true)
        {
            var responses = await _client.GetSearchResponsesAsync(credentials, searchId, cancellationToken).ConfigureAwait(false);
            if (responses.Count > 0 || DateTimeOffset.UtcNow >= deadline)
            {
                return responses;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Soulseek search {SearchId} reported complete with no responses yet; re-reading.", searchId);
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(bool Completed, int FileCount)> PollUntilCompleteAsync(
        SlskdCredentials credentials,
        Guid searchId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var stopping = false;

        while (true)
        {
            var search = await _client.GetSearchAsync(credentials, searchId, includeResponses: false, cancellationToken)
                .ConfigureAwait(false);

            if (search is null)
            {
                // slskd forgot the search, which normally means it expired. Treat it as finished so the
                // caller still gets whatever was collected.
                return (true, 0);
            }

            if (search.IsComplete)
            {
                return (!stopping, search.FileCount);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                if (stopping)
                {
                    throw new SoulseekUnavailableException("slskd did not finalize the stopped search in time.");
                }
                await _client.CancelSearchAsync(credentials, searchId, cancellationToken).ConfigureAwait(false);
                stopping = true;
                deadline = DateTimeOffset.UtcNow + ResponseCollectionGrace;
                continue;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Flattens the per-peer responses into raw candidates, dropping locked files.
    /// </summary>
    /// <remarks>
    ///     A locked file is one the peer will not release, so it can never be downloaded. Carrying peer health
    ///     onto each candidate is what lets the scorer compare peers without re-reading the response.
    /// </remarks>
    private static IReadOnlyList<SoulseekRawCandidate> Flatten(IReadOnlyList<SlskdSearchResponse> responses)
    {
        var candidates = new List<SoulseekRawCandidate>();

        foreach (var response in responses)
        {
            if (string.IsNullOrWhiteSpace(response.Username))
            {
                continue;
            }

            foreach (var file in response.Files)
            {
                if (file.IsLocked || string.IsNullOrWhiteSpace(file.Filename))
                {
                    continue;
                }

                candidates.Add(new SoulseekRawCandidate(
                    response.Username,
                    file.Filename,
                    file.Size,
                    file.Extension,
                    file.BitRate,
                    file.BitDepth,
                    file.SampleRate,
                    file.Length,
                    file.IsVariableBitRate,
                    IsLocked: false,
                    response.QueueLength,
                    response.HasFreeUploadSlot,
                    response.UploadSpeed));
            }
        }

        return candidates;
    }

    private async Task TryDeleteSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken)
    {
        try
        {
            await _client.DeleteSearchAsync(credentials, searchId, cancellationToken).ConfigureAwait(false);
        }
        catch (SlskdApiException ex)
        {
            // A search that slskd already dropped is the outcome we wanted, not a failure.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Could not delete Soulseek search {SearchId}.", searchId);
            }
        }
    }

    private async Task<SlskdCredentials> RequireCredentialsAsync(CancellationToken cancellationToken)
        => await _credentials.GetCredentialsAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new SoulseekUnavailableException("Soulseek is not configured.");

    private static string Collapse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        return builder.ToString();
    }
}

/// <summary>
///     Raised when Soulseek cannot be used for a download right now.
/// </summary>
/// <remarks>
///     A distinct exception type lets the engine report "slskd is not connected" in the activity log instead
///     of surfacing a generic transport error.
/// </remarks>
public sealed class SoulseekUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SoulseekUnavailableException"/> class.</summary>
    public SoulseekUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SoulseekUnavailableException"/> class.</summary>
    public SoulseekUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
