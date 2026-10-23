using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AnimeStudio.GUI;

// A location registry, not evidence of compatibility. Every consumer still
// validates the selected index against its current game before using it.
internal static class EndfieldSharedIndexes
{
    private sealed record Entry(string Game, string Workspace);
    private sealed record Registry(string Format, Entry[] Entries);
    private const string Format = "EFF_SHARED_INDEXES/1";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    internal static string Root => Path.GetFullPath(Environment.GetEnvironmentVariable("EFF_RESOURCE_ROOT") is { Length: > 0 } configured
        ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EFF", "Resources"));
    internal static string Game(EndfieldVfsArchive archive) => archive.GameDirectory ?? archive.VfsRoot;
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Replace('\\', '/').ToLowerInvariant();

    private static Entry[] Read()
    {
        try
        {
            var registry = JsonSerializer.Deserialize<Registry>(File.ReadAllText(Path.Combine(Root, "registry.json")), Json);
            return registry?.Format == Format && registry.Entries != null ? registry.Entries : Array.Empty<Entry>();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return Array.Empty<Entry>(); }
    }

    internal static IEnumerable<string> Workspaces(string game)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Read())
        {
            string workspace;
            try
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Game) || string.IsNullOrWhiteSpace(entry.Workspace) || Normalize(entry.Game) != Normalize(game)) continue;
                workspace = Path.GetFullPath(entry.Workspace);
            }
            catch (ArgumentException) { continue; }
            if (seen.Add(workspace)) yield return workspace;
        }
    }

    internal static string DefaultWorkspace(string game) => Path.Combine(Root, "installations",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(game))))[..24], "workspace");

    internal static string FindCurrent(EndfieldVfsArchive archive, CancellationToken token, params string[] additional)
    {
        foreach (var workspace in Workspaces(Game(archive)).Concat(additional).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (EndfieldVfsAssetIndexBuilder.TryGetOrRebindCurrentIndex(archive, workspace, out _, token)) return workspace;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }

    internal static void Remember(EndfieldVfsArchive archive, string workspace, CancellationToken token = default)
    {
        if (!EndfieldVfsAssetIndexBuilder.TryGetCurrentIndex(archive, workspace, out _))
            throw new InvalidDataException("Only a complete current index can be shared");
        workspace = Path.GetFullPath(workspace);var game = Path.GetFullPath(Game(archive));
        Directory.CreateDirectory(Root);
        using var gate = Lock(token);
        var entries = Read().Where(e => e != null && !string.Equals(e.Workspace, workspace, StringComparison.OrdinalIgnoreCase)).ToList();
        entries.Insert(0, new(game, workspace));
        var path = Path.Combine(Root, "registry.json");var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new Registry(Format, entries.ToArray()), Json));
            token.ThrowIfCancellationRequested();File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static bool TryRemember(EndfieldVfsArchive archive, string workspace, CancellationToken token = default)
    {
        try { Remember(archive, workspace, token);return true; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    private static FileStream Lock(CancellationToken token)
    {
        for (int retry = 0; ; retry++)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(Root, "registry.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (retry < 20) { token.WaitHandle.WaitOne(50); }
        }
    }
}
