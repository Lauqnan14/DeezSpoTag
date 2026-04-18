using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MusixmatchClientTests
{
    [Fact]
    public async Task FetchLyricsAsync_ToleratesMalformedMacroBody_AndReturnsPartialPayload()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri == null)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            var uri = request.RequestUri.ToString();
            if (uri.Contains("/token.get?", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                    {
                      "message": {
                        "header": { "status_code": 200 },
                        "body": { "user_token": "test-token" }
                      }
                    }
                    """);
            }

            if (uri.Contains("/macro.subtitles.get?", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                    {
                      "message": {
                        "header": { "status_code": 200 },
                        "body": {
                          "macro_calls": {
                            "track.lyrics.get": {
                              "message": {
                                "header": { "status_code": 200 },
                                "body": []
                              }
                            },
                            "track.subtitles.get": {
                              "message": {
                                "header": { "status_code": 200 },
                                "body": {
                                  "subtitle_list": [
                                    {
                                      "subtitle": {
                                        "subtitle_id": 1,
                                        "subtitle_body": "[00:01.00]Hello",
                                        "subtitle_length": 123,
                                        "subtitle_language": "en",
                                        "subtitle_language_description": "English"
                                      }
                                    }
                                  ]
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(handler);
        var client = new MusixmatchClient(httpClient, NullLogger<MusixmatchClient>.Instance);

        var result = await client.FetchLyricsAsync("Song", "Artist", CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.MacroCalls.ContainsKey("track.lyrics.get"));
        Assert.True(result.MacroCalls.ContainsKey("track.subtitles.get"));
        Assert.Null(result.MacroCalls["track.lyrics.get"].Message.Body);
        Assert.NotNull(result.MacroCalls["track.subtitles.get"].Message.Body?.SubtitleList);
    }

    private static HttpResponseMessage JsonResponse(string payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}
