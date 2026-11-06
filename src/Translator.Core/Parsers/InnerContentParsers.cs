using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Translator.Core.Abstractions;
using Translator.Core.Models;

namespace Translator.Core.Parsers;

public sealed class InnerContentParserRegistry
{
    private readonly IReadOnlyList<IInnerContentParser> _parsers;
    private readonly PlainTextContentParser _fallback = new();

    public InnerContentParserRegistry(IEnumerable<IInnerContentParser>? customParsers = null)
    {
        var list = new List<IInnerContentParser>();
        if (customParsers is not null)
        {
            list.AddRange(customParsers);
        }

        list.Add(new JsonContentParser());
        list.Add(new XmlContentParser());
        list.Add(new CsvTsvContentParser(InnerTextFormat.Tsv, '\t'));
        list.Add(new CsvTsvContentParser(InnerTextFormat.Csv, ','));
        list.Add(new IniKeyValueContentParser());
        _parsers = list;
    }

    public (InnerTextFormat Format, IReadOnlyList<ParsedTextNode> Nodes) ParseBestEffort(
        string fileNameOrHint,
        string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return (InnerTextFormat.PlainText, []);
        }

        ReadOnlySpan<char> span = content.AsSpan();
        foreach (IInnerContentParser parser in _parsers)
        {
            if (parser.CanParse(fileNameOrHint, span))
            {
                try
                {
                    IReadOnlyList<ParsedTextNode> parsed = parser.Parse(content);
                    if (parsed.Count > 0)
                    {
                        return (parser.Format, parsed);
                    }
                }
                catch
                {
                    // Fall through to next candidate parser
                }
            }
        }

        return (_fallback.Format, _fallback.Parse(content));
    }

    public string Reconstruct(
        InnerTextFormat format,
        string originalContent,
        IReadOnlyDictionary<string, string> translations)
    {
        if (string.IsNullOrEmpty(originalContent) || translations.Count == 0)
        {
            return originalContent;
        }

        foreach (IInnerContentParser parser in _parsers)
        {
            if (parser.Format == format && parser is IInnerContentReconstructor reconstructor)
            {
                return reconstructor.Reconstruct(originalContent, translations);
            }
        }

        return _fallback.Reconstruct(originalContent, translations);
    }
}

public sealed class JsonContentParser : IInnerContentParser, IInnerContentReconstructor
{
    public InnerTextFormat Format => InnerTextFormat.Json;

    public bool CanParse(string fileNameOrHint, ReadOnlySpan<char> content)
    {
        ReadOnlySpan<char> trimmed = content.Trim();
        if (trimmed.IsEmpty)
        {
            return false;
        }

        return (trimmed[0] == '{' && trimmed[^1] == '}') ||
               (trimmed[0] == '[' && trimmed[^1] == ']');
    }

    public IReadOnlyList<ParsedTextNode> Parse(string content)
    {
        var options = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        using JsonDocument doc = JsonDocument.Parse(content, options);
        var results = new List<ParsedTextNode>();
        WalkElement(doc.RootElement, "$", parentContext: "JSON Root", results);
        return results;
    }

