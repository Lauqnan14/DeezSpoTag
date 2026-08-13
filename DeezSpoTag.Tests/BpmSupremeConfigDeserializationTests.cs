using System.Text.Json;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BpmSupremeConfigDeserializationTests
{
    [Fact]
    public void LoadConfig_KeepsCredentialsWhenLibraryIsSupremeString()
    {
        var json = """
            {
              "email": "dj@example.com",
              "password": "secret",
              "library": "Supreme"
            }
            """;

        var parsed = JsonSerializer.Deserialize<BpmSupremeConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(parsed);
        Assert.Equal("dj@example.com", parsed!.Email);
        Assert.Equal("secret", parsed.Password);
        Assert.Equal(BpmSupremeLibrary.Supreme, parsed.Library);
    }

    [Fact]
    public void LoadConfig_AcceptsLatinoLibrary()
    {
        var json = """{"email":"a@b.c","password":"p","library":"Latino"}""";
        var parsed = JsonSerializer.Deserialize<BpmSupremeConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.Equal(BpmSupremeLibrary.Latino, parsed!.Library);
    }
}
