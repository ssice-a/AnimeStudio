using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AnimeStudio.GUI;

// Whole-package publication. Blender uses the same named mutex while reading.
internal static class EiemPackagePublication
{
    internal const string Receipt = "source/generated-files.json";
    internal static string LockName(string package)
    {
        var path = Path.GetFullPath(package).TrimEnd('\\', '/').Replace('\\', '/');
        path = new string(path.Select(c => c >= 'A' && c <= 'Z' ? (char)(c + 32) : c).ToArray());
        return @"Local\EFFSource-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
    }

    internal static void Publish(string package, Action<string> generate)
    {
        package = Path.GetFullPath(package).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(package) ?? throw new IOException("Package needs a parent directory");
        Directory.CreateDirectory(parent);
        using var gate = new Mutex(false, LockName(package));
        bool acquired;
        try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Blender or another export is using this source package. Retry after it finishes: " + package);
        var staging = Path.Combine(parent, ".eff-stage-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(parent, ".eff-backups", Path.GetFileName(package), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            generate(staging);
            if (!File.Exists(Path.Combine(staging, "source", "manifest.json")))
                throw new InvalidDataException("Export did not produce a complete source manifest; previous package retained");
            var generated = Files(staging).ToDictionary(p => Path.GetRelativePath(staging, p).Replace('\\', '/'),
                p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))), StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(package))
            {
                var managed = Managed(package);
                foreach (var file in Files(package))
                {
                    var relative = Path.GetRelativePath(package, file).Replace('\\', '/');
                    if (managed.Contains(relative) || generated.ContainsKey(relative) || relative == Receipt) continue;
                    var target = Path.Combine(staging, relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file, target);
                }
            }
            File.WriteAllText(Path.Combine(staging, Receipt), JsonSerializer.Serialize(new { format = "EFF_GENERATED_FILES/1", files = generated }, new JsonSerializerOptions { WriteIndented = true }));
            bool moved = false;
            if (Directory.Exists(package)) { Directory.CreateDirectory(Path.GetDirectoryName(backup)!);Directory.Move(package, backup);moved = true; }
            try { Directory.Move(staging, package); }
            catch { if (moved) Directory.Move(backup, package);throw; }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            gate.ReleaseMutex();
        }
    }

    private static IEnumerable<string> Files(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Source package cannot be a directory link: " + root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Source package contains a directory/file link: " + entry);
            if ((attributes & FileAttributes.Directory) != 0) { foreach (var file in Files(entry)) yield return file; }
            else yield return entry;
        }
    }

    private static HashSet<string> Managed(string root)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mod.ini", "source/manifest.json", Receipt, "export-errors.txt", "export-status.txt" };
        var receipt = Path.Combine(root, Receipt);
        if (File.Exists(receipt))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(receipt));
            if (json.RootElement.GetProperty("format").GetString() != "EFF_GENERATED_FILES/1") throw new InvalidDataException("Unsupported export receipt");
            foreach (var row in json.RootElement.GetProperty("files").EnumerateObject()) result.Add(row.Name);
            return result;
        }
        var manifest = Path.Combine(root, "source", "manifest.json");
        if (!File.Exists(manifest)) return result;
        using var old = JsonDocument.Parse(File.ReadAllText(manifest));
        foreach (var kind in new[] { "serializedFiles", "rawStreams" })
            if (old.RootElement.TryGetProperty(kind, out var rows)) foreach (var row in rows.EnumerateArray())
                result.Add(row.GetProperty("snapshot").GetProperty("file").GetString()!);
        if (old.RootElement.TryGetProperty("resources", out var resources)) foreach (var row in resources.EnumerateArray())
            if (row.TryGetProperty("authorFile", out var file) && file.ValueKind == JsonValueKind.String) result.Add(file.GetString()!);
        return result;
    }
}
