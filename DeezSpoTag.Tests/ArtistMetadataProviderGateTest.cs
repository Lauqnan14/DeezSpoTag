using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistMetadataProviderGateTest
{
    [Fact]
    public async Task EmptyOrFailedLookupDoesNotOpenTheProviderCircuit()
    {
        var gate = CreateGate();
        var secondInvoked = false;

        var first = await gate.RunAsync(
            "spotify",
            _ => Task.FromResult(0),
            CancellationToken.None);

        var second = await gate.RunAsync(
            "spotify",
            _ =>
            {
                secondInvoked = true;
                return Task.FromResult(2);
            },
            CancellationToken.None);

        Assert.Equal(0, first);
        Assert.Equal(2, second);
        Assert.True(secondInvoked);
        Assert.False(gate.IsUnavailable("spotify"));
    }

    [Fact]
    public async Task RateLimitOpensTheProviderCircuitForTheRestOfTheRun()
    {
        var gate = CreateGate();
        var secondInvoked = false;

        var first = await gate.RunAsync<int>(
            "tidal",
            _ => throw new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests),
            CancellationToken.None);

        var second = await gate.RunAsync(
            "tidal",
            _ =>
            {
                secondInvoked = true;
                return Task.FromResult(7);
            },
            CancellationToken.None);

        Assert.Equal(0, first);
        Assert.Equal(0, second);
        Assert.False(secondInvoked);
        Assert.True(gate.IsUnavailable("tidal"));
    }

    [Fact]
    public async Task SameProviderCallsAreSpacedByTheConfiguredMinimumInterval()
    {
        var gate = CreateGate(minInterval: _ => TimeSpan.FromMilliseconds(80));
        var started = DateTimeOffset.UtcNow;

        await gate.RunAsync("qobuz", _ => Task.FromResult(1), CancellationToken.None);
        await gate.RunAsync("qobuz", _ => Task.FromResult(2), CancellationToken.None);

        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(70));
    }

    [Fact]
    public async Task SameProviderAllowsOnlyOneInFlightCall()
    {
        var gate = CreateGate();
        var current = 0;
        var max = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Work(CancellationToken token)
        {
            var inFlight = Interlocked.Increment(ref current);
            while (true)
            {
                var observed = Volatile.Read(ref max);
                if (inFlight <= observed || Interlocked.CompareExchange(ref max, inFlight, observed) == observed)
                {
                    break;
                }
            }

            started.TrySetResult();
            await Task.Delay(80, token);
            Interlocked.Decrement(ref current);
            return inFlight;
        }

        var first = gate.RunAsync("lastfm", Work, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = await gate.RunAsync("lastfm", Work, CancellationToken.None);
        await first;

        Assert.Equal(1, max);
        Assert.Equal(1, second);
    }

    [Fact]
    public async Task ParentCancellationDoesNotOpenTheCircuit()
    {
        var gate = CreateGate();
        using var cts = new CancellationTokenSource();
        var workStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = gate.RunAsync(
            "apple",
            async token =>
            {
                workStarted.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                return 1;
            },
            cts.Token);

        await workStarted.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(gate.IsUnavailable("apple"));
    }

    [Fact]
    public void ThrowIfRateLimitedUsesHttp429()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var ex = Assert.Throws<HttpRequestException>(() => ArtistMetadataProviderGate.ThrowIfRateLimited(response));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.True(ArtistMetadataProviderGate.IsRateLimited(ex));
    }

    private static ArtistMetadataProviderGate CreateGate(Func<string, TimeSpan>? minInterval = null)
        => new(NullLogger<ArtistMetadataProviderGate>.Instance, minInterval ?? (_ => TimeSpan.Zero));
}
