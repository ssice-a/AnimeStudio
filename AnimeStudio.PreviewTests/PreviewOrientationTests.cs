using AnimeStudio;
using AnimeStudio.GUI;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Vector4 = OpenTK.Mathematics.Vector4;

internal static class PreviewOrientationTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    internal static void Camera()
    {
        // GL CCW front faces have an outward +Z normal in this orthographic
        // camera. They must be nearer than the opposite side of a closed mesh.
        foreach (var (width, height) in new[] { (800, 900), (900, 600), (512, 512) })
        {
            var projection = NarakaPreviewCamera.Projection(width, height);
            var outer = Vector4.TransformRow(new Vector4(0, 0, 0.4f, 1), projection);
            var inner = Vector4.TransformRow(new Vector4(0, 0, -0.4f, 1), projection);
            Check(outer.Z / outer.W < inner.Z / inner.W,
                "production camera puts the outward-facing surface behind the opposite/inside surface");
        }
        Console.WriteLine("ORIENTATION CAMERA PASS front faces are nearest in the production orthographic projection");
    }

    internal static int Source(string catalogPath)
    {
        Logger.Silent = true;
        var catalog = NarakaAppResIndex.Load(catalogPath);
        NarakaPrefabPackageCommand.PrepareMap(catalogPath, catalog);
        foreach (var prefab in new[] {
            "assets/res/prefab/actor_ui_weapon/wp_ui_throwingknife_bfeidao_right.prefab",
            "assets/res/prefab/actor_visual_part/ch_f_ming_yulinglong/ch_f_ming_yulinglong_lv_s16_ui.prefab" })
        {
            var record = catalog.Index.Records.Single(r => r.Container == prefab);
            var data = NarakaPrefabPreview.Load(record, CancellationToken.None);
            long aligned = 0, opposed = 0;
            foreach (var part in data.Parts.Where(p => p.Visible && !p.LowerLod))
            foreach (var indices in part.Indices)
            for (var i = 0; i < indices.Length; i += 3)
            {
                Vector3 Position(int index) => new(part.Vertices[index * 10], part.Vertices[index * 10 + 1], part.Vertices[index * 10 + 2]);
                Vector3 Normal(int index) => new(part.Vertices[index * 10 + 3], part.Vertices[index * 10 + 4], part.Vertices[index * 10 + 5]);
                var a = indices[i]; var b = indices[i + 1]; var c = indices[i + 2];
                var cross = Vector3.Cross(Position(b) - Position(a), Position(c) - Position(a));
                var dot = Vector3.Dot(cross, Normal(a) + Normal(b) + Normal(c));
                if (dot > 1e-10f) aligned++; else if (dot < -1e-10f) opposed++;
            }
            Console.WriteLine($"ORIENTATION SOURCE {prefab}: aligned={aligned}; opposed={opposed}");
            Check(aligned > opposed * 10, "source normals disagree with converted front-face winding");
        }
        Camera();
        return 0;
    }
}
