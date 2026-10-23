using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Navidrome;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Server-write truthfulness: per-target results keep confirmed fields even when the same target
/// errors, requested-but-unconfirmed fields stay pending, successful scheduling stamps and hashes
/// advance only for confirmed work, and a retry does not resend a field a prior attempt confirmed.
/// </summary>
[Collection("DataRoot Environment")]
public sealed class ArtistMetadataUpdaterReliabilityTest
{
    private readonly List<string> _tempPaths = [];

    [Fact]
    public async Task AcceptedTargetIsCheckpointedBeforeAnotherTargetIsContacted()
    {
        var jfCalls = 0;
        var service = CreateService(BuildPlexHappyResponder(null), _ =>
        { jfCalls++; return new HttpResponseMessage(HttpStatusCode.OK); }, null);
        var request = CreateRequest(0, CreateAuth(), new List<string> { PlexTarget, JellyfinTarget }, CreateTempFile(".png"), null, null);
        var sinkProperty = request.GetType().GetProperty("ResultSink");
        Assert.NotNull(sinkProperty);
        IReadOnlyList<ArtistTargetResult>? saved = null;
        sinkProperty!.SetValue(request, new Func<IReadOnlyList<ArtistTargetResult>, Task>(results =>
        { saved = results.ToList(); return Task.FromException(new IOException("journal unavailable")); }));
        var invocation = typeof(ArtistMetadataUpdaterService).GetMethod("PushArtistMetadataAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)invocation.Invoke(service, new[] { request, (object)CancellationToken.None })!;
        await Assert.ThrowsAsync<IOException>(() => task);
        Assert.Equal(0, jfCalls);
        Assert.Equal(ArtistTargetOutcome.Updated, Assert.Single(saved!).Outcome);
    }

    [Theory]
    [InlineData("plex")]
    [InlineData("jellyfin")]
    [InlineData("navidrome")]
    public async Task FieldIsConfirmedOnlyAfterEveryMatchedServerArtistAcceptsIt(string target)
    {
        var service = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && (path.Contains("/search", StringComparison.Ordinal)
                || path.EndsWith("/sections/1/all", StringComparison.Ordinal)))
                return Xml("<MediaContainer><Directory type=\"artist\" ratingKey=\"rk1\" title=\"Artist\" librarySectionID=\"1\" /><Directory type=\"artist\" ratingKey=\"rk2\" title=\"Artist\" librarySectionID=\"1\" /></MediaContainer>");
            if (path.EndsWith("/rk2/posters", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return BuildPlexHappyResponder(null)(request);
        }, request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Artists", StringComparison.Ordinal)) return Json("{\"Items\":[{\"Id\":\"jf1\",\"Name\":\"Artist\"},{\"Id\":\"jf2\",\"Name\":\"Artist\"}]}");
            if (path.EndsWith("/jf2/Images/Primary", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return BuildJellyfinHappyResponder()(request);
        }, request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/search3.view", StringComparison.Ordinal)) return Json("{\"subsonic-response\":{\"status\":\"ok\",\"searchResult3\":{\"artist\":[{\"id\":\"n1\",\"name\":\"Artist\"},{\"id\":\"n2\",\"name\":\"Artist\"}]}}}");
            if (path.EndsWith("/n2/image", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return BuildNavidromeHappyResponder()(request);
        });
        var result = Assert.Single(ReadTargets(await InvokePushMetadataAsync(service, CreateRequest(0, CreateAuth(),
            new List<string> { target }, CreateTempFile(".png"), null, null))));
        Assert.Equal(ArtistTargetOutcome.Failed, result.Outcome);
        Assert.DoesNotContain(ArtistTargetFields.Avatar, result.UpdatedFieldList);
        Assert.Single(DescribePendingTargetWork(new Dictionary<string, IReadOnlyList<string>>
            { [target] = new[] { ArtistTargetFields.Avatar } }, new[] { result }, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedArtistFailureRetainsOutstandingFields(bool retry)
    {
        var service = CreateService(null, null, null);
        var log = (LibraryConfigStore)RuntimeHelpers.GetUninitializedObject(typeof(LibraryConfigStore));
        SetPrivateField(log, "_logger", NullLogger<LibraryConfigStore>.Instance);
        SetPrivateField(service, "_configStore", log);
        var tracked = new MetadataUpdaterTrackedArtist { ArtistId = 5, ArtistName = "Artist",
            Targets = new List<string> { PlexTarget }, IncludeAvatar = true, IncludeBackground = true, IncludeBio = true };
        var prior = retry ? new ArtistRunOutcomeRecord(5, "partial", Targets: new[] {
            new ArtistTargetResult(PlexTarget, ArtistTargetOutcome.Failed, new[] { ArtistTargetFields.Avatar }) },
            PendingWork: new[] { new ArtistTargetPendingWork(PlexTarget, new[] { ArtistTargetFields.Background, ArtistTargetFields.Biography }, ArtistPendingWorkCategories.Transient) }) : null;
        var method = typeof(ArtistMetadataUpdaterService).GetMethod("ProcessTrackedArtistAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var args = new object?[] { tracked, new MetadataUpdaterRunRequest { Force = true }, CreateAuth(), DateTimeOffset.UtcNow, prior, CancellationToken.None, null };
        // The missing repository deliberately makes real artist preparation throw inside its boundary.
        var task = (Task)method.Invoke(service, args)!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var pending = Assert.Single((IReadOnlyList<ArtistTargetPendingWork>)Prop(result, "PendingWork")!);
        Assert.Contains(ArtistTargetFields.Background, pending.FieldList);
        Assert.Contains(ArtistTargetFields.Biography, pending.FieldList);
        Assert.Equal(!retry, pending.FieldList.Contains(ArtistTargetFields.Avatar));
    }

    [Fact]
    public async Task RetryUsesFrozenBytesAndOnlyUploadsOutstandingTargetFields()
    {
        var avatar = CreateTempFile(".png");
        var background = CreateTempFile(".png");
        var prepared = CreatePreparedVisuals(avatar, background, 4, 6);
        var freeze = typeof(ArtistMetadataUpdaterService).GetMethod("FreezePushPayloadAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(freeze);
        var task = (Task)freeze!.Invoke(null, new object?[] { prepared, "original biography", CancellationToken.None })!;
        await task;
        var payload = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var frozenAvatar = (string)Prop(payload, "AvatarPath")!;
        var frozenBackground = (string)Prop(payload, "BackgroundPath")!;
        _tempPaths.Add(frozenAvatar);
        _tempPaths.Add(frozenBackground);
        var bytes = System.IO.File.ReadAllBytes(frozenAvatar);
        System.IO.File.WriteAllText(avatar, "changed slot");
        System.IO.File.WriteAllText(background, "changed background slot");
        Assert.Equal(bytes, System.IO.File.ReadAllBytes(frozenAvatar));

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var jfUploads = 0;
        var reject = true;
        var happy = BuildJellyfinHappyResponder();
        var service = CreateService(BuildPlexHappyResponder(counts), request =>
        {
            if (request.Method == HttpMethod.Post) { jfUploads++; if (reject) return new HttpResponseMessage(HttpStatusCode.InternalServerError); }
            return happy(request);
        }, null);
        var directory = Path.Join(Path.GetTempPath(), "deezspotag-push-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var dbPath = Path.Join(directory, "library.db");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Library"] = $"Data Source={dbPath}" }).Build();
            await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
            SetPrivateField(service, "_libraryRepository", repository);
            var log = (LibraryConfigStore)RuntimeHelpers.GetUninitializedObject(typeof(LibraryConfigStore));
            SetPrivateField(log, "_logger", NullLogger<LibraryConfigStore>.Instance);
            SetPrivateField(service, "_configStore", log);
            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            await using (var insert = connection.CreateCommand())
            { insert.CommandText = @"INSERT INTO artist (id, name) VALUES (5, 'Artist');
INSERT INTO folder (id, root_path, display_name, enabled) VALUES (1, '/unused-test-folder', 'test', 1);
INSERT INTO album (id, artist_id, title) VALUES (1, 5, '');
INSERT INTO track (id, album_id, title) VALUES (1, 1, 'Track');
INSERT INTO audio_file (id, path, folder_id) VALUES (1, '/unused-test-folder/track.flac', 1);
INSERT INTO track_local (track_id, audio_file_id) VALUES (1, 1);"; await insert.ExecuteNonQueryAsync(); }
            var tracked = new MetadataUpdaterTrackedArtist { ArtistId = 5, ArtistName = "Artist",
                Targets = new List<string> { PlexTarget, JellyfinTarget }, IncludeAvatar = true, IncludeBackground = true,
                IncludeBio = true, AvatarRotationIndex = 3, BackgroundRotationIndex = 5 };
            var prior = new ArtistRunOutcomeRecord(5, ArtistRunOutcomes.Failed, Payload: (ArtistPushPayload)payload,
                PendingWork: new[] { new ArtistTargetPendingWork(JellyfinTarget, new[] { ArtistTargetFields.Avatar }, ArtistPendingWorkCategories.Transient) });
            var process = typeof(ArtistMetadataUpdaterService).GetMethod("ProcessTrackedArtistAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task<object> Attempt(ArtistRunOutcomeRecord evidence)
            {
                var operation = (Task)process.Invoke(service, new object?[] { tracked, new MetadataUpdaterRunRequest { Force = true },
                    CreateAuth(), DateTimeOffset.UtcNow, evidence, CancellationToken.None, null })!;
                await operation;
                return operation.GetType().GetProperty("Result")!.GetValue(operation)!;
            }
            var first = await Attempt(prior);
            Assert.True(Prop(first, "Targets") is not null, Prop(first, "Reason")?.ToString());
            var firstTargets = (IReadOnlyList<ArtistTargetResult>)Prop(first, "Targets")!;
            Assert.Equal(ArtistTargetOutcome.Updated, Assert.Single(firstTargets, target => target.Target == PlexTarget).Outcome);
            var pending = (IReadOnlyList<ArtistTargetPendingWork>)Prop(first, "PendingWork")!;
            Assert.Single(pending);
            Assert.Equal(3, tracked.AvatarRotationIndex);
            Assert.Equal(5, tracked.BackgroundRotationIndex);
            reject = false;
            // No artwork catalog dependency is installed: a retry must use the retained payload.
            var retained = new ArtistRunOutcomeRecord(5, ArtistRunOutcomes.Partial, Targets: firstTargets,
                PendingWork: pending, Payload: (ArtistPushPayload)Prop(first, "Payload")!);
            retained = System.Text.Json.JsonSerializer.Deserialize<ArtistRunOutcomeRecord>(
                System.Text.Json.JsonSerializer.Serialize(retained))!;
            var retry = await Attempt(retained);
            Assert.Empty((IReadOnlyList<ArtistTargetPendingWork>)Prop(retry, "PendingWork")!);
            Assert.Equal(4, tracked.AvatarRotationIndex);
            Assert.Equal(6, tracked.BackgroundRotationIndex);
            // Recover a crash after the accepted target evidence but before local scheduling.
            tracked.AvatarRotationIndex = 3;
            tracked.BackgroundRotationIndex = 5;
            var complete = await Attempt(new ArtistRunOutcomeRecord(5, ArtistRunOutcomes.Failed,
                Targets: (IReadOnlyList<ArtistTargetResult>)InvokeStatic("MergeTargetResults", firstTargets, Prop(retry, "Targets"))!,
                Payload: (ArtistPushPayload)Prop(retry, "Payload")!));
            Assert.Empty((IReadOnlyList<ArtistTargetPendingWork>)Prop(complete, "PendingWork")!);
            Assert.Equal(4, tracked.AvatarRotationIndex);
            Assert.Equal(6, tracked.BackgroundRotationIndex);
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT last_avatar_hash FROM artist_server_sync_state ORDER BY server";
            await using var reader = await query.ExecuteReaderAsync();
            var confirmed = 0;
            while (await reader.ReadAsync())
            { Assert.Equal(ComputeFileHashForTest(frozenAvatar), reader.GetString(0)); confirmed++; }
            Assert.Equal(2, confirmed);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
        Assert.Equal(1, counts["posters"]);
        Assert.Equal(1, counts["arts"]);
        Assert.Equal(1, counts["biography"]);
        Assert.True(jfUploads >= 3);
        Assert.Equal(bytes, System.IO.File.ReadAllBytes(frozenAvatar));
    }

    [Theory]
    [InlineData("plex", "avatar", HttpStatusCode.Unauthorized)]
    [InlineData("plex", "avatar", HttpStatusCode.InternalServerError)]
    [InlineData("plex", "background", HttpStatusCode.Unauthorized)]
    [InlineData("plex", "background", HttpStatusCode.InternalServerError)]
    [InlineData("plex", "biography", HttpStatusCode.Unauthorized)]
    [InlineData("plex", "biography", HttpStatusCode.InternalServerError)]
    [InlineData("jellyfin", "avatar", HttpStatusCode.Unauthorized)]
    [InlineData("jellyfin", "avatar", HttpStatusCode.InternalServerError)]
    [InlineData("jellyfin", "background", HttpStatusCode.Unauthorized)]
    [InlineData("jellyfin", "background", HttpStatusCode.InternalServerError)]
    [InlineData("jellyfin", "biography", HttpStatusCode.Unauthorized)]
    [InlineData("jellyfin", "biography", HttpStatusCode.InternalServerError)]
    [InlineData("navidrome", "avatar", HttpStatusCode.Unauthorized)]
    [InlineData("navidrome", "avatar", HttpStatusCode.InternalServerError)]
    public async Task HttpWriteRejectionsCannotConfirmFields(string target, string field, HttpStatusCode status)
    {
        Func<HttpRequestMessage, HttpResponseMessage> Wrap(Func<HttpRequestMessage, HttpResponseMessage> happy)
            => request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var isWrite = request.Method != HttpMethod.Get;
                var rejected = field switch
                {
                    "avatar" => path.EndsWith("/posters", StringComparison.Ordinal) || path.EndsWith("/Primary", StringComparison.Ordinal) || path.EndsWith("/n1/image", StringComparison.Ordinal),
                    "background" => path.EndsWith("/arts", StringComparison.Ordinal) || path.Contains("/Backdrop", StringComparison.Ordinal),
                    _ => request.RequestUri.Query.Contains("summary.value=", StringComparison.Ordinal) || path.EndsWith("/Items/jf1", StringComparison.Ordinal)
                };
                return isWrite && rejected ? new HttpResponseMessage(status) : happy(request);
            };
        var service = CreateService(Wrap(BuildPlexHappyResponder(null)), Wrap(BuildJellyfinHappyResponder()), Wrap(BuildNavidromeHappyResponder()));
        var result = Assert.Single(ReadTargets(await InvokePushMetadataAsync(service, CreateRequest(0, CreateAuth(),
            new List<string> { target }, CreateTempFile(".png"), CreateTempFile(".png"), "biography"))));
        Assert.Equal(ArtistTargetOutcome.Failed, result.Outcome);
        Assert.Contains(((int)status).ToString(), result.Error, StringComparison.Ordinal);
        var pending = Assert.Single(DescribePendingTargetWork(DescribeRequestedTargets(new List<string> { target }, true, "biography"), new[] { result }, null));
        Assert.Contains(field, pending.FieldList);
        if (field != "avatar") Assert.DoesNotContain(ArtistTargetFields.Avatar, pending.FieldList);
        Assert.Equal(status == HttpStatusCode.Unauthorized ? ArtistPendingWorkCategories.Authentication : ArtistPendingWorkCategories.Transient, pending.Category);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RejectedJellyfinUploadRemainsPending(HttpStatusCode status)
    {
        var happy = BuildJellyfinHappyResponder();
        var service = CreateService(null, request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(status) : happy(request), null);
        var pushed = await InvokePushMetadataAsync(service, CreateRequest(0, CreateAuth(),
            new List<string> { JellyfinTarget }, CreateTempFile(".png"), null, null));
        var result = Assert.Single(ReadTargets(pushed));
        Assert.Equal(ArtistTargetOutcome.Failed, result.Outcome);
        var pending = Assert.Single(DescribePendingTargetWork(
            new Dictionary<string, IReadOnlyList<string>> { [JellyfinTarget] = new[] { ArtistTargetFields.Avatar } },
            new[] { result }, null));
        Assert.Equal(status == HttpStatusCode.Unauthorized ? ArtistPendingWorkCategories.Authentication
            : ArtistPendingWorkCategories.Transient, pending.Category);
    }

    [Fact]
    public void ProvenAbsenceIsExcludedRatherThanRetried()
    {
        var pending = DescribePendingTargetWork(
            new Dictionary<string, IReadOnlyList<string>> { [PlexTarget] = new[] { ArtistTargetFields.Avatar } },
            new[] { ArtistTargetResult.NotFound(PlexTarget) }, null);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task PlexAcceptsWhileJellyfinLookupFailsAndTheRejectedFieldStaysPending()
    {
        var service = CreateService(
            BuildPlexHappyResponder(counts: null),
            request => new HttpResponseMessage(HttpStatusCode.NotFound),
            null);
        var auth = CreateAuth();
        var request = CreateRequest(
            localArtistId: 0,
            auth,
            new List<string> { PlexTarget, JellyfinTarget },
            CreateTempFile(".png"),
            CreateTempFile(".png"),
            "A biography.");

        var pushed = await InvokePushMetadataAsync(service, request);
        var targets = ReadTargets(pushed);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(Prop(pushed, "Warnings")));

        var plex = Assert.Single(targets, candidate => candidate.Target == "plex");
        Assert.Equal(ArtistTargetOutcome.Updated, plex.Outcome);
        Assert.Contains(ArtistTargetFields.Avatar, plex.UpdatedFieldList);
        Assert.Contains(ArtistTargetFields.Background, plex.UpdatedFieldList);
        Assert.Contains(ArtistTargetFields.Biography, plex.UpdatedFieldList);

        var jellyfin = Assert.Single(targets, candidate => candidate.Target == "jellyfin");
        Assert.Equal(ArtistTargetOutcome.Failed, jellyfin.Outcome);

        var requested = DescribeRequestedTargets(
            new List<string> { PlexTarget, JellyfinTarget },
            includeBio: true,
            biography: "A biography.");
        var pending = DescribePendingTargetWork(requested, targets, satisfied: null);
        var outstanding = Assert.Single(pending);
        Assert.Equal("jellyfin", outstanding.Target);
        Assert.Contains(ArtistTargetFields.Avatar, outstanding.FieldList);
        Assert.Contains(ArtistTargetFields.Background, outstanding.FieldList);
        Assert.Contains(ArtistTargetFields.Biography, outstanding.FieldList);
        Assert.Equal(ArtistPendingWorkCategories.Transient, outstanding.Category);
    }

    [Fact]
    public async Task JellyfinAcceptsWhilePlexLookupFailsAndBothResultsAreReported()
    {
        var service = CreateService(
            request => new HttpResponseMessage(HttpStatusCode.NotFound),
            BuildJellyfinHappyResponder(),
            null);
        var auth = CreateAuth();
        var request = CreateRequest(
            localArtistId: 0,
            auth,
            new List<string> { PlexTarget, JellyfinTarget },
            CreateTempFile(".png"),
            CreateTempFile(".png"),
            "A biography.");

        var pushed = await InvokePushMetadataAsync(service, request);
        var targets = ReadTargets(pushed);
        Assert.True(ReadPushedFlag(pushed, "Updated"));

        var jellyfin = Assert.Single(targets, candidate => candidate.Target == "jellyfin");
        Assert.Equal(ArtistTargetOutcome.Updated, jellyfin.Outcome);
        Assert.Contains(ArtistTargetFields.Biography, jellyfin.UpdatedFieldList);

        var plex = Assert.Single(targets, candidate => candidate.Target == "plex");
        Assert.Equal(ArtistTargetOutcome.Failed, plex.Outcome);

        // A failed Plex lookup is unknown, so its fields stay pending; only Jellyfin accepted content.
        var pending = DescribePendingTargetWork(
            DescribeRequestedTargets(new List<string> { PlexTarget, JellyfinTarget }, includeBio: true, biography: "A biography."),
            targets,
            satisfied: null);
        var outstanding = Assert.Single(pending);
        Assert.Equal("plex", outstanding.Target);
        Assert.Equal(3, outstanding.FieldList.Count);
    }

    [Fact]
    public async Task AcceptedFieldsSurviveWhenTheSameTargetRejectsAnother()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var service = CreateService(
            BuildPlexHappyResponder(counts, biographyFailure: true),
            null,
            null);
        var auth = CreateAuth();
        var request = CreateRequest(
            localArtistId: 0,
            auth,
            new List<string> { PlexTarget },
            CreateTempFile(".png"),
            CreateTempFile(".png"),
            "A biography.");

        var pushed = await InvokePushMetadataAsync(service, request);
        var plex = Assert.Single(ReadTargets(pushed));
        // A target that accepted some content is not *only* a failure: the accepted fields stay
        // recorded so the cumulative evidence is not erased by the rejected part of the push.
        Assert.Equal(ArtistTargetOutcome.Failed, plex.Outcome);
        Assert.Contains(ArtistTargetFields.Avatar, plex.UpdatedFieldList);
        Assert.Contains(ArtistTargetFields.Background, plex.UpdatedFieldList);
        Assert.DoesNotContain(ArtistTargetFields.Biography, plex.UpdatedFieldList);
        Assert.Contains("Plex update failed.", plex.Error, StringComparison.Ordinal);

        var requested = DescribeRequestedTargets([PlexTarget], includeBio: true, biography: "A biography.");
        var pending = DescribePendingTargetWork(requested, [plex], satisfied: null);
        var outstanding = Assert.Single(pending);
        Assert.Equal(PlexTarget, outstanding.Target);
        // The writes the failed attempt confirmed are real evidence a retry must not resend; only
        // the genuinely unresolved biography stays pending.
        Assert.Contains(ArtistTargetFields.Biography, outstanding.FieldList);
        Assert.DoesNotContain(ArtistTargetFields.Avatar, outstanding.FieldList);
        Assert.DoesNotContain(ArtistTargetFields.Background, outstanding.FieldList);
    }

    [Fact]
    public async Task FullyConfirmedButFailedAttemptLeavesNothingPending()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var service = CreateService(
            BuildPlexHappyResponder(counts, lockFailure: true),
            null,
            null);
        var auth = CreateAuth();
        var request = CreateRequest(
            localArtistId: 0,
            auth,
            new List<string> { PlexTarget },
            CreateTempFile(".png"),
            CreateTempFile(".png"),
            "A biography.");

        var pushed = await InvokePushMetadataAsync(service, request);
        var plex = Assert.Single(ReadTargets(pushed));
        // Every requested field was confirmed by the failed attempt (the rejection hit the artwork
        // lock after the writes succeeded), so nothing is pending for resend; the failure itself is
        // retained on the target error and keeps the artist due at the run level.
        Assert.Equal(ArtistTargetOutcome.Failed, plex.Outcome);
        Assert.Contains("lock failed", plex.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(DescribePendingTargetWork(
            DescribeRequestedTargets([PlexTarget], includeBio: true, biography: "A biography."),
            [plex],
            satisfied: null));
    }

    [Fact]
    public async Task DisabledBiographyIsNeverSentAndNeverRecordedAsPending()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var service = CreateService(BuildPlexHappyResponder(counts), null, null);
        var auth = CreateAuth();
        var request = CreateRequest(
            localArtistId: 0,
            auth,
            new List<string> { PlexTarget },
            CreateTempFile(".png"),
            CreateTempFile(".png"),
            biography: null);

        var pushed = await InvokePushMetadataAsync(service, request);
        var plex = Assert.Single(ReadTargets(pushed));
        Assert.Equal(ArtistTargetOutcome.Updated, plex.Outcome);
        Assert.DoesNotContain(ArtistTargetFields.Biography, plex.UpdatedFieldList);

        // The biography endpoint is never contacted when no biography was requested.
        Assert.False(counts.TryGetValue("biography", out var biographyWrites));
        var requested = DescribeRequestedTargets([PlexTarget], includeBio: false, biography: null);
        var pending = DescribePendingTargetWork(requested, [plex], satisfied: null);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task NavidromeReadonlyBiographyIsALimitationAndNeverPendingWork()
    {
        var service = CreateService(null, null, BuildNavidromeHappyResponder());
        var auth = CreateAuth();
        var request = CreateRequest(
            localArtistId: 0,
            auth,
            new List<string> { NavidromeTarget },
            CreateTempFile(".png"),
            CreateTempFile(".png"),
            "A biography.");

        var pushed = await InvokePushMetadataAsync(service, request);
        var navidrome = Assert.Single(ReadTargets(pushed));
        Assert.Equal(ArtistTargetOutcome.Updated, navidrome.Outcome);
        // One image slot: the avatar is uploaded, the scan is a notification, and the biography is a
        // declared capability limit.
        Assert.Contains(ArtistTargetFields.Avatar, navidrome.UpdatedFieldList);
        Assert.Contains(ArtistTargetFields.Scan, navidrome.UpdatedFieldList);
        Assert.Contains("Navidrome biography is read-only", Assert.Single(navidrome.LimitationList), StringComparison.Ordinal);

        // The single-slot artwork request and the unsupported biography are not pending: only what
        // the target could have written but did not confirm is outstanding.
        var requested = DescribeRequestedTargets([NavidromeTarget], includeBio: true, biography: "A biography.");
        var pending = DescribePendingTargetWork(requested, [navidrome], satisfied: null);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task PriorConfirmedFieldsAreWithheldAndNotRecreatedAsPending()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var service = CreateService(BuildPlexHappyResponder(counts), null, null);
        var auth = CreateAuth();
        var satisfied = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [PlexTarget] = new[] { ArtistTargetFields.Avatar, ArtistTargetFields.Background }
        };
        var request = CreateRequest(
            localArtistId: 0,
            auth,
            new List<string> { PlexTarget },
            CreateTempFile(".png"),
            CreateTempFile(".png"),
            "A replacement biography.",
            satisfied);

        var pushed = await InvokePushMetadataAsync(service, request);
        var plex = Assert.Single(ReadTargets(pushed));
        // The withheld artwork was not uploaded again; only the biography write happened.
        Assert.Equal(0, counts.GetValueOrDefault("posters", 0));
        Assert.Equal(0, counts.GetValueOrDefault("arts", 0));
        Assert.Equal(1, counts.GetValueOrDefault("biography", 0));
        Assert.Equal(ArtistTargetOutcome.Updated, plex.Outcome);
        Assert.Contains(ArtistTargetFields.Biography, plex.UpdatedFieldList);
        Assert.DoesNotContain(ArtistTargetFields.Avatar, plex.UpdatedFieldList);

        // Withheld-but-satisfied artwork is not pending again: the retry attempt stays settled.
        var requested = DescribeRequestedTargets([PlexTarget], includeBio: true, biography: "A replacement biography.");
        var pending = DescribePendingTargetWork(requested, [plex], satisfied);
        Assert.Empty(pending);
    }

    [Fact]
    public void PartialPushKeepsTheArtistDueAndDoesNotRotateTheUnresolvedArtwork()
    {
        var tracked = new MetadataUpdaterTrackedArtist
        {
            LastPushedAtUtc = DateTimeOffset.UnixEpoch,
            UpdatedAtUtc = DateTimeOffset.UnixEpoch,
            AvatarRotationIndex = 3,
            BackgroundRotationIndex = 5
        };
        var prepared = CreatePreparedVisuals(nextAvatarIndex: 4, nextBackgroundIndex: 6);
        var pending = new[]
        {
            new ArtistTargetPendingWork(PlexTarget, new[] { ArtistTargetFields.Background }, ArtistPendingWorkCategories.Transient)
        };

        ApplyConfirmedPushScheduling(tracked, prepared, pending);

        // The artist stays due for its retry window and the unresolved background keeps its slot,
        // while the confirmed avatar rotation advances exactly once.
        Assert.Equal(DateTimeOffset.UnixEpoch, tracked.LastPushedAtUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch, tracked.UpdatedAtUtc);
        Assert.Equal(4, tracked.AvatarRotationIndex);
        Assert.Equal(5, tracked.BackgroundRotationIndex);
    }

    [Fact]
    public void SettledPushAdvancesTheBaselineAndBothRotations()
    {
        var tracked = new MetadataUpdaterTrackedArtist
        {
            LastPushedAtUtc = DateTimeOffset.UnixEpoch,
            AvatarRotationIndex = 3,
            BackgroundRotationIndex = 5
        };
        var prepared = CreatePreparedVisuals(nextAvatarIndex: 4, nextBackgroundIndex: 6);
        ApplyConfirmedPushScheduling(tracked, prepared, pendingWork: []);

        Assert.NotEqual(DateTimeOffset.UnixEpoch, tracked.LastPushedAtUtc);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, tracked.UpdatedAtUtc);
        Assert.Equal(4, tracked.AvatarRotationIndex);
        Assert.Equal(6, tracked.BackgroundRotationIndex);
    }

    [Fact]
    public void SyncStateWritesOnlyConfirmedHashesAndStampsForTheTarget()
    {
        var tracked = new MetadataUpdaterTrackedArtist
        {
            ArtistId = 17,
            AvatarRotationIndex = 3,
            BackgroundRotationIndex = 5
        };
        var avatarPath = CreateTempFile(".png");
        var backgroundPath = CreateTempFile(".png");
        var prepared = CreatePreparedVisuals(avatarPath, backgroundPath, nextAvatarIndex: 4, nextBackgroundIndex: 6);
        var targetResult = new ArtistTargetResult(
            PlexTarget,
            ArtistTargetOutcome.Updated,
            new[] { ArtistTargetFields.Avatar, ArtistTargetFields.Scan });
        var pending = new[]
        {
            new ArtistTargetPendingWork(PlexTarget, new[] { ArtistTargetFields.Background }, ArtistPendingWorkCategories.Transient),
            new ArtistTargetPendingWork(PlexTarget, new[] { ArtistTargetFields.Biography }, ArtistPendingWorkCategories.Transient)
        };

        var input = BuildConfirmedServerSyncState(
            tracked,
            PlexTarget,
            requestedFields: new[] { ArtistTargetFields.Avatar, ArtistTargetFields.Background, ArtistTargetFields.Biography },
            targetResult,
            pending,
            prepared,
            biography: "A biography.",
            nowUtc: DateTimeOffset.UnixEpoch);

        // Only the confirmed avatar hash is written; the unconfirmed background and biography pass
        // null so the SQLite upsert preserves the earlier verified hashes, and no success timestamp
        // advances while the target still owes work.
        Assert.Equal(17, input.ArtistId);
        Assert.Equal(PlexTarget, input.Server);
        Assert.Null(input.LastCacheRefreshUtc);
        Assert.Null(input.LastSyncUtc);
        Assert.Equal(ComputeFileHashForTest(avatarPath), input.LastAvatarHash);
        Assert.Null(input.LastBackgroundHash);
        Assert.Null(input.LastBiographyHash);
        Assert.Equal("updated", input.LastResult);
        Assert.Null(input.LastError);
    }

    [Fact]
    public void SettledTargetStampsItsSuccessTimeAndConfirmedHashes()
    {
        var tracked = new MetadataUpdaterTrackedArtist
        {
            ArtistId = 17,
            AvatarRotationIndex = 4,
            BackgroundRotationIndex = 6
        };
        var avatarPath = CreateTempFile(".png");
        var backgroundPath = CreateTempFile(".png");
        var biography = "A biography.";
        var prepared = CreatePreparedVisuals(avatarPath, backgroundPath, nextAvatarIndex: 4, nextBackgroundIndex: 6);
        var targetResult = new ArtistTargetResult(
            PlexTarget,
            ArtistTargetOutcome.Updated,
            new[] { ArtistTargetFields.Avatar, ArtistTargetFields.Background, ArtistTargetFields.Biography });

        var input = BuildConfirmedServerSyncState(
            tracked,
            PlexTarget,
            requestedFields: new[] { ArtistTargetFields.Avatar, ArtistTargetFields.Background, ArtistTargetFields.Biography },
            targetResult,
            pendingWork: [],
            prepared,
            biography,
            nowUtc: DateTimeOffset.UnixEpoch);

        Assert.Equal(DateTimeOffset.UnixEpoch, input.LastCacheRefreshUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch, input.LastSyncUtc);
        Assert.Equal(ComputeFileHashForTest(avatarPath), input.LastAvatarHash);
        Assert.Equal(ComputeFileHashForTest(backgroundPath), input.LastBackgroundHash);
        Assert.Equal(ComputeTextHashForTest(biography), input.LastBiographyHash);
        Assert.Equal(4, input.AvatarRotationIndex);
        Assert.Equal(6, input.BackgroundRotationIndex);
        Assert.Equal("updated", input.LastResult);
    }

    [Fact]
    public void NotFoundTargetKeepsItsEarlierVerifiedHashesWithoutASuccessStamp()
    {
        var tracked = new MetadataUpdaterTrackedArtist { ArtistId = 17 };
        var prepared = CreatePreparedVisuals(
            CreateTempFile(".png"),
            CreateTempFile(".png"),
            nextAvatarIndex: 4,
            nextBackgroundIndex: 6);
        var targetResult = ArtistTargetResult.NotFound(PlexTarget);
        // The not-found classification itself leaves every requested field outstanding.
        var pending = DescribePendingTargetWork(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [PlexTarget] = new[] { ArtistTargetFields.Avatar, ArtistTargetFields.Background, ArtistTargetFields.Biography }
            },
            [targetResult],
            satisfied: null);

        var input = BuildConfirmedServerSyncState(
            tracked,
            PlexTarget,
            requestedFields: new[] { ArtistTargetFields.Avatar, ArtistTargetFields.Background, ArtistTargetFields.Biography },
            targetResult,
            pending,
            prepared,
            biography: "A biography.",
            nowUtc: DateTimeOffset.UnixEpoch);

        Assert.Null(input.LastCacheRefreshUtc);
        Assert.Null(input.LastSyncUtc);
        Assert.Null(input.LastAvatarHash);
        Assert.Null(input.LastBackgroundHash);
        Assert.Null(input.LastBiographyHash);
        Assert.Equal("not-found", input.LastResult);
        Assert.Null(input.LastError);
    }

    [Fact]
    public void SuccessLogNamesOnlyUpdatedServersAndReportsUnresolvedWork()
    {
        var pushed = CreatePushOutcome(
            new ArtistTargetResult(PlexTarget, ArtistTargetOutcome.Updated, new[] { ArtistTargetFields.Avatar }),
            new ArtistTargetResult(
                JellyfinTarget,
                ArtistTargetOutcome.Failed,
                Error: "The request failed with status code 401 (Unauthorized)."));
        var pending = new[]
        {
            new ArtistTargetPendingWork(JellyfinTarget, new[] { ArtistTargetFields.Avatar }, ArtistPendingWorkCategories.Authentication)
        };

        var text = DescribeConfirmedPushLogText("Artist", pushed, popularSongsSynced: false, pending);

        Assert.Contains("to plex.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("jellyfin", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 server write(s) remain unresolved and will be retried.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NavidromeSingleSlotRequestPrefersTheAvatarSlotWhenBothAreAvailable()
    {
        var tracked = new MetadataUpdaterTrackedArtist
        {
            ArtistId = 5,
            Targets = new List<string> { NavidromeTarget },
            IncludeAvatar = true,
            IncludeBackground = true,
            IncludeBio = true
        };
        var prepared = CreatePreparedVisuals(
            CreateTempFile(".png"),
            CreateTempFile(".png"),
            nextAvatarIndex: 1,
            nextBackgroundIndex: 2);

        // With both files available the single-slot request must be the avatar (the slot the push
        // would actually send) — never empty, which would fake a settled outcome.
        var requested = DescribeRequestedFields(tracked, prepared, biography: "A biography.");
        var navidromeRequest = Assert.Single(requested.Values);
        Assert.Equal([ArtistTargetFields.Avatar], navidromeRequest);

        tracked.Targets = new List<string> { PlexTarget };
        var plexRequest = Assert.Single(DescribeRequestedFields(tracked, prepared, biography: "A biography.").Values);
        Assert.Equal(
            new[] { ArtistTargetFields.Avatar, ArtistTargetFields.Background, ArtistTargetFields.Biography },
            plexRequest);
    }

    [Fact]
    public async Task PartialUnknownAuditStopsPreparationWithItsCause()
    {
        var service = CreateService(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var query = request.RequestUri?.Query ?? string.Empty;
            if (path.EndsWith("/library/sections", StringComparison.Ordinal))
            {
                return Xml("<MediaContainer><Directory type=\"artist\" key=\"1\" /></MediaContainer>");
            }

            if (query.Contains("Broken", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/library/metadata/rk1", StringComparison.Ordinal))
            {
                // Present artist without a thumbnail: a proven missing-art answer.
                return Xml("<MediaContainer><Directory type=\"artist\" ratingKey=\"rk1\" title=\"Artist One\" /></MediaContainer>");
            }

            return Xml("<MediaContainer><Directory type=\"artist\" ratingKey=\"rk1\" title=\"Artist One\" librarySectionID=\"1\" /></MediaContainer>");
        }, null, null);
        var artists = new List<ArtistDto>
        {
            new(1, "Artist One", true, null, null),
            new(2, "Broken Artist", true, null, null)
        };
        var warnings = new List<string>();

        var failure = await Assert.ThrowsAsync<TargetLookupException>(() =>
            InvokePlexMissingArtAuditAsync(service, artists, warnings));
        Assert.Contains("2", failure.Message, StringComparison.Ordinal);
        Assert.Contains(warnings, warning => warning.Contains("Broken Artist", StringComparison.Ordinal) && warning.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AuditThatCannotClassifyAnyArtistFailsPreparation()
    {
        var service = CreateService(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/library/sections", StringComparison.Ordinal))
            {
                return Xml("<MediaContainer><Directory type=\"artist\" key=\"1\" /></MediaContainer>");
            }

            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }, null, null);
        var artists = new List<ArtistDto>
        {
            new(1, "Broken One", true, null, null),
            new(2, "Broken Two", true, null, null)
        };

        await Assert.ThrowsAsync<TargetLookupException>(() => InvokePlexMissingArtAuditAsync(
            service,
            artists,
            new List<string>()));
    }

    // ---------------------------------------------------------------------------------------------
    // Fixture helpers
    // ---------------------------------------------------------------------------------------------

    private const string PlexTarget = "plex";
    private const string JellyfinTarget = "jellyfin";
    private const string NavidromeTarget = "navidrome";

    private static ArtistMetadataUpdaterService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage>? plexResponder,
        Func<HttpRequestMessage, HttpResponseMessage>? jellyfinResponder,
        Func<HttpRequestMessage, HttpResponseMessage>? navidromeResponder)
    {
        var instance = (ArtistMetadataUpdaterService)RuntimeHelpers.GetUninitializedObject(typeof(ArtistMetadataUpdaterService));
        if (plexResponder is not null)
        {
            SetPrivateField(instance, "_plexClient", new PlexApiClient(
                NullLogger<PlexApiClient>.Instance,
                new HttpClient(new StubHttpMessageHandler(plexResponder))));
        }

        if (jellyfinResponder is not null)
        {
            SetPrivateField(instance, "_jellyfinClient", new JellyfinApiClient(
                new HttpClient(new StubHttpMessageHandler(jellyfinResponder))));
        }

        if (navidromeResponder is not null)
        {
            SetPrivateField(instance, "_navidromeClient", new NavidromeApiClient(
                new HttpClient(new StubHttpMessageHandler(navidromeResponder))));
        }

        SetPrivateField(instance, "_logger", NullLogger<ArtistMetadataUpdaterService>.Instance);
        return instance;
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private static PlatformAuthState CreateAuth()
        => new()
        {
            Plex = new PlexAuth { Url = "http://plex.local:32400", Token = "token" },
            Jellyfin = new JellyfinAuth { Url = "http://jellyfin.local:8096", ApiKey = "key" },
            Navidrome = new NavidromeAuth { Url = "http://navidrome.local:4533", Username = "admin", Password = "password" }
        };

    private string CreateTempFile(string extension)
    {
        var path = Path.Join(Path.GetTempPath(), $"deezspotag-reliability-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        _tempPaths.Add(path);
        return path;
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> BuildPlexHappyResponder(
        IReadOnlyDictionary<string, int>? counts = null,
        bool lockFailure = false,
        bool biographyFailure = false)
        => request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var query = request.RequestUri?.Query ?? string.Empty;
            if (path.EndsWith("/library/sections", StringComparison.Ordinal))
            {
                return Xml("<MediaContainer><Directory type=\"artist\" key=\"1\" /></MediaContainer>");
            }

            if (request.Method == HttpMethod.Get
                && (path.Contains("/search", StringComparison.Ordinal)
                    || path.Contains("/library/sections/1/all", StringComparison.Ordinal)))
            {
                return Xml("<MediaContainer><Directory type=\"artist\" ratingKey=\"rk1\" title=\"Artist\" librarySectionID=\"1\" /></MediaContainer>");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/library/metadata/rk1/posters", StringComparison.Ordinal))
            {
                Count(counts, "posters");
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/library/metadata/rk1/arts", StringComparison.Ordinal))
            {
                Count(counts, "arts");
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Put
                && path.EndsWith("/library/sections/1/all", StringComparison.Ordinal)
                && query.Contains("summary.value=", StringComparison.Ordinal))
            {
                Count(counts, "biography");
                if (biographyFailure)
                {
                    // A transport-level rejection of the biography write: later than the artwork
                    // writes, so the attempt fails with part of the content already confirmed.
                    throw new HttpRequestException("connection reset during biography write");
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Put
                && path.EndsWith("/library/sections/1/all", StringComparison.Ordinal)
                && query.Contains("thumb.locked=1", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(lockFailure
                    ? HttpStatusCode.InternalServerError
                    : HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        };

    private static Func<HttpRequestMessage, HttpResponseMessage> BuildJellyfinHappyResponder()
        => request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/Artists", StringComparison.Ordinal))
            {
                return Json("{\"Items\":[{\"Id\":\"jf1\",\"Name\":\"Artist\"}],\"TotalRecordCount\":1}");
            }

            if (path.EndsWith("/Items/jf1/Images/Primary", StringComparison.Ordinal)
                || path.EndsWith("/Items/jf1/Images/Backdrop/0", StringComparison.Ordinal)
                || path.EndsWith("/Items/jf1/Images/Backdrop", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (path.EndsWith("/Items/jf1", StringComparison.Ordinal))
            {
                return request.Method.Method == HttpMethod.Post.Method
                    ? new HttpResponseMessage(HttpStatusCode.NoContent)
                    : Json("{\"Id\":\"jf1\",\"Name\":\"Artist\",\"Overview\":\"old\"}");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        };

    private static Func<HttpRequestMessage, HttpResponseMessage> BuildNavidromeHappyResponder()
        => request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/rest/search3.view", StringComparison.Ordinal))
            {
                return Json("{\"subsonic-response\":{\"status\":\"ok\",\"searchResult3\":{\"artist\":[{\"id\":\"n1\",\"name\":\"Artist\"}]}}}");
            }

            if (path.EndsWith("/auth/login", StringComparison.Ordinal))
            {
                return Json("{\"token\":\"tok\"}");
            }

            if (path.EndsWith("/api/artist/n1/image", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (path.EndsWith("/rest/startScan.view", StringComparison.Ordinal))
            {
                return Json("{\"subsonic-response\":{\"status\":\"ok\"}}");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        };

    private static void Count(IReadOnlyDictionary<string, int>? counts, string key)
    {
        if (counts is Dictionary<string, int> dictionary)
        {
            dictionary[key] = dictionary.TryGetValue(key, out var current) ? current + 1 : 1;
        }
    }

    private static HttpResponseMessage Xml(string xml)
        => new(HttpStatusCode.OK) { Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml") };

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    /// <summary>The requested fields when the cache is already prepared: flags and a biography.</summary>
    private static Dictionary<string, IReadOnlyList<string>> DescribeRequestedTargets(
        IReadOnlyList<string> targets,
        bool includeBio,
        string? biography)
    {
        var fieldsByTarget = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            var fields = new List<string>();
            var artworkOnly = ArtistMetadataUpdaterService.ResolveTargetCapability(target) == ArtistTargetCapability.ArtworkOnly;
            // Mirrors DescribeRequestedTargets' single-slot rule for artwork-only targets.
            if (artworkOnly)
            {
                fields.Add(ArtistTargetFields.Avatar);
            }
            else
            {
                fields.Add(ArtistTargetFields.Avatar);
                fields.Add(ArtistTargetFields.Background);
                if (includeBio && ArtistMetadataUpdaterService.ResolveTargetCapability(target) == ArtistTargetCapability.Full)
                {
                    fields.Add(ArtistTargetFields.Biography);
                }
            }

            fieldsByTarget[target] = fields;
        }

        return fieldsByTarget;
    }

    private static async Task<object> InvokePushMetadataAsync(ArtistMetadataUpdaterService service, object request)
    {
        var method = typeof(ArtistMetadataUpdaterService).GetMethod(
            "PushArtistMetadataAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(
            service,
            [request, CancellationToken.None]));
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task);
        Assert.NotNull(result);
        return result;
    }

    private static IReadOnlyList<ArtistTargetResult> ReadTargets(object pushed)
    {
        var property = pushed.GetType().GetProperty("Targets", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<IReadOnlyList<ArtistTargetResult>>(property!.GetValue(pushed));
    }

    private static object? Prop(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        return property!.GetValue(instance);
    }

    private static bool ReadPushedFlag(object pushed, string name)
        => Assert.IsType<bool>(Prop(pushed, name));

    internal static IReadOnlyList<ArtistTargetPendingWork> DescribePendingTargetWork(
        IReadOnlyDictionary<string, IReadOnlyList<string>> requestedByTarget,
        IReadOnlyList<ArtistTargetResult>? results,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? satisfied)
    {
        var pending = InvokeStatic("DescribePendingTargetWork",
            requestedByTarget, results, satisfied);
        return Assert.IsAssignableFrom<IReadOnlyList<ArtistTargetPendingWork>>(pending);
    }

    private static void ApplyConfirmedPushScheduling(
        MetadataUpdaterTrackedArtist tracked,
        object prepared,
        IReadOnlyList<ArtistTargetPendingWork> pendingWork)
        => InvokeStatic("ApplyConfirmedPushScheduling", tracked, prepared, pendingWork);

    private static ArtistServerSyncStateUpsertInput BuildConfirmedServerSyncState(
        MetadataUpdaterTrackedArtist tracked,
        string target,
        IReadOnlyList<string> requestedFields,
        ArtistTargetResult? targetResult,
        IReadOnlyList<ArtistTargetPendingWork> pendingWork,
        object prepared,
        string? biography,
        DateTimeOffset nowUtc)
        => (ArtistServerSyncStateUpsertInput)InvokeStatic(
            "BuildConfirmedServerSyncState",
            tracked, target, requestedFields, targetResult, pendingWork, prepared, biography, nowUtc);

    private static string DescribeConfirmedPushLogText(
        string artistName,
        object pushed,
        bool popularSongsSynced,
        IReadOnlyList<ArtistTargetPendingWork> pendingWork)
        => Assert.IsType<string>(InvokeStatic(
            "DescribeConfirmedPushLogText",
            artistName, pushed, popularSongsSynced, pendingWork));

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> DescribeRequestedFields(
        MetadataUpdaterTrackedArtist tracked,
        object prepared,
        string? biography)
        => Assert.IsAssignableFrom<IReadOnlyDictionary<string, IReadOnlyList<string>>>(InvokeStatic(
            "DescribeRequestedFields",
            tracked, prepared, biography));

    private static async Task<HashSet<long>> InvokePlexMissingArtAuditAsync(
        ArtistMetadataUpdaterService service,
        IReadOnlyList<ArtistDto> artists,
        List<string> warnings)
    {
        var method = typeof(ArtistMetadataUpdaterService).GetMethod(
            "FindPlexMissingArtistArtworkAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task<HashSet<long>>>(method!.Invoke(
            service,
            [artists, CreateAuth().Plex, warnings, CancellationToken.None]));
        return await task;
    }

    private static object InvokeStatic(string methodName, params object?[] arguments)
    {
        var method = typeof(ArtistMetadataUpdaterService).GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(null, arguments)!;
    }

    /// <summary>Builds the private <c>PreparedVisuals</c> record through its public-like shape.</summary>
    private static object CreatePreparedVisuals(
        int nextAvatarIndex,
        int nextBackgroundIndex)
        => CreatePreparedVisuals(null, null, nextAvatarIndex, nextBackgroundIndex);

    private static object CreatePreparedVisuals(
        string? avatarPath,
        string? backgroundPath,
        int nextAvatarIndex,
        int nextBackgroundIndex)
    {
        var runtimePaths = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["AvatarPath"] = avatarPath,
            ["BackgroundPath"] = backgroundPath
        };
        var record = Activator.CreateInstance(
            typeof(ArtistMetadataUpdaterService).GetNestedType("PreparedVisuals", BindingFlags.NonPublic) ??
            throw new InvalidOperationException("PreparedVisuals record not found"),
            runtimePaths["AvatarPath"],
            runtimePaths["BackgroundPath"],
            nextAvatarIndex,
            nextBackgroundIndex)!;
        return record;
    }

    private static object CreatePushOutcome(params ArtistTargetResult[] targets)
    {
        var type = typeof(ArtistMetadataUpdaterService).GetNestedType("PushOutcome", BindingFlags.NonPublic) ??
            throw new InvalidOperationException("PushOutcome record not found");
        var constructor = type
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 2);
        return constructor.Invoke([targets, new List<string>()]);
    }

    private static object CreateRequest(
        long localArtistId,
        PlatformAuthState auth,
        IReadOnlyList<string> targets,
        object? avatarPath,
        object? backgroundPath,
        string? biography,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? satisfiedByTarget = null)
        => Activator.CreateInstance(
            typeof(ArtistMetadataUpdaterService).GetNestedType("PushMetadataRequest", BindingFlags.NonPublic) ??
            throw new InvalidOperationException("PushMetadataRequest record not found"),
            localArtistId,
            auth,
            "Artist",
            targets,
            avatarPath,
            backgroundPath,
            biography,
            satisfiedByTarget)!;

    private static string? ComputeFileHashForTest(string path)
        => Assert.IsType<string>(InvokeStatic("ComputeFileHashOrNull", path));

    private static string? ComputeTextHashForTest(string value)
        => Assert.IsType<string>(InvokeStatic("ComputeTextHashOrNull", value));

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
