using AnimeStudio;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AssetObject = AnimeStudio.Object;

namespace AnimeStudio.GUI;

/// <summary>
/// Independent effect source document, not a model package or a game-loadable
/// replacement. Complete native snapshots remain authoritative for opaque data.
/// </summary>
internal sealed class EiemEffectSourceWriter
{
    private readonly IReadOnlyList<VirtualAssetRecord> records;
    private readonly EndfieldBundleDependencyIndex dependencies;
    private readonly string fingerprint, index;
    private readonly EndfieldVfsArchive archive;
    private static readonly HashSet<ClassIDType> BulkTypes = new()
    {
        ClassIDType.Mesh, ClassIDType.Texture2D, ClassIDType.Shader,
        ClassIDType.AnimationClip, ClassIDType.AudioClip, ClassIDType.Avatar
    };

    internal EiemEffectSourceWriter(IReadOnlyList<VirtualAssetRecord> records,
        EndfieldBundleDependencyIndex dependencies, string fingerprint, string index,
        EndfieldVfsArchive archive = null)
    { this.records = records; this.dependencies = dependencies; this.fingerprint = fingerprint; this.index = index; this.archive = archive; }

    internal void Write(VirtualAssetFile prefab, GameObject root, string directory, Action verifyCurrent)
    {
        var hierarchy = ValidateHierarchy(root);
        EiemPackagePublication.Publish(directory, staged =>
        {
            verifyCurrent();
            EiemSourceManifestWriter.Write(staged, prefab, root, dependencies, fingerprint,
                Array.Empty<EiemSourceManifestWriter.AuthorResource>(), index, archive);
            WriteDocument(staged, prefab, root, hierarchy);
            verifyCurrent();
        });
    }

    internal static string Identity(AssetObject asset) => asset.assetsFile.fileName.ToLowerInvariant() + ":" + asset.m_PathID;

