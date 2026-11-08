using AnimeStudio;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AnimeStudio.GUI;

/// <summary>Creates an author-owned particle Prefab from game TypeTrees only.</summary>
internal static class EiemEffectAuthorCommand
{
    private sealed record NativeSpec(long Id, int ClassId, int TypeId, string Name, string Owner,
        TypeTreeDocument Document, byte[] Payload);

    internal static int Run(string[] args)
    {
        if (args.Length != 5)
        {
            Console.Error.WriteLine("Usage: --create-eff-effect <schema-source.efffx> <target-prefab> <output.efffx> <root-name>");
            return 2;
        }
        try
        {
            Create(args[1], args[2], args[3], args[4]);
            Console.WriteLine("Created author-owned effect workspace: " + Path.GetFullPath(args[3]));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    internal static void Create(string schemaSource, string targetPrefab, string output, string rootName)
    {
        schemaSource = Path.GetFullPath(schemaSource);
        output = Path.GetFullPath(output);
        targetPrefab = NormalizePrefab(targetPrefab);
        rootName = NormalizeName(rootName);
        if (!Directory.Exists(schemaSource)) throw new InvalidDataException("Schema donor must be an exported .efffx directory");

        var donor = JObject.Parse(File.ReadAllText(Path.Combine(schemaSource, "source/manifest.json")));
        if ((string)donor["format"] != "EFFSOURCE" || (int?)donor["version"] != 1)
            throw new InvalidDataException("Unsupported TypeTree donor workspace");

        var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.ArknightsEndfield) };
        string selectedCab = null;
        byte[] donorBytes = null, metadataOnly = null;
        foreach (var row in donor["serializedFiles"] ?? new JArray())
        {
            byte[] bytes = ReadSnapshot(schemaSource, row["snapshot"]);
            using var reader = new FileReader((string)row["cab"], new MemoryStream(bytes, false));
            var file = new SerializedFile(reader, manager);
            var required = new[] { 1, 4, 198, 199, 142 };
            if (required.All(classId => file.m_Types.Any(t => t.classID == classId && t.m_Type?.m_Nodes?.Count > 0)) &&
                file.m_RefTypes?.Count == 0)
            {
                selectedCab = file.fileName;
                donorBytes = bytes;
                metadataOnly = EiemEffectPrefabWriter.CreateMetadataOnly(file, bytes);
                break;
            }
        }
        if (selectedCab == null)
            throw new InvalidDataException("No single donor CAB contains GameObject, Transform, ParticleSystem, ParticleSystemRenderer and AssetBundle TypeTrees without managed reference types");

        using var schemaReader = new FileReader(selectedCab, new MemoryStream(donorBytes, false));
        var donorFile = new SerializedFile(schemaReader, manager);

        var typeIds = new Dictionary<int, int>();
        foreach (int classId in new[] { 1, 4, 198, 199, 142 })
        {
            int typeId = donorFile.m_Types.FindIndex(t => t.classID == classId && t.m_Type?.m_Nodes?.Count > 0);
            if (typeId < 0) throw new InvalidDataException("Missing native TypeTree for class " + classId);
            typeIds.Add(classId, typeId);
        }

        long gameObjectId = 1, transformId = 2, particleId = 3, rendererId = 4, bundleId = 5;
        string cab = "CAB-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            targetPrefab + "\n" + rootName + "\n" + Guid.NewGuid().ToString("N"))))[..32].ToLowerInvariant();
        string rootIdentity = Identity(cab, gameObjectId);
        var components = new[] { (Id: transformId, ClassId: 4), (Id: particleId, ClassId: 198), (Id: rendererId, ClassId: 199) };
        var authored = new List<NativeSpec>();

        TypeTreeDocument Document(int classId) => TypeTreeDocument.CreateDefault(
            donorFile.m_Types[typeIds[classId]].m_Type, donorFile.m_RefTypes);
        TypeTreeDocument Add(int classId, long id, string name, string owner, Action<JObject> initialize)
        {
            var doc = Document(classId);
            var data = (JObject)doc.Root.ToJson();
            initialize(data);
            doc.ApplyJson(data);
            byte[] bytes = doc.Encode();
            _ = new TypeTreeDocument(donorFile.m_Types[typeIds[classId]].m_Type, bytes, donorFile.m_RefTypes);
            authored.Add(new(id, classId, typeIds[classId], name, owner, doc, bytes));
            return doc;
        }

        Add(1, gameObjectId, rootName, null, data =>
        {
            Set(data, "m_Name", new JValue(rootName));
            Set(data, "m_IsActive", new JValue(true));
            Set(data, "m_Component", new JArray(components.Select(c => new JObject
            {
                ["component"] = Pointer(c.Id)
            })));
        });
        Add(4, transformId, "", rootIdentity, data =>
        {
            Set(data, "m_GameObject", Pointer(gameObjectId));
            Set(data, "m_LocalPosition", Vector(0, 0, 0));
            Set(data, "m_LocalRotation", Quaternion(0, 0, 0, 1));
            Set(data, "m_LocalScale", Vector(1, 1, 1));
            Set(data, "m_Children", new JArray());
            Set(data, "m_Father", Pointer(0));
        });
        Add(198, particleId, "", rootIdentity, data => InitializeParticle(data, gameObjectId));
        Add(199, rendererId, "", rootIdentity, data =>
        {
            Set(data, "m_GameObject", Pointer(gameObjectId));
            Set(data, "m_Enabled", new JValue(true));
            Set(data, "m_RenderMode", new JValue(0));
            Set(data, "m_Materials", new JArray());
            Set(data, "m_Mesh", Pointer(0));
            Set(data, "m_MaxParticleSize", new JValue(0.5f));
        });
        Add(142, bundleId, "", null, data => InitializeBundle(data, targetPrefab, gameObjectId,
            new[] { gameObjectId, transformId, particleId, rendererId }));

        var nativeRows = authored.Select(spec => new EiemEffectPrefabWriter.NativeObject(spec.Id, spec.TypeId, spec.Payload)).ToArray();
        using var emptyReader = new FileReader(cab, new MemoryStream(metadataOnly, false));
        var emptyFile = new SerializedFile(emptyReader, manager);
        byte[] nativeBytes = EiemEffectPrefabWriter.Build(emptyFile, metadataOnly, nativeRows);
        using var outputReader = new FileReader(cab, new MemoryStream(nativeBytes, false));
        var outputFile = new SerializedFile(outputReader, manager);
        if (outputFile.m_Objects.Count != authored.Count || outputFile.m_Externals.Count != 0)
            throw new InvalidDataException("Generated effect object graph failed native table validation");

        var typeSchemaFiles = new Dictionary<int, string>();
        var schemaPayloads = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        foreach (int classId in authored.Select(a => a.ClassId).Distinct())
        {
            var type = outputFile.m_Types[typeIds[classId]];
            var schema = Schema(type.m_Type, outputFile.m_RefTypes);
            string canonical = schema.ToString(Formatting.None);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            string relative = "schemas/" + hash + ".json";
            typeSchemaFiles.Add(classId, relative);
            schemaPayloads.TryAdd(relative, schema);
        }

        var hierarchy = new JArray(new JObject
        {
            ["identity"] = rootIdentity, ["name"] = rootName,
            ["transform"] = Identity(cab, transformId), ["parent"] = JValue.CreateNull(),
            ["children"] = new JArray(), ["components"] = new JArray(authored.Where(a => a.Id != bundleId).Select(a => Identity(cab, a.Id)))
        });
        var effectObjects = new JArray();
        foreach (var spec in authored.OrderBy(a => a.Id))
        {
            var row = outputFile.m_Objects.Single(o => o.m_PathID == spec.Id);
            var objectData = new TypeTreeDocument(row.serializedType.m_Type,
                nativeBytes.AsSpan((int)row.byteStart, (int)row.byteSize).ToArray(), outputFile.m_RefTypes);
            string identity = Identity(cab, spec.Id);
            string dataFile = "objects/" + effectObjects.Count.ToString("D6") + ".json";
            effectObjects.Add(new JObject
            {
                ["identity"] = identity, ["name"] = spec.Name,
                ["classId"] = spec.ClassId, ["type"] = ((ClassIDType)spec.ClassId).ToString(),
                ["owner"] = spec.Owner, ["state"] = "parsed", ["schema"] = typeSchemaFiles[spec.ClassId],
                ["data"] = dataFile,
                ["native"] = new JObject { ["cab"] = cab, ["pathId"] = spec.Id, ["offset"] = row.byteStart, ["size"] = row.byteSize },
                ["sourceBundle"] = "author:" + targetPrefab,
                ["logicalPaths"] = new JArray(), ["references"] = References(objectData, cab, outputFile),
                ["referenceCoverage"] = "all-typed-pointers",
                ["particleModules"] = spec.ClassId == 198 ? ParticleModules(objectData) : new JArray()
            });
        }

        var manifest = new JObject
        {
            ["format"] = "EFFSOURCE", ["version"] = 1, ["purpose"] = "author-source-baseline",
            ["nativePack"] = false, ["runtimeNativeMemory"] = false,
            ["vfsFingerprint"] = (string)donor["vfsFingerprint"], ["sourceIndex"] = "author-generated",
            ["prefab"] = targetPrefab, ["root"] = rootIdentity, ["sourceBundle"] = "author:" + targetPrefab,
            ["containerEntries"] = new JArray(new JObject
            {
                ["bundle"] = Identity(cab, bundleId), ["serializedKey"] = targetPrefab,
                ["logicalPath"] = targetPrefab, ["asset"] = PointerText(cab, gameObjectId),
                ["preloadIndex"] = 0, ["preloadSize"] = 4
            }),
            ["closure"] = new JArray(), ["bundleInputs"] = new JArray(),
            ["resources"] = new JArray(), ["rendererUses"] = new JArray(),
            ["hierarchy"] = new JArray(new JObject
            {
                ["identity"] = rootIdentity, ["name"] = rootName, ["transform"] = Identity(cab, transformId),
                ["path"] = "", ["native"] = JObject.FromObject(NativeRange(outputFile, gameObjectId)),
                ["transformNative"] = JObject.FromObject(NativeRange(outputFile, transformId)), ["parent"] = PointerText(cab, 0),
                ["children"] = new JArray(), ["localPosition"] = Vector(0, 0, 0),
                ["localRotation"] = new JArray(0, 0, 0, 1), ["localScale"] = Vector(1, 1, 1),
                ["components"] = new JArray(components.Select(c => PointerText(cab, c.Id)))
            }),
            ["serializedFiles"] = new JArray(), ["rawStreams"] = new JArray(),
            ["streamReferences"] = new JArray(), ["runtimeEmbeddedBundles"] = new JArray(),
            ["preservation"] = new JArray("Author-created Prefab objects and TypeTree metadata only.",
                "No game Mesh, Material, Texture, or source Prefab objects are included.")
        };
        var serializedRow = new JObject
        {
            ["cab"] = cab, ["sourceBundle"] = "author:" + targetPrefab, ["bundleOffset"] = 0,
            ["snapshot"] = new JObject { ["file"] = "source/serialized/" + cab, ["size"] = nativeBytes.Length, ["sha256"] = EiemEffectBuildCommand.Hash(nativeBytes) },
            ["unityVersion"] = outputFile.unityVersion, ["platform"] = outputFile.m_TargetPlatform.ToString(),
            ["header"] = new JObject { ["fileSize"] = outputFile.header.m_FileSize, ["dataOffset"] = outputFile.header.m_DataOffset,
                ["metadataSize"] = outputFile.header.m_MetadataSize, ["version"] = (int)outputFile.header.m_Version, ["endian"] = outputFile.header.m_Endianess },
            ["externals"] = new JArray(),
            ["types"] = new JArray(outputFile.m_Types.Select((t, index) => new JObject
            {
                ["index"] = index, ["classId"] = t.classID, ["stripped"] = t.m_IsStrippedType,
                ["scriptTypeIndex"] = t.m_ScriptTypeIndex, ["scriptId"] = "", ["oldTypeHash"] = "",
                ["typeTreeNodes"] = t.m_Type?.m_Nodes?.Count ?? 0, ["dependencies"] = new JArray(t.m_TypeDependencies ?? Array.Empty<int>())
            })),
            ["objects"] = new JArray(outputFile.m_Objects.Select(row => new JObject
            {
                ["pathId"] = row.m_PathID, ["offset"] = row.byteStart, ["size"] = row.byteSize,
                ["typeIndex"] = row.typeID, ["classId"] = row.classID
            }))
        };
        ((JArray)manifest["serializedFiles"]).Add(serializedRow);

        var catalog = new JObject
        {
            ["format"] = "EFF_EFFECT_SOURCE", ["version"] = 1, ["prefab"] = targetPrefab, ["root"] = rootIdentity,
            ["vfsFingerprint"] = (string)donor["vfsFingerprint"], ["baseline"] = "source/manifest.json",
            ["hierarchy"] = hierarchy, ["objects"] = effectObjects,
            ["schemas"] = new JArray(schemaPayloads.Keys.Order(StringComparer.Ordinal).Select(path => new JObject
            {
                ["file"] = path, ["canonicalSha256"] = Path.GetFileNameWithoutExtension(path)
            })),
            ["warnings"] = new JArray("New particle systems have no material until a game logical Material is assigned."),
            ["preview"] = new JObject { ["format"] = "EFF_EFFECT_PREVIEW", ["version"] = 1,
                ["basis"] = "unity-y-up-left-handed", ["resources"] = new JArray() },
            ["capabilities"] = new JObject { ["nativePreservation"] = true, ["strictObjectRoundtrip"] = true,
                ["nativePackRebuild"] = true, ["blenderImport"] = true, ["gamePlayback"] = false },
            ["notes"] = new JArray("Author-created particle Prefab; TypeTrees are borrowed from the selected game-version schema donor.",
                "Assign a game logical Material before expecting visible in-game particles.")
        };

        EiemPackagePublication.Publish(output, staged =>
        {
            Directory.CreateDirectory(Path.Combine(staged, "objects"));
            Directory.CreateDirectory(Path.Combine(staged, "schemas"));
            Directory.CreateDirectory(Path.Combine(staged, "source/serialized"));
            File.WriteAllBytes(Path.Combine(staged, "source/serialized", cab), nativeBytes);
            foreach (var schema in schemaPayloads)
                File.WriteAllText(Path.Combine(staged, schema.Key.Replace('/', Path.DirectorySeparatorChar)),
                    schema.Value.ToString(Formatting.Indented), new UTF8Encoding(false));
            foreach (var row in effectObjects)
            {
                var spec = authored.Single(a => Identity(cab, a.Id) == (string)row["identity"]);
                File.WriteAllText(Path.Combine(staged, ((string)row["data"]).Replace('/', Path.DirectorySeparatorChar)),
                    spec.Document.Root.ToJson().ToString(Formatting.Indented), new UTF8Encoding(false));
            }
            File.WriteAllText(Path.Combine(staged, "effect.json"), catalog.ToString(Formatting.Indented), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(staged, "source/manifest.json"), manifest.ToString(Formatting.Indented), new UTF8Encoding(false));
        });
    }

