using System.Security.Cryptography;
using System.Text;
using System.Reflection;
using AnimeStudio.GUI;

internal static class VfsFormatTests
{
    private delegate void Cipher(Span<byte> data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter);
    private static readonly Cipher Encrypt = typeof(EndfieldVfsArchive).GetMethod("ChaCha20Xor",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.CreateDelegate<Cipher>();

    private static void RejectImpossibleCount(string methodName)
    {
        using var stream = new MemoryStream(new byte[4]);
        using var reader = new AnimeStudio.EndianBinaryReader(stream);
        var vfsUtils = typeof(AnimeStudio.Game).Assembly.GetType("AnimeStudio.VFSUtils", throwOnError: true)!;
        var method = vfsUtils.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!;
        try
        {
            method.Invoke(null, new object[] { reader, AnimeStudio.GameType.ArknightsEndfield });
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException)
        {
            return;
        }
        catch (TargetInvocationException error)
        {
            throw new Exception($"{methodName} did not reject an impossible metadata count with InvalidDataException", error.InnerException);
        }
        throw new Exception($"{methodName} accepted an impossible metadata count");
    }

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "eff-blc-formats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var code in new[] { 3, 4, 5 })
            {
                var dir = Path.Combine(root, code.ToString());Directory.CreateDirectory(dir);
                using var data = new MemoryStream();using var writer = new BinaryWriter(data, Encoding.ASCII, true);
                void Name(string value) { var bytes = Encoding.ASCII.GetBytes(value);writer.Write((ushort)bytes.Length);writer.Write(bytes); }
                void Tag() { if (code == 5) writer.Write(0xFEDCBA9876543210UL);else if (code == 4) writer.Write(0x76543210U); }
                // Legacy v3 writes the block version directly, while v4/v5 have
                // an explicit code version. Two chunks expose shifted counts.
                writer.Write(code == 3 ? 100 : code);if (code > 3) writer.Write(100);
                Name("Data/Bundles/Windows/main");writer.Write(1UL);writer.Write(4);writer.Write(4L);writer.Write((byte)0);writer.Write(2);
                for (var chunk = 0; chunk < 2; chunk++)
                {
                    byte[] payload = [(byte)(chunk + 1), (byte)(chunk + 3)];var digest = MD5.HashData(payload);
                    writer.Write(digest);writer.Write(digest);writer.Write(2L);writer.Write((byte)0);Tag();writer.Write(2);
                    File.WriteAllBytes(Path.Combine(dir, Convert.ToHexString(digest) + ".chk"), payload);
                    for (var file = 0; file < 2; file++)
                    {
                        Name($"Data/Bundles/Windows/main/{chunk}-{file}.ab");writer.Write(0x123456789ABCDL);
                        writer.Write(digest);writer.Write(MD5.HashData([payload[file]]));writer.Write((long)file);writer.Write(1L);
                        writer.Write((byte)11);writer.Write((byte)(file == 1 ? 1 : 0));
                        if (file == 1) writer.Write(0x1122334455667788L);
                        Tag();
                    }
                }
                writer.Write(0);writer.Flush();var plain = data.ToArray();byte[] nonce = new byte[12];
                Encrypt(plain, Convert.FromHexString("E95B317AC4F828569D23A86BF271DCB53E846FA75C924D671DBA8E38F4CA52E1"), nonce, 1);
                File.WriteAllBytes(Path.Combine(dir, "test.blc"), nonce.Concat(plain).ToArray());
                var archive = EndfieldVfsArchive.OpenLayer(dir);
                if (archive.Entries.Count != 4) throw new Exception($"BLC v{code}: wrong entry count");
                for (var chunk = 0; chunk < 2; chunk++) for (var file = 0; file < 2; file++)
                {
                    var entry = archive.Entries[$"Bundles/Windows/main/{chunk}-{file}.ab"];
                    if (entry.Offset != file || entry.Length != 1 || entry.FileType != 11 || entry.Encrypted != (file == 1) ||
                        entry.IvSeed != (file == 1 ? 0x1122334455667788L : 0) || entry.FileNameHash != 0x123456789ABCDL)
                        throw new Exception($"BLC v{code}: entry metadata shifted");
                    if (file == 0 && archive.ReadPayload(entry.LogicalPath)[0] != chunk + 1)
                        throw new Exception($"BLC v{code}: wrong CHK range");
                }
                if (code == 5)
                {
                    var game = Path.Combine(root, "layered-game");
                    var baseVfs = Path.Combine(game, "Endfield_Data", "StreamingAssets", "VFS");
                    var hotVfs = Path.Combine(game, "Endfield_Data", "Persistent", "VFS");
                    Directory.CreateDirectory(baseVfs);Directory.CreateDirectory(hotVfs);
                    File.Copy(Path.Combine(dir, "test.blc"), Path.Combine(baseVfs, "test.blc"));
                    File.Copy(Path.Combine(dir, "test.blc"), Path.Combine(hotVfs, "test.blc"));
                    foreach (var chunkPath in Directory.EnumerateFiles(dir, "*.chk"))
                        File.Copy(chunkPath, Path.Combine(baseVfs, Path.GetFileName(chunkPath)));
                    var layered = EndfieldVfsArchive.Open(game);
                    if (layered.LayerCount != 2 || layered.Entries.Count != 4 || layered.MissingPayloadCount != 0)
                        throw new Exception($"Layered VFS counts were unexpected: layers={layered.LayerCount}, entries={layered.Entries.Count}, missing={layered.MissingPayloadCount}");
                    if (layered.Entries.Values.Any(entry => !entry.SourceChk.StartsWith(baseVfs, StringComparison.OrdinalIgnoreCase)))
                        throw new Exception("Layered VFS merge did not redirect fallback payloads to the base CHK: " +
                            string.Join("; ", layered.Entries.Values.Select(entry => entry.LogicalPath + " => " + entry.SourceBlc)));
                    if (layered.ReadPayload("Bundles/Windows/main/0-0.ab")[0] != 1)
                        throw new Exception("Layered VFS merge selected the wrong payload");
                }
            }
            RejectImpossibleCount("ReadBlocksInfos");
            RejectImpossibleCount("ReadDirectoryInfos");
            Console.WriteLine("PASS BLC v3/v4/v5, layered CHK fallback, and malformed VFS count bounds");
        }
        finally { Directory.Delete(root, true); }
    }
}
