using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Soulseek;
using DeezSpoTag.Integrations.Soulseek;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Contract tests for the mechanical <c>slskd</c> adapter.
/// </summary>
/// <remarks>
///     These assert only what the adapter is responsible for: building the right request, sending the API
///     key as a header rather than in the URL, and reading slskd's several JSON shapes. They deliberately do
///     not assert anything about candidate quality or download decisions, which belong to the service layer.
/// </remarks>
public sealed class SlskdClientContractTest
{
    private const string ApiKey = "super-secret-api-key";

    [Fact]
    public void Credentials_ToString_NeverExposesTheApiKey()
    {
        var credentials = new SlskdCredentials("http://localhost:5030", ApiKey);

        Assert.DoesNotContain(ApiKey, credentials.ToString(), StringComparison.Ordinal);
        Assert.Contains("localhost:5030", credentials.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost:5030", "http://localhost:5030/api/v0/searches")]
    [InlineData("localhost:5030", "http://localhost:5030/api/v0/searches")]
    [InlineData("https://slskd.example.com/", "https://slskd.example.com/api/v0/searches")]
    [InlineData("  https://slskd.example.com  ", "https://slskd.example.com/api/v0/searches")]
    public void TryBuildRequestUri_AppendsApiV0Segment(string baseUrl, string expected)
    {
        var uri = SlskdBaseUri.TryBuildRequestUri(baseUrl, "searches");

        Assert.NotNull(uri);
        Assert.Equal(expected, uri!.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://slskd.example.com")]
    [InlineData("not a url")]
    public void TryBuildRequestUri_RejectsUnusableBaseUrls(string? baseUrl)
        => Assert.Null(SlskdBaseUri.TryBuildRequestUri(baseUrl, "searches"));

    [Fact]
    public async Task GetServerStateAsync_SendsApiKeyAsHeaderAndNeverInTheUrl()
    {
        var handler = new RecordingHandler(_ => Json("""
            { "isConnected": true, "isLoggedIn": true, "username": "listener", "state": "Connected,LoggedIn" }
            """));

        var client = CreateClient(handler);
        var state = await client.GetServerStateAsync(new SlskdCredentials("http://localhost:5030", ApiKey));

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("http://localhost:5030/api/v0/server", handler.RequestUri!.ToString());
        Assert.Equal(ApiKey, Assert.Single(handler.ApiKeyHeaderValues));
        Assert.DoesNotContain(ApiKey, handler.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, handler.Body, StringComparison.Ordinal);

        Assert.True(state.IsConnected);
        Assert.True(state.IsLoggedIn);
        Assert.Equal("listener", state.Username);
    }

    [Fact]
    public async Task GetServerStateAsync_OmitsApiKeyHeaderWhenNoKeyIsConfigured()
    {
        var handler = new RecordingHandler(_ => Json("""{ "isConnected": false, "isLoggedIn": false }"""));

        var client = CreateClient(handler);
        await client.GetServerStateAsync(new SlskdCredentials("http://localhost:5030", string.Empty));

        Assert.False(handler.HasApiKeyHeader);
    }

    [Fact]
    public async Task UnauthorizedResponse_SurfacesAsSlskdApiExceptionWithoutLeakingTheKey()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("nope", Encoding.UTF8, "text/plain")
        });

        var client = CreateClient(handler);
        var error = await Assert.ThrowsAsync<SlskdApiException>(
            () => client.GetServerStateAsync(new SlskdCredentials("http://localhost:5030", ApiKey)));

        Assert.True(error.IsUnauthorized);
        Assert.DoesNotContain(ApiKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreachableInstance_SurfacesAsUnavailable()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("connection refused"));

        var client = CreateClient(handler);
        var error = await Assert.ThrowsAsync<SlskdApiException>(
            () => client.GetServerStateAsync(new SlskdCredentials("http://localhost:5030", ApiKey)));

        Assert.False(error.IsUnauthorized);
        Assert.Contains("unavailable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSearchAsync_DerivesCompletionFromEndedAt_NotFromStateBitValues()
    {
        var handler = new RecordingHandler(_ => Json("""
            { "id": "6f1b2c34-0000-0000-0000-000000000001", "state": 0, "endedAt": "2026-01-02T03:04:05Z", "fileCount": 12, "responseCount": 3 }
            """));

        var client = CreateClient(handler);
        var search = await client.GetSearchAsync(
            new SlskdCredentials("http://localhost:5030", ApiKey),
            Guid.Parse("6f1b2c34-0000-0000-0000-000000000001"));

        Assert.NotNull(search);
        Assert.True(search!.IsComplete);
        Assert.Equal(12, search.FileCount);
        Assert.Equal(3, search.ResponseCount);
    }

    [Fact]
    public async Task GetSearchAsync_ReportsIncompleteWhenNoEndTimestamp()
    {
        var handler = new RecordingHandler(_ => Json("""
            { "id": "6f1b2c34-0000-0000-0000-000000000002", "state": 1 }
            """));

        var client = CreateClient(handler);
        var search = await client.GetSearchAsync(
            new SlskdCredentials("http://localhost:5030", ApiKey),
            Guid.Parse("6f1b2c34-0000-0000-0000-000000000002"));

        Assert.NotNull(search);
        Assert.False(search!.IsComplete);
    }

    [Fact]
    public async Task GetSearchResponsesAsync_ReadsPeerHealthAlongsideFiles()
    {
        var handler = new RecordingHandler(_ => Json("""
            [
              {
                "username": "listener",
                "queueLength": 42,
                "hasFreeUploadSlot": true,
                "uploadSpeed": 65536,
                "fileCount": 1,
                "token": 7,
                "files": [
                  {
                    "filename": "@@abc\\Music\\Artist\\Album\\01 Track.flac",
                    "extension": ".flac",
                    "size": 30000000,
                    "bitRate": 940,
                    "bitDepth": 24,
                    "sampleRate": 96000,
                    "length": 255,
                    "isVariableBitRate": false,
                    "isLocked": false
                  }
                ],
                "lockedFiles": []
              }
            ]
            """));

        var client = CreateClient(handler);
        var responses = await client.GetSearchResponsesAsync(
            new SlskdCredentials("http://localhost:5030", ApiKey),
            Guid.NewGuid());

        var response = Assert.Single(responses);
        Assert.Equal("listener", response.Username);
        Assert.Equal(42, response.QueueLength);
        Assert.True(response.HasFreeUploadSlot);
        Assert.Equal(65536, response.UploadSpeed);

        var file = Assert.Single(response.Files);
        Assert.Equal(".flac", file.Extension);
        Assert.Equal(24, file.BitDepth);
        Assert.Equal(96000, file.SampleRate);
        Assert.Equal(255, file.Length);
    }

    [Fact]
    public async Task EnqueueDownloadsAsync_PostsFilenameAndSizeToThePeerRoute()
    {
        var handler = new RecordingHandler(_ => Json("""
            { "id": "6f1b2c34-0000-0000-0000-000000000003", "username": "listener", "filename": "a.flac", "size": 10, "state": 2 }
            """));

        var client = CreateClient(handler);
        var transfers = await client.EnqueueDownloadsAsync(
            new SlskdCredentials("http://localhost:5030", ApiKey),
            "listener",
            [new SlskdQueueDownload { Filename = "a.flac", Size = 10 }]);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://localhost:5030/api/v0/transfers/downloads/listener", handler.RequestUri!.ToString());
        Assert.Contains("\"filename\":\"a.flac\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"size\":10", handler.Body, StringComparison.Ordinal);

        var transfer = Assert.Single(transfers);
        Assert.Equal(10, transfer.Size);
        Assert.True(transfer.StateInfo.IsQueued);
    }

    /// <summary>
    ///     slskd 0.26 answers an enqueue with a batch object rather than the transfer itself.
    /// </summary>
    /// <remarks>
    ///     The batch shape was the reason a working download was reported as a failure. The response held the
    ///     transfer id, and the parser walked past it: it looked for "transfers", "downloads" and "items", did
    ///     not find any of them, and then read the <c>enqueued</c> array itself as though it were a transfer -
    ///     so the id came back empty and the download reported that slskd had started nothing.
    /// </remarks>
    [Fact]
    public async Task EnqueueDownloadsAsync_ReadsTheTransferIdOutOfTheBatchShape()
    {
        var handler = new RecordingHandler(_ => Json("""
            {
              "enqueued": [
                {
                  "id": "B2184F13-4CAE-4C14-AD7D-4BEABE624880",
                  "username": "peer-name",
                  "filename": "@@peer\\Music\\Album\\04 Isabella.mp3",
                  "size": 13764977,
                  "state": 2
                }
              ],
              "failed": []
            }
            """));

        var client = CreateClient(handler);
        var transfers = await client.EnqueueDownloadsAsync(
            new SlskdCredentials("http://localhost:5030", ApiKey),
            "peer-name",
            [new SlskdQueueDownload { Filename = "@@peer\\Music\\Album\\04 Isabella.mp3", Size = 13764977 }]);

        // The route and the request body are part of the contract and must not have moved.
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://localhost:5030/api/v0/transfers/downloads/peer-name", handler.RequestUri!.ToString());
        Assert.Contains("\"size\":13764977", handler.Body, StringComparison.Ordinal);

        var transfer = Assert.Single(transfers);
        Assert.Equal(Guid.Parse("B2184F13-4CAE-4C14-AD7D-4BEABE624880"), transfer.Id);
        Assert.Equal("peer-name", transfer.Username);
        Assert.Equal(@"@@peer\Music\Album\04 Isabella.mp3", transfer.Filename);
        Assert.Equal(13764977, transfer.Size);
        Assert.True(transfer.StateInfo.IsQueued);
    }

    /// <summary>
    ///     slskd 0.26 lists downloads grouped by peer and then by directory.
    /// </summary>
    /// <remarks>
    ///     The list route answers with peers, each holding directories, each holding files - and the files are
    ///     the transfers. Reading the outer arrays as transfers produced records with no id and no filename, so
    ///     the transfer that was really in flight could never be found to adopt or to watch.
    /// </remarks>
    [Fact]
    public async Task ListDownloadsAsync_FlattensTheGroupedPeerAndDirectoryShape()
    {
        var handler = new RecordingHandler(_ => Json("""
            [
              {
                "username": "peer-name",
                "directories": [
                  {
                    "directory": "@@peer\\Music\\Album",
                    "fileCount": 1,
                    "files": [
                      {
                        "id": "B2184F13-4CAE-4C14-AD7D-4BEABE624880",
                        "username": "peer-name",
                        "filename": "@@peer\\Music\\Album\\04 Isabella.mp3",
                        "size": 13764977,
                        "state": 48
                      }
                    ]
                  }
                ]
              }
            ]
            """));

        var client = CreateClient(handler);
        var transfers = await client.ListDownloadsAsync(new SlskdCredentials("http://localhost:5030", ApiKey));

        var transfer = Assert.Single(transfers);
        Assert.Equal(Guid.Parse("B2184F13-4CAE-4C14-AD7D-4BEABE624880"), transfer.Id);
        Assert.Equal("peer-name", transfer.Username);
        Assert.Equal(@"@@peer\Music\Album\04 Isabella.mp3", transfer.Filename);
        Assert.Equal(13764977, transfer.Size);
        Assert.True(transfer.StateInfo.IsSuccessful);
    }

    [Fact]
    public async Task ListDownloadsAsync_FlattensSeveralPeersAndDirectoriesIntoTheirFiles()
    {
        var handler = new RecordingHandler(_ => Json("""
            [
              { "username": "one", "directories": [
                  { "directory": "d1", "files": [ { "id": "11111111-1111-1111-1111-111111111111", "username": "one", "filename": "a.flac", "size": 1 } ] },
                  { "directory": "d2", "files": [ { "id": "22222222-2222-2222-2222-222222222222", "username": "one", "filename": "b.flac", "size": 2 } ] }
              ] },
              { "username": "two", "directories": [
                  { "directory": "d3", "files": [ { "id": "33333333-3333-3333-3333-333333333333", "username": "two", "filename": "c.flac", "size": 3 } ] }
              ] }
            ]
            """));

        var client = CreateClient(handler);
        var transfers = await client.ListDownloadsAsync(new SlskdCredentials("http://localhost:5030", ApiKey));

        Assert.Equal(3, transfers.Count);
        Assert.Equal(["a.flac", "b.flac", "c.flac"], transfers.Select(transfer => transfer.Filename));
        Assert.All(transfers, transfer => Assert.NotNull(transfer.Id));
    }

    [Fact]
    public async Task ListDownloadsAsync_SurvivesEmptyGroupsAndMissingContainers()
    {
        // An empty group is not an error and must not become a transfer record with no id; a group that has no
        // "directories" at all must not throw the whole read away.
        var handler = new RecordingHandler(_ => Json("""
            [
              { "username": "empty", "directories": [] },
              { "username": "no-directories" },
              { "username": "empty-files", "directories": [ { "directory": "d", "fileCount": 0, "files": [] } ] },
              { "username": "real", "directories": [
                  { "directory": "d", "files": [ { "id": "44444444-4444-4444-4444-444444444444", "username": "real", "filename": "a.flac", "size": 1 } ] }
              ] }
            ]
            """));

        var client = CreateClient(handler);
        var transfers = await client.ListDownloadsAsync(new SlskdCredentials("http://localhost:5030", ApiKey));

        var transfer = Assert.Single(transfers);
        Assert.Equal("a.flac", transfer.Filename);
    }

    [Fact]
    public async Task ListDownloadsAsync_KeepsTheValidTransferWhenASiblingIsMalformed()
    {
        // One unreadable entry must not cost the download the transfer id that is sitting next to it.
        var handler = new RecordingHandler(_ => Json("""
            [
              { "username": "peer", "directories": [
                  { "directory": "d", "files": [ { "size": 5 }, { "id": "55555555-5555-5555-5555-555555555555", "username": "peer", "filename": "a.flac", "size": 5 } ] }
              ] }
            ]
            """));

        var client = CreateClient(handler);
        var transfers = await client.ListDownloadsAsync(new SlskdCredentials("http://localhost:5030", ApiKey));

        var transfer = Assert.Single(transfers);
        Assert.Equal("a.flac", transfer.Filename);
    }

    [Fact]
    public async Task EnqueueDownloadsAsync_ReturnsATransferOnlyOnceWhenBothListsCarryIt()
    {
        // slskd has been seen to report the same transfer in the enqueued list and again in the batch's own
        // list. Two records for one transfer would make the engine watch and record it twice.
        var handler = new RecordingHandler(_ => Json("""
            {
              "enqueued": [ { "id": "66666666-6666-6666-6666-666666666666", "username": "peer", "filename": "a.flac", "size": 1 } ],
              "transfers": [ { "id": "66666666-6666-6666-6666-666666666666", "username": "peer", "filename": "a.flac", "size": 1 } ]
            }
            """));

        var client = CreateClient(handler);
        var transfers = await client.EnqueueDownloadsAsync(
            new SlskdCredentials("http://localhost:5030", ApiKey),
            "peer",
            [new SlskdQueueDownload { Filename = "a.flac", Size = 1 }]);

        Assert.Single(transfers);
    }

    [Theory]
    [InlineData("Queued, Remotely", 4098, false, false)]
    [InlineData("InProgress", 8, false, false)]
    [InlineData("48", 48, true, true)]
    [InlineData("Completed, Succeeded", 48, true, true)]
    [InlineData("Completed, Errored", 272, true, false)]
    public async Task GetDownloadAsync_DecodesTheTextualTransferStateReturnedBySlskd(
        string state,
        long expectedRaw,
        bool terminal,
        bool successful)
    {
        var transferId = Guid.Parse("4a2bad59-d240-437c-aa86-85cee54f1269");
        var handler = new RecordingHandler(_ => Json($$"""
            {
              "id": "{{transferId}}",
              "username": "larue",
              "filename": "Album/Get Wicked.flac",
              "size": 45288038,
              "bytesTransferred": 45288038,
              "state": "{{state}}"
            }
            """));

        var client = CreateClient(handler);
        var transfer = await client.GetDownloadAsync(
            new SlskdCredentials("http://localhost:5030", ApiKey), "larue", transferId);

        Assert.NotNull(transfer);
        Assert.Equal(expectedRaw, transfer.State);
        Assert.Equal(terminal, transfer.StateInfo.IsTerminal);
        Assert.Equal(successful, transfer.StateInfo.IsSuccessful);
    }

    [Fact]
    public async Task ListSharesAsync_ReadsTheIdKeyedShapeSlskdReturns()
    {
        var handler = new RecordingHandler(_ => Json("""
            {
              "music": {
                "id": "music",
                "alias": "Library",
                "isExcluded": false,
                "localPath": "/srv/music",
                "raw": "[Library]/srv/music",
                "remotePath": "Library",
                "directories": 12,
                "files": 3456
              }
            }
            """));

        var client = CreateClient(handler);
        var shares = await client.ListSharesAsync(new SlskdCredentials("http://localhost:5030", ApiKey));

        var share = Assert.Single(shares);
        Assert.Equal("music", share.Id);
        Assert.Equal("Library", share.Alias);
        Assert.Equal("/srv/music", share.LocalPath);
        Assert.Equal(3456, share.Files);
        Assert.False(share.IsExcluded);
    }

    [Fact]
    public async Task ClearCompletedDownloadsAsync_UsesTheCompletedHistoryRoute()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var client = CreateClient(handler);
        await client.ClearCompletedDownloadsAsync(new SlskdCredentials("http://localhost:5030", ApiKey));

        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Equal(
            "http://localhost:5030/api/v0/transfers/downloads/all/completed",
            handler.RequestUri!.ToString());
    }

    [Fact]
    public async Task DeleteSearchAsync_ReturnsFalseWhenSlskdHasAlreadyForgottenTheSearch()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var client = CreateClient(handler);
        var deleted = await client.DeleteSearchAsync(new SlskdCredentials("http://localhost:5030", ApiKey), Guid.NewGuid());

        Assert.False(deleted);
    }

    [Theory]
    // slskd's TransferStates bit field, taken from its own TransferStateCategories.
    [InlineData(2, false, true, false, false, "queued")]
    [InlineData(8, true, false, false, false, "in_progress")]
    [InlineData(48, false, false, true, false, "completed")]
    [InlineData(80, false, false, false, true, "cancelled")]
    [InlineData(144, false, false, false, true, "timed_out")]
    [InlineData(272, false, false, false, true, "errored")]
    [InlineData(528, false, false, false, true, "rejected")]
    [InlineData(1040, false, false, false, true, "aborted")]
    public void TransferStateInfo_DecodesSlskdStateBits(
        long raw,
        bool inProgress,
        bool queued,
        bool successful,
        bool failed,
        string label)
    {
        var decoded = SlskdTransferStateInfo.Decode(raw);

        Assert.Equal(raw, decoded.Raw);
        Assert.Equal(inProgress, decoded.IsInProgress);
        Assert.Equal(queued, decoded.IsQueued);
        Assert.Equal(successful, decoded.IsSuccessful);
        Assert.Equal(failed, decoded.IsFailed);
        Assert.Equal(label, decoded.Label);
    }

    [Fact]
    public void TransferStateInfo_OnlyCompletedPlusSucceededCountsAsSuccessful()
    {
        // 16 (Completed) alone is a terminal state with no outcome, so it must not read as a success.
        var decoded = SlskdTransferStateInfo.Decode(16);

        Assert.True(decoded.IsTerminal);
        Assert.False(decoded.IsSuccessful);
        Assert.True(decoded.IsFailed);
        Assert.Equal("completed_unknown", decoded.Label);
    }

    private static SlskdClient CreateClient(HttpMessageHandler handler)
        => new(new HttpClient(handler), NullLogger<SlskdClient>.Instance);

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string Body { get; private set; } = string.Empty;

        public IReadOnlyList<string> ApiKeyHeaderValues { get; private set; } = [];

        public bool HasApiKeyHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            HasApiKeyHeader = request.Headers.Contains("X-API-Key");
            ApiKeyHeaderValues = HasApiKeyHeader
                ? request.Headers.GetValues("X-API-Key").ToList()
                : [];

            return responder(request);
        }
    }
}
