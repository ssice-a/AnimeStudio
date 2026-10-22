using AnimeStudio;
using AnimeStudio.GUI;
using Newtonsoft.Json;
using OpenTK.GLControl;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

internal static class PreviewExportLifecycleTests
{
    internal static int Run(string catalogPath, string output)
    {
        Logger.Silent = true;
        Directory.CreateDirectory(output);
        var catalog = NarakaAppResIndex.Load(catalogPath);
        NarakaPrefabPackageCommand.PrepareMap(catalogPath, catalog);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(-3000, -3000), Size = new(600, 750) };
        using var gl = new GLControl { Dock = DockStyle.Fill, APIVersion = new Version(3, 3), Profile = OpenTK.Windowing.Common.ContextProfile.Core };
        form.Controls.Add(gl); form.Show(); Application.DoEvents(); gl.MakeCurrent();
        var names = new[] {
            "assets/res/prefab/actor_visual_part/ch_f_ming_haikou/ch_f_ming_haikou_hair_lv_s7.prefab",
            "assets/res/prefab/actor_visual_part/ch_f_ming_haikou/ch_f_ming_haikou_hair_lv_s7_ui.prefab",
            "assets/res/prefab/actor_visual_part/ch_f_luotihuaban/ch_f_luotihuaban_lv_c0.prefab",
            "assets/res/prefab/actor_ui_weapon/wp_ui_throwingknife_bfeidao_right.prefab" };
        var failures = new List<string>();
        var results = new List<object>();
        foreach (var path in names)
        {
            var record = catalog.Index.Records.Single(r => r.Container == path);
            // Report source references before invoking either preview or export.
            using (var loader = new NarakaPrefabLoader(record.Source))
            {
                var root = loader.FindRoot(path, record.Source);
                var evidence = new List<object>();
                foreach (var go in EndfieldPrefabDocument.GetHierarchy(root))
                foreach (var pointer in go.m_Components)
                {
                    var component = loader.Resolve(pointer.Cast<AnimeStudio.Object>(), go.assetsFile);
                    if (component?.type != ClassIDType.Transform)
                        evidence.Add(new { gameObject = go.Name, component = component?.type.ToString(),
                            id = component == null ? "" : NarakaPackageWriter.Identity(component), fields = component == null ? null : NarakaPrefabLoader.Fields(component) });
                    if (component != null && NarakaPrefabLoader.Fields(component)?["avatarMeshAsset"] is System.Collections.Specialized.OrderedDictionary reference &&
                        Convert.ToInt64(reference["m_PathID"]) != 0)
                    {
                        var avatar = loader.Resolve(new PPtr<AnimeStudio.Object>(Convert.ToInt32(reference["m_FileID"]),
                            Convert.ToInt64(reference["m_PathID"]), component.assetsFile), component.assetsFile);
                        evidence.Add(new { gameObject = go.Name, component = "avatarMeshAsset", id = NarakaPackageWriter.Identity(avatar), fields = NarakaPrefabLoader.Fields(avatar) });
                    }
                }
                File.WriteAllText(Path.Combine(output, record.Name + ".components.json"), JsonConvert.SerializeObject(evidence, Formatting.Indented));
            }
            NarakaPrefabPreview beforeData = null;
            string Preview(string stage)
            {
                try
                {
                    var data = NarakaPrefabPreview.Load(record, CancellationToken.None);
                    if (stage == "before-export") beforeData = data;
                    var expected = path.Contains("haikou_hair_lv_s7") ? 12 : path.Contains("luotihuaban") ? 8 : 3;
                    if (data.Parts.Count != expected) throw new InvalidDataException("source renderer count differs");
                    Console.WriteLine($"LIFECYCLE {stage} {record.Name}: meshes={data.Parts.Count}; warnings={data.Warnings.Count}");
                    return string.Join("|", data.Parts.Select(p => p.RendererSource));
                }
                catch (Exception error)
                {
                    failures.Add(stage + ": " + path + ": " + error.Message);
                    Console.WriteLine($"LIFECYCLE {stage} FAIL {record.Name}: {error.Message}");
                    return "FAILED";
                }
            }
            var before = Preview("before-export");
            gl.MakeCurrent();
            using var retainedPreview = beforeData == null ? null : new NarakaPreviewRenderer(beforeData);
            byte[] Draw(string stage)
            {
                if (retainedPreview == null) return System.Array.Empty<byte>();
                gl.MakeCurrent();
                var bounds = beforeData!;
                var model = Matrix4.CreateTranslation(-(bounds.Min + bounds.Max) * 0.5f) * Matrix4.CreateScale(1.75f / (bounds.Max - bounds.Min).Length);
                var view = Matrix4.CreateRotationY(MathHelper.DegreesToRadians(15)) * Matrix4.CreateRotationX(MathHelper.DegreesToRadians(5));
                GL.Viewport(0, 0, gl.Width, gl.Height); GL.ClearColor(0.18f, 0.20f, 0.23f, 1);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                retainedPreview.DrawScene(model, view, NarakaPreviewCamera.Projection(gl.Width, gl.Height), 0, true);
                GL.Finish();
                var pixels = new byte[gl.Width * gl.Height * 4];
                GL.ReadPixels(0, 0, gl.Width, gl.Height, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
                if (GL.GetError() != ErrorCode.NoError || pixels.Where((b, i) => i % 4 == 0).Distinct().Count() < 15)
                    throw new InvalidDataException("retained preview has no geometry or GL error after export");
                using var image = ImageExtensions.CreateBitmapFromBgra(pixels, gl.Width, gl.Height);
                using var flipped = image.FlipVertical();
                using var stream = File.Create(Path.Combine(output, record.Name + "." + stage + ".png"));
                flipped.WriteToStream(stream, AnimeStudio.ImageFormat.Png);
                return pixels;
            }
            var beforePixels = Draw("before");
            try
            {
                NarakaPrefabPackageCommand.Export(record, Path.Combine(output, Path.GetFileNameWithoutExtension(record.Name) + ".naraka"));
                Console.WriteLine("LIFECYCLE EXPORT PASS " + record.Name);
            }
            catch (Exception error)
            {
                failures.Add("export: " + path + ": " + error.Message);
                Console.WriteLine("LIFECYCLE EXPORT FAIL " + record.Name + ": " + error.Message);
            }
            // GUI export's cleanup runs under the preview lock.
            AssetsHelper.ClearOffsets();
            var afterPixels = Draw("after");
            if (!beforePixels.SequenceEqual(afterPixels)) failures.Add("retained GPU preview changed after export: " + path);
            var after = Preview("after-export");
            if (before != after) failures.Add("preview identity changed after export: " + path);
            results.Add(new { prefab = path, meshes = beforeData?.Parts.Count, retainedGpuPixelsEqual = beforePixels.SequenceEqual(afterPixels), reloadedRendererIdentitiesEqual = before == after });
        }
        File.WriteAllText(Path.Combine(output, "verification.json"), JsonConvert.SerializeObject(results, Formatting.Indented));
        File.WriteAllLines(Path.Combine(output, "failures.txt"), failures);
        if (failures.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, failures));
        Console.WriteLine("PREVIEW EXPORT LIFECYCLE PASS all source renderers survive before/after export");
        return 0;
    }
}
