using AnimeStudio;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AnimeStudio.GUI;

/// <summary>Offline edit validation and native reconstruction. Never installs a Mod.</summary>
internal static class EiemEffectBuildCommand
{
    internal static int Run(string[] args)
    {
        if (args.Length != 4) { Console.Error.WriteLine("Usage: --build-eff-effects <efffx-directory> <edits.json> <output-directory>"); return 2; }
        try
        {
            Build(args[1], args[2], args[3]);
            File.WriteAllText(args[3] + ".status.txt", "离线构建和重新解析通过；未安装到游戏。");
            Console.WriteLine("Effect build verified: " + args[3]); return 0;
        }
        catch (Exception error)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3])));
            File.WriteAllText(args[3] + ".status.txt", "特效构建失败：" + error.Message);
            Console.Error.WriteLine(error); return 1;
        }
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string SafePath(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') ||
            relative.Split('/').Any(p => p is "" or "." or "..") || Path.IsPathRooted(relative)) throw new InvalidDataException("Unsafe effect source path");
        string root = Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Effect source path escapes directory");
        return path;
    }

    internal static void Build(string source, string editsPath, string output)
    {
        source = Path.GetFullPath(source).TrimEnd('\\', '/'); output = Path.GetFullPath(output).TrimEnd('\\', '/');
        if (source.Equals(output, StringComparison.OrdinalIgnoreCase) || source.StartsWith(output + "\\", StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Build output must be separate from its source");
        using var gate = new System.Threading.Mutex(false, EiemPackagePublication.LockName(source));
        bool acquired; try { acquired = gate.WaitOne(0); } catch (System.Threading.AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Effect source is being updated");
        var opened = new List<FileReader>();
        try
        {
            var edits = JObject.Parse(File.ReadAllText(editsPath));
            if ((string)edits["format"] != "EFF_EFFECT_EDIT" || (int?)edits["version"] != 1 || edits["objects"] is not JArray changes || changes.Count > 4096)
                throw new InvalidDataException("Unsupported effect edit document");
            bool allLods = EiemEffectLodPolicy.Read(edits);
            byte[] baselineBytes = File.ReadAllBytes(SafePath(source, "source/manifest.json")), effectBytes = File.ReadAllBytes(SafePath(source, "effect.json"));
            if (!string.Equals(Hash(baselineBytes), (string)edits["sourceManifestSha256"], StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Hash(effectBytes), (string)edits["sourceEffectSha256"], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Effect source changed after import; reimport before building");
            var baseline = JObject.Parse(Encoding.UTF8.GetString(baselineBytes)); var effect = JObject.Parse(Encoding.UTF8.GetString(effectBytes));
            if ((string)baseline["format"] != "EFFSOURCE" || (string)effect["format"] != "EFF_EFFECT_SOURCE" ||
                (int?)baseline["version"] != 1 || (int?)effect["version"] != 1 || (string)baseline["root"] != (string)effect["root"])
                throw new InvalidDataException("Unsupported or inconsistent effect baseline");
            var objects = effect["objects"].ToDictionary(o => (string)o["identity"], StringComparer.OrdinalIgnoreCase);
            var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.ArknightsEndfield) };
            var nativeFiles = new Dictionary<string, (SerializedFile File, byte[] Bytes, JToken Row)>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in baseline["serializedFiles"])
            {
                byte[] bytes = ReadSnapshot(source, row["snapshot"]);
                var reader = new FileReader((string)row["cab"], new MemoryStream(bytes, false)); opened.Add(reader);
                var file = new SerializedFile(reader, manager);
                if ((int)file.header.m_Version != 22 || file.reader.Endian != EndianType.LittleEndian || file.header.m_FileSize != bytes.Length)
                    throw new InvalidDataException("Effect rebuild supports complete little-endian v22 sources");
                nativeFiles.Add(file.fileName, (file, bytes, row));
            }
            var authored = EiemEffectAssetEdits.Read(edits["assets"], objects, nativeFiles);
            var emitters = new EiemEffectEmitterEdits(edits["emitters"], effect, objects, nativeFiles);
            var authoredTypes = authored.ToDictionary(a => a.Identity, a => a.ClassId, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in emitters.Types)
                if (!authoredTypes.TryAdd(pair.Key, pair.Value))throw new InvalidDataException("Emitter identity collides with authored resource");
            var validationFiles = nativeFiles.ToDictionary(p => p.Key, p => p.Value.File, StringComparer.OrdinalIgnoreCase);
            foreach (var asset in authored)
                ValidateEdit(asset.Before, asset.Document, nativeFiles[asset.Cab].File, validationFiles, authoredTypes);
            emitters.Compile(authoredTypes);
            authored.AddRange(emitters.Assets);
            var changed = new Dictionary<string, Dictionary<long, byte[]>>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in authored)
                changed.TryAdd(asset.Cab, new());
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var edit in changes)
            {
                string identity = (string)edit["identity"];
                if (identity == null || !seen.Add(identity) || !objects.TryGetValue(identity, out var obj) || (string)obj["state"] != "parsed")
                    throw new InvalidDataException("Unknown, duplicate or opaque effect edit: " + identity);
                int classId = (int)obj["classId"];
                if (classId is not (4 or 198 or 199 or 114 or 21)) throw new InvalidDataException("This object is not editable yet: " + identity);
                var dataBytes = File.ReadAllBytes(SafePath(source, (string)obj["data"]));
                if (!string.Equals(Hash(dataBytes), (string)edit["sourceDataSha256"], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Edited object baseline changed: " + identity);
                var owner = nativeFiles[(string)obj["native"]["cab"]];
                var row = owner.File.m_Objects.Single(r => r.m_PathID == (long)obj["native"]["pathId"]);
                byte[] original = owner.Bytes.AsSpan(checked((int)row.byteStart), checked((int)row.byteSize)).ToArray();
                var document = new TypeTreeDocument(row.serializedType.m_Type, original, owner.File.m_RefTypes);
                // JSON decimal text is rounded to the native scalar width before
                // comparison; comparing boxed Single and Double loses that contract.
                document.ApplyJson(JObject.Parse(Encoding.UTF8.GetString(dataBytes)));
                if (!document.Encode().AsSpan().SequenceEqual(original)) throw new InvalidDataException("Exported JSON differs from native source: " + identity);
                var before = document.Root.ToJson(); document.ApplyJson(edit["data"]);
                ValidateEdit(before, document, owner.File, validationFiles, authoredTypes);
                byte[] result = document.Encode();
                if (result.AsSpan().SequenceEqual(original)) continue;
                if (!changed.TryGetValue(owner.File.fileName, out var replacements)) changed.Add(owner.File.fileName, replacements = new());
                replacements.Add(row.m_PathID, result);
            }
            emitters.Apply(changed);
            var products = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in changed)
            {
                var original = nativeFiles[pair.Key];
                var added = authored.Where(a => a.Cab.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
                var rebuilt = EiemEffectAssetEdits.Rebuild(original.File, original.Bytes, pair.Value, added);
                // Validate every object, including all untouched/unknown payloads.
                using var checkReader = new FileReader(pair.Key, new MemoryStream(rebuilt, false));
                var check = new SerializedFile(checkReader, manager);
                if (check.m_Objects.Count != original.File.m_Objects.Count + added.Length) throw new InvalidDataException("Rebuild object count differs");
                foreach (var row in check.m_Objects)
                {
                    var asset = added.SingleOrDefault(a => a.Id == row.m_PathID);
                    if (asset != null)
                    {
                        if (row.typeID != asset.Type || !rebuilt.AsSpan((int)row.byteStart, (int)row.byteSize).SequenceEqual(asset.Payload))
                            throw new InvalidDataException("Authored resource native roundtrip mismatch");
                        _ = new TypeTreeDocument(row.serializedType.m_Type, asset.Payload, check.m_RefTypes);
                        continue;
                    }
                    var prior = original.File.m_Objects.Single(r => r.m_PathID == row.m_PathID);
                    var expected = pair.Value.TryGetValue(row.m_PathID, out var replacement) ? replacement : original.Bytes.AsSpan((int)prior.byteStart, (int)prior.byteSize).ToArray();
                    if (row.typeID != prior.typeID || !rebuilt.AsSpan((int)row.byteStart, (int)row.byteSize).SequenceEqual(expected)) throw new InvalidDataException("Native rebuild object mismatch");
                    if (replacement != null) _ = new TypeTreeDocument(row.serializedType.m_Type, replacement, check.m_RefTypes);
                }
                products.Add(pair.Key, rebuilt);
            }
            EiemPackagePublication.Publish(output, staged =>
            {
                Directory.CreateDirectory(Path.Combine(staged, "native")); Directory.CreateDirectory(Path.Combine(staged, "bundles")); Directory.CreateDirectory(Path.Combine(staged, "source"));
                foreach (var product in products) File.WriteAllBytes(Path.Combine(staged, "native", EiemPackageIdentity.SnapshotFileName(product.Key)), product.Value);
                EiemEffectRuntimeWriter.Write(staged, source, baseline, effect, nativeFiles, products, allLods);
                var bundles = new List<object>();
                foreach (var group in products.Keys.Select(cab => (string)nativeFiles[cab].Row["sourceBundle"]).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var files = nativeFiles.Where(p => (string)p.Value.Row["sourceBundle"] == group).ToArray();
                    var cabs = files.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var members = files.Select(p => (Name:p.Key, Bytes:products.GetValueOrDefault(p.Key) ?? p.Value.Bytes)).ToList();
                    foreach (var stream in baseline["rawStreams"].Where(s => cabs.Contains(Path.GetFileNameWithoutExtension((string)s["member"]))))
                        members.Add(((string)stream["member"], ReadSnapshot(source, stream["snapshot"])));
                    byte[] bundle = UnityFs(members, files[0].Value.File.unityVersion);
                    using var bundleReader = new FileReader("effect.ab", new MemoryStream(bundle, false));
                    var loaded = new BundleFile(bundleReader, manager.Game);
                    if (loaded.fileList.Count != members.Count) throw new InvalidDataException("Bundle member count differs");
                    foreach (var member in loaded.fileList)
                    {
                        var expected = members.Single(m => m.Name == member.fileName).Bytes;
                        using var buffer = new MemoryStream(); member.stream.CopyTo(buffer);
                        if (!buffer.ToArray().AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Bundle member roundtrip differs");
                        member.stream.Dispose();
                    }
                    string relative = "bundles/" + Hash(Encoding.UTF8.GetBytes(group)).ToLowerInvariant() + ".ab";
                    File.WriteAllBytes(Path.Combine(staged, relative), bundle);
                    bundles.Add(new { sourceBundle = group, file = relative, sha256 = Hash(bundle), size = bundle.Length });
                }
                var receipt = new { format = "EFF_EFFECT_BUILD", version = 1, prefab = (string)effect["prefab"],
                    lodCoverage = allLods ? "all" : "source",
                    sourceManifestSha256 = Hash(baselineBytes), sourceEffectSha256 = Hash(effectBytes),
                    changedObjects = changed.Sum(p => p.Value.Count), addedAssets = authored.Count,
                    nativeFiles = products.Select(p => new { cab = p.Key, file = "native/" + EiemPackageIdentity.SnapshotFileName(p.Key), sha256 = Hash(p.Value), size = p.Value.Length }),
                    bundles, offlineVerified = true, gameVerified = false, installed = false };
                string json = JsonConvert.SerializeObject(receipt, Formatting.Indented);
                File.WriteAllText(Path.Combine(staged, "build.json"), json); File.WriteAllText(Path.Combine(staged, "source/manifest.json"), json);
                if (!File.ReadAllBytes(SafePath(source, "source/manifest.json")).AsSpan().SequenceEqual(baselineBytes) ||
                    !File.ReadAllBytes(SafePath(source, "effect.json")).AsSpan().SequenceEqual(effectBytes)) throw new IOException("Effect source changed during build");
            });
        }
        finally { foreach (var reader in opened) reader.Dispose(); gate.ReleaseMutex(); }
    }

    internal static void ValidateEdit(JToken before, TypeTreeDocument document, SerializedFile owner, IReadOnlyDictionary<string, SerializedFile> files,
        IReadOnlyDictionary<string, int> authoredTypes = null)
    {
        var after = document.Root.ToJson();
        foreach (string field in new[] { "m_GameObject", "m_Script", "m_Father", "m_Children", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "m_ObjectHideFlags" })
            if (!JToken.DeepEquals(before[field], after[field])) throw new InvalidDataException("Editing ownership/topology is not enabled: " + field);
        foreach (var row in document.Root.Walk())
        {
            var node = row.Value;
            if (node.Schema.Type == "ManagedReferencesRegistry")
            {
                // Existing dynamic values are editable; type/rid registry topology is retained.
                var original = before.SelectToken(row.Path, false);
                var current = node.ToJson();
                var oldIds = original?["RefIds"] as JArray; var ids = current["RefIds"] as JArray;
                if (oldIds == null || ids == null || oldIds.Count != ids.Count || oldIds.Zip(ids).Any(p => !JToken.DeepEquals(p.First["rid"], p.Second["rid"]) || !JToken.DeepEquals(p.First["type"], p.Second["type"])))
                    throw new InvalidDataException("Managed type registry topology cannot be changed");
            }
            if (node.Fields.TryGetValue("m_Bursts", out var bursts) && node.Fields.TryGetValue("m_BurstCount", out var count) && Convert.ToInt32(count.Scalar) != bursts.Array.Items.Count)
                throw new InvalidDataException("Burst count does not match entries");
            if (node.Fields.TryGetValue("m_Curve", out var curve) &&
                (curve.Schema.Type == "Array" || curve.Fields.Count == 1 && curve.Fields.TryGetValue("Array", out _)))
            {
                double time = double.NegativeInfinity;
                foreach (var key in curve.Array.Items)
                { double next = Convert.ToDouble(key["time"].Scalar); if (!double.IsFinite(next) || next < time) throw new InvalidDataException("Curve times must be finite and sorted"); time = next; }
            }
            if (!node.Schema.Type.StartsWith("PPtr<", StringComparison.Ordinal)) continue;
            // Keep an original unresolved pointer if unchanged; a changed pointer must resolve exactly.
            if (JToken.DeepEquals(before.SelectToken(row.Path, false), node.ToJson())) continue;
            int fid = Convert.ToInt32(node["m_FileID"].Scalar); long pid = Convert.ToInt64(node["m_PathID"].Scalar);
            if (pid == 0 && fid == 0) continue;
            string cab = fid == 0 ? owner.fileName : fid > 0 && fid <= owner.m_Externals.Count ? owner.m_Externals[fid-1].fileName : null;
            var target = cab != null && files.TryGetValue(cab, out var file) ? file.m_Objects.SingleOrDefault(o => o.m_PathID == pid) : null;
            int targetClass = target?.classID ?? 0;
            if (target == null && (cab == null || authoredTypes == null || !authoredTypes.TryGetValue(cab + ":" + pid, out targetClass)))
                throw new InvalidDataException("Edited PPtr is unresolved: " + row.Path);
            string type = node.Schema.Type[5..^1];
            bool textureBase = type == "Texture" && targetClass is (28 or 89 or 117 or 187 or 188);
            bool componentBase = type == "Component" && targetClass is (4 or 114 or 198 or 199);
            if (type != "Object" && type != ((ClassIDType)targetClass).ToString() && !textureBase && !componentBase)
                throw new InvalidDataException("Edited PPtr target type mismatch: " + row.Path);
        }
    }

    private static byte[] ReadSnapshot(string source, JToken snapshot)
    {
        byte[] bytes = File.ReadAllBytes(SafePath(source, (string)snapshot["file"]));
        if (bytes.LongLength != (long)snapshot["size"] || !string.Equals(Hash(bytes), (string)snapshot["sha256"], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Effect snapshot hash mismatch");
        return bytes;
    }

    internal static byte[] Rebuild(SerializedFile file, byte[] original, IReadOnlyDictionary<long, byte[]> replacements)
    {
        if (replacements.Count == 0) return original;
        if ((int)file.header.m_Version != 22 || file.ObjectTableEnd - file.ObjectTableStart != file.m_Objects.Count * 24L ||
            replacements.Keys.Any(id => !file.m_Objects.Any(o => o.m_PathID == id))) throw new InvalidDataException("Unsupported native object table");
        using var output = new MemoryStream(); output.Write(original, 0, checked((int)file.header.m_DataOffset));
        var offsets = new Dictionary<long, (long Offset, int Size)>();
        foreach (var row in file.m_Objects.OrderBy(o => o.byteStart))
        {
            while (output.Position % 8 != 0) output.WriteByte(0);
            long offset = output.Position - file.header.m_DataOffset;
            if (replacements.TryGetValue(row.m_PathID, out var payload)) { output.Write(payload); offsets.Add(row.m_PathID, (offset, payload.Length)); }
            else { output.Write(original, checked((int)row.byteStart), checked((int)row.byteSize)); offsets.Add(row.m_PathID, (offset, checked((int)row.byteSize))); }
        }
        byte[] result = output.ToArray(); BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(24), result.Length);
        for (int i=0; i<file.m_Objects.Count; i++)
        {
            var range = offsets[file.m_Objects[i].m_PathID]; int position = checked((int)file.ObjectTableStart + i*24);
            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(position+8), range.Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(position+16), checked((uint)range.Size));
        }
        return result;
    }

    internal static byte[] UnityFs(IReadOnlyList<(string Name, byte[] Bytes)> members, string revision)
    {
        using var data = new MemoryStream(); foreach (var member in members) data.Write(member.Bytes);
        byte[] payload = data.ToArray(); using var info = new MemoryStream(); using var iw = new BinaryWriter(info, Encoding.UTF8, true);
        void U32(Stream s, uint n) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b,n); s.Write(b); }
        void I64(Stream s, long n) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b,n); s.Write(b); }
        void Text(Stream s, string value) { s.Write(Encoding.UTF8.GetBytes(value)); s.WriteByte(0); }
        info.Write(MD5.HashData(payload)); U32(info,1); U32(info,(uint)payload.Length); U32(info,(uint)payload.Length); info.Write(new byte[2]); U32(info,(uint)members.Count);
        long offset=0;
        foreach (var member in members)
        {
            if (member.Name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0) throw new InvalidDataException("Invalid native bundle member");
            I64(info,offset); I64(info,member.Bytes.Length); U32(info,member.Name.StartsWith("CAB-",StringComparison.OrdinalIgnoreCase) && !member.Name.EndsWith(".resS",StringComparison.OrdinalIgnoreCase) ? 4u : 0u); Text(info,member.Name); offset += member.Bytes.Length;
        }
        using var output = new MemoryStream(); Text(output,"UnityFS"); U32(output,7); Text(output,"2021.x.x"); Text(output,revision);
        long sizeOffset=output.Position; I64(output,0); U32(output,(uint)info.Length); U32(output,(uint)info.Length); U32(output,0x40);
        while (output.Position%16 != 0) output.WriteByte(0); info.Position=0; info.CopyTo(output); output.Write(payload);
        byte[] result=output.ToArray(); BinaryPrimitives.WriteInt64BigEndian(result.AsSpan((int)sizeOffset),result.Length); return result;
    }
}
