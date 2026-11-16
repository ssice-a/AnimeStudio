using AnimeStudio;
using AnimeStudio.GUI;
using Newtonsoft.Json.Linq;

internal static class EffectSelectionChecks
{
    internal static void Run(string source, string input, string output)
    {
        var edits = JObject.Parse(File.ReadAllText(input));
        var scope = edits["emitterScope"].Select(i => (string)i).ToHashSet(StringComparer.OrdinalIgnoreCase);
        void Check(bool value, string label) { if (!value) throw new Exception(label); }
        EiemEffectBuildCommand.Build(source, input, output);
        var receipt = JObject.Parse(File.ReadAllText(Path.Combine(output, "build.json")));
        var baseline = JObject.Parse(File.ReadAllText(Path.Combine(source, "source/manifest.json")));
        var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.ArknightsEndfield) };
        var documents = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        var classes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var externals = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in baseline["serializedFiles"])
        {
            string cab = (string)row["cab"];
            var product = receipt["nativeFiles"].SingleOrDefault(p => (string)p["cab"] == cab);
            byte[] bytes = File.ReadAllBytes(product != null ? Path.Combine(output, (string)product["file"]) : Path.Combine(source, (string)row["snapshot"]["file"]));
            using var reader = new FileReader(cab, new MemoryStream(bytes, false));
            var native = new SerializedFile(reader, manager);
            externals[cab] = native.m_Externals.Select(e => e.fileName).ToArray();
            foreach (var obj in native.m_Objects.Where(o => o.classID is 1 or 4 or 114 or 198 or 199))
            {
                string id = cab + ":" + obj.m_PathID;
                documents[id] = (JObject)new TypeTreeDocument(obj.serializedType.m_Type, bytes.AsSpan((int)obj.byteStart, (int)obj.byteSize).ToArray(), native.m_RefTypes).Root.ToJson();
                classes[id] = obj.classID;
            }
        }
        string Target(string owner, JToken ptr)
        {
            if ((long?)ptr?["m_PathID"] is null or 0) return "";
            string cab = owner[..owner.LastIndexOf(':')];int fid = (int)ptr["m_FileID"];
            return (fid == 0 ? cab : externals[cab][fid - 1]) + ":" + (long)ptr["m_PathID"];
        }
        var owners = new HashSet<string>();var seen = new HashSet<string>();
        void Walk(string go)
        {
            Check(seen.Add(go), "selection contains duplicate/cyclic GameObjects");
            foreach (var c in documents[go]["m_Component"])
            {
                string id = Target(go, c["component"]);
                if (classes[id] == 198) owners.Add(go);
                if (classes[id] == 4)
                    foreach (var child in documents[id]["m_Children"])
                    {
                        string transform = Target(id, child);
                        Walk(Target(transform, documents[transform]["m_GameObject"]));
                    }
            }
        }
        Walk((string)baseline["root"]);
        var particleOwners = scope.Where(id => documents[id]["m_Component"].Any(c => classes[Target(id, c["component"])] == 198)).ToHashSet();
        Check(particleOwners.Count > 0 && owners.SetEquals(particleOwners), "selected output contains extra/missing particle systems");
        foreach (string script in seen.SelectMany(go => documents[go]["m_Component"].Select(c => Target(go, c["component"]))).Where(id => classes[id] == 114))
            if (documents[script]["lodSetting"] is JArray rows)
                Check(rows.All(r => scope.Contains(Target(script, r["gameobject"]))), "EffectSetting can reactivate an excluded emitter");
        using (var manifest = new BinaryReader(File.OpenRead(Path.Combine(output, "runtime.bin"))))
        {
            string S() => System.Text.Encoding.UTF8.GetString(manifest.ReadBytes(checked((int)manifest.ReadUInt64())));
            S();S();S();S();manifest.ReadUInt64();
            Check(manifest.ReadUInt64() == (ulong)owners.Count, "runtime manifest particle count differs from native scene");
        }
        var hashes = Directory.GetFiles(output, "*", SearchOption.AllDirectories).Where(p => !p.Contains(".eff-backups")).ToDictionary(p => p, File.ReadAllBytes);
        EiemEffectBuildCommand.Build(source, input, output);
        Check(hashes.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)), "selection rebuild changed unchanged output");
        Console.WriteLine("PASS native emitter selection: exact particle owners, placement ancestors, filtered EffectSetting, runtime count and deterministic rebuild; particles=" + owners.Count);
    }
}
