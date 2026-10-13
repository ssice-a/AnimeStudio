using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AnimeStudio.GUI
{
    // The eidx wire format remains unchanged. This single sidecar describes
    // contiguous records owned by each logical Bundle, including empty Bundles.
    internal sealed record EndfieldBundleIndexSpan(string Path, string ContentMd5,
        long PayloadLength, long Offset, long Size, long Assets, long Cabs, byte[] Sha256);

    internal sealed class EndfieldBundleIndexCache
    {
        // Bump only when Bundle parsing/record semantics change, not on releases.
        public const string ParserIdentity = "endfield-bundle/asset+container+type+pathid+source+cab-dependencies/1";
        private const string Magic = "EFF_BUNDLE_INDEX_SPANS/1";
        public string Fingerprint { get; init; }
        public long IndexLength { get; init; }
        public long Assets { get; init; }
        public long Cabs { get; init; }
        public Dictionary<string, EndfieldBundleIndexSpan> Spans { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public static string PathFor(string indexPath) => indexPath + ".bundles";

        public static EndfieldBundleIndexCache TryLoad(string indexPath)
        {
            try
            {
                using var file = File.OpenRead(PathFor(indexPath));
                if (file.Length < 32) return null;
                var end = file.Length - 32;
                var buffer = new byte[64 * 1024];
                var digest = HashRange(file, 0, end, buffer, CancellationToken.None);
                var expected = new byte[32];file.ReadExactly(expected);
                if (!digest.SequenceEqual(expected)) return null;
                file.Position = 0;
                using var reader = new BinaryReader(file, Encoding.UTF8, true);
                if (reader.ReadString() != Magic || reader.ReadString() != ParserIdentity) return null;
                var cache = new EndfieldBundleIndexCache {
                    Fingerprint = reader.ReadString(), IndexLength = reader.ReadInt64(),
                    Assets = reader.ReadInt64(), Cabs = reader.ReadInt64()
                };
                int count = reader.ReadInt32();
                if (count < 0 || count > (end - file.Position) / 74) return null;
                for (int i = 0; i < count; i++)
                {
                    var row = new EndfieldBundleIndexSpan(reader.ReadString(), reader.ReadString(),
                        reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadBytes(32));
                    if (row.Sha256.Length != 32 || row.PayloadLength <= 0 || row.ContentMd5.Length != 32 ||
                        row.Size < 0 || row.Assets < 0 || row.Cabs < 0 || !cache.Spans.TryAdd(row.Path, row)) return null;
                }
                if (file.Position != end) return null;
                using var index = File.OpenRead(indexPath);
                if (!ValidateEnvelope(index, cache.Fingerprint, cache.IndexLength, cache.Assets, cache.Cabs, out var headerEnd)) return null;
                long position = headerEnd, assets = 0, cabs = 0;
                foreach (var row in cache.Spans.Values.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
                {
                    if (row.Offset != position || row.Offset > cache.IndexLength - 17 || row.Size > cache.IndexLength - 17 - row.Offset) return null;
                    position += row.Size;assets = checked(assets + row.Assets);cabs = checked(cabs + row.Cabs);
                }
                return position == cache.IndexLength - 17 && assets == cache.Assets && cabs == cache.Cabs ? cache : null;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException || ex is OverflowException) { return null; }
        }

        public void Save(string path)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(Magic);writer.Write(ParserIdentity);writer.Write(Fingerprint);
            writer.Write(IndexLength);writer.Write(Assets);writer.Write(Cabs);writer.Write(Spans.Count);
            foreach (var row in Spans.Values.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
            {
                writer.Write(row.Path);writer.Write(row.ContentMd5);writer.Write(row.PayloadLength);
                writer.Write(row.Offset);writer.Write(row.Size);writer.Write(row.Assets);writer.Write(row.Cabs);writer.Write(row.Sha256);
            }
            writer.Flush();long length = stream.Position;
            var digest = HashRange(stream, 0, length, new byte[64 * 1024], CancellationToken.None);
            stream.Position = length;writer.Write(digest);writer.Flush();stream.Flush(true);
        }

        internal static bool ValidateEnvelope(Stream index, string fingerprint, long length, long assets, long cabs, out long headerEnd)
        {
            headerEnd = 0;if (index.Length != length || length < 17) return false;
            index.Position = 0;using var reader = new BinaryReader(index, Encoding.UTF8, true);
            if (ReadHeader(reader) != fingerprint) return false;
            headerEnd = index.Position;if (headerEnd > length - 17) return false;
            index.Position = length - 17;
            return reader.ReadByte() == 0 && reader.ReadInt64() == assets && reader.ReadInt64() == cabs;
        }

        internal static string ReadHeader(BinaryReader reader)
        {
            var magic = reader.ReadString();
            if (magic != "EFF_END_FIELD_INDEX" && magic != "EIEM_END_FIELD_INDEX") throw new InvalidDataException("Not an Endfield index");
            if (reader.ReadInt32() != EndfieldIndexStore.FormatVersion) throw new InvalidDataException("Index record schema changed");
            return reader.ReadString();
        }

        internal static byte[] HashRange(Stream file, long offset, long size, byte[] buffer, CancellationToken token, Stream copy = null)
        {
            file.Position = offset;using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (size > 0)
            {
                token.ThrowIfCancellationRequested();int n = file.Read(buffer, 0, (int)Math.Min(size, buffer.Length));
                if (n == 0) throw new EndOfStreamException("Truncated Bundle index records");
                hash.AppendData(buffer, 0, n);copy?.Write(buffer, 0, n);size -= n;
            }
            return hash.GetHashAndReset();
        }

        // Seed a compatible current index, or recover a checkpoint prefix, by
        // reading compact records only. No game payloads or Unity parsing.
        internal static EndfieldBundleIndexCache Scan(string indexPath, string fingerprint,
            EndfieldVfsEntry[] bundles, long length, long expectedAssets, long expectedCabs,
            bool complete, CancellationToken token)
        {
            using var file = File.OpenRead(indexPath);using var reader = new BinaryReader(file, Encoding.UTF8, true);
            if (length > file.Length || ReadHeader(reader) != fingerprint) throw new InvalidDataException("Index baseline differs");
            var recordEnd = complete ? length - 17 : length;
            var ranges = new Dictionary<string, (long Offset, long End, long Assets, long Cabs)>(StringComparer.OrdinalIgnoreCase);
            var paths = bundles.ToDictionary(b => b.LogicalPath, StringComparer.OrdinalIgnoreCase);
            string previous = null;long assets = 0, cabs = 0, headerEnd = file.Position;
            while (file.Position < recordEnd)
            {
                token.ThrowIfCancellationRequested();long start = file.Position;byte kind = reader.ReadByte();string source;
                if (kind == 1) { reader.ReadString();reader.ReadString();reader.ReadString();reader.ReadInt64();source = reader.ReadString();assets++; }
                else if (kind == 2)
                {
                    reader.ReadString();source = reader.ReadString();int count = reader.ReadInt32();
                    if (count < 0 || count > recordEnd - file.Position) throw new InvalidDataException("CAB dependency extent");
                    for (int i = 0; i < count; i++) reader.ReadString();cabs++;
                }
                else throw new InvalidDataException("Unknown Bundle index record");
                if (file.Position > recordEnd || !paths.ContainsKey(source) ||
                    (previous != null && StringComparer.OrdinalIgnoreCase.Compare(previous, source) > 0))
                    throw new InvalidDataException("Bundle records are not a complete ordered baseline");
                if (!ranges.TryGetValue(source, out var row)) row = (start, start, 0, 0);
                ranges[source] = (row.Offset, file.Position, row.Assets + (kind == 1 ? 1 : 0), row.Cabs + (kind == 2 ? 1 : 0));previous = source;
            }
            if (file.Position != recordEnd || assets != expectedAssets || cabs != expectedCabs ||
                (complete && !ValidateEnvelope(file, fingerprint, length, assets, cabs, out _)))
                throw new InvalidDataException("Incomplete Bundle index baseline");
            var cache = new EndfieldBundleIndexCache { Fingerprint = fingerprint, IndexLength = length, Assets = assets, Cabs = cabs };
            long position = headerEnd;var buffer = new byte[64 * 1024];
            foreach (var bundle in bundles)
            {
                token.ThrowIfCancellationRequested();ranges.TryGetValue(bundle.LogicalPath, out var row);
                long size = row.End - row.Offset;
                if (size != 0 && row.Offset != position) throw new InvalidDataException("Noncontiguous Bundle records");
                cache.Spans.Add(bundle.LogicalPath, new(bundle.LogicalPath, bundle.FileDataMd5, bundle.Length,
                    position, size, row.Assets, row.Cabs, HashRange(file, position, size, buffer, token)));
                position += size;
            }
            if (position != recordEnd) throw new InvalidDataException("Unowned index records");
            return cache;
        }
    }
}
