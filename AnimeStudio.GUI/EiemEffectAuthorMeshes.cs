using AnimeStudio;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio.GUI;

/// <summary>Publish author Meshes through the ordinary single-Mesh native
/// resource contract. Effect renderers consume their public INI Resource IDs.</summary>
internal static class EiemEffectAuthorMeshes
{
    internal static JArray Write(string staged, JToken input, IReadOnlyDictionary<string, JToken> objects,
        IReadOnlyDictionary<string, (SerializedFile File, byte[] Bytes, JToken Row)> files)
    {
        var receipt = new JArray();
        if (input == null) return receipt;
        if (input is not JArray rows) throw new InvalidDataException("Invalid public author Mesh inputs");
        var assets = EiemEffectAssetEdits.Read(rows, objects, files);
        var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in rows.Zip(assets))
        {
            string resource = (string)pair.First["resource"]; var mesh = pair.Second;
            if (mesh.ClassId != 43 || string.IsNullOrWhiteSpace(resource) || resource.Length > 1024 ||
                resource.Any(char.IsControl) || !resources.Add(resource))
                throw new InvalidDataException("Duplicate/invalid public author Mesh resource");
            var origin = files[mesh.Cab];
            int bundleType = origin.File.m_Types.FindIndex(t => t.classID == 142 && t.m_Type?.m_Nodes?.Count > 0);
            if (bundleType < 0) throw new InvalidDataException("Mesh source has no native AssetBundle schema");
            string stem = new(resource.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            stem = stem.Trim().TrimEnd('.'); if (stem.Length == 0) stem = "Mesh";
            if (stem.Length > 96) stem = stem[..96];
            string name = stem; int suffix = 2; while (!names.Add(name)) name = stem + "_" + suffix++;
            string path = "assets/eff/meshes/" + name.ToLowerInvariant() + ".asset";
            mesh.Document.Root["m_Name"].Scalar = resource;
            byte[] payload = mesh.Document.Encode();
            string cab = "CAB-" + EiemEffectBuildCommand.Hash(Encoding.UTF8.GetBytes(
                "EFF_AUTHOR_MESH/1\n" + resource + "\n" + EiemEffectBuildCommand.Hash(payload))).ToLowerInvariant()[..32];
            var bundle = EiemEffectPrefabWriter.CreateBundle(origin.File.m_Types[bundleType].m_Type);
            var data = (JObject)bundle.Root.ToJson();
            JObject Pointer(long id) => new() { ["m_FileID"] = 0, ["m_PathID"] = id };
            data["m_Name"] = resource;
            if (data["m_AssetBundleName"] != null) data["m_AssetBundleName"] = resource;
            data["m_PreloadTable"] = new JArray(Pointer(1));
            data["m_Container"] = new JArray(new JObject { ["first"] = path,
                ["second"] = new JObject { ["preloadIndex"] = 0, ["preloadSize"] = 1, ["asset"] = Pointer(1) } });
            bundle.ApplyJson(data);
            byte[] metadata = EiemEffectPrefabWriter.CreateMetadataOnly(origin.File, origin.Bytes);
            using var templateReader = new FileReader(cab, new MemoryStream(metadata, false));
            var template = new SerializedFile(templateReader, origin.File.assetsManager);
            byte[] native = EiemEffectPrefabWriter.Build(template, metadata, new[]
            {
                new EiemEffectPrefabWriter.NativeObject(1, mesh.Type, payload),
                new EiemEffectPrefabWriter.NativeObject(2, bundleType, bundle.Encode())
            });
            using (var checkReader = new FileReader(cab, new MemoryStream(native, false)))
            {
                var check = new SerializedFile(checkReader, origin.File.assetsManager);
                if (check.m_Externals.Count != 0 || check.m_Objects.Count != 2 ||
                    !check.m_Objects.Select(o => o.classID).Order().SequenceEqual(new[] { 43, 142 }))
                    throw new InvalidDataException("Public author Mesh native resource contract differs");
            }
            byte[] archive = EiemEffectNativeArchive.Write(new[] { (Name: cab, Bytes: native) });
            string relative = "author-meshes/" + name + ".ab";
            Directory.CreateDirectory(Path.Combine(staged, "author-meshes"));
            File.WriteAllBytes(Path.Combine(staged, relative), archive);
            receipt.Add(new JObject { ["resource"] = resource, ["file"] = relative, ["cab"] = cab,
                ["asset"] = resource, ["pathId"] = 1, ["path"] = path });
        }
        return receipt;
    }
}
