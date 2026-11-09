using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Task 2 write-core contracts: the shared role-aware credit writer binds every
/// enabled role to its canonical dedicated raw field, honours independent
/// overwrite (per-role whitelist and global), merges/supersedes recognized
/// combined INVOLVEDPEOPLE credit entries without replacing the whole list, and
/// leaves disabled roles and unknown combined labels untouched — verified on
/// real MP3, FLAC and M4A files saved and re-read from disk.
/// </summary>
public sealed class AutoTagCreditRoleFileRoundTripTest
{
    public static IEnumerable<object[]> Extensions()
    {
        foreach (var extension in new[] { "mp3", "flac", "m4a" })
        {
            yield return new object[] { extension };
        }
    }

    [Theory]
    [MemberData(nameof(Extensions))]
    public async Task VerifiedShazamPageUsesTheSharedWriterAndIndependentSelections(string extension)
    {
        var card = ShazamSongDetailsTest.SourceCard();
        var accepted = (DeezSpoTag.Web.Services.ShazamTrackCard?)typeof(DeezSpoTag.Web.Services.ShazamDiscoveryService)
            .GetMethod("ParseSongDetails", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [card, ShazamSongDetailsTest.SongPage()]);
        Assert.NotNull(accepted?.SongDetails);
        var target = new AutoTagTrack { Title = card.Title, Artists = [card.Artist], Album = card.Album };
        var selected = new HashSet<string>(["bpm", "melodicness", "songwriter"], StringComparer.OrdinalIgnoreCase);
        var merged = (HashSet<string>)typeof(LocalAutoTagRunner).GetMethod("MergeSharedSongDetails", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [target, accepted.SongDetails, selected])!;
        Assert.Equal(3, merged.Count);
        var path = NewTrack(extension, out var directory);
        try
        {
            var settings = new TagSettings { Bpm = true, Melodicness = true, Songwriter = true, Producer = false };
            await RunWriterCoreAsync(path, selected.ToList(), settings, target, null, false);
            using var saved = TagLib.File.Create(path);
            Assert.Equal(104u, saved.Tag.BeatsPerMinute);
            Assert.Equal(.98, double.Parse(ReadOne(saved, extension, "MELODICNESS")!, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("Writer Person", ReadOne(saved, extension, "SONGWRITER"));
            Assert.Null(ReadOne(saved, extension, "PRODUCER"));
            Assert.Null(ReadOne(saved, extension, "COMPOSER"));
        }
        finally { Directory.Delete(directory, true); }
    }

    // -- basic round trip -------------------------------------------------

    [Theory]
    [MemberData(nameof(Extensions))]
    public async Task EnabledRolesWriteDedicatedRawFields(string extension)
    {
        var (path, _) = await WriteTrackAsync(extension, ["songwriter", "producer", "mixingEngineer"], settings =>
        {
            settings.Songwriter = true;
            settings.Producer = true;
            settings.MixingEngineer = true;
        }, track =>
        {
            track.Other["songwriter"] = ["Nile Rodgers"];
            track.Other["producer"] = ["Guy-Manuel de Homem-Christo", "Thomas Bangalter"];
            track.Other["mixingEngineer"] = ["Bob Ludwig"];
        });

        try
        {
            using var verify = TagLib.File.Create(path);
            Assert.Equal("Nile Rodgers", ReadOne(verify, extension, "SONGWRITER"));
            Assert.Null(ReadOne(verify, extension, "RECORDINGENGINEER")); // absent stays absent
            var mixing = ReadOne(verify, extension, "MIXINGENGINEER");
            Assert.Equal("Bob Ludwig", mixing);
            var producer = ReadOne(verify, extension, "PRODUCER");
            Assert.Contains("Guy-Manuel", producer);
            Assert.Contains("Thomas Bangalter", producer);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [MemberData(nameof(Extensions))]
    public async Task LegacyCombinedSelectionCannotWriteADisabledRole(string extension)
    {
        var path = NewTrack(extension, out _);
        try
        {
            SeedInvolvedPeople(path, extension, ["Producer: Existing Producer", "Chef: Existing Chef"]);
            await RunWriterAsync(path, ["involvedPeople", "engineer"], settings =>
            {
                settings.InvolvedPeople = true;
                settings.Engineer = true;
            }, track => track.Other["involvedPeople"] = ["Producer: Unwanted Producer", "Producer, Written-By: Unwanted Producer", "Engineer: New Engineer", "Chef: Source Chef"],
                overwriteTags: ["engineer"], overwriteAll: true);
            using var verify = TagLib.File.Create(path);
            var combined = ReadOne(verify, extension, "INVOLVEDPEOPLE");
            Assert.DoesNotContain("Unwanted Producer", combined ?? "");
            Assert.Contains("Existing Producer", combined);
            Assert.Equal("New Engineer", ReadOne(verify, extension, "ENGINEER"));
            Assert.Null(ReadOne(verify, extension, "PRODUCER"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [MemberData(nameof(Extensions))]
    public async Task PresenceChecksRecognizeDedicatedAndCombinedRoles(string extension)
    {
        var path = NewTrack(extension, out _);
        try
        {
            SeedInvolvedPeople(path, extension, ["Producer: Existing"]);
            using (var file = TagLib.File.Create(path))
            {
                var hasTag = typeof(LocalAutoTagRunner).GetMethod("HasTag", BindingFlags.NonPublic | BindingFlags.Static)!;
                Assert.True((bool)hasTag.Invoke(null, [file, "." + extension, SupportedTag.Producer, new LocalAutoTagRunner.AutoTagRunnerConfig(), "test"])!);
            }
            await RunWriterAsync(path, ["engineer"], settings => settings.Engineer = true,
                track => track.Other["engineer"] = ["Engineer"], null, false);
            using var saved = TagLib.File.Create(path);
            var check = typeof(LocalAutoTagRunner).GetMethod("HasTag", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.True((bool)check.Invoke(null, [saved, "." + extension, SupportedTag.Engineer, new LocalAutoTagRunner.AutoTagRunnerConfig(), "test"])!);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [MemberData(nameof(Extensions))]
    public async Task MelodicnessUsesItsCanonicalPhysicalField(string extension)
    {
        var (path, _) = await WriteTrackAsync(extension, ["melodicness"], settings => settings.Melodicness = true,
            track => track.Melodicness = 0.75);
        try
        {
            using var file = TagLib.File.Create(path);
            Assert.Equal("0.75", ReadOne(file, extension, "MELODICNESS"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [MemberData(nameof(Extensions))]
    public async Task OtherTagsCannotBypassTheSongwriterSelection(string extension)
    {
        var (path, _) = await WriteTrackAsync(extension, ["otherTags"], settings => settings.OtherTags = true,
            track =>
            {
                track.Other["writer"] = ["Unselected Writer"];
                track.Other["CUSTOM_NOTE"] = ["Keep ordinary metadata"];
            }, overwriteAll: true);
        try
        {
            using var file = TagLib.File.Create(path);
            Assert.Null(ReadOne(file, extension, "writer"));
            Assert.Null(ReadOne(file, extension, "SONGWRITER"));
            Assert.Equal("Keep ordinary metadata", ReadOne(file, extension, "CUSTOM_NOTE"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [MemberData(nameof(Extensions))]
    public async Task NativeRolesRespectExistingCombinedCredits(string extension)
    {
        foreach (var key in new[] { "composer", "lyricist", "remixer", "publisher" })
        {
            var path = NewTrack(extension, out _);
            var raw = key switch
            {
                "composer" => extension == "mp3" ? "TCOM" : extension == "m4a" ? "©wrt" : "COMPOSER",
                "lyricist" => extension == "mp3" ? "TEXT" : "LYRICIST",
                "remixer" => extension == "mp3" ? "TPE4" : "REMIXER",
                _ => "PUBLISHER"
            };
            try
            {
                SeedInvolvedPeople(path, extension, [$"{key}: Existing", "Chef: Keep"]);
                void ConfigureSettings(TagSettings settings)
                {
                    settings.Composer = settings.Lyricist = settings.Remixer = settings.Publisher = true;
                }
                void ConfigureTrack(AutoTagTrack track)
                {
                    track.Other[key] = ["Replacement"];
                    if (key == "lyricist") track.Lyricist = "Replacement";
                    if (key == "publisher") track.Publisher = "Replacement";
                    if (key == "remixer") track.Remixers = ["Replacement"];
                }
                await RunWriterAsync(path, [key], ConfigureSettings, ConfigureTrack, null, false);
                using (var file = TagLib.File.Create(path))
                {
                    Assert.Null(ReadOne(file, extension, raw));
                    Assert.Contains("Existing", ReadOne(file, extension, "INVOLVEDPEOPLE"));
                }
                await RunWriterAsync(path, [key], ConfigureSettings, ConfigureTrack, [key], false);
                using var saved = TagLib.File.Create(path);
                Assert.Equal("Replacement", ReadOne(saved, extension, raw));
                var combined = ReadOne(saved, extension, "INVOLVEDPEOPLE");
                Assert.DoesNotContain("Existing", combined ?? "");
                Assert.Contains("Chef: Keep", combined);
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }
    }

    [Theory]
    [MemberData(nameof(Extensions))]
    public async Task DeezerAndDiscogsCreditsReachTheSameSavedRoleFields(string extension)
    {
        var deezerInfo = new DeezerTrackInfo();
        typeof(DeezerMatcher).GetMethod("ApplyContributorRoles", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [deezerInfo, new[]
            {
                new DeezerArtist { Name = "Writer Person", Role = "writer" },
                new DeezerArtist { Name = "Producer Person", Role = "producer" },
                new DeezerArtist { Name = "Lyrics Person", Role = "lyricist" }
            }]);
        var deezer = (AutoTagTrack)typeof(DeezerMatcher)
            .GetMethod("ToAutoTagTrack", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [deezerInfo])!;
        var release = new DiscogsRelease
        {
            Id = 42, Title = "Album", Artists = [new DiscogsArtist { Name = "Artist", Id = 7 }],
            Tracks = [new DiscogsTrack { Position = "1", Title = "Song" }],
            ExtraArtists =
            [
                new DiscogsExtraArtist { Name = "Writer Person", Role = "Written-By" },
                new DiscogsExtraArtist { Name = "Producer Person", Role = "Producer" },
                new DiscogsExtraArtist { Name = "Lyrics Person", Role = "Lyrics By" }
            ]
        };
        var discogsInfo = (DiscogsTrackInfo)typeof(DiscogsMatcher)
            .GetMethod("ToTrack", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [release, 0, new DiscogsConfig()])!;
        var discogs = (AutoTagTrack)typeof(DiscogsMatcher)
            .GetMethod("ToAutoTagTrack", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [discogsInfo])!;
        foreach (var track in new[] { deezer, discogs })
        {
            var path = NewTrack(extension, out _);
            try
            {
                await RunWriterAsync(path, ["songwriter", "producer", "lyricist"], settings =>
                {
                    settings.Songwriter = settings.Producer = settings.Lyricist = true;
                }, _ => { }, null, false, track: track);
                using var saved = TagLib.File.Create(path);
                Assert.Equal("Writer Person", ReadOne(saved, extension, "SONGWRITER"));
                Assert.Equal("Producer Person", ReadOne(saved, extension, "PRODUCER"));
                Assert.Equal("Lyrics Person", ReadOne(saved, extension, extension == "mp3" ? "TEXT" : "LYRICIST"));
                Assert.Empty(saved.Tag.Composers);
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }
    }

    // -- independent overwrite semantics ----------------------------------

    [Theory]
    [MemberData(nameof(Extensions))]
    public async Task MissingFieldWithOverwriteOffWritesExistingFieldStays(string extension)
    {
        var path = NewTrack(extension, out _);
        try
        {
            // First write with overwrite off: role absent from the file, so the
            // value lands even though overwrite is disabled.
            await RunWriterAsync(path, ["producer", "engineer"], settings =>
            {
                settings.Producer = true;
                settings.Engineer = true;
            }, track =>
            {
                track.Other["producer"] = ["First Producer"];
                track.Other["engineer"] = ["First Engineer"];
            }, overwriteTags: null, overwriteAll: false);

            using (var verify = TagLib.File.Create(path))
            {
                Assert.Equal("First Producer", ReadOne(verify, extension, "PRODUCER"));
                Assert.Equal("First Engineer", ReadOne(verify, extension, "ENGINEER"));
            }

            // Second run, new values, still no overwrite permission: existing
            // values win and nothing changes.
            var second = BuildTrack(track =>
            {
                track.Other["producer"] = ["Second Producer"];
            });
            second.Other["producedByMarker"] = ["ignored"];
            await RunWriterAsync(path, ["producer"], settings => settings.Producer = true, _ => { }, overwriteTags: null, overwriteAll: false, track: second);

            using (var verify = TagLib.File.Create(path))
            {
                Assert.Equal("First Producer", ReadOne(verify, extension, "PRODUCER"));
            }

            // Per-role overwrite permission replaces exactly the granted role.
            await RunWriterAsync(path, ["producer"], settings => settings.Producer = true, track =>
            {
                track.Other["producer"] = ["Second Producer"];
            }, overwriteTags: ["producer"], overwriteAll: false);

            using (var verify = TagLib.File.Create(path))
            {
                Assert.Equal("Second Producer", ReadOne(verify, extension, "PRODUCER"));
                Assert.Equal("First Engineer", ReadOne(verify, extension, "ENGINEER"));
            }
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task PerRoleOverwriteDoesNotTouchOtherRolesOnAnyFormat()
    {
        foreach (var extension in new[] { "mp3", "flac", "m4a" })
        {
            var path = NewTrack(extension, out _);
            try
            {
                await RunWriterAsync(path, ["songwriter", "djMixer"], settings =>
                {
                    settings.Songwriter = true;
                    settings.DjMixer = true;
                }, track =>
                {
                    track.Other["songwriter"] = ["Old Writer"];
                    track.Other["djMixer"] = ["Old DJ"];
                }, overwriteTags: null, overwriteAll: false);

                // Only the granted role (songwriter) is replaced by a later
                // enabled write; djMixer is not in the whitelist and its value
                // stays despite new data.
                await RunWriterAsync(path, ["songwriter", "djMixer"], settings =>
                {
                    settings.Songwriter = true;
                    settings.DjMixer = true;
                }, track =>
                {
                    track.Other["songwriter"] = ["New Writer"];
                    track.Other["djMixer"] = ["New DJ"];
                }, overwriteTags: ["songwriter"], overwriteAll: false);

                using var verify = TagLib.File.Create(path);
                Assert.Equal("New Writer", ReadOne(verify, extension, "SONGWRITER"));
                Assert.Equal("Old DJ", ReadOne(verify, extension, "DJMIXER"));
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }
    }

    [Fact]
    public async Task GlobalOverwriteReplacesDedicatedRoleOnAllFormats()
    {
        foreach (var extension in new[] { "mp3", "flac", "m4a" })
        {
            var path = NewTrack(extension, out _);
            try
            {
                await RunWriterAsync(path, ["masteringEngineer"], settings => settings.MasteringEngineer = true,
                    track => track.Other["masteringEngineer"] = ["Old Master"], null, false);
                await RunWriterAsync(path, ["masteringEngineer"], settings => settings.MasteringEngineer = true,
                    track => track.Other["masteringEngineer"] = ["New Master"], null, overwriteAll: true);
                using var verify = TagLib.File.Create(path);
                Assert.Equal("New Master", ReadOne(verify, extension, "MASTERINGENGINEER"));
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }
    }

    // -- combined-credit (INVOLVEDPEOPLE) merge/supersede -------------------

    [Fact]
    public async Task ProducerCombinedUpdateLeavesEngineerAndUnknownRolesUntouched()
    {
        var path = NewTrack("mp3", out _);
        try
        {
            SeedInvolvedPeople(path, "mp3", ["Producer: Old Producer", "Engineer: Station Engineer", "Chef: Kitchen Guy"]);
            await RunWriterAsync(path, ["producer"], settings => settings.Producer = true,
                track => track.Other["producer"] = ["New Producer"], overwriteTags: ["producer"], false);

            using var verify = TagLib.File.Create(path);
            Assert.Equal("New Producer", ReadOne(verify, extensionmp3(), "PRODUCER"));
            // Combined representation still carried engineer + unrecognized labels.
            var combined = ReadOne(verify, extensionmp3(), "INVOLVEDPEOPLE");
            Assert.Contains("Engineer: Station Engineer", combined);
            Assert.Contains("Chef: Kitchen Guy", combined);
            // The superseded producer entries are gone: no conflicting duplicates.
            Assert.DoesNotContain("Old Producer", combined);
            // The role lives in the dedicated field only now.
            Assert.Equal(0, ContainsCount(combined, "Producer:"));        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task OverwriteOffDoesNotNeverDuplicateRecognizedCombinedRole()
    {
        var path = NewTrack("flac", out _);
        try
        {
            SeedInvolvedPeople(path, "flac", ["Producer: Old Producer"]);
            await RunWriterAsync(path, ["producer"], settings => settings.Producer = true,
                track => track.Other["producer"] = ["Second Producer"], overwriteTags: null, false);

            // Overwrite off + recognized combined presence: existing value wins.
            // The dedicated field is not created and no conflicting duplicate is
            // written anywhere.
            using var verify = TagLib.File.Create(path);
            Assert.Null(ReadOne(verify, "flac", "PRODUCER"));
            var combined = ReadOne(verify, "flac", "INVOLVEDPEOPLE");
            Assert.Contains("Old Producer", combined);
            Assert.DoesNotContain("Second Producer", combined);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task CombinedEntrySupersededWhenDedicatedWriteIsGranted()
    {
        var path = NewTrack("m4a", out _);
        try
        {
            SeedInvolvedPeople(path, "m4a", ["Mixing Engineer: Old Mixer"]);
            await RunWriterAsync(path, ["mixingEngineer"], settings => settings.MixingEngineer = true,
                track => track.Other["mixingEngineer"] = ["New Mixer"], overwriteTags: ["mixingEngineer"], false);

            using var verify = TagLib.File.Create(path);
            Assert.Equal("New Mixer", ReadOne(verify, "m4a", "MIXINGENGINEER"));
            // There must not be both a new dedicated value and a stale combined
            // entry carrying different data for the same role.
            var combined = ReadOne(verify, "m4a", "INVOLVEDPEOPLE");
            if (combined != null)
            {
                Assert.DoesNotContain("Old Mixer", combined);
            }
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    private static int ContainsCount(string? haystack, string needle)
    {
        if (haystack == null)
        {
            return 0;
        }

        var count = 0;
        var index = -1;
        while ((index = haystack.IndexOf(needle, index + 1, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
        }

        return count;
    }

    private static string extensionmp3() => "mp3";

    // ---------------------------------------------------------------------
    // harness
    // ---------------------------------------------------------------------

    private static void SeedInvolvedPeople(string path, string extension, string[] entries)
    {
        using var file = TagLib.File.Create(path);
        if (extension == "mp3")
        {
            var tag = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);
            TagLib.Id3v2.UserTextInformationFrame.Get(tag, "INVOLVEDPEOPLE", true).Text = entries;
        }
        else if (extension == "flac")
        {
            var tag = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, true);
            tag.SetField("INVOLVEDPEOPLE", entries);
        }
        else
        {
            var tag = (TagLib.Mpeg4.AppleTag)file.GetTag(TagLib.TagTypes.Apple, true);
            tag.SetDashBox("com.apple.iTunes", "INVOLVEDPEOPLE", string.Join(";", entries));
        }
        file.Save();
    }

    private static AutoTagTrack BuildTrack(Action<AutoTagTrack>? configure = null)
    {
        var track = new AutoTagTrack
        {
            Title = "Song",
            Album = "Album",
            Artists = ["Artist"],
            Other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        };
        configure?.Invoke(track);
        return track;
    }

    private static async Task<(string Path, TagSettings Settings)> WriteTrackAsync(
        string extension,
        string[] tags,
        Action<TagSettings> configureSettings,
        Action<AutoTagTrack> configureTrack,
        List<string>? overwriteTags = null,
        bool overwriteAll = false)
    {
        var path = NewTrack(extension, out _);
        var settings = new TagSettings();
        configureSettings(settings);
        var track = BuildTrack();
        configureTrack(track);
        await RunWriterCoreAsync(path, tags.ToList(), settings, track, overwriteTags, overwriteAll);
        return (path, settings);
    }

    private static string? ReadOne(TagLib.File file, string extension, string rawName)
        => LocalAutoTagRunner.ReadRawTagValues(
            file,
            extension.StartsWith('.') ? extension : "." + extension,
            rawName).FirstOrDefault();

    private static Task RunWriterAsync(
        string path,
        List<string> tags,
        Action<TagSettings> configureSettings,
        Action<AutoTagTrack> configureTrack,
        List<string>? overwriteTags,
        bool overwriteAll,
        AutoTagTrack? track = null,
        TagSettings? settings = null)
    {
        var (finalPath, finalTrack, finalSettings, finalTags, finalOverwriteTags, finalOverwriteAll) =
            (path,
             track ?? BuildTrack(configureTrack),
             settings ?? BuildSettings(configureSettings),
             tags,
             overwriteTags,
             overwriteAll);
        return RunWriterCoreAsync(finalPath, finalTags, finalSettings, finalTrack, finalOverwriteTags, finalOverwriteAll);
    }

    private static TagSettings BuildSettings(Action<TagSettings> configure)
    {
        var settings = new TagSettings();
        configure(settings);
        return settings;
    }

    private static async Task RunWriterCoreAsync(
        string path,
        List<string> tags,
        TagSettings settings,
        AutoTagTrack track,
        List<string>? overwriteTags,
        bool overwriteAll)
    {
        var collaborators = (LocalAutoTagRunner.LocalAutoTagRunnerCollaborators)Activator.CreateInstance(
            typeof(LocalAutoTagRunner.LocalAutoTagRunnerCollaborators))!;
        collaborators.GetType().GetProperty("Logger")!.SetValue(collaborators, NullLogger<LocalAutoTagRunner>.Instance);
        var runner = new LocalAutoTagRunner(collaborators);
        var appSettings = new DeezSpoTagSettings();
        var config = new LocalAutoTagRunner.AutoTagRunnerConfig
        {
            Tags = tags,
            OverwriteTags = overwriteTags ?? new List<string>(),
            Overwrite = overwriteAll,
            Separators = new LocalAutoTagRunner.AutoTagSeparators { Id3 = ";", Vorbis = ";", Mp4 = ";" },
            Platforms = new List<string> { "test" }
        };
        await runner.WriteTagsOnetaggerStyleAsync(
            new LocalAutoTagRunner.TagWriteRequest
            {
                FilePath = path,
                SourceTrack = track,
                CoreTrack = LocalAutoTagRunner.BuildCoreTrack(track, ";", false, appSettings),
                EffectiveTagSettings = settings,
                Config = config,
                Settings = appSettings,
                PlatformId = "test",
                Separator = ";"
            },
            CancellationToken.None);
    }

    private static string NewTrack(string extension, out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "credit-role-roundtrip-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "track." + extension);
        CreateSilentAudio(path);
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Title = "Song";
            file.Tag.Performers = ["Artist"];
            file.Tag.Album = "Album";
            file.Save();
        }

        return path;
    }

    private static void CreateSilentAudio(string path)
    {
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var argument in new[]
                 {
                     "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "0.1", path
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }
}
