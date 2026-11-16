using AnimeStudio;
using AnimeStudio.GUI;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text;
using W = AnimeStudio.GUI.EiemAnimationClipWriter;

internal static class NativeAnimationClipChecks
{
    internal static void Run(string source, string output)
    {
        source = Path.GetFullPath(source);
        output = Path.GetFullPath(output);
        var manifest = JObject.Parse(File.ReadAllText(Path.Combine(source, "source/manifest.json")));
        if ((string)manifest["format"] != "EFFSOURCE" || (int?)manifest["version"] != 1)
            throw new InvalidDataException("Expected an EFF source workspace");
        var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.ArknightsEndfield) };
        foreach (var snapshot in manifest["serializedFiles"])
        {
            string relative = (string)snapshot["snapshot"]["file"];
            string path = Path.GetFullPath(Path.Combine(source, relative));
            if (!Path.GetRelativePath(source, path).Replace('\\', '/').StartsWith("source/", StringComparison.Ordinal))
                throw new InvalidDataException("Unsafe schema snapshot path");
            byte[] original = File.ReadAllBytes(path);
            if (original.LongLength != (long)snapshot["snapshot"]["size"] ||
                Convert.ToHexString(SHA256.HashData(original)) != (string)snapshot["snapshot"]["sha256"])
                throw new InvalidDataException("Schema donor changed");
            using var reader = new FileReader((string)snapshot["cab"], new MemoryStream(original, false));
            var donor = new SerializedFile(reader, manager);
            int clipType = donor.m_Types.FindIndex(t => t.classID == 74 && t.m_Type?.m_Nodes?.Count > 0);
            int bundleType = donor.m_Types.FindIndex(t => t.classID == 142 && t.m_Type?.m_Nodes?.Count > 0);
            if (clipType < 0 || bundleType < 0 || donor.m_RefTypes.Count != 0) continue;
            var schema = donor.m_Types[clipType].m_Type;
            var tracks = new W.Track[]
            {
                new("Pivot", W.Kind.Position, "", 4, new float[][] { [0,0,0], [1,2,3], [0,0,0] }),
                new("Pivot", W.Kind.Rotation, "", 4, new float[][] { [0,0,0,1], [0,0.70710677f,0,0.70710677f], [0,0,0,1] }),
                new("Surface", W.Kind.Shape, "Pulse", 137, new float[][] { [0], [75], [0] }),
                new("Surface", W.Kind.MaterialFloat, "_Stencil", 137, new float[][] { [0.1f], [0.8f], [0.1f] }),
                new("Surface", W.Kind.MaterialColor, "_Color", 137, new float[][] { [1,0,0,1], [0,1,0,0.5f], [1,0,0,1] }),
                new("Surface", W.Kind.MaterialVector, "_MainTex_ST", 137, new float[][] { [1,1,0,0], [2,2,0.5f,0.25f], [1,1,0,0] })
            };
            var clip = new W.Clip("EFF_MixedAnimationProbe", 2, true, tracks);
            var document = W.Build(schema, clip);
            byte[] payload = document.Encode();
            Check(new TypeTreeDocument(schema, payload).Encode().SequenceEqual(payload), "native TypeTree roundtrip");
            Check(W.Build(schema, clip with { Tracks = tracks.Reverse().ToArray() }).Encode().SequenceEqual(payload), "deterministic track ordering");
            Reject(() => W.Build(schema, clip with { Tracks = tracks.Append(tracks[0]).ToArray() }), "duplicate property");
            Reject(() => W.Build(schema, clip with { Tracks = [tracks[0] with { Path = "../Pivot" }] }), "path traversal");
            Reject(() => W.Build(schema, clip with { Tracks = [tracks[1] with { Samples = new float[][] { [0,0,0,0], [0,0,0,1] } }] }), "invalid quaternion");
            Reject(() => W.Build(schema, clip with { Tracks = [tracks[0], tracks[2] with { Samples = new float[][] { [0], [1] } }] }), "mixed timeline mismatch");
            Reject(() => W.Build(schema, clip with { Tracks = [tracks[2] with { Samples = new float[][] { [0], [float.NaN] } }] }), "nonfinite sample");
            Reject(() => W.Build(schema, clip with { Tracks = [tracks[3] with { RendererClass = 4 }] }), "invalid renderer binding");
            Reject(() => W.Build(schema, clip with { SampleRate = float.NaN }), "nonfinite frame rate");
            Reject(() => W.Build(schema, clip with { SampleRate = float.PositiveInfinity }), "infinite frame rate");
            var fractional = W.Build(schema, clip with { SampleRate = 116f / 1.92f });
            Check(Math.Abs(Convert.ToDouble(fractional.Root["m_MuscleClip"]["m_StopTime"].Scalar) - 2d / (116f / 1.92f)) < 1e-6,
                "fractional sample rate preserves timeline duration");
            var modified = tracks.ToArray();
            modified[2] = modified[2] with { Samples = new float[][] { [0], [25], [0] } };
            Check(!W.Build(schema, clip with { Tracks = modified }).Encode().SequenceEqual(payload), "editing samples changes native Clip");

            byte[] metadata = EiemEffectPrefabWriter.CreateMetadataOnly(donor, original);
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, "animation-schema.bin"), metadata);
            var productionRequest = new JObject { ["format"] = "EFF_NATIVE_ANIMATION", ["version"] = 1,
                ["asset"] = "assets/eff/animations/mount.anim", ["name"] = "Mount", ["sampleRate"] = 2,
                ["loop"] = true, ["tracks"] = new JArray(new JObject { ["path"] = "Orbit/Tip", ["kind"] = "Position",
                    ["samples"] = new JArray(new JArray(0,0,0),new JArray(1,2,3),new JArray(0,0,0)) }) };
            var production = EiemAnimationBuildCommand.Build(productionRequest);
            Check(production.SequenceEqual(EiemAnimationBuildCommand.Build(productionRequest)), "production Clip compiler deterministic without a donor resource");
            using (var archiveReader = new FileReader("mount.ab",new MemoryStream(production,false)))
            {
                var archive = new VFSFile(archiveReader,"mount.ab",GameType.ArknightsEndfield);
                Check(archive.fileList.Count == 1, "production Clip contains one serialized member");
                var member=archive.fileList[0];using var stream=new MemoryStream();member.stream.CopyTo(stream);member.stream.Dispose();
                using var clipReader=new FileReader(member.fileName,new MemoryStream(stream.ToArray(),false));
                var checkedFile=new SerializedFile(clipReader,manager);
                Check(checkedFile.m_Objects.Count==2&&checkedFile.m_Externals.Count==0,"production Clip excludes source resources");
                var checkedInfo=checkedFile.m_Objects.Single(o=>o.classID==74);
                var checkedClip=new AnimationClip(new ObjectReader(clipReader,checkedFile,checkedInfo,manager.Game));
                Check(checkedClip.m_MuscleClip.m_Clip.m_DenseClip.m_FrameCount==3&&checkedClip.m_ClipBindingConstant.genericBindings.Count==1,"production Clip independently decodes");
            }
            File.WriteAllBytes(Path.Combine(output,"mount.ab"),production);
            string cab = "CAB-" + Convert.ToHexString(SHA256.HashData(payload))[..32].ToLowerInvariant();
            using var emptyReader = new FileReader(cab, new MemoryStream(metadata, false));
            var empty = new SerializedFile(emptyReader, manager);
            var bundle = EiemEffectPrefabWriter.CreateBundle(donor.m_Types[bundleType].m_Type);
            var bundleData = (JObject)bundle.Root.ToJson();
            JObject Pointer(long id) => new() { ["m_FileID"] = 0, ["m_PathID"] = id };
            const string asset = "assets/eff/diagnostics/mixed-animation.anim";
            bundleData["m_Name"] = "EFF_MixedAnimationProbe";
            bundleData["m_PreloadTable"] = new JArray(Pointer(1));
            bundleData["m_Container"] = new JArray(new JObject
            {
                ["first"] = asset, ["second"] = new JObject
                { ["preloadIndex"] = 0, ["preloadSize"] = 1, ["asset"] = Pointer(1) }
            });
            bundle.ApplyJson(bundleData);
            var incompatibleBundle = TypeTreeDocument.CreateDefault(donor.m_Types[bundleType].m_Type);
            Reject(() => EiemEffectPrefabWriter.Build(empty, metadata,
                [new(1, clipType, payload), new(2, bundleType, incompatibleBundle.Encode())]),
                "zero runtime compatibility must be rejected before engine submission");
            byte[] serialized = EiemEffectPrefabWriter.Build(empty, metadata,
                [new(1, clipType, payload), new(2, bundleType, bundle.Encode())]);
            using var parsedReader = new FileReader(cab, new MemoryStream(serialized, false));
            var parsed = new SerializedFile(parsedReader, manager);
            Check(parsed.m_Externals.Count == 0 && parsed.m_Objects.Count == 2, "independent Clip resource has no donor objects or dependencies");
            var bundleInfo = parsed.m_Objects.Single(o => o.classID == 142);
            var bundleCheck = new TypeTreeDocument(bundleInfo.serializedType.m_Type,
                serialized.AsSpan((int)bundleInfo.byteStart, (int)bundleInfo.byteSize).ToArray());
            Check(Convert.ToUInt32(bundleCheck.Root["m_RuntimeCompatibility"].Scalar) == 1,
                "authored bundle passes the engine runtime compatibility gate");
            var info = parsed.m_Objects.Single(o => o.classID == 74);
            var objectReader = new ObjectReader(parsedReader, parsed, info, manager.Game);
            var native = new AnimationClip(objectReader);
            Check(objectReader.Position == info.byteStart + info.byteSize, "independent AnimationClip reader consumes whole object");
            Check(native.m_ClipBindingConstant.genericBindings.Count == 12, "all Transform, Shape and Material bindings in one Clip");
            var dense = native.m_MuscleClip.m_Clip.m_DenseClip;
            Check(dense.m_FrameCount == 3 && dense.m_CurveCount == 17 && dense.m_SampleArray.Length == 51, "one native sample timeline");
            Check(native.m_ClipBindingConstant.genericBindings.Any(b => b.customType == 20) &&
                native.m_ClipBindingConstant.genericBindings.Any(b => b.customType == 22), "independent reader identifies Shape and RendererMaterial tracks");
            Check(Math.Abs(dense.m_SampleArray[17 + 7] - 75) < 1e-6, "Shape sample survives native serialization");
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, "mixed-animation.ab"), EiemEffectNativeArchive.Write([(cab, serialized)]));
            File.WriteAllText(Path.Combine(output, "clip.json"), document.Root.ToJson().ToString());
            File.WriteAllText(Path.Combine(output, "receipt.json"), new JObject
            {
                ["status"] = "offline-validated-awaiting-engine-evaluation", ["asset"] = asset,
                ["nativeClipSha256"] = Convert.ToHexString(SHA256.HashData(payload)),
                ["sourceSchema"] = source, ["frames"] = 3, ["duration"] = 1,
                ["tracks"] = JArray.FromObject(tracks), ["engineEvaluated"] = false
            }.ToString());
            Console.WriteLine("PASS mixed native Clip: independent reader, edits, roundtrip, isolation; engine evaluation pending: " + output);
            return;
        }
        throw new InvalidDataException("No schema donor contains AnimationClip and AssetBundle TypeTrees");
    }

    private static void Check(bool ok, string label) { if (!ok) throw new Exception(label); }
    private static void Reject(Action action, string label)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Expected rejection: " + label);
    }
}
