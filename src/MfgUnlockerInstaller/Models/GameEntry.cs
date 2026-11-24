using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MfgUnlockerInstaller.Models;

/// <summary>
/// Represents a discovered or manually added game and its current analysis state.
/// </summary>
public sealed class GameEntry : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _targetDirectory = string.Empty;
    private string _executablePath = string.Empty;
    private GameCompatibilityEntry? _compatibility;
    private InstallAnalysis? _analysis;

    public required string Id { get; init; }

    public required string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SubtitleText));
            }
        }
    }

    public required string Source { get; init; }
    public required string RootDirectory { get; init; }

    public required string TargetDirectory
    {
        get => _targetDirectory;
        set
        {
            if (_targetDirectory != value)
            {
                _targetDirectory = value;
                OnPropertyChanged();
            }
        }
    }

    public required string ExecutablePath
    {
        get => _executablePath;
        set
        {
            if (_executablePath != value)
            {
                _executablePath = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsManual { get; init; }

    public GameCompatibilityEntry? Compatibility
    {
        get => _compatibility;
        set
        {
            _compatibility = value;
            OnPropertyChanged();
            NotifyUiChanged();
        }
    }

    public InstallAnalysis? Analysis
    {
        get => _analysis;
        set
        {
            _analysis = value;
            OnPropertyChanged();
            NotifyUiChanged();
        }
    }

    public string StatusBadgeText
    {
        get
        {
            if (Analysis is null)
            {
                return "Not scanned";
            }

            if (Analysis.IsInstalled)
            {
                string ver = string.IsNullOrWhiteSpace(Analysis.InstalledVersion)
                    ? "Installed"
                    : Analysis.InstalledVersion;
                return $"{ver} ({Analysis.InstalledDllName})";
            }

            if (Analysis.HasAntiCheat)
            {
                return "Anti-Cheat";
            }

            if (Analysis.IsLegacyInstalled)
            {
                return "Legacy files";
            }

            if (Compatibility is not null)
            {
                return $"Supported ({Analysis.RecommendedDll})";
            }

            return "Not installed";
        }
    }

    public string StatusBadgeKind
    {
        get
        {
            if (Analysis is null)
            {
                return "Default";
            }

            if (Analysis.IsInstalled)
            {
                return "Installed";
            }

            if (Analysis.HasAntiCheat)
            {
                return "AntiCheat";
            }

            if (Analysis.IsLegacyInstalled)
            {
                return "Legacy";
            }

            return Compatibility is not null ? "Ready" : "Default";
        }
    }

    public string SubtitleText => Compatibility is not null
        ? $"{Source}  •  Recommended: {Compatibility.PreferredDll}"
        : $"{Source}  •  Not in compatibility DB";

    public void NotifyUiChanged()
    {
        OnPropertyChanged(nameof(StatusBadgeText));
        OnPropertyChanged(nameof(StatusBadgeKind));
        OnPropertyChanged(nameof(SubtitleText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
