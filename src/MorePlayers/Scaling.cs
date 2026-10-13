// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Mikael Järvinen
// More Players for Arizona Sunshine Remake - https://github.com/mikael-jarvinen/azs-remake-more-players
// Licensed under GPL-3.0 with the additional terms in ADDITIONAL-TERMS.md (attribution, permission to run with the game).

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Vertigo.AZS2;
using Vertigo.AZS2.Server;
using Vertigo.AZS2.Server.Horde;

namespace AZS1HD.MorePlayers;

/// <summary>
/// Difficulty scaling by player count (host only, since all spawning runs on the server side of the host).
///
/// The game never scales zombies with the player count: scripted spawn events carry fixed counts, and the
/// alive-zombie caps come from the zombie settings table. Only the dynamic ammo spawner looks the player count
/// up in a table (rows for 1 and 2 players; with more players the lookup fails and falls back to 1.0).
///
/// factor(n) = multiplier ^ (n - BaselinePlayers) for n above the baseline, else 1.
/// </summary>
internal static class Scaling
{
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> ZombieMultiplierPerPlayer;
    internal static ConfigEntry<float> AmmoMultiplierPerPlayer;
    internal static ConfigEntry<int> BaselinePlayers;
    internal static ConfigEntry<float> MaxZombieFactor;
    internal static ConfigEntry<bool> ScaleAliveCaps;
    internal static ConfigEntry<bool> ScaleHorde;

    internal static void Bind(ConfigFile config)
    {
        Enabled = config.Bind("Scaling", "Enabled", false,
            "Scale zombie counts and ammo with the number of players (host decides, applies to the whole session). Off by default; turn on if your group finds co-op too easy.");
        ZombieMultiplierPerPlayer = config.Bind("Scaling", "ZombieCountMultiplierPerPlayer", 1.25f,
            new ConfigDescription("Zombie count multiplier applied once per player above the baseline. 1.25 with 4 players = 1.25^3 = 1.95x zombies.",
                new AcceptableValueRange<float>(1f, 3f)));
        AmmoMultiplierPerPlayer = config.Bind("Scaling", "AmmoMultiplierPerPlayer", 1.5f,
            new ConfigDescription("Dynamic ammo spawn multiplier applied once per player above the baseline, on top of the game's own 1/2-player table.",
                new AcceptableValueRange<float>(1f, 4f)));
        BaselinePlayers = config.Bind("Scaling", "BaselinePlayers", 1,
            new ConfigDescription("Player count that gets the unmodified game. Every player above it applies the multipliers once.",
                new AcceptableValueRange<int>(1, 4)));
        MaxZombieFactor = config.Bind("Scaling", "MaxZombieFactor", 3f,
            new ConfigDescription("Hard ceiling for the total zombie factor (counts and alive caps), to protect the host's frame rate.",
                new AcceptableValueRange<float>(1f, 6f)));
        ScaleAliveCaps = config.Bind("Scaling", "ScaleAliveZombieCaps", true,
            "Also scale the 'max alive at once' caps (per spawn event and global) so the extra zombies actually show up together instead of trickling in.");
        ScaleHorde = config.Bind("Scaling", "ScaleHorde", false,
            "Also scale Horde mode (wave sizes, caps, ammo). Horde is already tuned for 4 players, so this is off by default.");
    }

    // ---- player count / level type, cached for half a second -------------------------------------------------

    private static long cacheStamp;
    private static int cachedPlayers = 1;
    private static bool cachedIsHorde;
    private static NetworkGameSystem cachedPlayerSystem;
    private static IntPtr cachedServerGame;

