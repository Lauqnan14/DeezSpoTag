using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MelodayRatingFilterTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(10, false)]
    public void ExplicitLowRating_DoesNotTreatPlexUnratedZeroAsDisliked(
        int? rating,
        bool expected)
    {
        Assert.Equal(expected, MelodayService.IsExplicitLowRating(rating));
    }
}
