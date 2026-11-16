using AnimeStudio;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AnimeStudio.GUI;

/// <summary>
/// One resolution contract for native compilation and its cache receipt.
/// Paths name game resources; CABs/PathIDs and index spans are generation evidence.
/// No borrowed resource payload is published or instantiated here.
/// </summary>
internal sealed class EiemEffectGameReferenceInputs
{
    internal sealed record Target(string Cab, long Id, int Class);
    internal Dictionary<string, Target> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal JArray Leases { get; } = new();
    internal JObject Receipt { get; private set; }
    private const string Format = "EFF_EFFECT_GAME_INPUTS/2";

    internal static string Logical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !(path.StartsWith("assets/") || path.StartsWith("packages/")) ||
            path.Contains('\\') || path.Contains(':') || path.Contains('\0') || path.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException("Invalid game logical resource path: " + path);
        return path.ToLowerInvariant();
    }

    internal static string Key(JToken row) => Logical((string)row["path"]) + "\n" + (string)row["asset"] + "\n" + (string)row["type"];

    internal static string IndexPath(JObject baseline)
    {
        string selected = EndfieldSharedResources.IndexWorkspace;
        string path = string.IsNullOrWhiteSpace(selected) ? (string)baseline["sourceIndex"] :
            EndfieldVfsAssetIndexBuilder.GetIndexPath(EndfieldSharedIndexes.WorkspacePath(selected));
        // An explicit current selection must never silently reuse a donor's
        // older index. A missing selected index needs repair at its source.
        if (!File.Exists(path)) throw new InvalidDataException("Select a current AnimeStudio game index before exporting game resource references");
        return Path.GetFullPath(path);
    }

    private static JObject IndexState(string path, out EndfieldBundleIndexCache spans)
    {
        spans = EndfieldBundleIndexCache.TryLoad(path);
        string generation;
        if (spans != null)
        {
            // TryLoad verified this compact sidecar and the index envelope.
            // The digest commits every Bundle record span without rehashing
            // a multi-gigabyte EIDX on every parameter-only export.
            using var file = File.OpenRead(EndfieldBundleIndexCache.PathFor(path));
            file.Position = file.Length - 32;var digest = new byte[32];file.ReadExactly(digest);
            generation = Convert.ToHexString(digest);
        }
        else
        {
            if (File.Exists(EndfieldBundleIndexCache.PathFor(path)))
                throw new InvalidDataException("Game index generation sidecar is invalid; refresh the game index");
            // Older indexes have no span receipt. Correctness is retained;
            // updating the index enables the bounded fast path.
            using var file = File.OpenRead(path);
            generation = Convert.ToHexString(SHA256.HashData(file));
        }
        return new JObject { ["path"] = path, ["generation"] = generation,
            ["parser"] = EndfieldBundleIndexCache.ParserIdentity,
            ["resolver"] = "logical/native-container/dynamic-effect-prefab/1", ["length"] = new FileInfo(path).Length };
    }

    internal static void VerifySpans(string path, EndfieldBundleIndexCache cache, JArray used)
    {
        if (cache == null) return; // Legacy generation hashes the entire file.
        using var file = File.OpenRead(path);var buffer = new byte[64 * 1024];
        foreach (var entry in used)
        {
            if (!cache.Spans.TryGetValue((string)entry, out var row) ||
                !EndfieldBundleIndexCache.HashRange(file, row.Offset, row.Size, buffer, CancellationToken.None).SequenceEqual(row.Sha256))
                throw new InvalidDataException("Game index records changed without a valid generation; refresh the index: " + entry);
        }
    }

    private static string Workspace(string index) => EndfieldSharedIndexes.WorkspacePath(index);

    private static JObject GameState(string path, string expected)
    {
        var failures = new List<string>();JObject compatible = null;
        foreach (string game in EndfieldSharedIndexes.GamesForWorkspace(Workspace(path)))
        {
            try
            {
                string fingerprint = EndfieldVfsArchive.ReadFingerprint(game);
                var candidate = new JObject { ["game"] = Path.GetFullPath(game), ["gameFingerprint"] = fingerprint };
                if (fingerprint == expected) return candidate;
                compatible ??= candidate;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
            { failures.Add(game + ": " + error.Message); }
        }
        return compatible ?? throw new InvalidDataException("No readable game installation for index " + path + ". " + string.Join("; ", failures));
    }

    private static void VerifyBundles(EndfieldVfsArchive archive, EndfieldBundleIndexCache cache, JArray used, string expected)
    {
        if (archive == null) return; // Explicit test reader supplies exact native containers.
        if (cache == null && archive.Fingerprint != expected)
            throw new InvalidDataException("Legacy game index has no per-Bundle content proof; refresh the game index");
        if (cache != null) foreach (string source in used.Values<string>())
        {
            if (!cache.Spans.TryGetValue(source, out var proof) || !archive.TryGet(source, out var current) ||
                current.Length != proof.PayloadLength || !current.FileDataMd5.Equals(proof.ContentMd5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Referenced game Bundle changed; refresh the game index: " + source);
        }
        archive.VerifyCurrent();
    }

    internal static JObject Describe(JObject edits, JObject baseline, JObject previous = null,
        Func<string, string, (string Name, string Identity)> rootReader = null)
    {
        string path = IndexPath(baseline);
        var state = IndexState(path, out var spans);
        if (rootReader == null) state.Merge(GameState(path, (string)baseline["vfsFingerprint"]));
        string request = RequestHash(edits, baseline);
        if ((string)previous?["format"] == Format &&
            (string)previous["request"] == request && JToken.DeepEquals(previous["index"], state) &&
            previous["spans"] is JArray && (string)previous["receiptSha256"] == ReceiptHash(previous))
        {
            VerifySpans(path, spans, (JArray)previous["spans"]);
            return previous;
        }
        return Resolve(edits, baseline, path, state, spans, request, rootReader).Receipt;
    }

    private static string ReceiptHash(JObject receipt)
    {
        var unsigned = (JObject)receipt.DeepClone();unsigned.Remove("receiptSha256");
        return EiemEffectBuildCommand.Hash(Encoding.UTF8.GetBytes(unsigned.ToString(Formatting.None)));
    }

    private static string RequestHash(JObject edits, JObject baseline) => EiemEffectBuildCommand.Hash(Encoding.UTF8.GetBytes(new JObject {
        ["vfsFingerprint"] = baseline["vfsFingerprint"], ["references"] = edits["gameReferences"]
    }.ToString(Formatting.None)));

    internal static EiemEffectGameReferenceInputs Resolve(JObject edits, JObject baseline) =>
        Resolve(edits, baseline, IndexPath(baseline), null, null, null, null);

    private static EiemEffectGameReferenceInputs Resolve(JObject edits, JObject baseline,
        string path, JObject state, EndfieldBundleIndexCache spans, string request,
        Func<string, string, (string Name, string Identity)> rootReader)
    {
        state ??= IndexState(path, out spans);
        if (rootReader == null && state["game"] == null) state.Merge(GameState(path, (string)baseline["vfsFingerprint"]));
        EndfieldVfsArchive archive = null;
        if (edits["gameReferences"] is not JArray refs || refs.Count > 4096)
            throw new InvalidDataException("Invalid game resource references");
        IReadOnlyCollection<EndfieldBundleIndexSpan> verifiedSpans = null;
        var verifiedRoots = new Dictionary<string, (string Name, string Identity)>(StringComparer.OrdinalIgnoreCase);
        var prior = edits["_gameReferenceInputs"] as JObject;
        if ((string)prior?["format"] == Format && (string)prior["request"] == RequestHash(edits, baseline) &&
            JToken.DeepEquals(prior["index"], state) && (string)prior["receiptSha256"] == ReceiptHash(prior) && prior["spans"] is JArray priorSpans && spans != null)
        {
            VerifySpans(path, spans, priorSpans);
            verifiedSpans = priorSpans.Values<string>().Select(s => spans.Spans[s]).ToArray();
            // These exact source Prefabs were resolved from a full index scan
            // for this generation. Their native roots are still rechecked.
            refs = (JArray)prior["bindings"].DeepClone();
            foreach (var proof in refs)
                verifiedRoots[(string)proof["sourcePrefab"]] = ((string)proof["sourceRoot"], (string)proof["sourceRootIdentity"]);
        }
        var paths = refs.Select(r => Logical((string)r["path"])).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceProofs = refs.Select(r => EiemEffectGameReferences.SourceLease(r, baseline, null, 0, 0, portableOnly: true)).ToArray();
        var declared = sourceProofs.Where(p => p != null).Select(p => Logical((string)p["sourcePrefab"]))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool discover = sourceProofs.Any(p => p == null);
        var index = EndfieldIndexStore.Load(path, recordFilter: r => paths.Contains(r.Container) ||
            declared.Contains(r.Container) || discover && r.Type == "GameObject" && r.Container.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase),
            verifiedSpans: verifiedSpans);
        if (index.VfsFingerprint != (string)baseline["vfsFingerprint"])
            throw new InvalidDataException("Effect source and game resource index differ; refresh the source before exporting");
        var result = new EiemEffectGameReferenceInputs();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bindings = new JArray();
        var prefabRows = index.Assets.Records.Where(r => r.Type == "GameObject" && r.Container.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.Container, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        string workspace = Workspace(path);
        var rootCache = new Dictionary<string, (string Name, string Identity)>(StringComparer.OrdinalIgnoreCase);
        (string Name, string Identity) ExactRoot(string prefab, string source)
        {
            if (rootCache.TryGetValue(prefab, out var known)) return known;
            if (rootReader != null) return rootReader(prefab, source);
            if (verifiedRoots.TryGetValue(prefab, out var verified)) return verified;
            archive ??= EndfieldVfsArchive.Open((string)state["game"]);
            if (archive.Fingerprint != (string)state["gameFingerprint"])
                throw new InvalidDataException("Game installation changed during effect resource resolution");
            VerifyBundles(archive, spans, new JArray(index.Dependencies.ResolveBundleClosure(source)), index.VfsFingerprint);
            var value = ReadRoot(archive, workspace, prefab, source, index.Dependencies.GetCabNames(source));
            rootCache.Add(prefab, value);return value;
        }
        foreach (var row in refs)
        {
            string logical = Logical((string)row["path"]), name = (string)row["asset"], kind = (string)row["type"];
            var found = index.Assets.Records.Where(r => r.Container.Equals(logical, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(name) || r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) &&
                (kind == "Object" || r.Type == kind || kind == "Texture" && r.Type is "Texture2D" or "Cubemap" or "Texture3D" or "Texture2DArray" or "CubemapArray"))
                .Distinct().ToArray();
            if (found.Length != 1 || !Enum.TryParse<ClassIDType>(found[0].Type, out var classId))
                throw new InvalidDataException("Game resource is absent, ambiguous or incompatible: " + logical + " / " + name + " (" + kind + ")");
            var asset = found[0];var cabs = index.Dependencies.GetCabNames(asset.Source).ToArray();
            if (cabs.Length != 1) throw new InvalidDataException("Game resource native file is ambiguous: " + logical);
            result.Targets[Key(row)] = new(cabs[0], asset.PathId, (int)classId);used.Add(asset.Source);
            var proof = EiemEffectGameReferences.SourceLease(row, baseline, null, 0, 0, portableOnly: true);
            if (proof == null)
            {
                try { proof = EiemEffectGameReferences.SourceLease(row, baseline, cabs[0], asset.PathId, (int)classId); }
                catch (Exception error) when (error is IOException or InvalidDataException) { }
            }
            string prefab = (string)proof?["sourcePrefab"];
            string selectedSource = null;
            if (prefab != null)
            {
                prefab = Logical(prefab);
                if (!prefabRows.TryGetValue(prefab, out var records)) throw new InvalidDataException("Source Prefab is absent from the current game index: " + prefab);
                var sources = records.Select(r => r.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (sources.Length != 1) throw new InvalidDataException("Source Prefab native file is ambiguous: " + prefab);
                selectedSource = sources[0];
                if (!index.Dependencies.ResolveBundleClosure(selectedSource).Contains(asset.Source, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("Game resource is outside its declared source Prefab dependency closure: " + prefab);
                var exact = ExactRoot(prefab, selectedSource);
                if (exact.Name == null || exact.Name != (string)proof["sourceRoot"] ||
                    proof["sourceRootIdentity"] != null && !string.Equals(exact.Identity, (string)proof["sourceRootIdentity"], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Source Prefab root differs from its exact container entry: " + prefab);
                proof["sourceRootIdentity"] = exact.Identity;proof["sourceVfsFingerprint"] = index.VfsFingerprint;
            }
            else
            {
                // Reverse Bundle dependencies select possible owning Prefabs;
                // only their AssetInfo.asset can identify the actual root.
                var direct = index.Dependencies.DirectBundleDependencies(out _);
                var reverse = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in direct) foreach (var dependency in pair.Value)
                { if (!reverse.TryGetValue(dependency, out var parents))reverse[dependency] = parents = new();parents.Add(pair.Key); }
                var distances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [asset.Source] = 0 };
                var queue = new Queue<string>();queue.Enqueue(asset.Source);
                while (queue.TryDequeue(out var current)) if (reverse.TryGetValue(current, out var parents))
                    foreach (var parent in parents) if (distances.TryAdd(parent, distances[current] + 1)) queue.Enqueue(parent);
                var candidates = prefabRows.Where(p => p.Key.StartsWith("assets/beyond/dynamicassets/gameplay/effects/prefabs/", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(p => p.Value.Select(r => r.Source).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(distances.ContainsKey).Select(s => (Prefab: p.Key, Source: s, Distance: distances[s],
                        Closure: index.Dependencies.ResolveBundleClosure(s))))
                    // A shared shader can be reached by thousands of scenes.
                    // Prefer the smallest actual dependency owner, not the
                    // lexicographically first enormous cutscene/character.
                    .OrderBy(c => c.Closure.Count).ThenBy(c => c.Distance)
                    .ThenBy(c => c.Prefab, StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var candidate in candidates)
                {
                    var exact = ExactRoot(candidate.Prefab, candidate.Source);
                    if (exact.Name == null) continue;
                    prefab = candidate.Prefab;selectedSource = candidate.Source;
                    proof = new JObject { ["sourcePrefab"] = prefab, ["sourceRoot"] = exact.Name,
                        ["sourceRootIdentity"] = exact.Identity, ["sourceVfsFingerprint"] = index.VfsFingerprint };
                    break;
                }
                if (proof == null) throw new InvalidDataException("No exact dynamic effect Prefab entry owns this game resource. " +
                    "Select a source Prefab from an imported game template: " + logical);
            }
            foreach (string source in index.Dependencies.ResolveBundleClosure(selectedSource)) used.Add(source);
            var lease = new JObject { ["path"] = logical, ["asset"] = asset.Name, ["type"] = asset.Type };
            lease.Merge(proof);
            if (!result.Leases.Any(l => JToken.DeepEquals(l, lease))) result.Leases.Add(lease);
            var binding = (JObject)row.DeepClone();binding.Merge(proof);
            // Resource discovery happens when selecting a new logical path.
            // Subsequent cold exports compile this selected native generation
            // directly and have no dependency on the global game index.
            binding["resourceIdentity"] = cabs[0] + ":" + asset.PathId;
            binding["resourceClassId"] = (int)classId;
            bindings.Add(binding);
        }
        archive?.VerifyCurrent();
        var spanRows = new JArray(used.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        VerifySpans(path, spans, spanRows);
        VerifyBundles(archive, spans, spanRows, index.VfsFingerprint);
        result.Receipt = new JObject { ["format"] = Format, ["request"] = request,
            ["index"] = state, ["spans"] = spanRows, ["bindings"] = bindings,
            ["targets"] = JObject.FromObject(result.Targets), ["leases"] = result.Leases.DeepClone() };
        result.Receipt["receiptSha256"] = ReceiptHash(result.Receipt);
        return result;
    }

    private static (string Name, string Identity) ReadRoot(EndfieldVfsArchive archive, string workspace,
        string prefab, string source, IReadOnlyCollection<string> cabs)
    {
        var flags = Enum.GetValues<ClassIDType>().Distinct().ToDictionary(t => t, t => (t.CanParse(), t.CanExport()));
        var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.ArknightsEndfield), ResolveDependencies = false,
            FilterData = new AssetsManager.AssetFilterData { Items = new List<AssetsManager.AssetFilterDataItem>() } };
        try
        {
            TypeFlags.SetTypes(new Dictionary<ClassIDType, (bool, bool)>());
            foreach (var type in new[] { ClassIDType.GameObject, ClassIDType.AssetBundle }) TypeFlags.SetType(type, true, true);
            manager.LoadFiles(new[] { archive.ExtractToCache(source, workspace) }, mergeSplitAssets: false);
            var root = EndfieldPrefabDocument.FindRoot(manager.assetsFileList.SelectMany(f => f.Objects).OfType<GameObject>(),
                new VirtualAssetFile(prefab, Array.Empty<VirtualAssetRecord>()), cabs);
            return root == null ? (null, null) : (root.m_Name, root.assetsFile.fileName + ":" + root.m_PathID);
        }
        finally { manager.Clear();TypeFlags.SetTypes(flags); }
    }

    internal static int Run(string[] args)
    {
        if (args.Length != 4) { Console.Error.WriteLine("Usage: --describe-eff-effect-inputs <source> <edits.json> <receipt.json>");return 2; }
        try
        {
            var baseline = JObject.Parse(File.ReadAllText(EiemEffectBuildCommand.SafePath(args[1], "source/manifest.json")));
            var edits = JObject.Parse(File.ReadAllText(args[2]));JObject previous = null;
            try { previous = JObject.Parse(File.ReadAllText(args[3])); } catch (Exception error) when (error is IOException or JsonException) { }
            var result = Describe(edits, baseline, previous);
            string target = Path.GetFullPath(args[3]);Directory.CreateDirectory(Path.GetDirectoryName(target));
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, result.ToString(Formatting.None));File.Move(temporary, target, true); }
            finally { if (File.Exists(temporary))File.Delete(temporary); }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error);return 1; }
    }
}
