using AnimeStudio.GUI;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class IndexDistributionTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++;return action(request, token); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Reject(Func<Task> action, string message)
    {
        try { await action(); } catch (Exception error) when (error is IOException or OperationCanceledException or JsonException) { return; }
        throw new Exception("Expected rejection: " + message);
    }

    internal static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "eff-index-download-" + Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var bytes = Encoding.UTF8.GetBytes("portable index download fixture");var sha = Convert.ToHexString(SHA256.HashData(bytes));
            var release = new EndfieldIndexRelease("EFF_INDEX_RELEASE/1", "fixture", "EFF_BUNDLE_CONTENT/1:" + new string('A', 64), "index.effindex.gz", bytes.Length, sha, 1234, new string('B', 64));
            var item = new IndexDownload(release, "https://example.test/index.effindex.gz");
            var json = JsonSerializer.Serialize(new RepairCatalog("EFF_REPAIR_CATALOG/1", "0.2.0", "https://example.test/tool", [item]));
            using var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath switch
                {
                    "/release" => new StringContent("""{"assets":[{"name":"eff-index-catalog.json","browser_download_url":"https://example.test/catalog"}]}"""),
                    "/catalog" => new StringContent(json),
                    _ => new ByteArrayContent(bytes)
                }
            }));
            using var client = new HttpClient(handler);
            var catalog = await EndfieldIndexUpdates.Check("https://example.test/release", CancellationToken.None, client);
            Check(catalog.Indexes.Single() == item && handler.Requests == 2, "GitHub catalog resolution lost index metadata");
            Check((await EndfieldIndexUpdates.Check("https://example.test/catalog", CancellationToken.None, client)).Indexes.Single() == item, "Direct catalog unsupported");
            var destination = Path.Combine(root, "downloads", "index.effindex.gz");
            await EndfieldIndexUpdates.Download(item, destination, null, CancellationToken.None, client);
            Check(File.ReadAllBytes(destination).SequenceEqual(bytes), "Download bytes changed");
            var requests = handler.Requests;
            await EndfieldIndexUpdates.Download(item, destination, null, CancellationToken.None, client);
            Check(requests == handler.Requests, "Valid cached package redownloaded");
            File.WriteAllBytes(destination, Enumerable.Repeat((byte)1, bytes.Length).ToArray());
            await EndfieldIndexUpdates.Download(item, destination, null, CancellationToken.None, client);
            Check(handler.Requests == requests + 1 && File.ReadAllBytes(destination).SequenceEqual(bytes), "Corrupt cache accepted");

            var accepted = File.ReadAllBytes(destination);
            using var badHandler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));
            using var badClient = new HttpClient(badHandler);
            // An existing valid destination must be retained if a replacement fails.
            await Reject(() => EndfieldIndexUpdates.Download(item with { Release = release with { Sha256 = new string('C', 64) } }, destination, null, CancellationToken.None, badClient), "Truncated download");
            await Reject(() => EndfieldIndexUpdates.Download(item with { Release = release with { Sha256 = new string('C', 64) } }, destination, null, CancellationToken.None, client), "Wrong SHA256");
            await Reject(() => EndfieldIndexUpdates.Download(item with { Release = release with { Bytes = 1 } }, destination, null, CancellationToken.None, client), "Oversized download");
            using var canceled = new CancellationTokenSource();canceled.Cancel();
            await Reject(() => EndfieldIndexUpdates.Download(item, Path.Combine(root, "canceled.gz"), null, canceled.Token, client), "Canceled download");
            Check(File.ReadAllBytes(destination).SequenceEqual(accepted) && !Directory.EnumerateFiles(root, "*.download", SearchOption.AllDirectories).Any(), "Failed download replaced the accepted package or left staging files");
            await Reject(() => Task.FromResult(EndfieldIndexUpdates.Parse(json.Replace("https://example.test/index", "http://example.test/index"))), "Insecure catalog URL");
            await Reject(() => Task.FromResult(EndfieldIndexUpdates.Parse(json.Replace(sha, "bad"))), "Invalid digest");
            using var missingHandler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
            using var missingClient = new HttpClient(missingHandler);
            Check(await EndfieldIndexUpdates.Check("https://example.test/catalog", CancellationToken.None, missingClient) == null, "Unpublished catalog should be optional");
            SharedToolsCache();
            Console.WriteLine("PASS shared catalog/download: GitHub and direct catalogs, cache reuse, corrupt-cache replacement, SHA/size/HTTPS validation, cancellation and failed-publication preservation");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void SharedToolsCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "eff-shared-tools-cache-" + Guid.NewGuid().ToString("N"));
        var originalRoot = Environment.GetEnvironmentVariable("EFF_RESOURCE_ROOT");
        var originalLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(root, "local"));
            Environment.SetEnvironmentVariable("EFF_RESOURCE_ROOT", null);
            EndfieldSharedResources.SetResourceRoot(root);
            var digest = new string('a', 64);
            var package = Path.Combine(root, "tools", digest);
            var native = Path.Combine(package, "nativepack");
            Directory.CreateDirectory(native);
            Directory.CreateDirectory(Path.Combine(package, "vendor", "PIL"));
            foreach (var name in new[] { "eff_resource_pack.exe", "eff_source_index.exe", "eff_author_core.dll", "package-protocol.json" })
                File.WriteAllText(Path.Combine(native, name), "fixture");
            File.WriteAllText(Path.Combine(package, "vendor", "PIL", "__init__.py"), "");
            File.WriteAllBytes(Path.Combine(package, "vendor", "PIL", "_imaging.cp311-win_amd64.pyd"), []);
            File.WriteAllBytes(Path.Combine(package, "vendor", "PIL", "_imaging.cp313-win_amd64.pyd"), []);
            File.WriteAllText(Path.Combine(root, "tools", "current.json"), JsonSerializer.Serialize(new
            {
                format = "EFF_SHARED_NATIVE_TOOLS_INSTALL/1",
                sha256 = digest,
                release = "fixture"
            }));
            Check(EndfieldSharedNativeTools.FindInstalledCompiler(root) == Path.Combine(native, "eff_resource_pack.exe"),
                "AnimeStudio did not discover tools installed by another client");
            Check(EndfieldSharedResources.ResourceRoot == Path.GetFullPath(root), "Shared selected root was not persisted");
            Check(Environment.GetEnvironmentVariable("EFF_RESOURCE_ROOT") is null,
                "Selecting a shared resource root must not pin a process-local override");
            Console.WriteLine("PASS shared tool cache: resource root and current.json package layout are reusable across clients");
        }
        finally
        {
            Environment.SetEnvironmentVariable("EFF_RESOURCE_ROOT", originalRoot);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocal);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    internal static void Registry(EndfieldVfsArchive archive, string workspace, EndfieldVfsArchive other, string otherWorkspace)
    {
        var root = Path.Combine(Path.GetTempPath(), "eff-index-registry-" + Guid.NewGuid().ToString("N"));
        var original = Environment.GetEnvironmentVariable("EFF_RESOURCE_ROOT");
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        Environment.SetEnvironmentVariable("EFF_RESOURCE_ROOT", root);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(root, "metadata"));
        try
        {
            void ClearSelectedWorkspace()
            {
                Directory.CreateDirectory(EndfieldSharedResources.MetadataRoot);
                File.WriteAllText(EndfieldSharedResources.SettingsPath, JsonSerializer.Serialize(new
                {
                    format = "EFF_SHARED_RESOURCES/1",
                    resourceRoot = root,
                    indexWorkspace = ""
                }));
            }

            EndfieldSharedIndexes.Remember(archive, workspace);
            EndfieldSharedIndexes.Remember(archive, workspace);
            ClearSelectedWorkspace();
            Check(EndfieldSharedIndexes.Workspaces(EndfieldSharedIndexes.Game(archive)).Single() == workspace, "Duplicate registry location");
            Check(!Directory.Exists(EndfieldSharedIndexes.DefaultWorkspace(EndfieldSharedIndexes.Game(archive))), "Registration copied an existing index");
            Check(EndfieldSharedIndexes.FindCurrent(archive, CancellationToken.None) == workspace, "Existing workspace not reusable");
            Check(EndfieldSharedIndexes.FindCurrent(other, CancellationToken.None) == null, "Registry crossed installation boundaries");
            EndfieldSharedIndexes.Remember(other, otherWorkspace);
            ClearSelectedWorkspace();
            Check(EndfieldSharedIndexes.FindCurrent(other, CancellationToken.None) == otherWorkspace && EndfieldSharedIndexes.FindCurrent(archive, CancellationToken.None) == workspace,
                "Registering another game lost existing location");
            Check(EndfieldSharedIndexes.DefaultWorkspace(EndfieldSharedIndexes.Game(archive)) != EndfieldSharedIndexes.DefaultWorkspace(EndfieldSharedIndexes.Game(other)), "New workspaces collide across games");
            try { EndfieldSharedIndexes.Remember(archive, Path.Combine(root, "missing"));throw new Exception("Invalid index registered"); } catch (InvalidDataException) { }
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();try { EndfieldSharedIndexes.Remember(archive, workspace, canceled.Token);throw new Exception("Canceled registration committed"); } catch (OperationCanceledException) { }
            }
            var registry = Path.Combine(root, "metadata", "EFF", "Resources", "registry.json");
            File.WriteAllText(registry, JsonSerializer.Serialize(new { format = "EFF_SHARED_INDEXES/1", entries = new[] {
                new { game = EndfieldSharedIndexes.Game(archive), workspace = Path.Combine(root, "missing") },
                new { game = EndfieldSharedIndexes.Game(archive), workspace } } }));
            Check(EndfieldSharedIndexes.FindCurrent(archive, CancellationToken.None) == workspace, "Stale first registration prevented fallback");
            File.WriteAllText(registry, "{broken");
            Check(!EndfieldSharedIndexes.Workspaces(EndfieldSharedIndexes.Game(archive)).Any() &&
                EndfieldSharedIndexes.FindCurrent(archive, CancellationToken.None, workspace) == workspace, "Broken registry prevented explicit legacy-workspace reuse");
            Console.WriteLine("PASS shared registry: zero-copy reuse, installation isolation, duplicate/current validation, cancellation, stale-entry and malformed-file fallback");
        }
        finally
        {
            Environment.SetEnvironmentVariable("EFF_RESOURCE_ROOT", original);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
