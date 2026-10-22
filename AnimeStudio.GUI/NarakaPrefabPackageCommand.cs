using AnimeStudio;
using System;
using System.IO;
using System.Linq;

namespace AnimeStudio.GUI;

internal static class NarakaPrefabPackageCommand
{
    internal static int Run(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("Usage: --export-naraka-prefab <game-root-or-AppRes.info> <logical-prefab> <output-directory>");
            return 2;
        }
        var output = Path.GetFullPath(args[3]);
        try
        {
            var catalogPath = NarakaAppResIndex.FindCatalog(new[] { args[1] }) ?? throw new FileNotFoundException("AppRes.info not found");
            var catalog = NarakaAppResIndex.Load(catalogPath);
            var path = args[2].Replace('\\', '/').Trim('/');
            var record = catalog.Index.Records.SingleOrDefault(r => string.Equals(r.Container, path, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Prefab is not in AppRes.info: " + path);
            PrepareMap(catalogPath, catalog);
            Export(record, output);
            return 0;
        }
        catch (Exception error)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output + ".failed.txt", error.ToString());
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    internal static void PrepareMap(string path, NarakaAppResIndex.LoadResult catalog)
    {
        var cache = NarakaAppResIndex.GetCabCache(path);
        if (File.Exists(cache.MarkerPath) && File.ReadAllText(cache.MarkerPath).Trim() == cache.Fingerprint &&
            File.Exists(cache.MapPath) && AssetsHelper.LoadCABMap(cache.MapPath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(cache.MapPath)!);
        if (!AssetsHelper.BuildCABMap(catalog.BundlePaths, "Naraka-auto", catalog.StreamingAssetsRoot,
                GameManager.GetGameByType(GameType.Naraka), cache.MapPath)) throw new InvalidDataException("Unable to build CAB dependency index");
        File.WriteAllText(cache.MarkerPath, cache.Fingerprint);
    }

    internal static void Export(VirtualAssetRecord record, string output)
    {
        if (!record.Container.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Select a Prefab");
        AssetsHelper.ClearOffsets();
        using var loader = new NarakaPrefabLoader(record.Source);
        var root = loader.FindRoot(record.Container, record.Source);
        new NarakaPackageWriter(loader).Write(record.Container, root, output);
        if (File.Exists(output + ".failed.txt")) File.Delete(output + ".failed.txt");
    }
}
