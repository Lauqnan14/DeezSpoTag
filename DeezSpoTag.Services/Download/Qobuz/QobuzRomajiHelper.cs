using System.Linq;
using System.Text;

namespace DeezSpoTag.Services.Download.Qobuz;

internal static class QobuzRomajiHelper
{
    private static readonly Dictionary<char, string> HiraganaToRomaji = new()
    {
        ['あ'] = "a",
        ['い'] = "i",
        ['う'] = "u",
        ['え'] = "e",
        ['お'] = "o",
        ['か'] = "ka",
        ['き'] = "ki",
        ['く'] = "ku",
        ['け'] = "ke",
        ['こ'] = "ko",
        ['さ'] = "sa",
        ['し'] = "shi",
        ['す'] = "su",
        ['せ'] = "se",
        ['そ'] = "so",
        ['た'] = "ta",
        ['ち'] = "chi",
        ['つ'] = "tsu",
        ['て'] = "te",
        ['と'] = "to",
        ['な'] = "na",
        ['に'] = "ni",
        ['ぬ'] = "nu",
        ['ね'] = "ne",
        ['の'] = "no",
        ['は'] = "ha",
        ['ひ'] = "hi",
        ['ふ'] = "fu",
        ['へ'] = "he",
        ['ほ'] = "ho",
        ['ま'] = "ma",
        ['み'] = "mi",
        ['む'] = "mu",
        ['め'] = "me",
        ['も'] = "mo",
        ['や'] = "ya",
        ['ゆ'] = "yu",
        ['よ'] = "yo",
        ['ら'] = "ra",
        ['り'] = "ri",
        ['る'] = "ru",
        ['れ'] = "re",
        ['ろ'] = "ro",
        ['わ'] = "wa",
        ['を'] = "wo",
        ['ん'] = "n",
        ['が'] = "ga",
        ['ぎ'] = "gi",
        ['ぐ'] = "gu",
        ['げ'] = "ge",
        ['ご'] = "go",
        ['ざ'] = "za",
        ['じ'] = "ji",
        ['ず'] = "zu",
        ['ぜ'] = "ze",
        ['ぞ'] = "zo",
        ['だ'] = "da",
        ['ぢ'] = "ji",
        ['づ'] = "zu",
        ['で'] = "de",
        ['ど'] = "do",
        ['ば'] = "ba",
        ['び'] = "bi",
        ['ぶ'] = "bu",
        ['べ'] = "be",
        ['ぼ'] = "bo",
        ['ぱ'] = "pa",
        ['ぴ'] = "pi",
        ['ぷ'] = "pu",
        ['ぺ'] = "pe",
        ['ぽ'] = "po",
        ['ゃ'] = "ya",
        ['ゅ'] = "yu",
        ['ょ'] = "yo",
        ['っ'] = "",
        ['ぁ'] = "a",
        ['ぃ'] = "i",
        ['ぅ'] = "u",
        ['ぇ'] = "e",
        ['ぉ'] = "o"
    };

    private static readonly Dictionary<char, string> KatakanaToRomaji = CreateKatakanaMap();

    private static readonly Dictionary<string, string> CombinationHiragana = new()
    {
        ["きゃ"] = "kya",
        ["きゅ"] = "kyu",
        ["きょ"] = "kyo",
        ["しゃ"] = "sha",
        ["しゅ"] = "shu",
        ["しょ"] = "sho",
        ["ちゃ"] = "cha",
        ["ちゅ"] = "chu",
        ["ちょ"] = "cho",
        ["にゃ"] = "nya",
        ["にゅ"] = "nyu",
        ["にょ"] = "nyo",
        ["ひゃ"] = "hya",
        ["ひゅ"] = "hyu",
        ["ひょ"] = "hyo",
        ["みゃ"] = "mya",
        ["みゅ"] = "myu",
        ["みょ"] = "myo",
        ["りゃ"] = "rya",
        ["りゅ"] = "ryu",
        ["りょ"] = "ryo",
        ["ぎゃ"] = "gya",
        ["ぎゅ"] = "gyu",
        ["ぎょ"] = "gyo",
        ["じゃ"] = "ja",
        ["じゅ"] = "ju",
        ["じょ"] = "jo",
        ["びゃ"] = "bya",
        ["びゅ"] = "byu",
        ["びょ"] = "byo",
        ["ぴゃ"] = "pya",
        ["ぴゅ"] = "pyu",
        ["ぴょ"] = "pyo"
    };

    private static readonly Dictionary<string, string> CombinationKatakana = CreateKatakanaCombinations();

    internal static bool ContainsJapanese(string value)
    {
        return value.Any(ch => IsHiragana(ch) || IsKatakana(ch) || IsKanji(ch));
    }

    internal static string JapaneseToRomaji(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !ContainsJapanese(text))
        {
            return text;
        }

        var runes = text.EnumerateRunes().ToArray();
        var result = new StringBuilder(text.Length * 2);
        var i = 0;

