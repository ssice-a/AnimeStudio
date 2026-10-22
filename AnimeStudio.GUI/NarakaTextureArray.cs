using AnimeStudio;
using System;
using System.Collections;
using System.Collections.Specialized;
using System.IO;

namespace AnimeStudio.GUI;

internal static class NarakaTextureArray
{
    internal sealed record Decoded(int Width, int Height, int Depth, int ColorSpace, TextureFormat Format,
        int Filter, int Wrap, byte[][] Slices);

    internal static Decoded Decode(AnimeStudio.Object source)
    {
        var fields = NarakaPrefabLoader.Fields(source) ?? throw new InvalidDataException("Texture array has no TypeTree");
        int I(string key) => Convert.ToInt32(fields[key]);
        var width = I("m_Width"); var height = I("m_Height"); var depth = I("m_Depth"); var mips = I("m_MipCount");
        // Texture2DArray uses GraphicsFormat, not TextureFormat. These installed
        // Windows arrays are BC7. Reject other layouts rather than guess strides.
        var format = I("m_Format") switch { 109 => TextureFormat.BC7,
            _ => throw new NotSupportedException("Naraka array GraphicsFormat " + I("m_Format")) };
        if (width <= 0 || height <= 0 || depth <= 0 || depth > 2048 || mips <= 0 || mips > 32)
            throw new InvalidDataException("Invalid array dimensions");
        long stride = 0;
        for (int mip = 0; mip < mips; mip++)
            stride = checked(stride + (long)Math.Max(1, (Math.Max(1, width >> mip) + 3) / 4) * Math.Max(1, (Math.Max(1, height >> mip) + 3) / 4) * 16);
        var stream = (OrderedDictionary)fields["m_StreamData"];
        byte[] data;
        if (!string.IsNullOrEmpty(stream?["path"]?.ToString()))
            data = new ResourceReader(stream["path"].ToString(), source.assetsFile,
                Convert.ToInt64(stream["offset"]), Convert.ToInt64(stream["size"])).GetData();
        else
        {
            var raw = fields["image data"];
            data = raw is byte[] bytes ? bytes : raw is IEnumerable values
                ? System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(System.Linq.Enumerable.Cast<object>(values), Convert.ToByte))
                : throw new InvalidDataException("Array pixels unavailable");
        }
        if (stride * depth != data.LongLength || data.LongLength != Convert.ToInt64(fields["m_DataSize"]))
            throw new InvalidDataException("Texture array mip/slice size mismatch");
        using var reader = new BinaryReader(new MemoryStream(data, false));
        var slices = new byte[depth][];
        for (int i = 0; i < depth; i++)
        {
            slices[i] = new byte[checked(width * height * 4)];
            var decoder = new Texture2DConverter(new ResourceReader(reader, i * stride, stride), width, height, format, source.version, source.platform);
            if (!decoder.DecodeTexture2D(slices[i])) throw new InvalidDataException("Cannot decode array slice " + i);
        }
        var settings = (OrderedDictionary)fields["m_TextureSettings"];
        return new(width, height, depth, I("m_ColorSpace"), format,
            Convert.ToInt32(settings["m_FilterMode"]), Convert.ToInt32(settings["m_WrapU"]), slices);
    }
}
