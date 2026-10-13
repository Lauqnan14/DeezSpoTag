using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MusicBrainzPicardMetadataTest
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RelationshipCredits_UseAttributesAndPicardRoles()
    {
        var recordingId = Guid.NewGuid().ToString();
        var mediumTrackId = Guid.NewGuid().ToString();
        var composerId = Guid.NewGuid().ToString();
        var releaseId = Guid.NewGuid().ToString();
        var urls = new List<string>();
        var recording = new Recording
        {
            Id = recordingId,
            Title = "Night Song",
            Length = 180000,
            FirstReleaseDate = "1999-09-09",
            Isrcs = ["OTHERISRC001", "FILEISRC0001"],
            Genres = [new Genre { Name = "Ambient" }, new Genre { Name = " " }],
            ArtistCredit =
            [
                new ArtistCredit
                {
                    Name = "Ada",
                    JoinPhrase = " & ",
                    Artist = new Artist { Id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", Name = "Ada" }
                },
                new ArtistCredit
                {
                    Name = "Bea",
                    JoinPhrase = " feat. ",
                    Artist = new Artist { Id = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", Name = "Bea" }
                }
            ],
            Relations =
            [
                ArtistRelation("instrument", "Pianist", attributes: ["piano"], credits: new Dictionary<string, string> { ["piano"] = "grand piano" }),
                ArtistRelation("vocal", "Singer", attributes: ["guest", "soprano"]),
                ArtistRelation("writer", "Writer Name"),
                ArtistRelation("mix-DJ", "DJ Name"),
                ArtistRelation("djmixer", "Old Mixer"),
                ArtistRelation("mastering", "Master Name"),
                ArtistRelation("recording", "Rec Name"),
                ArtistRelation("orchestrator", "Orch Name"),
                ArtistRelation("chorus master", "Chorus Name"),
                new Relation
                {
                    TargetType = "work",
                    Type = "performance",
                    Attributes = ["instrumental"],
                    Work = new Work
                    {
                        Id = Guid.NewGuid().ToString(),
                        Title = "Night Work",
                        Languages = ["eng"],
                        Iswcs = ["T-111.111.111-1"],
                        Relations =
                        [
                            ArtistRelation("composer", "Composer Name", composerId),
                            ArtistRelation("lyricist", "Hidden Lyricist")
                        ]
                    }
                },
                new Relation
                {
                    TargetType = "work",
                    Type = "performance",
                    Work = new Work
                    {
                        Id = Guid.NewGuid().ToString(),
                        Title = "Day Work",
                        Languages = ["deu"],
                        Iswcs = ["T-222.222.222-2"],
                        Relations = [ArtistRelation("librettist", "Librettist Name")]
                    }
                }
            ]
        };
        var release = new Release
        {
            Id = releaseId,
            Title = "Night Album",
            Country = "XW",
            Date = "2001-01-01",
            Status = "Official",
            Genres = [new Genre { Name = "Techno" }, new Genre { Name = "Ambient" }],
            TextRepresentation = new TextRepresentation { Language = "fra", Script = "Latn" },
            CoverArtArchive = new CoverArtArchive { Front = false, Back = true, Count = 1 },
            ReleaseGroup = new ReleaseGroup
            {
                Id = Guid.NewGuid().ToString(),
                PrimaryType = "Album",
                FirstReleaseDate = "1977-05-01",
                Genres = [new Genre { Name = "Ambient" }, new Genre { Name = "House" }, new Genre { Name = "" }]
            },
            LabelInfo =
            [
                new LabelInfo { CatalogNumber = "CAT-1", Label = new Label { Name = "Alpha" } },
                new LabelInfo { CatalogNumber = "CAT-2", Label = new Label { Name = "Beta" } }
            ],
            ArtistCredit =
            [
                new ArtistCredit { Name = "Group", JoinPhrase = " & ", Artist = new Artist { Id = "cccccccc-cccc-cccc-cccc-cccccccccccc", Name = "Group" } },
                new ArtistCredit { Name = "Guest", Artist = new Artist { Id = "dddddddd-dddd-dddd-dddd-dddddddddddd", Name = "Guest" } }
            ],
            Media =
            [
                new ReleaseMedia
                {
                    Title = "Disc One",
                    Position = 1,
                    Format = "CD",
                    TrackCount = 4,
                    Tracks =
                    [
                        new MusicBrainzTrack
                        {
                            Id = mediumTrackId,
                            Number = "A1",
                            Position = 4,
                            Recording = new Recording { Id = recordingId }
                        }
                    ]
                }
            ]
        };

        var result = await MatchAsync(recordingId, "Night Song", "Ada", ["Ada", "Bea"], "FILEISRC0001", urls, request =>
        {
            if (request.RequestUri!.Host.Contains("coverartarchive", StringComparison.OrdinalIgnoreCase))
            {
                return Json("""{"images":[{"types":["Back"],"image":"https://example.test/back.jpg"},{"front":true,"types":["Back"],"image":"https://example.test/not-front.jpg"}]}""");
            }

            if (request.RequestUri.AbsolutePath.Contains("/recording/", StringComparison.Ordinal))
            {
                return Json(WithFolksonomy(recording));
            }

            var browse = new JsonObject
            {
                ["release-count"] = 1,
                ["releases"] = new JsonArray(WithFolksonomy(release))
            };
            return Json(browse);
        });

        var track = result.Track;
        Assert.Equal(recordingId, track.TrackId);
        Assert.Equal(recordingId, track.RecordingId);
        Assert.Equal(mediumTrackId, track.MediumTrackId);
        Assert.DoesNotContain(urls, url => url.Contains(mediumTrackId, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(urls, url => url.Contains(recordingId, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(["Ada", "Bea"], track.Artists);
        Assert.DoesNotContain(track.Artists, name => name.Contains('&', StringComparison.Ordinal) || name.Contains("feat", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", track.ArtistId);
        Assert.Equal(["aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"], track.ArtistIds);
        Assert.Equal("cccccccc-cccc-cccc-cccc-cccccccccccc", track.AlbumArtistId);
        Assert.Equal(["cccccccc-cccc-cccc-cccc-cccccccccccc", "dddddddd-dddd-dddd-dddd-dddddddddddd"], track.AlbumArtistIds);
        Assert.Equal(["Group", "Guest"], track.AlbumArtists);

        Assert.Equal(["Performer (grand piano): Pianist", "Performer (guest soprano): Singer", "DJ-Mixer: DJ Name", "Arranger: Orch Name", "Performer (chorus master): Chorus Name"], track.Other["involvedPeople"]);
        Assert.Equal(["Composer Name"], track.Other["composer"]);
        Assert.DoesNotContain("Writer Name", track.Other["composer"]);
        Assert.Equal(["Writer Name"], track.Other["writer"]);
        Assert.Equal([composerId], track.Other["MUSICBRAINZ_COMPOSERID"]);
        Assert.Equal("Librettist Name", track.Lyricist);
        Assert.Equal(["Librettist Name"], track.Other["lyricist"]);
        Assert.DoesNotContain(track.Other["involvedPeople"], line => line.Contains("Master", StringComparison.Ordinal) || line.Contains("Old Mixer", StringComparison.Ordinal) || line.Contains("Rec Name", StringComparison.Ordinal) || line.Contains("Hidden Lyricist", StringComparison.Ordinal));

        Assert.Equal(["Night Work", "Day Work"], track.Other["WORK"]);
        Assert.Equal(["T-111.111.111-1", "T-222.222.222-2"], track.Other["ISWC"]);
        Assert.Equal(["zxx", "deu"], track.Other["language"]);
        Assert.Equal(["Latn"], track.Other["SCRIPT"]);
        Assert.Equal(["fra"], track.Other["RELEASELANGUAGE"]);
        Assert.Equal(["1977-05-01"], track.Other["ORIGINALDATE"]);
        Assert.Equal(new DateTime(2001, 1, 1), track.ReleaseDate);

        Assert.Equal("Alpha", track.Label);
        Assert.Equal(["Alpha", "Beta"], track.Labels);
        Assert.Equal("CAT-1", track.CatalogNumber);
        Assert.Equal(["CAT-1", "CAT-2"], track.CatalogNumbers);
        Assert.Equal("FILEISRC0001", track.Isrc);
        Assert.Equal(["FILEISRC0001", "OTHERISRC001"], track.Isrcs);

        Assert.Equal(["Ambient", "Techno", "House"], track.Genres);
        Assert.DoesNotContain(track.Genres, name => name.Contains("not-a-genre", StringComparison.Ordinal));

        Assert.Null(track.Art);
        Assert.Contains(urls, url => url.Contains("coverartarchive.org", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, track.TrackNumber);
        Assert.Equal(["A1"], track.Other["MUSICBRAINZ_TRACKNUMBER"]);
        Assert.Equal(["Disc One"], track.Other["DISCSUBTITLE"]);
    }

    [Fact]
    public async Task ReleaseBrowse_PrefersEventCountryOnALaterPageAndStopsAtThreePages()
    {
        var recordingId = Guid.NewGuid().ToString();
        var mediumTrackId = Guid.NewGuid().ToString();
        var selectedId = Guid.NewGuid().ToString();
        var urls = new List<string>();
        var recording = new Recording
        {
            Id = recordingId,
            Title = "Paged Song",
            Length = 180000,
            ArtistCredit = [new ArtistCredit { Name = "Ada", Artist = new Artist { Id = Guid.NewGuid().ToString(), Name = "Ada" } }]
        };
        var selected = new Release
        {
            Id = selectedId,
            Title = "Same Album",
            Country = "DE",
            Status = "Bootleg",
            Date = "1990-01-01",
            ReleaseEvents = [new ReleaseEvent { Area = new Area { Iso3166_1Codes = ["US"] } }],
            ReleaseGroup = new ReleaseGroup { Id = Guid.NewGuid().ToString(), PrimaryType = "Album" },
            Media =
            [
                new ReleaseMedia
                {
                    Position = 1,
                    Format = "CD",
                    Tracks = [new MusicBrainzTrack { Id = mediumTrackId, Position = 1, Recording = new Recording { Id = recordingId } }]
                }
            ]
        };

        var result = await MatchAsync(recordingId, "Paged Song", "Ada", ["Ada"], null, urls, request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/recording/", StringComparison.Ordinal))
            {
                return Json(recording);
            }

            var offset = QueryInt(request.RequestUri, "offset");
            Assert.Contains("release-events", request.RequestUri.Query, StringComparison.Ordinal);
            Assert.Contains("limit=100", request.RequestUri.Query, StringComparison.Ordinal);
            var releases = new List<Release>();
            for (var index = 0; index < 100; index++)
            {
                releases.Add(offset == 100 && index == 0
                    ? selected
                    : new Release
                    {
                        Id = Guid.NewGuid().ToString(),
                        Title = "Same Album",
                        Country = "GB",
                        Status = "Official",
                        Date = "2020-01-01",
                        ReleaseGroup = new ReleaseGroup { Id = Guid.NewGuid().ToString(), PrimaryType = "Album" }
                    });
            }

            return Json(new BrowseReleases { ReleaseCount = 350, Releases = releases });
        });

        Assert.Equal(selectedId, result.Track.ReleaseId);
        Assert.Equal("US", result.Track.ReleaseCountry);
        Assert.Equal(["US"], result.Track.Other["RELEASECOUNTRY"]);
        Assert.Equal(mediumTrackId, result.Track.MediumTrackId);
        Assert.Contains(urls, url => url.Contains("offset=0", StringComparison.Ordinal));
        Assert.Contains(urls, url => url.Contains("offset=100", StringComparison.Ordinal));
        Assert.Contains(urls, url => url.Contains("offset=200", StringComparison.Ordinal));
        Assert.DoesNotContain(urls, url => url.Contains("offset=300", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FrontTypedCover_IsUsedWhenTheFrontFlagIsFalse()
    {
        var recordingId = Guid.NewGuid().ToString();
        var releaseId = Guid.NewGuid().ToString();
        var urls = new List<string>();
        var recording = new Recording
        {
            Id = recordingId,
            Title = "Cover Song",
            Length = 180000,
            ArtistCredit = [new ArtistCredit { Name = "Ada", Artist = new Artist { Id = Guid.NewGuid().ToString(), Name = "Ada" } }]
        };
        var release = new Release
        {
            Id = releaseId,
            Title = "Cover Album",
            Country = "US",
            Status = "Official",
            Date = "2020-01-01",
            CoverArtArchive = new CoverArtArchive { Front = false, Back = true, Count = 2 },
            ReleaseGroup = new ReleaseGroup { Id = Guid.NewGuid().ToString(), PrimaryType = "Album" }
        };

        var result = await MatchAsync(recordingId, "Cover Song", "Ada", ["Ada"], null, urls, request =>
        {
            if (request.RequestUri!.Host.Contains("coverartarchive", StringComparison.OrdinalIgnoreCase))
            {
                return Json("""{"images":[{"types":["Back"],"image":"https://example.test/back.jpg"},{"types":["Front"],"image":"https://example.test/front-image.jpg"}]}""");
            }

            if (request.RequestUri.AbsolutePath.Contains("/recording/", StringComparison.Ordinal))
            {
                return Json(recording);
            }

            return Json(Browse(release));
        });

        Assert.Equal("https://example.test/front-image.jpg", result.Track.Art);
    }

    [Fact]
    public void QuotedSearch_EscapesLuceneOperatorsAndKeepsTheFuzzyTilde()
    {
        var method = typeof(MusicBrainzMatcher).GetMethod("BuildQueries", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("BuildQueries was not found.");
        var info = new AutoTagAudioInfo { Title = "Blue (Room): Take", Artist = "A+B" };
        var queries = Assert.IsType<List<string>>(method.Invoke(null, [info]));

        Assert.EndsWith("~", queries[0], StringComparison.Ordinal);
        Assert.DoesNotContain(@"\~", queries[0], StringComparison.Ordinal);
        Assert.Contains(@"\(", queries[0], StringComparison.Ordinal);
        Assert.Contains(@"\:", queries[0], StringComparison.Ordinal);
        Assert.Contains(@"\+", queries[0], StringComparison.Ordinal);
        Assert.Contains(@"recording:""blue \(room\)\: take""", queries[1], StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingLookup_IgnoresTheMediumTrackId()
    {
        var recordingId = Guid.NewGuid().ToString();
        var mediumTrackId = Guid.NewGuid().ToString();
        var family = AutoTagIdentityTags.ResolveFamily("musicbrainz", ProviderIdentityField.TrackId);
        Assert.Equal(["MUSICBRAINZ_TRACK_ID", "MUSICBRAINZ_RECORDING_ID"], family.WriteNames);
        Assert.DoesNotContain("MUSICBRAINZ_TRACKID", family.CleanupNames);
        Assert.Contains("MUSICBRAINZ_RECORDINGID", family.CleanupNames);
        Assert.Contains("MUSICBRAINZ_RECORDING_ID", family.CleanupNames);

        var method = typeof(MusicBrainzMatcher).GetMethod("GetRecordingIds", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("GetRecordingIds was not found.");
        var info = new AutoTagAudioInfo
        {
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["MUSICBRAINZ_TRACKID"] = [mediumTrackId],
                ["MUSICBRAINZ_TRACK_ID"] = [recordingId]
            }
        };
        var ids = Assert.IsType<List<string>>(method.Invoke(null, [info]));
        Assert.Equal(recordingId, Assert.Single(ids));

        info.Tags.Remove("MUSICBRAINZ_TRACK_ID");
        ids = Assert.IsType<List<string>>(method.Invoke(null, [info]));
        Assert.Empty(ids);
    }

    [Fact]
    public void RepeatedWriters_UseTheListWhenPresentAndTheSingleValueOtherwise()
    {
        var method = typeof(LocalAutoTagRunner).GetMethod("ResolveRepeatedTagValues", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ResolveRepeatedTagValues was not found.");
        var listed = Assert.IsType<List<string>>(method.Invoke(null, [new List<string> { "Alpha", "Beta" }, "Alpha"]));
        Assert.Equal(["Alpha", "Beta"], listed);
        var single = Assert.IsType<List<string>>(method.Invoke(null, [new List<string>(), "Alpha"]));
        Assert.Equal(["Alpha"], single);
        var missing = Assert.IsType<List<string>>(method.Invoke(null, [null, null]));
        Assert.Empty(missing);
    }

    [Fact]
    public void Capture_KeepsEveryArtistIdAndTheMediumTrackId()
    {
        var method = typeof(LocalAutoTagRunner).GetMethod("CaptureProviderIdentity", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("CaptureProviderIdentity was not found.");
        var recordingId = Guid.NewGuid().ToString();
        var mediumTrackId = Guid.NewGuid().ToString();
        var match = new AutoTagMatchResult
        {
            Track = new AutoTagTrack
            {
                TrackId = recordingId,
                ArtistId = "artist-1",
                ArtistIds = ["artist-1", "artist-2"],
                AlbumArtistId = "album-1",
                AlbumArtistIds = ["album-1", "album-2"],
                MediumTrackId = mediumTrackId,
                ReleaseId = Guid.NewGuid().ToString()
            }
        };

        var payload = Assert.IsType<ProviderIdentityPayload>(method.Invoke(null, ["musicbrainz", match]));
        Assert.Equal(recordingId, payload.TrackId);
        Assert.Equal(["artist-1", "artist-2"], payload.ArtistIds);
        Assert.Equal(["album-1", "album-2"], payload.AlbumArtistIds);
        Assert.Equal(mediumTrackId, payload.MediumTrackId);
    }

    private static async Task<AutoTagMatchResult> MatchAsync(
        string recordingId,
        string title,
        string artist,
        IReadOnlyList<string> artists,
        string? isrc,
        List<string> urls,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            urls.Add(request.RequestUri!.ToString());
            return respond(request);
        }));
        var matcher = new MusicBrainzMatcher(new MusicBrainzClient(httpClient, NullLogger<MusicBrainzClient>.Instance), NullLogger<MusicBrainzMatcher>.Instance);
        var info = new AutoTagAudioInfo
        {
            Title = title,
            Artist = artist,
            Artists = artists.ToList(),
            Album = "Same Album",
            DurationSeconds = 180,
            Isrc = isrc,
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["MUSICBRAINZ_TRACK_ID"] = [recordingId]
            }
        };
        var result = await matcher.MatchAsync(
            info,
            new AutoTagMatchingConfig { Strictness = 0.7, MatchDuration = false },
            new MusicBrainzMatchConfig { PreferredReleaseCountries = "US,GB" },
            CancellationToken.None);
        Assert.NotNull(result);
        return result!;
    }

    private static Relation ArtistRelation(
        string type,
        string name,
        string? artistId = null,
        IReadOnlyList<string>? attributes = null,
        Dictionary<string, string>? credits = null)
        => new()
        {
            TargetType = "artist",
            Type = type,
            Attributes = attributes?.ToList(),
            AttributeCredits = credits,
            Artist = new Artist { Id = artistId ?? Guid.NewGuid().ToString(), Name = name }
        };

    private static BrowseReleases Browse(Release release)
        => new() { ReleaseCount = 1, Releases = [release] };

    private static JsonObject WithFolksonomy(object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, JsonOptions)!.AsObject();
        node["tags"] = new JsonArray(new JsonObject { ["name"] = "not-a-genre" });
        node["user-tags"] = new JsonArray(new JsonObject { ["name"] = "also-not-a-genre" });
        return node;
    }

    private static HttpResponseMessage Json(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload is string text ? text : JsonSerializer.Serialize(payload, JsonOptions))
        };

    private static int QueryInt(Uri uri, string name)
    {
        var marker = name + "=";
        var query = uri.Query;
        var index = query.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return 0;
        }

        var start = index + marker.Length;
        var end = query.IndexOf('&', start);
        var text = end < 0 ? query[start..] : query[start..end];
        return int.Parse(text);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
