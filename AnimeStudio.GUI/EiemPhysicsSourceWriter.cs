using AnimeStudio;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using AssetObject = AnimeStudio.Object;

namespace AnimeStudio.GUI
{
    /// <summary>
    /// Writes the original BeyondDynamicBone component graph consumed by the
    /// Blender native-authoring importer. This is source data beside a normal
    /// EIEM package; it does not add a runtime Physics action to mod.ini.
    /// </summary>
    internal static class EiemPhysicsSourceWriter
    {
        private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
        {
            "BeyondBoneCloth", "BeyondBoneSphereCollider",
            "BeyondBoneCapsuleCollider", "BeyondBonePlaneCollider"
        };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        public static int Write(string output, GameObject root, string prefab,
            string vfsFingerprint, string source, IEnumerable<string> closure,
            IEnumerable<SerializedFile> serializedFiles, bool supportedOnly)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));
            if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(prefab) ||
                string.IsNullOrWhiteSpace(vfsFingerprint) || string.IsNullOrWhiteSpace(source))
                throw new InvalidDataException("Physics source export requires Prefab and VFS identities.");

            var files = serializedFiles?.Distinct().ToArray() ?? Array.Empty<SerializedFile>();
            var hierarchy = EndfieldPrefabDocument.GetHierarchy(root).ToArray();
            var hierarchyIds = hierarchy.Select(Identity).ToHashSet(StringComparer.Ordinal);
            var hierarchyTransforms = hierarchy.Where(x => x.m_Transform != null)
                .Select(x => x.m_Transform).ToHashSet();

            var components = files.SelectMany(file => file.Objects).OfType<MonoBehaviour>()
                .Where(component => !supportedOnly || IsSupported(component, hierarchyIds))
                .OrderBy(component => component.assetsFile.fileName, StringComparer.Ordinal)
                .ThenBy(component => component.m_PathID).ToArray();
            if (components.Length == 0)
                return 0;

            Directory.CreateDirectory(output);
            var rows = new List<object>();
            foreach (var component in components)
            {
                var id = rows.Count.ToString("D5");
                component.m_Script.TryGet(out var script);
                component.m_GameObject.TryGet(out var owner);
                var raw = component.GetRawData();
                if (raw.Length != component.byteSize)
                    throw new InvalidDataException($"Truncated raw component {Identity(component)}");
                File.WriteAllBytes(Path.Combine(output, id + ".bin"), raw);
                var nodes = component.serializedType?.m_Type?.m_Nodes;
                var hasSchema = nodes?.Count > 0;
                string error = null;
                long? consumed = null;
                var decoded = false;
                var references = new List<object>();
                if (hasSchema)
                {
                    WriteJson(Path.Combine(output, id + ".schema.json"), nodes.Select(node => new
                    {
                        type = node.m_Type, name = node.m_Name, level = node.m_Level,
                        byteSize = node.m_ByteSize, metaFlags = node.m_MetaFlag
                    }));
                    var saved = component.reader.Position;
                    try
                    {
                        var data = component.ToType();
                        consumed = component.reader.Position - component.reader.byteStart;
                        if (data == null || consumed != component.byteSize)
                            throw new InvalidDataException($"TypeTree consumed {consumed}/{component.byteSize} bytes.");
                        WriteJson(Path.Combine(output, id + ".data.json"), Plain(data));
                        CollectReferences(data, "$", component.assetsFile, references);
                        decoded = true;
                    }
                    catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                    finally { component.reader.Position = saved; }
                }
                if (supportedOnly && !decoded)
                    throw new InvalidDataException($"Cannot decode native Physics component {OwnerPath(owner)}: {error}");
                rows.Add(new
                {
                    id, identity = Identity(component), cab = component.assetsFile.fileName,
                    pathId = component.m_PathID, componentName = component.m_Name,
                    owner = owner == null ? null : Identity(owner), ownerPath = OwnerPath(owner),
                    inSelectedPrefab = owner != null && hierarchyIds.Contains(Identity(owner)),
                    script = script == null ? null : new
                    {
                        identity = Identity(script), assembly = script.m_AssemblyName,
                        ns = script.m_Namespace, type = script.m_ClassName
                    },
                    scriptPointer = new { fileId = component.m_Script.m_FileID, pathId = component.m_Script.m_PathID },
                    byteSize = raw.Length, sha256 = Convert.ToHexString(SHA256.HashData(raw)),
                    typeTreeNodes = nodes?.Count ?? 0, decoded, consumed, error, references
                });
            }

            var transforms = (supportedOnly
                    ? hierarchyTransforms
                    : files.SelectMany(file => file.Objects).OfType<Transform>())
                .OrderBy(transform => Identity(transform), StringComparer.Ordinal)
                .Select(transform =>
                {
                    transform.m_GameObject.TryGet(out var owner);
                    transform.m_Father.TryGet(out var parent);
                    if (supportedOnly && parent != null && !hierarchyTransforms.Contains(parent))
                        parent = null;
                    return new
                    {
                        identity = Identity(transform), owner = owner == null ? null : Identity(owner),
                        path = OwnerPath(owner), parent = parent == null ? null : Identity(parent),
                        inSelectedPrefab = owner != null && hierarchyIds.Contains(Identity(owner)),
                        localPosition = new[] { transform.m_LocalPosition.X, transform.m_LocalPosition.Y, transform.m_LocalPosition.Z },
                        localRotation = new[] { transform.m_LocalRotation.X, transform.m_LocalRotation.Y,
                            transform.m_LocalRotation.Z, transform.m_LocalRotation.W },
                        localScale = new[] { transform.m_LocalScale.X, transform.m_LocalScale.Y, transform.m_LocalScale.Z }
                    };
                }).ToArray();

            WriteJson(Path.Combine(output, "components.json"), new
            {
                purpose = "raw-component-evidence-not-a-physics-package", prefab,
                vfsFingerprint, source, closure = closure?.ToArray() ?? Array.Empty<string>(),
                files = files.Select(file => new
                {
                    cab = file.fileName, file.unityVersion,
                    externals = file.m_Externals.Select((external, index) =>
                        new { fileId = index + 1, cab = external.fileName })
                }),
                components = rows, transforms
            });
            return rows.Count;
        }

        private static bool IsSupported(MonoBehaviour component, HashSet<string> hierarchy)
        {
            component.m_GameObject.TryGet(out var owner);
            component.m_Script.TryGet(out var script);
            return owner != null && hierarchy.Contains(Identity(owner)) && script != null &&
                string.Equals(script.m_AssemblyName, "BeyondDynamicBone.dll", StringComparison.Ordinal) &&
                SupportedTypes.Contains(script.m_ClassName);
        }

        private static string Identity(AssetObject obj) => obj.assetsFile.fileName + ":" + obj.m_PathID;

        private static string OwnerPath(GameObject obj)
        {
            if (obj == null) return null;
            var names = new List<string>();
            var seen = new HashSet<string>();
            while (obj != null)
            {
                if (!seen.Add(Identity(obj))) throw new InvalidDataException("Cyclic Transform hierarchy.");
                names.Add(obj.m_Name);
                if (obj.m_Transform?.m_Father.TryGet(out var parent) != true ||
                    !parent.m_GameObject.TryGet(out obj)) break;
            }
            names.Reverse();
            return string.Join("/", names);
        }

        private static object Plain(object value)
        {
            if (value is KeyValuePair<object, object> pair)
                return new { Key = Plain(pair.Key), Value = Plain(pair.Value) };
            if (value is IDictionary dictionary)
            {
                var result = new Dictionary<string, object>();
                foreach (DictionaryEntry entry in dictionary)
                    result.Add((string)entry.Key, Plain(entry.Value));
                return result;
            }
            if (value is IEnumerable items && value is not string && value is not byte[])
                return items.Cast<object>().Select(Plain).ToArray();
            return value;
        }

        private static void CollectReferences(object value, string field, SerializedFile file, List<object> result)
        {
            if (value is IDictionary dictionary)
            {
                if (dictionary.Contains("m_FileID") && dictionary.Contains("m_PathID"))
                {
                    var fileId = Convert.ToInt32(dictionary["m_FileID"]);
                    var pathId = Convert.ToInt64(dictionary["m_PathID"]);
                    var pointer = new PPtr<AssetObject>(fileId, pathId, file);
                    pointer.TryGet(out var target);
                    GameObject owner = target as GameObject;
                    if (target is Component component) component.m_GameObject.TryGet(out owner);
                    result.Add(new
                    {
                        field, fileId, pathId, isNull = pointer.IsNull, resolved = target != null,
                        identity = target == null ? null : Identity(target),
                        type = target?.type.ToString(), ownerPath = OwnerPath(owner)
                    });
                    return;
                }
                foreach (DictionaryEntry entry in dictionary)
                    CollectReferences(entry.Value, field + "." + entry.Key, file, result);
            }
            else if (value is KeyValuePair<object, object> pair)
            {
                CollectReferences(pair.Key, field + ".Key", file, result);
                CollectReferences(pair.Value, field + ".Value", file, result);
            }
            else if (value is IEnumerable sequence && value is not string && value is not byte[])
            {
                var index = 0;
                foreach (var item in sequence)
                    CollectReferences(item, field + $"[{index++}]", file, result);
            }
        }

        private static void WriteJson(string path, object data)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            JsonSerializer.Serialize(stream, data, JsonOptions);
        }
    }
}
