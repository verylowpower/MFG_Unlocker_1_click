using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MfgUnlockerInstaller.Data;
using MfgUnlockerInstaller.Helpers;
using MfgUnlockerInstaller.Models;
using MfgUnlockerInstaller.Services;

namespace MfgUnlockerInstaller;

public partial class MainWindow : Window
{
    private sealed class DllOptionItem
    {
        public required string DllName { get; init; }
        public required string DisplayText { get; init; }
        public required string DetailText { get; init; }
        public required DllSlotStatus Status { get; init; }
        public bool IsRecommended { get; init; }
    }

    private readonly ManifestStore _manifestStore;
    private readonly GameScannerService _scannerService;
    private readonly ReleaseService _releaseService;
    private readonly InstallAnalyzerService _analyzerService;
    private readonly InstallerService _installerService;

    private readonly List<GameEntry> _allGames = [];
    private readonly ObservableCollection<GameEntry> _filteredGames = [];
    private ReleaseInfo? _currentRelease;
    private GameEntry? _selectedGame;
    private GpuInfo _gpuInfo = new();
    private bool _isBusy;
    private bool _isUpdatingDllList;

    public MainWindow()
    {
        InitializeComponent();

        _manifestStore = new ManifestStore();
        _scannerService = new GameScannerService(_manifestStore);
        _releaseService = new ReleaseService(_manifestStore);
        _analyzerService = new InstallAnalyzerService(_manifestStore);
        _installerService = new InstallerService(_manifestStore);

        GamesListBox.ItemsSource = _filteredGames;
        UpdateAdminButtonState();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        WindowThemeHelper.EnableDarkTitleBar(this);

        _gpuInfo = GpuDetectionService.DetectPrimaryGpu();
        UpdateGpuPill();

        _currentRelease = _releaseService.GetCachedRelease();
        UpdateReleasePill();

        await ScanGamesAsync();
        await CheckLatestReleaseSilentAsync();
    }

