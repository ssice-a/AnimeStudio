using AnimeStudio;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeStudio.GUI;

/// <summary>Compile one visual selection while retaining its placement ancestors.</summary>
internal static class EiemEffectSelection
{
    // Selection is part of the compile request, so unused declarations must be
    // removed before validating their resource pointers, not after compilation.
    internal static HashSet<string> Prepare(JObject edits, JObject effect, IReadOnlyDictionary<string, JToken> objects)
    {
        if (edits["emitterScope"] == null) return null;
        if (edits["emitterScope"] is not JArray scope || scope.Count is 0 or > 4096 || scope.Any(i => i.Type != JTokenType.String))
            throw new InvalidDataException("Invalid emitter export selection");
        var selected = scope.Select(i => (string)i).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count != scope.Count) throw new InvalidDataException("Duplicated emitter export selection");
        var nodes = effect["hierarchy"].ToDictionary(n => (string)n["identity"], n => (JObject)n.DeepClone(), StringComparer.OrdinalIgnoreCase);
        var added = edits["emitters"]?["added"] as JArray;
        var declarations = (added ?? new JArray()).ToDictionary(d => (string)d["identity"], StringComparer.OrdinalIgnoreCase);
        int Kind(JToken d) => objects.TryGetValue((string)d["template"], out var spec) ? (int)spec["classId"] : throw new InvalidDataException("Unknown emitter template");
        string Target(string owner, JToken ptr) => (int?)ptr?["m_FileID"] == 0 && (long?)ptr["m_PathID"] is long pid && pid != 0
            ? owner[..owner.LastIndexOf(':')] + ":" + pid : null;
        foreach (var go in declarations.Values.Where(d => Kind(d) == 1))
        {
            string id = (string)go["identity"];
            var components = declarations.Values.Where(d => Kind(d) != 1 && Target((string)d["identity"], d["data"]?["m_GameObject"]) == id).ToArray();
            var transform = components.SingleOrDefault(d => Kind(d) == 4) ?? throw new InvalidDataException("Emitter Transform is missing");
            nodes.Add(id, new JObject { ["identity"] = id, ["transform"] = transform["identity"].DeepClone(),
                ["parent"] = Target((string)transform["identity"], transform["data"]?["m_Father"]),
                ["components"] = new JArray(components.Select(d => d["identity"].DeepClone())) });
        }
        foreach (string removed in (edits["emitters"]?["removed"] as JArray ?? new JArray()).Values<string>()) nodes.Remove(removed);
        if (selected.Any(i => !nodes.ContainsKey(i))) throw new InvalidDataException("Emitter export selection is absent");
        var byTransform = nodes.Values.ToDictionary(n => (string)n["transform"], StringComparer.OrdinalIgnoreCase);
        var retained = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase) { (string)effect["root"] };
        foreach (string id in selected)
        {
            string parent = (string)nodes[id]["parent"]; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (parent != null && byTransform.TryGetValue(parent, out var node))
            {
                if (!seen.Add(parent)) throw new InvalidDataException("Cyclic emitter placement hierarchy");
                retained.Add((string)node["identity"]); parent = (string)node["parent"];
            }
        }
        int Class(string id) => objects.TryGetValue(id, out var o) ? (int)o["classId"] : Kind(declarations[id]);
        var keep = nodes.Values.Where(n => retained.Contains((string)n["identity"]))
            .SelectMany(n => n["components"].Values<string>().Prepend((string)n["identity"]))
            .Where(id => Class(id) is not (198 or 199) || selected.Contains(objects.TryGetValue(id, out var o)
                ? (string)o["owner"] : Target(id, declarations[id]["data"]?["m_GameObject"])))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excluded = declarations.Keys.Where(id => !keep.Contains(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (added != null)
        {
            foreach (var d in added.Where(d => excluded.Contains((string)d["identity"])).ToArray()) d.Remove();
            foreach (var d in added)
            {
                string id = (string)d["identity"]; var data = (JObject)d["data"];
                if (Kind(d) == 1) data["m_Component"] = new JArray(data["m_Component"].Where(p => !excluded.Contains(Target(id, p["component"]) ?? "")).Select(p => p.DeepClone()));
                if (Kind(d) == 4) data["m_Children"] = new JArray(data["m_Children"].Where(p => !excluded.Contains(Target(id, p) ?? "")).Select(p => p.DeepClone()));
            }
        }
        var scene = effect["hierarchy"].SelectMany(n => n["components"].Values<string>().Prepend((string)n["identity"]))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var change in ((JArray)edits["objects"]).Where(c => scene.Contains((string)c["identity"]) && !keep.Contains((string)c["identity"])).ToArray()) change.Remove();
        return selected;
    }

    internal static void Apply(JToken input, JObject effect,
        IReadOnlyDictionary<string, (SerializedFile File, byte[] Bytes, JToken Row)> files,
        Dictionary<string, Dictionary<long, byte[]>> changes, List<EiemEffectAssetEdits.Asset> authored)
    {
        if (input == null) return;
        if (input is not JArray scope || scope.Count is 0 or > 4096 || scope.Any(i => i.Type != JTokenType.String))
            throw new InvalidDataException("Invalid emitter export selection");
        var nodes = effect["hierarchy"].ToDictionary(n => (string)n["identity"], StringComparer.OrdinalIgnoreCase);
        var selected = scope.Select(i => (string)i).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count != scope.Count || selected.Any(i => !nodes.ContainsKey(i)))
            throw new InvalidDataException("Emitter export selection is absent or duplicated");
        var byTransform = nodes.Values.ToDictionary(n => (string)n["transform"], StringComparer.OrdinalIgnoreCase);
        var retained = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase) { (string)effect["root"] };
        foreach (string id in selected)
        {
            string parent = (string)nodes[id]["parent"];
            var seen = new HashSet<string>();
            while (parent != null && byTransform.TryGetValue(parent, out var node))
            {
                if (!seen.Add(parent)) throw new InvalidDataException("Cyclic emitter placement hierarchy");
                retained.Add((string)node["identity"]); parent = (string)node["parent"];
            }
        }
        var objects = effect["objects"].ToDictionary(o => (string)o["identity"], StringComparer.OrdinalIgnoreCase);
        void Patch(string identity, Action<JObject> patch)
        {
            int added = authored.FindIndex(a => a.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase));
            if (added >= 0)
            {
                var asset = authored[added];var data = (JObject)asset.Document.Root.ToJson();patch(data);
                asset.Document.ApplyJson(data);authored[added] = asset with { Payload = asset.Document.Encode() };return;
            }
            var spec = objects[identity];string cab = (string)spec["native"]["cab"];
            var native = files[cab];long pid = (long)spec["native"]["pathId"];
            var row = native.File.m_Objects.Single(o => o.m_PathID == pid);
            if (!changes.TryGetValue(cab, out var edits)) changes[cab] = edits = new();
            var raw = edits.GetValueOrDefault(pid) ?? native.Bytes.AsSpan((int)row.byteStart, (int)row.byteSize).ToArray();
            var document = new TypeTreeDocument(row.serializedType.m_Type, raw, native.File.m_RefTypes);
            var dataOriginal = (JObject)document.Root.ToJson();patch(dataOriginal);document.ApplyJson(dataOriginal);edits[pid] = document.Encode();
        }
        string Resolve(string owner, JToken ptr)
        {
            if ((long?)ptr?["m_PathID"] is null or 0) return null;
            string cab = owner[..owner.LastIndexOf(':')];int fid = (int)ptr["m_FileID"];
            if (fid != 0) cab = files[cab].File.m_Externals[fid - 1].fileName;
            return cab + ":" + (long)ptr["m_PathID"];
        }
        var retired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes.Values)
        {
            string id = (string)node["identity"], transform = (string)node["transform"];
            var removedComponents = node["components"].Select(i => (string)i)
                .Where(i => !selected.Contains(id) && (int)objects[i]["classId"] is 198 or 199).ToHashSet(StringComparer.OrdinalIgnoreCase);
            retired.UnionWith(removedComponents);
            Patch(id, data => {
                if (!retained.Contains(id)) data["m_IsActive"] = false;
                data["m_Component"] = new JArray(data["m_Component"].Where(c => !removedComponents.Contains(Resolve(id, c["component"]) ?? "")).Select(c => c.DeepClone()));
            });
            Patch(transform, data => {
                data["m_Children"] = new JArray(data["m_Children"].Where(c =>
                    byTransform.TryGetValue(Resolve(transform, c) ?? "", out var child) && retained.Contains((string)child["identity"])).Select(c => c.DeepClone()));
                if (!retained.Contains(id)) data["m_Father"] = new JObject { ["m_FileID"] = 0, ["m_PathID"] = 0L };
            });
            node["components"] = new JArray(node["components"].Where(i => !removedComponents.Contains((string)i)).Select(i => i.DeepClone()));
            node["children"] = new JArray(node["children"].Where(i => byTransform.TryGetValue((string)i, out var child) && retained.Contains((string)child["identity"])).Select(i => i.DeepClone()));
        }
        foreach (var spec in objects.Values.Where(o => (int)o["classId"] == 114 && (string)o["state"] == "parsed" && (string)o["script"]?["class"] == "EffectSetting"))
        {
            string id = (string)spec["identity"];
            Patch(id, data => {
                if (data["lodSetting"] is JArray rows)
                    data["lodSetting"] = new JArray(rows.Where(r => selected.Contains(Resolve(id, r["gameobject"]) ?? "")).Select(r => r.DeepClone()));
            });
        }
        foreach (var node in ((JArray)effect["hierarchy"]).Where(n => !retained.Contains((string)n["identity"])).ToArray()) node.Remove();
        foreach (var obj in ((JArray)effect["objects"]).Where(o => retired.Contains((string)o["identity"])).ToArray()) obj.Remove();
    }
}
