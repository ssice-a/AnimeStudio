using AnimeStudio;
using System;
using System.IO;
using System.Linq;

namespace AnimeStudio.GUI
{
    /// <summary>
    /// Non-interactive export path for diagnostics and automation.  It uses the
    /// same VFS reader, dependency index and package writer as the UI.
    /// </summary>
    internal static class EndfieldPrefabPackageCommand
    {
        public static int Run(string[] args)
        {
            var outputRoot = args?.Length > 4 ? Path.GetFullPath(args[4]) : string.Empty;
            if (args.Length != 5)
            {
                Console.Error.WriteLine("Usage: --export-eiem-prefab <vfs-root> <workspace> <logical-prefab> <output-root>");
                WriteStatus(outputRoot, "failed", "Expected four command arguments after --export-eiem-prefab.");
                return 2;
            }

            try
            {
                var vfsRoot = Path.GetFullPath(args[1]);
                var workspace = Path.GetFullPath(args[2]);
                var prefabPath = args[3].Replace('\\', '/').Trim('/');
                outputRoot = Path.GetFullPath(args[4]);
                var archive = EndfieldVfsArchive.Open(vfsRoot);
                if (!EndfieldVfsAssetIndexBuilder.TryGetCurrentIndex(archive, workspace, out var indexPath))
                    throw new InvalidDataException("No current Endfield index exists for this VFS and workspace.");

                var exportIndex = EndfieldIndexStore.LoadPrefabExportIndex(indexPath, prefabPath);
                if (!string.Equals(exportIndex.VfsFingerprint, archive.Fingerprint, StringComparison.Ordinal))
                    throw new InvalidDataException("The Endfield index belongs to a different VFS version.");

                var source = exportIndex.Prefab.Records.Select(x => x.Source)
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                if (string.IsNullOrWhiteSpace(source))
                    throw new InvalidDataException("The selected Prefab has no source Bundle.");
                var closure = exportIndex.Dependencies.ResolveBundleClosure(source)
                    .Where(path => archive.TryGet(path, out _))
                    .Select(path => archive.ExtractToCache(path, workspace))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (closure.Length == 0)
                    throw new InvalidDataException("No readable VFS bundles were found for the Prefab dependency closure.");

                var manager = new AssetsManager
                {
                    Game = GameManager.GetGameByType(GameType.ArknightsEndfield),
                    SpecifyUnityVersion = "2021.3.34f5",
                    ResolveDependencies = false,
                    FilterData = new AssetsManager.AssetFilterData
                    {
                        Items = new System.Collections.Generic.List<AssetsManager.AssetFilterDataItem>()
                    }
                };
                manager.LoadFiles(closure, mergeSplitAssets: false);
                var sourceCabs = exportIndex.Dependencies.GetCabNames(source);
                var root = EndfieldPrefabDocument.FindRoot(
                    manager.assetsFileList.SelectMany(file => file.Objects).OfType<GameObject>(),
                    exportIndex.Prefab, sourceCabs);
                if (root == null)
                    throw new InvalidDataException($"Prefab root '{exportIndex.Prefab.Stem}' was not found in its dependency closure.");

                if (!Exporter.ExportEndfieldPrefab(exportIndex.Prefab, root, outputRoot, includeResources: true,
                        exportIndex.Assets, exportIndex.Dependencies, exportIndex.VfsFingerprint))
                    throw new InvalidDataException("Prefab package export reported an error. See export-errors.txt in the package directory.");

                var packageDirectory = Path.Combine(outputRoot,
                    Path.GetDirectoryName(prefabPath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(prefabPath) + ".eiem");
                WriteStatus(outputRoot, "success", $"EIEM package exported: {packageDirectory}");
                Console.WriteLine($"EIEM package exported: {packageDirectory}");
                return 0;
            }
            catch (Exception ex)
            {
                WriteStatus(outputRoot, "failed", ex.ToString());
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void WriteStatus(string outputRoot, string result, string detail)
        {
            if (string.IsNullOrWhiteSpace(outputRoot))
                return;
            Directory.CreateDirectory(outputRoot);
            File.WriteAllText(Path.Combine(outputRoot, "export-status.txt"),
                $"result={result}{Environment.NewLine}time={DateTimeOffset.Now:O}{Environment.NewLine}{detail}{Environment.NewLine}");
        }
    }
}
