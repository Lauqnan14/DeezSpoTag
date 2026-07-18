using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MelodayHistoryImportResultTests
{
    [Fact]
    public void Status_SeparatesEndpointAvailabilityFromMappingQuality()
    {
        var mappingDegraded = new MelodayHistoryImportResult(
            "plex",
            Configured: true,
            Available: true,
            RemoteLibraries: 8,
            Fetched: 6,
            Imported: 0,
            Resolved: 1,
            Ambiguous: 3,
            Unresolved: 2,
            Error: null);

        Assert.Equal("degraded", mappingDegraded.Status);
        Assert.Equal("available", mappingDegraded.EndpointStatus);
        Assert.Equal("degraded", mappingDegraded.MappingStatus);

        var endpointUnavailable = MelodayHistoryImportResult.Unavailable("plex", "Plex library discovery failed.");

        Assert.Equal("unavailable", endpointUnavailable.Status);
        Assert.Equal("unavailable", endpointUnavailable.EndpointStatus);
        Assert.Equal("not-checked", endpointUnavailable.MappingStatus);
    }
}