    private static void Refresh()
    {
        long now = Environment.TickCount64;
        if (now - cacheStamp < 500) return;
        cacheStamp = now;
        try
        {
            int players = 1;
            if (ServerGame.IsActive)
            {
                var game = ServerGame.Instance;
                if (game != null)
                {
                    if (cachedPlayerSystem == null || cachedServerGame != game.Pointer)
                    {
                        cachedPlayerSystem = game.GetSystem<NetworkGameSystem>();
                        cachedServerGame = game.Pointer;
                    }
                    if (cachedPlayerSystem != null) players = Math.Max(1, cachedPlayerSystem.ConnectedNonAiPlayerCount);
                }
            }
            cachedPlayers = players;
            cachedIsHorde = LevelSettingsTO.TryGetCurrentLevelSettings(out var settings) && settings != null && settings.levelType == ELevelType.Horde;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Scaling: could not read player count / level type: {e.Message}");
        }
    }

    internal static int Players { get { Refresh(); return cachedPlayers; } }
    internal static bool IsHorde { get { Refresh(); return cachedIsHorde; } }

    private static bool Active => Enabled.Value && (!IsHorde || ScaleHorde.Value);

    internal static double FactorFor(int players, float perPlayer, float max)
    {
        int extra = players - BaselinePlayers.Value;
        if (extra <= 0 || perPlayer <= 1f) return 1.0;
        return Math.Min(max, Math.Pow(perPlayer, extra));
    }

    /// <summary>Current zombie factor for the host's session (1 when scaling does not apply).</summary>
    internal static double ZombieFactor() => Active ? FactorFor(Players, ZombieMultiplierPerPlayer.Value, MaxZombieFactor.Value) : 1.0;

    internal static double AmmoFactor(int players) => Active ? FactorFor(players, AmmoMultiplierPerPlayer.Value, 100f) : 1.0;

    internal static int ScaleCount(int count, double factor)
    {
        if (count <= 0 || factor <= 1.0) return count;
        return Math.Max(count, (int)Math.Round(count * factor, MidpointRounding.AwayFromZero));
    }

    // ---- cap tracking ----------------------------------------------------------------------------------------------
    // The game writes its caps through trivial setters that IL2CPP folded together with unrelated setters, so they
    // cannot be hooked. Instead the caps are re-applied every server update: remember the game's own value (base)
    // and what we last wrote; if the current value is not what we wrote, the game changed it and that is the new base.

    private struct Tracked { public int Base; public int LastWritten; }
    private static readonly Dictionary<(IntPtr, int), Tracked> tracked = new();

    internal static int TrackCap(IntPtr owner, int slot, int current, double factor)
    {
        if (tracked.Count > 8192) tracked.Clear();
        var key = (owner, slot);
        if (!tracked.TryGetValue(key, out var t) || t.LastWritten != current)
            t = new Tracked { Base = current, LastWritten = current };
        int desired = ScaleCount(t.Base, factor);
        t.LastWritten = desired;
        tracked[key] = t;
        return desired;
    }
}

/// <summary>Scripted spawn events: scale numberOfZombies of each group while the command builds its queue.</summary>
[HarmonyPatch]
internal static class SpawnCountPatches
{
    private static int lastLoggedPlayers = -1;

    [HarmonyPatch(typeof(SpawnZombieCommand), "CreateZombieSpawnQueue")]
    [HarmonyPrefix]
    private static void CreateZombieSpawnQueue_Prefix(SpawnZombieCommand __instance, out List<(ZombieSpawnGroup, int)> __state)
    {
        __state = null;
        try
        {
            double f = Scaling.ZombieFactor();
            if (f <= 1.0) return;
            var spawns = __instance.Spawns;
            var groups = spawns?.Groups;
            if (groups == null) return;
            var saved = new List<(ZombieSpawnGroup, int)>();
            int before = 0, after = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                if (g == null) continue;
                int orig = g.numberOfZombies;
                int scaled = Scaling.ScaleCount(orig, f);
                before += orig; after += scaled;
                if (scaled != orig)
                {
                    g.numberOfZombies = scaled;
                    saved.Add((g, orig));
                }
            }
            __state = saved;
            int players = Scaling.Players;
            if (Plugin.Verbose.Value || players != lastLoggedPlayers)
            {
                lastLoggedPlayers = players;
                Plugin.Logger.LogInfo($"Spawn event: {before} -> {after} zombies ({players} players, factor {f:0.00})");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Spawn count scaling failed: {e}");
        }
    }

