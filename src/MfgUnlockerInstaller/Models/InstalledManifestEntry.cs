namespace MfgUnlockerInstaller.Models;

/// <summary>
/// Persisted record of an MFG Unlocker installation performed by this app.
/// </summary>
public sealed class InstalledManifestEntry
{
    public required string TargetDirectory { get; set; }
    public required string GameName { get; set; }
    public required string InstalledDllName { get; set; }
    public required string VersionTag { get; set; }
    public required string DllSha256 { get; set; }
    public string? BinkHookedBackupName { get; set; }
    public DateTime InstalledAtUtc { get; set; }
}

/// <summary>
/// Persisted record of a user-added manual game entry.
/// </summary>
public sealed class CustomGameRecord
{
    public required string Name { get; set; }
    public required string RootDirectory { get; set; }
    public required string ExecutablePath { get; set; }
}
