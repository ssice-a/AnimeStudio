using AnimeStudio.GUI;
using System.Collections;
using System.Text;

internal static class EndfieldMeshPackageTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    internal static void Run()
    {
        var selection = new EndfieldMeshPrefabSelection();
        string Choose(IReadOnlyList<string> choices)
        {
            Check(choices.SequenceEqual(new[] { "assets/a.prefab", "assets/b.prefab" }),
                "The chooser must list each logical Prefab exactly once.");
            return choices[1];
        }
        string Unexpected(IReadOnlyList<string> _) =>
            throw new InvalidOperationException("An existing Prefab selection should be reused.");
        Check(selection.Select(new[] { "assets/a.prefab", "ASSETS\\A.PREFAB", "assets/b.prefab" }, Choose) == "assets/b.prefab",
            "The first ambiguous Mesh should use the chosen Prefab.");
        Check(selection.Select(new[] { "assets/c.prefab", "ASSETS/B.PREFAB" }, Unexpected) == "assets/b.prefab",
            "A different Mesh sharing the chosen Prefab should reuse it.");
        Check(selection.Select(new[] { "assets/c.prefab" }, Unexpected) == "assets/c.prefab" &&
              selection.Select(new[] { "assets/c.prefab", "assets/b.prefab" }, Unexpected) == "assets/b.prefab",
            "When several previously chosen Prefabs match, reuse the earliest choice.");
        Check(selection.Select(new[] { "assets/d.prefab", "assets/e.prefab" }, _ => null) == null &&
              selection.Select(new[] { "assets/d.prefab", "assets/e.prefab" }, choices => choices[1]) == "assets/e.prefab",
            "Cancelling a chooser must not affect another Mesh.");

        var root = Path.Combine(AppContext.BaseDirectory, "mesh-package-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var indexPath = Path.Combine(root, "index.effindex");
            const int unrelatedBundleCount = 20_000;
            using (var stream = File.Create(indexPath))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                EndfieldIndexStore.WriteHeader(writer, "mesh-package-fixture");

                // Asset records are ordered by their owning Bundle, matching the
                // production index format and enabling source-range lookups.
                WriteAsset(writer, "Mesh_Isolated", "assets/isolated.mesh", "Mesh", 21,
                    "Bundles/Isolated.bundle");
                WriteAsset(writer, "Mesh_Target", "assets/target.mesh", "Mesh", 20,
                    "Bundles/Meshes.bundle");
                WriteAsset(writer, "UnrelatedTexture", "assets/unrelated.texture", "Texture2D", 3,
                    "Bundles/Other.bundle");
                WriteAsset(writer, "SkinPrefab", "assets/skin.prefab", "GameObject", 1,
                    "Bundles/Prefabs.bundle");

                WriteCab(writer, "isolated-cab", "Bundles/Isolated.bundle");
                WriteCab(writer, "mesh-cab", "Bundles/Meshes.bundle");
                WriteCab(writer, "other-cab", "Bundles/Other.bundle");
                WriteCab(writer, "prefab-cab", "Bundles/Prefabs.bundle", "mesh-cab");
                for (var index = 0; index < unrelatedBundleCount; index++)
                {
                    var suffix = index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
                    WriteCab(writer, "unrelated-cab-" + suffix,
                        "Bundles/Unrelated/" + suffix + ".bundle");
                }

                EndfieldIndexStore.WriteEnd(writer, assetCount: 4,
                    cabCount: unrelatedBundleCount + 4);
            }

            var loaded = EndfieldIndexStore.Load(indexPath);
            var guardedRecords = new GuardedRecordList(loaded.Assets.Records);
            var assetSources = new EndfieldAssetSourceIndex(guardedRecords);
            var targetSource = "Bundles/Meshes.bundle";
            var prefabSource = "Bundles/Prefabs.bundle";
            var allowedRecordIndexes = loaded.Assets.Records
                .Select((record, index) => (record, index))
                .Where(row => row.record.Source.Equals(targetSource, StringComparison.OrdinalIgnoreCase) ||
                              row.record.Source.Equals(prefabSource, StringComparison.OrdinalIgnoreCase))
                .Select(row => row.index)
                .ToHashSet();
            guardedRecords.RestrictTo(allowedRecordIndexes);

            var target = new VirtualAssetRecord("Mesh_Target", "assets/target.mesh", "Mesh", 20,
                targetSource);
            var candidates = EndfieldIndexStore.LoadMeshPrefabCandidates(assetSources,
                loaded.Dependencies, loaded.VfsFingerprint, target);
            Check(candidates.Prefabs.Select(prefab => prefab.Container)
                    .SequenceEqual(new[] { "assets/skin.prefab" }),
                "The reverse dependency lookup should retain the Prefab that directly references the selected Mesh Bundle.");
            Check(guardedRecords.ReadIndexes.Count <= 3 &&
                  guardedRecords.ReadIndexes.All(allowedRecordIndexes.Contains),
                "Looking up one Mesh must read only its source Bundle and direct dependent Prefab records.");

            guardedRecords.AllowAll();
            var absentTarget = target with { Name = "MissingMesh", PathId = 99 };
            RejectMissing(assetSources, loaded, absentTarget,
                "A Mesh missing from the index must be rejected.");

            var isolatedTarget = target with
            {
                Name = "Mesh_Isolated",
                Container = "assets/isolated.mesh",
                PathId = 21,
                Source = "Bundles/Isolated.bundle"
            };
            RejectMissing(assetSources, loaded, isolatedTarget,
                "A Mesh with no Prefab dependency must be rejected.");

            Console.WriteLine($"PASS selected Mesh lookup: one direct Prefab candidate across {unrelatedBundleCount:N0} unrelated Bundles");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void RejectMissing(EndfieldAssetSourceIndex assetSources,
        LoadedEndfieldIndex loaded, VirtualAssetRecord target, string message)
    {
        try
        {
            EndfieldIndexStore.LoadMeshPrefabCandidates(assetSources, loaded.Dependencies,
                loaded.VfsFingerprint, target);
        }
        catch (FileNotFoundException)
        {
            return;
        }

        throw new InvalidDataException(message);
    }

    private static void WriteAsset(BinaryWriter writer, string name, string container,
        string type, long pathId, string source)
    {
        writer.Write((byte)1);
        writer.Write(name);
        writer.Write(container);
        writer.Write(type);
        writer.Write(pathId);
        writer.Write(source);
    }

    private static void WriteCab(BinaryWriter writer, string name, string source,
        params string[] dependencies)
    {
        writer.Write((byte)2);
        writer.Write(name);
        writer.Write(source);
        writer.Write(dependencies.Length);
        foreach (var dependency in dependencies)
            writer.Write(dependency);
    }

    private sealed class GuardedRecordList : IReadOnlyList<VirtualAssetRecord>
    {
        private readonly IReadOnlyList<VirtualAssetRecord> records;
        private HashSet<int> allowedIndexes;

        internal List<int> ReadIndexes { get; } = new();

        internal GuardedRecordList(IReadOnlyList<VirtualAssetRecord> records) =>
            this.records = records;

        public int Count => records.Count;

        public VirtualAssetRecord this[int index]
        {
            get
            {
                if (allowedIndexes != null && !allowedIndexes.Contains(index))
                    throw new InvalidOperationException("Mesh lookup read an unrelated Bundle's asset records.");
                ReadIndexes.Add(index);
                return records[index];
            }
        }

        internal void RestrictTo(HashSet<int> indexes)
        {
            ReadIndexes.Clear();
            allowedIndexes = indexes;
        }

        internal void AllowAll() => allowedIndexes = null;

        public IEnumerator<VirtualAssetRecord> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
                yield return this[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
