using System.Collections.Generic;

namespace DeezSpoTag.Web.Services.AutoTag;

internal static class SupportedTagFeatureMappings
{
    private static readonly (string Key, SupportedTag Tag)[] AudioFeatureTags =
    {
        ("bpm", SupportedTag.BPM),
        ("danceability", SupportedTag.Danceability),
        ("energy", SupportedTag.Energy),
        ("valence", SupportedTag.Valence),
        ("acousticness", SupportedTag.Acousticness),
        ("instrumentalness", SupportedTag.Instrumentalness),
        ("speechiness", SupportedTag.Speechiness),
        ("loudness", SupportedTag.Loudness),
        ("tempo", SupportedTag.Tempo),
        ("timeSignature", SupportedTag.TimeSignature),
        ("liveness", SupportedTag.Liveness),
        ("key", SupportedTag.Key),
        ("mood", SupportedTag.Mood)
    };

    public static void AddAudioFeatureTags(Dictionary<string, SupportedTag> lookup)
    {
        foreach (var (key, tag) in AudioFeatureTags)
        {
            lookup[key] = tag;
        }
    }
}
