using AnimeStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.GUI;

/// <summary>Offline debug-native inputs. The DLL does not parse TypeTrees or
/// construct effects. Unmodified terminal dependencies retain game ownership.</summary>
internal static class EiemEffectRuntimeWriter
{
    internal static void Write(string staged, string source, JObject baseline, JObject effect,
        IReadOnlyDictionary<string, (SerializedFile File, byte[] Bytes, JToken Row)> files,
        IReadOnlyDictionary<string, byte[]> products, bool allLods = false)
    {
        string rootCab = ((string)effect["root"]).Split(':')[0];
        var fileByCab = files.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        string rootBundle = (string)fileByCab[rootCab].Row["sourceBundle"];
        var groups = files.GroupBy(p => (string)p.Value.Row["sourceBundle"]).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
        var groupIds = groups.Select((g,i) => (g.Key,i)).ToDictionary(p => p.Key,p => p.i,StringComparer.OrdinalIgnoreCase);
        var cabGroups = files.ToDictionary(p => p.Key,p => groupIds[(string)p.Value.Row["sourceBundle"]],StringComparer.OrdinalIgnoreCase);
        var dependencies = groups.Select(g => g.SelectMany(p => p.Value.File.m_Externals)
            .Where(e => cabGroups.ContainsKey(e.fileName)).Select(e => cabGroups[e.fileName]).Where(i => i != groupIds[g.Key]).ToHashSet()).ToArray();
        var selected = products.Keys.Select(c => cabGroups[c]).ToHashSet(); selected.Add(groupIds[rootBundle]);
        var hierarchyOwners = effect["hierarchy"].Select(n => (string)n["identity"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lodSettings = effect["objects"].Where(o => (int)o["classId"] == 114 &&
            o["script"] is JObject script && (string)script["class"] == "EffectSetting" && hierarchyOwners.Contains((string)o["owner"]))
            .Select(o => (string)o["identity"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allLods) foreach (var identity in lodSettings) selected.Add(cabGroups[identity[..identity.LastIndexOf(':')]]);
        // Authored source variants may bring a different native shader. Pin
        // only declared, snapshotted dependencies reachable from this root;
        // otherwise the original Prefab lease cannot supply the new shader.
        if (baseline["runtimeEmbeddedBundles"] is JArray embedded)
        {
            if (embedded.Count > 128) throw new InvalidDataException("Too many embedded effect dependencies");
            var reachable = new HashSet<int>();
            void Visit(int id) { if (reachable.Add(id)) foreach (int edge in dependencies[id]) Visit(edge); }
            Visit(groupIds[rootBundle]);
            foreach (var item in embedded)
            {
                if (item.Type != JTokenType.String || !groupIds.TryGetValue((string)item, out int id) || !reachable.Contains(id))
                    throw new InvalidDataException("Embedded effect bundle is not a snapshotted root dependency");
                selected.Add(id);
            }
        }
        // Retain consumers of an isolated dependency; original terminal shaders
        // and textures are supplied by the game's original resource lease.
        bool more;
        do { more=false; for(int i=0;i<groups.Length;i++) if (!selected.Contains(i) && dependencies[i].Overlaps(selected)) { selected.Add(i); more=true; } } while(more);
        string key = EiemEffectBuildCommand.Hash(Encoding.UTF8.GetBytes("inactive-root-endfield-envelope-v1\n" + (allLods ? "all-lods-v1\n" : "") + (string)effect["prefab"] + "\n" +
            (allLods ? "particle-always-simulate-v1\n" : "") + EiemEffectBuildCommand.Hash(File.ReadAllBytes(EiemEffectBuildCommand.SafePath(source,"source/manifest.json"))) + "\n" +
            string.Join("\n",products.OrderBy(p => p.Key,StringComparer.Ordinal).Select(p => p.Key+":"+EiemEffectBuildCommand.Hash(p.Value)))));
        var names = new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(int i in selected) foreach(var file in groups[i])
        {
            string token = EiemEffectBuildCommand.Hash(Encoding.UTF8.GetBytes("EFF_EFFECT_DEBUG/"+key+"/"+file.Key)).ToLowerInvariant();
            names.Add(file.Key,"CAB-"+token[..32]); names.Add(file.Key+".resS",names[file.Key]+".resS");
        }
        string Remap(string value)
        {
            if (value == null) return null;
            // Longest first: an archive path may contain both CAB and CAB.resS.
            foreach(var pair in names.OrderByDescending(p => p.Key.Length)) value=value.Replace(pair.Key,pair.Value,StringComparison.Ordinal);
            return value;
        }
        Directory.CreateDirectory(Path.Combine(staged,"runtime/bundles"));
        var packets = new List<(int Group,string File,string Hash)>();
        foreach(int i in selected.Order())
        {
            var members = new List<(string Name,byte[] Bytes)>();
            foreach(var file in groups[i])
            {
                var bytes = (products.GetValueOrDefault(file.Key) ?? file.Value.Bytes).ToArray();
                using var reader=new FileReader(file.Key,new MemoryStream(bytes,false));
                var native=new SerializedFile(reader,file.Value.File.assetsManager);
                void Patch(long position,string before,string after)
                {
                    if(before==after)return;
                    byte[] a=Encoding.UTF8.GetBytes(before),b=Encoding.UTF8.GetBytes(after);
                    if(a.Length!=b.Length || position<0 || position>bytes.Length-a.Length || !bytes.AsSpan((int)position,a.Length).SequenceEqual(a))
                        throw new InvalidDataException("Effect runtime namespace patch mismatch");
                    b.CopyTo(bytes,(int)position);
                }
                foreach(var site in native.ExternalPathSites)Patch(site.Offset,site.Path,Remap(site.Path));
                foreach(var row in native.m_Objects)
                {
                    var raw=bytes.AsSpan((int)row.byteStart,(int)row.byteSize).ToArray();
                    TypeTreeDocument doc;
                    try { doc=new TypeTreeDocument(row.serializedType.m_Type,raw,native.m_RefTypes); }
                    catch(Exception error) when(error is InvalidDataException or OverflowException or DecoderFallbackException)
                    {
                        if(names.Keys.Any(name => raw.AsSpan().IndexOf(Encoding.UTF8.GetBytes(name))>=0))
                            throw new InvalidDataException("Opaque effect object contains an unverified native namespace: "+file.Key+":"+row.m_PathID,error);
                        continue;
                    }
                    // Only EffectSettings owned by this authored hierarchy.
                    // Do not alter a dependency prefab's quality settings or
                    // write this export policy back into the editable source.
                    bool ownedParticle = row.classID == 198 && effect["objects"].Any(o =>
                        string.Equals((string)o["identity"], file.Key + ":" + row.m_PathID, StringComparison.OrdinalIgnoreCase) &&
                        hierarchyOwners.Contains((string)o["owner"]));
                    if (allLods && ((row.classID == 114 && lodSettings.Contains(file.Key + ":" + row.m_PathID)) || ownedParticle))
                    {
                        var data = (JObject)doc.Root.ToJson();
                        if (ownedParticle) EiemEffectLodPolicy.ApplyParticle(data); else EiemEffectLodPolicy.Apply(data); doc.ApplyJson(data);
                        var patched = doc.Encode();
                        if (patched.Length != raw.Length) throw new InvalidDataException("Effect LOD policy changed native object extent");
                        patched.CopyTo(bytes, checked((int)row.byteStart));
                    }
                    foreach(var node in doc.Root.Walk().Where(n => n.Value.Schema.Type=="string"))
                    {
                        string before=(string)node.Value.Scalar,after=Remap(before);
                        if(row.classID == 142 &&
                            (node.Value.Schema.Name is "m_Name" or "m_AssetBundleName") &&
                            !string.IsNullOrEmpty(before))
                        {
                            string token=EiemEffectBuildCommand.Hash(Encoding.UTF8.GetBytes("EFF_EFFECT_BUNDLE/"+key+"/"+before)).ToLowerInvariant();
                            after=string.Concat(Enumerable.Repeat(token,(before.Length+63)/64))[..before.Length];
                        }
                        Patch(row.byteStart+node.Value.Offset+4,before,after);
                    }
                    if (string.Equals(file.Key + ":" + row.m_PathID, (string)effect["root"], StringComparison.OrdinalIgnoreCase))
                    {
                        var active = doc.Root["m_IsActive"];
                        if (row.classID != 1 || active.Length != 1) throw new InvalidDataException("Runtime root active field contract differs");
                        bytes[checked((int)row.byteStart + active.Offset)] = 0;
                    }
                }
                using (var checkReader = new FileReader(names[file.Key], new MemoryStream(bytes, false)))
                {
                    var check = new SerializedFile(checkReader, native.assetsManager);
                    if (!check.m_Objects.Select(o => (o.m_PathID, o.byteStart, o.byteSize)).SequenceEqual(native.m_Objects.Select(o => (o.m_PathID, o.byteStart, o.byteSize))))
                        throw new InvalidDataException("Runtime namespace changed native object extents");
                    foreach (var external in check.m_Externals)
                        if (names.ContainsKey(external.fileName)) throw new InvalidDataException("Runtime external namespace was not isolated");
                    foreach (var row in check.m_Objects)
                    {
                        var raw = bytes.AsSpan((int)row.byteStart, (int)row.byteSize).ToArray();
                        if (names.Keys.Any(name => raw.AsSpan().IndexOf(Encoding.UTF8.GetBytes(name)) >= 0))
                            throw new InvalidDataException("Runtime object retains original isolated CAB name");
                    }
                }
                members.Add((names[file.Key],bytes));
            }
            foreach(var stream in baseline["rawStreams"].Where(s => groups[i].Any(f => string.Equals(f.Key,Path.GetFileNameWithoutExtension((string)s["member"]),StringComparison.OrdinalIgnoreCase))))
            {
                string name=(string)stream["member"]; byte[] bytes=File.ReadAllBytes(EiemEffectBuildCommand.SafePath(source,(string)stream["snapshot"]["file"]));
                if(!string.Equals(EiemEffectBuildCommand.Hash(bytes),(string)stream["snapshot"]["sha256"],StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Runtime stream hash mismatch");
                members.Add((Remap(name),bytes));
            }
            byte[] bundle=EiemEffectNativeArchive.Write(members);
            using (var checkReader = new FileReader("effect-debug.ab", new MemoryStream(bundle, false)))
            {
                var check = new VFSFile(checkReader, "effect-native.ab", GameType.ArknightsEndfield);
                if (check.fileList.Count != members.Count) throw new InvalidDataException("Runtime bundle member count differs");
                foreach (var member in check.fileList)
                {
                    using var data = new MemoryStream(); member.stream.CopyTo(data); member.stream.Dispose();
                    if (!data.ToArray().AsSpan().SequenceEqual(members.Single(m => m.Name == member.fileName).Bytes))
                        throw new InvalidDataException("Runtime bundle member roundtrip differs");
                }
            }
            string relative="runtime/bundles/"+EiemEffectBuildCommand.Hash(Encoding.UTF8.GetBytes(groups[i].Key)).ToLowerInvariant()+".ab";
            File.WriteAllBytes(Path.Combine(staged,relative),bundle);packets.Add((i,relative,EiemEffectBuildCommand.Hash(bundle)));
        }
        using var output=File.Create(Path.Combine(staged,"runtime.bin"));using var writer=new BinaryWriter(output,Encoding.UTF8);
        void N(ulong value)=>writer.Write(value);
        void S(string value){byte[] bytes=Encoding.UTF8.GetBytes(value);N((ulong)bytes.Length);writer.Write(bytes);}
        S("EFF_EFFECT_DEBUG_NATIVE/inactive-root+le64");S(key);S((string)effect["prefab"]);
        S((string)effect["hierarchy"].First(n => (string)n["identity"]==(string)effect["root"])["name"]);
        N((ulong)packets.FindIndex(p => p.Group==groupIds[rootBundle]));
        var owners = effect["hierarchy"].Select(n => (string)n["identity"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        N((ulong)effect["objects"].Count(o => (int)o["classId"]==198 && owners.Contains((string)o["owner"])));
        N((ulong)packets.Count);
        foreach(var packet in packets)
        {
            S(packet.File);S(packet.Hash);var edges=dependencies[packet.Group].Where(selected.Contains).Select(g => packets.FindIndex(p => p.Group==g)).Order().ToArray();
            N((ulong)edges.Length);foreach(int edge in edges)N((ulong)edge);
        }
    }
}