    private static void WalkElement(
        JsonElement element,
        string currentPath,
        string parentContext,
        List<ParsedTextNode> output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // Try capturing an ID/Key sibling for better translator context
                string contextHint = parentContext;
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String &&
                        (prop.Name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                         prop.Name.Equals("key", StringComparison.OrdinalIgnoreCase) ||
                         prop.Name.Equals("speaker", StringComparison.OrdinalIgnoreCase)))
                    {
                        string? val = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            contextHint = $"{prop.Name}={val}";
                            break;
                        }
                    }
                }

                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    string childPath = IsSimpleJsonIdentifier(prop.Name)
                        ? $"{currentPath}.{prop.Name}"
                        : $"{currentPath}[\"{EscapeJsonKey(prop.Name)}\"]";
                    WalkElement(prop.Value, childPath, $"{contextHint} ({prop.Name})", output);
                }
                break;

            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WalkElement(item, $"{currentPath}[{index}]", $"{parentContext}[{index}]", output);
                    index++;
                }
                break;

            case JsonValueKind.String:
                string? str = element.GetString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    output.Add(new ParsedTextNode(currentPath, str, parentContext));
                }
                break;
        }
    }

    private static bool IsSimpleJsonIdentifier(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (!(char.IsLetter(key[0]) || key[0] == '_'))
        {
            return false;
        }

        for (int i = 1; i < key.Length; i++)
        {
            char c = key[i];
            if (!(char.IsLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string EscapeJsonKey(string key) =>
        key.Replace("\\", "\\\\", StringComparison.Ordinal)
           .Replace("\"", "\\\"", StringComparison.Ordinal);

    public string Reconstruct(
        string originalContent,
        IReadOnlyDictionary<string, string> translations)
    {
        if (string.IsNullOrWhiteSpace(originalContent) || translations.Count == 0)
        {
            return originalContent;
        }

        var docOptions = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        JsonNode? root = JsonNode.Parse(originalContent, nodeOptions: null, docOptions);
        if (root is null)
        {
            return originalContent;
        }

        bool modified = ApplyTranslationsToJsonNode(ref root, "$", translations);
        if (!modified || root is null)
        {
            return originalContent;
        }

        bool isMultiline = originalContent.Contains('\n');
        var writeOptions = new JsonSerializerOptions
        {
            WriteIndented = isMultiline,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        string result = root.ToJsonString(writeOptions);
        if (originalContent.Contains("\r\n", StringComparison.Ordinal) && !result.Contains("\r\n", StringComparison.Ordinal))
        {
            result = result.Replace("\n", "\r\n", StringComparison.Ordinal);
        }
        else if (!originalContent.Contains("\r\n", StringComparison.Ordinal) && result.Contains("\r\n", StringComparison.Ordinal))
        {
            result = result.Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        return result;
    }

    private static bool ApplyTranslationsToJsonNode(
        ref JsonNode? node,
        string currentPath,
        IReadOnlyDictionary<string, string> translations)
    {
        if (node is null)
        {
            return false;
        }

        bool anyModified = false;
        if (node is JsonObject obj)
        {
            List<string> keys = obj.Select(kvp => kvp.Key).ToList();
            foreach (string propName in keys)
            {
                JsonNode? child = obj[propName];
                if (child is null)
                {
                    continue;
                }

                string childPath = IsSimpleJsonIdentifier(propName)
                    ? $"{currentPath}.{propName}"
                    : $"{currentPath}[\"{EscapeJsonKey(propName)}\"]";

                if (child is JsonValue val && val.TryGetValue<string>(out _))
                {
                    if (translations.TryGetValue(childPath, out string? translated) &&
                        !string.IsNullOrEmpty(translated))
                    {
                        obj[propName] = JsonValue.Create(translated);
                        anyModified = true;
                    }
                }
                else
                {
                    JsonNode? mutableChild = child;
                    if (ApplyTranslationsToJsonNode(ref mutableChild, childPath, translations))
                    {
                        anyModified = true;
                    }
                }
            }
        }
        else if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                JsonNode? item = arr[i];
                if (item is null)
                {
                    continue;
                }

                string itemPath = $"{currentPath}[{i}]";
                if (item is JsonValue val && val.TryGetValue<string>(out _))
                {
                    if (translations.TryGetValue(itemPath, out string? translated) &&
                        !string.IsNullOrEmpty(translated))
                    {
                        arr[i] = JsonValue.Create(translated);
                        anyModified = true;
                    }
                }
                else
                {
                    JsonNode? mutableItem = item;
                    if (ApplyTranslationsToJsonNode(ref mutableItem, itemPath, translations))
                    {
                        anyModified = true;
                    }
                }
            }
        }
        else if (node is JsonValue rootVal && rootVal.TryGetValue<string>(out _))
        {
            if (translations.TryGetValue(currentPath, out string? translated) &&
                !string.IsNullOrEmpty(translated))
            {
                node = JsonValue.Create(translated);
                anyModified = true;
            }
        }

        return anyModified;
    }
}

public sealed class XmlContentParser : IInnerContentParser, IInnerContentReconstructor
{
    private static readonly HashSet<string> TranslatableAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "value", "label", "title", "desc", "description",
        "caption", "message", "content", "tooltip", "prompt", "name", "en", "string"
    };

    public InnerTextFormat Format => InnerTextFormat.Xml;

    public bool CanParse(string fileNameOrHint, ReadOnlySpan<char> content)
    {
        ReadOnlySpan<char> trimmed = content.TrimStart();
        if (trimmed.IsEmpty || trimmed[0] != '<')
        {
            return false;
        }

        // Avoid treating plain rich-text strings like "<color=red>Hello</color> world" as full XML files
        if (fileNameOrHint.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        ReadOnlySpan<char> endTrimmed = content.TrimEnd();
        return endTrimmed.Length > 4 && endTrimmed[^1] == '>' && trimmed.Contains('\n');
    }

    public IReadOnlyList<ParsedTextNode> Parse(string content)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            CheckCharacters = false
        };

        using var stringReader = new StringReader(content);
        using var xmlReader = XmlReader.Create(stringReader, settings);
        XDocument doc = XDocument.Load(xmlReader, LoadOptions.None);

        var results = new List<ParsedTextNode>();
        if (doc.Root is not null)
        {
            WalkElement(doc.Root, $"/{doc.Root.Name.LocalName}[0]", results);
        }

        return results;
    }

    private static void WalkElement(XElement element, string xpath, List<ParsedTextNode> output)
    {
        string? idAttr = element.Attribute("id")?.Value
                      ?? element.Attribute("name")?.Value
                      ?? element.Attribute("key")?.Value;
        string context = idAttr is not null
            ? $"<{element.Name.LocalName} key=\"{idAttr}\">"
            : $"<{element.Name.LocalName}>";

        foreach (XAttribute attr in element.Attributes())
        {
            string attrName = attr.Name.LocalName;
            if (TranslatableAttributes.Contains(attrName) &&
                !attrName.Equals("id", StringComparison.OrdinalIgnoreCase) &&
                !attrName.Equals("key", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(attr.Value))
            {
                // Skip "name" attribute when it also serves as technical key without spaces
                if (attrName.Equals("name", StringComparison.OrdinalIgnoreCase) &&
                    !attr.Value.Contains(' ') &&
                    !string.IsNullOrWhiteSpace(element.Value))
                {
                    continue;
                }

                output.Add(new ParsedTextNode($"xml:{xpath}@{attrName}", attr.Value, $"{context} @{attrName}"));
            }
        }

        if (!element.HasElements)
        {
            string val = element.Value;
            if (!string.IsNullOrWhiteSpace(val))
            {
                output.Add(new ParsedTextNode($"xml:{xpath}#text", val, context));
            }
            return;
        }

        // Mixed content text nodes or child elements grouped by tag occurrence index
        var tagCounters = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (XElement child in element.Elements())
        {
            string tag = child.Name.LocalName;
            tagCounters.TryGetValue(tag, out int idx);
            tagCounters[tag] = idx + 1;
            WalkElement(child, $"{xpath}/{tag}[{idx}]", output);
        }
    }

    public string Reconstruct(
        string originalContent,
        IReadOnlyDictionary<string, string> translations)
    {
        if (string.IsNullOrWhiteSpace(originalContent) || translations.Count == 0)
        {
            return originalContent;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            CheckCharacters = false
        };

        using var stringReader = new StringReader(originalContent);
        using var xmlReader = XmlReader.Create(stringReader, settings);
        XDocument doc = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);

        if (doc.Root is null)
        {
            return originalContent;
        }

        bool modified = ApplyTranslationsToXmlElement(doc.Root, $"/{doc.Root.Name.LocalName}[0]", translations);
        if (!modified)
        {
            return originalContent;
        }

        bool hasXmlDeclaration = doc.Declaration is not null ||
                                 originalContent.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder(originalContent.Length + 128);
        if (hasXmlDeclaration && doc.Declaration is not null)
        {
            sb.Append(doc.Declaration.ToString());
            string newline = originalContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            sb.Append(newline);
        }

        sb.Append(doc.ToString(SaveOptions.DisableFormatting));
        return sb.ToString();
    }

    private static bool ApplyTranslationsToXmlElement(
        XElement element,
        string xpath,
        IReadOnlyDictionary<string, string> translations)
    {
        bool anyModified = false;

        foreach (XAttribute attr in element.Attributes())
        {
            string attrName = attr.Name.LocalName;
            string attrKey = $"xml:{xpath}@{attrName}";
            if (translations.TryGetValue(attrKey, out string? translatedAttr) &&
                !string.IsNullOrEmpty(translatedAttr))
            {
                attr.Value = translatedAttr;
                anyModified = true;
            }
        }

        if (!element.HasElements)
        {
            string textKey = $"xml:{xpath}#text";
            if (translations.TryGetValue(textKey, out string? translatedText) &&
                !string.IsNullOrEmpty(translatedText))
            {
                element.Value = translatedText;
                anyModified = true;
            }

            return anyModified;
        }

        var tagCounters = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (XElement child in element.Elements())
        {
            string tag = child.Name.LocalName;
            tagCounters.TryGetValue(tag, out int idx);
            tagCounters[tag] = idx + 1;
            if (ApplyTranslationsToXmlElement(child, $"{xpath}/{tag}[{idx}]", translations))
            {
                anyModified = true;
            }
        }

        return anyModified;
    }
}

public sealed class CsvTsvContentParser : IInnerContentParser, IInnerContentReconstructor
{
    private readonly char _delimiter;

    public CsvTsvContentParser(InnerTextFormat format, char delimiter)
    {
        Format = format;
        _delimiter = delimiter;
    }

    public InnerTextFormat Format { get; }

    public bool CanParse(string fileNameOrHint, ReadOnlySpan<char> content)
    {
        bool extMatch = (_delimiter == ',' && fileNameOrHint.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) ||
                        (_delimiter == '\t' && fileNameOrHint.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase));

        ReadOnlySpan<char> trimmed = content.Trim();
        if (trimmed.IsEmpty || trimmed[0] is '{' or '[' or '<')
        {
            return false;
        }

        if (extMatch)
        {
            return trimmed.Contains(_delimiter);
        }

        // Sample up to 16 KB using quote-aware table parsing (handles multiline quoted cells & unquoted CJK commas!)
        int sampleLen = Math.Min(trimmed.Length, 16384);
        string sample = trimmed[..sampleLen].ToString();
        List<List<string>> rows = ParseTable(sample, _delimiter, maxRows: 12);
        if (rows.Count < 2)
        {
            return false;
        }

        // If sample cut mid-row on the last row, ignore the trailing partial row when >= 3 rows exist
        if (sampleLen < trimmed.Length && rows.Count >= 3)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        int multiColRows = rows.Count(r => r.Count >= 2);
        if ((double)multiColRows / rows.Count < 0.75)
        {
            return false;
        }

        List<int> colCounts = rows.Select(r => r.Count).OrderBy(x => x).ToList();
        int medianCols = colCounts[colCounts.Count / 2];

        if (_delimiter == '\t' && medianCols >= 2)
        {
            return true;
        }

        // For comma-separated without .csv extension: avoid false-positives on normal English prose paragraphs
        int keyFirstColRows = rows.Count(r => r.Count >= 2 && IsLikelyKeyCell(r[0].Trim()));
        bool hasIdColumn = (double)keyFirstColRows / rows.Count >= 0.6;
        bool tableNameHint = fileNameOrHint.Contains("excel", StringComparison.OrdinalIgnoreCase) ||
                             fileNameOrHint.Contains("csv", StringComparison.OrdinalIgnoreCase) ||
                             fileNameOrHint.Contains("table", StringComparison.OrdinalIgnoreCase);

        return (medianCols >= 4 && hasIdColumn) ||
               (medianCols >= 3 && (hasIdColumn || tableNameHint)) ||
               (medianCols == 2 && hasIdColumn && rows.All(r => r.Count == 2));
    }

    public IReadOnlyList<ParsedTextNode> Parse(string content)
    {
        List<List<string>> rows = ParseTable(content, _delimiter);
        if (rows.Count == 0)
        {
            return [];
        }

        bool hasHeader = LooksLikeHeaderRow(rows[0], rows.Count > 1 ? rows[1] : null);
        IReadOnlyList<string> headers = hasHeader ? rows[0] : [];
        int startRow = hasHeader ? 1 : 0;
        string prefix = _delimiter == '\t' ? "tsv" : "csv";

        var results = new List<ParsedTextNode>();
        for (int r = startRow; r < rows.Count; r++)
        {
            List<string> row = rows[r];
            if (row.Count == 0)
            {
                continue;
            }

            string rowKey = row[0].Trim();
            bool col0IsId = row.Count >= 2 && IsLikelyKeyCell(rowKey);

            int startCol = col0IsId ? 1 : 0;
            for (int c = startCol; c < row.Count; c++)
            {
                string cell = row[c];
                if (string.IsNullOrWhiteSpace(cell))
                {
                    continue;
                }

                string colHeader = c < headers.Count && !string.IsNullOrWhiteSpace(headers[c])
                    ? headers[c].Trim()
                    : $"Col{c}";

                string safeRowKey = SanitizeKeyToken(col0IsId ? rowKey : $"Row{r}");
                string innerKey = $"{prefix}:row={r};col={c};key={safeRowKey};hdr={SanitizeKeyToken(colHeader)}";
                string context = col0IsId
                    ? $"Row {r} | Key={rowKey} | Column={colHeader}"
                    : $"Row {r} | Column={colHeader}";

                results.Add(new ParsedTextNode(innerKey, cell, context));
            }
        }

        return results;
    }

    public static List<List<string>> ParseTable(string content, char delimiter, int maxRows = int.MaxValue)
    {
        var rows = new List<List<string>>();
        var currentRow = new List<string>();
        var currentCell = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        currentCell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    currentCell.Append(c);
                }
            }
            else
            {
                if (c == '"' && IsOnlyWhitespace(currentCell))
                {
                    currentCell.Clear();
                    inQuotes = true;
                }
                else if (c == delimiter)
                {
                    currentRow.Add(currentCell.ToString());
                    currentCell.Clear();
                }
                else if (c is '\r' or '\n')
                {
                    if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
                    {
                        i++;
                    }

                    currentRow.Add(currentCell.ToString());
                    currentCell.Clear();

                    if (currentRow.Count > 1 || (currentRow.Count == 1 && !string.IsNullOrWhiteSpace(currentRow[0])))
                    {
                        rows.Add(currentRow);
                        if (rows.Count >= maxRows)
                        {
                            return rows;
                        }
                    }
                    currentRow = [];
                }
                else
                {
                    currentCell.Append(c);
                }
            }
        }

        if (currentCell.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(currentCell.ToString());
            if (currentRow.Count > 1 || (currentRow.Count == 1 && !string.IsNullOrWhiteSpace(currentRow[0])))
            {
                rows.Add(currentRow);
            }
        }

        return rows;
    }

    private static bool IsOnlyWhitespace(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++)
        {
            if (sb[i] is not (' ' or '\t'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeHeaderRow(List<string> firstRow, List<string>? secondRow)
    {
        if (firstRow.Count < 2 || secondRow is null)
        {
            return false;
        }

        // Real header rows do not contain dialogue sentence punctuation!
        if (firstRow.Any(cell => cell.IndexOfAny(['.', '!', '?', '！', '？', '。', '…', '\n']) >= 0))
        {
            return false;
        }

        string firstCell = firstRow[0].Trim().ToLowerInvariant();
        if (firstCell is "key" or "id" or "term" or "string_id" or "stringid" or "name" or "code" or "index")
        {
            return true;
        }

        int recognizedHeaderTokens = firstRow.Count(cell =>
            Filters.LanguageDetector.InferLanguageFromIdentifier(cell.Trim()) != DetectedLanguage.Unknown ||
            cell.Trim().ToLowerInvariant() is "text" or "value" or "desc" or "description" or "context" or "note" or "comment" or "speaker");

        return recognizedHeaderTokens >= 2;
    }

    private static bool IsLikelyKeyCell(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 80)
        {
            return false;
        }

        return !value.Contains(' ') || int.TryParse(value, out _);
    }

    private static string SanitizeKeyToken(string token) =>
        token.Replace(";", "_", StringComparison.Ordinal)
             .Replace("=", "_", StringComparison.Ordinal)
             .Replace("\r", "", StringComparison.Ordinal)
             .Replace("\n", " ", StringComparison.Ordinal);

    public string Reconstruct(
        string originalContent,
        IReadOnlyDictionary<string, string> translations)
    {
        if (string.IsNullOrEmpty(originalContent) || translations.Count == 0)
        {
            return originalContent;
        }

        List<List<string>> rows = ParseTable(originalContent, _delimiter);
        if (rows.Count == 0)
        {
            return originalContent;
        }

        bool hasHeader = LooksLikeHeaderRow(rows[0], rows.Count > 1 ? rows[1] : null);
        IReadOnlyList<string> headers = hasHeader ? rows[0] : [];
        string prefix = _delimiter == '\t' ? "tsv" : "csv";

        // Also build a coordinate lookup for any key of form csv:row=R;col=C...
        var coordinateOverrides = new Dictionary<(int Row, int Col), string>();
        foreach ((string k, string v) in translations)
        {
            if (string.IsNullOrEmpty(v))
            {
                continue;
            }

            if (TryExtractCsvRowCol(k, out int r, out int c))
            {
                coordinateOverrides[(r, c)] = v;
            }
        }

        bool modified = false;
        for (int r = 0; r < rows.Count; r++)
        {
            List<string> row = rows[r];
            if (row.Count == 0)
            {
                continue;
            }

            string rowKey = row[0].Trim();
            bool col0IsId = row.Count >= 2 && IsLikelyKeyCell(rowKey);

            for (int c = 0; c < row.Count; c++)
            {
                string colHeader = c < headers.Count && !string.IsNullOrWhiteSpace(headers[c])
                    ? headers[c].Trim()
                    : $"Col{c}";

                string safeRowKey = SanitizeKeyToken(col0IsId ? rowKey : $"Row{r}");
                string exactKey = $"{prefix}:row={r};col={c};key={safeRowKey};hdr={SanitizeKeyToken(colHeader)}";

                if (translations.TryGetValue(exactKey, out string? translated) && !string.IsNullOrEmpty(translated))
                {
                    row[c] = translated;
                    modified = true;
                }
                else if (coordinateOverrides.TryGetValue((r, c), out string? coordVal) && !string.IsNullOrEmpty(coordVal))
                {
                    row[c] = coordVal;
                    modified = true;
                }
            }
        }

        if (!modified)
        {
            return originalContent;
        }

        string newline = originalContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        bool endsWithNewline = originalContent.EndsWith('\n') || originalContent.EndsWith('\r');

        var sb = new StringBuilder(originalContent.Length + 128);
        for (int r = 0; r < rows.Count; r++)
        {
            List<string> row = rows[r];
            for (int c = 0; c < row.Count; c++)
            {
                if (c > 0)
                {
                    sb.Append(_delimiter);
                }

                sb.Append(FormatTableCell(row[c], _delimiter));
            }

            if (r + 1 < rows.Count || endsWithNewline)
            {
                sb.Append(newline);
            }
        }

        return sb.ToString();
    }

    private static bool TryExtractCsvRowCol(string key, out int row, out int col)
    {
        row = -1;
        col = -1;
        int rowIdx = key.IndexOf("row=", StringComparison.OrdinalIgnoreCase);
        int colIdx = key.IndexOf("col=", StringComparison.OrdinalIgnoreCase);
        if (rowIdx < 0 || colIdx < 0)
        {
            return false;
        }

        rowIdx += 4;
        int rowEnd = key.IndexOf(';', rowIdx);
        ReadOnlySpan<char> rowSpan = rowEnd > rowIdx ? key.AsSpan(rowIdx, rowEnd - rowIdx) : key.AsSpan(rowIdx);

        colIdx += 4;
        int colEnd = key.IndexOf(';', colIdx);
        ReadOnlySpan<char> colSpan = colEnd > colIdx ? key.AsSpan(colIdx, colEnd - colIdx) : key.AsSpan(colIdx);

        return int.TryParse(rowSpan, out row) && int.TryParse(colSpan, out col);
    }

    private static string FormatTableCell(string value, char delimiter)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0)
        {
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value;
    }
}