    private async Task ScanGamesAsync()
    {
        string? previousId = _selectedGame?.Id;
        SetBusy(true, "Scanning installed games...", indeterminate: true);

        try
        {
            List<GameEntry> scanned = await _scannerService.ScanInstalledGamesAsync();
            foreach (GameEntry game in scanned)
            {
                game.Analysis = _analyzerService.Analyze(game, _currentRelease);
            }

            _allGames.Clear();
            _allGames.AddRange(scanned);
            ApplyFilterAndRestoreSelection(previousId);

            SetStatus($"Found {_allGames.Count} games ({_allGames.Count(g => g.Analysis?.IsInstalled == true)} with MFG Unlocker installed).");
        }
        catch (Exception ex)
        {
            SetStatus($"Error scanning games: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task CheckLatestReleaseSilentAsync()
    {
        try
        {
            ReleaseInfo latest = await _releaseService.GetLatestReleaseInfoAsync();
            _currentRelease = latest;
            UpdateReleasePill();
            ReanalyzeAllGames();
        }
        catch
        {
            if (_currentRelease is { HasValidCachedFile: true })
            {
                SetStatus($"Using offline cached release ({_currentRelease.TagName}).");
            }
            else
            {
                ReleaseStatusText.Text = "Offline • Not downloaded";
            }
        }
    }

    private async Task EnsureLatestReleaseReadyAsync()
    {
        if (_currentRelease is null || string.IsNullOrWhiteSpace(_currentRelease.ZipDownloadUrl))
        {
            SetStatus("Fetching latest release info from GitHub...");
            ReleaseInfo latest = await _releaseService.GetLatestReleaseInfoAsync();
            _currentRelease = latest;
            UpdateReleasePill();
        }

        if (_currentRelease.HasValidCachedFile)
        {
            return;
        }

        Progress<double> progress = new(pct =>
        {
            MainProgressBar.IsIndeterminate = false;
            MainProgressBar.Value = pct;
            SetStatus($"Downloading {_currentRelease.ZipAssetName} ({pct:F0}%)...");
        });

        _currentRelease = await _releaseService.DownloadAndVerifyReleaseAsync(_currentRelease, progress);
        UpdateReleasePill();
    }

    private void ReanalyzeAllGames()
    {
        foreach (GameEntry game in _allGames)
        {
            game.Analysis = _analyzerService.Analyze(game, _currentRelease);
        }

        if (_selectedGame is not null)
        {
            RenderSelectedGame(_selectedGame);
        }
    }

    private void ApplyFilterAndRestoreSelection(string? preferredGameId = null)
    {
        string search = SearchTextBox.Text.Trim();
        bool onlyInstalled = FilterInstalledRadio.IsChecked == true;
        bool onlyDb = FilterDbRadio.IsChecked == true;

        IEnumerable<GameEntry> query = _allGames;

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(g =>
                g.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || g.Source.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (onlyInstalled)
        {
            query = query.Where(g => g.Analysis?.IsInstalled == true);
        }
        else if (onlyDb)
        {
            query = query.Where(g => g.Compatibility is not null);
        }

        _filteredGames.Clear();
        foreach (GameEntry item in query)
        {
            _filteredGames.Add(item);
        }

        GamesCountText.Text = $"{_filteredGames.Count} / {_allGames.Count} games";

        if (!string.IsNullOrWhiteSpace(preferredGameId))
        {
            GameEntry? match = _filteredGames.FirstOrDefault(g =>
                string.Equals(g.Id, preferredGameId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                GamesListBox.SelectedItem = match;
                return;
            }
        }

        if (GamesListBox.SelectedItem is null && _filteredGames.Count > 0)
        {
            GamesListBox.SelectedIndex = 0;
        }
        else if (_filteredGames.Count == 0)
        {
            _selectedGame = null;
            RenderSelectedGame(null);
        }
    }

    private void RenderSelectedGame(GameEntry? game)
    {
        if (game is null)
        {
            EmptyStatePanel.Visibility = Visibility.Visible;
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyStatePanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;

        InstallAnalysis analysis = game.Analysis ?? _analyzerService.Analyze(game, _currentRelease);
        game.Analysis = analysis;

        DetailGameTitle.Text = game.Name;
        DetailSourceBadge.Text = game.Source;
        DetailExePath.Text = string.IsNullOrWhiteSpace(game.ExecutablePath)
            ? "(Executable not detected — installing to root folder)"
            : game.ExecutablePath;
        DetailTargetDir.Text = game.TargetDirectory;
        RemoveManualButton.Visibility = game.IsManual ? Visibility.Visible : Visibility.Collapsed;

        // Top right status pill
        DetailStatusPillText.Text = game.StatusBadgeText;
        (Brush pillBg, Brush pillFg) = GetStatusBrushes(game.StatusBadgeKind);
        DetailStatusPill.Background = pillBg;
        DetailStatusPillText.Foreground = pillFg;

        // Anti-cheat banner
        if (analysis.HasAntiCheat)
        {
            AntiCheatBanner.Visibility = Visibility.Visible;
            AntiCheatDetailsText.Text =
                string.Join(" • ", analysis.AntiCheatFindings) +
                "\nMFG Unlocker is not supported in games with anti-cheat and may result in an account ban.";
        }
        else
        {
            AntiCheatBanner.Visibility = Visibility.Collapsed;
        }

        // Legacy banner
        if (analysis.IsLegacyInstalled)
        {
            LegacyBanner.Visibility = Visibility.Visible;
            LegacyDetailsText.Text =
                $"Legacy mod files found: {string.Join(", ", analysis.LegacyFilesFound)}. Remove them manually to avoid conflicts.";
        }
        else
        {
            LegacyBanner.Visibility = Visibility.Collapsed;
        }

        // Compatibility card
        if (game.Compatibility is not null)
        {
            CompatDbStatusText.Text = $"Matched in compatibility database ({game.Compatibility.Name})";
            CompatDbStatusText.Foreground = (Brush)FindResource("BadgeInstalledFg");
            CompatPreferredText.Text = game.Compatibility.PreferredDll;
            CompatAlternativesText.Text = game.Compatibility.AlternativeDlls.Count > 0
                ? string.Join(", ", game.Compatibility.AlternativeDlls)
                : "None";
            CompatAvoidText.Text = game.Compatibility.AvoidDlls.Count > 0
                ? string.Join(" | ", game.Compatibility.AvoidDlls.Select(kv => $"{kv.Key} ({kv.Value})"))
                : "None";
            CompatAvoidText.Foreground = game.Compatibility.AvoidDlls.Count > 0
                ? (Brush)FindResource("BadgeAntiCheatFg")
                : (Brush)FindResource("TextSecondaryBrush");
            CompatNotesText.Text = string.IsNullOrWhiteSpace(game.Compatibility.Notes)
                ? "No additional notes."
                : game.Compatibility.Notes;
        }
        else
        {
            CompatDbStatusText.Text = "Not in compatibility DB — using default priority (version.dll / winmm.dll)";
            CompatDbStatusText.Foreground = (Brush)FindResource("TextPrimaryBrush");
            CompatPreferredText.Text = "version.dll (default)";
            CompatAlternativesText.Text = "winmm.dll, dinput8.dll, dxgi.dll...";
            CompatAvoidText.Text = "No game-specific data";
            CompatAvoidText.Foreground = (Brush)FindResource("TextSecondaryBrush");
            CompatNotesText.Text = "If Backspace does not open the menu in game, uninstall and try an alternative DLL name.";
        }

        CompatFilesText.Text =
            $"Config RTXMFG-Universal.json: {(analysis.HasConfigJson ? "Present" : "Not present")}  •  Diagnostic logs: {analysis.LogFileCount} file(s)";

        RecommendationReasonText.Text = analysis.RecommendationReason;

        PopulateDllComboBox(analysis);
        RenderOptimizationSection(analysis);
        UpdateActionButtons(analysis);
    }

    private void RenderOptimizationSection(InstallAnalysis analysis)
    {
        GameOptimizationSettings opt = analysis.Optimization;
        if (opt.IniExists)
        {
            UnlockGameFgCheckBox.IsChecked = opt.UnlockHiddenGameFgOption;
            LegacyRtx30PacingCheckBox.IsChecked = opt.UseLegacyRtx30Pacing;
            OptimizationStatusText.Text =
                $"RTX40MFG-UI.ini: Present • GpuNameOverride={(opt.UnlockHiddenGameFgOption ? 1 : 0)} • Rtx30FlipMetering={(opt.UseLegacyRtx30Pacing ? 0 : 1)}";
        }
        else
        {
            UnlockGameFgCheckBox.IsChecked = _gpuInfo.IsRtx20Or30;
            LegacyRtx30PacingCheckBox.IsChecked = false;
            OptimizationStatusText.Text =
                "RTX40MFG-UI.ini: Not present (will be created when saved or on 1-Click Install if auto-apply is checked).";
        }

        AutoApplyOptOnInstallCheckBox.IsChecked = _gpuInfo.IsRtx20Or30 || opt.IniExists;

        if (_gpuInfo.IsRtx20Or30)
        {
            Rtx2030InfoText.Text =
                $"Detected {_gpuInfo.Name} ({_gpuInfo.ArchitectureLabel}). " +
                (_gpuInfo.IsHagsEnabled
                    ? "HAGS is enabled. Recommended: unlock hidden in-game FG toggle (GpuNameOverride=1) and keep driver flip metering on."
                    : "WARNING: Windows HAGS is disabled! DLSS Frame Generation on RTX 20xx/30xx requires HAGS — click 'Enable HAGS' in the top bar and reboot.");
        }
        else
        {
            Rtx2030InfoText.Text =
                $"Detected {_gpuInfo.Name} ({_gpuInfo.ArchitectureLabel}). " +
                "These compatibility options in RTX40MFG-UI.ini are primarily for RTX 20xx / 30xx GPUs.";
        }
    }

    private GameOptimizationSettings ReadOptimizationFromUi() => new()
    {
        UnlockHiddenGameFgOption = UnlockGameFgCheckBox.IsChecked == true,
        UseLegacyRtx30Pacing = LegacyRtx30PacingCheckBox.IsChecked == true
    };

    private void PopulateDllComboBox(InstallAnalysis analysis)
    {
        _isUpdatingDllList = true;
        try
        {
            List<DllOptionItem> items = [];
            DllOptionItem? selected = null;

            foreach (string dll in ProxyDllCatalog.SupportedDlls)
            {
                DllSlotStatus status = analysis.DllStatuses.TryGetValue(dll, out DllSlotStatus st)
                    ? st
                    : DllSlotStatus.Available;
                string detail = analysis.DllStatusDetails.TryGetValue(dll, out string? dt)
                    ? dt
                    : "Available";

                bool isRecommended = string.Equals(dll, analysis.RecommendedDll, StringComparison.OrdinalIgnoreCase);

                string tag = status switch
                {
                    DllSlotStatus.InstalledMfg => "Installed here",
                    DllSlotStatus.OccupiedOther => "Occupied by another file",
                    DllSlotStatus.Avoided => "Avoid",
                    DllSlotStatus.BinkOriginalPresent => "Bink Hook (ready)",
                    DllSlotStatus.BinkMissingOriginal => "Missing original Bink DLL",
                    _ => isRecommended ? "Recommended" : "Available"
                };

                DllOptionItem option = new()
                {
                    DllName = dll,
                    DisplayText = $"{dll,-18} —  {tag}",
                    DetailText = detail,
                    Status = status,
                    IsRecommended = isRecommended
                };

                items.Add(option);
                if (isRecommended)
                {
                    selected = option;
                }
            }

            DllComboBox.ItemsSource = items;
            DllComboBox.SelectedItem = selected ?? items.FirstOrDefault();
            UpdateDllSlotDetail();
        }
        finally
        {
            _isUpdatingDllList = false;
        }
    }

    private void UpdateDllSlotDetail()
    {
        if (DllComboBox.SelectedItem is not DllOptionItem item)
        {
            DllSlotDetailText.Text = string.Empty;
            return;
        }

        DllSlotDetailText.Text = item.DetailText;
        DllSlotDetailText.Foreground = item.Status switch
        {
            DllSlotStatus.InstalledMfg => (Brush)FindResource("BadgeInstalledFg"),
            DllSlotStatus.OccupiedOther or DllSlotStatus.Avoided => (Brush)FindResource("BadgeAntiCheatFg"),
            DllSlotStatus.BinkOriginalPresent => (Brush)FindResource("BadgeReadyFg"),
            DllSlotStatus.BinkMissingOriginal => (Brush)FindResource("TextSecondaryBrush"),
            _ => (Brush)FindResource("TextSecondaryBrush")
        };
    }

    private void UpdateActionButtons(InstallAnalysis analysis)
    {
        DllOptionItem? selectedDll = DllComboBox.SelectedItem as DllOptionItem;

        UninstallButton.Visibility = analysis.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.IsEnabled = !_isBusy && analysis.IsInstalled;

        if (!analysis.IsInstalled)
        {
            PrimaryActionButton.Content = "Install Now (1-Click)";
            PrimaryActionButton.Style = (Style)FindResource("PrimaryGreenButton");
        }
        else if (_currentRelease is not null
                 && !string.IsNullOrWhiteSpace(analysis.InstalledVersion)
                 && !string.Equals(analysis.InstalledVersion, _currentRelease.TagName, StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(_currentRelease.TagName, "local", StringComparison.OrdinalIgnoreCase))
        {
            PrimaryActionButton.Content = $"Update to {_currentRelease.TagName}";
            PrimaryActionButton.Style = (Style)FindResource("PrimaryBlueButton");
        }
        else
        {
            PrimaryActionButton.Content = "Reinstall / Switch DLL";
            PrimaryActionButton.Style = (Style)FindResource("PrimaryBlueButton");
        }

        bool canUseSelectedSlot = selectedDll is not null
                                  && selectedDll.Status is not (DllSlotStatus.OccupiedOther or DllSlotStatus.BinkMissingOriginal);
        PrimaryActionButton.IsEnabled = !_isBusy && canUseSelectedSlot;
    }

    private (Brush Bg, Brush Fg) GetStatusBrushes(string kind) => kind switch
    {
        "Installed" => ((Brush)FindResource("BadgeInstalledBg"), (Brush)FindResource("BadgeInstalledFg")),
        "AntiCheat" => ((Brush)FindResource("BadgeAntiCheatBg"), (Brush)FindResource("BadgeAntiCheatFg")),
        "Legacy" => ((Brush)FindResource("BadgeLegacyBg"), (Brush)FindResource("BadgeLegacyFg")),
        "Ready" => ((Brush)FindResource("BadgeReadyBg"), (Brush)FindResource("BadgeReadyFg")),
        _ => ((Brush)FindResource("BadgeDefaultBg"), (Brush)FindResource("BadgeDefaultFg"))
    };

    private async void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is null || DllComboBox.SelectedItem is not DllOptionItem dllItem)
        {
            return;
        }

        InstallAnalysis? analysis = _selectedGame.Analysis;
        if (analysis?.HasAntiCheat == true)
        {
            MessageBoxResult warn = MessageBox.Show(
                this,
                $"'{_selectedGame.Name}' shows anti-cheat indicators ({string.Join(", ", analysis.AntiCheatFindings)}).\n\n" +
                "MFG Unlocker is NOT supported in anti-cheat games and may risk an account ban.\n\nAre you sure you want to continue?",
                "Anti-Cheat Warning",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (warn != MessageBoxResult.Yes)
            {
                return;
            }
        }

        if (dllItem.Status == DllSlotStatus.Avoided)
        {
            MessageBoxResult warnAvoid = MessageBox.Show(
                this,
                $"DLL name '{dllItem.DllName}' is marked as AVOID for this title:\n{dllItem.DetailText}\n\nInstall with this name anyway?",
                "Compatibility Warning",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (warnAvoid != MessageBoxResult.Yes)
            {
                return;
            }
        }

        SetBusy(true, $"Preparing to install for {_selectedGame.Name}...", indeterminate: false);

        try
        {
            await EnsureLatestReleaseReadyAsync();

            SetStatus($"Installing {dllItem.DllName} for {_selectedGame.Name}...");
            InstalledManifestEntry result = await _installerService.InstallAsync(_selectedGame, dllItem.DllName, _currentRelease!);

            bool savedOptimization = false;
            if (AutoApplyOptOnInstallCheckBox.IsChecked == true)
            {
                OptimizationService.Save(_selectedGame.TargetDirectory, ReadOptimizationFromUi());
                savedOptimization = true;
            }

            _selectedGame.Analysis = _analyzerService.Analyze(_selectedGame, _currentRelease);
            RenderSelectedGame(_selectedGame);

            SetStatus($"Installed {result.InstalledDllName} ({result.VersionTag}) for {_selectedGame.Name}.");
            string optNote = savedOptimization
                ? $"\n• RTX 20xx/30xx config: {OptimizationService.CompatibilityIniFileName} updated"
                : string.Empty;
            MessageBox.Show(
                this,
                $"MFG Unlocker ({result.VersionTag}) installed successfully!\n\n" +
                $"• DLL file: {result.InstalledDllName}\n" +
                $"• Directory: {result.TargetDirectory}{optNote}\n\n" +
                "Launch the game, enable Frame Generation in settings, and press Backspace to open the menu.",
                "Installation Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (UnauthorizedAccessException)
        {
            HandleUnauthorizedAccess("write to a Windows-protected game directory");
        }
        catch (Exception ex)
        {
            SetStatus($"Install failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Install Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is null || _selectedGame.Analysis is null)
        {
            return;
        }

        string? dllName = _selectedGame.Analysis.InstalledDllName;
        MessageBoxResult confirm = MessageBox.Show(
            this,
            $"Uninstall MFG Unlocker ({dllName}) from '{_selectedGame.Name}'?\n\n" +
            "(RTXMFG-Universal.json settings will be kept; any backed-up Bink DLL will be restored.)",
            "Confirm Uninstall",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        bool deleteLogs = DeleteLogsCheckBox.IsChecked == true;
        SetBusy(true, $"Uninstalling from {_selectedGame.Name}...", indeterminate: true);

        try
        {
            await _installerService.UninstallAsync(_selectedGame, _selectedGame.Analysis, deleteLogs);
            _selectedGame.Analysis = _analyzerService.Analyze(_selectedGame, _currentRelease);
            RenderSelectedGame(_selectedGame);
            SetStatus($"Uninstalled MFG Unlocker from {_selectedGame.Name}.");
        }
        catch (UnauthorizedAccessException)
        {
            HandleUnauthorizedAccess("delete files in a Windows-protected game directory");
        }
        catch (Exception ex)
        {
            SetStatus($"Uninstall failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Uninstall Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void CheckDownloadReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Checking latest GitHub release...", indeterminate: true);

        try
        {
            ReleaseInfo latest = await _releaseService.GetLatestReleaseInfoAsync();
            _currentRelease = latest;
            UpdateReleasePill();

            if (!latest.HasValidCachedFile)
            {
                Progress<double> progress = new(pct =>
                {
                    MainProgressBar.IsIndeterminate = false;
                    MainProgressBar.Value = pct;
                    SetStatus($"Downloading {latest.ZipAssetName} ({pct:F0}%)...");
                });

                _currentRelease = await _releaseService.DownloadAndVerifyReleaseAsync(latest, progress);
                UpdateReleasePill();
                SetStatus($"Downloaded and SHA256-verified {_currentRelease.TagName}.");
            }
            else
            {
                SetStatus($"Latest release {_currentRelease.TagName} is already cached.");
            }

            ReanalyzeAllGames();
        }
        catch (Exception ex)
        {
            SetStatus($"Release check failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "GitHub Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ImportLocalButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dlg = new()
        {
            Title = "Select MFGUnlocker-v*.zip or MFGUnlocker.dll",
            Filter = "MFG Unlocker (*.zip;*.dll)|*.zip;*.dll|All files (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        await ImportLocalPayloadAsync(dlg.FileName);
    }

    private async Task ImportLocalPayloadAsync(string filePath)
    {
        SetBusy(true, "Importing MFG Unlocker payload...", indeterminate: true);
        try
        {
            _currentRelease = await _releaseService.ImportLocalPayloadAsync(filePath);
            UpdateReleasePill();
            ReanalyzeAllGames();
            SetStatus($"Imported {_currentRelease.TagName} from local file.");
        }
        catch (Exception ex)
        {
            SetStatus($"Import failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void AddExeButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dlg = new()
        {
            Title = "Select game executable (.exe)",
            Filter = "Executable (*.exe)|*.exe"
        };

        if (dlg.ShowDialog(this) == true)
        {
            AddOrSelectCustomGameFromPath(dlg.FileName);
        }
    }

    private void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dlg = new()
        {
            Title = "Select game installation folder"
        };

        if (dlg.ShowDialog(this) == true)
        {
            AddOrSelectCustomGameFromPath(dlg.FolderName);
        }
    }

    private void ChangeExeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is null)
        {
            return;
        }

        OpenFileDialog dlg = new()
        {
            Title = $"Select main executable for {_selectedGame.Name}",
            Filter = "Executable (*.exe)|*.exe",
            InitialDirectory = Directory.Exists(_selectedGame.RootDirectory)
                ? _selectedGame.RootDirectory
                : _selectedGame.TargetDirectory
        };

        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        string exePath = Path.GetFullPath(dlg.FileName);
        string targetDir = ManifestStore.NormalizePathKey(Path.GetDirectoryName(exePath) ?? exePath);

        _selectedGame.ExecutablePath = exePath;
        _selectedGame.TargetDirectory = targetDir;
        _selectedGame.Compatibility ??= CompatibilityDatabase.Match(_selectedGame.Name, exePath);
        _selectedGame.Analysis = _analyzerService.Analyze(_selectedGame, _currentRelease);

        _manifestStore.AddOrUpdateCustomGame(new CustomGameRecord
        {
            Name = _selectedGame.Name,
            RootDirectory = _selectedGame.RootDirectory,
            ExecutablePath = exePath
        });

        RenderSelectedGame(_selectedGame);
        SetStatus($"Updated executable path for {_selectedGame.Name}.");
    }

    private void RemoveManualButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is null || !_selectedGame.IsManual)
        {
            return;
        }

        _manifestStore.RemoveCustomGame(_selectedGame.TargetDirectory);
        _allGames.Remove(_selectedGame);
        _selectedGame = null;
        ApplyFilterAndRestoreSelection();
        SetStatus("Removed game from manual list.");
    }

    private void OpenTargetFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is null || !Directory.Exists(_selectedGame.TargetDirectory))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_selectedGame.TargetDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open folder: {ex.Message}");
        }
    }

    private async void RescanButton_Click(object sender, RoutedEventArgs e)
        => await ScanGamesAsync();

    private void GamesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedGame = GamesListBox.SelectedItem as GameEntry;
        RenderSelectedGame(_selectedGame);
    }

    private void DllComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingDllList || _selectedGame?.Analysis is null)
        {
            return;
        }

        UpdateDllSlotDetail();
        UpdateActionButtons(_selectedGame.Analysis);
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        ApplyFilterAndRestoreSelection(_selectedGame?.Id);
    }

    private void FilterRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ApplyFilterAndRestoreSelection(_selectedGame?.Id);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        string first = paths[0];
        if (File.Exists(first)
            && (first.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || (first.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(first).Contains("MFG", StringComparison.OrdinalIgnoreCase))))
        {
            await ImportLocalPayloadAsync(first);
            return;
        }

        AddOrSelectCustomGameFromPath(first);
    }

    private void AddOrSelectCustomGameFromPath(string path)
    {
        try
        {
            bool isExe = File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            bool isDir = Directory.Exists(path);
            if (!isExe && !isDir)
            {
                MessageBox.Show(this, "Please select a game .exe file or installation folder.", "Invalid Path");
                return;
            }

            string inferredName = InferGameName(path);
            GameEntry newEntry = _scannerService.CreateGameEntry(inferredName, "Manual", path, isManual: true);

            if (string.IsNullOrWhiteSpace(newEntry.TargetDirectory) || !Directory.Exists(newEntry.TargetDirectory))
            {
                MessageBox.Show(this, "Could not resolve a valid game directory.", "Error");
                return;
            }

            // If this game target directory is already in the list, select it immediately.
            GameEntry? existing = _allGames.FirstOrDefault(g =>
                string.Equals(
                    ManifestStore.NormalizePathKey(g.TargetDirectory),
                    ManifestStore.NormalizePathKey(newEntry.TargetDirectory),
                    StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                if (isExe)
                {
                    existing.ExecutablePath = newEntry.ExecutablePath;
                }

                existing.Analysis = _analyzerService.Analyze(existing, _currentRelease);
                FilterAllRadio.IsChecked = true;
                SearchTextBox.Text = string.Empty;
                ApplyFilterAndRestoreSelection(existing.Id);
                SetStatus($"Selected '{existing.Name}'.");
                return;
            }

            // Prefer official compatibility name if matched cleanly
            if (newEntry.Compatibility is not null)
            {
                newEntry.Name = newEntry.Compatibility.Name;
            }

            newEntry.Analysis = _analyzerService.Analyze(newEntry, _currentRelease);

            _manifestStore.AddOrUpdateCustomGame(new CustomGameRecord
            {
                Name = newEntry.Name,
                RootDirectory = newEntry.RootDirectory,
                ExecutablePath = string.IsNullOrWhiteSpace(newEntry.ExecutablePath)
                    ? newEntry.TargetDirectory
                    : newEntry.ExecutablePath
            });

            _allGames.Insert(0, newEntry);
            FilterAllRadio.IsChecked = true;
            SearchTextBox.Text = string.Empty;
            ApplyFilterAndRestoreSelection(newEntry.Id);
            SetStatus($"Added '{newEntry.Name}' to game list.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could Not Add Game", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string InferGameName(string path)
    {
        if (File.Exists(path))
        {
            string exeName = Path.GetFileNameWithoutExtension(path);
            string? parentDir = Path.GetFileName(Path.GetDirectoryName(path));

            // Walk up past typical binary folder names (Win64/x64/bin/Content) to get a readable game name
            DirectoryInfo? dir = new DirectoryInfo(Path.GetDirectoryName(path)!);
            while (dir is not null
                   && (dir.Name.Equals("Win64", StringComparison.OrdinalIgnoreCase)
                       || dir.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase)
                       || dir.Name.Equals("x64", StringComparison.OrdinalIgnoreCase)
                       || dir.Name.Equals("x64_dx12", StringComparison.OrdinalIgnoreCase)
                       || dir.Name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                       || dir.Name.Equals("Content", StringComparison.OrdinalIgnoreCase)))
            {
                dir = dir.Parent;
            }

            if (dir is not null && !string.IsNullOrWhiteSpace(dir.Name))
            {
                return dir.Name;
            }

            return !string.IsNullOrWhiteSpace(parentDir) ? parentDir : exeName;
        }

        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string folderName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(folderName) ? trimmed : folderName;
    }

    private void AdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (AdminHelper.IsAdministrator())
        {
            return;
        }

        if (AdminHelper.RestartAsAdministrator())
        {
            Application.Current.Shutdown();
        }
    }

    private void HandleUnauthorizedAccess(string actionDescription)
    {
        SetStatus("Write access denied — Administrator privileges required.");
        MessageBoxResult res = MessageBox.Show(
            this,
            $"Windows denied permission to {actionDescription}.\n\nRestart the application as Administrator now?",
            "Administrator Required",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (res == MessageBoxResult.Yes && AdminHelper.RestartAsAdministrator())
        {
            Application.Current.Shutdown();
        }
    }

    private void ApplyRecommendedOptButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is null)
        {
            return;
        }

        GameOptimizationSettings recommended = GameOptimizationSettings.CreateRecommendedForRtx20Or30();
        UnlockGameFgCheckBox.IsChecked = recommended.UnlockHiddenGameFgOption;
        LegacyRtx30PacingCheckBox.IsChecked = recommended.UseLegacyRtx30Pacing;
        SaveOptimizationSettingsForSelectedGame(recommended, "Applied recommended RTX 20xx/30xx settings");
    }

    private void SaveOptimizationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is null)
        {
            return;
        }

        SaveOptimizationSettingsForSelectedGame(ReadOptimizationFromUi(), "Saved RTX40MFG-UI.ini settings");
    }

    private void SaveOptimizationSettingsForSelectedGame(GameOptimizationSettings settings, string statusPrefix)
    {
        if (_selectedGame is null || string.IsNullOrWhiteSpace(_selectedGame.TargetDirectory))
        {
            return;
        }

        try
        {
            OptimizationService.Save(_selectedGame.TargetDirectory, settings);
            _selectedGame.Analysis = _analyzerService.Analyze(_selectedGame, _currentRelease);
            RenderSelectedGame(_selectedGame);
            SetStatus($"{statusPrefix} for {_selectedGame.Name}.");
        }
        catch (UnauthorizedAccessException)
        {
            HandleUnauthorizedAccess("write RTX40MFG-UI.ini to a protected game directory");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to save optimization settings: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Save Optimization Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EnableHagsButton_Click(object sender, RoutedEventArgs e)
    {
        if (GpuDetectionService.TryEnableHags(out string? error))
        {
            _gpuInfo = GpuDetectionService.DetectPrimaryGpu();
            UpdateGpuPill();
            if (_selectedGame?.Analysis is not null)
            {
                RenderOptimizationSection(_selectedGame.Analysis);
            }

            SetStatus("HAGS enabled in Windows Registry. Restart your PC for it to take effect.");
            MessageBox.Show(
                this,
                "Hardware-Accelerated GPU Scheduling (HAGS) has been enabled.\n\nPlease restart Windows for the change to take effect.",
                "HAGS Enabled",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!AdminHelper.IsAdministrator())
        {
            HandleUnauthorizedAccess("enable Hardware-Accelerated GPU Scheduling (HAGS) in the Windows Registry");
        }
        else
        {
            MessageBox.Show(this, error ?? "Could not enable HAGS.", "HAGS Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateGpuPill()
    {
        string hagsText = _gpuInfo.IsHagsSupported
            ? (_gpuInfo.IsHagsEnabled ? "HAGS On" : "HAGS Off")
            : "HAGS N/A";

        GpuStatusText.Text = $"{_gpuInfo.ArchitectureLabel} • {hagsText}";
        GpuPillBorder.ToolTip = _gpuInfo.Name;

        if (_gpuInfo.IsHagsSupported && !_gpuInfo.IsHagsEnabled)
        {
            GpuStatusText.Foreground = (Brush)FindResource("BadgeLegacyFg");
            GpuPillBorder.Background = (Brush)FindResource("BadgeLegacyBg");
            EnableHagsButton.Visibility = Visibility.Visible;
        }
        else
        {
            GpuStatusText.Foreground = _gpuInfo.IsRtx20Or30
                ? (Brush)FindResource("BadgeReadyFg")
                : (Brush)FindResource("BadgeInstalledFg");
            GpuPillBorder.Background = (Brush)FindResource("BgCardBrush");
            EnableHagsButton.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateReleasePill()
    {
        if (_currentRelease is null)
        {
            ReleaseStatusText.Text = "MFG Unlocker not downloaded";
            ReleaseStatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
            return;
        }

        if (_currentRelease.HasValidCachedFile)
        {
            ReleaseStatusText.Text = $"{_currentRelease.TagName} • Cached";
            ReleaseStatusText.Foreground = (Brush)FindResource("BadgeInstalledFg");
        }
        else
        {
            ReleaseStatusText.Text = $"{_currentRelease.TagName} • Ready to download";
            ReleaseStatusText.Foreground = (Brush)FindResource("BadgeReadyFg");
        }
    }

    private void UpdateAdminButtonState()
    {
        if (AdminHelper.IsAdministrator())
        {
            AdminButton.Content = "Running as Admin";
            AdminButton.IsEnabled = false;
        }
        else
        {
            AdminButton.Content = "Run as Admin";
            AdminButton.IsEnabled = true;
        }
    }

    private void SetBusy(bool busy, string? statusText = null, bool indeterminate = false)
    {
        _isBusy = busy;
        CheckDownloadReleaseButton.IsEnabled = !busy;
        ImportLocalButton.IsEnabled = !busy;
        RescanButton.IsEnabled = !busy;
        ApplyRecommendedOptButton.IsEnabled = !busy;
        SaveOptimizationButton.IsEnabled = !busy;
        EnableHagsButton.IsEnabled = !busy;

        MainProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        MainProgressBar.IsIndeterminate = indeterminate;
        if (!busy)
        {
            MainProgressBar.Value = 0;
        }

        if (!string.IsNullOrWhiteSpace(statusText))
        {
            StatusTextBlock.Text = statusText;
        }

        if (_selectedGame?.Analysis is not null)
        {
            UpdateActionButtons(_selectedGame.Analysis);
        }
    }

    private void SetStatus(string text)
        => StatusTextBlock.Text = text;
}
