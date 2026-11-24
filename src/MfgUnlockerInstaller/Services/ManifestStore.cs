using System.IO;
using System.Text;
using System.Text.Json;
using MfgUnlockerInstaller.Models;

namespace MfgUnlockerInstaller.Services;

/// <summary>
/// Persists installation manifests, custom user games, and downloaded release cache metadata.
/// </summary>
public sealed class ManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string BaseDirectory { get; }
    public string CacheDirectory { get; }
    public string ManifestFilePath { get; }
    public string CustomGamesFilePath { get; }
    public string ReleaseMetaFilePath { get; }

    public ManifestStore(string? baseDirectory = null)
    {
        BaseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MfgUnlockerInstaller")
            : Path.GetFullPath(baseDirectory);

        CacheDirectory = Path.Combine(BaseDirectory, "cache");
        ManifestFilePath = Path.Combine(BaseDirectory, "installed_manifest.json");
        CustomGamesFilePath = Path.Combine(BaseDirectory, "custom_games.json");
        ReleaseMetaFilePath = Path.Combine(BaseDirectory, "release_meta.json");
    }

    public static string NormalizePathKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    public string GetCacheDllPathForTag(string tagName)
    {
        string safeTag = SanitizeFileName(string.IsNullOrWhiteSpace(tagName) ? "latest" : tagName);
        return Path.Combine(CacheDirectory, safeTag, "MFGUnlocker.dll");
    }

    public List<InstalledManifestEntry> LoadInstalledManifests()
        => ReadJsonOrDefault<List<InstalledManifestEntry>>(ManifestFilePath) ?? [];

    public void SaveInstalledManifests(List<InstalledManifestEntry> entries)
        => WriteJsonAtomic(ManifestFilePath, entries);

    public InstalledManifestEntry? GetInstalledEntry(string targetDirectory)
    {
        string key = NormalizePathKey(targetDirectory);
        return LoadInstalledManifests()
            .FirstOrDefault(e => string.Equals(NormalizePathKey(e.TargetDirectory), key, StringComparison.OrdinalIgnoreCase));
    }

    public void UpsertInstalledEntry(InstalledManifestEntry entry)
    {
        List<InstalledManifestEntry> entries = LoadInstalledManifests();
        string key = NormalizePathKey(entry.TargetDirectory);
        int idx = entries.FindIndex(e =>
            string.Equals(NormalizePathKey(e.TargetDirectory), key, StringComparison.OrdinalIgnoreCase));

        if (idx >= 0)
        {
            entries[idx] = entry;
        }
        else
        {
            entries.Add(entry);
        }

        SaveInstalledManifests(entries);
    }

    public void RemoveInstalledEntry(string targetDirectory)
    {
        List<InstalledManifestEntry> entries = LoadInstalledManifests();
        string key = NormalizePathKey(targetDirectory);
        int removed = entries.RemoveAll(e =>
            string.Equals(NormalizePathKey(e.TargetDirectory), key, StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
        {
            SaveInstalledManifests(entries);
        }
    }

    public List<CustomGameRecord> LoadCustomGames()
        => ReadJsonOrDefault<List<CustomGameRecord>>(CustomGamesFilePath) ?? [];

    public void SaveCustomGames(List<CustomGameRecord> games)
        => WriteJsonAtomic(CustomGamesFilePath, games);

    public void AddOrUpdateCustomGame(CustomGameRecord record)
    {
        List<CustomGameRecord> games = LoadCustomGames();
        string exeDirKey = NormalizePathKey(Path.GetDirectoryName(record.ExecutablePath) ?? record.RootDirectory);

        int idx = games.FindIndex(g =>
        {
            string existingKey = NormalizePathKey(Path.GetDirectoryName(g.ExecutablePath) ?? g.RootDirectory);
            return string.Equals(existingKey, exeDirKey, StringComparison.OrdinalIgnoreCase);
        });

        if (idx >= 0)
        {
            games[idx] = record;
        }
        else
        {
            games.Add(record);
        }

        SaveCustomGames(games);
    }

    public void RemoveCustomGame(string targetDirectoryOrExe)
    {
        List<CustomGameRecord> games = LoadCustomGames();
        string targetKey = NormalizePathKey(
            File.Exists(targetDirectoryOrExe)
                ? Path.GetDirectoryName(targetDirectoryOrExe) ?? targetDirectoryOrExe
                : targetDirectoryOrExe);

        int removed = games.RemoveAll(g =>
        {
            string gDirKey = NormalizePathKey(Path.GetDirectoryName(g.ExecutablePath) ?? g.RootDirectory);
            return string.Equals(gDirKey, targetKey, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(NormalizePathKey(g.ExecutablePath), NormalizePathKey(targetDirectoryOrExe), StringComparison.OrdinalIgnoreCase);
        });

        if (removed > 0)
        {
            SaveCustomGames(games);
        }
    }

    public ReleaseInfo? LoadCachedReleaseInfo()
    {
        ReleaseInfo? info = ReadJsonOrDefault<ReleaseInfo>(ReleaseMetaFilePath);
        if (info is not null)
        {
            info.IsCached = info.HasValidCachedFile;
        }

        return info;
    }

    public void SaveCachedReleaseInfo(ReleaseInfo info)
        => WriteJsonAtomic(ReleaseMetaFilePath, info);

    private T? ReadJsonOrDefault<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void WriteJsonAtomic<T>(string path, T data)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = path + ".tmp";
        string json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, path, overwrite: true);
    }

    private static string SanitizeFileName(string input)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder sb = new(input.Length);
        foreach (char c in input)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }

        return sb.ToString();
    }
}
