using System.Text.Json;
using DeezSpoTag.Services.Download.Shared.Models;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadIntentJsonConverterTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Deserialize_QualityNumericToken_BindsAsString()
    {
        const string json = """
            {
              "sourceService": "apple",
              "sourceUrl": "https://music.apple.com/us/album/x/1",
              "quality": 9
            }
            """;

        var intent = JsonSerializer.Deserialize<DownloadIntent>(json, SerializerOptions);

        Assert.NotNull(intent);
        Assert.Equal("9", intent!.Quality);
    }

    [Fact]
    public void Deserialize_QualityStringToken_PreservesValue()
    {
        const string json = """
            {
              "sourceService": "deezer",
              "sourceUrl": "https://www.deezer.com/track/3135556",
              "quality": "FLAC"
            }
            """;

        var intent = JsonSerializer.Deserialize<DownloadIntent>(json, SerializerOptions);

        Assert.NotNull(intent);
        Assert.Equal("FLAC", intent!.Quality);
    }
}
