using System.Buffers.Binary;
using System.Text;
using Translator.Core.Filters;

namespace Translator.Engines.Unity.TypeTrees;

public sealed record BinaryScannedString(
    int RelativeByteOffset,
    int ByteLength,
    string Value);

public static class AlignedBinaryStringScanner
{
    public static (string ObjectName, IReadOnlyList<BinaryScannedString> Strings) ScanMonoBehaviourPayload(
        ReadOnlySpan<byte> rawObjectBytes,
        bool isBigEndian = false,
        bool useLongPathId = true)
    {
        int ptrSize = useLongPathId ? 12 : 8;
        // m_GameObject (ptrSize) + m_Enabled aligned (4) + m_Script (ptrSize)
        int headerPos = ptrSize + 4 + ptrSize;
        string objectName = string.Empty;

        if (rawObjectBytes.Length < headerPos + 4)
        {
            return (objectName, []);
        }

        int startScanOffset = headerPos;
        int nameLen = ReadInt32(rawObjectBytes.Slice(headerPos, 4), isBigEndian);
        if (nameLen >= 0 && nameLen <= 512 && headerPos + 4 + nameLen <= rawObjectBytes.Length)
        {
            int alignedAfterName = Align4(headerPos + 4 + nameLen);
            if (alignedAfterName <= rawObjectBytes.Length &&
                HasZeroPadding(rawObjectBytes, headerPos + 4 + nameLen, alignedAfterName))
            {
                if (nameLen > 0 &&
                    TextHeuristicScorer.TryDecodeReadableText(rawObjectBytes.Slice(headerPos + 4, nameLen), out string parsedName))
                {
                    objectName = parsedName;
                }

                startScanOffset = alignedAfterName;
            }
        }

        IReadOnlyList<BinaryScannedString> strings = ScanAlignedRegion(
            rawObjectBytes,
            startScanOffset,
            isBigEndian);

        return (objectName, strings);
    }

    public static IReadOnlyList<BinaryScannedString> ScanAlignedRegion(
        ReadOnlySpan<byte> rawBytes,
        int startAlignedOffset = 0,
        bool isBigEndian = false)
    {
        var results = new List<BinaryScannedString>();
        int offset = Align4(Math.Max(0, startAlignedOffset));

        while (offset + 4 <= rawBytes.Length)
        {
            int candidateLen = ReadInt32(rawBytes.Slice(offset, 4), isBigEndian);
            int stringStart = offset + 4;
            int stringEnd = stringStart + candidateLen;

            if (candidateLen >= 2 &&
                candidateLen <= 65536 &&
                stringEnd <= rawBytes.Length)
            {
                int alignedEnd = Align4(stringEnd);
                if (alignedEnd <= rawBytes.Length &&
                    HasZeroPadding(rawBytes, stringEnd, alignedEnd))
                {
                    ReadOnlySpan<byte> strBytes = rawBytes.Slice(stringStart, candidateLen);
                    if (TextHeuristicScorer.TryDecodeReadableText(strBytes, out string decoded))
                    {
                        results.Add(new BinaryScannedString(offset, candidateLen, decoded));
                        offset = alignedEnd;
                        continue;
                    }
                }
            }

            offset += 4;
        }

        return results;
    }

    public static bool TryReadBinaryTextAsset(
        ReadOnlySpan<byte> rawBytes,
        bool isBigEndian,
        out string assetName,
        out byte[] scriptBytes)
    {
        assetName = string.Empty;
        scriptBytes = [];

        if (rawBytes.Length < 8)
        {
            return false;
        }

        int nameLen = ReadInt32(rawBytes[..4], isBigEndian);
        if (nameLen < 0 || nameLen > 2048 || 4 + nameLen > rawBytes.Length)
        {
            return false;
        }

        if (nameLen > 0)
        {
            assetName = Encoding.UTF8.GetString(rawBytes.Slice(4, nameLen));
        }

        int scriptLenOffset = Align4(4 + nameLen);
        if (scriptLenOffset + 4 > rawBytes.Length)
        {
            return false;
        }

        int scriptLen = ReadInt32(rawBytes.Slice(scriptLenOffset, 4), isBigEndian);
        int scriptDataStart = scriptLenOffset + 4;
        if (scriptLen < 0 || scriptDataStart + scriptLen > rawBytes.Length)
        {
            return false;
        }

        scriptBytes = rawBytes.Slice(scriptDataStart, scriptLen).ToArray();
        return true;
    }

    public static byte[] BuildBinaryTextAsset(
        string assetName,
        ReadOnlySpan<byte> scriptBytes,
        bool isBigEndian = false,
        ReadOnlySpan<byte> trailingBytes = default)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(assetName ?? string.Empty);
        int alignedNamePart = Align4(4 + nameBytes.Length);
        int alignedScriptPart = Align4(4 + scriptBytes.Length);

