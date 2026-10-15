using System;
using System.IO;
using System.Linq;
using System.Text.Json;

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

        private static string GetManifestPath(string workspace) => Path.Combine(Path.GetFullPath(workspace), "index", "endfield-index-manifest.json");
    }
}
