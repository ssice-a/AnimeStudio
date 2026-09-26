using ZstdSharp;
using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Buffers;
using System.Buffers.Binary;

namespace AnimeStudio
{
    [Flags]
    public enum ArchiveFlags
    {
        CompressionTypeMask = 0x3f,
        BlocksAndDirectoryInfoCombined = 0x40,
        BlocksInfoAtTheEnd = 0x80,
        OldWebPluginCompatibility = 0x100,
        BlockInfoNeedPaddingAtStart = 0x200,
        UnityCNEncryption = 0x400,
        UnityCNEncryption2 = 0x1000
    }

    [Flags]
    public enum StorageBlockFlags
    {
        CompressionTypeMask = 0x3f,
        Streamed = 0x40,
    }

    public enum CompressionType
    {
        None,
        Lzma,
        Lz4,
        Lz4HC,
        Lzham,
        Lz4Mr0k,
        Lz4Inv = 5,
        Zstd = 5,
        Lz4Lit4 = 4,
        Lz4Lit5 = 5,
        OodleHSR = 6,
        OodleMr0k = 7,
        Oodle = 9,
    }

    public class BundleFile
    {
        public class Header
        {
            public string signature;
            public uint version;
            public string unityVersion;
            public string unityRevision;
            public long size;
            public uint compressedBlocksInfoSize;
            public uint uncompressedBlocksInfoSize;
            public ArchiveFlags flags;
            public uint encFlags;

            public override string ToString()
            {
                var sb = new StringBuilder();
                sb.Append($"signature: {signature} | ");
                sb.Append($"version: {version} | ");
                sb.Append($"unityVersion: {unityVersion} | ");
                sb.Append($"unityRevision: {unityRevision} | ");
                sb.Append($"size: 0x{size:X8} | ");
                sb.Append($"compressedBlocksInfoSize: 0x{compressedBlocksInfoSize:X8} | ");
                sb.Append($"uncompressedBlocksInfoSize: 0x{uncompressedBlocksInfoSize:X8} | ");
                sb.Append($"flags: 0x{(int)flags:X8}");
                return sb.ToString();
            }
        }

        public class StorageBlock
        {
            public uint compressedSize;
            public uint uncompressedSize;
            public StorageBlockFlags flags;

            public override string ToString()
            {
                var sb = new StringBuilder();
                sb.Append($"compressedSize: 0x{compressedSize:X8} | ");
                sb.Append($"uncompressedSize: 0x{uncompressedSize:X8} | ");
                sb.Append($"flags: 0x{(int)flags:X8}");
                return sb.ToString();
            }
        }

        public class Node
        {
            public long offset;
            public long size;
            public uint flags;
            public string path;

            public override string ToString()
            {
                var sb = new StringBuilder();
                sb.Append($"offset: 0x{offset:X8} | ");
                sb.Append($"size: 0x{size:X8} | ");
                sb.Append($"flags: {flags} | ");
                sb.Append($"path: {path}");
                return sb.ToString();
            }
        }

        private Game Game;
        private UnityCN UnityCN;

        public Header m_Header;
        private List<Node> m_DirectoryInfo;
        private List<StorageBlock> m_BlocksInfo;

        public List<StreamFile> fileList;
        
        private bool HasUncompressedDataHash = true;
        private bool HasBlockInfoNeedPaddingAtStart = true;
        private bool IsNarakaPagedBundle;

        public BundleFile(FileReader reader, Game game, bool lazyIndex = false)
        {
            Game = game;
            m_Header = ReadBundleHeader(reader);
            switch (m_Header.signature)
            {
                case "UnityArchive":
                    break; //TODO
                case "UnityWeb":
                case "UnityRaw":
                    if (m_Header.version == 6)
                    {
                        goto case "UnityFS";
                    }
                    ReadHeaderAndBlocksInfo(reader);
                    using (var blocksStream = CreateBlocksStream(reader.FullPath))
                    {
                        ReadBlocksAndDirectory(reader, blocksStream);
                        ReadFiles(blocksStream, reader.FullPath);
                    }
                    break;
                case "UnityFS":
                case "ENCR":
                    ReadHeader(reader);
                    if (game.IsUnityCN())
                    {
                        ReadUnityCN(reader);
                    }
                    else if(game.Type.IsAzurPromiliaCBT2())
                    {
                        UnityCN.SetKey("7a346c32336268352333356826333231");
                        ReadUnityCN(reader);
                    }
                    ReadBlocksInfoAndDirectory(reader);
                    if (lazyIndex && IsNarakaPagedBundle && File.Exists(reader.FullPath))
                    {
                        ReadFilesLazily(reader);
                    }
                    else
                    {
                        using (var blocksStream = CreateBlocksStream(reader.FullPath))
                        {
                            ReadBlocks(reader, blocksStream);
                            ReadFiles(blocksStream, reader.FullPath);
                        }
                    }
                    break;
            }
        }

        private Header ReadBundleHeader(FileReader reader)
        {
            Header header = new Header();
            header.signature = reader.ReadStringToNull(20);
            Logger.Verbose($"Parsed signature {header.signature}");
            switch (header.signature)
            {
                case "UnityFS":
                    if (Game.Type.IsBH3Group() || Game.Type.IsBH3PrePre())
                    {
                        if (Game.Type.IsBH3Group())
                        {
                            var key = reader.ReadUInt32();
                            if (key <= 11)
                            {
                                reader.Position -= 4;
                                goto default;
                            }
                            Logger.Verbose($"Encrypted bundle header with key {key}");
                            XORShift128.InitSeed(key);
                        }
                        else if (Game.Type.IsBH3PrePre())
                        {
                            Logger.Verbose($"Encrypted bundle header with key {reader.Length}");
                            XORShift128.InitSeed((uint)reader.Length);
                        }

                        header.version = 6;
                        header.unityVersion = "5.x.x";
                        header.unityRevision = "2017.4.18f1";
                    }
                    else
                    {
                        header.version = reader.ReadUInt32();
                        header.unityVersion = reader.ReadStringToNull();
                        header.unityRevision = reader.ReadStringToNull();
                    }
                    break;
                case "ENCR":
                    header.version = 6; // is 7 but does not have uncompressedDataHash
                    header.unityVersion = "5.x.x";
                    header.unityRevision = "2019.4.32f1";
                    HasUncompressedDataHash = false;
                    break;
                default:
                    if (Game.Type.IsNaraka())
                    {
                        header.signature = "UnityFS";
                        goto case "UnityFS";
                    }
                    header.version = reader.ReadUInt32();
                    header.unityVersion = reader.ReadStringToNull();
                    header.unityRevision = reader.ReadStringToNull();
                    break;

            }
            return header;
        }

