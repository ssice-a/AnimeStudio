using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio.GUI
{
    internal sealed record LoadedEndfieldIndex(
        VirtualAssetPathIndex Assets, EndfieldBundleDependencyIndex Dependencies,
        EndfieldAssetSourceIndex AssetSources, string VfsFingerprint,
        long StoredAssetCount, long StoredCabCount);

    /// <summary>
    /// The small subset of the VFS index needed to export one Prefab.  Keeping
    /// this subset separate lets the package exporter run without materializing
    /// several million unrelated asset records.
    /// </summary>
    internal sealed record EndfieldPrefabExportIndex(
        VirtualAssetFile Prefab, IReadOnlyList<VirtualAssetRecord> Assets,
        EndfieldBundleDependencyIndex Dependencies, string VfsFingerprint);

    internal sealed record EndfieldMeshPrefabCandidateIndex(
        IReadOnlyList<VirtualAssetFile> Prefabs, EndfieldBundleDependencyIndex Dependencies,
        string VfsFingerprint);

    internal static class EndfieldIndexStore
    {
        public const int FormatVersion = 3;
        private const string Magic = "EFF_END_FIELD_INDEX";
        private const byte EndRecord = 0;
        private const byte AssetRecord = 1;
        private const byte CabRecord = 2;

        public static void WriteHeader(BinaryWriter writer, string fingerprint)
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(fingerprint ?? string.Empty);
        }

#if !EFF_SOURCE_READER
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

#endif
        public static void WriteEnd(BinaryWriter writer, long assetCount, long cabCount)
        {
            writer.Write(EndRecord);
            writer.Write(assetCount);
            writer.Write(cabCount);
        }

        public static LoadedEndfieldIndex Load(string path, ISet<string> containers = null,
            Func<VirtualAssetRecord, bool> recordFilter = null,
            IReadOnlyCollection<EndfieldBundleIndexSpan> verifiedSpans = null)
        {
            var assets = new VirtualAssetPathIndex("Assets");
            var dependencies = new EndfieldBundleDependencyIndex();
            var sourcePool = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var containerPool = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var typePool = new Dictionary<string, string>(StringComparer.Ordinal);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4 * 1024 * 1024, FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            var fingerprint = ReadHeader(reader);
            // A resolver generation receipt has already authenticated these
            // complete Bundle spans. Reuse the same record parser without a
            // second scan of unrelated resources during native compilation.
            var ranges = verifiedSpans?.OrderBy(r => r.Offset).ToArray();
            int rangeIndex = 0;
            long storedAssetCount = -1;
            long storedCabCount = -1;
            var ended = false;
            long readAssetCount = 0;

            while (stream.Position < stream.Length)
            {
                if (ranges != null)
                {
                    while (rangeIndex < ranges.Length && (ranges[rangeIndex].Size == 0 ||
                        stream.Position == ranges[rangeIndex].Offset + ranges[rangeIndex].Size)) rangeIndex++;
                    if (rangeIndex == ranges.Length)
                    {
                        storedAssetCount = ranges.Sum(r => r.Assets);storedCabCount = ranges.Sum(r => r.Cabs);
                        ended = true;goto Finished;
                    }
                    var range = ranges[rangeIndex];
                    if (stream.Position > range.Offset + range.Size) throw new InvalidDataException("Index record escapes its verified Bundle span");
                    if (stream.Position < range.Offset) stream.Position = range.Offset;
                }
                switch (reader.ReadByte())
                {
                    case EndRecord:
                        if (ranges != null) throw new InvalidDataException("Unexpected end record inside a Bundle span");
                        storedAssetCount = reader.ReadInt64();
                        storedCabCount = reader.ReadInt64();
                        ended = true;
                        goto Finished;
                    case AssetRecord:
                        readAssetCount++;
                        var name = reader.ReadString();
                        var container = reader.ReadString();
                        var type = reader.ReadString();
                        var pathId = reader.ReadInt64();
                        var source = reader.ReadString();
                        // Source preparation needs only the requested asset
                        // identities. Keep the complete CAB dependency graph,
                        // without materializing every unrelated asset/tree node.
                        if (containers != null && !containers.Contains(container.Replace('\\', '/')))
                            break;
                        if (recordFilter != null && !recordFilter(new VirtualAssetRecord(name, container, type, pathId, source)))
                            break;
                        container = Pool(containerPool, container);
                        type = Pool(typePool, type);
                        source = Pool(sourcePool, source);
                        assets.Add(new VirtualAssetRecord(name, container, type, pathId, source));
                        break;
                    case CabRecord:
                        var cabName = reader.ReadString();
                        var cabSource = Pool(sourcePool, reader.ReadString());
                        var dependencyCount = reader.ReadInt32();
                        if (dependencyCount < 0 || dependencyCount > reader.BaseStream.Length - reader.BaseStream.Position)
                            throw new InvalidDataException($"Invalid CAB dependency count: {dependencyCount}.");
                        var cabDependencies = new string[dependencyCount];
                        for (var i = 0; i < dependencyCount; i++)
                            cabDependencies[i] = reader.ReadString();
                        dependencies.AddCab(cabName, cabSource, cabDependencies);
                        break;
                    default:
                        throw new InvalidDataException("Unknown EFF Endfield index record.");
                }
            }

        Finished:
            dependencies.PrepareReverseDependencyIndex();
            assets.FinishLoading(sortRecords: false);
            if (!ended || ranges == null && stream.Position != stream.Length ||
                storedAssetCount != readAssetCount || storedCabCount != dependencies.CabCount)
                throw new InvalidDataException("The EFF Endfield index is incomplete or corrupt.");
            return new LoadedEndfieldIndex(
                assets, dependencies, new EndfieldAssetSourceIndex(assets.Records),
                fingerprint, storedAssetCount, storedCabCount);
        }

        public static EndfieldPrefabExportIndex LoadPrefabExportIndex(string path, string prefabContainer)
        {
            if (string.IsNullOrWhiteSpace(prefabContainer) ||
                !prefabContainer.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A logical .prefab path is required.", nameof(prefabContainer));

            return LoadPrefabExportIndexes(path, new[] { prefabContainer })[prefabContainer.Replace('\\', '/').Trim('/')];
        }

        internal static Dictionary<string, EndfieldPrefabExportIndex> LoadPrefabExportIndexes(string path, IEnumerable<string> containers)
            => LoadPrefabExportIndexes(path, containers, null);

        internal static Dictionary<string, EndfieldPrefabExportIndex> LoadPostmodelPrefabExportIndexes(
            string path, IReadOnlyCollection<(string Path, string Asset)> meshTargets)
        {
            if (meshTargets == null || meshTargets.Count == 0)
                throw new ArgumentException("At least one legacy Mesh target is required.", nameof(meshTargets));
            return LoadPrefabExportIndexes(path, null, meshTargets);
        }

        internal static EndfieldMeshPrefabCandidateIndex LoadMeshPrefabCandidates(
            EndfieldAssetSourceIndex assetSources, EndfieldBundleDependencyIndex dependencies,
            string fingerprint, VirtualAssetRecord meshTarget)
        {
            if (assetSources == null) throw new ArgumentNullException(nameof(assetSources));
            if (dependencies == null) throw new ArgumentNullException(nameof(dependencies));
            if (meshTarget == null || !meshTarget.Type.Equals("Mesh", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A Mesh asset record is required.", nameof(meshTarget));
            if (string.IsNullOrWhiteSpace(meshTarget.Source))
                throw new InvalidDataException("The selected Mesh has no source Bundle identity.");

            var meshSource = NormalizeSource(meshTarget.Source);
            var meshFound = assetSources.ForSource(meshSource).Any(record =>
                record.Type.Equals("Mesh", StringComparison.OrdinalIgnoreCase) &&
                record.PathId == meshTarget.PathId &&
                record.Name.Equals(meshTarget.Name, StringComparison.Ordinal) &&
                NormalizeContainer(record.Container).Equals(NormalizeContainer(meshTarget.Container),
                    StringComparison.OrdinalIgnoreCase));
            if (!meshFound)
                throw new FileNotFoundException("The selected Mesh is absent from the current Endfield index.");

            // Candidate selection uses the prebuilt reverse CAB index. A Mesh
            // lookup touches its direct dependents and their Prefab records,
            // rather than scanning every Bundle or every asset record.
            var dependentSources = dependencies.GetDirectBundleDependents(meshSource)
                .Append(meshSource);
            var candidateRecords = new Dictionary<string, List<VirtualAssetRecord>>(
                StringComparer.OrdinalIgnoreCase);
            var candidateSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in dependentSources)
            {
                foreach (var record in assetSources.ForSource(source))
                {
                    var container = NormalizeContainer(record.Container);
                    if (!container.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!candidateSources.TryGetValue(container, out var prefabSource))
                    {
                        candidateSources.Add(container, source);
                        candidateRecords.Add(container, new List<VirtualAssetRecord> { record });
                    }
                    else if (!prefabSource.Equals(source, StringComparison.OrdinalIgnoreCase))
                    {
                        candidateSources[container] = string.Empty;
                        candidateRecords.Remove(container);
                    }
                    else if (candidateRecords.TryGetValue(container, out var records))
                    {
                        records.Add(record);
                    }
                }
            }

            var unambiguousCandidates = candidateSources
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value) && candidateRecords.ContainsKey(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            if (unambiguousCandidates.Count == 0)
                throw new FileNotFoundException("No Prefab dependency closure contains the selected Mesh source Bundle.");

            var candidates = unambiguousCandidates.Select(pair =>
                new VirtualAssetFile(pair.Key, candidateRecords[pair.Key])).ToArray();
            return new EndfieldMeshPrefabCandidateIndex(candidates, dependencies, fingerprint);
        }

        internal static EndfieldPrefabExportIndex CreatePrefabExportIndex(
            VirtualAssetFile prefab, EndfieldAssetSourceIndex assetSources,
            EndfieldBundleDependencyIndex dependencies, string fingerprint)
        {
            var source = prefab.Records.Select(record => record.Source)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(source))
                throw new InvalidDataException("The selected Prefab has no source Bundle.");

            var closureSources = dependencies.ResolveBundleClosure(source);
            var records = closureSources.SelectMany(assetSources.ForSource).ToArray();
            return new EndfieldPrefabExportIndex(prefab, records, dependencies, fingerprint);
        }

        private static Dictionary<string, EndfieldPrefabExportIndex> LoadPrefabExportIndexes(
            string path, IEnumerable<string> containers,
            IReadOnlyCollection<(string Path, string Asset)> meshTargets)
        {
            var discoverPostmodels = containers == null;
            var targets = (containers ?? Array.Empty<string>()).Select(NormalizeContainer).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(p => p, _ => new List<VirtualAssetRecord>(), StringComparer.OrdinalIgnoreCase);
            var wantedMeshes = meshTargets?.Select(row => MeshKey(row.Path, row.Asset))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var foundMeshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targetSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dependencies = new EndfieldBundleDependencyIndex();
            string fingerprint;

            using (var stream = OpenIndex(path))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false))
            {
                fingerprint = ReadHeader(reader);
                long assetCount = 0, cabCount = 0;
                var ended = false;
                while (stream.Position < stream.Length)
                {
                    var kind = reader.ReadByte();
                    if (kind == EndRecord)
                    {
                        var storedAssets = reader.ReadInt64();
                        var storedCabs = reader.ReadInt64();
                        if (storedAssets != assetCount || storedCabs != cabCount || stream.Position != stream.Length)
                            throw new InvalidDataException("The Endfield index is incomplete or corrupt.");
                        ended = true;
                        break;
                    }

                    if (kind == AssetRecord)
                    {
                        assetCount++;
                        var record = ReadAsset(reader);
                        var container = NormalizeContainer(record.Container);
                        if (wantedMeshes != null && record.Type.Equals("Mesh", StringComparison.OrdinalIgnoreCase) &&
                            wantedMeshes.Contains(MeshKey(container, record.Name)))
                        {
                            foundMeshes.Add(MeshKey(container, record.Name));
                            if (!string.IsNullOrWhiteSpace(record.Source)) targetSources.Add(record.Source);
                        }
                        if (!targets.TryGetValue(container, out var records) && discoverPostmodels && IsCharacterPostmodel(container))
                        {
                            records = new List<VirtualAssetRecord>();
                            targets.Add(container, records);
                        }
                        records?.Add(record);
                        continue;
                    }

                    if (kind == CabRecord)
                    {
                        cabCount++;
                        var cabName = reader.ReadString();
                        var source = reader.ReadString();
                        var dependencyCount = reader.ReadInt32();
                        if (dependencyCount < 0 || dependencyCount > reader.BaseStream.Length - reader.BaseStream.Position)
                            throw new InvalidDataException($"Invalid CAB dependency count: {dependencyCount}.");
                        var cabDependencies = new string[dependencyCount];
                        for (var i = 0; i < dependencyCount; i++)
                            cabDependencies[i] = reader.ReadString();
                        dependencies.AddCab(cabName, source, cabDependencies);
                        continue;
                    }

                    throw new InvalidDataException("Unknown EFF Endfield index record.");
                }
                if (!ended) throw new InvalidDataException("The Endfield index has no end record.");
            }

            if (wantedMeshes != null)
            {
                var missingMeshes = wantedMeshes.Except(foundMeshes, StringComparer.OrdinalIgnoreCase).ToArray();
                if (missingMeshes.Length != 0)
                    throw new FileNotFoundException("Mesh target is absent from the current Endfield index: " + missingMeshes[0].Replace('\n', '/'));

                var matchingPostmodels = new Dictionary<string, List<VirtualAssetRecord>>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in targets)
                {
                    if (!IsCharacterPostmodel(pair.Key)) continue;
                    var sources = pair.Value.Select(row => row.Source).Where(source => !string.IsNullOrWhiteSpace(source))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (sources.Length != 1) continue;
                    var closure = dependencies.ResolveBundleClosure(sources[0]);
                    if (targetSources.Overlaps(closure)) matchingPostmodels.Add(pair.Key, pair.Value);
                }
                targets = matchingPostmodels;
                if (targets.Count == 0)
                    throw new FileNotFoundException("No character postmodel Prefab depends on the requested Mesh targets.");
            }

            var closures = new Dictionary<string, (VirtualAssetFile Prefab, HashSet<string> Sources)>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in targets)
            {
                if (pair.Value.Count == 0) throw new FileNotFoundException("Prefab is not present in the Endfield index: " + pair.Key);
                var sources = pair.Value.Select(r => r.Source).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (sources.Length != 1) throw new InvalidDataException("Ambiguous Prefab source Bundle: " + pair.Key);
                closures.Add(pair.Key, (new VirtualAssetFile(pair.Key, pair.Value), dependencies.ResolveBundleClosure(sources[0]).ToHashSet(StringComparer.OrdinalIgnoreCase)));
            }
            var closureSources = closures.Values.SelectMany(c => c.Sources).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                        if (dependencyCount < 0 || dependencyCount > reader.BaseStream.Length - reader.BaseStream.Position)
                            throw new InvalidDataException($"Invalid CAB dependency count: {dependencyCount}.");
                        for (var i = 0; i < dependencyCount; i++)
                            _ = reader.ReadString();
                        continue;
                    }

                    throw new InvalidDataException("Unknown EFF Endfield index record.");
                }
            }

            var bySource = closureAssets.ToLookup(r => r.Source, StringComparer.OrdinalIgnoreCase);
            return closures.ToDictionary(c => c.Key, c => new EndfieldPrefabExportIndex(c.Value.Prefab,
                c.Value.Sources.SelectMany(s => bySource[s]).ToArray(), dependencies, fingerprint), StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizeContainer(string value) => value.Replace('\\', '/').Trim('/');

        private static string NormalizeSource(string value)
        {
            var normalized = (value ?? string.Empty).Replace('\\', '/');
            var marker = normalized.IndexOf("Bundles/", StringComparison.OrdinalIgnoreCase);
            return marker >= 0 ? normalized[marker..] : normalized;
        }

        private static string MeshKey(string path, string asset) => NormalizeContainer(path) + "\n" + (asset ?? string.Empty).Trim();

        private static bool IsCharacterPostmodel(string container)
        {
            const string root = "assets/beyond/dynamicassets/gameplay/actors/postmodels/characters/";
            if (!container.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            var name = container[(container.LastIndexOf('/') + 1)..];
            return name.StartsWith("chr_", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith("_postmodel.prefab", StringComparison.OrdinalIgnoreCase);
        }

        private static FileStream OpenIndex(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4 * 1024 * 1024, FileOptions.SequentialScan);

        private static string ReadHeader(BinaryReader reader)
        {
            // EIEM v3 predates the EFF name; its record layout is identical.
            // Read it without rewriting the user's index, retaining all v3 checks.
            var magic = reader.ReadString();
            if (!string.Equals(magic, Magic, StringComparison.Ordinal) &&
                !string.Equals(magic, "EIEM_END_FIELD_INDEX", StringComparison.Ordinal))
                throw new InvalidDataException("This is not an EFF Endfield index.");
            // Compatibility follows record fields, counts and complete extent.
            // A release label alone cannot reject an otherwise identical index.
            reader.ReadInt32();
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
