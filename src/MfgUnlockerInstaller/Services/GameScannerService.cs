using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MfgUnlockerInstaller.Data;
using MfgUnlockerInstaller.Models;

namespace MfgUnlockerInstaller.Services;

/// <summary>
/// Discovers installed PC games across Steam, Epic Games, Xbox/Game Pass, GOG, and manual entries.
/// </summary>
public sealed partial class GameScannerService
{
    private readonly ManifestStore _manifestStore;

    private static readonly HashSet<string> IgnoredSteamAppIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "228980",  // Steamworks Common Redistributables
        "1070560", // Steam Linux Runtime
        "1391110", // Steam Linux Runtime - Soldier
        "1493710", // Proton Experimental
        "1628350", // Steam Linux Runtime - Sniper
        "1826330", // Proton EasyAntiCheat Runtime
        "2180100", // Proton Hotfix
        "250820"   // SteamVR
    };

    public GameScannerService(ManifestStore manifestStore)
    {
        _manifestStore = manifestStore;
    }

    public Task<List<GameEntry>> ScanInstalledGamesAsync(CancellationToken ct = default)
        => Task.Run(() => ScanInstalledGames(ct), ct);

    public List<GameEntry> ScanInstalledGames(CancellationToken ct = default)
    {
        Dictionary<string, GameEntry> byTargetDir = new(StringComparer.OrdinalIgnoreCase);

        // Load manual custom games first so their chosen target exe takes priority.
        foreach (CustomGameRecord custom in _manifestStore.LoadCustomGames())
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(custom.RootDirectory) && !File.Exists(custom.ExecutablePath))
            {
                continue;
            }

            GameEntry entry = CreateGameEntry(
                custom.Name,
                "Manual",
                File.Exists(custom.ExecutablePath) ? custom.ExecutablePath : custom.RootDirectory,
                isManual: true,
                explicitRootDir: custom.RootDirectory);

            if (!string.IsNullOrEmpty(entry.TargetDirectory))
            {
                byTargetDir[ManifestStore.NormalizePathKey(entry.TargetDirectory)] = entry;
            }
        }

        foreach (GameEntry steamGame in ScanSteamGames(ct))
        {
            AddIfAbsent(byTargetDir, steamGame);
        }

        foreach (GameEntry epicGame in ScanEpicGames(ct))
        {
            AddIfAbsent(byTargetDir, epicGame);
        }

        foreach (GameEntry xboxGame in ScanXboxGames(ct))
        {
            AddIfAbsent(byTargetDir, xboxGame);
        }

        foreach (GameEntry gogGame in ScanGogGames(ct))
        {
            AddIfAbsent(byTargetDir, gogGame);
        }

        return byTargetDir.Values
            .OrderByDescending(g => g.Compatibility is not null)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public GameEntry CreateGameEntry(
        string name,
        string source,
        string rootOrExePath,
        bool isManual,
        string? explicitRootDir = null)
    {
        bool isExeFile = File.Exists(rootOrExePath)
                         && rootOrExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

        string rootDir = !string.IsNullOrWhiteSpace(explicitRootDir)
            ? ManifestStore.NormalizePathKey(explicitRootDir)
            : ManifestStore.NormalizePathKey(
                isExeFile ? Path.GetDirectoryName(rootOrExePath) ?? rootOrExePath : rootOrExePath);

        GameCompatibilityEntry? compatByName = CompatibilityDatabase.Match(name);

        string resolvedExe = isExeFile
            ? Path.GetFullPath(rootOrExePath)
            : ExeResolverService.ResolveGameExecutable(rootDir, compatByName);

        GameCompatibilityEntry? finalCompat = compatByName ?? CompatibilityDatabase.Match(name, resolvedExe);

        if (finalCompat is not null && compatByName is null && !isExeFile)
        {
            string reResolved = ExeResolverService.ResolveGameExecutable(rootDir, finalCompat);
            if (!string.IsNullOrEmpty(reResolved))
            {
                resolvedExe = reResolved;
            }
        }

        string targetDir = !string.IsNullOrWhiteSpace(resolvedExe) && File.Exists(resolvedExe)
            ? ManifestStore.NormalizePathKey(Path.GetDirectoryName(resolvedExe) ?? rootDir)
            : rootDir;

        return new GameEntry
        {
            Id = ManifestStore.NormalizePathKey(string.IsNullOrEmpty(targetDir) ? rootDir : targetDir),
            Name = name,
            Source = source,
            RootDirectory = rootDir,
            TargetDirectory = targetDir,
            ExecutablePath = resolvedExe,
            IsManual = isManual,
            Compatibility = finalCompat
        };
    }

    public static List<string> ParseSteamLibraryPaths(string vdfContent)
    {
        List<string> paths = [];
        if (string.IsNullOrWhiteSpace(vdfContent))
        {
            return paths;
        }

        foreach (Match match in VdfPathRegex().Matches(vdfContent))
        {
            string rawPath = match.Groups[1].Value.Replace(@"\\", @"\").Trim();
            if (!string.IsNullOrEmpty(rawPath) && !paths.Contains(rawPath, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(rawPath);
            }
        }

        return paths;
    }

    public static (string? AppId, string? Name, string? InstallDir) ParseAppManifest(string acfContent)
    {
        if (string.IsNullOrWhiteSpace(acfContent))
        {
            return (null, null, null);
        }

        string? appId = null;
        string? name = null;
        string? installDir = null;

        foreach (Match match in AcfKeyValueRegex().Matches(acfContent))
        {
            string key = match.Groups[1].Value;
            string val = match.Groups[2].Value.Replace(@"\\", @"\");

            if (string.Equals(key, "appid", StringComparison.OrdinalIgnoreCase) && appId is null)
            {
                appId = val;
            }
            else if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase) && name is null)
            {
                name = val;
            }
            else if (string.Equals(key, "installdir", StringComparison.OrdinalIgnoreCase) && installDir is null)
            {
                installDir = val;
            }
        }

        return (appId, name, installDir);
    }

    private List<GameEntry> ScanSteamGames(CancellationToken ct)
    {
        List<GameEntry> games = [];
        try
        {
            HashSet<string> libraryRoots = new(StringComparer.OrdinalIgnoreCase);

            foreach (string steamRoot in GetSteamInstallRoots())
            {
                libraryRoots.Add(steamRoot);
                string vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdfPath))
                {
                    foreach (string libPath in ParseSteamLibraryPaths(File.ReadAllText(vdfPath)))
                    {
                        libraryRoots.Add(libPath);
                    }
                }
            }

            foreach (string libRoot in libraryRoots)
            {
                ct.ThrowIfCancellationRequested();
                string steamAppsDir = Path.Combine(libRoot, "steamapps");
                if (!Directory.Exists(steamAppsDir))
                {
                    continue;
                }

                foreach (string acfFile in Directory.EnumerateFiles(steamAppsDir, "appmanifest_*.acf"))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        (string? appId, string? name, string? installDir) = ParseAppManifest(File.ReadAllText(acfFile));
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir))
                        {
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(appId) && IgnoredSteamAppIds.Contains(appId))
                        {
                            continue;
                        }

                        if (name.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Steamworks Common", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string gameRoot = Path.Combine(steamAppsDir, "common", installDir);
                        if (!Directory.Exists(gameRoot))
                        {
                            continue;
                        }

                        games.Add(CreateGameEntry(name, "Steam", gameRoot, isManual: false));
                    }
                    catch
                    {
                        // Skip malformed manifest files.
                    }
                }
            }
        }
        catch
        {
            // Ignore Steam discovery errors.
        }

        return games;
    }

    private static IEnumerable<string> GetSteamInstallRoots()
    {
        List<string> candidates = [];

        try
        {
            using RegistryKey? hkcu = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (hkcu?.GetValue("SteamPath") is string p1 && Directory.Exists(p1))
            {
                candidates.Add(p1);
            }

            using RegistryKey? hklm32 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (hklm32?.GetValue("InstallPath") is string p2 && Directory.Exists(p2))
            {
                candidates.Add(p2);
            }

            using RegistryKey? hklm64 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (hklm64?.GetValue("InstallPath") is string p3 && Directory.Exists(p3))
            {
                candidates.Add(p3);
            }
        }
        catch
        {
            // Ignore registry errors.
        }

        string defaultSteam = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam");
        if (Directory.Exists(defaultSteam))
        {
            candidates.Add(defaultSteam);
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private List<GameEntry> ScanEpicGames(CancellationToken ct)
    {
        List<GameEntry> games = [];
        try
        {
            string manifestsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic",
                "EpicGamesLauncher",
                "Data",
                "Manifests");

            if (Directory.Exists(manifestsDir))
            {
                foreach (string itemFile in Directory.EnumerateFiles(manifestsDir, "*.item"))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(itemFile));
                        JsonElement root = doc.RootElement;

                        if (root.TryGetProperty("CatalogNamespace", out JsonElement nsElem)
                            && string.Equals(nsElem.GetString(), "ue", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string? displayName = root.TryGetProperty("DisplayName", out JsonElement dn) ? dn.GetString() : null;
                        string? installLoc = root.TryGetProperty("InstallLocation", out JsonElement il) ? il.GetString() : null;

                        if (string.IsNullOrWhiteSpace(displayName)
                            || string.IsNullOrWhiteSpace(installLoc)
                            || !Directory.Exists(installLoc))
                        {
                            continue;
                        }

                        string? launchExe = root.TryGetProperty("LaunchExecutable", out JsonElement le) ? le.GetString() : null;
                        string fullLaunchExe = !string.IsNullOrWhiteSpace(launchExe)
                            ? Path.Combine(installLoc, launchExe)
                            : string.Empty;

                        string rootOrHint = File.Exists(fullLaunchExe) ? installLoc : installLoc;
                        GameEntry entry = CreateGameEntry(displayName, "Epic", rootOrHint, isManual: false);

                        if (string.IsNullOrWhiteSpace(entry.ExecutablePath) && File.Exists(fullLaunchExe))
                        {
                            entry = CreateGameEntry(displayName, "Epic", fullLaunchExe, isManual: false, explicitRootDir: installLoc);
                        }

                        games.Add(entry);
                    }
                    catch
                    {
                        // Ignore malformed Epic item files.
                    }
                }
            }

            string launcherInstalledPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic",
                "UnrealEngineLauncher",
                "LauncherInstalled.dat");

            if (File.Exists(launcherInstalledPath))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(launcherInstalledPath));
                if (doc.RootElement.TryGetProperty("InstallationList", out JsonElement list)
                    && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in list.EnumerateArray())
                    {
                        ct.ThrowIfCancellationRequested();
                        string? installLoc = item.TryGetProperty("InstallLocation", out JsonElement il) ? il.GetString() : null;
                        string? appName = item.TryGetProperty("AppName", out JsonElement an) ? an.GetString() : null;
                        if (string.IsNullOrWhiteSpace(installLoc) || !Directory.Exists(installLoc))
                        {
                            continue;
                        }

                        string folderName = Path.GetFileName(installLoc.TrimEnd(Path.DirectorySeparatorChar));
                        string displayName = string.IsNullOrWhiteSpace(folderName) ? appName ?? "Epic Game" : folderName;
                        if (!games.Any(g => string.Equals(
                                ManifestStore.NormalizePathKey(g.RootDirectory),
                                ManifestStore.NormalizePathKey(installLoc),
                                StringComparison.OrdinalIgnoreCase)))
                        {
                            games.Add(CreateGameEntry(displayName, "Epic", installLoc, isManual: false));
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore Epic discovery errors.
        }

        return games;
    }

    private List<GameEntry> ScanXboxGames(CancellationToken ct)
    {
        List<GameEntry> games = [];
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                ct.ThrowIfCancellationRequested();
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                {
                    continue;
                }

                string xboxGamesDir = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
                if (!Directory.Exists(xboxGamesDir))
                {
                    continue;
                }

                foreach (string gameDir in Directory.EnumerateDirectories(xboxGamesDir))
                {
                    ct.ThrowIfCancellationRequested();
                    string gameName = Path.GetFileName(gameDir);
                    if (string.IsNullOrWhiteSpace(gameName))
                    {
                        continue;
                    }

                    string contentDir = Path.Combine(gameDir, "Content");
                    string rootToInspect = Directory.Exists(contentDir) ? contentDir : gameDir;
                    games.Add(CreateGameEntry(gameName, "Xbox", rootToInspect, isManual: false, explicitRootDir: gameDir));
                }
            }
        }
        catch
        {
            // Ignore Xbox discovery errors.
        }

        return games;
    }

    private List<GameEntry> ScanGogGames(CancellationToken ct)
    {
        List<GameEntry> games = [];
        try
        {
            string[] regRoots =
            [
                @"SOFTWARE\WOW6432Node\GOG.com\Games",
                @"SOFTWARE\GOG.com\Games"
            ];

            foreach (string regPath in regRoots)
            {
                using RegistryKey? baseKey = Registry.LocalMachine.OpenSubKey(regPath);
                if (baseKey is null)
                {
                    continue;
                }

                foreach (string subKeyName in baseKey.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    using RegistryKey? gameKey = baseKey.OpenSubKey(subKeyName);
                    if (gameKey is null)
                    {
                        continue;
                    }

                    string? gameName = gameKey.GetValue("gameName") as string;
                    string? path = gameKey.GetValue("path") as string;
                    string? exe = gameKey.GetValue("exe") as string;

                    if (string.IsNullOrWhiteSpace(gameName) || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                    {
                        continue;
                    }

                    string target = !string.IsNullOrWhiteSpace(exe) && File.Exists(exe) ? exe : path;
                    games.Add(CreateGameEntry(gameName, "GOG", target, isManual: false, explicitRootDir: path));
                }
            }
        }
        catch
        {
            // Ignore GOG discovery errors.
        }

        return games;
    }

    private static void AddIfAbsent(Dictionary<string, GameEntry> dict, GameEntry entry)
    {
        string key = ManifestStore.NormalizePathKey(entry.TargetDirectory);
        if (!string.IsNullOrEmpty(key) && !dict.ContainsKey(key))
        {
            dict[key] = entry;
        }
    }

    [GeneratedRegex("^\\s*\"path\"\\s+\"([^\"]+)\"", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex VdfPathRegex();

    [GeneratedRegex("^\\s*\"([A-Za-z0-9_]+)\"\\s+\"([^\"]*)\"", RegexOptions.Multiline)]
    private static partial Regex AcfKeyValueRegex();
}
