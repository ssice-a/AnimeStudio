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

    internal static int Refresh(EndfieldVfsArchive archive, string workspace, IProgress<string> progress,
        CancellationToken token, string outputRoot = null, string unityVersion = null)
    {
        var packages = outputRoot == null ? Registered(workspace) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in Discover(outputRoot ?? Path.Combine(workspace, "assets"))) packages.Add(package);
        int updated = 0;var failures = new List<string>();var skipped = new List<string>();
        var pending = new List<(string Package, string Prefab)>();
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
                var currentIndex = EndfieldVfsAssetIndexBuilder.GetIndexPath(workspace);
                if (outputRoot == null && root.GetProperty("vfsFingerprint").GetString() == archive.Fingerprint &&
                    root.TryGetProperty("sourceIndex", out var index) && index.GetString() == currentIndex) continue;
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
            try
            {
                EndfieldPrefabPackageCommand.ExportOne(archive, workspace, item.Prefab, item.Package, unityVersion, prepared?.GetValueOrDefault(item.Prefab));
                Remember(workspace, item.Package);updated++;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failures.Add(item.Package + ": " + error);progress?.Report("Source retained after failed update: " + item.Package + ": " + error.Message);
            }
        }
        Directory.CreateDirectory(workspace);
        var skippedLog = Path.Combine(workspace, "source-update-skipped.log");
        if (skipped.Count > 0) File.WriteAllLines(skippedLog, skipped);else if (File.Exists(skippedLog)) File.Delete(skippedLog);
        var log = Path.Combine(workspace, "source-update-errors.log");
        if (failures.Count > 0) File.WriteAllLines(log, failures);else if (File.Exists(log)) File.Delete(log);
        progress?.Report($"Source update finished: {updated} updated, {skipped.Count} skipped without a Prefab anchor, {failures.Count} failed (previous packages retained)");
        if (failures.Count > 0) throw new IOException("Some unpacked sources could not be updated; previous packages retained. See " + log);
        return updated;
    }
}
