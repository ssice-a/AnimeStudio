using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace AnimeStudio.GUI;

internal static class EndfieldSourceRefresh
{
    private static string Registry(string workspace) => Path.Combine(workspace, "extracted-packages.json");
    internal static void Remember(string workspace, string package)
    {
        var paths = Registered(workspace);paths.Add(Path.GetFullPath(package));
        Directory.CreateDirectory(workspace);
        var path = Registry(workspace);var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)));
        File.Move(temp, path, true);
    }

    private static HashSet<string> Registered(string workspace)
    {
        var path = Registry(workspace);
        return new HashSet<string>(File.Exists(path) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))! : Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Discover(string root)
    {
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) yield break;
        if (File.Exists(Path.Combine(root, "source", "manifest.json"))) { yield return root;yield break; }
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(directory).StartsWith('.')) continue;
            foreach (var package in Discover(directory)) yield return package;
        }
    }

    internal static bool BundleInputsMatch(JsonElement manifest,
        IReadOnlyDictionary<string, EndfieldVfsEntry> currentBundles,
        IReadOnlyCollection<string> currentClosure = null)
    {
        if (!manifest.TryGetProperty("closure", out var closureElement) ||
            closureElement.ValueKind != JsonValueKind.Array ||
            !manifest.TryGetProperty("bundleInputs", out var inputsElement) ||
            inputsElement.ValueKind != JsonValueKind.Array || inputsElement.GetArrayLength() == 0)
            return false;

        static string Normalize(string path) => path.Replace('\\', '/').Trim('/').ToLowerInvariant();
        var closure = closureElement.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => Normalize(item.GetString() ?? string.Empty))
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (currentClosure != null)
        {
            var normalizedCurrent = currentClosure.Select(Normalize)
                .Where(path => path.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!closure.SetEquals(normalizedCurrent)) return false;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputsElement.EnumerateArray())
        {
            if (!input.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String ||
                !input.TryGetProperty("fileDataMd5", out var md5Element) || md5Element.ValueKind != JsonValueKind.String ||
                !input.TryGetProperty("length", out var lengthElement) || lengthElement.ValueKind != JsonValueKind.Number)
                return false;
            var path = Normalize(pathElement.GetString() ?? string.Empty);
            if (path.Length == 0 || !closure.Contains(path) || !seen.Add(path) ||
                !currentBundles.TryGetValue(path, out var current) ||
                current.Length != lengthElement.GetInt64() ||
                !string.Equals(current.FileDataMd5, md5Element.GetString(), StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return seen.SetEquals(closure);
    }

    internal static bool NeedsRefresh(JsonElement manifest, string currentFingerprint,
        string currentIndex, IReadOnlyDictionary<string, EndfieldVfsEntry> currentBundles,
        bool force = false, IReadOnlyCollection<string> currentClosure = null)
    {
        if (force) return true;
        if (BundleInputsMatch(manifest, currentBundles, currentClosure)) return false;

        // Legacy manifests have no per-Bundle stamps. Avoid rewriting them on
        // every open when their exact original VFS and index are still active.
        bool sameVfs = manifest.TryGetProperty("vfsFingerprint", out var fingerprint) &&
            fingerprint.ValueKind == JsonValueKind.String &&
            string.Equals(fingerprint.GetString(), currentFingerprint, StringComparison.Ordinal);
        bool sameIndex = manifest.TryGetProperty("sourceIndex", out var index) &&
            index.ValueKind == JsonValueKind.String &&
            string.Equals(index.GetString(), currentIndex, StringComparison.OrdinalIgnoreCase);
        bool hasBundleInputs = manifest.TryGetProperty("bundleInputs", out var inputs) &&
            inputs.ValueKind == JsonValueKind.Array;
        return hasBundleInputs || !sameVfs || !sameIndex;
    }

    internal static int Refresh(EndfieldVfsArchive archive, string workspace, IProgress<string> progress,
        CancellationToken token, string outputRoot = null, string unityVersion = null,
        SemaphoreSlim packageLock = null, EndfieldBundleDependencyIndex dependencies = null)
    {
        var packages = outputRoot == null ? Registered(workspace) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in Discover(outputRoot ?? Path.Combine(workspace, "assets"))) packages.Add(package);
        int updated = 0, unchanged = 0;var failures = new List<string>();var skipped = new List<string>();
        var pending = new List<(string Package, string Prefab)>();
        var currentIndex = EndfieldVfsAssetIndexBuilder.GetIndexPath(workspace);
        foreach (var package in packages.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var manifest = Path.Combine(package, "source", "manifest.json");
            if (!File.Exists(manifest)) continue;
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(manifest));var root = json.RootElement;
                if (root.GetProperty("format").GetString() != "EFFSOURCE") continue;
                var prefab = root.TryGetProperty("prefab", out var anchor) && anchor.ValueKind == JsonValueKind.String ? anchor.GetString() : null;
                if (string.IsNullOrWhiteSpace(prefab))
                {
                    var message = "Skipped source without a Prefab anchor (retained unchanged): " + package;
                    skipped.Add(message);progress?.Report(message);continue;
                }
                if (!NeedsRefresh(root, archive.Fingerprint, currentIndex, archive.Entries,
                        force: outputRoot != null,
                        currentClosure: CurrentClosure(root, dependencies)))
                {
                    unchanged++;
                    continue;
                }
                pending.Add((package, prefab));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failures.Add(package + ": " + error);progress?.Report("Source retained after failed update: " + package + ": " + error.Message);
            }
        }
        Dictionary<string, EndfieldPrefabExportIndex> prepared = null;
        if (pending.Count > 0)
        {
            progress?.Report("Resolving updated sources from the current index…");
            try { prepared = EndfieldIndexStore.LoadPrefabExportIndexes(EndfieldVfsAssetIndexBuilder.GetIndexPath(workspace), pending.Select(p => p.Prefab)); }
            catch (FileNotFoundException) { } // Isolate a retired Prefab; other packages may still update.
        }
        foreach (var item in pending)
        {
            token.ThrowIfCancellationRequested();progress?.Report("Updating unpacked source: " + item.Package);
            bool lockHeld = false;
            try
            {
                if (packageLock != null) { packageLock.Wait(token);lockHeld = true; }
                EndfieldPrefabPackageCommand.ExportOne(archive, workspace, item.Prefab, item.Package, unityVersion, prepared?.GetValueOrDefault(item.Prefab));
                Remember(workspace, item.Package);updated++;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failures.Add(item.Package + ": " + error);progress?.Report("Source retained after failed update: " + item.Package + ": " + error.Message);
            }
            finally { if (lockHeld) packageLock.Release(); }
        }
        Directory.CreateDirectory(workspace);
        var skippedLog = Path.Combine(workspace, "source-update-skipped.log");
        if (skipped.Count > 0) File.WriteAllLines(skippedLog, skipped);else if (File.Exists(skippedLog)) File.Delete(skippedLog);
        var log = Path.Combine(workspace, "source-update-errors.log");
        if (failures.Count > 0) File.WriteAllLines(log, failures);else if (File.Exists(log)) File.Delete(log);
        progress?.Report($"Source update finished: {updated} updated, {unchanged} unchanged, {skipped.Count} skipped without a Prefab anchor, {failures.Count} failed (failed packages retained)");
        if (failures.Count > 0) throw new IOException("Some unpacked sources could not be updated; failed packages were retained. See " + log);
        return updated;
    }

    private static IReadOnlyCollection<string> CurrentClosure(JsonElement manifest,
        EndfieldBundleDependencyIndex dependencies)
    {
        if (dependencies == null ||
            !manifest.TryGetProperty("sourceBundle", out var source) ||
            source.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(source.GetString()))
            return null;
        return dependencies.ResolveBundleClosure(source.GetString())
            .ToArray();
    }
}
