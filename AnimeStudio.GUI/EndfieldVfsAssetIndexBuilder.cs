using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeStudio.GUI
{
    internal sealed record EndfieldIndexProgress(
        int ProcessedBundles, int TotalBundles, long AssetCount,
        long CabCount, int FailedBundles, string CurrentPath, TimeSpan Elapsed);

    internal sealed record EndfieldIndexManifest(
        int FormatVersion, string VfsFingerprint, int BundleCount,
        long AssetCount, long CabCount, int FailedBundles, long IndexLength,
        DateTime CreatedUtc);

    internal static class EndfieldVfsAssetIndexBuilder
    {
        private const int FormatVersion = EndfieldIndexStore.FormatVersion;
        private const int CheckpointInterval = 32;
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private sealed record Checkpoint(
            int FormatVersion, string VfsFingerprint, int NextBundle,
            long OutputLength, long AssetCount, long CabCount, int FailedBundles);

        public static string GetIndexPath(string workspace) =>
            Path.Combine(Path.GetFullPath(workspace), "index", "endfield_assets.eidx");

        public static bool TryGetCurrentIndex(EndfieldVfsArchive archive, string workspace, out string indexPath)
        {
            indexPath = GetIndexPath(workspace);
            var manifestPath = GetManifestPath(workspace);
            if (!File.Exists(indexPath) || !File.Exists(manifestPath))
                return false;
            try
            {
                var manifest = JsonSerializer.Deserialize<EndfieldIndexManifest>(File.ReadAllText(manifestPath));
                return manifest != null &&
                    manifest.FormatVersion == FormatVersion &&
                    string.Equals(manifest.VfsFingerprint, archive.Fingerprint, StringComparison.Ordinal) &&
                    manifest.BundleCount == archive.Entries.Values.Count(x =>
                        x.LogicalPath.EndsWith(".ab", StringComparison.OrdinalIgnoreCase)) &&
                    manifest.IndexLength == new FileInfo(indexPath).Length;
            }
            catch
            {
                return false;
            }
        }

        public static Task<string> BuildAsync(
            EndfieldVfsArchive archive, string workspace, Game game,
            IProgress<EndfieldIndexProgress> progress, CancellationToken cancellationToken,
            int? bundleLimit = null)
        {
            return Task.Run(() => Build(archive, workspace, game, progress, cancellationToken, bundleLimit), cancellationToken);
        }

        private static string Build(
            EndfieldVfsArchive archive, string workspace, Game game,
            IProgress<EndfieldIndexProgress> progress, CancellationToken cancellationToken,
            int? bundleLimit)
        {
            var indexRoot = Path.Combine(Path.GetFullPath(workspace), "index");
            Directory.CreateDirectory(indexRoot);
            var indexPath = GetIndexPath(workspace);
            var partialPath = indexPath + ".partial";
            var checkpointPath = GetCheckpointPath(workspace);
            var manifestPath = GetManifestPath(workspace);
            var errorPath = Path.Combine(indexRoot, "endfield-index-errors.log");

            var bundles = archive.Entries.Values
                .Where(x => x.LogicalPath.EndsWith(".ab", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.LogicalPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (bundleLimit.HasValue)
                bundles = bundles.Take(bundleLimit.Value).ToArray();
            if (bundles.Length == 0)
                throw new InvalidDataException("The Endfield VFS contains no logical .ab files.");

            AssetsHelper.Clear();

            var checkpoint = ReadCheckpoint(checkpointPath, partialPath, archive.Fingerprint, bundles.Length);
            var startBundle = checkpoint?.NextBundle ?? 0;
            var assetCount = checkpoint?.AssetCount ?? 0;
            var cabCount = checkpoint?.CabCount ?? 0;
            var failedBundles = checkpoint?.FailedBundles ?? 0;

            using var output = new FileStream(partialPath,
                checkpoint == null ? FileMode.Create : FileMode.Open,
                FileAccess.ReadWrite, FileShare.Read, 1024 * 1024,
                FileOptions.SequentialScan);
            var writer = new BinaryWriter(output, new UTF8Encoding(false), leaveOpen: true);
            if (checkpoint == null)
            {
                EndfieldIndexStore.WriteHeader(writer, archive.Fingerprint);
                writer.Flush();
                WriteCheckpoint(checkpointPath, new Checkpoint(
                    FormatVersion, archive.Fingerprint, 0, output.Position, 0, 0, 0));
                if (File.Exists(errorPath))
                    File.Delete(errorPath);
            }
            else
            {
                output.SetLength(checkpoint.OutputLength);
                output.Position = checkpoint.OutputLength;
            }
            var stopwatch = Stopwatch.StartNew();

            for (var bundleIndex = startBundle; bundleIndex < bundles.Length; bundleIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bundle = bundles[bundleIndex];
                try
                {
                    var bytes = archive.ReadPayload(bundle.LogicalPath);
                    using var payload = new MemoryStream(bytes, writable: false);
                    var bundleIndexData = AssetsHelper.BuildBundleIndexFromStream(payload, bundle.LogicalPath, game);
                    foreach (var asset in bundleIndexData.Assets)
                    {
                        if (asset.Type == ClassIDType.AssetBundle)
                            continue;
                        EndfieldIndexStore.WriteAsset(writer, asset);
                        assetCount++;
                    }
                    foreach (var file in bundleIndexData.Files)
                    {
                        EndfieldIndexStore.WriteCab(writer, file, bundle.LogicalPath);
                        cabCount++;
                    }
                }
                catch (Exception ex)
                {
                    failedBundles++;
                    File.AppendAllText(errorPath,
                        $"{DateTime.UtcNow:O}\t{bundle.LogicalPath}\t{ex}\n", Encoding.UTF8);
                }

                var processed = bundleIndex + 1;
                if (processed % CheckpointInterval == 0 || processed == bundles.Length)
                {
                    writer.Flush();
                    output.Flush(flushToDisk: false);
                    WriteCheckpoint(checkpointPath, new Checkpoint(
                        FormatVersion, archive.Fingerprint, processed,
                        output.Position, assetCount, cabCount, failedBundles));
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
                }

                if (processed % 8 == 0 || processed == bundles.Length)
                {
                    progress?.Report(new EndfieldIndexProgress(
                        processed, bundles.Length, assetCount, cabCount, failedBundles,
                        bundle.LogicalPath, stopwatch.Elapsed));
                }
            }

            EndfieldIndexStore.WriteEnd(writer, assetCount, cabCount);
            writer.Flush();
            output.Flush(flushToDisk: true);
            var finalLength = output.Length;
            writer.Dispose();
            output.Dispose();

            File.Move(partialPath, indexPath, overwrite: true);
            var manifest = new EndfieldIndexManifest(
                FormatVersion, archive.Fingerprint, bundles.Length,
                assetCount, cabCount, failedBundles, finalLength, DateTime.UtcNow);
            WriteJsonAtomic(manifestPath, manifest);
            if (File.Exists(checkpointPath))
                File.Delete(checkpointPath);
            return indexPath;
        }

        private static Checkpoint ReadCheckpoint(
            string checkpointPath, string partialPath, string fingerprint, int bundleCount)
        {
            if (!File.Exists(checkpointPath) || !File.Exists(partialPath))
                return null;
            try
            {
                var checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(checkpointPath));
                if (checkpoint == null || checkpoint.FormatVersion != FormatVersion ||
                    !string.Equals(checkpoint.VfsFingerprint, fingerprint, StringComparison.Ordinal) ||
                    checkpoint.NextBundle < 0 || checkpoint.NextBundle > bundleCount ||
                    checkpoint.OutputLength <= 0 || checkpoint.OutputLength > new FileInfo(partialPath).Length)
                    return null;
                return checkpoint;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteCheckpoint(string path, Checkpoint checkpoint) =>
            WriteJsonAtomic(path, checkpoint);

        private static void WriteJsonAtomic<T>(string path, T value)
        {
            var temp = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 7)
                {
                    Thread.Sleep(25 * (attempt + 1));
                }
            }
        }

        private static string GetCheckpointPath(string workspace) =>
            Path.Combine(Path.GetFullPath(workspace), "index", "endfield-index-progress.json");

        private static string GetManifestPath(string workspace) =>
            Path.Combine(Path.GetFullPath(workspace), "index", "endfield-index-manifest.json");
    }
}
