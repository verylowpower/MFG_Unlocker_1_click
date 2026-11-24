using System.IO;
using System.Text;
using MfgUnlockerInstaller.Models;

namespace MfgUnlockerInstaller.Services;

/// <summary>
/// Reads and writes RTX 20xx/30xx optimization settings in RTX40MFG-UI.ini beside a game executable,
/// preserving any unrelated INI sections and keys.
/// </summary>
public static class OptimizationService
{
    public const string CompatibilityIniFileName = "RTX40MFG-UI.ini";
    private const string CompatibilitySection = "Compatibility";
    private const string GpuNameOverrideKey = "GpuNameOverride";
    private const string Rtx30FlipMeteringKey = "Rtx30FlipMetering";

    public static string GetIniPath(string targetDirectory)
        => Path.Combine(targetDirectory, CompatibilityIniFileName);

    public static GameOptimizationSettings Load(string? targetDirectory)
    {
        GameOptimizationSettings settings = new();
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return settings;
        }

        string iniPath = GetIniPath(targetDirectory);
        if (!File.Exists(iniPath))
        {
            return settings;
        }

        settings.IniExists = true;
        string? currentSection = null;

        foreach (string rawLine in File.ReadAllLines(iniPath))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']') && line.Length >= 2)
            {
                currentSection = line[1..^1].Trim();
                continue;
            }

            if (!string.Equals(currentSection, CompatibilitySection, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int eqIndex = line.IndexOf('=');
            if (eqIndex <= 0)
            {
                continue;
            }

            string key = line[..eqIndex].Trim();
            string value = line[(eqIndex + 1)..].Trim();

            if (string.Equals(key, GpuNameOverrideKey, StringComparison.OrdinalIgnoreCase))
            {
                settings.UnlockHiddenGameFgOption = IsTruthy(value);
            }
            else if (string.Equals(key, Rtx30FlipMeteringKey, StringComparison.OrdinalIgnoreCase))
            {
                // Default is on (1); only false/0/off switches to legacy V1.5.0 pacing.
                settings.UseLegacyRtx30Pacing = IsFalsy(value);
            }
        }

        return settings;
    }

    public static void Save(string targetDirectory, GameOptimizationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory))
        {
            throw new DirectoryNotFoundException($"Game directory does not exist: {targetDirectory}");
        }

        string iniPath = GetIniPath(targetDirectory);
        List<string> lines = File.Exists(iniPath)
            ? [.. File.ReadAllLines(iniPath)]
            : [];

        UpsertKeyInSection(lines, CompatibilitySection, GpuNameOverrideKey, settings.UnlockHiddenGameFgOption ? "1" : "0");
        UpsertKeyInSection(lines, CompatibilitySection, Rtx30FlipMeteringKey, settings.UseLegacyRtx30Pacing ? "0" : "1");

        File.WriteAllLines(iniPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        settings.IniExists = true;
    }

    private static void UpsertKeyInSection(List<string> lines, string sectionName, string keyName, string value)
    {
        int sectionStart = -1;
        int sectionEnd = lines.Count;

        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length >= 2)
            {
                string header = trimmed[1..^1].Trim();
                if (sectionStart == -1)
                {
                    if (string.Equals(header, sectionName, StringComparison.OrdinalIgnoreCase))
                    {
                        sectionStart = i;
                    }
                }
                else
                {
                    sectionEnd = i;
                    break;
                }
            }
        }

        string formatted = $"{keyName}={value}";

        if (sectionStart == -1)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }

            lines.Add($"[{sectionName}]");
            lines.Add(formatted);
            return;
        }

        for (int i = sectionStart + 1; i < sectionEnd; i++)
        {
            string trimmed = lines[i].Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                continue;
            }

            int eqIndex = trimmed.IndexOf('=');
            if (eqIndex <= 0)
            {
                continue;
            }

            string existingKey = trimmed[..eqIndex].Trim();
            if (string.Equals(existingKey, keyName, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = formatted;
                return;
            }
        }

        int insertAt = sectionEnd;
        while (insertAt > sectionStart + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1]))
        {
            insertAt--;
        }

        lines.Insert(insertAt, formatted);
    }

    private static bool IsTruthy(string value)
        => string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);

    private static bool IsFalsy(string value)
        => string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
}
