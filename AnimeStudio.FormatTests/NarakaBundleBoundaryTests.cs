using System.Buffers.Binary;
using System.Text;
using AnimeStudio;

internal static class NarakaBundleBoundaryTests
{
    internal static void Run()
    {
        // The inflated private metadata size crosses a 4 KiB page. Its tail and
        // the beginning of the data page also decode as LZ4, so schema validation
        // alone accepts a false boundary one page after the actual data start.
        var bytes = Fixture();
        var path = Path.Combine(Path.GetTempPath(), "anime-naraka-boundary-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, bytes);
        try
        {
            foreach (var lazy in new[] { true, false })
            {
                using var reader = new FileReader(path);
                var bundle = new BundleFile(reader, GameManager.GetGameByType(GameType.Naraka), lazyIndex: lazy);
                try
                {
                    var actual = new byte[256];
                    bundle.fileList.Single().stream.ReadExactly(actual);
                    if (!actual.AsSpan().SequenceEqual(bytes.AsSpan(0x2000, 256)))
                        throw new InvalidDataException("Naraka metadata padding moved the data start to another page");
                }
                finally { foreach (var file in bundle.fileList) file.stream.Dispose(); }
            }
            File.WriteAllBytes(path, bytes.AsSpan(0, 0x2080).ToArray());
            try
            {
                using var reader = new FileReader(path);
                var invalid = new BundleFile(reader, GameManager.GetGameByType(GameType.Naraka), lazyIndex: true);
                foreach (var file in invalid.fileList) file.stream.Dispose();
                throw new Exception("Truncated Naraka data extent was accepted");
            }
            catch (InvalidDataException) { }
            Console.WriteLine("PASS Naraka metadata boundary across pages, eager/lazy parity and truncated block rejection");
        }
        finally { File.Delete(path); }
    }

    private static byte[] Fixture()
    {
        var metadata = new byte[0xFE0];
        var position = 16;
        void U32(uint value) { BinaryPrimitives.WriteUInt32BigEndian(metadata.AsSpan(position), value); position += 4; }
        void U64(ulong value) { BinaryPrimitives.WriteUInt64BigEndian(metadata.AsSpan(position), value); position += 8; }
        U32(1); U32(256); U32(256);
        BinaryPrimitives.WriteUInt16BigEndian(metadata.AsSpan(position), 0); position += 2;
        U32(1); U64(0); U64(256); U32(4);
        Encoding.UTF8.GetBytes("CAB-boundary-fixture").CopyTo(metadata, position);
        using var encoded = new MemoryStream();
        encoded.WriteByte(0xF0);
        var length = metadata.Length - 15;
        while (length >= 255) { encoded.WriteByte(255); length -= 255; }
        encoded.WriteByte((byte)length);
        encoded.Write(metadata);
        var payload = encoded.ToArray();
        var bytes = new byte[0x3000];
        using var header = new MemoryStream(bytes, writable: true);
        header.Write(new byte[] { 0x15, 0x1E, 0x1C, 0x0D, 0x0D, 0x23, 0x21, 0 });
        void H32(uint value) { var buffer = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(buffer, value); header.Write(buffer); }
        H32(7);
        header.Write(Encoding.UTF8.GetBytes("5.x.x\0"));
        header.Write(Encoding.UTF8.GetBytes("2019.4.14f1\0"));
        // Naraka's stored bundle size is biased and can be smaller than its
        // data extent. The physical stream, not this field, bounds the blocks.
        var size = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(size, 0x2077); header.Write(size);
        H32(0x1066); H32((uint)metadata.Length + 0x82); H32(0x246);
        payload.CopyTo(bytes, 0x1000);
        // offset=1, then token=0, repeated: legal overlapping zero matches that
        // make the private tail look like an extension of the metadata block.
        for (var i = 0x1000 + payload.Length; i < bytes.Length; i++)
            bytes[i] = (byte)((i - 0x1000 - payload.Length) % 3 == 0 ? 1 : 0);
        return bytes;
    }
}
