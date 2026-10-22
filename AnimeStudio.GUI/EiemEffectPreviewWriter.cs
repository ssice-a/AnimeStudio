using AnimeStudio;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetObject = AnimeStudio.Object;
namespace AnimeStudio.GUI;

// Preview assets only. The native snapshots remain the build authority.
internal static class EiemEffectPreviewWriter
{
    internal static object Write(string directory, IEnumerable<AssetObject> assets)
    {
        Directory.CreateDirectory(Path.Combine(directory, "preview"));
        var resources = new List<object>();
        foreach (var asset in assets)
        {
            string identity = EiemEffectSourceWriter.Identity(asset);
            string stem = "preview/" + resources.Count.ToString("D6");
            try
            {
                if (asset is Texture2D texture)
                {
                    using var image = texture.ConvertToImage(true);
                    if (image == null) throw new InvalidDataException("Texture pixels unavailable");
                    string file = stem + ".png";
                    using (var stream = File.Create(Path.Combine(directory, file))) image.WriteToStream(stream, ImageFormat.Png);
                    resources.Add(new { identity, kind = "Texture2D", file, name = asset.Name, width = texture.m_Width, height = texture.m_Height });
                }
                else if (asset is Mesh mesh && mesh.m_Vertices?.Length > 0)
                {
                    int count = checked((int)mesh.m_VertexCount), stride = mesh.m_Vertices.Length / count;
                    if (stride < 3 || mesh.m_Indices.Count % 3 != 0) throw new InvalidDataException("Unsupported preview mesh layout");
                    var vertices = Enumerable.Range(0, count).Select(i => mesh.m_Vertices.Skip(i * stride).Take(3).ToArray()).ToArray();
                    string file = stem + ".mesh.json";
                    File.WriteAllText(Path.Combine(directory, file), JsonConvert.SerializeObject(new { vertices, indices = mesh.m_Indices, uv = mesh.m_UV0, normals = mesh.m_Normals }));
                    resources.Add(new { identity, kind = "Mesh", file, name = asset.Name });
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                resources.Add(new { identity, kind = asset.type.ToString(), name = asset.Name, error = e.Message });
            }
        }
        return new { format = "EFF_EFFECT_PREVIEW", version = 1, basis = "unity-y-up-left-handed", resources };
    }
}
