using System;
using System.IO;
using System.Text;

namespace AnimeStudio.GUI
{
    /// <summary>Structural reader used to reject a malformed package at export time.</summary>
    internal static class EiemPackageValidator
    {
        private const int MaxCollectionLength = 100_000_000;

        public static void ValidateMesh(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            RequireMagic(reader, "EIEMESH\0");
            var version = reader.ReadInt32();
            if (version != 2 && version != 3)
                throw new InvalidDataException($"Unsupported EIEM format version {version} in {path}.");
            _ = reader.ReadString(); // coordinate space
            _ = reader.ReadString(); // source path
            _ = reader.ReadString(); // name
            _ = Count(reader, path); // vertex count
            for (var i = 0; i < 12; i++) SkipFloats(reader, path); // P/N/T/C + UV0..7
            SkipUInts(reader, path);

            var subMeshes = Count(reader, path);
            Skip(reader, checked((long)subMeshes * (sizeof(int) + sizeof(uint) * 5)), path);
            var weights = Count(reader, path);
            Skip(reader, checked((long)weights * (sizeof(float) * 4 + sizeof(int) * 4)), path);
            var bindPoses = Count(reader, path);
            Skip(reader, checked((long)bindPoses * sizeof(float) * 16), path);
            SkipUInts(reader, path);
            if (version >= 3)
            {
                var bonePaths = Count(reader, path);
                for (var i = 0; i < bonePaths; i++) _ = reader.ReadString();
            }
            SkipBlendShapes(reader, path);
            RequireEnd(stream, path);
        }

        public static void ValidateSkeleton(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            RequireMagic(reader, "EIESKEL\0");
            RequireVersion(reader.ReadInt32(), 1, path);
            _ = reader.ReadString(); // coordinate space
            var nodes = Count(reader, path);
            for (var i = 0; i < nodes; i++)
            {
                _ = reader.ReadString();
                Skip(reader, sizeof(int) + sizeof(float) * 10, path);
            }
            var boneCount = Count(reader, path);
            Skip(reader, checked((long)boneCount * sizeof(int) + sizeof(int)), path);
            RequireEnd(stream, path);
        }

        private static void SkipBlendShapes(BinaryReader reader, string path)
        {
            var vertices = Count(reader, path);
            Skip(reader, checked((long)vertices * (sizeof(uint) + sizeof(float) * 9)), path);
            var shapes = Count(reader, path);
            for (var i = 0; i < shapes; i++)
            {
                _ = reader.ReadString();
                Skip(reader, sizeof(uint) * 2 + sizeof(bool) * 3, path);
            }
            var channels = Count(reader, path);
            for (var i = 0; i < channels; i++)
            {
                _ = reader.ReadString();
                Skip(reader, sizeof(uint) + sizeof(int) * 2, path);
            }
            SkipFloats(reader, path);
            var additionalNormals = Count(reader, path);
            Skip(reader, checked((long)additionalNormals * sizeof(float) * 3), path);
        }

        private static void SkipFloats(BinaryReader reader, string path)
        {
            var count = Count(reader, path);
            Skip(reader, checked((long)count * sizeof(float)), path);
        }

        private static void SkipUInts(BinaryReader reader, string path)
        {
            var count = Count(reader, path);
            Skip(reader, checked((long)count * sizeof(uint)), path);
        }

        private static int Count(BinaryReader reader, string path)
        {
            var value = reader.ReadInt32();
            if (value < 0 || value > MaxCollectionLength)
                throw new InvalidDataException($"Invalid EIEM collection length in {path}.");
            return value;
        }

        private static void RequireMagic(BinaryReader reader, string value)
        {
            var actual = Encoding.ASCII.GetString(reader.ReadBytes(8));
            if (!string.Equals(actual, value, StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected EIEM file header.");
        }

        private static void RequireVersion(int actual, int expected, string path)
        {
            if (actual != expected)
                throw new InvalidDataException($"Unsupported EIEM format version {actual} in {path}.");
        }

        private static void Skip(BinaryReader reader, long bytes, string path)
        {
            if (bytes < 0 || bytes > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException($"Truncated EIEM data in {path}.");
            reader.BaseStream.Seek(bytes, SeekOrigin.Current);
        }

        private static void RequireEnd(Stream stream, string path)
        {
            if (stream.Position != stream.Length)
                throw new InvalidDataException($"Unexpected trailing EIEM data in {path}.");
        }
    }
}
