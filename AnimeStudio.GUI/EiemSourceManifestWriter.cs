using AnimeStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using AssetObject = AnimeStudio.Object;

namespace AnimeStudio.GUI
{
    /// <summary>
    /// Lossless offline source baseline beside the editable author payloads.
    /// This is not a native Mod pack or a snapshot of runtime native memory.
    /// </summary>
    internal static class EiemSourceManifestWriter
    {
        internal sealed record AuthorResource(AssetObject Asset, string Section, string File, string LogicalPath);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        public static void Write(string package, VirtualAssetFile prefab, GameObject root,
            EndfieldBundleDependencyIndex dependencies, string vfsFingerprint,
            IReadOnlyList<AuthorResource> resources)
        {
            if (dependencies == null || string.IsNullOrWhiteSpace(vfsFingerprint))
                throw new InvalidDataException("Source baseline requires the current VFS and dependency index.");
            var source = dependencies.GetBundleSource(root.assetsFile.fileName);
            if (string.IsNullOrWhiteSpace(source))
                throw new InvalidDataException("The exact Prefab root CAB has no source Bundle identity.");
            var closure = dependencies.ResolveBundleClosure(source);
            var cabs = closure.SelectMany(dependencies.GetCabNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var manager = root.assetsFile.assetsManager;
            var files = manager.assetsFileList.Where(file => cabs.Contains(file.fileName))
                .OrderBy(file => file.fileName, StringComparer.OrdinalIgnoreCase).ToArray();
            var loaded = files.Select(file => file.fileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = cabs.Where(cab => !loaded.Contains(cab)).ToArray();
            if (missing.Length != 0)
                throw new InvalidDataException("Dependency CABs were not loaded: " + string.Join(", ", missing));
            foreach (var resource in resources)
                if (!loaded.Contains(resource.Asset.assetsFile.fileName))
                    throw new InvalidDataException("Author resource is outside the source closure: " + Identity(resource.Asset));

            var hierarchy = EndfieldPrefabDocument.GetHierarchy(root);
            var paths = new Dictionary<Transform, string>();
            foreach (var go in hierarchy)
            {
                if (go.m_Transform == null) throw new InvalidDataException("Missing Transform: " + Identity(go));
                var transform = go.m_Transform;
                var path = string.Empty;
                if (go != root)
                {
                    if (!transform.m_Father.TryGet(out var parent) || !paths.TryGetValue(parent, out var parentPath))
                        throw new InvalidDataException("Unresolved hierarchy parent: " + Identity(transform));
                    path = string.IsNullOrEmpty(parentPath) ? go.m_Name : parentPath + "/" + go.m_Name;
                }
                paths.Add(transform, path);
            }

            var uses = new List<object>();
            foreach (var go in hierarchy)
            foreach (var componentPtr in go.m_Components)
            {
                if (!componentPtr.Cast<AssetObject>().TryGet(out var component))
                    throw new InvalidDataException("Unresolved component on " + Identity(go));
                if (component is not Renderer renderer) continue;
                var skinned = renderer as SkinnedMeshRenderer;
                var filter = skinned == null ? go.m_MeshFilter : null;
                var meshPtr = skinned?.m_Mesh ?? filter?.m_Mesh;
                Require(meshPtr, "Renderer Mesh", allowNull: true);
                foreach (var pointer in renderer.m_Materials ?? new List<PPtr<Material>>())
                    Require(pointer, "Renderer Material", allowNull: true);
                foreach (var pointer in skinned?.m_Bones ?? new List<PPtr<Transform>>())
                    Require(pointer, "Renderer bone", allowNull: false);
                Require(skinned?.m_RootBone, "rootBone", allowNull: true);
                Require(skinned?.m_SkinningRoot, "skinningRoot", allowNull: true);
                uses.Add(new
                {
                    identity = Identity(renderer), type = renderer.type.ToString(),
                    owner = Identity(go), ownerPath = paths[go.m_Transform], transform = Identity(go.m_Transform),
                    native = NativeRange(renderer), mesh = Pointer(meshPtr, skinned?.assetsFile ?? filter?.assetsFile ?? renderer.assetsFile),
                    meshFilter = filter == null ? null : Identity(filter),
                    materials = (renderer.m_Materials ?? new List<PPtr<Material>>())
                        .Select((p, slot) => new { slot, pointer = Pointer(p, renderer.assetsFile) }).ToArray(),
                    bones = (skinned?.m_Bones ?? new List<PPtr<Transform>>())
                        .Select(p => Pointer(p, renderer.assetsFile)).ToArray(),
                    rootBone = Pointer(skinned?.m_RootBone, renderer.assetsFile),
                    skinningRoot = Pointer(skinned?.m_SkinningRoot, renderer.assetsFile),
                    blendShapeWeights = skinned?.m_BlendShapeWeights,
                    bounds = skinned?.m_AABB == null ? null : new
                    {
                        center = Vector(skinned.m_AABB.m_Center), extent = Vector(skinned.m_AABB.m_Extent),
                        dirty = skinned.m_DirtyAABB
                    },
                    subsetIndices = renderer.m_SubsetIndices,
                    staticBatch = renderer.m_StaticBatchInfo == null ? null : new
                    {
                        firstSubmesh = renderer.m_StaticBatchInfo.firstSubMesh,
                        submeshCount = renderer.m_StaticBatchInfo.subMeshCount
                    }
                });
            }

            var entries = new List<object>();
            foreach (var bundle in files.SelectMany(file => file.Objects).OfType<AssetBundle>())
            foreach (var entry in bundle.m_Container)
            {
                var path = ResolveContainer(entry.Key);
                if (!string.Equals(path, prefab.Container.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) continue;
                if (!entry.Value.asset.TryGet<GameObject>(out var target) || target != root) continue;
                entries.Add(new { bundle = Identity(bundle), serializedKey = entry.Key, logicalPath = path,
                    asset = Pointer(entry.Value.asset, bundle.assetsFile), entry.Value.preloadIndex, entry.Value.preloadSize });
            }
            if (entries.Count != 1)
                throw new InvalidDataException($"Expected one exact container entry for the root; found {entries.Count}.");

            var sourceDir = Path.Combine(package, "source");
            var snapshotPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string SnapshotPath(string kind, string member)
            {
                var relative = "source/" + kind + "/" + EiemPackageIdentity.SnapshotFileName(member);
                if (!snapshotPaths.Add(relative)) throw new InvalidDataException("Conflicting source snapshot member: " + member);
                return relative;
            }
            Directory.CreateDirectory(Path.Combine(sourceDir, "serialized"));
            Directory.CreateDirectory(Path.Combine(sourceDir, "streams"));
            var serialized = new List<object>();
            foreach (var file in files)
            {
                foreach (var item in file.m_Objects)
                    if (item.byteStart < file.header.m_DataOffset || item.byteStart > file.header.m_FileSize ||
                        item.byteSize > file.header.m_FileSize - item.byteStart)
                        throw new InvalidDataException("Object outside SerializedFile: " + file.fileName + ":" + item.m_PathID);
                var relative = SnapshotPath("serialized", file.fileName);
                var snapshot = Snapshot(file.reader.BaseStream, package, relative, file.header.m_FileSize);
                serialized.Add(new
                {
                    cab = file.fileName, sourceBundle = dependencies.GetBundleSource(file.fileName),
                    bundleOffset = file.offset, snapshot, file.unityVersion,
                    platform = file.m_TargetPlatform.ToString(),
                    header = new { fileSize = file.header.m_FileSize, dataOffset = file.header.m_DataOffset,
                        metadataSize = file.header.m_MetadataSize, version = (int)file.header.m_Version, endian = file.header.m_Endianess },
                    externals = file.m_Externals.Select((e, index) => new { fileId = index + 1,
                        cab = e.fileName, path = e.pathName, e.type, guid = e.guid.ToString(),
                        inSnapshot = loaded.Contains(e.fileName) }).ToArray(),
                    types = file.m_Types.Select((t, index) => new { index, classId = t.classID,
                        stripped = t.m_IsStrippedType, scriptTypeIndex = t.m_ScriptTypeIndex,
                        scriptId = Hex(t.m_ScriptID), oldTypeHash = Hex(t.m_OldTypeHash),
                        typeTreeNodes = t.m_Type?.m_Nodes?.Count ?? 0, dependencies = t.m_TypeDependencies }).ToArray(),
                    objects = file.m_Objects.Select(item => new { pathId = item.m_PathID,
                        offset = item.byteStart, size = item.byteSize, typeIndex = item.typeID, classId = item.classID }).ToArray()
                });
            }

            // Preserve complete raw stream members, not decoded PNG pixels alone.
            var streams = manager.ResourceFileReaders.Where(pair =>
                    loaded.Contains(Path.GetFileNameWithoutExtension(pair.Key)))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            var streamNames = streams.Select(pair => Path.GetFileName(pair.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var streamReferences = new List<object>();
            foreach (var asset in files.SelectMany(file => file.Objects))
            {
                var info = asset is Mesh mesh ? mesh.StreamData : (asset as Texture2D)?.m_StreamData;
                if (info == null || string.IsNullOrWhiteSpace(info.path) || info.size == 0) continue;
                var member = Path.GetFileName(info.path.Replace('\\', '/'));
                if (!streamNames.Contains(member))
                    throw new InvalidDataException("Missing stream member " + member + " for " + Identity(asset));
                var stream = manager.ResourceFileReaders[member].BaseStream;
                if (info.offset < 0 || info.offset > stream.Length || info.size > stream.Length - info.offset)
                    throw new InvalidDataException("Streaming range outside member for " + Identity(asset));
                streamReferences.Add(new { identity = Identity(asset), path = info.path, member,
                    offset = info.offset, size = info.size });
            }
            var rawStreams = streams.Select(pair => new { member = pair.Key,
                snapshot = Snapshot(pair.Value.BaseStream, package,
                    SnapshotPath("streams", pair.Key), pair.Value.BaseStream.Length) }).ToArray();

            var manifest = new
            {
                format = "EFFSOURCE", version = 1, purpose = "author-source-baseline",
                nativePack = false, runtimeNativeMemory = false, vfsFingerprint,
                prefab = prefab.Container.Replace('\\', '/'), root = Identity(root), sourceBundle = source,
                containerEntries = entries, closure,
                resources = resources.Select(r => new { identity = Identity(r.Asset),
                    type = r.Asset.type.ToString(), name = r.Asset.Name, logicalPath = r.LogicalPath,
                    sourceBundle = dependencies.GetBundleSource(r.Asset.assetsFile.fileName),
                    authorSection = r.Section, authorFile = r.File.Replace('\\', '/'),
                    native = NativeRange(r.Asset), details = ResourceDetails(r.Asset) }).ToArray(),
                rendererUses = uses,
                hierarchy = hierarchy.Select(go => new { identity = Identity(go), name = go.m_Name,
                    transform = Identity(go.m_Transform), path = paths[go.m_Transform],
                    native = NativeRange(go), transformNative = NativeRange(go.m_Transform),
                    parent = Pointer(go.m_Transform.m_Father, go.m_Transform.assetsFile),
                    children = go.m_Transform.m_Children.Select(p => Pointer(p, go.m_Transform.assetsFile)).ToArray(),
                    localPosition = Vector(go.m_Transform.m_LocalPosition),
                    localRotation = new[] { go.m_Transform.m_LocalRotation.X, go.m_Transform.m_LocalRotation.Y,
                        go.m_Transform.m_LocalRotation.Z, go.m_Transform.m_LocalRotation.W },
                    localScale = Vector(go.m_Transform.m_LocalScale),
                    components = go.m_Components.Select(p => Pointer(p, go.assetsFile)).ToArray() }).ToArray(),
                serializedFiles = serialized, rawStreams, streamReferences,
                preservation = new[] { "Complete SerializedFile bytes retain unknown fields, type trees, object tables and native component data.",
                    "Renderer uses cover this selected Prefab, not all reverse consumers in the game.",
                    "Logical resource paths identify targets; CAB/PathID and hashes are evidence for this VFS version only.",
                    "These snapshots are source inputs, not a game-loadable replacement pack or proof of native reload." }
            };
            var temp = Path.Combine(sourceDir, "manifest.json.tmp");
            using (var output = File.Create(temp)) JsonSerializer.Serialize(output, manifest, JsonOptions);
            File.Move(temp, Path.Combine(sourceDir, "manifest.json"), overwrite: true);
        }

        private static object ResourceDetails(AssetObject asset)
        {
            if (asset is Material material)
                return new { shader = Pointer(material.m_Shader, asset.assetsFile),
                    textures = (material.m_SavedProperties?.m_TexEnvs ?? new List<KeyValuePair<string, UnityTexEnv>>())
                        .Select(pair => new { property = pair.Key, pointer = Pointer(pair.Value?.m_Texture, asset.assetsFile) }).ToArray() };
            if (asset is Mesh mesh)
                return new { vertices = mesh.m_VertexCount, indices = mesh.m_Indices?.Count ?? 0,
                    submeshes = mesh.m_SubMeshes?.Count ?? 0, bindposes = mesh.m_BindPose?.Length ?? 0,
                    stream = Streaming(mesh.StreamData) };
            if (asset is Texture2D texture)
                return new { width = texture.m_Width, height = texture.m_Height,
                    format = texture.m_TextureFormat.ToString(), mipCount = texture.m_MipCount,
                    colorSpace = texture.m_ColorSpace, stream = Streaming(texture.m_StreamData) };
            return null;
        }

        private static object Streaming(StreamingInfo info) => info == null ? null :
            new { info.path, info.offset, info.size };

        private static void Require<T>(PPtr<T> pointer, string field, bool allowNull) where T : AssetObject
        {
            if (pointer == null) return; // Field not serialized for this renderer type/version.
            if (pointer.IsNull && allowNull) return;
            if (!pointer.TryGet(out _)) throw new InvalidDataException("Unresolved " + field + " PPtr: " + pointer.m_FileID + ":" + pointer.m_PathID);
        }

        private static object Pointer<T>(PPtr<T> pointer, SerializedFile owner) where T : AssetObject
        {
            if (pointer == null) return null;
            pointer.Cast<AssetObject>().TryGet(out var target);
            var cab = pointer.m_FileID == 0 ? owner.fileName :
                pointer.m_FileID > 0 && pointer.m_FileID <= owner.m_Externals.Count ?
                    owner.m_Externals[pointer.m_FileID - 1].fileName : null;
            return new { fileId = pointer.m_FileID, pathId = pointer.m_PathID, isNull = pointer.IsNull,
                resolved = target != null, cab,
                identity = pointer.IsNull || cab == null ? null : cab.ToLowerInvariant() + ":" + pointer.m_PathID,
                type = target?.type.ToString() };
        }

        private static object NativeRange(AssetObject asset) => new { cab = asset.assetsFile.fileName,
            pathId = asset.m_PathID, offset = asset.reader.byteStart, size = asset.byteSize,
            classId = (int)asset.type, typeHash = Hex(asset.serializedType?.m_OldTypeHash) };

        private static object Snapshot(Stream source, string package, string relative, long expectedSize)
        {
            if (!source.CanSeek || source.Length != expectedSize)
                throw new InvalidDataException($"Snapshot length mismatch for {relative}: {source.Length}/{expectedSize}.");
            var saved = source.Position;
            try
            {
                source.Position = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var output = File.Create(Path.Combine(package, relative.Replace('/', Path.DirectorySeparatorChar)));
                var buffer = new byte[128 * 1024];
                long copied = 0;
                int count;
                while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                {
                    output.Write(buffer, 0, count);
                    hash.AppendData(buffer, 0, count);
                    copied += count;
                }
                if (copied != expectedSize) throw new EndOfStreamException(relative);
                return new { file = relative, size = copied, sha256 = Convert.ToHexString(hash.GetHashAndReset()) };
            }
            finally { source.Position = saved; }
        }

        private static string Hex(byte[] bytes) => bytes == null ? null : Convert.ToHexString(bytes);
        private static float[] Vector(Vector3 value) => new[] { value.X, value.Y, value.Z };
        private static string Identity(AssetObject asset) => asset.assetsFile.fileName.ToLowerInvariant() + ":" + asset.m_PathID;
        private static string ResolveContainer(string value) =>
            (ulong.TryParse(value, out var hash) && AssetsHelper.Paths.TryGetValue(hash, out var path) ? path : value).Replace('\\', '/');
    }
}
