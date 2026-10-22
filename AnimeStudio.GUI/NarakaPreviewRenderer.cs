using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

namespace AnimeStudio.GUI;

/// <summary>Base-color-only GL preview; owns and releases every GPU allocation.</summary>
internal sealed class NarakaPreviewRenderer : IDisposable
{
    private sealed record Draw(int Part, int Vao, int Count, int Texture, NarakaPreviewMaterial Material, OpenTK.Mathematics.Vector3 Center);
    private readonly List<Draw> draws = new();
    private readonly List<int> buffers = new(), arrays = new(), textures = new();
    private readonly Dictionary<NarakaPreviewTexture, int> textureIds = new();
    private int program;
    internal readonly NarakaPrefabPreview Data;
    internal int Selection = -1;
    internal bool ShowAll;

    private const string Vertex = """
        #version 330 core
        layout(location=0) in vec3 position;
        layout(location=1) in vec3 normal;
        layout(location=2) in vec2 uv0;
        layout(location=3) in vec2 uv1;
        uniform mat4 model, view, projection;
        uniform vec4 textureST;
        uniform int uvChannel;
        out vec2 uv;
        out vec3 n;
        void main() {
            gl_Position = projection * view * model * vec4(position,1);
            n = mat3(view) * normal;
            uv = (uvChannel==1 ? uv1 : uv0) * textureST.xy + textureST.zw;
        }
        """;
    private const string Fragment = """
        #version 330 core
        in vec2 uv;
        in vec3 n;
        uniform sampler2D baseTexture;
        uniform vec4 tint;
        uniform int hasTexture, alphaEnabled, silkCoverage, useBaseAlpha, blendEnabled, shaded, wire;
        uniform float cutoff;
        out vec4 color;
        void main() {
            vec4 base = (hasTexture==1 ? texture(baseTexture,uv) : vec4(1)) * tint;
            float sourceAlpha=useBaseAlpha==1 ? base.a : 1.0;
            if(alphaEnabled==1 && sourceAlpha<cutoff) discard;
            vec3 normal=normalize(n) * (gl_FrontFacing ? 1.0 : -1.0);
            float alpha=sourceAlpha;
            if(silkCoverage==1) {
                float coverage=clamp(sourceAlpha/max(0.01,cutoff),0.0,1.0);
                // Orthographic preview uses the geometric camera-space normal;
                // the game pass uses its shaded normal and per-pixel view.
                alpha=(1.0-pow(1.0-coverage,1.0/max(0.0000000596046448,abs(normal.z))))*min(1.0,255.0*coverage);
            }
            if(blendEnabled==1 && alpha<=0.001) discard;
            if(wire==1) { color=vec4(0.08,0.09,0.11,1); return; }
            float light=shaded==1 ? 0.72+0.28*max(0.0,dot(normal,normalize(vec3(-0.4,0.7,0.6)))) : 1.0;
            color=vec4(pow(max(base.rgb*light,vec3(0)),vec3(1.0/2.2)),blendEnabled==1 ? alpha : 1.0);
        }
        """;

