using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Cpp2IL;
using AssetsTools.NET.Extra;
using Translator.Core.Models;

namespace Translator.Engines.Unity.TypeTrees;

public sealed record ScriptIdentity(
    string ClassName,
    string Namespace,
    string AssemblyName);

public sealed class CachedTemplateFieldDto
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public AssetValueType ValueType { get; set; }
    public bool IsArray { get; set; }
    public bool IsAligned { get; set; }
    public bool HasValue { get; set; }
    public List<CachedTemplateFieldDto> Children { get; set; } = [];

    public static CachedTemplateFieldDto FromField(AssetTypeTemplateField field) => new()
    {
        Name = field.Name ?? string.Empty,
        Type = field.Type ?? string.Empty,
        ValueType = field.ValueType,
        IsArray = field.IsArray,
        IsAligned = field.IsAligned,
        HasValue = field.HasValue,
        Children = field.Children?.Select(FromField).ToList() ?? []
    };

    public AssetTypeTemplateField ToField() => new()
    {
        Name = Name,
        Type = Type,
        ValueType = ValueType,
        IsArray = IsArray,
        IsAligned = IsAligned,
        HasValue = HasValue,
        Children = Children.Select(c => c.ToField()).ToList()
    };
}

public sealed class TypeTreeResolver : IDisposable
{
    private readonly GameProfile _profile;
    private readonly string _diskCacheFolder;
    private readonly object _generatorLock = new();
    private readonly ConcurrentDictionary<string, List<AssetTypeTemplateField>?> _memoryTemplateCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ScriptIdentity?> _scriptIdentityCache = new(StringComparer.Ordinal);

    private IMonoBehaviourTemplateGenerator? _innerGenerator;
    private bool _generatorInitialized;
    private bool _generatorUnavailable;

    public TypeTreeResolver(GameProfile profile)
    {
        _profile = profile;
        string gameKey = ComputeShortHash($"{profile.RootPath}|{profile.EngineVersion}|{profile.ScriptingBackend}");
        _diskCacheFolder = Path.Combine(profile.RootPath, ".translator_cache", $"typetrees_{gameKey}");
    }

    public bool UsedFallbackBinaryScanner { get; private set; }

    public AssetsManager CreateAssetsManager()
    {
        var am = new AssetsManager
        {
            UseTemplateFieldCache = true,
            UseMonoTemplateFieldCache = true,
            UseRefTypeManagerCache = true,
            UseQuickLookup = true
        };

        string tpkPath = Path.Combine(AppContext.BaseDirectory, "classdata.tpk");
        if (File.Exists(tpkPath))
        {
            try
            {
                am.LoadClassPackage(tpkPath);
            }
            catch
            {
                // Built-in standalone templates will be used safely
            }
        }

        return am;
    }

