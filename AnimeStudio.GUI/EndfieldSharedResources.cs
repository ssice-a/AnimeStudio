using System;
using System.IO;
using System.Text.Json;

namespace AnimeStudio.GUI;

internal sealed record SharedResourceSettings(string Format, string ResourceRoot, string IndexWorkspace = "");

internal static class EndfieldSharedResources
{
    private const string Format = "EFF_SHARED_RESOURCES/1";
    internal static string MetadataRoot => Path.Combine(
        Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EFF", "Resources");
    internal static string SettingsPath => Path.Combine(MetadataRoot, "settings.json");
    internal static string ResourceRoot
    {
        get
        {
            if (Environment.GetEnvironmentVariable("EFF_RESOURCE_ROOT") is { Length: > 0 } configured)
                return Path.GetFullPath(configured);
            try
            {
                var settings = JsonSerializer.Deserialize<SharedResourceSettings>(File.ReadAllText(SettingsPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (settings?.Format == Format && !string.IsNullOrWhiteSpace(settings.ResourceRoot))
                    return Path.GetFullPath(settings.ResourceRoot);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
            return string.Empty;
        }
    }

    internal static string IndexWorkspace
    {
        get
        {
            try
            {
                var settings = JsonSerializer.Deserialize<SharedResourceSettings>(File.ReadAllText(SettingsPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (settings?.Format == Format && !string.IsNullOrWhiteSpace(settings.IndexWorkspace))
                    return Path.GetFullPath(settings.IndexWorkspace);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
            return string.Empty;
        }
    }

    internal static void SetResourceRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择资源根目录。", nameof(path));
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(MetadataRoot);
        var temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new SharedResourceSettings(Format, path, IndexWorkspace),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
            File.Move(temp, SettingsPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static void SetIndexWorkspace(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择 AnimeStudio 索引工作区。", nameof(path));
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(MetadataRoot);
        var settings = JsonSerializer.Deserialize<SharedResourceSettings>(File.Exists(SettingsPath)
            ? File.ReadAllText(SettingsPath) : "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var current = settings?.Format == Format ? settings : new SharedResourceSettings(Format, ResourceRoot);
        var updated = current with { IndexWorkspace = path };
        var temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(updated,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
            File.Move(temp, SettingsPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
