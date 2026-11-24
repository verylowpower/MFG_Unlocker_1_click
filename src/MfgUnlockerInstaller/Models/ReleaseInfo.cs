using System.IO;
using System.Text.Json.Serialization;

namespace MfgUnlockerInstaller.Models;

/// <summary>
/// Metadata and local cache status for an MFG-Unlocker GitHub release or manual payload.
/// </summary>
public sealed class ReleaseInfo
{
    public required string TagName { get; set; }
    public required string ReleaseName { get; set; }
    public required string ZipAssetName { get; set; }
    public required string ZipDownloadUrl { get; set; }
    public string? ChecksumsUrl { get; set; }
    public string? ExpectedDllSha256 { get; set; }
    public string? CachedDllPath { get; set; }
    public bool IsCached { get; set; }
    public DateTime? DownloadedAtUtc { get; set; }

    [JsonIgnore]
    public bool HasValidCachedFile =>
        !string.IsNullOrWhiteSpace(CachedDllPath) && File.Exists(CachedDllPath);
}