    private static void InitializeParticle(JObject data, long gameObjectId)
    {
        Set(data, "m_GameObject", Pointer(gameObjectId));
        Set(data, "lengthInSec", new JValue(5f));
        Set(data, "simulationSpeed", new JValue(1f));
        Set(data, "cullingMode", new JValue(3));
        Set(data, "looping", new JValue(true));
        Set(data, "prewarm", new JValue(false));
        Set(data, "playOnAwake", new JValue(true));
        foreach (var module in data.Properties().Where(p => p.Name.EndsWith("Module", StringComparison.Ordinal) && p.Value is JObject))
            if (module.Value["enabled"] != null) module.Value["enabled"] = module.Name is "InitialModule" or "ShapeModule" or "EmissionModule";
        Set(data, "InitialModule.maxNumParticles", new JValue(100));
        Set(data, "InitialModule.startLifetime.minMaxState", new JValue(0));
        Set(data, "InitialModule.startLifetime.scalar", new JValue(1.5f));
        Set(data, "InitialModule.startSize.minMaxState", new JValue(0));
        Set(data, "InitialModule.startSize.scalar", new JValue(0.15f));
        Set(data, "InitialModule.startSpeed.minMaxState", new JValue(0));
        Set(data, "InitialModule.startSpeed.scalar", new JValue(0f));
        Set(data, "InitialModule.startColor.minMaxState", new JValue(0));
        Set(data, "InitialModule.startColor.minColor", Color(1, 1, 1, 1));
        Set(data, "InitialModule.startColor.maxColor", Color(1, 1, 1, 1));
        Set(data, "EmissionModule.rateOverTime.minMaxState", new JValue(0));
        Set(data, "EmissionModule.rateOverTime.scalar", new JValue(8f));
        Set(data, "EmissionModule.rateOverDistance.minMaxState", new JValue(0));
        Set(data, "EmissionModule.rateOverDistance.scalar", new JValue(0f));
        Set(data, "EmissionModule.m_BurstCount", new JValue(0));
        Set(data, "EmissionModule.m_Bursts", new JArray());
        Set(data, "ShapeModule.m_Scale", Vector(1, 1, 1));
    }

