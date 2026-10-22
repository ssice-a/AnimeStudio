using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace AnimeStudio.GUI;

/// <summary>The game's archive envelope, matching eiem_endfield_input.h.
/// UnityFS remains an offline interchange format; the game reads native VFS.</summary>
internal static class EiemEffectNativeArchive
{
    private static (ushort High, ushort Low) Size32(uint size)
    {
        uint value = BitOperations.RotateLeft(size ^ 0xF74324EEu, 18);
        ushort low = (ushort)value;
        return ((ushort)((value >> 16) ^ low ^ 0xA121), low);
    }
    private static (uint High, uint Low) Size64(ulong size)
    {
        ulong value = BitOperations.RotateLeft(size ^ 0xA4F1A11747816520ul, 50);
        uint low = (uint)value;
        return ((uint)(value >> 32) ^ low ^ 0xDAD76848u, low);
    }
    private static uint Count(uint count, uint mask, uint finalMask)
    {
        uint value = BitOperations.RotateLeft(count ^ finalMask, 18);
        ushort low = (ushort)value, high = (ushort)((value >> 16) ^ low);
        return (((uint)high << 16) | low) ^ mask;
    }
    private static void U16(Stream stream, ushort value)
    {
        Span<byte> data = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(data, value); stream.Write(data);
    }
    private static void U32(Stream stream, uint value)
    {
        Span<byte> data = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(data, value); stream.Write(data);
    }
    private static void Pad(Stream stream)
    {
        while (stream.Position % 16 != 0) stream.WriteByte(0);
    }
    internal static byte[] Write(IReadOnlyList<(string Name, byte[] Bytes)> members)
    {
        if (members.Count == 0) throw new InvalidDataException("Native effect archive is empty");
        using var payload = new MemoryStream(); var offsets = new List<ulong>();
        foreach (var member in members)
        {
            if (string.IsNullOrEmpty(member.Name) || member.Name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
                throw new InvalidDataException("Invalid native effect archive member");
            Pad(payload); offsets.Add((ulong)payload.Position); payload.Write(member.Bytes);
        }
        if (payload.Length == 0) throw new InvalidDataException("Native effect archive payload is empty");
        using var info = new MemoryStream(); U32(info, Count(1, 0x23F77B8A, 0x91CE0A4F));
        var block = Size32(checked((uint)payload.Length));
        ushort rotated = (ushort)(((ushort)(block.Low ^ 0x523F) << 2) | ((ushort)(block.Low ^ 0x523F) >> 14));
        byte low = (byte)rotated, high = (byte)((rotated >> 8) ^ low);
        U16(info, block.High); U16(info, block.High); U16(info, block.Low);
        U16(info, (ushort)((((ushort)high << 8) | low) ^ 0x9CD6)); U16(info, block.Low);
        U32(info, Count((uint)members.Count, 0x6B0AE55D, 0xE4C1D9F2));
        for (int i = 0; i < members.Count; i++)
        {
            var member = members[i]; var size = Size64((ulong)member.Bytes.LongLength); var offset = Size64(offsets[i]);
            uint flags = member.Name.StartsWith("CAB-", StringComparison.OrdinalIgnoreCase) &&
                !member.Name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) ? 4u : 0u;
            uint encoded = BitOperations.RotateLeft(flags ^ 0xF13927C4u ^ size.High, 18);
            ushort flagLow = (ushort)encoded, flagHigh = (ushort)((encoded >> 16) ^ flagLow);
            U32(info, (((uint)flagHigh << 16) | flagLow) ^ 0x8E06A9F8);
            U32(info, size.High); U32(info, offset.Low); U32(info, offset.High);
            for (int j = 0; j < member.Name.Length; j++)
            {
                if (member.Name[j] >= 128) throw new InvalidDataException("Native archive member must be ASCII");
                info.WriteByte((byte)(member.Name[j] ^ (byte)(j ^ 0x97)));
            }
            info.WriteByte(0); U32(info, size.Low);
        }
        using var output = new MemoryStream(); output.Write(new byte[48]); info.Position = 0; info.CopyTo(output);
        Pad(output); payload.Position = 0; payload.CopyTo(output); byte[] result = output.ToArray();
        void Header32(int position, uint value) => BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(position), value);
        void Header16(int position, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(position), value);
        var infoSize = Size32(checked((uint)info.Length)); var fileSize = Size64((ulong)result.LongLength);
        Header32(0, 0x4A92F0CD); Header32(4, 0xD8B1E637); Header16(8, infoSize.Low);
        Header32(14, 8); Header32(18, fileSize.Low); Header32(22, 0x240 ^ 0xA7F49310);
        Header16(26, infoSize.High); Header16(32, infoSize.Low); Header32(34, fileSize.High); Header16(38, infoSize.High);
        return result;
    }
}
