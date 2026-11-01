using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace Translator.Core.Filters;

public static partial class TextHeuristicScorer
{
    private static readonly string[] HighValueKeywords =
    [
        "localization", "localize", "localized", "i2languages", "stringtable",
        "sharedtabledata", "language", "languages", "dialogue", "dialog",
        "conversation", "subtitle", "subtitles", "caption", "narration",
        "story", "quest", "i18n", "l10n", "translate", "translation",
        "strings", "texttable", "uitext", "speech", "glossary",
        "en_us", "en-us", "en_gb", "english", "vietnamese", "vi_vn"
    ];

    private static readonly string[] MediumValueKeywords =
    [
        "text", "desc", "description", "title", "tooltip", "hint",
        "tutorial", "message", "mail", "note", "lore", "codex",
        "item", "skill", "character", "npc", "menu", "prompt"
    ];

    private static readonly string[] NoiseTargetKeywords =
    [
        "shader", "hlsl", "glsl", "cginc", "compute",
        "linebreaking leading", "linebreaking following",
        "tmp settings", "tmp_settings", "emojione",
        "unityservicesprojectconfiguration", "scriptingassemblies",
        "runtimeinitializeonloads", "billingmode",
        "inputactions", "lightingdata", "navmesh", "occlusion",
        "link.xml", "csc.rsp", "mcs.rsp", "boot.config",
        "catalog_", "settings.json"
    ];

    private static readonly string[] NoiseFieldKeyKeywords =
    [
        "guid", "fileid", "pathid", "m_script", "m_shader",
        "spritename", "spritepath", "iconpath", "iconname",
        "audioclip", "soundname", "sfx", "bgm", "prefab",
        "animname", "animation", "hash", "checksum", "checksums",
        "assemblyname", "classname", "methodname", "layername", "tagname"
    ];

    private static readonly string[] NoiseExtensions =
    [
        ".prefab", ".mat", ".shader", ".png", ".jpg", ".jpeg", ".tga", ".psd",
        ".ogg", ".wav", ".mp3", ".bank", ".fsb", ".cs", ".dll", ".so",
        ".bundle", ".unity", ".unity3d", ".asset", ".assets", ".anim",
        ".controller", ".overridecontroller", ".ttf", ".otf", ".fbx", ".obj"
    ];

    [GeneratedRegex(@"^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$|^[0-9a-fA-F]{24,64}$", RegexOptions.Compiled)]
    private static partial Regex HexOrGuidRegex();

    [GeneratedRegex(@"^(Assets|Resources|Packages|Library|ProjectSettings|Prefabs|Scenes|Shaders|StreamingAssets)[/\\]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UnityPathRegex();

    public static bool TryDecodeReadableText(ReadOnlySpan<byte> bytes, out string text)
    {
        text = string.Empty;
        if (bytes.IsEmpty)
        {
            return false;
        }

        // Reject NUL-heavy binary blobs unless UTF-16 BOM is present
        bool hasUtf16LeBom = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE;
        bool hasUtf16BeBom = bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF;
        bool hasUtf8Bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

        try
        {
            if (hasUtf16LeBom)
            {
                text = Encoding.Unicode.GetString(bytes[2..]);
            }
            else if (hasUtf16BeBom)
            {
                text = Encoding.BigEndianUnicode.GetString(bytes[2..]);
            }
            else
            {
                int checkLen = Math.Min(bytes.Length, 8192);
                int nulCount = 0;
                for (int i = 0; i < checkLen; i++)
                {
                    if (bytes[i] == 0)
                    {
                        nulCount++;
                    }
                }

                // Standard UTF-8 text should not contain embedded NUL bytes
                if (nulCount > 1)
                {
                    return false;
                }

                var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                ReadOnlySpan<byte> slice = hasUtf8Bom ? bytes[3..] : bytes;
                text = utf8Strict.GetString(slice);
            }
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Check control character ratio
        int sampleLength = Math.Min(text.Length, 4096);
        int badControlCount = 0;
        for (int i = 0; i < sampleLength; i++)
        {
            char c = text[i];
            if (c < 0x20 && c != '\r' && c != '\n' && c != '\t')
            {
                badControlCount++;
            }
            else if (c == '\uFFFD')
            {
                badControlCount += 4;
            }
        }

        return (double)badControlCount / sampleLength <= 0.01;
    }

    public static (int Score, bool IsLikelyNoise) ScoreTargetMetadata(
        string displayName,
        string? scriptClassName = null,
        string? relativePath = null)
    {
        string combined = $"{displayName} {scriptClassName} {relativePath}".ToLowerInvariant();

        foreach (string noise in NoiseTargetKeywords)
        {
            if (combined.Contains(noise, StringComparison.Ordinal))
            {
                return (10, true);
            }
        }

        if (displayName.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase) ||
            displayName.EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase) ||
            displayName.EndsWith(".skel", StringComparison.OrdinalIgnoreCase))
        {
            return (8, true);
        }

        int score = 45;

        foreach (string kw in HighValueKeywords)
        {
            if (combined.Contains(kw, StringComparison.Ordinal))
            {
                score += 40;
                break;
            }
        }

        foreach (string kw in MediumValueKeywords)
        {
            if (combined.Contains(kw, StringComparison.Ordinal))
            {
                score += 15;
                break;
            }
        }

        return (Math.Clamp(score, 0, 100), false);
    }

    public static (int Score, bool IsSystemNoise) EvaluateString(string? rawValue, string? fieldOrKeyHint = null)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return (0, true);
        }

