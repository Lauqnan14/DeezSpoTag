using DeezSpoTag.Services.Download.Shared;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SidecarFetchActivityTests
{
    public static TheoryData<SidecarFetchWork, string?> Cases => new()
    {
        { new(false, false, false, false), null },
        { new(true, false, false, false), "Fetching album artwork" },
        { new(false, true, false, false), "Fetching animated artwork" },
        { new(false, false, true, false), "Fetching artist artwork" },
        { new(false, false, false, true), "Fetching lyrics" },
        { new(true, true, false, false), "Fetching album artwork and animated artwork" },
        { new(true, false, true, true), "Fetching album artwork, artist artwork and lyrics" },
        { new(true, true, true, true), "Fetching album artwork, animated artwork, artist artwork and lyrics" }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Describe_UsesDownloadOrderingAndWording(SidecarFetchWork work, string? expected)
        => Assert.Equal(expected, SidecarFetchActivity.Describe(work));
}
