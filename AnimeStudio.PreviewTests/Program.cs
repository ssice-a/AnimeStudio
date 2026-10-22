using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using AnimeStudio;
using AnimeStudio.GUI;

internal static class Program
{
    private static readonly Assembly Gui = typeof(AssetItem).Assembly;
    private static readonly BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly Type Record = Gui.GetType("AnimeStudio.GUI.VirtualAssetRecord", true)!;
    private static readonly Type Index = Gui.GetType("AnimeStudio.GUI.VirtualAssetPathIndex", true)!;
    private static readonly Type Form = Gui.GetType("AnimeStudio.GUI.MainForm", true)!;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "--bone-sources") return NarakaBoneSourceTests.Run(args[1], args[2]);
            if (args.Length == 4 && args[0] == "--shader-evidence") return ShaderAlphaEvidence.Dump(args[1], args[3], args[2]);
            if (args.Length == 3 && args[0] == "--export-lifecycle") return PreviewExportLifecycleTests.Run(args[1], args[2]);
            if (args.Length == 1 && args[0] == "--camera") { PreviewOrientationTests.Camera(); return 0; }
            if (args.Length == 2 && args[0] == "--orientation") return PreviewOrientationTests.Source(args[1]);
            if (args.Length == 3 && args[0] == "--shader-alpha") return ShaderAlphaEvidence.Dump(args[1], args[2]);
            if (args.Length == 3 && args[0] == "--prefab") return PrefabPreviewTests.Run(args[1], args[2]);
            if (args.Length == 1 && args[0] == "--states") { PreviewVisibilityTests.MaterialStates(); return 0; }
            if (args.Length == 3 && args[0] == "--prefab-state") return PrefabPreviewTests.Snapshots(args[1], args[2]);
            if (args.Length == 1 && args[0] == "--model") { ModelPreviewMustNotFallBackToTexture(); return 0; }
            CatalogDirectoriesRemainExpandable();
            Console.WriteLine("PREVIEW PASS catalog directories retain every logical file");
            ModelPreviewMustNotFallBackToTexture();
            if (args.Length == 2 && args[0] == "--real") RealCatalog(args[1]);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("PREVIEW FAIL " + (error.InnerException ?? error)); return 1; }
    }

    private static object Entry(string path, long pathId = long.MinValue, bool catalog = true) =>
        Activator.CreateInstance(Record, Path.GetFileName(path), path, "Mesh", pathId, "fixture.bundle", 0L, catalog)!;

    private static object NewIndex() => Activator.CreateInstance(Index, All, null, new object[] { "Assets" }, null)!;
    private static void Add(object index, object entry) => Index.GetMethod("Add", All, new[] { Record })!.Invoke(index, new[] { entry });
    private static object Root(object index) => Index.GetProperty("Root")!.GetValue(index)!;
    private static object Child(object node, string name) => node.GetType().GetProperty("Children")!.GetValue(node)!.GetType().GetProperty("Item")!.GetValue(node.GetType().GetProperty("Children")!.GetValue(node), new object[] { name })!;
    private static TreeNode Children(object node)
    {
        var tree = new TreeNode("parent");
        // Exercise the exact production TreeView builder without launching the
        // MainForm constructor, logger, FMOD or changing saved preferences.
        var form = RuntimeHelpers.GetUninitializedObject(Form);
        Form.GetMethod("AddVirtualAssetTreeChildren", All)!.Invoke(form, new[] { tree, node });
        GC.SuppressFinalize(form);
        return tree;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }

    private static void CatalogDirectoriesRemainExpandable()
    {
        var index = NewIndex();
        Add(index, Entry("assets/hero/model/costume.fbx"));
        Add(index, Entry("assets/hero/model/costume_01.fbx"));
        Add(index, Entry("assets/hero/outfits/skin_00_ui.prefab"));
        Add(index, Entry("assets/hero/outfits/skin_01_ui.prefab"));
        var hero = Child(Child(Root(index), "assets"), "hero");
        var tree = Children(hero);
        var model = tree.Nodes.Cast<TreeNode>().Single(n => n.Text == "model");
        Require(model.Tag?.GetType().Name == "VirtualAssetPathNode" && model.Nodes.Count > 0,
            "model directory became a file leaf; two FBX files are hidden and it cannot expand");
        var files = Children(Child(hero, "model"));
        Require(files.Nodes.Count == 2 && files.Nodes.Cast<TreeNode>().All(n => n.Tag?.GetType().Name == "VirtualAssetFile"),
            "model must reveal both FBX logical files");
        var outfits = tree.Nodes.Cast<TreeNode>().Single(n => n.Text == "outfits");
        Require(outfits.Tag?.GetType().Name == "VirtualAssetPathNode" && outfits.Nodes.Count > 0,
            "costume folder became the first Prefab and hid remaining costumes");

        var legacy = NewIndex();
        Add(legacy, Entry("assets/hero/skin.prefab", 21, false));
        Add(legacy, Entry("assets/hero/skin.prefab", 22, false));
        var legacyTree = Children(Child(Child(Root(legacy), "assets"), "hero"));
        Require(legacyTree.Nodes.Count == 1 && legacyTree.Nodes[0].Tag?.GetType().Name == "VirtualAssetFile",
            "object-indexed Endfield Prefabs must remain single logical file leaves");
    }

    private static void RealCatalog(string catalogPath)
    {
        var loader = Gui.GetType("AnimeStudio.GUI.NarakaAppResIndex", true)!;
        var result = loader.GetMethod("Load", All)!.Invoke(null, new object[] { catalogPath })!;
        var index = result.GetType().GetProperty("Index")!.GetValue(result)!;
        var res = Child(Child(Root(index), "assets"), "res");
        var hero = Child(Child(Child(res, "character"), "female"), "ch_f_christmassnowman");
        var tree = Children(hero);
        var model = tree.Nodes.Cast<TreeNode>().Single(n => n.Text == "model");
        Require(model.Tag?.GetType().Name == "VirtualAssetPathNode" && model.Nodes.Count > 0, "real model folder is not expandable");
        var files = Children(Child(hero, "model"));
        Require(files.Nodes.Count == 2, "real model must expose both catalog FBX paths");
        var visuals = Child(Child(res, "prefab"), "actor_visual_part");
        var heroes = Children(visuals);
        Require(heroes.Nodes.Cast<TreeNode>().Where(n => !n.Text.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            .All(n => n.Tag?.GetType().Name == "VirtualAssetPathNode" && n.Nodes.Count > 0), "actor_visual_part hero folders must not collapse into their first outfit");
        var yulinglong = Children(Child(visuals, "ch_f_ming_yulinglong"));
        Require(yulinglong.Nodes.Count > 100, "Yulinglong directory lost most of its Prefabs");
        Console.WriteLine($"PREVIEW REAL PASS model files={files.Nodes.Count}; hero directories={heroes.Nodes.Count}; Yulinglong Prefabs={yulinglong.Nodes.Count}");
        Logger.Silent = true;
        var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.Naraka), ResolveDependencies = false };
        var source = Path.Combine(Path.GetDirectoryName(catalogPath)!, "3", "b", "3ba96b5b20206299");
        manager.LoadFiles(new[] { source }, mergeSplitAssets: false);
        var resolver = Gui.GetType("AnimeStudio.GUI.LogicalModelObjects", true)!;
        foreach (var name in new[] { "ch_f_christmassnowman.fbx", "ch_f_christmassnowman_01.fbx" })
        {
            var container = "assets/res/character/female/ch_f_christmassnowman/model/" + name;
            var resolved = (IReadOnlyList<Mesh>)resolver.GetMethod("Resolve", All)!.Invoke(null, new object[] { manager.assetsFileList, container })!;
            Require(resolved.Count > 0 && resolved.All(m => m.m_VertexCount > 0), "real model did not resolve actual meshes: " + name);
            Console.WriteLine($"PREVIEW REAL PASS {name}: {resolved.Count} meshes; first={resolved[0].Name}; vertices={resolved.Sum(m => m.m_VertexCount)}");
        }
        manager.Clear();
    }

    private static void ModelPreviewMustNotFallBackToTexture()
    {
        // BuildAssetData assigns a shared preload object to the last container
        // mentioning it. A model path may therefore label only a texture while
        // its Mesh has another container; never preview that texture as a model.
        var texture = RuntimeHelpers.GetUninitializedObject(typeof(AssetItem)) as AssetItem;
        texture!.Container = "assets/hero/model/costume.fbx";
        texture.TypeString = "Texture2D";
        texture.Text = "female_Body_D";
        var studio = Gui.GetType("AnimeStudio.GUI.Studio", true)!;
        var assets = (List<AssetItem>)studio.GetField("exportableAssets", All)!.GetValue(null)!;
        assets.Clear(); assets.Add(texture);
        var form = RuntimeHelpers.GetUninitializedObject(Form);
        try
        {
            var preview = Form.GetMethod("FindLoadedContainerAsset", All)!.Invoke(form, new[] { Entry(texture.Container) });
            Require(preview == null || ((AssetItem)preview).TypeString != "Texture2D", "FBX preview selected a texture from its preload table instead of a Mesh");
        }
        finally { assets.Clear(); GC.SuppressFinalize(form); }
        Console.WriteLine("PREVIEW PASS model containers never use a texture fallback");
    }
}