        byte[] output = new byte[alignedNamePart + alignedScriptPart + trailingBytes.Length];
        WriteInt32(output.AsSpan(0, 4), nameBytes.Length, isBigEndian);
        nameBytes.CopyTo(output.AsSpan(4));

        WriteInt32(output.AsSpan(alignedNamePart, 4), scriptBytes.Length, isBigEndian);
        scriptBytes.CopyTo(output.AsSpan(alignedNamePart + 4));

        if (!trailingBytes.IsEmpty)
        {
            trailingBytes.CopyTo(output.AsSpan(alignedNamePart + alignedScriptPart));
        }

        return output;
    }

    public static byte[]? PatchBinaryMonoBehaviourPayload(
        ReadOnlySpan<byte> originalBytes,
        long pathId,
        IReadOnlyDictionary<string, string> translations,
        bool isBigEndian,
        out int patchedCount)
    {
        patchedCount = 0;
        if (originalBytes.Length < 28 || translations.Count == 0)
        {
            return null;
        }

        var edits = new List<(int Offset, int OldLen, string NewValue)>();
        string prefixWithPathId = $"PathId={pathId}::bin:";

        foreach ((string key, string val) in translations)
        {
            if (string.IsNullOrEmpty(val))
            {
                continue;
            }

            ReadOnlySpan<char> span = key.AsSpan();
            if (key.StartsWith(prefixWithPathId, StringComparison.OrdinalIgnoreCase))
            {
                span = span[prefixWithPathId.Length..];
            }
            else if (key.StartsWith("bin:", StringComparison.OrdinalIgnoreCase))
            {
                span = span[4..];
            }
            else
            {
                continue;
            }

            if (TryParseBinaryOffsetAndLength(span, out int offset, out int oldLen))
            {
                edits.Add((offset, oldLen, val));
            }
        }

        if (edits.Count == 0)
        {
            return null;
        }

        edits.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        using var ms = new MemoryStream(originalBytes.Length + 256);
        Span<byte> lenBuf = stackalloc byte[4];
        int cursor = 0;

        foreach ((int offset, int oldLen, string newValue) in edits)
        {
            if (offset < cursor || offset + 4 > originalBytes.Length)
            {
                continue;
            }

            int actualLen = ReadInt32(originalBytes.Slice(offset, 4), isBigEndian);
            int oldAlignedEnd = Align4(offset + 4 + actualLen);
            if (actualLen != oldLen || oldAlignedEnd > originalBytes.Length)
            {
                continue;
            }

            if (offset > cursor)
            {
                ms.Write(originalBytes.Slice(cursor, offset - cursor));
            }

            byte[] newUtf8 = Encoding.UTF8.GetBytes(newValue);
            WriteInt32(lenBuf, newUtf8.Length, isBigEndian);
            ms.Write(lenBuf);
            ms.Write(newUtf8);

            int pad = Align4(newUtf8.Length) - newUtf8.Length;
            for (int p = 0; p < pad; p++)
            {
                ms.WriteByte(0);
            }

            cursor = oldAlignedEnd;
            patchedCount++;
        }

        if (patchedCount == 0)
        {
            return null;
        }

        if (cursor < originalBytes.Length)
        {
            ms.Write(originalBytes[cursor..]);
        }

        return ms.ToArray();
    }

    private static bool TryParseBinaryOffsetAndLength(ReadOnlySpan<char> span, out int offset, out int length)
    {
        offset = -1;
        length = -1;

        int offIdx = span.IndexOf("offset=", StringComparison.OrdinalIgnoreCase);
        int lenIdx = span.IndexOf("len=", StringComparison.OrdinalIgnoreCase);
        if (offIdx < 0 || lenIdx < 0)
        {
            return false;
        }

        ReadOnlySpan<char> offSlice = span[(offIdx + 7)..];
        int semi1 = offSlice.IndexOf(';');
        if (semi1 >= 0)
        {
            offSlice = offSlice[..semi1];
        }

        ReadOnlySpan<char> lenSlice = span[(lenIdx + 4)..];
        int semi2 = lenSlice.IndexOf(';');
        if (semi2 >= 0)
        {
            lenSlice = lenSlice[..semi2];
        }

        return int.TryParse(offSlice, out offset) && int.TryParse(lenSlice, out length);
    }

    private static bool HasZeroPadding(ReadOnlySpan<byte> buffer, int unalignedEnd, int alignedEnd)
    {
        for (int i = unalignedEnd; i < alignedEnd; i++)
        {
            if (buffer[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int Align4(int value) => (value + 3) & ~3;

    private static int ReadInt32(ReadOnlySpan<byte> span, bool isBigEndian) =>
        isBigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(span)
            : BinaryPrimitives.ReadInt32LittleEndian(span);

    private static void WriteInt32(Span<byte> span, int value, bool isBigEndian)
    {
        if (isBigEndian)
        {
            BinaryPrimitives.WriteInt32BigEndian(span, value);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
        }
    }
}
