using AnimeStudio;
using Newtonsoft.Json.Linq;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio.GUI;

/// <summary>Resolve game-owned PPtrs without copying their native payloads.</summary>
internal static class EiemEffectGameReferences
{
    /// <summary>Compile selected/imported bindings, without consulting a game index.
    /// Imported snapshots provide native identities; new paths bind at runtime.
    /// CAB/PathID here describe the native source generation, not a logical identity.
    /// </summary>
    internal static (Dictionary<string, EiemEffectGameReferenceInputs.Target> Targets, JArray Leases)
        Imported(JArray references, JObject baseline, IReadOnlyDictionary<string, JToken> objects)
    {
        var targets = new Dictionary<string, EiemEffectGameReferenceInputs.Target>(StringComparer.OrdinalIgnoreCase);
        var leases = new JArray();
        var catalogs = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in references)
        {
            string logical = EiemEffectGameReferenceInputs.Logical((string)row["path"]);
            string expected = (string)row["type"], asset = (string)row["asset"];
            bool Matches(JToken spec) => spec["logicalPaths"] is JArray paths && paths.Any(p =>
                string.Equals((string)p["path"], logical, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(asset) || (string)p["name"] == asset));
            string identity = (string)row["resourceIdentity"];
            int? classId = (int?)row["resourceClassId"];
            if (identity == null || classId == null)
            {
                // Compatibility for existing projects: use their already exported
                // catalog, never scan EIDX or reverse-search owning game Prefabs.
                IEnumerable<JToken> candidates = objects.Values.Where(Matches);
                string source = (string)row["previewSource"];
                if (!string.IsNullOrWhiteSpace(source) && File.Exists(Path.Combine(source, "effect.json")))
                {
                    if (!catalogs.TryGetValue(source, out var catalog))
                        catalogs[source] = catalog = (JArray)JObject.Parse(File.ReadAllText(
                            EiemEffectBuildCommand.SafePath(source, "effect.json")))["objects"];
                    candidates = catalog.Where(Matches);
                }
                var found = candidates.ToArray();
                if (found.Length == 1)
                { identity = (string)found[0]["identity"];classId = (int?)found[0]["classId"]; }
                else if (found.Length == 0)
                {
                    // A new logical reference needs no author-side discovery.
                    // Give its PPtr a private relocation slot; the DLL resolves
                    // this exact path through the running game's manifest.
                    string kindName = expected == "Texture" ? "Texture2D" : expected;
                    if (!Enum.TryParse<ClassIDType>(kindName, out var kindValue) || kindName == "Object")
                        throw new InvalidDataException("Logical resource needs a concrete PPtr type: " + logical);
                    string marker = "CAB-" + EiemEffectBuildCommand.Hash(Encoding.UTF8.GetBytes("EFF_LOGICAL_RESOURCE/1\n" +
                        EiemEffectGameReferenceInputs.Key(row))).ToLowerInvariant()[..32];
                    var deferred = new JObject { ["path"] = logical, ["asset"] = asset ?? "", ["type"] = kindName,
                        ["relocationCab"] = marker, ["relocationId"] = 1 };
                    if (row["sourcePrefab"] != null)deferred.Merge(SourceLease(row, baseline, null, 0, 0, portableOnly:true));
                    if (!leases.Any(l => JToken.DeepEquals(l, deferred)))leases.Add(deferred);
                    targets[EiemEffectGameReferenceInputs.Key(row)] = new(marker, 1, (int)kindValue);
                    continue;
                }
                else throw new InvalidDataException("Imported game resource is ambiguous: " + logical);
            }
            int separator = identity.LastIndexOf(':');
            string cab = separator < 0 ? "" : identity[..separator];
            if (cab.Length != 36 || !cab.StartsWith("CAB-", StringComparison.OrdinalIgnoreCase) ||
                cab[4..].Any(c => !Uri.IsHexDigit(c)) || !long.TryParse(identity[(separator + 1)..], out long id) || id == 0 ||
                classId == null || !Enum.IsDefined(typeof(ClassIDType), classId.Value))
                throw new InvalidDataException("Invalid imported game resource native identity: " + logical);
            string kind = ((ClassIDType)classId.Value).ToString();
            if (expected != "Object" && expected != kind && !(expected == "Texture" &&
                kind is "Texture2D" or "Cubemap" or "Texture3D" or "Texture2DArray" or "CubemapArray"))
                throw new InvalidDataException("Imported game resource type differs: " + logical + " / " + expected);
            var proof = SourceLease(row, baseline, cab, id, classId.Value);
            if (proof == null)
                throw new InvalidDataException("Game resource has no selected source Prefab. Bind it before exporting: " + logical);
            // Imported identities prove the author source only. They are not
            // stable runtime addresses: every binding resolves the logical
            // path in the running game's resource generation.
            string markerCab = "CAB-" + EiemEffectBuildCommand.Hash(Encoding.UTF8.GetBytes("EFF_LOGICAL_RESOURCE/1\n" +
                EiemEffectGameReferenceInputs.Key(row))).ToLowerInvariant()[..32];
            var target = new EiemEffectGameReferenceInputs.Target(markerCab, 1, classId.Value);
            string key = EiemEffectGameReferenceInputs.Key(row);
            if (targets.TryGetValue(key, out var prior) && prior != target)
                throw new InvalidDataException("Conflicting imported game resource identities: " + logical);
            targets[key] = target;
            var lease = new JObject { ["path"] = logical, ["asset"] = asset ?? "", ["type"] = kind,
                ["relocationCab"] = markerCab, ["relocationId"] = 1 };
            lease.Merge(proof);
            if (!leases.Any(l => JToken.DeepEquals(l, lease)))leases.Add(lease);
        }
        return (targets, leases);
    }

    // A logical path in AssetBundle.m_Container is not necessarily registered
    // as an independently loadable asset in RuntimeManifestBinary. Retain the
    // source Prefab through the game's loader to supply its native dependency
    // closure; never instantiate the donor or copy its resource payloads.
    internal static JObject SourceLease(JToken row, JObject baseline, string cab, long id, int classId, bool portableOnly = false)
    {
        if (row["sourcePrefab"] != null)
        {
            string prefabPath = EiemEffectGameReferenceInputs.Logical((string)row["sourcePrefab"]);
            if (!prefabPath.EndsWith(".prefab"))
                throw new InvalidDataException("Invalid portable game source Prefab proof");
            var proof = new JObject { ["sourcePrefab"] = prefabPath };
            if (!string.IsNullOrWhiteSpace((string)row["sourceRoot"]))proof["sourceRoot"] = row["sourceRoot"].DeepClone();
            foreach (string key in new[] { "sourceRootIdentity", "sourceVfsFingerprint" })
                if (row[key] != null) proof[key] = row[key].DeepClone();
            return proof;
        }
        if (portableOnly) return null;
        string source = (string)row["previewSource"];
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(Path.Combine(source, "source/manifest.json"))) return null;
        var manifest = JObject.Parse(File.ReadAllText(EiemEffectBuildCommand.SafePath(source, "source/manifest.json")));
        string catalogPath = EiemEffectBuildCommand.SafePath(source, "effect.json");
        var catalog = File.Exists(catalogPath) ? JObject.Parse(File.ReadAllText(catalogPath)) : null;
        // Verify the imported identity against the selected author source.
        // No installed game index participates in this source proof.
        if ((string)manifest["format"] != "EFFSOURCE" ||
            catalog != null && ((string)manifest["vfsFingerprint"] != (string)baseline["vfsFingerprint"] ||
            (string)catalog["format"] != "EFF_EFFECT_SOURCE" ||
            (string)manifest["root"] != (string)catalog["root"] || (string)manifest["prefab"] != (string)catalog["prefab"]))
            throw new InvalidDataException("Game reference source Prefab differs; refresh its author source");
        string prefab = (string)manifest["prefab"], identity = cab + ":" + id;
        if (prefab == null || !prefab.StartsWith("assets/") || !prefab.EndsWith(".prefab") ||
            prefab.Contains('\\') || prefab.Contains(':') || prefab.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException("Game reference source is not a logical Prefab");
        var resource = ((catalog?["objects"] ?? manifest["resources"]) as JArray)?.SingleOrDefault(o => string.Equals((string)o["identity"], identity, StringComparison.OrdinalIgnoreCase));
        bool matchesPath = catalog != null ? resource?["logicalPaths"] is JArray paths &&
            paths.Any(p => string.Equals((string)p["path"], (string)row["path"], StringComparison.OrdinalIgnoreCase)) :
            string.Equals((string)resource?["logicalPath"], (string)row["path"], StringComparison.OrdinalIgnoreCase);
        if (resource == null || (int?)(resource["classId"] ?? resource["native"]?["classId"]) != classId || !matchesPath)
            throw new InvalidDataException("Game reference is outside its source Prefab dependency closure");
        var root = ((catalog?["hierarchy"] ?? manifest["hierarchy"]) as JArray)?.SingleOrDefault(n => (string)n["identity"] == (string)manifest["root"]);
        if (string.IsNullOrWhiteSpace((string)root?["name"]))
            throw new InvalidDataException("Game reference source Prefab root is missing");
        var result = new JObject { ["sourcePrefab"] = prefab, ["sourceRoot"] = (string)root["name"] };
        if ((manifest["containerEntries"] as JArray)?.Count(e =>
            string.Equals((string)e["logicalPath"], prefab, StringComparison.OrdinalIgnoreCase) &&
            string.Equals((string)e["asset"]?["identity"], (string)manifest["root"], StringComparison.OrdinalIgnoreCase)) == 1)
        {
            result["sourceRootIdentity"] = manifest["root"].DeepClone();
            result["sourceVfsFingerprint"] = manifest["vfsFingerprint"].DeepClone();
        }
        return result;
    }

    internal static bool IsAuthoredResource(JObject edits, IReadOnlyDictionary<string, JToken> objects,
        string cab, long selectedId, int classId)
    {
        var authored = (edits["assets"] as JArray)?.SingleOrDefault(a =>
            string.Equals((string)a["identity"], cab + ":" + selectedId, StringComparison.OrdinalIgnoreCase));
        return authored != null && objects.TryGetValue((string)authored["template"], out var template) &&
            (int)template["classId"] == classId &&
            string.Equals((string)template["native"]["cab"], cab, StringComparison.OrdinalIgnoreCase);
    }

    internal static JArray Apply(JObject edits, JObject baseline,
        Dictionary<string, (SerializedFile File, byte[] Bytes, JToken Row)> files,
        IReadOnlyDictionary<string, JToken> objects, List<FileReader> opened,
        IDictionary<string, int> types)
    {
        var leases = new JArray();
        if (edits["gameReferences"] == null) return leases;
        if (edits["gameReferences"] is not JArray refs || refs.Count > 4096)
            throw new InvalidDataException("Invalid game resource references");
        if (refs.Count == 0) return leases;
        var resolution = Imported(refs, baseline, objects);
        foreach (var target in resolution.Targets.Values) types[target.Cab + ":" + target.Id] = target.Class;
        leases = resolution.Leases;
        ApplyBindings(edits, refs, resolution.Targets, files, objects, opened, preserveAuthored: true);
        return leases;
    }

    internal static void ApplyBindings(JObject edits, JArray refs,
        IReadOnlyDictionary<string, EiemEffectGameReferenceInputs.Target> targets,
        Dictionary<string, (SerializedFile File, byte[] Bytes, JToken Row)> files,
        IReadOnlyDictionary<string, JToken> objects, List<FileReader> opened, bool preserveAuthored)
    {
        var seen = new HashSet<string>();
        foreach (var row in refs)
        {
            string owner = (string)row["owner"], field = (string)row["field"];
            if (owner == null || field == null || !seen.Add(owner + "\n" + field)) throw new InvalidDataException("Duplicate game resource binding");
            var change = ((JArray)edits["objects"]).Concat(edits["emitters"]?["added"] ?? new JArray()).SingleOrDefault(c => (string)c["identity"] == owner);
            var asset = (edits["assets"] as JArray)?.SingleOrDefault(a => (string)a["identity"] == owner);
            TypeTreeDocument assetDocument = null;
            if (change == null)
            {
                if (asset != null)
                {
                    // Authored Materials can borrow a game's Shader/Texture
                    // without copying the donor Material or dependency bundle.
                    assetDocument = EiemEffectAssetEdits.Read(new JArray(asset.DeepClone()), objects, files).Single().Document;
                    change = new JObject { ["data"] = assetDocument.Root.ToJson() };
                }
                else
                {
                    if (!objects.TryGetValue(owner, out var spec)) throw new InvalidDataException("Unknown game resource owner: " + owner);
                    throw new InvalidDataException("Game resource owner requires an explicit edit: " + (string)spec["name"]);
                }
            }
            var data = change["data"] as JObject;
            var ptr = data?.SelectToken(field, false) as JObject;
            if (ptr == null || ptr["m_FileID"] == null || ptr["m_PathID"] == null)
                throw new InvalidDataException("Game resource binding is not a PPtr: " + field);
            var target = targets[ReferenceKey(row)];
            string cab = owner[..owner.LastIndexOf(':')];
            if (!files.TryGetValue(cab, out var native)) throw new InvalidDataException("Unknown game resource owner file");
            int selectedFile = (int)ptr["m_FileID"];long selectedPath = (long)ptr["m_PathID"];
            string selectedCab = selectedFile == 0 ? cab : selectedFile > 0 && selectedFile <= native.File.m_Externals.Count
                ? native.File.m_Externals[selectedFile - 1].fileName : null;
            // Local author resources can live in a different native file
            // from their game provenance. The selected PPtr owns that choice.
            if (preserveAuthored && selectedCab != null && IsAuthoredResource(edits, objects, selectedCab, selectedPath, target.Class))
                continue;
            int fid = native.File.m_Externals.FindIndex(e => e.fileName.Equals(target.Cab, StringComparison.OrdinalIgnoreCase)) + 1;
            if (cab.Equals(target.Cab, StringComparison.OrdinalIgnoreCase)) fid = 0;
            else if (fid == 0)
            {
                byte[] rebuilt = AddExternal(native.File, native.Bytes, target.Cab);
                var reader = new FileReader(cab, new MemoryStream(rebuilt, false)); opened.Add(reader);
                var file = new SerializedFile(reader, native.File.assetsManager);
                files[cab] = (file, rebuilt, native.Row);
                fid = file.m_Externals.Count;
            }
            // A local Material/Texture clone can retain a game reference as
            // provenance and as the lease supplying its unchanged shader.
            // Do not overwrite that explicit authored resource with the donor.
            ptr["m_FileID"] = fid; ptr["m_PathID"] = target.Id;
            if (assetDocument != null)
            {
                assetDocument.ApplyJson(data);
                asset["payloadBase64"] = Convert.ToBase64String(assetDocument.Encode());
            }
        }
    }

    internal static string ReferenceKey(JToken row) => row["resource"] != null
        ? (string)row["resource"] + "\n" + (string)row["type"] : EiemEffectGameReferenceInputs.Key(row);

    internal static byte[] AddExternal(SerializedFile file, byte[] original, string cab)
    {
        if ((int)file.header.m_Version != 22 || cab.Length != 36 || !cab.StartsWith("CAB-", StringComparison.OrdinalIgnoreCase) ||
            cab[4..].Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Unsupported external native identity");
        using var metadata = new MemoryStream();
        metadata.Write(original, 48, checked((int)file.ExternalTableStart - 48));
        using var writer = new BinaryWriter(metadata, Encoding.UTF8, true);
        writer.Write(file.m_Externals.Count + 1);
        metadata.Write(original, checked((int)file.ExternalTableStart + 4), checked((int)(file.ExternalTableEnd - file.ExternalTableStart - 4)));
        writer.Write((byte)0); writer.Write(new byte[16]); writer.Write(0);
        writer.Write(Encoding.UTF8.GetBytes("archive:/" + cab + "/" + cab)); writer.Write((byte)0);
        metadata.Write(original, (int)file.ExternalTableEnd, checked(48 + (int)file.header.m_MetadataSize - (int)file.ExternalTableEnd));
        long offset = (48 + metadata.Length + 15) & ~15L;
        using var output = new MemoryStream();output.Write(original, 0, 48);output.Write(metadata.ToArray());
        while (output.Position < offset) output.WriteByte(0);
        output.Write(original, (int)file.header.m_DataOffset, checked(original.Length - (int)file.header.m_DataOffset));
        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), checked((uint)metadata.Length));
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(24), bytes.LongLength);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(32), offset);
        return bytes;
    }
}