    public ScriptIdentity ResolveMonoBehaviourScript(
        AssetsManager am,
        AssetsFileInstance fileInst,
        AssetFileInfo mbInfo)
    {
        string cacheKey = $"{fileInst.path}::{fileInst.name}::{mbInfo.PathId}";
        if (_scriptIdentityCache.TryGetValue(cacheKey, out ScriptIdentity? cached) && cached is not null)
        {
            return cached;
        }

        // Option A: TypeTree has script type info or embedded MonoScript reference
        AssetsFile file = fileInst.file;
        bool useLongPathId = file.Header.Version >= 14;
        bool isBigEndian = file.Header.Endianness;

        // Read m_Script PPtr directly from MonoBehaviour binary header:
        // m_GameObject (FileID 4 + PathID 8/4) + m_Enabled aligned (4) => m_Script starts at byte 16 (or 12)
        int scriptPtrOffset = (useLongPathId ? 12 : 8) + 4;
        int scriptPtrSize = useLongPathId ? 12 : 8;

        int scriptFileId = 0;
        long scriptPathId = 0;

        if (mbInfo.ByteSize >= scriptPtrOffset + scriptPtrSize)
        {
            byte[] headerBuf = ReadRawSlice(fileInst, mbInfo.GetAbsoluteByteOffset(file), scriptPtrOffset + scriptPtrSize);
            if (headerBuf.Length == scriptPtrOffset + scriptPtrSize)
            {
                ReadOnlySpan<byte> ptrSpan = headerBuf.AsSpan(scriptPtrOffset, scriptPtrSize);
                scriptFileId = isBigEndian
                    ? BinaryPrimitives.ReadInt32BigEndian(ptrSpan[..4])
                    : BinaryPrimitives.ReadInt32LittleEndian(ptrSpan[..4]);

                scriptPathId = useLongPathId
                    ? (isBigEndian
                        ? BinaryPrimitives.ReadInt64BigEndian(ptrSpan.Slice(4, 8))
                        : BinaryPrimitives.ReadInt64LittleEndian(ptrSpan.Slice(4, 8)))
                    : (isBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(ptrSpan.Slice(4, 4))
                        : BinaryPrimitives.ReadInt32LittleEndian(ptrSpan.Slice(4, 4)));
            }
        }

        if (scriptPathId != 0)
        {
            ScriptIdentity? resolved = TryReadMonoScriptTarget(am, fileInst, scriptFileId, scriptPathId);
            if (resolved is not null && !string.IsNullOrWhiteSpace(resolved.ClassName))
            {
                _scriptIdentityCache[cacheKey] = resolved;
                return resolved;
            }
        }

        // Option B: Check embedded TypeTreeType name if present
        if (file.Metadata.TypeTreeEnabled)
        {
            ushort scriptIdx = mbInfo.GetScriptIndex(file);
            TypeTreeType? tt = file.Metadata.FindTypeTreeTypeByID(114, scriptIdx)
                            ?? file.Metadata.FindTypeTreeTypeByScriptIndex(scriptIdx);
            if (tt?.Nodes is { Count: > 0 })
            {
                var fallbackId = new ScriptIdentity($"MonoBehaviour_Script{scriptIdx}", string.Empty, "Assembly-CSharp.dll");
                _scriptIdentityCache[cacheKey] = fallbackId;
                return fallbackId;
            }
        }

        ushort fallbackScriptIdx = mbInfo.GetScriptIndex(file);
        string fallbackName = fallbackScriptIdx != ushort.MaxValue
            ? $"Script_{fallbackScriptIdx}"
            : $"MonoBehaviour_{mbInfo.PathId}";

        var unknown = new ScriptIdentity(fallbackName, string.Empty, "Assembly-CSharp.dll");
        _scriptIdentityCache[cacheKey] = unknown;
        return unknown;
    }

    public AssetTypeValueField? TryDeserializeMonoBehaviour(
        AssetsManager am,
        AssetsFileInstance fileInst,
        AssetFileInfo mbInfo,
        ScriptIdentity scriptId)
    {
        AssetsFile file = fileInst.file;
        long absOffset = mbInfo.GetAbsoluteByteOffset(file);

        // 1. Embedded TypeTree directly inside the .assets / .bundle file
        if (file.Metadata.TypeTreeEnabled)
        {
            ushort scriptIndex = mbInfo.GetScriptIndex(file);
            TypeTreeType? ttType = file.Metadata.FindTypeTreeTypeByID(114, scriptIndex)
                                ?? file.Metadata.FindTypeTreeTypeByScriptIndex(scriptIndex);
            if (ttType is not null && ttType.Nodes is { Count: > 1 })
            {
                try
                {
                    var template = new AssetTypeTemplateField();
                    template.FromTypeTree(ttType);
                    RefTypeManager? refMan = TryGetRefTypeManager(am, fileInst);
                    lock (fileInst.LockReader)
                    {
                        return template.MakeValue(fileInst.file.Reader, absOffset, refMan);
                    }
                }
                catch
                {
                    // Fall through to generator
                }
            }
        }

        // 2. Reconstruct via MonoCecil (Mono) or Cpp2IL (IL2CPP) + Disk Cache
        if (!string.IsNullOrWhiteSpace(scriptId.ClassName) &&
            !scriptId.ClassName.StartsWith("Script_", StringComparison.Ordinal) &&
            !scriptId.ClassName.StartsWith("MonoBehaviour_", StringComparison.Ordinal))
        {
            bool useLongPathId = file.Header.Version >= 14;
            AssetTypeTemplateField baseTemplate = BuildMonoBehaviourBaseTemplate(useLongPathId);
            List<AssetTypeTemplateField>? customChildren = GetOrGenerateCustomFields(
                baseTemplate,
                scriptId,
                file.Metadata.UnityVersion);

            if (customChildren is not null)
            {
                baseTemplate.Children.AddRange(customChildren.Select(c => c.Clone()));
                try
                {
                    lock (fileInst.LockReader)
                    {
                        return baseTemplate.MakeValue(fileInst.file.Reader, absOffset);
                    }
                }
                catch
                {
                    // Fall through to binary fallback
                }
            }
        }

        UsedFallbackBinaryScanner = true;
        return null;
    }

