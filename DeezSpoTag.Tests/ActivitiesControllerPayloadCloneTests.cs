using System.Collections.Generic;
using DeezSpoTag.Web.Controllers;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ActivitiesControllerPayloadCloneTests
{
    [Fact]
    public void ClonePayloadDictionary_DoesNotThrow_OnMixedCaseFinalDestinationKeys()
    {
        var payload = new Dictionary<string, object>
        {
            ["FinalDestinations"] = new Dictionary<string, string> { ["a"] = "b" },
            ["finalDestinations"] = new Dictionary<string, string> { ["c"] = "d" }
        };

        var clone = ActivitiesController.ClonePayloadDictionary(payload);

        Assert.Single(clone);
        Assert.True(clone.ContainsKey("finalDestinations"));
        var finalDestinations = Assert.IsType<Dictionary<string, string>>(clone["finalDestinations"]);
        Assert.Equal("d", finalDestinations["c"]);
    }
}
