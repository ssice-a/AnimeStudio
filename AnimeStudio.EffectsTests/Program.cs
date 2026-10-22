using AnimeStudio;
using AnimeStudio.GUI;
using Newtonsoft.Json.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using AssetObject = AnimeStudio.Object;

Logger.Silent = true;
Progress.Silent = true;
if (args.Length >= 3 && args[0] is "--inspect-particles" or "--inspect-effect-components")
{
    var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.ArknightsEndfield) };
    var result = new JObject();
    foreach (string path in args.Skip(2))
    {
        using var input = new FileReader(path, new MemoryStream(File.ReadAllBytes(path), false));
        var archive = new VFSFile(input, path, GameType.ArknightsEndfield);
        foreach (var member in archive.fileList)
        {
            using var payload = new MemoryStream(); member.stream.CopyTo(payload); member.stream.Dispose();
            if (member.fileName.EndsWith(".resS")) continue;
            byte[] bytes = payload.ToArray();
            using var reader = new FileReader(member.fileName, new MemoryStream(bytes, false));
            var inspected = new SerializedFile(reader, manager);
            foreach (var row in inspected.m_Objects.Where(o => o.classID == 198 || args[0] == "--inspect-effect-components" && o.classID == 114))
                result[member.fileName + ":" + row.m_PathID] = new TypeTreeDocument(row.serializedType.m_Type,
                    bytes.AsSpan((int)row.byteStart, (int)row.byteSize).ToArray(), inspected.m_RefTypes).Root.ToJson();
        }
    }
    File.WriteAllText(args[1], result.ToString());
    Console.WriteLine("Inspected native particle systems: " + result.Count);
    return;
}
if (args.Length == 3 && args[0] == "--rewrap-native")
{
    using var input = new FileReader(args[1], new MemoryStream(File.ReadAllBytes(args[1]), false));
    var archive = new BundleFile(input, GameManager.GetGameByType(GameType.ArknightsEndfield));
    var members = new List<(string Name, byte[] Bytes)>();
    foreach (var member in archive.fileList)
    {
        using var payload = new MemoryStream(); member.stream.CopyTo(payload); member.stream.Dispose();
        members.Add((member.fileName, payload.ToArray()));
    }
    File.WriteAllBytes(args[2], EiemEffectNativeArchive.Write(members));
    Console.WriteLine("Native envelope written without changing member payloads: " + args[2]);
    return;
}
if (args.Length > 1 && args[0] == "--native-archives")
{
    foreach (string path in args.Skip(1))
    {
        using var input = new FileReader(path, new MemoryStream(File.ReadAllBytes(path), false));
        var archive = new VFSFile(input, path, GameType.ArknightsEndfield);
        if (archive.fileList.Count == 0) throw new Exception("Native effect archive is empty");
        foreach (var member in archive.fileList) member.stream.Dispose();
        Console.WriteLine("PASS Endfield native archive: " + path);
    }
    return;
}
int assertions = 0;
void Check(bool condition, string label) { assertions++; if (!condition) throw new Exception(label); }
void Reject(Action action, string label)
{
    assertions++;
    try { action(); } catch (InvalidDataException) { return; }
    throw new Exception("Expected rejection: " + label);
}
TypeTree Tree(params (string Type, string Name, int Level, bool Align)[] rows) =>
    new() { m_Nodes = rows.Select(r => new TypeTreeNode(r.Type, r.Name, r.Level, r.Align)).ToList() };
byte[] Raw(Action<BinaryWriter> action)
{
    using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
    action(writer); return stream.ToArray();
}

var simple = Tree(("Root", "Base", 0, false), ("float", "n", 1, false), ("bool", "enabled", 1, true),
    ("vector", "pointers", 1, false), ("Array", "Array", 2, true), ("int", "size", 3, false),
    ("PPtr<Object>", "data", 3, false), ("int", "m_FileID", 4, false), ("SInt64", "m_PathID", 4, false),
    ("string", "name", 1, false));
var raw = Raw(w => { w.Write(unchecked((int)0x80000000)); w.Write(true); w.Write(new byte[] { 7, 8, 9 });
    w.Write(1); w.Write(2); w.Write(-12345678901234567L); w.Write(1); w.Write((byte)'x'); w.Write(new byte[] { 4, 5, 6 }); });
var doc = new TypeTreeDocument(simple, raw);
Check(doc.Encode().SequenceEqual(raw), "nonzero padding and negative zero preserved");
Check(BitConverter.SingleToInt32Bits((float)doc.Root["n"].Scalar) == unchecked((int)0x80000000), "float identity");
var extra = doc.Root["pointers"].Array.Items[0].Clone(); extra["m_PathID"].Scalar = long.MaxValue;
doc.Root["pointers"].Array.Items.Add(extra); doc.Root["name"].Scalar = "longer UTF8 名称";
var edited = new TypeTreeDocument(simple, doc.Encode());
Check(edited.Root["pointers"].Array.Items.Count == 2 && (long)edited.Root["pointers"].Array.Items[1]["m_PathID"].Scalar == long.MaxValue, "array insertion and 64bit ID");
Check((long)edited.Root["pointers"].Array.Items[0]["m_PathID"].Scalar == -12345678901234567L, "clone independence");
Check((string)edited.Root["name"].Scalar == "longer UTF8 名称", "string growth and alignment");
var jsonView = JObject.Parse(doc.Root.ToJson().ToString());
doc.ApplyJson(jsonView);
Check(doc.Encode().SequenceEqual(edited.Encode()), "JSON import preserves original native representations");
var invalidJson = (JObject)jsonView.DeepClone(); invalidJson["extra"] = 1;
Reject(() => doc.ApplyJson(invalidJson), "unknown JSON field");
invalidJson = (JObject)jsonView.DeepClone(); invalidJson.Remove("n");
Reject(() => doc.ApplyJson(invalidJson), "missing JSON field");
invalidJson = (JObject)jsonView.DeepClone(); invalidJson["enabled"] = 1;
Reject(() => doc.ApplyJson(invalidJson), "bool type coercion");
invalidJson = (JObject)jsonView.DeepClone(); invalidJson["pointers"][0]["m_FileID"] = (long)int.MaxValue + 1;
Reject(() => doc.ApplyJson(invalidJson), "JSON integer overflow");
Check(doc.Encode().SequenceEqual(edited.Encode()), "invalid JSON leaves original document intact");
Reject(() => new TypeTreeDocument(simple, raw[..^1]), "truncation");
var badCount = raw.ToArray(); BitConverter.GetBytes(int.MaxValue).CopyTo(badCount, 8);
Reject(() => new TypeTreeDocument(simple, badCount), "array count bound");
Reject(() => new TypeTreeDocument(simple, raw.Concat(new byte[4]).ToArray()), "trailing bytes");
Reject(() => new TypeTreeDocument(Tree(("Unknown", "Base", 0, false)), Array.Empty<byte>()), "opaque leaf");
var nanTree = Tree(("float", "n", 0, false));
var nanBytes = BitConverter.GetBytes(unchecked((int)0x7fa00001));
Check(new TypeTreeDocument(nanTree, nanBytes).Encode().SequenceEqual(nanBytes), "NaN payload retained");
var byteTree = Tree(("vector", "bytes", 0, false), ("Array", "Array", 1, true), ("int", "size", 2, false), ("UInt8", "data", 2, false));
var byteDoc = new TypeTreeDocument(byteTree, new byte[] { 3,0,0,0, 1,2,3, 91 });
Check((string)byteDoc.Root.ToJson()["encoding"] == "base64", "byte arrays are tagged");
Check(byteDoc.Encode().Last() == 91, "byte array padding retained");

