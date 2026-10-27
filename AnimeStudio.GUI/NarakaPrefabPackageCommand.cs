using AnimeStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeStudio.GUI;

internal static class NarakaPrefabPackageCommand
{
    internal sealed record EffectMount(NarakaPrefabLoader OwnerLoader, GameObject Owner, string ParentPrefab, string Path);

    internal sealed class EffectPrefab : IDisposable
    {
        internal string Container { get; }
        internal NarakaPrefabLoader Loader { get; }
        internal GameObject Root { get; }
        internal List<EffectMount> Mounts { get; } = new();

        internal EffectPrefab(string container, NarakaPrefabLoader loader, GameObject root)
        {
            Container = container;
            Loader = loader;
            Root = root;
        }

        public void Dispose() => Loader.Dispose();
    }

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
            Export(record, output, BuildPrefabRecordMap(catalog.Index.Records));
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

    internal static IReadOnlyDictionary<string, VirtualAssetRecord> BuildPrefabRecordMap(IEnumerable<VirtualAssetRecord> records) =>
        records.Where(r => r.Container.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => NormalizePath(r.Container), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    internal static void Export(VirtualAssetRecord record, string output,
        IReadOnlyDictionary<string, VirtualAssetRecord> prefabRecords)
    {
        if (!record.Container.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Select a Prefab");
        AssetsHelper.ClearOffsets();
        using var loader = new NarakaPrefabLoader(record.Source);
        var root = loader.FindRoot(record.Container, record.Source);
        var effects = ResolveEffects(record.Container, loader, root, prefabRecords, out var referenceWarnings);
        try
        {
            new NarakaPackageWriter(loader).Write(record.Container, root, effects, referenceWarnings, output);
            if (File.Exists(output + ".failed.txt")) File.Delete(output + ".failed.txt");
        }
        finally
        {
            foreach (var effect in effects) effect.Dispose();
        }
    }

    private static List<EffectPrefab> ResolveEffects(string rootPath, NarakaPrefabLoader rootLoader,
        GameObject root, IReadOnlyDictionary<string, VirtualAssetRecord> prefabRecords, out List<string> warnings)
    {
        warnings = new List<string>();
        var effects = new List<EffectPrefab>();
        var byPath = new Dictionary<string, EffectPrefab>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(NarakaPrefabLoader OwnerLoader, GameObject OwnerRoot, string ParentPath,
            NarakaPrefabLoader.PrefabReference Reference, bool RootReference)>();
        foreach (var reference in rootLoader.PrefabLoaderReferences(root))
            pending.Enqueue((rootLoader, root, rootPath, reference, true));

        try
        {
            while (pending.Count > 0)
            {
                var item = pending.Dequeue();
                var path = NormalizePath(item.Reference.Path);
                if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                if (item.RootReference && !IsEffectPath(path)) continue;
                if (!prefabRecords.TryGetValue(path, out var record))
                {
                    warnings.Add($"Static PrefabLoader reference is not in AppRes.info: {item.Reference.Path}");
                    continue;
                }

                var mountPath = TransformPath(item.OwnerLoader, item.OwnerRoot, item.Reference.Owner);
                var mount = new EffectMount(item.OwnerLoader, item.Reference.Owner, item.ParentPath, mountPath);
                if (byPath.TryGetValue(path, out var existing))
                {
                    existing.Mounts.Add(mount);
                    continue;
                }

                var effectLoader = new NarakaPrefabLoader(record.Source);
                try
                {
                    var effectRoot = effectLoader.FindRoot(record.Container, record.Source);
                    var effect = new EffectPrefab(record.Container, effectLoader, effectRoot);
                    effect.Mounts.Add(mount);
                    effects.Add(effect);
                    byPath.Add(path, effect);
                    foreach (var childReference in effectLoader.PrefabLoaderReferences(effectRoot))
                        pending.Enqueue((effectLoader, effectRoot, record.Container, childReference, false));
                }
                catch
                {
                    effectLoader.Dispose();
                    throw;
                }
            }
        }
        catch
        {
            foreach (var effect in effects) effect.Dispose();
            throw;
        }

        return effects;
    }

    private static string NormalizePath(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');

    private static bool IsEffectPath(string path)
    {
        var value = "/" + path.ToLowerInvariant();
        return value.Contains("/effect/") || value.Contains("/effects/") || value.Contains("/vfx/") ||
               value.Contains("/particle/");
    }

    private static string TransformPath(NarakaPrefabLoader loader, GameObject root, GameObject target)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Visit(Transform transform, string path)
        {
            if (transform == null || paths.ContainsKey(NarakaPackageWriter.Identity(transform))) return;
            paths.Add(NarakaPackageWriter.Identity(transform), path);
            foreach (var childPointer in transform.m_Children)
            {
                var child = loader.Require(childPointer, transform.assetsFile);
                var gameObject = loader.Require(child.m_GameObject, child.assetsFile);
                Visit(child, path.Length == 0 ? gameObject.Name : path + "/" + gameObject.Name);
            }
        }

        Visit(root.m_Transform, string.Empty);
        return paths.GetValueOrDefault(NarakaPackageWriter.Identity(target.m_Transform), target.Name);
    }
}
