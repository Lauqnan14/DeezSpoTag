using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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

    [Fact]
    public async Task PrepareVisualsAsync_DoesNotUseBackgroundSlotAsAvatarOrAvatarSlotAsBackground()
    {
        var tempRoot = CreateTempDirectory();
        var service = CreateServiceForVisualPreparation(tempRoot);
        var previousDataRoot = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        Environment.SetEnvironmentVariable("DEEZSPOTAG_DATA_DIR", Path.Join(tempRoot, "Data"));
        try
        {
            var managedRoot = Path.Join(tempRoot, "Data", "library-artist-images", "spotify", "artists", "42");
            Directory.CreateDirectory(managedRoot);

            var avatarPath = Path.Join(managedRoot, "avatar.png");
            var backgroundPath = Path.Join(managedRoot, "background.png");
            WriteSolidPng(avatarPath, new Rgba32(220, 20, 60));
            WriteSolidPng(backgroundPath, new Rgba32(25, 80, 210));
            var expectedAvatar = await File.ReadAllBytesAsync(avatarPath);
            var expectedBackground = await File.ReadAllBytesAsync(backgroundPath);

            var tracked = new MetadataUpdaterTrackedArtist
            {
                ArtistId = 42,
                ArtistName = "Artist",
                IncludeAvatar = true,
                IncludeBackground = true
            };

            var prepared = await InvokePrepareVisualsAsync(service, tracked);

            Assert.Equal(Path.GetFullPath(avatarPath), Path.GetFullPath(GetPreparedPath(prepared, "AvatarPath")!));
            Assert.Equal(Path.GetFullPath(backgroundPath), Path.GetFullPath(GetPreparedPath(prepared, "BackgroundPath")!));
            Assert.Equal(expectedAvatar, await File.ReadAllBytesAsync(avatarPath));
            Assert.Equal(expectedBackground, await File.ReadAllBytesAsync(backgroundPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEZSPOTAG_DATA_DIR", previousDataRoot);
        }
    }

    [Fact]
    public async Task RegisterFromManualPushAsync_MarksArtistAsRecentlyPushed()
    {
        var tempRoot = CreateTempDirectory();
        var statePath = Path.Join(tempRoot, "metadata-updater-state.json");
        var service = CreateServiceForManualRegistration(statePath);

        await service.RegisterFromManualPushAsync(
            new ManualPushRegistrationRequest
            {
                ArtistId = 42,
                ArtistName = "Artist",
                Target = "plex",
                IncludeAvatar = true,
                IncludeBackground = true,
                IncludeBio = true,
                IntervalDays = 30
            },
            CancellationToken.None);

        var stateJson = await File.ReadAllTextAsync(statePath);
        var state = JsonSerializer.Deserialize<MetadataUpdaterState>(stateJson);
        var tracked = Assert.Single(state!.Artists);
        Assert.NotNull(tracked.LastPushedAtUtc);
        Assert.Equal(0, tracked.AvatarRotationIndex);
        Assert.Equal(0, tracked.BackgroundRotationIndex);
    }

    [Fact]
    public void ScheduleEligibility_UsesRegistrationTime_WhenArtistHasNotBeenPushedYet()
    {
        var now = DateTimeOffset.UtcNow;
        var tracked = new MetadataUpdaterTrackedArtist
        {
            ArtistId = 42,
            ArtistName = "Artist",
            IntervalDays = 30,
            LastPushedAtUtc = null,
            UpdatedAtUtc = now.AddDays(-1)
        };

        Assert.True(InvokeShouldSkipTrackedArtist(tracked, new MetadataUpdaterRunRequest(), 30, now));
        Assert.False(InvokeIsTrackedArtistDueForAutomaticRun(tracked, now));
    }

    [Fact]
    public void ScheduleEligibility_TreatsZeroIntervalAsManualForceOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var tracked = new MetadataUpdaterTrackedArtist
        {
            ArtistId = 42,
            ArtistName = "Artist",
            IntervalDays = 0,
            LastPushedAtUtc = now.AddDays(-365),
            UpdatedAtUtc = now.AddDays(-365)
        };

        Assert.True(InvokeShouldSkipTrackedArtist(tracked, new MetadataUpdaterRunRequest(), 0, now));
        Assert.False(InvokeIsTrackedArtistDueForAutomaticRun(tracked, now));
        Assert.False(InvokeShouldSkipTrackedArtist(tracked, new MetadataUpdaterRunRequest { Force = true }, 0, now));
    }

    [Fact]
    public void ScheduleEligibility_AllowsAutomaticRunOnlyAfterIntervalElapses()
    {
        var now = DateTimeOffset.UtcNow;
        var tracked = new MetadataUpdaterTrackedArtist
        {
            ArtistId = 42,
            ArtistName = "Artist",
            IntervalDays = 30,
            LastPushedAtUtc = now.AddDays(-31),
            UpdatedAtUtc = now.AddDays(-1)
        };

        Assert.False(InvokeShouldSkipTrackedArtist(tracked, new MetadataUpdaterRunRequest(), 30, now));
        Assert.True(InvokeIsTrackedArtistDueForAutomaticRun(tracked, now));
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
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
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

    private static ArtistMetadataUpdaterService CreateServiceForVisualPreparation(string contentRootPath)
    {
        var instance = (ArtistMetadataUpdaterService)RuntimeHelpers.GetUninitializedObject(typeof(ArtistMetadataUpdaterService));
        SetPrivateField(instance, "_environment", new StubWebHostEnvironment(contentRootPath));
        SetPrivateField(instance, "_logger", NullLogger<ArtistMetadataUpdaterService>.Instance);
        return instance;
    }

    private static ArtistMetadataUpdaterService CreateServiceForManualRegistration(string statePath)
    {
        var instance = (ArtistMetadataUpdaterService)RuntimeHelpers.GetUninitializedObject(typeof(ArtistMetadataUpdaterService));
        SetPrivateField(instance, "_statePath", statePath);
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

    private string CreateTempDirectory()
    {
        var path = Path.Join(Path.GetTempPath(), $"deezspotag-plex-push-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempPaths.Add(path);
        return path;
    }

    private static void WriteSolidPng(string path, Rgba32 color)
    {
        using var image = new Image<Rgba32>(4, 4, color);
        image.SaveAsPng(path);
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

    private static async Task<object> InvokePrepareVisualsAsync(
        ArtistMetadataUpdaterService service,
        MetadataUpdaterTrackedArtist tracked)
    {
        var method = typeof(ArtistMetadataUpdaterService).GetMethod("PrepareVisualsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var candidateType = typeof(ArtistMetadataUpdaterService).GetNestedType("ArtworkCandidate", BindingFlags.NonPublic);
        Assert.NotNull(candidateType);
        var candidates = Array.CreateInstance(candidateType!, 0);

        var task = method!.Invoke(service, [tracked, candidates, CancellationToken.None]);
        var runningTask = Assert.IsAssignableFrom<Task>(task);
        await runningTask;
        var resultProperty = runningTask.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(resultProperty);
        return resultProperty!.GetValue(runningTask)!;
    }

    private static string? GetPreparedPath(object prepared, string propertyName)
    {
        var property = prepared.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        return Assert.IsType<string?>(property!.GetValue(prepared));
    }

    private static bool InvokeShouldSkipTrackedArtist(
        MetadataUpdaterTrackedArtist tracked,
        MetadataUpdaterRunRequest request,
        int effectiveIntervalDays,
        DateTimeOffset nowUtc)
    {
        var method = typeof(ArtistMetadataUpdaterService).GetMethod("ShouldSkipTrackedArtist", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(null, [tracked, request, effectiveIntervalDays, nowUtc]));
    }

    private static bool InvokeIsTrackedArtistDueForAutomaticRun(
        MetadataUpdaterTrackedArtist tracked,
        DateTimeOffset nowUtc)
    {
        var method = typeof(ArtistMetadataUpdaterService).GetMethod("IsTrackedArtistDueForAutomaticRun", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(null, [tracked, nowUtc]));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public StubWebHostEnvironment(string rootPath)
        {
            ContentRootPath = rootPath;
            ContentRootFileProvider = new PhysicalFileProvider(rootPath);
            WebRootPath = rootPath;
            WebRootFileProvider = new PhysicalFileProvider(rootPath);
        }

        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
    }
}
