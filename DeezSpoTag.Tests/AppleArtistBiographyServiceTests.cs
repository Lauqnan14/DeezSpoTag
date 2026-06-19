using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AppleArtistBiographyServiceTests
{
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
