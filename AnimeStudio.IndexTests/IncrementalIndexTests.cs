using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnimeStudio;
using AnimeStudio.GUI;

internal static class IncrementalIndexTests
{
    private delegate void Cipher(Span<byte> bytes, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter);
    private static readonly Cipher Encrypt = typeof(EndfieldVfsArchive).GetMethod("ChaCha20Xor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.CreateDelegate<Cipher>();
    private sealed class Progress : IProgress<EndfieldIndexProgress>
    {
        public EndfieldIndexProgress Last;
        public Action<EndfieldIndexProgress> OnReport;
        public void Report(EndfieldIndexProgress value) { Last = value;OnReport?.Invoke(value); }
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (IOException) { return; } catch (InvalidDataException) { return; } catch (OperationCanceledException) { return; }
        throw new Exception("Expected rejection: " + message);
    }

    private static void Block(string root, int version, params (string Path, byte Value)[] items) =>
        PayloadBlock(root, version, items.Select(i => (i.Path, new[] { i.Value })).ToArray());
    private static void PayloadBlock(string root, int version, params (string Path, byte[] Payload)[] items)
    {
        Directory.CreateDirectory(root);using var payload = new MemoryStream();payload.Write(new byte[version % 7 + 1]);
        var offsets = items.Select(i => { long offset = payload.Position;payload.Write(i.Payload);return offset; }).ToArray();
        var bytes = payload.ToArray();var chunk = MD5.HashData(bytes);File.WriteAllBytes(Path.Combine(root, Convert.ToHexString(chunk) + ".chk"), bytes);
        using var block = new MemoryStream();using var w = new BinaryWriter(block, Encoding.ASCII, true);
        w.Write(4);w.Write(version);var name = Encoding.ASCII.GetBytes("Data/Bundles/Windows/main");w.Write((ushort)name.Length);w.Write(name);w.Write(1UL);
        w.Write(items.Length);w.Write((long)bytes.Length);w.Write((byte)0);w.Write(1);
        w.Write(chunk);w.Write(chunk);w.Write((long)bytes.Length);w.Write((byte)0);w.Write(0);w.Write(items.Length);
        for (int n = 0; n < items.Length; n++)
        {
            var path = Encoding.ASCII.GetBytes("Data/" + items[n].Path);w.Write((ushort)path.Length);w.Write(path);w.Write(0L);w.Write(chunk);
            w.Write(MD5.HashData(items[n].Payload));w.Write(offsets[n]);w.Write((long)items[n].Payload.Length);w.Write((byte)0);w.Write((byte)0);w.Write(0);
        }
        w.Write(0);w.Flush();var plain = block.ToArray();byte[] nonce = new byte[12];
        Encrypt(plain, Convert.FromHexString("E95B317AC4F828569D23A86BF271DCB53E846FA75C924D671DBA8E38F4CA52E1"), nonce, 1);
        File.WriteAllBytes(Path.Combine(root, "test.blc"), nonce.Concat(plain).ToArray());
    }

    public static int Real(string gamePath, string outputRoot)
    {
        if (Directory.Exists(outputRoot)) throw new IOException("Real incremental fixture must be new");
        var archive = EndfieldVfsArchive.Open(gamePath);var bundles = archive.Entries.Values.Where(b => b.LogicalPath.EndsWith(".ab", StringComparison.OrdinalIgnoreCase)).OrderBy(b => b.LogicalPath, StringComparer.OrdinalIgnoreCase).ToArray();
        Check(bundles.Length >= 9, "Real fixture needs at least nine Bundles");
        var originals = Enumerable.Range(0, 9).Select(n => bundles[n * (bundles.Length - 1) / 8]).ToArray();
        var samples = originals.Select((b, i) => (Path: $"bundles/sample{i:D2}.ab", Payload: archive.ReadPayload(b.LogicalPath))).ToArray();
        var vfs = Path.Combine(outputRoot, "vfs");var workspace = Path.Combine(outputRoot, "incremental");var progress = new Progress();var game = GameManager.GetGameByType(GameType.ArknightsEndfield);
        PayloadBlock(vfs, 100, samples.Take(8).ToArray());
        var first = EndfieldVfsAssetIndexBuilder.Build(EndfieldVfsArchive.Open(vfs), workspace, game, progress, CancellationToken.None);
        Check(progress.Last.ParsedBundles == 8 && EndfieldIndexStore.Load(first).StoredAssetCount > 0, "Real Bundle initial parse");
        var updated = samples.Take(7).Select((s, i) => i == 1 ? (s.Path, samples[8].Payload) : s).Append(("bundles/new.ab", samples[7].Payload)).ToArray();
        PayloadBlock(vfs, 200, updated);
        var incremental = EndfieldVfsAssetIndexBuilder.Build(EndfieldVfsArchive.Open(vfs), workspace, game, progress, CancellationToken.None);
        Check(progress.Last.ParsedBundles == 2 && progress.Last.ReusedBundles == 6 && progress.Last.RemovedBundles == 1, "Real Bundle delta selection");
        var deltaTime = progress.Last.Elapsed.TotalMilliseconds;
        var cold = EndfieldVfsAssetIndexBuilder.Build(EndfieldVfsArchive.Open(vfs), Path.Combine(outputRoot, "cold"), game, progress, CancellationToken.None);
        var coldTime = progress.Last.Elapsed.TotalMilliseconds;Check(File.ReadAllBytes(incremental).SequenceEqual(File.ReadAllBytes(cold)), "Real incremental/cold bytes differ");
        var result = new { parsed = 2, reused = 6, removed = 1, incrementalMs = deltaTime, coldMs = coldTime, indexBytes = new FileInfo(incremental).Length,
            assets = EndfieldIndexStore.Load(incremental).StoredAssetCount, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(incremental))), gameUnmodified = true };
        Directory.CreateDirectory(outputRoot);File.WriteAllText(Path.Combine(outputRoot, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS real Bundle incremental/cold byte equality: " + JsonSerializer.Serialize(result));return 0;
    }

    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "eff-incremental-index-" + Guid.NewGuid().ToString("N"));
        var vfs = Path.Combine(root, "vfs");var workspace = Path.Combine(root, "workspace");
        var parsed = new List<string>();var progress = new Progress();
        (long Assets, long Cabs) Parse(EndfieldVfsEntry entry, BinaryWriter writer)
        {
            parsed.Add(entry.LogicalPath);byte value = EndfieldVfsArchive.Open(vfs).ReadPayload(entry.LogicalPath)[0];
            if (value == 238) return (0, 0); // Valid empty indexed Bundle.
            writer.Write((byte)1);writer.Write("asset-" + value);writer.Write("assets/" + entry.LogicalPath + ".prefab");writer.Write("GameObject");writer.Write((long)value);writer.Write(entry.LogicalPath);
            writer.Write((byte)2);writer.Write("CAB-" + entry.LogicalPath);writer.Write(entry.LogicalPath);writer.Write(1);writer.Write("CAB-dependency-" + value);
            return (1, 1);
        }
        string Build(string output = null, Func<EndfieldVfsEntry, BinaryWriter, (long Assets, long Cabs)> parser = null, CancellationToken token = default) =>
            EndfieldVfsAssetIndexBuilder.Build(EndfieldVfsArchive.Open(vfs), output ?? workspace, null, progress, token, parse: parser ?? Parse);
        string index = EndfieldVfsAssetIndexBuilder.GetIndexPath(workspace);
        string Manifest() => Path.Combine(workspace, "index", "endfield-index-manifest.json");
        try
        {
            Block(vfs, 100, ("bundles/keep.ab", 1), ("bundles/change.ab", 2), ("bundles/delete.ab", 3), ("bundles/empty.ab", 238));
            Build();Check(parsed.Count == 4 && progress.Last.ParsedBundles == 4, "Initial baseline must parse all Bundles");
            Check(EndfieldIndexStore.Load(index).StoredAssetCount == 3, "Initial compact index");
            var before = File.ReadAllBytes(index);parsed.Clear();Build();Check(parsed.Count == 0 && before.SequenceEqual(File.ReadAllBytes(index)), "Unchanged game must reuse index without rewriting");

            // The repair tool shares this exact path, without a Unity parser.
            // Same logical payloads moved to another CHK/range remain reusable.
            var contentBefore = EndfieldPortableIndex.ContentIdentity(EndfieldVfsArchive.Open(vfs));
            Block(vfs, 101, ("bundles/keep.ab", 1), ("bundles/change.ab", 2), ("bundles/delete.ab", 3), ("bundles/empty.ab", 238));
            var relocated = EndfieldVfsArchive.Open(vfs);
            Check(!EndfieldVfsAssetIndexBuilder.TryGetCurrentIndex(relocated, workspace, out _) &&
                contentBefore == EndfieldPortableIndex.ContentIdentity(relocated), "Relocation did not separate location and content identities");
            Check(EndfieldVfsAssetIndexBuilder.TryRebindUnchangedContent(relocated, workspace, CancellationToken.None) &&
                EndfieldVfsAssetIndexBuilder.TryGetCurrentIndex(relocated, workspace, out _), "Content-identical index could not rebind");
            var relocatedCold = Build(Path.Combine(root, "relocated-cold"));
            Check(File.ReadAllBytes(index).SequenceEqual(File.ReadAllBytes(relocatedCold)), "Rebound index differs from cold current index");parsed.Clear();
            var retainedIndex = File.ReadAllBytes(index);
            Block(vfs, 102, ("bundles/keep.ab", 1), ("bundles/change.ab", 7), ("bundles/delete.ab", 3), ("bundles/empty.ab", 238));
            Check(!EndfieldVfsAssetIndexBuilder.TryRebindUnchangedContent(EndfieldVfsArchive.Open(vfs), workspace, CancellationToken.None) &&
                retainedIndex.SequenceEqual(File.ReadAllBytes(index)), "Changed content was stamped onto old index records");
            Block(vfs, 103, ("bundles/keep.ab", 1), ("bundles/change.ab", 2), ("bundles/delete.ab", 3), ("bundles/empty.ab", 238));
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();Reject(() => EndfieldVfsAssetIndexBuilder.TryRebindUnchangedContent(EndfieldVfsArchive.Open(vfs), workspace, canceled.Token), "Canceled rebind");
                Check(retainedIndex.SequenceEqual(File.ReadAllBytes(index)), "Canceled rebind replaced the index");
            }
            Build();parsed.Clear();
            Console.WriteLine("PASS logical index rebind shared by AnimeStudio/Repair: moved CHK/range, cold equality, changed-content rejection and cancellation");

