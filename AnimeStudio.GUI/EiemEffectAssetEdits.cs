using AnimeStudio;
using Newtonsoft.Json.Linq;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeStudio.GUI;

/// <summary>Only schema-backed Mesh/Texture2D/Material clones; no external-table or effect-topology changes.</summary>
internal static class EiemEffectAssetEdits
{
    internal sealed record Asset(string Identity, string Cab, long Id, int Type, int ClassId,
        byte[] Payload, TypeTreeDocument Document, JToken Before);

    internal static List<Asset> Read(JToken input, IReadOnlyDictionary<string, JToken> objects,
        IReadOnlyDictionary<string, (SerializedFile File, byte[] Bytes, JToken Row)> files)
    {
        if (input == null) return new();
        if (input is not JArray rows || rows.Count > 4096) throw new InvalidDataException("Invalid authored effect assets");
        var result = new List<Asset>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in rows)
        {
            string identity = (string)item["identity"], template = (string)item["template"];
            if (identity == null || !seen.Add(identity) || objects.ContainsKey(identity) ||
                template == null || !objects.TryGetValue(template, out var source))
                throw new InvalidDataException("Duplicate or missing authored asset identity/template");
            string cab = (string)source["native"]["cab"];
            int split = identity.LastIndexOf(':');
            if (split <= 0 || !identity[..split].Equals(cab, StringComparison.OrdinalIgnoreCase) ||
                !long.TryParse(identity[(split + 1)..], out long id) || id == 0)
                throw new InvalidDataException("Authored asset must use its template's native file");
            var file = files[cab];
            if (file.File.m_Objects.Any(o => o.m_PathID == id)) throw new InvalidDataException("Authored PathID collides with source");
            var row = file.File.m_Objects.Single(o => o.m_PathID == (long)source["native"]["pathId"]);
            if (row.classID is not (21 or 28 or 43)) throw new InvalidDataException("Only particle Mesh/Texture2D/Material assets may be authored");
            byte[] original = file.Bytes.AsSpan(checked((int)row.byteStart), checked((int)row.byteSize)).ToArray();
            if (!EiemEffectBuildCommand.Hash(original).Equals((string)item["sourceObjectSha256"], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Authored asset template hash mismatch");
            string encoded = (string)item["payloadBase64"];
            if (encoded == null || encoded.Length > 384 * 1024 * 1024) throw new InvalidDataException("Authored asset payload exceeds limit");
            byte[] payload;
            try { payload = Convert.FromBase64String(encoded); }
            catch (FormatException e) { throw new InvalidDataException("Invalid authored asset payload", e); }
            total += payload.LongLength;
            if (total > 256 * 1024 * 1024) throw new InvalidDataException("Authored assets exceed 256 MiB");
            var before = new TypeTreeDocument(row.serializedType.m_Type, original, file.File.m_RefTypes).Root.ToJson();
            var doc = new TypeTreeDocument(row.serializedType.m_Type, payload, file.File.m_RefTypes);
            ValidateResource(row.classID, before, doc);
            result.Add(new(identity, cab, id, row.typeID, row.classID, payload, doc, before));
        }
        return result;
    }

    private static void ValidateResource(int kind, JToken before, TypeTreeDocument doc)
    {
        var after = doc.Root.ToJson();
        string[] allowed = kind switch
        {
            43 => new[] { "m_Name", "m_SubMeshes", "m_Shapes", "m_BindPose", "m_BoneNameHashes", "m_RootBoneNameHash",
                "m_BonesAABB", "m_VariableBoneCountWeights", "m_MeshCompression", "m_IndexFormat", "m_IndexBuffer",
                "m_VertexData", "m_CompressedMesh", "m_LocalAABB", "m_StreamData", "m_BonesPerVertex" },
            28 => new[] { "m_Name", "m_Width", "m_Height", "m_CompleteImageSize", "m_MipsStripped", "m_TextureFormat",
                "m_MipCount", "m_IsPreProcessed", "m_StreamingMipmaps", "m_ColorSpace", "m_TextureSettings",
                "m_PlatformBlob", "image data", "m_StreamData" },
            _ => null
        };
        if (allowed != null)
            foreach (var property in ((JObject)before).Properties())
                if (!allowed.Contains(property.Name) && !JToken.DeepEquals(property.Value, after[property.Name]))
                    throw new InvalidDataException("Authored asset changed a protected field: " + property.Name);
        foreach (var entry in doc.Root.Walk())
            if (entry.Value.Scalar is float number && !float.IsFinite(number) ||
                entry.Value.Scalar is double wide && !double.IsFinite(wide))
                throw new InvalidDataException("Authored resource contains a nonfinite value");
        if (kind == 28)
        {
            int width = (int)after["m_Width"], height = (int)after["m_Height"], mips = (int)after["m_MipCount"];
            if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || mips <= 0 ||
                mips > 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height))) ||
                (int)after["m_TextureFormat"] != 4 || (int)after["m_ColorSpace"] is not (0 or 1) ||
                (int)after["m_ImageCount"] != 1 || (int)after["m_TextureDimension"] != 2 ||
                (long)after["m_StreamData"]["size"] != 0 || (string)after["m_StreamData"]["path"] != "")
                throw new InvalidDataException("Invalid authored Texture2D metadata");
            long size = 0; for (int i = 0; i < mips; i++) size += (long)Math.Max(1, width >> i) * Math.Max(1, height >> i) * 4;
            if (doc.Root["image data"].Length != size + 4 || (long)after["m_CompleteImageSize"] != size)
                throw new InvalidDataException("Authored texture pixel/mipmap extent mismatch");
        }
        if (kind == 43 && ((int)after["m_VertexData"]["m_VertexCount"] is <= 0 or > 4000000 ||
            (long)after["m_StreamData"]["size"] != 0 || (string)after["m_StreamData"]["path"] != "" ||
            ((JArray)after["m_BindPose"]).Count != 0 || (int)after["m_BonesPerVertex"] != 0))
            throw new InvalidDataException("Authored particle mesh must contain static inline geometry");
    }

    internal static byte[] Rebuild(SerializedFile file, byte[] original,
        IReadOnlyDictionary<long, byte[]> replacements, IReadOnlyList<Asset> additions)
    {
        if (additions.Count == 0) return EiemEffectBuildCommand.Rebuild(file, original, replacements);
        if ((int)file.header.m_Version != 22 || file.ObjectTableEnd - file.ObjectTableStart != file.m_Objects.Count * 24L ||
            file.ObjectCountOffset < 48 || file.ObjectCountOffset + 4 > file.ObjectTableStart)
            throw new InvalidDataException("Unsupported native object table for added assets");
        var rows = file.m_Objects.Select(o => (Id:o.m_PathID, Type:o.typeID,
            Bytes:replacements.GetValueOrDefault(o.m_PathID) ?? original.AsSpan((int)o.byteStart, (int)o.byteSize).ToArray()))
            .Concat(additions.Select(a => (Id:a.Id, Type:a.Type, Bytes:a.Payload))).OrderBy(o => o.Id).ToArray();
        if (rows.Select(r => r.Id).Distinct().Count() != rows.Length) throw new InvalidDataException("Duplicate native asset ID");
        using var metadata = new MemoryStream();
        metadata.Write(original, 48, checked((int)file.ObjectCountOffset - 48));
        using var writer = new BinaryWriter(metadata, System.Text.Encoding.UTF8, true);
        writer.Write(rows.Length);
        while (metadata.Position % 4 != 0) writer.Write((byte)0);
        long table = metadata.Position; writer.Write(new byte[rows.Length * 24]);
        metadata.Write(original, (int)file.ObjectTableEnd, checked(48 + (int)file.header.m_MetadataSize - (int)file.ObjectTableEnd));
        long offset = (48 + metadata.Length + 15) & ~15L;
        using var output = new MemoryStream(); output.Write(original, 0, 48);
        output.Write(metadata.ToArray()); while (output.Position < offset) output.WriteByte(0);
        for (int i = 0; i < rows.Length; i++)
        {
            while (output.Position % 8 != 0) output.WriteByte(0);
            long start = output.Position - offset; output.Write(rows[i].Bytes);
            metadata.Position = table + i * 24; writer.Write(rows[i].Id); writer.Write(start);
            writer.Write((uint)rows[i].Bytes.Length); writer.Write(rows[i].Type);
        }
        byte[] result = output.ToArray(); metadata.ToArray().CopyTo(result, 48);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20), (uint)metadata.Length);
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(24), result.LongLength);
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(32), offset);
        return result;
    }
}
