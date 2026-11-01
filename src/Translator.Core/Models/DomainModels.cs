using System.Text.Json.Serialization;

namespace Translator.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TextSourceKind
{
    LooseFile,
    TextAsset,
    MonoBehaviour,
    CodeString
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InnerTextFormat
{
    PlainText,
    Json,
    Csv,
    Tsv,
    Xml,
    IniKeyValue,
    StructuredFields
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DetectedLanguage
{
    Unknown,
    English,
    Chinese,
    Russian,
    Japanese,
    Other
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceLanguageFilter
{
    All = 0,
    English = 1,
    Chinese = 2,
    Russian = 3,
    Japanese = 4
}

public sealed record GameProfile(
    string GameName,
    string RootPath,
    string DataFolderPath,
    string EngineName,
    string EngineVersion,
    string ScriptingBackend,
    string? ManagedFolderPath = null,
    string? GlobalMetadataPath = null,
    string? GameAssemblyPath = null);

public sealed record EncryptedFileWarning(
    string RelativePath,
    string FileCategory,
    string Reason);

public sealed record EntryLocation
{
    public required string ContainerRelativePath { get; init; }
    public string? BundleMemberName { get; init; }
    public int? AssetClassId { get; init; }
    public long? PathId { get; init; }
    public string? ScriptClassName { get; init; }
    public required string FieldOrInnerKey { get; init; }
    public string? CodeTypeFullName { get; init; }
    public string? CodeMethodName { get; init; }
    public int? CodeInstructionIndex { get; init; }
    public int? MetadataLiteralIndex { get; init; }
}

public sealed record TextEntry
{
    public required string Id { get; init; }
    public required string Key { get; init; }
    public required string Context { get; init; }
    public required string OriginalText { get; init; }
    public string TranslatedText { get; init; } = string.Empty;
    public int RelevanceScore { get; init; }
    public bool IsSystemNoise { get; init; }
    public DetectedLanguage Language { get; init; } = DetectedLanguage.Unknown;
    public required EntryLocation Location { get; init; }
}

public sealed class ScannedTarget
{
    public required string TargetId { get; init; }
    public required string DisplayName { get; init; }
    public required TextSourceKind SourceKind { get; init; }
    public required string ContainerRelativePath { get; init; }
    public string? BundleMemberName { get; init; }
    public long? PrimaryPathId { get; init; }
    public string? ScriptClassName { get; init; }
    public long EstimatedSizeBytes { get; init; }
    public int ObjectCount { get; init; } = 1;
    public int RelevanceScore { get; set; }
    public bool IsLikelyNoise { get; set; }
    public bool SupportsRawExport { get; init; }
    public bool IsIndexed { get; set; }
    public int ValidEntryCount { get; set; }
    public int TotalEntryCount { get; set; }
    public InnerTextFormat DetectedFormat { get; set; } = InnerTextFormat.PlainText;
    public IReadOnlyList<long>? GroupedPathIds { get; init; }
    public Dictionary<DetectedLanguage, int> LanguageEntryCounts { get; } = [];

    public int GetValidCountForFilter(SourceLanguageFilter filter)
    {
        if (filter == SourceLanguageFilter.All || !IsIndexed)
        {
            return ValidEntryCount;
        }

        DetectedLanguage targetLang = filter switch
        {
            SourceLanguageFilter.English => DetectedLanguage.English,
            SourceLanguageFilter.Chinese => DetectedLanguage.Chinese,
            SourceLanguageFilter.Russian => DetectedLanguage.Russian,
            SourceLanguageFilter.Japanese => DetectedLanguage.Japanese,
            _ => DetectedLanguage.Unknown
        };

        return LanguageEntryCounts.TryGetValue(targetLang, out int count) ? count : 0;
    }
}

public sealed record ExtractedTargetContent(
    ScannedTarget Target,
    string? RawText,
    byte[]? RawBytes,
    string? RawExtension,
    string ContainerSha256,
    InnerTextFormat InnerFormat,
    IReadOnlyList<TextEntry> Entries);

public sealed class TranslationPackage
{
    public string SchemaVersion { get; init; } = "1.0";
    public required string Engine { get; init; }
    public required string EngineVersion { get; init; }
    public required string ScriptingBackend { get; init; }
    public required string TargetId { get; init; }
    public required string DisplayName { get; init; }
    public required TextSourceKind SourceKind { get; init; }
    public required string ContainerRelativePath { get; init; }
    public string? BundleMemberName { get; init; }
    public required string OriginalContainerSha256 { get; init; }
    public required InnerTextFormat InnerFormat { get; init; }
    public required List<TextEntry> Entries { get; init; }
}

public sealed class ProjectExtractionManifest
{
    public string SchemaVersion { get; init; } = "1.0";
    public DateTimeOffset ExtractedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string GameName { get; init; }
    public required string GameRootPath { get; init; }
    public required string Engine { get; init; }
    public required string EngineVersion { get; init; }
    public required string ScriptingBackend { get; init; }
    public required List<ExportedFileRecord> Files { get; init; }
}

public sealed record ExportedFileRecord(
    string TargetId,
    string DisplayName,
    TextSourceKind SourceKind,
    string ContainerRelativePath,
    string? StructuredJsonRelativePath,
    string? CsvRelativePath,
    string? RawRelativePath,
    int EntryCount);

public sealed record ScanPhase1Result(
    GameProfile Profile,
    IReadOnlyList<ScannedTarget> Targets,
    IReadOnlyList<EncryptedFileWarning> EncryptedWarnings);

public sealed record ScanProgress(
    string Phase,
    int CompletedCount,
    int TotalCount,
    string CurrentItem);

public sealed record ExportOptions
{
    public required string OutputDirectory { get; init; }
    public bool ExportTranslatorJson { get; init; } = true;
    public bool ExportCsv { get; init; } = true;
    public bool IncludeRawFile { get; init; } = false;
    public bool IncludeSystemNoiseEntries { get; init; } = false;
    public SourceLanguageFilter LanguageFilter { get; init; } = SourceLanguageFilter.All;
}

public sealed record ExportSummary(
    int ExportedTargetCount,
    int ExportedEntryCount,
    int RawFilesWritten,
    string OutputDirectory,
    IReadOnlyList<string> GeneratedFiles,
    IReadOnlyList<string> Errors);
