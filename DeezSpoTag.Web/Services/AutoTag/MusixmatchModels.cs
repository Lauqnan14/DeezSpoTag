using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class MusixmatchResponse<T>
{
    public MusixmatchMessage<T> Message { get; set; } = new();
}

public sealed class MusixmatchMessage<T>
{
    public MusixmatchHeader Header { get; set; } = new();
    public T? Body { get; set; }
}

public sealed class MusixmatchHeader
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }
}

public sealed class MusixmatchMacroCallsBody<T>
{
    [JsonPropertyName("macro_calls")]
    public Dictionary<string, MusixmatchResponse<T>> MacroCalls { get; set; } = new();
}

public sealed class MusixmatchBody
{
    [JsonPropertyName("lyrics")]
    public MusixmatchLyrics? Lyrics { get; set; }

    [JsonPropertyName("subtitle_list")]
    public List<MusixmatchSubtitleWrap>? SubtitleList { get; set; }

    [JsonPropertyName("richsync")]
    public MusixmatchRichsync? Richsync { get; set; }
}

public sealed class MusixmatchLyrics
{
    [JsonPropertyName("lyrics_id")]
    public long LyricsId { get; set; }

    [JsonPropertyName("lyrics_body")]
    public string LyricsBody { get; set; } = "";

    [JsonPropertyName("lyrics_language")]
    public string LyricsLanguage { get; set; } = "";

    [JsonPropertyName("lyrics_language_description")]
    public string LyricsLanguageDescription { get; set; } = "";
}

public sealed class MusixmatchSubtitleWrap
{
    [JsonPropertyName("subtitle")]
    public MusixmatchSubtitle Subtitle { get; set; } = new();
}

public sealed class MusixmatchSubtitle
{
    [JsonPropertyName("subtitle_id")]
    public long SubtitleId { get; set; }

    [JsonPropertyName("subtitle_body")]
    public string SubtitleBody { get; set; } = "";

    [JsonPropertyName("subtitle_length")]
    public uint SubtitleLength { get; set; }

    [JsonPropertyName("subtitle_language")]
    public string SubtitleLanguage { get; set; } = "";

    [JsonPropertyName("subtitle_language_description")]
    public string SubtitleLanguageDescription { get; set; } = "";
}

public sealed class MusixmatchRichsync
{
    [JsonPropertyName("richsync_id")]
    public long RichsyncId { get; set; }

    [JsonPropertyName("richsync_body")]
    public string RichsyncBody { get; set; } = "";

    [JsonPropertyName("richsync_length")]
    public uint RichsyncLength { get; set; }

    [JsonPropertyName("richssync_language")]
    public string RichssyncLanguage { get; set; } = "";

    [JsonPropertyName("richsync_language_description")]
    public string RichsyncLanguageDescription { get; set; } = "";
}

public sealed class MusixmatchRichsyncLine
{
    [JsonPropertyName("ts")]
    public float Ts { get; set; }

    [JsonPropertyName("te")]
    public float Te { get; set; }

    [JsonPropertyName("x")]
    public string X { get; set; } = "";

    [JsonPropertyName("l")]
    public List<MusixmatchRichsyncWord> L { get; set; } = new();
}

public sealed class MusixmatchRichsyncWord
{
    [JsonPropertyName("c")]
    public string C { get; set; } = "";

    [JsonPropertyName("o")]
    public float O { get; set; }
}

public sealed class MusixmatchSubtitleLine
{
    public TimeSpan Timestamp { get; set; }
    public string Line { get; set; } = "";
}