        private void ReadHeaderAndBlocksInfo(FileReader reader)
        {
            if (m_Header.version >= 4)
            {
                var hash = reader.ReadBytes(16);
                var crc = reader.ReadUInt32();
            }
            var minimumStreamedBytes = reader.ReadUInt32();
            m_Header.size = reader.ReadUInt32();
            var numberOfLevelsToDownloadBeforeStreaming = reader.ReadUInt32();
            var levelCount = reader.ReadInt32();
            m_BlocksInfo = new List<StorageBlock>();
            for (int i = 0; i < levelCount; i++)
            {
                var storageBlock = new StorageBlock()
                {
                    compressedSize = reader.ReadUInt32(),
                    uncompressedSize = reader.ReadUInt32(),
                };
                if (i == levelCount - 1)
                {
                    m_BlocksInfo.Add(storageBlock);
                }
            }
            if (m_Header.version >= 2)
            {
                var completeFileSize = reader.ReadUInt32();
            }
            if (m_Header.version >= 3)
            {
                var fileInfoHeaderSize = reader.ReadUInt32();
            }
            reader.Position = m_Header.size;
        }

        private Stream CreateBlocksStream(string path)
        {
            Stream blocksStream;
            var uncompressedSizeSum = m_BlocksInfo.Sum(x => (long)x.uncompressedSize);
            Logger.Verbose($"Total size of decompressed blocks: {uncompressedSizeSum}");

            // Guard against corrupt/misaligned block info that would request multi-GB buffers.
            // Real UnityFS blocks are rarely more than a few hundred MB decompressed.
            const long maxInMemory = 512L * 1024 * 1024; // 512 MB
            if (uncompressedSizeSum <= 0)
            {
                throw new InvalidDataException($"Invalid decompressed block size: {uncompressedSizeSum}");
            }
            if (uncompressedSizeSum >= int.MaxValue || uncompressedSizeSum > maxInMemory)
            {
                /*var memoryMappedFile = MemoryMappedFile.CreateNew(null, uncompressedSizeSum);
                assetsDataStream = memoryMappedFile.CreateViewStream();*/
                Logger.Verbose($"Using temp file for large decompressed blocks ({uncompressedSizeSum} bytes)");
                blocksStream = new FileStream(
                    path + ".temp",
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete,
                    4096,
                    FileOptions.DeleteOnClose | FileOptions.SequentialScan);
            }
            else
            {
                // publiclyVisible so ReadFiles can slice CAB streams from the same buffer
                // instead of allocating a second full copy of every node (halves peak RAM).
                var buffer = GC.AllocateUninitializedArray<byte>((int)uncompressedSizeSum);
                blocksStream = new MemoryStream(buffer, 0, buffer.Length, writable: true, publiclyVisible: true);
            }
            return blocksStream;
        }

        private void ReadBlocksAndDirectory(FileReader reader, Stream blocksStream)
        {
            Logger.Verbose($"Writing block and directory to blocks stream...");

            var isCompressed = m_Header.signature == "UnityWeb";
            foreach (var blockInfo in m_BlocksInfo)
            {
                var uncompressedBytes = reader.ReadBytes((int)blockInfo.compressedSize);
                if (isCompressed)
                {
                    using var memoryStream = new MemoryStream(uncompressedBytes);
                    using var decompressStream = SevenZipHelper.StreamDecompress(memoryStream);
                    uncompressedBytes = decompressStream.ToArray();
                }
                blocksStream.Write(uncompressedBytes, 0, uncompressedBytes.Length);
            }
            blocksStream.Position = 0;
            var blocksReader = new EndianBinaryReader(blocksStream);
            var nodesCount = blocksReader.ReadInt32();
            m_DirectoryInfo = new List<Node>();
            Logger.Verbose($"Directory count: {nodesCount}");
            for (int i = 0; i < nodesCount; i++)
            {
                m_DirectoryInfo.Add(new Node
                {
                    path = blocksReader.ReadStringToNull(),
                    offset = blocksReader.ReadUInt32(),
                    size = blocksReader.ReadUInt32()
                });
            }
        }

