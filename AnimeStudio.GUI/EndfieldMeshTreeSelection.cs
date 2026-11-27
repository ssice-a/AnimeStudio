using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace AnimeStudio.GUI
{
    internal sealed record EndfieldMeshTreeSelection(
        bool HasCheckedAssetNodes,
        IReadOnlyList<VirtualAssetRecord> Meshes);

    internal static class EndfieldMeshTreeSelectionCollector
    {
        internal static EndfieldMeshTreeSelection CollectChecked(IEnumerable<TreeNode> roots)
        {
            var meshes = new List<VirtualAssetRecord>();
            var seenMeshes = new HashSet<VirtualAssetRecord>(MeshIdentityComparer.Instance);
            var hasCheckedAssetNodes = false;

            void AddMesh(VirtualAssetRecord record)
            {
                if (record.Type.Equals("Mesh", StringComparison.OrdinalIgnoreCase) && seenMeshes.Add(record))
                    meshes.Add(record);
            }

            void Visit(TreeNode node)
            {
                if (node.Checked)
                {
                    if (node.Tag is VirtualAssetFile file)
                    {
                        hasCheckedAssetNodes = true;
                        foreach (var record in file.Records)
                            AddMesh(record);
                    }
                    else if (node.Tag is VirtualAssetRecord record)
                    {
                        hasCheckedAssetNodes = true;
                        AddMesh(record);
                    }
                }

                foreach (TreeNode child in node.Nodes)
                    Visit(child);
            }

            foreach (var root in roots)
                Visit(root);

            return new EndfieldMeshTreeSelection(hasCheckedAssetNodes, meshes);
        }

        private sealed class MeshIdentityComparer : IEqualityComparer<VirtualAssetRecord>
        {
            internal static readonly MeshIdentityComparer Instance = new();

            public bool Equals(VirtualAssetRecord left, VirtualAssetRecord right) =>
                ReferenceEquals(left, right) || left != null && right != null &&
                left.PathId == right.PathId &&
                string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
                string.Equals(left.Container, right.Container, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.Source, right.Source, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(VirtualAssetRecord record) => HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(record.Name ?? string.Empty),
                StringComparer.OrdinalIgnoreCase.GetHashCode(record.Container ?? string.Empty),
                StringComparer.OrdinalIgnoreCase.GetHashCode(record.Source ?? string.Empty),
                record.PathId);
        }
    }
}