public sealed class IniKeyValueContentParser : IInnerContentParser, IInnerContentReconstructor
{
    public InnerTextFormat Format => InnerTextFormat.IniKeyValue;

    public bool CanParse(string fileNameOrHint, ReadOnlySpan<char> content)
    {
        bool extMatch = fileNameOrHint.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ||
                        fileNameOrHint.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) ||
                        fileNameOrHint.EndsWith(".properties", StringComparison.OrdinalIgnoreCase);

        int nonEmpty = 0;
        int kvLines = 0;
        foreach (ReadOnlySpan<char> rawLine in content.EnumerateLines())
        {
            ReadOnlySpan<char> line = rawLine.Trim();
            if (line.IsEmpty || line[0] is '#' or ';')
            {
                continue;
            }

            if (line.StartsWith("//"))
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                continue;
            }

            nonEmpty++;
            int eq = line.IndexOf('=');
            if (eq > 0 && eq < 80 && !line[..eq].Trim().Contains(' '))
            {
                kvLines++;
            }

            if (nonEmpty >= 25)
            {
                break;
            }
        }

        if (nonEmpty == 0)
        {
            return false;
        }

        double ratio = (double)kvLines / nonEmpty;
        return extMatch ? ratio >= 0.4 : (nonEmpty >= 2 && ratio >= 0.75);
    }

    public IReadOnlyList<ParsedTextNode> Parse(string content)
    {
        var results = new List<ParsedTextNode>();
        string currentSection = string.Empty;
        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or ';' || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                currentSection = trimmed[1..^1].Trim();
                continue;
            }

            int eq = trimmed.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = trimmed[..eq].Trim();
            string val = trimmed[(eq + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(val))
            {
                continue;
            }

            string fullKey = string.IsNullOrEmpty(currentSection)
                ? $"ini:{key}"
                : $"ini:[{currentSection}]{key}";
            string context = string.IsNullOrEmpty(currentSection)
                ? $"Key={key} (Line {i + 1})"
                : $"[{currentSection}] {key} (Line {i + 1})";

            results.Add(new ParsedTextNode(fullKey, val, context));
        }

        return results;
    }

    public string Reconstruct(
        string originalContent,
        IReadOnlyDictionary<string, string> translations)
    {
        if (string.IsNullOrEmpty(originalContent) || translations.Count == 0)
        {
            return originalContent;
        }

        string newline = originalContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = originalContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string currentSection = string.Empty;
        bool modified = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string rawLine = lines[i];
            string trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or ';' || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                currentSection = trimmed[1..^1].Trim();
                continue;
            }

            int eqInRaw = rawLine.IndexOf('=');
            if (eqInRaw <= 0)
            {
                continue;
            }

            string key = rawLine[..eqInRaw].Trim();
            string fullKey = string.IsNullOrEmpty(currentSection)
                ? $"ini:{key}"
                : $"ini:[{currentSection}]{key}";

            if (translations.TryGetValue(fullKey, out string? translated) && !string.IsNullOrEmpty(translated))
            {
                int valStart = eqInRaw + 1;
                while (valStart < rawLine.Length && rawLine[valStart] is ' ' or '\t')
                {
                    valStart++;
                }

                lines[i] = $"{rawLine[..valStart]}{translated}";
                modified = true;
            }
        }

        return modified ? string.Join(newline, lines) : originalContent;
    }
}

public sealed class PlainTextContentParser : IInnerContentParser, IInnerContentReconstructor
{
    public InnerTextFormat Format => InnerTextFormat.PlainText;

    public bool CanParse(string fileNameOrHint, ReadOnlySpan<char> content) => true;

    public IReadOnlyList<ParsedTextNode> Parse(string content)
    {
        var results = new List<ParsedTextNode>();
        string normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] lines = normalized.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            results.Add(new ParsedTextNode($"line:{i}", line, $"Line {i + 1}"));
        }

        return results;
    }

    public string Reconstruct(
        string originalContent,
        IReadOnlyDictionary<string, string> translations)
    {
        if (string.IsNullOrEmpty(originalContent) || translations.Count == 0)
        {
            return originalContent;
        }

        string newline = originalContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = originalContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        bool modified = false;

        for (int i = 0; i < lines.Length; i++)
        {
            if (translations.TryGetValue($"line:{i}", out string? translated) &&
                !string.IsNullOrEmpty(translated))
            {
                lines[i] = translated;
                modified = true;
            }
        }

        return modified ? string.Join(newline, lines) : originalContent;
    }
}
