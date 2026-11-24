using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MfgUnlockerInstaller.Models;

namespace MfgUnlockerInstaller.Services;

/// <summary>
/// Fetches release metadata from dashdogy/MFG-Unlocker on GitHub, verifies SHA256, and manages local cache.
/// </summary>
public sealed partial class ReleaseService
{
    public const string LatestReleaseApiUrl =
        "https://api.github.com/repos/dashdogy/MFG-Unlocker/releases/latest";

    private readonly ManifestStore _manifestStore;
    private readonly HttpClient _httpClient;

    public ReleaseService(ManifestStore manifestStore, HttpClient? httpClient = null)
    {
        _manifestStore = manifestStore;
        _httpClient = httpClient ?? CreateDefaultHttpClient();
    }

    public static HttpClient CreateDefaultHttpClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MfgUnlockerInstaller/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public ReleaseInfo? GetCachedRelease() => _manifestStore.LoadCachedReleaseInfo();

    public async Task<ReleaseInfo> GetLatestReleaseInfoAsync(CancellationToken ct = default)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(LatestReleaseApiUrl, ct);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        JsonElement root = doc.RootElement;

        string tagName = root.TryGetProperty("tag_name", out JsonElement tagEl)
            ? tagEl.GetString() ?? "unknown"
            : "unknown";
        string releaseName = root.TryGetProperty("name", out JsonElement nameEl)
            ? nameEl.GetString() ?? tagName
            : tagName;

        string? zipName = null;
        string? zipUrl = null;
        string? checksumsUrl = null;

        if (root.TryGetProperty("assets", out JsonElement assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assetsEl.EnumerateArray())
            {
                string? assetName = asset.TryGetProperty("name", out JsonElement an) ? an.GetString() : null;
                string? downloadUrl = asset.TryGetProperty("browser_download_url", out JsonElement du) ? du.GetString() : null;
                if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
                }

                if (string.Equals(assetName, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    checksumsUrl = downloadUrl;
                }
                else if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                         && (zipName is null || assetName.Contains("MFGUnlocker", StringComparison.OrdinalIgnoreCase)))
                {
                    zipName = assetName;
                    zipUrl = downloadUrl;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(zipName) || string.IsNullOrWhiteSpace(zipUrl))
        {
            throw new InvalidOperationException("Could not find an MFGUnlocker zip asset in the latest GitHub release.");
        }

        ReleaseInfo info = new()
        {
            TagName = tagName,
            ReleaseName = releaseName,
            ZipAssetName = zipName,
            ZipDownloadUrl = zipUrl,
            ChecksumsUrl = checksumsUrl
        };

        ReleaseInfo? cached = _manifestStore.LoadCachedReleaseInfo();
        string expectedCachePath = _manifestStore.GetCacheDllPathForTag(tagName);
        if (cached is not null
            && string.Equals(cached.TagName, tagName, StringComparison.OrdinalIgnoreCase)
            && cached.HasValidCachedFile)
        {
            info.CachedDllPath = cached.CachedDllPath;
            info.ExpectedDllSha256 = cached.ExpectedDllSha256;
            info.DownloadedAtUtc = cached.DownloadedAtUtc;
            info.IsCached = true;
        }
        else if (File.Exists(expectedCachePath))
        {
            info.CachedDllPath = expectedCachePath;
            info.ExpectedDllSha256 = ComputeSha256(expectedCachePath);
            info.DownloadedAtUtc = File.GetLastWriteTimeUtc(expectedCachePath);
            info.IsCached = true;
        }

        return info;
    }

    public async Task<ReleaseInfo> DownloadAndVerifyReleaseAsync(
        ReleaseInfo release,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Dictionary<string, string> checksums = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(release.ChecksumsUrl))
        {
            string sumsContent = await _httpClient.GetStringAsync(release.ChecksumsUrl, ct);
            checksums = ParseSha256Sums(sumsContent);
        }

