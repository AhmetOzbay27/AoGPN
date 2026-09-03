namespace ServiceLib.Common;

/// <summary>
/// Curated catalog of well-known apps with a recommended default connection option.
/// Games (and game platforms) ignore the system proxy, so they are suggested to use
/// the VPN (TUN) option; every other app keeps the default proxy option.
/// </summary>
public static class KnownAppCatalog
{
    // Exact process names (case-insensitive) -> suggested action.
    // Short or ambiguous names live here; the keyword list below only carries
    // distinctive phrases so it does not false-positive on unrelated apps.
    private static readonly Dictionary<string, string> KnownProcessActions = new(StringComparer.OrdinalIgnoreCase)
    {
        // --- Games (their sockets bypass the system proxy → VPN/TUN) ---
        ["EscapeFromTarkov.exe"] = "vpn",
        ["EscapeFromTarkov_BE.exe"] = "vpn", // BattlEye client of the game
        // Battlestate Games launcher (login/auth): auth trafiği TEMİZ egress (WARP)
        // ister; Oracle/VPN çıkışı profile.tarkov.com'un Cloudflare WAF'ı tarafından
        // engellenir.
        ["BsGLauncher.exe"] = "warp",
        ["EFT.exe"] = "vpn",
        ["Valorant.exe"] = "vpn",
        ["VALORANT-Win64-Shipping.exe"] = "vpn",
        ["RiotClientServices.exe"] = "vpn",
        ["csgo.exe"] = "vpn",
        ["cs2.exe"] = "vpn",
        ["FortniteClient-Win64-Shipping.exe"] = "vpn",
        ["FortniteLauncher.exe"] = "vpn",
        ["r5apex.exe"] = "vpn",
        ["TslGame.exe"] = "vpn",
        ["cod.exe"] = "vpn",
        ["ModernWarfare.exe"] = "vpn",
        ["LeagueClient.exe"] = "vpn",
        ["League of Legends.exe"] = "vpn",
        ["dota2.exe"] = "vpn",
        ["GTA5.exe"] = "vpn",
        ["RDR2.exe"] = "vpn",
        ["RocketLeague.exe"] = "vpn",
        ["Overwatch.exe"] = "vpn",
        ["RainbowSix.exe"] = "vpn",
        ["destiny2.exe"] = "vpn",
        ["HaloInfinite.exe"] = "vpn",
        ["eldenring.exe"] = "vpn",
        ["Cyberpunk2077.exe"] = "vpn",
        ["StarCitizen.exe"] = "vpn",
        ["Wow.exe"] = "vpn",
        ["WowClassic.exe"] = "vpn",
        ["DiabloIV.exe"] = "vpn",
        ["Minecraft.exe"] = "vpn",
        ["RustClient.exe"] = "vpn",
        ["Valheim.exe"] = "vpn",
        ["GenshinImpact.exe"] = "vpn",
        ["StarRail.exe"] = "vpn",
        ["DayZ_x64.exe"] = "vpn",
        ["ARKAscended.exe"] = "vpn",
        ["Palworld.exe"] = "vpn",
        ["AmongUs.exe"] = "vpn",
        ["FallGuys_client.exe"] = "vpn",
        ["PathOfExile.exe"] = "vpn",
        ["LostArk.exe"] = "vpn",
        ["GuildWars2-64.exe"] = "vpn",
        ["BlackDesert64.exe"] = "vpn",
        ["TslGame_BE.exe"] = "vpn",

        // --- Game platforms / launchers (games launched through them ignore proxy) ---
        ["steam.exe"] = "vpn",
        ["SteamService.exe"] = "vpn",
        ["EpicGamesLauncher.exe"] = "vpn",
        ["Battle.net.exe"] = "vpn",
        ["Origin.exe"] = "vpn",
        ["upc.exe"] = "vpn", // Ubisoft Connect
        ["UbisoftConnect.exe"] = "vpn",
        ["XboxApp.exe"] = "vpn",
        ["GOG Galaxy.exe"] = "vpn",
        ["GameBar.exe"] = "vpn",
        ["GameBarPresenceWriter.exe"] = "vpn",
        ["GeForceExperience.exe"] = "vpn",
        ["NVIDIA App.exe"] = "vpn",
        ["NVIDIA GeForce Experience.exe"] = "vpn",
    };

