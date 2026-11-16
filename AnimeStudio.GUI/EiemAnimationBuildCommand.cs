using AnimeStudio;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace AnimeStudio.GUI;

// A schema-only template contains no source assets or external references.
// Author clips contain only AnimationClip and AssetBundle objects.
internal static class EiemAnimationBuildCommand
{
    internal static int Run(string[] args)
    {
        if (args.Length != 3) return 2;
        try { Build(JObject.Parse(File.ReadAllText(args[1])), args[2]); return 0; }
        catch (Exception e) { File.WriteAllText(args[2] + ".error.txt", e.ToString()); return 1; }
    }

    internal static byte[] Build(JObject request, string output = null)
    {
        if ((string)request["format"] != "EFF_NATIVE_ANIMATION" || (int?)request["version"] != 1)
            throw new InvalidDataException("Unsupported native animation request");
        string asset = (string)request["asset"];
        if (asset == null || !asset.StartsWith("assets/eff/animations/", StringComparison.Ordinal) ||
            !asset.EndsWith(".anim", StringComparison.Ordinal) || asset.Contains('\\') ||
            asset.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException("Invalid native animation asset path");
        var tracks = ((JArray)request["tracks"]).Select(t => new EiemAnimationClipWriter.Track(
            (string)t["path"], Enum.Parse<EiemAnimationClipWriter.Kind>((string)t["kind"], false),
            (string)t["property"] ?? "", (int?)t["rendererClass"] ?? 4,
            ((JArray)t["samples"]).Select(s => s.ToObject<float[]>()).ToArray())).ToArray();
        var clip = new EiemAnimationClipWriter.Clip((string)request["name"],
            (float)request["sampleRate"], (bool)request["loop"], tracks);
        using var schemaStream = typeof(EiemAnimationBuildCommand).Assembly.GetManifestResourceStream(
            "AnimeStudio.GUI.Resources.animation-schema.bin") ?? throw new InvalidDataException("Animation schema missing");
        using var copy = new MemoryStream(); schemaStream.CopyTo(copy); byte[] metadata = copy.ToArray();
        var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.ArknightsEndfield) };
        using var reader = new FileReader("animation-schema", new MemoryStream(metadata, false));
        var file = new SerializedFile(reader, manager);
        if (file.m_Objects.Count != 0 || file.m_Externals.Count != 0 || file.m_RefTypes.Count != 0)
            throw new InvalidDataException("Animation schema must contain no assets or external references");
        int ci = file.m_Types.FindIndex(t => t.classID == 74);
        int bi = file.m_Types.FindIndex(t => t.classID == 142);
        var document = EiemAnimationClipWriter.Build(file.m_Types[ci].m_Type, clip);
        var bundle = EiemEffectPrefabWriter.CreateBundle(file.m_Types[bi].m_Type);
        JObject Ptr(long id) => new() { ["m_FileID"] = 0, ["m_PathID"] = id };
        var data = (JObject)bundle.Root.ToJson(); data["m_Name"] = clip.Name;
        data["m_PreloadTable"] = new JArray(Ptr(1));
        data["m_Container"] = new JArray(new JObject { ["first"] = asset,
            ["second"] = new JObject { ["preloadIndex"] = 0, ["preloadSize"] = 1, ["asset"] = Ptr(1) } });
        bundle.ApplyJson(data);
        byte[] serialized = EiemEffectPrefabWriter.Build(file, metadata,
            [new(1, ci, document.Encode()), new(2, bi, bundle.Encode())]);
        string cab = "CAB-" + Convert.ToHexString(SHA256.HashData(serialized))[..32].ToLowerInvariant();
        byte[] archive = EiemEffectNativeArchive.Write([(cab, serialized)]);
        if (output != null) File.WriteAllBytes(output, archive);
        return archive;
    }
}