    private static void InitializeBundle(JObject data, string targetPrefab, long rootId, IReadOnlyList<long> preload)
    {
        Set(data, "m_Name", new JValue(targetPrefab));
        Set(data, "m_PreloadTable", new JArray(preload.Select(Pointer)));
        Set(data, "m_Container", new JArray(new JObject
        {
            ["first"] = targetPrefab,
            ["second"] = new JObject
            {
                ["preloadIndex"] = 0, ["preloadSize"] = preload.Count, ["asset"] = Pointer(rootId)
            }
        }));
    }

    private static JObject Schema(TypeTree tree, IEnumerable<SerializedType> referenceTypes) => new()
    {
        ["format"] = "EFF_TYPE_TREE", ["version"] = 1,
        ["nodes"] = new JArray(tree.m_Nodes.Select(n => new JObject
        {
            ["type"] = n.m_Type, ["name"] = n.m_Name, ["level"] = n.m_Level,
            ["byteSize"] = n.m_ByteSize, ["metaFlag"] = n.m_MetaFlag, ["version"] = n.m_Version,
            ["typeFlags"] = n.m_TypeFlags, ["refTypeHash"] = n.m_RefTypeHash.ToString(System.Globalization.CultureInfo.InvariantCulture)
        })),
        ["managedTypes"] = new JArray((referenceTypes ?? Array.Empty<SerializedType>()).Select(t => new JObject
        {
            ["class"] = t.m_KlassName, ["namespace"] = t.m_NameSpace, ["assembly"] = t.m_AsmName,
            ["nodes"] = new JArray(t.m_Type.m_Nodes.Select(n => new JObject
            {
                ["type"] = n.m_Type, ["name"] = n.m_Name, ["level"] = n.m_Level,
                ["byteSize"] = n.m_ByteSize, ["metaFlag"] = n.m_MetaFlag, ["version"] = n.m_Version,
                ["typeFlags"] = n.m_TypeFlags, ["refTypeHash"] = n.m_RefTypeHash.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }))
        }))
    };

