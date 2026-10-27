using System.Diagnostics;
using System.Text.Json;
using AnimeStudio;
using AnimeStudio.GUI;
using OpenTK.GLControl;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

internal static class NarakaPrefabProbe
{
    internal static int Bundle(string path)
    {
        Logger.Silent = false;
        Logger.Flags = LoggerEvent.All;
        Logger.Default = new ProbeLogger();
        using var reader = new FileReader(path);
        var bundle = new BundleFile(reader, GameManager.GetGameByType(GameType.Naraka), lazyIndex: true);
        Console.WriteLine($"BUNDLE dataStart=0x{reader.Position:X} {bundle.m_Header}");
        var blocks = (List<BundleFile.StorageBlock>)typeof(BundleFile).GetField("m_BlocksInfo",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(bundle)!;
        Console.WriteLine($"BUNDLE blocks={blocks.Count} first={string.Join("; ", blocks.Take(4))}");
        var physicalEnd = reader.Position;
        foreach (var block in blocks) physicalEnd = ((physicalEnd + 0xFFF) & ~0xFFFL) + block.compressedSize;
        Console.WriteLine($"BUNDLE LAYOUT end=0x{physicalEnd:X} fileEnd=0x{reader.Length:X}");
        try
        {
            foreach (var file in bundle.fileList)
            {
                var bytes = new byte[Math.Min(64, file.stream.Length)];
                try
                {
                    file.stream.ReadExactly(bytes);
                    Console.WriteLine($"BUNDLE NODE PASS {file.path} size=0x{file.stream.Length:X} head={Convert.ToHexString(bytes)}");
                }
                catch (Exception error) { Console.WriteLine($"BUNDLE NODE FAIL {file.path}: {error}"); return 1; }
            }
            return 0;
        }
        finally { foreach (var file in bundle.fileList) file.stream.Dispose(); }
    }

    private sealed class ProbeLogger : ILogger
    {
        public void Log(LoggerEvent kind, string message)
        {
            if (kind == LoggerEvent.Error || message.Contains("Naraka blocks info:") || message.Contains("Bundle header Info:"))
                Console.WriteLine($"BUNDLE {kind} {message}");
        }
    }

    internal static int Run(string catalogPath, string filter, string output, int limit, bool render = false)
    {
        Logger.Silent = false;
        Logger.Flags = LoggerEvent.Error;
        Logger.Default = new ConsoleLogger();
        var catalog = NarakaAppResIndex.Load(catalogPath);
        var records = catalog.Index.Records.Where(r => r.Container.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
            && r.Container.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Container.EndsWith("_ui.prefab", StringComparison.OrdinalIgnoreCase))
            .ThenBy(r => r.Container, StringComparer.Ordinal).ToArray();
        Console.WriteLine($"PREFAB PROBE matches={records.Length} limit={limit}");
        NarakaPrefabPackageCommand.PrepareMap(catalogPath, catalog);
        var results = new List<object>();
        var failed = 0;
        foreach (var record in records.Take(limit))
        {
            var timer = Stopwatch.StartNew();
            try
            {
                var data = NarakaPrefabPreview.Load(record, CancellationToken.None);
                var visible = data.Parts.Count(p => p.Visible && !p.LowerLod);
                var finite = data.Parts.All(p => p.Vertices.All(float.IsFinite));
                var bounds = new[] { data.Min.X, data.Min.Y, data.Min.Z, data.Max.X, data.Max.Y, data.Max.Z };
                var textured = data.Parts.SelectMany(p => p.Materials).Count(m => m.Texture != null);
                if (data.Parts.Count == 0 || !finite || !bounds.All(float.IsFinite)) failed++;
                if (render) Render(data, output);
                results.Add(new { prefab = record.Container, record.Source, meshes = data.Parts.Count,
                    visible, finite, textured, bounds, data.Warnings, elapsedMs = timer.ElapsedMilliseconds });
                Console.WriteLine($"PREFAB PROBE {(data.Parts.Count > 0 && finite && bounds.All(float.IsFinite) ? "PASS" : "EMPTY")} meshes={data.Parts.Count} visible={visible} textured={textured} {record.Container}");
            }
            catch (Exception error)
            {
                failed++;
                results.Add(new { prefab = record.Container, record.Source, error = error.ToString(), elapsedMs = timer.ElapsedMilliseconds });
                Console.WriteLine($"PREFAB PROBE FAIL {record.Container}: {error}");
            }
        }
        Directory.CreateDirectory(Path.GetFullPath(output));
        File.WriteAllText(Path.Combine(output, "prefab-probe.json"), JsonSerializer.Serialize(new
            { filter, matches = records.Length, tested = results.Count, failed, records = records.Select(r => r.Container), results },
            new JsonSerializerOptions { WriteIndented = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        return records.Length > 0 && failed == 0 ? 0 : 1;
    }

    private static void Render(NarakaPrefabPreview data, string output)
    {
        Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new(-3000, -3000), Size = new(800, 900) };
        using var gl = new GLControl { Dock = DockStyle.Fill, APIVersion = new Version(3, 3),
            Profile = OpenTK.Windowing.Common.ContextProfile.Core };
        form.Controls.Add(gl); form.Show(); Application.DoEvents(); gl.MakeCurrent();
        using var renderer = new NarakaPreviewRenderer(data);
        if (renderer.VisibleCount == 0) throw new InvalidDataException("No visible Prefab geometry");
        var extent = (data.Max - data.Min).Length;
        var model = Matrix4.CreateTranslation(-(data.Min + data.Max) * 0.5f) * Matrix4.CreateScale(1.75f / extent);
        var view = Matrix4.CreateRotationY(MathHelper.DegreesToRadians(15)) * Matrix4.CreateRotationX(MathHelper.DegreesToRadians(5));
        GL.Viewport(0, 0, gl.Width, gl.Height); GL.ClearColor(0.18f, 0.20f, 0.23f, 1);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit); GL.Enable(EnableCap.DepthTest);
        renderer.DrawScene(model, view, NarakaPreviewCamera.Projection(gl.Width, gl.Height), 0, true);
        GL.Finish();
        var pixels = new byte[gl.Width * gl.Height * 4];
        GL.ReadPixels(0, 0, gl.Width, gl.Height, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
        if (GL.GetError() != ErrorCode.NoError || pixels.Where((b, i) => i % 4 == 0).Distinct().Count() <= 35)
            throw new InvalidDataException("GL preview is empty or failed");
        using var image = ImageExtensions.CreateBitmapFromBgra(pixels, gl.Width, gl.Height);
        using var flipped = image.FlipVertical();
        using var stream = File.Create(Path.Combine(output, "prefab.png"));
        flipped.WriteToStream(stream, AnimeStudio.ImageFormat.Png);
        Console.WriteLine($"PREFAB GL PASS visible={renderer.VisibleCount} {GL.GetString(StringName.Version)}");
    }
}
