using AnimeStudio.GUI;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text;

internal static class GameReferenceInputsChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        string directory = Path.Combine(Path.GetTempPath(), "eff-game-inputs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string originalLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        Environment.SetEnvironmentVariable("LOCALAPPDATA", directory);
        const string fingerprint = "fixture-generation";
        const string rootCab = "CAB-11111111111111111111111111111111";
        const string textureCab = "CAB-22222222222222222222222222222222";
        const string prefab = "assets/beyond/dynamicassets/gameplay/effects/prefabs/effect.prefab";
        const string texture = "assets/game/star.png";
        string index = Path.Combine(directory, "index.eidx");
        void WriteIndex(long textureId)
        {
            var spans = new List<EndfieldBundleIndexSpan>();
            using (var output = File.Create(index))
            using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
            {
                EndfieldIndexStore.WriteHeader(writer, fingerprint);
                void Bundle(string source, string cab, string dependency, params (string Name, string Path, string Type, long Id)[] assets)
                {
                    long start = output.Position;
                    foreach (var asset in assets)
                    { writer.Write((byte)1);writer.Write(asset.Name);writer.Write(asset.Path);writer.Write(asset.Type);writer.Write(asset.Id);writer.Write(source); }
                    writer.Write((byte)2);writer.Write(cab);writer.Write(source);writer.Write(dependency == null ? 0 : 1);
                    if (dependency != null) writer.Write(dependency);
                    spans.Add(new(source, new string('1', 32), 100, start, output.Position - start, assets.Length, 1, Array.Empty<byte>()));
                }
                Bundle("Bundles/effect.ab", rootCab, textureCab,
                    ("Root", prefab, "GameObject", 1), ("Root", prefab, "GameObject", 2));
                Bundle("Bundles/star.ab", textureCab, null, ("Star", texture, "Texture2D", textureId));
                EndfieldIndexStore.WriteEnd(writer, 3, 2);
            }
            using var input = File.OpenRead(index);
            var cache = new EndfieldBundleIndexCache { Fingerprint = fingerprint, IndexLength = input.Length, Assets = 3, Cabs = 2 };
            foreach (var span in spans)
            {
                input.Position = span.Offset;var bytes = new byte[span.Size];input.ReadExactly(bytes);
                cache.Spans.Add(span.Path, span with { Sha256 = SHA256.HashData(bytes) });
            }
            cache.Save(EndfieldBundleIndexCache.PathFor(index));
        }
        try
        {
            WriteIndex(7);
            var cache = EndfieldBundleIndexCache.TryLoad(index);
            var subset = EndfieldIndexStore.Load(index, verifiedSpans: new[] { cache.Spans["Bundles/star.ab"] });
            check(subset.Assets.Records.Count == 1 && subset.Dependencies.CabCount == 1 && subset.Assets.Records[0].PathId == 7,
                "verified Bundle ranges use the existing parser and skip unrelated record spans");
            var baseline = new JObject { ["sourceIndex"] = index, ["vfsFingerprint"] = fingerprint };
            var binding = new JObject { ["owner"] = rootCab + ":99", ["field"] = "$.m_Texture",
                ["path"] = texture, ["asset"] = "Star", ["type"] = "Texture",
                ["sourcePrefab"] = prefab, ["sourceRoot"] = "Root", ["sourceRootIdentity"] = rootCab + ":1",
                ["sourceVfsFingerprint"] = fingerprint, ["previewSource"] = Path.Combine(directory, "deleted-donor") };
            var edits = new JObject { ["gameReferences"] = new JArray(binding) };
            int rootReads = 0;
            (string Name, string Identity) ReadRoot(string path, string source)
            { rootReads++;check(path == prefab && source == "Bundles/effect.ab", "logical reference resolves the containing Prefab Bundle");return ("Root", rootCab + ":1"); }
            var first = EiemEffectGameReferenceInputs.Describe(edits, baseline, rootReader: ReadRoot);
            check((string)first["bindings"][0]["sourcePrefab"] == prefab && rootReads == 1,
                "portable source proof resolves without the donor directory");
            check(ReferenceEquals(first, EiemEffectGameReferenceInputs.Describe(edits, baseline, first, ReadRoot)) && rootReads == 1,
                "unchanged valid generation reuses the descriptor without another root parse");
            var originalTime = File.GetLastWriteTimeUtc(index);var originalBytes = File.ReadAllBytes(index);
            var modified = (byte[])originalBytes.Clone();modified[modified.Length - 25] ^= 1;
            File.WriteAllBytes(index, modified);File.SetLastWriteTimeUtc(index, originalTime);
            reject(() => EiemEffectGameReferenceInputs.Describe(edits, baseline, first, ReadRoot),
                "referenced span corruption preserving index length and timestamp invalidates a cache hit");
            File.WriteAllBytes(index, originalBytes);
            WriteIndex(8);
            var revised = EiemEffectGameReferenceInputs.Describe(edits, baseline, first, ReadRoot);
            check((string)revised["index"]["generation"] != (string)first["index"]["generation"],
                "same-VFS corrected index changes the build generation");
            binding["sourceRootIdentity"] = rootCab + ":2";
            reject(() => EiemEffectGameReferenceInputs.Describe(edits, baseline, rootReader: ReadRoot),
                "same-named child GameObject cannot substitute for the exact container root");
            binding["sourceRootIdentity"] = rootCab + ":1";
            binding.Remove("sourcePrefab");binding.Remove("sourceRoot");binding.Remove("sourceRootIdentity");binding.Remove("sourceVfsFingerprint");
            var discovered = EiemEffectGameReferenceInputs.Describe(edits, baseline, rootReader: ReadRoot);
            check((string)discovered["bindings"][0]["sourceRootIdentity"] == rootCab + ":1",
                "bare logical resource path discovers and parses a source Prefab through reverse dependencies");
            binding["type"] = "Material";
            reject(() => EiemEffectGameReferenceInputs.Describe(edits, baseline, rootReader: ReadRoot), "wrong logical PPtr type is rejected");
            binding["type"] = "Texture";
            string second = Path.Combine(directory, "other.eidx");
            File.Copy(index, second);File.Copy(EndfieldBundleIndexCache.PathFor(index), EndfieldBundleIndexCache.PathFor(second));
            baseline["sourceIndex"] = second;
            var selected = EiemEffectGameReferenceInputs.Describe(edits, baseline, discovered, ReadRoot);
            check((string)selected["index"]["path"] == second, "switching the selected index changes cache identity");
            string shared = Path.Combine(directory, "selected-workspace");Directory.CreateDirectory(Path.Combine(shared, "index"));
            string sharedIndex = EndfieldVfsAssetIndexBuilder.GetIndexPath(shared);
            File.Copy(index, sharedIndex);File.Copy(EndfieldBundleIndexCache.PathFor(index), EndfieldBundleIndexCache.PathFor(sharedIndex));
            EndfieldSharedResources.SetIndexWorkspace(shared);
            var settings = JObject.Parse(File.ReadAllText(EndfieldSharedResources.SettingsPath));
            settings["animeStudio"] = Path.Combine(directory, "AnimeStudio.GUI.exe");
            File.WriteAllText(EndfieldSharedResources.SettingsPath, settings.ToString());
            EndfieldSharedResources.SetResourceRoot(Path.Combine(directory, "resources"));
            check((string)JObject.Parse(File.ReadAllText(EndfieldSharedResources.SettingsPath))["animeStudio"] == (string)settings["animeStudio"],
                "updating resource locations preserves the registered deployed compiler");
            var sharedSelection = EiemEffectGameReferenceInputs.Describe(edits, baseline, selected, ReadRoot);
            check((string)sharedSelection["index"]["path"] == sharedIndex && File.Exists(second),
                "explicit shared index selection supersedes a still-existing author baseline index");
            EndfieldSharedResources.SetIndexWorkspace(Path.Combine(directory, "missing-selected-workspace"));
            reject(() => EiemEffectGameReferenceInputs.Describe(edits, baseline, sharedSelection, ReadRoot),
                "missing selected index does not silently reuse the older author baseline");
            EndfieldSharedResources.SetIndexWorkspace(shared);
            baseline["vfsFingerprint"] = "different-game";
            reject(() => EiemEffectGameReferenceInputs.Describe(edits, baseline, rootReader: ReadRoot), "wrong VFS index cannot be hidden by a build cache");
        }
        finally { Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocal);Directory.Delete(directory, true); }
    }
}
