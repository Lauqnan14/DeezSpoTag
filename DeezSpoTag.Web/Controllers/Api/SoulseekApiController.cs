using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Models.Soulseek;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Integrations.Soulseek;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DeezSpoTag.Web.Controllers.Api;

/// <summary>
///     The Soulseek API: connection status, download settings, search, browse, queue and import.
/// </summary>
/// <remarks>
///     <para>
///         Controllers stay thin here. Every action validates input, calls a service and shapes the response;
///         no matching, quality, dedupe or path decision is made in this file.
///     </para>
///     <para>
///         The connection read endpoints deliberately overlap with the login page's existing
///         <c>/api/platform-auth/soulseek/connection</c>. That is intentional: the login page must keep using
///         the route the shipped guardrail test pins, and the sidebar must keep reading the global auth state,
///         while this namespace serves the download path. All three read the same
///         <c>ISoulseekConnectionService</c>-backed state, so there is no second status path.
///     </para>
/// </remarks>
[Route("api/v1/soulseek")]
[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[EnableRateLimiting("DefaultApi")]
public sealed class SoulseekApiController : ControllerBase
{
    private readonly ISoulseekConnectionService _connection;
    private readonly ISoulseekSearchService _search;
    private readonly ISoulseekTransferService _transfer;
    private readonly ISoulseekCredentialProvider _credentials;
    private readonly ISlskdClient _slskd;
    private readonly SoulseekSettingsService _settings;
    private readonly ISettingsService _appSettings;
    private readonly SoulseekRepository _repository;
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DownloadIntentService _intentService;
    private readonly EnhancedPathTemplateProcessor _pathProcessor;
    private readonly DeezerClient _deezerClient;
    private readonly ILogger<SoulseekApiController> _logger;

    /// <summary>Initializes a new instance of the <see cref="SoulseekApiController"/> class.</summary>
    public SoulseekApiController(
        ISoulseekConnectionService connection,
        ISoulseekSearchService search,
        ISoulseekTransferService transfer,
        ISoulseekCredentialProvider credentials,
        ISlskdClient slskd,
        SoulseekSettingsService settings,
        ISettingsService appSettings,
        SoulseekRepository repository,
        DownloadQueueRepository queueRepository,
        DownloadIntentService intentService,
        EnhancedPathTemplateProcessor pathProcessor,
        DeezerClient deezerClient,
        ILogger<SoulseekApiController> logger)
    {
        _deezerClient = deezerClient;
        _logger = logger;
        _connection = connection;
        _search = search;
        _transfer = transfer;
        _credentials = credentials;
        _slskd = slskd;
        _settings = settings;
        _appSettings = appSettings;
        _repository = repository;
        _queueRepository = queueRepository;
        _intentService = intentService;
        _pathProcessor = pathProcessor;
    }

    // ---------------------------------------------------------------- connection

    /// <summary>Reads the Soulseek connection state.</summary>
    [HttpGet("connection")]
    public async Task<IActionResult> GetConnection(
        [FromQuery] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var status = await _connection.GetStatusAsync(refresh, cancellationToken);
        return Ok(ToConnectionResponse(status));
    }

    /// <summary>Forces a fresh probe, for the "test connection" button.</summary>
    [HttpPost("connection/test")]
    [EnableRateLimiting("SensitiveWrites")]
    public async Task<IActionResult> TestConnection(CancellationToken cancellationToken)
    {
        var status = await _connection.GetStatusAsync(force: true, cancellationToken);
        return Ok(ToConnectionResponse(status));
    }

    /// <summary>Asks slskd to connect and log in to Soulseek.</summary>
    [HttpPost("connection/connect")]
    [EnableRateLimiting("SensitiveWrites")]
    public async Task<IActionResult> Connect(CancellationToken cancellationToken)
    {
        var status = await _connection.EnsureAvailableAsync(cancellationToken);
        return status.IsUsable
            ? Ok(ToConnectionResponse(status))
            : StatusCode(503, ToConnectionResponse(status));
    }

    /// <summary>Asks slskd to disconnect from Soulseek.</summary>
    [HttpPost("connection/disconnect")]
    [EnableRateLimiting("SensitiveWrites")]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        var credentials = await _credentialsAsync(cancellationToken);
        if (credentials is null)
        {
            return NotConfigured();
        }

        try
        {
            await _slskd.DisconnectAsync(credentials, "Disconnected from DeezSpoTag.", cancellationToken);
        }
        catch (SlskdApiException ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }

        _connection.Invalidate();
        var status = await _connection.GetStatusAsync(force: true, cancellationToken);
        return Ok(ToConnectionResponse(status));
    }

    /// <summary>Reads the connection state without forcing a probe.</summary>
    [HttpGet("connection/status")]
    public async Task<IActionResult> GetConnectionStatus(CancellationToken cancellationToken)
        => Ok(ToConnectionResponse(await _connection.GetStatusAsync(cancellationToken: cancellationToken)));

    // ------------------------------------------------------------------ settings

    /// <summary>Reads the Soulseek download behaviour settings.</summary>
    [HttpGet("download-settings")]
    public IActionResult GetDownloadSettings() => Ok(ToSettingsResponse(_settings.GetSettings()));

    /// <summary>Updates the Soulseek download behaviour settings.</summary>
    [HttpPut("download-settings")]
    [EnableRateLimiting("SensitiveWrites")]
    public IActionResult UpdateDownloadSettings([FromBody] SoulseekDownloadSettings request)
        => request is null
            ? BadRequest(new { error = "Soulseek download settings are required." })
            : Ok(ToSettingsResponse(_settings.Update(request)));

    // -------------------------------------------------------------------- search

    /// <summary>Runs a Soulseek search and returns the scored candidates.</summary>
    /// <summary>
    ///     Refuses new Soulseek work unless slskd is verified connected and logged in.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Returns <see langword="null"/> when the source may be used. Every new-work endpoint goes through
    ///         here, so one wording and one reason code cover search, queue and browse: a caller that is told
    ///         "not connected" by one and "login required" by another cannot act on either.
    ///     </para>
    ///     <para>
    ///         It probes rather than reconnecting. A search silently logging slskd back in is how a source
    ///         returns after the reader logged it out, and why a stale page would keep working. The explicit
    ///         Connect action is the only thing that asks slskd to log in.
    ///     </para>
    ///     <para>
    ///         It never redirects the work to another engine. A reader who asked for Soulseek asked for
    ///         Soulseek; quietly fetching the track somewhere else hands back a file they did not choose.
    ///     </para>
    /// </remarks>
    private async Task<IActionResult?> RequireEligibilityAsync(CancellationToken cancellationToken)
    {
        var eligibility = await _connection.GetEligibilityAsync(cancellationToken).ConfigureAwait(false);
        if (eligibility.IsUsable)
        {
            return null;
        }

        return StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            error = eligibility.Message,
            reasonCode = SoulseekLoginRequiredReasonCode,
            loginRequired = true,
            loginUrl = SoulseekLoginUrl
        });
    }

    /// <summary>The one reason code every blocked Soulseek request answers with.</summary>
    private const string SoulseekLoginRequiredReasonCode = "soulseek_login_required";

    /// <summary>Where the reader goes to activate the source.</summary>
    private const string SoulseekLoginUrl = "/Login";

    [HttpPost("searches")]
    [EnableRateLimiting("SensitiveWrites")]
    public async Task<IActionResult> Search([FromBody] SoulseekSearchRequestModel request, CancellationToken cancellationToken)
    {
        // A manual search from the Soulseek tab sends the term the user already typed in the search box,
        // which is free text and may carry no separable artist. Soulseek matches on the whole string, so the
        // artist is optional here: BuildSearchText falls back to the title alone and manual scoring already
        // tolerates a candidate with no artist.
        if (request is null || (string.IsNullOrWhiteSpace(request.Artist) && string.IsNullOrWhiteSpace(request.Title)))
        {
            return BadRequest(new { error = "A search term is required to search Soulseek." });
        }

        var gate = await RequireEligibilityAsync(cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return gate;
        }

        var mode = request.Automated == true ? SoulseekSearchMode.Automated : SoulseekSearchMode.Manual;
        var target = new SoulseekSearchTarget(
            request.Artist,
            request.Title,
            request.Album,
            request.DurationMs,
            request.Isrc,
            request.ReleaseYear);

        // Live results mean the caller must not be held for the length of the search. The search is started
        // and observed in the background; ranked results arrive over the search_update hub event, and
        // GET /searches/{id}/results serves the accumulated set for clients without the hub.
        var credentials = await _credentials.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (credentials is null)
        {
            return StatusCode(503, new { error = "Soulseek is not configured." });
        }

        var searchText = SoulseekSearchService.BuildSearchText(target);

        DeezSpoTag.Integrations.Soulseek.SlskdSearch? started;
        try
        {
            var settings = _settings.GetSettings();
            var remoteTimeout = TimeSpan.FromSeconds(Math.Clamp(settings.SearchTimeoutSeconds, 5, 600));
            started = await _slskd.StartSearchAsync(
                    credentials,
                    new SlskdSearchRequest
                    {
                        SearchText = searchText,
                        SearchTimeout = (int)remoteTimeout.TotalMilliseconds,
                        MaximumPeerQueueLength = settings.MaximumPeerQueueLength,
                        MinimumPeerUploadSpeed = (int)Math.Min(settings.MinimumPeerUploadSpeedBytesPerSecond, int.MaxValue),
                        FilterResponses = true
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SoulseekUnavailableException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
        catch (SlskdApiException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }

        if (started.Id == Guid.Empty)
        {
            return StatusCode(502, new { error = "slskd did not return a search id." });
        }

        var searchId = started.Id;

        // Resolved once for the search rather than once per candidate, because every candidate in a search is
        // the same track from a different peer. The lookup is display-only and non-fatal, so a catalogue miss
        // costs the card its artwork and nothing else.
        var coverUrl = await ResolveSearchCoverUrlAsync(target, cancellationToken).ConfigureAwait(false);

        _ = Task.Run(async () =>
        {
            _logger.LogInformation("Soulseek live observation starting for search {SearchId}.", searchId);
            try
            {
                await _search.ObserveAsync(target, searchId, searchText, mode, queueUuid: null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Must be logged. Swallowing this is what let a background failure masquerade as
                // "no results"; the caller is streaming, but the stream never starts if this throws.
                _logger.LogError(ex, "Soulseek live observation failed for search {SearchId}.", searchId);
            }
        }, CancellationToken.None);

        return Accepted(new { searchId, searchText, coverUrl, started = true });
    }

    /// <summary>Cancels an in-flight Soulseek search.</summary>
    [HttpPost("searches/{searchId:guid}/cancel")]
    [EnableRateLimiting("SensitiveWrites")]
    public IActionResult CancelSearch(Guid searchId)
        => SoulseekSearchService.TryCancelSearch(searchId)
            ? Accepted(new { searchId, cancelled = true })
            : NotFound(new { error = "Search is not active." });

    /// <summary>Reads a recorded search.</summary>
    [HttpGet("searches/{searchId:guid}")]
    public async Task<IActionResult> GetSearch(Guid searchId, CancellationToken cancellationToken)
    {
        var record = await _repository.GetSearchAsync(searchId, cancellationToken);
        if (record is null)
        {
            return NotFound(new { error = "Search not found." });
        }

        var candidates = await _repository.GetCandidatesAsync(searchId, cancellationToken);
        return Ok(new
        {
            searchId,
            record.QueueUuid,
            record.SearchText,
            mode = record.Mode.ToString().ToLowerInvariant(),
            record.FileCount,
            record.ResponseCount,
            record.CandidateCount,
            record.Completed,
            record.TimedOut,
            failed = record.ErrorCode is not null,
            errorCode = record.ErrorCode,
            error = record.Error,
            record.StartedAtUtc,
            record.EndedAtUtc,

            // A persisted failure outranks the peer-count classification below. ClassifyOutcome reads only how
            // many peers answered, so it would call a search with answers and unread files "no_network_responses".
            outcome = record.ErrorCode is not null
                ? record.ErrorCode
                : ClassifyOutcome(record.ResponseCount, candidates.Count, candidates.Count(candidate => candidate.Accepted)),
            candidates = candidates.Select(candidate => new
            {
                candidate.Id,
                candidate.Username,
                candidate.Filename,
                candidate.Size,
                candidate.QualityCode,
                candidate.QualityLabel,
                candidate.TierValue,
                candidate.CanonicalRank,
                candidate.Score,
                candidate.Accepted,
                candidate.RejectedBecause
            })
        });
    }

    /// <summary>Lists the recorded searches for a download queue item.</summary>
    [HttpGet("searches/queue/{queueUuid}")]
    public async Task<IActionResult> GetSearchesForQueue(string queueUuid, CancellationToken cancellationToken)
        => Ok(await _repository.GetSearchesForQueueAsync(queueUuid, cancellationToken));

    /// <summary>
    ///     Reads one peer's exact remote directory.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the endpoint the album drawer opens, so its failure modes are the difference between
    ///         "this peer did not answer, try again" and "the tab is broken". Every expected Soulseek failure
    ///         is therefore translated into a structured, non-500 answer: an empty peer or directory is a
    ///         request the caller got wrong, a peer or slskd that will not answer is retryable, and a Soulseek
    ///         with no stored connection is a setup problem on the login page. A reader who cancels the
    ///         request is not a failure at all, and genuine unexpected exceptions are still allowed to
    ///         surface and be logged rather than being flattened into an empty album.
    ///     </para>
    /// </remarks>
    [HttpGet("users/{username}/directory")]
    public async Task<IActionResult> BrowseDirectory(string username, [FromQuery] string? path, CancellationToken cancellationToken)
    {
        var gate = await RequireEligibilityAsync(cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return gate;
        }

        // Both values originate on the Soulseek network, so they are trimmed and normalized before anything
        // is asked of slskd. An empty peer or directory is refused here rather than spending a peer timeout
        // to arrive at the same conclusion.
        var peer = (username ?? string.Empty).Trim();
        var remoteDirectory = SoulseekRemotePath.Normalize(path);
        if (peer.Length == 0)
        {
            return BadRequest(new
            {
                error = "A peer username is required.",
                reasonCode = "username_required",
                retryable = false
            });
        }

        if (remoteDirectory.Length == 0)
        {
            return BadRequest(new
            {
                error = "A remote directory is required.",
                reasonCode = "remote_directory_required",
                retryable = false
            });
        }

        // Read the peer's whole directory list rather than asking for one folder.
        //
        // slskd's directory endpoint answers with leaf names and no directory prefix, and only for a share
        // root: a real nested folder comes back empty, which is indistinguishable from a folder that exists and
        // holds nothing. That is what made peers with tens of thousands of files report as empty. The browse
        // call returns every directory the peer publishes together with the files in it, so the requested
        // folder is found there by its real path rather than asked for and hoped for.
        IReadOnlyList<SlskdDirectory> published;
        try
        {
            published = await _search.BrowseAsync(peer, directory: null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SoulseekUnavailableException ex)
        {
            // Soulseek has no stored slskd connection. This is a setup problem the reader fixes on the login
            // page, and it is the expected failure this action used to let escape as an HTTP 500.
            _logger.LogWarning(
                "Soulseek browse of {RemoteDirectory} from {Peer} was refused: {Reason}",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(remoteDirectory),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(peer),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(ex.Message));
            return StatusCode(503, new
            {
                error = ex.Message,
                reasonCode = "soulseek_unavailable",
                retryable = true
            });
        }
        catch (SlskdApiException ex)
        {
            // A peer that does not answer and an slskd that is down both land here, and neither is a fault in
            // this request. slskd's message is already sanitized - no credential and no local destination path
            // is in it - and the reason code lets the drawer offer Retry honestly.
            _logger.LogWarning(
                "Soulseek browse of {RemoteDirectory} from {Peer} failed with slskd status {StatusCode}: {Reason}",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(remoteDirectory),
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(peer),
                ex.StatusCode,
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(ex.Message));
            return StatusCode(502, new
            {
                error = ex.Message,
                reasonCode = "peer_browse_failed",
                retryable = true
            });
        }

        // A peer that publishes nothing at all is an empty answer, not a fault, and saying so is the contract
        // this action has always had: the reader asked to see a peer's share and the peer has none to show.
        if (published.Count == 0)
        {
            return Ok(new
            {
                username = peer,
                remoteDirectory,
                directories = Array.Empty<object>()
            });
        }

        // Exact match on the normalized path only. A nearest match would put a folder the reader did not ask
        // for under a release name, which is worse than being told the folder is not there.
        var directories = published
            .Where(directory => SoulseekFolderPath.IsSameFolder(directory.Directory, remoteDirectory))
            .ToArray();

        // The peer has a share, and this path is not in it. That is a different fact from an empty share, and
        // conflating the two is what made the app accuse peers of holding nothing they plainly held.
        if (directories.Length == 0)
        {
            return NotFound(new
            {
                error = "That peer does not publish a folder at this path.",
                reasonCode = "folder_not_published",
                retryable = false
            });
        }

        var settings = _settings.GetSettings();
        var enabledQualities = DownloadSourceOrder.ResolveEnabledSoulseekQualities(_appSettings.LoadSettings());

        // Carried with every file so the drawer can say whether a sidecar would actually be taken, rather than
        // promising artwork the settings will not fetch.
        var peerArtworkEnabled = settings.UsePeerArtwork;
        var peerLyricsEnabled = settings.UsePeerLyrics;

        var projectedDirectories = directories.Select(directory =>
        {
            // The browsed folder is handed to the projection because slskd lists the files relative to it.
            // Without it every file comes back as a bare leaf and nothing can later be pinned or queued.
            var files = directory.Files
                .Select(rawFile => new
                {
                    rawFile,
                    file = SoulseekBrowseFilePolicy.Evaluate(
                        peer,
                        rawFile,
                        settings,
                        enabledQualities,
                        directory.Directory)
                })
                .ToArray();

            return new
            {
                directory.Directory,
                remoteDirectory = SoulseekRemotePath.Normalize(directory.Directory),
                directory.FileCount,
                audioFileCount = files.Count(item => item.file.RejectedBecause != "non_audio_file"),
                eligibleFileCount = files.Count(item => item.file.Eligible),
                nonAudioFileCount = files.Count(item => item.file.RejectedBecause == "non_audio_file"),
                coverCount = files.Count(item => item.file.SidecarRole == SoulseekSidecarPolicy.Cover),
                lyricsCount = files.Count(item => item.file.SidecarRole is SoulseekSidecarPolicy.Lyrics or SoulseekSidecarPolicy.CueSheet),
                files = files.Select(item => new
                {
                    item.file.Id,
                    item.file.Username,
                    item.file.RemoteDirectory,
                    item.file.Filename,
                    item.file.DisplayFilename,
                    item.file.Title,
                    item.file.Artist,
                    item.file.Album,
                    item.file.TrackNumber,
                    item.file.Size,
                    item.file.DurationSeconds,
                    item.file.BitrateKbps,
                    item.file.BitDepth,
                    item.file.SampleRateHz,
                    item.file.QualityCode,
                    item.file.QualityLabel,
                    item.file.Eligible,
                    item.file.RejectedBecause,
                    item.file.SidecarRole,
                    sidecarFetchable = SoulseekSidecarPolicy.IsFetchable(item.file.SidecarRole),
                    sidecarEnabled = item.file.SidecarRole switch
                    {
                        SoulseekSidecarPolicy.Cover => peerArtworkEnabled,
                        SoulseekSidecarPolicy.Lyrics or SoulseekSidecarPolicy.CueSheet => peerLyricsEnabled,
                        _ => false
                    },
                    item.rawFile.Extension,
                    item.rawFile.IsVariableBitRate,
                    item.rawFile.IsLocked,
                    BitRate = item.rawFile.BitRate,
                    SampleRate = item.rawFile.SampleRate,
                    Length = item.rawFile.Length
                })
            };
        });

        // An empty folder is an answer, not a failure: a peer folder the reader chose can legitimately hold
        // nothing this policy admits, and the drawer states that rather than treating it as an error.
        return Ok(new
        {
            username = peer,
            remoteDirectory,
            directories = projectedDirectories
        });
    }

    /// <summary>Deletes a recorded search from slskd and locally.</summary>
    [HttpDelete("searches/{searchId:guid}")]
    [EnableRateLimiting("SensitiveWrites")]
    public async Task<IActionResult> DeleteSearch(Guid searchId, CancellationToken cancellationToken)
    {
        var credentials = await _credentialsAsync(cancellationToken);
        if (credentials is not null)
        {
            try
            {
                await _slskd.DeleteSearchAsync(credentials, searchId, cancellationToken);
            }
            catch (SlskdApiException ex) when (!ex.IsNotFound)
            {
                return StatusCode(502, new { error = ex.Message });
            }
        }

        return Ok(new { deleted = true });
    }

    // ------------------------------------------------------------------ transfers

    /// <summary>Lists the Soulseek transfers recorded for a download queue item.</summary>
    [HttpGet("downloads")]
    public async Task<IActionResult> GetDownloads([FromQuery] string? queueUuid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return BadRequest(new { error = "A queueUuid is required." });
        }

        var transfers = await _repository.GetTransfersForQueueAsync(queueUuid, cancellationToken);
        return Ok(transfers.Select(ToTransferResponse));
    }

    /// <summary>Reads one recorded transfer.</summary>
    [HttpGet("downloads/{id:guid}")]
    public async Task<IActionResult> GetDownload(Guid id, CancellationToken cancellationToken)
    {
        var record = await _repository.GetTransferAsync(id, cancellationToken);
        return record is null ? NotFound(new { error = "Transfer not found." }) : Ok(ToTransferResponse(record));
    }

    /// <summary>Queues a Soulseek download through the normal engine path.</summary>
    /// <remarks>
    ///     This delegates to <see cref="DownloadIntentService"/>, so the item goes through the same dedupe,
    ///     destination guard and activity handling as every other engine rather than a side door. That is the
    ///     whole point: Soulseek must not be able to bypass the shared queue.
    /// </remarks>
    [HttpPost("downloads/queue")]
    [EnableRateLimiting("SensitiveWrites")]
    public async Task<IActionResult> QueueDownload(
        [FromBody] QueueSoulseekDownloadRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new { error = "A title is required to queue a Soulseek download." });
        }

        var gate = await RequireEligibilityAsync(cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return gate;
        }

        var pinned = !string.IsNullOrWhiteSpace(request.Username) && !string.IsNullOrWhiteSpace(request.RemotePath);
        var intent = BuildQueueDownloadIntent(request);
        var artist = intent.Artist;
        var album = intent.Album;

        var result = await _intentService.EnqueueManualAsync(intent, cancellationToken);

        if (!result.Success)
        {
            return StatusCode(422, new
            {
                queued = false,
                result.Message,
                skipped = result.Skipped,
                result.SkipReasonCodes
            });
        }

        return Accepted(new
        {
            queued = result.Queued.Count > 0,
            queueUuids = result.Queued,
            result.Skipped,
            result.SkipReasons,
            engine = result.Engine,
            artist,
            title = intent.Title,
            album,
            pinned,
            request.DestinationFolderId
        });
    }

    /// <summary>Queues several exact files from one peer directory through normal manual admission.</summary>
    [HttpPost("downloads/queue-batch")]
    [EnableRateLimiting("SensitiveWrites")]
    public async Task<IActionResult> QueueBatchDownload(
        [FromBody] QueueSoulseekBatchDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var gate = await RequireEligibilityAsync(cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return gate;
        }

        var folders = await _settings.GetAllFoldersAsync(cancellationToken);
        var destination = request?.DestinationFolderId is > 0
            ? folders.FirstOrDefault(folder => folder.Id == request.DestinationFolderId.Value)
            : null;
        // The pre-flight pass judges the request's shape only. Sidecars are checked against the peer's own
        // listing in the pass below, which is the one that has actually asked the peer.
        var initialPlan = SoulseekBatchQueuePlanner.CreatePlan(request, [], destination, validateSidecars: false);
        if (!initialPlan.IsValid)
        {
            return BadRequest(new
            {
                error = initialPlan.RequestErrorMessage,
                reasonCode = initialPlan.RequestErrorCode
            });
        }

        var validatedRequest = request!;
        IReadOnlyList<SlskdDirectory> directories;
        try
        {
            directories = await _search.BrowseAsync(
                validatedRequest.Username.Trim(),
                validatedRequest.RemoteDirectory,
                cancellationToken);
        }
        catch (SlskdApiException ex)
        {
            return StatusCode(502, new { error = ex.Message, reasonCode = "peer_browse_failed" });
        }

        var settings = _settings.GetSettings();
        var enabledQualities = DownloadSourceOrder.ResolveEnabledSoulseekQualities(_appSettings.LoadSettings());

        // Re-read the same folder the drawer read, so the fresh files carry the same full remote paths the
        // client asked about. Comparing a full path against a leaf would report every file as gone.
        var freshFiles = directories
            .SelectMany(directory => directory.Files.Select(file => new
            {
                file,
                directory = directory.Directory
            }))
            .Select(item => SoulseekBrowseFilePolicy.Evaluate(
                validatedRequest.Username,
                item.file,
                settings,
                enabledQualities,
                item.directory))
            .ToArray();
        var plan = SoulseekBatchQueuePlanner.CreatePlan(validatedRequest, freshFiles, destination);
        if (!plan.IsValid)
        {
            return BadRequest(new
            {
                error = plan.RequestErrorMessage,
                reasonCode = plan.RequestErrorCode
            });
        }

        var results = plan.Rejections.ToList();
        for (var index = 0; index < plan.Items.Count; index++)
        {
            var item = plan.Items[index];
            if (cancellationToken.IsCancellationRequested)
            {
                foreach (var pending in plan.Items.Skip(index))
                {
                    results.Add(new SoulseekBatchQueueFileResult(
                        pending.RemotePath,
                        "failed",
                        [],
                        ["request_cancelled"],
                        "Batch admission was cancelled before this file was queued."));
                }

                break;
            }

            try
            {
                var enqueue = await _intentService.EnqueueManualAsync(item.Intent, cancellationToken);
                var status = enqueue.Queued.Count > 0
                    ? "queued"
                    : enqueue.Skipped > 0
                        ? "skipped"
                        : "failed";
                results.Add(new SoulseekBatchQueueFileResult(
                    item.RemotePath,
                    status,
                    enqueue.Queued,
                    enqueue.SkipReasonCodes,
                    enqueue.Message));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results.Add(new SoulseekBatchQueueFileResult(
                    item.RemotePath,
                    "failed",
                    [],
                    ["request_cancelled"],
                    "Batch admission was cancelled before this file was queued."));
                foreach (var pending in plan.Items.Skip(index + 1))
                {
                    results.Add(new SoulseekBatchQueueFileResult(
                        pending.RemotePath,
                        "failed",
                        [],
                        ["request_cancelled"],
                        "Batch admission was cancelled before this file was queued."));
                }

                break;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                _logger.LogWarning(ex, "Could not admit Soulseek batch file {RemotePath}.", item.RemotePath);
                results.Add(new SoulseekBatchQueueFileResult(
                    item.RemotePath,
                    "failed",
                    [],
                    ["queue_admission_failed"],
                    "The file could not be added to the download queue."));
            }
        }

        return Accepted(new
        {
            queued = results.Count(result => result.Status == "queued"),
            skipped = results.Count(result => result.Status == "skipped"),
            failed = results.Count(result => result.Status == "failed"),
            results
        });
    }

    /// <summary>Cancels a transfer.</summary>
    [HttpPost("downloads/{id:guid}/cancel")]
    [EnableRateLimiting("SensitiveWrites")]
    public async Task<IActionResult> CancelDownload(Guid id, [FromQuery] bool remove = true, CancellationToken cancellationToken = default)
    {
        var record = await _repository.GetTransferAsync(id, cancellationToken);
        if (record is null)
        {
            return NotFound(new { error = "Transfer not found." });
        }

        await _transfer.CancelAsync(record.Username, id, remove, cancellationToken);
        return Ok(new { canceled = true });
    }

    /// <summary>Re-queues a failed or cancelled transfer through the shared download queue.</summary>
    /// <remarks>
    ///     <para>
    ///         This hands the work to <see cref="DeezSpoTagApp.RetryDownloadAsync"/> rather than asking slskd for
    ///         a replacement transfer directly. A direct enqueue starts bytes moving that no queue item owns: there
    ///         is no row to progress, no retry budget, no pinned peer or destination to honour, and nothing for the
    ///         activity view or the download history to report. The shared operation restores all of that and creates
    ///         the replacement transfer id itself, as part of running the item.
    ///     </para>
    ///     <para>
    ///         Retry is still opt-in per user. Re-enqueueing on every failure would hammer a peer that is already in
    ///         cooldown, so it only happens when the user has switched automatic retry on.
    ///     </para>
    /// </remarks>
    [HttpPost("downloads/{id:guid}/retry")]
    [EnableRateLimiting("SensitiveWrites")]
    public async Task<IActionResult> RetryDownload(
        Guid id,
        [FromServices] DeezSpoTag.Services.Download.Shared.DeezSpoTagApp app,
        CancellationToken cancellationToken)
    {
        var record = await _repository.GetTransferAsync(id, cancellationToken);
        if (record is null)
        {
            return NotFound(new { error = "Transfer not found." });
        }

        if (!record.IsTerminal)
        {
            return Conflict(new { error = "That transfer is still running." });
        }

        if (record.IsSuccessful || record.Verified)
        {
            // It completed and its file was verified, so there is nothing to retry. The file is the download.
            return Conflict(new { error = "That transfer already completed and its file was verified." });
        }

        if (string.IsNullOrWhiteSpace(record.QueueUuid))
        {
            return Conflict(new { error = "That transfer is not owned by a download queue item, so it cannot be retried through the queue." });
        }

        if (!_settings.GetSettings().AutoRetryIncompleteTransfers)
        {
            return Conflict(new
            {
                error = "Automatic retry is switched off for Soulseek. Turn it on in download settings, or queue the track again.",
                retried = false
            });
        }

        if (await _credentialsAsync(cancellationToken) is null)
        {
            return NotConfigured();
        }

        var gate = await RequireEligibilityAsync(cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return gate;
        }

        // A newer transfer for the same queue item means this id is stale. Retrying it would requeue the work while
        // the transfer that replaced it is still running, so the newer record wins and this one is refused.
        var siblings = await _repository.GetTransfersForQueueAsync(record.QueueUuid, cancellationToken);
        var newer = siblings
            .Where(sibling => sibling.TransferId != id && sibling.TransferId != Guid.Empty)
            .Where(sibling => !sibling.IsTerminal)
            .Where(sibling => sibling.UpdatedAtUtc > record.UpdatedAtUtc)
            .OrderByDescending(sibling => sibling.UpdatedAtUtc)
            .FirstOrDefault();
        if (newer is not null)
        {
            return Conflict(new
            {
                error = "A newer transfer for this download is already running.",
                currentTransferId = newer.TransferId
            });
        }

        var owner = await _queueRepository.GetByUuidAsync(record.QueueUuid, cancellationToken).ConfigureAwait(false);
        if (owner is null)
        {
            return Conflict(new { error = "The download queue item for that transfer no longer exists." });
        }

        if (!string.Equals(owner.Engine, SoulseekQueueItem.EngineId, StringComparison.OrdinalIgnoreCase))
        {
            // The item is not a Soulseek download any more. Retrying it here would put Soulseek bytes behind a queue
            // row that another engine owns, and that engine would never see them.
            return Conflict(new { error = "That download is not a Soulseek item." });
        }

        if (owner.Status?.Trim().ToLowerInvariant() is not ("failed" or "canceled" or "cancelled" or "unavailable" or "paused"))
        {
            return Conflict(new { error = "Only a failed, cancelled, unavailable, or paused download can be retried." });
        }

        if (!await app.RetryDownloadAsync(record.QueueUuid, cancellationToken).ConfigureAwait(false))
        {
            return Conflict(new { error = "That download could not be requeued." });
        }

        // No replacement transfer id is invented here. The worker creates the new transfer and attaches it to the
        // queue item's path claims when it runs, so the id that matters is the one the shared retry operation
        // reports for the item, not a guess made at request time.
        return Accepted(new { retried = true, queueUuid = record.QueueUuid, previousTransferId = id });
    }

    /// <summary>Removes a terminal transfer from slskd and forgets it.</summary>
    [HttpDelete("downloads/{id:guid}")]
    [EnableRateLimiting("SensitiveWrites")]
    public async Task<IActionResult> DeleteDownload(Guid id, CancellationToken cancellationToken)
    {
        var record = await _repository.GetTransferAsync(id, cancellationToken);
        if (record is null)
        {
            return NotFound(new { error = "Transfer not found." });
        }

        if (!record.IsTerminal)
        {
            return Conflict(new { error = "Cancel the transfer before deleting it." });
        }

        try
        {
            await _transfer.CancelAsync(record.Username, id, remove: true, cancellationToken);
        }
        catch (SlskdApiException ex) when (ex.IsNotFound)
        {
            // slskd has already forgotten it, which is the outcome the reader asked for. Treated as success so the
            // local row is removed rather than left behind forever for a transfer that no longer exists remotely.
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "slskd no longer knows Soulseek transfer {TransferId}; removing the local record anyway.",
                    id);
            }
        }

        // Removed locally only once slskd has confirmed. An authentication or transport failure above propagates, so
        // the history still describes a transfer that slskd may still be holding - which is the honest state when the
        // remote side was never successfully asked.
        var removed = await _repository.DeleteTerminalTransferAsync(id, cancellationToken);
        if (!removed)
        {
            // Terminal state can change between the read above and this write. Reported rather than claimed.
            return Conflict(new { error = "That transfer is no longer terminal, so its history was not removed." });
        }

        // Only this transfer's claims. Another queue item's or another peer's file is untouched, and the audio on
        // disk is not deleted: this endpoint forgets a transfer, it does not undo a download.
        await ReleaseClaimsForTransferAsync(record, cancellationToken).ConfigureAwait(false);

        return Ok(new { deleted = true });
    }

    /// <summary>
    ///     Releases the path claims attached to a transfer whose remote side is confirmed gone.
    /// </summary>
    /// <remarks>
    ///     Only after the remote removal, and only claims naming this transfer. Released earlier or more broadly, a
    ///     path whose file may still be on disk could be claimed by a second operation and read as its own download.
    /// </remarks>
    private async Task ReleaseClaimsForTransferAsync(SoulseekTransferRecord record, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var claim in await _repository.GetSourceClaimsForTransferAsync(record.TransferId, cancellationToken))
            {
                await _repository.ReleaseSourceClaimsAsync(claim.OwnershipId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The history row is already gone, so this is a leak rather than a wrong answer. The file at the path
            // stays, which keeps the claim's own protection in place rather than weakening it.
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    ex,
                    "Could not release the Soulseek path claims for removed transfer {TransferId}.",
                    record.TransferId);
            }
        }
    }

    /// <summary>
    ///     Says why a search produced what it produced, so a failure is never reported as "no results".
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Four states are genuinely different to the user and must not collapse into one message:
    ///         slskd could not be reached, peers answered but every file was rejected, the network answered
    ///         nobody at all, or the search simply succeeded. The first two are already distinct by the time
    ///         they reach here, because an unreachable or logged-out slskd raises
    ///         <see cref="SoulseekUnavailableException" /> rather than returning an empty result.
    ///     </para>
    ///     <para>
    ///         The last two are distinguished by whether any peer responded. That is the only signal available
    ///         and it is the one that matters: no peer responses means the Soulseek network returned nothing
    ///         for this query, which is not the same as returning files that did not match.
    ///     </para>
    /// </remarks>
    private static string ClassifyOutcome(int responseCount, int candidateCount, int acceptedCount)
        => acceptedCount > 0
            ? "matched"
            : responseCount <= 0
                ? "no_network_responses"
                : candidateCount > 0
                    ? "all_candidates_rejected"
                    : "no_files_returned";

    /// <summary>Reads the recorded candidates for a search.</summary>
    [HttpGet("searches/{searchId:guid}/results")]
    public async Task<IActionResult> GetSearchResults(Guid searchId, CancellationToken cancellationToken)
    {
        // A running search has not been persisted yet, so the live view is served first. Without this a client
        // watching progress mid-search is told the search does not exist and stops listening.
        if (SoulseekSearchService.LiveResults.TryGetValue(searchId, out var live))
        {
            // The peer count is slskd's own, not the number of candidates. They used to be the same value
            // because a running search accumulated candidates as it went; with stock slskd the candidate list
            // stays empty until the search finalizes, so using it here would report zero peers throughout.
            var counts = SoulseekSearchService.LiveCounts.TryGetValue(searchId, out var liveCounts)
                ? liveCounts
                : (ResponseCount: 0, FileCount: 0);

            return Ok(new
            {
                searchId,
                searchText = string.Empty,
                completed = false,
                timedOut = false,
                responseCount = counts.ResponseCount,
                fileCount = counts.FileCount,
                candidateCount = live.Count,
                outcome = ClassifyOutcome(counts.ResponseCount, live.Count, live.Count(candidate => candidate.Accepted)),
                qualityGroups = BuildQualityGroups(),
                candidates = live.Select(ToLiveCandidate)
            });
        }

        var record = await _repository.GetSearchAsync(searchId, cancellationToken);
        if (record is null)
        {
            return NotFound(new { error = "Search not found." });
        }

        var candidates = await _repository.GetCandidatesAsync(searchId, cancellationToken);

        // Artwork has to survive the handover from live to persisted results, or the card loses its cover the
        // moment the search finalizes. The live branch is polled every 750ms, so the cover is not re-resolved
        // there; a persisted search is read once per search, and the best recorded candidate is the only
        // identity it still carries, because a search record stores no artist of its own.
        var coverSource = candidates.FirstOrDefault(candidate => candidate.Accepted) ?? candidates.FirstOrDefault();
        string? coverUrl = null;
        if (coverSource is not null)
        {
            var coverName = SoulseekFilenameProjection.Describe(coverSource.Filename);
            coverUrl = await ResolveSearchCoverUrlAsync(
                new SoulseekSearchTarget(coverName.Artist ?? string.Empty, coverName.Title, coverName.Album),
                cancellationToken).ConfigureAwait(false);
        }

        return Ok(new
        {
            searchId,
            record.SearchText,
            coverUrl,
            record.CandidateCount,
            completed = record.Completed,
            timedOut = record.TimedOut,
            responseCount = record.ResponseCount,
            fileCount = record.FileCount,

            // As in the search endpoint: a recorded retrieval failure is reported as itself, never folded into the
            // "no peers responded" answer that its own counts would otherwise produce.
            failed = record.ErrorCode is not null,
            errorCode = record.ErrorCode,
            error = record.Error,
            outcome = record.ErrorCode is not null
                ? record.ErrorCode
                : ClassifyOutcome(record.ResponseCount, candidates.Count, candidates.Count(candidate => candidate.Accepted)),
            qualityGroups = BuildQualityGroups(),
            candidates = candidates.Select(candidate =>
            {
                // Same shape as the live payloads: a row restored after the search finalizes must name itself
                // the same way, or the panel loses the track the moment a search is reloaded. A record stores
                // no duration, so the name is read from the path alone.
                var name = SoulseekFilenameProjection.Describe(candidate.Filename);

                return new
                {
                    candidate.Id,
                    candidate.Username,
                    candidate.Filename,
                    title = name.Title,
                    artist = name.Artist,
                    album = name.Album,
                    trackNumber = name.TrackNumber,
                    candidate.Size,
                    candidate.QualityCode,
                    candidate.QualityLabel,
                    candidate.TierValue,
                    candidate.CanonicalRank,
                    candidate.Score,
                    candidate.Accepted,
                    candidate.RejectedBecause,

                    // What the peer reported about itself when it answered the search: how fast it uploads, how
                    // many requests were ahead of it, and whether it had a slot free. These are the reader's
                    // basis for choosing between peers offering the same album, so they are stored and returned
                    // rather than left live-only - a search read back after finalizing would otherwise show every
                    // peer as idle at zero speed.
                    peerUploadSpeed = candidate.PeerUploadSpeed,
                    peerQueueLength = candidate.PeerQueueLength,
                    peerHasFreeUploadSlot = candidate.PeerHasFreeUploadSlot,

                    // A recorded candidate carries no evidence about the peer's current state: the repository
                    // stores the file it offered, not the outcome of a liveness probe. Sending false makes the
                    // client say "Unknown" instead of defaulting a restored row to "Online", which it otherwise
                    // does because only a live response legitimately implies the peer answered.
                    online = false
                };
            })
        });
    }

    /// <summary>
    ///     The Soulseek qualities the search tab renders as groups, in ladder order, with their labels.
    /// </summary>
    /// <remarks>
    ///     Resolved from the same <see cref="DownloadSourceOrder"/> the downloader walks, so the groups the
    ///     search tab shows are literally the ladder the download will attempt. The enabled set is read
    ///     first and then projected through the display rule: an empty or complete selection shows the whole
    ///     ladder, a partial Custom selection shows only the ticked qualities, and unknown quality is never a
    ///     group because it belongs in the tab's trailing "Other" bucket. The downloader's own answer is not
    ///     touched by that projection.
    /// </remarks>
    private object[] BuildQualityGroups()
    {
        var labels = QualityCatalog.GetEngineQualityOptions()
            .TryGetValue(SoulseekQueueItem.EngineId, out var options)
            ? options.ToDictionary(option => option.Value, option => option.Label, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return DownloadSourceOrder
            .ResolveDisplayQualityGroups(
                DownloadSourceOrder.ResolveEnabledSoulseekQualities(_appSettings.LoadSettings()),
                _settings.GetSettings().AllowUnknownQuality)
            .Select(code => new
            {
                code,
                label = labels.TryGetValue(code, out var label) ? label : code
            })
            .ToArray();
    }

    /// <summary>Clears completed and stale transfers.</summary>
    [HttpPost("downloads/cleanup")]
    [EnableRateLimiting("SensitiveWrites")]
    public async Task<IActionResult> CleanupTransfers(CancellationToken cancellationToken)
        => Ok(new { cleared = await _transfer.CleanupStaleTransfersAsync(cancellationToken) });

    /// <summary>Clears searches slskd is still holding.</summary>
    [HttpPost("searches/cleanup")]
    [EnableRateLimiting("SensitiveWrites")]
    public async Task<IActionResult> CleanupSearches(CancellationToken cancellationToken)
    {
        var settings = _settings.GetSettings();
        var deleted = await _search.CleanupStaleSearchesAsync(
            TimeSpan.FromMinutes(Math.Clamp(settings.SearchRetentionMinutes, 1, 1_440)),
            cancellationToken);

        return Ok(new { deleted });
    }

    // ------------------------------------------------------- templates and import

    /// <summary>Previews the path a track would be written to.</summary>
    /// <remarks>
    ///     Runs the real shared path template processor, so the preview is the path the download will use rather
    ///     than an approximation that could drift from it.
    /// </remarks>
    [HttpPost("templates/preview")]
    public IActionResult PreviewTemplate(
        [FromBody] SoulseekTemplatePreviewRequest request,
        [FromServices] DeezSpoTagSettingsService settingsService)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Artist) || string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new { error = "Both artist and title are required to preview a path." });
        }

        var settings = settingsService.LoadSettings();
        var track = new DeezSpoTag.Core.Models.Track
        {
            // Track.Artist is the existing artist->roles map, so the preview reuses the same structure the
            // engine builds rather than inventing a parallel shape.
            Artist = new Dictionary<string, List<string>>
            {
                [request.Artist.Trim()] = [string.Empty]
            },
            Artists = [request.Artist.Trim()],
            Album = string.IsNullOrWhiteSpace(request.Album)
                ? null
                : BuildAlbum(request.Artist.Trim(), request.Album.Trim()),
            Title = request.Title.Trim()
        };

        var paths = _pathProcessor.GeneratePaths(track, "track", settings);
        return Ok(new
        {
            request.Artist,
            request.Title,
            request.Album,
            paths.Filename,
            paths.FilePath,
            paths.ArtistPath,
            paths.CoverPath,
            paths.ExtrasPath,
            writePath = string.IsNullOrWhiteSpace(paths.WritePath)
                ? Path.Join(paths.FilePath, paths.Filename)
                : paths.WritePath
        });
    }

    /// <summary>Re-opens a completed Soulseek download for import.</summary>
    /// <remarks>
    ///     Import is performed by the shared post-download pipeline, never here. This re-opens the item for it
    ///     by clearing the enrichment marker, which is the same lever the other engines use.
    /// </remarks>
    [HttpPost("import/{downloadId}")]
    [EnableRateLimiting("SensitiveWrites")]
    public async Task<IActionResult> Import(string downloadId, CancellationToken cancellationToken)
    {
        var item = (await _queueRepository.GetTasksAsync(SoulseekQueueItem.EngineId, cancellationToken))
            .FirstOrDefault(task => string.Equals(task.QueueUuid, downloadId, StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            return NotFound(new { error = "Download not found." });
        }

        if (string.IsNullOrWhiteSpace(item.PayloadJson))
        {
            return Conflict(new { error = "That download has no recorded staging file to import." });
        }

        await _queueRepository.SetEnrichmentStatusAsync([downloadId], "pending", cancellationToken);

        return Ok(new
        {
            downloadId,
            previousStatus = item.Status,
            previousFinalizationStatus = item.FinalizationStatus,
            enrichmentStatus = "pending",
            message = "Re-opened for the shared import pipeline."
        });
    }

    /// <summary>Re-queues tagging for a completed download.</summary>
    [HttpPost("tagging/reprocess/{downloadId}")]
    [EnableRateLimiting("SensitiveWrites")]
    public async Task<IActionResult> ReprocessTagging(string downloadId, CancellationToken cancellationToken)
    {
        var item = (await _queueRepository.GetTasksAsync(SoulseekQueueItem.EngineId, cancellationToken))
            .FirstOrDefault(task => string.Equals(task.QueueUuid, downloadId, StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            return NotFound(new { error = "Download not found." });
        }

        await _queueRepository.SetEnrichmentStatusAsync([downloadId], "pending", cancellationToken);
        return Ok(new { reprocess = true, status = "pending" });
    }

    // ------------------------------------------------------------------- helpers

    private Task<SlskdCredentials?> _credentialsAsync(CancellationToken cancellationToken)
        => _credentials.GetCredentialsAsync(cancellationToken);

    /// <summary>
    ///     Builds the album shape the shared path template processor expects.
    /// </summary>
    /// <remarks>
    ///     Reuses the existing <c>Album</c> model rather than a parallel one, so the preview resolves the same
    ///     artist and album folder names the download will use.
    /// </remarks>
    private static DeezSpoTag.Core.Models.Album BuildAlbum(string artist, string albumTitle)
    {
        var album = new DeezSpoTag.Core.Models.Album(albumTitle)
        {
            Artist = new Dictionary<string, List<string>> { [artist] = [string.Empty] },
            Artists = [artist],
            MainArtist = new DeezSpoTag.Core.Models.Artist(artist)
        };

        return album;
    }

    private IActionResult NotConfigured() => StatusCode(503, new { error = "Soulseek is not configured." });

    private static object ToConnectionResponse(SoulseekConnectionStatus status) => new
    {
        state = status.State.ToString().ToLowerInvariant(),
        status.Message,
        status.Username,
        status.LastError,
        status.CheckedAtUtc,
        status.ResponseTimeMs,
        usable = status.IsUsable
    };

    private static object ToSettingsResponse(SoulseekDownloadSettings settings) => new
    {
        settings.AllowUnknownQuality,
        settings.SearchTimeoutSeconds,
        settings.MinimumPeerUploadSpeedBytesPerSecond,
        settings.MaximumPeerQueueLength,
        settings.RequireFreeUploadSlot,
        settings.BlockedUsers,
        settings.BlockedFilenamePatterns,
        settings.PeerCooldownMinutes,
        settings.SearchRetentionMinutes,
        settings.AutoRetryIncompleteTransfers,
        settings.AutomationEnabled,
        settings.UsePeerArtwork,
        settings.UsePeerLyrics
    };

    /// <summary>
    ///     Resolves the catalogue cover for a Soulseek search.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A Soulseek candidate is a peer filename and carries no artwork of its own, so the cover comes
    ///         from the catalogue, the same way it does everywhere else in the app. The lookup is done once per
    ///         search rather than per candidate, because every candidate in a search is the same track from a
    ///         different peer.
    ///     </para>
    ///     <para>
    ///         This is display-only. A miss is not an error: the card falls back to its placeholder, and the
    ///         downloaded file still gets artwork from the shared post-download pipeline, which honours the
    ///         per-profile, per-folder preferences.
    ///     </para>
    /// </remarks>
    private async Task<string?> ResolveSearchCoverUrlAsync(SoulseekSearchTarget target, CancellationToken cancellationToken)
    {
        var query = string.IsNullOrWhiteSpace(target.Artist)
            ? target.Title
            : $"{target.Artist} {target.Title}";

        query = query.Trim();
        if (query.Length == 0)
        {
            return null;
        }

        try
        {
            var tracks = await _deezerClient.SearchTracksAsync(query, 1, cancellationToken).ConfigureAwait(false);
            return tracks.FirstOrDefault()?.Album?.CoverMedium;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cover is decoration. Never let it fail the search.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Could not resolve a catalogue cover for the Soulseek search '{Query}'.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(query));
            }

            return null;
        }
    }

    private static object ToLiveCandidate(SoulseekCandidate candidate)
    {
        // The panel names a track, an artist and an album rather than showing the search term again. The
        // facts come from the parser the scorer already ran, so a row reads the same way the matcher did.
        var name = SoulseekFilenameProjection.Describe(candidate.Filename, candidate.Raw.DurationSeconds);

        return new
        {
            id = $"{candidate.Username}\u0000{candidate.Filename}",
            candidate.Username,
            candidate.Filename,
            title = name.Title,
            artist = name.Artist,
            album = name.Album,
            trackNumber = name.TrackNumber,
            size = candidate.Raw.Size,
            bitrateKbps = candidate.Raw.BitrateKbps,
            bitDepth = candidate.Raw.BitDepth,
            sampleRateHz = candidate.Raw.SampleRateHz,
            durationSeconds = candidate.Raw.DurationSeconds,
            peerQueueLength = candidate.Raw.PeerQueueLength,
            peerHasFreeUploadSlot = candidate.Raw.PeerHasFreeUploadSlot,
            peerUploadSpeed = candidate.Raw.PeerUploadSpeed,
            isLocked = candidate.Raw.IsLocked,
            candidate.Quality,
            candidate.QualityLabel,
            candidate.TierValue,
            candidate.CanonicalRank,
            candidate.Score,
            candidate.Accepted,
            candidate.RejectedBecause
        };
    }

    private static object ToTransferResponse(SoulseekTransferRecord record) => new
    {
        record.Id,
        record.QueueUuid,
        transferId = record.TransferId,
        record.Username,
        record.Filename,
        record.State,
        record.IsTerminal,
        record.IsSuccessful,
        record.BytesTransferred,
        record.Size,
        record.ExpectedPath,
        record.Verified,
        record.Error,
        record.UpdatedAtUtc
    };

    private static DownloadIntent BuildQueueDownloadIntent(QueueSoulseekDownloadRequest request)
    {
        var pinned = !string.IsNullOrWhiteSpace(request.Username)
            && !string.IsNullOrWhiteSpace(request.RemotePath);
        return SoulseekBatchQueuePlanner.BuildPinnedIntent(
            pinned ? request.Username : null,
            pinned ? request.RemotePath : null,
            request.RemoteSizeBytes ?? 0,
            request.Title,
            request.Artist,
            request.Album,
            request.DurationMs,
            null,
            request.Quality,
            request.DestinationFolderId,
            request.DisplayCoverUrl,
            request.Isrc);
    }
}

/// <summary>A Soulseek search request.</summary>
public sealed record SoulseekSearchRequestModel(
    string Artist,
    string Title,
    string? Album = null,
    int? DurationMs = null,
    string? Isrc = null,
    int? ReleaseYear = null,
    bool? Automated = null);

/// <summary>A request to queue a Soulseek download.</summary>
public sealed record QueueSoulseekDownloadRequest(
    string Artist,
    string Title,
    string? Album = null,
    string? Isrc = null,
    int? DurationMs = null,
    string? Quality = null,
    long? DestinationFolderId = null,
    string? DisplayCoverUrl = null,
    string? Username = null,
    string? RemotePath = null,
    long? RemoteSizeBytes = null);

/// <summary>A request to preview the destination path for a track.</summary>
public sealed record SoulseekTemplatePreviewRequest(
    string Artist,
    string Title,
    string? Album = null);
