using AnimeStudio.GUI;
using System.Windows.Forms;

internal static class EndfieldMeshTreeSelectionTests
{
    internal static void Run()
    {
        var mesh = new VirtualAssetRecord("nefys_body", "models/nefys_body.asset", "Mesh", 101,
            "bundles/nefys.bundle");
        var duplicateMesh = mesh with
        {
            Container = "MODELS/NEFYS_BODY.ASSET",
            Source = "BUNDLES/NEFYS.BUNDLE"
        };
        var otherMesh = new VirtualAssetRecord("nefys_hair", "models/nefys.fbx", "Mesh", 202,
            "bundles/nefys.bundle");
        var nonMesh = new VirtualAssetRecord("nefys_root", "models/nefys.fbx", "GameObject", 303,
            "bundles/nefys.bundle");

        var tree = new TreeView { CheckBoxes = true };
        tree.Nodes.Add(new TreeNode("nefys")
        {
            Nodes =
            {
                new TreeNode("nefys.fbx")
                {
                    Checked = true,
                    Tag = new VirtualAssetFile("models/nefys.fbx", new[] { otherMesh, nonMesh })
                },
                new TreeNode("nefys_body.asset")
                {
                    Checked = true,
                    Tag = new VirtualAssetFile("models/nefys_body.asset", new[] { mesh, duplicateMesh })
                },
                new TreeNode("direct mesh node") { Checked = true, Tag = otherMesh }
            }
        });

        var selection = EndfieldMeshTreeSelectionCollector.CollectChecked(tree.Nodes.Cast<TreeNode>());
        Check(selection.HasCheckedAssetNodes, "checked Scene Hierarchy files must be recognized");
        Check(selection.Meshes.Count == 2, "checked files and direct Mesh nodes must be deduplicated");
        Check(selection.Meshes[0] == otherMesh && selection.Meshes[1] == mesh,
            "Mesh records must retain hierarchy order while excluding non-Mesh objects");

        Console.WriteLine("ENDFIELD-MESH-TREE-SELECTION passed");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("ENDFIELD-MESH-TREE-SELECTION failed: " + message);
    }
}
