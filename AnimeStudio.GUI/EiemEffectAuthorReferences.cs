using AnimeStudio;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio.GUI;

/// <summary>Bind native fields to the same public Resource IDs used by the
/// ordinary INI mesh/material/texture assembly. No resource loader lives here.</summary>
internal static class EiemEffectAuthorReferences
{
    internal static JArray Apply(JObject edits,
        Dictionary<string, (SerializedFile File, byte[] Bytes, JToken Row)> files,
        IReadOnlyDictionary<string, JToken> objects, List<FileReader> opened, IDictionary<string, int> types)
    {
        var leases = new JArray();
        if (edits["authorReferences"] == null) return leases;
        if (edits["authorReferences"] is not JArray rows || rows.Count > 4096)
            throw new InvalidDataException("Invalid author resource references");
        var bindings = new JArray();
        var targets = new Dictionary<string, EiemEffectGameReferenceInputs.Target>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            string resource = (string)row["resource"], kind = (string)row["type"];
            if (string.IsNullOrWhiteSpace(resource) || resource.Length > 1024 || resource.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(kind)) throw new InvalidDataException("Missing author Resource ID/type");
            if (kind == "Texture") kind = "Texture2D";
            if (!Enum.TryParse<ClassIDType>(kind, out var classId) || classId is not (ClassIDType.Mesh or ClassIDType.Material or ClassIDType.Texture2D))
                throw new InvalidDataException("Unsupported native author resource type: " + kind);
            string marker = "CAB-" + EiemEffectBuildCommand.Hash(Encoding.UTF8.GetBytes(
                "EFF_AUTHOR_RESOURCE/1\n" + resource + "\n" + kind)).ToLowerInvariant()[..32];
            // The binding walker uses a private key only. Public Resource IDs
            // are never interpreted as game paths or native identities.
            var binding = new JObject { ["owner"] = row["owner"]?.DeepClone(), ["field"] = row["field"]?.DeepClone(),
                ["resource"] = resource, ["type"] = kind };
            bindings.Add(binding);
            targets[EiemEffectGameReferences.ReferenceKey(binding)] = new(marker, 1, (int)classId);
            types[marker + ":1"] = (int)classId;
            var lease = new JObject { ["resource"] = resource, ["type"] = kind, ["relocationCab"] = marker, ["relocationId"] = 1 };
            if (!leases.Any(l => JToken.DeepEquals(l, lease))) leases.Add(lease);
        }
        EiemEffectGameReferences.ApplyBindings(edits, bindings, targets, files, objects, opened, preserveAuthored: false);
        return leases;
    }
}
