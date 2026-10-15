using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AnimeStudio.GUI;

internal sealed record EndfieldIndexRelease(string Format, string Version, string GameContent,
    string File, long Bytes, string Sha256, long IndexBytes, string IndexSha256);

// The public payload carries content proofs, never a publisher's local workspace.
internal static class EndfieldPortableIndex
{
    private const string Magic = "EFF_PORTABLE_INDEX/1";
    internal static EndfieldVfsEntry[] Bundles(EndfieldVfsArchive archive) => archive.Entries.Values
        .Where(b => b.LogicalPath.EndsWith(".ab", StringComparison.OrdinalIgnoreCase))
        .OrderBy(b => b.LogicalPath, StringComparer.OrdinalIgnoreCase).ToArray();

    internal static string ContentIdentity(EndfieldVfsArchive archive)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new MemoryStream();using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        foreach (var row in Bundles(archive))
        {
            stream.SetLength(0);stream.Position = 0;
            writer.Write(row.LogicalPath.Replace('\\', '/').ToLowerInvariant());writer.Write(row.FileDataMd5.ToUpperInvariant());writer.Write(row.Length);writer.Flush();
            hash.AppendData(stream.GetBuffer().AsSpan(0, (int)stream.Length));
        }
        return "EFF_BUNDLE_CONTENT/1:" + Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static EndfieldIndexRelease Pack(EndfieldVfsArchive archive, string workspace, string destination, string version)
    {
        if (!EndfieldVfsAssetIndexBuilder.TryGetCurrentIndex(archive, workspace, out var indexPath)) throw new InvalidDataException("Publish requires a complete current index");
        archive.VerifyCurrent();
        using var index = File.OpenRead(indexPath);var indexHash = Convert.ToHexString(SHA256.HashData(index));index.Position = 0;
        destination = Path.GetFullPath(destination);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temp))
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            using (var writer = new BinaryWriter(gzip, Encoding.UTF8, true))
            {
                var bundles = Bundles(archive);writer.Write(Magic);writer.Write(ContentIdentity(archive));writer.Write(index.Length);writer.Write(indexHash);writer.Write(bundles.Length);
                foreach (var row in bundles) { writer.Write(row.LogicalPath);writer.Write(row.FileDataMd5);writer.Write(row.Length); }
                writer.Flush();index.CopyTo(gzip);
            }
            archive.VerifyCurrent();File.Move(temp, destination, true);
            using var published = File.OpenRead(destination);
            var release = new EndfieldIndexRelease("EFF_INDEX_RELEASE/1", version, ContentIdentity(archive), Path.GetFileName(destination),
                published.Length, Convert.ToHexString(SHA256.HashData(published)), index.Length, indexHash);
            File.WriteAllText(destination + ".json", JsonSerializer.Serialize(release, new JsonSerializerOptions { WriteIndented = true }));return release;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static string Import(EndfieldVfsArchive archive, string workspace, string package, CancellationToken token)
    {
        Directory.CreateDirectory(workspace);
        var tempRoot = Path.Combine(workspace, ".eff-index-import-" + Guid.NewGuid().ToString("N"));Directory.CreateDirectory(tempRoot);
        try
        {
            using var file = File.OpenRead(package);using var gzip = new GZipStream(file, CompressionMode.Decompress);using var reader = new BinaryReader(gzip, Encoding.UTF8, true);
            if (reader.ReadString() != Magic || reader.ReadString() != ContentIdentity(archive)) throw new InvalidDataException("The published index does not match the installed game content");
            long length = reader.ReadInt64();var expected = reader.ReadString();var bundles = Bundles(archive);
            if (length < 17 || expected.Length != 64 || reader.ReadInt32() != bundles.Length) throw new InvalidDataException("Invalid public index envelope");
            foreach (var row in bundles)
            {
                token.ThrowIfCancellationRequested();
                if (!string.Equals(reader.ReadString(), row.LogicalPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(reader.ReadString(), row.FileDataMd5, StringComparison.OrdinalIgnoreCase) || reader.ReadInt64() != row.Length)
                    throw new InvalidDataException("Bundle content differs: " + row.LogicalPath);
                var size = new FileInfo(row.SourceChk).Length;
                if (row.Offset < 0 || row.Length <= 0 || row.Offset > size || row.Length > size - row.Offset) throw new InvalidDataException("Current Bundle payload unavailable: " + row.LogicalPath);
            }
            var raw = Path.Combine(tempRoot, "published.eidx");
            using (var output = File.Create(raw))
            {
                var buffer = new byte[1024 * 1024];long remaining = length;using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();int n = gzip.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));if (n == 0) throw new EndOfStreamException("Truncated public index");
                    hash.AppendData(buffer, 0, n);output.Write(buffer, 0, n);remaining -= n;
                }
                if (Convert.ToHexString(hash.GetHashAndReset()) != expected || gzip.ReadByte() != -1) throw new InvalidDataException("Public index checksum/extent differs");
            }
            var stagedWorkspace = Path.Combine(tempRoot, "bound");var stagedIndexRoot = Path.Combine(stagedWorkspace, "index");Directory.CreateDirectory(stagedIndexRoot);
            var bound = EndfieldVfsAssetIndexBuilder.GetIndexPath(stagedWorkspace);long assets, cabs;
            using (var input = File.OpenRead(raw))
            using (var sourceReader = new BinaryReader(input, Encoding.UTF8, true))
            using (var output = File.Create(bound))
            using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
            {
                _ = EndfieldBundleIndexCache.ReadHeader(sourceReader);long start = input.Position;input.Position = input.Length - 17;
                if (sourceReader.ReadByte() != 0) throw new InvalidDataException("Missing index end record");assets = sourceReader.ReadInt64();cabs = sourceReader.ReadInt64();
                if (assets < 0 || cabs < 0) throw new InvalidDataException("Negative index record counts");
                input.Position = start;EndfieldIndexStore.WriteHeader(writer, archive.Fingerprint);writer.Flush();input.CopyTo(output);
            }
            var cache = EndfieldBundleIndexCache.Scan(bound, archive.Fingerprint, bundles, new FileInfo(bound).Length, assets, cabs, true, token);
            cache.Save(EndfieldBundleIndexCache.PathFor(bound));
            File.WriteAllText(Path.Combine(stagedIndexRoot, "endfield-index-manifest.json"), JsonSerializer.Serialize(new EndfieldIndexManifest(
                EndfieldIndexStore.FormatVersion, archive.Fingerprint, bundles.Length, assets, cabs, 0, cache.IndexLength, DateTime.UtcNow,
                EndfieldBundleIndexCache.ParserIdentity, 0, bundles.Length, 0)));
            token.ThrowIfCancellationRequested();archive.VerifyCurrent();
            var indexRoot = Path.Combine(workspace, "index");Directory.CreateDirectory(indexRoot);
            using var gate = new FileStream(Path.Combine(indexRoot, "endfield-index.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var names = new[] { "endfield_assets.eidx", "endfield_assets.eidx.bundles", "endfield-index-manifest.json" };
            var backupRoot = Path.Combine(tempRoot, "previous");Directory.CreateDirectory(backupRoot);
            var committed = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var name in names)
                {
                    var target = Path.Combine(indexRoot, name);
                    if (File.Exists(target)) File.Copy(target, Path.Combine(backupRoot, name));
                    File.Move(Path.Combine(stagedIndexRoot, name), target, true);committed.Add(name);
                }
            }
            catch
            {
                foreach (var name in committed.AsEnumerable().Reverse())
                {
                    var backup = Path.Combine(backupRoot, name);var target = Path.Combine(indexRoot, name);
                    if (File.Exists(backup)) File.Move(backup, target, true);else File.Delete(target);
                }
                throw;
            }
            return EndfieldVfsAssetIndexBuilder.GetIndexPath(workspace);
        }
        finally { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); }
    }
}
