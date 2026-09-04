using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio.GUI
{
    internal static class EndfieldPrefabDocument
    {
        public static GameObject FindRoot(IEnumerable<GameObject> objects, VirtualAssetFile file,
            IReadOnlyCollection<string> preferredCabNames)
        {
            var candidates = objects.Where(x =>
                    string.Equals(x.m_Name, file.Stem, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (candidates.Length == 0)
                return null;

            return candidates
                .OrderByDescending(x => preferredCabNames?.Contains(
                    Path.GetFileName(x.assetsFile?.fileName ?? string.Empty),
                    StringComparer.OrdinalIgnoreCase) == true)
                .ThenByDescending(IsRoot)
                .ThenByDescending(x => x.HasModel())
                .ThenBy(x => x.m_PathID)
                .FirstOrDefault();
        }

        public static string Build(VirtualAssetFile file, GameObject root)
        {
            var builder = new StringBuilder(32 * 1024);
            builder.AppendLine($"Prefab: {file.Container}");
            builder.AppendLine($"Root: {Describe(root)}");
            builder.AppendLine($"Source: {FindSource(file)}");
            builder.AppendLine();
            builder.AppendLine("Hierarchy and component references:");

            var visited = new HashSet<long>();
            WriteGameObject(builder, root, 0, visited);
            builder.AppendLine();
            builder.AppendLine($"GameObjects: {visited.Count:N0}");
            builder.AppendLine("Note: this is the serialized Prefab object graph, not a byte-for-byte Unity Editor source .prefab file.");
            return builder.ToString();
        }

        public static IReadOnlyList<GameObject> GetHierarchy(GameObject root)
        {
            var result = new List<GameObject>();
            var visited = new HashSet<long>();
            var pending = new Stack<GameObject>();
            if (root != null)
                pending.Push(root);
            while (pending.Count > 0)
            {
                var gameObject = pending.Pop();
                if (gameObject == null || !visited.Add(gameObject.m_PathID))
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
            HashSet<long> visited)
        {
            if (gameObject == null || depth > 512 || !visited.Add(gameObject.m_PathID))
                return;

            var indent = new string(' ', depth * 2);
            builder.Append(indent).Append("- ").AppendLine(Describe(gameObject));

            foreach (var componentPtr in gameObject.m_Components)
            {
                if (!componentPtr.TryGet(out var component))
                    continue;
                builder.Append(indent).Append("  [").Append(component.type).Append("] PathID=")
                    .Append(component.m_PathID);

                switch (component)
                {
                    case SkinnedMeshRenderer skinned:
                        AppendMeshAndMaterials(builder, skinned.m_Mesh, skinned.m_Materials);
                        builder.Append(" Bones=").Append(skinned.m_Bones?.Count ?? 0);
                        if (skinned.m_RootBone.TryGet(out var rootBone))
                            builder.Append(" RootBone=").Append(rootBone.Name)
                                .Append("@").Append(rootBone.m_PathID);
                        break;
                    case MeshRenderer renderer:
                        if (gameObject.m_MeshFilter?.m_Mesh != null)
                            AppendMeshAndMaterials(builder, gameObject.m_MeshFilter.m_Mesh,
                                renderer.m_Materials);
                        else
                            AppendMaterials(builder, renderer.m_Materials);
                        break;
                    case MeshFilter filter:
                        if (filter.m_Mesh.TryGet(out var mesh))
                            builder.Append(" Mesh=").Append(mesh.Name).Append("@").Append(mesh.m_PathID);
                        break;
                    case Animator animator:
                        if (animator.m_Avatar.TryGet(out var avatar))
                            builder.Append(" Avatar=").Append(avatar.Name).Append("@").Append(avatar.m_PathID);
                        if (animator.m_Controller.TryGet(out var controller))
                            builder.Append(" Controller=").Append(controller.Name).Append("@").Append(controller.m_PathID);
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
                    WriteGameObject(builder, childObject, depth + 1, visited);
            }
        }

        private static void AppendMeshAndMaterials(StringBuilder builder, PPtr<Mesh> meshPtr,
            List<PPtr<Material>> materials)
        {
            if (meshPtr != null && meshPtr.TryGet(out var mesh))
                builder.Append(" Mesh=").Append(mesh.Name).Append("@").Append(mesh.m_PathID)
                    .Append(" SubMeshes=").Append(mesh.m_SubMeshes?.Count ?? 0);
            AppendMaterials(builder, materials);
        }

        private static void AppendMaterials(StringBuilder builder, List<PPtr<Material>> materials)
        {
            if (materials == null || materials.Count == 0)
                return;
            builder.Append(" Materials=[");
            for (var i = 0; i < materials.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                if (materials[i].TryGet(out var material))
                    builder.Append(material.Name).Append("@").Append(material.m_PathID);
                else
                    builder.Append("missing@").Append(materials[i].m_PathID);
            }
            builder.Append(']');
        }

        private static bool IsRoot(GameObject gameObject) =>
            gameObject?.m_Transform != null && gameObject.m_Transform.m_Father.IsNull;

        private static string Describe(GameObject gameObject) => gameObject == null
            ? "[not found]"
            : $"{gameObject.m_Name} [GameObject] PathID={gameObject.m_PathID} " +
              $"CAB={gameObject.assetsFile?.fileName} Root={IsRoot(gameObject)} HasModel={gameObject.HasModel()}";

        private static string FindSource(VirtualAssetFile file) =>
            file.Records.Select(x => x.Source).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    }
}
