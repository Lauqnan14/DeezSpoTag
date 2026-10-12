using System;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyArtistNameMatchingGuardrailTest
{
    [Theory]
    [InlineData("Album 119", 1)]
    [InlineData("Unrelated Album", 0)]
    public async System.Threading.Tasks.Task CandidateAlbumEvidenceDoesNotFetchEveryAlbumsTracks(string localTitle, int expectedOverlap)
    {
        using var handler = new DiscographyHandler();
        var client = new SpotifyPathfinderMetadataClient(null!, null!, null!, new BackgroundUser(),
            new DiscographyHttpFactory(handler), Microsoft.Extensions.Logging.Abstractions.NullLogger<SpotifyPathfinderMetadataClient>.Instance);
        var clientType = typeof(SpotifyPathfinderMetadataClient);
        const string blobPath = "test-weekly-artist-evidence";
        clientType.GetField("_backgroundUserBlobCachedPath", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, blobPath);
        clientType.GetField("_backgroundUserBlobCheckedAt", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, DateTimeOffset.UtcNow);
        var contextType = clientType.GetNestedType("PathfinderAuthContext", BindingFlags.NonPublic)!;
        var context = Activator.CreateInstance(contextType, ["test-access", "test-client", "test-version", "test-device"]);
        var stateType = clientType.GetNestedType("BlobAuthState", BindingFlags.NonPublic)!;
        var state = Activator.CreateInstance(stateType, nonPublic: true)!;
        stateType.GetProperty("Context")!.SetValue(state, context);
        stateType.GetProperty("AccessTokenExpiresAt")!.SetValue(state, DateTimeOffset.UtcNow.AddHours(1));
        stateType.GetProperty("ClientTokenExpiresAt")!.SetValue(state, DateTimeOffset.UtcNow.AddHours(1));
        var states = (System.Collections.IDictionary)clientType.GetField("_blobAuthStates", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
        states.Add(blobPath, state);
        var service = (SpotifyArtistService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SpotifyArtistService));
        typeof(SpotifyArtistService).GetField("_pathfinderMetadataClient", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, client);
        var method = typeof(SpotifyArtistService).GetMethod("ResolveLocalAlbumOverlapAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var overlap = await (System.Threading.Tasks.Task<int>)method.Invoke(service,
            ["0000000000000000000001", true, new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { localTitle }, System.Threading.CancellationToken.None])!;

        Assert.Equal(expectedOverlap, overlap);
        Assert.Equal(3, handler.Operations.Count);
        Assert.All(handler.Operations, operation => Assert.Equal("queryArtistDiscographyAll", operation));
    }

    private sealed class BackgroundUser : ISpotifyUserContextAccessor
    {
        public string? UserId => null;
    }

    private sealed class DiscographyHttpFactory(DiscographyHandler handler) : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DiscographyHandler : System.Net.Http.HttpMessageHandler
    {
        public readonly System.Collections.Generic.List<string> Operations = [];

        protected override async System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            using var payload = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var operation = payload.RootElement.GetProperty("operationName").GetString()!;
            Operations.Add(operation);
            object artist = new { profile = new { name = "Library Artist" } };
            object response;
            if (operation == "queryArtistDiscographyAll")
            {
                var offset = payload.RootElement.GetProperty("variables").GetProperty("offset").GetInt32();
                var items = System.Linq.Enumerable.Range(offset, Math.Min(50, 120 - offset))
                    .Select(index => new { uri = "spotify:album:" + index.ToString("D22"), name = "Album " + index, type = "ALBUM", tracks = new { totalCount = 15 } }).ToArray();
                response = new { data = new { artistUnion = new { discography = new { all = new { totalCount = 120, items } } } } };
            }
            else if (operation == "getAlbum")
                response = new { data = new { albumUnion = new { tracks = new { totalCount = 0, items = Array.Empty<object>() } } } };
            else response = new { data = new { artistUnion = artist } };
            return new(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(response)) };
        }
    }

    private static readonly MethodInfo EquivalentMethod =
        typeof(SpotifyArtistService).GetMethod(
            "IsEquivalentArtistName",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not resolve SpotifyArtistService.IsEquivalentArtistName.");

    [Theory]
    [InlineData("2 pac", "2pac")]
    [InlineData("Jay-Z", "Jay Z")]
    [InlineData("Soulja Boy Tell 'Em", "Soulja Boy")]
    [InlineData("Marvin Gaye", "marvin gaye")]
    [InlineData("ROMANS", "RØMANS")]
    public void EquivalentArtistNames_AreRecognized(string left, string right)
    {
        var equivalent = InvokeEquivalent(left, right);
        Assert.True(equivalent);
    }

    [Theory]
    [InlineData("2 pac", "2 chainz")]
    [InlineData("Bob Marley", "Bob Dylan")]
    [InlineData("Adele", "Alicia Keys")]
    public void DifferentArtistNames_AreNotMatched(string left, string right)
    {
        var equivalent = InvokeEquivalent(left, right);
        Assert.False(equivalent);
    }

    [Fact]
    public void ExactNameMatch_DoesNotUseCanonicalFallbackWhenLocalAlbumsExist()
    {
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(
            FindSourceRoot(),
            "DeezSpoTag.Web",
            "Services",
            "SpotifyArtistService.cs"));
        var methodStart = source.IndexOf("private async Task<string?> TrySelectExactCandidateArtistIdAsync", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("private async Task<string?> ResolveArtistIdWithShazamFallbackAsync", methodStart, StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains("if (localAlbumTitleSet.Count > 0)", method, StringComparison.Ordinal);
        Assert.Contains("skipped canonical name fallback", method, StringComparison.Ordinal);
        Assert.True(
            method.IndexOf("if (localAlbumTitleSet.Count > 0)", StringComparison.Ordinal)
            < method.IndexOf("TrySelectCanonicalFallbackExactCandidateAsync", StringComparison.Ordinal));
    }

    private static string FindSourceRoot()
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "DeezSpoTag.Web", "Services", "SpotifyArtistService.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate source root.");
    }

    private static bool InvokeEquivalent(string candidate, string target)
    {
        var value = EquivalentMethod.Invoke(null, [candidate, target]);
        return value is true;
    }
}
