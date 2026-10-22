using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using OpenTK.Mathematics;
using GVector3 = OpenTK.Mathematics.Vector3;
using GVector4 = OpenTK.Mathematics.Vector4;
using NMatrix = System.Numerics.Matrix4x4;
using NVector = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace AnimeStudio.GUI;

internal sealed record NarakaPreviewSelection(string Prefab, int Renderer);
internal sealed record NarakaPreviewTexture(byte[] Bgra, int Width, int Height, int Wrap, int Filter, bool Linear);
internal sealed record NarakaPreviewMaterial(string Name, string Source, NarakaPreviewTexture Texture,
    GVector4 Tint, GVector4 TextureST, int UvChannel, int Cull, bool Cutout, float Cutoff,
    bool Blend = false, bool ZWrite = true, int ZTest = 4, int Queue = 2000,
    int SrcBlend = 5, int DstBlend = 10, bool SilkCoverage = false, bool DepthOnly = false,
    bool AlphaResolved = false, bool UseBaseAlpha = false, string AlphaReason = "unresolved-shader-alpha-unused");
internal sealed record NarakaPreviewPart(string Name, string RendererSource, string MeshSource,
    string MeshName, float[] Vertices, int[][] Indices, NarakaPreviewMaterial[] Materials,
    bool Visible, bool LowerLod);

/// <summary>Owned CPU snapshots, independent of disposed Bundle streams.</summary>
internal sealed class NarakaPrefabPreview
{
    internal string Prefab;
    internal List<NarakaPreviewPart> Parts = new();
    internal List<string> Warnings = new();
    internal GVector3 Min, Max;

    internal static NarakaPrefabPreview Load(VirtualAssetRecord record, CancellationToken token)
    {
        using var loader = new NarakaPrefabLoader(record.Source);
        var root = loader.FindRoot(record.Container, record.Source);
        return Build(loader, root, record.Container, token);
    }

