using System.Buffers.Binary;
using System.Text;
using Translator.Core.Models;
using Translator.Engines.Unity.Handlers;

namespace Translator.Engines.Unity.Patching;

public static class Il2CppMetadataPatcher
{
    public static async Task<int> PatchFileAtomicAsync(
        string backupMetadataPath,
        string targetGameMetadataPath,
        IReadOnlyList<PatchPayload> payloads,
        CancellationToken cancellationToken = default)
    {
        var replacements = new Dictionary<int, string>();
        foreach (PatchPayload payload in payloads)
        {
            foreach (TextEntry entry in payload.Package.Entries)
            {
                if (string.IsNullOrEmpty(entry.TranslatedText))
                {
                    continue;
                }

                int? literalIdx = entry.Location.MetadataLiteralIndex;
                if (!literalIdx.HasValue &&
                    entry.Key.StartsWith("il2cpp:literal[", StringComparison.OrdinalIgnoreCase) &&
                    entry.Key.EndsWith(']'))
                {
                    if (int.TryParse(entry.Key.AsSpan(15, entry.Key.Length - 16), out int parsedIdx))
                    {
                        literalIdx = parsedIdx;
                    }
                }

                if (literalIdx is >= 0)
                {
                    replacements[literalIdx.Value] = entry.TranslatedText;
                }
            }
        }

        if (replacements.Count == 0)
        {
            return 0;
        }

        byte[] originalBytes = await File.ReadAllBytesAsync(backupMetadataPath, cancellationToken).ConfigureAwait(false);
        byte[] patchedBytes = PatchMetadataBytes(originalBytes, replacements, out int patchedCount);

        if (patchedCount == 0)
        {
            return 0;
        }

        string tempPath = $"{targetGameMetadataPath}.translator.tmp";
        await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
        {
            await fs.WriteAsync(patchedBytes, cancellationToken).ConfigureAwait(false);
            await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, targetGameMetadataPath, overwrite: true);
        return patchedCount;
    }

