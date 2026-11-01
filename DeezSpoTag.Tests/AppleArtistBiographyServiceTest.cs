using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AppleArtistBiographyServiceTest
{
    [Fact]
    public async System.Threading.Tasks.Task ExtendedCatalogRefreshBypassesOldPayloadAndKeepsNormalReadsCached()
    {
        using var fixture = new ArtistHttpFixture();
        var first = await fixture.Service.ResolveByArtistIdAsync("1623154745", "4Mr Frank White", default, allowArtistPageScrape:false);
        Assert.Equal("Nairobi", first?.Location?.City);
        Assert.Equal("Real artist biography.", first?.Biography);
        fixture.Handler.Origin = "Mombasa, Kenya";
        var cached = await fixture.Service.ResolveByArtistIdAsync("1623154745", "4Mr Frank White", default, allowArtistPageScrape:false);
        Assert.Equal("Nairobi", cached?.Location?.City);
        var fresh = await fixture.Service.ResolveByArtistIdAsync("1623154745", "4Mr Frank White", default,
            allowArtistPageScrape:false, propagatePageErrors:true);
        Assert.Equal("Mombasa", fresh?.Location?.City);
        Assert.Equal(2, fixture.Handler.Calls);
        Assert.Contains("extend=artistBio,origin,bornOrFormed,isGroup", fixture.Handler.LastQuery);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async System.Threading.Tasks.Task AppleRefreshPreservesProviderErrors(int status)
    {
        using var fixture = new ArtistHttpFixture();
        fixture.Handler.Status = status;
        var error = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => fixture.Service.ResolveByArtistIdAsync(
            "1623154745", "4Mr Frank White", default, allowArtistPageScrape:false, propagatePageErrors:true));
        Assert.Equal((System.Net.HttpStatusCode)status, error.StatusCode);
    }

    internal sealed class ArtistHttpFixture : System.IDisposable
    {
        private readonly string root = System.IO.Directory.CreateTempSubdirectory("apple-origin-http-").FullName;
        private readonly Microsoft.Extensions.Caching.Memory.MemoryCache cache = new(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        public string Root => root;
        public readonly ArtistHandler Handler = new();
        public readonly AppleArtistBiographyService Service;
        public System.Net.Http.IHttpClientFactory Factory { get; }
        public ArtistHttpFixture()
        {
            var settings = new DeezSpoTag.Services.Settings.DeezSpoTagSettingsService(Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>.Instance);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(DeezSpoTag.Services.Settings.DeezSpoTagSettingsService).GetField("_configFolder",flags)!.SetValue(settings,root);
            typeof(DeezSpoTag.Services.Settings.DeezSpoTagSettingsService).GetField("_settingsFilePath",flags)!.SetValue(settings,System.IO.Path.Combine(root,"config.json"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(root,"config.json"),"{\"appleMusic\":{\"storefront\":\"us\"}}");
            var factory = new ArtistFactory(Handler);
            Factory = factory;
            var catalog = new DeezSpoTag.Services.Apple.AppleMusicCatalogService(factory,settings,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Services.Apple.AppleMusicCatalogService>.Instance,cache);
            catalog.GetType().GetField("_cachedToken",flags)!.SetValue(catalog,"fixture-token");
            catalog.GetType().GetField("_cachedTokenExpiresAt",flags)!.SetValue(catalog,System.DateTimeOffset.UtcNow.AddHours(1));
            Service = new AppleArtistBiographyService(catalog,settings,factory,Microsoft.Extensions.Logging.Abstractions.NullLogger<AppleArtistBiographyService>.Instance);
        }
        public void Dispose() { cache.Dispose(); Handler.Dispose(); System.IO.Directory.Delete(root,true); }
    }

    internal sealed class ArtistHandler : System.Net.Http.HttpMessageHandler
    {
        public int Calls;
        public int Status = 200;
        public string Origin = "Nairobi, Kenya";
        public string LastQuery = "";
        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken token)
        {
            Calls++; LastQuery = request.RequestUri!.Query;
            var content = System.Text.Json.JsonSerializer.Serialize(new { data = new[] { new { id = "1623154745", attributes = new { name = "4Mr Frank White", origin = Origin, artistBio = "Real artist biography." } } } });
            return System.Threading.Tasks.Task.FromResult(new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)Status)
                { Content = new System.Net.Http.StringContent(content) });
        }
    }
    private sealed class ArtistFactory(ArtistHandler handler) : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new(handler, disposeHandler:false);
    }

    [Theory]
    [InlineData("Nairobi, Kenya", "Nairobi", "Kenya", "KE", null)]
    [InlineData("Atlanta, Georgia, United States of America", "Atlanta", "United States of America", "US", "Georgia")]
    public void AppleOriginIsExplicitLocationBoundToTheRequestedArtist(string origin, string city, string country, string code, string? region)
    {
        using var item = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new
        { id = "1623154745", attributes = new { name = "4Mr Frank White", origin } }));
        var method = typeof(AppleArtistBiographyService).GetMethod("ResolveAppleArtistLocation", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = (DeezSpoTag.Web.Services.ArtistLocation.ArtistLocationResult?)method!.Invoke(null, new object[] { item.RootElement, "1623154745" });
        Assert.Equal(city, result?.City);
        Assert.Equal(country, result?.Country);
        Assert.Equal(code, result?.CountryCode);
        Assert.Equal(region, result?.Region);
        Assert.Equal("apple", result?.Source);
        Assert.Equal("artist-origin", result?.LocationMeaning);
        Assert.Null(result?.Hometown); // Origin is not automatically birthplace or hometown.
        Assert.Null(method.Invoke(null, new object[] { item.RootElement, "894820464" }));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":\"1623154745\",\"attributes\":{\"artistBio\":\"Born in Nairobi, Kenya\"}}")]
    [InlineData("{\"id\":\"1623154745\",\"attributes\":{\"origin\":null}}")]
    public void AppleLocationDoesNotGuessFromBiographyOrMalformedData(string json)
    {
        using var item = System.Text.Json.JsonDocument.Parse(json);
        var method = typeof(AppleArtistBiographyService).GetMethod("ResolveAppleArtistLocation", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.Null(method!.Invoke(null, new object[] { item.RootElement, "1623154745" }));
    }

    [Fact]
    public void ResolveAppleArtistPageBiography_RejectsAppleMarketingDescription()
    {
        const string html = """
            <html><head>
            <script type="application/ld+json">
            {
              "@type": "MusicGroup",
              "name": "Alicios",
              "url": "https://music.apple.com/us/artist/alicios/12345",
              "description": "Listen to music by Alicios on Apple Music. Find top songs and albums by Alicios, including Mpita Njia (feat. Juliana Kanyomozi), Posa Ya Bolingo and more."
            }
            </script>
            </head><body></body></html>
            """;

        var biography = AppleArtistBiographyService.ResolveAppleArtistPageBiography(html, "12345", "Alicios");

        Assert.Null(biography);
    }

    [Fact]
    public void ResolveAppleArtistPageBiography_ReturnsRealMusicGroupDescription()
    {
        const string html = """
            <html><head>
            <script type="application/ld+json">
            {
              "@type": "MusicGroup",
              "name": "Alicios",
              "url": "https://music.apple.com/us/artist/alicios/12345",
              "description": "Alicios is a Congolese singer known for melodic Afro-pop collaborations and Lingala-driven ballads."
            }
            </script>
            </head><body></body></html>
            """;

        var biography = AppleArtistBiographyService.ResolveAppleArtistPageBiography(html, "12345", "Alicios");

        Assert.Equal("Alicios is a Congolese singer known for melodic Afro-pop collaborations and Lingala-driven ballads.", biography);
    }
}