var registry = Tree(("ManagedReferencesRegistry", "references", 0, false), ("int", "version", 1, false),
    ("vector", "RefIds", 1, false), ("Array", "Array", 2, true), ("int", "size", 3, false),
    ("ReferencedObject", "data", 3, false), ("SInt64", "rid", 4, false),
    ("ReferencedManagedType", "type", 4, false), ("string", "class", 5, false), ("string", "ns", 5, false), ("string", "asm", 5, false),
    ("ReferencedObjectData", "data", 4, false));
void Text(BinaryWriter writer, string text)
{
    var bytes = System.Text.Encoding.UTF8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes);
    while (writer.BaseStream.Position % 4 != 0) writer.Write((byte)0);
}
var refType = new SerializedType { m_KlassName = "PlaySound", m_NameSpace = "Example", m_AsmName = "Game",
    m_Type = Tree(("PlaySound", "Base", 0, false), ("string", "soundName", 1, false), ("UInt8", "largeType", 1, true)) };
var managedRaw = Raw(w => { w.Write(2); w.Write(1); w.Write(123L); Text(w, "PlaySound"); Text(w, "Example"); Text(w, "Game"); Text(w, "sound01"); w.Write(new byte[] {1, 9, 8, 7}); });
var managed = new TypeTreeDocument(registry, managedRaw, new[] { refType });
Check((string)managed.Root["RefIds"].Array.Items[0]["data"].ToJson()["soundName"] == "sound01", "managed content decoded");
managed.Root["RefIds"].Array.Items[0]["data"].DynamicData["soundName"].Scalar = "sound02_longer";
Check((string)new TypeTreeDocument(registry, managed.Encode(), new[] { refType }).Root.ToJson()["RefIds"][0]["data"]["soundName"] == "sound02_longer", "managed variable length edit");
managed.Root["RefIds"].Array.Items[0]["type"]["class"].Scalar = "Other";
Reject(() => managed.Encode(), "managed descriptor mismatch");
Reject(() => new TypeTreeDocument(registry, managedRaw), "missing managed schema");
var badVersion = managedRaw.ToArray(); BitConverter.GetBytes(1).CopyTo(badVersion, 0);
Reject(() => new TypeTreeDocument(registry, badVersion, new[] { refType }), "unsupported managed registry");
var nullManaged = Raw(w => { w.Write(2); w.Write(1); w.Write(-2L); Text(w, ""); Text(w, ""); Text(w, ""); });
Check(new TypeTreeDocument(registry, nullManaged).Encode().SequenceEqual(nullManaged), "managed null marker");

T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
var file = Blank<SerializedFile>(); file.fileName = "CAB-test"; file.ObjectsDic = new(); file.m_Externals = new();
(GameObject, Transform) Node(long id, string name)
{
    var go = Blank<GameObject>(); var transform = Blank<Transform>();
    go.assetsFile = transform.assetsFile = file; go.m_PathID = id; transform.m_PathID = id + 1; go.m_Name = name;
    go.m_Transform = transform; transform.m_GameObject = new(0, id, file); transform.m_Father = new(0, 0, file); transform.m_Children = new();
    go.m_Components = new() { new(0, id + 1, file) }; file.ObjectsDic[id] = go; file.ObjectsDic[id + 1] = transform;
    return (go, transform);
}
var (root, rootTransform) = Node(1, "same-name"); var (child, childTransform) = Node(3, "same-name");
rootTransform.m_Children.Add(new(0, 4, file)); childTransform.m_Father = new(0, 2, file);
// ParticleSystem is deliberately a generic Object, not Component.
var particle = Blank<AssetObject>(); particle.assetsFile = file; particle.m_PathID = 5; particle.type = ClassIDType.ParticleSystem;
file.ObjectsDic[5] = particle; child.m_Components.Add(new(0, 5, file));
Check(EiemEffectSourceWriter.ValidateHierarchy(root).Count == 2, "pure particles and duplicate names accepted");
childTransform.m_Father = new(0, 0, file);
Reject(() => EiemEffectSourceWriter.ValidateHierarchy(root), "broken parent");
childTransform.m_Father = new(0, 2, file); childTransform.m_Children.Add(new(0, 2, file));
Reject(() => EiemEffectSourceWriter.ValidateHierarchy(root), "cycle"); childTransform.m_Children.Clear();
rootTransform.m_Children.Add(new(0, 999, file));
Reject(() => EiemEffectSourceWriter.ValidateHierarchy(root), "missing child"); rootTransform.m_Children.RemoveAt(1);
child.m_Components.Add(new(0, 999, file));
Reject(() => EiemEffectSourceWriter.ValidateHierarchy(root), "missing component");
Check(EndfieldEffectSourceCommand.PackagePath(Path.GetTempPath(), "assets/fx/test.prefab").EndsWith("test.efffx"), "effect path");
Reject(() => EndfieldEffectSourceCommand.PackagePath(Path.GetTempPath(), "../escape.prefab"), "path escape");

var temp = Path.Combine(Path.GetTempPath(), "EFF-EffectsTests-" + Guid.NewGuid().ToString("N"));
try
{
    EiemPackagePublication.Publish(temp, staged => { Directory.CreateDirectory(Path.Combine(staged, "source")); File.WriteAllText(Path.Combine(staged, "source/manifest.json"), "old"); });
    File.WriteAllText(Path.Combine(temp, "user.txt"), "keep");
    try { EiemPackagePublication.Publish(temp, staged => throw new InvalidDataException("injected failure")); } catch (InvalidDataException) { }
    Check(File.ReadAllText(Path.Combine(temp, "source/manifest.json")) == "old", "failure retains previous complete source");
}
finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }

TypeTree FixtureTree(JToken nodes) => new() { m_Nodes = nodes.Select(n => new TypeTreeNode((string)n["type"], (string)n["name"], (int)n["level"], false)
    { m_MetaFlag = (int)n["metaFlag"], m_ByteSize = (int)n["byteSize"], m_Version = (int)n["version"] }).ToList() };