        while (i < runes.Length)
        {
            if (TryAppendSokuon(runes, i, result))
            {
                i++;
                continue;
            }

            if (TryAppendCombination(runes, i, result))
            {
                i += 2;
                continue;
            }

            AppendSingleRune(runes[i], result);
            i++;
        }

        return result.ToString();
    }

    internal static string CleanToAscii(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (IsAsciiWordChar(ch))
            {
                builder.Append(ch);
            }
            else if (ch == ',' || ch == '.')
            {
                builder.Append(' ');
            }
        }

        var cleaned = string.Join(' ', builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return cleaned.Trim();
    }

    private static bool TryAppendSokuon(Rune[] runes, int index, StringBuilder result)
    {
        var current = runes[index].Value;
        if (current != 'っ' && current != 'ッ')
        {
            return false;
        }

        if (index >= runes.Length - 1)
        {
            return true;
        }

        if (TryGetRomajiForRune(runes[index + 1], out var nextRomaji) && !string.IsNullOrWhiteSpace(nextRomaji))
        {
            result.Append(nextRomaji[0]);
        }

        return true;
    }

    private static bool TryAppendCombination(Rune[] runes, int index, StringBuilder result)
    {
        if (index >= runes.Length - 1 || runes[index].Value > char.MaxValue || runes[index + 1].Value > char.MaxValue)
        {
            return false;
        }

        var combo = $"{(char)runes[index].Value}{(char)runes[index + 1].Value}";
        if (CombinationHiragana.TryGetValue(combo, out var comboHiragana))
        {
            result.Append(comboHiragana);
            return true;
        }

        if (CombinationKatakana.TryGetValue(combo, out var comboKatakana))
        {
            result.Append(comboKatakana);
            return true;
        }

        return false;
    }

    private static void AppendSingleRune(Rune rune, StringBuilder result)
    {
        if (TryGetRomajiForRune(rune, out var romaji))
        {
            result.Append(romaji);
            return;
        }

        result.Append(rune);
    }

    private static bool TryGetRomajiForRune(Rune rune, out string romaji)
    {
        romaji = string.Empty;
        if (rune.Value > char.MaxValue)
        {
            return false;
        }

        var kana = (char)rune.Value;
        if (HiraganaToRomaji.TryGetValue(kana, out var hiraganaRomaji) && !string.IsNullOrEmpty(hiraganaRomaji))
        {
            romaji = hiraganaRomaji;
            return true;
        }

        if (KatakanaToRomaji.TryGetValue(kana, out var katakanaRomaji) && !string.IsNullOrEmpty(katakanaRomaji))
        {
            romaji = katakanaRomaji;
            return true;
        }

        return false;
    }

    private static Dictionary<char, string> CreateKatakanaMap()
    {
        var map = new Dictionary<char, string>();
        foreach (var entry in HiraganaToRomaji)
        {
            if (TryToKatakana(entry.Key, out var katakana))
            {
                map[katakana] = entry.Value;
            }
        }

        map['ー'] = string.Empty;
        map['ヴ'] = "vu";
        return map;
    }

    private static Dictionary<string, string> CreateKatakanaCombinations()
    {
        var map = new Dictionary<string, string>();
        foreach (var entry in CombinationHiragana)
        {
            if (TryToKatakana(entry.Key, out var katakana))
            {
                map[katakana] = entry.Value;
            }
        }

        map["ティ"] = "ti";
        map["ディ"] = "di";
        map["トゥ"] = "tu";
        map["ドゥ"] = "du";
        map["ファ"] = "fa";
        map["フィ"] = "fi";
        map["フェ"] = "fe";
        map["フォ"] = "fo";
        map["ウィ"] = "wi";
        map["ウェ"] = "we";
        map["ウォ"] = "wo";
        return map;
    }

    private static bool TryToKatakana(string value, out string katakana)
    {
        var chars = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            if (!TryToKatakana(value[i], out chars[i]))
            {
                katakana = string.Empty;
                return false;
            }
        }

        katakana = new string(chars);
        return true;
    }

    private static bool TryToKatakana(char value, out char katakana)
    {
        if (value >= '\u3041' && value <= '\u3096')
        {
            katakana = (char)(value + 0x60);
            return true;
        }

        katakana = '\0';
        return false;
    }

    private static bool IsAsciiWordChar(char value)
    {
        return IsAsciiLetter(value)
            || IsAsciiDigit(value)
            || value == ' '
            || value == '-'
            || value == '\'';
    }

    private static bool IsAsciiLetter(char value)
    {
        return (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z');
    }

    private static bool IsAsciiDigit(char value)
    {
        return value >= '0' && value <= '9';
    }

    private static bool IsHiragana(int codePoint)
    {
        return codePoint >= 0x3040 && codePoint <= 0x309F;
    }

    private static bool IsKatakana(int codePoint)
    {
        return codePoint >= 0x30A0 && codePoint <= 0x30FF;
    }

    private static bool IsKanji(int codePoint)
    {
        return (codePoint >= 0x4E00 && codePoint <= 0x9FFF) ||
            (codePoint >= 0x3400 && codePoint <= 0x4DBF);
    }
}