    internal static IReadOnlyList<GameObject> ValidateHierarchy(GameObject root)
    {
        if (root == null) throw new InvalidDataException("Missing effect root");
        var result = new List<GameObject>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(GameObject Node, Transform Parent)>();
        pending.Push((root, null));
        while (pending.TryPop(out var row))
        {
            if (result.Count > 100000) throw new InvalidDataException("Effect hierarchy is too large");
            var go = row.Node;
            if (!seen.Add(Identity(go))) throw new InvalidDataException("Cycle or duplicate child: " + Identity(go));
            var transform = go.m_Transform ?? throw new InvalidDataException("Missing Transform: " + Identity(go));
            if (!transform.m_GameObject.TryGet(out var owner) || owner != go)
                throw new InvalidDataException("Transform owner mismatch: " + Identity(transform));
            if (row.Parent != null && (!transform.m_Father.TryGet(out var parent) || parent != row.Parent))
                throw new InvalidDataException("Child/parent mismatch: " + Identity(transform));
            var components = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pointer in go.m_Components)
            {
                if (!pointer.Cast<AssetObject>().TryGet(out var component)) throw new InvalidDataException("Missing component on " + Identity(go));
                if (!components.Add(Identity(component))) throw new InvalidDataException("Duplicate component on " + Identity(go));
            }
            if (!components.Contains(Identity(transform))) throw new InvalidDataException("Transform is not registered on " + Identity(go));
            result.Add(go);
            for (int i = transform.m_Children.Count - 1; i >= 0; i--)
            {
                if (!transform.m_Children[i].TryGet(out var child) || !child.m_GameObject.TryGet(out var childObject))
                    throw new InvalidDataException("Unresolved child of " + Identity(go));
                pending.Push((childObject, transform));
            }
        }
        return result;
    }

    private void WriteDocument(string directory, VirtualAssetFile prefab, GameObject root, IReadOnlyList<GameObject> hierarchy)
    {
        Directory.CreateDirectory(Path.Combine(directory, "objects"));
        Directory.CreateDirectory(Path.Combine(directory, "schemas"));
        var manager = root.assetsFile.assetsManager;
        var files = manager.assetsFileList.ToDictionary(f => f.fileName, StringComparer.OrdinalIgnoreCase);
        var schemas = new Dictionary<string, string>(StringComparer.Ordinal);
        var schemaCache = new Dictionary<(SerializedFile, SerializedType), string>();
        var schemaFiles = new List<object>();
        var assets = new List<object>();
        var warnings = new List<string>();
        var queue = new Queue<AssetObject>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previewAssets = new List<AssetObject>();
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var logical = records.GroupBy(r => r.PathId).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var go in hierarchy)
        {
            queue.Enqueue(go);
            foreach (var pointer in go.m_Components)
            {
                pointer.Cast<AssetObject>().TryGet(out var component);
                if (!owners.TryAdd(Identity(component), Identity(go))) throw new InvalidDataException("Component has multiple owners: " + Identity(component));
                queue.Enqueue(component);
            }
        }

        string SchemaFile(AssetObject obj)
        {
            var key = (obj.assetsFile, obj.serializedType);
            if (schemaCache.TryGetValue(key, out var cached)) return cached;
            var tree = obj.serializedType?.m_Type;
            if (tree?.m_Nodes?.Count is not > 0) return null;
            var refs = obj.assetsFile.m_RefTypes ?? new List<SerializedType>();
            var payload = new JObject
            {
                ["format"] = "EFF_TYPE_TREE", ["version"] = 1,
                ["nodes"] = Nodes(tree),
                ["managedTypes"] = new JArray(refs.Select(t => new JObject
                {
                    ["class"] = t.m_KlassName, ["namespace"] = t.m_NameSpace, ["assembly"] = t.m_AsmName,
                    ["nodes"] = Nodes(t.m_Type)
                }))
            };
            string canonical = payload.ToString(Formatting.None);
            string hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            if (schemas.TryGetValue(hash, out var known)) { schemaCache[key] = known; return known; }
            string file = "schemas/" + hash + ".json";
            Save(directory, file, payload);
            schemas.Add(hash, file);
            schemaCache[key] = file;
            schemaFiles.Add(new { file, canonicalSha256 = hash });
            return file;
        }

        while (queue.TryDequeue(out var asset))
        {
            string id = Identity(asset);
            if (!visited.Add(id)) continue;
            previewAssets.Add(asset);
            var schemaFile = SchemaFile(asset);
            string state = "raw-backed", reason = null, dataFile = null;
            TypeTreeDocument doc = null;
            if (schemaFile == null) reason = "TypeTree is absent";
            else if (BulkTypes.Contains(asset.type)) reason = "Bulk resource is preserved in native snapshots";
            else if (asset.byteSize > 16 * 1024 * 1024) reason = "Object exceeds structured decoding limit (16 MiB); native bytes preserved";
            else
            {
                try
                {
                    if (asset.reader.Endian != EndianType.LittleEndian) throw new InvalidDataException("Unsupported byte order");
                    doc = new TypeTreeDocument(asset.serializedType.m_Type, asset.GetRawData(), asset.assetsFile.m_RefTypes);
                    state = "parsed";
                }
                catch (Exception error) when (error is InvalidDataException or OverflowException or System.Text.DecoderFallbackException)
                { state = "opaque"; reason = error.Message; warnings.Add(id + ": " + reason); }
            }

            var references = new List<object>();
            var modules = new List<object>();
            object script = null;
            if (doc != null)
            {
                if (owners.TryGetValue(id, out var expectedOwner) && doc.Root.Fields.TryGetValue("m_GameObject", out var ownerPtr))
                {
                    var target = Resolve(ownerPtr, asset.assetsFile, files);
                    if (target == null || Identity(target) != expectedOwner) throw new InvalidDataException("Component owner mismatch: " + id);
                }
                foreach (var row in doc.Root.Walk())
                {
                    if (!row.Value.Schema.Type.StartsWith("PPtr<", StringComparison.Ordinal)) continue;
                    int fileId = Convert.ToInt32(row.Value["m_FileID"].Scalar);
                    long pathId = Convert.ToInt64(row.Value["m_PathID"].Scalar);
                    var target = Resolve(row.Value, asset.assetsFile, files);
                    bool isNull = pathId == 0 || fileId < 0;
                    string cab = fileId == 0 ? asset.assetsFile.fileName : fileId > 0 && fileId <= asset.assetsFile.m_Externals.Count ? asset.assetsFile.m_Externals[fileId - 1].fileName : null;
                    references.Add(new { field = row.Path, fileId, pathId, cab, isNull,
                        resolved = target != null, identity = isNull || cab == null ? null : cab.ToLowerInvariant() + ":" + pathId,
                        type = target?.type.ToString() });
                    if (!isNull && target == null) warnings.Add(id + row.Path + ": unresolved PPtr " + fileId + ":" + pathId);
                    if (target != null) queue.Enqueue(target);
                    if (row.Path == "$.m_Script" && target is MonoScript mono)
                        script = new { identity = Identity(mono), @class = mono.m_ClassName, @namespace = mono.m_Namespace, assembly = mono.m_AssemblyName };
                }
                if ((int)asset.type == 198)
                    foreach (var field in doc.Root.Fields.Where(p => p.Key.EndsWith("Module", StringComparison.Ordinal)))
                        modules.Add(new { field = field.Key,
                            enabled = field.Value.Fields.TryGetValue("enabled", out var enabled) ? (bool?)Convert.ToBoolean(enabled.Scalar) : null,
                            offset = field.Value.Offset, size = field.Value.Length });
                dataFile = "objects/" + assets.Count.ToString("D6") + ".json";
                Save(directory, dataFile, doc.Root.ToJson());
            }
            var paths = logical.TryGetValue(asset.m_PathID, out var candidates)
                ? candidates.Where(r => string.Equals(r.Source, dependencies.GetBundleSource(asset.assetsFile.fileName), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.Type, asset.type.ToString(), StringComparison.OrdinalIgnoreCase))
                    .Select(r => new { path = r.Container, name = r.Name }).Distinct().ToArray() : null;
            assets.Add(new { identity = id, name = asset.Name, classId = (int)asset.type, type = asset.type.ToString(),
                owner = owners.GetValueOrDefault(id), state, reason, schema = schemaFile, data = dataFile, script,
                native = new { cab = asset.assetsFile.fileName, pathId = asset.m_PathID, offset = asset.reader.byteStart, size = asset.byteSize },
                sourceBundle = dependencies.GetBundleSource(asset.assetsFile.fileName), logicalPaths = paths,
                particleModules = modules, references, referenceCoverage = doc == null ? "native-snapshot-only" : "all-typed-pointers" });
        }

        var preview = EiemEffectPreviewWriter.Write(directory, previewAssets);
        Save(directory, "effect.json", new
        {
            format = "EFF_EFFECT_SOURCE", version = 1, prefab = prefab.Container, root = Identity(root),
            vfsFingerprint = fingerprint, baseline = "source/manifest.json",
            hierarchy = hierarchy.Select(go => new
            {
                identity = Identity(go), name = go.m_Name, transform = Identity(go.m_Transform),
                parent = go == root ? null : go.m_Transform.m_Father.TryGet(out var p) ? Identity(p) : null,
                children = go.m_Transform.m_Children.Select(p => { p.TryGet(out var t); return Identity(t); }).ToArray(),
                components = go.m_Components.Select(p => { p.Cast<AssetObject>().TryGet(out var c); return Identity(c); }).ToArray()
            }).ToArray(),
            objects = assets, schemas = schemaFiles, warnings, preview,
            capabilities = new { nativePreservation = true, strictObjectRoundtrip = true,
                nativePackRebuild = false, blenderImport = false, gamePlayback = false },
            notes = new[] { "Modules include disabled slots; absence is not equivalent to disabled.",
                "Curves, gradients, random ranges and managed data retain native field names and numeric modes.",
                "Only parsed objects expose complete typed pointers. Bulk/opaque data remains in the complete dependency snapshots.",
                "Object codec roundtrip does not validate native PFB construction, added components, or game lifecycle." }
        });
    }

    private static AssetObject Resolve(TypeTreeDocument.Value value, SerializedFile owner, IReadOnlyDictionary<string, SerializedFile> files)
    {
        int fileId = Convert.ToInt32(value["m_FileID"].Scalar);
        long pathId = Convert.ToInt64(value["m_PathID"].Scalar);
        if (pathId == 0 || fileId < 0) return null;
        var cab = fileId == 0 ? owner.fileName : fileId <= owner.m_Externals.Count ? owner.m_Externals[fileId - 1].fileName : null;
        return cab != null && files.TryGetValue(cab, out var file) && file.ObjectsDic.TryGetValue(pathId, out var target) ? target : null;
    }

    private static JArray Nodes(TypeTree tree) => tree?.m_Nodes == null ? null : new JArray(tree.m_Nodes.Select(n => new JObject
    {
        ["type"] = n.m_Type, ["name"] = n.m_Name, ["level"] = n.m_Level,
        ["byteSize"] = n.m_ByteSize, ["metaFlag"] = n.m_MetaFlag, ["version"] = n.m_Version,
        ["typeFlags"] = n.m_TypeFlags, ["refTypeHash"] = n.m_RefTypeHash.ToString(System.Globalization.CultureInfo.InvariantCulture)
    }));

    private static void Save(string directory, string relative, object value)
    {
        using var output = File.CreateText(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        using var writer = new JsonTextWriter(output) { Formatting = Formatting.Indented };
        JsonSerializer.CreateDefault(new JsonSerializerSettings { FloatFormatHandling = FloatFormatHandling.String }).Serialize(writer, value);
    }
}
