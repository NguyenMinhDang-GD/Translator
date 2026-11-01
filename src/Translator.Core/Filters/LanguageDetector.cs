using System.Text.RegularExpressions;
using Translator.Core.Models;

namespace Translator.Core.Filters;

public readonly record struct ScriptCounts(
    int LatinCount,
    int KanaCount,
    int CjkIdeographCount,
    int CyrillicCount,
    int HangulCount,
    bool HasWordSpace);

public static partial class LanguageDetector
{
    [GeneratedRegex(@"<[^>]{1,64}>|\{[0-9a-zA-Z_:\.\-]{1,32}\}|%[0-9\.\-]*[sdifx]", RegexOptions.Compiled)]
    private static partial Regex RichTextAndPlaceholderRegex();

    [GeneratedRegex(@"\[(\d+)\](?=.)", RegexOptions.Compiled)]
    private static partial Regex NonTerminalArrayIndexRegex();

    public static bool MatchesFilter(DetectedLanguage language, SourceLanguageFilter filter)
    {
        return filter switch
        {
            SourceLanguageFilter.All => true,
            SourceLanguageFilter.English => language == DetectedLanguage.English,
            SourceLanguageFilter.Chinese => language == DetectedLanguage.Chinese,
            SourceLanguageFilter.Russian => language == DetectedLanguage.Russian,
            SourceLanguageFilter.Japanese => language == DetectedLanguage.Japanese,
            _ => true
        };
    }

    public static IReadOnlyList<TextEntry> AssignLanguages(
        IReadOnlyList<TextEntry> entries,
        string? targetDisplayName = null,
        string? containerRelativePath = null)
    {
        if (entries.Count == 0)
        {
            return entries;
        }

        DetectedLanguage fileLevelHint = InferLanguageFromIdentifier(targetDisplayName) != DetectedLanguage.Unknown
            ? InferLanguageFromIdentifier(targetDisplayName)
            : InferLanguageFromIdentifier(Path.GetFileNameWithoutExtension(containerRelativePath ?? string.Empty));

        var analyses = new (ScriptCounts Counts, DetectedLanguage KeyHint, string? SlotKey)[entries.Count];
        var slotStats = new Dictionary<string, SlotLanguageVotes>(StringComparer.Ordinal);

        // Pass 1: Script counting & slot vote accumulation
        for (int i = 0; i < entries.Count; i++)
        {
            TextEntry entry = entries[i];
            ScriptCounts counts = AnalyzeScript(entry.OriginalText);
            DetectedLanguage keyHint = ExtractKeyOrHeaderLanguageHint(entry.Key);
            string? slotKey = ExtractSlotGroupingKey(entry.Key);

            analyses[i] = (counts, keyHint, slotKey);

            if (entry.IsSystemNoise || slotKey is null)
            {
                continue;
            }

            if (!slotStats.TryGetValue(slotKey, out SlotLanguageVotes? votes))
            {
                votes = new SlotLanguageVotes();
                slotStats[slotKey] = votes;
            }

            if (keyHint != DetectedLanguage.Unknown)
            {
                votes.ExplicitHint = keyHint;
            }

            if (counts.KanaCount > 0)
            {
                votes.JapaneseKanaRows++;
            }
            else if (counts.HangulCount > 0)
            {
                votes.OtherRows++;
            }
            else if (counts.CyrillicCount > 0)
            {
                votes.RussianRows++;
            }
            else if (counts.CjkIdeographCount > 0)
            {
                votes.PureCjkRows++;
            }
            else if (counts.LatinCount > 0)
            {
                votes.LatinRows++;
            }
        }

        var slotDominant = new Dictionary<string, DetectedLanguage>(StringComparer.Ordinal);
        foreach ((string slotKey, SlotLanguageVotes votes) in slotStats)
        {
            slotDominant[slotKey] = votes.ResolveDominantLanguage();
        }

        // Pass 2: Final per-entry classification
        var updated = new List<TextEntry>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            TextEntry entry = entries[i];
            (ScriptCounts counts, DetectedLanguage keyHint, string? slotKey) = analyses[i];

            DetectedLanguage slotLang = slotKey is not null && slotDominant.TryGetValue(slotKey, out DetectedLanguage dom)
                ? dom
                : DetectedLanguage.Unknown;

            DetectedLanguage resolved = ClassifySingle(entry.OriginalText, counts, keyHint, slotLang, fileLevelHint);
            updated.Add(entry with { Language = resolved });
        }

