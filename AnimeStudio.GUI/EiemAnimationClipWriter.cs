using AnimeStudio;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio.GUI;

/// <summary>Author numeric tracks in one native Clip using the current game's schema.
/// Playback and target assembly belong to EFF; this writer creates no controllers.
/// This is an experimental writer until the mixed Clip passes in-game evaluation.</summary>
internal static class EiemAnimationClipWriter
{
    internal enum Kind { Position, Rotation, Scale, Shape, MaterialFloat, MaterialColor, MaterialVector }
    internal sealed record Track(string Path, Kind Kind, string Property, int RendererClass,
        IReadOnlyList<float[]> Samples);
    internal sealed record Clip(string Name, float SampleRate, bool Loop, IReadOnlyList<Track> Tracks);

    private static uint Crc(string value)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }

    private static int Dimension(Kind kind) => kind switch
    {
        Kind.Position or Kind.Scale => 3,
        Kind.Rotation or Kind.MaterialColor or Kind.MaterialVector => 4,
        Kind.Shape or Kind.MaterialFloat => 1,
        _ => throw new InvalidDataException("Unsupported animation track kind")
    };

    private static (int ClassId, byte CustomType, uint Attribute) Binding(Track track, int component)
    {
        if (track.Kind is Kind.Position or Kind.Rotation or Kind.Scale)
            return (4, 0, track.Kind == Kind.Position ? 1u : track.Kind == Kind.Rotation ? 2u : 3u);
        if (track.Kind == Kind.Shape) return (137, 20, Crc(track.Property));
        uint attribute = Crc(track.Property) & 0x0fffffff;
        attribute |= track.Kind == Kind.MaterialFloat ? 0x80000000u : (uint)component << 28;
        if (track.Kind == Kind.MaterialColor) attribute |= 0x40000000u;
        return (track.RendererClass, 22, attribute);
    }

    private static void CheckName(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
            throw new InvalidDataException("Invalid animation " + label);
    }

    private static int Validate(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        CheckName(clip.Name, "name");
        if (!float.IsFinite(clip.SampleRate) || clip.SampleRate is < 1 or > 480 || clip.Tracks == null || clip.Tracks.Count is < 1 or > 16384)
            throw new InvalidDataException("Invalid animation sample rate or track count");
        int frames = clip.Tracks[0]?.Samples?.Count ?? 0;
        if (frames < 2 || frames > 10_000_000) throw new InvalidDataException("Invalid animation frame count");
        var targets = new HashSet<(uint Path, int ClassId, byte CustomType, uint Attribute)>();
        var paths = new Dictionary<uint, string>();
        long scalarCount = 0;
        foreach (var track in clip.Tracks)
        {
            if (track == null || track.Path == null || track.Path.Contains('\\') || track.Path.Contains('\0') ||
                track.Path.Length != 0 && track.Path.Split('/').Any(p => p is "" or "." or ".."))
                throw new InvalidDataException("Invalid relative animation target path");
            uint path = Crc(track.Path);
            if (paths.TryGetValue(path, out var previous) && previous != track.Path)
                throw new InvalidDataException("Animation target path CRC collision");
            paths[path] = track.Path;
            int dimension = Dimension(track.Kind);
            scalarCount += (long)frames * dimension;
            if (scalarCount > 1_000_000) throw new InvalidDataException("Animation sample budget exceeded");
            bool transform = track.Kind is Kind.Position or Kind.Rotation or Kind.Scale;
            if (!transform) CheckName(track.Property, "property");
            if (transform && !string.IsNullOrEmpty(track.Property) ||
                track.Kind == Kind.Shape && track.RendererClass != 137 ||
                !transform && track.Kind != Kind.Shape && track.RendererClass is not (23 or 137 or 199))
                throw new InvalidDataException("Animation component type differs from track kind");
            for (int i = 0; i < (transform ? 1 : dimension); i++)
            {
                var binding = Binding(track, i);
                if (!targets.Add((path, binding.ClassId, binding.CustomType, binding.Attribute)))
                    throw new InvalidDataException("Duplicate or colliding animation property binding");
            }
            if (track.Samples == null || track.Samples.Count != frames)
                throw new InvalidDataException("Mixed animation tracks must share a sample timeline");
            foreach (var sample in track.Samples)
            {
                if (sample == null || sample.Length != dimension || sample.Any(v => !float.IsFinite(v)))
                    throw new InvalidDataException("Invalid animation sample dimensions or value");
                if (track.Kind == Kind.Rotation && Math.Abs(sample.Sum(v => (double)v * v) - 1) > .001)
                    throw new InvalidDataException("Animation quaternion is not normalized");
            }
        }
        return frames;
    }

    internal static TypeTreeDocument Build(TypeTree schema, Clip clip)
    {
        int frames = Validate(clip);
        var document = TypeTreeDocument.CreateDefault(schema);
        var data = (JObject)document.Root.ToJson();
        if (document.Root.Schema.Type != "AnimationClip") throw new InvalidDataException("Expected AnimationClip TypeTree");
        data["m_Name"] = clip.Name;
        data["m_SampleRate"] = clip.SampleRate;
        data["m_TransferCompressed"] = true;
        data["m_WrapMode"] = clip.Loop ? 2 : 1;
        var muscle = (JObject)data["m_MuscleClip"];
        muscle["m_StartTime"] = 0;
        muscle["m_StopTime"] = (frames - 1f) / clip.SampleRate;
        muscle["m_LoopTime"] = clip.Loop;
        muscle["m_KeepOriginalPositionY"] = true;
        muscle["m_StartAtOrigin"] = true;
        // Default zero quaternions/scales are not identity poses. Normalize all
        // native xforms, including the delta pose, without copying donor poses.
        void Neutral(JToken node)
        {
            if (node is not JObject obj) return;
            if (obj["t"] is JObject && obj["q"] is JObject q && obj["s"] is JObject s)
            {
                q["w"] = 1;
                s["x"] = s["y"] = s["z"] = 1;
            }
            foreach (var child in obj.Properties()) Neutral(child.Value);
        }
        Neutral(muscle);
        var bindings = new JArray();
        var ordered = clip.Tracks.OrderBy(t => t.Kind is Kind.Position or Kind.Rotation or Kind.Scale ? 0 : 1)
            .ThenBy(t => t.Path, StringComparer.Ordinal).ThenBy(t => t.Kind).ThenBy(t => t.Property, StringComparer.Ordinal).ToArray();
        int curves = ordered.Sum(t => Dimension(t.Kind));
        foreach (var track in ordered)
        {
            bool transform = track.Kind is Kind.Position or Kind.Rotation or Kind.Scale;
            for (int component = 0; component < (transform ? 1 : Dimension(track.Kind)); component++)
            {
                var binding = Binding(track, component);
                bindings.Add(new JObject
                {
                    ["path"] = Crc(track.Path), ["attribute"] = binding.Attribute,
                    ["script"] = new JObject { ["m_FileID"] = 0, ["m_PathID"] = 0 },
                    ["typeID"] = binding.ClassId, ["customType"] = binding.CustomType, ["isPPtrCurve"] = 0
                });
            }
        }
        data["m_ClipBindingConstant"]["genericBindings"] = bindings;
        var dense = (JObject)muscle["m_Clip"]["data"]["m_DenseClip"];
        dense["m_FrameCount"] = frames;
        dense["m_CurveCount"] = curves;
        dense["m_SampleRate"] = clip.SampleRate;
        dense["m_BeginTime"] = 0;
        foreach (string factor in new[] { "m_PositionFactor", "m_EulerFactor", "m_ScaleFactor", "m_FloatFactor" }) dense[factor] = 1;
        var samples = new JArray();
        for (int frame = 0; frame < frames; frame++)
            foreach (var track in ordered)
                foreach (float value in track.Samples[frame]) samples.Add(value);
        dense["m_SampleArray"] = samples;
        muscle["m_ValueArrayDelta"] = new JArray(ordered.SelectMany(t =>
            Enumerable.Range(0, Dimension(t.Kind)).Select(c => new JObject
            { ["m_Start"] = t.Samples[0][c], ["m_Stop"] = t.Samples[^1][c] })));
        var acl = data["m_AclCompressedBuffer"];
        acl["Header"]["Version"] = uint.MaxValue;
        foreach (string index in new[] { "RootPosIndex", "RootRotIndex", "RootScaleIndex" }) acl[index] = ushort.MaxValue;
        data["m_HasMotionFloatCurves"] = ordered.Any(t => t.Kind is not (Kind.Position or Kind.Rotation or Kind.Scale));
        document.ApplyJson(data);
        // Both extents are serialized metadata, never copied from the donor.
        // The engine's interpretation still needs the live evaluation probe.
        data["m_MuscleClipSize"] = document.Root["m_MuscleClip"].Length;
        data["m_TotalSize"] = document.Encode().Length;
        document.ApplyJson(data);
        return document;
    }
}
