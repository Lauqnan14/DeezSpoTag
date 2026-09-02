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
/// web page, and the sessionless mobile-API playlist fetch by numeric ID.
/// </summary>
public sealed class BoomplaySlugMappingFlowTests : IAsyncLifetime
{
    private const string SlugPlaylistUrl = "https://www.boomplay.com/playlists/EQHud9xtGwnEb_YTbYPawvee?from=home";
    private const string Slug = "EQHud9xtGwnEb_YTbYPawvee";
    private const string KnownNumericPlaylistId = "6990547";

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
    public async Task ResolveContentIdAsync_UnmappedSlug_FailsLoudAgainstLiveCloudflarePage()
    {
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
    public async Task GetPlaylistAsync_NumericId_FetchesSessionlessFromLiveMobileApi()
    {
        var service = new BoomplayMetadataService(
            new LiveHttpClientFactory(),
            _auth,
            NullLogger<BoomplayMetadataService>.Instance,
            _repository);

        var playlist = await service.GetPlaylistAsync(KnownNumericPlaylistId, CancellationToken.None);

        Assert.NotNull(playlist);
        Assert.False(string.IsNullOrWhiteSpace(playlist!.Title));
        Assert.NotEmpty(playlist.TrackIds);
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new InvalidOperationException("No HTTP request may be made when a slug mapping exists.");
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
