using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistMetadataUpdaterServicePlexPushTests : IDisposable
{
    private readonly List<string> _tempPaths = new();

    [Fact]
    public async Task PushToPlexAsync_AddsWarning_WhenPlexIsNotConfigured()
    {
        var service = CreateService(CreatePlexClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var updates = CreateUpdates();
        var warnings = new List<string>();
        var request = CreateRequest(
            localArtistId: 0,
            auth: new PlatformAuthState { Plex = null },
            avatarPath: null,
            backgroundPath: null,
            biography: null);

        await InvokePushToPlexAsync(service, request, updates, warnings);

        Assert.Contains("Plex is not configured.", warnings);
        Assert.False(GetUpdateFlag(updates, "AvatarUpdated"));
        Assert.False(GetUpdateFlag(updates, "BackgroundUpdated"));
        Assert.False(GetUpdateFlag(updates, "BioUpdated"));
    }

    [Fact]
    public async Task PushToPlexAsync_AddsNotFoundWarning_WhenArtistCannotBeResolved()
    {
        var service = CreateService(CreatePlexClient(BuildArtistNotFoundResponder()));
        var updates = CreateUpdates();
        var warnings = new List<string>();
        var request = CreateRequest(
            localArtistId: 0,
            auth: CreatePlexAuth(),
            avatarPath: null,
            backgroundPath: null,
            biography: null);

        await InvokePushToPlexAsync(service, request, updates, warnings);

        Assert.Contains("Plex artist not found.", warnings);
        Assert.False(GetUpdateFlag(updates, "AvatarUpdated"));
        Assert.False(GetUpdateFlag(updates, "BackgroundUpdated"));
        Assert.False(GetUpdateFlag(updates, "BioUpdated"));
    }

    [Fact]
    public async Task PushToPlexAsync_UpdatesArtworkAndBiography_AndWarnsWhenLockFails()
    {
        var service = CreateService(CreatePlexClient(BuildHappyPathWithLockFailureResponder()));
        var updates = CreateUpdates();
        var warnings = new List<string>();
        var avatarPath = CreateTempFile(".jpg");
        var backgroundPath = CreateTempFile(".png");
        var request = CreateRequest(
            localArtistId: 0,
            auth: CreatePlexAuth(),
            avatarPath: avatarPath,
            backgroundPath: backgroundPath,
            biography: "Biography text");

        await InvokePushToPlexAsync(service, request, updates, warnings);

        Assert.True(GetUpdateFlag(updates, "AvatarUpdated"));
        Assert.True(GetUpdateFlag(updates, "BackgroundUpdated"));
        Assert.True(GetUpdateFlag(updates, "BioUpdated"));
        Assert.Contains("Plex artwork lock failed; Plex may revert avatar/background on refresh.", warnings);
    }

    [Fact]
    public async Task PushToPlexAsync_SkipsArtworkLockAndBiographyUpdate_WhenNoArtworkOrBioProvided()
    {
        var seenRequests = new List<string>();
        var service = CreateService(CreatePlexClient(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var query = request.RequestUri?.Query ?? string.Empty;
            seenRequests.Add($"{request.Method.Method} {path}{query}");

            if (path.EndsWith("/library/sections", StringComparison.Ordinal))
            {
                return Xml("<MediaContainer><Directory type=\"artist\" key=\"1\" /></MediaContainer>");
            }

            if (request.Method == HttpMethod.Get &&
                (path.Contains("/search", StringComparison.Ordinal) || path.Contains("/library/sections/1/all", StringComparison.Ordinal)))
            {
                return Xml("<MediaContainer><Directory type=\"artist\" ratingKey=\"rk1\" title=\"Artist\" librarySectionID=\"1\" /></MediaContainer>");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var updates = CreateUpdates();
        var warnings = new List<string>();
        var request = CreateRequest(
            localArtistId: 0,
            auth: CreatePlexAuth(),
            avatarPath: null,
            backgroundPath: null,
            biography: "   ");

        await InvokePushToPlexAsync(service, request, updates, warnings);

        Assert.Empty(warnings);
        Assert.False(GetUpdateFlag(updates, "AvatarUpdated"));
        Assert.False(GetUpdateFlag(updates, "BackgroundUpdated"));
        Assert.False(GetUpdateFlag(updates, "BioUpdated"));
        Assert.DoesNotContain(seenRequests, entry => entry.Contains("/posters", StringComparison.Ordinal));
        Assert.DoesNotContain(seenRequests, entry => entry.Contains("/arts", StringComparison.Ordinal));
        Assert.DoesNotContain(seenRequests, entry => entry.Contains("thumb.locked=1", StringComparison.Ordinal));
        Assert.DoesNotContain(seenRequests, entry => entry.Contains("summary.value=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PushToPlexAsync_AddsFailureWarning_WhenPlexClientThrows()
    {
        var service = CreateService(CreatePlexClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not xml")
        }));
        var updates = CreateUpdates();
        var warnings = new List<string>();
        var request = CreateRequest(
            localArtistId: 0,
            auth: CreatePlexAuth(),
            avatarPath: null,
            backgroundPath: null,
            biography: null);

        await InvokePushToPlexAsync(service, request, updates, warnings);

        Assert.Contains("Plex update failed.", warnings);
        Assert.False(GetUpdateFlag(updates, "AvatarUpdated"));
        Assert.False(GetUpdateFlag(updates, "BackgroundUpdated"));
        Assert.False(GetUpdateFlag(updates, "BioUpdated"));
    }

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static ArtistMetadataUpdaterService CreateService(PlexApiClient plexClient)
    {
        var instance = (ArtistMetadataUpdaterService)RuntimeHelpers.GetUninitializedObject(typeof(ArtistMetadataUpdaterService));
        SetPrivateField(instance, "_plexClient", plexClient);
        SetPrivateField(instance, "_libraryRepository", RuntimeHelpers.GetUninitializedObject(typeof(LibraryRepository)));
        SetPrivateField(instance, "_logger", NullLogger<ArtistMetadataUpdaterService>.Instance);
        return instance;
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private static PlatformAuthState CreatePlexAuth()
        => new()
        {
            Plex = new PlexAuth
            {
                Url = "http://plex.local:32400",
                Token = "token"
            }
        };

    private string CreateTempFile(string extension)
    {
        var path = Path.Join(Path.GetTempPath(), $"deezspotag-plex-push-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        _tempPaths.Add(path);
        return path;
    }

    private static HttpResponseMessage Xml(string xml, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(xml)
        };

    private static Func<HttpRequestMessage, HttpResponseMessage> BuildArtistNotFoundResponder()
        => request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/library/sections", StringComparison.Ordinal))
            {
                return Xml("<MediaContainer><Directory type=\"artist\" key=\"1\" /></MediaContainer>");
            }

            if (path.Contains("/search", StringComparison.Ordinal) || path.Contains("/library/sections/1/all", StringComparison.Ordinal))
            {
                return Xml("<MediaContainer size=\"0\" />");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

    private static Func<HttpRequestMessage, HttpResponseMessage> BuildHappyPathWithLockFailureResponder()
        => request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var query = request.RequestUri?.Query ?? string.Empty;
            var method = request.Method.Method;

            if (path.EndsWith("/library/sections", StringComparison.Ordinal))
            {
                return Xml("<MediaContainer><Directory type=\"artist\" key=\"1\" /></MediaContainer>");
            }

            if (method == HttpMethod.Get.Method &&
                (path.Contains("/search", StringComparison.Ordinal) || path.Contains("/library/sections/1/all", StringComparison.Ordinal)))
            {
                return Xml("<MediaContainer><Directory type=\"artist\" ratingKey=\"rk1\" title=\"Artist\" librarySectionID=\"1\" /></MediaContainer>");
            }

            if (method == HttpMethod.Post.Method && path.EndsWith("/library/metadata/rk1/posters", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (method == HttpMethod.Post.Method && path.EndsWith("/library/metadata/rk1/arts", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (method == HttpMethod.Put.Method &&
                path.EndsWith("/library/sections/1/all", StringComparison.Ordinal) &&
                query.Contains("thumb.locked=1", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            if (method == HttpMethod.Put.Method &&
                path.EndsWith("/library/sections/1/all", StringComparison.Ordinal) &&
                query.Contains("summary.value=", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

    private static PlexApiClient CreatePlexClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            NullLogger<PlexApiClient>.Instance,
            new HttpClient(new StubHttpMessageHandler(responder)));

    private static object CreateRequest(
        long localArtistId,
        PlatformAuthState auth,
        string? avatarPath,
        string? backgroundPath,
        string? biography)
    {
        var requestType = typeof(ArtistMetadataUpdaterService).GetNestedType("PushMetadataRequest", BindingFlags.NonPublic);
        Assert.NotNull(requestType);
        return Activator.CreateInstance(
            requestType!,
            localArtistId,
            auth,
            "Artist",
            "plex",
            avatarPath,
            backgroundPath,
            biography)!;
    }

    private static object CreateUpdates()
    {
        var updatesType = typeof(ArtistMetadataUpdaterService).GetNestedType("PushUpdateAccumulator", BindingFlags.NonPublic);
        Assert.NotNull(updatesType);
        return Activator.CreateInstance(updatesType!)!;
    }

    private static bool GetUpdateFlag(object updates, string name)
    {
        var prop = updates.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(prop);
        return Assert.IsType<bool>(prop!.GetValue(updates));
    }

    private static async Task InvokePushToPlexAsync(
        ArtistMetadataUpdaterService service,
        object request,
        object updates,
        List<string> warnings)
    {
        var method = typeof(ArtistMetadataUpdaterService).GetMethod("PushToPlexAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = method!.Invoke(
            service,
            [request, updates, warnings, CancellationToken.None]);
        var runningTask = Assert.IsAssignableFrom<Task>(task);
        await runningTask;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
