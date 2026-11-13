using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Services.Download.Identity;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Metadata.Qobuz;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Reproductions for the open CodeQL log-forging alerts (#486-#489): user-provided
/// values interpolated into log entries must never reach the sink with carriage
/// returns or line feeds intact, or a forged attacker line becomes indistinguishable
/// from a trusted application log line. The repo's established sanitizer is
/// <c>DeezSpoTag.Core.Security.LogSanitizer.OneLine</c>; these tests drive the real
/// private methods through reflection so the actual logging arm is exercised.
/// </summary>
public sealed class LogForgingSanitizationTest
{
    private const string ForgedPayload = "Legit Name\r\n[ERR] forged trusted log line";

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " :: " + exception.Message));
    }

    private sealed class ProfileEnvironment(string root) : IWebHostEnvironment, DeezSpoTag.Web.Services.IAppDataRootOverride
    {
        public string? AppDataRoot => root;
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class ThrowingQobuzMetadata : IQobuzMetadataService
    {
        public Task<QobuzTrack?> FindTrackByISRC(string isrc, CancellationToken ct) => throw new InvalidOperationException("qobuz down");
        public Task<QobuzAlbum?> FindAlbumByUPC(string upc, CancellationToken ct) => throw new InvalidOperationException("qobuz down");
        public Task<QobuzArtist?> FindArtistByName(string name, CancellationToken ct) => throw new InvalidOperationException("qobuz down");
        public Task<List<QobuzTrack>> SearchTracks(string query, CancellationToken ct) => throw new InvalidOperationException("qobuz down");
        public Task<List<QobuzTrack>> SearchAlbumTracks(string query, CancellationToken ct) => throw new InvalidOperationException("qobuz down");
        public Task<List<QobuzTrack>> SearchTracksAutosuggest(string query, string? store, CancellationToken ct) => throw new InvalidOperationException("qobuz down");
        public Task<List<QobuzAlbum>> SearchAlbums(string query, CancellationToken ct) => throw new InvalidOperationException("qobuz down");
        public Task<List<QobuzArtist>> SearchArtists(string query, CancellationToken ct) => throw new InvalidOperationException("qobuz down");
        public Task<QobuzArtist?> GetArtistDiscography(int artistId, string store, CancellationToken ct) => throw new InvalidOperationException("qobuz down");
        public Task<List<QobuzAlbum>> GetArtistAlbums(int artistId, string store, CancellationToken ct) => throw new InvalidOperationException("qobuz down");
        public Task<QobuzTrack?> GetTrack(int trackId, CancellationToken ct) => throw new InvalidOperationException("qobuz down");
    }

    private sealed class ThrowingTidalTokens : DeezSpoTag.Integrations.Tidal.ITidalAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("tidal down");
        public Task<string> GetCountryCodeAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("tidal down");
        public Task<bool> HasAuthenticatedSessionAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> ValidateCredentialsAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public void Invalidate() { }
    }

    private sealed class StubIdentityResolver(TrackIdentityResolution resolution) : ITrackIdentityResolver
    {
        public Task<TrackIdentityResolution> ResolveAsync(TrackIdentityResolutionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(resolution);
    }

    private static void SetField(object target, string name, object? value)
        => target.GetType()
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    private static object Invoke(object target, string name, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Method '{name}' was not found.");
        var parameters = method.GetParameters();
        if (arguments.Length < parameters.Length && parameters.Skip(arguments.Length).All(parameter => parameter.HasDefaultValue))
            arguments = arguments.Concat(parameters.Skip(arguments.Length).Select(parameter => parameter.DefaultValue)).ToArray();
        try
        {
            return method.Invoke(target, arguments)!;
        }
        catch (TargetInvocationException wrapper) when (wrapper.InnerException is not null)
        {
            throw wrapper.InnerException;
        }
    }

    private static void AssertLogEntriesCannotForgeLines(IReadOnlyList<string> messages, string mustPreservePrefix)
    {
        Assert.True(messages.Count > 0, "The expected log entry was never written.");
        foreach (var message in messages)
        {
            Assert.DoesNotContain('\r', message);
            Assert.DoesNotContain('\n', message);
        }

        // The diagnostic value survives; only its line structure is neutralized.
        Assert.Contains(messages, message => message.Contains(mustPreservePrefix, StringComparison.Ordinal));
    }

    private static DeezSpoTag.Web.Services.ArtistArtworkCatalogService CreateOverlapCatalog(
        RecordingLogger<DeezSpoTag.Web.Services.ArtistArtworkCatalogService> logger)
    {
        return new DeezSpoTag.Web.Services.ArtistArtworkCatalogService(
            null!, null!, null!, new ThrowingTidalTokens(), null!, new ThrowingQobuzMetadata(),
            null!, null!, null!, new ProfileEnvironment("/tmp/opencode/log-forging-test"), logger);
    }

    private static string RenderedMessages<T>(RecordingLogger<T> logger) => string.Join(Environment.NewLine, logger.Messages);

    // ---- ArtistArtworkCatalogService.Matching.Overlap.cs (alerts #486-#488) ----

    [Fact]
    public async Task DeezerArtistSearchFailure_LogsSanitizedArtistName()
    {
        var logger = new RecordingLogger<DeezSpoTag.Web.Services.ArtistArtworkCatalogService>();
        var catalog = CreateOverlapCatalog(logger);

        try
        {
            await (Task)Invoke(catalog, "SearchDeezerArtistCandidatesAsync", ForgedPayload, CancellationToken.None);
        }
        catch (NullReferenceException)
        {
            // The stubbed provider failure that routes control into the logged catch arm.
        }

        AssertLogEntriesCannotForgeLines(logger.Messages, "Legit Name");
        Assert.Contains("Deezer artist search failed", RenderedMessages(logger), StringComparison.Ordinal);
    }

    [Fact]
    public async Task QobuzArtistSearchFailure_LogsSanitizedArtistName()
    {
        var logger = new RecordingLogger<DeezSpoTag.Web.Services.ArtistArtworkCatalogService>();
        var catalog = CreateOverlapCatalog(logger);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await (Task)Invoke(catalog, "SearchQobuzArtistCandidatesAsync", ForgedPayload, CancellationToken.None));

        AssertLogEntriesCannotForgeLines(logger.Messages, "Legit Name");
        Assert.Contains("Qobuz artist search failed", RenderedMessages(logger), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TidalArtistSearchFailure_LogsSanitizedArtistName()
    {
        var logger = new RecordingLogger<DeezSpoTag.Web.Services.ArtistArtworkCatalogService>();
        var catalog = CreateOverlapCatalog(logger);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await (Task)Invoke(catalog, "SearchTidalArtistCandidatesAsync", ForgedPayload, CancellationToken.None));

        AssertLogEntriesCannotForgeLines(logger.Messages, "Legit Name");
        Assert.Contains("Tidal artist search failed", RenderedMessages(logger), StringComparison.Ordinal);
    }

    // ---- DownloadIntentService.cs (alert #489) ----

    [Fact]
    public async Task YouTubeIdentityFailure_LogsSanitizedResolution()
    {
        var logger = new RecordingLogger<DeezSpoTag.Web.Services.DownloadIntentService>();
        var forgedReason = "No verified match for 'Legit Name\r\n[ERR] forged identity line'";
        var resolution = new TrackIdentityResolution(
            "Title", "Artist", "Album", null, null,
            "", null, "", null, "", null, null, null, null, null, null,
            "", null, "", null, "", null,
            [new PlatformIdentityCandidate("spotify", null, null, "test", Accepted: false, Reason: forgedReason)]);

        var settingsService = CreateIsolatedSettingsService();
        var service = (DeezSpoTag.Web.Services.DownloadIntentService)RuntimeHelpers.GetUninitializedObject(
            typeof(DeezSpoTag.Web.Services.DownloadIntentService));
        SetField(service, "_logger", logger);
        SetField(service, "_settingsService", settingsService);
        SetField(service, "_trackIdentityResolver", new StubIdentityResolver(resolution));

        var intent = new DownloadIntent
        {
            SourceService = "ytmusic",
            SourceUrl = "https://music.youtube.com/watch?v=dQw4w9WgXcQ",
            Title = "Legit Name",
            Artist = "Legit Artist",
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => (Task)Invoke(service, "PrepareEnqueueAsync", intent, false, CancellationToken.None));

        Assert.Contains("Could not find a verified match", exception.Message, StringComparison.Ordinal);
        AssertLogEntriesCannotForgeLines(logger.Messages, "Legit Name");
        Assert.Contains("could not be resolved", RenderedMessages(logger), StringComparison.Ordinal);
    }

    private static DeezSpoTag.Services.Settings.DeezSpoTagSettingsService CreateIsolatedSettingsService()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-forging-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", root);
        try
        {
            return new DeezSpoTag.Services.Settings.DeezSpoTagSettingsService(
                NullLogger<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>.Instance);
        }
        finally
        {
            if (previous is null)
            {
                Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", null);
            }
            else
            {
                Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", previous);
            }
        }
    }
}
