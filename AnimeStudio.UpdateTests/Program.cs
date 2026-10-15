using AnimeStudio.GUI;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "eff-source-update-tests-" + Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try
{
    void Check(bool result, string reason) { if (!result) throw new Exception(reason); }
    void Generate(string target, string name, string value)
    {
        Directory.CreateDirectory(Path.Combine(target, "source"));Directory.CreateDirectory(Path.Combine(target, "meshes"));
        File.WriteAllText(Path.Combine(target, "source", "manifest.json"), "{\"format\":\"EFFSOURCE\",\"serializedFiles\":[],\"resources\":[]}");
        File.WriteAllText(Path.Combine(target, "meshes", name), value);
    }
    var package = Path.Combine(root, "character.eff");
    EiemPackagePublication.Publish(package, p => Generate(p, "old.mesh", "old"));
    File.WriteAllText(Path.Combine(package, "notes.txt"), "author notes");
    try { EiemPackagePublication.Publish(package, p => { Generate(p, "broken.mesh", "broken");throw new IOException("injected failure"); });throw new Exception("Failure was swallowed"); }
    catch (IOException) { }
    Check(File.ReadAllText(Path.Combine(package, "meshes", "old.mesh")) == "old", "failed export corrupted old package");
    EiemPackagePublication.Publish(package, p => Generate(p, "new.mesh", "new"));
    Check(!File.Exists(Path.Combine(package, "meshes", "old.mesh")), "retired generated file retained");
    Check(File.ReadAllText(Path.Combine(package, "notes.txt")) == "author notes", "unowned author file lost");
    Check(Directory.EnumerateFiles(Path.Combine(root, ".eff-backups"), "old.mesh", SearchOption.AllDirectories).Any(), "previous source not backed up");
    using (var gate = new Mutex(false, EiemPackagePublication.LockName(package)))
    {
        gate.WaitOne();
        try
        {
            var rejected = Task.Run(() => { try { EiemPackagePublication.Publish(package, p => Generate(p, "wrong.mesh", "wrong"));return false; } catch (IOException) { return true; } }).GetAwaiter().GetResult();
            Check(rejected && File.Exists(Path.Combine(package, "meshes", "new.mesh")), "concurrent source reader was not protected");
        }
        finally { gate.ReleaseMutex(); }
    }
    Console.WriteLine("PASS source publication: failure rollback, generated retirement, unknown file preservation, backup");
    if (args.Length == 3 && args[0] == "--portable-real")
    {
        var archive = EndfieldVfsArchive.Open(args[1]);var workspace = args[2];var payload = Path.Combine(root, "game.effindex.gz");
        var release = EndfieldPortableIndex.Pack(archive, workspace, payload, "test");
        var imported = Path.Combine(root, "imported");EndfieldPortableIndex.Import(archive, imported, payload, CancellationToken.None);
        Check(EndfieldVfsAssetIndexBuilder.TryGetCurrentIndex(archive, imported, out _), "import not accepted by AnimeStudio");
        Check(File.ReadAllBytes(EndfieldVfsAssetIndexBuilder.GetIndexPath(workspace)).SequenceEqual(File.ReadAllBytes(EndfieldVfsAssetIndexBuilder.GetIndexPath(imported))), "same-machine binding changed asset records");
        Console.WriteLine("PASS real full index proof, gzip, local binding and AnimeStudio acceptance: " + JsonSerializer.Serialize(release));
    }
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error);return 1; }
finally { Directory.Delete(root, true); }
