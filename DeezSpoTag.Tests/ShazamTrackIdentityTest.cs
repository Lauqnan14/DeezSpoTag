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
/// Shazam's track id is the recording id AutoTag stores. Selecting either Track ID or
/// Recording ID writes SHAZAM_TRACK_ID. An id-first match keeps that Shazam id and does
/// not adopt the Deezer or Spotify id it used to find the metadata.
/// </summary>
public sealed class ShazamTrackIdentityTest
{
    private const string ShazamTrackId = "shazam-track-id";
    private const string ShazamUrl = "https://www.shazam.com/track/shazam-track-id";
    private const string SpotifyTrackId = "0VjIjW4GlUZAMYd2vXMi3b";

    [Fact]
    public void IdFirstMatch_KeepsTheShazamTrackIdAndDropsTheResolverIdentity()
    {
        var match = new AutoTagMatchResult
        {
            Track = new AutoTagTrack
            {
                TrackId = SpotifyTrackId,
                ReleaseId = "spotify-album",
                ArtistId = "spotify-artist",
                AlbumArtistId = "spotify-album-artist",
                Url = "https://open.spotify.com/track/" + SpotifyTrackId,
                Other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SPOTIFY_TRACK_ID"] = [SpotifyTrackId]
                }
            }
        };
        var source = new AutoTagAudioInfo
        {
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["SHAZAM_TRACK_ID"] = [ShazamTrackId],
                ["SHAZAM_URL"] = [ShazamUrl]
            }
        };

        var prepared = (AutoTagMatchResult)Invoke(
            "PrepareShazamIdFirstMatch",
            [match, "spotify", source])!;
        var payload = Capture(prepared);

        Assert.True(payload.IsNativeProviderResult);
        Assert.Equal("shazam", payload.ProviderId);
        Assert.Equal(ShazamTrackId, payload.TrackId);
        Assert.Equal(ShazamUrl, payload.Url);
        Assert.Null(payload.ReleaseId);
        Assert.Null(payload.ArtistId);
        Assert.Null(payload.AlbumId);
        Assert.Null(payload.AlbumArtistId);
    }

    [Fact]
    public void ForeignResolverId_IsNotStoredAsTheShazamTrackId()
    {
        var match = new AutoTagMatchResult
        {
            Track = new AutoTagTrack
            {
                TrackId = SpotifyTrackId,
                ArtistId = "spotify-artist",
                Url = "https://open.spotify.com/track/" + SpotifyTrackId,
                Other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SHAZAM_MATCH_PROVIDER"] = ["SPOTIFY"],
                    ["SHAZAM_TRACK_ID"] = ["different-shazam-id"]
                }
            }
        };

        var payload = Capture(match);

        Assert.False(payload.IsNativeProviderResult);
        Assert.Null(payload.TrackId);
        Assert.Null(payload.Url);
        Assert.Null(payload.ArtistId);
    }

    [Fact]
    public void RecordingIdSelection_EnablesOnlyTheProviderTrackIdField()
    {
        var recordingOnly = EnabledFields("recordingId");
        var trackOnly = EnabledFields("trackId");
        var both = EnabledFields("recordingId", "albumId");
        var neither = EnabledFields("title");

        Assert.Equal(new[] { ProviderIdentityField.TrackId }, recordingOnly.OrderBy(field => field).ToArray());
        Assert.Equal(new[] { ProviderIdentityField.TrackId }, trackOnly.OrderBy(field => field).ToArray());
        Assert.Equal(
            new[] { ProviderIdentityField.TrackId, ProviderIdentityField.AlbumId },
            both.OrderBy(field => field).ToArray());
        Assert.Empty(neither);
    }

    [Fact]
    public void ReturnedTags_ShowRecordingIdWhenTheProviderTrackIdIsPresent()
    {
        var payload = new ProviderIdentityPayload(
            "shazam",
            ShazamTrackId,
            null,
            null,
            null,
            null,
            ShazamUrl,
            true);
        var track = new AutoTagTrack { TrackId = ShazamTrackId, RecordingId = ShazamTrackId, Url = ShazamUrl };

        var shown = ReturnedTags(track, payload, SupportedTag.RecordingId);
        var hidden = ReturnedTags(track, payload, SupportedTag.Title);

        Assert.Contains(SupportedTag.RecordingId, shown);
        Assert.DoesNotContain(SupportedTag.RecordingId, hidden);
    }

    [Fact]
    public async Task RecordingIdSelection_WritesTheShazamTrackIdAndLeavesGenericRecordingIdAlone()
    {
        using var fixture = await ProviderIdentityTestAudioFactory.CreateAsync(".flac");
        Assert.True(fixture.Available);

        var config = Config(overwrite: true, "recordingId");
        var result = await Write(
            fixture.Path,
            new ProviderIdentityPayload("shazam", ShazamTrackId, "album", "release", "artist", "album-artist", ShazamUrl, true),
            config,
            EnabledFields(config));

        Assert.Empty(Failures(result));
        Assert.Contains(SupportedTag.TrackId, Attempted(result));
        Assert.Contains(SupportedTag.RecordingId, Attempted(result));
        Assert.Equal(ShazamTrackId, LocalAutoTagRunner.ReadRawIdentityValue(fixture.Path, "SHAZAM_TRACK_ID"));
        Assert.Null(LocalAutoTagRunner.ReadRawIdentityValue(fixture.Path, "SHAZAM_ALBUM_ID"));
        Assert.Null(LocalAutoTagRunner.ReadRawIdentityValue(fixture.Path, "RECORDINGID"));
        Assert.True(HasTag(fixture.Path, SupportedTag.RecordingId, "shazam"));
        Assert.True(HasTag(fixture.Path, SupportedTag.TrackId, "shazam"));
        Assert.False(HasTag(fixture.Path, SupportedTag.RecordingId, "spotify"));
    }

    [Fact]
    public async Task RecordingIdSelection_WritesTheMusicBrainzRecordingIdWithoutTheGenericField()
    {
        using var fixture = await ProviderIdentityTestAudioFactory.CreateAsync(".flac");
        Assert.True(fixture.Available);

        const string recordingId = "cb91b696-c25a-42e4-9338-4ce493b401c6";
        var config = Config(overwrite: true, "recordingId");
        var result = await Write(
            fixture.Path,
            new ProviderIdentityPayload("musicbrainz", recordingId, null, null, null, null, null, true)
            {
                MediumTrackId = "11111111-1111-1111-1111-111111111111"
            },
            config,
            EnabledFields(config));

        Assert.Empty(Failures(result));
        Assert.Equal(recordingId, LocalAutoTagRunner.ReadRawIdentityValue(fixture.Path, "MUSICBRAINZ_TRACK_ID"));
        Assert.Equal(recordingId, LocalAutoTagRunner.ReadRawIdentityValue(fixture.Path, "MUSICBRAINZ_RECORDING_ID"));
        Assert.Equal("11111111-1111-1111-1111-111111111111", LocalAutoTagRunner.ReadRawIdentityValue(fixture.Path, "MUSICBRAINZ_TRACKID"));
        Assert.Null(LocalAutoTagRunner.ReadRawIdentityValue(fixture.Path, "RECORDINGID"));
        Assert.True(HasTag(fixture.Path, SupportedTag.RecordingId, "musicbrainz"));
    }

    [Fact]
    public async Task RecordingIdOverwrite_ReplacesTheStoredShazamTrackId()
    {
        using var fixture = await ProviderIdentityTestAudioFactory.CreateAsync(".flac");
        Assert.True(fixture.Available);

        var first = Config(overwrite: true, "recordingId");
        await Write(
            fixture.Path,
            new ProviderIdentityPayload("shazam", "old-shazam-id", null, null, null, null, null, true),
            first,
            EnabledFields(first));

        var second = Config(overwrite: false, "recordingId");
        SetOverwriteTags(second, "recordingId");
        await Write(
            fixture.Path,
            new ProviderIdentityPayload("shazam", "new-shazam-id", null, null, null, null, null, true),
            second,
            EnabledFields(second));

        Assert.Equal("new-shazam-id", LocalAutoTagRunner.ReadRawIdentityValue(fixture.Path, "SHAZAM_TRACK_ID"));
        Assert.Null(LocalAutoTagRunner.ReadRawIdentityValue(fixture.Path, "RECORDINGID"));
    }

    private static ProviderIdentityPayload Capture(AutoTagMatchResult match)
        => Assert.IsType<ProviderIdentityPayload>(Invoke("CaptureProviderIdentity", ["shazam", match]));

    private static HashSet<ProviderIdentityField> EnabledFields(params string[] tags)
        => EnabledFields(Config(overwrite: false, tags));

    private static HashSet<ProviderIdentityField> EnabledFields(object config)
        => Assert.IsType<HashSet<ProviderIdentityField>>(Invoke("ResolveEnabledProviderIdentityFields", [config]));

    private static HashSet<SupportedTag> ReturnedTags(
        AutoTagTrack track,
        ProviderIdentityPayload payload,
        params SupportedTag[] eligible)
    {
        var planType = typeof(LocalAutoTagRunner).GetNestedType("ProviderTagPlan", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ProviderTagPlan was not found.");
        var selected = eligible.ToHashSet();
        var plan = Activator.CreateInstance(planType, selected, selected, new HashSet<SupportedTag>());
        return Assert.IsType<HashSet<SupportedTag>>(Invoke("ResolveReturnedEligibleTags", [track, plan!, payload]));
    }

    private static bool HasTag(string path, SupportedTag tag, string platformId)
    {
        using var file = TagLib.File.Create(path);
        return Assert.IsType<bool>(Invoke(
            "HasTag",
            [file, System.IO.Path.GetExtension(path), tag, Config(overwrite: false), platformId]));
    }

    private static async Task<object> Write(
        string path,
        ProviderIdentityPayload payload,
        object config,
        IReadOnlySet<ProviderIdentityField> fields)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "WriteProviderIdentityAsync",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("WriteProviderIdentityAsync was not found.");
        var task = (Task)method.Invoke(null, [path, payload, config, fields, CancellationToken.None])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static object Config(bool overwrite, params string[] tags)
    {
        var type = typeof(LocalAutoTagRunner).GetNestedType("AutoTagRunnerConfig", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AutoTagRunnerConfig was not found.");
        var config = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("AutoTagRunnerConfig could not be created.");
        type.GetProperty("Overwrite")!.SetValue(config, overwrite);
        type.GetProperty("Tags")!.SetValue(config, tags.ToList());
        return config;
    }

    private static void SetOverwriteTags(object config, params string[] tags)
    {
        config.GetType().GetProperty("OverwriteTags")!.SetValue(config, tags.ToList());
    }

    private static IReadOnlyList<object> Failures(object result)
    {
        var failures = (System.Collections.IEnumerable)result.GetType().GetProperty("Failures")!.GetValue(result)!;
        return failures.Cast<object>().ToList();
    }

    private static HashSet<SupportedTag> Attempted(object result)
        => Assert.IsType<HashSet<SupportedTag>>(result.GetType().GetProperty("AttemptedTags")!.GetValue(result));

    private static object? Invoke(string name, object?[] arguments)
    {
        var method = typeof(LocalAutoTagRunner)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(candidate => candidate.Name == name && candidate.GetParameters().Length == arguments.Length);
        return method.Invoke(null, arguments);
    }
}
