using System.Text;
using DeezSpoTag.Services.Download.Shared.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class Base64UrlDecoderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void TryDecode_ReturnsNull_WhenInputIsEmpty(string? input)
    {
        var decoded = Base64UrlDecoder.TryDecode(input);

        Assert.Null(decoded);
    }

    [Fact]
    public void TryDecode_DecodesStandardBase64()
    {
        var decoded = Base64UrlDecoder.TryDecode("SGVsbG8=");

        Assert.NotNull(decoded);
        Assert.Equal("Hello", Encoding.UTF8.GetString(decoded!));
    }

    [Fact]
    public void TryDecode_DecodesUnpaddedInput()
    {
        var decoded = Base64UrlDecoder.TryDecode("SGVsbG8");

        Assert.NotNull(decoded);
        Assert.Equal("Hello", Encoding.UTF8.GetString(decoded!));
    }

    [Fact]
    public void TryDecode_DecodesUrlSafeCharacters()
    {
        var decoded = Base64UrlDecoder.TryDecode("_-8");

        Assert.NotNull(decoded);
        Assert.Equal(new byte[] { 255, 239 }, decoded);
    }

    [Fact]
    public void TryDecode_ReturnsNull_WhenInputIsInvalid()
    {
        var decoded = Base64UrlDecoder.TryDecode("!not-base64!");

        Assert.Null(decoded);
    }
}
