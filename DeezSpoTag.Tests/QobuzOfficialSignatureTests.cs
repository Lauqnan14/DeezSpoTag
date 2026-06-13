using DeezSpoTag.Services.Download.Qobuz;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class QobuzOfficialSignatureTests
{
    [Fact]
    public void ComputeProtocolDigestHex_MatchesOfficialDigestVector()
    {
        var digest = QobuzOfficialSignature.ComputeProtocolDigestHex("abc");

        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", digest);
    }
}
