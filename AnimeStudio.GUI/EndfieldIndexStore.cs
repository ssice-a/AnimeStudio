using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio.GUI
{
    internal sealed record LoadedEndfieldIndex(
        VirtualAssetPathIndex Assets, EndfieldBundleDependencyIndex Dependencies,
        string VfsFingerprint, long StoredAssetCount, long StoredCabCount);

    /// <summary>
    /// The small subset of the VFS index needed to export one Prefab.  Keeping
    /// this subset separate lets the package exporter run without materializing
    /// several million unrelated asset records.
    /// </summary>
    internal sealed record EndfieldPrefabExportIndex(
        VirtualAssetFile Prefab, IReadOnlyList<VirtualAssetRecord> Assets,
        EndfieldBundleDependencyIndex Dependencies, string VfsFingerprint);

    internal static class EndfieldIndexStore
    {
        public const int FormatVersion = 3;
        private const string Magic = "EIEM_END_FIELD_INDEX";
        private const byte EndRecord = 0;
        private const byte AssetRecord = 1;
        private const byte CabRecord = 2;

        public static void WriteHeader(BinaryWriter writer, string fingerprint)
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(fingerprint ?? string.Empty);
        }

        public static void WriteAsset(BinaryWriter writer, AssetEntry asset)
        {
            writer.Write(AssetRecord);
            writer.Write(asset.Name ?? string.Empty);
            writer.Write(asset.Container ?? string.Empty);
            writer.Write(asset.Type.ToString());
            writer.Write(asset.PathID);
            writer.Write(asset.Source ?? string.Empty);
        }

        public static void WriteCab(
            BinaryWriter writer, AssetsHelper.BundleDependencyEntry file, string source)
        {
            writer.Write(CabRecord);
            writer.Write(file.Name ?? string.Empty);
            writer.Write(source ?? string.Empty);
            writer.Write(file.Dependencies.Length);
            foreach (var dependency in file.Dependencies)
                writer.Write(dependency ?? string.Empty);
        }

        public static void WriteEnd(BinaryWriter writer, long assetCount, long cabCount)
        {
            writer.Write(EndRecord);
            writer.Write(assetCount);
            writer.Write(cabCount);
        }

        public static LoadedEndfieldIndex Load(string path)
        {
            var assets = new VirtualAssetPathIndex("Assets");
            var dependencies = new EndfieldBundleDependencyIndex();
            var sourcePool = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var containerPool = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var typePool = new Dictionary<string, string>(StringComparer.Ordinal);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4 * 1024 * 1024, FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (!string.Equals(reader.ReadString(), Magic, StringComparison.Ordinal))
                throw new InvalidDataException("This is not an EIEM Endfield index.");
            var version = reader.ReadInt32();
            if (version != FormatVersion)
                throw new InvalidDataException($"Unsupported EIEM Endfield index version: {version}.");
            var fingerprint = reader.ReadString();
            long storedAssetCount = -1;
            long storedCabCount = -1;

            while (stream.Position < stream.Length)
            {
                switch (reader.ReadByte())
                {
                    case EndRecord:
                        storedAssetCount = reader.ReadInt64();
                        storedCabCount = reader.ReadInt64();
                        goto Finished;
                    case AssetRecord:
                        var name = reader.ReadString();
                        var container = Pool(containerPool, reader.ReadString());
                        var type = Pool(typePool, reader.ReadString());
                        var pathId = reader.ReadInt64();
                        var source = Pool(sourcePool, reader.ReadString());
                        assets.Add(new VirtualAssetRecord(name, container, type, pathId, source));
                        break;
                    case CabRecord:
                        var cabName = reader.ReadString();
                        var cabSource = Pool(sourcePool, reader.ReadString());
                        var dependencyCount = reader.ReadInt32();
                        if (dependencyCount < 0 || dependencyCount > 100_000)
                            throw new InvalidDataException($"Invalid CAB dependency count: {dependencyCount}.");
                        var cabDependencies = new string[dependencyCount];
                        for (var i = 0; i < dependencyCount; i++)
                            cabDependencies[i] = reader.ReadString();
                        dependencies.AddCab(cabName, cabSource, cabDependencies);
                        break;
                    default:
                        throw new InvalidDataException("Unknown EIEM Endfield index record.");
                }
            }

        Finished:
            assets.FinishLoading(sortRecords: false);
            if (storedAssetCount != assets.AssetCount || storedCabCount != dependencies.CabCount)
                throw new InvalidDataException("The EIEM Endfield index is incomplete or corrupt.");
            return new LoadedEndfieldIndex(
                assets, dependencies, fingerprint, storedAssetCount, storedCabCount);
        }

        public static EndfieldPrefabExportIndex LoadPrefabExportIndex(string path, string prefabContainer)
        {
            if (string.IsNullOrWhiteSpace(prefabContainer) ||
                !prefabContainer.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A logical .prefab path is required.", nameof(prefabContainer));

            var target = prefabContainer.Replace('\\', '/').Trim('/');
            var prefabRecords = new List<VirtualAssetRecord>();
            var dependencies = new EndfieldBundleDependencyIndex();
            string fingerprint;

            using (var stream = OpenIndex(path))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false))
            {
                fingerprint = ReadHeader(reader);
                while (stream.Position < stream.Length)
                {
                    var kind = reader.ReadByte();
                    if (kind == EndRecord)
                    {
                        _ = reader.ReadInt64();
                        _ = reader.ReadInt64();
                        break;
                    }

                    if (kind == AssetRecord)
                    {
                        var record = ReadAsset(reader);
                        if (string.Equals(record.Container.Replace('\\', '/').Trim('/'), target,
                                StringComparison.OrdinalIgnoreCase))
                            prefabRecords.Add(record);
                        continue;
                    }

                    if (kind == CabRecord)
                    {
                        var cabName = reader.ReadString();
                        var source = reader.ReadString();
                        var dependencyCount = reader.ReadInt32();
                        if (dependencyCount < 0 || dependencyCount > 100_000)
                            throw new InvalidDataException($"Invalid CAB dependency count: {dependencyCount}.");
                        var cabDependencies = new string[dependencyCount];
                        for (var i = 0; i < dependencyCount; i++)
                            cabDependencies[i] = reader.ReadString();
                        dependencies.AddCab(cabName, source, cabDependencies);
                        continue;
                    }

                    throw new InvalidDataException("Unknown EIEM Endfield index record.");
                }
            }

            if (prefabRecords.Count == 0)
                throw new FileNotFoundException($"Prefab is not present in the Endfield index: {target}");

            var prefab = new VirtualAssetFile(target, prefabRecords);
            var closureSources = dependencies.ResolveBundleClosure(prefabRecords[0].Source)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var closureAssets = new List<VirtualAssetRecord>();

            using (var stream = OpenIndex(path))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false))
            {
                _ = ReadHeader(reader);
                while (stream.Position < stream.Length)
                {
                    var kind = reader.ReadByte();
                    if (kind == EndRecord)
                    {
                        _ = reader.ReadInt64();
                        _ = reader.ReadInt64();
                        break;
                    }

                    if (kind == AssetRecord)
                    {
                        var record = ReadAsset(reader);
                        if (closureSources.Contains(record.Source))
                            closureAssets.Add(record);
                        continue;
                    }

                    if (kind == CabRecord)
                    {
                        _ = reader.ReadString();
                        _ = reader.ReadString();
                        var dependencyCount = reader.ReadInt32();
                        if (dependencyCount < 0 || dependencyCount > 100_000)
                            throw new InvalidDataException($"Invalid CAB dependency count: {dependencyCount}.");
                        for (var i = 0; i < dependencyCount; i++)
                            _ = reader.ReadString();
                        continue;
                    }

                    throw new InvalidDataException("Unknown EIEM Endfield index record.");
                }
            }

            return new EndfieldPrefabExportIndex(prefab, closureAssets, dependencies, fingerprint);
        }

        private static FileStream OpenIndex(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4 * 1024 * 1024, FileOptions.SequentialScan);

        private static string ReadHeader(BinaryReader reader)
        {
            if (!string.Equals(reader.ReadString(), Magic, StringComparison.Ordinal))
                throw new InvalidDataException("This is not an EIEM Endfield index.");
            var version = reader.ReadInt32();
            if (version != FormatVersion)
                throw new InvalidDataException($"Unsupported EIEM Endfield index version: {version}.");
            return reader.ReadString();
        }

        private static VirtualAssetRecord ReadAsset(BinaryReader reader) => new(
            reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadInt64(), reader.ReadString());

        private static string Pool(Dictionary<string, string> pool, string value)
        {
            if (pool.TryGetValue(value, out var existing))
                return existing;
            pool.Add(value, value);
            return value;
        }
    }
}
