# MFG Unlocker 1-Click Installer

A Windows WPF desktop application (`MfgUnlockerInstaller`) for installing, upgrading, switching proxy DLL slots, and uninstalling [MFG-Unlocker](https://github.com/dashdogy/MFG-Unlocker) in PC games.

## Features

- **Automatic Game Discovery**: Scans installed games across **Steam**, **Epic Games**, **Xbox / Game Pass**, and **GOG**, plus support for **Manual** custom game entries.
- **Smart Executable & Target Folder Resolution**: Automatically resolves the target game executable and installation directory.
- **Release Management & SHA-256 Verification**: Fetches the latest release from `dashdogy/MFG-Unlocker` on GitHub, verifies `SHA256SUMS.txt`, and caches downloaded releases locally.
- **Safe Install / Upgrade / Uninstall**: Checks if the target game is currently running before modifying files, supports switching proxy DLL slots, and tracks installed manifests for clean removal or upgrades.
- **Built-in Compatibility & Proxy DLL Catalog**: Includes a compatibility database and proxy DLL catalog to guide DLL slot selection.

## Requirements

- **OS**: Windows
- **SDK**: [.NET 11 SDK](https://dotnet.microsoft.com/) (`net11.0-windows`, WPF)

## Project Structure

```text
MfgUnlockerInstaller.sln
build-release.ps1
src/
  MfgUnlockerInstaller/          # WPF application
    Data/                        # Compatibility database & proxy DLL catalog
    Helpers/                     # Admin & window theme helpers
    Models/                      # Data models
    Services/                    # Game scanner, release fetcher, analyzer, installer, manifest store
    Themes/                      # Dark theme XAML
tests/
  MfgUnlockerInstaller.Tests/    # Automated test runner
```

## Build & Publish

Run the release build script from PowerShell:

```powershell
.\build-release.ps1
```

This script:
1. Builds `MfgUnlockerInstaller.sln` in `Release` configuration.
2. Runs the automated tests (`tests/MfgUnlockerInstaller.Tests`).
3. Publishes a single-file `win-x64` executable to `publish\MfgUnlockerInstaller.exe`.

You can also run the commands manually:

```powershell
# Build
dotnet build MfgUnlockerInstaller.sln -c Release

# Run tests
dotnet run --project tests/MfgUnlockerInstaller.Tests/MfgUnlockerInstaller.Tests.csproj -c Release

# Run the app
dotnet run --project src/MfgUnlockerInstaller/MfgUnlockerInstaller.csproj
```
