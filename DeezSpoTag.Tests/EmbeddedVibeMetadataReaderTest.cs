using System;
using System.Diagnostics;
using System.IO;
using DeezSpoTag.Web.Services.Vibe;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class EmbeddedVibeMetadataReaderTest
{
    [Theory]
    [InlineData(".mp3")]
    [InlineData(".flac")]
    [InlineData(".m4a")]
    public void ReadsGenresAndStyleFieldsWithoutMoodContaminationOrFileWrites(string extension)
    {
        var directory = Directory.CreateTempSubdirectory("vibe-reader-").FullName;
        try
        {
            var path = Path.Combine(directory, "track" + extension);
            var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
            foreach (var arg in new[] { "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "0.1", path })
                start.ArgumentList.Add(arg);
            using (var process = Process.Start(start)!)
            {
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, error);
            }
            using (var file = TagLib.File.Create(path))
            {
                file.Tag.Genres = new[] { "Hip-Hop", "Afrobeats" };
                if (extension == ".mp3")
                {
                    var tag = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);
                    TagLib.Id3v2.UserTextInformationFrame.Get(tag, "MY_STYLE", true).Text = new[] { "Gengetone" };
                    TagLib.Id3v2.UserTextInformationFrame.Get(tag, "SUBGENRE", true).Text = new[] { "Drill" };
                    TagLib.Id3v2.TextInformationFrame.Get(tag, "TMOO", true).Text = new[] { "Happy" };
                }
                else if (extension == ".flac")
                {
                    var tag = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, true);
                    tag.SetField("MY_STYLE", new[] { "Gengetone", "Afrobeat" });
                    tag.SetField("SUBGENRE", new[] { "Drill" });
                    tag.SetField("MOOD", new[] { "Happy" });
                }
                else
                {
                    var tag = (TagLib.Mpeg4.AppleTag)file.GetTag(TagLib.TagTypes.Apple, true);
                    tag.SetDashBox("com.apple.iTunes", "MY_STYLE", "Gengetone");
                    tag.SetDashBox("com.apple.iTunes", "SUBGENRE", "Drill");
                    tag.SetDashBox("com.apple.iTunes", "MOOD", "Happy");
                }
                file.Save();
            }
            var before = File.ReadAllBytes(path);
            var result = new EmbeddedVibeMetadataReader().Read(path, "MY_STYLE", "MY_STYLE", "MY_STYLE");
            Assert.Contains("Hip-Hop", result.Genres);
            Assert.Contains("Afrobeats", result.Genres);
            Assert.Contains("Gengetone", result.Styles);
            Assert.Contains("Drill", result.Styles);
            Assert.DoesNotContain("Happy", result.Styles);
            Assert.Equal(new[] { "Happy" }, result.Moods);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(directory, true); }
    }
}
