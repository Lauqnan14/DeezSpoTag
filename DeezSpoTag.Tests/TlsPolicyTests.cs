using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Authentication;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TlsPolicyTests
{
    [Fact]
    public void AllowInsecure_ReturnsTrue_WhenConfigured()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Download:AllowInsecureTls"] = "true"
            })
            .Build();

        var allowed = TlsPolicy.AllowInsecure(config);

        Assert.True(allowed);
    }

    [Fact]
    public void AllowInsecure_ReturnsTrue_WhenEnvVarSet()
    {
        using var _ = new EnvVarScope("DEEZSPOTAG_ALLOW_INSECURE_TLS", "yes");

        var allowed = TlsPolicy.AllowInsecure(configuration: null);

        Assert.True(allowed);
    }

    [Fact]
    public void AllowInsecure_ReturnsFalse_WhenUnset()
    {
        using var _ = new EnvVarScope("DEEZSPOTAG_ALLOW_INSECURE_TLS", null);

        var allowed = TlsPolicy.AllowInsecure(configuration: null);

        Assert.False(allowed);
    }

    [Fact]
    public void AllowLegacy_ReturnsTrue_WhenLegacyEnvVarSet()
    {
        using var _ = new EnvVarScope("DEEZSPOTAG_ALLOW_LEGACY_TLS", "1");

        var allowed = TlsPolicy.AllowLegacy(configuration: null);

        Assert.True(allowed);
    }

    [Fact]
    public void ApplyIfAllowed_EnablesCertificateRevocationCheck()
    {
        using var handler = new HttpClientHandler { CheckCertificateRevocationList = false };

        TlsPolicy.ApplyIfAllowed(handler, configuration: null);

        Assert.True(handler.CheckCertificateRevocationList);
    }

    [Fact]
    public void GetSslProtocols_ReturnsTls12AndTls13()
    {
        var protocols = TlsPolicy.GetSslProtocols(configuration: null);

        Assert.Equal(SslProtocols.Tls12 | SslProtocols.Tls13, protocols);
    }

    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _key;
        private readonly string? _previousValue;

        public EnvVarScope(string key, string? value)
        {
            _key = key;
            _previousValue = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_key, _previousValue);
        }
    }
}
