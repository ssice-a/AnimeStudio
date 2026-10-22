using AnimeStudio;
using AnimeStudio.GUI;
using Newtonsoft.Json;

internal static class ShaderAlphaEvidence
{
    // Read-only diagnostic: retain actual pass/program evidence for adapters.
    internal static int Dump(string catalogPath, string output, string prefab = null)
    {
        Logger.Silent = true;
        var catalog = NarakaAppResIndex.Load(catalogPath);
        NarakaPrefabPackageCommand.PrepareMap(catalogPath, catalog);
        var record = catalog.Index.Records.Single(r => r.Container == (prefab ??
            "assets/res/prefab/actor_visual_part/ch_f_ming_yulinglong/ch_f_ming_yulinglong_lv_s0_ui.prefab"));
        using var loader = new NarakaPrefabLoader(record.Source);
        var root = loader.FindRoot(record.Container, record.Source);
        Directory.CreateDirectory(output);
        var visited = new HashSet<string>();
        foreach (var go in EndfieldPrefabDocument.GetHierarchy(root))
        {
            Renderer renderer = go.m_SkinnedMeshRenderer ?? (Renderer)go.m_MeshRenderer;
            if (renderer == null) continue;
            foreach (var ptr in renderer.m_Materials)
            {
                var mat = loader.Require(ptr, renderer.assetsFile);
                foreach (var pair in mat.m_SavedProperties.m_TexEnvs)
                {
                    var tex = loader.Resolve(pair.Value.m_Texture.Cast<AnimeStudio.Object>(), mat.assetsFile);
                    if (tex?.type == ClassIDType.Texture2DArray && visited.Add("array:" + tex.m_PathID))
                        File.WriteAllText(Path.Combine(output, tex.Name + ".array.json"), JsonConvert.SerializeObject(NarakaPrefabLoader.Fields(tex)));
                }
                var shader = loader.Require(mat.m_Shader.Cast<AnimeStudio.Object>(), mat.assetsFile);
                var path = loader.LogicalPath(shader);
                if (!visited.Add(path) || (prefab == null && !path.EndsWith("lx22standardcharacterclothsilk.shader"))) continue;
                File.WriteAllText(Path.Combine(output, Path.GetFileName(path) + ".json"),
                    JsonConvert.SerializeObject(NarakaPrefabLoader.Fields(shader)));
                Console.WriteLine("SHADER EVIDENCE " + path);
            }
        }
        return 0;
    }
}
