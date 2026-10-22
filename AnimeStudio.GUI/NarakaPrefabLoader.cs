using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AnimeStudio;
using AssetObject = AnimeStudio.Object;

namespace AnimeStudio.GUI;

// Read only the selected Prefab graph, not every decoded object/texture in its
// multi-costume bundles. Bundle streams stay lazy until a referenced object is used.
internal sealed class NarakaPrefabLoader : IDisposable
{
    internal readonly AssetsManager Manager;
    private readonly Dictionary<string, SerializedFile> files;
    private readonly Dictionary<SerializedFile, Dictionary<long, ObjectInfo>> objects;
    private readonly HashSet<string> reading = new(StringComparer.Ordinal);
    private Dictionary<string, string> logicalPaths;
    private readonly Dictionary<AssetObject, Mesh> runtimeMeshes = new();
    internal readonly Dictionary<Mesh, AssetObject> RuntimeMeshSources = new();
    internal NarakaPrefabLoader(string source)
    {
        Manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.Naraka),
            SkipProcess = true, ResolveDependencies = true };
        try
        {
            Manager.LoadFiles(new[] { source }, mergeSplitAssets: false);
            files = Manager.assetsFileList.ToDictionary(f => f.fileName, StringComparer.OrdinalIgnoreCase);
            objects = files.Values.ToDictionary(f => f, f => f.m_Objects.ToDictionary(o => o.m_PathID));
        }
        catch { Manager.Clear(); throw; }
    }

    internal GameObject FindRoot(string container, string source)
    {
        var roots = new List<GameObject>();
        foreach (var file in files.Values.Where(f => string.Equals(f.originalPath, source, StringComparison.OrdinalIgnoreCase)))
        foreach (var info in file.m_Objects.Where(o => o.classID == (int)ClassIDType.AssetBundle))
        {
            var bundle = (AssetBundle)Read(file, info.m_PathID);
            foreach (var entry in bundle.m_Container.Where(e => string.Equals(e.Key.Replace('\\', '/'), container, StringComparison.OrdinalIgnoreCase)))
                if (Resolve(entry.Value.asset, file) is GameObject root && !roots.Contains(root)) roots.Add(root);
        }
        if (roots.Count != 1) throw new InvalidDataException($"Expected one Prefab root for {container}, found {roots.Count}");
        Visit(roots[0], new HashSet<Transform>());
        return roots[0];
    }

    private void Visit(GameObject gameObject, HashSet<Transform> visited)
    {
        foreach (var pointer in gameObject.m_Components)
        {
            var component = Resolve(pointer, gameObject.assetsFile);
            switch (component)
            {
                case Transform transform: gameObject.m_Transform = transform; break;
                case MeshFilter filter: gameObject.m_MeshFilter = filter; break;
                case SkinnedMeshRenderer skinned: gameObject.m_SkinnedMeshRenderer = skinned; break;
                case MeshRenderer renderer: gameObject.m_MeshRenderer = renderer; break;
            }
        }
        var node = gameObject.m_Transform ?? throw new InvalidDataException("Prefab object has no Transform: " + gameObject.Name);
        if (!visited.Add(node)) throw new InvalidDataException("Cycle in Prefab Transform hierarchy");
        foreach (var pointer in node.m_Children)
        {
            var child = Require(pointer, node.assetsFile);
            Visit(Require(child.m_GameObject, child.assetsFile), visited);
        }
    }

    internal T Require<T>(PPtr<T> pointer, SerializedFile owner) where T : AssetObject =>
        Resolve(pointer, owner) ?? throw new InvalidDataException($"Unresolved {typeof(T).Name}: {owner.fileName}:{pointer?.m_FileID}:{pointer?.m_PathID}");

    private AssetObject FieldReference(object value, AssetObject owner)
    {
        if (value is not System.Collections.Specialized.OrderedDictionary reference) return null;
        return Resolve(new PPtr<AssetObject>(Convert.ToInt32(reference["m_FileID"]),
            Convert.ToInt64(reference["m_PathID"]), owner.assetsFile), owner.assetsFile);
    }

    internal Mesh RendererMesh(GameObject gameObject, Renderer renderer)
    {
        var native = renderer is SkinnedMeshRenderer skin ? skin.m_Mesh : gameObject.m_MeshFilter?.m_Mesh;
        if (native != null && !native.IsNull) return Require(native, renderer.assetsFile);
        var sources = new List<AssetObject>();
        foreach (var pointer in gameObject.m_Components)
        {
            var component = Resolve(pointer.Cast<AssetObject>(), gameObject.assetsFile);
            if (component?.type != ClassIDType.MonoBehaviour) continue;
            var fields = Fields(component);
            if (FieldReference(fields?["_renderer"], component) != renderer) continue;
            var source = FieldReference(fields?["avatarMeshAsset"], component);
            if (source != null && !sources.Contains(source)) sources.Add(source);
        }
        if (sources.Count != 1) throw new InvalidDataException($"Renderer {gameObject.Name} ({NarakaPackageWriter.Identity(renderer)}) has no native Mesh and {sources.Count} exact avatarMeshAsset references");
        var asset = sources[0];
        if (!runtimeMeshes.TryGetValue(asset, out var mesh))
        {
            mesh = NarakaRuntimeMesh.Decode(asset, Fields(asset) ?? throw new InvalidDataException("Runtime mesh has no TypeTree"));
            runtimeMeshes.Add(asset, mesh); RuntimeMeshSources.Add(mesh, asset);
        }
        return mesh;
    }

    internal string LogicalPath(AssetObject value)
    {
        if (logicalPaths == null)
        {
            logicalPaths = new(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files.Values)
            foreach (var info in file.m_Objects.Where(o => o.classID == (int)ClassIDType.AssetBundle))
            foreach (var entry in ((AssetBundle)Read(file, info.m_PathID)).m_Container)
            {
                var pointer = entry.Value.asset;
                var cab = pointer.m_FileID == 0 ? file.fileName : file.m_Externals[pointer.m_FileID - 1].fileName;
                logicalPaths.TryAdd(cab + ":" + pointer.m_PathID, entry.Key);
            }
        }
        return logicalPaths.GetValueOrDefault(value.assetsFile.fileName + ":" + value.m_PathID, "");
    }

    internal string ReferenceIdentity(PPtr<AssetObject> pointer, SerializedFile owner)
    {
        if (pointer == null || pointer.IsNull) return null;
        if (pointer.m_FileID < 0 || pointer.m_FileID > owner.m_Externals.Count)
            throw new InvalidDataException("Invalid source object reference");
        var cab = pointer.m_FileID == 0 ? owner.fileName : owner.m_Externals[pointer.m_FileID - 1].fileName;
        return cab.ToLowerInvariant() + ":" + pointer.m_PathID;
    }

    internal ClassIDType ReferenceType(PPtr<AssetObject> pointer, SerializedFile owner)
    {
        if (pointer == null || pointer.IsNull) return ClassIDType.UnknownType;
        var file = owner;
        if (pointer.m_FileID != 0 && (pointer.m_FileID < 1 || pointer.m_FileID > owner.m_Externals.Count ||
            !files.TryGetValue(owner.m_Externals[pointer.m_FileID - 1].fileName, out file))) return ClassIDType.UnknownType;
        return objects[file].TryGetValue(pointer.m_PathID, out var info) ? (ClassIDType)info.classID : ClassIDType.UnknownType;
    }

    internal Transform ObjectTransform(GameObject gameObject)
    {
        if (gameObject.m_Transform != null) return gameObject.m_Transform;
        foreach (var pointer in gameObject.m_Components)
            if (ReferenceType(pointer.Cast<AssetObject>(), gameObject.assetsFile) == ClassIDType.Transform)
                return gameObject.m_Transform = Require(pointer.Cast<Transform>(), gameObject.assetsFile);
        throw new InvalidDataException("Source model object has no Transform: " + gameObject.Name);
    }

    internal T Resolve<T>(PPtr<T> pointer, SerializedFile owner) where T : AssetObject
    {
        if (pointer == null || pointer.IsNull) return null;
        var file = owner;
        if (pointer.m_FileID != 0)
        {
            if (pointer.m_FileID < 1 || pointer.m_FileID > owner.m_Externals.Count ||
                !files.TryGetValue(owner.m_Externals[pointer.m_FileID - 1].fileName, out file))
                throw new InvalidDataException($"Missing external CAB for {owner.fileName}:{pointer.m_FileID}:{pointer.m_PathID}");
        }
        return Read(file, pointer.m_PathID) as T;
    }

    private AssetObject Read(SerializedFile file, long pathId)
    {
        if (file.ObjectsDic.TryGetValue(pathId, out var existing)) return existing;
        if (!objects[file].TryGetValue(pathId, out var info)) throw new InvalidDataException($"Missing object {file.fileName}:{pathId}");
        var key = file.fileName + ":" + pathId;
        if (!reading.Add(key)) throw new InvalidDataException("Recursive object construction: " + key);
        try
        {
            var reader = new ObjectReader(file.reader, file, info, Manager.Game);
            AssetObject value = (ClassIDType)info.classID switch
            {
                ClassIDType.AssetBundle => new AssetBundle(reader),
                ClassIDType.GameObject => new GameObject(reader),
                ClassIDType.Transform => new Transform(reader),
                ClassIDType.MeshFilter => new MeshFilter(reader),
                ClassIDType.MeshRenderer => new MeshRenderer(reader),
                ClassIDType.SkinnedMeshRenderer => new SkinnedMeshRenderer(reader),
                ClassIDType.Mesh => new Mesh(reader),
                ClassIDType.Material => new Material(reader),
                ClassIDType.Texture2D => new Texture2D(reader),
                // Only the shader identity is needed; the compiled program is not
                // interpreted as Blender code. Shader data is decoded via TypeTree.
                _ => new AssetObject(reader),
            };
            file.AddObject(value);
            return value;
        }
        catch (Exception error) { throw new InvalidDataException($"Unable to parse {(ClassIDType)info.classID} {key}", error); }
        finally { reading.Remove(key); }
    }

    internal static System.Collections.Specialized.OrderedDictionary Fields(AssetObject asset) => asset.ToType();
    public void Dispose() => Manager.Clear();
}
