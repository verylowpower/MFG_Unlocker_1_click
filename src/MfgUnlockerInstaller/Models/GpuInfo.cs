namespace MfgUnlockerInstaller.Models;

public enum GpuArchitecture
{
    Unknown,
    Rtx20Turing,
    Rtx30Ampere,
    Rtx40Ada,
    Rtx50Blackwell,
    OtherNvidia,
    NonNvidia
}

/// <summary>
/// Detected primary GPU and Windows Hardware-Accelerated GPU Scheduling (HAGS) state.
/// </summary>
public sealed class GpuInfo
{
    public string Name { get; init; } = "Unknown GPU";
    public GpuArchitecture Architecture { get; init; } = GpuArchitecture.Unknown;
    public bool IsHagsEnabled { get; init; }
    public bool IsHagsSupported { get; init; }

    public bool IsRtx20Or30 =>
        Architecture is GpuArchitecture.Rtx20Turing or GpuArchitecture.Rtx30Ampere;

    public string ArchitectureLabel => Architecture switch
    {
        GpuArchitecture.Rtx20Turing => "RTX 20xx (Turing)",
        GpuArchitecture.Rtx30Ampere => "RTX 30xx (Ampere)",
        GpuArchitecture.Rtx40Ada => "RTX 40xx (Ada)",
        GpuArchitecture.Rtx50Blackwell => "RTX 50xx (Blackwell)",
        GpuArchitecture.OtherNvidia => "NVIDIA",
        GpuArchitecture.NonNvidia => "Non-NVIDIA",
        _ => "Unknown"
    };
}
