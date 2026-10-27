using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeStudio.GUI;

internal sealed record IndexDownload(EndfieldIndexRelease Release, string Url);
// Keep the published catalog schema compatible with EFF Repair.
internal sealed record RepairCatalog(string Format, string ToolVersion, string ToolUrl, IndexDownload[] Indexes);

internal static class EndfieldIndexUpdates
{
    internal static string DefaultSource => Environment.GetEnvironmentVariable("EFF_INDEX_CATALOG") is { Length: > 0 } source
        ? source : "https://api.github.com/repos/ssice-a/EFF/releases/latest";
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EFF-Index/1.0");return client;
    }
    internal static Uri Https(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new IOException("更新源或下载地址必须使用 HTTPS");return uri;
    }
    internal static async Task<RepairCatalog> Check(string source, CancellationToken token, HttpClient client = null)
    {
        client ??= Client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(Https(source), timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.TryGetProperty("assets", out var assets))
        {
            var asset = assets.EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == "eff-index-catalog.json");
            if (asset.ValueKind == JsonValueKind.Undefined) return null;
            using var catalog = await client.GetAsync(Https(asset.GetProperty("browser_download_url").GetString()), timeout.Token).ConfigureAwait(false);
            catalog.EnsureSuccessStatusCode();return Parse(await catalog.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        }
        return Parse(root.GetRawText());
    }
    internal static RepairCatalog Parse(string json)
    {
        var result = JsonSerializer.Deserialize<RepairCatalog>(json) ?? throw new IOException("空更新目录");
        if (result.Format != "EFF_REPAIR_CATALOG/1" || result.Indexes is null) throw new IOException("不支持的更新目录结构");
        static bool Hash(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
        foreach (var index in result.Indexes)
        {
            if (index == null) throw new IOException("索引发布信息不完整");
            Https(index.Url);var release = index.Release;
            if (release == null || release.Format != "EFF_INDEX_RELEASE/1" || release.Bytes <= 0 || release.IndexBytes <= 0 ||
                !Hash(release.Sha256) || !Hash(release.IndexSha256) || release.GameContent == null ||
                !release.GameContent.StartsWith("EFF_BUNDLE_CONTENT/1:", StringComparison.Ordinal) || !Hash(release.GameContent[21..]))
                throw new IOException("索引发布信息不完整");
        }
        if (!string.IsNullOrEmpty(result.ToolUrl)) Https(result.ToolUrl);return result;
    }
    internal static string CachedPackage(IndexDownload index, string workspace) => Path.Combine(Path.GetFullPath(workspace), ".eff-index-downloads",
        index.Release.GameContent[21..], index.Release.Sha256.ToUpperInvariant() + ".effindex.gz");

    internal static async Task Download(IndexDownload index, string destination, IProgress<string> progress, CancellationToken token, HttpClient client = null)
    {
        client ??= Client;destination = Path.GetFullPath(destination);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) && new FileInfo(destination).Length == index.Release.Bytes)
        {
            using var existing = File.OpenRead(destination);
            if (Convert.ToHexString(await SHA256.HashDataAsync(existing, token).ConfigureAwait(false)).Equals(index.Release.Sha256, StringComparison.OrdinalIgnoreCase))
            { progress?.Report("复用已校验的索引压缩包");return; }
        }
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var response = await client.GetAsync(Https(index.Url), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);response.EnsureSuccessStatusCode();
            using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytes = 0;var reported = DateTime.MinValue;
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            {
                var buffer = new byte[1024 * 1024];int n;
                while ((n = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    bytes += n;if (bytes > index.Release.Bytes) throw new IOException("下载超出发布文件大小");
                    hash.AppendData(buffer, 0, n);await output.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
                    if (DateTime.UtcNow - reported > TimeSpan.FromMilliseconds(250))
                    { progress?.Report($"下载索引 {bytes / 1048576.0:F1} / {index.Release.Bytes / 1048576.0:F1} MB");reported = DateTime.UtcNow; }
                }
            }
            if (bytes != index.Release.Bytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(index.Release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("索引下载大小或 SHA256 校验失败");
            token.ThrowIfCancellationRequested();File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
