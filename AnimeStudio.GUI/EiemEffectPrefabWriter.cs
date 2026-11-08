using AnimeStudio;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeStudio.GUI;

/// <summary>Build author object payloads from game TypeTrees without retaining source PFB objects.</summary>
internal static class EiemEffectPrefabWriter
{
    internal sealed record NativeObject(long PathId, int TypeId, byte[] Payload);

    internal static byte[] CreateMetadataOnly(SerializedFile file, byte[] original)
    {
        if ((int)file.header.m_Version != 22 || file.reader.Endian != EndianType.LittleEndian ||
            original.LongLength != file.header.m_FileSize ||
            file.ObjectTableEnd - file.ObjectTableStart != file.m_Objects.Count * 24L ||
            file.m_RefTypes?.Count > 0 ||
            file.ObjectCountOffset < 48 || file.ObjectCountOffset + 4 > file.ObjectTableStart ||
            file.ObjectTableEnd > 48L + file.header.m_MetadataSize ||
            file.header.m_DataOffset < 48L + file.header.m_MetadataSize)
            throw new InvalidDataException("Unsupported SerializedFile TypeTree template");

        using var metadata = new MemoryStream();
        metadata.Write(original, 48, checked((int)file.ObjectCountOffset - 48));
        using var writer = new BinaryWriter(metadata, System.Text.Encoding.UTF8, true);
        writer.Write(0);
        writer.Write(0); // script types
        writer.Write(0); // external files
        writer.Write(0); // managed reference types
        writer.Write((byte)0); // user information

        long dataOffset = (48L + metadata.Length + 15) & ~15L;
        var output = new byte[checked((int)dataOffset)];
        original.AsSpan(0, 48).CopyTo(output);
        metadata.ToArray().CopyTo(output, 48);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(20), checked((uint)metadata.Length));
        BinaryPrimitives.WriteInt64BigEndian(output.AsSpan(24), dataOffset);
        BinaryPrimitives.WriteInt64BigEndian(output.AsSpan(32), dataOffset);
        return output;
    }

    internal static byte[] Build(SerializedFile template, byte[] metadataOnly, IReadOnlyList<NativeObject> objects)
    {
        if ((int)template.header.m_Version != 22 || template.reader.Endian != EndianType.LittleEndian ||
            template.header.m_FileSize != metadataOnly.LongLength || template.m_Objects.Count != 0 ||
            template.ObjectTableEnd != template.ObjectCountOffset + 4 ||
            template.ObjectTableStart != ((template.ObjectCountOffset + 4 + 3) & ~3L) ||
            template.ObjectCountOffset < 48 || template.ObjectCountOffset + 4 > template.ObjectTableStart ||
            template.ObjectTableEnd > 48L + template.header.m_MetadataSize ||
            template.header.m_DataOffset < 48L + template.header.m_MetadataSize ||
            template.m_Externals.Count != 0 || template.m_RefTypes.Count != 0 || objects.Count == 0)
            throw new InvalidDataException("Expected an empty little-endian v22 metadata template");

        var rows = objects.OrderBy(o => o.PathId).ToArray();
        if (rows.Any(o => o.PathId == 0 || o.Payload == null || o.Payload.Length == 0 ||
                          o.TypeId < 0 || o.TypeId >= template.m_Types.Count) ||
            rows.Select(o => o.PathId).Distinct().Count() != rows.Length)
            throw new InvalidDataException("Invalid authored SerializedFile object table");

        using var metadata = new MemoryStream();
        metadata.Write(metadataOnly, 48, checked((int)template.ObjectCountOffset - 48));
        using var writer = new BinaryWriter(metadata, System.Text.Encoding.UTF8, true);
        writer.Write(rows.Length);
        while ((48 + metadata.Position) % 4 != 0) writer.Write((byte)0);
        long tableStart = metadata.Position;
        metadata.Write(new byte[checked(rows.Length * 24)]);
        metadata.Write(metadataOnly, checked((int)template.ObjectTableEnd),
            checked(48 + (int)template.header.m_MetadataSize - (int)template.ObjectTableEnd));

        long dataOffset = (48L + metadata.Length + 15) & ~15L;
        using var payloads = new MemoryStream();
        var ranges = new Dictionary<long, (long Offset, uint Size)>();
        foreach (var row in rows)
        {
            while (payloads.Position % 8 != 0) payloads.WriteByte(0);
            long offset = payloads.Position;
            payloads.Write(row.Payload);
            ranges.Add(row.PathId, (offset, checked((uint)row.Payload.Length)));
        }
        foreach (var row in rows)
        {
            var range = ranges[row.PathId];
            writer.BaseStream.Position = tableStart + Array.IndexOf(rows, row) * 24L;
            writer.Write(row.PathId);
            writer.Write(range.Offset);
            writer.Write(range.Size);
            writer.Write(row.TypeId);
        }

        byte[] metadataBytes = metadata.ToArray();
        byte[] payloadBytes = payloads.ToArray();
        byte[] output = new byte[checked((int)dataOffset + payloadBytes.Length)];
        metadataOnly.AsSpan(0, 48).CopyTo(output);
        metadataBytes.CopyTo(output, 48);
        payloadBytes.CopyTo(output, checked((int)dataOffset));
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(20), checked((uint)metadataBytes.Length));
        BinaryPrimitives.WriteInt64BigEndian(output.AsSpan(24), output.LongLength);
        BinaryPrimitives.WriteInt64BigEndian(output.AsSpan(32), dataOffset);
        return output;
    }
}
