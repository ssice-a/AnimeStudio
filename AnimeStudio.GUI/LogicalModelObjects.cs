using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetObject = AnimeStudio.Object;

namespace AnimeStudio.GUI;

/// <summary>
/// Resolve a model's real objects from its own AssetBundle entry. Display
/// Container labels are lossy: shared preload objects are relabeled by later
/// entries in Studio.BuildAssetData and cannot identify a model's meshes.
/// </summary>
internal static class LogicalModelObjects
{
    internal static IReadOnlyList<Mesh> Resolve(IEnumerable<SerializedFile> files, string container)
    {
        var meshes = new HashSet<Mesh>();
        var visited = new HashSet<GameObject>();
        void Visit(AssetObject value)
        {
            if (value is Mesh mesh) { meshes.Add(mesh); return; }
            if (value is not GameObject gameObject || !visited.Add(gameObject)) return;
            if (gameObject.m_SkinnedMeshRenderer?.m_Mesh.TryGet(out var skin) == true) meshes.Add(skin);
            if (gameObject.m_MeshFilter?.m_Mesh.TryGet(out var geometry) == true) meshes.Add(geometry);
            if (gameObject.m_Transform == null) return;
            foreach (var pointer in gameObject.m_Transform.m_Children)
                if (pointer.TryGet(out var child) && child.m_GameObject.TryGet(out var childObject)) Visit(childObject);
        }
        foreach (var bundle in files.SelectMany(f => f.Objects).OfType<AssetBundle>())
        foreach (var entry in bundle.m_Container.Where(e =>
                     string.Equals(e.Key.Replace('\\', '/'), container.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
        {
            if (entry.Value.asset.TryGet(out var main)) Visit(main);
            var start = entry.Value.preloadIndex;
            var end = (long)start + entry.Value.preloadSize;
            if (start < 0 || end < start || end > bundle.m_PreloadTable.Count)
                throw new InvalidDataException("Invalid model preload range: " + container);
            for (var i = start; i < end; i++)
                if (bundle.m_PreloadTable[i].TryGet(out var value)) Visit(value);
        }
        return meshes.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.assetsFile.fileName, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.m_PathID).ToArray();
    }
}