    public static byte[] ReadRawAssetBytes(AssetsFileInstance fileInst, AssetFileInfo info)
    {
        long absOffset = info.GetAbsoluteByteOffset(fileInst.file);
        return ReadRawSlice(fileInst, absOffset, (int)info.ByteSize);
    }

    private static byte[] ReadRawSlice(AssetsFileInstance fileInst, long absOffset, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        lock (fileInst.LockReader)
        {
            AssetsFileReader reader = fileInst.file.Reader;
            reader.Position = absOffset;
            return reader.ReadBytes(count);
        }
    }

    private ScriptIdentity? TryReadMonoScriptTarget(
        AssetsManager am,
        AssetsFileInstance sourceFile,
        int fileId,
        long pathId)
    {
        string ptrKey = $"{sourceFile.path}::{sourceFile.name}::dep{fileId}::{pathId}";
        if (_scriptIdentityCache.TryGetValue(ptrKey, out ScriptIdentity? cached))
        {
            return cached;
        }

        AssetsFileInstance? targetFile = fileId == 0 ? sourceFile : null;
        if (fileId > 0)
        {
            try
            {
                targetFile = sourceFile.GetDependency(am, fileId - 1);
            }
            catch
            {
                targetFile = null;
            }
        }

        if (targetFile?.file is null)
        {
            return null;
        }

        AssetFileInfo? scriptInfo = targetFile.file.GetAssetInfo(pathId);
        if (scriptInfo is null)
        {
            return null;
        }

        // First try embedded TypeTree if enabled
        if (targetFile.file.Metadata.TypeTreeEnabled)
        {
            TypeTreeType? tt = targetFile.file.Metadata.FindTypeTreeTypeByID(115);
            if (tt is not null && tt.Nodes is { Count: > 0 })
            {
                try
                {
                    var tmpl = new AssetTypeTemplateField();
                    tmpl.FromTypeTree(tt);
                    AssetTypeValueField vf;
                    lock (targetFile.LockReader)
                    {
                        vf = tmpl.MakeValue(targetFile.file.Reader, scriptInfo.GetAbsoluteByteOffset(targetFile.file));
                    }

                    string cls = vf["m_ClassName"].IsDummy ? string.Empty : vf["m_ClassName"].AsString;
                    string ns = vf["m_Namespace"].IsDummy ? string.Empty : vf["m_Namespace"].AsString;
                    string asm = vf["m_AssemblyName"].IsDummy ? "Assembly-CSharp.dll" : vf["m_AssemblyName"].AsString;
                    if (!string.IsNullOrWhiteSpace(cls))
                    {
                        var id = new ScriptIdentity(cls, ns, asm);
                        _scriptIdentityCache[ptrKey] = id;
                        return id;
                    }
                }
                catch
                {
                    // Fall through to binary MonoScript parser
                }
            }
        }

        // Standalone Binary MonoScript Parser (works even when TypeTree is stripped and no classdata.tpk!)
        byte[] raw = ReadRawAssetBytes(targetFile, scriptInfo);
        ScriptIdentity? binaryParsed = TryParseBinaryMonoScript(
            raw,
            targetFile.file.Header.Endianness,
            targetFile.file.Header.Version);

        _scriptIdentityCache[ptrKey] = binaryParsed;
        return binaryParsed;
    }

    public static ScriptIdentity? TryParseBinaryMonoScript(
        ReadOnlySpan<byte> raw,
        bool isBigEndian,
        uint formatVersion)
    {
        // Unity 5+ (formatVersion >= 14):
        // string m_Name (aligned 4)
        // int32 m_ExecutionOrder (4)
        // Hash128 m_PropertiesHash (16 bytes) [or uint32 (4 bytes) on formatVersion < 14]
        // string m_ClassName (aligned 4)
        // string m_Namespace (aligned 4)
        // string m_AssemblyName (aligned 4)
        if (!TryReadAlignedString(raw, 0, isBigEndian, out _, out int afterName))
        {
            return null;
        }

        int hashSize = formatVersion >= 14 ? 16 : 4;
        int pos = afterName + 4 + hashSize;

        if (!TryReadAlignedString(raw, pos, isBigEndian, out string className, out pos))
        {
            return null;
        }

        if (!TryReadAlignedString(raw, pos, isBigEndian, out string nameSpace, out pos))
        {
            return null;
        }

        if (!TryReadAlignedString(raw, pos, isBigEndian, out string assemblyName, out _))
        {
            assemblyName = "Assembly-CSharp.dll";
        }

        if (string.IsNullOrWhiteSpace(className))
        {
            return null;
        }

        return new ScriptIdentity(
            className,
            nameSpace,
            string.IsNullOrWhiteSpace(assemblyName) ? "Assembly-CSharp.dll" : assemblyName);
    }

