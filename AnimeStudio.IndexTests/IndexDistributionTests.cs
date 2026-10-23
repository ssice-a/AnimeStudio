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
            Console.WriteLine("PASS shared catalog/download: GitHub and direct catalogs, cache reuse, corrupt-cache replacement, SHA/size/HTTPS validation, cancellation and failed-publication preservation");
        }
        finally { Directory.Delete(root, true); }
    }

    internal static void Registry(EndfieldVfsArchive archive, string workspace, EndfieldVfsArchive other, string otherWorkspace)
    {
        var root = Path.Combine(Path.GetTempPath(), "eff-index-registry-" + Guid.NewGuid().ToString("N"));
        var original = Environment.GetEnvironmentVariable("EFF_RESOURCE_ROOT");Environment.SetEnvironmentVariable("EFF_RESOURCE_ROOT", root);
        try
        {
            EndfieldSharedIndexes.Remember(archive, workspace);
            EndfieldSharedIndexes.Remember(archive, workspace);
            Check(EndfieldSharedIndexes.Workspaces(EndfieldSharedIndexes.Game(archive)).Single() == workspace, "Duplicate registry location");
            Check(!Directory.Exists(EndfieldSharedIndexes.DefaultWorkspace(EndfieldSharedIndexes.Game(archive))), "Registration copied an existing index");
            Check(EndfieldSharedIndexes.FindCurrent(archive, CancellationToken.None) == workspace, "Existing workspace not reusable");
            Check(EndfieldSharedIndexes.FindCurrent(other, CancellationToken.None) == null, "Registry crossed installation boundaries");
            EndfieldSharedIndexes.Remember(other, otherWorkspace);
            Check(EndfieldSharedIndexes.FindCurrent(other, CancellationToken.None) == otherWorkspace && EndfieldSharedIndexes.FindCurrent(archive, CancellationToken.None) == workspace,
                "Registering another game lost existing location");
            Check(EndfieldSharedIndexes.DefaultWorkspace(EndfieldSharedIndexes.Game(archive)) != EndfieldSharedIndexes.DefaultWorkspace(EndfieldSharedIndexes.Game(other)), "New workspaces collide across games");
            try { EndfieldSharedIndexes.Remember(archive, Path.Combine(root, "missing"));throw new Exception("Invalid index registered"); } catch (InvalidDataException) { }
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();try { EndfieldSharedIndexes.Remember(archive, workspace, canceled.Token);throw new Exception("Canceled registration committed"); } catch (OperationCanceledException) { }
            }
            var registry = Path.Combine(root, "registry.json");
            File.WriteAllText(registry, JsonSerializer.Serialize(new { format = "EFF_SHARED_INDEXES/1", entries = new[] {
                new { game = EndfieldSharedIndexes.Game(archive), workspace = Path.Combine(root, "missing") },
                new { game = EndfieldSharedIndexes.Game(archive), workspace } } }));
            Check(EndfieldSharedIndexes.FindCurrent(archive, CancellationToken.None) == workspace, "Stale first registration prevented fallback");
            File.WriteAllText(registry, "{broken");
            Check(!EndfieldSharedIndexes.Workspaces(EndfieldSharedIndexes.Game(archive)).Any() &&
                EndfieldSharedIndexes.FindCurrent(archive, CancellationToken.None, workspace) == workspace, "Broken registry prevented explicit legacy-workspace reuse");
            Console.WriteLine("PASS shared registry: zero-copy reuse, installation isolation, duplicate/current validation, cancellation, stale-entry and malformed-file fallback");
        }
        finally { Environment.SetEnvironmentVariable("EFF_RESOURCE_ROOT", original);if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
