namespace MfgUnlockerInstaller.Models;

public enum DllSlotStatus
{
    Available,
    InstalledMfg,
    OccupiedOther,
    BinkOriginalPresent,
    BinkMissingOriginal,
    Avoided
}

/// <summary>
/// Result of analyzing a game's target directory for MFG Unlocker installation.
/// </summary>
public sealed class InstallAnalysis
{
    public bool IsInstalled { get; set; }
    public string? InstalledDllName { get; set; }
    public string? InstalledVersion { get; set; }
    public bool IsLegacyInstalled { get; set; }
    public List<string> LegacyFilesFound { get; } = [];
    public bool HasAntiCheat { get; set; }
    public List<string> AntiCheatFindings { get; } = [];
    public string RecommendedDll { get; set; } = "version.dll";
    public string RecommendationReason { get; set; } = string.Empty;
    public Dictionary<string, DllSlotStatus> DllStatuses { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DllStatusDetails { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasConfigJson { get; set; }
    public int LogFileCount { get; set; }
}
