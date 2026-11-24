using System.IO;
using System.IO.Compression;
using System.Text;
using MfgUnlockerInstaller.Data;
using MfgUnlockerInstaller.Models;
using MfgUnlockerInstaller.Services;

namespace MfgUnlockerInstaller.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        string tempRoot = Path.Combine(Path.GetTempPath(), $"MfgUnlockerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            RunTest("CompatibilityDatabase.Match & AntiCheat", TestCompatibilityDatabase);
            RunTest("GameScannerService VDF & ACF parsing", TestSteamManifestParsing);
            RunTest("ReleaseService SHA256SUMS & local zip import", () => TestReleaseService(tempRoot));
            RunTest("ExeResolverService UE, Cyberpunk, Witcher 3", () => TestExeResolver(tempRoot));
            RunTest("InstallAnalyzerService conflict & avoid resolution", () => TestInstallAnalyzer(tempRoot));
            RunTest("InstallerService standard & Bink hook install/uninstall", () => TestInstallerEndToEnd(tempRoot));
            RunTest("GpuDetectionService.ClassifyGpuName", TestGpuClassification);
            RunTest("OptimizationService RTX 20xx/30xx INI read/write", () => TestOptimizationService(tempRoot));
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Ignore cleanup failure in temp.
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Summary: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void RunTest(string name, Action action)
    {
        try
        {
            action();
            _passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"[FAIL] {name}: {ex.Message}");
        }
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T actual, T expected, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            throw new InvalidOperationException($"{message} (expected '{expected}', actual '{actual}')");
        }
    }

    private static void TestCompatibilityDatabase()
    {
        AssertEqual(CompatibilityDatabase.Entries.Count, 38, "Expected 38 documented games");
        AssertEqual(ProxyDllCatalog.SupportedDlls.Count, 19, "Expected 19 supported DLL names");

        var cp = CompatibilityDatabase.Match("Cyberpunk 2077");
        AssertTrue(cp is not null, "Cyberpunk 2077 should match");
        AssertEqual(cp!.PreferredDll, "xinput9_1_0.dll", "Cyberpunk preferred DLL");
        AssertTrue(cp.AvoidDlls.ContainsKey("version.dll"), "Cyberpunk should avoid version.dll");

        var wukong = CompatibilityDatabase.Match("Black Myth: Wukong");
        AssertTrue(wukong is not null && wukong.PreferredDll == "version.dll", "Wukong preferred DLL");

        var mhw = CompatibilityDatabase.Match("Monster Hunter Wilds");
        AssertTrue(mhw is not null && mhw.PreferredDll == "version.dll", "MHW preferred DLL");
        AssertTrue(mhw!.AvoidDlls.ContainsKey("dinput8.dll"), "MHW should avoid dinput8.dll");

        var witcher = CompatibilityDatabase.Match("The Witcher 3: Wild Hunt");
        AssertTrue(witcher is not null && witcher.PreferredDll == "winmm.dll", "Witcher 3 should match with prefix");

        var byExe = CompatibilityDatabase.Match("Unknown Folder Name", @"D:\Games\Stalker2\Binaries\Win64\Stalker2-Win64-Shipping.exe");
        AssertTrue(byExe is not null && byExe.Name == "S.T.A.L.K.E.R. 2", "Should match via exe hint");

        AssertTrue(CompatibilityDatabase.IsAntiCheatGame("Grand Theft Auto V Enhanced"), "GTA V Enhanced anti-cheat");
        AssertTrue(CompatibilityDatabase.IsAntiCheatGame("Battlefield 6"), "Battlefield 6 anti-cheat");
        AssertTrue(!CompatibilityDatabase.IsAntiCheatGame("Cyberpunk 2077"), "Cyberpunk has no anti-cheat");
    }

    private static void TestSteamManifestParsing()
    {
        string vdf = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            	}
            	"1"
            	{
            		"path"		"D:\\SteamLibrary"
            	}
            }
            """;

        List<string> libs = GameScannerService.ParseSteamLibraryPaths(vdf);
        AssertEqual(libs.Count, 2, "Should parse 2 Steam libraries");
        AssertEqual(libs[0], @"C:\Program Files (x86)\Steam", "First Steam library path");
        AssertEqual(libs[1], @"D:\SteamLibrary", "Second Steam library path");

        string acf = """
            "AppState"
            {
            	"appid"		"1091500"
            	"Universe"		"1"
            	"name"		"Cyberpunk 2077"
            	"installdir"		"Cyberpunk 2077"
            }
            """;

        (string? appId, string? name, string? installDir) = GameScannerService.ParseAppManifest(acf);
        AssertEqual(appId, "1091500", "Steam appid");
        AssertEqual(name, "Cyberpunk 2077", "Steam name");
        AssertEqual(installDir, "Cyberpunk 2077", "Steam installdir");
    }

    private static void TestReleaseService(string tempRoot)
    {
        string sums = """
            E002D624E3E7BA5CEDAB7211043C096FE8F555C30FF43500235A0686C7893F22  MFGUnlocker-v1.7.2.zip
            A9B1C01727FA72763BEF8F604E44EC165CE1C98272DE00719572B766A11CF28F  MFGUnlocker.dll
            """;

        Dictionary<string, string> parsed = ReleaseService.ParseSha256Sums(sums);
        AssertEqual(parsed.Count, 2, "Should parse 2 SHA256SUMS lines");
        AssertEqual(
            parsed["MFGUnlocker.dll"],
            "A9B1C01727FA72763BEF8F604E44EC165CE1C98272DE00719572B766A11CF28F",
            "MFGUnlocker.dll hash");

        string storeDir = Path.Combine(tempRoot, "store_rel");
        ManifestStore store = new(storeDir);
        ReleaseService service = new(store);

        string payloadZip = Path.Combine(tempRoot, "MFGUnlocker-test.zip");
        byte[] dummyDllBytes = Encoding.UTF8.GetBytes("DUMMY_MFG_UNLOCKER_PE_PAYLOAD_v1.7.2");
        using (ZipArchive zip = ZipFile.Open(payloadZip, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = zip.CreateEntry("MFGUnlocker.dll");
            using Stream s = entry.Open();
            s.Write(dummyDllBytes);
        }

        ReleaseInfo imported = service.ImportLocalPayload(payloadZip);
        AssertTrue(imported.IsCached && imported.HasValidCachedFile, "Imported payload should be cached");
        AssertTrue(File.Exists(imported.CachedDllPath!), "Cached DLL file must exist");
        AssertEqual(
            File.ReadAllBytes(imported.CachedDllPath!).SequenceEqual(dummyDllBytes),
            true,
            "Extracted bytes must match original");
    }

    private static void TestExeResolver(string tempRoot)
    {
        // Unreal Engine layout: root launcher + nested shipping exe
        string ueGame = Path.Combine(tempRoot, "UEGame");
        string ueShipping = Path.Combine(ueGame, "MyGame", "Binaries", "Win64", "MyGame-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(ueShipping)!);
        File.WriteAllBytes(Path.Combine(ueGame, "MyGameLauncher.exe"), new byte[1024]);
        File.WriteAllBytes(ueShipping, new byte[4096]);

        string resolvedUe = ExeResolverService.ResolveGameExecutable(ueGame);
        AssertEqual(resolvedUe, Path.GetFullPath(ueShipping), "Should resolve UE Shipping exe over root launcher");

        // Cyberpunk hint layout
        string cpGame = Path.Combine(tempRoot, "CP2077");
        string cpExe = Path.Combine(cpGame, "bin", "x64", "Cyberpunk2077.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(cpExe)!);
        File.WriteAllBytes(Path.Combine(cpGame, "REDprelauncher.exe"), new byte[2048]);
        File.WriteAllBytes(cpExe, new byte[2048]);

        string resolvedCp = ExeResolverService.ResolveGameExecutable(cpGame, CompatibilityDatabase.Match("Cyberpunk 2077"));
        AssertEqual(resolvedCp, Path.GetFullPath(cpExe), "Should resolve Cyberpunk exe via hint");

        // Witcher 3 DX12 hint layout
        string w3Game = Path.Combine(tempRoot, "Witcher3");
        string w3Dx12 = Path.Combine(w3Game, "bin", "x64_dx12", "witcher3.exe");
        string w3Dx11 = Path.Combine(w3Game, "bin", "x64", "witcher3.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(w3Dx12)!);
        Directory.CreateDirectory(Path.GetDirectoryName(w3Dx11)!);
        File.WriteAllBytes(w3Dx12, new byte[2048]);
        File.WriteAllBytes(w3Dx11, new byte[2048]);

        string resolvedW3 = ExeResolverService.ResolveGameExecutable(w3Game, CompatibilityDatabase.Match("The Witcher 3"));
        AssertEqual(resolvedW3, Path.GetFullPath(w3Dx12), "Should resolve Witcher 3 DX12 executable");
    }

    private static void TestInstallAnalyzer(string tempRoot)
    {
        ManifestStore store = new(Path.Combine(tempRoot, "store_analyze"));
        InstallAnalyzerService analyzer = new(store);

        // Monster Hunter Wilds with occupied version.dll and EasyAntiCheat folder
        string mhwDir = Path.Combine(tempRoot, "MHW");
        Directory.CreateDirectory(mhwDir);
        Directory.CreateDirectory(Path.Combine(mhwDir, "EasyAntiCheat"));
        string mhwExe = Path.Combine(mhwDir, "MonsterHunterWilds.exe");
        File.WriteAllBytes(mhwExe, new byte[64]);
        File.WriteAllBytes(Path.Combine(mhwDir, "version.dll"), Encoding.UTF8.GetBytes("OTHER_MOD_DLL"));
        File.WriteAllBytes(Path.Combine(mhwDir, "RTXMFG.dll"), Encoding.UTF8.GetBytes("OLD_MOD"));
        File.WriteAllText(Path.Combine(mhwDir, "RTXMFG-Universal.json"), "{}");
        File.WriteAllText(Path.Combine(mhwDir, "RTXMFG-01.log"), "log");

        GameEntry game = new()
        {
            Id = mhwDir,
            Name = "Monster Hunter Wilds",
            Source = "Steam",
            RootDirectory = mhwDir,
            TargetDirectory = mhwDir,
            ExecutablePath = mhwExe,
            Compatibility = CompatibilityDatabase.Match("Monster Hunter Wilds")
        };

        InstallAnalysis analysis = analyzer.Analyze(game, null);
        AssertTrue(analysis.HasAntiCheat, "Should detect EasyAntiCheat folder");
        AssertTrue(analysis.IsLegacyInstalled, "Should detect RTXMFG.dll legacy file");
        AssertTrue(analysis.HasConfigJson, "Should detect RTXMFG-Universal.json");
        AssertEqual(analysis.LogFileCount, 1, "Should count 1 log file");
        AssertEqual(analysis.DllStatuses["version.dll"], DllSlotStatus.OccupiedOther, "version.dll should be OccupiedOther");
        AssertEqual(analysis.DllStatuses["dinput8.dll"], DllSlotStatus.Avoided, "dinput8.dll should be Avoided");
        AssertEqual(analysis.RecommendedDll, "winmm.dll", "Should fall back to alternative winmm.dll");
    }

    private static void TestInstallerEndToEnd(string tempRoot)
    {
        ManifestStore store = new(Path.Combine(tempRoot, "store_install"));
        ReleaseService releaseService = new(store);
        InstallAnalyzerService analyzer = new(store);
        InstallerService installer = new(store);

        // Prepare cached payload DLL
        string sourceDll = Path.Combine(tempRoot, "MFGUnlocker.dll");
        byte[] mfgBytes = Encoding.UTF8.GetBytes("MFG_UNLOCKER_TEST_BINARY_V172");
        File.WriteAllBytes(sourceDll, mfgBytes);
        ReleaseInfo release = releaseService.ImportLocalPayload(sourceDll);
        release.TagName = "v1.7.2";

        // 1. Standard proxy install, conflict guard, slot switch, and uninstall
        string stdGameDir = Path.Combine(tempRoot, "StdGame");
        Directory.CreateDirectory(stdGameDir);
        string stdExe = Path.Combine(stdGameDir, "Game.exe");
        File.WriteAllBytes(stdExe, new byte[64]);
        File.WriteAllBytes(Path.Combine(stdGameDir, "dxgi.dll"), Encoding.UTF8.GetBytes("RESHADE_OTHER_MOD"));
        File.WriteAllText(Path.Combine(stdGameDir, "RTXMFG-Universal.json"), "{\"keep\":true}");
        File.WriteAllText(Path.Combine(stdGameDir, "RTXMFG-1.log"), "diag");

        GameEntry stdGame = new()
        {
            Id = stdGameDir,
            Name = "Hogwarts Legacy",
            Source = "Steam",
            RootDirectory = stdGameDir,
            TargetDirectory = stdGameDir,
            ExecutablePath = stdExe,
            Compatibility = CompatibilityDatabase.Match("Hogwarts Legacy")
        };

        // Must refuse to overwrite third-party dxgi.dll
        bool threwOnConflict = false;
        try
        {
            installer.Install(stdGame, "dxgi.dll", release);
        }
        catch (InvalidOperationException)
        {
            threwOnConflict = true;
        }
        AssertTrue(threwOnConflict, "Installer must refuse to overwrite unrelated dxgi.dll");

        // Install as version.dll
        installer.Install(stdGame, "version.dll", release);
        AssertTrue(File.Exists(Path.Combine(stdGameDir, "version.dll")), "version.dll must be created");

        InstallAnalysis afterInstall = analyzer.Analyze(stdGame, release);
        AssertTrue(afterInstall.IsInstalled, "Analyzer must detect installation");
        AssertEqual(afterInstall.InstalledDllName, "version.dll", "Installed DLL name");

        // Switch slot from version.dll -> winmm.dll (must clean version.dll so only one copy exists)
        installer.Install(stdGame, "winmm.dll", release);
        AssertTrue(!File.Exists(Path.Combine(stdGameDir, "version.dll")), "Old slot version.dll must be removed on switch");
        AssertTrue(File.Exists(Path.Combine(stdGameDir, "winmm.dll")), "New slot winmm.dll must exist");

        InstallAnalysis afterSwitch = analyzer.Analyze(stdGame, release);
        AssertEqual(afterSwitch.InstalledDllName, "winmm.dll", "Switched DLL name");

        // Uninstall with deleteLogs=true (must preserve JSON config)
        installer.Uninstall(stdGame, afterSwitch, deleteLogs: true);
        AssertTrue(!File.Exists(Path.Combine(stdGameDir, "winmm.dll")), "winmm.dll must be removed on uninstall");
        AssertTrue(File.Exists(Path.Combine(stdGameDir, "RTXMFG-Universal.json")), "Config JSON must be preserved");
        AssertTrue(!File.Exists(Path.Combine(stdGameDir, "RTXMFG-1.log")), "Log file must be deleted when requested");

        // 2. Bink Hook route install & uninstall
        string binkGameDir = Path.Combine(tempRoot, "BinkGame");
        Directory.CreateDirectory(binkGameDir);
        string binkExe = Path.Combine(binkGameDir, "Control.exe");
        File.WriteAllBytes(binkExe, new byte[64]);
        byte[] originalBinkBytes = Encoding.UTF8.GetBytes("ORIGINAL_RAD_BINK2W64_DLL");
        File.WriteAllBytes(Path.Combine(binkGameDir, "bink2w64.dll"), originalBinkBytes);

        GameEntry binkGame = new()
        {
            Id = binkGameDir,
            Name = "Control Resonant",
            Source = "Steam",
            RootDirectory = binkGameDir,
            TargetDirectory = binkGameDir,
            ExecutablePath = binkExe,
            Compatibility = CompatibilityDatabase.Match("Control Resonant")
        };

        installer.Install(binkGame, "bink2w64.dll", release);
        AssertTrue(File.Exists(Path.Combine(binkGameDir, "bink2w64Hooked.dll")), "Original bink2w64.dll must be backed up as bink2w64Hooked.dll");
        AssertTrue(File.ReadAllBytes(Path.Combine(binkGameDir, "bink2w64Hooked.dll")).SequenceEqual(originalBinkBytes), "Backup bytes must match original");
        AssertTrue(File.ReadAllBytes(Path.Combine(binkGameDir, "bink2w64.dll")).SequenceEqual(mfgBytes), "bink2w64.dll must now be MFG Unlocker");

        InstallAnalysis binkAnalysis = analyzer.Analyze(binkGame, release);
        AssertTrue(binkAnalysis.IsInstalled && binkAnalysis.InstalledDllName == "bink2w64.dll", "Analyzer must report Bink install");

        installer.Uninstall(binkGame, binkAnalysis, deleteLogs: false);
        AssertTrue(!File.Exists(Path.Combine(binkGameDir, "bink2w64Hooked.dll")), "Backup Hooked DLL must be moved back");
        AssertTrue(File.ReadAllBytes(Path.Combine(binkGameDir, "bink2w64.dll")).SequenceEqual(originalBinkBytes), "Original bink2w64.dll must be restored byte-for-byte");
    }

    private static void TestGpuClassification()
    {
        AssertEqual(GpuDetectionService.ClassifyGpuName("NVIDIA GeForce RTX 2080 Ti"), GpuArchitecture.Rtx20Turing, "RTX 2080 Ti");
        AssertEqual(GpuDetectionService.ClassifyGpuName("NVIDIA GeForce RTX 3070 Laptop GPU"), GpuArchitecture.Rtx30Ampere, "RTX 3070");
        AssertEqual(GpuDetectionService.ClassifyGpuName("NVIDIA GeForce RTX 4090"), GpuArchitecture.Rtx40Ada, "RTX 4090");
        AssertEqual(GpuDetectionService.ClassifyGpuName("NVIDIA GeForce RTX 5080"), GpuArchitecture.Rtx50Blackwell, "RTX 5080");
        AssertEqual(GpuDetectionService.ClassifyGpuName("AMD Radeon RX 7900 XTX"), GpuArchitecture.NonNvidia, "AMD Radeon");
        AssertEqual(GpuDetectionService.ClassifyGpuName("Intel(R) Arc(TM) A770 Graphics"), GpuArchitecture.NonNvidia, "Intel Arc");
    }

    private static void TestOptimizationService(string tempRoot)
    {
        string gameDir = Path.Combine(tempRoot, "OptGame");
        Directory.CreateDirectory(gameDir);
        string iniPath = OptimizationService.GetIniPath(gameDir);

        // Pre-existing INI with an unrelated section that must be preserved
        File.WriteAllText(iniPath, "[Menu]\nShowOverlay=1\n\n[Compatibility]\nOtherKey=keep\nGpuNameOverride=0\n");

        GameOptimizationSettings loaded = OptimizationService.Load(gameDir);
        AssertTrue(loaded.IniExists, "INI should be detected");
        AssertTrue(!loaded.UnlockHiddenGameFgOption, "GpuNameOverride=0 should load as false");
        AssertTrue(!loaded.UseLegacyRtx30Pacing, "Missing Rtx30FlipMetering should default to false (flip metering on)");

        // Save recommended + legacy pacing
        GameOptimizationSettings updated = new()
        {
            UnlockHiddenGameFgOption = true,
            UseLegacyRtx30Pacing = true
        };
        OptimizationService.Save(gameDir, updated);

        GameOptimizationSettings reloaded = OptimizationService.Load(gameDir);
        AssertTrue(reloaded.UnlockHiddenGameFgOption, "GpuNameOverride=1 should reload as true");
        AssertTrue(reloaded.UseLegacyRtx30Pacing, "Rtx30FlipMetering=0 should reload as legacy pacing true");

        string rawText = File.ReadAllText(iniPath);
        AssertTrue(rawText.Contains("ShowOverlay=1", StringComparison.Ordinal), "Unrelated [Menu] section must be preserved");
        AssertTrue(rawText.Contains("OtherKey=keep", StringComparison.Ordinal), "Unrelated [Compatibility] key must be preserved");
        AssertTrue(rawText.Contains("GpuNameOverride=1", StringComparison.Ordinal), "GpuNameOverride=1 must be written");
        AssertTrue(rawText.Contains("Rtx30FlipMetering=0", StringComparison.Ordinal), "Rtx30FlipMetering=0 must be written");
    }
}
