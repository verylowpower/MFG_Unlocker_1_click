namespace MfgUnlockerInstaller.Data;

/// <summary>
/// Supported proxy DLL filenames, Bink hook helpers, known mod conflicts, and legacy files.
/// </summary>
public static class ProxyDllCatalog
{
    /// <summary>
    /// All 19 supported filenames in default recommendation order.
    /// </summary>
    public static IReadOnlyList<string> SupportedDlls { get; } =
    [
        "version.dll",
        "winmm.dll",
        "dinput8.dll",
        "dxgi.dll",
        "d3d12.dll",
        "xinput9_1_0.dll",
        "dsound.dll",
        "d3d11.dll",
        "d3d10.dll",
        "d3d9.dll",
        "wininet.dll",
        "winhttp.dll",
        "xinput1_4.dll",
        "xinput1_3.dll",
        "xinput1_2.dll",
        "xinput1_1.dll",
        "xinputuap.dll",
        "bink2w64.dll",
        "binkw64.dll"
    ];

    public static IReadOnlySet<string> SupportedDllSet { get; } =
        new HashSet<string>(SupportedDlls, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> CommonModConflicts { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["version.dll"] = "Cyber Engine Tweaks / Ultimate ASI Loader",
            ["winmm.dll"] = "RED4ext / Mod Loader",
            ["dinput8.dll"] = "REFramework / ASI Loader",
            ["dxgi.dll"] = "ReShade / Special K",
            ["dsound.dll"] = "FF7RebirthFix / ASI Loader"
        };

    public static IReadOnlyList<string> LegacyFileNames { get; } =
    [
        "RTXMFG.dll",
        "RTX40MFGCore.dll",
        "RTX40MFG.asi",
        "RTX40MFG-UI.addon64"
    ];

    public static bool IsSupported(string? dllName)
        => !string.IsNullOrWhiteSpace(dllName) && SupportedDllSet.Contains(dllName.Trim());

    public static bool IsBinkDll(string? dllName)
        => string.Equals(dllName, "binkw64.dll", StringComparison.OrdinalIgnoreCase)
           || string.Equals(dllName, "bink2w64.dll", StringComparison.OrdinalIgnoreCase);

    public static string? GetBinkHookedName(string? dllName)
    {
        if (string.Equals(dllName, "binkw64.dll", StringComparison.OrdinalIgnoreCase))
        {
            return "binkw64Hooked.dll";
        }

        if (string.Equals(dllName, "bink2w64.dll", StringComparison.OrdinalIgnoreCase))
        {
            return "bink2w64Hooked.dll";
        }

        return null;
    }
}