            // The GUI normally disables GameObject export. Its display/export
            // preferences must never remove Prefab records from the shared index.
            var savedFlags = Enum.GetValues<ClassIDType>().Distinct().ToDictionary(t => t, t => (t.CanParse(), t.CanExport()));
            var savedMinimal = AssetsHelper.Minimal;
            try
            {
                TypeFlags.SetTypes(new Dictionary<ClassIDType, (bool, bool)> { [ClassIDType.GameObject] = (false, false), [ClassIDType.Mesh] = (false, false) });
                AssetsHelper.Minimal = false;
                bool indexPolicyObserved = false;
                Build(Path.Combine(root, "gui-filtered"), parser: (entry, writer) =>
                {
                    Check(ClassIDType.GameObject.CanParse() && ClassIDType.GameObject.CanExport() && ClassIDType.Mesh.CanExport() && AssetsHelper.Minimal,
                        "GUI type preferences leaked into the persistent resource index");
                    indexPolicyObserved = true;return Parse(entry, writer);
                });
                Check(indexPolicyObserved && !ClassIDType.GameObject.CanParse() && !ClassIDType.Mesh.CanExport() && !AssetsHelper.Minimal,
                    "Index build did not restore GUI preferences");
                Reject(() => Build(Path.Combine(root, "gui-filtered-failure"), parser: (_, _) => throw new IOException("Injected index failure")), "Filtered build failure");
                Check(!ClassIDType.GameObject.CanParse() && !AssetsHelper.Minimal, "Failed index build changed GUI preferences");
            }
            finally { TypeFlags.SetTypes(savedFlags);AssetsHelper.Minimal = savedMinimal;parsed.Clear(); }
            Block(vfs, 200, ("bundles/keep.ab", 1), ("bundles/change.ab", 4), ("bundles/new.ab", 5), ("bundles/empty.ab", 238));
            Build();Check(parsed.Order().SequenceEqual(new[] { "bundles/change.ab", "bundles/new.ab" }), "Only new/changed Bundles parsed, despite new CHK and offsets");
            Check(progress.Last.ReusedBundles == 2 && progress.Last.RemovedBundles == 1, "Empty/unchanged reuse and deleted count");
            var loaded = EndfieldIndexStore.Load(index);
            Check(!loaded.Assets.Records.Any(r => r.Source == "bundles/delete.ab" || r.Name == "asset-2"), "Deleted and replaced asset records must disappear");
            Check(loaded.Dependencies.GetBundleSource("CAB-bundles/delete.ab") == null, "Deleted CAB must disappear");
            var fresh = Path.Combine(root, "fresh");parsed.Clear();var freshIndex = Build(fresh);
            Check(File.ReadAllBytes(index).SequenceEqual(File.ReadAllBytes(freshIndex)), "Incremental output differs from full cold build");

