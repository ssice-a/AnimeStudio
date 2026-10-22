using AnimeStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetObject = AnimeStudio.Object;

namespace AnimeStudio.GUI;

/// <summary>Recover only a mesh's own FBX skeleton, by exact source references.</summary>
internal sealed class NarakaModelBones
{
    internal sealed record Bone(Transform Transform, string Path);
    internal sealed record Palette(string Container, GameObject Root, Bone[] Bones, IReadOnlyDictionary<Transform, string> Paths);
    private sealed record Model(string Container, AssetBundle Bundle, AssetInfo[] Entries);
    private readonly NarakaPrefabLoader loader;
    private List<Model> models;
    private readonly Dictionary<AssetObject, Palette> palettes = new();
    private readonly Dictionary<GameObject, Dictionary<uint, List<Bone>>> hierarchies = new();

    internal NarakaModelBones(NarakaPrefabLoader loader) => this.loader = loader;

    internal Palette Resolve(Mesh mesh)
    {
        AssetObject source = loader.RuntimeMeshSources.TryGetValue(mesh, out var runtime) ? runtime : mesh;
        if (palettes.TryGetValue(source, out var cached)) return cached;
        if (mesh.m_BoneNameHashes == null || mesh.m_BindPose == null ||
            mesh.m_BoneNameHashes.Length != mesh.m_BindPose.Length || mesh.m_BoneNameHashes.Length == 0)
            return palettes[source] = null;
        models ??= loader.Manager.assetsFileList.SelectMany(file => file.m_Objects
            .Where(o => o.classID == (int)ClassIDType.AssetBundle)
            .Select(o => loader.Require(new PPtr<AssetBundle>(0, o.m_PathID, file), file)))
            .SelectMany(bundle => bundle.m_Container.Where(e => e.Key.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                .GroupBy(e => e.Key.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
                .Select(group => new Model(group.Key, bundle, group.Select(e => e.Value).ToArray())))
            .ToList();
        var matches = new List<Palette>();
        foreach (var model in models)
        {
            if (!References(model, source)) continue;
            // A logical FBX may have one root entry and many Mesh subasset entries.
            // Reading only GameObject entries avoids decoding unrelated textures.
            foreach (var root in model.Entries.Where(e => loader.ReferenceType(e.asset, model.Bundle.assetsFile) == ClassIDType.GameObject)
                         .Select(e => loader.Resolve(e.asset, model.Bundle.assetsFile)).OfType<GameObject>().Distinct())
            {
                if (!hierarchies.TryGetValue(root, out var byHash))
                    hierarchies.Add(root, byHash = Hierarchy(root));
                if (!TryMatch(mesh.m_BoneNameHashes, byHash, out var bones)) continue;
                if (!matches.Any(m => m.Root == root)) matches.Add(new Palette(model.Container, root, bones,
                    byHash.Values.SelectMany(list => list).ToDictionary(b => b.Transform, b => b.Path)));
            }
        }
        // Shared geometry in distinct source models is not enough evidence to pick
        // a skeleton. Keep explicit unresolved status instead of choosing by name.
        return palettes[source] = matches.Count == 1 ? matches[0] : null;
    }

    private bool References(Model model, AssetObject target)
    {
        bool Same(PPtr<AssetObject> pointer) => loader.ReferenceIdentity(pointer, model.Bundle.assetsFile) == NarakaPackageWriter.Identity(target);
        foreach (var entry in model.Entries)
        {
            if (Same(entry.asset)) return true;
            long end = (long)entry.preloadIndex + entry.preloadSize;
            if (entry.preloadIndex < 0 || entry.preloadSize < 0 || end > model.Bundle.m_PreloadTable.Count)
                throw new InvalidDataException("Invalid FBX preload range: " + model.Container);
            for (var i = entry.preloadIndex; i < end; i++)
                if (Same(model.Bundle.m_PreloadTable[i])) return true;
        }
        return false;
    }

    private Dictionary<uint, List<Bone>> Hierarchy(GameObject root)
    {
        var result = new Dictionary<uint, List<Bone>>();
        var visited = new HashSet<Transform>();
        void Visit(GameObject go, string path)
        {
            var transform = loader.ObjectTransform(go);
            if (!visited.Add(transform)) throw new InvalidDataException("Repeated/cyclic FBX skeleton Transform");
            var hash = SevenZip.CRC.CalculateDigestUTF8(path);
            if (!result.TryGetValue(hash, out var list)) result.Add(hash, list = new());
            list.Add(new Bone(transform, path));
            foreach (var pointer in transform.m_Children)
            {
                var child = loader.Require(pointer, transform.assetsFile);
                var childObject = loader.Require(child.m_GameObject, child.assetsFile);
                Visit(childObject, path.Length == 0 ? childObject.Name : path + "/" + childObject.Name);
            }
        }
        Visit(root, "");
        return result;
    }

    internal static bool TryMatch(uint[] hashes, Dictionary<uint, List<Bone>> hierarchy, out Bone[] bones)
    {
        bones = new Bone[hashes.Length];
        for (var i = 0; i < hashes.Length; i++)
        {
            if (!hierarchy.TryGetValue(hashes[i], out var rows) || rows.Count != 1)
            { bones = null; return false; }
            bones[i] = rows[0];
        }
        return true;
    }
}
