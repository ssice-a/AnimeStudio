using AnimeStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeStudio.GUI
{
    internal sealed record EndfieldMeshPrefabMatchResult(
        IReadOnlyList<string> Prefabs, IReadOnlyList<string> FailedPrefabs);

    internal sealed record EndfieldMeshPrefabSearchTarget(
        VirtualAssetRecord Target, EndfieldMeshPrefabCandidateIndex Candidates);

    internal sealed record EndfieldMeshPrefabBatchMatchResult(
        IReadOnlyDictionary<VirtualAssetRecord, EndfieldMeshPrefabMatchResult> Results);

    internal sealed record EndfieldMeshPackageExportResult(
        int Exported, IReadOnlyList<string> Failures);

    /// <summary>
    /// Finds Prefabs that render indexed Meshes and exports the selected
    /// Meshes' authoring resources with one source baseline per Prefab.
    /// </summary>
    internal static class EndfieldMeshPackageCommand
    {
        internal static EndfieldMeshPrefabBatchMatchResult FindMatchingPrefabs(
            EndfieldVfsArchive archive, string workspace,
            IReadOnlyList<EndfieldMeshPrefabSearchTarget> searchTargets, string unityVersion)
        {
            var matchesByTarget = searchTargets.ToDictionary(item => item.Target,
                _ => new List<string>());
            var failuresByTarget = searchTargets.ToDictionary(item => item.Target,
                _ => new List<string>());
            var checksBySource = new Dictionary<string, Dictionary<string,
                (VirtualAssetFile Prefab, HashSet<VirtualAssetRecord> Targets)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in searchTargets)
            {
                if (!string.Equals(item.Candidates.VfsFingerprint, archive.Fingerprint, StringComparison.Ordinal))
                    throw new InvalidDataException("The Endfield index belongs to a different VFS version.");

                foreach (var prefab in item.Candidates.Prefabs)
                {
                    var source = prefab.Records.Select(record => record.Source)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                    if (string.IsNullOrWhiteSpace(source))
                    {
                        failuresByTarget[item.Target].Add(prefab.Container + ": Prefab has no source Bundle.");
                        continue;
                    }

                    source = NormalizeSource(source);
                    if (!checksBySource.TryGetValue(source, out var checksByPrefab))
                    {
                        checksByPrefab = new Dictionary<string,
                            (VirtualAssetFile Prefab, HashSet<VirtualAssetRecord> Targets)>(StringComparer.OrdinalIgnoreCase);
                        checksBySource.Add(source, checksByPrefab);
                    }

                    if (!checksByPrefab.TryGetValue(prefab.Container, out var check))
                        check = (prefab, new HashSet<VirtualAssetRecord>());
                    check.Targets.Add(item.Target);
                    checksByPrefab[prefab.Container] = check;
                }
            }

            foreach (var sourceGroup in checksBySource)
            {
                var prefabs = sourceGroup.Value.Values.ToArray();
                var representative = prefabs[0].Prefab;
                var dependencies = searchTargets[0].Candidates.Dependencies;
                var fingerprint = searchTargets[0].Candidates.VfsFingerprint;
                var exportIndex = new EndfieldPrefabExportIndex(representative,
                    Array.Empty<VirtualAssetRecord>(), dependencies, fingerprint);
                try
                {
                    WithLoadedPrefab(archive, workspace, representative, exportIndex,
                        unityVersion, (representativeRoot, manager) =>
                        {
                            var requestedTargets = prefabs.SelectMany(check => check.Targets)
                                .Distinct().ToArray();
                            var requestedByIdentity = requestedTargets
                                .GroupBy(TargetMeshKey, StringComparer.Ordinal)
                                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
                            var meshesByIdentity = new Dictionary<string, List<Mesh>>(StringComparer.Ordinal);
                            foreach (var file in manager.assetsFileList)
                            {
                                var bundleSource = dependencies.GetBundleSource(file.fileName);
                                if (string.IsNullOrWhiteSpace(bundleSource)) continue;
                                foreach (var mesh in file.Objects.OfType<Mesh>())
                                {
                                    var key = TargetMeshKey(bundleSource, mesh.m_PathID, mesh.Name);
                                    if (!requestedByIdentity.ContainsKey(key)) continue;
                                    if (!meshesByIdentity.TryGetValue(key, out var values))
                                    {
                                        values = new List<Mesh>();
                                        meshesByIdentity.Add(key, values);
                                    }
                                    values.Add(mesh);
                                }
                            }

                            var allGameObjects = manager.assetsFileList
                                .SelectMany(file => file.Objects).OfType<GameObject>().ToArray();
                            var prefabFiles = prefabs.Select(check => check.Prefab).ToArray();
                            var representativeSource = representative.Records.Select(record => record.Source)
                                .First(value => !string.IsNullOrWhiteSpace(value));
                            var roots = EndfieldPrefabDocument.FindRoots(allGameObjects, prefabFiles,
                                dependencies.GetCabNames(representativeSource), out var ambiguousRoots);
                            if (!ambiguousRoots.Contains(representative.Container) &&
                                !roots.ContainsKey(representative.Container))
                                roots = roots.Concat(new[] { new KeyValuePair<string, GameObject>(
                                        representative.Container, representativeRoot) })
                                    .ToDictionary(pair => pair.Key, pair => pair.Value,
                                        StringComparer.OrdinalIgnoreCase);
                            foreach (var check in prefabs)
                            {
                                try
                                {
                                    var prefab = check.Prefab;
                                    if (ambiguousRoots.Contains(prefab.Container))
                                        throw new InvalidDataException($"Ambiguous GameObject entry for '{prefab.Container}'.");
                                    if (!roots.TryGetValue(prefab.Container, out var root))
                                        throw new InvalidDataException($"Prefab root '{prefab.Stem}' was not found in its dependency closure.");

                                    var renderedMeshes = EiemPackageWriter.GetReferencedMeshes(root);
                                    foreach (var target in check.Targets)
                                    {
                                        var key = TargetMeshKey(target);
                                        if (!meshesByIdentity.TryGetValue(key, out var meshMatches) || meshMatches.Count == 0)
                                            continue;
                                        if (meshMatches.Count > 1)
                                            throw new InvalidDataException("The selected Mesh identity is ambiguous inside its source Bundle.");
                                        if (renderedMeshes.Contains(meshMatches[0]))
                                            matchesByTarget[target].Add(prefab.Container);
                                    }
                                }
                                catch (Exception error)
                                {
                                    foreach (var target in check.Targets)
                                        failuresByTarget[target].Add(check.Prefab.Container + ": " + error.Message);
                                }
                            }
                            return true;
                        });
                }
                catch (Exception error)
                {
                    foreach (var check in prefabs)
                    foreach (var target in check.Targets)
                        failuresByTarget[target].Add(check.Prefab.Container + ": " + error.Message);
                }
            }

            var results = searchTargets.ToDictionary(item => item.Target, item =>
                new EndfieldMeshPrefabMatchResult(matchesByTarget[item.Target], failuresByTarget[item.Target]));
            return new EndfieldMeshPrefabBatchMatchResult(results);
        }

        internal static EndfieldMeshPackageExportResult ExportMany(
            EndfieldVfsArchive archive, string workspace, string indexPath,
            VirtualAssetFile prefab, EndfieldAssetSourceIndex assetSources,
            EndfieldBundleDependencyIndex dependencies, string fingerprint,
            IReadOnlyList<VirtualAssetRecord> targets, string packageDirectory,
            string unityVersion = null)
        {
            var exportIndex = EndfieldIndexStore.CreatePrefabExportIndex(
                prefab, assetSources, dependencies, fingerprint);
            return ExportMany(archive, workspace, indexPath, exportIndex, targets, packageDirectory, unityVersion);
        }

        internal static EndfieldMeshPackageExportResult ExportMany(
            EndfieldVfsArchive archive, string workspace, string indexPath, EndfieldPrefabExportIndex exportIndex,
            IReadOnlyList<VirtualAssetRecord> targets, string packageDirectory, string unityVersion = null)
        {
            if (!string.Equals(exportIndex.VfsFingerprint, archive.Fingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("The Endfield index belongs to a different VFS version.");
            var prefab = exportIndex.Prefab;
            var dependencies = exportIndex.Dependencies;
            var failures = new List<string>();

            return WithLoadedPrefab(archive, workspace, prefab, exportIndex, unityVersion,
                (root, manager) =>
                {
                    var requestedMeshKeys = targets.Select(TargetMeshKey)
                        .ToHashSet(StringComparer.Ordinal);
                    var targetIndexKeys = targets.Select(TargetIndexKey)
                        .ToHashSet(StringComparer.Ordinal);
                    var indexedTargets = exportIndex.Assets
                        .Where(record => record.Type.Equals("Mesh", StringComparison.OrdinalIgnoreCase))
                        .Select(TargetIndexKey)
                        .Where(targetIndexKeys.Contains)
                        .ToHashSet(StringComparer.Ordinal);
                    var requestedSources = targets.Select(target => NormalizeSource(target.Source))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var meshesByIdentity = new Dictionary<string, List<Mesh>>(StringComparer.Ordinal);
                    foreach (var file in manager.assetsFileList)
                    {
                        var source = dependencies.GetBundleSource(file.fileName);
                        if (string.IsNullOrWhiteSpace(source) || !requestedSources.Contains(source)) continue;
                        foreach (var mesh in file.Objects.OfType<Mesh>())
                        {
                            var key = TargetMeshKey(source, mesh.m_PathID, mesh.Name);
                            if (!requestedMeshKeys.Contains(key)) continue;
                            if (!meshesByIdentity.TryGetValue(key, out var values))
                            {
                                values = new List<Mesh>();
                                meshesByIdentity.Add(key, values);
                            }
                            values.Add(mesh);
                        }
                    }

                    var renderedMeshes = EiemPackageWriter.GetReferencedMeshes(root);
                    var selectedMeshes = new HashSet<Mesh>();
                    foreach (var target in targets)
                    {
                        if (!indexedTargets.Contains(TargetIndexKey(target)))
                            failures.Add(target.Name + ": The selected Mesh is absent from the Prefab dependency index.");
                        else if (!meshesByIdentity.TryGetValue(TargetMeshKey(target), out var meshMatches))
                            failures.Add(target.Name + ": The selected Mesh was not loaded from the Prefab dependency closure.");
                        else if (meshMatches.Count > 1)
                            failures.Add(target.Name + ": The selected Mesh identity is ambiguous inside its source Bundle.");
                        else if (!renderedMeshes.Contains(meshMatches[0]))
                            failures.Add(target.Name + ": The selected Prefab does not render the selected Mesh.");
                        else
                            selectedMeshes.Add(meshMatches[0]);
                    }
                    if (failures.Count != 0 || selectedMeshes.Count == 0)
                        return new EndfieldMeshPackageExportResult(0, failures);

                    archive.VerifyCurrent();
                    new EiemPackageWriter(exportIndex.Assets, dependencies,
                        exportIndex.VfsFingerprint, indexPath, archive)
                        .Write(prefab, root, packageDirectory, archive.VerifyCurrent, selectedMeshes);
                    return new EndfieldMeshPackageExportResult(selectedMeshes.Count, failures);
                });
        }

        private static T WithLoadedPrefab<T>(EndfieldVfsArchive archive, string workspace,
            VirtualAssetFile prefab, EndfieldPrefabExportIndex exportIndex, string unityVersion,
            Func<GameObject, AssetsManager, T> action)
        {
            var source = prefab.Records.Select(record => record.Source)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(source))
                throw new InvalidDataException("The selected Prefab has no source Bundle.");

            var closure = exportIndex.Dependencies.ResolveBundleClosure(source)
                .Where(path => archive.TryGet(path, out _))
                .Select(path => archive.ExtractToCache(path, workspace))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (closure.Length == 0)
                throw new InvalidDataException("No readable VFS bundles were found for the Prefab dependency closure.");

            var flags = TypeFlags.CaptureTypes();
            try
            {
                TypeFlags.SetTypes(new Dictionary<ClassIDType, (bool, bool)>());
                foreach (var type in new[]
                {
                    ClassIDType.GameObject, ClassIDType.Transform, ClassIDType.RectTransform,
                    ClassIDType.SkinnedMeshRenderer, ClassIDType.MeshRenderer, ClassIDType.MeshFilter,
                    ClassIDType.Mesh, ClassIDType.Material, ClassIDType.Texture2D, ClassIDType.MonoBehaviour,
                    ClassIDType.MonoScript, ClassIDType.AssetBundle, ClassIDType.Animator, ClassIDType.Avatar
                })
                    TypeFlags.SetType(type, parse: true, export: true);

                var manager = new AssetsManager
                {
                    Game = GameManager.GetGameByType(GameType.ArknightsEndfield),
                    SpecifyUnityVersion = unityVersion,
                    ResolveDependencies = false,
                    FilterData = new AssetsManager.AssetFilterData
                    {
                        Items = new List<AssetsManager.AssetFilterDataItem>()
                    }
                };
                try
                {
                    manager.LoadFiles(closure, mergeSplitAssets: false);
                    var prefabSource = prefab.Records.Select(record => record.Source)
                        .First(value => !string.IsNullOrWhiteSpace(value));
                    var sourceCabs = exportIndex.Dependencies.GetCabNames(prefabSource);
                    var root = EndfieldPrefabDocument.FindRoot(
                        manager.assetsFileList.SelectMany(file => file.Objects).OfType<GameObject>(),
                        prefab, sourceCabs);
                    if (root == null)
                        throw new InvalidDataException($"Prefab root '{prefab.Stem}' was not found in its dependency closure.");
                    return action(root, manager);
                }
                finally
                {
                    manager.Clear();
                }
            }
            finally
            {
                TypeFlags.SetTypes(flags);
            }
        }

        private static string TargetMeshKey(VirtualAssetRecord target) =>
            TargetMeshKey(target.Source, target.PathId, target.Name);

        private static string TargetIndexKey(VirtualAssetRecord target) =>
            TargetMeshKey(target) + "\n" + NormalizeContainer(target.Container).ToUpperInvariant();

        private static string TargetMeshKey(string source, long pathId, string name) =>
            NormalizeSource(source).ToUpperInvariant() + "\n" +
            pathId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + (name ?? string.Empty);

        private static string NormalizeSource(string source)
        {
            var normalized = (source ?? string.Empty).Replace('\\', '/');
            var marker = normalized.IndexOf("Bundles/", StringComparison.OrdinalIgnoreCase);
            return marker >= 0 ? normalized[marker..] : normalized;
        }

        private static string NormalizeContainer(string container) =>
            (container ?? string.Empty).Replace('\\', '/').Trim('/');
    }
}
