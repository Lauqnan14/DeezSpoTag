using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services.AutoTag;
using TagLib;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Exact provider identity boundaries in the shared pipeline: one provider pass may
/// overwrite only its own field family and must never touch another provider, another
/// field, or a generic compatibility field.
/// </summary>
public sealed class ProviderIdentityPipelineRegressionTest
{
    private const string ContaminatedReleaseId = "6guJZpZ52v4MrJKIH7tASl";
    private const string MusicBrainzReleaseId = "f67cd8b2-1ac6-4e21-8451-4d6a58eb0ee5";

    [Fact]
    public async Task MusicBrainzOverwrite_DoesNotDisturbForeignOrGenericIdentity()
    {
        using var fixture = await ProviderIdentityTestAudioFactory.CreateAsync(".flac");
        Assert.True(fixture.Available);
        var path = fixture.Path;

        Seed(path, "SPOTIFY_RELEASE_ID", ContaminatedReleaseId);
        Seed(path, "MUSICBRAINZ_RELEASE_ID", ContaminatedReleaseId);
        Seed(path, "ITUNES_RELEASE_ID", ContaminatedReleaseId);
        Seed(path, "ALBUMID", "preserve-generic");

        await ApplyMusicBrainz(path, MusicBrainzReleaseId, overwrite: true);

        Assert.Equal(MusicBrainzReleaseId, Read(path, "MUSICBRAINZ_RELEASE_ID"));
        Assert.Equal(ContaminatedReleaseId, Read(path, "SPOTIFY_RELEASE_ID"));
        Assert.Equal(ContaminatedReleaseId, Read(path, "ITUNES_RELEASE_ID"));
        Assert.Equal("preserve-generic", Read(path, "ALBUMID"));
    }

    [Fact]
    public async Task MissingAuthoritativeValue_LeavesEveryAliasUntouchedEvenWhenOverwriting()
    {
        using var fixture = await ProviderIdentityTestAudioFactory.CreateAsync(".flac");
        var path = fixture.Path;

        Seed(path, "MUSICBRAINZ_RELEASE_ID", ContaminatedReleaseId);
        Seed(path, "ALBUMID", "preserve-generic");

        // The provider returned a match but no release id at all.
        await Write(
            path,
            new ProviderIdentityPayload("musicbrainz", null, null, null, null, null, null, true),
            overwrite: true);

        Assert.Equal(ContaminatedReleaseId, Read(path, "MUSICBRAINZ_RELEASE_ID"));
        Assert.Equal("preserve-generic", Read(path, "ALBUMID"));
    }

    [Fact]
    public async Task EveryProviderFieldOverwrite_ChangesExactlyOneFamily()
    {
        string[] providers = ["spotify", "deezer", "itunes", "musicbrainz", "shazam"];
        var fields = Enum.GetValues<ProviderIdentityField>();

        foreach (var provider in providers)
        {
            foreach (var targetField in fields)
            {
                using var fixture = await ProviderIdentityTestAudioFactory.CreateAsync(".flac");
                var path = fixture.Path;

                var baseline = new Dictionary<ProviderIdentityField, string>();
                foreach (var field in fields)
                {
                    var family = AutoTagIdentityTags.ResolveFamily(provider, field);
                    var value = $"old-{provider}-{field}";
                    baseline[field] = value;
                    Seed(path, family.WriteNames[0], value);
                }

                Seed(path, "AMAZON_TRACK_ID", "foreign-amazon");
                Seed(path, "ALBUMID", "preserve-generic");

                var newValue = $"new-{provider}-{targetField}";
                await Write(
                    path,
                    PayloadFor(provider, targetField, newValue),
                    overwrite: true,
                    fields: new HashSet<ProviderIdentityField> { targetField });

                foreach (var field in fields)
                {
                    var family = AutoTagIdentityTags.ResolveFamily(provider, field);
                    var actual = Read(path, family.WriteNames[0]);
                    var expected = field == targetField ? newValue : baseline[field];
                    Assert.Equal(expected, actual);
                }

                Assert.Equal("foreign-amazon", Read(path, "AMAZON_TRACK_ID"));
                Assert.Equal("preserve-generic", Read(path, "ALBUMID"));
            }
        }
    }

