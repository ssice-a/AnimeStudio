using Newtonsoft.Json.Linq;
using System.IO;

namespace AnimeStudio.GUI;

/// <summary>Export policy for the owned runtime copy, never the author source.</summary>
internal static class EiemEffectLodPolicy
{
    internal static bool Read(JObject edits) => (string)edits["lodCoverage"] switch
    {
        null or "source" => false,
        "all" => true,
        _ => throw new InvalidDataException("Unknown effect LOD coverage")
    };

    internal static void Apply(JObject data)
    {
        void Flag(JObject obj, string field, bool enabled)
        {
            if (obj[field] == null) return;
            obj[field] = obj[field].Type switch
            {
                JTokenType.Boolean => new JValue(enabled),
                JTokenType.Integer => new JValue(enabled ? 1 : 0),
                _ => throw new InvalidDataException("Effect LOD flag type differs: " + field)
            };
        }
        Flag(data, "isUseDistanceLod", false);
        Flag(data, "_isCullDisable", true);
        Flag(data, "useGuardLod", false);
        Flag(data, "dynamicChangeGuardLod", false);
        if (data["lodSetting"] is JArray settings)
            foreach (JObject row in settings)
            {
                // Beyond.Gameplay.SettingLodLevel is a flags enum:
                // Low=1, Mid=2, High=4, Highest=8. Platform/target masks stay.
                if (row["settingLodLevel"] != null) row["settingLodLevel"] = 15;
                if (row["_distanceLodInfos"] is not JArray distances) continue;
                foreach (JObject distance in distances)
                {
                    Flag(distance, "isActive", true);
                    if (distance["speedPercent"] != null) distance["speedPercent"] = 1.0;
                }
            }
        if (data["distanceLodCfgList"] is JArray levels)
            foreach (JObject level in levels)
                if (level["framePercent"] != null) level["framePercent"] = 1.0;
    }

    internal static void ApplyParticle(JObject data)
    {
        // A newly attached/UI particle can have empty first-frame bounds.
        // Pausing while invisible prevents bounds ever becoming visible.
        // This policy applies only to the isolated all-LOD runtime copy.
        if (data["cullingMode"] == null) return;
        if (data["cullingMode"].Type != JTokenType.Integer)
            throw new InvalidDataException("Particle culling mode type differs");
        data["cullingMode"] = 3; // ParticleSystemCullingMode.AlwaysSimulate
    }
}
