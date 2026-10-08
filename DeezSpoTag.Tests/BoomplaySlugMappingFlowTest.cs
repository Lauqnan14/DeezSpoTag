using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// End-to-end coverage for the Boomplay slug→numeric bridge: persisted mapping lookup
/// (no HTTP), fail-clean resolution of unmapped slugs against the live (Cloudflare-gated)
/// web page, and the mandatory authenticated-session boundary.
/// </summary>
public sealed class BoomplaySlugMappingFlowTest : IAsyncLifetime
{
    private const string SlugPlaylistUrl = "https://www.boomplay.com/playlists/EQHud9xtGwnEb_YTbYPawvee?from=home";
    private const string Slug = "EQHud9xtGwnEb_YTbYPawvee";
    private const string KnownNumericPlaylistId = "6990547";
    private const string SessionId = "N_AAfRyBMcg1oQTBnuOV1O_d.W4gEmatso.AmZ4t67AyV.nOK6OlLb6zGACx.A7DE.";
    private const string UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/152.0.0.0 Safari/537.36";

    private string _tempRoot = string.Empty;
    private IConfiguration _configuration = default!;
    private LibraryRepository _repository = default!;
    private PlatformAuthService _auth = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-boomplay-slugmap-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={Path.Join(_tempRoot, "library.db")}"
            })
            .Build();
        await new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        _repository = new LibraryRepository(_configuration, NullLogger<LibraryRepository>.Instance);
        _auth = new PlatformAuthService(
            new SlugTestWebHostEnvironment(_tempRoot),
            NullLogger<PlatformAuthService>.Instance,
            DataProtectionProvider.Create(new DirectoryInfo(Path.Join(_tempRoot, "keys"))));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ResolveContentIdAsync_MappedSlug_ReturnsNumericIdWithoutAnyHttpRequest()
    {
        await SaveValidSessionAsync();
        await _repository.UpsertBoomplaySlugMappingAsync(
            new LibraryRepository.BoomplaySlugMappingUpsertInput("playlist", Slug, KnownNumericPlaylistId, SlugPlaylistUrl));

        var stored = await _repository.GetBoomplaySlugMappingAsync("playlist", Slug);
        Assert.NotNull(stored);
        Assert.Equal(KnownNumericPlaylistId, stored!.NumericId);
        Assert.Equal("playlist", stored.ContentType);

        var service = new BoomplayMetadataService(
            new ThrowingHttpClientFactory(),
            _auth,
            NullLogger<BoomplayMetadataService>.Instance,
            _repository);

        var resolved = await service.ResolveContentIdAsync("playlist", SlugPlaylistUrl, CancellationToken.None);

        Assert.Equal(KnownNumericPlaylistId, resolved);
    }

    [Fact]
    public async Task ResolveContentIdAsync_MappedSlug_RequiresAuthenticatedSession()
    {
        await _repository.UpsertBoomplaySlugMappingAsync(
            new LibraryRepository.BoomplaySlugMappingUpsertInput("playlist", Slug, KnownNumericPlaylistId, SlugPlaylistUrl));
        var service = new BoomplayMetadataService(
            new ThrowingHttpClientFactory(),
            _auth,
            NullLogger<BoomplayMetadataService>.Instance,
            _repository);

        var exception = await Assert.ThrowsAsync<BoomplaySourceException>(
            () => service.ResolveContentIdAsync("playlist", SlugPlaylistUrl, CancellationToken.None));

        Assert.Equal(BoomplayFailureCodes.SessionMissing, exception.FailureCode);
    }

    [Fact]
    public async Task ResolveContentIdAsync_UnmappedSlug_FailsLoudAgainstLiveCloudflarePage()
    {
        await SaveValidSessionAsync();
        var service = new BoomplayMetadataService(
            new LiveHttpClientFactory(),
            _auth,
            NullLogger<BoomplayMetadataService>.Instance,
            _repository);

        // The web page is Cloudflare-403 for server clients and no mapping exists yet.
        // Contract: fail loud with a Boomplay failure code (parse-link translates it into
        // bookmarklet-bridge guidance; the watchlist maps it to its own error payload).
        var exception = await Assert.ThrowsAsync<BoomplaySourceException>(
            () => service.ResolveContentIdAsync("playlist", SlugPlaylistUrl, CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(exception.FailureCode));
    }

    [Fact]
    public async Task GetPlaylistAsync_NumericId_RequiresAuthenticatedSessionBeforeHttp()
    {
        var service = new BoomplayMetadataService(
            new ThrowingHttpClientFactory(),
            _auth,
            NullLogger<BoomplayMetadataService>.Instance,
            _repository);

        var exception = await Assert.ThrowsAsync<BoomplaySourceException>(
            () => service.GetPlaylistAsync(KnownNumericPlaylistId, CancellationToken.None));

        Assert.Equal(BoomplayFailureCodes.SessionMissing, exception.FailureCode);
    }

    [Fact]
    public async Task GetSongsAsync_RequiresAuthenticatedSessionInsteadOfReturningAnEmptyList()
    {
        var service = new BoomplayMetadataService(
            new ThrowingHttpClientFactory(),
            _auth,
            NullLogger<BoomplayMetadataService>.Instance,
            _repository);

        var exception = await Assert.ThrowsAsync<BoomplaySourceException>(
            () => service.GetSongsAsync(["256487581"], CancellationToken.None));

        Assert.Equal(BoomplayFailureCodes.SessionMissing, exception.FailureCode);
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new InvalidOperationException("No HTTP request may be made when a slug mapping exists.");
    }

    private async Task SaveValidSessionAsync()
    {
        await _auth.UpdateAsync(state =>
        {
            state.Boomplay = new BoomplayAuth
            {
                Cookie = $"sessionID={SessionId}",
                UserAgent = UserAgent,
                SessionValid = true,
                LastStatus = "session_verified"
            };
            return state.Boomplay;
        });
    }

    private sealed class LiveHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class SlugTestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Web";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Development";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
    }
}
