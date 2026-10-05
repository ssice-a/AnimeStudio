using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio.GUI
{
    internal static class EndfieldPrefabDocument
    {
        private static string Identity(AnimeStudio.Object obj) =>
            obj.assetsFile.fileName.ToLowerInvariant() + ":" + obj.m_PathID;

        public static GameObject FindRoot(IEnumerable<GameObject> objects, VirtualAssetFile file,
            IReadOnlyCollection<string> preferredCabNames)
        {
            // Preloads are dependencies; AssetInfo.asset is the container entry.
            var files = objects.Select(x => x.assetsFile).Distinct()
                .Where(x => preferredCabNames == null || preferredCabNames.Count == 0 ||
                    preferredCabNames.Contains(x.fileName, StringComparer.OrdinalIgnoreCase));
            var roots = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var bundle in files.SelectMany(x => x.Objects).OfType<AssetBundle>())
            foreach (var entry in bundle.m_Container)
            {
                var container = entry.Key;
                if (ulong.TryParse(container, out var hash) && AssetsHelper.Paths.TryGetValue(hash, out var path))
                    container = path;
                if (!string.Equals(container.Replace('\\', '/'), file.Container.Replace('\\', '/'),
                        StringComparison.OrdinalIgnoreCase)) continue;
                // Embedded subassets can share the same container path.
                if (entry.Value.asset.TryGet<GameObject>(out var root))
                    roots.TryAdd(Identity(root), root);
            }
            if (roots.Count > 1)
                throw new InvalidDataException($"Ambiguous GameObject entry for '{file.Container}'.");
            return roots.Values.SingleOrDefault();
        }

        public static string Build(VirtualAssetFile file, GameObject root,
            IReadOnlyList<VirtualAssetRecord> records = null,
            EndfieldBundleDependencyIndex dependencies = null)
        {
            var builder = new StringBuilder(32 * 1024);
            var transformPaths = BuildTransformPaths(root);
            var resolver = new LogicalAssetResolver(records, dependencies);
            builder.AppendLine($"Prefab: {file.Container}");
            builder.AppendLine($"Root: {Describe(root, transformPaths)}");
            builder.AppendLine($"Source: {FindSource(file)}");
            builder.AppendLine();
            builder.AppendLine("Hierarchy and component references:");

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            WriteGameObject(builder, root, 0, visited, transformPaths, resolver);
            builder.AppendLine();
            builder.AppendLine($"GameObjects: {visited.Count:N0}");
            builder.AppendLine("Note: this is the serialized Prefab object graph, not a byte-for-byte Unity Editor source .prefab file.");
            return builder.ToString();
        }

        public static IReadOnlyList<GameObject> GetHierarchy(GameObject root)
        {
            var result = new List<GameObject>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<GameObject>();
            if (root != null)
                pending.Push(root);
            while (pending.Count > 0)
            {
                var gameObject = pending.Pop();
                if (gameObject == null || !visited.Add(Identity(gameObject)))
                    continue;
                result.Add(gameObject);
                var children = gameObject.m_Transform?.m_Children;
                if (children == null)
                    continue;
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    if (children[i].TryGet(out var childTransform) &&
                        childTransform.m_GameObject.TryGet(out var childObject))
                        pending.Push(childObject);
                }
            }
            return result;
        }

        private static void WriteGameObject(StringBuilder builder, GameObject gameObject, int depth,
            HashSet<string> visited, IReadOnlyDictionary<Transform, string> transformPaths,
            LogicalAssetResolver resolver)
        {
            if (gameObject == null || depth > 512 || !visited.Add(Identity(gameObject)))
                return;

            var indent = new string(' ', depth * 2);
            builder.Append(indent).Append("- ").AppendLine(Describe(gameObject, transformPaths));

            foreach (var componentPtr in gameObject.m_Components)
            {
                if (!componentPtr.TryGet(out var component))
                    continue;
                builder.Append(indent).Append("  [").Append(component.type).Append(']');

                switch (component)
                {
                    case SkinnedMeshRenderer skinned:
                        AppendMeshAndMaterials(builder, skinned.m_Mesh, skinned.m_Materials, resolver);
                        builder.Append(" Bones=").Append(skinned.m_Bones?.Count ?? 0);
                        if (skinned.m_RootBone.TryGet(out var rootBone))
                            builder.Append(" RootBone=").Append(GetTransformPath(rootBone, transformPaths));
                        break;
                    case MeshRenderer renderer:
                        if (gameObject.m_MeshFilter?.m_Mesh != null)
                            AppendMeshAndMaterials(builder, gameObject.m_MeshFilter.m_Mesh,
                                renderer.m_Materials, resolver);
                        else
                            AppendMaterials(builder, renderer.m_Materials, resolver);
                        break;
                    case MeshFilter filter:
                        if (filter.m_Mesh.TryGet(out var mesh))
                            builder.Append(" Mesh=").Append(resolver.Describe(mesh));
                        break;
                    case Animator animator:
                        if (animator.m_Avatar.TryGet(out var avatar))
                            builder.Append(" Avatar=").Append(resolver.Describe(avatar));
                        if (animator.m_Controller.TryGet(out var controller))
                            builder.Append(" Controller=").Append(resolver.Describe(controller));
                        break;
                }
                builder.AppendLine();
            }

            var transform = gameObject.m_Transform;
            if (transform?.m_Children == null)
                return;
            foreach (var childPtr in transform.m_Children)
            {
                if (childPtr.TryGet(out var childTransform) &&
                    childTransform.m_GameObject.TryGet(out var childObject))
                    WriteGameObject(builder, childObject, depth + 1, visited,
                        transformPaths, resolver);
            }
        }

        private static void AppendMeshAndMaterials(StringBuilder builder, PPtr<Mesh> meshPtr,
            List<PPtr<Material>> materials, LogicalAssetResolver resolver)
        {
            if (meshPtr != null && meshPtr.TryGet(out var mesh))
                builder.Append(" Mesh=").Append(resolver.Describe(mesh))
                    .Append(" SubMeshes=").Append(mesh.m_SubMeshes?.Count ?? 0);
            AppendMaterials(builder, materials, resolver);
        }

        private static void AppendMaterials(StringBuilder builder, List<PPtr<Material>> materials,
            LogicalAssetResolver resolver)
        {
            if (materials == null || materials.Count == 0)
                return;
            builder.Append(" Materials=[");
            for (var i = 0; i < materials.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                if (materials[i].TryGet(out var material))
                    builder.Append(resolver.Describe(material));
                else
                    builder.Append("missing");
            }
            builder.Append(']');
        }

        private static bool IsRoot(GameObject gameObject) =>
            gameObject?.m_Transform != null && gameObject.m_Transform.m_Father.IsNull;

        private static string Describe(GameObject gameObject,
            IReadOnlyDictionary<Transform, string> transformPaths) => gameObject == null
            ? "[not found]"
            : $"{gameObject.m_Name} [GameObject] " +
              $"Path={GetTransformPath(gameObject.m_Transform, transformPaths)} " +
              $"Root={IsRoot(gameObject)} HasModel={gameObject.HasModel()}";

        private static string GetTransformPath(Transform transform,
            IReadOnlyDictionary<Transform, string> transformPaths)
        {
            if (transform != null && transformPaths.TryGetValue(transform, out var path))
                return string.IsNullOrEmpty(path) ? "/" : path;
            return transform?.Name ?? "?";
        }

        private static Dictionary<Transform, string> BuildTransformPaths(GameObject prefabRoot)
        {
            var paths = new Dictionary<Transform, string>();
            void Visit(Transform transform, string path)
            {
                if (transform == null || paths.ContainsKey(transform)) return;
                paths.Add(transform, path);
                foreach (var child in transform.m_Children ?? Enumerable.Empty<PPtr<Transform>>())
                {
                    if (!child.TryGet(out var childTransform) ||
                        !childTransform.m_GameObject.TryGet(out var childObject))
                        continue;
                    var childPath = string.IsNullOrEmpty(path)
                        ? childObject.m_Name
                        : path + "/" + childObject.m_Name;
                    Visit(childTransform, childPath);
                }
            }
            Visit(prefabRoot?.m_Transform, string.Empty);
            return paths;
        }

        private sealed class LogicalAssetResolver
        {
            private readonly IReadOnlyList<VirtualAssetRecord> records;
            private readonly EndfieldBundleDependencyIndex dependencies;

            public LogicalAssetResolver(IReadOnlyList<VirtualAssetRecord> records,
                EndfieldBundleDependencyIndex dependencies)
            {
                this.records = records ?? Array.Empty<VirtualAssetRecord>();
                this.dependencies = dependencies;
            }

            public string Describe(AnimeStudio.Object asset)
            {
                if (asset == null)
                    return "missing";

                var logical = Resolve(asset);
                if (string.IsNullOrWhiteSpace(logical))
                    return string.IsNullOrWhiteSpace(asset.Name) ? "unnamed" : asset.Name;
                return string.IsNullOrWhiteSpace(asset.Name) ||
                       logical.EndsWith(" :: " + asset.Name, StringComparison.Ordinal)
                    ? logical
                    : asset.Name + " [" + logical + "]";
            }

            private string Resolve(AnimeStudio.Object asset)
            {
                var type = asset.type.ToString();
                var candidates = records.Where(x => x.PathId == asset.m_PathID &&
                        string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (candidates.Length == 0)
                    return null;

                var cab = Path.GetFileName(asset.assetsFile?.fileName ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(cab) && dependencies != null)
                {
                    var cabMatches = candidates.Where(x => dependencies.GetCabNames(x.Source)
                        .Contains(cab, StringComparer.OrdinalIgnoreCase)).ToArray();
                    if (cabMatches.Length == 1)
                        return Format(cabMatches[0]);
                    candidates = cabMatches.Length > 0 ? cabMatches : candidates;
                }

                var named = candidates.Where(x => string.Equals(x.Name, asset.Name,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
                return Format(named.Length == 1 ? named[0] :
                    candidates.Length == 1 ? candidates[0] : null);
            }

            private static string Format(VirtualAssetRecord record)
            {
                if (record == null) return null;
                var path = (record.Container ?? string.Empty).Replace('\\', '/');
                return string.IsNullOrWhiteSpace(record.Name) ||
                       string.Equals(record.Name, Path.GetFileNameWithoutExtension(path),
                           StringComparison.OrdinalIgnoreCase)
                    ? path
                    : path + " :: " + record.Name;
            }
        }

        private static string FindSource(VirtualAssetFile file) =>
            file.Records.Select(x => x.Source).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    }
}
