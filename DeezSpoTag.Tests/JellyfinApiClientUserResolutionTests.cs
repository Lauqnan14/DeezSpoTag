using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Jellyfin;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class JellyfinApiClientUserResolutionTests
{
    [Fact]
    public async Task ResolveUserAsync_ReturnsCurrentUser_WhenUsersMeSucceeds()
    {
        using var handler = new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/Users/Me")
            {
                return JsonResponse(HttpStatusCode.OK, "{\"Id\":\"me-id\",\"Name\":\"Current User\"}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = CreateClient(handler);

        var resolved = await client.ResolveUserAsync(
            "http://jellyfin.local",
            "api-key",
            username: "ignored",
            userId: "ignored",
            cancellationToken: CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("me-id", resolved!.Id);
        Assert.Equal("Current User", resolved.Name);
        Assert.Single(handler.RequestedUris);
        Assert.EndsWith("/Users/Me", handler.RequestedUris[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveUserAsync_FallsBackToUserId_WhenUsersMeFails()
    {
        using var handler = new StubHandler(request =>
        {
            return request.RequestUri?.AbsolutePath switch
            {
                "/Users/Me" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                "/Users/id-42" => JsonResponse(HttpStatusCode.OK, "{\"Id\":\"id-42\",\"Name\":\"By Id\"}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });

        var client = CreateClient(handler);

        var resolved = await client.ResolveUserAsync(
            "http://jellyfin.local",
            "api-key",
            username: null,
            userId: "id-42",
            cancellationToken: CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("id-42", resolved!.Id);
        Assert.Equal("By Id", resolved.Name);
        Assert.Equal(2, handler.RequestedUris.Count);
        Assert.EndsWith("/Users/Me", handler.RequestedUris[0], StringComparison.Ordinal);
        Assert.EndsWith("/Users/id-42", handler.RequestedUris[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveUserAsync_FallsBackToUserName_WhenIdLookupFails()
    {
        using var handler = new StubHandler(request =>
        {
            return request.RequestUri?.AbsolutePath switch
            {
                "/Users/Me" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                "/Users/missing-user" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/Users" => JsonResponse(
                    HttpStatusCode.OK,
                    "[{\"Id\":\"u-1\",\"Name\":\"Alice\"},{\"Id\":\"u-2\",\"Name\":\"Bob\"}]"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });

        var client = CreateClient(handler);

        var resolved = await client.ResolveUserAsync(
            "http://jellyfin.local",
            "api-key",
            username: "Bob",
            userId: "missing-user",
            cancellationToken: CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("u-2", resolved!.Id);
        Assert.Equal("Bob", resolved.Name);
        Assert.Equal(3, handler.RequestedUris.Count);
        Assert.EndsWith("/Users/Me", handler.RequestedUris[0], StringComparison.Ordinal);
        Assert.EndsWith("/Users/missing-user", handler.RequestedUris[1], StringComparison.Ordinal);
        Assert.EndsWith("/Users", handler.RequestedUris[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveUserAsync_ReturnsNull_WhenAllLookupsFail()
    {
        using var handler = new StubHandler(request =>
        {
            return request.RequestUri?.AbsolutePath switch
            {
                "/Users/Me" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                "/Users/missing-user" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/Users" => JsonResponse(HttpStatusCode.OK, "[]"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });

        var client = CreateClient(handler);

        var resolved = await client.ResolveUserAsync(
            "http://jellyfin.local",
            "api-key",
            username: "NoMatch",
            userId: "missing-user",
            cancellationToken: CancellationToken.None);

        Assert.Null(resolved);
        Assert.Equal(3, handler.RequestedUris.Count);
    }

    [Fact]
    public async Task GetUserByIdAsync_ReturnsNull_WhenUserIdMissing()
    {
        using var handler = new StubHandler(_ => throw new InvalidOperationException("HTTP should not be called."));
        var client = CreateClient(handler);

        var result = await client.GetUserByIdAsync(
            "http://jellyfin.local",
            "api-key",
            userId: "   ",
            cancellationToken: CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(handler.RequestedUris);
    }

    [Fact]
    public async Task GetUserByNameAsync_ReturnsNull_WhenUsernameMissing()
    {
        using var handler = new StubHandler(_ => throw new InvalidOperationException("HTTP should not be called."));
        var client = CreateClient(handler);

        var result = await client.GetUserByNameAsync(
            "http://jellyfin.local",
            "api-key",
            username: "",
            cancellationToken: CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(handler.RequestedUris);
    }

    [Fact]
    public async Task UpdateItemMetadataAsync_UsesUserScopedItemReadBeforePostingUpdate()
    {
        using var handler = new StubHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get
                && request.RequestUri?.AbsolutePath == "/Users/user-1/Items/playlist-1")
            {
                return JsonResponse(HttpStatusCode.OK, """
                    {
                      "Id": "playlist-1",
                      "Name": "Old Name",
                      "Overview": ""
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/Items/playlist-1")
            {
                var body = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                Assert.Contains("\"Name\":\"Bongo Pop\"", body, StringComparison.Ordinal);
                Assert.Contains("\"Overview\":\"Bongo sounds\"", body, StringComparison.Ordinal);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = CreateClient(handler);

        var updated = await client.UpdateItemMetadataAsync(
            "http://jellyfin.local",
            "api-key",
            "user-1",
            "playlist-1",
            "Bongo Pop",
            "Bongo sounds",
            CancellationToken.None);

        Assert.True(updated);
        Assert.Equal(2, handler.RequestedUris.Count);
        Assert.Contains("/Users/user-1/Items/playlist-1", handler.RequestedUris[0], StringComparison.Ordinal);
        Assert.Contains("/Items/playlist-1", handler.RequestedUris[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateItemPrimaryImageFromFileAsync_UploadsBase64WithImageContentType()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"jellyfin-playlist-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(imagePath, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
        try
        {
            using var handler = new StubHandler(async (request, cancellationToken) =>
            {
                if (request.Method == HttpMethod.Post
                    && request.RequestUri?.AbsolutePath == "/Items/playlist-1/Images/Primary")
                {
                    Assert.Equal("image/jpeg", request.Content?.Headers.ContentType?.MediaType);
                    var body = request.Content is null
                        ? string.Empty
                        : await request.Content.ReadAsStringAsync(cancellationToken);
                    Assert.Equal(Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }), body);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                if (request.Method == HttpMethod.Get
                    && request.RequestUri?.AbsolutePath == "/Items/playlist-1/Images/Primary")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 })
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            var client = CreateClient(handler);

            var updated = await client.UpdateItemPrimaryImageFromFileAsync(
                "http://jellyfin.local",
                "api-key",
                "playlist-1",
                imagePath,
                "image/jpeg",
                CancellationToken.None);

            Assert.True(updated);
            Assert.Equal(2, handler.RequestedUris.Count);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task UpdateArtistImageAsync_UploadsBase64WithImageContentType()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"jellyfin-artist-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(imagePath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        try
        {
            using var handler = new StubHandler(async (request, cancellationToken) =>
            {
                if (request.Method == HttpMethod.Post
                    && request.RequestUri?.AbsolutePath == "/Items/artist-1/Images/Primary")
                {
                    Assert.Equal("image/png", request.Content?.Headers.ContentType?.MediaType);
                    var body = request.Content is null
                        ? string.Empty
                        : await request.Content.ReadAsStringAsync(cancellationToken);
                    Assert.Equal(Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 }), body);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            var client = CreateClient(handler);

            var updated = await client.UpdateArtistImageAsync(
                "http://jellyfin.local",
                "api-key",
                "artist-1",
                imagePath,
                CancellationToken.None);

            Assert.True(updated);
            Assert.Single(handler.RequestedUris);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    private static JellyfinApiClient CreateClient(HttpMessageHandler handler)
    {
        return new JellyfinApiClient(new HttpClient(handler));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string payload)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = (request, _) => Task.FromResult(responder(request));
        }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public List<string> RequestedUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            return _responder(request, cancellationToken);
        }
    }
}
