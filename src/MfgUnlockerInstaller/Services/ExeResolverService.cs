using System.IO;
using MfgUnlockerInstaller.Models;

namespace MfgUnlockerInstaller.Services;

/// <summary>
/// Resolves the actual game executable (bypassing launchers) inside a game installation directory.
/// </summary>
public static class ExeResolverService
{
    private static readonly string[] ExcludedExeTokens =
    [
        "unins",
        "crash",
        "report",
        "launcher",
        "redist",
        "setup",
        "helper",
        "updater",
        "overlay",
        "prereq",
        "cefsubprocess",
        "unitycrash",
        "vc_redist",
        "vcredist",
        "dxsetup",
        "easyanticheat",
        "beservice",
        "battleye",
        "dotnet",
        "installer",
        "cleanup",
        "touchup",
        "activation",
        "webhelper",
        "zfgamebrowser"
    ];

    private static readonly string[] IgnoredDirectories =
    [
        "engine",
        "redist",
        "_commonredist",
        "directx",
        "easyanticheat",
        "battleye",
        "__installer",
        "support",
        "crashreportclient",
        "dotnet"
    ];

    private static readonly string[] PreferredSubfolders =
    [
        @"bin\x64_dx12",
        @"bin\x64",
        "x64",
        "Content",
        @"Binaries\Win64"
    ];

    public static string ResolveGameExecutable(string rootOrExePath, GameCompatibilityEntry? compat = null)
    {
        if (string.IsNullOrWhiteSpace(rootOrExePath))
        {
            return string.Empty;
        }

        if (File.Exists(rootOrExePath) && rootOrExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(rootOrExePath);
        }

        if (!Directory.Exists(rootOrExePath))
        {
            return string.Empty;
        }

        string rootDir = Path.GetFullPath(rootOrExePath);

        // 1. Check compatibility database relative hints first.
        if (compat is not null)
        {
            foreach (string hint in compat.ExeHints)
            {
                string candidate = Path.Combine(rootDir, hint);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        // 2. Look for Unreal Engine *-Win64-Shipping.exe / *-WinGDK-Shipping.exe under *\Binaries\Win64
        List<FileInfo> allExes = EnumerateExecutablesSafe(rootDir, maxDepth: 4);
        FileInfo? ueShipping = allExes
            .Where(f => IsUnrealShippingExe(f.FullName) && !IsExcludedExe(f.Name))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();
        if (ueShipping is not null)
        {
            return ueShipping.FullName;
        }

        // 3. Check known binary subfolders in priority order.
        foreach (string subfolder in PreferredSubfolders)
        {
            string dir = Path.Combine(rootDir, subfolder);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            FileInfo? bestInSubfolder = EnumerateTopLevelExesSafe(dir)
                .Where(f => !IsExcludedExe(f.Name))
                .OrderByDescending(f => f.Length)
                .FirstOrDefault();

            if (bestInSubfolder is not null)
            {
                return bestInSubfolder.FullName;
            }
        }

        // 4. Score remaining executables up to depth 3.
        FileInfo? bestCandidate = allExes
            .Where(f => GetRelativeDepth(rootDir, f.FullName) <= 3 && !IsExcludedExe(f.Name))
            .OrderByDescending(f => ScoreExecutable(rootDir, f))
            .ThenByDescending(f => f.Length)
            .FirstOrDefault();

        if (bestCandidate is not null)
        {
            return bestCandidate.FullName;
        }

        // 5. Last resort: pick largest exe in root directory even if name contains launcher.
        FileInfo? rootFallback = EnumerateTopLevelExesSafe(rootDir)
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();

        return rootFallback?.FullName ?? string.Empty;
    }

    private static bool IsUnrealShippingExe(string fullPath)
    {
        string normalized = fullPath.Replace('/', '\\');
        if (!normalized.Contains(@"\Binaries\Win64\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fileName = Path.GetFileName(fullPath);
        return fileName.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith("-WinGDK-Shipping.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcludedExe(string fileName)
    {
        string lower = fileName.ToLowerInvariant();
        foreach (string token in ExcludedExeTokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int ScoreExecutable(string rootDir, FileInfo file)
    {
        int score = 0;
        string relPath = Path.GetRelativePath(rootDir, file.FullName);

        if (relPath.Contains("x64_dx12", StringComparison.OrdinalIgnoreCase))
        {
            score += 40;
        }
        else if (relPath.Contains("Win64", StringComparison.OrdinalIgnoreCase)
                 || relPath.Contains(@"\x64\", StringComparison.OrdinalIgnoreCase)
                 || relPath.StartsWith(@"x64\", StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        // Slight preference for actual binaries over tiny stub launchers in root
        if (file.Length >= 5 * 1024 * 1024)
        {
            score += 20;
        }
        else if (file.Length < 1024 * 1024)
        {
            score -= 10;
        }

        return score;
    }

    private static int GetRelativeDepth(string rootDir, string filePath)
    {
        string rel = Path.GetRelativePath(rootDir, Path.GetDirectoryName(filePath) ?? rootDir);
        if (rel == ".")
        {
            return 0;
        }

        return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length;
    }

    private static List<FileInfo> EnumerateTopLevelExesSafe(string dir)
    {
        List<FileInfo> results = [];
        try
        {
            foreach (string path in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                results.Add(new FileInfo(path));
            }
        }
        catch
        {
            // Ignore unreadable directories.
        }

        return results;
    }

    private static List<FileInfo> EnumerateExecutablesSafe(string rootDir, int maxDepth)
    {
        List<FileInfo> results = [];
        Queue<(string Dir, int Depth)> queue = new();
        queue.Enqueue((rootDir, 0));

        while (queue.Count > 0)
        {
            (string currentDir, int depth) = queue.Dequeue();

            try
            {
                foreach (string file in Directory.EnumerateFiles(currentDir, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    results.Add(new FileInfo(file));
                }

                if (depth >= maxDepth)
                {
                    continue;
                }

                foreach (string subDir in Directory.EnumerateDirectories(currentDir))
                {
                    string folderName = Path.GetFileName(subDir);
                    if (IgnoredDirectories.Contains(folderName, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    queue.Enqueue((subDir, depth + 1));
                }
            }
            catch
            {
                // Skip protected or inaccessible folders.
            }
        }

        return results;
    }
}
