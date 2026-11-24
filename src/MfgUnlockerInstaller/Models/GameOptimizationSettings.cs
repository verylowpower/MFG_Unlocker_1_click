namespace MfgUnlockerInstaller.Models;

/// <summary>
/// Per-game MFG Unlocker compatibility & optimization settings stored in RTX40MFG-UI.ini.
/// </summary>
public sealed class GameOptimizationSettings
{
    public bool IniExists { get; set; }

    /// <summary>
    /// [Compatibility] GpuNameOverride=1: Makes the game see an RTX 4090 so hidden DLSS FG toggles appear on RTX 20xx/30xx.
    /// </summary>
    public bool UnlockHiddenGameFgOption { get; set; }

    /// <summary>
    /// [Compatibility] Rtx30FlipMetering=0: Disables driver flip metering on RTX 30xx and uses V1.5.0 CPU-paced presentation.
    /// Default in MFG Unlocker is true (Rtx30FlipMetering=1).
    /// </summary>
    public bool UseLegacyRtx30Pacing { get; set; }

    public static GameOptimizationSettings CreateRecommendedForRtx20Or30() => new()
    {
        UnlockHiddenGameFgOption = true,
        UseLegacyRtx30Pacing = false
    };
}
