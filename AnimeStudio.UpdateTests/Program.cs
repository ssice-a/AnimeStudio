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
    var watched = Path.Combine(root, "watched.eff");
    EiemPackagePublication.Publish(watched, p => Generate(p, "first.mesh", "first"));
    File.WriteAllText(Path.Combine(watched, "notes.txt"), "keep notes");
    using (var watcher = DirectoryWatchHandle.Open(Path.Combine(watched, "meshes")))
    {
        EiemPackagePublication.Publish(watched, p => Generate(p, "second.mesh", "second"));
        Check(File.ReadAllText(Path.Combine(watched, "meshes", "second.mesh")) == "second" && !File.Exists(Path.Combine(watched, "meshes", "first.mesh")),
            "Directory watcher prevented source update or retirement");
        Check(File.ReadAllText(Path.Combine(watched, "notes.txt")) == "keep notes", "Watched update lost unknown author file");
    }
    var rollback = Path.Combine(root, "watched-rollback.eff");
    void Pair(string target, string value) { Generate(target, "a.mesh", value);File.WriteAllText(Path.Combine(target, "meshes", "z.mesh"), value); }
    EiemPackagePublication.Publish(rollback, p => Pair(p, "old"));
    using (var watcher = DirectoryWatchHandle.Open(Path.Combine(rollback, "meshes")))
    using (var locked = new FileStream(Path.Combine(rollback, "meshes", "z.mesh"), FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try { EiemPackagePublication.Publish(rollback, p => Pair(p, "new"));throw new Exception("Locked source file should reject replacement"); }
        catch (IOException) { }
        Check(File.ReadAllText(Path.Combine(rollback, "meshes", "a.mesh")) == "old" && File.ReadAllText(Path.Combine(rollback, "meshes", "z.mesh")) == "old",
            "Failed watched update did not roll back earlier files");
    }
    Console.WriteLine("PASS watched-directory source update, unknown preservation, retirement and locked-file rollback");
    var standalone = Path.Combine(root, "material.eff");Directory.CreateDirectory(Path.Combine(standalone, "source"));
    var standaloneManifest = "{\"format\":\"EFFSOURCE\",\"purpose\":\"material-author-source\",\"resources\":[]}";
    File.WriteAllText(Path.Combine(standalone, "source", "manifest.json"), standaloneManifest);
    Directory.CreateDirectory(Path.Combine(root, "unused-workspace"));
    var messages = new List<string>();
    Check(EndfieldSourceRefresh.Refresh(null!, Path.Combine(root, "unused-workspace"), new InlineProgress(messages.Add), CancellationToken.None, standalone) == 0,
        "Standalone source without a Prefab anchor should be skipped");
    Check(messages.Any(m => m.Contains("without a Prefab anchor")) && File.ReadAllText(Path.Combine(standalone, "source", "manifest.json")) == standaloneManifest,
        "Standalone material source was modified or skipped silently");
    Console.WriteLine("PASS standalone material source skip without missing-key exception");
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

sealed class InlineProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}

static class DirectoryWatchHandle
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    internal static Microsoft.Win32.SafeHandles.SafeFileHandle Open(string path)
    {
        var handle = CreateFile(path, 1, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception();return handle;
    }
}
