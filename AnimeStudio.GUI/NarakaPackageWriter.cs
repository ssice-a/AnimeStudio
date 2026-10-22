using AnimeStudio;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AssetObject = AnimeStudio.Object;

namespace AnimeStudio.GUI;

internal sealed class NarakaPackageWriter
{
    private readonly NarakaPrefabLoader loader;
    private readonly Dictionary<string, string> logicalPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<Transform, int> nodeIds = new();
    private readonly Dictionary<string, string> payloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> materials = new();
    private readonly Dictionary<string, object> textures = new();
    private readonly List<object> nodes = new();
    private readonly List<object> renderers = new();
    private readonly List<object> lodGroups = new();
    private readonly Dictionary<string, object> shaderProperties = new();
    private readonly List<string> warnings = new();
    private readonly NarakaModelBones modelBones;
    private string directory;

    internal NarakaPackageWriter(NarakaPrefabLoader loader)
    {
        this.loader = loader;
        modelBones = new NarakaModelBones(loader);
        foreach (var file in loader.Manager.assetsFileList)
        foreach (var info in file.m_Objects.Where(o => o.classID == (int)ClassIDType.AssetBundle))
        {
            var bundlePointer = new PPtr<AssetBundle>(0, info.m_PathID, file);
            foreach (var entry in loader.Require(bundlePointer, file).m_Container)
            {
                var reference = entry.Value.asset;
                var cab = reference.m_FileID == 0 ? file.fileName : file.m_Externals[reference.m_FileID - 1].fileName;
                logicalPaths.TryAdd(cab.ToLowerInvariant() + ":" + reference.m_PathID, entry.Key);
            }
        }
    }

    internal static string Identity(AssetObject asset) => asset.assetsFile.fileName.ToLowerInvariant() + ":" + asset.m_PathID;
    private string Logical(AssetObject asset) => logicalPaths.GetValueOrDefault(Identity(asset), "");
    private string FileName(string folder, AssetObject asset, string extension) =>
        EiemPackageIdentity.PayloadFileName(folder, asset.Name.Length > 0 ? asset.Name : asset.type.ToString(),
            asset.assetsFile.fileName, asset.m_PathID, extension, payloads).Replace('\\', '/');

