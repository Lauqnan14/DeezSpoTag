using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Web.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DeezSpoTagSearchSerializationTests
{
    [Fact]
    public void DeezerJTokenResultsAreConvertedToSystemTextJsonValues()
    {
        var method = typeof(DeezSpoTagSearchService).GetMethod(
            "ToObjectList",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var source = JObject.Parse("""
            {
              "id": 122381,
              "name": "August Alsina",
              "picture_xl": "https://example.test/artist.jpg"
            }
            """);
        var result = Assert.IsType<List<object>>(method!.Invoke(null, new object?[] { new object[] { source } }));
        var artist = Assert.IsType<JsonElement>(Assert.Single(result));

        Assert.Equal(122381, artist.GetProperty("id").GetInt32());
        Assert.Equal("August Alsina", artist.GetProperty("name").GetString());
        Assert.Equal("https://example.test/artist.jpg", artist.GetProperty("picture_xl").GetString());
    }
}
