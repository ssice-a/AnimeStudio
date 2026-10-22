using System.Drawing;
using System.Text.Json;
using AnimeStudio;
using AnimeStudio.GUI;
using OpenTK.GLControl;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

internal static class PrefabPreviewTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    internal static int Snapshots(string catalogPath, string output)
    {
        Logger.Silent = true;
        var catalog = NarakaAppResIndex.Load(catalogPath);
        NarakaPrefabPackageCommand.PrepareMap(catalogPath, catalog);
        var results = new List<object>();
        foreach (var (suffix, count) in new[] { ("s16", 59), ("s0", 52) })
        {
            var path = $"assets/res/prefab/actor_visual_part/ch_f_ming_yulinglong/ch_f_ming_yulinglong_lv_{suffix}_ui.prefab";
            var record = catalog.Index.Records.Single(r => r.Container == path);
            var data = NarakaPrefabPreview.Load(record, CancellationToken.None);
            Check(data.Parts.Count == count, "source mesh count changed");
            var materials = data.Parts.SelectMany(p => p.Materials).Where(m => m.Source.Length > 0).Distinct().ToArray();
            Check(materials.Any(m => m.Cull == 0) && materials.Any(m => m.Cull == 1) && materials.Any(m => m.Cull == 2), "source single/double-sided state lost");
            Check(materials.Any(m => m.Blend) && materials.Any(m => !m.Blend), "source opaque/transparent distinction lost");
            if (suffix == "s16")
                Check(materials.Count(m => m.Blend && !m.ZWrite && m.SilkCoverage) == 9, "periodic transparent pass/depth setup incorrect");
            else
                Check(materials.Any(m => m.Blend && m.ZWrite) && materials.Count(m => m.DepthOnly) == 2, "silk/depth-only passes incorrect");
            results.Add(new { prefab = path, parts = count, materials = materials.Select(m => new
            { m.Name, m.Cull, m.Cutout, m.Cutoff, m.Blend, m.SilkCoverage, m.AlphaResolved, m.UseBaseAlpha, m.AlphaReason, m.ZWrite, m.ZTest, m.Queue, m.SrcBlend, m.DstBlend, m.DepthOnly }) });
            if (suffix == "s16")
                Check(materials.Where(m => m.Name == "female_Body" || m.Name.Contains("body02") || m.Name.Contains("cloth01"))
                    .All(m => !m.Blend && !m.Cutout && !m.SilkCoverage), "opaque source materials consume diffuse alpha");
            Console.WriteLine($"VISIBILITY SOURCE PASS {suffix}: {count} renderers; per-material alpha policy checked (not a GPU pass)");
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "material-state-verification.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    internal static int Run(string catalogPath, string output)
    {
        Logger.Silent = true;
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(-3000, -3000), Size = new(800, 900) };
        using var gl = new GLControl { Dock = DockStyle.Fill, APIVersion = new Version(3, 3), Profile = OpenTK.Windowing.Common.ContextProfile.Core };
        form.Controls.Add(gl); form.Show(); Application.DoEvents(); gl.MakeCurrent();
        Console.WriteLine("VISIBILITY GL " + GL.GetString(StringName.Version));
        PreviewVisibilityTests.Run(gl);
        var catalog = NarakaAppResIndex.Load(catalogPath);
        NarakaPrefabPackageCommand.PrepareMap(catalogPath, catalog);
        var results = new List<object>();
        foreach (var (suffix, total, visible, materials, textures) in new[] { ("s16", 59, 18, 12, 3), ("s0", 52, 15, 15, 4) })
        {
            var prefab = $"assets/res/prefab/actor_visual_part/ch_f_ming_yulinglong/ch_f_ming_yulinglong_lv_{suffix}_ui.prefab";
            var record = catalog.Index.Records.Single(r => r.Container == prefab);
            var data = NarakaPrefabPreview.Load(record, CancellationToken.None);
            Check(data.Parts.Count == total, $"{suffix}: lost renderer instances, expected {total}, got {data.Parts.Count}");
            Check(data.Parts.Select(p => p.RendererSource).Distinct().Count() == total, "merged renderer identity");
            Check(data.Parts.SelectMany(p => p.Materials).Where(m => m.Source.Length > 0).Distinct().Count() == materials,
                $"material slot association changed: {string.Join(", ", data.Parts.SelectMany(p => p.Materials).Select(m => m.Name).Distinct())}");
            Check(data.Parts.SelectMany(p => p.Materials).Select(m => m.Texture).Where(t => t != null).Distinct().Count() >= textures, "missing base color textures");
            Check(data.Parts.All(p => p.Vertices.All(float.IsFinite)), "non-finite world geometry");
            Check(data.Max.Y - data.Min.Y > 1.5f && data.Max.Y - data.Min.Y < 2.3f, "Prefab world rotation is missing; character is not upright");
            foreach (var part in data.Parts)
                Check(part.Indices.Length == part.Materials.Length && part.Indices.All(a => a.All(i => i >= 0 && i < part.Vertices.Length / 10)), "invalid submesh/material mapping");
            gl.MakeCurrent();
            using var renderer = new NarakaPreviewRenderer(data);
            Check(renderer.VisibleCount > 0 && renderer.VisibleCount < total, "LOD/source visibility was not preserved");
            Check(renderer.VisibleCount == visible, suffix + " visible renderer set differs from source states/LOD");
            var extent = (data.Max - data.Min).Length;
            var model = Matrix4.CreateTranslation(-(data.Min + data.Max) * 0.5f) * Matrix4.CreateScale(1.75f / extent);
            var view = Matrix4.CreateRotationY(MathHelper.DegreesToRadians(15)) * Matrix4.CreateRotationX(MathHelper.DegreesToRadians(5));
            var projection = NarakaPreviewCamera.Projection(gl.Width, gl.Height);
            byte[] Render(string name, int wire = 0)
            {
                GL.Viewport(0, 0, gl.Width, gl.Height); GL.ClearColor(0.18f, 0.20f, 0.23f, 1);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit); GL.Enable(EnableCap.DepthTest);
                renderer.DrawScene(model, view, projection, wire, true);
                GL.Finish();
                var pixels = new byte[gl.Width * gl.Height * 4];
                GL.ReadPixels(0, 0, gl.Width, gl.Height, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
                Check(GL.GetError() == ErrorCode.NoError, "GL error after preview drawing");
                using var image = ImageExtensions.CreateBitmapFromBgra(pixels, gl.Width, gl.Height);
                using var flipped = image.FlipVertical();
                using var stream = File.Create(Path.Combine(output, name)); flipped.WriteToStream(stream, AnimeStudio.ImageFormat.Png);
                Check(pixels.Where((b, i) => i % 4 == 0).Distinct().Count() > 35, "preview framebuffer has no textured geometry");
                return pixels;
            }
            var normal = Render(suffix + "-prefab.png");
            renderer.ShowAll = true; Check(renderer.VisibleCount == total, "Show all omits renderer instances");
            var all = Render(suffix + "-all.png");
            renderer.Selection = data.Parts.FindIndex(p => p.Name == "body02");
            Check(renderer.Selection >= 0 && renderer.VisibleCount == 1, "individual Mesh selection failed");
            var single = Render(suffix + "-body02.png");
            Check(!normal.SequenceEqual(single), "individual selection still draws the whole Prefab");
            Render(suffix + "-wire.png", 2);
            results.Add(new { prefab, meshes = total, defaultVisible = data.Parts.Count(p => p.Visible && !p.LowerLod),
                materials, baseColorTextures = data.Parts.SelectMany(p => p.Materials).Select(m => m.Texture).Where(t => t != null).Distinct().Count(),
                bounds = new[] { new[] { data.Min.X, data.Min.Y, data.Min.Z }, new[] { data.Max.X, data.Max.Y, data.Max.Z } }, data.Warnings });
            Console.WriteLine($"PREFAB PREVIEW PASS {suffix}: {total} renderers, {materials} materials, default/all/single/wire GL output validated");
        }
        File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        WeaponViews(gl, catalog, output);
        return 0;
    }

    private static void WeaponViews(GLControl gl, NarakaAppResIndex.LoadResult catalog, string output)
    {
        var prefab = "assets/res/prefab/actor_ui_weapon/wp_ui_throwingknife_bfeidao_right.prefab";
        var record = catalog.Index.Records.Single(r => r.Container == prefab);
        var data = NarakaPrefabPreview.Load(record, CancellationToken.None);
        Check(data.Parts.Count == 3, "selected throwing-knife lost source renderers");
        gl.MakeCurrent();
        using var renderer = new NarakaPreviewRenderer(data);
        var model = Matrix4.CreateTranslation(-(data.Min + data.Max) * 0.5f) * Matrix4.CreateScale(1.75f / (data.Max - data.Min).Length);
        foreach (var angle in new[] { 15, 75, 195, 285 })
        {
            var view = Matrix4.CreateRotationY(MathHelper.DegreesToRadians(angle)) * Matrix4.CreateRotationX(MathHelper.DegreesToRadians(5));
            GL.Viewport(0, 0, gl.Width, gl.Height);
            GL.ClearColor(0.18f, 0.20f, 0.23f, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            renderer.DrawScene(model, view, NarakaPreviewCamera.Projection(gl.Width, gl.Height), 0, true);
            GL.Finish();
            var pixels = new byte[gl.Width * gl.Height * 4];
            GL.ReadPixels(0, 0, gl.Width, gl.Height, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
            Check(GL.GetError() == ErrorCode.NoError && pixels.Where((b, i) => i % 4 == 0).Distinct().Count() > 35,
                "throwing-knife view has no textured geometry or GL error");
            using var image = ImageExtensions.CreateBitmapFromBgra(pixels, gl.Width, gl.Height);
            using var flipped = image.FlipVertical();
            using var stream = File.Create(Path.Combine(output, $"throwingknife-{angle}.png"));
            flipped.WriteToStream(stream, AnimeStudio.ImageFormat.Png);
        }
        Console.WriteLine("ORIENTATION WEAPON GL PASS selected throwing-knife, 3 renderers, 4 camera angles");
    }
}
