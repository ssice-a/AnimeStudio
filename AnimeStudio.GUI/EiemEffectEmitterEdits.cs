using AnimeStudio;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeStudio.GUI;

/// <summary>Declared emitter instances; ownership is derived and checked here.</summary>
internal sealed class EiemEffectEmitterEdits
{
    private readonly JObject effect;
    private readonly IReadOnlyDictionary<string, JToken> objects;
    private readonly IReadOnlyDictionary<string, (SerializedFile File, byte[] Bytes, JToken Row)> files;
    private readonly List<(string Identity, string Template, JObject Data)> declarations = new();
    private readonly HashSet<string> removed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JToken> nodes;
    internal readonly List<EiemEffectAssetEdits.Asset> Assets = new();
    internal readonly Dictionary<string, int> Types = new(StringComparer.OrdinalIgnoreCase);

    internal EiemEffectEmitterEdits(JToken input, JObject effect, IReadOnlyDictionary<string, JToken> objects,
        IReadOnlyDictionary<string, (SerializedFile File, byte[] Bytes, JToken Row)> files)
    {
        this.effect = effect; this.objects = objects; this.files = files;
        nodes = effect["hierarchy"].ToDictionary(n => (string)n["identity"], StringComparer.OrdinalIgnoreCase);
        if (input == null) return;
        if (input["added"] is not JArray added || input["removed"] is not JArray deleted || added.Count > 4096 || deleted.Count > 1024)
            throw new InvalidDataException("Invalid emitter operations");
        foreach (var item in deleted)
        {
            string id = (string)item;
            if (id == null || !removed.Add(id) || !nodes.TryGetValue(id, out var node) || id.Equals((string)effect["root"], StringComparison.OrdinalIgnoreCase) ||
                !node["components"].Any(c => (int)objects[(string)c]["classId"] == 198) ||
                node["components"].Prepend(node["identity"]).Any(c => (string)objects[(string)c]["state"] != "parsed" || (int)objects[(string)c]["classId"] is not (1 or 4 or 114 or 198 or 199)))
                throw new InvalidDataException("Only parsed non-root emitter nodes may be removed");
        }
        var deletedTransforms = nodes.Values.Where(n => removed.Contains((string)n["identity"])).Select(n => (string)n["transform"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (nodes.Values.Any(n => removed.Contains((string)n["identity"]) && n["children"].Any(c => !deletedTransforms.Contains((string)c))))
            throw new InvalidDataException("Remove child emitters before their parent");
        foreach (var item in added)
        {
            string id = (string)item["identity"], template = (string)item["template"];
            if (id == null || template == null || !objects.TryGetValue(template, out var source) ||
                (string)source["state"] != "parsed" || objects.ContainsKey(id) || Types.ContainsKey(id) || item["data"] is not JObject data)
                throw new InvalidDataException("Invalid emitter identity/template/data");
            int kind = (int)source["classId"];
            if (kind is not (1 or 4 or 114 or 198 or 199)) throw new InvalidDataException("Unsupported emitter component template");
            string cab = (string)source["native"]["cab"];
            if (!id.StartsWith(cab + ":", StringComparison.OrdinalIgnoreCase) || !long.TryParse(id[(cab.Length + 1)..], out long pid) ||
                pid == 0 || files[cab].File.m_Objects.Any(o => o.m_PathID == pid))
                throw new InvalidDataException("Emitter PathID collision or wrong native file");
            declarations.Add((id, template, data)); Types.Add(id, kind);
        }
        foreach (var go in declarations.Where(d => Types[d.Identity] == 1))
        {
            if (!nodes.TryGetValue(go.Template, out var sourceNode)) throw new InvalidDataException("Emitter template must be a prefab node");
            var components = declarations.Where(d => Types[d.Identity] != 1 && Pointer(d.Identity, d.Data["m_GameObject"]) == go.Identity).ToArray();
            var required = sourceNode["components"].Select(c => (string)c).ToArray();
            if (components.Length != required.Length || components.Select(c => c.Template).Distinct().Count() != required.Length ||
                required.Any(c => !components.Any(d => d.Template == c)) ||
                components.Count(d => Types[d.Identity] == 4) != 1 || components.Count(d => Types[d.Identity] == 198) != 1 ||
                components.Count(d => Types[d.Identity] == 199) != 1)
                throw new InvalidDataException("New emitter must clone its complete GameObject component set");
            var expected = new JArray(required.Select(c => new JObject { ["component"] = PPtr(components.Single(d => d.Template == c).Identity) }));
            if (!JToken.DeepEquals(expected, go.Data["m_Component"])) throw new InvalidDataException("Emitter component ownership mismatch");
        }
        foreach (var declaration in declarations.Where(d => Types[d.Identity] != 1))
        {
            string owner = Pointer(declaration.Identity, declaration.Data["m_GameObject"]);
            var go = declarations.SingleOrDefault(d => d.Identity.Equals(owner, StringComparison.OrdinalIgnoreCase) && Types[d.Identity] == 1);
            if (go.Identity == null || !nodes[go.Template]["components"].Any(c => (string)c == declaration.Template))
                throw new InvalidDataException("Orphan emitter component");
        }
        foreach (var transform in declarations.Where(d => Types[d.Identity] == 4))
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { transform.Identity };
            string parent = Pointer(transform.Identity, transform.Data["m_Father"]);
            while (Types.TryGetValue(parent ?? "", out int kind))
            {
                if (kind != 4 || !visited.Add(parent)) throw new InvalidDataException("Emitter parent is cyclic or not a Transform");
                var p = declarations.Single(d => d.Identity == parent);parent = Pointer(parent, p.Data["m_Father"]);
            }
            if (!nodes.Values.Any(n => (string)n["transform"] == parent && !removed.Contains((string)n["identity"])))
                throw new InvalidDataException("Emitter parent is outside the active prefab hierarchy");
        }
    }

    private static JObject PPtr(string id) => new() { ["m_FileID"] = 0, ["m_PathID"] = long.Parse(id[(id.LastIndexOf(':') + 1)..]) };
    private static string Pointer(string owner, JToken ptr)
    {
        if (ptr == null || (int?)ptr["m_FileID"] != 0 || (long?)ptr["m_PathID"] == 0) return null;
        return owner[..owner.LastIndexOf(':')] + ":" + (long)ptr["m_PathID"];
    }

    private (TypeTreeDocument Doc, ObjectInfo Row, byte[] Raw) Source(string identity)
    {
        var spec = objects[identity];var file = files[(string)spec["native"]["cab"]];
        var row = file.File.m_Objects.Single(o => o.m_PathID == (long)spec["native"]["pathId"]);
        var raw = file.Bytes.AsSpan((int)row.byteStart, (int)row.byteSize).ToArray();
        return (new TypeTreeDocument(row.serializedType.m_Type, raw, file.File.m_RefTypes), row, raw);
    }

    internal void Compile(IReadOnlyDictionary<string, int> authoredTypes)
    {
        var validation = files.ToDictionary(p => p.Key, p => p.Value.File, StringComparer.OrdinalIgnoreCase);
        foreach (var item in declarations)
        {
            var (doc, row, raw) = Source(item.Template);
            var expected = (JObject)doc.Root.ToJson().DeepClone();
            int kind = Types[item.Identity];
            if (kind != 1) expected["m_GameObject"] = item.Data["m_GameObject"].DeepClone();
            if (kind == 4)
            {
                expected["m_Father"] = item.Data["m_Father"].DeepClone();
                expected["m_Children"] = new JArray(declarations.Where(d => Types[d.Identity] == 4 && Pointer(d.Identity, d.Data["m_Father"]) == item.Identity).Select(d => PPtr(d.Identity)));
                if (!JToken.DeepEquals(expected["m_Children"], item.Data["m_Children"])) throw new InvalidDataException("New emitter child membership differs");
            }
            doc.ApplyJson(item.Data);
            string cab = (string)objects[item.Template]["native"]["cab"];
            EiemEffectBuildCommand.ValidateEdit(expected, doc, files[cab].File, validation, authoredTypes);
            Assets.Add(new(item.Identity, cab, long.Parse(item.Identity[(item.Identity.LastIndexOf(':') + 1)..]), row.typeID, kind, doc.Encode(), doc, expected));
        }
    }

    internal void Apply(Dictionary<string, Dictionary<long, byte[]>> changes)
    {
        void Patch(string identity, Action<JObject> patch)
        {
            var (doc, row, _) = Source(identity);string cab = (string)objects[identity]["native"]["cab"];
            changes.TryGetValue(cab, out var edits);
            if (edits != null && edits.TryGetValue(row.m_PathID, out var bytes))doc = new TypeTreeDocument(row.serializedType.m_Type, bytes, files[cab].File.m_RefTypes);
            var before = doc.Encode();var data = (JObject)doc.Root.ToJson();patch(data);doc.ApplyJson(data);var result=doc.Encode();
            if (before.AsSpan().SequenceEqual(result))return;
            if (edits == null)changes[cab] = edits = new();
            edits[row.m_PathID] = result;
        }
        var removedTransforms = nodes.Values.Where(n => removed.Contains((string)n["identity"])).Select(n => (string)n["transform"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes.Values)
        {
            string transform = (string)node["transform"];
            var additions = declarations.Where(d => Types[d.Identity] == 4 && Pointer(d.Identity, d.Data["m_Father"]) == transform).ToArray();
            if (additions.Length > 0 || node["children"].Any(c => removedTransforms.Contains((string)c)))
                Patch(transform, data => {
                    data["m_Children"] = new JArray(((JArray)data["m_Children"]).Where(p => !removedTransforms.Contains(Pointer(transform, p))).Select(p => p.DeepClone()).Concat(additions.Select(d => PPtr(d.Identity))));
                });
            if (!removed.Contains((string)node["identity"]))continue;
            // Keep unused source records as inactive tombstones so unknown
            // source references and AssetBundle preload tables remain valid.
            Patch((string)node["identity"], data => data["m_IsActive"] = false);
            Patch(transform, data => data["m_Father"] = new JObject { ["m_FileID"] = 0, ["m_PathID"] = 0L });
            foreach (var component in node["components"].Select(c => (string)c))
            {
                int kind = (int)objects[component]["classId"];
                if (kind == 198)Patch(component, data => data["EmissionModule"]["enabled"] = false);
                if (kind is 114 or 199)Patch(component, data => { if (data["m_Enabled"] != null)data["m_Enabled"] = data["m_Enabled"].Type == JTokenType.Boolean ? new JValue(false) : new JValue(0); });
            }
        }
        // EffectSetting explicitly registers the emitter/renderer pairs for
        // game distance LOD. Keep that verified registry in step with the
        // hierarchy so removed records cannot be re-enabled through old rows.
        string Resolve(string owner, JToken ptr)
        {
            if (ptr == null || (long?)ptr["m_PathID"] == 0)return null;
            string cab=owner[..owner.LastIndexOf(':')];int fid=(int)ptr["m_FileID"];
            if (fid != 0)cab=files[cab].File.m_Externals[fid-1].fileName;
            return cab+":"+(long)ptr["m_PathID"];
        }
        if (declarations.Count > 0 || removed.Count > 0)
            foreach (var spec in objects.Values.Where(o => (int)o["classId"] == 114 && (string)o["state"] == "parsed" && (string)o["script"]?["class"] == "EffectSetting"))
            {
                string script=(string)spec["identity"];
                Patch(script, data => {
                    if (data["lodSetting"] is not JArray lod)return;
                    var addedRows=new List<JToken>();
                    foreach (var go in declarations.Where(d => Types[d.Identity] == 1))
                    {
                        var mapping=declarations.Where(d => Types[d.Identity] != 1 && Pointer(d.Identity,d.Data["m_GameObject"]) == go.Identity)
                            .ToDictionary(d => d.Template,d => d.Identity,StringComparer.OrdinalIgnoreCase);
                        mapping.Add(go.Template,go.Identity);
                        foreach (var row in lod.Where(r => Resolve(script,r["gameobject"]) == go.Template).ToArray())
                        {
                            var clone=(JObject)row.DeepClone();
                            foreach (var ptr in clone.DescendantsAndSelf().OfType<JObject>().Where(p => p["m_FileID"] != null && p["m_PathID"] != null))
                                if (mapping.TryGetValue(Resolve(script,ptr) ?? "",out string replacement))ptr["m_PathID"] = long.Parse(replacement[(replacement.LastIndexOf(':')+1)..]);
                            addedRows.Add(clone);
                        }
                    }
                    foreach (var row in lod.Where(r => removed.Contains(Resolve(script,r["gameobject"]) ?? "")).ToArray())row.Remove();
                    foreach (var row in addedRows)lod.Add(row);
                });
            }
        var hierarchy = (JArray)effect["hierarchy"];
        foreach (var n in hierarchy.Where(n => removed.Contains((string)n["identity"])).ToArray())n.Remove();
        foreach (var n in hierarchy)
            n["children"] = new JArray(n["children"].Where(c => !removedTransforms.Contains((string)c)).Select(c => c.DeepClone())
                .Concat(declarations.Where(d => Types[d.Identity] == 4 && Pointer(d.Identity, d.Data["m_Father"]) == (string)n["transform"]).Select(d => new JValue(d.Identity))));
        foreach (var go in declarations.Where(d => Types[d.Identity] == 1))
        {
            var transform = declarations.Single(d => Types[d.Identity] == 4 && Pointer(d.Identity, d.Data["m_GameObject"]) == go.Identity);
            hierarchy.Add(new JObject { ["identity"] = go.Identity, ["name"] = (string)go.Data["m_Name"], ["transform"] = transform.Identity,
                ["parent"] = Pointer(transform.Identity, transform.Data["m_Father"]),
                ["children"] = new JArray(declarations.Where(d => Types[d.Identity] == 4 && Pointer(d.Identity, d.Data["m_Father"]) == transform.Identity).Select(d => d.Identity)),
                ["components"] = new JArray(declarations.Where(d => Types[d.Identity] != 1 && Pointer(d.Identity, d.Data["m_GameObject"]) == go.Identity).Select(d => d.Identity)) });
        }
        var catalog = (JArray)effect["objects"];
        foreach (var item in declarations)
        {
            var spec = (JObject)objects[item.Template].DeepClone();spec["identity"] = item.Identity;
            spec["native"]["pathId"] = long.Parse(item.Identity[(item.Identity.LastIndexOf(':') + 1)..]);
            spec["owner"] = Types[item.Identity] == 1 ? null : Pointer(item.Identity, item.Data["m_GameObject"]);catalog.Add(spec);
        }
    }
}
