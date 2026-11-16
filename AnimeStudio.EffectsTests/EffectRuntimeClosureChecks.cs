using AnimeStudio;
using AnimeStudio.GUI;
using Newtonsoft.Json.Linq;
using System.Buffers.Binary;
using System.Text;

internal static class EffectRuntimeClosureChecks
{
    private sealed record Native(SerializedFile File, byte[] Bytes, Dictionary<long, JObject> Objects);
    private sealed record Manifest(string Key, string RootName, List<byte[]> Bundles, JArray Game, JArray Author);
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); }

    internal static void Run(string source, string input, string output)
    {
        var edits = JObject.Parse(File.ReadAllText(input));
        var catalog = JObject.Parse(File.ReadAllText(Path.Combine(source, "effect.json")));
        var types = catalog["objects"].ToDictionary(o => (string)o["identity"], o => (int)o["classId"], StringComparer.OrdinalIgnoreCase);
        EiemEffectBuildCommand.Build(source, input, output);
        int count = Verify(source, edits, output);

        // Bind an actual Mesh renderer to public Resource IDs. Its original
        // authored payloads must leave the effect bundle, and every native slot
        // must address those resources through the one relocation contract.
        var renderer = edits["emitters"]?["added"]?.FirstOrDefault(r => types[(string)r["template"]] == 199 && (long?)r["data"]?["m_Mesh"]?["m_PathID"] != 0);
        if (renderer != null)
        {
            var copy = (JObject)edits.DeepClone();
            copy["authorReferences"] = new JArray(
                new JObject { ["owner"] = renderer["identity"], ["field"] = "$.m_Mesh", ["resource"] = "SwordRingMesh", ["type"] = "Mesh" },
                new JObject { ["owner"] = renderer["identity"], ["field"] = "$.m_Materials[0]", ["resource"] = "SwordFlowMaterial", ["type"] = "Material" });
            var mesh = (edits["assets"] as JArray)?.FirstOrDefault(a => types[(string)a["template"]] == 43);
            if (mesh != null)
            {
                var externalMesh = (JObject)mesh.DeepClone(); externalMesh["resource"] = "SwordRingMesh";
                copy["authorMeshes"] = new JArray(externalMesh);
            }
            string request = output + "-author.json";
            File.WriteAllText(request, copy.ToString());
            EiemEffectBuildCommand.Build(source, request, output + "-author");
            var manifest = ReadManifest(output + "-author");
            Check(manifest.Author.Count == 2 && manifest.Author.All(r => (int)r["pointerCount"] > 0), "public author resources have no actual native consumers");
            int authoredCount = Verify(source, copy, output + "-author");
            Check(authoredCount < count, "external author bindings retain their replaced embedded payloads");
            if (mesh != null) VerifyAuthorMeshes(output + "-author");
        }
        Console.WriteLine("PASS runtime Prefab closure: no detached emitters/objects, exact stream ranges, rebuilt preload/container, public author resource relocation; objects=" + count);
    }

    private static void VerifyAuthorMeshes(string output)
    {
        var receipt = JObject.Parse(File.ReadAllText(Path.Combine(output, "build.json")));
        var descriptor = receipt["authorMeshes"].Single();
        Check((string)descriptor["path"] == ((string)descriptor["path"]).ToLowerInvariant(),
            "author Mesh logical path must use the game's canonical lowercase form");
        string path = Path.Combine(output, (string)descriptor["file"]);
        var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.ArknightsEndfield) };
        using var input = new FileReader(path, new MemoryStream(File.ReadAllBytes(path), false));
        var archive = new VFSFile(input, path, GameType.ArknightsEndfield);
        Check(archive.fileList.Count == 1, "author Mesh must have exactly one native CAB member");
        var member = archive.fileList.Single();
        using var payload = new MemoryStream(); member.stream.CopyTo(payload); member.stream.Dispose(); byte[] bytes = payload.ToArray();
        using var reader = new FileReader(member.fileName, new MemoryStream(bytes, false));
        var file = new SerializedFile(reader, manager);
        Check(file.m_Externals.Count == 0 && file.m_Objects.Count == 2 && file.m_Objects.Count(o => o.classID == 43) == 1 &&
            file.m_Objects.Count(o => o.classID == 142) == 1 && (string)descriptor["cab"] == member.fileName,
            "author Mesh is incompatible with the ordinary native resource reader");
        JObject Data(ObjectInfo row) => (JObject)new TypeTreeDocument(row.serializedType.m_Type,
            bytes.AsSpan((int)row.byteStart, (int)row.byteSize).ToArray(), file.m_RefTypes).Root.ToJson();
        var mesh = file.m_Objects.Single(o => o.classID == 43); var bundle = Data(file.m_Objects.Single(o => o.classID == 142));
        Check(mesh.m_PathID == (long)descriptor["pathId"] && (string)Data(mesh)["m_Name"] == (string)descriptor["asset"] &&
            bundle["m_Container"].Count() == 1 && (string)bundle["m_Container"][0]["first"] == (string)descriptor["path"] &&
            (long)bundle["m_Container"][0]["second"]["asset"]["m_PathID"] == mesh.m_PathID,
            "author Mesh receipt does not describe its actual native container/asset");
    }

    private static Manifest ReadManifest(string output)
    {
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(output, "runtime.bin")));
        ulong N() => reader.ReadUInt64();
        string S() => Encoding.UTF8.GetString(reader.ReadBytes(checked((int)N())));
        Check(S() == "EFF_EFFECT_DEBUG_NATIVE/inactive-root+le64", "runtime manifest format differs");
        string key = S(); S(); string root = S(); N(); N(); int count = checked((int)N());
        var bundles = new List<byte[]>();
        for (int i = 0; i < count; i++)
        {
            string file = S(); S(); int n = checked((int)N()); for (int j = 0; j < n; j++) N();
            bundles.Add(File.ReadAllBytes(Path.Combine(output, file)));
        }
        var game = new JArray(); var author = new JArray();
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            string tag = S(); Check(tag is "EFF_GAME_RESOURCES/3" or "EFF_AUTHOR_RESOURCES/1", "unexpected runtime binding version");
            int resources = checked((int)N());
            for (int i = 0; i < resources; i++)
            {
                string name = S(), kind = S();
                if (tag == "EFF_GAME_RESOURCES/3") { S(); S(); S(); }
                string cab = S(); ulong id = N(); int n = checked((int)N()), pointers = 0;
                for (int j = 0; j < n; j++)
                {
                    int bundle = checked((int)N()), offset = checked((int)N()), patch = checked((int)N());
                    byte[] expected = patch == 0 ? Encoding.UTF8.GetBytes(cab) : BitConverter.GetBytes(id);
                    Check(bundles[bundle].AsSpan(offset, expected.Length).SequenceEqual(expected), "runtime binding has a stale patch offset");
                    if (patch == 1) pointers++;
                }
                (tag == "EFF_GAME_RESOURCES/3" ? game : author).Add(new JObject { ["name"] = name, ["type"] = kind,
                    ["cab"] = cab, ["id"] = id, ["pointerCount"] = pointers });
            }
        }
        return new(key, root, bundles, game, author);
    }

    private static int Verify(string source, JObject edits, string output)
    {
        var manifest = ReadManifest(output);
        var baseline = JObject.Parse(File.ReadAllText(Path.Combine(source, "source/manifest.json")));
        string Isolated(string cab)
        {
            string canonical = (string)baseline["serializedFiles"].Single(f => string.Equals((string)f["cab"], cab, StringComparison.OrdinalIgnoreCase))["cab"];
            return "CAB-" + EiemEffectBuildCommand.Hash(Encoding.UTF8.GetBytes("EFF_EFFECT_DEBUG/" + manifest.Key + "/" + canonical)).ToLowerInvariant()[..32];
        }
        var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.ArknightsEndfield) };
        var native = new Dictionary<string, Native>(StringComparer.OrdinalIgnoreCase);
        var streams = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var readers = new List<FileReader>();
        try
        {
            foreach (byte[] bundle in manifest.Bundles)
            {
                using var archiveReader = new FileReader("effect.ab", new MemoryStream(bundle, false));
                var archive = new VFSFile(archiveReader, "effect.ab", GameType.ArknightsEndfield);
                foreach (var member in archive.fileList)
                {
                    using var payload = new MemoryStream(); member.stream.CopyTo(payload); member.stream.Dispose();
                    byte[] bytes = payload.ToArray();
                    if (member.fileName.EndsWith(".resS")) { streams.Add(member.fileName, bytes); continue; }
                    var reader = new FileReader(member.fileName, new MemoryStream(bytes, false)); readers.Add(reader);
                    var file = new SerializedFile(reader, manager);
                    var objects = file.m_Objects.ToDictionary(o => o.m_PathID, o => (JObject)new TypeTreeDocument(o.serializedType.m_Type,
                        bytes.AsSpan((int)o.byteStart, (int)o.byteSize).ToArray(), file.m_RefTypes).Root.ToJson());
                    native.Add(member.fileName, new(file, bytes, objects));
                }
            }
            var seen = new HashSet<(string Cab, long Id)>(); var pending = new Queue<(string Cab, long Id)>();
            string root = (string)baseline["root"]; int split = root.LastIndexOf(':');
            Check(native.ContainsKey(Isolated(root[..split])), "runtime closure has no root native member");
            pending.Enqueue((Isolated(root[..split]), long.Parse(root[(split + 1)..])));
            (string Cab, long Id) Target(string cab, JToken ptr)
            {
                int fid = (int)ptr["m_FileID"]; long id = (long)ptr["m_PathID"];
                return (fid == 0 ? cab : native[cab].File.m_Externals[fid - 1].fileName, id);
            }
            var scripted = new HashSet<string>();
            while (pending.TryDequeue(out var current))
            {
                if (!native.TryGetValue(current.Cab, out var file) || current.Id == 0 || !seen.Add(current)) continue;
                Check(file.Objects.ContainsKey(current.Id), "retained PPtr targets a removed native object");
                foreach (var p in file.Objects[current.Id].DescendantsAndSelf().OfType<JObject>().Where(p => p["m_FileID"] != null && p["m_PathID"] != null))
                    pending.Enqueue(Target(current.Cab, p));
                if (scripted.Add(current.Cab))
                {
                    int at = (int)file.File.ObjectTableEnd;
                    int count = BinaryPrimitives.ReadInt32LittleEndian(file.Bytes.AsSpan(at)); at += 4;
                    for (int i = 0; i < count; i++)
                    {
                        int fid = BinaryPrimitives.ReadInt32LittleEndian(file.Bytes.AsSpan(at)); at = (at + 7) & ~3;
                        long id = BinaryPrimitives.ReadInt64LittleEndian(file.Bytes.AsSpan(at)); at += 8;
                        pending.Enqueue(Target(current.Cab, new JObject { ["m_FileID"] = fid, ["m_PathID"] = id }));
                    }
                }
            }
            int total = 0; var streamUse = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in native)
                foreach (var row in item.Value.File.m_Objects)
                {
                    var data = item.Value.Objects[row.m_PathID];
                    if (row.classID == 142)
                    {
                        foreach (var entry in data["m_PreloadTable"])
                        {
                            var target = Target(item.Key, entry);
                            Check(!native.ContainsKey(target.Cab) || target.Id == 0 || seen.Contains(target), "bundle preload retains an unreachable object");
                        }
                        foreach (var entry in data["m_Container"])
                        {
                            int start = (int)entry["second"]["preloadIndex"], size = (int)entry["second"]["preloadSize"];
                            Check(start >= 0 && size >= 0 && start + size <= data["m_PreloadTable"].Count(), "rebuilt container preload extent differs");
                        }
                        continue;
                    }
                    total++;
                    Check(seen.Contains((item.Key, row.m_PathID)), "runtime package retains an unreachable source object: " + item.Key + ":" + row.m_PathID + " / " + row.classID);
                    foreach (var stream in data.DescendantsAndSelf().OfType<JObject>().Where(s => s["path"]?.Type == JTokenType.String && s["offset"] != null && s["size"] != null && (long)s["size"] > 0))
                    {
                        string name = ((string)stream["path"]).Split('/').Last(); long offset = (long)stream["offset"], size = (long)stream["size"];
                        Check(streams.TryGetValue(name, out byte[] bytes) && offset >= 0 && size <= bytes.LongLength - offset, "pruned stream misses a reachable byte range");
                        streamUse[name] = Math.Max(streamUse.GetValueOrDefault(name), offset + size);
                    }
                }
            Check(streams.Count == streamUse.Count && streams.All(s => streamUse[s.Key] == s.Value.LongLength), "runtime retains unused source streams or unused stream suffix bytes");
            foreach (string removed in (edits["emitters"]?["removed"] as JArray ?? new JArray()).Values<string>())
            {
                int at = removed.LastIndexOf(':'); string cab = Isolated(removed[..at]); long id = long.Parse(removed[(at + 1)..]);
                Check(!native.TryGetValue(cab, out var file) || !file.Objects.ContainsKey(id), "runtime retains a deleted emitter tombstone");
            }
            return total;
        }
        finally { foreach (var reader in readers) reader.Dispose(); }
    }
}
