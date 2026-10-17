using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AnimeStudio.GUI
{
    internal sealed record EndfieldIndexManifest(int FormatVersion, string VfsFingerprint, int BundleCount,
        long AssetCount, long CabCount, int FailedBundles, long IndexLength, DateTime CreatedUtc,
        string ParserIdentity = null, int ParsedBundles = 0, int ReusedBundles = 0, int RemovedBundles = 0);

    // Shared read-only workspace contract; the repair tool does not include
    // Unity parsers, index builders, graphics libraries or AnimeStudio UI.
    internal static partial class EndfieldVfsAssetIndexBuilder
    {
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
                if (manifest == null || manifest.FormatVersion != EndfieldIndexStore.FormatVersion || manifest.FailedBundles != 0 ||
                    manifest.ParserIdentity != EndfieldBundleIndexCache.ParserIdentity || !File.Exists(EndfieldBundleIndexCache.PathFor(indexPath)) || manifest.VfsFingerprint != archive.Fingerprint ||
                    manifest.BundleCount != archive.Entries.Values.Count(x => x.LogicalPath.EndsWith(".ab", StringComparison.OrdinalIgnoreCase))) return false;
                using var index = File.OpenRead(indexPath);
                return EndfieldBundleIndexCache.ValidateEnvelope(index, manifest.VfsFingerprint,
                    manifest.IndexLength, manifest.AssetCount, manifest.CabCount, out _);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException) { return false; }
        }

        // Shared by AnimeStudio and EFF Repair. A moved CHK is not a changed
        // Bundle. Rebind a complete, content-identical index without Unity
        // parsing or downloading another copy of the same public index.
        internal static bool TryRebindUnchangedContent(EndfieldVfsArchive archive, string workspace, CancellationToken token)
        {
            if (TryGetCurrentIndex(archive, workspace, out _)) return true;
            var index = GetIndexPath(workspace);if (!File.Exists(index)) return false;
            using var gate = new FileStream(Path.Combine(Path.GetDirectoryName(index)!, "endfield-index.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return TryRebindUnchangedContentLocked(archive, workspace, token);
        }

        internal static bool TryGetOrRebindCurrentIndex(EndfieldVfsArchive archive, string workspace, out string indexPath, CancellationToken token = default)
        {
            return TryGetCurrentIndex(archive, workspace, out indexPath) || TryRebindUnchangedContent(archive, workspace, token);
        }

        private static bool TryRebindUnchangedContentLocked(EndfieldVfsArchive archive, string workspace, CancellationToken token)
        {
            if (TryGetCurrentIndex(archive, workspace, out var indexPath)) return true;
            var old = EndfieldBundleIndexCache.TryLoad(indexPath);var manifest = ReadManifest(workspace);
            if (old == null || manifest == null || manifest.FailedBundles != 0 ||
                manifest.ParserIdentity != EndfieldBundleIndexCache.ParserIdentity || manifest.VfsFingerprint != old.Fingerprint ||
                manifest.BundleCount != old.Spans.Count || manifest.IndexLength != old.IndexLength ||
                manifest.AssetCount != old.Assets || manifest.CabCount != old.Cabs) return false;
            var bundles = EndfieldPortableIndex.Bundles(archive);
            if (bundles.Length != old.Spans.Count || bundles.Any(b => !old.Spans.TryGetValue(b.LogicalPath, out var span) ||
                span.Path != b.LogicalPath || !string.Equals(span.ContentMd5, b.FileDataMd5, StringComparison.OrdinalIgnoreCase) || span.PayloadLength != b.Length)) return false;
            archive.VerifyCurrent();
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var bundle in bundles)
            {
                token.ThrowIfCancellationRequested();
                if (!sizes.TryGetValue(bundle.SourceChk, out var size))
                    sizes.Add(bundle.SourceChk, size = File.Exists(bundle.SourceChk) ? new FileInfo(bundle.SourceChk).Length : -1);
                if (bundle.Encrypted || bundle.Offset < 0 || bundle.Length <= 0 || bundle.Offset > size || bundle.Length > size - bundle.Offset)
                    throw new InvalidDataException("Current Bundle payload is unavailable: " + bundle.LogicalPath);
            }
            var staging = Path.Combine(Path.GetDirectoryName(indexPath)!, ".rebind-" + Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);
            try
            {
                var target = Path.Combine(staging, "index.eidx");var spans = new Dictionary<string, EndfieldBundleIndexSpan>(StringComparer.OrdinalIgnoreCase);
                using (var source = File.OpenRead(indexPath))
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
                {
                    EndfieldIndexStore.WriteHeader(writer, archive.Fingerprint);writer.Flush();var buffer = new byte[1024 * 1024];
                    foreach (var bundle in bundles)
                    {
                        var span = old.Spans[bundle.LogicalPath];long offset = output.Position;
                        var digest = EndfieldBundleIndexCache.HashRange(source, span.Offset, span.Size, buffer, token, output);
                        if (!digest.SequenceEqual(span.Sha256)) return false; // Builder reparses only the damaged range.
                        spans.Add(bundle.LogicalPath, span with { Offset = offset });
                    }
                    EndfieldIndexStore.WriteEnd(writer, old.Assets, old.Cabs);writer.Flush();output.Flush(true);
                }
                var rebound = new EndfieldBundleIndexCache { Fingerprint = archive.Fingerprint, IndexLength = new FileInfo(target).Length,
                    Assets = old.Assets, Cabs = old.Cabs, Spans = spans };
                var cache = EndfieldBundleIndexCache.PathFor(target);rebound.Save(cache);
                var boundManifest = manifest with { VfsFingerprint = archive.Fingerprint, IndexLength = rebound.IndexLength,
                    CreatedUtc = DateTime.UtcNow, ParsedBundles = 0, ReusedBundles = bundles.Length, RemovedBundles = 0 };
                var metadata = Path.Combine(staging, "manifest.json");File.WriteAllText(metadata, JsonSerializer.Serialize(boundManifest));
                token.ThrowIfCancellationRequested();archive.VerifyCurrent();
                // Each consumer checks the three identities as a set. A failed
                // partial publication cannot accept an index from another game.
                File.Move(target, indexPath, true);File.Move(cache, EndfieldBundleIndexCache.PathFor(indexPath), true);
                File.Move(metadata, GetManifestPath(workspace), true);return true;
            }
            finally { Directory.Delete(staging, true); }
        }

        private static string GetManifestPath(string workspace) => Path.Combine(Path.GetFullPath(workspace), "index", "endfield-index-manifest.json");
    }
}