        public void ReadFiles(Stream blocksStream, string path)
        {
            Logger.Verbose($"Writing files from blocks stream...");

            fileList = new List<StreamFile>();
            // Prefer zero-copy slices over the decompressed block buffer when possible.
            ArraySegment<byte> shared = default;
            bool hasShared = blocksStream is MemoryStream memoryStream
                             && memoryStream.TryGetBuffer(out shared)
                             && shared.Array != null;

            for (int i = 0; i < m_DirectoryInfo.Count; i++)
            {
                var node = m_DirectoryInfo[i];
                var file = new StreamFile();
                fileList.Add(file);
                file.path = node.path;
                file.fileName = Path.GetFileName(node.path);
                if (node.offset < 0 || node.size < 0 || node.offset > blocksStream.Length - node.size)
                {
                    throw new InvalidDataException(
                        $"Bundle node {node.path} range 0x{node.offset:X}+0x{node.size:X} exceeds decompressed data size 0x{blocksStream.Length:X}");
                }
                if (blocksStream is FileStream tempFileStream)
                {
                    // Keep large CAB/resource nodes on disk. Each node gets an independent,
                    // bounded handle; closing the original DeleteOnClose handle only removes
                    // the temp file after the final node stream has also been disposed.
                    file.stream = new BoundedFileStream(
                        tempFileStream.Name,
                        node.offset,
                        node.size);
                }
                else if (node.size >= int.MaxValue)
                {
                    /*var memoryMappedFile = MemoryMappedFile.CreateNew(null, entryinfo_size);
                    file.stream = memoryMappedFile.CreateViewStream();*/
                    var extractPath = path + "_unpacked" + Path.DirectorySeparatorChar;
                    Directory.CreateDirectory(extractPath);
                    file.stream = new FileStream(extractPath + file.fileName, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                    blocksStream.Position = node.offset;
                    blocksStream.CopyTo(file.stream, node.size);
                    file.stream.Position = 0;
                }
                else if (hasShared
                         && node.offset >= 0
                         && node.offset <= int.MaxValue
                         && node.size <= int.MaxValue
                         && node.offset + node.size <= shared.Count)
                {
                    // View into the shared decompressed buffer — no per-CAB copy.
                    file.stream = new MemoryStream(
                        shared.Array,
                        shared.Offset + (int)node.offset,
                        (int)node.size,
                        writable: false,
                        publiclyVisible: true);
                }
                else
                {
                    file.stream = new MemoryStream((int)node.size);
                    blocksStream.Position = node.offset;
                    blocksStream.CopyTo(file.stream, node.size);
                    file.stream.Position = 0;
                }
            }
        }

        private void ReadFilesLazily(FileReader reader)
        {
            Logger.Verbose("Creating on-demand Naraka block streams...");
            fileList = new List<StreamFile>();
            var source = new LazyNarakaBundleData(reader.FullPath, reader.Position, m_BlocksInfo);
            try
            {
                foreach (var node in m_DirectoryInfo)
                {
                    if (node.offset < 0 || node.size < 0 || node.offset > source.Length - node.size)
                    {
                        throw new InvalidDataException(
                            $"Bundle node {node.path} range 0x{node.offset:X}+0x{node.size:X} exceeds decompressed data size 0x{source.Length:X}");
                    }

                    fileList.Add(new StreamFile
                    {
                        path = node.path,
                        fileName = Path.GetFileName(node.path),
                        stream = new LazyNarakaNodeStream(source, node.offset, node.size)
                    });
                }
            }
            catch
            {
                foreach (var file in fileList)
                {
                    file.stream?.Dispose();
                }
                fileList.Clear();
                throw;
            }
            finally
            {
                source.Release();
            }
        }

        private sealed class LazyNarakaBundleData
        {
            private readonly struct Block
            {
                public readonly long LogicalOffset;
                public readonly long PhysicalOffset;
                public readonly uint CompressedSize;
                public readonly uint UncompressedSize;
                public readonly CompressionType Compression;

                public Block(long logicalOffset, long physicalOffset, StorageBlock info)
                {
                    LogicalOffset = logicalOffset;
                    PhysicalOffset = physicalOffset;
                    CompressedSize = info.compressedSize;
                    UncompressedSize = info.uncompressedSize;
                    Compression = (CompressionType)(info.flags & StorageBlockFlags.CompressionTypeMask);
                }
            }

            private sealed class CachedBlock
            {
                public byte[] Buffer;
                public LinkedListNode<int> LruNode;
            }

            private const long MaxCacheBytes = 32L * 1024 * 1024;
            private readonly object sync = new();
            private readonly FileStream source;
            private readonly Block[] blocks;
            private readonly Dictionary<int, CachedBlock> cache = new();
            private readonly LinkedList<int> lru = new();
            private long cachedBytes;
            private int referenceCount = 1;
            private bool disposed;

            public long Length { get; }

            public LazyNarakaBundleData(string path, long dataStart, List<StorageBlock> blockInfo)
            {
                source = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    4096,
                    FileOptions.RandomAccess);
                blocks = new Block[blockInfo.Count];
                var logicalOffset = 0L;
                var physicalOffset = dataStart;
                for (var i = 0; i < blockInfo.Count; i++)
                {
                    physicalOffset = Align(physicalOffset, 0x1000);
                    blocks[i] = new Block(logicalOffset, physicalOffset, blockInfo[i]);
                    logicalOffset = checked(logicalOffset + blockInfo[i].uncompressedSize);
                    physicalOffset = checked(physicalOffset + blockInfo[i].compressedSize);
                }
                Length = logicalOffset;
            }

            public void AddReference()
            {
                lock (sync)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    referenceCount++;
                }
            }

            public void Release()
            {
                lock (sync)
                {
                    if (--referenceCount > 0)
                    {
                        return;
                    }
                    disposed = true;
                    foreach (var item in cache.Values)
                    {
                        ArrayPool<byte>.Shared.Return(item.Buffer);
                    }
                    cache.Clear();
                    lru.Clear();
                    source.Dispose();
                }
            }

            public int Read(long position, Span<byte> destination)
            {
                lock (sync)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    if (position < 0 || position > Length)
                    {
                        throw new ArgumentOutOfRangeException(nameof(position));
                    }

                    var remaining = (int)Math.Min(destination.Length, Length - position);
                    var totalRead = remaining;
                    while (remaining > 0)
                    {
                        var blockIndex = FindBlock(position);
                        var block = blocks[blockIndex];
                        var buffer = GetBlock(blockIndex);
                        var offsetInBlock = checked((int)(position - block.LogicalOffset));
                        var count = Math.Min(remaining, checked((int)block.UncompressedSize) - offsetInBlock);
                        buffer.AsSpan(offsetInBlock, count).CopyTo(destination);
                        destination = destination[count..];
                        remaining -= count;
                        position += count;
                    }
                    return totalRead;
                }
            }

            private int FindBlock(long position)
            {
                var low = 0;
                var high = blocks.Length - 1;
                while (low <= high)
                {
                    var middle = low + ((high - low) / 2);
                    var block = blocks[middle];
                    if (position < block.LogicalOffset)
                    {
                        high = middle - 1;
                    }
                    else if (position >= block.LogicalOffset + block.UncompressedSize)
                    {
                        low = middle + 1;
                    }
                    else
                    {
                        return middle;
                    }
                }
                throw new EndOfStreamException($"No Naraka data block contains logical offset 0x{position:X}");
            }

            private byte[] GetBlock(int index)
            {
                if (cache.TryGetValue(index, out var cached))
                {
                    lru.Remove(cached.LruNode);
                    lru.AddLast(cached.LruNode);
                    return cached.Buffer;
                }

                var block = blocks[index];
                var compressedSize = checked((int)block.CompressedSize);
                var uncompressedSize = checked((int)block.UncompressedSize);
                var compressed = ArrayPool<byte>.Shared.Rent(compressedSize);
                var uncompressed = ArrayPool<byte>.Shared.Rent(uncompressedSize);
                try
                {
                    source.Position = block.PhysicalOffset;
                    source.ReadExactly(compressed.AsSpan(0, compressedSize));
                    int numWrite;
                    switch (block.Compression)
                    {
                        case CompressionType.None:
                            if (compressedSize != uncompressedSize)
                            {
                                throw new InvalidDataException(
                                    $"Uncompressed Naraka block has mismatched sizes {compressedSize}/{uncompressedSize}");
                            }
                            compressed.AsSpan(0, compressedSize).CopyTo(uncompressed);
                            numWrite = uncompressedSize;
                            break;
                        case CompressionType.Lz4:
                        case CompressionType.Lz4HC:
                            numWrite = LZ4.Instance.Decompress(
                                compressed.AsSpan(0, compressedSize),
                                uncompressed.AsSpan(0, uncompressedSize));
                            break;
                        case CompressionType.Zstd:
                            using (var decompressor = new Decompressor())
                            {
                                numWrite = decompressor.Unwrap(
                                    compressed,
                                    0,
                                    compressedSize,
                                    uncompressed,
                                    0,
                                    uncompressedSize);
                            }
                            break;
                        default:
                            throw new IOException($"Unsupported lazy Naraka compression type {block.Compression}");
                    }
                    if (numWrite != uncompressedSize)
                    {
                        throw new IOException(
                            $"Naraka block decompression error, wrote {numWrite} bytes but expected {uncompressedSize}");
                    }
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(uncompressed);
                    throw;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(compressed);
                }

                var lruNode = lru.AddLast(index);
                cache.Add(index, new CachedBlock { Buffer = uncompressed, LruNode = lruNode });
                cachedBytes += uncompressedSize;
                while (cachedBytes > MaxCacheBytes && lru.First != lru.Last)
                {
                    var evictIndex = lru.First.Value;
                    lru.RemoveFirst();
                    var evicted = cache[evictIndex];
                    cache.Remove(evictIndex);
                    cachedBytes -= blocks[evictIndex].UncompressedSize;
                    ArrayPool<byte>.Shared.Return(evicted.Buffer);
                }
                return uncompressed;
            }

