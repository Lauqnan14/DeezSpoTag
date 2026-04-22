using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyBlobServiceBlobKindTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly SpotifyBlobService _service;

    public SpotifyBlobServiceBlobKindTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), $"deezspotag-blob-kind-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _service = new SpotifyBlobService(new StubWebHostEnvironment(_tempRoot), NullLogger<SpotifyBlobService>.Instance);
    }

    [Fact]
    public async Task Detects_WebPlayer_And_Librespot_Blob_Types_Correctly()
    {
        var webBlobPath = Path.Join(_tempRoot, "web.json");
        var librespotBlobPath = Path.Join(_tempRoot, "credentials.json");

        await WriteJsonAsync(webBlobPath, new
        {
            version = 1,
            createdAt = DateTimeOffset.UtcNow,
            userAgent = "test-agent",
            cookies = new[]
            {
                new { name = "sp_dc", value = "cookie-value", domain = ".spotify.com", path = "/" }
            }
        });

        await WriteJsonAsync(librespotBlobPath, new
        {
            username = "test-user",
            credentials = "abc123",
            type = "AUTHENTICATION_STORED_SPOTIFY_CREDENTIALS"
        });

        Assert.True(await _service.IsWebPlayerBlobAsync(webBlobPath));
        Assert.False(await _service.IsLibrespotBlobAsync(webBlobPath));

        Assert.False(await _service.IsWebPlayerBlobAsync(librespotBlobPath));
        Assert.True(await _service.IsLibrespotBlobAsync(librespotBlobPath));
    }

    [Fact]
    public async Task GetWebApiAccessTokenAsync_Returns_InvalidBlobError_For_WebPlayerBlob()
    {
        var webBlobPath = Path.Join(_tempRoot, "web-only.json");
        await WriteJsonAsync(webBlobPath, new
        {
            version = 1,
            createdAt = DateTimeOffset.UtcNow,
            userAgent = "test-agent",
            cookies = new[]
            {
                new { name = "sp_dc", value = "cookie-value", domain = ".spotify.com", path = "/" }
            }
        });

        var result = await _service.GetWebApiAccessTokenAsync(webBlobPath, allowRetries: false);

        Assert.Null(result.AccessToken);
        Assert.Equal("invalid_librespot_blob", result.Error);
    }

    private static async Task WriteJsonAsync<T>(string path, T payload)
    {
        var json = JsonSerializer.Serialize(payload);
        await File.WriteAllTextAsync(path, json);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public StubWebHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new PhysicalFileProvider(contentRootPath);
            WebRootPath = contentRootPath;
            WebRootFileProvider = new PhysicalFileProvider(contentRootPath);
            ApplicationName = "DeezSpoTag.Tests";
            EnvironmentName = "Development";
        }

        public string ApplicationName { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; }
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
