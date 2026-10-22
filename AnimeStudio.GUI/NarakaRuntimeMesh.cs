using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;

namespace AnimeStudio.GUI;

// Naraka RendererAssistant.avatarMeshAsset stores runtime geometry while the
// native SkinnedMeshRenderer.m_Mesh remains null. Use exact references only.
internal static class NarakaRuntimeMesh
{
    private static object Required(OrderedDictionary data, string key) => data.Contains(key) ? data[key] :
        throw new InvalidDataException("Runtime mesh missing " + key);
    private static OrderedDictionary Map(object value) => value as OrderedDictionary ?? throw new InvalidDataException("Invalid runtime mesh structure");
    private static object[] Array(OrderedDictionary data, string key) => ((IEnumerable)Required(data, key)).Cast<object>().ToArray();
    private static float Number(object value)
    {
        var result = Convert.ToSingle(value);
        if (!float.IsFinite(result)) throw new InvalidDataException("Non-finite runtime mesh value");
        return result;
    }
    private static float[] Vector(object value, int dim) => "xyzw"[..dim].Select(axis => Number(Required(Map(value), axis.ToString()))).ToArray();

    internal static Mesh Decode(AnimeStudio.Object source, OrderedDictionary data)
    {
        if (Convert.ToInt32(Required(data, "m_Version")) != 2 || Convert.ToInt32(Required(data, "m_MeshDataType")) is not (0 or 1))
            throw new InvalidDataException("Unsupported Naraka runtime mesh version/layout");
        var vertices = Array(data, "m_VertexData");
        var count = vertices.Length;
        if (count == 0) throw new InvalidDataException("Runtime mesh has no vertices");
        var mesh = new Mesh(source, Required(data, "m_MeshName").ToString()) { m_VertexCount = count };
        mesh.m_Vertices = vertices.SelectMany(v => Vector(Required(Map(v), "position"), 3)).ToArray();
        mesh.m_Normals = vertices.SelectMany(v => Vector(Required(Map(v), "normal"), 3)).ToArray();
        mesh.m_Tangents = vertices.SelectMany(v => Vector(Required(Map(v), "tangent"), 4)).ToArray();
        mesh.m_Indices = Array(data, "m_Indices").Select(Convert.ToUInt32).ToList();
        if (mesh.m_Indices.Count == 0 || mesh.m_Indices.Count % 3 != 0 || mesh.m_Indices.Any(i => i >= count))
            throw new InvalidDataException("Runtime mesh triangle indices are invalid");
        // This verified layout stores one geometry part per AvatarMeshAsset.
        mesh.m_SubMeshes = new() { new SubMesh { topology = GfxPrimitiveType.Triangles,
            indexCount = (uint)mesh.m_Indices.Count, triangleCount = (uint)mesh.m_Indices.Count / 3, vertexCount = (uint)count } };
        var uv = Array(data, "m_UVData"); var hairUv = Array(data, "m_HairUVSetData");
        if (Array(data, "m_HairUVData").Length != 0 || (uv.Length != 0 && hairUv.Length != 0))
            throw new InvalidDataException("Unsupported mixed/legacy runtime hair UV layout");
        if (uv.Length > 0)
        {
            if (uv.Length != count) throw new InvalidDataException("Runtime UV count differs from vertices");
            mesh.m_UV0 = uv.SelectMany(v => Vector(v, 2)).ToArray();
        }
        if (hairUv.Length > 0)
        {
            if (hairUv.Length != count) throw new InvalidDataException("Runtime hair UV count differs from vertices");
            mesh.m_UV0 = hairUv.SelectMany(v => Vector(Required(Map(v), "uv0"), 2)).ToArray();
            mesh.m_UV1 = hairUv.SelectMany(v => Vector(Required(Map(v), "uv1"), 2)).ToArray();
        }
        var colors = Array(data, "m_VertexColorData");
        if (colors.Length != 0 && colors.Length != count) throw new InvalidDataException("Runtime vertex color count differs from vertices");
        mesh.m_Colors = colors.SelectMany(v => { var rgba = Convert.ToUInt32(Required(Map(v), "rgba"));
            return Enumerable.Range(0, 4).Select(channel => ((rgba >> (8 * channel)) & 255) / 255f); }).ToArray();
        // TypeTree e00,e01,... is the same serialized row order read by native
        // Mesh. EFFMESH's writer handles its existing column-order conversion.
        mesh.m_BindPose = Array(data, "m_BindPoses").Select(v => new Matrix4x4(Enumerable.Range(0, 16)
            .Select(i => Number(Required(Map(v), $"e{i / 4}{i % 4}"))).ToArray())).ToArray();
        var skin = Array(data, "m_AnimSkinData");
        if (skin.Length != 0 && skin.Length != count) throw new InvalidDataException("Runtime skin count differs from vertices");
        mesh.m_Skin = skin.Select(v => new BoneWeights4 { weight = Vector(Required(Map(v), "boneWeight"), 4),
            boneIndex = "xyzw".Select(axis => Convert.ToInt32(Required(Map(Required(Map(v), "boneIndex")), axis.ToString()))).ToArray() }).ToList();
        if (mesh.m_Skin.Any(v => Enumerable.Range(0, 4).Any(i => v.weight[i] < 0 ||
            (v.weight[i] > 0 && (v.boneIndex[i] < 0 || v.boneIndex[i] >= mesh.m_BindPose.Length)))))
            throw new InvalidDataException("Runtime skin bone index/weight is invalid");
        return mesh;
    }
}