    internal void Write(string container, GameObject root, string output)
    {
        EiemPackagePublication.Publish(output, staging =>
        {
            directory = staging;
            foreach (var folder in new[] { "meshes", "textures", "materials", "source" }) Directory.CreateDirectory(Path.Combine(staging, folder));
            Collect(root.m_Transform, -1, "");
            foreach (var gameObject in EndfieldPrefabDocument.GetHierarchy(root))
            foreach (var pointer in gameObject.m_Components)
                if (loader.Resolve(pointer, gameObject.assetsFile) is Renderer renderer && renderer is MeshRenderer or SkinnedMeshRenderer)
                    WriteRenderer(gameObject, renderer);
            if (renderers.Count == 0) throw new InvalidDataException("The Prefab has no mesh renderers");
            var manifest = new
            {
                format = "NARAKA_PREFAB_PACKAGE", version = 1, coordinate = "unity-y-up-left-handed",
                prefab = container, source = Identity(root), name = root.Name,
                nodes, renderers, lodGroups, materials, textures, warnings,
                scope = "All MeshRenderer/SkinnedMeshRenderer instances beneath this Prefab, including inactive nodes, plus verified original FBX skeleton ancestors. Runtime-created renderers are outside this graph.",
            };
            var json = JsonConvert.SerializeObject(manifest, Formatting.Indented);
            File.WriteAllText(Path.Combine(staging, "naraka.json"), json);
            // Reuse the existing staged publication/backup transaction.
            File.WriteAllText(Path.Combine(staging, "source", "manifest.json"), json);
            var receipts = Directory.GetFiles(staging, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(staging, f).Replace('\\', '/'),
                    f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
            File.WriteAllText(Path.Combine(staging, "checksums.json"), JsonConvert.SerializeObject(receipts, Formatting.Indented));
        });
    }

    private void Collect(Transform transform, int parent, string path)
    {
        if (nodeIds.ContainsKey(transform)) throw new InvalidDataException("Repeated/cyclic Transform in Prefab");
        var gameObject = loader.Require(transform.m_GameObject, transform.assetsFile);
        var id = nodeIds.Count;
        nodeIds.Add(transform, id);
        var fields = NarakaPrefabLoader.Fields(gameObject);
        nodes.Add(new { id, source = Identity(transform), name = gameObject.Name, path, parent,
            position = V(transform.m_LocalPosition), rotation = Q(transform.m_LocalRotation), scale = V(transform.m_LocalScale),
            active = fields == null || !fields.Contains("m_IsActive") || Convert.ToBoolean(fields["m_IsActive"]) });
        foreach (var pointer in transform.m_Children)
        {
            var child = loader.Require(pointer, transform.assetsFile);
            var childName = loader.Require(child.m_GameObject, child.assetsFile).Name;
            Collect(child, id, path.Length == 0 ? childName : path + "/" + childName);
        }
        foreach (var pointer in gameObject.m_Components)
        {
            var component = loader.Resolve(pointer, gameObject.assetsFile);
            if (component?.type != ClassIDType.LODGroup) continue;
            var data = NarakaPrefabLoader.Fields(component);
            if (data?["m_LODs"] is not IEnumerable levels) continue;
            var lods = new List<object>();
            foreach (OrderedDictionary level in levels)
            {
                var ids = new List<string>();
                foreach (OrderedDictionary entry in (IEnumerable)level["renderers"])
                {
                    var reference = (OrderedDictionary)entry["renderer"];
                    var linked = loader.Resolve(new PPtr<AssetObject>(Convert.ToInt32(reference["m_FileID"]),
                        Convert.ToInt64(reference["m_PathID"]), component.assetsFile), component.assetsFile);
                    if (linked != null) ids.Add(Identity(linked));
                }
                lods.Add(new { screenHeight = level["screenRelativeHeight"], renderers = ids });
            }
            lodGroups.Add(new { node = id, levels = lods });
        }
    }

    private void WriteRenderer(GameObject gameObject, Renderer renderer)
    {
        var skin = renderer as SkinnedMeshRenderer;
        var mesh = loader.RendererMesh(gameObject, renderer);
        if (mesh.m_VertexCount == 0) throw new InvalidDataException("Empty Mesh: " + Identity(mesh));
        var palette = new List<int>();
        object boneSource = null;
        foreach (var pointer in skin?.m_Bones ?? Enumerable.Empty<PPtr<Transform>>())
        {
            var bone = loader.Require(pointer, renderer.assetsFile);
            var id = CollectBone(bone, null, null, new HashSet<Transform>());
            palette.Add(id);
        }
        if (skin != null && palette.Count == 0 && mesh.m_BindPose?.Length > 0)
        {
            var source = modelBones.Resolve(mesh);
            if (source != null)
            {
                foreach (var bone in source.Bones)
                    palette.Add(CollectBone(bone.Transform, source.Container, source.Paths, new HashSet<Transform>()));
                boneSource = new { method = "fbx-root-relative-path-crc32", container = source.Container,
                    root = Identity(source.Root), mesh = Identity(mesh) };
            }
        }
        if (palette.Count > 0 && mesh.m_Skin?.Any(s => s.weight.Any(w => w > 0)) == true && palette.Count != mesh.m_BindPose?.Length)
            throw new InvalidDataException($"Bone palette/bindpose mismatch on {gameObject.Name}: bones={palette.Count}, bindposes={mesh.m_BindPose?.Length}, hashes={mesh.m_BoneNameHashes?.Length}, renderer={Identity(renderer)}, mesh={Identity(mesh)}");
        if (skin != null && palette.Count == 0 && mesh.m_BindPose?.Length > 0)
            warnings.Add($"{gameObject.Name}: No unique original FBX skeleton matches the exact source mesh and all bone hashes. Bindposes and original hashes retained; bone names/parents unresolved.");
        var meshPath = EiemPackageIdentity.PayloadFileName("meshes", gameObject.Name,
            renderer.assetsFile.fileName, renderer.m_PathID, ".mesh", payloads).Replace('\\', '/');
        EiemPackageWriter.WriteMeshPayload(Path.Combine(directory, meshPath), mesh,
            Logical(mesh).Length > 0 ? Logical(mesh) : Identity(mesh), palette.Select(n => n.ToString()).ToArray());
        if (loader.RuntimeMeshSources.TryGetValue(mesh, out var runtimeSource))
        {
            File.WriteAllText(Path.Combine(directory, meshPath + ".runtime.json"),
                JsonConvert.SerializeObject(NarakaPrefabLoader.Fields(runtimeSource), Formatting.Indented));
            warnings.Add($"{gameObject.Name}: decoded exact RendererAssistant.avatarMeshAsset {Identity(runtimeSource)}; runtime avatar/hair deformation fields retained in {meshPath}.runtime.json, not executed in Blender.");
        }
        EiemPackageValidator.ValidateMesh(Path.Combine(directory, meshPath));
        var slots = new List<string>();
        foreach (var pointer in renderer.m_Materials)
        {
            var material = loader.Resolve(pointer, renderer.assetsFile);
            slots.Add(material == null ? null : WriteMaterial(material));
        }
        var data = NarakaPrefabLoader.Fields(renderer);
        var enabled = data == null || !data.Contains("m_Enabled") || Convert.ToBoolean(data["m_Enabled"]);
        renderers.Add(new { id = Identity(renderer), name = gameObject.Name, node = nodeIds[gameObject.m_Transform],
            mesh = meshPath, meshSource = Identity(mesh), meshName = mesh.Name, enabled, skinned = skin != null,
            bones = palette, boneSource, materialSlots = slots, blendShapeWeights = skin?.m_BlendShapeWeights ?? Array.Empty<float>(),
            submeshSlots = Enumerable.Range(0, slots.Count).Select(i => renderer.m_SubsetIndices?.Length > i
                ? checked((int)renderer.m_SubsetIndices[i]) : renderer.m_StaticBatchInfo?.subMeshCount > i
                    ? renderer.m_StaticBatchInfo.firstSubMesh + i : i).ToArray() });
    }

    private int CollectBone(Transform transform, string container, IReadOnlyDictionary<Transform, string> paths, HashSet<Transform> visiting)
    {
        if (nodeIds.TryGetValue(transform, out var existing)) return existing;
        if (!visiting.Add(transform)) throw new InvalidDataException("Cyclic source bone hierarchy");
        var parent = transform.m_Father.IsNull ? -1 : CollectBone(loader.Require(transform.m_Father, transform.assetsFile), container, paths, visiting);
        var go = loader.Require(transform.m_GameObject, transform.assetsFile);
        var id = nodeIds.Count;
        nodeIds.Add(transform, id);
        var fields = NarakaPrefabLoader.Fields(go);
        nodes.Add(new { id, source = Identity(transform), name = go.Name,
            path = paths != null && paths.TryGetValue(transform, out var path) ? path : "", parent,
            position = V(transform.m_LocalPosition), rotation = Q(transform.m_LocalRotation), scale = V(transform.m_LocalScale),
            active = fields == null || !fields.Contains("m_IsActive") || Convert.ToBoolean(fields["m_IsActive"]),
            role = "source-skeleton", modelContainer = container });
        visiting.Remove(transform);
        return id;
    }

    private string WriteMaterial(Material material)
    {
        var id = Identity(material);
        if (materials.ContainsKey(id)) return id;
        var shader = loader.Require(material.m_Shader.Cast<AssetObject>(), material.assetsFile);
        var shaderPath = Logical(shader);
        if (shaderPath.Length == 0) warnings.Add("Shader logical path is unavailable: " + Identity(shader));
        var bindings = new Dictionary<string, object>();
        foreach (var pair in material.m_SavedProperties.m_TexEnvs)
        {
            var texture = loader.Resolve(pair.Value.m_Texture.Cast<AssetObject>(), material.assetsFile);
            string textureId = null;
            if (texture is Texture2D pixels) textureId = WriteTexture(pixels);
            else if (texture?.type == ClassIDType.Texture2DArray)
            {
                try { textureId = WriteTextureArray(texture); }
                catch (NotSupportedException error) { warnings.Add($"{material.Name}:{pair.Key}: {error.Message}; exact array reference retained without decoded pixels"); }
            }
            else if (texture != null) warnings.Add($"Unsupported texture type {texture.type} in {material.Name}:{pair.Key}");
            bindings[pair.Key] = new { texture = textureId, source = texture == null ? null : Identity(texture),
                scale = new[] { pair.Value.m_Scale.X, pair.Value.m_Scale.Y },
                offset = new[] { pair.Value.m_Offset.X, pair.Value.m_Offset.Y } };
        }
        var fields = NarakaPrefabLoader.Fields(material);
        if (!shaderProperties.TryGetValue(Identity(shader), out var declarations))
        {
            shader.reader.Reset();
            _ = shader.reader.ReadAlignedString(); // NamedObject header in this Windows player.
            var props = new SerializedProperties(shader.reader);
            declarations = props.m_Props.ToDictionary(p => p.m_Name, p => new
            {
                description = p.m_Description, type = p.m_Type.ToString(), flags = (int)p.m_Flags,
                defaults = p.m_DefValue, attributes = p.m_Attributes,
            });
            shaderProperties.Add(Identity(shader), declarations);
        }
        var data = new { id, name = material.Name, logicalPath = Logical(material), shader = shaderPath, shaderProperties = declarations,
            shaderSource = Identity(shader), textures = bindings,
            floats = material.m_SavedProperties.m_Floats.ToDictionary(p => p.Key, p => p.Value),
            ints = material.m_SavedProperties.m_Ints?.ToDictionary(p => p.Key, p => p.Value) ?? new Dictionary<string, int>(),
            vectors = material.m_SavedProperties.m_Colors.ToDictionary(p => p.Key, p => new[] { p.Value.R, p.Value.G, p.Value.B, p.Value.A }),
            keywords = fields?["m_ShaderKeywords"]?.ToString() ?? "", renderQueue = fields?["m_CustomRenderQueue"],
            disabledPasses = fields?["disabledShaderPasses"], disabledPassNames = fields?["disabledShaderPassNames"] };
        materials[id] = data;
        File.WriteAllText(Path.Combine(directory, FileName("materials", material, ".json")), JsonConvert.SerializeObject(data, Formatting.Indented));
        return id;
    }

    private string WriteTexture(Texture2D texture)
    {
        var id = Identity(texture);
        if (textures.ContainsKey(id)) return id;
        var path = FileName("textures", texture, ".png");
        using var image = texture.ConvertToImage(true) ?? throw new InvalidDataException("No pixels for " + id);
        using (var stream = File.Create(Path.Combine(directory, path))) image.WriteToStream(stream, ImageFormat.Png);
        textures[id] = new { id, name = texture.Name, logicalPath = Logical(texture), path,
            width = texture.m_Width, height = texture.m_Height, linear = texture.m_ColorSpace == 0,
            storageFormat = texture.m_TextureFormat.ToString(),
            wrap = texture.m_TextureSettings.m_WrapMode, filter = texture.m_TextureSettings.m_FilterMode };
        return id;
    }

    private string WriteTextureArray(AssetObject texture)
    {
        var id = Identity(texture);
        if (textures.ContainsKey(id)) return id;
        var array = NarakaTextureArray.Decode(texture);
        var slices = new List<string>();
        var stem = FileName("textures", texture, ".array");
        for (int i = 0; i < array.Depth; i++)
        {
            var path = stem + "-" + i + ".png";
            using var original = ImageExtensions.CreateBitmapFromBgra(array.Slices[i], array.Width, array.Height);
            using var image = original.FlipVertical();
            using var stream = File.Create(Path.Combine(directory, path));
            image.WriteToStream(stream, ImageFormat.Png);
            slices.Add(path);
        }
        textures[id] = new { id, name = NarakaPrefabLoader.Fields(texture)?["m_Name"]?.ToString() ?? texture.Name, logicalPath = Logical(texture), path = slices[0],
            dimension = "2DArray", slices, width = array.Width, height = array.Height, depth = array.Depth,
            linear = array.ColorSpace == 0, storageFormat = array.Format.ToString(), wrap = array.Wrap, filter = array.Filter };
        return id;
    }

    private static float[] V(Vector3 v) => new[] { v.X, v.Y, v.Z };
    private static float[] Q(Quaternion q) => new[] { q.X, q.Y, q.Z, q.W };
}
