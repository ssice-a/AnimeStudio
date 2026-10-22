using AnimeStudio;
using AnimeStudio.GUI;
using Newtonsoft.Json.Linq;

internal static class NarakaBoneSourceTests
{
    internal static int Run(string catalogPath, string output)
    {
        MatchingPolicy();
        Logger.Silent = true;
        var catalog = NarakaAppResIndex.Load(catalogPath);
        NarakaPrefabPackageCommand.PrepareMap(catalogPath, catalog);
        var path = "assets/res/prefab/actor_visual_part/ch_f_ming_wuzhen/ch_f_ming_wuzhen_lv_s3_billboard.prefab";
        var record = catalog.Index.Records.Single(r => r.Container == path);
        NarakaPrefabPackageCommand.Export(record, output);
        var document = JObject.Parse(File.ReadAllText(Path.Combine(output, "naraka.json")));
        var nodes = (JArray)document["nodes"]!;
        var renderers = (JArray)document["renderers"]!;
        if (renderers.Count != 67) throw new InvalidDataException("Original renderer graph changed");
        foreach (var renderer in renderers.OfType<JObject>())
        {
            if (!((JArray)renderer["bones"]!).Any())
                throw new InvalidDataException("FBX source bones were not resolved for " + renderer["name"]);
            if ((string)renderer["boneSource"]?["method"] != "fbx-root-relative-path-crc32")
                throw new InvalidDataException("Bone source proof missing");
            foreach (var bone in (JArray)renderer["bones"]!)
                if ((string)nodes[(int)bone]["role"]! != "source-skeleton")
                    throw new InvalidDataException("Bone references a non-skeleton source node");
        }
        var names = renderers.SelectMany(r => (JArray)r["bones"]!).Select(b => (string)nodes[(int)b]["name"]!).ToHashSet();
        if (names.Count != 118 || !names.Contains("gMan L Forearm") || !names.Contains("L_sleeveA_02"))
            throw new InvalidDataException("Original bone names do not match the verified source");
        if (names.Any(n => n.StartsWith("bone_"))) throw new InvalidDataException("Hash placeholders remain");
        Console.WriteLine($"BONE SOURCE PASS: {renderers.Count} renderers, {names.Count} original bone names");
        return 0;
    }

    private static void MatchingPolicy()
    {
        var a = new NarakaModelBones.Bone(null, "MotionRoot/A");
        var b = new NarakaModelBones.Bone(null, "MotionRoot/B");
        var hierarchy = new Dictionary<uint, List<NarakaModelBones.Bone>> { [11] = new() { a }, [22] = new() { b } };
        if (!NarakaModelBones.TryMatch(new uint[] { 22,11,22 }, hierarchy, out var result) ||
            !result.SequenceEqual(new[] { b,a,b })) throw new InvalidDataException("Bone palette order/duplicates changed");
        if (NarakaModelBones.TryMatch(new uint[] { 33 }, hierarchy, out _))
            throw new InvalidDataException("Missing bone was guessed");
        hierarchy[11].Add(new NarakaModelBones.Bone(null, "Another/Collision"));
        if (NarakaModelBones.TryMatch(new uint[] { 11 }, hierarchy, out _))
            throw new InvalidDataException("Ambiguous bone hash was accepted");
    }
}
