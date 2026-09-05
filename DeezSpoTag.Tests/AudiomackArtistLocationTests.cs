using DeezSpoTag.Web.Services.Audiomack;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AudiomackArtistLocationTests
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
        private const string FlightPageHtml = """
            <html lang="en"><head><meta charSet="utf-8"/></head><body>
            <script>self.__next_f.push([1,"3:\"name\":\"Sista Ashlee\",\"verified\":\"authenticated\",\"hometown\":\"Paris, France\",\"image_base\":\"https://i.audiomack.com/sista-ashlee/x.webp\",\"location\":null,\"url_slug\":\"sista-ashlee\",\"type\":\"artist\""])</script>
            <script>self.__next_f.push([1,"9:\"name\":\"Still Shadey\",\"verified\":\"authenticated\",\"hometown\":\"Accra, Ghana\",\"image_base\":\"https://i.audiomack.com/still-shadey/y.webp\",\"location\":null,\"url_slug\":\"still-shadey\",\"type\":\"artist\""])</script>
            </body></html>
            """;

        [Fact]
        public void TryExtractRawLocation_UsesArtistObjectMatchingRequestedSlugAndName()
        {
            var raw = AudiomackArtistPageParser.TryExtractRawLocation(FlightPageHtml, "still-shadey", "Still Shadey");

            Assert.Equal("Accra, Ghana", raw);
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
                <script>self.__next_f.push([1,"3:\"name\":\"Ghost\",\"hometown\":\"\",\"location\":null,\"url_slug\":\"ghost-artist\",\"type\":\"artist\""])</script>
                """;

            Assert.Null(AudiomackArtistPageParser.TryExtractRawLocation(html, "ghost-artist", "Ghost"));
        }

        [Fact]
        public void TryExtractRawLocation_EmptyHometownFallsBackToLocationField()
        {
            const string html = """
                <script>self.__next_f.push([1,"3:\"name\":\"Test\",\"hometown\":\"\",\"location\":\"Nairobi, Kenya\",\"url_slug\":\"test-artist\",\"type\":\"artist\""])</script>
                """;

            Assert.Equal("Nairobi, Kenya", AudiomackArtistPageParser.TryExtractRawLocation(html, "test-artist", "Test"));
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
}
