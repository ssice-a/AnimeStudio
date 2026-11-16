using AnimeStudio;
using Newtonsoft.Json.Linq;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeStudio.GUI;

/// <summary>The runtime payload is the final Prefab's actual native reference
/// closure, not its source bundle's historical object and stream inventory.</summary>
internal static class EiemEffectRuntimeClosure
{
    internal sealed record Result(Dictionary<string, byte[]> Files, Dictionary<string, byte[]> Streams,
        Dictionary<string, HashSet<string>> Dependencies);
    private sealed record Native(SerializedFile File, byte[] Bytes, Dictionary<long, ObjectInfo> Rows);

    internal static Result Build(string source, JObject baseline, string root,
        IReadOnlyDictionary<string, (SerializedFile File, byte[] Bytes, JToken Row)> files,
        IReadOnlyDictionary<string, byte[]> products, IReadOnlySet<string> selected)
    {
        var readers = new List<FileReader>();
        try
        {
            var native = new Dictionary<string, Native>(StringComparer.OrdinalIgnoreCase);
            foreach (string cab in selected.Order(StringComparer.Ordinal))
            {
                byte[] bytes = products.GetValueOrDefault(cab) ?? files[cab].Bytes;
                var reader = new FileReader(cab, new MemoryStream(bytes, false)); readers.Add(reader);
                var file = new SerializedFile(reader, files[cab].File.assetsManager);
                native.Add(cab, new(file, bytes, file.m_Objects.ToDictionary(o => o.m_PathID)));
            }
            var retained = native.Keys.ToDictionary(c => c, _ => new HashSet<long>(), StringComparer.OrdinalIgnoreCase);
            var dependencies = native.Keys.ToDictionary(c => c, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            var documents = new Dictionary<string, Dictionary<long, TypeTreeDocument>>(StringComparer.OrdinalIgnoreCase);
            foreach (string cab in native.Keys) documents.Add(cab, new());
            var scriptFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<(string Cab, long Id)>();
            int separator = root.LastIndexOf(':');
            pending.Enqueue((root[..separator], long.Parse(root[(separator + 1)..])));

            (string Cab, long Id) Target(string cab, int fid, long pid)
            {
                if (pid == 0) return (null, 0);
                var file = native[cab].File;
                if (fid < 0 || fid > file.m_Externals.Count) throw new InvalidDataException("Invalid runtime native PPtr file index");
                return (fid == 0 ? cab : file.m_Externals[fid - 1].fileName, pid);
            }
            void Enqueue(string cab, int fid, long pid)
            {
                var target = Target(cab, fid, pid);
                if (target.Cab != null && native.ContainsKey(target.Cab))
                {
                    if (!string.Equals(cab, target.Cab, StringComparison.OrdinalIgnoreCase)) dependencies[cab].Add(target.Cab);
                    pending.Enqueue(target);
                }
            }
            TypeTreeDocument Read(string cab, ObjectInfo row)
            {
                if (!documents[cab].TryGetValue(row.m_PathID, out var doc))
                {
                    var value = native[cab];
                    doc = new TypeTreeDocument(row.serializedType.m_Type,
                        value.Bytes.AsSpan(checked((int)row.byteStart), checked((int)row.byteSize)).ToArray(), value.File.m_RefTypes);
                    documents[cab].Add(row.m_PathID, doc);
                }
                return doc;
            }
            while (pending.TryDequeue(out var current))
            {
                if (!retained[current.Cab].Add(current.Id)) continue;
                var value = native[current.Cab];
                if (!value.Rows.TryGetValue(current.Id, out var row))
                    throw new InvalidDataException("Runtime Prefab points to a missing selected object: " + current.Cab + ":" + current.Id);
                if (row.classID == 142) continue; // Rebuilt below; old preload rows are not roots.
                var doc = Read(current.Cab, row);
                foreach (var pointer in doc.Root.Walk().Where(n => n.Value.Schema.Type.StartsWith("PPtr<", StringComparison.Ordinal)))
                    Enqueue(current.Cab, Convert.ToInt32(pointer.Value["m_FileID"].Scalar), Convert.ToInt64(pointer.Value["m_PathID"].Scalar));
                if (scriptFiles.Add(current.Cab))
                {
                    // Script type indices are metadata references, not TypeTree
                    // PPtrs. Preserve their exact table and its implicit targets.
                    int at = checked((int)value.File.ObjectTableEnd);
                    int count = BinaryPrimitives.ReadInt32LittleEndian(value.Bytes.AsSpan(at)); at += 4;
                    if (count < 0 || count > (value.File.ExternalTableStart - at) / 12)
                        throw new InvalidDataException("Invalid runtime script table");
                    for (int i = 0; i < count; i++)
                    {
                        int fid = BinaryPrimitives.ReadInt32LittleEndian(value.Bytes.AsSpan(at)); at = (at + 4 + 3) & ~3;
                        long pid = BinaryPrimitives.ReadInt64LittleEndian(value.Bytes.AsSpan(at)); at += 8;
                        Enqueue(current.Cab, fid, pid);
                    }
                    if (at != value.File.ExternalTableStart) throw new InvalidDataException("Runtime script table extent differs");
                }
            }

            var streamRows = (baseline["rawStreams"] as JArray ?? new JArray()).ToDictionary(
                s => (string)s["member"], StringComparer.OrdinalIgnoreCase);
            var streamInputs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var streamOutputs = new Dictionary<string, MemoryStream>(StringComparer.OrdinalIgnoreCase);
            var streamRanges = new Dictionary<(string Name, long Offset, long Size), long>();
            foreach (string cab in native.Keys.Order(StringComparer.Ordinal))
                foreach (long id in retained[cab].Order())
                {
                    var row = native[cab].Rows[id];
                    if (row.classID == 142) continue;
                    foreach (var item in Read(cab, row).Root.Walk())
                    {
                        var node = item.Value;
                        if (!node.Fields.TryGetValue("path", out var path) || path.Scalar is not string name ||
                            !node.Fields.TryGetValue("offset", out var offset) || !node.Fields.TryGetValue("size", out var size)) continue;
                        long length = Convert.ToInt64(size.Scalar), start = Convert.ToInt64(offset.Scalar);
                        if (length == 0) continue;
                        string member = name.Split('/').Last();
                        if (!streamRows.TryGetValue(member, out var stream))
                            throw new InvalidDataException("Reachable runtime stream has no author snapshot: " + name);
                        if (!streamInputs.TryGetValue(member, out byte[] bytes))
                        {
                            bytes = File.ReadAllBytes(EiemEffectBuildCommand.SafePath(source, (string)stream["snapshot"]["file"]));
                            if (!string.Equals(EiemEffectBuildCommand.Hash(bytes), (string)stream["snapshot"]["sha256"], StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Runtime stream hash mismatch");
                            streamInputs.Add(member, bytes);
                            streamOutputs.Add(member, new MemoryStream());
                        }
                        if (start < 0 || length < 0 || start > bytes.LongLength - length)
                            throw new InvalidDataException("Runtime stream range escapes its snapshot");
                        var key = (member.ToLowerInvariant(), start, length);
                        if (!streamRanges.TryGetValue(key, out long moved))
                        {
                            var output = streamOutputs[member];
                            while (output.Position % 16 != 0) output.WriteByte(0);
                            moved = output.Position;
                            output.Write(bytes, checked((int)start), checked((int)length));
                            streamRanges.Add(key, moved);
                        }
                        offset.Scalar = moved;
                    }
                }
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string cab in native.Keys.Order(StringComparer.Ordinal))
            {
                if (retained[cab].Count == 0) continue;
                var value = native[cab];
                foreach (var bundle in value.File.m_Objects.Where(o => o.classID == 142))
                {
                    var doc = Read(cab, bundle); var data = (JObject)doc.Root.ToJson();
                    var pointers = new HashSet<(int Fid, long Id)>();
                    foreach (long id in retained[cab].Where(id => value.Rows[id].classID != 142))
                    {
                        pointers.Add((0, id));
                        foreach (var p in Read(cab, value.Rows[id]).Root.Walk().Where(p => p.Value.Schema.Type.StartsWith("PPtr<", StringComparison.Ordinal)))
                        {
                            int fid = Convert.ToInt32(p.Value["m_FileID"].Scalar); long pid = Convert.ToInt64(p.Value["m_PathID"].Scalar);
                            if (pid != 0) pointers.Add((fid, pid));
                        }
                    }
                    JObject Pointer((int Fid, long Id) ptr) => new() { ["m_FileID"] = ptr.Fid, ["m_PathID"] = ptr.Id };
                    bool Kept(JToken ptr)
                    {
                        if (ptr == null) return false;
                        var target = Target(cab, (int)ptr["m_FileID"], (long)ptr["m_PathID"]);
                        return target.Cab != null && retained.TryGetValue(target.Cab, out var ids) && ids.Contains(target.Id);
                    }
                    data["m_PreloadTable"] = new JArray(pointers.OrderBy(p => p.Fid).ThenBy(p => p.Id).Select(Pointer));
                    var containers = new JArray();
                    foreach (var entry in data["m_Container"] ?? new JArray())
                        if (Kept(entry["second"]?["asset"]))
                        {
                            var clone = entry.DeepClone(); clone["second"]["preloadIndex"] = 0; clone["second"]["preloadSize"] = pointers.Count;
                            containers.Add(clone);
                        }
                    data["m_Container"] = containers;
                    if (data["m_MainAsset"] is JObject main)
                    {
                        bool kept = Kept(main["asset"]);
                        main["preloadIndex"] = 0; main["preloadSize"] = kept ? pointers.Count : 0;
                        if (!kept) main["asset"] = Pointer((0, 0));
                    }
                    if (data["m_Dependencies"] is JArray) data["m_Dependencies"] = new JArray();
                    doc.ApplyJson(data); retained[cab].Add(bundle.m_PathID);
                }
                var replacements = retained[cab].ToDictionary(id => id, id => Read(cab, value.Rows[id]).Encode());
                result.Add(cab, EiemEffectAssetEdits.Rebuild(value.File, value.Bytes, replacements,
                    Array.Empty<EiemEffectAssetEdits.Asset>(), retained[cab]));
            }
            var outputStreams = streamOutputs.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            foreach (var stream in streamOutputs.Values) stream.Dispose();
            return new(result, outputStreams, dependencies);
        }
        finally { foreach (var reader in readers) reader.Dispose(); }
    }
}