    private static JArray References(TypeTreeDocument document, string cab, SerializedFile file)
    {
        var objects = file.m_Objects.ToDictionary(o => o.m_PathID);
        return new JArray(document.Root.Walk().Where(row => row.Value.Schema.Type.StartsWith("PPtr<", StringComparison.Ordinal)).Select(row =>
        {
            int fileId = Convert.ToInt32(row.Value["m_FileID"].Scalar);
            long pathId = Convert.ToInt64(row.Value["m_PathID"].Scalar);
            bool isNull = pathId == 0;
            bool resolved = !isNull && fileId == 0 && objects.ContainsKey(pathId);
            return new JObject
            {
                ["field"] = row.Path, ["fileId"] = fileId, ["pathId"] = pathId,
                ["cab"] = isNull ? null : fileId == 0 ? cab : null, ["isNull"] = isNull,
                ["resolved"] = resolved, ["identity"] = resolved ? Identity(cab, pathId) : null,
                ["type"] = resolved ? ((ClassIDType)objects[pathId].classID).ToString() : null
            };
        }));
    }

    private static JArray ParticleModules(TypeTreeDocument document) => new(document.Root.Fields
        .Where(p => p.Key.EndsWith("Module", StringComparison.Ordinal))
        .Select(p => new JObject { ["field"] = p.Key,
            ["enabled"] = p.Value.Fields.TryGetValue("enabled", out var enabled) ? JToken.FromObject(Convert.ToBoolean(enabled.Scalar)) : JValue.CreateNull(),
            ["offset"] = p.Value.Offset, ["size"] = p.Value.Length }));

