using AnimeStudio.GUI;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using OpenTK.GLControl;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using Vec3 = OpenTK.Mathematics.Vector3;
using Vec4 = OpenTK.Mathematics.Vector4;

internal static class PreviewVisibilityTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static NarakaPreviewMaterial Mat(Vec4 tint, int cull = 0, bool clip = false) =>
        new("fixture", "fixture", null, tint, new(1, 1, 0, 0), 0, cull, clip, 0.5f, UseBaseAlpha: true);
    private static NarakaPreviewPart Quad(string name, float depth, NarakaPreviewMaterial material, bool reverse = false)
    {
        var vertices = new List<float>();
        foreach (var (x, y, u, v) in new[] { (-0.8f, -0.8f, 0f, 0f), (0.8f, -0.8f, 1f, 0f), (0.8f, 0.8f, 1f, 1f), (-0.8f, 0.8f, 0f, 1f) })
            vertices.AddRange(new[] { x, y, depth, 0, 0, 1, u, v, u, v });
        return new(name, name, name, name, vertices.ToArray(), new[] { reverse ? new[] { 0, 2, 1, 0, 3, 2 } : new[] { 0, 1, 2, 0, 2, 3 } }, new[] { material }, true, false);
    }

    internal static void Run(GLControl gl)
    {
        gl.MakeCurrent();
        (byte R, byte G, byte B) Pixel(params NarakaPreviewPart[] parts)
        {
            var data = new NarakaPrefabPreview(); data.Parts.AddRange(parts);
            using var renderer = new NarakaPreviewRenderer(data);
            GL.Viewport(0, 0, gl.Width, gl.Height);
            GL.DepthMask(true); GL.ClearColor(0, 0, 0, 1);
            GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Lequal);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            renderer.DrawScene(Matrix4.Identity, Matrix4.Identity,
                NarakaPreviewCamera.Projection(gl.Width, gl.Height), 0, false);
            GL.Finish();
            var rgba = new byte[4]; GL.ReadPixels(gl.Width/2, gl.Height/2, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
            Check(GL.GetError() == ErrorCode.NoError, "visibility fixture GL error");
            return (rgba[0], rgba[1], rgba[2]);
        }
        var red = Mat(new(1, 0, 0, 1)); var blue = Mat(new(0, 0, 1, 1));
        // Opposite sides of a closed shell: outward +Z is the side facing this
        // camera, independent of whether the material draws both sides.
        var shellOutside = Quad("outside +Z", 0.4f, red);
        var shellOpposite = Quad("opposite -Z", -0.4f, blue, true);
        var shell = Pixel(shellOutside, shellOpposite);
        Check(shell.R > 240 && shell.B < 5 && shell == Pixel(shellOpposite, shellOutside),
            "closed double-sided shell shows the inside/opposite texture through the outward surface");
        var rgba = red with { Texture = new(new byte[] { 0, 0, 255, 0 }, 1, 1, 0, 0, false) };
        var opaqueRgba = NarakaPreviewMaterialState.Apply(rgba, new Dictionary<string,float> { ["_Cutoff"] = 0.5f }, "", "lx22standardcharacterskin.shader", -1);
        Check(Pixel(Quad("opaque diffuse A=0", 0, opaqueRgba)).R > 240, "data-bearing diffuse A makes an opaque material disappear");
        Check(Pixel(Quad("front", 0, red, false)).R > 240, "front winding is not visible");
        Check(Pixel(Quad("front-cull-back", 0, red with { Cull = 2 })).R > 240, "back culling removes front faces");
        Check(Pixel(Quad("back-cull-back", 0, red with { Cull = 2 }, true)).R == 0, "back culling does not remove back faces");
        Check(Pixel(Quad("front-cull-front", 0, red with { Cull = 1 })).R == 0, "front culling does not remove front faces");
        Check(Pixel(Quad("back-cull-front", 0, red with { Cull = 1 }, true)).R > 240, "front culling removes back faces");
        Check(Pixel(Quad("back-double-sided", 0, red, true)).R > 240, "double-sided material loses its back face");
        var front = Quad("near", 0.4f, red); var back = Quad("far", -0.4f, blue);
        var a = Pixel(front, back); var b = Pixel(back, front);
        Check(a.R > 240 && a.B < 5 && a == b, "opaque occlusion depends on draw order or depth buffer state");
        var hole = Quad("cutout", 0.4f, Mat(new(1, 0, 0, 0), clip: true));
        Check(Pixel(hole, back).B > 240, "discarded alpha fragments still occlude the mesh behind");
        var glass = Quad("half alpha", 0.4f, Mat(new(1, 0, 0, 0.5f)) with { Blend = true, ZWrite = false, Queue = 3000 });
        var blended = Pixel(glass, back);
        Check(blended.R > 100 && blended.B > 100, "half-transparent foreground is rendered opaque; background is lost");
        Check(Pixel(back, glass) == blended, "transparent layer ignores camera ordering");
        var farGlass = Quad("far glass", -0.1f, Mat(new(0, 1, 0, 0.5f)) with { Blend = true, ZWrite = false, Queue = 3000 });
        Check(Pixel(glass, farGlass, back) == Pixel(back, farGlass, glass), "transparent layers change with source list order");
        var covered = Pixel(Quad("near opaque", 0.7f, red), farGlass, back);
        Check(covered.R > 240 && covered.G < 5 && covered.B < 5, "transparent geometry shows through an opaque foreground");
        var remap = Quad("alpha remap", 0.4f, Mat(new(1, 0, 0, 0)) with { Blend = true, SilkCoverage = true, Cutoff = 0.93f, ZWrite = false, Queue = 3000 });
        Check(Pixel(remap, back).B > 240, "zero-coverage silk pixels occlude the background");
        MaterialStates();
        Check(GL.GetBoolean(GetPName.DepthWritemask) && !GL.IsEnabled(EnableCap.Blend), "render state leaks after preview");
        Console.WriteLine("VISIBILITY PASS front/back/double-sided, order-independent opaque depth, alpha holes and half transparency");
    }

    internal static void MaterialStates()
    {
        var red = Mat(new(1, 0, 0, 1));
        // Queue, depth, incidental property names and image alpha are not an
        // opacity contract. The same RGBA source must stay opaque here.
        var rgba = red with { Texture = new(new byte[] { 0, 0, 255, 0 }, 1, 1, 0, 0, false) };
        foreach (var shader in new[] { "unknown_hair.shader", "unknown_uber.shader", "unknown_transparent.shader" })
        {
            var unclassified = NarakaPreviewMaterialState.Apply(rgba,
                new Dictionary<string, float> { ["_Cutoff"] = 0.5f, ["_AlphaClipThreshold"] = 0.7f,
                    ["_ZWrite"] = 0, ["_Mode"] = 3, ["_DstBlend"] = 10 },
                "_USE_CUTOUT_DISABLED _ALPHATEST_ON_EXTRA", shader, 3100);
            Check(!unclassified.Cutout && !unclassified.Blend && !unclassified.SilkCoverage,
                shader + ": unverified metadata incorrectly activates diffuse alpha");
        }
        var legacy = NarakaPreviewMaterialState.Apply(red, new Dictionary<string,float> { ["_Culling"] = 1 }, "", "", -1);
        Check(legacy.Cull == 1, "legacy _Culling front-face rule is ignored");
        var opaqueGraph = NarakaPreviewMaterialState.Apply(red, new Dictionary<string,float> { ["_AlphaClipThreshold"] = 0.5f }, "", "ch_cloth_silk_specular4_periodic2_opaque.shadergraph", -1);
        Check(!opaqueGraph.Cutout && !opaqueGraph.Blend, "opaque ShaderGraph is incorrectly clipped by a serialized threshold");
        var transparent = NarakaPreviewMaterialState.Apply(red,
            new Dictionary<string,float> { ["_Cull"] = 2, ["_ZWrite"] = 0, ["_AlphaClipThreshold"] = 0.93f },
            "", "ch_cloth_silk_specular4_periodic2.shadergraph", -1);
        Check(transparent.Blend && transparent.SilkCoverage && !transparent.ZWrite && transparent.Queue == 3000 && transparent.Cull == 2,
            "Periodic2 transparent cloth lost alpha/depth/pass configuration");
        var silk = NarakaPreviewMaterialState.Apply(red, new Dictionary<string,float> { ["_ClothMaterialSrcBlend"] = 5, ["_SilkZWrite"] = 1, ["_Culling"] = 0 },
            "", "lx22standardcharacterclothsilk.shader", -1);
        Check(silk.Blend && silk.ZWrite && silk.Cull == 0 && silk.SrcBlend == 5, "silk depth-writing and double-sided settings were discarded");
        var cutout = NarakaPreviewMaterialState.Apply(red, new Dictionary<string,float> { ["_Cutoff"] = 0.5f }, "_USE_CUTOUT", "", -1);
        Check(cutout.Cutout && !cutout.Blend && cutout.ZWrite && cutout.Queue == 2450, "cutout incorrectly became translucent");
        var depth = NarakaPreviewMaterialState.Apply(red, new Dictionary<string,float>(), "", "lx22transparentanimdepth.shader", -1);
        Check(depth.DepthOnly && depth.ZWrite && !depth.Blend, "source depth-only material draws opaque color");
        Check(NarakaPreviewMaterialState.SilkAlpha(0, 0.93f, 1) == 0 &&
            Math.Abs(NarakaPreviewMaterialState.SilkAlpha(0.25f, 0.5f, 1) - 0.5f) < 1e-6f &&
            Math.Abs(NarakaPreviewMaterialState.SilkAlpha(0.25f, 0.5f, 0.5f) - 0.75f) < 1e-6f &&
            NarakaPreviewMaterialState.SilkAlpha(1, 0.93f, 1) == 1, "verified silk coverage equation changed");
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "alpha_policy_cases.json")));
        foreach (var fixture in cases.RootElement.EnumerateArray())
        {
            var floats = fixture.TryGetProperty("floats", out var properties) ? properties.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetSingle()) : new Dictionary<string, float>();
            var state = NarakaPreviewMaterialState.Apply(rgba, floats,
                fixture.TryGetProperty("keywords", out var keywords) ? keywords.GetString()! : "",
                fixture.GetProperty("shader").GetString()!,
                fixture.TryGetProperty("renderQueue", out var queue) ? queue.GetInt32() : -1);
            var actual = JsonSerializer.SerializeToElement(new { cutout = state.Cutout, blend = state.Blend,
                silkCoverage = state.SilkCoverage, depthOnly = state.DepthOnly, resolved = state.AlphaResolved, useBaseAlpha = state.UseBaseAlpha,
                cull = state.Cull, cutoff = state.Cutoff });
            foreach (var expected in fixture.GetProperty("expected").EnumerateObject())
            {
                var value = actual.GetProperty(expected.Name);
                Check(expected.Value.ValueKind == JsonValueKind.Number ?
                    Math.Abs(value.GetDouble() - expected.Value.GetDouble()) < 1e-6 : value.GetBoolean() == expected.Value.GetBoolean(),
                    fixture.GetProperty("name").GetString() + ": " + expected.Name + " differs");
            }
        }
        Console.WriteLine($"VISIBILITY STATE PASS {cases.RootElement.GetArrayLength()} shared alpha-policy cases; culling, depth and silk coverage equation");
    }

    internal static void ViewportResize()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(-3000, -3000), Size = new(800, 450) };
        using var gl = new GLControl { Dock = DockStyle.Fill, APIVersion = new Version(3, 3), Profile = OpenTK.Windowing.Common.ContextProfile.Core };
        form.Controls.Add(gl); form.Show(); Application.DoEvents(); gl.MakeCurrent();
        var originalClientSize = form.ClientSize;
        var data = new NarakaPrefabPreview(); data.Parts.Add(Quad("resize", 0, Mat(new(1, 0, 0, 1))));
        using var renderer = new NarakaPreviewRenderer(data);

        (bool Top, bool Bottom) RenderCoverage()
        {
            GL.ClearColor(0, 0, 0, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            renderer.DrawScene(Matrix4.Identity, Matrix4.Identity,
                NarakaPreviewCamera.Projection(gl.ClientSize.Width, gl.ClientSize.Height), 0, false);
            GL.Finish();
            bool RedAt(int y)
            {
                var pixel = new byte[4];
                GL.ReadPixels(gl.ClientSize.Width / 2, y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel);
                return pixel[0] > 240 && pixel[1] < 5 && pixel[2] < 5;
            }
            return (RedAt(gl.ClientSize.Height * 3 / 4), RedAt(gl.ClientSize.Height / 4));
        }

        NarakaPreviewCamera.SetViewport(gl.ClientSize.Width, gl.ClientSize.Height);
        var initial = RenderCoverage();
        Check(initial.Top && initial.Bottom, "initial preview does not cover both viewport halves");

        form.ClientSize = new Size(originalClientSize.Width, originalClientSize.Height * 2);
        Application.DoEvents(); gl.MakeCurrent();
        Check(gl.ClientSize.Height > originalClientSize.Height * 3 / 2, "GLControl did not grow with its parent");
        var stale = RenderCoverage();
        Check(!stale.Top && stale.Bottom, "resize fixture did not reproduce clipping with the stale viewport");

        NarakaPreviewCamera.SetViewport(gl.ClientSize.Width, gl.ClientSize.Height);
        var resized = RenderCoverage();
        Check(resized.Top && resized.Bottom, "resized preview clips geometry above the viewport midpoint");

        form.ClientSize = originalClientSize; Application.DoEvents(); gl.MakeCurrent();
        NarakaPreviewCamera.SetViewport(gl.ClientSize.Width, gl.ClientSize.Height);
        Console.WriteLine("VISIBILITY VIEWPORT PASS stale resized viewport clips upper half; synchronized viewport restores full model");
    }
}
