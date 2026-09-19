using AnimeStudio;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio.GUI
{
    /// <summary>
    /// Writes the common EIEM package consumed by the future Blender add-on and
    /// runtime loader. The binary files preserve parsed Unity data directly;
    /// they are not an FBX/OBJ-derived approximation.
    /// </summary>
    internal sealed class EiemPackageWriter
    {
        private const int MeshVersion = 3;
        private const int SkeletonVersion = 1;
        private readonly IReadOnlyList<VirtualAssetRecord> records;
        private readonly EndfieldBundleDependencyIndex dependencies;
        private readonly string vfsFingerprint;
        private readonly Dictionary<string, List<VirtualAssetRecord>> assetsByIdentity =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Mesh, string> meshes = new();
        private readonly HashSet<Mesh> renderedMeshes = new();
        private readonly Dictionary<Mesh, string[]> meshBonePaths = new();
        private readonly Dictionary<Material, string> materials = new();
        private readonly Dictionary<Texture2D, string> textures = new();
        private string skeleton;
        private readonly List<string> errors = new();
        private readonly List<string> ini = new();
        private readonly List<string> resourceIni = new();
        private readonly List<string> renderIni = new();
        private readonly List<string> renderSections = new();
        private readonly List<string> binaryFiles = new();
        private string rootDirectory;
        private GameObject root;
        private Dictionary<Transform, string> transformPaths;
        private HashSet<Transform> skeletonTransforms;

        public EiemPackageWriter(IReadOnlyList<VirtualAssetRecord> records,
            EndfieldBundleDependencyIndex dependencies)
            : this(records, dependencies, null)
        {
        }

        public EiemPackageWriter(IReadOnlyList<VirtualAssetRecord> records,
            EndfieldBundleDependencyIndex dependencies, string vfsFingerprint)
        {
            this.records = records ?? Array.Empty<VirtualAssetRecord>();
            this.dependencies = dependencies;
            this.vfsFingerprint = vfsFingerprint;
            foreach (var record in this.records)
            {
                var key = AssetKey(record.Type, record.PathId);
                if (!assetsByIdentity.TryGetValue(key, out var assets))
                {
                    assets = new List<VirtualAssetRecord>();
                    assetsByIdentity.Add(key, assets);
                }
                assets.Add(record);
            }
        }

        public bool Write(VirtualAssetFile prefab, GameObject prefabRoot, string outputDirectory)
        {
            if (prefab == null || prefabRoot == null || string.IsNullOrWhiteSpace(outputDirectory))
                return false;

            meshes.Clear();
            renderedMeshes.Clear();
            meshBonePaths.Clear();
            materials.Clear();
            textures.Clear();
            errors.Clear();
            ini.Clear();
            resourceIni.Clear();
            renderIni.Clear();
            renderSections.Clear();
            binaryFiles.Clear();
            skeleton = null;

            rootDirectory = outputDirectory;
            root = prefabRoot;
            Directory.CreateDirectory(rootDirectory);
            var physicsDirectory = Path.Combine(rootDirectory, "physics");
            if (Directory.Exists(physicsDirectory))
                Directory.Delete(physicsDirectory, recursive: true);
            Directory.CreateDirectory(Path.Combine(rootDirectory, "meshes"));
            Directory.CreateDirectory(Path.Combine(rootDirectory, "skeletons"));
            Directory.CreateDirectory(Path.Combine(rootDirectory, "materials"));
            Directory.CreateDirectory(Path.Combine(rootDirectory, "textures"));
            transformPaths = BuildTransformPaths(root);

            var hierarchy = EndfieldPrefabDocument.GetHierarchy(root).ToArray();
            skeletonTransforms = BuildSkeletonTransforms(hierarchy);

            var renderIndex = 0;
            foreach (var gameObject in hierarchy)
            {
                if (gameObject.m_SkinnedMeshRenderer != null)
                    WriteRenderer(gameObject, gameObject.m_SkinnedMeshRenderer, renderIndex++);
                else if (gameObject.m_MeshRenderer != null && gameObject.m_MeshFilter?.m_Mesh != null)
                    WriteRenderer(gameObject, gameObject.m_MeshRenderer, renderIndex++);
            }

            if (renderIndex == 0)
            {
                errors.Add("The selected Prefab has no readable MeshRenderer or SkinnedMeshRenderer.");
                WriteReport();
                return false;
            }

            var prefabPath = (prefab.Container ?? string.Empty).Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(prefabPath))
                errors.Add("The selected Prefab has no logical container path.");

            try
            {
                var source = prefab.Records.Select(record => record.Source)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (dependencies != null && !string.IsNullOrWhiteSpace(vfsFingerprint) &&
                    !string.IsNullOrWhiteSpace(source))
                {
                    var closure = dependencies.ResolveBundleClosure(source);
                    EiemPhysicsSourceWriter.Write(physicsDirectory, root,
                        prefabPath, vfsFingerprint, source, closure,
                        root.assetsFile.assetsManager.assetsFileList, supportedOnly: true);
                }
            }
            catch (Exception ex)
            {
                errors.Add("Native Physics source export failed: " + ex.Message);
            }

            ini.Add("; Generated by EIEM. Blender may rewrite this file; source paths are game logical paths.");
            ini.Add("; Prefab is the identity/lifecycle anchor. Render sections are local actions inside it.");
            ini.Add(string.Empty);
            ini.AddRange(resourceIni);
            var prefabSection = UniqueName("Prefab", root.Name, 0);
            ini.Add($"[{prefabSection}]");
            ini.Add($"path={prefabPath}");
            for (var index = 0; index < renderSections.Count; index++)
                ini.Add($"render.{index}={renderSections[index]}");
            ini.Add(string.Empty);
            ini.AddRange(renderIni);

            try
            {
                foreach (var file in binaryFiles)
                {
                    if (file.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase))
                        EiemPackageValidator.ValidateMesh(file);
                    else
                        EiemPackageValidator.ValidateSkeleton(file);
                }
            }
            catch (Exception ex)
            {
                errors.Add("EIEM binary validation failed: " + ex.Message);
            }

            File.WriteAllLines(Path.Combine(rootDirectory, "mod.ini"), ini, new UTF8Encoding(false));
            WriteReport();
            return errors.Count == 0;
        }

        private void WriteRenderer(GameObject gameObject, Renderer renderer, int index)
        {
            var mesh = GetMesh(renderer);
            if (mesh == null || mesh.m_VertexCount <= 0)
            {
                errors.Add($"Unreadable mesh on renderer '{GetTransformPath(gameObject.m_Transform)}'.");
                return;
            }

            // A Mesh resource may be consumed by the visible renderer,
            // shadow proxy and other render instances. Export one resource
            // action, selected by Mesh asset identity inside this Prefab;
            // the runtime applies it to every matching consumer.
            if (!renderedMeshes.Add(mesh))
                return;

            var meshName = EnsureMesh(mesh, renderer as SkinnedMeshRenderer);
            string skeletonName = null;
            if (renderer is SkinnedMeshRenderer)
                skeletonName = EnsureSkeleton();

            var renderName = UniqueName("Render", gameObject.m_Name, index);
            renderSections.Add(renderName);
            renderIni.Add($"[{renderName}]");
            renderIni.Add($"asset={mesh.Name}");
            renderIni.Add($"mesh={meshName}");
            if (!string.IsNullOrEmpty(skeletonName))
                renderIni.Add($"skeleton={skeletonName}");

            var materialPointers = renderer.m_Materials ?? new List<PPtr<Material>>();
            for (var slot = 0; slot < materialPointers.Count; slot++)
            {
                var subMesh = GetRendererSubMesh(renderer, slot);
                if (subMesh >= 0)
                    renderIni.Add($"submesh.{subMesh}={slot}");
                if (materialPointers[slot].TryGet(out var material))
                    renderIni.Add($"material.{slot}={EnsureMaterial(material)}");
                else
                    errors.Add($"Unresolved material slot {slot} on renderer '{GetTransformPath(gameObject.m_Transform)}'.");
            }
            renderIni.Add(string.Empty);
        }

        private string EnsureMesh(Mesh mesh, SkinnedMeshRenderer renderer)
        {
            var currentBonePaths = GetRendererBonePaths(renderer);
            if (meshes.TryGetValue(mesh, out var known))
            {
                if (meshBonePaths.TryGetValue(mesh, out var previous) &&
                    !previous.SequenceEqual(currentBonePaths, StringComparer.Ordinal))
                    errors.Add($"Mesh '{mesh.Name}' is bound to two different skeleton palettes in the selected Prefab.");
                return known;
            }

            var section = UniqueName("Mesh", mesh.Name, meshes.Count);
            var fileName = UniqueFileName("meshes", mesh.Name, mesh.m_PathID, ".mesh");
            var source = ResolveLogicalPath(mesh, "Mesh");
            if (string.IsNullOrEmpty(source))
                errors.Add($"No unambiguous logical Mesh path for '{mesh.Name}' (PathID {mesh.m_PathID}).");
            WriteMesh(Path.Combine(rootDirectory, fileName), mesh, source, currentBonePaths);
            meshes.Add(mesh, section);
            meshBonePaths.Add(mesh, currentBonePaths);
            
            resourceIni.Add($"[{section}]");
            resourceIni.Add($"path={fileName.Replace('\\', '/')}");
            resourceIni.Add("source=" + (source ?? string.Empty));
            resourceIni.Add("asset=" + (mesh.Name ?? string.Empty));
            resourceIni.Add(string.Empty);
            return section;
        }

        private string EnsureSkeleton()
        {
            if (!string.IsNullOrEmpty(skeleton))
                return skeleton;

            var section = UniqueName("Skeleton", root.Name, 0);
            var fileName = UniqueFileName("skeletons", root.Name, root.m_PathID, ".skeleton");
            WriteSkeleton(Path.Combine(rootDirectory, fileName));
            skeleton = section;
            resourceIni.Add($"[{section}]");
            resourceIni.Add($"path={fileName.Replace('\\', '/')}");
            resourceIni.Add(string.Empty);
            return section;
        }

        private string[] GetRendererBonePaths(SkinnedMeshRenderer renderer)
        {
            if (renderer == null)
                return Array.Empty<string>();
            var values = new List<string>();
            foreach (var pointer in renderer.m_Bones ?? Enumerable.Empty<PPtr<Transform>>())
            {
                if (pointer.TryGet(out var bone))
                    values.Add(GetTransformPath(bone));
                else
                {
                    values.Add(string.Empty);
                    errors.Add($"Unresolved bone in renderer '{renderer.Name}'.");
                }
            }
            return values.ToArray();
        }

        private string EnsureMaterial(Material material)
        {
            if (materials.TryGetValue(material, out var known))
                return known;

            var section = UniqueName("Material", material.Name, materials.Count);
            var fileName = UniqueFileName("materials", material.Name, material.m_PathID, ".mat");
            var source = ResolveLogicalPath(material, "Material");
            if (string.IsNullOrEmpty(source))
                errors.Add($"No unambiguous logical Material path for '{material.Name}' (PathID {material.m_PathID}).");
            WriteMaterial(Path.Combine(rootDirectory, fileName), material, source);
            materials.Add(material, section);
            resourceIni.AddRange(new[]
            {
                $"[{section}]",
                $"path={fileName.Replace('\\', '/')}",
                string.Empty,
            });
            return section;
        }

        private string EnsureTexture(Texture2D texture)
        {
            if (textures.TryGetValue(texture, out var known))
                return known;

            var section = UniqueName("Texture", texture.Name, textures.Count);
            var fileName = UniqueFileName("textures", texture.Name, texture.m_PathID, ".png");
            var source = ResolveLogicalPath(texture, "Texture2D") ?? ResolveLogicalPath(texture, "Texture");
            var originalName = texture.Name ?? string.Empty;
            try
            {
                using var image = texture.ConvertToImage(true);
                if (image == null)
                    throw new InvalidDataException("Texture pixels are unavailable.");
                using var stream = File.Create(Path.Combine(rootDirectory, fileName));
                image.WriteToStream(stream, ImageFormat.Png);
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to export PNG texture '{texture.Name}': {ex.Message}");
            }

            textures.Add(texture, section);
            var declaration = new List<string>
            {
                $"[{section}]",
                $"path={fileName.Replace('\\', '/')}",
                "source=" + (source ?? string.Empty),
            };
            declaration.Add("name=" + originalName);
            declaration.Add("linear=" + (texture.m_ColorSpace == 0 ? "true" : "false"));
            declaration.Add("mipmaps=" + ((texture.m_MipCount > 1 || texture.m_MipMap) ? "true" : "false"));
            declaration.Add("filter=" + (texture.m_TextureSettings?.m_FilterMode ?? 1).ToString(CultureInfo.InvariantCulture));
            declaration.Add("wrap=" + (texture.m_TextureSettings?.m_WrapMode ?? 0).ToString(CultureInfo.InvariantCulture));
            declaration.Add("aniso=" + (texture.m_TextureSettings?.m_Aniso ?? 1).ToString(CultureInfo.InvariantCulture));
            declaration.Add("mip_bias=" + Format(texture.m_TextureSettings?.m_MipBias ?? 0f));
            declaration.Add(string.Empty);
            resourceIni.AddRange(declaration);
            return section;
        }

        private void WriteMesh(string path, Mesh mesh, string source, IReadOnlyList<string> bonePaths)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
            writer.Write(Encoding.ASCII.GetBytes("EIEMESH\0"));
            writer.Write(MeshVersion);
            // Keep all serialized mesh vectors and bindposes in their source
            // basis. Blender owns the reversible mesh conversion; do not infer
            // an extra vertex rotation from the prefab Transform hierarchy.
            writer.Write("unity-y-up-left-handed");
            writer.Write(source ?? string.Empty);
            writer.Write(mesh.Name ?? string.Empty);
            writer.Write(mesh.m_VertexCount);
            WriteFloatArray(writer, mesh.m_Vertices);
            WriteFloatArray(writer, mesh.m_Normals);
            WriteFloatArray(writer, mesh.m_Tangents);
            WriteFloatArray(writer, mesh.m_Colors);
            for (var channel = 0; channel < 8; channel++)
                WriteFloatArray(writer, mesh.GetUV(channel));

            writer.Write(mesh.m_Indices?.Count ?? 0);
            foreach (var value in mesh.m_Indices ?? Enumerable.Empty<uint>())
                writer.Write(value);

            writer.Write(mesh.m_SubMeshes?.Count ?? 0);
            uint outputIndexStart = 0;
            foreach (var subMesh in mesh.m_SubMeshes ?? Enumerable.Empty<SubMesh>())
            {
                // Mesh.m_Indices is AnimeStudio's normalized triangle list,
                // not Unity's original byte-addressed index buffer. Store the
                // matching range in that normalized list so the reader can
                // reconstruct each material slot without reusing firstByte.
                writer.Write((int)subMesh.topology);
                writer.Write(outputIndexStart);
                writer.Write(subMesh.indexCount);
                writer.Write(subMesh.baseVertex);
                writer.Write(subMesh.firstVertex);
                writer.Write(subMesh.vertexCount);
                outputIndexStart += subMesh.indexCount;
            }

            writer.Write(mesh.m_Skin?.Count ?? 0);
            foreach (var weight in mesh.m_Skin ?? Enumerable.Empty<BoneWeights4>())
            {
                for (var i = 0; i < 4; i++) writer.Write(weight.weight[i]);
                for (var i = 0; i < 4; i++) writer.Write(weight.boneIndex[i]);
            }

            writer.Write(mesh.m_BindPose?.Length ?? 0);
            foreach (var bindPose in mesh.m_BindPose ?? Array.Empty<Matrix4x4>())
                WriteMatrix(writer, bindPose);
            writer.Write(mesh.m_BoneNameHashes?.Length ?? 0);
            foreach (var hash in mesh.m_BoneNameHashes ?? Array.Empty<uint>())
                writer.Write(hash);
            writer.Write(bonePaths?.Count ?? 0);
            foreach (var bonePath in bonePaths ?? Array.Empty<string>())
                writer.Write(bonePath ?? string.Empty);
            WriteBlendShapes(writer, mesh.m_Shapes);
            binaryFiles.Add(path);
        }

        private void WriteSkeleton(string path)
        {
            var nodes = new List<Transform>();
            CollectTransforms(root.m_Transform, nodes, new HashSet<Transform>(), skeletonTransforms);
            var nodeIndex = new Dictionary<Transform, int>();
            for (var i = 0; i < nodes.Count; i++)
                nodeIndex[nodes[i]] = i;

            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
            writer.Write(Encoding.ASCII.GetBytes("EIESKEL\0"));
            writer.Write(SkeletonVersion);
            writer.Write("unity-y-up-left-handed");
            writer.Write(nodes.Count);
            foreach (var node in nodes)
            {
                writer.Write(GetTransformPath(node));
                var parent = -1;
                if (node.m_Father.TryGet(out var father) && nodeIndex.TryGetValue(father, out var found))
                    parent = found;
                writer.Write(parent);
                WriteVector3(writer, node.m_LocalPosition);
                WriteQuaternion(writer, node.m_LocalRotation);
                WriteVector3(writer, node.m_LocalScale);
            }

            // One Prefab hierarchy is one skeleton resource. Compact renderer
            // palettes belong to Mesh bindings and are written as bone paths
            // in EIEMESH v3 rather than producing one skeleton per Mesh.
            writer.Write(0);
            writer.Write(-1);
            binaryFiles.Add(path);
        }

        private void WriteMaterial(string path, Material material, string source)
        {
            var lines = new List<string>
            {
                "format=EIEMMAT",
                "version=1",
                "source=" + (source ?? string.Empty),
                "name=" + (material.Name ?? string.Empty),
            };
            if (material.m_Shader.TryGet(out var shader))
                lines.Add("shader=" + shader.Name);

            var properties = material.m_SavedProperties;
            if (properties != null)
            {
                foreach (var pair in properties.m_TexEnvs ?? Enumerable.Empty<KeyValuePair<string, UnityTexEnv>>())
                {
                    var value = pair.Value;
                    if (value?.m_Texture.TryGet(out var texture) == true && texture is Texture2D texture2D)
                    {
                        var textureSection = EnsureTexture(texture2D);
                        lines.Add($"texture.{pair.Key}={textureSection}");
                    }
                    lines.Add($"texture_scale.{pair.Key}={Format(value?.m_Scale.X ?? 1f)},{Format(value?.m_Scale.Y ?? 1f)}");
                    lines.Add($"texture_offset.{pair.Key}={Format(value?.m_Offset.X ?? 0f)},{Format(value?.m_Offset.Y ?? 0f)}");
                }
                foreach (var pair in properties.m_Ints ?? Enumerable.Empty<KeyValuePair<string, int>>())
                    lines.Add($"int.{pair.Key}={pair.Value}");
                foreach (var pair in properties.m_Floats ?? Enumerable.Empty<KeyValuePair<string, float>>())
                    lines.Add($"float.{pair.Key}={Format(pair.Value)}");
                foreach (var pair in properties.m_Colors ?? Enumerable.Empty<KeyValuePair<string, Color>>())
                    lines.Add($"value4.{pair.Key}={Format(pair.Value.R)},{Format(pair.Value.G)},{Format(pair.Value.B)},{Format(pair.Value.A)}");
            }
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }

        private static void WriteBlendShapes(BinaryWriter writer, BlendShapeData shapes)
        {
            writer.Write(shapes?.vertices?.Count ?? 0);
            foreach (var vertex in shapes?.vertices ?? Enumerable.Empty<BlendShapeVertex>())
            {
                writer.Write(vertex.index);
                WriteVector3(writer, vertex.vertex);
                WriteVector3(writer, vertex.normal);
                WriteVector3(writer, vertex.tangent);
            }
            writer.Write(shapes?.shapes?.Count ?? 0);
            foreach (var shape in shapes?.shapes ?? Enumerable.Empty<MeshBlendShape>())
            {
                writer.Write(shape.name ?? string.Empty);
                writer.Write(shape.firstVertex);
                writer.Write(shape.vertexCount);
                writer.Write(shape.hasNormals);
                writer.Write(shape.hasTangents);
                writer.Write(shape.hasAdditionalNormals);
            }
            writer.Write(shapes?.channels?.Count ?? 0);
            foreach (var channel in shapes?.channels ?? Enumerable.Empty<MeshBlendShapeChannel>())
            {
                writer.Write(channel.name ?? string.Empty);
                writer.Write(channel.nameHash);
                writer.Write(channel.frameIndex);
                writer.Write(channel.frameCount);
            }
            WriteFloatArray(writer, shapes?.fullWeights);
            writer.Write(shapes?.additionalVertices?.Count ?? 0);
            foreach (var vertex in shapes?.additionalVertices ?? Enumerable.Empty<BlendShapeAdditionalVertex>())
                WriteVector3(writer, vertex.additionalNormal);
        }

        private string ResolveLogicalPath(AnimeStudio.Object asset, string type)
        {
            if (asset == null) return null;
            if (!assetsByIdentity.TryGetValue(AssetKey(type, asset.m_PathID), out var candidates))
                return null;

            var cabName = Path.GetFileName(asset.assetsFile?.fileName ?? string.Empty);
            var cabMatches = candidates.Where(candidate =>
                    dependencies?.GetCabNames(candidate.Source)
                        .Contains(cabName, StringComparer.OrdinalIgnoreCase) == true)
                .ToArray();
            if (cabMatches.Length == 1)
                return cabMatches[0].Container;

            var namedMatches = candidates.Where(candidate =>
                    string.Equals(candidate.Name, asset.Name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return namedMatches.Length == 1 ? namedMatches[0].Container : null;
        }

        private static string AssetKey(string type, long pathId) =>
            $"{type}\u001f{pathId}";

        private Dictionary<Transform, string> BuildTransformPaths(GameObject prefabRoot)
        {
            var paths = new Dictionary<Transform, string>();
            void Visit(Transform transform, string path)
            {
                if (transform == null || paths.ContainsKey(transform)) return;
                paths.Add(transform, path);
                foreach (var child in transform.m_Children ?? Enumerable.Empty<PPtr<Transform>>())
                {
                    if (!child.TryGet(out var childTransform) || !childTransform.m_GameObject.TryGet(out var childObject))
                        continue;
                    var childPath = string.IsNullOrEmpty(path) ? childObject.m_Name : path + "/" + childObject.m_Name;
                    Visit(childTransform, childPath);
                }
            }
            Visit(prefabRoot.m_Transform, string.Empty);
            return paths;
        }

        private string GetTransformPath(Transform transform)
        {
            if (transform != null && transformPaths.TryGetValue(transform, out var path))
                return path;
            return transform?.Name ?? string.Empty;
        }

        private HashSet<Transform> BuildSkeletonTransforms(IEnumerable<GameObject> hierarchy)
        {
            var result = new HashSet<Transform>();
            foreach (var gameObject in hierarchy)
            {
                if (gameObject.m_SkinnedMeshRenderer is not SkinnedMeshRenderer renderer)
                    continue;

                foreach (var pointer in renderer.m_Bones ?? Enumerable.Empty<PPtr<Transform>>())
                {
                    if (pointer.TryGet(out var bone))
                        AddTransformAndAncestors(bone, result);
                }

                if (renderer.m_RootBone.TryGet(out var rootBone))
                    AddTransformAndAncestors(rootBone, result);
            }

            return result;
        }

        private void AddTransformAndAncestors(Transform transform, HashSet<Transform> result)
        {
            var seen = new HashSet<Transform>();
            while (transform != null && seen.Add(transform))
            {
                if (!transformPaths.ContainsKey(transform))
                {
                    errors.Add($"Skeleton transform '{transform.Name}' is outside the selected Prefab hierarchy.");
                    return;
                }

                result.Add(transform);
                if (!transform.m_Father.TryGet(out transform))
                    return;
            }
        }

        private static void CollectTransforms(Transform transform, List<Transform> nodes,
            HashSet<Transform> seen, IReadOnlySet<Transform> selected)
        {
            if (transform == null || !seen.Add(transform)) return;
            if (selected.Contains(transform))
                nodes.Add(transform);
            foreach (var child in transform.m_Children ?? Enumerable.Empty<PPtr<Transform>>())
                if (child.TryGet(out var childTransform))
                    CollectTransforms(childTransform, nodes, seen, selected);
        }

        private static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned)
                return skinned.m_Mesh.TryGet(out var mesh) ? mesh : null;
            if (renderer.m_GameObject.TryGet(out var gameObject) && gameObject.m_MeshFilter?.m_Mesh.TryGet(out var staticMesh) == true)
                return staticMesh;
            return null;
        }

        private static int GetRendererSubMesh(Renderer renderer, int materialSlot)
        {
            if (renderer.m_SubsetIndices?.Length > materialSlot)
                return checked((int)renderer.m_SubsetIndices[materialSlot]);
            if (renderer.m_StaticBatchInfo?.subMeshCount > materialSlot)
                return renderer.m_StaticBatchInfo.firstSubMesh + materialSlot;
            return materialSlot;
        }

        private static void WriteFloatArray(BinaryWriter writer, float[] values)
        {
            writer.Write(values?.Length ?? 0);
            if (values == null) return;
            foreach (var value in values) writer.Write(value);
        }

        private static void WriteVector3(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z);
        }

        private static void WriteQuaternion(BinaryWriter writer, Quaternion value)
        {
            writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); writer.Write(value.W);
        }

        private static void WriteMatrix(BinaryWriter writer, Matrix4x4 value)
        {
            // Unity serializes this game's bind-pose payload in the transpose
            // of the managed Matrix4x4 field order.  The EIEM file is a
            // runtime exchange format, so store the exact order consumed by
            // Mesh.bindposes (m00,m10,m20,m30, ...), not the raw asset order.
            for (var column = 0; column < 4; column++)
                for (var row = 0; row < 4; row++)
                    writer.Write(value[column, row]);
        }

        private static string UniqueName(string prefix, string name, int index) =>
            prefix + Sanitize(name) + "_" + index.ToString(CultureInfo.InvariantCulture);

        private static string UniqueFileName(string directory, string name, long pathId, string extension) =>
            Path.Combine(directory, Sanitize(name) + "_" + pathId.ToString(CultureInfo.InvariantCulture) + extension);

        private static string Sanitize(string value)
        {
            var chars = (value ?? "resource").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            return new string(chars).Trim('_') is { Length: > 0 } result ? result : "resource";
        }

        private static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        private void WriteReport()
        {
            if (errors.Count == 0) return;
            File.WriteAllLines(Path.Combine(rootDirectory, "export-errors.txt"), errors, new UTF8Encoding(false));
        }
    }
}
