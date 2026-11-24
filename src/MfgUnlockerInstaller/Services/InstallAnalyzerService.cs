using System.Diagnostics;
using System.IO;
using MfgUnlockerInstaller.Data;
using MfgUnlockerInstaller.Models;

namespace MfgUnlockerInstaller.Services;

/// <summary>
/// Analyzes a game's target directory for existing installations, conflicts, anti-cheat, and best DLL selection.
/// </summary>
public sealed class InstallAnalyzerService
{
    private static readonly string[] AntiCheatDirectories =
    [
        "EasyAntiCheat",
        "EasyAntiCheat_EOS",
        "BattlEye",
        "EAAntiCheat",
        "GameGuard"
    ];

    private static readonly string[] AntiCheatFiles =
    [
        "EasyAntiCheat_EOS.exe",
        "EasyAntiCheat.exe",
        "BEService.exe",
        "BEService_x64.exe",
        "BEDaisy.sys",
        "EAAntiCheat.GameServiceLauncher.exe",
        "HoYoKProtect.sys",
        "randgrid.sys"
    ];

    private readonly ManifestStore _manifestStore;

    public InstallAnalyzerService(ManifestStore manifestStore)
    {
        _manifestStore = manifestStore;
    }

    public InstallAnalysis Analyze(GameEntry game, ReleaseInfo? currentRelease)
    {
        InstallAnalysis analysis = new();

        CheckAntiCheat(game, analysis);

        if (string.IsNullOrWhiteSpace(game.TargetDirectory) || !Directory.Exists(game.TargetDirectory))
        {
            analysis.RecommendedDll = game.Compatibility?.PreferredDll ?? "version.dll";
            analysis.RecommendationReason = "Target directory is invalid or game executable was not found.";
            return analysis;
        }

        InstalledManifestEntry? manifest = _manifestStore.GetInstalledEntry(game.TargetDirectory);
        string? cachedDllSha = currentRelease is { HasValidCachedFile: true }
            ? (currentRelease.ExpectedDllSha256 ?? ReleaseService.ComputeSha256(currentRelease.CachedDllPath!))
            : null;

        // Check legacy mod files, config JSON, and log files
        foreach (string legacy in ProxyDllCatalog.LegacyFileNames)
        {
            if (File.Exists(Path.Combine(game.TargetDirectory, legacy)))
            {
                analysis.IsLegacyInstalled = true;
                analysis.LegacyFilesFound.Add(legacy);
            }
        }

        analysis.HasConfigJson = File.Exists(Path.Combine(game.TargetDirectory, "RTXMFG-Universal.json"));
        analysis.Optimization = OptimizationService.Load(game.TargetDirectory);
        try
        {
            analysis.LogFileCount = Directory.EnumerateFiles(game.TargetDirectory, "RTXMFG-*.log").Count();
        }
        catch
        {
            analysis.LogFileCount = 0;
        }

        // Inspect all 19 supported proxy DLL slots
        foreach (string dll in ProxyDllCatalog.SupportedDlls)
        {
            string fullPath = Path.Combine(game.TargetDirectory, dll);
            bool exists = File.Exists(fullPath);
            bool isBink = ProxyDllCatalog.IsBinkDll(dll);
            bool isAvoided = game.Compatibility?.AvoidDlls.TryGetValue(dll, out string? avoidReason) == true;
            avoidReason = isAvoided ? game.Compatibility!.AvoidDlls[dll] : null;

            if (exists)
            {
                if (IsMfgUnlockerFile(fullPath, dll, manifest, currentRelease, cachedDllSha, out string? detectedVersion))
                {
                    analysis.DllStatuses[dll] = DllSlotStatus.InstalledMfg;
                    analysis.IsInstalled = true;
                    analysis.InstalledDllName = dll;
                    analysis.InstalledVersion = detectedVersion;
                    analysis.DllStatusDetails[dll] = string.IsNullOrWhiteSpace(detectedVersion)
                        ? "MFG Unlocker installed"
                        : $"MFG Unlocker installed ({detectedVersion})";
                    continue;
                }

                if (isBink)
                {
                    string hookedName = ProxyDllCatalog.GetBinkHookedName(dll)!;
                    string hookedPath = Path.Combine(game.TargetDirectory, hookedName);
                    if (!File.Exists(hookedPath))
                    {
                        analysis.DllStatuses[dll] = DllSlotStatus.BinkOriginalPresent;
                        analysis.DllStatusDetails[dll] = $"Original {dll} found (will be backed up as {hookedName})";
                    }
                    else
                    {
                        analysis.DllStatuses[dll] = DllSlotStatus.OccupiedOther;
                        analysis.DllStatusDetails[dll] = $"Both {dll} and {hookedName} already exist (not MFG Unlocker)";
                    }

                    continue;
                }

                analysis.DllStatuses[dll] = DllSlotStatus.OccupiedOther;
                analysis.DllStatusDetails[dll] = ProxyDllCatalog.CommonModConflicts.TryGetValue(dll, out string? modHint)
                    ? $"Occupied by another file (possibly {modHint})"
                    : "Occupied by a game or third-party mod file";
            }
            else
            {
                if (isBink)
                {
                    analysis.DllStatuses[dll] = DllSlotStatus.BinkMissingOriginal;
                    analysis.DllStatusDetails[dll] = $"Original {dll} not found in game folder";
                }
                else if (isAvoided)
                {
                    analysis.DllStatuses[dll] = DllSlotStatus.Avoided;
                    analysis.DllStatusDetails[dll] = $"Avoid: {avoidReason}";
                }
                else
                {
                    analysis.DllStatuses[dll] = DllSlotStatus.Available;
                    analysis.DllStatusDetails[dll] = "Available";
                }
            }
        }

        SelectRecommendation(game, analysis);
        return analysis;
    }

