using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AnimeStudio.GUI
{
    /// <summary>
    /// Reads Naraka's own global resource catalog. AppRes.info is a small ZIP whose
    /// data entry maps every logical Unity asset path to its physical Bundle. Using
    /// it avoids opening thousands of Bundles merely to reconstruct the same tree.
    /// </summary>
    internal static class NarakaAppResIndex
    {
        internal sealed record LoadResult(
            VirtualAssetPathIndex Index,
            string StreamingAssetsRoot,
            string[] BundlePaths,
            string Branch,
            string BuiltAt);

        private const long ContainerPathId = long.MinValue;

        public static bool IsContainerRecord(VirtualAssetRecord record) =>
            record?.ContainerOnly == true && record.PathId == ContainerPathId;

        public static string FindCatalog(IEnumerable<string> paths)
        {
            if (paths == null)
                return null;

            foreach (var input in paths.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var fullPath = Path.GetFullPath(input);
                if (File.Exists(fullPath) &&
                    Path.GetFileName(fullPath).Equals("AppRes.info", StringComparison.OrdinalIgnoreCase))
                    return fullPath;
                if (!Directory.Exists(fullPath))
                    continue;

                var direct = Path.Combine(fullPath, "AppRes.info");
                if (File.Exists(direct))
                    return direct;

                var gameRoot = Path.Combine(fullPath, "NarakaBladepoint_Data", "StreamingAssets", "AppRes.info");
                if (File.Exists(gameRoot))
                    return gameRoot;

                var current = new DirectoryInfo(fullPath);
                for (var depth = 0; current != null && depth < 3; depth++, current = current.Parent)
                {
                    var parentCatalog = Path.Combine(current.FullName, "AppRes.info");
                    if (File.Exists(parentCatalog))
                        return parentCatalog;
                }
            }
            return null;
        }

        public static LoadResult Load(string catalogPath)
        {
            catalogPath = Path.GetFullPath(catalogPath);
            var root = Path.GetDirectoryName(catalogPath)
                ?? throw new InvalidDataException("AppRes.info has no parent directory.");
            var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            var index = new VirtualAssetPathIndex("Assets");
            var bundles = new List<string>(16_384);

            using var archive = ZipFile.OpenRead(catalogPath);
            var entry = archive.GetEntry("data")
                ?? throw new InvalidDataException("AppRes.info does not contain its data entry.");
            using var stream = entry.Open();
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            var formatVersion = reader.ReadInt32();
            if (formatVersion != 1)
                throw new InvalidDataException($"Unsupported AppRes.info format version {formatVersion}.");
            var branch = reader.ReadString();
            var builtAt = reader.ReadString();
            _ = reader.ReadString(); // Catalog data version.
            _ = reader.ReadString(); // Build target.
            _ = reader.ReadString(); // Operating system.

            // The current 1.x catalog header contains five build fields followed by
            // the number of Bundle records. Their meanings are game-internal; only
            // the final count is required to walk the catalog safely.
            for (var i = 0; i < 5; i++)
                _ = reader.ReadInt32();
            var bundleCount = reader.ReadInt32();
            if (bundleCount < 0 || bundleCount > 1_000_000)
                throw new InvalidDataException($"Invalid AppRes.info Bundle count {bundleCount:N0}.");

            for (var bundleIndex = 0; bundleIndex < bundleCount; bundleIndex++)
            {
                _ = reader.ReadByte();
                _ = reader.ReadByte();
                _ = reader.ReadByte();
                _ = reader.ReadInt32();
                _ = reader.ReadString(); // Content hash.
                _ = reader.ReadString(); // On-disk MD5.
                _ = reader.ReadInt32();  // On-disk length.
                var relativeBundlePath = reader.ReadString().Replace('/', Path.DirectorySeparatorChar);
                _ = reader.ReadInt32();
                _ = reader.ReadInt32();
                var assetCount = reader.ReadUInt16();

                var bundlePath = Path.GetFullPath(Path.Combine(root, relativeBundlePath));
                if (!bundlePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"AppRes.info contains a Bundle outside StreamingAssets: {relativeBundlePath}");
                bundles.Add(bundlePath);

                for (var assetIndex = 0; assetIndex < assetCount; assetIndex++)
                {
                    var logicalPath = reader.ReadString().Replace('\\', '/').TrimStart('/');
                    if (logicalPath.Length == 0)
                        continue;
                    index.Add(new VirtualAssetRecord(
                        Path.GetFileName(logicalPath),
                        logicalPath,
                        InferType(logicalPath),
                        ContainerPathId,
                        bundlePath,
                        0,
                        ContainerOnly: true));
                }
            }

            index.FinishLoading(sortRecords: true);
            return new LoadResult(index, root, bundles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                branch, builtAt);
        }

        public static (string MapPath, string MarkerPath, string Fingerprint) GetCabCache(string catalogPath)
        {
            var fullCatalogPath = Path.GetFullPath(catalogPath);
            var root = Path.GetDirectoryName(fullCatalogPath) ?? fullCatalogPath;
            var rootHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..16];
            var info = new FileInfo(fullCatalogPath);
            var fingerprint = $"{info.Length:X16}:{info.LastWriteTimeUtc.Ticks:X16}";
            var cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AnimeStudio", "Indexes", "Naraka", rootHash);
            return (Path.Combine(cacheDirectory, "cabmap.bin"),
                Path.Combine(cacheDirectory, "catalog.fingerprint"), fingerprint);
        }

        private static string InferType(string logicalPath)
        {
            return Path.GetExtension(logicalPath).ToLowerInvariant() switch
            {
                ".png" or ".tga" or ".jpg" or ".jpeg" or ".dds" or ".exr" => "Texture2D",
                ".fbx" or ".mesh" => "Mesh",
                ".mat" => "Material",
                ".anim" => "AnimationClip",
                ".controller" => "AnimatorController",
                ".prefab" => "GameObject",
                ".wav" or ".mp3" or ".ogg" => "AudioClip",
                ".txt" or ".bytes" or ".json" or ".xml" => "TextAsset",
                ".shader" or ".shadergraph" => "Shader",
                ".shadervariants" => "ShaderVariantCollection",
                ".spriteatlas" => "SpriteAtlas",
                ".compute" => "ComputeShader",
                ".unity" => "Scene",
                { Length: > 1 } extension => extension[1..],
                _ => "Asset"
            };
        }
    }
}
