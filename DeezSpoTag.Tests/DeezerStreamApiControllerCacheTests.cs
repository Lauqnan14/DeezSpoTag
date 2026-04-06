using System;
using System.Reflection;
using System.Runtime.Serialization;
using DeezSpoTag.Web.Controllers.Api;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DeezerStreamApiControllerCacheTests
{
    [Fact]
    public void ClearPlaybackContextCache_RemovesAllCachedEntries()
    {
        var controllerType = typeof(DeezerStreamApiController);
        var cacheField = controllerType.GetField("PlaybackContextCache", BindingFlags.NonPublic | BindingFlags.Static);
        var clearMethod = controllerType.GetMethod("ClearPlaybackContextCache", BindingFlags.Public | BindingFlags.Static);
        var cacheEntryType = controllerType.GetNestedType("CachedPlaybackContext", BindingFlags.NonPublic);

        Assert.NotNull(cacheField);
        Assert.NotNull(clearMethod);
        Assert.NotNull(cacheEntryType);

        try
        {
            clearMethod!.Invoke(null, null);

            var cacheEntry = FormatterServices.GetUninitializedObject(cacheEntryType!);
            Assert.NotNull(cacheEntry);

            var cache = cacheField!.GetValue(null);
            Assert.NotNull(cache);

            var tryAddMethod = cache.GetType().GetMethod("TryAdd", BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(tryAddMethod);
            var added = (bool)(tryAddMethod!.Invoke(cache, new[] { "123", cacheEntry })!);
            Assert.True(added);

            var countProperty = cache.GetType().GetProperty("Count");
            Assert.NotNull(countProperty);
            Assert.Equal(1, (int)(countProperty!.GetValue(cache)!));

            clearMethod!.Invoke(null, null);

            Assert.Equal(0, (int)(countProperty.GetValue(cache)!));
        }
        finally
        {
            clearMethod?.Invoke(null, null);
        }
    }
}
