using System.Diagnostics;
using System.IO;
using MfgUnlockerInstaller.Data;
using MfgUnlockerInstaller.Models;

namespace MfgUnlockerInstaller.Services;

/// <summary>
/// Safely installs, upgrades, switches DLL slot, and uninstalls MFG Unlocker in a game's target folder.
/// </summary>
public sealed class InstallerService
{
    private readonly ManifestStore _manifestStore;

    public InstallerService(ManifestStore manifestStore)
    {
        _manifestStore = manifestStore;
    }

    public static bool IsGameRunning(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.ExecutablePath))
        {
            return false;
        }

        string procName = Path.GetFileNameWithoutExtension(game.ExecutablePath);
        if (string.IsNullOrWhiteSpace(procName))
        {
            return false;
        }

        Process[] processes = [];
        try
        {
            processes = Process.GetProcessesByName(procName);
            foreach (Process proc in processes)
            {
                try
                {
                    string? fullPath = proc.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(fullPath)
                        || string.Equals(
                            ManifestStore.NormalizePathKey(fullPath),
                            ManifestStore.NormalizePathKey(game.ExecutablePath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // If MainModule cannot be queried (e.g. elevated process), assume matching process name is running.
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (Process proc in processes)
            {
                proc.Dispose();
            }
        }

        return false;
    }

    public Task<InstalledManifestEntry> InstallAsync(
        GameEntry game,
        string targetDllName,
        ReleaseInfo release,
        CancellationToken ct = default)
        => Task.Run(() => Install(game, targetDllName, release), ct);

    public InstalledManifestEntry Install(GameEntry game, string targetDllName, ReleaseInfo release)
    {
        if (string.IsNullOrWhiteSpace(game.TargetDirectory) || !Directory.Exists(game.TargetDirectory))
        {
            throw new DirectoryNotFoundException($"Game directory does not exist: {game.TargetDirectory}");
        }

        if (!ProxyDllCatalog.IsSupported(targetDllName))
        {
            throw new ArgumentException($"DLL name '{targetDllName}' is not in the supported list.", nameof(targetDllName));
        }

        if (!release.HasValidCachedFile || string.IsNullOrWhiteSpace(release.CachedDllPath))
        {
            throw new FileNotFoundException("MFGUnlocker.dll is not cached locally yet.");
        }

        if (IsGameRunning(game))
        {
            throw new InvalidOperationException("Game is currently running. Close the game completely before installing.");
        }

        InstalledManifestEntry? existingManifest = _manifestStore.GetInstalledEntry(game.TargetDirectory);
        string targetPath = Path.Combine(game.TargetDirectory, targetDllName);
        string newDllSha256 = release.ExpectedDllSha256 ?? ReleaseService.ComputeSha256(release.CachedDllPath);
        string? binkBackupName = null;

        // If switching away from an earlier installed slot in the same folder, remove the old one first.
        if (existingManifest is not null
            && !string.Equals(existingManifest.InstalledDllName, targetDllName, StringComparison.OrdinalIgnoreCase))
        {
            RemoveInstalledSlot(game.TargetDirectory, existingManifest.InstalledDllName, existingManifest.BinkHookedBackupName);
        }

        if (ProxyDllCatalog.IsBinkDll(targetDllName))
        {
            string hookedName = ProxyDllCatalog.GetBinkHookedName(targetDllName)!;
            string hookedPath = Path.Combine(game.TargetDirectory, hookedName);

            bool targetIsAlreadyMfg = File.Exists(targetPath)
                                      && InstallAnalyzerService.IsMfgUnlockerFile(
                                          targetPath,
                                          targetDllName,
                                          existingManifest,
                                          release,
                                          newDllSha256);

            if (!targetIsAlreadyMfg)
            {
                if (!File.Exists(targetPath) && !File.Exists(hookedPath))
                {
                    throw new InvalidOperationException(
                        $"Cannot use {targetDllName}: original {targetDllName} was not found to hook.");
                }

                if (File.Exists(targetPath) && !File.Exists(hookedPath))
                {
                    File.Move(targetPath, hookedPath);
                }
            }

            File.Copy(release.CachedDllPath, targetPath, overwrite: true);
            binkBackupName = File.Exists(hookedPath) ? hookedName : null;
        }
        else
        {
            if (File.Exists(targetPath))
            {
                bool isExistingMfg = InstallAnalyzerService.IsMfgUnlockerFile(
                    targetPath,
                    targetDllName,
                    existingManifest,
                    release,
                    newDllSha256);

                if (!isExistingMfg)
                {
                    throw new InvalidOperationException(
                        $"File '{targetDllName}' already exists and belongs to the game or another mod. Refusing to overwrite — choose a different DLL name.");
                }
            }

            File.Copy(release.CachedDllPath, targetPath, overwrite: true);
        }

        InstalledManifestEntry newEntry = new()
        {
            TargetDirectory = ManifestStore.NormalizePathKey(game.TargetDirectory),
            GameName = game.Name,
            InstalledDllName = targetDllName,
            VersionTag = release.TagName,
            DllSha256 = newDllSha256,
            BinkHookedBackupName = binkBackupName,
            InstalledAtUtc = DateTime.UtcNow
        };

        _manifestStore.UpsertInstalledEntry(newEntry);
        return newEntry;
    }

    public Task UninstallAsync(GameEntry game, InstallAnalysis analysis, bool deleteLogs, CancellationToken ct = default)
        => Task.Run(() => Uninstall(game, analysis, deleteLogs), ct);

    public void Uninstall(GameEntry game, InstallAnalysis analysis, bool deleteLogs)
    {
        if (string.IsNullOrWhiteSpace(game.TargetDirectory) || !Directory.Exists(game.TargetDirectory))
        {
            _manifestStore.RemoveInstalledEntry(game.TargetDirectory);
            return;
        }

        if (IsGameRunning(game))
        {
            throw new InvalidOperationException("Game is currently running. Close the game completely before uninstalling.");
        }

        InstalledManifestEntry? manifest = _manifestStore.GetInstalledEntry(game.TargetDirectory);
        string? installedDll = analysis.InstalledDllName ?? manifest?.InstalledDllName;

        if (!string.IsNullOrWhiteSpace(installedDll))
        {
            string? backupName = manifest?.BinkHookedBackupName
                                 ?? (ProxyDllCatalog.IsBinkDll(installedDll)
                                     ? ProxyDllCatalog.GetBinkHookedName(installedDll)
                                     : null);

            RemoveInstalledSlot(game.TargetDirectory, installedDll, backupName);
        }

        if (deleteLogs)
        {
            try
            {
                foreach (string logFile in Directory.EnumerateFiles(game.TargetDirectory, "RTXMFG-*.log"))
                {
                    File.Delete(logFile);
                }
            }
            catch
            {
                // Non-fatal if a log file cannot be deleted.
            }
        }

        _manifestStore.RemoveInstalledEntry(game.TargetDirectory);
    }

    private static void RemoveInstalledSlot(string targetDirectory, string dllName, string? binkHookedBackupName)
    {
        string installedPath = Path.Combine(targetDirectory, dllName);
        if (File.Exists(installedPath))
        {
            File.Delete(installedPath);
        }

        if (!string.IsNullOrWhiteSpace(binkHookedBackupName))
        {
            string hookedPath = Path.Combine(targetDirectory, binkHookedBackupName);
            if (File.Exists(hookedPath) && !File.Exists(installedPath))
            {
                File.Move(hookedPath, installedPath);
            }
        }
    }
}
