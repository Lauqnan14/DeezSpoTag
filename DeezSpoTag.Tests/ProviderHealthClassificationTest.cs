using System;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Core.Constants;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     The three provider registries each carried a byte-identical copy of the health classifier -
///     the code that decides whether a provider is considered healthy enough to be written to. Two
///     of the three copies are gone; this covers the one that remains, which is the version all
///     three now call.
///     <para>
///         A regression here is not cosmetic. A provider wrongly classified as healthy is queried
///         and fails; one wrongly classified as degraded is skipped and silently never used. Both
///         present as "downloads stopped working", with nothing in the logs to explain it.
///     </para>
/// </summary>
public sealed class ProviderHealthClassificationTest
{
    /// <summary>
    ///     Runs the shared classifier, which is internal to the web assembly.
    /// </summary>
    private static string? Classify(JsonElement root, string? serviceKey = null)
    {
        var method = typeof(ProviderHealthPayloadClassifier).GetMethod(
            "Classify",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (string?)method.Invoke(null, [root, serviceKey]);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    /// <summary>
    ///     A healthy provider must return null, which is what lets the caller record a success.
    ///     An empty string is deliberately not treated as healthy: null is the single "no problem"
    ///     signal, so a second spelling of it would be a second thing to forget.
    /// </summary>
    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(299)]
    public void ASuccessfulHttpStatusIsHealthy(int code)
        => Assert.Null(Classify(Json($$"""{"status": {{code}}}""")));

    /// <summary>
    ///     429 is the one code that means "throttled" rather than "broken", and it is what puts a
    ///     provider into cooldown instead of being taken out of rotation.
    /// </summary>
    [Theory]
    [InlineData(429)]
    public void TooManyRequestsIsRateLimited(int code)
        => Assert.Equal(ProviderHealthStatus.RateLimited, Classify(Json($$"""{"status": {{code}}}""")));

    /// <summary>
    ///     A server-side fault is expected to clear on its own, so it is transient rather than a
    ///     permanent failure that would take the provider out of use.
    /// </summary>
    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void AServerFaultIsTransient(int code)
        => Assert.Equal(ProviderHealthStatus.Transient, Classify(Json($$"""{"status": {{code}}}""")));

    /// <summary>
    ///     A client-side fault is the provider's problem to fix, so it is permanent.
    /// </summary>
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public void AClientFaultIsOffline(int code)
        => Assert.Equal(ProviderHealthStatus.Offline, Classify(Json($$"""{"status": {{code}}}""")));

    /// <summary>
    ///     Providers report health as a word rather than a code, and they do not agree on the
    ///     spelling. Every synonym a provider actually sends has to land somewhere sensible, and
    ///     the healthy ones must all reach null.
    /// </summary>
    [Theory]
    [InlineData("ok")]
    [InlineData("up")]
    [InlineData("online")]
    [InlineData("healthy")]
    [InlineData("operational")]
    [InlineData("pass")]
    [InlineData("passing")]
    [InlineData("OK")]
    [InlineData("  Online  ")]
    public void AHealthyWordIsHealthyRegardlessOfHowTheProviderSpellsIt(string word)
        => Assert.Null(Classify(Json($$"""{"status": "{{word}}"}""")));

    /// <summary>
    ///     A provider saying it is merely degraded still works, so it is transient: kept in use,
    ///     with a cooldown, rather than dropped.
    /// </summary>
    [Theory]
    [InlineData("degraded")]
    [InlineData("partial")]
    [InlineData("warning")]
    [InlineData("warn")]
    public void ADegradedWordIsTransient(string word)
        => Assert.Equal(ProviderHealthStatus.Transient, Classify(Json($$"""{"status": "{{word}}"}""")));

    /// <summary>
    ///     A provider saying it is down is not coming back on its own, so it is offline.
    /// </summary>
    [Theory]
    [InlineData("down")]
    [InlineData("offline")]
    [InlineData("error")]
    [InlineData("failed")]
    [InlineData("fail")]
    [InlineData("unhealthy")]
    public void ADownWordIsOffline(string word)
        => Assert.Equal(ProviderHealthStatus.Offline, Classify(Json($$"""{"status": "{{word}}"}""")));

    /// <summary>
    ///     An unrecognised word is treated as healthy rather than as a failure. Guessing "broken"
    ///     on a status this code has never seen would take a working provider out of rotation over
    ///     a word added after this was written.
    /// </summary>
    [Fact]
    public void AnUnknownWordIsNotTreatedAsAFailure()
        => Assert.Null(Classify(Json("""{"status": "some-future-state"}""")));

    /// <summary>
    ///     A body with no status at all carries no information, so it must not be read as a fault.
    /// </summary>
    [Fact]
    public void APayloadWithNoStatusIsHealthy()
        => Assert.Null(Classify(Json("""{"unrelated": true}""")));

    /// <summary>
    ///     Some providers group their health by service, and the per-service entry is the one that
    ///     describes this provider. Reading the top-level status instead would report on the
    ///     wrong thing.
    /// </summary>
    [Fact]
    public void ThePerServiceEntryWinsOverTheTopLevelStatus()
    {
        const string body = """
            {"status":"online","services":{"download":{"status":"offline"},"other":{"status":"online"}}}
            """;

        Assert.Equal(ProviderHealthStatus.Offline, Classify(Json(body), "download"));
    }

    /// <summary>
    ///     The explicit ok flag is the clearest signal a provider can give, so it is preferred over
    ///     any status word alongside it.
    /// </summary>
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, ProviderHealthStatus.Offline)]
    public void TheOkFlagIsHonouredWhenTheProviderSendsOne(bool ok, string? expected)
    {
        var flag = ok ? "true" : "false";
        var body = "{\"services\":{\"download\":{\"ok\":" + flag + ",\"status\":\"online\"}}}";

        Assert.Equal(expected, Classify(Json(body), "download"));
    }

    /// <summary>
    ///     A service key that is absent from the payload falls back to the top-level status rather
    ///     than reporting a fault. The provider said something about itself, just not under that key.
    /// </summary>
    [Fact]
    public void AMissingServiceEntryFallsBackToTheTopLevelStatus()
        => Assert.Equal(
            ProviderHealthStatus.Offline,
            Classify(Json("""{"status":"offline","services":{"other":{"ok":true}}}"""), "download"));

    /// <summary>
    ///     A provider that reports being throttled by *word* rather than by 429 is read as healthy.
    /// </summary>
    /// <remarks>
    ///     This is pre-existing behaviour, preserved deliberately: the word vocabulary has never
    ///     included "rate_limited", which the code only ever produces from a 429. It is pinned here
    ///     rather than fixed, because changing it would alter how every provider is classified and
    ///     that is a separate decision. If a provider is ever seen sending this word, the fix is to
    ///     add it to the switch - and this test is what will show the change was made on purpose.
    /// </remarks>
    [Fact]
    public void AProviderReportingRateLimitedAsAWordIsNotInTheRecognisedVocabulary()
        => Assert.Null(Classify(Json("""{"status":"rate_limited"}""")));

    /// <summary>
    ///     A services object that is not an object at all - an array or a string - must not throw.
    ///     A health check that throws takes the provider out of rotation, which is a worse outcome
    ///     than reporting whatever the payload actually said.
    /// </summary>
    [Theory]
    [InlineData("""{"services":["download"],"status":"online"}""")]
    [InlineData("""{"services":"download","status":"online"}""")]
    [InlineData("""{"services":null,"status":"online"}""")]
    public void AMalformedServicesFieldFallsBackInsteadOfThrowing(string body)
        => Assert.Null(Classify(Json(body), "download"));

    /// <summary>
    ///     A blank service key means "no per-service lookup", which is a different request from
    ///     looking up a key that happens to be named "".
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankServiceKeyReadsTheTopLevelStatus(string? serviceKey)
        => Assert.Equal(
            ProviderHealthStatus.Offline,
            Classify(Json("""{"status":"offline"}"""), serviceKey));

    /// <summary>
    ///     The status values this returns are compared against strings held by the registries, so
    ///     they must be the shared constants rather than look-alike literals.
    /// </summary>
    [Fact]
    public void EveryVerdictIsOneOfTheSharedHealthStates()
    {
        var verdicts = new[]
        {
            Classify(Json("""{"status":"rate_limited"}""")),
            Classify(Json("""{"status":"transient"}""")),
            Classify(Json("""{"status":"offline"}""")),
            Classify(Json("""{"status":"degraded"}""")),
            Classify(Json("""{"status":"ok"}""")),
        };

        var allowed = new[]
        {
            null,
            ProviderHealthStatus.Online,
            ProviderHealthStatus.Offline,
            ProviderHealthStatus.Degraded,
            ProviderHealthStatus.Transient,
            ProviderHealthStatus.RateLimited,
            ProviderHealthStatus.Timeout,
        };

        foreach (var verdict in verdicts)
        {
            Assert.Contains(verdict, allowed);
        }
    }

    /// <summary>
    ///     The states are persisted as a provider's status, so their bytes are part of stored data.
    ///     Written against hard-coded text on purpose: a test written against the constants would
    ///     agree with any value, including a wrong one.
    /// </summary>
    [Theory]
    [InlineData("Online", "online")]
    [InlineData("Offline", "offline")]
    [InlineData("Degraded", "degraded")]
    [InlineData("Transient", "transient")]
    [InlineData("RateLimited", "rate_limited")]
    [InlineData("Timeout", "timeout")]
    public void TheHealthStateValues_AreTheOnesStoredInProviderRows(string field, string expected)
        => Assert.Equal(
            expected,
            typeof(ProviderHealthStatus)
                .GetField(field, BindingFlags.Public | BindingFlags.Static)
                ?.GetRawConstantValue());
}
