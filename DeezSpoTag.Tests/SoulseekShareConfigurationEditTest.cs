using System;
using System.Linq;
using DeezSpoTag.Services.Download.Soulseek;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Covers rewriting <c>shares.directories</c> in an slskd configuration document.
/// </summary>
/// <remarks>
///     <para>
///         This is the only mechanism available for changing what slskd shares, and the document it rewrites
///         also holds the Soulseek credentials, the transfer limits and the relay configuration. Every other
///         line therefore has to survive untouched, and anything the editor cannot read with certainty has to
///         be refused rather than guessed at.
///     </para>
/// </remarks>
public sealed class SoulseekShareConfigurationEditTest
{
    private const string TypicalConfig = """
        instance_name: default

        soulseek:
          username: Loaqx
          password: hunter2
          listen_port: 5030

        directories:
          incomplete: /incomplete
          downloads: /downloads

        shares:
          directories:
            - '/music/Albums'
            - '[Lossless]/music/FLAC'
          filters:
            - '\.ini$'
          cache:
            storage_mode: memory
            workers: 12

        remote_configuration: false
        """;

    [Fact]
    public void ApplyDirectories_AddsAPathAndLeavesEveryOtherSettingAlone()
    {
        var updated = SoulseekShareConfiguration.ApplyDirectories(
            TypicalConfig,
            ["/music/Albums", "[Lossless]/music/FLAC", "/music/Incoming"]);

        Assert.Contains("- '/music/Incoming'", updated, StringComparison.Ordinal);

        // Everything the user configured by hand must still be there, byte for byte.
        Assert.Contains("username: Loaqx", updated, StringComparison.Ordinal);
        Assert.Contains("password: hunter2", updated, StringComparison.Ordinal);
        Assert.Contains("listen_port: 5030", updated, StringComparison.Ordinal);
        Assert.Contains("incomplete: /incomplete", updated, StringComparison.Ordinal);
        Assert.Contains("storage_mode: memory", updated, StringComparison.Ordinal);
        Assert.Contains("remote_configuration: false", updated, StringComparison.Ordinal);

        // The sibling keys inside shares must not be absorbed into the directories list.
        Assert.Contains("filters:", updated, StringComparison.Ordinal);
        Assert.Contains("cache:", updated, StringComparison.Ordinal);
        Assert.Equal(["/music/Albums", "[Lossless]/music/FLAC", "/music/Incoming"], SoulseekShareConfiguration.ReadDirectories(updated));
    }

    [Fact]
    public void ApplyDirectories_RemovesAPath()
    {
        var updated = SoulseekShareConfiguration.ApplyDirectories(TypicalConfig, ["/music/Albums"]);

        Assert.Equal(["/music/Albums"], SoulseekShareConfiguration.ReadDirectories(updated));
    }