    internal static NarakaPrefabPreview Build(NarakaPrefabLoader loader, GameObject root, string prefab, CancellationToken token)
    {
        var result = new NarakaPrefabPreview { Prefab = prefab };
        var materials = new Dictionary<Material, NarakaPreviewMaterial>();
        var textures = new Dictionary<Texture2D, NarakaPreviewTexture>();
        var nativeLod = new Dictionary<string, int>();
        // Native LOD groups take precedence; names are only a fallback.
        foreach (var gameObject in EndfieldPrefabDocument.GetHierarchy(root))
        foreach (var pointer in gameObject.m_Components)
        {
            var component = loader.Resolve(pointer, gameObject.assetsFile);
            if (component?.type != ClassIDType.LODGroup) continue;
            if (NarakaPrefabLoader.Fields(component)?["m_LODs"] is not System.Collections.IEnumerable levels) continue;
            var levelIndex = 0;
            foreach (System.Collections.Specialized.OrderedDictionary level in levels)
            {
                foreach (System.Collections.Specialized.OrderedDictionary entry in (System.Collections.IEnumerable)level["renderers"])
                {
                    var reference = (System.Collections.Specialized.OrderedDictionary)entry["renderer"];
                    var renderer = loader.Resolve(new PPtr<AnimeStudio.Object>(Convert.ToInt32(reference["m_FileID"]),
                        Convert.ToInt64(reference["m_PathID"]), component.assetsFile), component.assetsFile);
                    if (renderer != null)
                    {
                        var id = NarakaPackageWriter.Identity(renderer);
                        nativeLod[id] = Math.Min(nativeLod.GetValueOrDefault(id, levelIndex), levelIndex);
                    }
                }
                levelIndex++;
            }
        }

        NarakaPreviewMaterial Material(Material material)
        {
            if (material == null) return new("[no material]", "", null, GVector4.One, new(1, 1, 0, 0), 0, 0, false, 0);
            if (materials.TryGetValue(material, out var existing)) return existing;
            var floats = material.m_SavedProperties.m_Floats.ToDictionary(p => p.Key, p => p.Value);
            var colors = material.m_SavedProperties.m_Colors.ToDictionary(p => p.Key, p => p.Value);
            var bindings = material.m_SavedProperties.m_TexEnvs.ToDictionary(p => p.Key, p => p.Value);
            UnityTexEnv binding = null;
            foreach (var property in new[] { "_MainTex", "_BaseMap", "_MainTexture", "_DiffuseTexture", "_BaseColorMap" })
                if (bindings.TryGetValue(property, out var candidate) && !candidate.m_Texture.IsNull) { binding = candidate; break; }
            NarakaPreviewTexture image = null;
            if (binding != null && loader.Resolve(binding.m_Texture.Cast<Texture2D>(), material.assetsFile) is Texture2D source)
            {
                if (!textures.TryGetValue(source, out image))
                {
                    using var decoded = source.ConvertToImage(false) ?? throw new InvalidDataException("Unable to decode " + source.Name);
                    // Preview memory is bounded independently of export resolution.
                    var factor = Math.Min(1f, 2048f / Math.Max(decoded.Width, decoded.Height));
                    using var resized = factor < 1 ? decoded.Resize(Math.Max(1, (int)(decoded.Width * factor)), Math.Max(1, (int)(decoded.Height * factor))) : null;
                    var bitmap = resized ?? decoded;
                    image = new(bitmap.ConvertToBytes(), bitmap.Width, bitmap.Height, source.m_TextureSettings.m_WrapMode,
                        source.m_TextureSettings.m_FilterMode, source.m_ColorSpace == 0);
                    textures.Add(source, image);
                }
            }
            var tint = colors.GetValueOrDefault("_Color", colors.GetValueOrDefault("_BaseColor", new Color(1, 1, 1, 1)));
            var selection = colors.GetValueOrDefault("_TextureUVSelection", new Color(0, 0, 0, 0));
            var fields = NarakaPrefabLoader.Fields(material);
            var keywords = fields?["m_ShaderKeywords"]?.ToString() ?? "";
            var shader = loader.LogicalPath(loader.Require(material.m_Shader.Cast<AnimeStudio.Object>(), material.assetsFile));
            var data = new NarakaPreviewMaterial(material.Name, NarakaPackageWriter.Identity(material), image,
                new(tint.R, tint.G, tint.B, tint.A), binding == null ? new(1, 1, 0, 0) :
                    new(binding.m_Scale.X, binding.m_Scale.Y, binding.m_Offset.X, binding.m_Offset.Y),
                selection.R != 0 && bindings.ContainsKey("_MainTexture") ? 1 : 0,
                (int)floats.GetValueOrDefault("_Cull", floats.GetValueOrDefault("_Culling", 2)), false,
                floats.GetValueOrDefault("_AlphaClipThreshold", floats.GetValueOrDefault("_Cutoff", 0.5f)));
            data = NarakaPreviewMaterialState.Apply(data, floats, keywords, shader,
                fields?.Contains("m_CustomRenderQueue") == true ? Convert.ToInt32(fields["m_CustomRenderQueue"]) : -1);
            if (!data.AlphaResolved) result.Warnings.Add(material.Name + ": Shader alpha usage unresolved; diffuse A retained without opacity wiring");
            if (image == null) result.Warnings.Add(material.Name + ": no recognized base color texture; using source tint");
            materials.Add(material, data);
            return data;
        }

        void Visit(Transform transform, NMatrix parent, bool parentActive)
        {
            token.ThrowIfCancellationRequested();
            var gameObject = loader.Require(transform.m_GameObject, transform.assetsFile);
            var p = transform.m_LocalPosition; var q = transform.m_LocalRotation; var s = transform.m_LocalScale;
            var world = NMatrix.CreateScale(s.X, s.Y, s.Z) * NMatrix.CreateFromQuaternion(new NQuaternion(q.X, q.Y, q.Z, q.W)) *
                NMatrix.CreateTranslation(p.X, p.Y, p.Z) * parent;
            NMatrix.Invert(world, out var inverse);
            var normalMatrix = NMatrix.Transpose(inverse);
            var fields = NarakaPrefabLoader.Fields(gameObject);
            var active = parentActive && (fields == null || !fields.Contains("m_IsActive") || Convert.ToBoolean(fields["m_IsActive"]));
            foreach (var pointer in gameObject.m_Components)
            {
                if (loader.Resolve(pointer, gameObject.assetsFile) is not Renderer renderer || renderer is not (MeshRenderer or SkinnedMeshRenderer)) continue;
                var mesh = loader.RendererMesh(gameObject, renderer);
                if (loader.RuntimeMeshSources.ContainsKey(mesh))
                    result.Warnings.Add(gameObject.Name + ": geometry decoded from exact avatarMeshAsset; runtime hair/avatar deformation is not executed in this preview");
                var data = new float[checked(mesh.m_VertexCount * 10)]; // P3 N3 UV0-2 UV1-2
                var vertexDim = mesh.m_Vertices.Length / mesh.m_VertexCount;
                var normalDim = mesh.m_Normals?.Length / mesh.m_VertexCount ?? 0;
                for (var i = 0; i < mesh.m_VertexCount; i++)
                {
                    var v = NVector.Transform(new(mesh.m_Vertices[i * vertexDim], mesh.m_Vertices[i * vertexDim + 1], mesh.m_Vertices[i * vertexDim + 2]), world);
                    var n = normalDim >= 3 ? NVector.TransformNormal(new(mesh.m_Normals[i * normalDim], mesh.m_Normals[i * normalDim + 1], mesh.m_Normals[i * normalDim + 2]), normalMatrix) : NVector.UnitY;
                    n = n.LengthSquared() > 1e-12f ? NVector.Normalize(n) : NVector.UnitY;
                    var offset = i * 10;
                    data[offset] = -v.X; data[offset + 1] = v.Y; data[offset + 2] = v.Z;
                    data[offset + 3] = -n.X; data[offset + 4] = n.Y; data[offset + 5] = n.Z;
                    foreach (var (uv, channel) in new[] { (mesh.m_UV0, 0), (mesh.m_UV1, 1) })
                    {
                        var dim = uv?.Length / mesh.m_VertexCount ?? 0;
                        if (dim < 2) continue;
                        data[offset + 6 + channel * 2] = uv[i * dim]; data[offset + 7 + channel * 2] = uv[i * dim + 1];
                    }
                }
                var submeshes = new int[mesh.m_SubMeshes.Count][];
                var cursor = 0;
                for (var j = 0; j < submeshes.Length; j++)
                {
                    var sub = mesh.m_SubMeshes[j];
                    var count = checked((int)sub.indexCount);
                    var indices = new int[count];
                    for (var k = 0; k < count; k += 3)
                    {
                        for (var axis = 0; axis < 3; axis++)
                        {
                            var value = checked((int)(mesh.m_Indices[cursor + k + axis] + sub.baseVertex));
                            if (value < 0 || value >= mesh.m_VertexCount) throw new InvalidDataException("Preview vertex index exceeds mesh");
                            // X handedness conversion and mirrored source
                            // transforms each reverse winding. Correct their
                            // combined parity so material front/back survives.
                            var reverse = world.GetDeterminant() >= 0;
                            indices[k + (reverse ? axis == 1 ? 2 : axis == 2 ? 1 : 0 : axis)] = value;
                        }
                    }
                    submeshes[j] = indices;
                    cursor += count;
                }
                var slots = renderer.m_Materials.Select(ptr => Material(loader.Resolve(ptr, renderer.assetsFile))).ToArray();
                var drawIndices = new List<int[]>();
                for (var slot = 0; slot < (slots.Length > 0 ? slots.Length : submeshes.Length); slot++)
                {
                    var sub = renderer.m_SubsetIndices?.Length > slot ? (int)renderer.m_SubsetIndices[slot] :
                        renderer.m_StaticBatchInfo?.subMeshCount > slot ? renderer.m_StaticBatchInfo.firstSubMesh + slot : slot;
                    drawIndices.Add(sub < submeshes.Length ? submeshes[sub] : Array.Empty<int>());
                }
                if (slots.Length < drawIndices.Count) slots = Enumerable.Range(0, drawIndices.Count).Select(i => i < slots.Length ? slots[i] : Material(null)).ToArray();
                fields = NarakaPrefabLoader.Fields(renderer);
                var enabled = fields == null || !fields.Contains("m_Enabled") || Convert.ToBoolean(fields["m_Enabled"]);
                var id = NarakaPackageWriter.Identity(renderer);
                var lower = nativeLod.Count > 0 ? nativeLod.GetValueOrDefault(id) > 0 : Regex.IsMatch(gameObject.Name, "_L[1-9][0-9]*$", RegexOptions.IgnoreCase);
                result.Parts.Add(new(gameObject.Name, id, NarakaPackageWriter.Identity(mesh), mesh.Name, data,
                    drawIndices.ToArray(), slots, active && enabled, lower));
            }
            foreach (var pointer in transform.m_Children) Visit(loader.Require(pointer, transform.assetsFile), world, active);
        }
        Visit(root.m_Transform, NMatrix.Identity, true);
        var visible = result.Parts.Where(p => p.Visible && !p.LowerLod).ToArray();
        if (visible.Length == 0) visible = result.Parts.ToArray();
        result.Min = new(float.PositiveInfinity); result.Max = new(float.NegativeInfinity);
        foreach (var part in visible)
        for (var i = 0; i < part.Vertices.Length; i += 10)
        {
            var p = new GVector3(part.Vertices[i], part.Vertices[i + 1], part.Vertices[i + 2]);
            result.Min = GVector3.ComponentMin(result.Min, p); result.Max = GVector3.ComponentMax(result.Max, p);
        }
        return result;
    }
}
