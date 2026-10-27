using AnimeStudio;
using AnimeStudio.GUI;
using Newtonsoft.Json.Linq;

internal static class NarakaEffectPackageTests
{
    private const string Outfit = "assets/res/prefab/actor_visual_part/ch_f_ming_yulinglong/ch_f_ming_yulinglong_lv_s16_ui.prefab";
    private const string Effect = "assets/res/effect/fashion/appearance/yulinglong/fx_ch_f_ming_yulinglong_lv_s16.prefab";

    internal static int Run(string catalogPath, string output)
    {
        Logger.Silent = true;
        var catalog = NarakaAppResIndex.Load(catalogPath);
        NarakaPrefabPackageCommand.PrepareMap(catalogPath, catalog);
        var records = NarakaPrefabPackageCommand.BuildPrefabRecordMap(catalog.Index.Records);
        var outfit = catalog.Index.Records.Single(record => record.Container == Outfit);
        NarakaPrefabPackageCommand.Export(outfit, output, records);

        var package = JObject.Parse(File.ReadAllText(Path.Combine(output, "naraka.json")));
        var effects = (JArray)package["effects"]!;
        var effect = effects.OfType<JObject>().Single(row => (string)row["prefab"]! == Effect);
        var nodes = (JArray)package["nodes"]!;
        var mount = (JObject)((JArray)effect["mounts"]!)[0]!;
        if ((string)nodes[(int)mount["node"]!]["name"]! != "effect")
            throw new InvalidDataException("Effect Prefab mount node was not retained");

        var systems = ((JArray)package["particleSystems"]!).OfType<JObject>()
            .Where(row => (string)row["prefab"]! == Effect).ToDictionary(row => (string)row["name"]!, StringComparer.Ordinal);
        if (!systems.Keys.OrderBy(name => name).SequenceEqual(new[] { "lizi1", "lizi2", "smoke" }))
            throw new InvalidDataException("Expected the three source ParticleSystems");
        foreach (var system in systems.Values)
        {
            if ((string)system["renderModeName"]! != "Billboard" || ((JArray)system["meshes"]!).Count != 0)
                throw new InvalidDataException("Billboard source geometry was incorrectly reported as a Mesh");
            foreach (var material in (JArray)system["materials"]!)
                if (package["materials"]![(string)material!] == null)
                    throw new InvalidDataException("Particle material is missing from the package");
            foreach (var texture in (JArray)system["textures"]!)
                if (package["textures"]![(string)texture!] == null)
                    throw new InvalidDataException("Particle texture is missing from the package");
        }

        var componentFile = (string)effect["components"]!;
        var componentPath = Path.Combine(output, componentFile.Replace('/', Path.DirectorySeparatorChar));
        var components = JObject.Parse(File.ReadAllText(componentPath));
        var componentTypes = ((JArray)components["components"]!).OfType<JObject>()
            .Select(row => (string)row["type"]!).ToHashSet(StringComparer.Ordinal);
        if (!componentTypes.Contains("ParticleSystem") || !componentTypes.Contains("ParticleSystemRenderer"))
            throw new InvalidDataException("Particle source component fields were not exported");
        var checksums = JObject.Parse(File.ReadAllText(Path.Combine(output, "checksums.json")));
        if (checksums[componentFile] == null)
            throw new InvalidDataException("Particle component source file is not checksum protected");

        Console.WriteLine($"NARAKA EFFECT PACKAGE PASS: {systems.Count} ParticleSystems, " +
            $"{systems.Values.Sum(row => ((JArray)row["materials"]!).Count)} material references, " +
            $"{systems.Values.Sum(row => ((JArray)row["textures"]!).Count)} texture references");
        return 0;
    }
}
