using AnimeStudio;
using AnimeStudio.GUI;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using W = AnimeStudio.GUI.EiemAnimationClipWriter;

// Offline conversion of an existing Mod's shape tracks. This does not modify
// the source Mod and does not claim that a real Renderer has been sampled.
internal static class NativeAnimationModChecks
{
    internal static void Run(string source, string requestFile, string output)
    {
        var request = JObject.Parse(File.ReadAllText(requestFile));
        double duration = (double)request["duration"];
        if (!double.IsFinite(duration) || duration <= 0) throw new InvalidDataException("Invalid source duration");
        var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.ArknightsEndfield) };
        var manifest = JObject.Parse(File.ReadAllText(Path.Combine(source, "source/manifest.json")));
        Directory.CreateDirectory(output);
        foreach (var snapshot in manifest["serializedFiles"])
        {
            string relative = (string)snapshot["snapshot"]["file"];
            string path = Path.GetFullPath(Path.Combine(source, relative));
            if (!Path.GetRelativePath(source, path).Replace('\\', '/').StartsWith("source/", StringComparison.Ordinal))
                throw new InvalidDataException("Unsafe schema donor path");
            byte[] original = File.ReadAllBytes(path);
            if (original.LongLength != (long)snapshot["snapshot"]["size"] ||
                Convert.ToHexString(SHA256.HashData(original)) != (string)snapshot["snapshot"]["sha256"])
                throw new InvalidDataException("Schema donor changed");
            using var reader = new FileReader((string)snapshot["cab"], new MemoryStream(original, false));
            var donor = new SerializedFile(reader, manager);
            int ct = donor.m_Types.FindIndex(t => t.classID == 74 && t.m_Type?.m_Nodes?.Count > 0);
            int bt = donor.m_Types.FindIndex(t => t.classID == 142 && t.m_Type?.m_Nodes?.Count > 0);
            if (ct < 0 || bt < 0 || donor.m_RefTypes.Count != 0) continue;
            byte[] metadata = EiemEffectPrefabWriter.CreateMetadataOnly(donor, original);
            var receipts = new JArray();
            int index = 0;
            foreach (JObject target in request["targets"])
            {
                var tracks = ((JArray)target["tracks"]).Select(t => new W.Track("", W.Kind.Shape,
                    (string)t["shape"], 137, ((JArray)t["samples"]).Select(s => new[] { (float)s * 100f }).ToArray())).ToArray();
                int frames = tracks.First().Samples.Count;
                float rate = (float)((frames - 1) / duration);
                var clip = new W.Clip("EFF_ModShapeExperiment_" + index, rate, (bool)request["loop"], tracks);
                var document = W.Build(donor.m_Types[ct].m_Type, clip);
                byte[] payload = document.Encode();
                // Fractional rates preserve the legacy endpoint without resampling.
                if (Math.Abs((frames - 1d) / rate - duration) > 0.000001)
                    throw new InvalidDataException("Native timeline would change source duration");
                string asset = $"assets/eff/diagnostics/mod-shape-{index}.anim";
                string file = $"clip-{index}.ab";
                JObject Pointer(long id) => new() { ["m_FileID"] = 0, ["m_PathID"] = id };
                var bundle = EiemEffectPrefabWriter.CreateBundle(donor.m_Types[bt].m_Type);
                var bd = (JObject)bundle.Root.ToJson();
                bd["m_Name"] = clip.Name;
                bd["m_PreloadTable"] = new JArray(Pointer(1));
                bd["m_Container"] = new JArray(new JObject { ["first"] = asset, ["second"] = new JObject
                    { ["preloadIndex"] = 0, ["preloadSize"] = 1, ["asset"] = Pointer(1) } });
                bundle.ApplyJson(bd);
                string cab = "CAB-" + Convert.ToHexString(SHA256.HashData(payload))[..32].ToLowerInvariant();
                using var er = new FileReader(cab, new MemoryStream(metadata, false));
                var empty = new SerializedFile(er, manager);
                byte[] serialized = EiemEffectPrefabWriter.Build(empty, metadata,
                    [new(1, ct, payload), new(2, bt, bundle.Encode())]);
                using var pr = new FileReader(cab, new MemoryStream(serialized, false));
                var parsed = new SerializedFile(pr, manager);
                var info = parsed.m_Objects.Single(o => o.classID == 74);
                var or = new ObjectReader(pr, parsed, info, manager.Game);
                var native = new AnimationClip(or);
                var dense = native.m_MuscleClip.m_Clip.m_DenseClip;
                var ordered = tracks.OrderBy(t => t.Property, StringComparer.Ordinal).ToArray();
                float[] expected = Enumerable.Range(0, frames).SelectMany(f => ordered.Select(t => t.Samples[f][0])).ToArray();
                if (or.Position != info.byteStart + info.byteSize || parsed.m_Externals.Count != 0 ||
                    parsed.m_Objects.Count != 2 || dense.m_FrameCount != frames ||
                    !dense.m_SampleArray.SequenceEqual(expected) ||
                    native.m_ClipBindingConstant.genericBindings.Count != tracks.Length ||
                    native.m_ClipBindingConstant.genericBindings.Any(b => b.customType != 20 || b.path != 0 || (int)b.typeID != 137))
                    throw new InvalidDataException("Independent native reader differs from source tracks");
                byte[] archive = EiemEffectNativeArchive.Write([(cab, serialized)]);
                File.WriteAllBytes(Path.Combine(output, file), archive);
                receipts.Add(new JObject { ["part"] = target["part"], ["file"] = file, ["asset"] = asset,
                    ["sha256"] = Convert.ToHexString(SHA256.HashData(archive)), ["frames"] = frames,
                    ["sampleRate"] = rate, ["duration"] = duration, ["tracks"] = target["tracks"],
                    ["engineEvaluated"] = false });
                index++;
            }
            if (index == 0) throw new InvalidDataException("No targets in source animation");
            File.WriteAllText(Path.Combine(output, "native-clips.json"), new JObject {
                ["status"] = "offline-validated-awaiting-real-mod", ["targets"] = receipts }.ToString());
            Console.WriteLine($"PASS real Mod conversion: {index} native Clips, all samples and endpoints preserved; game evaluation pending");
            return;
        }
        throw new InvalidDataException("No native Clip schema donor");
    }
}
