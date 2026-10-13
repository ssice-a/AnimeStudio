using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeStudio.GUI
{
    internal sealed record EndfieldIndexProgress(int ProcessedBundles, int TotalBundles, long AssetCount,
        long CabCount, int FailedBundles, string CurrentPath, TimeSpan Elapsed,
        int ParsedBundles = 0, int ReusedBundles = 0, int RemovedBundles = 0);

    internal sealed record EndfieldIndexManifest(int FormatVersion, string VfsFingerprint, int BundleCount,
        long AssetCount, long CabCount, int FailedBundles, long IndexLength, DateTime CreatedUtc,
        string ParserIdentity = null, int ParsedBundles = 0, int ReusedBundles = 0, int RemovedBundles = 0);

    internal static class EndfieldVfsAssetIndexBuilder
    {
        private const int FormatVersion = EndfieldIndexStore.FormatVersion;
        private const int CheckpointInterval = 32;
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private static readonly SemaphoreSlim BuildGate = new(1, 1); // AssetsHelper owns process-wide parser state.
        private sealed record Checkpoint(int FormatVersion, string VfsFingerprint, int NextBundle,
            long OutputLength, long AssetCount, long CabCount, int FailedBundles,
            string ParserIdentity, int ParsedBundles, int ReusedBundles, int RemovedBundles, string PrefixDigest);

        public static string GetIndexPath(string workspace) => Path.Combine(Path.GetFullPath(workspace), "index", "endfield_assets.eidx");

        private static EndfieldIndexManifest ReadManifest(string workspace)
        {
            try { return JsonSerializer.Deserialize<EndfieldIndexManifest>(File.ReadAllText(GetManifestPath(workspace))); }
            catch (Exception ex) when (ex is IOException || ex is JsonException) { return null; }
        }

        public static bool TryGetCurrentIndex(EndfieldVfsArchive archive, string workspace, out string indexPath)
        {
            indexPath = GetIndexPath(workspace);var manifest = ReadManifest(workspace);
            try
            {
                if (manifest == null || manifest.FormatVersion != FormatVersion || manifest.FailedBundles != 0 ||
                    manifest.ParserIdentity != EndfieldBundleIndexCache.ParserIdentity || !File.Exists(EndfieldBundleIndexCache.PathFor(indexPath)) || manifest.VfsFingerprint != archive.Fingerprint ||
                    manifest.BundleCount != archive.Entries.Values.Count(x => x.LogicalPath.EndsWith(".ab", StringComparison.OrdinalIgnoreCase))) return false;
                using var index = File.OpenRead(indexPath);
                return EndfieldBundleIndexCache.ValidateEnvelope(index, manifest.VfsFingerprint,
                    manifest.IndexLength, manifest.AssetCount, manifest.CabCount, out _);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException) { return false; }
        }

        public static Task<string> BuildAsync(EndfieldVfsArchive archive, string workspace, Game game,
            IProgress<EndfieldIndexProgress> progress, CancellationToken cancellationToken, int? bundleLimit = null) =>
            Task.Run(() => Build(archive, workspace, game, progress, cancellationToken, bundleLimit), cancellationToken);

        // Tests inject only Bundle parsing; archive, incremental selection and
        // publication remain the production path.
        internal static string Build(EndfieldVfsArchive archive, string workspace, Game game,
            IProgress<EndfieldIndexProgress> progress, CancellationToken token, int? bundleLimit = null,
            Func<EndfieldVfsEntry, BinaryWriter, (long Assets, long Cabs)> parse = null)
        {
            BuildGate.Wait(token);
            try
            {
                var indexRoot = Path.Combine(Path.GetFullPath(workspace), "index");Directory.CreateDirectory(indexRoot);
                using var workspaceLock = new FileStream(Path.Combine(indexRoot, "endfield-index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return BuildLocked(archive, workspace, game, progress, token, bundleLimit, parse);
            }
            finally { BuildGate.Release(); }
        }

        private static string BuildLocked(EndfieldVfsArchive archive, string workspace, Game game,
            IProgress<EndfieldIndexProgress> progress, CancellationToken token, int? bundleLimit,
            Func<EndfieldVfsEntry, BinaryWriter, (long Assets, long Cabs)> parse)
        {
            var indexPath = GetIndexPath(workspace);var partialPath = indexPath + ".partial";
            var cachePath = EndfieldBundleIndexCache.PathFor(indexPath);var cachePartial = cachePath + ".partial";
            var checkpointPath = GetCheckpointPath(workspace);
            var errorPath = Path.Combine(Path.GetDirectoryName(indexPath), "endfield-index-errors.log");
            var bundles = archive.Entries.Values.Where(x => x.LogicalPath.EndsWith(".ab", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.LogicalPath, StringComparer.OrdinalIgnoreCase).ToArray();
            if (bundleLimit.HasValue) bundles = bundles.Take(bundleLimit.Value).ToArray();
            if (bundles.Length == 0) throw new InvalidDataException("The Endfield VFS contains no logical .ab files.");
            var stopwatch = Stopwatch.StartNew();
            progress?.Report(new(0, bundles.Length, 0, 0, 0, "Comparing Bundle content identities", stopwatch.Elapsed));
            archive.VerifyCurrent();
            // Availability is checked even for reused records, without reading
            // payloads. An incomplete update must never use different old bytes.
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var bundle in bundles)
            {
                token.ThrowIfCancellationRequested();
                if (!sizes.TryGetValue(bundle.SourceChk, out var size))
                {
                    size = File.Exists(bundle.SourceChk) ? new FileInfo(bundle.SourceChk).Length : -1;sizes.Add(bundle.SourceChk, size);
                }
                if (bundle.Offset < 0 || bundle.Length <= 0 || bundle.Offset > size || bundle.Length > size - bundle.Offset)
                    throw new InvalidDataException("Current Bundle payload is unavailable: " + bundle.LogicalPath);
            }
            if (!bundleLimit.HasValue && TryGetCurrentIndex(archive, workspace, out _))
            {
                var m = ReadManifest(workspace);archive.VerifyCurrent();
                progress?.Report(new(bundles.Length, bundles.Length, m.AssetCount, m.CabCount, 0, "Current index reused", stopwatch.Elapsed, 0, bundles.Length));return indexPath;
            }

            var old = EndfieldBundleIndexCache.TryLoad(indexPath);
            var manifest = ReadManifest(workspace);
            // Only an EXACT current pre-incremental baseline may be seeded from
            // compact records. Older game records cannot acquire today's MD5s.
            if (old == null && !File.Exists(cachePath) && manifest != null && (manifest.ParserIdentity == null || manifest.ParserIdentity == EndfieldBundleIndexCache.ParserIdentity) &&
                manifest.FormatVersion == FormatVersion && manifest.FailedBundles == 0 && manifest.BundleCount == bundles.Length && manifest.VfsFingerprint == archive.Fingerprint)
            {
                try { old = EndfieldBundleIndexCache.Scan(indexPath, archive.Fingerprint, bundles, manifest.IndexLength, manifest.AssetCount, manifest.CabCount, true, token); }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException) { old = null; }
                if (old != null)
                {
                    old.Save(cachePartial);archive.VerifyCurrent();File.Move(cachePartial, cachePath, true);
                    WriteManifest(workspace, archive, old, 0, bundles.Length, 0);
                    progress?.Report(new(bundles.Length, bundles.Length, old.Assets, old.Cabs, 0, "Existing index adopted without Bundle parsing", stopwatch.Elapsed, 0, bundles.Length));return indexPath;
                }
            }

            var checkpoint = ReadCheckpoint(checkpointPath, partialPath, archive.Fingerprint, bundles.Length);
            EndfieldBundleIndexCache prefix = null;
            if (checkpoint != null)
            {
                try
                {
                    prefix = EndfieldBundleIndexCache.Scan(partialPath, archive.Fingerprint, bundles.Take(checkpoint.NextBundle).ToArray(),
                        checkpoint.OutputLength, checkpoint.AssetCount, checkpoint.CabCount, false, token);
                    using var check = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    foreach (var row in prefix.Spans.Values) check.AppendData(row.Sha256);
                    if (Convert.ToHexString(check.GetCurrentHash()) != checkpoint.PrefixDigest) throw new InvalidDataException("Checkpoint records changed");
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException) { checkpoint = null;prefix = null; }
            }
            int startBundle = checkpoint?.NextBundle ?? 0, parsed = checkpoint?.ParsedBundles ?? 0, reused = checkpoint?.ReusedBundles ?? 0;
            int removed = old?.Spans.Keys.Except(bundles.Select(b => b.LogicalPath), StringComparer.OrdinalIgnoreCase).Count() ?? 0;
            long assetCount = checkpoint?.AssetCount ?? 0, cabCount = checkpoint?.CabCount ?? 0;
            var rows = prefix?.Spans ?? new Dictionary<string, EndfieldBundleIndexSpan>(StringComparer.OrdinalIgnoreCase);
            using var checkpointHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var row in rows.Values) checkpointHash.AppendData(row.Sha256);
            var buffer = new byte[64 * 1024];
            using var previous = old == null ? null : File.OpenRead(indexPath);
            using var output = new FileStream(partialPath, checkpoint == null ? FileMode.Create : FileMode.Open,
                FileAccess.ReadWrite, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            var writer = new BinaryWriter(output, new UTF8Encoding(false), true);
            int checkpointParsed = parsed, reportedParsed = parsed;TimeSpan checkpointTime = stopwatch.Elapsed, reportTime = stopwatch.Elapsed;
            void SaveCheckpoint(int next)
            {
                writer.Flush();output.Flush(false);
                WriteJsonAtomic(checkpointPath, new Checkpoint(FormatVersion, archive.Fingerprint, next,
                    output.Position, assetCount, cabCount, 0, EndfieldBundleIndexCache.ParserIdentity, parsed, reused, removed, Convert.ToHexString(checkpointHash.GetCurrentHash())));
                checkpointParsed = parsed;checkpointTime = stopwatch.Elapsed;
            }
            if (checkpoint == null)
            {
                EndfieldIndexStore.WriteHeader(writer, archive.Fingerprint);SaveCheckpoint(0);
                if (File.Exists(errorPath)) File.Delete(errorPath);
            }
            else { output.SetLength(checkpoint.OutputLength);output.Position = checkpoint.OutputLength; }
            if (parse == null) { AssetsHelper.Clear();parse = (bundle, destination) => ParseBundle(archive, bundle, game, destination); }

            for (int i = startBundle; i < bundles.Length; i++)
            {
                token.ThrowIfCancellationRequested();var bundle = bundles[i];long offset = output.Position;
                EndfieldBundleIndexSpan row = null;
                if (old != null && old.Spans.TryGetValue(bundle.LogicalPath, out var cached) &&
                    cached.Path == bundle.LogicalPath && cached.ContentMd5 == bundle.FileDataMd5 && cached.PayloadLength == bundle.Length)
                {
                    var digest = EndfieldBundleIndexCache.HashRange(previous, cached.Offset, cached.Size, buffer, token, output);
                    if (digest.SequenceEqual(cached.Sha256)) { row = cached with { Offset = offset };reused++; }
                    else { output.SetLength(offset);output.Position = offset; }
                }
                if (row == null)
                {
                    try
                    {
                        using var records = new MemoryStream();using var recordWriter = new BinaryWriter(records, Encoding.UTF8, true);
                        var counts = parse(bundle, recordWriter);recordWriter.Flush();
                        if (counts.Assets < 0 || counts.Cabs < 0) throw new InvalidDataException("Negative Bundle record count");
                        var bytes = records.GetBuffer().AsSpan(0, checked((int)records.Length));
                        row = new(bundle.LogicalPath, bundle.FileDataMd5, bundle.Length, offset, bytes.Length, counts.Assets, counts.Cabs, SHA256.HashData(bytes));
                        writer.Write(bytes);parsed++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        File.AppendAllText(errorPath, $"{DateTime.UtcNow:O}\t{bundle.LogicalPath}\t{ex}\n", Encoding.UTF8);
                        throw new InvalidDataException("Bundle indexing failed; previous index retained: " + bundle.LogicalPath, ex);
                    }
                }
                rows.Add(bundle.LogicalPath, row);checkpointHash.AppendData(row.Sha256);assetCount = checked(assetCount + row.Assets);cabCount = checked(cabCount + row.Cabs);
                int processed = i + 1;
                if (processed == bundles.Length || (processed % CheckpointInterval == 0 &&
                    (parsed > checkpointParsed || stopwatch.Elapsed - checkpointTime >= TimeSpan.FromSeconds(1)))) SaveCheckpoint(processed);
                if (processed == bundles.Length || (processed % CheckpointInterval == 0 && parsed > reportedParsed) || stopwatch.Elapsed - reportTime >= TimeSpan.FromMilliseconds(100))
                {
                    progress?.Report(new(processed, bundles.Length, assetCount, cabCount, 0, bundle.LogicalPath, stopwatch.Elapsed, parsed, reused, removed));
                    reportedParsed = parsed;reportTime = stopwatch.Elapsed;
                }
            }

            EndfieldIndexStore.WriteEnd(writer, assetCount, cabCount);writer.Flush();output.Flush(true);
            var result = new EndfieldBundleIndexCache { Fingerprint = archive.Fingerprint, IndexLength = output.Length, Assets = assetCount, Cabs = cabCount, Spans = rows };
            writer.Dispose();output.Dispose();previous?.Dispose();result.Save(cachePartial);token.ThrowIfCancellationRequested();archive.VerifyCurrent();
            // Sidecar and manifest validate index identity/extent. An interrupted
            // pair replacement cannot reuse old metadata against a new index.
            File.Move(partialPath, indexPath, true);File.Move(cachePartial, cachePath, true);WriteManifest(workspace, archive, result, parsed, reused, removed);
            File.Delete(checkpointPath);
            progress?.Report(new(bundles.Length, bundles.Length, assetCount, cabCount, 0, "Incremental index published", stopwatch.Elapsed, parsed, reused, removed));return indexPath;
        }

        private static (long Assets, long Cabs) ParseBundle(EndfieldVfsArchive archive, EndfieldVfsEntry bundle, Game game, BinaryWriter writer)
        {
            using var payload = new MemoryStream(archive.ReadPayload(bundle.LogicalPath), false);
            var data = AssetsHelper.BuildBundleIndexFromStream(payload, bundle.LogicalPath, game);long assets = 0;
            foreach (var asset in data.Assets) if (asset.Type != ClassIDType.AssetBundle) { EndfieldIndexStore.WriteAsset(writer, asset);assets++; }
            foreach (var file in data.Files) EndfieldIndexStore.WriteCab(writer, file, bundle.LogicalPath);
            return (assets, data.Files.Count);
        }

        private static Checkpoint ReadCheckpoint(string path, string partial, string fingerprint, int count)
        {
            try
            {
                var c = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(path));
                return c != null && c.FormatVersion == FormatVersion && c.ParserIdentity == EndfieldBundleIndexCache.ParserIdentity && c.VfsFingerprint == fingerprint &&
                    c.FailedBundles == 0 && c.NextBundle >= 0 && c.NextBundle <= count && c.ParsedBundles >= 0 && c.ReusedBundles >= 0 &&
                    c.ParsedBundles + c.ReusedBundles == c.NextBundle && c.OutputLength > 0 && c.OutputLength <= new FileInfo(partial).Length ? c : null;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException) { return null; }
        }

        private static void WriteManifest(string workspace, EndfieldVfsArchive archive, EndfieldBundleIndexCache cache, int parsed, int reused, int removed) =>
            WriteJsonAtomic(GetManifestPath(workspace), new EndfieldIndexManifest(FormatVersion, archive.Fingerprint, cache.Spans.Count,
                cache.Assets, cache.Cabs, 0, cache.IndexLength, DateTime.UtcNow, EndfieldBundleIndexCache.ParserIdentity, parsed, reused, removed));

        private static void WriteJsonAtomic<T>(string path, T value)
        {
            var temp = $"{path}.{Environment.ProcessId}.tmp";File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8);
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temp, path, true);return; }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 7) { Thread.Sleep(25 * (attempt + 1)); }
            }
        }
        private static string GetCheckpointPath(string workspace) => Path.Combine(Path.GetFullPath(workspace), "index", "endfield-index-progress.json");
        private static string GetManifestPath(string workspace) => Path.Combine(Path.GetFullPath(workspace), "index", "endfield-index-manifest.json");
    }
}
