using System.Text.Json;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AcoustIdLookupTests
{
    [Fact]
    public void LookupResponse_ParsesRecordingCandidates()
    {
        const string json = """
        {
          "status": "ok",
          "results": [
            {
              "score": 0.93,
              "duration": 212,
              "recordings": [
                {
                  "id": "rec-1",
                  "title": "Song A",
                  "duration": 212,
                  "artists": [ { "name": "Artist A" } ],
                  "releases": [ { "title": "Album A", "date": { "year": "2020" } } ]
                }
              ]
            },
            {
              "score": 0.5,
              "recordings": [ { "id": "rec-2", "title": "Song B" } ]
            }
          ]
        }
        """;

        var response = JsonSerializer.Deserialize<AcoustIdLookupResponse>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(response);
        Assert.Equal("ok", response!.Status);
        Assert.Equal(2, response.Results!.Count);
        Assert.Equal(0.93, response.Results[0].Score);
        Assert.Equal("rec-1", response.Results[0].Recordings![0].Id);
        Assert.Equal("Album A", response.Results[0].Recordings[0].Releases![0].Title);
        Assert.Equal("2020", response.Results[0].Recordings[0].Releases[0].Date!.Year);
    }

    [Fact]
    public void LookupResponse_ParsesErrorPayload()
    {
        const string json = """
        {
          "status": "error",
          "error": { "code": 6, "message": "fingerprint not found" }
        }
        """;

        var response = JsonSerializer.Deserialize<AcoustIdLookupResponse>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(response);
        Assert.Equal("error", response!.Status);
        Assert.Equal("fingerprint not found", response.Error?.Message);
        Assert.Null(response.Results);
    }
}