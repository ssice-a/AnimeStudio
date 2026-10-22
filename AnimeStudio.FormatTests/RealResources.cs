using AnimeStudio;
using System.Collections.Specialized;

internal static class RealResources
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static KeyValuePair<object, object>[] Pairs(object value) =>
        ((IEnumerable<KeyValuePair<object, object>>)value).ToArray();

    private static float Number(object value) => Convert.ToSingle(value);

    // Compare the specialized readers against the independent schema carried in
    // the installed game files. No game files are copied into the test project.
    public static void Run(string[] paths)
    {
        var game = GameManager.GetGameByType(GameType.Naraka);
        foreach (var path in paths)
        {
            var manager = new AssetsManager { Game = game, SkipProcess = true, ResolveDependencies = false };
            var failures = new List<Exception>();
            try
            {
                manager.LoadFiles(new[] { path }, mergeSplitAssets: false);
                Check(manager.assetsFileList.Count > 0, "No serialized file in " + path);
                int materials = 0, meshes = 0, splitPayloads = 0;
                foreach (var file in manager.assetsFileList)
                foreach (var info in file.m_Objects.Where(x => x.classID is 21 or 43))
                {
                    Check(info.serializedType.m_Type?.m_Nodes?.Count > 0, "A TypeTree is required for the comparison");
                    var reader = new ObjectReader(file.reader, file, info, game);
                    reader.Reset();
                    var expected = TypeTreeHelper.ReadType(info.serializedType.m_Type, reader);
                    Check(reader.BytesLeft() == 0, $"Schema read did not end at object boundary: {info.m_PathID}");
                    try
                    {
                    if (info.classID == 21)
                    {
                        var material = new Material(reader);
                        Check(material.Name == (string)expected["m_Name"], "Material name mismatch");
                        CompareProperties(material, (OrderedDictionary)expected["m_SavedProperties"]);
                        materials++;
                    }
                    else
                    {
                        var mesh = new Mesh(reader);
                        Check(mesh.Name == (string)expected["m_Name"], "Mesh name mismatch");
                        var vertex = (OrderedDictionary)expected["m_VertexData"];
                        var count = Convert.ToInt32(vertex["m_VertexCount"]);
                        Check(count == 0 || mesh.m_VertexCount == count, "Mesh vertex count mismatch");
                        Check(mesh.m_VertexCount == 0 || mesh.m_Vertices?.Length >= mesh.m_VertexCount * 3,
                            "Mesh positions missing");
                        Check(mesh.m_Vertices == null || mesh.m_Vertices.All(float.IsFinite), "Non-finite vertex position");
                        Check(mesh.m_Normals == null || mesh.m_Normals.All(float.IsFinite), "Non-finite normal");
                        Check(mesh.m_Tangents == null || mesh.m_Tangents.All(float.IsFinite), "Non-finite tangent");
                        Check(mesh.m_Indices.All(index => index < mesh.m_VertexCount), "Triangle index exceeds vertex count");
                        var stream = (OrderedDictionary)expected["m_StreamData"];
                        Check(mesh.StreamData.path == (string)stream["path"] &&
                              mesh.StreamData.offset == Convert.ToInt64(stream["offset"]) &&
                              mesh.StreamData.size == Convert.ToUInt32(stream["size"]), "Mesh resource reference mismatch");
                        var compressed = (OrderedDictionary)expected["m_CompressedMesh"];
                        foreach (var field in new[] { "m_VerticesY", "m_VerticesZ", "m_LightmapUV" })
                            if (compressed.Contains(field) &&
                                Convert.ToUInt32(((OrderedDictionary)compressed[field])["m_NumItems"]) > 0)
                                splitPayloads++;
                        meshes++;
                    }
                    Check(reader.BytesLeft() == 0, $"Specialized reader did not end at object boundary: {info.m_PathID}");
                    }
                    catch (Exception error)
                    {
                        var detail = $"{file.fileName}:{info.m_PathID} {expected["m_Name"]}: {error.Message}";
                        failures.Add(new InvalidDataException(detail, error));
                        if (failures.Count <= 3)
                        {
                            Console.Error.WriteLine("FAIL real " + detail);
                            var dump = Path.Combine(Path.GetTempPath(), $"anime-format-failure-{info.m_PathID}.json");
                            File.WriteAllText(dump, Newtonsoft.Json.JsonConvert.SerializeObject(expected, Newtonsoft.Json.Formatting.Indented));
                            Console.Error.WriteLine("Schema: " + dump);
                        }
                    }
                }
                Console.WriteLine($"RESULT real {Path.GetFileName(path)}: materials={materials}, meshes={meshes}, nonemptySplitPayloads={splitPayloads}, failures={failures.Count}");
                if (failures.Count > 0) throw new AggregateException(failures);
            }
            finally { manager.Clear(); }
        }
    }

    private static void CompareProperties(Material material, OrderedDictionary expected)
    {
        var properties = material.m_SavedProperties;
        var textures = Pairs(expected["m_TexEnvs"]);
        Check(properties.m_TexEnvs.Count == textures.Length, "Material texture count mismatch");
        for (var i = 0; i < textures.Length; i++)
        {
            var actual = properties.m_TexEnvs[i];
            var env = (OrderedDictionary)textures[i].Value;
            var reference = (OrderedDictionary)env["m_Texture"];
            var scale = (OrderedDictionary)env["m_Scale"];
            var offset = (OrderedDictionary)env["m_Offset"];
            Check(actual.Key == (string)textures[i].Key &&
                  actual.Value.m_Texture.m_FileID == Convert.ToInt32(reference["m_FileID"]) &&
                  actual.Value.m_Texture.m_PathID == Convert.ToInt64(reference["m_PathID"]) &&
                  actual.Value.m_Scale.X.Equals(Number(scale["x"])) &&
                  actual.Value.m_Scale.Y.Equals(Number(scale["y"])) &&
                  actual.Value.m_Offset.X.Equals(Number(offset["x"])) &&
                  actual.Value.m_Offset.Y.Equals(Number(offset["y"])), "Material texture binding/UV mismatch");
        }
        var floats = Pairs(expected["m_Floats"]);
        Check(properties.m_Floats.Count == floats.Length, "Material float count mismatch");
        for (var i = 0; i < floats.Length; i++)
            Check(properties.m_Floats[i].Key == (string)floats[i].Key &&
                  properties.m_Floats[i].Value.Equals(Number(floats[i].Value)), "Material float mismatch");
        var colors = Pairs(expected["m_Colors"]);
        Check(properties.m_Colors.Count == colors.Length, "Material color count mismatch");
        for (var i = 0; i < colors.Length; i++)
        {
            var actual = properties.m_Colors[i];
            var color = (OrderedDictionary)colors[i].Value;
            Check(actual.Key == (string)colors[i].Key &&
                  actual.Value.R.Equals(Number(color["r"])) && actual.Value.G.Equals(Number(color["g"])) &&
                  actual.Value.B.Equals(Number(color["b"])) && actual.Value.A.Equals(Number(color["a"])),
                "Material color mismatch");
        }
    }
}