    private static bool TryReadAlignedString(
        ReadOnlySpan<byte> buffer,
        int offset,
        bool isBigEndian,
        out string value,
        out int nextAlignedOffset)
    {
        value = string.Empty;
        nextAlignedOffset = offset;

        if (offset < 0 || offset + 4 > buffer.Length)
        {
            return false;
        }

        int len = isBigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(offset, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4));

        if (len < 0 || len > 1024 || offset + 4 + len > buffer.Length)
        {
            return false;
        }

        if (len > 0)
        {
            value = Encoding.UTF8.GetString(buffer.Slice(offset + 4, len));
        }

        nextAlignedOffset = (offset + 4 + len + 3) & ~3;
        return nextAlignedOffset <= buffer.Length;
    }

    private List<AssetTypeTemplateField>? GetOrGenerateCustomFields(
        AssetTypeTemplateField baseTemplate,
        ScriptIdentity scriptId,
        string? fileUnityVersion)
    {
        string assemblyName = scriptId.AssemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? scriptId.AssemblyName
            : $"{scriptId.AssemblyName}.dll";

        string cacheKey = $"{assemblyName}::{scriptId.Namespace}::{scriptId.ClassName}";
        if (_memoryTemplateCache.TryGetValue(cacheKey, out List<AssetTypeTemplateField>? memCached))
        {
            return memCached;
        }

        // Check Disk Cache first (huge speedup for IL2CPP games on subsequent launches!)
        string diskCacheFile = Path.Combine(_diskCacheFolder, $"{ComputeShortHash(cacheKey)}.json");
        if (File.Exists(diskCacheFile))
        {
            try
            {
                string json = File.ReadAllText(diskCacheFile, Encoding.UTF8);
                List<CachedTemplateFieldDto>? dtos = JsonSerializer.Deserialize<List<CachedTemplateFieldDto>>(json);
                if (dtos is not null)
                {
                    List<AssetTypeTemplateField> loaded = dtos.Select(d => d.ToField()).ToList();
                    _memoryTemplateCache[cacheKey] = loaded;
                    return loaded;
                }
            }
            catch
            {
                // Ignore corrupted cache file and regenerate
            }
        }

        IMonoBehaviourTemplateGenerator? generator = EnsureGeneratorInitialized();
        if (generator is null)
        {
            _memoryTemplateCache[cacheKey] = null;
            return null;
        }

        string verStr = !string.IsNullOrWhiteSpace(fileUnityVersion) && fileUnityVersion != "0.0.0"
            ? fileUnityVersion
            : (_profile.EngineVersion != "Unknown" ? _profile.EngineVersion : "2021.3.0f1");

        UnityVersion unityVer;
        try
        {
            unityVer = new UnityVersion(verStr);
        }
        catch
        {
            unityVer = new UnityVersion("2021.3.0f1");
        }

        try
        {
            AssetTypeTemplateField clonedBase = baseTemplate.Clone();
            int baseChildCount = clonedBase.Children.Count;
            AssetTypeTemplateField? generated;

            lock (_generatorLock)
            {
                generated = generator.GetTemplateField(
                    clonedBase,
                    assemblyName,
                    scriptId.Namespace ?? string.Empty,
                    scriptId.ClassName,
                    unityVer);
            }

            if (generated is null || generated.Children.Count < baseChildCount)
            {
                _memoryTemplateCache[cacheKey] = null;
                return null;
            }

            List<AssetTypeTemplateField> customFields = generated.Children
                .Skip(baseChildCount)
                .Select(f => f.Clone())
                .ToList();

            _memoryTemplateCache[cacheKey] = customFields;

            // Persist to Disk Cache
            try
            {
                Directory.CreateDirectory(_diskCacheFolder);
                List<CachedTemplateFieldDto> dtos = customFields.Select(CachedTemplateFieldDto.FromField).ToList();
                string json = JsonSerializer.Serialize(dtos);
                File.WriteAllText(diskCacheFile, json, Encoding.UTF8);
            }
            catch
            {
                // Disk cache failure is non-fatal
            }

            return customFields;
        }
        catch
        {
            _memoryTemplateCache[cacheKey] = null;
            return null;
        }
    }

    private IMonoBehaviourTemplateGenerator? EnsureGeneratorInitialized()
    {
        if (_generatorUnavailable)
        {
            return null;
        }

        if (_generatorInitialized)
        {
            return _innerGenerator;
        }

        lock (_generatorLock)
        {
            if (_generatorUnavailable)
            {
                return null;
            }

            if (_generatorInitialized)
            {
                return _innerGenerator;
            }

            try
            {
                if (_profile.ManagedFolderPath is not null && Directory.Exists(_profile.ManagedFolderPath))
                {
                    _innerGenerator = new MonoCecilTempGenerator(_profile.ManagedFolderPath);
                    _generatorInitialized = true;
                    return _innerGenerator;
                }

                if (_profile.GlobalMetadataPath is not null &&
                    _profile.GameAssemblyPath is not null &&
                    File.Exists(_profile.GlobalMetadataPath) &&
                    File.Exists(_profile.GameAssemblyPath))
                {
                    _innerGenerator = new Cpp2IlTempGenerator(_profile.GlobalMetadataPath, _profile.GameAssemblyPath);
                    _generatorInitialized = true;
                    return _innerGenerator;
                }
            }
            catch
            {
                // Encrypted IL2CPP or missing dependencies -> safely fall back to AlignedBinaryStringScanner
            }

            _generatorUnavailable = true;
            _generatorInitialized = true;
            return null;
        }
    }

    public static AssetTypeTemplateField BuildMonoBehaviourBaseTemplate(bool useLongPathId = true)
    {
        return new AssetTypeTemplateField
        {
            Name = "Base",
            Type = "MonoBehaviour",
            ValueType = AssetValueType.None,
            Children =
            [
                BuildPPtrTemplate("m_GameObject", "GameObject", useLongPathId),
                new AssetTypeTemplateField
                {
                    Name = "m_Enabled",
                    Type = "UInt8",
                    ValueType = AssetValueType.UInt8,
                    HasValue = true,
                    IsAligned = true,
                    Children = []
                },
                BuildPPtrTemplate("m_Script", "MonoScript", useLongPathId),
                BuildStringTemplate("m_Name")
            ]
        };
    }

    private static AssetTypeTemplateField BuildPPtrTemplate(string fieldName, string targetType, bool useLongPathId) =>
        new()
        {
            Name = fieldName,
            Type = $"PPtr<{targetType}>",
            ValueType = AssetValueType.None,
            Children =
            [
                new AssetTypeTemplateField
                {
                    Name = "m_FileID",
                    Type = "int",
                    ValueType = AssetValueType.Int32,
                    HasValue = true,
                    Children = []
                },
                new AssetTypeTemplateField
                {
                    Name = "m_PathID",
                    Type = useLongPathId ? "SInt64" : "int",
                    ValueType = useLongPathId ? AssetValueType.Int64 : AssetValueType.Int32,
                    HasValue = true,
                    Children = []
                }
            ]
        };

    public static AssetTypeTemplateField BuildStringTemplate(string fieldName) =>
        new()
        {
            Name = fieldName,
            Type = "string",
            ValueType = AssetValueType.String,
            HasValue = true,
            IsAligned = true,
            Children =
            [
                new AssetTypeTemplateField
                {
                    Name = "Array",
                    Type = "Array",
                    ValueType = AssetValueType.ByteArray,
                    IsArray = true,
                    IsAligned = true,
                    HasValue = true,
                    Children =
                    [
                        new AssetTypeTemplateField
                        {
                            Name = "size",
                            Type = "int",
                            ValueType = AssetValueType.Int32,
                            HasValue = true,
                            Children = []
                        },
                        new AssetTypeTemplateField
                        {
                            Name = "data",
                            Type = "char",
                            ValueType = AssetValueType.UInt8,
                            HasValue = true,
                            Children = []
                        }
                    ]
                }
            ]
        };

    private static RefTypeManager? TryGetRefTypeManager(AssetsManager am, AssetsFileInstance fileInst)
    {
        try
        {
            return am.GetRefTypeManager(fileInst);
        }
        catch
        {
            return null;
        }
    }

    private static string ComputeShortHash(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16];
    }

    public void Dispose()
    {
        lock (_generatorLock)
        {
            _innerGenerator?.Dispose();
            _innerGenerator = null;
        }
    }
}