        return updated;
    }

    public static ScriptCounts AnalyzeScript(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return default;
        }

        string cleaned = RichTextAndPlaceholderRegex().Replace(rawText, " ");

        int latin = 0;
        int kana = 0;
        int cjk = 0;
        int cyrillic = 0;
        int hangul = 0;
        bool hasSpace = false;

        foreach (char c in cleaned)
        {
            if (c == ' ')
            {
                hasSpace = true;
                continue;
            }

            // Hiragana, Katakana, Halfwidth Katakana
            if ((c >= '\u3040' && c <= '\u309F') ||
                (c >= '\u30A0' && c <= '\u30FF') ||
                (c >= '\u31F0' && c <= '\u31FF') ||
                (c >= '\uFF66' && c <= '\uFF9F'))
            {
                kana++;
            }
            // CJK Unified Ideographs & Extensions
            else if ((c >= '\u4E00' && c <= '\u9FFF') ||
                     (c >= '\u3400' && c <= '\u4DBF') ||
                     (c >= '\uF900' && c <= '\uFAFF'))
            {
                cjk++;
            }
            // Cyrillic
            else if (c >= '\u0400' && c <= '\u052F')
            {
                cyrillic++;
            }
            // Korean Hangul
            else if ((c >= '\uAC00' && c <= '\uD7AF') ||
                     (c >= '\u1100' && c <= '\u11FF') ||
                     (c >= '\u3130' && c <= '\u318F'))
            {
                hangul++;
            }
            // Latin (ASCII & Extended Latin)
            else if ((c >= 'A' && c <= 'Z') ||
                     (c >= 'a' && c <= 'z') ||
                     (c >= '\u00C0' && c <= '\u024F') ||
                     (c >= '\u1E00' && c <= '\u1EFF'))
            {
                latin++;
            }
        }

        return new ScriptCounts(latin, kana, cjk, cyrillic, hangul, hasSpace);
    }

    private static bool HasChineseExclusiveMarkers(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // Common Mandarin grammar characters/simplified forms never used in pure-Kanji Japanese UI terms
        return text.IndexOfAny(['吗', '嗎', '呢', '吧', '这', '這', '们', '們', '没', '沒', '个', '個', '还', '還', '经', '經', '无', '無']) >= 0 ||
               text.Contains("的", StringComparison.Ordinal) ||
               text.Contains("了", StringComparison.Ordinal);
    }

    private static DetectedLanguage ClassifySingle(
        string? rawText,
        ScriptCounts counts,
        DetectedLanguage keyHint,
        DetectedLanguage slotDominant,
        DetectedLanguage fileHint)
    {
        DetectedLanguage contextHint = keyHint != DetectedLanguage.Unknown
            ? keyHint
            : (slotDominant != DetectedLanguage.Unknown ? slotDominant : fileHint);

        // 1. Japanese Hiragana/Katakana is unmistakable
        if (counts.KanaCount > 0)
        {
            return DetectedLanguage.Japanese;
        }

        // 2. Korean Hangul
        if (counts.HangulCount > 0 && counts.HangulCount >= counts.CyrillicCount)
        {
            return DetectedLanguage.Other;
        }

        // 3. Cyrillic (Russian)
        if (counts.CyrillicCount > 0 && counts.CyrillicCount >= counts.CjkIdeographCount)
        {
            return DetectedLanguage.Russian;
        }

        // 4. CJK Ideographs without Kana: Chinese unless inside an established Japanese column/file AND free of Chinese-only grammar markers!
        if (counts.CjkIdeographCount > 0)
        {
            if (contextHint == DetectedLanguage.Japanese && !HasChineseExclusiveMarkers(rawText))
            {
                return DetectedLanguage.Japanese;
            }

            return DetectedLanguage.Chinese;
        }

        // 5. Pure Latin characters
        if (counts.LatinCount > 0)
        {
            if (keyHint == DetectedLanguage.Other || fileHint == DetectedLanguage.Other)
            {
                return DetectedLanguage.Other;
            }

            // A full multi-word English sentence overrides column shift (e.g., unquoted comma in CSV shifting columns!)
            bool isClearLatinSentence = counts.LatinCount >= 6 && counts.HasWordSpace;
            if (isClearLatinSentence)
            {
                return DetectedLanguage.English;
            }

            // Short ASCII loanword (e.g. "OK", "HP", "MAX") inside a non-English column stays with that column
            if (contextHint is DetectedLanguage.Chinese or DetectedLanguage.Japanese or DetectedLanguage.Russian or DetectedLanguage.Other)
            {
                return contextHint;
            }

            return DetectedLanguage.English;
        }

        return contextHint;
    }

    private static string? ExtractSlotGroupingKey(string entryKey)
    {
        if (string.IsNullOrEmpty(entryKey))
        {
            return null;
        }

        // 1. CSV / TSV: "csv:row=219;col=2;key=90401_1;hdr=EN" -> group by column index "csv_col:2"
        if (entryKey.StartsWith("csv:", StringComparison.Ordinal) ||
            entryKey.StartsWith("tsv:", StringComparison.Ordinal))
        {
            int colIdx = entryKey.IndexOf(";col=", StringComparison.Ordinal);
            if (colIdx >= 0)
            {
                int valStart = colIdx + 5;
                int semi = entryKey.IndexOf(';', valStart);
                string colNum = semi > valStart ? entryKey[valStart..semi] : entryKey[valStart..];
                return $"table_col:{colNum}";
            }
        }

        // 2. MonoBehaviour field path: "PathId=452::mSource.mTerms[12].Languages[2]"
        // Normalize intermediate array indices [12] -> [*] while keeping terminal index [2]
        if (entryKey.StartsWith("PathId=", StringComparison.Ordinal))
        {
            int sep = entryKey.IndexOf("::", StringComparison.Ordinal);
            if (sep > 0 && sep + 2 < entryKey.Length)
            {
                string fieldPath = entryKey[(sep + 2)..];
                if (!fieldPath.StartsWith("bin:", StringComparison.Ordinal))
                {
                    string normalized = NonTerminalArrayIndexRegex().Replace(fieldPath, "[*]");
                    return $"mb:{normalized}";
                }
            }
        }

        // 3. JSON leaf property: "$.items[4].title_en" -> "json_leaf:title_en"
        if (entryKey.StartsWith("$.", StringComparison.Ordinal))
        {
            int lastDot = entryKey.LastIndexOf('.');
            if (lastDot >= 0 && lastDot + 1 < entryKey.Length)
            {
                string leaf = entryKey[(lastDot + 1)..];
                if (!leaf.Contains('['))
                {
                    return $"json_leaf:{leaf}";
                }
            }
        }

        return null;
    }

    private static DetectedLanguage ExtractKeyOrHeaderLanguageHint(string entryKey)
    {
        if (string.IsNullOrEmpty(entryKey))
        {
            return DetectedLanguage.Unknown;
        }

        // Check CSV header tag "hdr=..."
        int hdrIdx = entryKey.IndexOf(";hdr=", StringComparison.Ordinal);
        if (hdrIdx >= 0)
        {
            string hdr = entryKey[(hdrIdx + 5)..];
            return InferLanguageFromIdentifier(hdr);
        }

        // Check JSON / XML / INI leaf name
        int lastSep = entryKey.LastIndexOfAny(['.', '@', ']', ':']);
        string leaf = lastSep >= 0 && lastSep + 1 < entryKey.Length
            ? entryKey[(lastSep + 1)..]
            : entryKey;

        return InferLanguageFromIdentifier(leaf);
    }

    public static DetectedLanguage InferLanguageFromIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return DetectedLanguage.Unknown;
        }

        string[] tokens = identifier
            .ToLowerInvariant()
            .Split(['_', '-', '.', '/', '\\', ' ', '[', ']', '(', ')', ':', ';', '='], StringSplitOptions.RemoveEmptyEntries);

        // Scan tokens from right to left (suffixes like "Localization_EN" or "dialog_jp" carry the language tag at the end)
        for (int i = tokens.Length - 1; i >= 0; i--)
        {
            DetectedLanguage lang = MatchExactLanguageToken(tokens[i]);
            if (lang != DetectedLanguage.Unknown)
            {
                return lang;
            }
        }

        // Also check combined locale pairs like "en-us" -> "enus", "zh-tw" -> "zhtw", "ja-jp" -> "jajp", "ru-ru" -> "ruru"
        string compact = identifier.Replace("_", "", StringComparison.Ordinal)
                                   .Replace("-", "", StringComparison.Ordinal)
                                   .ToLowerInvariant();
        return MatchExactLanguageToken(compact);
    }

    private static DetectedLanguage MatchExactLanguageToken(string token)
    {
        return token switch
        {
            "en" or "eng" or "english" or "enus" or "engb" => DetectedLanguage.English,
            "zh" or "cn" or "chs" or "cht" or "sc" or "tc" or "zhtw" or "zhcn" or "zhhk"
                or "zhhans" or "zhhant" or "chinese" or "schinese" or "tchinese" or "china" => DetectedLanguage.Chinese,
            "ru" or "rus" or "russian" or "ruru" => DetectedLanguage.Russian,
            "ja" or "jp" or "jpn" or "japanese" or "jajp" or "japan" => DetectedLanguage.Japanese,
            "ko" or "kr" or "kor" or "korean" or "kokr"
                or "fr" or "fra" or "french"
                or "de" or "ger" or "german"
                or "es" or "spa" or "spanish"
                or "pt" or "ptbr" or "portuguese"
                or "it" or "italian"
                or "th" or "thai" => DetectedLanguage.Other,
            _ => DetectedLanguage.Unknown
        };
    }

    private sealed class SlotLanguageVotes
    {
        public DetectedLanguage ExplicitHint { get; set; } = DetectedLanguage.Unknown;
        public int LatinRows { get; set; }
        public int JapaneseKanaRows { get; set; }
        public int PureCjkRows { get; set; }
        public int RussianRows { get; set; }
        public int OtherRows { get; set; }

        public DetectedLanguage ResolveDominantLanguage()
        {
            if (ExplicitHint != DetectedLanguage.Unknown)
            {
                return ExplicitHint;
            }

            int total = LatinRows + JapaneseKanaRows + PureCjkRows + RussianRows + OtherRows;
            if (total < 2)
            {
                return DetectedLanguage.Unknown;
            }

            // In a genuine Japanese column, rows containing Hiragana/Katakana are >= pure-Kanji rows (>= 50%)
            int cjkFamily = JapaneseKanaRows + PureCjkRows;
            if (cjkFamily >= LatinRows && cjkFamily >= RussianRows && cjkFamily >= OtherRows && cjkFamily > 0)
            {
                return JapaneseKanaRows >= PureCjkRows
                    ? DetectedLanguage.Japanese
                    : DetectedLanguage.Chinese;
            }

            if (RussianRows >= LatinRows && RussianRows >= OtherRows && RussianRows > 0)
            {
                return DetectedLanguage.Russian;
            }

            if (OtherRows > LatinRows && OtherRows > 0)
            {
                return DetectedLanguage.Other;
            }

            if (LatinRows > 0)
            {
                return DetectedLanguage.English;
            }

            return DetectedLanguage.Unknown;
        }
    }
}