            // Seed a pre-incremental current index from records; no reparse/rewrite.
            var legacy = Path.Combine(root, "legacy");var legacyIndex = EndfieldVfsAssetIndexBuilder.GetIndexPath(legacy);Directory.CreateDirectory(Path.GetDirectoryName(legacyIndex)!);File.Copy(index, legacyIndex);
            var m = JsonSerializer.Deserialize<EndfieldIndexManifest>(File.ReadAllText(Manifest()))!;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(legacyIndex)!, "endfield-index-manifest.json"), JsonSerializer.Serialize(m with { ParserIdentity = null }));
            parsed.Clear();Build(legacy);Check(parsed.Count == 0 && File.ReadAllBytes(legacyIndex).SequenceEqual(File.ReadAllBytes(index)) && EndfieldBundleIndexCache.TryLoad(legacyIndex) != null, "Current legacy index adoption");

            // A damaged reused range reparses exactly its Bundle, not every Bundle.
            var cache = EndfieldBundleIndexCache.TryLoad(index)!;var keep = cache.Spans["bundles/keep.ab"];
            using (var file = new FileStream(index, FileMode.Open, FileAccess.ReadWrite)) { file.Position = keep.Offset + 2;int b = file.ReadByte();file.Position--;file.WriteByte((byte)(b ^ 1)); }
            Block(vfs, 201, ("bundles/keep.ab", 1), ("bundles/change.ab", 4), ("bundles/new.ab", 5), ("bundles/empty.ab", 238));
            parsed.Clear();Build();Check(parsed.SequenceEqual(new[] { "bundles/keep.ab" }), "Corrupt record range must be locally repaired");

            // Failed Bundle retry never skips a previous parse failure or publishes partial records.
            before = File.ReadAllBytes(index);Block(vfs, 300, ("bundles/keep.ab", 1), ("bundles/change.ab", 9), ("bundles/new.ab", 5));
            Reject(() => Build(parser: (entry, writer) => { writer.Write((byte)1);throw new IOException("Injected parse failure"); }), "Parse failure");
            Check(before.SequenceEqual(File.ReadAllBytes(index)), "Failed update replaced committed index");parsed.Clear();Build();Check(parsed.SequenceEqual(new[] { "bundles/change.ab" }), "Failed Bundle was skipped on retry");

            // Unavailable current payload cannot use cached old content.
            before = File.ReadAllBytes(index);Block(vfs, 301, ("bundles/keep.ab", 1), ("bundles/change.ab", 10), ("bundles/new.ab", 5));
            var unavailable = EndfieldVfsArchive.Open(vfs);File.Delete(unavailable.Entries.Values.First().SourceChk);
            Reject(() => Build(), "Missing payload");Check(before.SequenceEqual(File.ReadAllBytes(index)), "Missing payload replaced committed index");

            // Resume only the exact game baseline, verifying persisted prefix bytes.
            var many = Enumerable.Range(0, 40).Select(n => ($"bundles/{n:D3}.ab", (byte)(n + 20))).ToArray();Block(vfs, 400, many);
            var resume = Path.Combine(root, "resume");using var cancellation = new CancellationTokenSource();
            progress.OnReport = p => { if (p.ProcessedBundles == 32) cancellation.Cancel(); };parsed.Clear();
            Reject(() => Build(resume, token: cancellation.Token), "Cancellation");Check(parsed.Count == 32, "Cancellation checkpoint position");
            progress.OnReport = null;parsed.Clear();var resumed = Build(resume);Check(parsed.Count == 8, "Resume reparsed completed Bundles");
            var cold = Build(Path.Combine(root, "resume-cold"));Check(File.ReadAllBytes(resumed).SequenceEqual(File.ReadAllBytes(cold)), "Resumed output differs from cold");

            var corruptResume = Path.Combine(root, "corrupt-resume");using var cancelCorrupt = new CancellationTokenSource();progress.OnReport = p => { if (p.ProcessedBundles == 32) cancelCorrupt.Cancel(); };
            Reject(() => Build(corruptResume, token: cancelCorrupt.Token), "Corrupt checkpoint setup");progress.OnReport = null;
            var part = EndfieldVfsAssetIndexBuilder.GetIndexPath(corruptResume) + ".partial";
            using (var file = new FileStream(part, FileMode.Open, FileAccess.ReadWrite)) { using var r = new BinaryReader(file, Encoding.UTF8, true);EndfieldBundleIndexCache.ReadHeader(r);file.Position += 2;int b = file.ReadByte();file.Position--;file.WriteByte((byte)(b ^ 1)); }
            parsed.Clear();Build(corruptResume);Check(parsed.Count == 40, "Corrupt checkpoint prefix must not be blessed by new hashes");

            // Index parser-semantic changes invalidate reuse, including same-game fast path.
            var parserWorkspace = Path.Combine(root, "parser");var parserIndex = Build(parserWorkspace);
            var ledger = EndfieldBundleIndexCache.PathFor(parserIndex);var bytes = File.ReadAllBytes(ledger);
            using (var s = new MemoryStream(bytes)) { using var r = new BinaryReader(s, Encoding.UTF8, true);r.ReadString();var start = s.Position;var identity = r.ReadString();bytes[s.Position - 1] ^= 1; }
            SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes.AsSpan(bytes.Length - 32));File.WriteAllBytes(ledger, bytes);
            var pm = Path.Combine(Path.GetDirectoryName(parserIndex)!, "endfield-index-manifest.json");var prior = JsonSerializer.Deserialize<EndfieldIndexManifest>(File.ReadAllText(pm))!;File.WriteAllText(pm, JsonSerializer.Serialize(prior with { ParserIdentity = "changed-parser" }));
            parsed.Clear();Build(parserWorkspace);Check(parsed.Count == 40, "Changed parsing semantics must invalidate reuse");

            // A portable full index must accept other paths/times with the same content.
            var packed = Path.Combine(root, "public.effindex.gz");var publisher = EndfieldVfsArchive.Open(vfs);
            EndfieldPortableIndex.Pack(publisher, parserWorkspace, packed, "fixture");
            var otherVfs = Path.Combine(root, "other-machine-vfs");Directory.CreateDirectory(otherVfs);
            foreach (var file in Directory.EnumerateFiles(vfs)) { var target = Path.Combine(otherVfs, Path.GetFileName(file));File.Copy(file, target);File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddHours(-12)); }
            var other = EndfieldVfsArchive.Open(otherVfs);
            Check(other.Fingerprint != publisher.Fingerprint && EndfieldPortableIndex.ContentIdentity(other) == EndfieldPortableIndex.ContentIdentity(publisher), "Public identity depends on local timestamps");
            var importedWorkspace = Path.Combine(root, "public-import");var imported = EndfieldPortableIndex.Import(other, importedWorkspace, packed, CancellationToken.None);
            Check(EndfieldVfsAssetIndexBuilder.TryGetCurrentIndex(other, importedWorkspace, out _), "Portable index not accepted by AnimeStudio");
            Check(EndfieldIndexStore.Load(imported).StoredAssetCount == EndfieldIndexStore.Load(parserIndex).StoredAssetCount, "Portable asset records lost");
            IndexDistributionTests.Registry(publisher, parserWorkspace, other, importedWorkspace);
            var accepted = File.ReadAllBytes(imported);var truncated = Path.Combine(root, "truncated.effindex.gz");File.WriteAllBytes(truncated, File.ReadAllBytes(packed).Take(30).ToArray());
            Reject(() => EndfieldPortableIndex.Import(other, importedWorkspace, truncated, CancellationToken.None), "Truncated portable payload");
            Check(accepted.SequenceEqual(File.ReadAllBytes(imported)), "Failed public import changed current index");
            Block(otherVfs, 600, ("bundles/other.ab", 92));
            Reject(() => EndfieldPortableIndex.Import(EndfieldVfsArchive.Open(otherVfs), importedWorkspace, packed, CancellationToken.None), "Different actual game content");
            Console.WriteLine("PASS portable index: other machine/path/times, shared AnimeStudio acceptance, content mismatch and truncation rejection");

            // Resource changes during preparation reject commit and preserve last generation.
            before = File.ReadAllBytes(index);Block(vfs, 500, ("bundles/only.ab", 90));
            Reject(() => Build(parser: (entry, writer) => { var counts = Parse(entry, writer);Block(vfs, 501, ("bundles/only.ab", 91));return counts; }), "Concurrent VFS update");
            Check(before.SequenceEqual(File.ReadAllBytes(index)), "Changed VFS committed stale candidate");
            Console.WriteLine("PASS incremental index: new/changed/deleted/empty, moved CHK/range reuse, zero-work reopen, cold-byte equality, legacy adoption, corrupt-range repair, failure retry, unavailable payload rejection, verified checkpoint resume, parser invalidation, concurrent update rejection");return 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
