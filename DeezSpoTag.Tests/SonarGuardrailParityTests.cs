using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SonarGuardrailParityTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    [Fact]
    public void DefaultConfig_FileTemplates_AreUnified()
    {
        var root = FindRepoRoot();
        var configPath = Path.Combine(root, "DeezSpoTag.Workers", "Data", "deezspotag", "config.json");
        Assert.True(File.Exists(configPath), $"Config file not found: {configPath}");

        var json = File.ReadAllText(configPath);
        var trackTemplate = ExtractJsonString(json, "tracknameTemplate");
        var albumTemplate = ExtractJsonString(json, "albumTracknameTemplate");
        var playlistTemplate = ExtractJsonString(json, "playlistTracknameTemplate");

        Assert.Equal(trackTemplate, albumTemplate);
        Assert.Equal(trackTemplate, playlistTemplate);
    }

    [Fact]
    public void Enrichment_Mp4PublishDate_IsSupported()
    {
        var root = FindRepoRoot();
        var runnerPath = Path.Combine(root, "DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        Assert.True(File.Exists(runnerPath), $"File not found: {runnerPath}");

        var source = File.ReadAllText(runnerPath);
        Assert.Contains(
            "SupportedTag.PublishDate => Mp4TagHelper.HasRaw(file, \"ORIGINALDATE\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "TrySetAppleDashBox(apple, \"ORIGINALDATE\", new[] { dateString });",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Enrichment_Mp4ReleaseDate_RecognizesDateAndDay()
    {
        var root = FindRepoRoot();
        var runnerPath = Path.Combine(root, "DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        Assert.True(File.Exists(runnerPath), $"File not found: {runnerPath}");

        var source = File.ReadAllText(runnerPath);
        Assert.Contains(
            "SupportedTag.ReleaseDate =>",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Mp4TagHelper.HasRaw(file, \"DATE\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "TrySetAppleDashBox(appleRelease, \"DATE\", new[] { dateString });",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Enrichment_Duration_UsesMilliseconds()
    {
        var root = FindRepoRoot();
        var runnerPath = Path.Combine(root, "DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        Assert.True(File.Exists(runnerPath), $"File not found: {runnerPath}");

        var source = File.ReadAllText(runnerPath);
        Assert.Contains("TotalMilliseconds", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Tagging_ExplicitValues_UseOneZero_NotOneTwo()
    {
        var root = FindRepoRoot();
        var taggerPath = Path.Combine(root, "DeezSpoTag.Services", "Download", "Utils", "AudioTagger.cs");
        var runnerPath = Path.Combine(root, "DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        Assert.True(File.Exists(taggerPath), $"File not found: {taggerPath}");
        Assert.True(File.Exists(runnerPath), $"File not found: {runnerPath}");

        var tagger = File.ReadAllText(taggerPath);
        var runner = File.ReadAllText(runnerPath);

        var oneTwoPattern = new Regex(@"\?\s*""1""\s*:\s*""2""", RegexOptions.CultureInvariant, RegexTimeout);
        Assert.DoesNotMatch(oneTwoPattern, tagger);
        Assert.DoesNotMatch(oneTwoPattern, runner);
    }

    [Fact]
    public void Tagging_Mp3Rating_IsWritten()
    {
        var root = FindRepoRoot();
        var taggerPath = Path.Combine(root, "DeezSpoTag.Services", "Download", "Utils", "AudioTagger.cs");
        Assert.True(File.Exists(taggerPath), $"File not found: {taggerPath}");

        var source = File.ReadAllText(taggerPath);
        Assert.Contains(
            "ApplyMp3RatingMetadata(tag, track, save);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetCustomFrame(tag, \"TXXX\", \"RATING\", rank.ToString(CultureInfo.InvariantCulture), save);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void QuickTag_Mp4Snapshot_EnumeratesAtlAdditionalFields()
    {
        var root = FindRepoRoot();
        var quickTagPath = Path.Combine(root, "DeezSpoTag.Web", "Services", "QuickTagService.cs");
        Assert.True(File.Exists(quickTagPath), $"File not found: {quickTagPath}");

        var source = File.ReadAllText(quickTagPath);
        Assert.Contains("ReadMp4AtlAdditionalTags(tags, file.Name, separators);", source, StringComparison.Ordinal);
        Assert.Contains("track.AdditionalFields", source, StringComparison.Ordinal);
        Assert.Contains("NormalizeMp4AtlAdditionalDisplayKey", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Enrichment_Mp4CompatibilityAliases_AreSynchronizedWithNativeFields()
    {
        var root = FindRepoRoot();
        var runnerPath = Path.Combine(root, "DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        Assert.True(File.Exists(runnerPath), $"File not found: {runnerPath}");

        var source = File.ReadAllText(runnerPath);
        Assert.Contains("WriteMp4AtlCompatibilityRaw(additional, \"ARTIST\", atlTrack.Artist", source, StringComparison.Ordinal);
        Assert.Contains("WriteMp4AtlCompatibilityRaw(additional, \"TPE1\", atlTrack.Artist", source, StringComparison.Ordinal);
        Assert.Contains("WriteMp4AtlCompatibilityRaw(additional, \"ALBUMARTIST\", atlTrack.AlbumArtist", source, StringComparison.Ordinal);
        Assert.Contains("Mp4AtlRawMatches(additional, normalized, value)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickTag_CloneToVorbis_RestoresFullDateAfterYearWrite()
    {
        var root = FindRepoRoot();
        var quickTagPath = Path.Combine(root, "DeezSpoTag.Web", "Services", "QuickTagService.cs");
        Assert.True(File.Exists(quickTagPath), $"File not found: {quickTagPath}");

        var source = File.ReadAllText(quickTagPath);
        Assert.Contains("RestoreVorbisCloneDate(destinationFile, destinationExtension, snapshot.RawTags);", source, StringComparison.Ordinal);
        Assert.Contains("SetVorbisRaw(vorbis, \"DATE\", new List<string> { date }, string.Empty);", source, StringComparison.Ordinal);
        Assert.Contains("foreach (var key in new[] { \"DATE\", \"TDRC\", \"TDOR\", \"ORIGINALDATE\", \"©day\", \"iTunes:DATE\" })", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagStatus_DiffButton_RequestsFinalAccumulatedDiff()
    {
        var root = FindRepoRoot();
        var statusScriptPath = Path.Combine(root, "DeezSpoTag.Web", "wwwroot", "js", "autotag-status.js");
        Assert.True(File.Exists(statusScriptPath), $"File not found: {statusScriptPath}");

        var source = File.ReadAllText(statusScriptPath);
        Assert.Contains("showTagDiff(decodeURIComponent(encodedPath), null);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("data-platform=\"${encodedPlatform}\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("const encodedPlatform = platform && platform !== \"--\" ? encodeURIComponent(platform) : \"\";", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "scripts", "scan.sh"))
                && Directory.Exists(Path.Combine(current.FullName, "DeezSpoTag.Services")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root.");
    }

    private static string ExtractJsonString(string json, string propertyName)
    {
        var pattern = $@"""{Regex.Escape(propertyName)}""\s*:\s*""(?<value>(?:[^""\\]|\\.)*)""";
        var match = Regex.Match(json, pattern, RegexOptions.CultureInvariant, RegexTimeout);
        Assert.True(match.Success, $"Missing JSON property: {propertyName}");
        return Regex.Unescape(match.Groups["value"].Value);
    }
}
