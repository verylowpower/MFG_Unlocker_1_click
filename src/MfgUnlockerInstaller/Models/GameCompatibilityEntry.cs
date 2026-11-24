namespace MfgUnlockerInstaller.Models;

/// <summary>
/// Compatibility profile for a known game from MFG-Unlocker documentation.
/// </summary>
public sealed class GameCompatibilityEntry
{
    public required string Name { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public required string PreferredDll { get; init; }
    public IReadOnlyList<string> AlternativeDlls { get; init; } = [];
    public IReadOnlyDictionary<string, string> AvoidDlls { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> ExeHints { get; init; } = [];
    public string? Notes { get; init; }
}
