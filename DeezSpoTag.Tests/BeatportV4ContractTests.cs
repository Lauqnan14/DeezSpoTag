using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BeatportV4ContractTests
{
    [Fact]
    public void Client_uses_authenticated_v4_catalog_and_removes_legacy_scraping()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "BeatportClient.cs");
        Assert.Contains("catalog/search/", source, StringComparison.Ordinal);
        Assert.Contains("catalog/tracks/{id}/", source, StringComparison.Ordinal);
        Assert.Contains("catalog/releases/{id}/", source, StringComparison.Ordinal);
        Assert.Contains("AuthenticationHeaderValue(\"Bearer\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("embed.beatport.com", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("__NEXT_DATA__", source, StringComparison.Ordinal);
        Assert.DoesNotContain("www.beatport.com/search", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V4_search_response_deserializes_results()
    {
        const string json = """{"count":1,"next":null,"results":[{"id":123,"name":"Track","mix_name":"Original Mix","isrc":"AA111","length_ms":180000,"artists":[{"id":2,"name":"Artist"}],"genre":{"id":3,"name":"House"},"release":{"id":4,"name":"Release","label":{"id":5,"name":"Label"},"image":{"id":6,"dynamic_uri":"https://img/{w}x{h}.jpg"}}}]}""";
        var result = JsonSerializer.Deserialize<BeatportTrackResults>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var track = Assert.Single(Assert.IsType<BeatportTrackResults>(result).Results);
        Assert.Equal(123, track.Id);
        Assert.Equal("Artist", Assert.Single(track.Artists).Name);
        Assert.Equal(180000, track.LengthMs);
    }

    [Fact]
    public void OAuth_uses_authorization_code_pkce_and_refresh_tokens()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "BeatportTokenService.cs");
        Assert.Contains("code_challenge_method", source, StringComparison.Ordinal);
        Assert.Contains("S256", source, StringComparison.Ordinal);
        Assert.Contains("authorization_code", source, StringComparison.Ordinal);
        Assert.Contains("refresh_token", source, StringComparison.Ordinal);
        Assert.Contains("auth/o/token/", source, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }
}
