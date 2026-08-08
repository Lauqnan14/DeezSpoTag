using System.Reflection;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AudioFileQualityRankerTests
{
    [Theory]
    [InlineData("/x/a.flac", "FLAC", null, 24, 96000, 2, 4)]
    [InlineData("/x/a.flac", "FLAC", null, 16, 44100, 2, 3)]
    [InlineData("/x/a.flac", "FLAC", null, null, 192000, 2, 4)]
    [InlineData("/x/a.flac", "FLAC", null, null, 44100, 2, 3)]
    [InlineData("/x/a.m4a", "ALAC", null, 16, 44100, 2, 3)]
    [InlineData("/x/a.mp3", "MPEG Audio", 320, null, 44100, 2, 2)]
    [InlineData("/x/a.mp3", "MPEG Audio", 192, null, 44100, 2, 2)]
    [InlineData("/x/a.mp3", "MPEG Audio", 128, null, 44100, 2, 1)]
    [InlineData("/x/a.m4a", "MPEG-4 AAC", 256, null, 44100, 2, 2)]
    public void EstimateRank_MapsAudioPropertiesToCanonicalTiers(
        string path,
        string codec,
        int? bitrate,
        int? bitsPerSample,
        int? sampleRate,
        int? channels,
        int expected)
    {
        var facts = new AudioQualityFacts(codec, bitrate, bitsPerSample, sampleRate, channels, path);

        Assert.Equal(expected, AudioFileQualityRanker.EstimateRank(facts));
    }

    [Fact]
    public void EstimateRank_PromotesMultichannelAtmosAboveHiRes()
    {
        var atmos = new AudioQualityFacts("EC-3", 768, null, 48000, 6, "/x/a.m4a");

        Assert.Equal(5, AudioFileQualityRanker.EstimateRank(atmos));
    }

    [Fact]
    public void EstimateRank_CanDeferAtmosPromotion()
    {
        var atmos = new AudioQualityFacts("EC-3", 768, null, 48000, 6, "/x/a.m4a");

        Assert.NotEqual(5, AudioFileQualityRanker.EstimateRank(atmos, promoteAtmos: false));
    }

    [Fact]
    public void EstimateRank_DemotesFakeLosslessToItsRealBitrate()
    {
        var facts = new AudioQualityFacts("FLAC", null, 16, 44100, 2, "/x/a.flac");
        var fakeLossless = new SignalQualityAnalysis(
            Codec: "flac",
            SampleRateHz: 44100,
            StatedBitrateKbps: null,
            MaxFrequencyHz: 16000,
            NyquistFrequencyHz: 22050,
            PeakFrequencyRatio: 0.72,
            EquivalentBitrateKbps: 128,
            IsTrueLossless: false,
            IsLosslessCodecContainer: true);

        Assert.Equal(3, AudioFileQualityRanker.EstimateRank(facts));
        Assert.Equal(1, AudioFileQualityRanker.EstimateRank(facts, fakeLossless));
    }

    [Theory]
    [InlineData("/x/a.flac", 3)]
    [InlineData("/x/a.wav", 3)]
    [InlineData("/x/a.mp3", 2)]
    [InlineData("/x/a.m4a", 2)]
    [InlineData("/x/a.txt", null)]
    public void EstimateRankFromExtension_CoversTheUnreadableFileFallback(string path, int? expected)
    {
        Assert.Equal(expected, AudioFileQualityRanker.EstimateRankFromExtension(path));
    }

    [Fact]
    public void SharedRanker_MatchesLibraryScannerAcrossThePropertyMatrix()
    {
        var scannerEstimate = typeof(LocalLibraryScanner).GetMethod(
            "EstimateQualityRank",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        string?[] codecs = [null, "FLAC", "ALAC", "MPEG Audio", "MPEG-4 AAC", "Opus", "Vorbis", "PCM", "EC-3"];
        string[] extensions = [".flac", ".alac", ".wav", ".aiff", ".m4a", ".m4b", ".mp3", ".ogg", ".opus", ".bin"];
        int?[] bitrates = [null, 0, 96, 128, 192, 256, 320, 1411];
        int?[] bitDepths = [null, 8, 16, 24, 32];
        int?[] sampleRates = [null, 22050, 44100, 48000, 96000, 192000];

        var compared = 0;
        foreach (var extension in extensions)
        {
            var path = "/library/artist/album/track" + extension;
            foreach (var codec in codecs)
            {
                foreach (var bitrate in bitrates)
                {
                    foreach (var bitDepth in bitDepths)
                    {
                        foreach (var sampleRate in sampleRates)
                        {
                            var expected = (int?)scannerEstimate.Invoke(
                                null,
                                [path, codec, bitrate, bitDepth, sampleRate, null]);

                            var actual = AudioFileQualityRanker.EstimateRank(
                                new AudioQualityFacts(codec, bitrate, bitDepth, sampleRate, null, path),
                                signalAnalysis: null,
                                promoteAtmos: false);

                            Assert.True(
                                expected == actual,
                                $"rank mismatch for ext={extension} codec={codec ?? "<null>"} "
                                + $"bitrate={bitrate?.ToString() ?? "<null>"} bits={bitDepth?.ToString() ?? "<null>"} "
                                + $"rate={sampleRate?.ToString() ?? "<null>"}: scanner={expected?.ToString() ?? "<null>"} "
                                + $"shared={actual?.ToString() ?? "<null>"}");
                            compared++;
                        }
                    }
                }
            }
        }

        Assert.True(compared > 20000, $"expected a broad matrix, compared only {compared}");
    }
}