            private static long Align(long value, int alignment)
                => (value + alignment - 1) & ~(alignment - 1L);
        }

        private sealed class LazyNarakaNodeStream : Stream
        {
            private LazyNarakaBundleData source;
            private readonly long start;
            private readonly long length;
            private long position;

            public LazyNarakaNodeStream(LazyNarakaBundleData source, long start, long length)
            {
                this.source = source;
                this.start = start;
                this.length = length;
                source.AddReference();
            }

            public override bool CanRead => source != null;
            public override bool CanSeek => source != null;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position
            {
                get => position;
                set => Seek(value, SeekOrigin.Begin);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                ArgumentNullException.ThrowIfNull(buffer);
                return Read(buffer.AsSpan(offset, count));
            }

            public override int Read(Span<byte> buffer)
            {
                ObjectDisposedException.ThrowIf(source == null, this);
                var count = (int)Math.Min(buffer.Length, length - position);
                if (count <= 0)
                {
                    return 0;
                }
                var read = source.Read(start + position, buffer[..count]);
                position += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                ObjectDisposedException.ThrowIf(source == null, this);
                var target = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => position + offset,
                    SeekOrigin.End => length + offset,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin))
                };
                if (target < 0 || target > length)
                {
                    throw new IOException("Attempted to seek outside the lazy bundle node");
                }
                position = target;
                return position;
            }

            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    var current = source;
                    source = null;
                    current?.Release();
                }
                base.Dispose(disposing);
            }
        }

        private sealed class BoundedFileStream : Stream
        {
            private readonly FileStream stream;
            private readonly long start;
            private readonly long length;

            public BoundedFileStream(string path, long start, long length)
            {
                this.start = start;
                this.length = length;
                stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    4096,
                    FileOptions.RandomAccess);
                stream.Position = start;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position
            {
                get => stream.Position - start;
                set => Seek(value, SeekOrigin.Begin);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var remaining = length - Position;
                if (remaining <= 0)
                {
                    return 0;
                }
                return stream.Read(buffer, offset, (int)Math.Min(count, remaining));
            }

            public override int Read(Span<byte> buffer)
            {
                var remaining = length - Position;
                if (remaining <= 0)
                {
                    return 0;
                }
                return stream.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                var target = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => Position + offset,
                    SeekOrigin.End => length + offset,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin))
                };
                if (target < 0 || target > length)
                {
                    throw new IOException("Attempted to seek outside the bounded bundle node");
                }
                stream.Position = start + target;
                return target;
            }

            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    stream.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        private void ReadHeader(FileReader reader)
        {
            if (XORShift128.Init)
            {
                if (Game.Type.IsBH3PrePre())
                {
                    m_Header.uncompressedBlocksInfoSize = reader.ReadUInt32() ^ XORShift128.NextDecryptUInt();
                    m_Header.compressedBlocksInfoSize = reader.ReadUInt32() ^ XORShift128.NextDecryptUInt();
                    m_Header.flags = (ArchiveFlags)(reader.ReadUInt32() ^ XORShift128.NextDecryptInt());
                    m_Header.size = reader.ReadInt64() ^ XORShift128.NextDecryptLong();
                    reader.ReadUInt32(); // version
                }
                else
                {
                    m_Header.flags = (ArchiveFlags)(reader.ReadUInt32() ^ XORShift128.NextDecryptInt());
                    m_Header.size = reader.ReadInt64() ^ XORShift128.NextDecryptLong();
                    m_Header.uncompressedBlocksInfoSize = reader.ReadUInt32() ^ XORShift128.NextDecryptUInt();
                    m_Header.compressedBlocksInfoSize = reader.ReadUInt32() ^ XORShift128.NextDecryptUInt();
                }

                XORShift128.Init = false;
                Logger.Verbose($"Bundle header decrypted");
               
                var encUnityVersion = reader.ReadStringToNull();
                var encUnityRevision = reader.ReadStringToNull();
                return;
            }

            m_Header.size = reader.ReadInt64();
            m_Header.compressedBlocksInfoSize = reader.ReadUInt32();
            m_Header.uncompressedBlocksInfoSize = reader.ReadUInt32();
            m_Header.flags = (ArchiveFlags)reader.ReadUInt32();
            if (m_Header.signature != "UnityFS" && !Game.Type.IsSRGroup())
            {
                reader.ReadByte();
            }

            if (Game.Type.IsNaraka())
            {
                var compressionType = (CompressionType)(m_Header.flags & ArchiveFlags.CompressionTypeMask);
                if (compressionType == CompressionType.OodleHSR
                    && (m_Header.flags & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0)
                {
                    // Current Naraka bundles add 3 to compression ids and store both
                    // metadata and data blocks on independent 4 KiB pages.
                    IsNarakaPagedBundle = true;
                    m_Header.flags = (ArchiveFlags)(
                        ((int)m_Header.flags & ~(int)ArchiveFlags.CompressionTypeMask)
                        | (int)CompressionType.Lz4HC);
                }
                else
                {
                    // Older Naraka format.
                    m_Header.compressedBlocksInfoSize -= 0xCA;
                    m_Header.uncompressedBlocksInfoSize -= 0xCA;
                }
            }

            Logger.Verbose($"Bundle header Info: {m_Header}");
        }

        private void ReadUnityCN(FileReader reader)
        {
            if(Game.Type.IsAzurPromiliaCBT2() && (m_Header.flags & ArchiveFlags.UnityCNEncryption) != 0)
            {
                UnityCN = new UnityCN(reader);
                return;
            }

            Logger.Verbose($"Attempting to decrypt file {reader.FileName} with UnityCN encryption");
            ArchiveFlags mask;

            var version = ParseVersion();
            //Flag changed it in these versions
            if (version[0] < 2020 || //2020 and earlier
                (version[0] == 2020 && version[1] == 3 && version[2] <= 34) || //2020.3.34 and earlier
                (version[0] == 2021 && version[1] == 3 && version[2] <= 2) || //2021.3.2 and earlier
                (version[0] == 2022 && version[1] == 3 && version[2] <= 1)) //2022.3.1 and earlier
            {
                mask = ArchiveFlags.BlockInfoNeedPaddingAtStart;
                HasBlockInfoNeedPaddingAtStart = false;
            }
            else
            {
                mask = ArchiveFlags.UnityCNEncryption;
                HasBlockInfoNeedPaddingAtStart = true;
            }

            Logger.Verbose($"Mask set to {mask}");

            if ((m_Header.flags & mask) != 0 || (m_Header.flags & ArchiveFlags.UnityCNEncryption2) != 0)
            {
                Logger.Verbose($"Encryption flag exist, file is encrypted, attempting to decrypt");
                UnityCN = new UnityCN(reader);
            }
        }

        private void ReadBlocksInfoAndDirectory(FileReader reader)
        {
            byte[] blocksInfoBytes;
            long narakaBlocksInfoStart = 0;
            var narakaBlocksInfoPayloadSize = 0;
            if (IsNarakaPagedBundle)
            {
                reader.AlignStream(0x1000);
                narakaBlocksInfoStart = reader.Position;
            }
            else if (m_Header.version >= 7 && !Game.Type.IsSRGroup())
            {
                reader.AlignStream(16);
            }
            if ((m_Header.flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0) //kArchiveBlocksInfoAtTheEnd
            {
                var position = reader.Position;
                // Multi-bundle containers (HSR ENCR .block) share one outer stream. Using
                // BaseStream.Length would seek to the end of the entire .block and parse the
                // wrong trailer. Prefer header.size (bundle byte length from signature).
                long bundleEnd = m_Header.size > 0
                    ? m_Header.size
                    : reader.BaseStream.Length;
                if (bundleEnd <= m_Header.compressedBlocksInfoSize)
                {
                    throw new InvalidDataException(
                        $"Bundle size {bundleEnd} is smaller than blocks-info size {m_Header.compressedBlocksInfoSize}");
                }
                reader.Position = bundleEnd - m_Header.compressedBlocksInfoSize;
                blocksInfoBytes = reader.ReadBytes((int)m_Header.compressedBlocksInfoSize);
                reader.Position = position;
            }
            else //0x40 BlocksAndDirectoryInfoCombined
            {
                blocksInfoBytes = reader.ReadBytes((int)m_Header.compressedBlocksInfoSize);
            }
            MemoryStream blocksInfoUncompresseddStream;
            var blocksInfoBytesSpan = blocksInfoBytes.AsSpan(0, (int)m_Header.compressedBlocksInfoSize);
            var uncompressedSize = m_Header.uncompressedBlocksInfoSize;
            var compressionType = (CompressionType)(m_Header.flags & ArchiveFlags.CompressionTypeMask);
            Logger.Verbose($"BlockInfo compression type: {compressionType}");
            switch (compressionType) //kArchiveCompressionTypeMask
            {
                case CompressionType.None: //None
                    {
                        blocksInfoUncompresseddStream = new MemoryStream(blocksInfoBytes);
                        break;
                    }
                case CompressionType.Lzma: //LZMA
                    {
                        blocksInfoUncompresseddStream = new MemoryStream((int)(uncompressedSize));
                        using (var blocksInfoCompressedStream = new MemoryStream(blocksInfoBytes))
                        {
                            SevenZipHelper.StreamDecompress(blocksInfoCompressedStream, blocksInfoUncompresseddStream, m_Header.compressedBlocksInfoSize, m_Header.uncompressedBlocksInfoSize);
                        }
                        blocksInfoUncompresseddStream.Position = 0;
                        break;
                    }
                case CompressionType.Lz4: //LZ4
                case CompressionType.Lz4HC: //LZ4HC
                    {
                        if (IsNarakaPagedBundle)
                        {
                            blocksInfoUncompresseddStream = DecompressNarakaBlocksInfo(
                                blocksInfoBytes,
                                checked((int)uncompressedSize),
                                out narakaBlocksInfoPayloadSize);
                            break;
                        }

                        var uncompressedBytes = ArrayPool<byte>.Shared.Rent((int)uncompressedSize);
                        try
                        {
                            var uncompressedBytesSpan = uncompressedBytes.AsSpan(0, (int)uncompressedSize);
                            if (Game.Type.IsPerpetualNovelty())
                            {
                                var key = blocksInfoBytesSpan[1];
                                for (int j = 0; j < Math.Min(0x32, blocksInfoBytesSpan.Length); j++)
                                {
                                    blocksInfoBytesSpan[j] ^= key;
                                }
                            }
                            var numWrite = LZ4.Instance.Decompress(blocksInfoBytesSpan, uncompressedBytesSpan);
                            if (numWrite != uncompressedSize)
                            {
                                throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                            }
                            blocksInfoUncompresseddStream = new MemoryStream(uncompressedBytesSpan.ToArray());
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(uncompressedBytes, true);
                        }
                        break;
                    }
                case CompressionType.Lz4Mr0k: //Lz4Mr0k
                    if (Mr0kUtils.IsMr0k(blocksInfoBytesSpan))
                    {
                        Logger.Verbose($"Header encrypted with mr0k, decrypting...");
                        blocksInfoBytesSpan = Mr0kUtils.Decrypt(blocksInfoBytesSpan, (Mr0k)Game).ToArray();
                    }
                    goto case CompressionType.Lz4HC;
                default:
                    throw new IOException($"Unsupported compression type {compressionType}");
            }
            using (var blocksInfoReader = new EndianBinaryReader(blocksInfoUncompresseddStream))
            {
                if (HasUncompressedDataHash)
                {
                    var uncompressedDataHash = blocksInfoReader.ReadBytes(16);
                }
                var blocksInfoCount = blocksInfoReader.ReadInt32();
                m_BlocksInfo = new List<StorageBlock>();
                Logger.Verbose($"Blocks count: {blocksInfoCount}");
                for (int i = 0; i < blocksInfoCount; i++)
                {
                    var blockUncompressedSize = blocksInfoReader.ReadUInt32();
                    var blockCompressedSize = blocksInfoReader.ReadUInt32();
                    var storageFlags = (StorageBlockFlags)blocksInfoReader.ReadUInt16();
                    if (IsNarakaPagedBundle)
                    {
                        var rawCompression = (int)(storageFlags & StorageBlockFlags.CompressionTypeMask);
                        if (rawCompression == 6 || rawCompression == 8)
                        {
                            storageFlags = (StorageBlockFlags)(
                                ((int)storageFlags & ~(int)StorageBlockFlags.CompressionTypeMask)
                                | (rawCompression - 3));
                        }
                    }

                    m_BlocksInfo.Add(new StorageBlock
                    {
                        uncompressedSize = blockUncompressedSize,
                        compressedSize = blockCompressedSize,
                        flags = storageFlags
                    });

                    Logger.Verbose($"Block {i} Info: {m_BlocksInfo[i]}");
                }

                var nodesCount = blocksInfoReader.ReadInt32();
                m_DirectoryInfo = new List<Node>();
                Logger.Verbose($"Directory count: {nodesCount}");
                for (int i = 0; i < nodesCount; i++)
                {
                    m_DirectoryInfo.Add(new Node
                    {
                        offset = blocksInfoReader.ReadInt64(),
                        size = blocksInfoReader.ReadInt64(),
                        flags = blocksInfoReader.ReadUInt32(),
                        path = blocksInfoReader.ReadStringToNull(),
                    });

                    Logger.Verbose($"Directory {i} Info: {m_DirectoryInfo[i]}");
                }
            }
            if (IsNarakaPagedBundle)
            {
                reader.Position = narakaBlocksInfoStart + narakaBlocksInfoPayloadSize;
                reader.AlignStream(0x1000);
            }
            else if (HasBlockInfoNeedPaddingAtStart && (m_Header.flags & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0)
            {
                reader.AlignStream(16);
            }
        }

        private MemoryStream DecompressNarakaBlocksInfo(
            byte[] compressedBytes,
            int outputCapacity,
            out int compressedPayloadSize)
        {
            compressedPayloadSize = 0;
            var uncompressedBytes = GC.AllocateUninitializedArray<byte>(outputCapacity);
            var marker = FindNarakaBlocksInfoPadding(compressedBytes);
            var triedSizes = new HashSet<int>();
            var matchedSize = 0;

            bool TryCandidate(int compressedSize, out MemoryStream result)
            {
                result = null;
                if (compressedSize <= 0 || !triedSizes.Add(compressedSize))
                {
                    return false;
                }

                try
                {
                    var numWrite = LZ4.Instance.Decompress(
                        compressedBytes.AsSpan(0, compressedSize),
                        uncompressedBytes);
                    if (!LooksLikeNarakaBlocksInfo(uncompressedBytes.AsSpan(0, numWrite)))
                    {
                        return false;
                    }

                    Logger.Verbose(
                        $"Naraka blocks info: compressed 0x{compressedSize:X}/0x{compressedBytes.Length:X}, decompressed 0x{numWrite:X}/0x{outputCapacity:X}");
                    result = new MemoryStream(
                        uncompressedBytes,
                        0,
                        numWrite,
                        writable: false,
                        publiclyVisible: true);
                    matchedSize = compressedSize;
                    return true;
                }
                catch (Exception ex) when (ex is ArgumentException
                                           or IndexOutOfRangeException
                                           or InvalidDataException)
                {
                    return false;
                }
            }

            if (marker > 0 && TryCandidate(marker, out var stream))
            {
                compressedPayloadSize = matchedSize;
                return stream;
            }

            // A small number of current bundles omit the recognizable footer.
            // Their private tail is still short, so locate the LZ4 boundary by
            // validating the decompressed Unity block-directory structure.
            var maxPadding = Math.Min(0x200, compressedBytes.Length - 1);
            for (var padding = 0; padding <= maxPadding; padding++)
            {
                if (TryCandidate(compressedBytes.Length - padding, out stream))
                {
                    compressedPayloadSize = matchedSize;
                    return stream;
                }
            }

            throw new InvalidDataException("Unable to locate Naraka blocks-info payload");
        }

        private bool LooksLikeNarakaBlocksInfo(ReadOnlySpan<byte> data)
        {
            try
            {
                var position = HasUncompressedDataHash ? 16 : 0;
                var blocksCount = ReadInt32BigEndian(data, ref position);
                if (blocksCount < 0 || blocksCount > 100_000)
                {
                    return false;
                }

                ulong totalBlockSize = 0;
                for (var i = 0; i < blocksCount; i++)
                {
                    totalBlockSize += ReadUInt32BigEndian(data, ref position);
                    _ = ReadUInt32BigEndian(data, ref position);
                    var flags = ReadUInt16BigEndian(data, ref position);
                    var compression = flags & (ushort)StorageBlockFlags.CompressionTypeMask;
                    if (compression != 0 && compression != 6 && compression != 8)
                    {
                        return false;
                    }
                }

                var nodesCount = ReadInt32BigEndian(data, ref position);
                if (nodesCount < 0 || nodesCount > 100_000)
                {
                    return false;
                }

                ulong totalNodeSize = 0;
                for (var i = 0; i < nodesCount; i++)
                {
                    _ = ReadUInt64BigEndian(data, ref position);
                    totalNodeSize += ReadUInt64BigEndian(data, ref position);
                    _ = ReadUInt32BigEndian(data, ref position);
                    var terminator = data[position..].IndexOf((byte)0);
                    if (terminator < 0)
                    {
                        return false;
                    }
                    position += terminator + 1;
                }

                return totalNodeSize <= totalBlockSize;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        private static int FindNarakaBlocksInfoPadding(ReadOnlySpan<byte> data)
        {
            for (var i = data.Length - 4; i >= 0; i--)
            {
                if (data[i] != 0x01 || data[i + 1] != 0x00)
                {
                    continue;
                }

                var cursor = i + 2;
                while (cursor < data.Length && data[cursor] == 0xFF)
                {
                    cursor++;
                }
                if (cursor + 1 >= data.Length || data[cursor + 1] != 0x50)
                {
                    continue;
                }

                var zeroPadded = true;
                for (var tail = cursor + 2; tail < data.Length; tail++)
                {
                    if (data[tail] != 0)
                    {
                        zeroPadded = false;
                        break;
                    }
                }
                if (zeroPadded)
                {
                    return i;
                }
            }

            return -1;
        }

        private static int ReadInt32BigEndian(ReadOnlySpan<byte> data, ref int position)
        {
            var value = BinaryPrimitives.ReadInt32BigEndian(data.Slice(position, sizeof(int)));
            position += sizeof(int);
            return value;
        }

        private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data, ref int position)
        {
            var value = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(position, sizeof(uint)));
            position += sizeof(uint);
            return value;
        }

        private static ulong ReadUInt64BigEndian(ReadOnlySpan<byte> data, ref int position)
        {
            var value = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(position, sizeof(ulong)));
            position += sizeof(ulong);
            return value;
        }

        private static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> data, ref int position)
        {
            var value = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position, sizeof(ushort)));
            position += sizeof(ushort);
            return value;
        }

        private void ReadBlocks(FileReader reader, Stream blocksStream)
        {
            Logger.Verbose($"Writing block to blocks stream...");

            byte[] firstBlockBytes = new byte[0];
            Span<byte> firstBlockSpan = Span<byte>.Empty;

            for (int i = 0; i < m_BlocksInfo.Count; i++)
            {
                if (IsNarakaPagedBundle)
                {
                    reader.AlignStream(0x1000);
                }

                Logger.Verbose($"Reading block {i}...");
                var blockInfo = m_BlocksInfo[i];
                var compressionType = (CompressionType)(blockInfo.flags & StorageBlockFlags.CompressionTypeMask);
                Logger.Verbose($"Block compression type {compressionType}");
                switch (compressionType) //kStorageBlockCompressionTypeMask
                {
                    case CompressionType.None: //None
                        {
                            reader.BaseStream.CopyTo(blocksStream, blockInfo.compressedSize);
                            break;
                        }
                    case CompressionType.Lzma: //LZMA
                        {
                            var compressedStream = reader.BaseStream;
                            if (Game.Type.IsNetEase() && i == 0)
                            {
                                var compressedBytesSpan = reader.ReadBytes((int)blockInfo.compressedSize).AsSpan();
                                NetEaseUtils.DecryptWithoutHeader(compressedBytesSpan);
                                var ms = new MemoryStream(compressedBytesSpan.ToArray());
                                compressedStream = ms;
                            }
                            SevenZipHelper.StreamDecompress(compressedStream, blocksStream, blockInfo.compressedSize, blockInfo.uncompressedSize);
                            break;
                        }
                    case CompressionType.OodleHSR:
                    case CompressionType.OodleMr0k:
                        {
                            // Star Rail v2.7 fix, thanks to Yarik
                            var compressedSize = (int)blockInfo.compressedSize;
                            var uncompressedSize = (int)blockInfo.uncompressedSize;

                            // Avoid ArrayPool for large blocks: Shared keeps returned buffers for
                            // reuse, so walking 100+ HSR ENCRs would permanently retain the largest
                            // (~100MB+) decompress buffers in the pool for the process lifetime.
                            const int arrayPoolLimit = 1 * 1024 * 1024;
                            var poolCompressed = compressedSize <= arrayPoolLimit;
                            var poolUncompressed = uncompressedSize <= arrayPoolLimit;
                            var compressedBytes = poolCompressed
                                ? ArrayPool<byte>.Shared.Rent(compressedSize)
                                : GC.AllocateUninitializedArray<byte>(compressedSize);
                            var uncompressedBytes = poolUncompressed
                                ? ArrayPool<byte>.Shared.Rent(uncompressedSize)
                                : GC.AllocateUninitializedArray<byte>(uncompressedSize);

                            var compressedBytesSpan = compressedBytes.AsSpan(0, compressedSize);
                            var uncompressedBytesSpan = uncompressedBytes.AsSpan(0, uncompressedSize);

                            try
                            {
                                reader.Read(compressedBytesSpan);
                                if (compressionType == CompressionType.OodleMr0k && Mr0kUtils.IsMr0k(compressedBytes))
                                {
                                    Logger.Verbose($"Block encrypted with mr0k, decrypting...");
                                    compressedBytesSpan = Mr0kUtils.Decrypt(compressedBytesSpan, (Mr0k)Game);
                                }

                                var numWrite = OodleHelper.Decompress(compressedBytesSpan, uncompressedBytesSpan);
                                if (numWrite != uncompressedSize)
                                {
                                    Logger.Warning($"Oodle decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                                }
                            }
                            finally
                            {
                                blocksStream.Write(uncompressedBytesSpan);
                                if (poolCompressed)
                                    ArrayPool<byte>.Shared.Return(compressedBytes, true);
                                if (poolUncompressed)
                                    ArrayPool<byte>.Shared.Return(uncompressedBytes, true);
                            }

                            break;
                        }
                    case CompressionType.Lz4: //LZ4
                    case CompressionType.Lz4HC: //LZ4HC
                    case CompressionType.Lz4Mr0k when Game.Type.IsMhyGroup(): //Lz4Mr0k
                        {
                            var compressedSize = (int)blockInfo.compressedSize;
                            var uncompressedSize = (int)blockInfo.uncompressedSize;

                            var compressedBytes = ArrayPool<byte>.Shared.Rent(compressedSize);
                            var uncompressedBytes = ArrayPool<byte>.Shared.Rent(uncompressedSize);

                            try
                            {
                                var compressedBytesSpan = compressedBytes.AsSpan(0, compressedSize);
                                var uncompressedBytesSpan = uncompressedBytes.AsSpan(0, uncompressedSize);

                                reader.Read(compressedBytesSpan);

                                if (Game.Type.IsHNACB1())
                                {
                                    if (i == 0)
                                    {
                                        // if first block, decrypt
                                        WmvUtils.Decrypt(compressedBytesSpan);
                                    }
                                    else
                                    {
                                        // else, xor with first one
                                        for (int j = 0; j < compressedBytesSpan.Length; j++)
                                        {
                                            compressedBytesSpan[j] ^= firstBlockSpan[j % firstBlockSpan.Length];
                                        }
                                    }
                                }
                                
                                if (compressionType == CompressionType.Lz4Mr0k && Mr0kUtils.IsMr0k(compressedBytes))
                                {
                                    Logger.Verbose($"Block encrypted with mr0k, decrypting...");
                                    compressedBytesSpan = Mr0kUtils.Decrypt(compressedBytesSpan, (Mr0k)Game);
                                }
                                if (Game.IsUnityCN() && ((int)blockInfo.flags & 0x100) != 0)
                                {
                                    Logger.Verbose($"Decrypting block with UnityCN...");
                                    UnityCN.DecryptBlock(compressedBytes, compressedSize, i);
                                }
                                if (Game.Type.IsAzurPromiliaCBT2() && ((int)blockInfo.flags & 0x100) != 0)
                                {
                                    Logger.Verbose($"Decrypting block with AzurPromilia CBT2...");
                                    UnityCN.DecryptBlock(compressedBytes, compressedSize, i);
                                }
                                if (Game.Type.IsNetEase() && i == 0)
                                {
                                    NetEaseUtils.DecryptWithHeader(compressedBytesSpan);
                                }
                                if ((Game.Type.IsArknightsEndfieldCB1() || Game.Type.IsArknightsEndfieldCB2()) && i == 0 && compressedBytesSpan[..32].Count((byte)0xa6) > 5)
                                {
                                    FairGuardUtils.Decrypt(compressedBytesSpan, Game.Type);
                                }
                                if (Game.Type.IsOPFP())
                                {
                                    OPFPUtils.Decrypt(compressedBytesSpan, reader.FullPath);
                                }
                                var numWrite = LZ4.Instance.Decompress(compressedBytesSpan, uncompressedBytesSpan);
                                if (numWrite != uncompressedSize)
                                {
                                    throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                                }

                                if (Game.Type.IsHNACB1() && i == 0)
                                {
                                    firstBlockBytes = ArrayPool<byte>.Shared.Rent(uncompressedSize);
                                    firstBlockSpan = firstBlockBytes.AsSpan(0, uncompressedSize);
                                    uncompressedBytesSpan.CopyTo(firstBlockSpan);
                                }

                                blocksStream.Write(uncompressedBytesSpan);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(compressedBytes, true);
                                ArrayPool<byte>.Shared.Return(uncompressedBytes, true);
                            }
                            break;
                        }
                    case CompressionType.Lz4Inv when Game.Type.IsArknightsEndfieldCB2():
                        {
                            var compressedSize = (int)blockInfo.compressedSize;
                            var uncompressedSize = (int)blockInfo.uncompressedSize;

                            var compressedBytes = ArrayPool<byte>.Shared.Rent(compressedSize);
                            var uncompressedBytes = ArrayPool<byte>.Shared.Rent(uncompressedSize);

                            var compressedBytesSpan = compressedBytes.AsSpan(0, compressedSize);
                            var uncompressedBytesSpan = uncompressedBytes.AsSpan(0, uncompressedSize);

                            try
                            {
                                reader.Read(compressedBytesSpan);
                                if (i == 0 && compressedBytesSpan[..32].Count((byte)0xa6) > 5)
                                {
                                    FairGuardUtils.Decrypt(compressedBytesSpan, Game.Type);
                                }

                                var numWrite = LZ4Inv.Instance.Decompress(compressedBytesSpan, uncompressedBytesSpan);
                                if (numWrite != uncompressedSize)
                                {
                                    throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                                }
                                blocksStream.Write(uncompressedBytesSpan);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(compressedBytes, true);
                                ArrayPool<byte>.Shared.Return(uncompressedBytes, true);
                            }
                            break;
                        }
                    case CompressionType.Lz4Inv when Game.Type.IsArknightsEndfieldCB1():
                        {
                            var compressedSize = (int)blockInfo.compressedSize;
                            var uncompressedSize = (int)blockInfo.uncompressedSize;

                            var compressedBytes = ArrayPool<byte>.Shared.Rent(compressedSize);
                            var uncompressedBytes = ArrayPool<byte>.Shared.Rent(uncompressedSize);

                            var compressedBytesSpan = compressedBytes.AsSpan(0, compressedSize);
                            var uncompressedBytesSpan = uncompressedBytes.AsSpan(0, uncompressedSize);

                            try
                            {
                                reader.Read(compressedBytesSpan);
                                if (i == 0 && compressedBytesSpan[..32].Count((byte)0xa6) > 5)
                                {
                                    FairGuardUtils.Decrypt(compressedBytesSpan, Game.Type);
                                }

                                var numWrite = LZ4Ak.Instance.Decompress(compressedBytesSpan, uncompressedBytesSpan);
                                if (numWrite != uncompressedSize)
                                {
                                    throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                                }
                                blocksStream.Write(uncompressedBytesSpan);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(compressedBytes, true);
                                ArrayPool<byte>.Shared.Return(uncompressedBytes, true);
                            }
                            break;
                        }
                    case CompressionType.Lz4Lit4 or CompressionType.Lz4Lit5 when Game.Type.IsExAstris():
                        {
                            var compressedSize = (int)blockInfo.compressedSize;
                            var uncompressedSize = (int)blockInfo.uncompressedSize;

                            var compressedBytes = ArrayPool<byte>.Shared.Rent(compressedSize);
                            var uncompressedBytes = ArrayPool<byte>.Shared.Rent(uncompressedSize);

                            var compressedBytesSpan = compressedBytes.AsSpan(0, compressedSize);
                            var uncompressedBytesSpan = uncompressedBytes.AsSpan(0, uncompressedSize);

                            try
                            {
                                reader.Read(compressedBytesSpan);
                                var numWrite = LZ4Lit.Instance.Decompress(compressedBytesSpan, uncompressedBytesSpan);
                                if (numWrite != uncompressedSize)
                                {
                                    throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                                }
                                blocksStream.Write(uncompressedBytesSpan);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(compressedBytes, true);
                                ArrayPool<byte>.Shared.Return(uncompressedBytes, true);
                            }
                            break;
                        }
                    case CompressionType.Zstd when !Game.Type.IsMhyGroup(): //Zstd
                        {
                            var compressedSize = (int)blockInfo.compressedSize;
                            var uncompressedSize = (int)blockInfo.uncompressedSize;

                            var compressedBytes = ArrayPool<byte>.Shared.Rent(compressedSize);
                            var uncompressedBytes = ArrayPool<byte>.Shared.Rent(uncompressedSize);

                            try
                            {
                                reader.Read(compressedBytes, 0, compressedSize);
                                using var decompressor = new Decompressor();
                                var numWrite = decompressor.Unwrap(compressedBytes, 0, compressedSize, uncompressedBytes, 0, uncompressedSize);
                                if (numWrite != uncompressedSize)
                                {
                                    throw new IOException($"Zstd decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                                }
                                blocksStream.Write(uncompressedBytes.ToArray(), 0, uncompressedSize);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Zstd decompression error:\n{ex}");
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(compressedBytes, true);
                                ArrayPool<byte>.Shared.Return(uncompressedBytes, true);
                            }
                            break;
                        }
                    case CompressionType.Lz4Lit4 or CompressionType.Lz4Lit5 when Game.Type.IsArknights():
                        {
                            var compressedSize = (int)blockInfo.compressedSize;
                            var uncompressedSize = (int)blockInfo.uncompressedSize;

                            var compressedBytes = ArrayPool<byte>.Shared.Rent(compressedSize);
                            var uncompressedBytes = ArrayPool<byte>.Shared.Rent(uncompressedSize);

                            var compressedBytesSpan = compressedBytes.AsSpan(0, compressedSize);
                            var uncompressedBytesSpan = uncompressedBytes.AsSpan(0, uncompressedSize);

                            try
                            {
                                reader.Read(compressedBytesSpan);
                                var numWrite = LZ4Ak.Instance.Decompress(compressedBytesSpan, uncompressedBytesSpan);
                                if (numWrite != uncompressedSize)
                                {
                                    throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                                }
                                blocksStream.Write(uncompressedBytesSpan);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(compressedBytes, true);
                                ArrayPool<byte>.Shared.Return(uncompressedBytes, true);
                            }
                            break;
                        }
                    default:
                        throw new IOException($"Unsupported compression type {compressionType}");
                }
            }
            ArrayPool<byte>.Shared.Return(firstBlockBytes, true);
            blocksStream.Position = 0;
        }

        public int[] ParseVersion()
        {
            var versionSplit = Regex.Replace(m_Header.unityRevision, @"\D", ".").Split(new[] { "." }, StringSplitOptions.RemoveEmptyEntries);
            return versionSplit.Select(int.Parse).ToArray();
        }
    }
}