    public static bool IsMfgUnlockerFile(
        string filePath,
        string dllName,
        InstalledManifestEntry? manifest,
        ReleaseInfo? release,
        string? cachedDllSha = null)
        => IsMfgUnlockerFile(filePath, dllName, manifest, release, cachedDllSha, out _);

    private static bool IsMfgUnlockerFile(
        string filePath,
        string dllName,
        InstalledManifestEntry? manifest,
        ReleaseInfo? release,
        string? cachedDllSha,
        out string? detectedVersion)
    {
        detectedVersion = null;

        try
        {
            string sha256 = ReleaseService.ComputeSha256(filePath);

            if (manifest is not null
                && string.Equals(manifest.InstalledDllName, dllName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(manifest.DllSha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                detectedVersion = manifest.VersionTag;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(release?.ExpectedDllSha256)
                && string.Equals(release.ExpectedDllSha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                detectedVersion = release.TagName;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(cachedDllSha)
                && string.Equals(cachedDllSha, sha256, StringComparison.OrdinalIgnoreCase))
            {
                detectedVersion = release?.TagName;
                return true;
            }

            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(filePath);
            string[] fields =
            [
                fvi.ProductName ?? string.Empty,
                fvi.FileDescription ?? string.Empty,
                fvi.InternalName ?? string.Empty,
                fvi.OriginalFilename ?? string.Empty
            ];

            if (fields.Any(f => f.Contains("MFGUnlocker", StringComparison.OrdinalIgnoreCase)
                                || f.Contains("MFG Unlocker", StringComparison.OrdinalIgnoreCase)
                                || f.Contains("RTXMFG", StringComparison.OrdinalIgnoreCase)))
            {
                string? rawVer = !string.IsNullOrWhiteSpace(fvi.ProductVersion) ? fvi.ProductVersion : fvi.FileVersion;
                if (!string.IsNullOrWhiteSpace(rawVer))
                {
                    string clean = rawVer.Split(['+', ' '], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    detectedVersion = clean.StartsWith('v') ? clean : "v" + clean;
                }

                return true;
            }
        }
        catch
        {
            // Ignore unreadable file metadata.
        }

        return false;
    }

    private static void SelectRecommendation(GameEntry game, InstallAnalysis analysis)
    {
        if (analysis.IsInstalled && !string.IsNullOrWhiteSpace(analysis.InstalledDllName))
        {
            analysis.RecommendedDll = analysis.InstalledDllName;
            analysis.RecommendationReason = $"Currently using {analysis.InstalledDllName}.";
            return;
        }

        GameCompatibilityEntry? compat = game.Compatibility;
        List<string> candidates = [];
        HashSet<string> explicitBinkAllowed = new(StringComparer.OrdinalIgnoreCase);

        if (compat is not null)
        {
            // Special case from compatibility.md: MSFS 2020 uses dinput8.dll on Store/Xbox, version.dll on Steam
            if (string.Equals(compat.Name, "Microsoft Flight Simulator 2020", StringComparison.OrdinalIgnoreCase)
                && string.Equals(game.Source, "Xbox", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add("dinput8.dll");
                candidates.Add("version.dll");
            }
            else
            {
                candidates.Add(compat.PreferredDll);
                candidates.AddRange(compat.AlternativeDlls);
            }

            if (ProxyDllCatalog.IsBinkDll(compat.PreferredDll))
            {
                explicitBinkAllowed.Add(compat.PreferredDll);
            }

            foreach (string alt in compat.AlternativeDlls)
            {
                if (ProxyDllCatalog.IsBinkDll(alt))
                {
                    explicitBinkAllowed.Add(alt);
                }
            }
        }

        foreach (string dll in ProxyDllCatalog.SupportedDlls)
        {
            if (!ProxyDllCatalog.IsBinkDll(dll) && !candidates.Contains(dll, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(dll);
            }
        }

        foreach (string candidate in candidates)
        {
            if (!analysis.DllStatuses.TryGetValue(candidate, out DllSlotStatus status))
            {
                continue;
            }

            bool usable = status == DllSlotStatus.Available
                          || (status == DllSlotStatus.BinkOriginalPresent && explicitBinkAllowed.Contains(candidate));

            if (!usable)
            {
                continue;
            }

            analysis.RecommendedDll = candidate;

            if (compat is not null && string.Equals(candidate, compat.PreferredDll, StringComparison.OrdinalIgnoreCase))
            {
                analysis.RecommendationReason = $"Recommended by the compatibility database for {compat.Name}.";
            }
            else if (compat is not null && compat.AlternativeDlls.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                analysis.RecommendationReason =
                    $"Switched to fallback {candidate} because {compat.PreferredDll} is occupied or avoided.";
            }
            else if (compat is not null)
            {
                analysis.RecommendationReason =
                    $"Preferred DLLs for {compat.Name} are occupied; automatically selected safe fallback {candidate}.";
            }
            else
            {
                analysis.RecommendationReason = string.Equals(candidate, "version.dll", StringComparison.OrdinalIgnoreCase)
                    ? "Defaulting to version.dll (per MFG Unlocker install guide)."
                    : $"version.dll is occupied; automatically selected next safe option ({candidate}).";
            }

            return;
        }

        analysis.RecommendedDll = compat?.PreferredDll ?? "version.dll";
        analysis.RecommendationReason = "No completely free slot was found; please inspect the DLL list.";
    }

    private static void CheckAntiCheat(GameEntry game, InstallAnalysis analysis)
    {
        if (CompatibilityDatabase.IsAntiCheatGame(game.Name))
        {
            analysis.HasAntiCheat = true;
            analysis.AntiCheatFindings.Add("Game is listed with known anti-cheat conflicts/restrictions.");
        }

        HashSet<string> dirsToInspect = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(game.RootDirectory) && Directory.Exists(game.RootDirectory))
        {
            dirsToInspect.Add(game.RootDirectory);
        }

        if (!string.IsNullOrWhiteSpace(game.TargetDirectory) && Directory.Exists(game.TargetDirectory))
        {
            dirsToInspect.Add(game.TargetDirectory);
        }

        foreach (string dir in dirsToInspect)
        {
            foreach (string acDir in AntiCheatDirectories)
            {
                if (Directory.Exists(Path.Combine(dir, acDir)))
                {
                    analysis.HasAntiCheat = true;
                    string msg = $"Detected {acDir} folder";
                    if (!analysis.AntiCheatFindings.Contains(msg, StringComparer.OrdinalIgnoreCase))
                    {
                        analysis.AntiCheatFindings.Add(msg);
                    }
                }
            }

            foreach (string acFile in AntiCheatFiles)
            {
                if (File.Exists(Path.Combine(dir, acFile)))
                {
                    analysis.HasAntiCheat = true;
                    string msg = $"Detected {acFile}";
                    if (!analysis.AntiCheatFindings.Contains(msg, StringComparer.OrdinalIgnoreCase))
                    {
                        analysis.AntiCheatFindings.Add(msg);
                    }
                }
            }
        }
    }
}
