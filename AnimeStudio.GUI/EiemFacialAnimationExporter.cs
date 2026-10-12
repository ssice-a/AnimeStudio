using AnimeStudio;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NVector = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace AnimeStudio.GUI;

/// <summary>Offline SkeletalMorph evaluation. The output contains only bone TRS, never game scripts.</summary>
internal static class EiemFacialAnimationExporter
{
    private static readonly string[] PoseFields = { "_browValueL", "_browValueR", "_eyeValueL", "_eyeValueR", "_mouthValue", "_otherValue" };
    private static readonly string[] CurveFields = { "_browCurveL", "_browCurveR", "_eyeCurveL", "_eyeCurveR", "_mouthCurve", "_otherCurve" };

    internal static int Export(EndfieldVfsArchive archive, string workspace, string avatarPath,
        string output, IEnumerable<string> animationPaths, string unityVersion = null, Action<string> progress = null)
    {
        if (!EndfieldVfsAssetIndexBuilder.TryGetCurrentIndex(archive, workspace, out var indexPath))
            throw new InvalidDataException("Open/index the Endfield VFS before exporting animations.");
        var target = avatarPath.Replace('\\', '/').Trim('/');
        var selected = animationPaths.Select(p => p.Replace('\\', '/').Trim('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0) throw new InvalidDataException("Select animation .asset files to export.");
        var index = EndfieldIndexStore.Load(indexPath, recordFilter: r =>
            r.Container.Equals(target, StringComparison.OrdinalIgnoreCase) || selected.Contains(r.Container));
        if (index.VfsFingerprint != archive.Fingerprint) throw new InvalidDataException("Stale VFS animation index.");
        var records = index.Assets.Records;
        var avatarRecords = records.Where(r => r.Container.Equals(target, StringComparison.OrdinalIgnoreCase)).ToArray();
        var sources = avatarRecords.Select(r => r.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sources.Length != 1) throw new InvalidDataException("Select exactly one facial avatar .asset file.");
        var manager = Load(archive, workspace, sources, unityVersion);
        try
        {
            var avatarObjects = manager.assetsFileList.SelectMany(f => f.Objects)
                .Where(a => avatarRecords.Any(r => r.PathId == a.m_PathID) &&
                    a.serializedType?.m_Type?.m_Nodes?.Any(n => n.m_Name == "morphMappingNames") == true).ToArray();
            if (avatarObjects.Length != 1) throw new InvalidDataException("The selected .asset is not a unique SkeletalMorph avatar.");
            var avatarObject = avatarObjects[0];
            var avatar = EiemAnimationSourceReader.Read(avatarObject);
            var pointer = avatar["avatar"];
            var fileId = (int)pointer["m_FileID"]; var pathId = (long)pointer["m_PathID"];
            var cab = fileId == 0 ? avatarObject.assetsFile.fileName : avatarObject.assetsFile.m_Externals.ElementAtOrDefault(fileId - 1)?.fileName;
            var source = index.Dependencies.GetBundleSource(cab);
            if (pathId == 0 || string.IsNullOrEmpty(source)) throw new InvalidDataException("Facial avatar has no resolvable original skeleton reference.");
            var skeletonManager = Load(archive, workspace, new[] { source }, unityVersion ?? avatarObject.assetsFile.unityVersion);
            try
            {
                var nativeAvatar = skeletonManager.assetsFileList.Where(f => Path.GetFileName(f.fileName).Equals(Path.GetFileName(cab), StringComparison.OrdinalIgnoreCase))
                    .SelectMany(f => f.Objects).OfType<Avatar>().SingleOrDefault(a => a.m_PathID == pathId);
                if (nativeAvatar == null) throw new InvalidDataException("Original Unity Avatar is unavailable.");
                var skeleton = SkeletonRows(nativeAvatar);
                var resources = new List<JObject>();
                var exportedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var batch in records.Where(r => selected.Contains(r.Container)).GroupBy(r => r.Source, StringComparer.OrdinalIgnoreCase))
                {
                    var animationManager = Load(archive, workspace, new[] { batch.Key }, unityVersion ?? avatarObject.assetsFile.unityVersion);
                    try
                    {
                        foreach (var record in batch)
                        {
                            var asset = animationManager.assetsFileList.SelectMany(f => f.Objects).SingleOrDefault(a => a.m_PathID == record.PathId);
                            var fields = asset?.serializedType?.m_Type?.m_Nodes;
                            if (fields == null || !fields.Any(n => n.m_Name == "_mouthValue" || n.m_Name == "_mouthCurve"))
                                throw new InvalidDataException("Selected resource is not a facial pose/animation: " + record.Container);
                            var resource = EiemAnimationSourceReader.Read(asset);
                            resource["eiem_source_identity"] = asset.assetsFile.fileName + ":" + asset.m_PathID;
                            resources.Add(resource); exportedPaths.Add(record.Container);
                        }
                    }
                    finally { animationManager.Clear(); }
                }
                if (!selected.SetEquals(exportedPaths)) throw new InvalidDataException("Some selected animations are absent from the current index.");
                progress?.Invoke($"Decoded selected facial resources: {resources.Count}");
                var data = Bake(avatar, resources, skeleton, target, avatarObject.assetsFile.fileName + ":" + avatarObject.m_PathID);
                var path = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var staging = path + ".tmp";
                try
                {
                    File.WriteAllText(staging, data.ToString(Formatting.None), new System.Text.UTF8Encoding(false));
                    File.Move(staging, path, overwrite: true);
                }
                finally { if (File.Exists(staging)) File.Delete(staging); }
                return ((JArray)data["clips"]).Count;
            }
            finally { skeletonManager.Clear(); }
        }
        finally { manager.Clear(); }
    }

    private static AssetsManager Load(EndfieldVfsArchive archive, string workspace, IEnumerable<string> sources, string version)
    {
        var types = new[] { ClassIDType.Avatar, ClassIDType.MonoBehaviour, ClassIDType.MonoScript, ClassIDType.AssetBundle };
        var flags = types.Select(t => (t, parse: t.CanParse(), export: t.CanExport())).ToArray();
        try
        {
            foreach (var t in types) TypeFlags.SetType(t, true, t.CanExport());
            var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.ArknightsEndfield),
                SpecifyUnityVersion = version, ResolveDependencies = false };
            var files = sources.Distinct(StringComparer.OrdinalIgnoreCase).Select(s => archive.ExtractToCache(s, workspace)).ToArray();
            manager.LoadFiles(files, mergeSplitAssets: false);
            return manager;
        }
        finally { foreach (var flag in flags) TypeFlags.SetType(flag.t, flag.parse, flag.export); }
    }

    private static JArray SkeletonRows(Avatar avatar)
    {
        var skeleton = avatar.m_Avatar.m_AvatarSkeleton; var pose = avatar.m_Avatar.m_DefaultPose.m_X;
        if (skeleton.m_ID.Length != skeleton.m_Node.Count || pose.Length != skeleton.m_Node.Count)
            throw new InvalidDataException("Unity Avatar skeleton and reference pose counts disagree.");
        var rows = new JArray(); var paths = new List<string>();
        for (var i = 0; i < pose.Length; i++)
        {
            var path = avatar.FindBonePath(skeleton.m_ID[i]); var parent = skeleton.m_Node[i].m_ParentId;
            if (path == null || parent >= i || parent < -1) throw new InvalidDataException("Invalid original Avatar hierarchy.");
            paths.Add(path); var x = pose[i];
            rows.Add(new JObject { ["path"] = path, ["parent"] = parent < 0 ? null : paths[parent],
                ["position"] = new JArray(x.t.X, x.t.Y, x.t.Z),
                ["rotation"] = new JArray(x.q.X, x.q.Y, x.q.Z, x.q.W),
                ["scale"] = new JArray(x.s.X, x.s.Y, x.s.Z) });
        }
        return rows;
    }

    private static NQuaternion MayaQuaternion(NVector degrees)
    {
        var e = degrees * (float)(Math.PI / 180);
        return NQuaternion.CreateFromAxisAngle(NVector.UnitZ, -e.Z) * NQuaternion.CreateFromAxisAngle(NVector.UnitY, -e.Y) * NQuaternion.CreateFromAxisAngle(NVector.UnitX, e.X);
    }

    internal static JObject Bake(JObject avatar, IEnumerable<JObject> resources, JArray skeleton, string avatarPath, string identity)
    {
        var data = avatar["data"];
        var refs = ((JArray)avatar["references"]["RefIds"]).ToDictionary(r => (long)r["rid"], r => r["data"]);
        var controls = ((JArray)data["morphMappingNames"]).Zip((JArray)data["morphMappingCfgs"],
            (name, cfg) => (name: (string)name, data: refs[(long)cfg["rid"]])).ToDictionary(p => p.name, p => p.data);
        var bones = ((JArray)data["allBoneNames"]).Zip((JArray)data["basePoseConfig"],
            (name, cfg) => (name: (string)name, cfg)).ToDictionary(p => (int)p.cfg["boneID"], p => p);
        if (bones.Values.Select(b => b.name).Distinct().Count() != bones.Count) throw new InvalidDataException("Ambiguous facial bone names.");
        var paths = skeleton.OfType<JObject>().GroupBy(t => ((string)t["path"]).Split('/').Last()).ToDictionary(g => g.Key, g => g.ToArray());
        var bonePaths = bones.ToDictionary(p => p.Key, p => paths.TryGetValue(p.Value.name, out var match) && match.Length == 1
            ? (string)match[0]["path"] : throw new InvalidDataException("Ambiguous/missing facial Transform: " + p.Value.name));
        foreach (var control in controls.Values)
            foreach (var delta in (JArray)control["bones"])
                if (!bones.TryGetValue((int)delta["boneID"], out var bone) || (long)delta["boneNameHash"] != (long)bone.cfg["boneNameHash"])
                    throw new InvalidDataException("Facial mapping bone identity mismatch.");
        var clips = new JArray(); var skipped = new JArray();
        var resourceList = resources.ToArray();
        foreach (var resource in resourceList.OrderBy(r => (string)r["m_Name"], StringComparer.Ordinal))
        {
            var entries = Entries(resource).ToArray();
            var unknown = entries.Select(e => (string)e["_ctrlName"]).Except(controls.Keys).ToArray();
            if (unknown.Length != 0) { skipped.Add(new JObject { ["source"] = resource["m_Name"], ["reason"] = "Unmapped controllers", ["controllers"] = new JArray(unknown) }); continue; }
            if (entries.Select(e => (string)e["_ctrlName"]).Distinct().Count() != entries.Length) throw new InvalidDataException("Duplicate facial control.");
            var duration = resource["_pose"] != null ? 0 : Number(resource["_duration"]);
            if (duration < 0) throw new InvalidDataException("Negative facial duration.");
            var times = new SortedSet<double> { 0, duration };
            for (var n = 1L; n / 60.0 < duration; n++) times.Add(n / 60.0);
            foreach (var entry in entries.Where(e => e["_curve"] != null))
            {
                ValidateCurve((JArray)entry["_curve"]["m_Curve"]);
                foreach (var key in (JArray)entry["_curve"]["m_Curve"])
                    if (Number(key["time"]) >= 0 && Number(key["time"]) <= duration) times.Add(Number(key["time"]));
            }
            var tracks = bones.ToDictionary(p => p.Key, p => new List<float[]>());
            var previous = new Dictionary<int, NQuaternion>();
            foreach (var time in times)
            {
                var pose = bones.ToDictionary(p => p.Key, p => new[] { Vec(p.Value.cfg["position"]), Vec(p.Value.cfg["rotation"]), Vec(p.Value.cfg["scale"]) });
                foreach (var entry in entries)
                {
                    var cfg = controls[(string)entry["_ctrlName"]];
                    var weight = entry["_curve"] == null ? Number(entry["_value"]) : Curve((JArray)entry["_curve"]["m_Curve"], time);
                    if ((int)cfg["partType"] != 32) weight = Math.Clamp(weight, 0, 1);
                    foreach (var delta in (JArray)cfg["bones"])
                    {
                        var values = pose[(int)delta["boneID"]];
                        values[0] += Vec(delta["position"]) * (float)weight;
                        values[1] += Vec(delta["rotation"]) * (float)weight;
                        values[2] += Vec(delta["scale"]) * (float)weight;
                    }
                }
                foreach (var bone in pose)
                {
                    var p = bone.Value[0]; var s = bone.Value[2];
                    // The source Maya XYZ order/signs were checked against the original neutral quaternions.
                    var q = MayaQuaternion(bone.Value[1]);
                    if (previous.TryGetValue(bone.Key, out var last) && NQuaternion.Dot(last, q) < 0) q = -q;
                    previous[bone.Key] = q;
                    tracks[bone.Key].Add(new[] { p.X, p.Y, p.Z, q.X, q.Y, q.Z, q.W, s.X, s.Y, s.Z });
                }
            }
            clips.Add(new JObject { ["name"] = resource["m_Name"], ["identity"] = resource["eiem_source_identity"],
                ["kind"] = resource["_blinkInterval"] != null ? "Emotion" : resource["_pose"] != null ? "Pose" : "Anim",
                ["duration"] = duration, ["times"] = new JArray(times),
                ["tracks"] = new JArray(tracks.Select(t =>
                {
                    var constant = t.Value.Count > 2 && t.Value.Skip(1).All(row => row.SequenceEqual(t.Value[0]));
                    var track = new JObject { ["path"] = bonePaths[t.Key], ["samples"] = new JArray((constant ? new[] { t.Value[0], t.Value[^1] } : t.Value.ToArray()).Select(row => new JArray(row))) };
                    if (constant) track["times"] = new JArray(0, duration);
                    return track;
                })) });
        }
        if (clips.Count == 0) throw new InvalidDataException("No facial animations match the selected avatar.");
        return new JObject { ["format"] = "EFF_ANIMATION_LIBRARY", ["coordinate"] = "unity-y-up-left-handed",
            ["sourceAvatar"] = avatarPath, ["avatar"] = avatar["m_Name"], ["avatarIdentity"] = identity,
            ["skeleton"] = skeleton, ["clips"] = clips, ["skipped"] = skipped,
            ["limitations"] = new JArray("Bone animation only; shader expressions excluded.", "Emotion exports its base pose; no random blink/micro-expression timing is invented.") };
    }

    private static IEnumerable<JToken> Entries(JObject resource) => (resource["_pose"] != null ? PoseFields : CurveFields)
        .SelectMany(field => (IEnumerable<JToken>)(resource["_pose"]?[field] ?? resource[field] ?? new JArray()));
    private static double Number(JToken value)
    {
        if (value == null) throw new InvalidDataException("Missing animation scalar.");
        var result = (double)value; if (!double.IsFinite(result)) throw new InvalidDataException("Nonfinite animation scalar."); return result;
    }
    private static NVector Vec(JToken value) => new((float)Number(value["x"]), (float)Number(value["y"]), (float)Number(value["z"]));
    private static void ValidateCurve(JArray keys)
    {
        if (keys.Any(k => (int)k["weightedMode"] != 0)) throw new InvalidDataException("Weighted facial curve has no verified evaluator.");
        for (var i = 1; i < keys.Count; i++)
            if (Number(keys[i]["time"]) <= Number(keys[i - 1]["time"])) throw new InvalidDataException("Unordered facial curve.");
    }
    private static double Curve(JArray keys, double time)
    {
        if (keys.Count == 0) return 0;
        if (time <= Number(keys[0]["time"])) return Number(keys[0]["value"]);
        if (time >= Number(keys.Last["time"])) return Number(keys.Last["value"]);
        var low = 1; var high = keys.Count - 1;
        while (low < high) { var middle = (low + high) / 2; if (time >= Number(keys[middle]["time"])) low = middle + 1; else high = middle; }
        var index = low;
        var a = keys[index - 1]; var b = keys[index]; var length = Number(b["time"]) - Number(a["time"]);
        var t = (time - Number(a["time"])) / length;
        var slopeA = (double)a["outSlope"]; var slopeB = (double)b["inSlope"];
        if (!double.IsFinite(slopeA) || !double.IsFinite(slopeB)) return Number(a["value"]);
        return (2*t*t*t-3*t*t+1)*Number(a["value"]) + (t*t*t-2*t*t+t)*length*slopeA +
            (-2*t*t*t+3*t*t)*Number(b["value"]) + (t*t*t-t*t)*length*slopeB;
    }
}