    [Fact]
    public async Task NonNativePayload_NeverWritesProviderIdentityTags()
    {
        using var fixture = await ProviderIdentityTestAudioFactory.CreateAsync(".flac");
        var path = fixture.Path;

        await Write(
            path,
            new ProviderIdentityPayload("shazam", "shazam-track", "shazam-album", "shazam-release", null, null, null, false),
            overwrite: true);

        foreach (var field in Enum.GetValues<ProviderIdentityField>())
        {
            var family = AutoTagIdentityTags.ResolveFamily("shazam", field);
            Assert.All(family.WriteNames, name => Assert.Null(Read(path, name)));
        }

        Assert.Null(Read(path, "ALBUMID"));
    }

    [Fact]
    public async Task GenericCompatibilityFields_AreNeverWrittenByAnyProvider()
    {
        using var fixture = await ProviderIdentityTestAudioFactory.CreateAsync(".flac");
        var path = fixture.Path;

        await Write(
            path,
            new ProviderIdentityPayload(
                "musicbrainz",
                "track-id",
                "album-id",
                MusicBrainzReleaseId,
                "artist-id",
                "album-artist-id",
                "https://musicbrainz.org/recording/track-id",
                true),
            overwrite: true);

        foreach (var name in LocalAutoTagRunner.GenericIdentityCompatibilityFields)
        {
            Assert.Null(Read(path, name));
        }

        Assert.Equal(MusicBrainzReleaseId, Read(path, "MUSICBRAINZ_RELEASE_ID"));
        Assert.Equal("album-id", Read(path, "MUSICBRAINZ_ALBUMID"));
    }

    private static ProviderIdentityPayload PayloadFor(string provider, ProviderIdentityField field, string value)
        => new(
            provider,
            field == ProviderIdentityField.TrackId ? value : null,
            field == ProviderIdentityField.AlbumId ? value : null,
            field == ProviderIdentityField.ReleaseId ? value : null,
            field == ProviderIdentityField.ArtistId ? value : null,
            field == ProviderIdentityField.AlbumArtistId ? value : null,
            field == ProviderIdentityField.Url ? value : null,
            true);

    private static Task ApplyMusicBrainz(string path, string releaseId, bool overwrite)
        => Write(
            path,
            new ProviderIdentityPayload("musicbrainz", null, null, releaseId, null, null, null, true),
            overwrite,
            fields: new HashSet<ProviderIdentityField> { ProviderIdentityField.ReleaseId });

    internal static async Task Write(
        string path,
        ProviderIdentityPayload payload,
        bool overwrite,
        IReadOnlySet<ProviderIdentityField>? fields = null)
    {
        var config = CreateConfig(overwrite);
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "WriteProviderIdentityAsync",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("WriteProviderIdentityAsync not found.");
        var task = (Task)method.Invoke(
            null,
            [path, payload, config, fields ?? Enum.GetValues<ProviderIdentityField>().ToHashSet(), CancellationToken.None])!;
        await task;
    }

    private static object CreateConfig(bool overwrite)
    {
        var type = typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunnerConfig", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AutoTagRunnerConfig not found.");
        var config = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("AutoTagRunnerConfig could not be created.");
        type.GetProperty("Overwrite")!.SetValue(config, overwrite);
        return config;
    }

    internal static void Seed(string path, string rawName, string value)
    {
        using var file = TagLib.File.Create(path);
        var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true);
        xiph.SetField(rawName, [value]);
        file.Save();
    }

    internal static string? Read(string path, string rawName)
        => LocalAutoTagRunner.ReadRawIdentityValue(path, rawName);
}