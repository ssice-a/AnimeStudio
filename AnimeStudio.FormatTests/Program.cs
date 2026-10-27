using AnimeStudio;
using System.Runtime.CompilerServices;
using System.Text;

try
{
    Logger.Silent = true;
    FormatTests.Run();
    if (args.Length > 0) RealResources.Run(args);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("FORMAT-TESTS failed: " + error);
    return 1;
}

internal static class FormatTests
{
    public static void Run()
    {
        var failures = new List<Exception>();
        foreach (var test in new Action[]
        {
            () => MaterialProperties(GameType.Normal, false),
            () => MaterialProperties(GameType.Naraka, false),
            () => MaterialProperties(GameType.Naraka, true),
            () => StreamedMesh(GameType.Normal, false),
            () => StreamedMesh(GameType.Naraka, true),
            () => StreamedMesh(GameType.Naraka, false),
            () => StreamedMesh(GameType.Naraka, true, true),
            CompressedNarakaMesh,
            ConstantPackedFloats,
            NarakaBundleBoundaryTests.Run,
        })
        {
            try { test(); }
            catch (Exception error) { failures.Add(error); Console.Error.WriteLine("FAIL " + error.Message); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
        Console.WriteLine("PASS material pass lists/properties and streamed mesh vertices/triangles");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void Align(BinaryWriter writer)
    {
        while (writer.BaseStream.Position % 4 != 0) writer.Write((byte)0);
    }

    private static void Text(BinaryWriter writer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write(bytes.Length);
        writer.Write(bytes);
        Align(writer);
    }

    private static void Strings(BinaryWriter writer, params string[] values)
    {
        writer.Write(values.Length);
        foreach (var value in values) Text(writer, value);
    }

    private static ObjectReader Reader(byte[] bytes, AssetsManager manager, int classId,
        string typeHash, string path = "format-fixture.assets")
    {
        // Exercise the actual object constructors without requiring a Unity installation.
        var file = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
        file.assetsManager = manager;
        file.fullName = Path.GetFullPath(path);
        file.fileName = Path.GetFileName(path);
        file.version = new[] { 2019, 4, 41, 2 };
        file.buildType = new BuildType("f");
        file.m_TargetPlatform = BuildTarget.StandaloneWindows64;
        file.header = new SerializedFileHeader { m_Version = (SerializedFileFormatVersion)17 };
        var type = new SerializedType { classID = classId, m_OldTypeHash = Convert.FromHexString(typeHash) };
        var info = new ObjectInfo { classID = classId, m_PathID = 1, byteSize = (uint)bytes.Length, serializedType = type };
        var binary = new EndianBinaryReader(new MemoryStream(bytes), EndianType.LittleEndian);
        return new ObjectReader(binary, file, info, manager.Game);
    }

    private static void MaterialProperties(GameType game, bool populatedPasses)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        Text(writer, "flowing-cloth");
        writer.Write(2); writer.Write(123456789L); // Shader reference.
        Text(writer, "_UV_DISTORT_ON");
        writer.Write(0u); writer.Write(false); writer.Write(true); Align(writer);
        writer.Write(3000);
        writer.Write(1); Text(writer, "RenderType"); Text(writer, "Transparent");
        Strings(writer, populatedPasses ? new[] { "MOTIONVECTORS", "ShadowCaster" } : Array.Empty<string>());
        if (game == GameType.Naraka) Strings(writer, populatedPasses ? new[] { "DepthOnly" } : Array.Empty<string>());
        writer.Write(1); Text(writer, "_MainTexture");
        writer.Write(3); writer.Write(987654321L);
        writer.Write(2f); writer.Write(3f); writer.Write(0.25f); writer.Write(-0.5f);
        writer.Write(2); Text(writer, "_UV_DISTORT"); writer.Write(1f);
        Text(writer, "_ScrollSpeed"); writer.Write(-0.1f);
        writer.Write(1); Text(writer, "_Tint");
        writer.Write(0.25f); writer.Write(0.5f); writer.Write(0.75f); writer.Write(1f);
        var manager = new AssetsManager { Game = GameManager.GetGameByType(game) };
        using var reader = Reader(stream.ToArray(), manager, 21,
            game == GameType.Naraka ? "473B0755862575D404112DDD15B45EA5" : new string('0', 32));
        var material = new Material(reader);
        var texture = material.m_SavedProperties.m_TexEnvs.Single();
        Check(texture.Key == "_MainTexture" && texture.Value.m_Texture.m_FileID == 3 &&
              texture.Value.m_Texture.m_PathID == 987654321L, "Material texture reference changed");
        Check(texture.Value.m_Scale.X == 2f && texture.Value.m_Offset.Y == -0.5f, "Material UV transform changed");
        Check(material.m_SavedProperties.m_Floats.Single(x => x.Key == "_ScrollSpeed").Value == -0.1f,
            "Material animation parameter changed");
        Check(material.m_SavedProperties.m_Colors.Single().Value.B == 0.75f, "Material tint changed");
        Check(reader.BytesLeft() == 0, "Material reader did not consume the complete object");
        Console.WriteLine($"PASS {game} material, populatedPasses={populatedPasses}");
    }

    private static void PackedFloat(BinaryWriter writer)
    {
        writer.Write(0u); writer.Write(0f); writer.Write(0f); writer.Write(0);
        writer.Write((byte)0); Align(writer);
    }

    private static void PackedInt(BinaryWriter writer)
    {
        writer.Write(0u); writer.Write(0); writer.Write((byte)0); Align(writer);
    }

    private static void PackedFloatValues(BinaryWriter writer, int count, float start, float range,
        byte bits, params byte[] data)
    {
        writer.Write((uint)count); writer.Write(range); writer.Write(start);
        writer.Write(data.Length); writer.Write(data); Align(writer);
        writer.Write(bits); Align(writer);
    }

    private static void PackedIntValues(BinaryWriter writer, int count, byte bits, params byte[] data)
    {
        writer.Write((uint)count); writer.Write(data.Length); writer.Write(data); Align(writer);
        writer.Write(bits); Align(writer);
    }

    private static void ConstantPackedFloats()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        PackedFloatValues(writer, 6, 2.5f, 0, 0);
        var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.Normal) };
        using var reader = Reader(stream.ToArray(), manager, 74, new string('0', 32));
        var packed = new PackedFloatVector(reader);
        Check(packed.UnpackFloats(2, 8).SequenceEqual(Enumerable.Repeat(2.5f, 6)),
            "Zero-bit constant unpack produced incorrect values");
        Check(packed.UnpackFloats(2, 8, 2, 2).SequenceEqual(Enumerable.Repeat(2.5f, 4)),
            "Zero-bit constant slice has incorrect values or length");
        Console.WriteLine("PASS zero-bit packed constants and slices for regular Unity");
    }

    private static void CompressedNarakaMesh()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        Text(writer, "compressed-effect");
        writer.Write(1);
        writer.Write(0u); writer.Write(3u); writer.Write(0); writer.Write(0u);
        writer.Write(0u); writer.Write(3u); writer.Write(new byte[24]);
        for (var i = 0; i < 4; i++) writer.Write(0);
        writer.Write(0); writer.Write(0); writer.Write(0u); writer.Write(0); writer.Write(0);
        writer.Write((byte)1); writer.Write(true); writer.Write(true); writer.Write(true);
        writer.Write(0); writer.Write(0); // Index format and empty inline indices.
        writer.Write(0u); writer.Write(14); writer.Write(new byte[14 * 4]); writer.Write(0);
        PackedFloatValues(writer, 3, 0, 1, 1, 2); // X = 0, 1, 0.
        PackedFloatValues(writer, 6, 0, 1, 1, 36); // UV0.
        PackedFloatValues(writer, 6, 0, 1, 1, 52); // Oct normals: +Z, +X, -Z.
        PackedFloatValues(writer, 6, 0, 1, 1, 21); // Oct tangents: +X.
        PackedInt(writer); PackedInt(writer);
        PackedIntValues(writer, 3, 1, 2); // Tangent handedness: -, +, -.
        PackedFloat(writer); PackedInt(writer);
        PackedIntValues(writer, 3, 2, 36); // Triangle: 0, 1, 2.
        writer.Write(13u);
        PackedFloatValues(writer, 3, 0, 1, 1, 4); // Y = 0, 0, 1.
        PackedFloatValues(writer, 3, 2, 0, 0); // Constant Z, zero-bit packed values.
        PackedFloatValues(writer, 6, 0, 1, 1, 36); // Lightmap UV.
        writer.Write(new byte[24]); writer.Write(0); writer.Write(0); writer.Write(0);
        writer.Write(1f); writer.Write(1f); writer.Write(0u); writer.Write(0u); Text(writer, "");
        var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.Naraka) };
        using var reader = Reader(stream.ToArray(), manager, 43, "6E586577362A6D19119377919A6D1962");
        var mesh = new Mesh(reader);
        Check(mesh.m_VertexCount == 3 && mesh.m_Vertices.SequenceEqual(new[] { 0f, 0f, 2f, 1f, 0f, 2f, 0f, 1f, 2f }),
            "Split XYZ or constant packed positions decoded incorrectly");
        Check(mesh.m_Normals.SequenceEqual(new[] { 0f, 0f, 1f, 1f, 0f, 0f, 0f, 0f, -1f }),
            "Octahedral normals decoded incorrectly");
        Check(mesh.m_Tangents.SequenceEqual(new[] { 1f, 0f, 0f, -1f, 1f, 0f, 0f, 1f, 1f, 0f, 0f, -1f }),
            "Octahedral tangents or handedness decoded incorrectly");
        Check(mesh.m_UV0.SequenceEqual(new[] { 0f, 0f, 1f, 0f, 0f, 1f }) && mesh.m_UV1.SequenceEqual(mesh.m_UV0),
            "Compressed main/lightmap UV decoded incorrectly");
        Check(mesh.m_Indices.SequenceEqual(new uint[] { 0, 1, 2 }) && reader.BytesLeft() == 0,
            "Compressed triangle or object boundary changed");
        Console.WriteLine("PASS Naraka compressed XYZ, octahedral normals/tangents, constant values and lightmap UV");
    }

    private static void StreamedMesh(GameType game, bool splitVertices, bool typeTree = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "anime-format-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var manager = new AssetsManager { Game = GameManager.GetGameByType(game) };
        try
        {
            var vertices = new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f };
            using (var data = new BinaryWriter(File.Create(Path.Combine(root, "fixture.resS"))))
            {
                data.Write(new byte[16]);
                foreach (var value in vertices) data.Write(value);
            }
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            Text(writer, "streamed-cloth");
            writer.Write(1); // Submesh, three triangle indices.
            writer.Write(0u); writer.Write(3u); writer.Write(0); writer.Write(0u);
            writer.Write(0u); writer.Write(3u); writer.Write(new byte[24]);
            for (var i = 0; i < 4; i++) writer.Write(0); // Blend shapes.
            writer.Write(0); writer.Write(0); writer.Write(0u); // Bind poses and bone hashes.
            writer.Write(0); writer.Write(0); // Bone bounds and variable weights.
            writer.Write((byte)0); writer.Write(true); writer.Write(true); writer.Write(true);
            writer.Write(0); writer.Write(6); // UInt16 indices.
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2); Align(writer);
            writer.Write(3u); writer.Write(14); // Vertex count and channels.
            for (var i = 0; i < 14; i++)
            {
                writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((byte)(i == 0 ? 3 : 0));
            }
            writer.Write(0); // Vertex payload is in resS.
            for (var i = 0; i < 4; i++) PackedFloat(writer);
            for (var i = 0; i < 3; i++) PackedInt(writer);
            PackedFloat(writer); PackedInt(writer); PackedInt(writer); writer.Write(0u);
            if (splitVertices)
            {
                PackedFloat(writer); PackedFloat(writer); PackedFloat(writer);
            }
            writer.Write(new byte[24]); writer.Write(0); // Bounds and usage.
            writer.Write(0); writer.Write(0); // Collision payloads.
            writer.Write(1f); writer.Write(1f); // Mesh metrics.
            writer.Write(16u); writer.Write((uint)(vertices.Length * sizeof(float)));
            Text(writer, "fixture.resS");
            using var reader = Reader(stream.ToArray(), manager, 43,
                splitVertices && !typeTree ? "6E586577362A6D19119377919A6D1962" : new string('0', 32),
                Path.Combine(root, "fixture.assets"));
            if (typeTree)
                reader.serializedType.m_Type = new TypeTree
                {
                    m_Nodes = new List<TypeTreeNode> { new() { m_Name = "m_VerticesY", m_Level = 2 } }
                };
            var mesh = new Mesh(reader);
            Check(mesh.m_Vertices.SequenceEqual(vertices), "Streamed mesh positions changed");
            Check(mesh.m_Indices.SequenceEqual(new uint[] { 0, 1, 2 }), "Streamed mesh triangle changed");
            Check(mesh.StreamData.path == "fixture.resS" && mesh.StreamData.offset == 16,
                "Streamed mesh resource reference changed");
            Check(reader.BytesLeft() == 0, "Mesh reader did not consume the complete object");
            Console.WriteLine($"PASS {game} streamed mesh, splitVertices={splitVertices}, typeTree={typeTree}");
        }
        finally
        {
            manager.Clear();
            Directory.Delete(root, true);
        }
    }
}
