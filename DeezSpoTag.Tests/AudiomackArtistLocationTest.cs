using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Controllers;
using DeezSpoTag.Web.Services.Audiomack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AudiomackArtistLocationTest
{
    public sealed class Normalizer
    {
        [Theory]
        [InlineData("Nairobi, Kenya", "Nairobi", "Kenya", "KE")]
        [InlineData("Lagos, Nigeria", "Lagos", "Nigeria", "NG")]
        [InlineData("Accra, Ghana", "Accra", "Ghana", "GH")]
        [InlineData("London, United Kingdom", "London", "United Kingdom", "GB")]
        [InlineData("New York, USA", "New York", "USA", "US")]
        [InlineData("Johannesburg, South Africa", "Johannesburg", "South Africa", "ZA")]
        public void Normalize_CityAndCountry_ParsesBoth(string raw, string city, string country, string code)
        {
            var result = AudiomackLocationNormalizer.Normalize(raw);

            Assert.NotNull(result);
            Assert.Equal(raw, result!.RawLocation);
            Assert.Equal(city, result.City);
            Assert.Equal(country, result.Country);
            Assert.Equal(code, result.CountryCode);
        }

        [Theory]
        [InlineData("Ghana", "GH")]
        [InlineData("Kenya", "KE")]
        [InlineData("Nigeria", "NG")]
        [InlineData("germany", "DE")]
        public void Normalize_CountryOnly_SetsCountryWithoutCity(string raw, string code)
        {
            var result = AudiomackLocationNormalizer.Normalize(raw);

            Assert.NotNull(result);
            Assert.Null(result!.City);
            Assert.Equal(raw, result.Country);
            Assert.Equal(code, result.CountryCode);
        }

        [Theory]
        [InlineData("Thika")]
        [InlineData("KONONGO")]
        [InlineData("Atlanta, TX")]
        public void Normalize_UnknownCountry_KeepsCityLevelTextWithoutInventingCountry(string raw)
        {
            var result = AudiomackLocationNormalizer.Normalize(raw);

            Assert.NotNull(result);
            Assert.Equal(raw, result.City);
            Assert.Null(result.Country);
            Assert.Null(result.CountryCode);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(", ,")]
        public void Normalize_Empty_ReturnsNull(string? raw)
        {
            Assert.Null(AudiomackLocationNormalizer.Normalize(raw));
        }

        [Fact]
        public void Normalize_TrimsWhitespaceAroundParts()
        {
            var result = AudiomackLocationNormalizer.Normalize("  Nairobi , Kenya  ");

            Assert.NotNull(result);
            Assert.Equal("Nairobi", result!.City);
            Assert.Equal("Kenya", result.Country);
            Assert.Equal("KE", result.CountryCode);
        }
    }

    public sealed class Parser
    {
        // Mirrors the real flight-payload structure: each artist object sits in its
        // own balanced JSON object inside an escaped push string.
        private const string FlightPageHtml = """
            <html lang="en"><head><meta charSet="utf-8"/></head><body>
            <script>self.__next_f.push([1,"3:[\"$\",\"div\",null,{\"className\":\"ArtistPage-content\",\"children\":[[\"$\",\"$L4b\",null,{\"artist\":{\"id\":11,\"name\":\"Sista Ashlee\",\"verified\":\"authenticated\",\"hometown\":\"Paris, France\",\"image_base\":\"https://i.audiomack.com/sista-ashlee/x.webp\",\"location\":null,\"url_slug\":\"sista-ashlee\",\"type\":\"artist\"}}]]}"])</script>
            <script>self.__next_f.push([1,"9:[\"$\",\"div\",null,{\"artist\":{\"id\":22,\"name\":\"Still Shadey\",\"verified\":\"authenticated\",\"hometown\":\"Accra, Ghana\",\"image_base\":\"https://i.audiomack.com/still-shadey/y.webp\",\"location\":null,\"url_slug\":\"still-shadey\",\"type\":\"artist\"}}"])</script>
            </body></html>
            """;

        [Fact]
        public void TryExtractRawLocation_UsesArtistObjectMatchingRequestedSlugAndName()
        {
            var raw = AudiomackArtistPageParser.TryExtractRawLocation(FlightPageHtml, "still-shadey", "Still Shadey");

            Assert.Equal("Accra, Ghana", raw);
        }

        [Fact]
        public void TryExtractArtistPageInfo_ReturnsCanonicalSlugAlongsideLocation()
        {
            var info = AudiomackArtistPageParser.TryExtractArtistPageInfo(FlightPageHtml, "still-shadey", "Still Shadey");

            Assert.NotNull(info);
            Assert.Equal("still-shadey", info!.CanonicalUrlSlug);
            Assert.Equal("Accra, Ghana", info.RawLocation);
        }

        [Fact]
        public void TryExtractRawLocation_NeverMixesFieldsAcrossArtistObjects()
        {
            // Real-world regression: still-shadey's own object has an empty hometown,
            // while a related artist further down the page (IMSOTUMELO) carries
            // "Johannesburg, Gauteng, South Africa". The old window-based parser
            // paired Still Shadey's name with the neighbour's hometown.
            const string html = """
                <script>self.__next_f.push([1,"9:[\"$\",\"div\",null,{\"artist\":{\"id\":22,\"name\":\"Still Shadey\",\"hometown\":\"\",\"location\":null,\"url_slug\":\"still-shadey\",\"type\":\"artist\"}}"])</script>
                <script>self.__next_f.push([1,"12:[\"$\",\"$L4c\",null,{\"related\":[{\"id\":77,\"name\":\"IMSOTUMELO\",\"hometown\":\"Johannesburg, Gauteng, South Africa\",\"url_slug\":\"imsotumelo\",\"type\":\"artist\"}]}"])</script>
                """;

            Assert.Null(AudiomackArtistPageParser.TryExtractRawLocation(html, "still-shadey", "Still Shadey"));
            Assert.Equal(
                "Johannesburg, Gauteng, South Africa",
                AudiomackArtistPageParser.TryExtractRawLocation(html, "imsotumelo", "IMSOTUMELO"));
        }

        [Fact]
        public void TryExtractRawLocation_RecycledSlugWithDifferentArtist_ReturnsNull()
        {
            // Real-world case: audiomack.com/khaligraph-jones currently serves an
            // object whose url_slug matches but whose name is "Kay The Magician".
            const string html = """
                <script>self.__next_f.push([1,"8:[\"$\",\"div\",null,{\"className\":\"ArtistPage-content\",\"children\":[[\"$\",\"$L4b\",null,{\"artist\":{\"id\":3096221,\"name\":\"Kay The Magician\",\"hometown\":\"Thika\",\"bio\":\"\",\"url_slug\":\"khaligraph-jones\",\"type\":\"artist\"}"])]</script>
                """;

            Assert.Null(AudiomackArtistPageParser.TryExtractRawLocation(html, "khaligraph-jones", "Khaligraph Jones"));
        }

        [Fact]
        public void TryExtractRawLocation_MatchingArtistWithoutHometown_ReturnsNull()
        {
            const string html = """
                <script>self.__next_f.push([1,"3:[\"$\",\"div\",null,{\"artist\":{\"name\":\"Ghost\",\"hometown\":\"\",\"location\":null,\"url_slug\":\"ghost-artist\",\"type\":\"artist\"}}"])</script>
                """;

            Assert.Null(AudiomackArtistPageParser.TryExtractRawLocation(html, "ghost-artist", "Ghost"));
        }

        [Fact]
        public void TryExtractRawLocation_EmptyHometownFallsBackToLocationField()
        {
            const string html = """
                <script>self.__next_f.push([1,"3:[\"$\",\"div\",null,{\"artist\":{\"name\":\"Test\",\"hometown\":\"\",\"location\":\"Nairobi, Kenya\",\"url_slug\":\"test-artist\",\"type\":\"artist\"}}"])</script>
                """;

            Assert.Equal("Nairobi, Kenya", AudiomackArtistPageParser.TryExtractRawLocation(html, "test-artist", "Test"));
        }

        [Fact]
        public void TryExtractRawLocation_StructuredLocationObjectDisplayUsedAsFallback()
        {
            // Real-world shape: Black Sherif's profile carries hometown "KONONGO"
            // plus a structured location {"tag":"...","display":"Accra, Ghana"}.
            const string html = """
                <script>self.__next_f.push([1,"3:[\"$\",\"div\",null,{\"artist\":{\"name\":\"Test\",\"hometown\":\"\",\"location\":{\"tag\":\"ghanagreateraccraaccra\",\"display\":\"Accra, Ghana\"},\"url_slug\":\"test-artist\",\"type\":\"artist\"}}"])</script>
                """;

            Assert.Equal("Accra, Ghana", AudiomackArtistPageParser.TryExtractRawLocation(html, "test-artist", "Test"));
        }

        [Fact]
        public void TryExtractRawLocation_HometownTakesPriorityOverStructuredLocation()
        {
            const string html = """
                <script>self.__next_f.push([1,"3:[\"$\",\"div\",null,{\"artist\":{\"name\":\"Test\",\"hometown\":\"KONONGO\",\"location\":{\"tag\":\"ghanagreateraccraaccra\",\"display\":\"Accra, Ghana\"},\"url_slug\":\"test-artist\",\"type\":\"artist\"}}"])</script>
                """;

            Assert.Equal("KONONGO", AudiomackArtistPageParser.TryExtractRawLocation(html, "test-artist", "Test"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("<html><body>no data here</body></html>")]
        public void TryExtractRawLocation_MissingOrUnknownSlug_ReturnsNull(string? html)
        {
            Assert.Null(AudiomackArtistPageParser.TryExtractRawLocation(html, "still-shadey", "Still Shadey"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void TryExtractRawLocation_MissingExpectedArtistName_ReturnsNull(string? expectedArtistName)
        {
            Assert.Null(AudiomackArtistPageParser.TryExtractRawLocation(FlightPageHtml, "still-shadey", expectedArtistName));
        }

        // ---- Biography (bio) extraction -------------------------------------------------

        private const string BioArtistHtml = """
            <script>self.__next_f.push([1,"3:[\"$\",\"div\",null,{\"artist\":{\"id\":7,\"name\":\"Bio Artist\",\"hometown\":\"\",\"bio\":\"A real profile biography.\",\"url_slug\":\"bio-artist\",\"type\":\"artist\"}}]"])</script>
            """;

        [Fact]
        public void TryExtractRawBiography_ReturnsBioOfTheMatchedArtistObject()
        {
            Assert.Equal(
                "A real profile biography.",
                AudiomackArtistPageParser.TryExtractRawBiography(BioArtistHtml, "bio-artist", "Bio Artist"));
        }

        [Fact]
        public void TryExtractArtistPageInfo_BiographyWithoutLocation_StillReturnsInfo()
        {
            var info = AudiomackArtistPageParser.TryExtractArtistPageInfo(BioArtistHtml, "bio-artist", "Bio Artist");

            Assert.NotNull(info);
            Assert.Equal("bio-artist", info!.CanonicalUrlSlug);
            Assert.Null(info.RawLocation);
            Assert.Equal("A real profile biography.", info.RawBiography);
        }

        [Fact]
        public void TryExtractRawBiography_LocationWithoutBiography_ReturnsNullBio()
        {
            var info = AudiomackArtistPageParser.TryExtractArtistPageInfo(FlightPageHtml, "still-shadey", "Still Shadey");

            Assert.NotNull(info);
            Assert.Equal("Accra, Ghana", info!.RawLocation);
            Assert.Null(info.RawBiography);
            Assert.Null(AudiomackArtistPageParser.TryExtractRawBiography(FlightPageHtml, "still-shadey", "Still Shadey"));
        }

        [Fact]
        public void TryExtractRawBiography_EmptyBio_ReturnsNull()
        {
            const string html = """
                <script>self.__next_f.push([1,"3:[\"$\",\"div\",null,{\"artist\":{\"name\":\"No Bio\",\"hometown\":\"Nairobi, Kenya\",\"bio\":\"   \",\"url_slug\":\"no-bio\",\"type\":\"artist\"}}]"])</script>
                """;

            var info = AudiomackArtistPageParser.TryExtractArtistPageInfo(html, "no-bio", "No Bio");

            Assert.NotNull(info);
            Assert.Equal("Nairobi, Kenya", info!.RawLocation);
            Assert.Null(info.RawBiography);
        }

        [Fact]
        public void TryExtractRawBiography_NeverCrossesArtistObjects()
        {
            // A matching artist with no bio must never inherit a neighbour's bio,
            // exactly like the location cross-object regression.
            const string html = """
                <script>self.__next_f.push([1,"9:[\"$\",\"div\",null,{\"artist\":{\"name\":\"Quiet Artist\",\"hometown\":\"Lagos, Nigeria\",\"bio\":\"\",\"url_slug\":\"quiet-artist\",\"type\":\"artist\"}}"])</script>
                <script>self.__next_f.push([1,"12:[\"$\",\"$L4c\",null,{\"related\":[{\"name\":\"Loud Artist\",\"bio\":\"Someone else's biography.\",\"url_slug\":\"loud-artist\",\"type\":\"artist\"}]}"])</script>
                """;

            Assert.Null(AudiomackArtistPageParser.TryExtractRawBiography(html, "quiet-artist", "Quiet Artist"));
            Assert.Equal("Someone else's biography.", AudiomackArtistPageParser.TryExtractRawBiography(html, "loud-artist", "Loud Artist"));
        }

        [Fact]
        public void TryExtractRawBiography_RecycledSlugWithDifferentArtist_ReturnsNull()
        {
            const string html = """
                <script>self.__next_f.push([1,"8:[\"$\",\"div\",null,{\"artist\":{\"name\":\"Kay The Magician\",\"hometown\":\"Thika\",\"bio\":\"Wrong artist bio.\",\"url_slug\":\"khaligraph-jones\",\"type\":\"artist\"}}]"])</script>
                """;

            Assert.Null(AudiomackArtistPageParser.TryExtractRawBiography(html, "khaligraph-jones", "Khaligraph Jones"));
        }

        [Fact]
        public void TryExtractArtistPageInfo_RealArtistPageFixture_ReturnsLocationAndBiography()
        {
            // Fixture derived verbatim from the captured public payload: the artist
            // object carries both hometown/location and a creator-written bio.
            var html = ReadFixture("artist-page-flight.txt");

            var info = AudiomackArtistPageParser.TryExtractArtistPageInfo(html, "alikiba", "Alikiba");

            Assert.NotNull(info);
            Assert.Equal("alikiba", info!.CanonicalUrlSlug);
            Assert.Equal("Dar es Salaam,Tanzania", info.RawLocation);
            Assert.NotNull(info.RawBiography);
            Assert.Contains("Ally Saleh Kiba", info.RawBiography);
            Assert.Contains("Tanzanian recording artiste", info.RawBiography);
        }

        [Fact]
        public void Normalize_RealFixtureBiography_IsPlainTextAfterSanitizing()
        {
            // The raw bio is HTML-free but carries runs of text; the shared sanitizer
            // is the single cleaner used by the biography cache pipeline.
            var html = ReadFixture("artist-page-flight.txt");
            var raw = AudiomackArtistPageParser.TryExtractRawBiography(html, "alikiba", "Alikiba");

            var clean = DeezSpoTag.Web.Services.ArtistBiographySanitizer.Clean(raw);

            Assert.NotNull(clean);
            Assert.DoesNotContain("<", clean);
            Assert.StartsWith("Ally Saleh Kiba", clean);
        }

        private static string ReadFixture(string name)
        {
            var directory = Directory.GetCurrentDirectory();
            while (!string.IsNullOrWhiteSpace(directory))
            {
                var candidate = Path.Join(directory, "DeezSpoTag.Tests", "Fixtures", "Audiomack", name);
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }

                directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
            }

            throw new FileNotFoundException($"Audiomack fixture '{name}' was not found.");
        }
    }

    public sealed class SlugBuilder
    {
        [Theory]
        [InlineData("Still Shadey", "still-shadey")]
        [InlineData("Khaligraph Jones", "khaligraph-jones")]
        [InlineData("A-Reece", "a-reece")]
        [InlineData("50 Cent", "50-cent")]
        [InlineData("Sigag   Lauren!", "sigag-lauren")]
        [InlineData("  Black Sherif  ", "black-sherif")]
        public void BuildUrlSlug_SlugifiesArtistNames(string artistName, string expected)
        {
            Assert.Equal(expected, AudiomackArtistLocationService.BuildUrlSlug(artistName));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("!!!")]
        public void BuildUrlSlug_EmptyOrUnusableNames_ReturnsNull(string? artistName)
        {
            Assert.Null(AudiomackArtistLocationService.BuildUrlSlug(artistName));
        }
    }

    /// <summary>
    /// Tests for the anonymous search-API client: response parsing, name
    /// matching, signed-URL structure and the web-identity extraction used to
    /// avoid embedding Audiomack's signing secret as a permanent constant.
    /// </summary>
    public sealed class AudiomackSearchApi
    {
        private const string SearchResponseJson = """
            {"verified_artist":{"id":16579133,"name":"Alikiba","url_slug":"alikiba"},
             "results":[
               {"id":1,"uploader":{"id":"16579133","name":"Alikiba","url_slug":"alikiba"}},
               {"id":2,"uploader":{"id":"50212214","name":"Bien","url_slug":"bien"}}
             ]}
            """;

        [Fact]
        public void ParseSearchResponse_VerifiedArtistMatch_ReturnsVerifiedCandidate()
        {
            var candidate = AudiomackApiClient.ParseSearchResponse(SearchResponseJson, "Alikiba");

            Assert.NotNull(candidate);
            Assert.True(candidate!.Verified);
            Assert.Equal("alikiba", candidate.UrlSlug);
            Assert.Equal(16579133, candidate.Id);
        }

        [Fact]
        public void ParseSearchResponse_NoVerifiedBlock_FallsBackToMatchingUploader()
        {
            const string json = """
                {"results":[{"id":1,"uploader":{"id":"99","name":"Still Shadey","url_slug":"still-shadey"}}]}
                """;

            var candidate = AudiomackApiClient.ParseSearchResponse(json, "Still Shadey");

            Assert.NotNull(candidate);
            Assert.False(candidate!.Verified);
            Assert.Equal("still-shadey", candidate.UrlSlug);
        }

        [Theory]
        [InlineData("Kay The Magician")]
        [InlineData("Ghost")]
        public void ParseSearchResponse_NameMismatch_ReturnsNull(string expected)
        {
            Assert.Null(AudiomackApiClient.ParseSearchResponse(SearchResponseJson, expected));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        public void ParseSearchResponse_InvalidPayload_ReturnsNull(string? json)
        {
            Assert.Null(AudiomackApiClient.ParseSearchResponse(json, "Alikiba"));
        }

        [Theory]
        [InlineData("Alikiba", "alikiba", true)]
        [InlineData("ALIKIBA", "Alikiba", true)]
        [InlineData("Still Shadey", "Still   Shadey!", true)]
        [InlineData("Alikiba Jr", "alikiba", true)]
        [InlineData("Bien", "Alikiba", false)]
        [InlineData("Al", "alikiba", false)]
        public void NameMatches_NormalizesAndRequiresConfidence(string candidate, string expected, bool shouldMatch)
        {
            Assert.Equal(shouldMatch, AudiomackApiClient.NameMatches(candidate, expected));
        }

        [Fact]
        public void BuildSignedUrl_ContainsOAuthParametersAndSortedSignatureBase()
        {
            var credentials = new AudiomackWebCredentials("https://api.audiomack.com/v1/", "audiomack-web", "secret");
            var url = AudiomackApiClient.BuildSignedUrl(credentials, "search", new Dictionary<string, string>
            {
                ["q"] = "still shadey",
                ["type"] = "artists"
            });

            Assert.StartsWith("https://api.audiomack.com/v1/search?", url);
            Assert.Contains("q=still%20shadey", url, StringComparison.Ordinal);
            Assert.Contains("type=artists", url, StringComparison.Ordinal);
            Assert.Contains("oauth_consumer_key=audiomack-web", url, StringComparison.Ordinal);
            Assert.Contains("oauth_signature_method=HMAC-SHA1", url, StringComparison.Ordinal);
            Assert.Contains("oauth_version=1.0", url, StringComparison.Ordinal);
            Assert.Contains("oauth_signature=", url, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", url.Replace("oauth_signature=", string.Empty), StringComparison.Ordinal);
        }

        [Fact]
        public void TryExtractCredentials_ReadsIdentityFromWebBundleSnippet()
        {
            const string bundleSnippet = """
                let o={APP_URL:"https://audiomack.com/",API_PUBLIC_API_URL:"https://api.audiomack.com/v1",API_CONSUMER_SECRET:"bd8a07e9f23fbe9d808646b730f89b8e",API_CONSUMER_KEY:"audiomack-web"};
                """;

            var credentials = AudiomackWebCredentialsProvider.TryExtractCredentials(bundleSnippet);

            Assert.NotNull(credentials);
            Assert.Equal("audiomack-web", credentials!.ConsumerKey);
            Assert.Equal("bd8a07e9f23fbe9d808646b730f89b8e", credentials.ConsumerSecret);
            Assert.Equal("https://api.audiomack.com/v1", credentials.ApiBaseUrl);
        }

        [Fact]
        public void TryExtractCredentials_WithoutSecret_ReturnsNull()
        {
            Assert.Null(AudiomackWebCredentialsProvider.TryExtractCredentials("let o={APP_URL:\"https://audiomack.com/\"};"));
        }
    }

    public sealed class AudiomackIdNormalizerTest
    {
        [Theory]
        [InlineData("alikiba", "alikiba")]
        [InlineData("Alikiba", "alikiba")]
        [InlineData("https://audiomack.com/alikiba", "alikiba")]
        [InlineData("https://audiomack.com/alikiba/", "alikiba")]
        [InlineData("http://audiomack.com/Still-Shadey", "still-shadey")]
        [InlineData("/khaligraph-jones", "khaligraph-jones")]
        [InlineData("  a-reece  ", "a-reece")]
        public void Normalize_AcceptsSlugsAndProfileUrls(string input, string expected)
        {
            Assert.Equal(expected, AudiomackIdNormalizer.Normalize(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("https://open.spotify.com/artist/1ZwdS5xdxEREPySFridCfh")]
        [InlineData("not a slug!!")]
        [InlineData("-leading-dash")]
        [InlineData("double--dash")]
        public void Normalize_RejectsNonSlugs(string? input)
        {
            Assert.Null(AudiomackIdNormalizer.Normalize(input));
        }
    }

    /// <summary>Round-trip tests for the DB-backed manual location override store.</summary>
    public sealed class ArtistLocationOverrideStoreTest
    {
        private static (DeezSpoTag.Services.Library.ArtistLocationOverrideStore Store, string DbPath) CreateStore()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"loc-override-{Guid.NewGuid():N}.db");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Library"] = $"Data Source={dbPath}"
                })
                .Build();
            var store = new DeezSpoTag.Services.Library.ArtistLocationOverrideStore(
                configuration,
                NullLogger<DeezSpoTag.Services.Library.ArtistLocationOverrideStore>.Instance);
            return (store, dbPath);
        }

        [Fact]
        public async Task GetAsync_MissingArtist_ReturnsNull()
        {
            var (store, _) = CreateStore();

            Assert.Null(await store.GetAsync(1234));
        }

        [Fact]
        public async Task SetAndGet_RoundTripsOverride()
        {
            var (store, _) = CreateStore();

            await store.SetAsync(559, "Atlanta", "USA", "US");
            var overrideValue = await store.GetAsync(559);

            Assert.NotNull(overrideValue);
            Assert.Equal("Atlanta", overrideValue!.City);
            Assert.Equal("USA", overrideValue.Country);
            Assert.Equal("US", overrideValue.CountryCode);
        }

        [Fact]
        public async Task SetAsync_EmptyFields_RemovesOverride()
        {
            var (store, dbPath) = CreateStore();

            await store.SetAsync(559, "Atlanta", "USA", "US");
            await store.SetAsync(559, null, null, null);

            Assert.Null(await store.GetAsync(559));
            // The row must be gone from the DB, not just hidden.
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM artist_location_override WHERE artist_id = 559;";
            Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
        }

        [Fact]
        public async Task Store_IsolatesArtists()
        {
            var (store, _) = CreateStore();

            await store.SetAsync(1, "Lagos", "Nigeria", "NG");
            await store.SetAsync(2, "Accra", "Ghana", "GH");

            Assert.Equal("Lagos", (await store.GetAsync(1))!.City);
            Assert.Equal("Accra", (await store.GetAsync(2))!.City);
        }

        [Fact]
        public async Task Values_PersistAcrossStoreInstances()
        {
            var (store, dbPath) = CreateStore();
            await store.SetAsync(649, "Dar es Salaam", "Tanzania", "TZ");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Library"] = $"Data Source={dbPath}"
                })
                .Build();
            var secondStore = new DeezSpoTag.Services.Library.ArtistLocationOverrideStore(
                configuration,
                NullLogger<DeezSpoTag.Services.Library.ArtistLocationOverrideStore>.Instance);

            Assert.Equal("Dar es Salaam", (await secondStore.GetAsync(649))!.City);
        }
    }

    /// <summary>
    /// Regression tests for the /api/artist-page cached-payload location attach:
    /// the guard used to treat a successful artist-name read as a failure and
    /// returned the payload untouched, so cache hits never carried a location.
    /// The controller method is private and the controller constructor is heavy,
    /// so the instance is created without running its constructor and only the
    /// fields the method touches are populated (repo convention: reflection).
    /// </summary>
    public sealed class ApiControllerCachedPayloadAttach
    {
        private const string FlightPageHtml = """
            <script>self.__next_f.push([1,"3:[\"$\",\"div\",null,{\"artist\":{\"name\":\"Alikiba\",\"hometown\":\"Dar es Salaam,Tanzania\",\"location\":null,\"url_slug\":\"alikiba\",\"type\":\"artist\"}}"])</script>
            """;

        [Fact]
        public async Task CachedPayloadWithoutLocation_GetsLocationAttached()
        {
            var controller = CreateController();
            const string payloadJson = """{"name":"Alikiba","nb_fan":483100,"picture_big":"https://example.test/a.png"}""";

            var result = await InvokeAttachAsync(controller, payloadJson);

            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            Assert.Equal("Dar es Salaam", root.GetProperty("city").GetString());
            Assert.Equal("Tanzania", root.GetProperty("country").GetString());
            Assert.Equal("TZ", root.GetProperty("country_code").GetString());
            Assert.Equal("audiomack", root.GetProperty("location_source").GetString());
            Assert.Equal("Dar es Salaam,Tanzania", root.GetProperty("raw_location").GetString());
        }

        [Fact]
        public async Task CachedPayloadWithLocationSource_ReturnedUnchanged()
        {
            var controller = CreateController();
            const string payloadJson = """{"name":"Alikiba","city":"Dar es Salaam","country":"Tanzania","country_code":"TZ","location_source":"audiomack"}""";

            var result = await InvokeAttachAsync(controller, payloadJson);

            Assert.Equal(payloadJson, result);
        }

        [Fact]
        public async Task CachedPayloadWithoutArtistName_ReturnedUnchanged()
        {
            var controller = CreateController();
            const string payloadJson = """{"nb_fan":483100}""";

            var result = await InvokeAttachAsync(controller, payloadJson);

            Assert.Equal(payloadJson, result);
        }

        [Fact]
        public async Task CachedPayloadWithUnresolvableArtist_ReturnedUnchanged()
        {
            // The stub page only contains the alikiba object; a name that matches
            // no object on the page must never invent a location.
            var controller = CreateController();
            const string payloadJson = """{"name":"Totally Unknown Artist"}""";

            var result = await InvokeAttachAsync(controller, payloadJson);

            Assert.Equal(payloadJson, result);
        }

        private static object CreateController()
        {
            var controller = RuntimeHelpers.GetUninitializedObject(typeof(ApiController));
            typeof(ApiController)
                .GetField("_audiomackArtistLocation", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(controller, CreateLocationService());
            typeof(ApiController)
                .GetField("_logger", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(controller, NullLogger<ApiController>.Instance);
            return controller;
        }

        private static AudiomackArtistLocationService CreateLocationService()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Library"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"audiomack-location-attach-{Guid.NewGuid():N}.db")}"
                })
                .Build();
            var httpClientFactory = new StubHttpClientFactory();
            return new AudiomackArtistLocationService(
                httpClientFactory,
                new ArtistPageCacheRepository(configuration, NullLogger<ArtistPageCacheRepository>.Instance),
                new AudiomackApiClient(
                    httpClientFactory,
                    new AudiomackWebCredentialsProvider(httpClientFactory, NullLogger<AudiomackWebCredentialsProvider>.Instance),
                    NullLogger<AudiomackApiClient>.Instance),
                NullLogger<AudiomackArtistLocationService>.Instance,
                libraryRepository: null);
        }

        private static async Task<string> InvokeAttachAsync(object controller, string payloadJson)
        {
            var method = typeof(ApiController).GetMethod(
                "TryAttachLocationToCachedPayloadAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task<string>)method.Invoke(controller, [payloadJson, CancellationToken.None])!;
            return await task;
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(FlightPageHtml, Encoding.UTF8, "text/html")
                });
            }
        }

        private sealed class StubHttpClientFactory : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(new StubHandler());
        }
    }

    /// <summary>
    /// Fetch/cache integration for the shared artist-profile path (location + bio from
    /// one match). HTTP is stubbed from the fixture; these tests never hit the network.
    /// </summary>
    public sealed class ArtistProfileService
    {
        [Fact]
        public async Task ResolveProfileAsync_RealFixture_ReturnsLocationAndBiographyFromOneFetch()
        {
            var (service, factory) = CreateService(ReadFixture("artist-page-flight.txt"));

            var profile = await service.ResolveProfileAsync("Alikiba", CancellationToken.None);

            Assert.NotNull(profile);
            Assert.NotNull(profile!.Location);
            Assert.Equal("Dar es Salaam", profile.Location!.City);
            Assert.Equal("Tanzania", profile.Location.Country);
            Assert.Equal("TZ", profile.Location.CountryCode);
            Assert.Equal("Dar es Salaam,Tanzania", profile.Location.RawLocation);
            Assert.NotNull(profile.Biography);
            Assert.StartsWith("Ally Saleh Kiba", profile.Biography);

            // Second read is served from the fetch cache: no extra page request.
            var cached = await service.ResolveProfileAsync("Alikiba", CancellationToken.None);
            Assert.NotNull(cached);
            Assert.Equal("TZ", cached!.Location!.CountryCode);
            Assert.Equal(1, factory.RequestCount);
        }

        [Fact]
        public async Task ResolveBiographyAsync_LibraryArtistOverload_ReturnsBiography()
        {
            var (service, _) = CreateService(ReadFixture("artist-page-flight.txt"));

            var biography = await service.ResolveBiographyAsync(4242, "Alikiba", CancellationToken.None);

            Assert.NotNull(biography);
            Assert.Contains("Tanzanian recording artiste", biography);
        }

        [Fact]
        public async Task ResolveProfileAsync_BioOnlyProfile_ReturnsBiographyWithNullLocation()
        {
            const string html = """
                <script>self.__next_f.push([1,"3:[\"$\",\"div\",null,{\"artist\":{\"name\":\"Bio Artist\",\"bio\":\"Only a bio.\",\"url_slug\":\"bio-artist\",\"type\":\"artist\"}}]"])</script>
                """;
            var (service, _) = CreateService(html);

            var profile = await service.ResolveProfileAsync("Bio Artist", CancellationToken.None);

            Assert.NotNull(profile);
            Assert.Null(profile!.Location);
            Assert.Equal("Only a bio.", profile.Biography);
        }

        [Fact]
        public async Task ResolveProfileAsync_UnavailablePage_ReturnsNullAndRetriesInsteadOfCachingFailure()
        {
            var (service, factory) = CreateService("<html><body>not found</body></html>", HttpStatusCode.NotFound);

            Assert.Null(await service.ResolveProfileAsync("Alikiba", CancellationToken.None));
            Assert.Null(await service.ResolveProfileAsync("Alikiba", CancellationToken.None));

            // A failed fetch must never become a cached negative: the second call retried.
            Assert.Equal(2, factory.RequestCount);
        }

        [Fact]
        public async Task ResolveProfileAsync_ParseMiss_ReturnsNullWithoutThrowing()
        {
            var (service, _) = CreateService("<html><body>no flight chunks here</body></html>");

            Assert.Null(await service.ResolveProfileAsync("Alikiba", CancellationToken.None));
        }

        private static (AudiomackArtistLocationService Service, StubHttpClientFactory Factory) CreateService(
            string html,
            HttpStatusCode status = HttpStatusCode.OK)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Library"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"audiomack-profile-{Guid.NewGuid():N}.db")}"
                })
                .Build();
            var factory = new StubHttpClientFactory(html, status);
            var service = new AudiomackArtistLocationService(
                factory,
                new ArtistPageCacheRepository(configuration, NullLogger<ArtistPageCacheRepository>.Instance),
                new AudiomackApiClient(
                    factory,
                    new AudiomackWebCredentialsProvider(factory, NullLogger<AudiomackWebCredentialsProvider>.Instance),
                    NullLogger<AudiomackApiClient>.Instance),
                NullLogger<AudiomackArtistLocationService>.Instance,
                libraryRepository: null);
            return (service, factory);
        }

        private static string ReadFixture(string name)
        {
            var directory = Directory.GetCurrentDirectory();
            while (!string.IsNullOrWhiteSpace(directory))
            {
                var candidate = Path.Join(directory, "DeezSpoTag.Tests", "Fixtures", "Audiomack", name);
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }

                directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
            }

            throw new FileNotFoundException($"Audiomack fixture '{name}' was not found.");
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly string _html;
            private readonly HttpStatusCode _status;

            public StubHandler(string html, HttpStatusCode status)
            {
                _html = html;
                _status = status;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_html, Encoding.UTF8, "text/html")
                });
            }

            public Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => SendAsync(request, cancellationToken);
        }

        private sealed class StubHttpClientFactory : IHttpClientFactory
        {
            private readonly StubHandler _handler;
            private int _requestCount;

            public StubHttpClientFactory(string html, HttpStatusCode status)
            {
                _handler = new StubHandler(html, status);
            }

            public int RequestCount => Volatile.Read(ref _requestCount);

            public HttpClient CreateClient(string name) => new(new CountingHandler(this, _handler));

            private sealed class CountingHandler : HttpMessageHandler
            {
                private readonly StubHttpClientFactory _owner;
                private readonly StubHandler _inner;

                public CountingHandler(StubHttpClientFactory owner, StubHandler inner)
                {
                    _owner = owner;
                    _inner = inner;
                }

                protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    Interlocked.Increment(ref _owner._requestCount);
                    return _inner.RespondAsync(request, cancellationToken);
                }
            }
        }
    }
}
