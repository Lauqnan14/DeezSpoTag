using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class RetryPolicyTests
{
    [Fact]
    public void IsTransient_ReturnsTrue_ForTransientHttpStatusCodes()
    {
        var ex = new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests);

        var transient = RetryPolicy.IsTransient(ex);

        Assert.True(transient);
    }

    [Fact]
    public void IsTransient_ReturnsTrue_ForTimeoutAndCancellation()
    {
        Assert.True(RetryPolicy.IsTransient(new TaskCanceledException("cancelled")));
        Assert.True(RetryPolicy.IsTransient(new TimeoutException("timeout")));
    }

    [Fact]
    public void IsTransient_ReturnsTrue_ForSocketWrappedInIOException()
    {
        var ex = new IOException("io failed", new SocketException((int)SocketError.TimedOut));

        var transient = RetryPolicy.IsTransient(ex);

        Assert.True(transient);
    }

    [Fact]
    public void IsTransient_ReturnsTrue_ForNestedTransientInnerException()
    {
        var ex = new InvalidOperationException(
            "outer",
            new InvalidOperationException(
                "middle",
                new TaskCanceledException("cancelled")));

        var transient = RetryPolicy.IsTransient(ex);

        Assert.True(transient);
    }

    [Fact]
    public void IsTransient_ReturnsTrue_ForAggregateContainingTransientException()
    {
        var aggregate = new AggregateException(
            new InvalidOperationException("non-transient"),
            new TimeoutException("transient"));

        var transient = RetryPolicy.IsTransient(aggregate);

        Assert.True(transient);
    }

    [Fact]
    public void IsTransient_ReturnsFalse_ForNonTransientException()
    {
        var ex = new InvalidOperationException("not transient");

        var transient = RetryPolicy.IsTransient(ex);

        Assert.False(transient);
    }
}
