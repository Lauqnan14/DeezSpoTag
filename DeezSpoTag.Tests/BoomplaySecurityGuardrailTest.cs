using System;
using System.IO;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BoomplaySecurityGuardrailTest
{
    [Fact]
    public void Boomplay_cookie_rejects_header_injection()
    {
        var accepted = BoomplaySessionCookie.TryNormalize(
            "sessionID=abc123\r\nX-Injected: yes",
            out var normalized);

        Assert.False(accepted);
        Assert.Empty(normalized);
    }

    [Fact]
    public void Boomplay_cookie_normalizes_valid_browser_cookie_header()
    {
        var accepted = BoomplaySessionCookie.TryNormalize(
            " sessionID = abc123 ; valid=T ; countryCode=KE ",
            out var normalized);

        Assert.True(accepted);
        Assert.Equal("sessionID=abc123; valid=T; countryCode=KE", normalized);
    }

    [Fact]
    public void Boomplay_cookie_accepts_bare_session_id_without_dots()
    {
        const string sessionId = "a94f18d7c2134f5a98e3b27d0c61ee42";

        var accepted = BoomplaySessionCookie.TryNormalize(sessionId, out var normalized);

        Assert.True(accepted);
        Assert.Equal("sessionID=" + sessionId, normalized);
        Assert.True(BoomplaySessionCookie.TryExtractSessionId(normalized, out var extracted));
        Assert.Equal(sessionId, extracted);
    }

    [Fact]
    public void Boomplay_user_agent_rejects_header_injection()
    {
        Assert.False(BoomplaySessionCookie.TryNormalizeUserAgent(
            "Mozilla/5.0\r\nX-Injected: yes",
            out var normalized));
        Assert.Empty(normalized);
    }

    [Fact]
    public void Boomplay_resource_cipher_does_not_store_literal_secret_strings()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "DeezSpoTag.Web",
            "Services",
            "BoomplayMetadataService.cs"));

        Assert.DoesNotContain("boomplayVr3xopAM", source, StringComparison.Ordinal);
        Assert.DoesNotContain("boomplay8xIsKTn9", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Encoding.ASCII.GetBytes(\"", source, StringComparison.Ordinal);
    }

    private static string GetRepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Combine(directory, "DeezSpoTag.Web", "Services", "BoomplayMetadataService.cs");
            if (File.Exists(candidate))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Unable to locate repository root.");
    }
}