    private static object NativeRange(SerializedFile file, long id)
    {
        var row = file.m_Objects.Single(o => o.m_PathID == id);
        return new { offset = row.byteStart, size = row.byteSize };
    }

    private static string NormalizePrefab(string value)
    {
        string path = (value ?? "").Replace('\\', '/').Trim();
        if (!path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ||
            path.Split('/').Any(p => p is "" or "." or "..") || path.Contains(':'))
            throw new InvalidDataException("Target Prefab must be a safe logical assets/... .prefab path");
        return path;
    }

    private static string NormalizeName(string value)
    {
        string name = (value ?? "").Trim();
        if (name.Length == 0 || name.Length > 128 || name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
            throw new InvalidDataException("Root name must be a nonempty GameObject name up to 128 characters");
        return name;
    }

    private static byte[] ReadSnapshot(string root, JToken snapshot)
    {
        string relative = (string)snapshot["file"];
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') ||
            relative.Split('/').Any(part => part is "" or "." or "..")) throw new InvalidDataException("Unsafe TypeTree donor snapshot path");
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("TypeTree donor snapshot escapes workspace");
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.LongLength != (long)snapshot["size"] || EiemEffectBuildCommand.Hash(bytes) != (string)snapshot["sha256"])
            throw new InvalidDataException("TypeTree donor snapshot hash mismatch: " + relative);
        return bytes;
    }

    private static string Identity(string cab, long id) => cab.ToLowerInvariant() + ":" + id;
    private static JObject Pointer(long id) => new() { ["m_FileID"] = 0, ["m_PathID"] = id };
    private static string PointerText(string cab, long id) => id == 0 ? null : Identity(cab, id);
    private static JObject Vector(float x, float y, float z) => new() { ["x"] = x, ["y"] = y, ["z"] = z };
    private static JObject Quaternion(float x, float y, float z, float w) => new() { ["x"] = x, ["y"] = y, ["z"] = z, ["w"] = w };
    private static JObject Color(float r, float g, float b, float a) => new() { ["r"] = r, ["g"] = g, ["b"] = b, ["a"] = a };

    private static void Set(JObject target, string path, JToken value)
    {
        string[] fields = path.Split('.');
        JObject current = target;
        foreach (string field in fields.Take(fields.Length - 1))
            current = current[field] as JObject ?? throw new InvalidDataException("TypeTree donor lacks " + path);
        if (current.Property(fields[^1]) == null) throw new InvalidDataException("TypeTree donor lacks " + path);
        current[fields[^1]] = value;
    }
}
