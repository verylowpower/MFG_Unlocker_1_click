using System.Text.RegularExpressions;
using Microsoft.Win32;
using MfgUnlockerInstaller.Models;

namespace MfgUnlockerInstaller.Services;

/// <summary>
/// Detects installed NVIDIA RTX GPUs (20xx/30xx/40xx/50xx) and Windows HAGS status.
/// </summary>
public static partial class GpuDetectionService
{
    private const string DisplayClassKeyPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private const string GraphicsDriversKeyPath =
        @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    [GeneratedRegex(@"\bRTX\s*(?<series>20|30|40|50)\d{2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RtxSeriesRegex();

    [GeneratedRegex(@"\bRTX\s*A?\d{4}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkstationRtxRegex();

    public static GpuInfo DetectPrimaryGpu()
    {
        List<string> adapterNames = ReadAdapterNamesFromRegistry();
        string? bestName = SelectBestAdapterName(adapterNames);
        GpuArchitecture arch = ClassifyGpuName(bestName);
        (bool hagsSupported, bool hagsEnabled) = ReadHagsStatus();

        return new GpuInfo
        {
            Name = string.IsNullOrWhiteSpace(bestName) ? "Unknown GPU" : bestName,
            Architecture = arch,
            IsHagsSupported = hagsSupported,
            IsHagsEnabled = hagsEnabled
        };
    }

    public static GpuArchitecture ClassifyGpuName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return GpuArchitecture.Unknown;
        }

        Match match = RtxSeriesRegex().Match(name);
        if (match.Success)
        {
            return match.Groups["series"].Value switch
            {
                "20" => GpuArchitecture.Rtx20Turing,
                "30" => GpuArchitecture.Rtx30Ampere,
                "40" => GpuArchitecture.Rtx40Ada,
                "50" => GpuArchitecture.Rtx50Blackwell,
                _ => GpuArchitecture.OtherNvidia
            };
        }

        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
            || WorkstationRtxRegex().IsMatch(name))
        {
            return GpuArchitecture.OtherNvidia;
        }

        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Intel", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
        {
            return GpuArchitecture.NonNvidia;
        }

        return GpuArchitecture.Unknown;
    }

    public static (bool Supported, bool Enabled) ReadHagsStatus()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(GraphicsDriversKeyPath, writable: false);
            if (key is null)
            {
                return (false, false);
            }

            object? val = key.GetValue("HwSchMode");
            if (val is int mode)
            {
                return (true, mode == 2);
            }

            // Key exists on Windows 10 2004+ / Windows 11 even if HwSchMode is not yet set (defaults to disabled on older/some setups).
            return (true, false);
        }
        catch
        {
            return (false, false);
        }
    }

    public static bool TryEnableHags(out string? errorMessage)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(GraphicsDriversKeyPath, writable: true);
            if (key is null)
            {
                errorMessage = "GraphicsDrivers registry key was not found.";
                return false;
            }

            key.SetValue("HwSchMode", 2, RegistryValueKind.DWord);
            errorMessage = null;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            errorMessage = "Administrator privileges are required to enable HAGS. Click 'Run as Admin' and try again.";
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static List<string> ReadAdapterNamesFromRegistry()
    {
        List<string> names = [];
        try
        {
            using RegistryKey? baseKey = Registry.LocalMachine.OpenSubKey(DisplayClassKeyPath, writable: false);
            if (baseKey is null)
            {
                return names;
            }

            foreach (string subKeyName in baseKey.GetSubKeyNames())
            {
                if (!subKeyName.StartsWith("0", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using RegistryKey? subKey = baseKey.OpenSubKey(subKeyName, writable: false);
                    if (subKey is null)
                    {
                        continue;
                    }

                    string? desc = subKey.GetValue("DriverDesc") as string
                                   ?? subKey.GetValue("HardwareInformation.AdapterString") as string;
                    if (!string.IsNullOrWhiteSpace(desc))
                    {
                        names.Add(desc.Trim());
                    }
                }
                catch
                {
                    // Ignore unreadable subkeys.
                }
            }
        }
        catch
        {
            // Ignore registry access failures.
        }

        return names;
    }

    private static string? SelectBestAdapterName(List<string> adapterNames)
    {
        if (adapterNames.Count == 0)
        {
            return null;
        }

        // Prefer discrete RTX 20/30/40/50 over integrated GPUs (e.g. Intel UHD / AMD Radeon Graphics).
        foreach (string name in adapterNames)
        {
            GpuArchitecture arch = ClassifyGpuName(name);
            if (arch is GpuArchitecture.Rtx50Blackwell
                or GpuArchitecture.Rtx40Ada
                or GpuArchitecture.Rtx30Ampere
                or GpuArchitecture.Rtx20Turing)
            {
                return name;
            }
        }

        foreach (string name in adapterNames)
        {
            if (ClassifyGpuName(name) == GpuArchitecture.OtherNvidia)
            {
                return name;
            }
        }

        return adapterNames[0];
    }
}