        string tempDir = Path.Combine(_manifestStore.CacheDirectory, "temp");
        Directory.CreateDirectory(tempDir);
        string tempZipPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}_{release.ZipAssetName}");

        try
        {
            using (HttpResponseMessage response = await _httpClient.GetAsync(
                       release.ZipDownloadUrl,
                       HttpCompletionOption.ResponseHeadersRead,
                       ct))
            {
                response.EnsureSuccessStatusCode();
                long? totalBytes = response.Content.Headers.ContentLength;

                await using Stream contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using FileStream fs = new(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                byte[] buffer = new byte[81920];
                long readTotal = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    readTotal += bytesRead;

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        double pct = Math.Min(92.0, readTotal * 92.0 / totalBytes.Value);
                        progress?.Report(pct);
                    }
                }
            }

            if (checksums.TryGetValue(release.ZipAssetName, out string? expectedZipHash))
            {
                string actualZipHash = ComputeSha256(tempZipPath);
                if (!string.Equals(actualZipHash, expectedZipHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Zip SHA256 verification failed (expected {expectedZipHash}, actual {actualZipHash}).");
                }
            }

            string targetDllPath = _manifestStore.GetCacheDllPathForTag(release.TagName);
            string targetFolder = Path.GetDirectoryName(targetDllPath)!;
            Directory.CreateDirectory(targetFolder);
            string tempDllPath = targetDllPath + ".tmp";

            using (ZipArchive archive = ZipFile.OpenRead(tempZipPath))
            {
                ZipArchiveEntry? dllEntry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(Path.GetFileName(e.FullName), "MFGUnlocker.dll", StringComparison.OrdinalIgnoreCase));

                if (dllEntry is null)
                {
                    throw new InvalidDataException($"MFGUnlocker.dll was not found inside {release.ZipAssetName}.");
                }

                dllEntry.ExtractToFile(tempDllPath, overwrite: true);
            }

            string actualDllHash = ComputeSha256(tempDllPath);
            if (checksums.TryGetValue("MFGUnlocker.dll", out string? expectedDllHash)
                && !string.Equals(actualDllHash, expectedDllHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempDllPath);
                throw new InvalidDataException(
                    $"MFGUnlocker.dll SHA256 verification failed (expected {expectedDllHash}, actual {actualDllHash}).");
            }

            File.Move(tempDllPath, targetDllPath, overwrite: true);
            progress?.Report(100.0);

            release.CachedDllPath = targetDllPath;
            release.ExpectedDllSha256 = actualDllHash;
            release.IsCached = true;
            release.DownloadedAtUtc = DateTime.UtcNow;

            _manifestStore.SaveCachedReleaseInfo(release);
            return release;
        }
        finally
        {
            try
            {
                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }
            }
            catch
            {
                // Ignore temp cleanup failure.
            }
        }
    }

    public Task<ReleaseInfo> ImportLocalPayloadAsync(string filePath, CancellationToken ct = default)
        => Task.Run(() => ImportLocalPayload(filePath), ct);

    public ReleaseInfo ImportLocalPayload(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Selected file was not found.", filePath);
        }

        string destPath = _manifestStore.GetCacheDllPathForTag("local");
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        string tempPath = destPath + ".tmp";

        if (filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using ZipArchive archive = ZipFile.OpenRead(filePath);
            ZipArchiveEntry? dllEntry = archive.Entries.FirstOrDefault(e =>
                string.Equals(Path.GetFileName(e.FullName), "MFGUnlocker.dll", StringComparison.OrdinalIgnoreCase));

            if (dllEntry is null)
            {
                throw new InvalidDataException("Zip archive does not contain MFGUnlocker.dll.");
            }

            dllEntry.ExtractToFile(tempPath, overwrite: true);
        }
        else if (filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(filePath, tempPath, overwrite: true);
        }
        else
        {
            throw new InvalidOperationException("Only MFG Unlocker .zip or .dll files are supported.");
        }

        File.Move(tempPath, destPath, overwrite: true);

        string versionLabel = ReadVersionTagFromDll(destPath) ?? "local";
        string sha256 = ComputeSha256(destPath);

        ReleaseInfo info = new()
        {
            TagName = versionLabel,
            ReleaseName = $"Manual import ({versionLabel})",
            ZipAssetName = Path.GetFileName(filePath),
            ZipDownloadUrl = string.Empty,
            CachedDllPath = destPath,
            ExpectedDllSha256 = sha256,
            IsCached = true,
            DownloadedAtUtc = DateTime.UtcNow
        };

        _manifestStore.SaveCachedReleaseInfo(info);
        return info;
    }

    public static Dictionary<string, string> ParseSha256Sums(string content)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(content))
        {
            return result;
        }

        foreach (string line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = Sha256LineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            string hash = match.Groups[1].Value.ToUpperInvariant();
            string fileName = Path.GetFileName(match.Groups[2].Value.Trim());
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                result[fileName] = hash;
            }
        }

        return result;
    }

    public static string ComputeSha256(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private static string? ReadVersionTagFromDll(string dllPath)
    {
        try
        {
            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(dllPath);
            string? raw = !string.IsNullOrWhiteSpace(fvi.ProductVersion) ? fvi.ProductVersion : fvi.FileVersion;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string clean = raw.Split(['+', ' '], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            return clean.StartsWith('v') ? clean : "v" + clean;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex("^\\s*([0-9a-fA-F]{64})\\s+[\\*\\s]*(.+?)\\s*$")]
    private static partial Regex Sha256LineRegex();
}