if (args.Length == 3 && args[0] == "--lod-coverage")
{
    string source = Path.GetFullPath(args[1]), destination = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(destination);
    var catalog = JObject.Parse(File.ReadAllText(Path.Combine(source, "effect.json")));
    var ownerIds = catalog["hierarchy"].Select(n => (string)n["identity"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var settings = catalog["objects"].Where(o => o["script"] is JObject script && (string)script["class"] == "EffectSetting" && ownerIds.Contains((string)o["owner"])).ToArray();
    var particles = catalog["objects"].Where(o => (int)o["classId"] == 198 && ownerIds.Contains((string)o["owner"])).ToArray();
    Check(settings.Length > 0, "real source supplies owned EffectSetting");
    var payload = new JObject { ["format"]="EFF_EFFECT_EDIT", ["version"]=1, ["objects"]=new JArray(),
        ["sourceManifestSha256"]=EiemEffectBuildCommand.Hash(File.ReadAllBytes(Path.Combine(source,"source/manifest.json"))),
        ["sourceEffectSha256"]=EiemEffectBuildCommand.Hash(File.ReadAllBytes(Path.Combine(source,"effect.json"))) };
    string input = Path.Combine(destination,"edits.json");
    Dictionary<string,JObject> Runtime(string root)
    {
        var result = new Dictionary<string,JObject>();
        var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.ArknightsEndfield) };
        foreach (string path in Directory.GetFiles(Path.Combine(root,"runtime/bundles"),"*.ab"))
        {
            using var archiveReader = new FileReader(path, new MemoryStream(File.ReadAllBytes(path),false));
            var archive = new VFSFile(archiveReader,path,GameType.ArknightsEndfield);
            foreach (var member in archive.fileList)
            {
                using var data = new MemoryStream();member.stream.CopyTo(data);member.stream.Dispose();
                if (member.fileName.EndsWith(".resS"))continue;
                byte[] bytes = data.ToArray();using var reader = new FileReader(member.fileName,new MemoryStream(bytes,false));
                var file = new SerializedFile(reader,manager);
                foreach (var obj in settings.Concat(particles))
                {
                    var row = file.m_Objects.SingleOrDefault(r => r.m_PathID == (long)obj["native"]["pathId"] && r.classID == (int)obj["classId"]);
                    if (row != null)result[(string)obj["identity"]] = (JObject)new TypeTreeDocument(row.serializedType.m_Type,
                        bytes.AsSpan((int)row.byteStart,(int)row.byteSize).ToArray(),file.m_RefTypes).Root.ToJson();
                }
            }
        }return result;
    }
    foreach (string policy in new[]{"source","all"})
    {
        payload["lodCoverage"]=policy;File.WriteAllText(input,payload.ToString());
        string output = Path.Combine(destination,policy);EiemEffectBuildCommand.Build(source,input,output);
        var runtime = Runtime(output);Check(runtime.Count == settings.Length+particles.Length,"all owned settings and particles included in native runtime");
        foreach (var setting in settings)
        {
            var before = JObject.Parse(File.ReadAllText(Path.Combine(source,(string)setting["data"])));
            var expected = (JObject)before.DeepClone();if(policy == "all")EiemEffectLodPolicy.Apply(expected);
            // TypeTree returns Single values; source JSON parses as Double.
            // Compare their canonical JSON values after native-width rounding.
            Check(JToken.DeepEquals(JToken.Parse(runtime[(string)setting["identity"]].ToString()),JToken.Parse(expected.ToString())),"real native LOD policy changes exactly the intended fields");
            Check(JToken.DeepEquals(before,JObject.Parse(File.ReadAllText(Path.Combine(source,(string)setting["data"])))),"source remains editable and unchanged");
        }
        foreach(var particleSetting in particles)
        {
            var before = JObject.Parse(File.ReadAllText(Path.Combine(source,(string)particleSetting["data"])));
            var expected = (JObject)before.DeepClone();if(policy == "all")EiemEffectLodPolicy.ApplyParticle(expected);
            Check(JToken.DeepEquals(JToken.Parse(runtime[(string)particleSetting["identity"]].ToString()),JToken.Parse(expected.ToString())),"runtime particle culling policy preserves every other authored field");
            Check(JToken.DeepEquals(before,JObject.Parse(File.ReadAllText(Path.Combine(source,(string)particleSetting["data"])))),"particle source culling remains editable and unchanged");
        }
        byte[] first = File.ReadAllBytes(Path.Combine(output,"runtime.bin"));EiemEffectBuildCommand.Build(source,input,output);
        Check(File.ReadAllBytes(Path.Combine(output,"runtime.bin")).SequenceEqual(first),"LOD build deterministic");
    }
    Check(!File.ReadAllBytes(Path.Combine(destination,"all/runtime.bin")).SequenceEqual(File.ReadAllBytes(Path.Combine(destination,"source/runtime.bin"))),"LOD policy has an independent native generation");
    Console.WriteLine("PASS real native effect LOD coverage, source preservation, independent generations and deterministic rebuilds");
}
var lodFixture = JObject.Parse("{isUseDistanceLod:1,_isCullDisable:false,useGuardLod:true,dynamicChangeGuardLod:1,m_Enabled:0,lodSetting:[{gameobject:{m_FileID:0,m_PathID:123},settingLodLevel:8,platformLayer:7,targetLayer:3,_distanceLodInfos:[{isActive:0,speedPercent:0.5}]}],distanceLodCfgList:[{distance:200,framePercent:0.25}],unknown:42}");
EiemEffectLodPolicy.Apply(lodFixture);
Check((int)lodFixture["isUseDistanceLod"] == 0 && (bool)lodFixture["_isCullDisable"] && !(bool)lodFixture["useGuardLod"] && (int)lodFixture["dynamicChangeGuardLod"] == 0,"disable native effect degradation with exact bool/int types");
Check((int)lodFixture["lodSetting"][0]["settingLodLevel"] == 15 && (int)lodFixture["lodSetting"][0]["_distanceLodInfos"][0]["isActive"] == 1 && (double)lodFixture["lodSetting"][0]["_distanceLodInfos"][0]["speedPercent"] == 1,"all quality/distance levels remain active at authored simulation speed");
Check((int)lodFixture["m_Enabled"] == 0 && (int)lodFixture["unknown"] == 42 && (int)lodFixture["lodSetting"][0]["gameobject"]["m_PathID"] == 123 && (int)lodFixture["lodSetting"][0]["platformLayer"] == 7 && (int)lodFixture["lodSetting"][0]["targetLayer"] == 3 && (double)lodFixture["distanceLodCfgList"][0]["distance"] == 200,"LOD policy preserves enable state, references, layer masks and unrelated values");
Check(!EiemEffectLodPolicy.Read(new JObject()) && EiemEffectLodPolicy.Read(JObject.Parse("{lodCoverage:'all'}")),"old build requests retain source policy; explicit all LOD supported");
Reject(() => EiemEffectLodPolicy.Read(JObject.Parse("{lodCoverage:'typo'}")),"unknown LOD policy");
var particleLodFixture = JObject.Parse("{cullingMode:2,looping:true,simulationSpeed:0.75,unknown:42}");
EiemEffectLodPolicy.ApplyParticle(particleLodFixture);
Check((int)particleLodFixture["cullingMode"] == 3 && (bool)particleLodFixture["looping"] && (double)particleLodFixture["simulationSpeed"] == 0.75 && (int)particleLodFixture["unknown"] == 42,"all LOD policy prevents invisible UI particles from pausing before their first bounds exist");
if (args.Length >= 2 && args[0] == "--research")
{
    string research = Path.GetFullPath(args[1]);
    var samples = JObject.Parse(File.ReadAllText(Path.Combine(research, "sample-results.json")));
    var totals = new Dictionary<int, int>(); var particleDocs = new List<(TypeTree Tree, byte[] Raw, TypeTreeDocument Doc)>();
    bool soundVerified = false;
    foreach (var group in samples["results"])
    foreach (var obj in group["objects"].Where(o => new[] { 198, 199, 114 }.Contains((int)o["classId"])))
    {
        var fixture = JObject.Parse(File.ReadAllText(Path.Combine(research, (string)obj["key"] + ".json")));
        var tree = FixtureTree(fixture["schema"]);
        var bytes = File.ReadAllBytes(Path.Combine(research, (string)obj["key"] + ".raw"));
        var refTypes = group["referenceTypes"].Where(t => (string)t["cab"] == (string)obj["cab"]).Select(t => new SerializedType
            { m_KlassName = (string)t["name"], m_NameSpace = (string)t["ns"], m_AsmName = (string)t["assembly"], m_Type = FixtureTree(t["schema"]) }).ToArray();
        var decoded = new TypeTreeDocument(tree, bytes, refTypes);
        Check(decoded.Encode().SequenceEqual(bytes), "real object byte equality: " + obj["key"]);
        decoded.ApplyJson(JToken.Parse(decoded.Root.ToJson().ToString()));
        Check(decoded.Encode().SequenceEqual(bytes), "real JSON import byte equality: " + obj["key"]);
        int classId = (int)obj["classId"]; totals[classId] = totals.GetValueOrDefault(classId) + 1;
        if (classId == 198) particleDocs.Add((tree, bytes, decoded));
        if (((string)obj["key"]).Contains("-6227734248324571238"))
        {
            var sound = decoded.Root["references"]["RefIds"].Array.Items[0]["data"].DynamicData["soundName"];
            Check((string)sound.Scalar == "au_amb_emitter_damagefire_small_01", "real PlaySound content");
            sound.Scalar = "author_test_sound";
            Check((string)new TypeTreeDocument(tree, decoded.Encode(), refTypes).Root["references"]["RefIds"].Array.Items[0]["data"].DynamicData["soundName"].Scalar == "author_test_sound", "real managed data edit");
            soundVerified = true;
        }
    }
    Check(totals.GetValueOrDefault(198) == 28 && totals.GetValueOrDefault(199) == 28 && totals.GetValueOrDefault(114) == 60 && soundVerified, "all research fixtures exercised");
    void Edit(string label, string module, Func<TypeTreeDocument, bool> choose, Action<TypeTreeDocument> change, Func<TypeTreeDocument, bool> verify)
    {
        var row = particleDocs.First(p => choose(p.Doc));
        var candidate = new TypeTreeDocument(row.Tree, row.Raw);
        change(candidate); var encoded = candidate.Encode(); var after = new TypeTreeDocument(row.Tree, encoded);
        Check(verify(after), label);
        foreach (var field in row.Doc.Root.Fields.Where(p => p.Key != module))
        {
            var a = row.Raw.AsSpan(field.Value.Offset, field.Value.Length);
            var changed = after.Root[field.Key]; var b = encoded.AsSpan(changed.Offset, changed.Length);
            Check(a.SequenceEqual(b), label + " unrelated field unchanged: " + field.Key);
        }
    }
    Edit("enable disabled noise", "NoiseModule", d => !(bool)d.Root["NoiseModule"]["enabled"].Scalar,
        d => { d.Root["NoiseModule"]["enabled"].Scalar = true; d.Root["NoiseModule"]["strength"]["scalar"].Scalar = 0.2f; },
        d => (bool)d.Root["NoiseModule"]["enabled"].Scalar);
    Edit("append burst", "EmissionModule", d => d.Root["EmissionModule"]["m_Bursts"].Array.Items.Count > 0,
        d => { var list = d.Root["EmissionModule"]["m_Bursts"].Array; var added = list.Items[0].Clone(); added["time"].Scalar = 0.5f; list.Items.Add(added); d.Root["EmissionModule"]["m_BurstCount"].Scalar = list.Items.Count; },
        d => d.Root["EmissionModule"]["m_Bursts"].Array.Items.Count == Convert.ToInt32(d.Root["EmissionModule"]["m_BurstCount"].Scalar));
    Edit("append curve key", "SizeModule", d => d.Root["SizeModule"]["curve"]["maxCurve"]["m_Curve"].Array.Items.Count > 0,
        d => { var list = d.Root["SizeModule"]["curve"]["maxCurve"]["m_Curve"].Array; var added = list.Items[0].Clone(); added["time"].Scalar = 0.5f; list.Items.Add(added); },
        d => d.Root["SizeModule"]["curve"]["maxCurve"]["m_Curve"].Array.Items.Any(v => (float)v["time"].Scalar == 0.5f));
    Edit("link sub emitter", "SubModule", d => d.Root["SubModule"]["subEmitters"].Array.Items.Count > 0,
        d => { var module = d.Root["SubModule"]; module["enabled"].Scalar = true; var emitter = module["subEmitters"].Array.Items[0]["emitter"];
            emitter["m_FileID"].Scalar = 0; emitter["m_PathID"].Scalar = -76543210987654321L; },
        d => (bool)d.Root["SubModule"]["enabled"].Scalar && (long)d.Root["SubModule"]["subEmitters"].Array.Items[0]["emitter"]["m_PathID"].Scalar == -76543210987654321L);
    Console.WriteLine("Real fixtures: " + string.Join(", ", totals.Select(p => p.Key + "=" + p.Value)));
}
if (args.Length == 5 && args[0] == "--export-samples")
{
    var archive = EndfieldVfsArchive.Open(args[1]);
    var paths = File.ReadAllLines(args[3]).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
    if (!EndfieldVfsAssetIndexBuilder.TryGetOrRebindCurrentIndex(archive, args[2], out var indexPath)) throw new Exception("No current index");
    var prepared = EndfieldIndexStore.LoadPrefabExportIndexes(indexPath, paths);
    foreach (var path in paths)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string output = EndfieldEffectSourceCommand.PackagePath(args[4], path);
        EndfieldEffectSourceCommand.ExportOne(archive, args[2], path, output, preparedIndex: prepared[path]);
        var effect = JObject.Parse(File.ReadAllText(Path.Combine(output, "effect.json")));
        Check((string)effect["format"] == "EFF_EFFECT_SOURCE" && effect["hierarchy"].Any(), "effect graph exported");
        Check(effect["objects"].Any(o => (int)o["classId"] == 198 || (int)o["classId"] == 114), "effect components present");
        foreach (var particleRow in effect["objects"].Where(o => (int)o["classId"] == 198))
            Check((string)particleRow["state"] == "parsed" && particleRow["particleModules"].Count() == 23, "all particle modules exported");
        var manifest = JObject.Parse(File.ReadAllText(Path.Combine(output, "source/manifest.json")));
        foreach (var snapshot in manifest["serializedFiles"].Select(f => f["snapshot"]).Concat(manifest["rawStreams"].Select(f => f["snapshot"])))
        {
            using var stream = File.OpenRead(Path.Combine(output, (string)snapshot["file"]));
            Check(Convert.ToHexString(SHA256.HashData(stream)) == (string)snapshot["sha256"], "snapshot SHA256");
        }
        Console.WriteLine($"Exported {Path.GetFileName(path)}: {effect["hierarchy"].Count()} nodes, {effect["objects"].Count()} objects, {effect["warnings"].Count()} warnings, {watch.Elapsed.TotalSeconds:F2}s");
    }
}
if (args.Length == 3 && args[0] is "--build-sources" or "--build-source")
{
    string sourceRoot = Path.GetFullPath(args[1]), outputRoot = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(outputRoot);
    int builds = 0;
    foreach (string filePath in Directory.GetFiles(sourceRoot, "effect.json", SearchOption.AllDirectories))
    {
        if (filePath.Contains(".eff-backups")) continue;
        string source = Path.GetDirectoryName(filePath);
        var catalog = JObject.Parse(File.ReadAllText(filePath));
        var objectsToEdit = new JArray();
        var particleObject = catalog["objects"].FirstOrDefault(o => (int)o["classId"] == 198 && (string)o["state"] == "parsed");
        var managedObject = catalog["objects"].FirstOrDefault(o => (string)o["state"] == "parsed" && o["script"] is JObject script && (string)script["class"] == "EffectSetting" &&
            File.ReadAllText(Path.Combine(source,(string)o["data"])).Contains("au_amb_emitter_damagefire_small_01"));
        foreach (var obj in new[] { particleObject, managedObject }.Where(o => o != null))
        {
            byte[] sourceData = File.ReadAllBytes(Path.Combine(source,(string)obj["data"]));
            var value = JObject.Parse(System.Text.Encoding.UTF8.GetString(sourceData));
            if ((int)obj["classId"] == 198)
            {
                value["simulationSpeed"] = 1.25;
                value["NoiseModule"]["enabled"] = true;
                var emission = value["EmissionModule"]; var bursts = (JArray)emission["m_Bursts"];
                if (bursts.Count > 0) { bursts.Add(bursts[0].DeepClone()); emission["m_BurstCount"] = bursts.Count; }
            }
            else value["references"]["RefIds"][0]["data"]["soundName"] = "author_test_sound_changed_length";
            objectsToEdit.Add(new JObject { ["identity"] = obj["identity"], ["sourceDataSha256"] = EiemEffectBuildCommand.Hash(sourceData), ["data"] = value });
        }
        if (objectsToEdit.Count == 0) continue;
        var edits = new JObject { ["format"]="EFF_EFFECT_EDIT", ["version"]=1,
            ["sourceManifestSha256"]=EiemEffectBuildCommand.Hash(File.ReadAllBytes(Path.Combine(source,"source/manifest.json"))),
            ["sourceEffectSha256"]=EiemEffectBuildCommand.Hash(File.ReadAllBytes(filePath)), ["objects"]=objectsToEdit };
        string input = Path.Combine(outputRoot, Path.GetFileName(source)+".edits.json"), output = Path.Combine(outputRoot, Path.GetFileName(source)+".build");
        File.WriteAllText(input, edits.ToString());
        var clock = System.Diagnostics.Stopwatch.StartNew(); EiemEffectBuildCommand.Build(source,input,output);
        var receipt = JObject.Parse(File.ReadAllText(Path.Combine(output,"build.json")));
        Check((int)receipt["changedObjects"] == objectsToEdit.Count && receipt["bundles"].Any(), "native effect edits rebuilt");
        ValidateRuntime(output, catalog);
        var runtimeBefore = Directory.GetFiles(Path.Combine(output,"runtime"),"*",SearchOption.AllDirectories).Append(Path.Combine(output,"runtime.bin"))
            .ToDictionary(p => Path.GetRelativePath(output,p),File.ReadAllBytes);
        var originalReceipt = File.ReadAllBytes(Path.Combine(output,"build.json"));
        // A second build is deterministic and no dependent geometry needs author re-export.
        EiemEffectBuildCommand.Build(source,input,output);
        Check(File.ReadAllBytes(Path.Combine(output,"build.json")).SequenceEqual(originalReceipt), "deterministic effect build");
        Check(runtimeBefore.All(p => File.ReadAllBytes(Path.Combine(output,p.Key)).AsSpan().SequenceEqual(p.Value)), "deterministic isolated runtime bundles and manifest");
        if (particleObject != null)
        {
            var invalid = (JObject)edits.DeepClone(); var obj = invalid["objects"][0]["data"];
            obj["m_GameObject"]["m_PathID"] = 0;
            File.WriteAllText(input, invalid.ToString());
            Reject(() => EiemEffectBuildCommand.Build(source,input,output), "owner rewrite rejected");
            Check(File.ReadAllBytes(Path.Combine(output,"build.json")).SequenceEqual(originalReceipt), "bad edit retains complete build");
            invalid = (JObject)edits.DeepClone(); obj = invalid["objects"][0]["data"];
            obj["EmissionModule"]["m_BurstCount"] = 9999;
            File.WriteAllText(input, invalid.ToString());
            Reject(() => EiemEffectBuildCommand.Build(source,input,output), "inconsistent burst count rejected");
            var reference = ((JObject)obj).DescendantsAndSelf().OfType<JObject>().FirstOrDefault(o => o.Property("m_FileID") != null && !ReferenceEquals(o, obj["m_GameObject"]));
            if (reference != null)
            {
                obj["EmissionModule"]["m_BurstCount"] = obj["EmissionModule"]["m_Bursts"].Count();
                reference["m_PathID"] = long.MaxValue; reference["m_FileID"] = 0;
                File.WriteAllText(input, invalid.ToString());
                Reject(() => EiemEffectBuildCommand.Build(source,input,output), "unresolved edited PPtr rejected");
            }
        }
        var stale = (JObject)edits.DeepClone(); stale["sourceEffectSha256"] = new string('0',64); File.WriteAllText(input,stale.ToString());
        Reject(() => EiemEffectBuildCommand.Build(source,input,output), "changed source rejected");
        File.WriteAllText(input,edits.ToString());
        if (builds == 0)
        {
            var noChanges = (JObject)edits.DeepClone(); noChanges["objects"] = new JArray();
            string noChangesInput = Path.Combine(outputRoot,"unchanged.edits.json"), noChangesOutput = Path.Combine(outputRoot,"unchanged.build");
            File.WriteAllText(noChangesInput,noChanges.ToString()); EiemEffectBuildCommand.Build(source,noChangesInput,noChangesOutput);
            var empty = JObject.Parse(File.ReadAllText(Path.Combine(noChangesOutput,"build.json")));
            Check((int)empty["changedObjects"] == 0 && !empty["bundles"].Any(), "unchanged editor emits no replacement bundle");
            ValidateRuntime(noChangesOutput, catalog);
        }
        Console.WriteLine($"Native verified: {Path.GetFileName(source)}, changed={objectsToEdit.Count}, 2 builds and rejection checks {clock.Elapsed.TotalSeconds:F2}s");
        builds++;
    }
    Check(builds >= (args[0] == "--build-source" ? 1 : 8),"representative native builds exercised");
}
if (args.Length == 4 && args[0] == "--authored-assets")
{
    string source=Path.GetFullPath(args[1]), input=Path.GetFullPath(args[2]), output=Path.GetFullPath(args[3]);
    var catalog=JObject.Parse(File.ReadAllText(Path.Combine(source,"effect.json")));
    var author=JObject.Parse(File.ReadAllText(input));
    EiemEffectBuildCommand.Build(source,input,output);
    var receipt=JObject.Parse(File.ReadAllText(Path.Combine(output,"build.json")));
    Check((int)receipt["addedAssets"]==author["assets"].Count() && (int)receipt["addedAssets"]>=3,"authored native asset count");
    var manager=new AssetsManager {Game=GameManager.GetGameByType(GameType.ArknightsEndfield)};
    int meshes=0,textures=0,materials=0;
    var rawDocuments=new List<(SerializedFile File,TypeTreeDocument Document)>();
    var allIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach(var product in receipt["nativeFiles"])
    {
        byte[] bytes=File.ReadAllBytes(Path.Combine(output,(string)product["file"]));
        using var reader=new FileReader((string)product["cab"],new MemoryStream(bytes,false));
        var native=new SerializedFile(reader,manager);
        foreach(var nativeRow in native.m_Objects)
        {
            allIds.Add(native.fileName+":"+nativeRow.m_PathID);
            if(nativeRow.classID is 21 or 199)
                rawDocuments.Add((native,new TypeTreeDocument(nativeRow.serializedType.m_Type,
                    bytes.AsSpan((int)nativeRow.byteStart,(int)nativeRow.byteSize).ToArray(),native.m_RefTypes)));
        }
        foreach(var asset in author["assets"].Where(a=>((string)a["identity"]).StartsWith(((string)product["cab"])+":",StringComparison.OrdinalIgnoreCase)))
        {
            long id=long.Parse(((string)asset["identity"]).Split(':')[1]);
            var row=native.m_Objects.Single(r=>r.m_PathID==id);
            byte[] expected=Convert.FromBase64String((string)asset["payloadBase64"]);
            Check(bytes.AsSpan((int)row.byteStart,(int)row.byteSize).SequenceEqual(expected),"authored payload survives object table rebuild");
            var objectReader=new ObjectReader(reader,native,row,manager.Game);
            if(row.classID==43)
            {
                var mesh=new Mesh(objectReader);
                Check(mesh.m_VertexCount>4 && mesh.m_Indices.Count>6 && mesh.m_UV0.Length>0,"native Mesh reads custom geometry and UVs");
                Check(mesh.m_Vertices.All(float.IsFinite) && mesh.m_Normals.All(float.IsFinite),"native frames remain finite");meshes++;
            }
            else if(row.classID==28)
            {
                var texture=new Texture2D(objectReader);
                Check(texture.m_Width==4 && texture.m_Height==2 && texture.m_TextureFormat==TextureFormat.RGBA32,"native Texture2D reads author dimensions/format");
                using var bitmap=texture.ConvertToImage(true);
                Check(bitmap!=null,"native texture decoder accepts inline author RGBA");textures++;
            }
            else if(row.classID==21)
            {
                var material=new Material(objectReader);
                Check(material.m_SavedProperties.m_TexEnvs.Any(),"native Material reads texture slots");materials++;
            }
        }
    }
    Check(meshes==1 && textures==2 && materials==1,"separate Mesh and material/pixel clones decoded");
    var addedIds=author["assets"].Select(a=>(string)a["identity"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
    int meshBindings=0,pixelBindings=0;
    foreach(var item in rawDocuments)
        foreach(var reference in item.Document.Root.Walk().Where(r=>r.Value.Schema.Type.StartsWith("PPtr<")))
        {
            int fid=Convert.ToInt32(reference.Value["m_FileID"].Scalar);long pid=Convert.ToInt64(reference.Value["m_PathID"].Scalar);
            if(pid==0)continue;
            string cab=fid==0?item.File.fileName:item.File.m_Externals[fid-1].fileName;
            string identity=cab+":"+pid;
            if(!addedIds.Contains(identity))continue;
            Check(allIds.Contains(identity),"renderer/material authored dependency resolves in rebuilt native files");
            if(reference.Path.Contains("m_TexEnvs"))pixelBindings++;else if(reference.Path.EndsWith(".m_Mesh"))meshBindings++;
        }
    var meshPathIds=author["assets"].Where(a=>(int)catalog["objects"].First(o=>(string)o["identity"]==(string)a["template"])["classId"]==43)
        .Select(a=>long.Parse(((string)a["identity"]).Split(':')[1])).ToHashSet();
    int expectedMeshBindings=author["objects"].Count(o=>o["data"]?["m_Mesh"]?["m_PathID"] is JToken pid && meshPathIds.Contains((long)pid));
    Check(expectedMeshBindings>0 && meshBindings==expectedMeshBindings && pixelBindings==2,"all authored renderers point to their shared custom Mesh and cloned material uses both author images");
    ValidateRuntime(output,catalog);
    var files=Directory.GetFiles(output,"*",SearchOption.AllDirectories).Where(p=>!p.Contains(".eff-backups"))
        .ToDictionary(p=>Path.GetRelativePath(output,p),p=>File.ReadAllBytes(p));
    EiemEffectBuildCommand.Build(source,input,output);
    Check(files.All(p=>File.ReadAllBytes(Path.Combine(output,p.Key)).SequenceEqual(p.Value)),"authored native/runtime builds deterministic");
    string invalid=Path.Combine(Path.GetDirectoryName(input),"invalid-assets.effedit.json");
    var broken=(JObject)author.DeepClone();broken["assets"][0]["sourceObjectSha256"]=new string('0',64);File.WriteAllText(invalid,broken.ToString());
    Reject(()=>EiemEffectBuildCommand.Build(source,invalid,output),"stale authored template rejected");
    broken=(JObject)author.DeepClone();broken["assets"][0]["identity"]=broken["assets"][0]["template"];File.WriteAllText(invalid,broken.ToString());
    Reject(()=>EiemEffectBuildCommand.Build(source,invalid,output),"authored resource cannot overwrite source identity");
    broken=(JObject)author.DeepClone();((JArray)broken["assets"]).Add(broken["assets"][0].DeepClone());File.WriteAllText(invalid,broken.ToString());
    Reject(()=>EiemEffectBuildCommand.Build(source,invalid,output),"duplicate authored resource rejected");
    broken=(JObject)author.DeepClone();
    var textureAsset=broken["assets"].First(a=>(int)catalog["objects"].First(o=>(string)o["identity"]==(string)a["template"])["classId"]==28);
    var textureTemplate=catalog["objects"].First(o=>(string)o["identity"]==(string)textureAsset["template"]);
    string textureCab=(string)textureTemplate["native"]["cab"];
    var textureProduct=receipt["nativeFiles"].First(p=>((string)p["cab"]).Equals(textureCab,StringComparison.OrdinalIgnoreCase));
    byte[] textureNative=File.ReadAllBytes(Path.Combine(output,(string)textureProduct["file"]));
    using(var reader=new FileReader(textureCab,new MemoryStream(textureNative,false)))
    {
        var native=new SerializedFile(reader,manager);
        var row=native.m_Objects.First(r=>r.classID==28);
        var badTexture=new TypeTreeDocument(row.serializedType.m_Type,Convert.FromBase64String((string)textureAsset["payloadBase64"]),native.m_RefTypes);
        badTexture.Root["m_Width"].Scalar=1000;
        textureAsset["payloadBase64"]=Convert.ToBase64String(badTexture.Encode());
        File.WriteAllText(invalid,broken.ToString());
        Reject(()=>EiemEffectBuildCommand.Build(source,invalid,output),"authored texture dimension/pixel mismatch rejected");
    }
    Check(files.All(p=>File.ReadAllBytes(Path.Combine(output,p.Key)).SequenceEqual(p.Value)),"failed authored builds preserve published output");
    Console.WriteLine("PASS authored assets: native Mesh/Material/Texture decoding, runtime inputs, deterministic rebuild and rejected candidates");
}
if (args.Length == 4 && args[0] == "--emitters")
{
    string source=Path.GetFullPath(args[1]), input=Path.GetFullPath(args[2]), output=Path.GetFullPath(args[3]);
    var catalog=JObject.Parse(File.ReadAllText(Path.Combine(source,"effect.json")));
    var author=JObject.Parse(File.ReadAllText(input));
    var added=(JArray)author["emitters"]["added"];var deleted=(JArray)author["emitters"]["removed"];
    var specs=catalog["objects"].ToDictionary(o=>(string)o["identity"]);
    int Kind(JToken row)=>(int)specs[(string)row["template"]]["classId"];
    long Id(string identity)=>long.Parse(identity[(identity.LastIndexOf(':')+1)..]);
    string Target(string owner,JToken ptr)=>owner[..owner.LastIndexOf(':')]+":"+(long)ptr["m_PathID"];
    EiemEffectBuildCommand.Build(source,input,output);
    var receipt=JObject.Parse(File.ReadAllText(Path.Combine(output,"build.json")));
    Check((int)receipt["addedAssets"]==added.Count+(author["assets"]?.Count()??0),"complete native emitter/resource records added");
    var manager=new AssetsManager {Game=GameManager.GetGameByType(GameType.ArknightsEndfield)};
    var decoded=new Dictionary<string,JObject>(StringComparer.OrdinalIgnoreCase);
    foreach(var product in receipt["nativeFiles"])
    {
        byte[] bytes=File.ReadAllBytes(Path.Combine(output,(string)product["file"]));
        using var reader=new FileReader((string)product["cab"],new MemoryStream(bytes,false));
        var native=new SerializedFile(reader,manager);
        foreach(var row in native.m_Objects.Where(r=>r.classID is 1 or 4 or 114 or 198 or 199))
            decoded[native.fileName+":"+row.m_PathID]=(JObject)new TypeTreeDocument(row.serializedType.m_Type,
                bytes.AsSpan((int)row.byteStart,(int)row.byteSize).ToArray(),native.m_RefTypes).Root.ToJson();
    }
    var effective=(JObject)catalog.DeepClone();
    foreach(var node in ((JArray)effective["hierarchy"]).Where(n=>deleted.Any(d=>(string)d==(string)n["identity"])).ToArray())node.Remove();
    foreach(var row in added)
    {
        string id=(string)row["identity"];var data=decoded[id];int kind=Kind(row);
        if(kind==1)
        {
            Check((string)data["m_Name"]==(string)row["data"]["m_Name"],"new GameObject name decoded");
            Check(JToken.DeepEquals(data["m_Component"],row["data"]["m_Component"]),"new component membership decoded");
            ((JArray)effective["hierarchy"]).Add(new JObject{["identity"]=id});
        }
        else Check(JToken.DeepEquals(data["m_GameObject"],row["data"]["m_GameObject"]),"new component owner decoded");
        if(kind==4)
        {
            string parent=Target(id,data["m_Father"]);
            Check(decoded[parent]["m_Children"].Any(p=>(long)p["m_PathID"]==Id(id)),"new Transform attached to parent");
        }
        if(kind==198)
        {
            Check(Math.Abs((double)data["InitialModule"]["startRotation"]["scalar"]-(double)row["data"]["InitialModule"]["startRotation"]["scalar"])<1e-6,"degree controls exported native radians");
            Check(JToken.DeepEquals(data["EmissionModule"]["m_Bursts"],row["data"]["EmissionModule"]["m_Bursts"]),"new emitter bursts decoded");
            Check((bool)data["looping"]==(bool)row["data"]["looping"],"new emitter loop decoded");
            var spec=(JObject)specs[(string)row["template"]].DeepClone();spec["identity"]=id;spec["owner"]=Target(id,data["m_GameObject"]);
            ((JArray)effective["objects"]).Add(spec);
        }
        if(kind==199)
        {
            Check(JToken.DeepEquals(data["m_Mesh"],row["data"]["m_Mesh"]),"new renderer Mesh binding decoded");
            Check(JToken.DeepEquals(data["m_Materials"],row["data"]["m_Materials"]),"new renderer material bindings decoded");
        }
    }
    foreach(string id in deleted.Select(d=>(string)d))
    {
        var node=catalog["hierarchy"].Single(n=>(string)n["identity"]==id);string transform=(string)node["transform"];
        Check(!(bool)decoded[id]["m_IsActive"],"deleted emitter GameObject inactive");
        Check((long)decoded[transform]["m_Father"]["m_PathID"]==0,"deleted emitter detached");
        Check(!decoded[(string)node["parent"]]["m_Children"].Any(p=>(long)p["m_PathID"]==Id(transform)),"deleted emitter absent from parent children");
        foreach(string component in node["components"].Select(c=>(string)c))
        {
            int kind=(int)specs[component]["classId"];
            if(kind==198)Check(!(bool)decoded[component]["EmissionModule"]["enabled"],"deleted native particle emission disabled");
            if(kind is 114 or 199)Check(!Convert.ToBoolean(((JValue)decoded[component]["m_Enabled"]).Value),"deleted renderer/script disabled");
        }
    }
    foreach(var script in specs.Values.Where(o=>(int)o["classId"]==114 && (string)o["state"]=="parsed" && (string)o["script"]?["class"]=="EffectSetting"))
    {
        string id=(string)script["identity"];
        var sourceData=JObject.Parse(File.ReadAllText(Path.Combine(source,(string)script["data"])));
        if(sourceData["lodSetting"] is not JArray sourceLod)continue;
        var resultLod=(JArray)decoded[id]["lodSetting"];
        foreach(var row in added.Where(r=>Kind(r)==1 && sourceLod.Any(l=>Target(id,l["gameobject"])==(string)r["template"])))
        {
            var lod=resultLod.Single(l=>Target(id,l["gameobject"])==(string)row["identity"]);
            var ps=added.Single(r=>Kind(r)==198 && Target((string)r["identity"],r["data"]["m_GameObject"])==(string)row["identity"]);
            Check(Target(id,lod["particle"])==(string)ps["identity"],"game EffectSetting LOD registers new native particle");
        }
        Check(!resultLod.Any(l=>deleted.Any(d=>(string)d==Target(id,l["gameobject"]))),"game EffectSetting LOD cannot reactivate removed emitter");
    }
    ValidateRuntime(output,effective);
    var files=Directory.GetFiles(output,"*",SearchOption.AllDirectories).Where(p=>!p.Contains(".eff-backups"))
        .ToDictionary(p=>Path.GetRelativePath(output,p),File.ReadAllBytes);
    EiemEffectBuildCommand.Build(source,input,output);
    Check(files.All(p=>File.ReadAllBytes(Path.Combine(output,p.Key)).SequenceEqual(p.Value)),"emitter builds deterministic");
    string invalid=Path.Combine(Path.GetDirectoryName(input),"invalid-emitters.effedit.json");
    void Bad(Action<JObject> breakIt,string label)
    {
        var broken=(JObject)author.DeepClone();breakIt(broken);File.WriteAllText(invalid,broken.ToString());
        Reject(()=>EiemEffectBuildCommand.Build(source,invalid,output),label);
        Check(files.All(p=>File.ReadAllBytes(Path.Combine(output,p.Key)).SequenceEqual(p.Value)),"rejected topology preserves output: "+label);
    }
    JToken Added(JObject value,int kind)=>value["emitters"]["added"].First(r=>Kind(r)==kind);
    if(added.Any())
    {
        Bad(v=>Added(v,198)["data"]["m_GameObject"]["m_PathID"]=0L,"orphan particle component");
        Bad(v=>Added(v,4)["data"]["m_Father"]["m_PathID"]=Id((string)Added(v,4)["identity"]),"cyclic emitter parent");
        Bad(v=>Added(v,1)["identity"]=Added(v,1)["template"].DeepClone(),"source PathID collision");
        Bad(v=>((JArray)Added(v,1)["data"]["m_Component"]).RemoveAt(0),"incomplete component membership");
        Bad(v=>Added(v,4)["data"]["m_Children"]=new JArray(new JObject{["m_FileID"]=0,["m_PathID"]=1L}),"undeclared child membership");
    }
    Bad(v=>((JArray)v["emitters"]["removed"]).Add(catalog["root"].DeepClone()),"root emitter deletion");
    Console.WriteLine("PASS emitters: native component ownership, parent links, inactive removals, radians, render resources, runtime count, deterministic builds and rejected topology");
}
Console.WriteLine($"PASS: {assertions} assertions");

void ValidateRuntime(string directory, JObject source)
{
    using var manifest = new BinaryReader(File.OpenRead(Path.Combine(directory,"runtime.bin")));
    string S() { ulong n=manifest.ReadUInt64(); if(n>int.MaxValue)throw new Exception("runtime string extent");return System.Text.Encoding.UTF8.GetString(manifest.ReadBytes((int)n)); }
    Check(S()=="EFF_EFFECT_DEBUG_NATIVE/inactive-root+le64","runtime native inactive-root protocol");
    Check(S().Length==64 && S()==(string)source["prefab"],"runtime identity");string rootName=S();
    ulong rootBundle=manifest.ReadUInt64(), particles=manifest.ReadUInt64(), count=manifest.ReadUInt64();
    Check(rootBundle<count && count>0,"runtime root input");
    var owners=source["hierarchy"].Select(n=>(string)n["identity"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Check(particles==(ulong)source["objects"].Count(o=>(int)o["classId"]==198 && owners.Contains((string)o["owner"])),"runtime root hierarchy particle count");
    var manager=new AssetsManager {Game=GameManager.GetGameByType(GameType.ArknightsEndfield)};
    long rootId=long.Parse(((string)source["root"]).Split(':')[1]);bool found=false;
    for(ulong i=0;i<count;++i)
    {
        string relative=S(),hash=S();byte[] bytes=File.ReadAllBytes(Path.Combine(directory,relative));
        Check(EiemEffectBuildCommand.Hash(bytes)==hash,"runtime bundle SHA256");
        ulong n=manifest.ReadUInt64();for(ulong j=0;j<n;++j)Check(manifest.ReadUInt64()<count,"runtime dependency points to emitted input");
        using var reader=new FileReader(relative,new MemoryStream(bytes,false));var bundle=new VFSFile(reader,relative,GameType.ArknightsEndfield);
        foreach(var member in bundle.fileList)
        {
            using var data=new MemoryStream();member.stream.CopyTo(data);member.stream.Dispose();
            if(member.fileName.EndsWith(".resS"))continue;
            using var fileReader=new FileReader(member.fileName,new MemoryStream(data.ToArray(),false));var file=new SerializedFile(fileReader,manager);
            if(i!=rootBundle)continue;
            foreach(var row in file.m_Objects.Where(o=>o.classID==1 && o.m_PathID==rootId))
            {
                var doc=new TypeTreeDocument(row.serializedType.m_Type,data.ToArray().AsSpan((int)row.byteStart,(int)row.byteSize).ToArray(),file.m_RefTypes);
                Check((string)doc.Root["m_Name"].Scalar==rootName && !Convert.ToBoolean(doc.Root["m_IsActive"].Scalar),"offline runtime root inactive before Instantiate");found=true;
            }
        }
    }
    Check(found && manifest.BaseStream.Position==manifest.BaseStream.Length,"runtime root found and packet fully consumed");
}
