using AnimeStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeStudio.GUI;

internal static class EndfieldEffectSourceCommand
{
    public static int Run(string[] args)
    {
        if (args.Length != 5 && (args.Length != 7 || args[5] != "--unity-version" || string.IsNullOrWhiteSpace(args[6])))
        {
            Console.Error.WriteLine("Usage: --export-eff-effects <game-or-vfs-root> <workspace> <logical-prefab> <output-root> [--unity-version <version>]");
            return 2;
        }
        try
        {
            var archive = EndfieldVfsArchive.Open(Path.GetFullPath(args[1]));
            string output = PackagePath(args[4], args[3]);
            ExportOne(archive, Path.GetFullPath(args[2]), args[3], output, args.Length == 7 ? args[6] : null);
            Directory.CreateDirectory(args[4]);
            File.WriteAllText(Path.Combine(args[4], "effect-export-status.txt"), "success\n" + output);
            Console.WriteLine("Effect source exported: " + output);
            return 0;
        }
        catch (Exception error)
        {
            Directory.CreateDirectory(args[4]);
            File.WriteAllText(Path.Combine(args[4], "effect-export-status.txt"), "failed\n" + error);
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    internal static string PackagePath(string output, string logicalPrefab)
    {
        string path = logicalPrefab.Replace('\\', '/');
        if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(path) ||
            path.Split('/').Any(p => p is "." or ".." || p.Length == 0 || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Expected a relative logical .prefab path");
        return Path.Combine(Path.GetFullPath(output), Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + ".efffx");
    }

    internal static void ExportOne(EndfieldVfsArchive archive, string workspace, string prefabPath,
        string output, string unityVersion = null, EndfieldPrefabExportIndex preparedIndex = null)
    {
        if (!EndfieldVfsAssetIndexBuilder.TryGetOrRebindCurrentIndex(archive, workspace, out var indexPath))
            throw new InvalidDataException("Open the current game folder and update the workspace index first");
        var exportIndex = preparedIndex ?? EndfieldIndexStore.LoadPrefabExportIndex(indexPath, prefabPath.Replace('\\', '/'));
        if (!string.Equals(exportIndex.Prefab.Container, prefabPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Prepared effect index belongs to a different Prefab");
        if (!string.Equals(exportIndex.VfsFingerprint, archive.Fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Effect index and installed VFS differ");
        string source = exportIndex.Prefab.Records.Select(r => r.Source).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("Effect Prefab has no source Bundle");
        var closure = exportIndex.Dependencies.ResolveBundleClosure(source);
        var missing = closure.Where(path => !archive.TryGet(path, out _)).ToArray();
        if (missing.Length > 0) throw new InvalidDataException("Missing dependency bundles: " + string.Join(", ", missing));
        var paths = closure.Select(path => archive.ExtractToCache(path, workspace)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) throw new InvalidDataException("Effect source closure is empty");
        var flags = Enum.GetValues<ClassIDType>().Distinct().ToDictionary(t => t, t => (t.CanParse(), t.CanExport()));
        try
        {
            // Particle and other unknown components remain Object instances;
            // their actual TypeTrees, rather than guessed class layouts, decode them.
            TypeFlags.SetTypes(new Dictionary<ClassIDType, (bool, bool)>());
            foreach (var type in new[] { ClassIDType.GameObject, ClassIDType.Transform, ClassIDType.RectTransform,
                ClassIDType.SkinnedMeshRenderer, ClassIDType.MeshRenderer, ClassIDType.MeshFilter,
                ClassIDType.Mesh, ClassIDType.Material, ClassIDType.Texture2D, ClassIDType.MonoBehaviour,
                ClassIDType.MonoScript, ClassIDType.AssetBundle, ClassIDType.Animator, ClassIDType.Avatar })
                TypeFlags.SetType(type, parse: true, export: true);
            var manager = new AssetsManager
            {
                Game = GameManager.GetGameByType(GameType.ArknightsEndfield),
                SpecifyUnityVersion = unityVersion, ResolveDependencies = false,
                FilterData = new AssetsManager.AssetFilterData { Items = new List<AssetsManager.AssetFilterDataItem>() }
            };
            try
            {
                manager.LoadFiles(paths, mergeSplitAssets: false);
                var root = EndfieldPrefabDocument.FindRoot(manager.assetsFileList.SelectMany(f => f.Objects).OfType<GameObject>(),
                    exportIndex.Prefab, exportIndex.Dependencies.GetCabNames(source));
                if (root == null) throw new InvalidDataException("Exact effect container root was not found: " + prefabPath);
                archive.VerifyCurrent();
                new EiemEffectSourceWriter(exportIndex.Assets, exportIndex.Dependencies,
                    exportIndex.VfsFingerprint, indexPath, archive)
                    .Write(exportIndex.Prefab, root, output, archive.VerifyCurrent);
            }
            finally { manager.Clear(); }
        }
        finally { TypeFlags.SetTypes(flags); }
    }
}