    [Fact]
    public void ApplyDirectories_EmptyListEmptiesTheListRatherThanDeletingTheKey()
    {
        var updated = SoulseekShareConfiguration.ApplyDirectories(TypicalConfig, []);

        Assert.Contains("directories:", updated, StringComparison.Ordinal);
        Assert.Empty(SoulseekShareConfiguration.ReadDirectories(updated));
        Assert.Contains("username: Loaqx", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyDirectories_RoundTripsThroughReadDirectories()
    {
        var wanted = new[] { "/music/Albums", "[Lossless]/music/FLAC", "/mnt/John Doe's Music" };

        var once = SoulseekShareConfiguration.ApplyDirectories(TypicalConfig, wanted);
        var twice = SoulseekShareConfiguration.ApplyDirectories(once, wanted);

        Assert.Equal(once, twice);
        Assert.Equal(wanted, SoulseekShareConfiguration.ReadDirectories(once));
    }

    [Fact]
    public void ApplyDirectories_EscapesAnEmbeddedQuoteSoTheDocumentStillParses()
    {
        var updated = SoulseekShareConfiguration.ApplyDirectories(TypicalConfig, ["/mnt/John Doe's Music"]);

        Assert.Contains("John Doe''s Music", updated, StringComparison.Ordinal);
        Assert.Equal(["/mnt/John Doe's Music"], SoulseekShareConfiguration.ReadDirectories(updated));
    }

    [Fact]
    public void ApplyDirectories_CreatesTheSharesBlockWhenTheDocumentHasNone()
    {
        const string noShares = """
            instance_name: default
            soulseek:
              username: Loaqx
            """;

        var updated = SoulseekShareConfiguration.ApplyDirectories(noShares, ["/music"]);

        Assert.Equal(["/music"], SoulseekShareConfiguration.ReadDirectories(updated));
        Assert.Contains("username: Loaqx", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyDirectories_AddsTheListWhenSharesExistsWithoutOne()
    {
        const string sharesWithoutList = """
            shares:
              cache:
                storage_mode: memory
            """;

        var updated = SoulseekShareConfiguration.ApplyDirectories(sharesWithoutList, ["/music"]);

        Assert.Equal(["/music"], SoulseekShareConfiguration.ReadDirectories(updated));
        Assert.Contains("storage_mode: memory", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyDirectories_KeepsWindowsLineEndings()
    {
        var crlf = TypicalConfig.Replace("\n", "\r\n", StringComparison.Ordinal);

        var updated = SoulseekShareConfiguration.ApplyDirectories(crlf, ["/music/Albums"]);

        Assert.DoesNotContain("\n", updated.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("\r\n", updated, StringComparison.Ordinal);
        Assert.Equal(["/music/Albums"], SoulseekShareConfiguration.ReadDirectories(updated));
    }

    [Fact]
    public void ApplyDirectories_FillsAnExistingButEmptyList()
    {
        // Nothing to copy the author's indentation from here, so the editor has to choose a column itself.
        // Choosing the key's own column produces a sequence that is not a child of the key, which reads
        // back as no shares at all.
        const string emptyList = """
            shares:
              directories:
              filters:
                - '\.ini$'
            """;

        var updated = SoulseekShareConfiguration.ApplyDirectories(emptyList, ["/music"]);

        Assert.Equal(["/music"], SoulseekShareConfiguration.ReadDirectories(updated));
        Assert.Contains("filters:", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyDirectories_RefusesADirectoriesKeyThatIsNotANestedList()
    {
        // shares.directories written flat cannot be rewritten without guessing what it meant, and guessing
        // here would risk the credentials in the same document.
        const string malformed = """
            shares:
              directories: /music
            """;

        var error = Assert.Throws<SoulseekShareConfigurationException>(
            () => SoulseekShareConfiguration.ApplyDirectories(malformed, ["/music/Albums"]));

        Assert.Contains("cannot be rewritten safely", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyDirectories_RefusesAMalformedBlockListRatherThanCorruptingIt()
    {
        // An inline flow sequence is legal YAML but is not the shape this editor claims to understand.
        const string flowStyle = """
            shares:
              directories: ['/music/Albums']
            """;

        Assert.Throws<SoulseekShareConfigurationException>(
            () => SoulseekShareConfiguration.ApplyDirectories(flowStyle, ["/music/Albums"]));
    }

    [Fact]
    public void ReadDirectories_OnADocumentWithNoSharesBlockIsEmptyRatherThanThrowing()
    {
        // Reading is a reporting path used on every folder render, so a config that simply has no shares
        // yet is an ordinary state and must not be an error.
        Assert.Empty(SoulseekShareConfiguration.ReadDirectories("instance_name: default\n"));
    }

    [Fact]
    public void ReadDirectories_SkipsCommentsAndBlankLinesInsideTheList()
    {
        const string commented = """
            shares:
              directories:
                # the good stuff
                - '/music/Albums'

                - '/music/FLAC'
              cache:
                storage_mode: memory
            """;

        Assert.Equal(["/music/Albums", "/music/FLAC"], SoulseekShareConfiguration.ReadDirectories(commented));
    }

    [Fact]
    public void ReadDirectories_StopsAtTheNextKeySoASiblingIsNotReadAsAPath()
    {
        // Asserting the whole list, not just that two known values are absent: shares.filters and
        // shares.cache.cache sit at the very column the sequence stops at, so an off-by-one here would
        // report "filters:" as a shared path while still passing a test that only looked for the values.
        Assert.Equal(["/music/Albums", "[Lossless]/music/FLAC"], SoulseekShareConfiguration.ReadDirectories(TypicalConfig));
    }

    [Fact]
    public void ReadDirectories_ReportsWhatSlskdIsServingSoAnOutsideEditIsVisible()
    {
        // Someone editing slskd's own file must be visible here, or the next toggle would silently revert it.
        var edited = TypicalConfig.Replace("'/music/Albums'", "'/mnt/USB'", StringComparison.Ordinal);

        Assert.Equal(["/mnt/USB", "[Lossless]/music/FLAC"], SoulseekShareConfiguration.ReadDirectories(edited));
    }
}
