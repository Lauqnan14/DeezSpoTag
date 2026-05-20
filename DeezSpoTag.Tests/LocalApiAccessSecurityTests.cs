using System;
using System.Net;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LocalApiAccessSecurityTests
{
    [Fact]
    public void IsAllowedForSensitiveAuth_RejectsPrivateNetworkEvenWhenTrustedByEnvironment()
    {
        var previous = Environment.GetEnvironmentVariable("DEEZSPOTAG_TRUST_PRIVATE_NETWORK");
        Environment.SetEnvironmentVariable("DEEZSPOTAG_TRUST_PRIVATE_NETWORK", "1");
        try
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.45");

            Assert.False(LocalApiAccess.IsAllowedForSensitiveAuth(context));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEZSPOTAG_TRUST_PRIVATE_NETWORK", previous);
        }
    }

    [Fact]
    public void IsAllowedForSensitiveAuth_AllowsLoopbackWithoutAuthentication()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;

        Assert.True(LocalApiAccess.IsAllowedForSensitiveAuth(context));
    }

    [Fact]
    public void IsAllowedForSensitiveAuth_RejectsForwardedHeadersWithoutAuthentication()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Forwarded-For"] = "1.2.3.4";

        Assert.False(LocalApiAccess.IsAllowedForSensitiveAuth(context));
    }
}
