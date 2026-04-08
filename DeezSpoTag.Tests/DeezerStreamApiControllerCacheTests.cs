using System;
using System.Reflection;
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
        var playbackContextType = controllerType.GetNestedType("DeezerPlaybackContext", BindingFlags.NonPublic);

        Assert.NotNull(cacheField);
        Assert.NotNull(clearMethod);
        Assert.NotNull(cacheEntryType);
        Assert.NotNull(playbackContextType);

        try
        {
            clearMethod!.Invoke(null, null);

            var playbackContext = Activator.CreateInstance(
                playbackContextType!,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null,
                args: new object[] { "123", "stream-123", "token-123", "md5", "1", "Title" },
                culture: null);
            var cacheEntry = Activator.CreateInstance(
                cacheEntryType!,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null,
                args: new[] { playbackContext!, DateTimeOffset.UtcNow },
                culture: null);
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

    [Fact]
    public void ClearPlaybackContextCache_RemovesPreparedMediaEntries()
    {
        var controllerType = typeof(DeezerStreamApiController);
        var cacheField = controllerType.GetField("PreparedMediaCache", BindingFlags.NonPublic | BindingFlags.Static);
        var clearMethod = controllerType.GetMethod("ClearPlaybackContextCache", BindingFlags.Public | BindingFlags.Static);
        var cacheEntryType = controllerType.GetNestedType("CachedPreparedMediaResult", BindingFlags.NonPublic);

        Assert.NotNull(cacheField);
        Assert.NotNull(clearMethod);
        Assert.NotNull(cacheEntryType);

        var mediaResultType = cacheEntryType!.GetProperty("Result")!.PropertyType;
        var emptyMethod = mediaResultType.GetMethod("Empty", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(emptyMethod);

        try
        {
            clearMethod!.Invoke(null, null);

            var mediaResult = emptyMethod!.Invoke(null, null);
            var urlProperty = mediaResultType.GetProperty("Url", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(urlProperty);
            urlProperty!.SetValue(mediaResult, "https://media.example.test/track");

            var cacheEntry = Activator.CreateInstance(
                cacheEntryType,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null,
                args: new[] { mediaResult!, DateTimeOffset.UtcNow },
                culture: null);
            Assert.NotNull(cacheEntry);

            var cache = cacheField!.GetValue(null);
            Assert.NotNull(cache);

            var tryAddMethod = cache.GetType().GetMethod("TryAdd", BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(tryAddMethod);
            var added = (bool)(tryAddMethod!.Invoke(cache, new[] { "MP3_320:token-123", cacheEntry })!);
            Assert.True(added);

            var countProperty = cache.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(countProperty);
            Assert.Equal(1, (int)(countProperty!.GetValue(cache)!));

            clearMethod.Invoke(null, null);

            Assert.Equal(0, (int)(countProperty.GetValue(cache)!));
        }
        finally
        {
            clearMethod?.Invoke(null, null);
        }
    }

    [Fact]
    public void BuildPreparedStreamUrl_IncludesContextAndQualityHint()
    {
        var controllerType = typeof(DeezerStreamApiController);
        var buildMethod = controllerType.GetMethod("BuildPreparedStreamUrl", BindingFlags.NonPublic | BindingFlags.Static);
        var playbackContextType = controllerType.GetNestedType("DeezerPlaybackContext", BindingFlags.NonPublic);

        Assert.NotNull(buildMethod);
        Assert.NotNull(playbackContextType);

        var playbackContext = Activator.CreateInstance(
            playbackContextType!,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object[] { "123", "stream-123", "token-123", "md5-123", "7", "Title" },
            culture: null);

        var result = buildMethod!.Invoke(null, new[] { "123", playbackContext!, "MP3_320" }) as string;

        Assert.NotNull(result);
        Assert.Equal(
            "/api/deezer/stream/123?q=3&streamTrackId=stream-123&trackToken=token-123&md5origin=md5-123&mv=7",
            result);
    }

    [Fact]
    public void TryParseSingleRangeHeader_ParsesStartAndEndOffsets()
    {
        var controllerType = typeof(DeezerStreamApiController);
        var parseMethod = controllerType.GetMethod("TryParseSingleRangeHeader", BindingFlags.NonPublic | BindingFlags.Static);
        var rangeType = controllerType.GetNestedType("StreamRange", BindingFlags.NonPublic);

        Assert.NotNull(parseMethod);
        Assert.NotNull(rangeType);

        var range = Activator.CreateInstance(rangeType!);
        var parameters = new object?[] { "bytes=4096-8191", range };
        var parsed = (bool)parseMethod!.Invoke(null, parameters)!;

        Assert.True(parsed);
        Assert.NotNull(parameters[1]);

        var startProperty = parameters[1]!.GetType().GetProperty("Start", BindingFlags.Public | BindingFlags.Instance);
        var endProperty = parameters[1]!.GetType().GetProperty("End", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(startProperty);
        Assert.NotNull(endProperty);

        Assert.Equal(4096L, (long)startProperty!.GetValue(parameters[1])!);
        Assert.Equal(8191L, (long?)endProperty!.GetValue(parameters[1])!);
    }
}
