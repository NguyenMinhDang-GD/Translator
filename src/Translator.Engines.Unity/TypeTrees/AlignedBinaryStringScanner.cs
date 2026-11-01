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
}