        string trimmed = rawValue.Trim();
        if (trimmed.Length == 0)
        {
            return (0, true);
        }

        // Control chars check
        int letterCount = 0;
        int cjkOrNonAsciiLetterCount = 0;
        int spaceCount = 0;
        int underscoreCount = 0;
        int slashCount = 0;
        int digitCount = 0;

        foreach (char c in trimmed)
        {
            if (c < 0x20 && c != '\r' && c != '\n' && c != '\t')
            {
                return (0, true);
            }

            if (char.IsLetter(c))
            {
                letterCount++;
                if (c > 0x007F)
                {
                    cjkOrNonAsciiLetterCount++;
                }
            }
            else if (c == ' ')
            {
                spaceCount++;
            }
            else if (c == '_')
            {
                underscoreCount++;
            }
            else if (c is '/' or '\\')
            {
                slashCount++;
            }
            else if (char.IsDigit(c))
            {
                digitCount++;
            }
        }

        if (letterCount == 0)
        {
            return (0, true);
        }

        // Single ASCII letter is almost always an enum flag, format specifier, or hotkey symbol
        if (trimmed.Length == 1 && cjkOrNonAsciiLetterCount == 0)
        {
            return (15, true);
        }

        // Check field/key hints for internal asset references
        if (!string.IsNullOrWhiteSpace(fieldOrKeyHint))
        {
            string lowerKey = fieldOrKeyHint.ToLowerInvariant();
            string leafKey = lowerKey;
            int lastDot = leafKey.LastIndexOf('.');
            if (lastDot >= 0 && lastDot + 1 < leafKey.Length)
            {
                leafKey = leafKey[(lastDot + 1)..];
            }

            foreach (string nk in NoiseFieldKeyKeywords)
            {
                if (leafKey == nk || (leafKey.EndsWith(nk, StringComparison.Ordinal) && spaceCount == 0))
                {
                    return (10, true);
                }
            }
        }

        // Hex / GUID
        if (spaceCount == 0 && HexOrGuidRegex().IsMatch(trimmed))
        {
            return (5, true);
        }

        // URLs or Unity asset paths
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            UnityPathRegex().IsMatch(trimmed))
        {
            return (10, true);
        }

        if (spaceCount == 0)
        {
            foreach (string ext in NoiseExtensions)
            {
                if (trimmed.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    return (10, true);
                }
            }

            // Typical C# / Unity / Shader identifiers
            if (trimmed.StartsWith("m_", StringComparison.Ordinal) ||
                trimmed.StartsWith("UNITY_", StringComparison.Ordinal) ||
                trimmed.StartsWith("System.", StringComparison.Ordinal) ||
                trimmed.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                trimmed.StartsWith("TMPro.", StringComparison.Ordinal) ||
                trimmed.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                trimmed.StartsWith("Cysharp.", StringComparison.Ordinal) ||
                trimmed.StartsWith("DG.Tweening", StringComparison.Ordinal) ||
                trimmed.StartsWith("get_", StringComparison.Ordinal) ||
                trimmed.StartsWith("set_", StringComparison.Ordinal) ||
                trimmed.StartsWith("<", StringComparison.Ordinal) && trimmed.EndsWith(">", StringComparison.Ordinal) && !trimmed.Contains('/'))
            {
                return (8, true);
            }

            // Path-like token without spaces (e.g. "UI/Popups/ShopWindow")
            if (slashCount >= 1 && cjkOrNonAsciiLetterCount == 0 && !trimmed.Contains('<'))
            {
                return (15, true);
            }

            // Snake_case identifier (e.g. "player_max_health_bonus")
            if (underscoreCount >= 1 && cjkOrNonAsciiLetterCount == 0)
            {
                return (18, true);
            }

            // CamelCase / PascalCase code symbol without spaces (e.g. "OnPlayerHealthChanged")
            if (cjkOrNonAsciiLetterCount == 0 && HasInternalCamelCaseTransition(trimmed) && trimmed.Length >= 8)
            {
                return (20, true);
            }

            // Mostly digits + ASCII code ID (e.g. "ITEM00421")
            if (cjkOrNonAsciiLetterCount == 0 && digitCount >= 2 && letterCount <= 6)
            {
                return (15, true);
            }
        }

        // Spine atlas or raw shader code snippet detection
        if (trimmed.Contains("size:", StringComparison.Ordinal) && trimmed.Contains("orig:", StringComparison.Ordinal) && trimmed.Contains("rotate:", StringComparison.Ordinal))
        {
            return (5, true);
        }

        if (trimmed.Contains("#pragma ", StringComparison.Ordinal) || trimmed.Contains("SV_Target", StringComparison.Ordinal))
        {
            return (5, true);
        }

        // Compute natural language score
        int score = 50;
        if (spaceCount >= 1)
        {
            score += 20;
        }

        if (spaceCount >= 3)
        {
            score += 10;
        }

        if (cjkOrNonAsciiLetterCount > 0)
        {
            score += 20;
        }

        if (trimmed.IndexOfAny(['.', '!', '?', ',', ':', ';', '…', '“', '”']) >= 0)
        {
            score += 10;
        }

        if (trimmed.Contains("<color", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("{0}", StringComparison.Ordinal))
        {
            score += 10;
        }

        return (Math.Clamp(score, 0, 100), false);
    }

    private static bool HasInternalCamelCaseTransition(string token)
    {
        for (int i = 1; i < token.Length; i++)
        {
            if (char.IsLower(token[i - 1]) && char.IsUpper(token[i]))
            {
                return true;
            }
        }

        return false;
    }
}