    public static byte[] PatchMetadataBytes(
        ReadOnlySpan<byte> originalData,
        IReadOnlyDictionary<int, string> literalReplacements,
        out int patchedCount)
    {
        patchedCount = 0;
        if (originalData.Length < 24 || literalReplacements.Count == 0)
        {
            return originalData.ToArray();
        }

        uint sanity = BinaryPrimitives.ReadUInt32LittleEndian(originalData[..4]);
        if (sanity != CodeStringHandler.Il2CppMagic)
        {
            throw new InvalidDataException($"global-metadata.dat có magic header 0x{sanity:X8} không hợp lệ.");
        }

        int stringLiteralOffset = BinaryPrimitives.ReadInt32LittleEndian(originalData.Slice(8, 4));
        int stringLiteralByteSize = BinaryPrimitives.ReadInt32LittleEndian(originalData.Slice(12, 4));
        int stringLiteralDataOffset = BinaryPrimitives.ReadInt32LittleEndian(originalData.Slice(16, 4));
        int stringLiteralDataByteSize = BinaryPrimitives.ReadInt32LittleEndian(originalData.Slice(20, 4));

        if (stringLiteralOffset < 24 ||
            stringLiteralByteSize <= 0 ||
            stringLiteralDataOffset < 24 ||
            stringLiteralDataByteSize < 0 ||
            (long)stringLiteralOffset + stringLiteralByteSize > originalData.Length ||
            (long)stringLiteralDataOffset + stringLiteralDataByteSize > originalData.Length)
        {
            throw new InvalidDataException("Bảng StringLiteral trong global-metadata.dat có offset nằm ngoài phạm vi file.");
        }

        int literalCount = stringLiteralByteSize / 8;
        ReadOnlySpan<byte> oldLiteralTable = originalData.Slice(stringLiteralOffset, literalCount * 8);
        ReadOnlySpan<byte> oldDataPool = originalData.Slice(stringLiteralDataOffset, stringLiteralDataByteSize);

        byte[] newLiteralTable = new byte[stringLiteralByteSize];
        using var newDataPoolStream = new MemoryStream(stringLiteralDataByteSize + 4096);

        for (int i = 0; i < literalCount; i++)
        {
            ReadOnlySpan<byte> oldRow = oldLiteralTable.Slice(i * 8, 8);
            uint oldLen = BinaryPrimitives.ReadUInt32LittleEndian(oldRow[..4]);
            int oldDataIdx = BinaryPrimitives.ReadInt32LittleEndian(oldRow.Slice(4, 4));

            int newDataIdx = (int)newDataPoolStream.Position;
            uint newLen;

            if (literalReplacements.TryGetValue(i, out string? translated) && !string.IsNullOrEmpty(translated))
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(translated);
                newDataPoolStream.Write(utf8);
                newLen = (uint)utf8.Length;
                patchedCount++;
            }
            else if (oldLen > 0 && oldDataIdx >= 0 && (long)oldDataIdx + oldLen <= oldDataPool.Length)
            {
                newDataPoolStream.Write(oldDataPool.Slice(oldDataIdx, (int)oldLen));
                newLen = oldLen;
            }
            else
            {
                newLen = 0;
            }

            Span<byte> newRow = newLiteralTable.AsSpan(i * 8, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(newRow[..4], newLen);
            BinaryPrimitives.WriteInt32LittleEndian(newRow.Slice(4, 4), newDataIdx);
        }

        if (patchedCount == 0)
        {
            return originalData.ToArray();
        }

        int newDataByteSize = (int)newDataPoolStream.Length;
        int oldDataEnd = stringLiteralDataOffset + stringLiteralDataByteSize;
        bool hasTrailingSections = oldDataEnd < originalData.Length;

        // If there are subsequent metadata tables after StringLiteralData, keep 4-byte alignment
        int padAfterPool = 0;
        if (hasTrailingSections)
        {
            int rawDelta = newDataByteSize - stringLiteralDataByteSize;
            padAfterPool = (4 - (rawDelta & 3)) & 3;
            for (int p = 0; p < padAfterPool; p++)
            {
                newDataPoolStream.WriteByte(0);
            }
        }

        byte[] newDataPoolPadded = newDataPoolStream.ToArray();
        int sectionShiftDelta = newDataPoolPadded.Length - stringLiteralDataByteSize;

        // Determine header boundary (minimum positive section offset in the header, usually stringLiteralOffset)
        int headerSize = FindHeaderBoundary(originalData, stringLiteralOffset, stringLiteralDataOffset);

        int newTotalSize = originalData.Length + sectionShiftDelta;
        byte[] output = new byte[newTotalSize];

        // 1. Copy everything before stringLiteralDataOffset
        originalData[..stringLiteralDataOffset].CopyTo(output);

        // 2. Write new StringLiteralDataPool (plus alignment pad if trailing sections exist)
        newDataPoolPadded.CopyTo(output.AsSpan(stringLiteralDataOffset));

        // 3. Copy everything after old StringLiteralDataPool to shifted position
        if (hasTrailingSections)
        {
            originalData[oldDataEnd..].CopyTo(output.AsSpan(stringLiteralDataOffset + newDataPoolPadded.Length));
        }

        // 4. Write updated StringLiteral table (shifted if it lived after StringLiteralData)
        int updatedLiteralTableOffset = stringLiteralOffset >= oldDataEnd
            ? stringLiteralOffset + sectionShiftDelta
            : stringLiteralOffset;
        newLiteralTable.CopyTo(output.AsSpan(updatedLiteralTableOffset));

        // 5. Update Header: stringLiteralDataByteSize at 0x14 + shift all section offsets >= oldDataEnd
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(20, 4), newDataByteSize);

        for (int pos = 8; pos + 8 <= headerSize; pos += 8)
        {
            if (pos == 16)
            {
                // stringLiteralDataOffset itself does not move
                continue;
            }

            int secOffset = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(pos, 4));
            int secSize = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(pos + 4, 4));

            if (secOffset >= oldDataEnd && secOffset <= originalData.Length && secSize >= 0)
            {
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(pos, 4), secOffset + sectionShiftDelta);
            }
        }

        return output;
    }

    private static int FindHeaderBoundary(
        ReadOnlySpan<byte> data,
        int stringLiteralOffset,
        int stringLiteralDataOffset)
    {
        int minOffset = Math.Min(stringLiteralOffset, stringLiteralDataOffset);
        int maxScan = Math.Min(minOffset, 1024);

        for (int pos = 24; pos + 8 <= maxScan; pos += 8)
        {
            int candidateOffset = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos, 4));
            int candidateSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos + 4, 4));

            if (candidateOffset >= 24 &&
                candidateSize > 0 &&
                (long)candidateOffset + candidateSize <= data.Length &&
                candidateOffset < minOffset)
            {
                minOffset = candidateOffset;
                maxScan = Math.Min(minOffset, 1024);
            }
        }

        return minOffset;
    }
}
