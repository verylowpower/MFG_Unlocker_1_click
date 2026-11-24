using System.IO;
using System.Text;
using MfgUnlockerInstaller.Models;

namespace MfgUnlockerInstaller.Data;

/// <summary>
/// Built-in compatibility database derived from dashdogy/MFG-Unlocker docs/compatibility.md.
/// </summary>
public static class CompatibilityDatabase
{
    public static IReadOnlyList<GameCompatibilityEntry> Entries { get; } = BuildEntries();

    public static IReadOnlyList<string> AntiCheatGameNames { get; } =
    [
        "Battlefield 6",
        "RedSec",
        "Forza Horizon 6",
        "Gears of War: E-Day",
        "Grand Theft Auto V Enhanced",
        "GTA V Enhanced",
        "Zenless Zone Zero"
    ];

    public static GameCompatibilityEntry? Match(string gameName, string? exePath = null)
    {
        string normalizedGame = NormalizeName(gameName);
        string? exeFileName = string.IsNullOrWhiteSpace(exePath) ? null : Path.GetFileName(exePath);

        if (!string.IsNullOrEmpty(normalizedGame))
        {
            // 1. Exact normalized match on primary name or aliases
            foreach (GameCompatibilityEntry entry in Entries)
            {
                if (NormalizeName(entry.Name) == normalizedGame)
                {
                    return entry;
                }

                foreach (string alias in entry.Aliases)
                {
                    if (NormalizeName(alias) == normalizedGame)
                    {
                        return entry;
                    }
                }
            }

            // 2. Prefix word-boundary match (longest normalized match wins)
            GameCompatibilityEntry? bestMatch = null;
            int bestLength = 0;

            foreach (GameCompatibilityEntry entry in Entries)
            {
                foreach (string candidate in GetAllNames(entry))
                {
                    string normCandidate = NormalizeName(candidate);
                    if (normCandidate.Length < 4)
                    {
                        continue;
                    }

                    if (normalizedGame.StartsWith(normCandidate + " ", StringComparison.Ordinal)
                        && normCandidate.Length > bestLength)
                    {
                        bestMatch = entry;
                        bestLength = normCandidate.Length;
                    }
                }
            }

            if (bestMatch is not null)
            {
                return bestMatch;
            }
        }

        // 3. Fallback: match by exact executable filename hint
        if (!string.IsNullOrWhiteSpace(exeFileName))
        {
            foreach (GameCompatibilityEntry entry in Entries)
            {
                foreach (string hint in entry.ExeHints)
                {
                    string hintFile = Path.GetFileName(hint);
                    if (string.Equals(hintFile, exeFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry;
                    }
                }
            }
        }

        return null;
    }

    public static bool IsAntiCheatGame(string gameName)
    {
        string normalized = NormalizeName(gameName);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        foreach (string acName in AntiCheatGameNames)
        {
            string normAc = NormalizeName(acName);
            if (normalized == normAc
                || normalized.StartsWith(normAc + " ", StringComparison.Ordinal)
                || normalized.Contains(" " + normAc + " ", StringComparison.Ordinal)
                || normalized.EndsWith(" " + normAc, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static string NormalizeName(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        StringBuilder sb = new(input.Length);
        bool lastWasSpace = true;

        foreach (char ch in input)
        {
            if (ch is '™' or '®' or '©')
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private static IEnumerable<string> GetAllNames(GameCompatibilityEntry entry)
    {
        yield return entry.Name;
        foreach (string alias in entry.Aliases)
        {
            yield return alias;
        }
    }

    private static IReadOnlyDictionary<string, string> Avoid(params (string Dll, string Reason)[] pairs)
    {
        Dictionary<string, string> dict = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string dll, string reason) in pairs)
        {
            dict[dll] = reason;
        }

        return dict;
    }

    private static List<GameCompatibilityEntry> BuildEntries() =>
    [
        new GameCompatibilityEntry
        {
            Name = "Ace Combat 8",
            PreferredDll = "version.dll"
        },
        new GameCompatibilityEntry
        {
            Name = "AION 2",
            PreferredDll = "d3d12.dll",
            AlternativeDlls = ["dxgi.dll"]
        },
        new GameCompatibilityEntry
        {
            Name = "Alan Wake 2",
            PreferredDll = "winmm.dll",
            AlternativeDlls = ["dinput8.dll"],
            AvoidDlls = Avoid(("version.dll", "Not loaded on Epic")),
            ExeHints = ["AlanWake2.exe"]
        },
        new GameCompatibilityEntry
        {
            Name = "Assassin's Creed IV Black Flag Resynced",
            Aliases = ["Assassins Creed IV Black Flag Resynced"],
            PreferredDll = "version.dll",
            AlternativeDlls = ["dinput8.dll"]
        },
        new GameCompatibilityEntry
        {
            Name = "Black Myth: Wukong",
            Aliases = ["Black Myth Wukong"],
            PreferredDll = "version.dll",
            AlternativeDlls = ["dsound.dll", "dxgi.dll"],
            ExeHints = [@"b1\Binaries\Win64\b1-Win64-Shipping.exe"]
        },
        new GameCompatibilityEntry
        {
            Name = "Call of Duty: Modern Warfare 4",
            Aliases = ["Call of Duty Modern Warfare 4"],
            PreferredDll = "dxgi.dll"
        },
        new GameCompatibilityEntry
        {
            Name = "Control Resonant",
            PreferredDll = "version.dll",
            AlternativeDlls = ["winmm.dll", "bink2w64.dll"],
            Notes = "bink2w64.dll uses the Bink hook route (automatically renames the original to bink2w64Hooked.dll)."
        },
        new GameCompatibilityEntry
        {
            Name = "Crimson Desert",
            PreferredDll = "d3d12.dll",
            AvoidDlls = Avoid(
                ("version.dll", "Not loaded by the game"),
                ("dinput8.dll", "Not loaded by the game"),
                ("winmm.dll", "Game's mod loader"))
        },
        new GameCompatibilityEntry
        {
            Name = "Cyberpunk 2077",
            PreferredDll = "xinput9_1_0.dll",
            AlternativeDlls = ["dxgi.dll", "d3d12.dll"],
            AvoidDlls = Avoid(
                ("version.dll", "Cyber Engine Tweaks"),
                ("winmm.dll", "RED4ext"),
                ("dinput8.dll", "Not loaded by the game")),
            ExeHints = [@"bin\x64\Cyberpunk2077.exe"],
            Notes = "Place in bin\\x64 beside Cyberpunk2077.exe."
        },
        new GameCompatibilityEntry
        {
            Name = "DOOM: The Dark Ages",
            Aliases = ["DOOM The Dark Ages"],
            PreferredDll = "winmm.dll",
            Notes = "Vulkan title."
        },
        new GameCompatibilityEntry
        {
            Name = "Dragon's Dogma 2",
            Aliases = ["Dragons Dogma 2"],
            PreferredDll = "winmm.dll",
            AvoidDlls = Avoid(("dinput8.dll", "REFramework")),
            ExeHints = ["DD2.exe"]
        },
        new GameCompatibilityEntry
        {
            Name = "Dying Light 2",
            Aliases = ["Dying Light 2 Stay Human"],
            PreferredDll = "winmm.dll",
            AlternativeDlls = ["version.dll"],
            ExeHints = [@"ph\work\bin\x64\DyingLightGame_x64_rwdi.exe"]
        },
        new GameCompatibilityEntry
        {
            Name = "Dying Light: The Beast",
            Aliases = ["Dying Light The Beast"],
            PreferredDll = "winmm.dll"
        },
        new GameCompatibilityEntry
        {
            Name = "Enshrouded",
            PreferredDll = "winmm.dll",
            Notes = "Vulkan title."
        },
        new GameCompatibilityEntry
        {
            Name = "Farming Simulator 25",
            PreferredDll = "dinput8.dll",
            AlternativeDlls = ["version.dll"],
            ExeHints = [@"x64\FarmingSimulator2025Game.exe"]
        },
        new GameCompatibilityEntry
        {
            Name = "Final Fantasy VII Rebirth",
            Aliases = ["FINAL FANTASY VII REBIRTH"],
            PreferredDll = "version.dll",
            AlternativeDlls = ["dxgi.dll"],
            AvoidDlls = Avoid(("dsound.dll", "FF7RebirthFix")),
            ExeHints = [@"End\Binaries\Win64\ff7rebirth_.exe"]
        },
        new GameCompatibilityEntry
        {
            Name = "Gothic 1 Remake",
            PreferredDll = "version.dll",
            AlternativeDlls = ["winmm.dll"]
        },
        new GameCompatibilityEntry
        {
            Name = "Hogwarts Legacy",
            PreferredDll = "version.dll",
            ExeHints = [@"Phoenix\Binaries\Win64\HogwartsLegacy.exe"]
        },
        new GameCompatibilityEntry
        {
            Name = "Indiana Jones and the Great Circle",
            PreferredDll = "dinput8.dll",
            ExeHints = ["TheGreatCircle.exe"],
            Notes = "Vulkan title."
        },
        new GameCompatibilityEntry
        {
            Name = "Marvel's Spider-Man 2",
            Aliases = ["Marvels Spider-Man 2", "Marvels Spider Man 2"],
            PreferredDll = "version.dll",
            ExeHints = ["Spider-Man2.exe"]
        },
        new GameCompatibilityEntry
        {
            Name = "Microsoft Flight Simulator 2020",
            Aliases = ["Microsoft Flight Simulator"],
            PreferredDll = "version.dll",
            AlternativeDlls = ["dinput8.dll"],
            ExeHints = ["FlightSimulator.exe"],
            Notes = "Steam uses version.dll; Microsoft Store/Xbox uses dinput8.dll."
        },
        new GameCompatibilityEntry
        {
            Name = "Microsoft Flight Simulator 2024",
            PreferredDll = "version.dll",
            AlternativeDlls = ["dinput8.dll", "d3d12.dll"],
            ExeHints = ["FlightSimulator2024.exe"]
        },
        new GameCompatibilityEntry
        {
            Name = "Monster Hunter Wilds",
            PreferredDll = "version.dll",
            AlternativeDlls = ["winmm.dll"],
            AvoidDlls = Avoid(("dinput8.dll", "REFramework")),
            ExeHints = ["MonsterHunterWilds.exe"]
        },
        new GameCompatibilityEntry
        {
            Name = "Onimusha: Way of the Sword",
            Aliases = ["Onimusha Way of the Sword"],
            PreferredDll = "winmm.dll",
            AvoidDlls = Avoid(("version.dll", "Causes crash"))
        },
        new GameCompatibilityEntry
        {
            Name = "Phantom Blade Zero",
            PreferredDll = "version.dll",
            AlternativeDlls = ["dxgi.dll"],
            AvoidDlls = Avoid(("winmm.dll", "Game ships its own winmm.dll"))
        },
        new GameCompatibilityEntry
        {
            Name = "Planet Zoo 2",
            PreferredDll = "winmm.dll",
            AvoidDlls = Avoid(("dxgi.dll", "Breaks HDR"))
        },
        new GameCompatibilityEntry
        {
            Name = "Pragmata",
            PreferredDll = "version.dll"
        },
        new GameCompatibilityEntry
        {
            Name = "Resident Evil Requiem",
            PreferredDll = "version.dll",
            AlternativeDlls = ["dxgi.dll"],
            AvoidDlls = Avoid(("dinput8.dll", "REFramework"))
        },
        new GameCompatibilityEntry
        {
            Name = "S.T.A.L.K.E.R. 2",
            Aliases = ["STALKER 2", "S.T.A.L.K.E.R. 2: Heart of Chornobyl", "STALKER 2 Heart of Chornobyl"],
            PreferredDll = "version.dll",
            AlternativeDlls = ["d3d12.dll"],
            AvoidDlls = Avoid(("dxgi.dll", "Not loaded by the game")),
            ExeHints = [@"Stalker2\Binaries\Win64\Stalker2-Win64-Shipping.exe"]
        },
        new GameCompatibilityEntry
        {
            Name = "Silent Hill: Townfall",
            Aliases = ["Silent Hill Townfall"],
            PreferredDll = "version.dll"
        },
        new GameCompatibilityEntry
        {
            Name = "Star Wars: Galactic Racer",
            Aliases = ["Star Wars Galactic Racer"],
            PreferredDll = "version.dll",
            AlternativeDlls = ["dxgi.dll"]
        },
        new GameCompatibilityEntry
        {
            Name = "Subnautica 2",
            PreferredDll = "version.dll"
        },
        new GameCompatibilityEntry
        {
            Name = "The Blood of Dawnwalker",
            PreferredDll = "version.dll"
        },
        new GameCompatibilityEntry
        {
            Name = "The First Descendant",
            PreferredDll = "dxgi.dll",
            ExeHints = [@"M1\Binaries\Win64\M1-Win64-Shipping.exe"]
        },
        new GameCompatibilityEntry
        {
            Name = "The Last of Us Part II",
            Aliases = ["The Last of Us Part II Remastered"],
            PreferredDll = "dinput8.dll",
            ExeHints = ["tlou-ii.exe"]
        },
        new GameCompatibilityEntry
        {
            Name = "The Witcher 3",
            Aliases = ["The Witcher 3: Wild Hunt", "The Witcher 3 Wild Hunt"],
            PreferredDll = "winmm.dll",
            AlternativeDlls = ["version.dll", "d3d11.dll"],
            AvoidDlls = Avoid(("dxgi.dll", "ReShade")),
            ExeHints = [@"bin\x64_dx12\witcher3.exe"],
            Notes = "Use the DX12 executable in bin\\x64_dx12."
        },
        new GameCompatibilityEntry
        {
            Name = "Where Winds Meet",
            PreferredDll = "winmm.dll"
        },
        new GameCompatibilityEntry
        {
            Name = "Wuthering Waves",
            PreferredDll = "version.dll",
            AlternativeDlls = ["dxgi.dll"],
            AvoidDlls = Avoid(
                ("dinput8.dll", "Menu does not appear"),
                ("winmm.dll", "Menu does not appear"),
                ("d3d12.dll", "Menu does not appear")),
            ExeHints = [@"Client\Binaries\Win64\Client-Win64-Shipping.exe"]
        }
    ];
}
