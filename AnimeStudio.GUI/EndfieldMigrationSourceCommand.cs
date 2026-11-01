using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AnimeStudio.GUI;

internal static class EndfieldMigrationSourceCommand
{
    private sealed record MeshTarget(string Path, string Asset);

    internal static int Run(string[] args)
    {
        try
        {
            if (args.Length < 6 || args[0] != "--export-eff-migration-sources")
                throw new ArgumentException("Usage: --export-eff-migration-sources <game> <workspace> <output-root> --mod-directory <format-2-mod> [...]");

            var game = Path.GetFullPath(args[1]);
            var workspace = Path.GetFullPath(args[2]);
            var outputRoot = Path.GetFullPath(args[3]);
            var modDirectories = new List<string>();
            for (var index = 4; index < args.Length;)
            {
                if (args[index] != "--mod-directory" || index + 1 >= args.Length)
                    throw new ArgumentException("Expected --mod-directory followed by a Format 2 Mod directory.");
                modDirectories.Add(Path.GetFullPath(args[index + 1]));
                index += 2;
            }
            if (modDirectories.Count == 0)
                throw new ArgumentException("At least one Format 2 Mod directory is required.");

            var targets = modDirectories.SelectMany(ReadMeshTargets)
                .GroupBy(row => MeshKey(row.Path, row.Asset), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).OrderBy(row => row.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Asset, StringComparer.OrdinalIgnoreCase).ToArray();
            if (targets.Length == 0)
                throw new InvalidDataException("The selected Format 2 Mods do not declare any Mesh targets.");

            Directory.CreateDirectory(outputRoot);
            var archive = EndfieldVfsArchive.Open(game);
            if (!EndfieldVfsAssetIndexBuilder.TryGetOrRebindCurrentIndex(archive, workspace, out var indexPath))
                throw new InvalidDataException("The selected AnimeStudio index does not match the current game VFS. Refresh it in AnimeStudio first.");

            var exportIndexes = EndfieldIndexStore.LoadPostmodelPrefabExportIndexes(indexPath,
                targets.Select(row => (row.Path, row.Asset)).ToArray());
            var exported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failures = new List<string>();
            foreach (var exportIndex in exportIndexes.Values.OrderBy(row => row.Prefab.Container, StringComparer.OrdinalIgnoreCase))
            {
                var matchingTargets = targets.Where(target => exportIndex.Assets.Any(row => IsTarget(row, target))).ToArray();
                if (matchingTargets.Length == 0) continue;

                var prefab = exportIndex.Prefab.Container.Replace('\\', '/');
                var relative = Path.ChangeExtension(prefab, ".eff").Replace('/', Path.DirectorySeparatorChar);
                var package = Path.GetFullPath(Path.Combine(outputRoot, relative));
                if (!package.StartsWith(Path.TrimEndingDirectorySeparator(outputRoot) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Indexed Prefab output path escapes the migration staging directory.");
                try
                {
                    EndfieldPrefabPackageCommand.ExportOne(archive, workspace, prefab, package,
                        preparedIndex: exportIndex);
                    var resources = ReadMeshResources(package);
                    foreach (var target in matchingTargets)
                    {
                        if (!resources.Contains(MeshKey(target.Path, target.Asset))) continue;
                        exported.Add(MeshKey(target.Path, target.Asset));
                    }
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        status = "source-exported", prefab, meshTargets = matchingTargets.Length,
                        verifiedMeshTargets = matchingTargets.Count(row => resources.Contains(MeshKey(row.Path, row.Asset)))
                    }));
                }
                catch (Exception error)
                {
                    failures.Add(prefab + ": " + error.Message);
                    Console.WriteLine(JsonSerializer.Serialize(new { status = "source-failed", prefab, error = error.Message }));
                }
            }

            archive.VerifyCurrent();
            var missing = targets.Where(row => !exported.Contains(MeshKey(row.Path, row.Asset))).ToArray();
            if (missing.Length != 0)
            {
                var detail = string.Join(Environment.NewLine, missing.Select(row => "Mesh source was not exported: " + row.Path + " / " + row.Asset));
                if (failures.Count != 0) detail += Environment.NewLine + string.Join(Environment.NewLine, failures);
                throw new InvalidDataException(detail);
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "complete", meshTargets = targets.Length,
                sourcePackages = exportIndexes.Count, output = outputRoot
            }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static IReadOnlyList<MeshTarget> ReadMeshTargets(string directory)
    {
        var manifest = Path.Combine(directory, "mod.ini");
        if (!File.Exists(manifest)) throw new FileNotFoundException("Format 2 Mod manifest is missing.", manifest);
        var result = new List<MeshTarget>();
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sectionName = string.Empty;

        void Flush()
        {
            if (!sectionName.StartsWith("Replace", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sections.GetValueOrDefault("handling"), "skip", StringComparison.OrdinalIgnoreCase)) return;
            Add("target.path", "target.asset");
            var lods = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in sections)
            {
                var parts = pair.Key.Split('.');
                if (parts.Length != 3 || !string.Equals(parts[0], "lod", StringComparison.OrdinalIgnoreCase)) continue;
                if (parts[2] is not ("path" or "asset")) continue;
                if (!lods.TryGetValue(parts[1], out var values)) lods.Add(parts[1], values = new(StringComparer.OrdinalIgnoreCase));
                values[parts[2]] = pair.Value;
            }
            foreach (var lod in lods.Values)
            {
                if (lod.Count != 2) throw new InvalidDataException("Format 2 replacement has an incomplete LOD target in " + manifest);
                result.Add(new MeshTarget(lod["path"], lod["asset"]));
            }
        }

        void Add(string pathKey, string assetKey)
        {
            if (!sections.TryGetValue(pathKey, out var path) || !sections.TryGetValue(assetKey, out var asset) ||
                string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(asset))
                throw new InvalidDataException("Format 2 replacement target is incomplete in " + manifest);
            result.Add(new MeshTarget(path.Trim(), asset.Trim()));
        }

        foreach (var raw in File.ReadLines(manifest, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                Flush();
                sectionName = line[1..^1].Trim();
                sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator > 0 && sectionName.StartsWith("Replace", StringComparison.OrdinalIgnoreCase))
                sections[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        Flush();
        return result;
    }

    private static HashSet<string> ReadMeshResources(string package)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(package, "source", "manifest.json")));
        if (!document.RootElement.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("AnimeStudio source package has no resource list: " + package);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in resources.EnumerateArray())
        {
            if (row.TryGetProperty("type", out var type) && type.GetString() == "Mesh" &&
                row.TryGetProperty("logicalPath", out var path) && row.TryGetProperty("name", out var name))
                result.Add(MeshKey(path.GetString() ?? string.Empty, name.GetString() ?? string.Empty));
        }
        return result;
    }

    private static bool IsTarget(VirtualAssetRecord row, MeshTarget target) =>
        row.Type.Equals("Mesh", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(MeshKey(row.Container, row.Name), MeshKey(target.Path, target.Asset), StringComparison.OrdinalIgnoreCase);

    private static string MeshKey(string path, string asset) =>
        path.Replace('\\', '/').Trim('/') + "\n" + (asset ?? string.Empty).Trim();
}
