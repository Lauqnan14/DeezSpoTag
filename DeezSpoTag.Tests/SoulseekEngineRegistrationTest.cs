using System;
using System.Linq;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Soulseek;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Regression tests for the engine-registration gaps found during review.
/// </summary>
/// <remarks>
///     <para>
///         The intent service builds the queue payload by switching on the engine name, and copies the track
///         metadata by switching on the concrete payload type. Both switches originally ended in a Deezer
///         default, so a Soulseek download was enqueued carrying a Deezer payload with <c>Engine="deezer"</c> and
///         no Soulseek fields. The engine was still dispatched, because dispatch uses the queue row's engine
///         column, so the failure was silent: the download ran with nothing to search for.
///     </para>
/// </remarks>
public sealed class SoulseekEngineRegistrationTest
{
    [Fact]
    public void EveryEnginePayloadTypeIsCreatedForItsEngine()
    {
        var source = ReadRepoFile("DeezSpoTag.Web", "Services", "DownloadIntentService.cs");
        var block = ExtractMethod(source, "private static EngineQueueItemBase CreateQueuePayloadForEngine");

        // Each engine that has a payload type must be named. A missing entry silently falls back to Deezer.
        foreach (var (engine, payload) in new[]
        {
            ("ApplePlatform", "AppleQueueItem"),
            ("TidalPlatform", "TidalQueueItem"),
            ("AmazonPlatform", "AmazonQueueItem"),
            ("QobuzPlatform", "QobuzQueueItem"),
            ("SoulseekPlatform", "SoulseekQueueItem")
        })
        {
            Assert.Contains($"{engine} => new {payload}()", block);
        }
    }

    [Fact]
    public void TrackMetadataIsCopiedOntoEveryEnginePayload()
    {
        var source = ReadRepoFile("DeezSpoTag.Web", "Services", "DownloadIntentService.cs");
        var block = ExtractMethod(source, "private static void ApplyIntentMetadataForPayload");

        // Without a case here the payload keeps an empty Title/Artist/Album, so the engine searches for nothing.
        foreach (var payload in new[]
        {
            "DeezerQueueItem", "AppleQueueItem", "TidalQueueItem",
            "AmazonQueueItem", "QobuzQueueItem", "SoulseekQueueItem"
        })
        {
            Assert.Contains($"case {payload}", block);
        }
    }

    [Fact]
    public void EveryEnginePayloadIsAnnouncedToTheActivityListener()
    {
        var source = ReadRepoFile("DeezSpoTag.Web", "Services", "DownloadIntentService.cs");

        // Anchor on the listener call, because there are two `switch (payload)` blocks and only this one
        // feeds the activities page.
        var anchor = source.IndexOf("SendAddedToQueue", StringComparison.Ordinal);
        Assert.True(anchor >= 0, "the listener call was not found");

        var start = source.LastIndexOf("switch (payload)", anchor, StringComparison.Ordinal);
        Assert.True(start >= 0 && start < anchor, "the listener switch was not found");

        var end = source.IndexOf("\n        }", anchor, StringComparison.Ordinal);
        var block = source.Substring(start, (end < 0 ? source.Length : end) - start);

        // The activities page is fed from these events; a missing case means Soulseek downloads never appear.
        foreach (var payload in new[]
        {
            "DeezerQueueItem", "AppleQueueItem", "TidalQueueItem",
            "AmazonQueueItem", "QobuzQueueItem", "SoulseekQueueItem"
        })
        {
            Assert.Contains($"case {payload}", block);
        }
    }

    [Fact]
    public void QualityAndContentTypeAreReadableForEveryStereoPayload()
    {
        var source = ReadRepoFile("DeezSpoTag.Web", "Services", "DownloadIntentService.cs");

        Assert.Contains("SoulseekQueueItem soulseek => soulseek.Quality", source);
        Assert.Contains("SoulseekQueueItem soulseek => soulseek.ContentType", source);
    }

    [Fact]
    public void SoulseekQueueItemIdentifiesItselfAsSoulseek()
    {
        var queueItem = new SoulseekQueueItem();

        Assert.Equal("soulseek", queueItem.Engine);
        Assert.Equal("soulseek", queueItem.SourceService);
        Assert.Equal("soulseek", SoulseekQueueItem.EngineId);
    }