    // Distinctive phrases (case-insensitive) matched against the process + display name.
    // Only long/unique phrases — short words like "rust" or "origin" are handled by
    // the exact-process list to avoid false positives (e.g. RustDesk, other tools).
    private static readonly (string Fragment, string Action)[] KeywordActions =
    {
        ("minecraft", "vpn"),
        ("league of legends", "vpn"),
        ("counter-strike", "vpn"),
        ("call of duty", "vpn"),
        ("apex legends", "vpn"),
        ("escape from tarkov", "vpn"),
        ("tarkov", "vpn"),
        ("warzone", "vpn"),
        ("fortnite", "vpn"),
        ("pubg", "vpn"),
        ("battle royale", "vpn"),
        ("world of warcraft", "vpn"),
        ("warcraft", "vpn"),
        ("rocket league", "vpn"),
        ("overwatch", "vpn"),
        ("rainbow six", "vpn"),
        ("destiny 2", "vpn"),
        ("halo infinite", "vpn"),
        ("elden ring", "vpn"),
        ("cyberpunk 2077", "vpn"),
        ("star citizen", "vpn"),
        ("genshin impact", "vpn"),
        ("honkai", "vpn"),
        ("star rail", "vpn"),
        ("wuthering waves", "vpn"),
        ("valheim", "vpn"),
        ("rust client", "vpn"),
        ("dayz", "vpn"),
        ("7 days to die", "vpn"),
        ("fall guys", "vpn"),
        ("among us", "vpn"),
        ("stardew valley", "vpn"),
        ("terraria", "vpn"),
        ("factorio", "vpn"),
        ("borderlands", "vpn"),
        ("the witcher", "vpn"),
        ("skyrim", "vpn"),
        ("fallout", "vpn"),
        ("far cry", "vpn"),
        ("assassin's creed", "vpn"),
        ("fifa", "vpn"),
        ("ea sports fc", "vpn"),
        ("forza", "vpn"),
        ("need for speed", "vpn"),
        ("grand theft auto", "vpn"),
        ("red dead redemption", "vpn"),
        ("diablo", "vpn"),
        ("hearthstone", "vpn"),
        ("eve online", "vpn"),
        ("world of tanks", "vpn"),
        ("world of warships", "vpn"),
        ("dead by daylight", "vpn"),
        ("sea of thieves", "vpn"),
        ("palworld", "vpn"),
        ("monster hunter", "vpn"),
        ("final fantasy", "vpn"),
        ("kingdom come", "vpn"),
        ("ghost of tsushima", "vpn"),
        ("god of war", "vpn"),
        ("horizon zero dawn", "vpn"),
        ("the last of us", "vpn"),
        ("mortal kombat", "vpn"),
        ("tekken", "vpn"),
        ("street fighter", "vpn"),
        ("path of exile", "vpn"),
        ("lost ark", "vpn"),
        ("new world", "vpn"),
        ("throne and liberty", "vpn"),
        ("guild wars", "vpn"),
        ("black desert", "vpn"),
        ("the sims", "vpn"),
        ("nba 2k", "vpn"),
        ("madden", "vpn"),
        ("gran turismo", "vpn"),
        ("mario", "vpn"),
        ("zelda", "vpn"),
        ("pokemon", "vpn"),
        ("splatoon", "vpn"),
        ("smash bros", "vpn"),
        ("emulator", "vpn"),
        ("retroarch", "vpn"),
        ("dolphin emulator", "vpn"),
        ("ryujinx", "vpn"),
        ("rpcs3", "vpn"),
        ("pcsx", "vpn"),
        ("cemu", "vpn"),
        ("mame", "vpn"),
        ("steam", "vpn"),
        ("epic games", "vpn"),
        ("battle.net", "vpn"),
        ("blizzard", "vpn"),
        ("ubisoft connect", "vpn"),
        ("uplay", "vpn"),
        ("gog galaxy", "vpn"),
        ("xbox game", "vpn"),
        ("game pass", "vpn"),
        ("game bar", "vpn"),
        ("geforce experience", "vpn"),
        ("nvidia app", "vpn"),
        ("game launcher", "vpn"),
        ("game client", "vpn"),
    };

    /// <summary>
    /// Recommends the default connection option for an app: "vpn" for known games and
    /// game platforms (they bypass the system proxy), otherwise "proxy".
    /// </summary>
    public static string SuggestAction(string processName, string displayName)
    {
        if (processName.IsNotEmpty() && KnownProcessActions.TryGetValue(processName, out var action))
        {
            return action;
        }

        var haystack = $"{processName} {displayName}";
        foreach (var (fragment, suggested) in KeywordActions)
        {
            if (haystack.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return suggested;
            }
        }

        return "proxy";
    }
}