    internal NarakaPreviewRenderer(NarakaPrefabPreview data)
    {
        Data = data;
        try
        {
            program = GL.CreateProgram();
            foreach (var (source, kind) in new[] { (Vertex, ShaderType.VertexShader), (Fragment, ShaderType.FragmentShader) })
            {
                var shader = GL.CreateShader(kind);
                try
                {
                    GL.ShaderSource(shader, source); GL.CompileShader(shader);
                    GL.GetShader(shader, ShaderParameter.CompileStatus, out var status);
                    if (status == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
                    GL.AttachShader(program, shader);
                }
                finally { GL.DeleteShader(shader); }
            }
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linked);
            if (linked == 0) throw new InvalidOperationException(GL.GetProgramInfoLog(program));
            for (var partIndex = 0; partIndex < data.Parts.Count; partIndex++)
            {
                var part = data.Parts[partIndex];
                var vbo = GL.GenBuffer(); buffers.Add(vbo);
                GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                GL.BufferData(BufferTarget.ArrayBuffer, part.Vertices.Length * sizeof(float), part.Vertices, BufferUsageHint.StaticDraw);
                for (var sub = 0; sub < part.Indices.Length; sub++)
                {
                    var indices = part.Indices[sub];
                    if (indices.Length == 0) continue;
                    var material = part.Materials[sub];
                    var vao = GL.GenVertexArray(); arrays.Add(vao); GL.BindVertexArray(vao);
                    GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                    for (var a = 0; a < 4; a++)
                    {
                        GL.EnableVertexAttribArray(a);
                        GL.VertexAttribPointer(a, a < 2 ? 3 : 2, VertexAttribPointerType.Float, false, 40, a < 2 ? a * 12 : 24 + (a - 2) * 8);
                    }
                    var ebo = GL.GenBuffer(); buffers.Add(ebo);
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
                    GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(int), indices, BufferUsageHint.StaticDraw);
                    var texture = 0;
                    if (material.Texture is { } image && !textureIds.TryGetValue(image, out texture))
                    {
                        texture = GL.GenTexture(); textures.Add(texture); textureIds.Add(image, texture);
                        GL.BindTexture(TextureTarget.Texture2D, texture);
                        GL.TexImage2D(TextureTarget.Texture2D, 0, image.Linear ? PixelInternalFormat.Rgba8 : PixelInternalFormat.Srgb8Alpha8,
                            image.Width, image.Height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, image.Bgra);
                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, image.Filter == 0 ? (int)TextureMinFilter.Nearest : (int)TextureMinFilter.Linear);
                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, image.Filter == 0 ? (int)TextureMagFilter.Nearest : (int)TextureMagFilter.Linear);
                        var wrap = image.Wrap == 1 ? TextureWrapMode.ClampToEdge : image.Wrap == 2 ? TextureWrapMode.MirroredRepeat : TextureWrapMode.Repeat;
                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);
                    }
                    var min = new OpenTK.Mathematics.Vector3(float.PositiveInfinity);
                    var max = new OpenTK.Mathematics.Vector3(float.NegativeInfinity);
                    foreach (var index in indices)
                    {
                        var position = new OpenTK.Mathematics.Vector3(part.Vertices[index * 10], part.Vertices[index * 10 + 1], part.Vertices[index * 10 + 2]);
                        min = OpenTK.Mathematics.Vector3.ComponentMin(min, position);
                        max = OpenTK.Mathematics.Vector3.ComponentMax(max, position);
                    }
                    draws.Add(new(partIndex, vao, indices.Length, texture, material, (min + max) * 0.5f));
                }
            }
            GL.BindVertexArray(0); GL.BindBuffer(BufferTarget.ArrayBuffer, 0); GL.BindTexture(TextureTarget.Texture2D, 0);
        }
        catch { Dispose(); throw; }
    }

    internal int VisibleCount => Selection >= 0 ? 1 : Data.Parts.FindAll(p => ShowAll || (p.Visible && !p.LowerLod)).Count;

    internal void DrawScene(Matrix4 model, Matrix4 view, Matrix4 projection, int wireMode, bool shaded)
    {
        GL.UseProgram(program);
        GL.UniformMatrix4(GL.GetUniformLocation(program, "model"), false, ref model);
        GL.UniformMatrix4(GL.GetUniformLocation(program, "view"), false, ref view);
        GL.UniformMatrix4(GL.GetUniformLocation(program, "projection"), false, ref projection);
        GL.Uniform1(GL.GetUniformLocation(program, "shaded"), shaded ? 1 : 0);
        GL.Uniform1(GL.GetUniformLocation(program, "baseTexture"), 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.FrontFace(FrontFaceDirection.Ccw);
        GL.Enable(EnableCap.DepthTest);
        // Opaque/cutout passes establish depth first. Within each transparent
        // queue, draw submeshes back-to-front in the current camera space.
        var camera = model * view;
        var ordered = draws.Where(draw => Selection >= 0 ? draw.Part == Selection : ShowAll ||
                (Data.Parts[draw.Part].Visible && !Data.Parts[draw.Part].LowerLod))
            .OrderBy(draw => draw.Material.Queue)
            .ThenBy(draw => draw.Material.Blend ? OpenTK.Mathematics.Vector4.TransformRow(new OpenTK.Mathematics.Vector4(draw.Center, 1), camera).Z : 0);
        foreach (var draw in ordered)
        {
            var mat = draw.Material;
            GL.DepthMask(mat.ZWrite);
            GL.DepthFunc(mat.ZTest switch
            {
                1 => DepthFunction.Never, 2 => DepthFunction.Less, 3 => DepthFunction.Equal,
                5 => DepthFunction.Greater, 6 => DepthFunction.Notequal, 7 => DepthFunction.Gequal,
                0 or 8 => DepthFunction.Always, _ => DepthFunction.Lequal,
            });
            GL.ColorMask(!mat.DepthOnly, !mat.DepthOnly, !mat.DepthOnly, !mat.DepthOnly);
            if (mat.Blend)
            {
                GL.Enable(EnableCap.Blend); GL.BlendEquation(BlendEquationMode.FuncAdd);
                GL.BlendFunc((BlendingFactor)SourceFactor(mat.SrcBlend), (BlendingFactor)DestinationFactor(mat.DstBlend));
            }
            else GL.Disable(EnableCap.Blend);
            GL.Uniform4(GL.GetUniformLocation(program, "tint"), mat.Tint);
            GL.Uniform4(GL.GetUniformLocation(program, "textureST"), mat.TextureST);
            GL.Uniform1(GL.GetUniformLocation(program, "uvChannel"), mat.UvChannel);
            GL.Uniform1(GL.GetUniformLocation(program, "hasTexture"), draw.Texture != 0 ? 1 : 0);
            GL.Uniform1(GL.GetUniformLocation(program, "alphaEnabled"), mat.Cutout && mat.UseBaseAlpha ? 1 : 0);
            GL.Uniform1(GL.GetUniformLocation(program, "useBaseAlpha"), mat.UseBaseAlpha ? 1 : 0);
            GL.Uniform1(GL.GetUniformLocation(program, "silkCoverage"), mat.SilkCoverage ? 1 : 0);
            GL.Uniform1(GL.GetUniformLocation(program, "blendEnabled"), mat.Blend ? 1 : 0);
            GL.Uniform1(GL.GetUniformLocation(program, "cutoff"), mat.Cutoff);
            GL.BindTexture(TextureTarget.Texture2D, draw.Texture);
            GL.BindVertexArray(draw.Vao);
            if (mat.Cull == 0) GL.Disable(EnableCap.CullFace);
            else { GL.Enable(EnableCap.CullFace); GL.CullFace(mat.Cull == 1 ? TriangleFace.Front : TriangleFace.Back); }
            if (wireMode != 1)
            {
                GL.Uniform1(GL.GetUniformLocation(program, "wire"), 0);
                GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
                GL.DrawElements(PrimitiveType.Triangles, draw.Count, DrawElementsType.UnsignedInt, 0);
            }
            if (wireMode != 0)
            {
                GL.Enable(EnableCap.PolygonOffsetLine); GL.PolygonOffset(-1, -1);
                GL.Uniform1(GL.GetUniformLocation(program, "wire"), 1);
                GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
                GL.DrawElements(PrimitiveType.Triangles, draw.Count, DrawElementsType.UnsignedInt, 0);
                GL.Disable(EnableCap.PolygonOffsetLine);
            }
        }
        GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend); GL.DepthMask(true); GL.DepthFunc(DepthFunction.Lequal);
        GL.ColorMask(true, true, true, true);
        GL.BindTexture(TextureTarget.Texture2D, 0); GL.BindVertexArray(0); GL.UseProgram(0);
    }

    private static BlendingFactorSrc SourceFactor(int value) => value switch
    {
        0 => BlendingFactorSrc.Zero, 1 => BlendingFactorSrc.One, 2 => BlendingFactorSrc.DstColor,
        3 => BlendingFactorSrc.SrcColor, 4 => BlendingFactorSrc.OneMinusDstColor, 5 => BlendingFactorSrc.SrcAlpha,
        6 => BlendingFactorSrc.OneMinusSrcColor, 7 => BlendingFactorSrc.DstAlpha,
        8 => BlendingFactorSrc.OneMinusDstAlpha, 9 => BlendingFactorSrc.SrcAlphaSaturate,
        10 => BlendingFactorSrc.OneMinusSrcAlpha, _ => BlendingFactorSrc.SrcAlpha,
    };
    private static BlendingFactorDest DestinationFactor(int value) => value switch
    {
        0 => BlendingFactorDest.Zero, 1 => BlendingFactorDest.One, 2 => BlendingFactorDest.DstColor,
        3 => BlendingFactorDest.SrcColor, 4 => BlendingFactorDest.OneMinusDstColor, 5 => BlendingFactorDest.SrcAlpha,
        6 => BlendingFactorDest.OneMinusSrcColor, 7 => BlendingFactorDest.DstAlpha,
        8 => BlendingFactorDest.OneMinusDstAlpha, 10 => BlendingFactorDest.OneMinusSrcAlpha,
        _ => BlendingFactorDest.OneMinusSrcAlpha,
    };

    public void Dispose()
    {
        foreach (var id in arrays) GL.DeleteVertexArray(id);
        foreach (var id in buffers) GL.DeleteBuffer(id);
        foreach (var id in textures) GL.DeleteTexture(id);
        if (program != 0) GL.DeleteProgram(program);
        arrays.Clear(); buffers.Clear(); textures.Clear(); textureIds.Clear(); draws.Clear(); program = 0;
    }
}
