using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MusicBrainzImplementationTest
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"preferred_release_countries\":null}")]
    [InlineData("{\"preferred_release_countries\":\"\"}")]
    [InlineData("{\"preferred_release_countries\":\"   \"}")]
    [InlineData("{\"preferred_release_countries\":\"FR,US\"}")]
    public void CountryPreferences_DefaultUnsetValuesAndPreserveConfiguredOrder(string json)
    {
        var config = JsonSerializer.Deserialize<MusicBrainzMatchConfig>(json)!;
        var preferences = CreatePreferences(config);
        var countries = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            preferences.GetType().GetProperty("PreferredCountries")!.GetValue(preferences));
        Assert.Equal(json.Contains("FR,US", StringComparison.Ordinal) ? new[] { "FR", "US" } : new[] { "US" }, countries);
    }

    [Fact]
    public void CountryUiDefaults_FillUnsetValuesAndPreserveOtherSettings()
    {
        var source = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var start = source.IndexOf("function ensurePlatformOptionDefaults(", StringComparison.Ordinal);
        var end = source.IndexOf("    // Order here", start, StringComparison.Ordinal);
        var script = "const assert = require('node:assert/strict'); let state;\n" + source[start..end] + """
            const options = [{ id: 'preferred_release_countries', value: { value: 'US' } }];
            for (const custom of [{}, { musicbrainz: {} }, { musicbrainz: { preferred_release_countries: null } },
                { musicbrainz: { preferred_release_countries: '' } }, { musicbrainz: { preferred_release_countries: '   ' } }]) {
                state = { config: { custom } };
                ensurePlatformOptionDefaults('musicbrainz', options);
                assert.equal(state.config.custom.musicbrainz.preferred_release_countries, 'US');
            }
            for (const value of ['FR,US', ' US,GB ', 'FR']) {
                state = { config: { custom: { musicbrainz: { preferred_release_countries: value } } } };
                ensurePlatformOptionDefaults('musicbrainz', options);
                assert.equal(state.config.custom.musicbrainz.preferred_release_countries, value);
            }
            state = { config: { custom: { other: { preferred_release_countries: '' } } } };
            ensurePlatformOptionDefaults('other', options);
            assert.equal(state.config.custom.other.preferred_release_countries, '');
            """;
        var info = new ProcessStartInfo("node") { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(info)!;
        process.StandardInput.Write(script);
        process.StandardInput.Close();
        var errors = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10000), "Node defaulting test timed out.");
        Assert.True(process.ExitCode == 0, errors);
    }

    [Fact]
    public void CountryOption_DefaultsToUsAndExplainsOrderedSelection()
    {
        var descriptor = new MusicBrainzPlatform(new StubWebHostEnvironment()).Describe();
        var option = Assert.Single(descriptor.Platform.CustomOptions.Options, o => o.Id == "preferred_release_countries");
        Assert.Equal("US", Assert.IsType<PlatformCustomOptionString>(option.Value).Value);
        Assert.Contains("first", option.Tooltip!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exhausted", option.Tooltip!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("US", option.Tooltip!, StringComparison.Ordinal);
    }

    [Fact]
    public void Descriptor_AdvertisesExistingSharedTagsMusicBrainzCanPopulate()
    {
        var platform = new MusicBrainzPlatform(new StubWebHostEnvironment());

        var descriptor = platform.Describe();

        Assert.Contains(SupportedTag.AlbumArt, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.DiscNumber, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.ReleaseDate, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.OtherTags, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.RecordingId, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.ArtistId, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.AlbumArtistId, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.ReleaseGroupId, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.AlbumId, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.ReleaseStatus, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.ReleaseCountry, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.Barcode, descriptor.SupportedTags);
        Assert.Contains(SupportedTag.Media, descriptor.SupportedTags);
    }

    [Fact]
    public void PicardStyleMetadata_IsExposedAsGenericTagTogglesAndWrites()
    {
        var repoRoot = ResolveRepoRoot();
        var autoTagJs = File.ReadAllText(Path.Combine(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "autotag.js"));
        var runner = PartialSourceReader.ReadTypeSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var matcher = File.ReadAllText(Path.Combine(repoRoot, "DeezSpoTag.Web", "Services", "AutoTag", "MusicBrainzMatcher.cs"));
        var canonicalizer = File.ReadAllText(Path.Combine(repoRoot, "DeezSpoTag.Web", "Services", "TaggingProfileCanonicalizer.cs"));
        var downloadConverter = File.ReadAllText(Path.Combine(repoRoot, "DeezSpoTag.Web", "Services", "DownloadTagSettingsConverter.cs"));
        var audioTagger = File.ReadAllText(Path.Combine(repoRoot, "DeezSpoTag.Services", "Download", "Utils", "AudioTagger.cs"));
        var deezerPlatform = File.ReadAllText(Path.Combine(repoRoot, "DeezSpoTag.Web", "Services", "AutoTag", "DeezerPlatform.cs"));
        var spotifyPlatform = File.ReadAllText(Path.Combine(repoRoot, "DeezSpoTag.Web", "Services", "AutoTag", "SpotifyPlatform.cs"));
        var itunesPlatform = File.ReadAllText(Path.Combine(repoRoot, "DeezSpoTag.Web", "Services", "AutoTag", "ITunesPlatform.cs"));

        foreach (var tag in new[]
        {
            "recordingId",
            "artistId",
            "albumArtistId",
            "releaseGroupId",
            "albumId",
            "releaseStatus",
            "releaseCountry",
            "barcode",
            "media"
        })
        {
            Assert.Contains($"tag: \"{tag}\"", autoTagJs, StringComparison.Ordinal);
            Assert.Contains($"\"{tag}\"", autoTagJs, StringComparison.Ordinal);
            Assert.Contains($"new(\"{tag}\"", canonicalizer, StringComparison.Ordinal);
        }

        Assert.Contains("RecordingId = recording.Id", matcher, StringComparison.Ordinal);
        Assert.Contains("ReleaseGroupId = release.ReleaseGroup.Id", matcher, StringComparison.Ordinal);
        Assert.Contains("track.ReleaseStatus = release.Status", matcher, StringComparison.Ordinal);
        Assert.Contains("track.ReleaseCountry = release.Country", matcher, StringComparison.Ordinal);
        Assert.Contains("track.Media = release.Media", matcher, StringComparison.Ordinal);

        Assert.Contains("private const string RecordingIdRawTag = \"RECORDINGID\";", runner, StringComparison.Ordinal);
        // The generic compatibility fields are preserved, never written by a provider pass:
        // MusicBrainz publishes its recording identity through its own provider family, and
        // the release-group/media writes keep their own descriptive pipeline.
        Assert.DoesNotContain("WriteSingleRawTag(tagWriteContext, context, RecordingIdTag", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("AddSingleValueCustomTagWrite(writes, RecordingIdTag", runner, StringComparison.Ordinal);
        Assert.Contains("MUSICBRAINZ_TRACK_ID", runner, StringComparison.Ordinal);
        Assert.Contains("AddSingleValueCustomTagWrite(writes, ReleaseGroupIdTag, SupportedTag.ReleaseGroupId, ReleaseGroupIdRawTag, track.ReleaseGroupId);", runner, StringComparison.Ordinal);
        Assert.Contains("AddSingleValueCustomTagWrite(writes, ReleaseGroupIdTag, SupportedTag.ReleaseGroupId, \"MUSICBRAINZ_RELEASEGROUPID\", track.ReleaseGroupId);", runner, StringComparison.Ordinal);
        Assert.Contains("new CustomTagWrite(MediaTag, SupportedTag.Media, MediaRawTag, track.Media.ToList())", runner, StringComparison.Ordinal);
        Assert.Contains("FirstClassRawOtherTags", runner, StringComparison.Ordinal);

        Assert.Contains("RecordingId = UsesDownload(config.RecordingId)", downloadConverter, StringComparison.Ordinal);
        Assert.Contains("AlbumId = UsesDownload(config.AlbumId)", downloadConverter, StringComparison.Ordinal);
        Assert.Contains("SetCustomFrameIfPresent(tag, \"TXXX\", RecordingIdUpperTag", audioTagger, StringComparison.Ordinal);
        Assert.Contains("SetVorbisCommentIf(tag, save.RecordingId, RecordingIdUpperTag", audioTagger, StringComparison.Ordinal);
        Assert.Contains("SetAtlAdditionalFieldIf(file, save.RecordingId, RecordingIdUpperTag", audioTagger, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveMetadataValue", audioTagger, StringComparison.Ordinal);
        Assert.Contains("\"recordingId\"", deezerPlatform, StringComparison.Ordinal);
        Assert.Contains("\"artistId\"", deezerPlatform, StringComparison.Ordinal);
        Assert.Contains("\"albumArtistId\"", deezerPlatform, StringComparison.Ordinal);
        Assert.DoesNotContain("\"releaseGroupId\"", deezerPlatform, StringComparison.Ordinal);
        Assert.DoesNotContain("\"releaseStatus\"", deezerPlatform, StringComparison.Ordinal);
        Assert.DoesNotContain("\"releaseCountry\"", deezerPlatform, StringComparison.Ordinal);
        Assert.DoesNotContain("\"media\"", deezerPlatform, StringComparison.Ordinal);
        Assert.Contains("\"recordingId\"", spotifyPlatform, StringComparison.Ordinal);
        Assert.Contains("\"albumId\"", spotifyPlatform, StringComparison.Ordinal);
        Assert.Contains("\"recordingId\"", itunesPlatform, StringComparison.Ordinal);
        Assert.Contains("\"artistId\"", itunesPlatform, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Sweet Love", "Sweet Love (Instrumental)")]
    [InlineData("Sweet Love", "Sweet Love - Radio Edit")]
    [InlineData("One Night", "One Night (Extended)")]
    [InlineData("Sweet Love", "Sweet Love (Extended Mix)")]
    [InlineData("Sweet Love (Live)", "Sweet Love")]
    public void VariantGuard_RejectsCandidateWithDifferentVersionIntent(string sourceTitle, string candidateTitle)
    {
        Assert.False(MusicBrainzMatcher.IsVariantCompatible(sourceTitle, candidateTitle));
    }

    [Theory]
    [InlineData("Sweet Love", "Sweet Love")]
    [InlineData("Sweet Love (Instrumental)", "Sweet Love - Instrumental")]
    [InlineData("Sweet Love (Radio Edit)", "Sweet Love - Radio Version")]
    [InlineData("One Night (Extended)", "One Night - Extended")]
    [InlineData("Sweet Love (Extended Mix)", "Sweet Love - Extended Version")]
    public void VariantGuard_AllowsSameVersionIntent(string sourceTitle, string candidateTitle)
    {
        Assert.True(MusicBrainzMatcher.IsVariantCompatible(sourceTitle, candidateTitle));
    }

    [Theory]
    [InlineData("Hold Me Close", "Hold Me Closer")]
    [InlineData("Close", "Closer")]
    [InlineData("The One", "The Ones")]
    public void CandidateGuard_RejectsNearMissAlternativeTitleFromSameArtist(string sourceTitle, string candidateTitle)
    {
        var method = typeof(MusicBrainzMatcher).GetMethod("IsCandidateCompatibleWithSource", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MusicBrainzMatcher.IsCandidateCompatibleWithSource not found.");
        var info = new AutoTagAudioInfo
        {
            Title = sourceTitle,
            Artist = "Same Artist",
            Artists = ["Same Artist"]
        };
        var candidate = new MusicBrainzTrack
        {
            Title = candidateTitle,
            Artists = ["Same Artist"]
        };
        var config = new AutoTagMatchingConfig
        {
            Strictness = 0.7,
            MatchDuration = false,
            MaxDurationDifferenceSeconds = 4
        };

        var compatible = (bool)method.Invoke(null, [info, candidate, config, new MusicBrainzMatchConfig()])!;

        Assert.False(compatible);
    }

    [Fact]
    public void CandidateGuard_RejectsSameArtistDifferentShortTitle()
    {
        var method = typeof(MusicBrainzMatcher).GetMethod("IsCandidateCompatibleWithSource", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MusicBrainzMatcher.IsCandidateCompatibleWithSource not found.");
        var info = new AutoTagAudioInfo
        {
            Title = "Hey Girl",
            Artist = "Same Artist",
            Artists = ["Same Artist"]
        };
        var candidate = new MusicBrainzTrack
        {
            Title = "She's Hot",
            Artists = ["Same Artist"]
        };
        var config = new AutoTagMatchingConfig
        {
            Strictness = 0.7,
            MatchDuration = false,
            MaxDurationDifferenceSeconds = 4
        };

        var compatible = (bool)method.Invoke(null, [info, candidate, config, new MusicBrainzMatchConfig()])!;

        Assert.False(compatible);
    }

    [Theory]
    [InlineData("Hey Girl", "Hey, Girl")]
    [InlineData("She's Hot", "Shes Hot")]
    [InlineData("Sweet Love (Radio Edit)", "Sweet Love - Radio Version")]
    public void CandidateGuard_AllowsEquivalentTitles(string sourceTitle, string candidateTitle)
    {
        var method = typeof(MusicBrainzMatcher).GetMethod("IsCandidateCompatibleWithSource", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MusicBrainzMatcher.IsCandidateCompatibleWithSource not found.");
        var info = new AutoTagAudioInfo
        {
            Title = sourceTitle,
            Artist = "Same Artist",
            Artists = ["Same Artist"]
        };
        var candidate = new MusicBrainzTrack
        {
            Title = candidateTitle,
            Artists = ["Same Artist"]
        };
        var config = new AutoTagMatchingConfig
        {
            Strictness = 0.7,
            MatchDuration = false,
            MaxDurationDifferenceSeconds = 4
        };

        var compatible = (bool)method.Invoke(null, [info, candidate, config, new MusicBrainzMatchConfig()])!;

        Assert.True(compatible);
    }

    [Fact]
    public async Task SearchAsync_UsesConfiguredLimitInRequest()
    {
        Uri? capturedUri = null;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"recordings":[]}""")
            };
        }));
        var client = new MusicBrainzClient(httpClient, NullLogger<MusicBrainzClient>.Instance);

        _ = await client.SearchAsync("artist title", 7, CancellationToken.None);

        Assert.NotNull(capturedUri);
        Assert.Contains("limit=7", capturedUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_DoesNotSwallowCancellation()
    {
        using var httpClient = new HttpClient(new ThrowingHttpMessageHandler(new OperationCanceledException()));
        var client = new MusicBrainzClient(httpClient, NullLogger<MusicBrainzClient>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            client.SearchAsync("artist title", 5, CancellationToken.None));
    }

    [Fact]
    public void BuildOtherDictionary_MergesDuplicateRawKeys()
    {
        var method = typeof(MusicBrainzMatcher).GetMethod("BuildOtherDictionary", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MusicBrainzMatcher.BuildOtherDictionary not found.");
        var track = new MusicBrainzTrack
        {
            Other =
            [
                ("MUSICBRAINZ_RELEASEGROUPID", ["release-group-1"]),
                ("MUSICBRAINZ_RELEASEGROUPID", ["release-group-1", "release-group-2"])
            ]
        };

        var result = (System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>)method.Invoke(null, [track])!;

        Assert.True(result.TryGetValue("MUSICBRAINZ_RELEASEGROUPID", out var values));
        Assert.Equal(["release-group-1", "release-group-2"], values);
    }

    [Fact]
    public void MusicBrainzMatchConfig_Defaults_AreStrictness65AndDuration45()
    {
        var config = new MusicBrainzMatchConfig();

        Assert.Equal(65, config.MinStrictness);
        Assert.Equal(45, config.MinDurationDifferenceSeconds);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(65, false)]
    [InlineData(98, false)]
    [InlineData(99, false)]
    [InlineData(100, false)]
    public void CandidateGuard_StrictnessFloorStillAppliesAtDefault(int minStrictness, bool expectedWhenGlobalDroppedToZero)
    {
        var method = typeof(MusicBrainzMatcher).GetMethod("IsCandidateCompatibleWithSource", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MusicBrainzMatcher.IsCandidateCompatibleWithSource not found.");
        var info = new AutoTagAudioInfo
        {
            Title = "Same Title",
            Artist = "Same Artist",
            Artists = ["Same Artist"],
            DurationSeconds = 200
        };
        var candidate = new MusicBrainzTrack
        {
            Title = "Same Title",
            Artists = ["Different Artist"],
            Duration = TimeSpan.FromSeconds(200)
        };
        var config = new AutoTagMatchingConfig
        {
            Strictness = 0,
            MatchDuration = false,
            MaxDurationDifferenceSeconds = 4
        };
        var provider = new MusicBrainzMatchConfig { MinStrictness = minStrictness };

        var compatible = (bool)method.Invoke(null, [info, candidate, config, provider])!;

        Assert.Equal(expectedWhenGlobalDroppedToZero, compatible);
    }

    [Theory]
    [InlineData(65, true)]
    [InlineData(99, false)]
    [InlineData(100, false)]
    public void CandidateGuard_HonorsConfiguredStrictnessFloor(int minStrictness, bool expected)
    {
        var method = typeof(MusicBrainzMatcher).GetMethod("IsCandidateCompatibleWithSource", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MusicBrainzMatcher.IsCandidateCompatibleWithSource not found.");
        var info = new AutoTagAudioInfo
        {
            Title = "Same Title",
            Artist = "Same Artist",
            Artists = ["Same Artist"]
        };
        var candidate = new MusicBrainzTrack
        {
            Title = "Same Title",
            Artists = ["Some Artist"]
        };
        var config = new AutoTagMatchingConfig
        {
            Strictness = 0.7,
            MatchDuration = false,
            MaxDurationDifferenceSeconds = 4
        };
        var provider = new MusicBrainzMatchConfig { MinStrictness = minStrictness };

        var compatible = (bool)method.Invoke(null, [info, candidate, config, provider])!;

        Assert.Equal(expected, compatible);
    }

    [Fact]
    public void CandidateGuard_Floor100_RejectsMismatchingArtistInsteadOfThrowing()
    {
        var method = typeof(MusicBrainzMatcher).GetMethod("IsCandidateCompatibleWithSource", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MusicBrainzMatcher.IsCandidateCompatibleWithSource not found.");
        var info = new AutoTagAudioInfo
        {
            Title = "Same Title",
            Artist = "Same Artist",
            Artists = ["Same Artist"]
        };
        var candidate = new MusicBrainzTrack
        {
            Title = "Same Title",
            Artists = ["Different Artist"]
        };
        var config = new AutoTagMatchingConfig
        {
            Strictness = 0.7,
            MatchDuration = false,
            MaxDurationDifferenceSeconds = 4
        };

        var compatible = (bool)method.Invoke(null, [info, candidate, config, new MusicBrainzMatchConfig { MinStrictness = 100 }])!;

        Assert.False(compatible);
    }

    [Fact]
    public void CandidateGuard_ExactArtist_StillAcceptedAtFloor100()
    {
        var method = typeof(MusicBrainzMatcher).GetMethod("IsCandidateCompatibleWithSource", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MusicBrainzMatcher.IsCandidateCompatibleWithSource not found.");
        var info = new AutoTagAudioInfo
        {
            Title = "Same Title",
            Artist = "Same Artist",
            Artists = ["Same Artist"]
        };
        var candidate = new MusicBrainzTrack
        {
            Title = "Same Title",
            Artists = ["Same Artist"]
        };
        var config = new AutoTagMatchingConfig
        {
            Strictness = 0.7,
            MatchDuration = false,
            MaxDurationDifferenceSeconds = 4
        };

        var compatible = (bool)method.Invoke(null, [info, candidate, config, new MusicBrainzMatchConfig { MinStrictness = 100 }])!;

        Assert.True(compatible);
    }

    [Fact]
    public void CandidateGuard_ConfiguredDurationFloor_CombinesWithGlobalLimit()
    {
        var method = typeof(MusicBrainzMatcher).GetMethod("IsCandidateCompatibleWithSource", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MusicBrainzMatcher.IsCandidateCompatibleWithSource not found.");
        var info = new AutoTagAudioInfo
        {
            Title = "Same Title",
            Artist = "Same Artist",
            Artists = ["Same Artist"],
            DurationSeconds = 200
        };
        var candidate = new MusicBrainzTrack
        {
            Title = "Same Title",
            Artists = ["Same Artist"],
            Duration = TimeSpan.FromSeconds(100)
        };
        var config = new AutoTagMatchingConfig
        {
            Strictness = 0.7,
            MatchDuration = false,
            MaxDurationDifferenceSeconds = 40
        };

        var shorter = new MusicBrainzTrack
        {
            Title = "Same Title",
            Artists = ["Same Artist"],
            Duration = TimeSpan.FromSeconds(60)
        };
        var defaultUnbounded = (bool)method.Invoke(null, [info, candidate, config, new MusicBrainzMatchConfig()])!;
        var widerFloor = (bool)method.Invoke(null, [info, candidate, config, new MusicBrainzMatchConfig { MinDurationDifferenceSeconds = 120 }])!;
        var outsideCombinedWindow = (bool)method.Invoke(null, [info, shorter, config, new MusicBrainzMatchConfig { MinDurationDifferenceSeconds = 120 }])!;

        Assert.False(defaultUnbounded);
        Assert.True(widerFloor);
        Assert.False(outsideCombinedWindow);
    }

    [Fact]
    public void MatchTracks_RankingDurationWindow_CombinesWithConfiguredFloor()
    {
        var method = typeof(MusicBrainzMatcher).GetMethod("MatchTracks", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MusicBrainzMatcher.MatchTracks not found.");
        var info = new AutoTagAudioInfo
        {
            Title = "Same Title",
            Artist = "Same Artist",
            Artists = ["Same Artist"],
            DurationSeconds = 200
        };
        var tracks = new List<MusicBrainzTrack>
        {
            new()
            {
                Title = "Same Title",
                Artists = ["Same Artist"],
                Duration = TimeSpan.FromSeconds(180)
            }
        };
        var config = new AutoTagMatchingConfig
        {
            Strictness = 0.7,
            MatchDuration = true,
            MaxDurationDifferenceSeconds = 5
        };

        var widened = CountRanked(method.Invoke(null, [info, tracks, config, new MusicBrainzMatchConfig { MinDurationDifferenceSeconds = 45 }]));
        var narrowed = CountRanked(method.Invoke(null, [info, tracks, config, new MusicBrainzMatchConfig { MinDurationDifferenceSeconds = 0 }]));

        Assert.True(widened > narrowed, $"Expected the configured floor to widen ranking admission (widened={widened}, narrowed={narrowed}).");

        static int CountRanked(object? ranked)
            => ((System.Collections.ICollection)ranked!).Count;
    }

    [Fact]
    public void MatchTracks_RankingStrictness_IgnoresProviderArtistFloor()
    {
        var method = typeof(MusicBrainzMatcher).GetMethod("MatchTracks", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MusicBrainzMatcher.MatchTracks not found.");
        var info = new AutoTagAudioInfo
        {
            Title = "Same Title",
            Artist = "Same Artist",
            Artists = ["Same Artist"],
            DurationSeconds = 200
        };
        var tracks = new List<MusicBrainzTrack>
        {
            new()
            {
                Title = "Same Title",
                Artists = ["Same Artist"],
                Duration = TimeSpan.FromSeconds(200)
            }
        };
        var config = new AutoTagMatchingConfig
        {
            Strictness = 0.7,
            MatchDuration = true,
            MaxDurationDifferenceSeconds = 45
        };

        var withDefaultFloor = CountRanked(method.Invoke(null, [info, tracks, config, new MusicBrainzMatchConfig()]));
        var withMaxFloor = CountRanked(method.Invoke(null, [info, tracks, config, new MusicBrainzMatchConfig { MinStrictness = 100 }]));

        Assert.Equal(withDefaultFloor, withMaxFloor);

        static int CountRanked(object? ranked)
            => ((System.Collections.ICollection)ranked!).Count;
    }

    [Fact]
    public async Task MatchAsync_SavedIdCandidate_HonorsConfiguredStrictnessFloor()
    {
        var recordingId = Guid.NewGuid().ToString();
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/recording/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""
                        {
                          "id": "{{recordingId}}",
                          "title": "Same Title",
                          "length": 200000,
                          "artist-credit": [ { "name": "Some Artist", "artist": { "id": "{{Guid.NewGuid()}}", "name": "Some Artist" } } ]
                        }
                        """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"releases":[]}""")
            };
        }));
        var matcher = new MusicBrainzMatcher(new MusicBrainzClient(httpClient, NullLogger<MusicBrainzClient>.Instance), NullLogger<MusicBrainzMatcher>.Instance);
        var info = new AutoTagAudioInfo
        {
            Title = "Same Title",
            Artist = "Same Artist",
            Artists = ["Same Artist"],
            DurationSeconds = 200,
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["MUSICBRAINZ_RECORDING_ID"] = [recordingId]
            }
        };
        var matching = new AutoTagMatchingConfig
        {
            Strictness = 0.7,
            MatchDuration = false,
            MaxDurationDifferenceSeconds = 45
        };

        var acceptedAtDefault = await matcher.MatchAsync(info, matching, new MusicBrainzMatchConfig(), CancellationToken.None);
        var rejectedAtFloor99 = await matcher.MatchAsync(info, matching, new MusicBrainzMatchConfig { MinStrictness = 99 }, CancellationToken.None);

        Assert.NotNull(acceptedAtDefault);
        Assert.Equal("id", acceptedAtDefault!.MatchStrategy);
        Assert.Null(rejectedAtFloor99);
    }

    [Theory]
    [InlineData("US,GB", "US", "GB", "first", 0)]
    [InlineData("US,GB", "US", "GB", "first", 3)]
    [InlineData("US,GB", "US", "GB", "first", 20)]
    [InlineData("US,GB", "GB", "FR", "first", 0)]
    [InlineData("US,GB", "FR", "DE", "second", 20)]
    [InlineData("GB,US", "US", "GB", "second", 3)]
    [InlineData(" us , US,, gb ", "us", "GB", "first", 0)]
    [InlineData("XW,US", "XW", "US", "first", 0)]
    [InlineData("US,GB", null, "FR", "second", 3)]
    [InlineData("", "US", "FR", "first", 20)]
    public void ReleaseSelectors_ExhaustOrderedCountriesBeforeOrdinaryRanking(
        string countries, string? firstCountry, string? secondCountry, string expectedId, int countryWeight)
    {
        var config = new MusicBrainzMatchConfig
        {
            PreferredReleaseCountries = countries,
            CountryWeight = countryWeight,
            OfficialWeight = 30,
            CompilationPenaltyWeight = 40
        };
        var preferences = CreatePreferences(config);
        var poorGroup = new ReleaseGroup { PrimaryType = "Album", SecondaryTypes = ["Compilation"] };
        var goodGroup = new ReleaseGroup { PrimaryType = "Album", SecondaryTypes = [] };
        var small = new List<ReleaseSmall>
        {
            new() { Id = "first", Title = "Same Album", Country = firstCountry, Status = "Bootleg", Date = "2020-01-01", ReleaseGroup = poorGroup },
            new() { Id = "second", Title = "Same Album", Country = secondCountry, Status = "Official", Date = "2020-01-01", ReleaseGroup = goodGroup }
        };
        var detailed = new List<Release>
        {
            new() { Id = "first", Title = "Same Album", Country = firstCountry, Status = "Bootleg", Date = "2020-01-01", ReleaseGroup = poorGroup },
            new() { Id = "second", Title = "Same Album", Country = secondCountry, Status = "Official", Date = "2020-01-01", ReleaseGroup = goodGroup }
        };
        var selectSmall = typeof(MusicBrainzMatcher).GetMethod("SelectBestReleaseSmall", BindingFlags.NonPublic | BindingFlags.Static)!;
        var selectDetailed = typeof(MusicBrainzMatcher).GetMethod("SelectBestRelease", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expectedId, ((ReleaseSmall)selectSmall.Invoke(null, [small, "2020-01-01", preferences])!).Id);
        Assert.Equal(expectedId, ((Release)selectDetailed.Invoke(null, [detailed, new DateTime(2020, 1, 1), preferences, null, "Same Album"])!).Id);
    }

    [Theory]
    [InlineData("US,GB")]
    [InlineData("")]
    public void ReleaseSelectors_UseDateThenIdForTiesRegardlessOfInputOrder(string countries)
    {
        var preferences = CreatePreferences(new MusicBrainzMatchConfig { PreferredReleaseCountries = countries, PreferReleaseYear = false });
        var small = new List<ReleaseSmall>
        {
            new() { Id = "later", Country = "US", Date = "2021-01-01" },
            new() { Id = "b", Country = "US", Date = "2020-01-01" },
            new() { Id = "a", Country = "US", Date = "2020-01-01" }
        };
        var detailed = small.ConvertAll(r => new Release { Id = r.Id, Country = r.Country, Date = r.Date });
        var selectSmall = typeof(MusicBrainzMatcher).GetMethod("SelectBestReleaseSmall", BindingFlags.NonPublic | BindingFlags.Static)!;
        var selectDetailed = typeof(MusicBrainzMatcher).GetMethod("SelectBestRelease", BindingFlags.NonPublic | BindingFlags.Static)!;
        for (var repeat = 0; repeat < 2; repeat++)
        {
            Assert.Equal("a", ((ReleaseSmall)selectSmall.Invoke(null, [small, null, preferences])!).Id);
            Assert.Equal("a", ((Release)selectDetailed.Invoke(null, [detailed, null, preferences, null, null])!).Id);
            small.Reverse(); detailed.Reverse();
        }
    }

    [Theory]
    [InlineData(null, "US")]
    [InlineData("FR", "FR")]
    [InlineData("missing", "US")]
    public async Task MatchAsync_UsesDetailedCountryChoiceOrSharedAlbumHintBeforeCopyingReleaseTags(
        string? anchor, string expectedCountry)
    {
        var recordingIds = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
        var usId = Guid.NewGuid().ToString();
        var frId = Guid.NewGuid().ToString();
        var artistId = Guid.NewGuid().ToString();
        var releases = new List<Release>
        {
            MakeRelease(usId, "US", "2020-01-01", "Digital Media", 0),
            MakeRelease(frId, "FR", "2005-01-01", "CD", 10)
        };
        var releaseRequests = 0;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            object payload;
            if (request.RequestUri!.AbsolutePath.Contains("/recording/", StringComparison.Ordinal))
            {
                var index = request.RequestUri.AbsolutePath.EndsWith(recordingIds[0], StringComparison.Ordinal) ? 0 : 1;
                var preliminaryCountry = anchor == "FR" ? "US" : "FR";
                payload = new Recording
                {
                    Id = recordingIds[index], Title = $"Song {index + 1}", Length = 200000,
                    ArtistCredit = [new ArtistCredit { Name = "Same Artist", Artist = new Artist { Id = artistId, Name = "Same Artist" } }],
                    Releases = [new ReleaseSmall { Id = preliminaryCountry == "US" ? usId : frId, Title = "Same Album", Country = preliminaryCountry, Date = "2020-01-01" }]
                };
            }
            else
            {
                Assert.Contains("release?recording=", request.RequestUri.ToString(), StringComparison.Ordinal);
                releaseRequests++;
                payload = new BrowseReleases { ReleaseCount = 2, Releases = releases };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            };
        }));
        var matcher = new MusicBrainzMatcher(new MusicBrainzClient(httpClient, NullLogger<MusicBrainzClient>.Instance), NullLogger<MusicBrainzMatcher>.Instance);
        for (var index = 0; index < recordingIds.Length; index++)
        {
            var info = new AutoTagAudioInfo
            {
                Title = $"Song {index + 1}", Artist = "Same Artist", Artists = ["Same Artist"], Album = "Same Album", DurationSeconds = 200,
                Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["MUSICBRAINZ_RECORDING_ID"] = [recordingIds[index]] }
            };
            if (anchor is not null)
                info.Tags["MUSICBRAINZ_ALBUMID"] = [anchor == "FR" ? frId : Guid.NewGuid().ToString()];
            var result = await matcher.MatchAsync(info, new AutoTagMatchingConfig { Strictness = 0.7, MatchDuration = false },
                new MusicBrainzMatchConfig { PreferredReleaseCountries = "US,FR" }, CancellationToken.None);
            Assert.NotNull(result);
            var track = result!.Track;
            var isUs = expectedCountry == "US";
            Assert.Equal(isUs ? usId : frId, track.ReleaseId);
            Assert.Equal(track.ReleaseId, track.AlbumId);
            Assert.Equal(expectedCountry, track.ReleaseCountry);
            Assert.Equal("Same Album", track.Album);
            Assert.Equal($"{expectedCountry} Label", track.Label);
            Assert.Equal($"{expectedCountry}-catalog", track.CatalogNumber);
            Assert.Equal($"{expectedCountry}-barcode", track.Barcode);
            Assert.Equal(isUs ? new DateTime(2020, 1, 1) : new DateTime(2005, 1, 1), track.ReleaseDate);
            Assert.Equal(isUs ? "Digital Media" : "CD", Assert.Single(track.Media));
            Assert.Equal(index + 1 + (isUs ? 0 : 10), track.TrackNumber);
            Assert.Equal(2, track.TrackTotal);
            Assert.Equal(track.AlbumId, Assert.Single(track.Other["MUSICBRAINZ_ALBUMID"]));
            Assert.Equal(expectedCountry, Assert.Single(track.Other["RELEASECOUNTRY"]));
        }
        Assert.Equal(2, releaseRequests);

        Release MakeRelease(string id, string country, string date, string format, int offset)
            => new()
            {
                Id = id, Title = "Same Album", Country = country, Date = date, Status = "Official",
                Barcode = $"{country}-barcode",
                LabelInfo = [new LabelInfo { CatalogNumber = $"{country}-catalog", Label = new Label { Name = $"{country} Label" } }],
                Media = [new ReleaseMedia
                {
                    Position = 1, Format = format, TrackCount = 2,
                    Tracks = [new MusicBrainzTrack { Position = 1 + offset, Recording = new Recording { Id = recordingIds[0] } },
                              new MusicBrainzTrack { Position = 2 + offset, Recording = new Recording { Id = recordingIds[1] } }]
                }]
            };
    }

    private static object CreatePreferences(MusicBrainzMatchConfig config)
        => typeof(MusicBrainzMatcher).GetNestedType("MusicBrainzPreferences", BindingFlags.NonPublic)!
            .GetMethod("FromConfig", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [config])!;

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null
            && !(File.Exists(Path.Combine(current.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(current.FullName, "DeezSpoTag.Web"))))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
