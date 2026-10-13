using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeStudio.GUI
{
    internal sealed record EndfieldVfsEntry(
        string LogicalPath,
        string SourceBlc,
        string SourceChk,
        long Offset,
        long Length,
        bool Encrypted,
        long IvSeed,
        byte FileType,
        long FileNameHash,
        string FileDataMd5,
        string ChunkContentMd5 = "");

    internal sealed class EndfieldVfsArchive
    {
        private static readonly byte[] Key = Convert.FromHexString(
            "E95B317AC4F828569D23A86BF271DCB53E846FA75C924D671DBA8E38F4CA52E1");
        private const int ProtocolVersion = 3;
        private const int BlockHeaderLength = 12;

        private readonly Dictionary<string, EndfieldVfsEntry> entries =
            new(StringComparer.OrdinalIgnoreCase);

        public string VfsRoot { get; }
        public string GameDirectory { get; private set; }
        public int LayerCount { get; private set; } = 1;
        public int MissingPayloadCount { get; private set; }
        public string Fingerprint { get; private set; }
        public IReadOnlyDictionary<string, EndfieldVfsEntry> Entries => entries;
        private sealed record Block(int Version, string Source, Dictionary<string, EndfieldVfsEntry> Entries);
        private readonly Dictionary<string, Block> blocks = new(StringComparer.OrdinalIgnoreCase);
        private readonly SortedDictionary<string, string> blcDigests = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> chunkDigests = new(StringComparer.OrdinalIgnoreCase);
        internal const string LayersPrefix = "EFF_VFS_LAYERS_V1:";

        private EndfieldVfsArchive(string vfsRoot)
        {
            VfsRoot = Path.GetFullPath(vfsRoot);
        }

        // Explicit single-layer access is kept for diagnostics that compare layers.
        public static EndfieldVfsArchive OpenLayer(string vfsRoot)
        {
            if (!Directory.Exists(vfsRoot))
                throw new DirectoryNotFoundException($"Endfield VFS was not found: {vfsRoot}");

            var archive = new EndfieldVfsArchive(vfsRoot);
            var blcFiles = Directory.EnumerateFiles(vfsRoot, "*.blc", SearchOption.AllDirectories)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var blc in blcFiles)
                archive.ReadBlc(blc);

            archive.Fingerprint = BuildFingerprint(vfsRoot, blcFiles);

            if (archive.entries.Count == 0)
                throw new InvalidDataException($"No logical files were found in Endfield VFS: {vfsRoot}");
            return archive;
        }

        public static string ResolveSelection(string selectedPath)
        {
            var selected = Path.GetFullPath(selectedPath);
            if (!Directory.Exists(selected)) throw new DirectoryNotFoundException(selected);
            bool Game(string path) => Directory.Exists(Path.Combine(path, "Endfield_Data", "StreamingAssets", "VFS"));
            if (Game(selected)) return selected;
            var info = new DirectoryInfo(selected);
            if (info.Name.Equals("Endfield_Data", StringComparison.OrdinalIgnoreCase) && info.Parent != null && Game(info.Parent.FullName))
                return info.Parent.FullName;
            if (info.Name.Equals("VFS", StringComparison.OrdinalIgnoreCase) && info.Parent?.Parent?.Name.Equals("Endfield_Data", StringComparison.OrdinalIgnoreCase) == true)
            {
                var game = info.Parent.Parent.Parent?.FullName;
                if (game != null && Game(game)) return game;
            }
            if ((info.Name.Equals("StreamingAssets", StringComparison.OrdinalIgnoreCase) || info.Name.Equals("Persistent", StringComparison.OrdinalIgnoreCase)) && info.Parent?.Name.Equals("Endfield_Data", StringComparison.OrdinalIgnoreCase) == true && info.Parent.Parent != null && Game(info.Parent.Parent.FullName))
                return info.Parent.Parent.FullName;
            var games = Directory.EnumerateDirectories(selected).Where(Game).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            if (games.Length == 1) return games[0];
            if (games.Length > 1) throw new InvalidDataException("Multiple Endfield installations were found. Select one game folder:\n" + string.Join("\n", games));
            if (Directory.EnumerateFiles(selected, "*.blc", SearchOption.AllDirectories).Any()) return selected;
            throw new InvalidDataException("Select an Endfield game folder, its games parent, or a VFS folder.");
        }

        public static EndfieldVfsArchive Open(string selectedPath)
        {
            var resolved = ResolveSelection(selectedPath);
            var baseRoot = Path.Combine(resolved, "Endfield_Data", "StreamingAssets", "VFS");
            if (!Directory.Exists(baseRoot)) return OpenLayer(resolved);
            var shipped = OpenLayer(baseRoot);
            var hotRoot = Path.Combine(resolved, "Endfield_Data", "Persistent", "VFS");
            var hot = Directory.Exists(hotRoot) && Directory.EnumerateFiles(hotRoot, "*.blc", SearchOption.AllDirectories).Any() ? OpenLayer(hotRoot) : null;
            var archive = new EndfieldVfsArchive(resolved) { GameDirectory = resolved, LayerCount = hot == null ? 1 : 2 };
            var active = new Dictionary<string, Block>(shipped.blocks, StringComparer.OrdinalIgnoreCase);
            int baseVersion = shipped.blocks.Values.Max(b => b.Version);
            if (hot != null) foreach (var pair in hot.blocks)
            {
                if (active.TryGetValue(pair.Key, out var prior))
                {
                    if (pair.Value.Version < prior.Version) continue;
                    if (pair.Value.Version == prior.Version && !SameBlock(pair.Value, prior))
                        throw new InvalidDataException($"Conflicting BLCs with the same version: {prior.Source} / {pair.Value.Source}");
                }
                else if (pair.Value.Version < baseVersion) continue; // obsolete hot-only block after a base update
                active[pair.Key] = pair.Value;
            }
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in new[] { baseRoot, hotRoot }) if (Directory.Exists(root))
                foreach (var path in Directory.EnumerateFiles(root, "*.chk", SearchOption.AllDirectories)) sizes[path] = new FileInfo(path).Length;
            bool Available(EndfieldVfsEntry entry) => sizes.TryGetValue(entry.SourceChk, out var size) && entry.Offset >= 0 && entry.Length > 0 && entry.Offset <= size && entry.Length <= size - entry.Offset;
            foreach (var block in active.Values) foreach (var entry in block.Entries.Values)
            {
                var chosen = entry;
                if (!Available(chosen) && hot != null && chosen.SourceBlc.StartsWith(hotRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    var atBase = chosen with { SourceChk = Path.Combine(baseRoot, Path.GetRelativePath(hotRoot, chosen.SourceChk)) };
                    if (Available(atBase) && shipped.chunkDigests.TryGetValue(atBase.SourceChk, out var digest) && digest == chosen.ChunkContentMd5) chosen = atBase;
                    else if (shipped.TryGet(chosen.LogicalPath, out var original) && SameContent(chosen, original) && Available(original)) chosen = original;
                }
                if (!Available(chosen)) archive.MissingPayloadCount++;
                if (!archive.entries.TryAdd(chosen.LogicalPath, chosen))
                    throw new InvalidDataException("Conflicting active VFS owners: " + chosen.LogicalPath);
            }
            archive.Fingerprint = LayerIdentity(shipped, hot);
            return archive;
        }

        private static string LayerIdentity(EndfieldVfsArchive shipped, EndfieldVfsArchive hot)
        {
            var identity = new StringBuilder("EFF_VFS_LAYERS_V1\n").Append("streaming|").Append(shipped.Fingerprint).Append('\n')
                .Append("persistent|").Append(hot?.Fingerprint ?? "none").Append('\n');
            foreach (var pair in shipped.blcDigests) identity.Append("streamingBlc|").Append(pair.Key).Append('|').Append(pair.Value).Append('\n');
            if (hot != null) foreach (var pair in hot.blcDigests) identity.Append("persistentBlc|").Append(pair.Key).Append('|').Append(pair.Value).Append('\n');
            return LayersPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
        }

        public void VerifyCurrent()
        {
            EndfieldVfsArchive Metadata(string root)
            {
                var files = Directory.EnumerateFiles(root, "*.blc", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                var result = new EndfieldVfsArchive(root) { Fingerprint = BuildFingerprint(root, files) };
                foreach (var path in files) result.blcDigests.Add(Path.GetRelativePath(root, path).Replace('\\', '/'), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
                return result;
            }
            string current;
            if (GameDirectory == null) current = Metadata(VfsRoot).Fingerprint;
            else
            {
                var shipped = Metadata(Path.Combine(GameDirectory, "Endfield_Data", "StreamingAssets", "VFS"));
                var hotRoot = Path.Combine(GameDirectory, "Endfield_Data", "Persistent", "VFS");
                var hot = Directory.Exists(hotRoot) && Directory.EnumerateFiles(hotRoot, "*.blc", SearchOption.AllDirectories).Any() ? Metadata(hotRoot) : null;
                current = LayerIdentity(shipped, hot);
            }
            if (current != Fingerprint) throw new InvalidDataException("Installed VFS changed while it was open. Reopen the game folder after the update finishes.");
        }

        private static bool SameContent(EndfieldVfsEntry a, EndfieldVfsEntry b) => a.Length == b.Length && a.FileDataMd5 == b.FileDataMd5;
        private static bool SameBlock(Block a, Block b) => a.Entries.Count == b.Entries.Count && a.Entries.All(pair => b.Entries.TryGetValue(pair.Key, out var other) && SameContent(pair.Value, other));

        public bool TryGet(string logicalPath, out EndfieldVfsEntry entry)
        {
            return entries.TryGetValue(NormalizeLogicalPath(logicalPath), out entry);
        }

        public string ExtractToCache(string logicalPath, string workspace)
        {
            if (!TryGet(logicalPath, out var entry))
                throw new FileNotFoundException($"VFS logical file was not found: {logicalPath}");

            var cacheRoot = Path.Combine(Path.GetFullPath(workspace), "cache", "bundles");
            Directory.CreateDirectory(cacheRoot);
            var originalName = Path.GetFileName(entry.LogicalPath);
            var stem = Path.GetFileNameWithoutExtension(originalName);
            var extension = Path.GetExtension(originalName);
            if (string.IsNullOrWhiteSpace(stem))
                stem = $"{entry.FileNameHash:X16}";
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".bin";
            var fileName = $"{stem}-{entry.FileDataMd5}{extension}";
            var output = Path.Combine(cacheRoot, fileName);

            if (File.Exists(output) && new FileInfo(output).Length == entry.Length)
                return output;

            var payload = ReadPayload(entry);
            var temp = output + ".tmp";
            File.WriteAllBytes(temp, payload);
            File.Move(temp, output, true);
            return output;
        }

        public byte[] ReadPayload(string logicalPath)
        {
            if (!TryGet(logicalPath, out var entry))
                throw new FileNotFoundException($"VFS logical file was not found: {logicalPath}");
            return ReadPayload(entry);
        }

        private static byte[] ReadPayload(EndfieldVfsEntry entry)
        {
            if (entry.Offset < 0 || entry.Length <= 0)
                throw new InvalidDataException($"Invalid VFS range for {entry.LogicalPath}");

            var chunkLength = new FileInfo(entry.SourceChk).Length;
            if (entry.Offset + entry.Length > chunkLength)
                throw new InvalidDataException($"VFS range exceeds CHK length: {entry.LogicalPath}");
            if (entry.Length > int.MaxValue)
                throw new InvalidDataException($"VFS entry is too large to preview: {entry.LogicalPath}");

            var payload = new byte[(int)entry.Length];
            using (var stream = new FileStream(entry.SourceChk, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Position = entry.Offset;
                stream.ReadExactly(payload);
            }
            if (entry.Encrypted)
            {
                Span<byte> nonce = stackalloc byte[12];
                BinaryPrimitives.WriteInt32LittleEndian(nonce, ProtocolVersion);
                BinaryPrimitives.WriteInt64LittleEndian(nonce[4..], entry.IvSeed);
                ChaCha20Xor(payload, Key, nonce, initialCounter: 1);
            }

            return payload;
        }

        private static string BuildFingerprint(string vfsRoot, IReadOnlyList<string> blcFiles)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var path in blcFiles.Concat(Directory.EnumerateFiles(vfsRoot, "*.chk", SearchOption.AllDirectories)
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)))
            {
                var info = new FileInfo(path);
                var value = $"{Path.GetRelativePath(vfsRoot, path).Replace('\\', '/')}|{info.Length}|{info.LastWriteTimeUtc.Ticks}\n";
                hash.AppendData(Encoding.UTF8.GetBytes(value));
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        private void ReadBlc(string blcPath)
        {
            var raw = File.ReadAllBytes(blcPath);
            if (raw.Length < BlockHeaderLength + 4)
                throw new InvalidDataException($"BLC is too short: {blcPath}");

            var data = raw.AsSpan(BlockHeaderLength).ToArray();
            ChaCha20Xor(data, Key, raw.AsSpan(0, BlockHeaderLength), initialCounter: 1);
            using var stream = new MemoryStream(data, 0, data.Length - 4, writable: false);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

            var rawVersion = reader.ReadInt32();
            var codeVersion = rawVersion < 11 ? rawVersion : 3;
            var blockVersion = rawVersion < 11 ? reader.ReadInt32() : rawVersion;
            _ = ReadAscii(reader, reader.ReadUInt16());
            var blockKey = reader.ReadUInt64().ToString("X16");
            var blockEntries = new Dictionary<string, EndfieldVfsEntry>(StringComparer.OrdinalIgnoreCase);
            _ = reader.ReadInt32();
            _ = reader.ReadInt64();
            _ = reader.ReadByte();
            var chunkCount = ReadCount(reader, "chunk", blcPath);

            for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                var chunkMd5 = Convert.ToHexString(ReadExact(reader, 16));
                var chunkContentMd5 = Convert.ToHexString(ReadExact(reader, 16));
                _ = reader.ReadInt64();
                _ = reader.ReadByte();
                if (codeVersion > 3)
                    _ = reader.ReadInt32();
                var fileCount = ReadCount(reader, "file", blcPath);
                var chunkPath = Path.Combine(Path.GetDirectoryName(blcPath)!, $"{chunkMd5}.chk");
                if (!chunkDigests.TryAdd(chunkPath, chunkContentMd5) && chunkDigests[chunkPath] != chunkContentMd5)
                    throw new InvalidDataException("Conflicting CHK content identity: " + chunkPath);

                for (var fileIndex = 0; fileIndex < fileCount; fileIndex++)
                {
                    var rawLogicalPath = ReadAscii(reader, reader.ReadUInt16());
                    var logicalPath = NormalizeLogicalPath(rawLogicalPath);
                    var fileNameHash = reader.ReadInt64();
                    _ = ReadExact(reader, 16);
                    var fileDataMd5 = Convert.ToHexString(ReadExact(reader, 16));
                    var offset = reader.ReadInt64();
                    var length = reader.ReadInt64();
                    var fileType = reader.ReadByte();
                    var encrypted = reader.ReadByte() != 0;
                    var ivSeed = encrypted ? reader.ReadInt64() : 0;
                    if (codeVersion > 3)
                        _ = reader.ReadInt32();

                    if (string.IsNullOrWhiteSpace(logicalPath))
                        continue;
                    var entry = new EndfieldVfsEntry(
                        logicalPath, blcPath, chunkPath, offset, length, encrypted,
                        ivSeed, fileType, fileNameHash, fileDataMd5, chunkContentMd5);
                    if (!blockEntries.TryAdd(logicalPath, entry)) throw new InvalidDataException("Duplicate BLC logical path: " + logicalPath + " raw=" + rawLogicalPath);
                    entries[logicalPath] = entry;
                }
            }
            if (!blocks.TryAdd(blockKey, new Block(blockVersion, blcPath, blockEntries))) throw new InvalidDataException("Duplicate VFS block identity: " + blcPath);
            blcDigests.Add(Path.GetRelativePath(VfsRoot, blcPath).Replace('\\', '/'), Convert.ToHexString(SHA256.HashData(raw)));
        }

        public static string NormalizeLogicalPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            value = Regex.Replace(value, @"[^\x20-\x7e/\\]", string.Empty).Replace('\\', '/');
            var match = Regex.Match(value, @"(Data/|Assets/)[\x20-\x7e]+", RegexOptions.IgnoreCase);
            if (match.Success)
                value = match.Value;
            foreach (var prefix in new[] { "Assets/StreamingAssets/", "Assets/", "Data/" })
            {
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    value = value[prefix.Length..];
                    break;
                }
            }
            return value.Trim('/');
        }

        private static int ReadCount(BinaryReader reader, string description, string source)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > 2_000_000)
                throw new InvalidDataException($"Invalid {description} count {count} in {source}");
            return count;
        }

        private static string ReadAscii(BinaryReader reader, int length)
        {
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new EndOfStreamException("Truncated BLC string");
            return Encoding.ASCII.GetString(ReadExact(reader, length));
        }

        private static byte[] ReadExact(BinaryReader reader, int length)
        {
            var data = reader.ReadBytes(length);
            if (data.Length != length)
                throw new EndOfStreamException("Truncated BLC entry");
            return data;
        }

        private static void ChaCha20Xor(Span<byte> data, ReadOnlySpan<byte> key,
                                        ReadOnlySpan<byte> nonce, uint initialCounter)
        {
            if (key.Length != 32 || nonce.Length != 12)
                throw new ArgumentException("ChaCha20 requires a 32-byte key and 12-byte nonce");

            Span<uint> state = stackalloc uint[16];
            state[0] = 0x61707865;
            state[1] = 0x3320646e;
            state[2] = 0x79622d32;
            state[3] = 0x6b206574;
            for (var i = 0; i < 8; i++)
                state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 4, 4));
            state[12] = initialCounter;
            state[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[..4]);
            state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4, 4));
            state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(8, 4));

            Span<byte> block = stackalloc byte[64];
            Span<uint> working = stackalloc uint[16];
            for (var offset = 0; offset < data.Length; offset += 64)
            {
                state.CopyTo(working);
                for (var round = 0; round < 10; round++)
                {
                    QuarterRound(working, 0, 4, 8, 12);
                    QuarterRound(working, 1, 5, 9, 13);
                    QuarterRound(working, 2, 6, 10, 14);
                    QuarterRound(working, 3, 7, 11, 15);
                    QuarterRound(working, 0, 5, 10, 15);
                    QuarterRound(working, 1, 6, 11, 12);
                    QuarterRound(working, 2, 7, 8, 13);
                    QuarterRound(working, 3, 4, 9, 14);
                }
                for (var i = 0; i < 16; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(i * 4, 4), working[i] + state[i]);

                var count = Math.Min(64, data.Length - offset);
                for (var i = 0; i < count; i++)
                    data[offset + i] ^= block[i];
                state[12]++;
            }
        }

        private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
        {
            x[a] += x[b]; x[d] ^= x[a]; x[d] = RotateLeft(x[d], 16);
            x[c] += x[d]; x[b] ^= x[c]; x[b] = RotateLeft(x[b], 12);
            x[a] += x[b]; x[d] ^= x[a]; x[d] = RotateLeft(x[d], 8);
            x[c] += x[d]; x[b] ^= x[c]; x[b] = RotateLeft(x[b], 7);
        }

        private static uint RotateLeft(uint value, int count) =>
            (value << count) | (value >> (32 - count));
    }
}