    [HarmonyPatch(typeof(SpawnZombieCommand), "CreateZombieSpawnQueue")]
    [HarmonyPostfix]
    private static void CreateZombieSpawnQueue_Postfix(List<(ZombieSpawnGroup, int)> __state)
    {
        if (__state == null) return;
        foreach (var (g, orig) in __state)
        {
            try { g.numberOfZombies = orig; } catch { }
        }
    }
}

/// <summary>Alive caps: per spawn queue / spawner (how many of one event may be alive) and the global enemy manager caps.</summary>
[HarmonyPatch]
internal static class CapPatches
{
    [HarmonyPatch(typeof(ZombieSpawner), "OnUpdate")]
    [HarmonyPostfix]
    private static void ZombieSpawner_OnUpdate_Postfix(ZombieSpawner __instance)
    {
        if (!Scaling.ScaleAliveCaps.Value) return;
        try
        {
            double f = Scaling.ZombieFactor();
            var p = __instance.Pointer;
            int v;
            v = Scaling.TrackCap(p, 0, __instance.MaxSimultaneousZombies, f);       if (v != __instance.MaxSimultaneousZombies) __instance.MaxSimultaneousZombies = v;
            v = Scaling.TrackCap(p, 1, __instance.MaxSimultaneousSlowZombies, f);   if (v != __instance.MaxSimultaneousSlowZombies) __instance.MaxSimultaneousSlowZombies = v;
            v = Scaling.TrackCap(p, 2, __instance.MaxSimultaneousMediumZombies, f); if (v != __instance.MaxSimultaneousMediumZombies) __instance.MaxSimultaneousMediumZombies = v;
            v = Scaling.TrackCap(p, 3, __instance.MaxSimultaneousFastZombies, f);   if (v != __instance.MaxSimultaneousFastZombies) __instance.MaxSimultaneousFastZombies = v;

            var queues = __instance.queues; // the private List<ZombieSpawnQueue> behind the Queues property
            if (queues == null) return;
            int n = queues.Count;
            for (int i = 0; i < n; i++)
            {
                var q = queues[i];
                if (q == null) continue;
                var qp = q.Pointer;
                v = Scaling.TrackCap(qp, 0, q.MaxSimultaneousZombies, f);       if (v != q.MaxSimultaneousZombies) q.MaxSimultaneousZombies = v;
                v = Scaling.TrackCap(qp, 1, q.MaxSimultaneousSlowZombies, f);   if (v != q.MaxSimultaneousSlowZombies) q.MaxSimultaneousSlowZombies = v;
                v = Scaling.TrackCap(qp, 2, q.MaxSimultaneousMediumZombies, f); if (v != q.MaxSimultaneousMediumZombies) q.MaxSimultaneousMediumZombies = v;
                v = Scaling.TrackCap(qp, 3, q.MaxSimultaneousFastZombies, f);   if (v != q.MaxSimultaneousFastZombies) q.MaxSimultaneousFastZombies = v;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Spawn queue cap scaling failed: {e}");
        }
    }

    private static int lastLoggedNormal = -1;

    [HarmonyPatch(typeof(ServerEnemyManager), "OnUpdate")]
    [HarmonyPostfix]
    private static void ServerEnemyManager_OnUpdate_Postfix(ServerEnemyManager __instance)
    {
        if (!Scaling.ScaleAliveCaps.Value) return;
        try
        {
            double f = Scaling.ZombieFactor();
            var p = __instance.Pointer;
            int normal = Scaling.TrackCap(p, 10, __instance.maxNormalEnemies, f);
            int special = Scaling.TrackCap(p, 11, __instance.maxSpecialEnemies, f);
            int background = Scaling.TrackCap(p, 12, __instance.maxBackgroundEnemies, f);
            if (normal != __instance.maxNormalEnemies) __instance.maxNormalEnemies = normal;
            if (special != __instance.maxSpecialEnemies) __instance.maxSpecialEnemies = special;
            if (background != __instance.maxBackgroundEnemies) __instance.maxBackgroundEnemies = background;
            if (normal != lastLoggedNormal && Plugin.Verbose.Value)
            {
                lastLoggedNormal = normal;
                Plugin.Logger.LogInfo($"Global alive zombie caps now {normal}/{special}/{background} (normal/special/background, factor {f:0.00})");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Global cap scaling failed: {e}");
        }
    }
}

/// <summary>Dynamic ammo: the game's table has rows for 1 and 2 players only; extend it and apply the per-player multiplier.</summary>
[HarmonyPatch]
internal static class AmmoPatches
{
    private static bool reentry;
    private static int lastLoggedPlayers = -1;

    [HarmonyPatch(typeof(DifficultySettingsTO), nameof(DifficultySettingsTO.TryGetDynamicAmmoSpawnMultiplierForDifficulty))]
    [HarmonyPostfix]
    private static void AmmoMultiplier_Postfix(DifficultySettingsTO __instance, EGameplayDifficulty difficulty, int amountOfPlayers, ref float multiplier, ref bool __result)
    {
        if (reentry) return;
        try
        {
            double f = Scaling.AmmoFactor(amountOfPlayers);
            float baseValue = multiplier;
            bool baseFound = __result;
            if (!baseFound)
            {
                // no table row for this many players: use the row for the largest player count that exists
                reentry = true;
                try
                {
                    for (int p = amountOfPlayers - 1; p >= 1 && !baseFound; p--)
                    {
                        if (__instance.TryGetDynamicAmmoSpawnMultiplierForDifficulty(difficulty, p, out float b)) { baseValue = b; baseFound = true; }
                    }
                }
                finally { reentry = false; }
                if (!baseFound) return;
            }
            if (f <= 1.0 && __result) return;
            multiplier = (float)(baseValue * f);
            __result = true;
            if (Plugin.Verbose.Value || amountOfPlayers != lastLoggedPlayers)
            {
                lastLoggedPlayers = amountOfPlayers;
                Plugin.Logger.LogInfo($"Dynamic ammo multiplier for {amountOfPlayers} players ({difficulty}): {multiplier:0.00} (game table {baseValue:0.00}, factor {f:0.00})");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Ammo scaling failed: {e}");
        }
    }
}

/// <summary>Horde wave sizes (opt-in): scale the start count and per-round increase pattern of the level's horde settings.</summary>
[HarmonyPatch]
internal static class HordePatches
{
    private static readonly Dictionary<IntPtr, (int start, int[] pattern)> originals = new();

    [HarmonyPatch(typeof(ServerHordeModeSystem), "SetLevelSettings")]
    [HarmonyPostfix]
    private static void SetLevelSettings_Postfix(LevelSettingsTO levelSettings)
    {
        try
        {
            var h = levelSettings?.hordeModeSettings;
            if (h == null) return;
            var key = h.Pointer;
            if (!originals.TryGetValue(key, out var orig))
            {
                var pat = h.zombiesCountIncreasePattern;
                var copy = new int[pat != null ? pat.Length : 0];
                for (int i = 0; i < copy.Length; i++) copy[i] = pat[i];
                orig = (h.startZombieCount, copy);
                originals[key] = orig;
            }
            double f = Scaling.Enabled.Value && Scaling.ScaleHorde.Value
                ? Scaling.FactorFor(Scaling.Players, Scaling.ZombieMultiplierPerPlayer.Value, Scaling.MaxZombieFactor.Value)
                : 1.0;
            h.startZombieCount = Scaling.ScaleCount(orig.start, f);
            var pattern = h.zombiesCountIncreasePattern;
            if (pattern != null)
                for (int i = 0; i < pattern.Length && i < orig.pattern.Length; i++)
                    pattern[i] = Scaling.ScaleCount(orig.pattern[i], f);
            if (f > 1.0)
                Plugin.Logger.LogInfo($"Horde: start zombies {orig.start} -> {h.startZombieCount}, increase pattern scaled by {f:0.00} ({Scaling.Players} players)");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Horde scaling failed: {e}");
        }
    }
}