    [Fact]
    public void TheQueueItemRoundTripsThroughItsOwnJson()
    {
        // The shared pipeline serialises the payload at enqueue and deserialises it as SoulseekQueueItem at run
        // time, so the engine name has to survive the trip.
        var original = new SoulseekQueueItem
        {
            Title = "Roygbiv",
            Artist = "Boards of Canada",
            SoulseekUsername = "listener",
            SoulseekRemotePath = "share/track.flac",
            SoulseekQualityCode = "FLAC"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(original);
        var restored = System.Text.Json.JsonSerializer.Deserialize<SoulseekQueueItem>(json);

        Assert.NotNull(restored);
        Assert.Equal("soulseek", restored!.Engine);
        Assert.Equal("Roygbiv", restored.Title);
        Assert.Equal("listener", restored.SoulseekUsername);
        Assert.Equal("FLAC", restored.SoulseekQualityCode);
    }

    /// <summary>
    ///     Task 2: the display cover is presentation only. It has to survive the queue round trip so the
    ///     activity view can render it, while the base <c>Cover</c> stays empty so the tagging pipeline can
    ///     never pick it up as prefetched artwork.
    /// </summary>
    [Fact]
    public void TheDisplayCoverSurvivesTheRoundTripWithoutTouchingTheTaggingCover()
    {
        var original = new SoulseekQueueItem
        {
            Title = "Roygbiv",
            Artist = "Boards of Canada",
            SoulseekUsername = "listener",
            SoulseekQualityCode = "FLAC",
            SoulseekDisplayCoverUrl = "https://cdn.example/cover.jpg"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(original);
        var restored = System.Text.Json.JsonSerializer.Deserialize<SoulseekQueueItem>(json);

        Assert.NotNull(restored);
        Assert.Equal("https://cdn.example/cover.jpg", restored!.SoulseekDisplayCoverUrl);

        // The load-bearing assertion: the field the tagger reads must still be empty.
        Assert.Equal(string.Empty, restored.Cover);

        // The UI payload shows the display cover.
        var payload = restored.ToQueuePayload();
        Assert.Equal("https://cdn.example/cover.jpg", payload["cover"]);

        // And a rebuilt payload keeps it, so a restored queue entry still renders.
        var rebuilt = System.Text.Json.JsonSerializer.Deserialize<SoulseekQueueItem>(
            System.Text.Json.JsonSerializer.Serialize(restored));
        Assert.NotNull(rebuilt);
        Assert.Equal("https://cdn.example/cover.jpg", rebuilt!.ToQueuePayload()["cover"]);
    }

    [Fact]
    public void AnAbsentDisplayCoverFallsBackToTheStandardPlaceholder()
    {
        var item = new SoulseekQueueItem
        {
            Title = "Roygbiv",
            Artist = "Boards of Canada",
            SoulseekDisplayCoverUrl = "   "
        };

        // Same placeholder every other engine gets when it has no artwork.
        Assert.Equal("/images/unavailable/unavailable.jpg", item.ToQueuePayload()["cover"]);
    }

    /// <summary>
    ///     The mapper must copy the display value and nothing else. If it also assigned the base cover, the
    ///     value would become the tagger's prefetched artwork source and override the profile preference.
    /// </summary>
    [Fact]
    public void TheSoulseekMapperNeverPopulatesTheTaggingCoverFromTheDisplayValue()
    {
        var intents = ReadRepoFile("DeezSpoTag.Web", "Services", "DownloadIntentService.cs");
        var mapper = ExtractMethod(intents, "private static void ApplyIntentMetadata(SoulseekQueueItem payload, DownloadIntent intent)");

        Assert.Contains("ApplyIntentMetadataToStereoPayload(payload, intent)", mapper, System.StringComparison.Ordinal);
        Assert.Contains("SoulseekDisplayCoverUrl", mapper, System.StringComparison.Ordinal);

        // The tagging field is assigned from intent.Cover, never from the display value. This asserts on an
        // assignment, so the explanatory comment that names the field does not trip it.
        Assert.DoesNotContain("payload.Cover =", mapper, System.StringComparison.Ordinal);
        Assert.DoesNotContain("payload.Cover ??=", mapper, System.StringComparison.Ordinal);

        // And the shared stereo mapper must not assign the base cover either, so nothing on the Soulseek
        // path can reach the tagging field.
        var shared = ExtractMethod(intents, "private static void ApplyIntentMetadataToStereoPayload");
        Assert.DoesNotContain("Cover", shared, System.StringComparison.Ordinal);

        // The whole mapper is the only place a Soulseek payload gets its display value.
        Assert.Equal(1, CountOccurrences(mapper, "SoulseekDisplayCoverUrl ="));
    }

    [Fact]
    public void TheEngineIsRegisteredNextToTheProcessorThatNeedsIt()
    {
        // The processor is registered for every host that calls AddDeezSpoTagQueue, including the workers host,
        // which owns no slskd credential store. Resolving the processor collection therefore throws unless the
        // engine's own services are registered in the same place, which is what took the whole download queue
        // down in the workers host.
        var queue = ReadRepoFile("DeezSpoTag.Services", "Download", "Shared", "DeezSpoTagServiceExtensions.cs");

        Assert.Contains("services.AddSoulseekDownloadEngine();", queue);
        Assert.Contains(
            "AddScoped<IQueueEngineProcessor, DeezSpoTag.Services.Download.Soulseek.SoulseekEngineProcessor>",
            queue);

        var extension = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekServiceExtensions.cs");
        foreach (var registration in new[]
                 {
                     "ISoulseekCredentialProvider",
                     "ISoulseekRealtimePublisher",
                     "ISoulseekConnectionService",
                     "SoulseekRepository",
                     "SoulseekSettingsService",
                     "ISoulseekPeerPolicyService",
                     "ISoulseekResultScoringService",
                     "ISoulseekSearchService",
                     "ISoulseekTransferService",
                     "ISoulseekDownloadService",
                     "ISoulseekShareService",
                     "IQueueMaintenanceTask"
                 })
        {
            Assert.Contains(registration, extension);
        }

        // A host that owns no credential store still has to satisfy the graph, so the null provider is the
        // default rather than a missing registration.
        Assert.Contains("NullSoulseekCredentialProvider.Instance", extension);
        Assert.Contains("NullSoulseekRealtimePublisher.Instance", extension);
    }

    /// <summary>
    ///     The web tier's slskd credential provider and hub publisher have to be the <em>last</em> descriptors
    ///     for their service types, or the shared registration clobbers them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>AddSoulseekDownloadEngine</c> registers null-object defaults, because the workers host owns no
    ///         encrypted credential store and must still be able to construct the engine graph. Last registration
    ///         wins, so the web tier has to override those defaults <em>after</em> every call that reaches
    ///         <c>AddDeezSpoTagQueue</c> - and there are two such calls in <c>Program.cs</c>: one directly, and one
    ///         inside <c>AddDownloadEngine</c>.
    ///     </para>
    ///     <para>
    ///         Overriding after only the first call is what silently disabled the whole Soulseek download path: the
    ///         services layer resolved the null provider, so the connection probe reported
    ///         <c>notconfigured</c> for a perfectly valid saved instance, no reconnect was ever attempted, and no
    ///         search update was ever published. Every unit test still passed, because they assert the presence of
    ///         the registration strings and build their own container without the web composition.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheWebTierSoulseekOverridesAreRegisteredAfterTheSharedDefaults()
    {
        const string credentialProviderRegistration =
            "ISoulseekCredentialProvider, DeezSpoTag.Web.Services.PlatformAuthSoulseekCredentialProvider>";
        const string realtimePublisherRegistration = "ISoulseekRealtimePublisher>";

        var program = ReadRepoFile("DeezSpoTag.Web", "Program.cs");

        // The order that matters is the order the methods are *called*, not their position in the file.
        // RegisterCoreApplicationServices is defined far below the call to AddDownloadEngine, so a plain index
        // comparison across the whole file passes even though the registration runs first. The overrides
        // therefore have to live in the same method that performs the shared call, after it.
        var application = ExtractRegistrationMethod(program, "static void RegisterApplicationServices(");
        var core = ExtractRegistrationMethod(program, "static void RegisterCoreApplicationServices(");

        var sharedCall = application.IndexOf("services.AddDownloadEngine();", StringComparison.Ordinal);
        Assert.True(sharedCall >= 0, "RegisterApplicationServices no longer calls AddDownloadEngine().");

        var credentialProvider = application.IndexOf(credentialProviderRegistration, StringComparison.Ordinal);
        var realtimePublisher = application.IndexOf(realtimePublisherRegistration, StringComparison.Ordinal);

        Assert.True(
            credentialProvider > sharedCall,
            "The web tier's Soulseek credential provider must be registered after AddDownloadEngine() in "
            + "RegisterApplicationServices, because AddDownloadEngine calls AddDeezSpoTagQueue, which "
            + "re-registers the null default that would otherwise win.");
        Assert.True(
            realtimePublisher > sharedCall,
            "The web tier's Soulseek realtime publisher must be registered after AddDownloadEngine() in "
            + "RegisterApplicationServices, for the same reason as the credential provider.");

        // And they must not have been left behind in a helper that runs before the shared call, which is the
        // arrangement that silently disabled the Soulseek download path in the first place.
        Assert.DoesNotContain(credentialProviderRegistration, core, StringComparison.Ordinal);
        Assert.DoesNotContain(realtimePublisherRegistration, core, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Returns the body of a <c>static void</c> registration helper, from its signature to the next one.
    /// </summary>
    private static string ExtractRegistrationMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found in Program.cs");

        // Registration helpers are top-level members, so the next one starts a fresh "\n    static " line.
        var next = source.IndexOf("\n    static ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    [Fact]
    public void ProgressEventsCarryTheQueueItemTheyBelongTo()
    {
        // Both events used to hard-code an empty uuid, so a client could not attribute progress to a download.
        var search = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekSearchService.cs");
        var transfer = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekTransferService.cs");

        Assert.DoesNotContain("QueueUuid: null", search);
        Assert.DoesNotContain("PublishDownloadUpdate(new SoulseekDownloadProgress(\n                    string.Empty", transfer);

        // The uuid has to be threaded from the request through to the event.
        var request = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekRequestBuilder.cs");
        Assert.Contains("request.QueueUuid = item.Id;", request);
    }

    [Fact]
    public void TerminalTransfersArePersistedWhicheverWayTheyEnd()
    {
        // Only the failure branch recorded a row, so the transfer table stayed empty for every download that
        // worked and the history endpoints had nothing to report.
        var transfer = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekTransferService.cs");

        Assert.Contains("RecordTerminalAsync", transfer);
        Assert.DoesNotContain("verified: false, cancellationToken)", transfer);
    }

    [Fact]
    public void TheDocumentedEndpointSetIsComplete()
    {
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");

        foreach (var route in new[]
        {
            "connection/test", "connection/connect", "connection/disconnect", "connection/status",
            "download-settings",
            "searches\"", "searches/queue/{queueUuid}", "searches/{searchId:guid}",
            "searches/{searchId:guid}/results", "searches/cleanup", "searches/{searchId:guid}",
            "users/{username}/directory",
            "downloads\"", "downloads/queue", "downloads/{id:guid}",
            "downloads/{id:guid}/cancel", "downloads/{id:guid}/retry", "downloads/{id:guid}\"",
            "downloads/cleanup", "searches/cleanup",
            "templates/preview", "import/{downloadId}", "tagging/reprocess/{downloadId}"
        })
        {
            Assert.Contains(route, controller);
        }
    }

    [Fact]
    public void ImportActuallyReopensTheItemInsteadOfOnlyDescribingIt()
    {
        var controller = ReadRepoFile("DeezSpoTag.Web", "Controllers", "Api", "SoulseekApiController.cs");
        var import = ExtractMethod(controller, "public async Task<IActionResult> Import");

        Assert.Contains("SetEnrichmentStatusAsync", import);
        Assert.DoesNotContain("message = \"Import runs through the shared post-download pipeline.\"", controller);
    }

    private static string ExtractMethod(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{marker} not found");
        var next = source.IndexOf("\n    private ", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source.Substring(start, next - start);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, System.StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, System.StringComparison.Ordinal);
        }

        return count;
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var root = ResolveRepoRoot();
        return System.IO.File.ReadAllText(System.IO.Path.Join(new[] { root }.Concat(parts).ToArray()));
    }

    private static string ResolveRepoRoot()
    {
        var current = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (System.IO.Directory.Exists(System.IO.Path.Join(current.FullName, "DeezSpoTag.Services")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
