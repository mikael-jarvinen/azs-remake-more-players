// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Mikael Järvinen
// More Players for Arizona Sunshine Remake - https://github.com/mikael-jarvinen/azs-remake-more-players
// Licensed under GPL-3.0 with the additional terms in ADDITIONAL-TERMS.md (attribution, permission to run with the game).

using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Steamworks;
using Vertigo;
using Vertigo.AZS2;
using Vertigo.AZS2.Client;
using Vertigo.Firebase;

namespace AZS1HD.MorePlayers;

/// <summary>
/// Raises the co-op campaign player cap of Arizona Sunshine Remake (AZS1HD, built on the Arizona Sunshine 2 code base).
///
/// Two things decide the cap:
///  1. When a lobby is HOSTED, MatchmakingSystem.TryStartMatchmaking is called with playerCountPerTeam = 2 for
///     campaign. That number becomes the party's TeamSize and the Photon room size. We raise it for private
///     campaign lobbies the local player hosts.
///  2. Every level has LevelSettingsTO.maxPlayers. The server uses it for player slots, max connections and the
///     handshake; client and server both use it for the network entity-id ranges. Instead of a fixed value we
///     set it to the size of the party the local player is currently in, so every machine in the session agrees,
///     and a player with the mod can still join a normal 2-player game (party size 2 -> level stays at 2).
/// </summary>
[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "azs1hd.moreplayers";
    public const string Name = "More Players (co-op cap)";
    public const string Version = "1.5.1";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<int> CampaignLobbySize;
    internal static ConfigEntry<bool> AllowPublicLobbies;
    internal static ConfigEntry<bool> AllowFriendsOnlyLobbies;
    internal static ConfigEntry<bool> PatchSteamLobby;
    internal static ConfigEntry<bool> Verbose;

    public override void Load()
    {
        Logger = Log;

        CampaignLobbySize = Config.Bind("General", "CampaignLobbySize", 4,
            new ConfigDescription(
                "Players allowed in campaign (and DLC campaign) lobbies that YOU host. Game default is 2. " +
                "Joiners pick the size up from the lobby automatically. The party screen is built for 4.",
                new AcceptableValueRange<int>(2, 4)));
        AllowPublicLobbies = Config.Bind("General", "AllowPublicLobbies", false,
            "Also enlarge PUBLIC campaign lobbies you host. Leave off unless everyone who might join has the mod, " +
            "because a player without the mod breaks the session.");
        AllowFriendsOnlyLobbies = Config.Bind("General", "AllowFriendsOnlyLobbies", true,
            "Also enlarge friends-only lobbies you host (not just private / invite-only ones). " +
            "Only friends with the mod should join those.");
        PatchSteamLobby = Config.Bind("General", "PatchSteamLobbyLimit", true,
            "Also raise the member limit of the Steam lobby the game creates for the session.");
        Verbose = Config.Bind("Debug", "VerboseLogging", false,
            "Log every lookup and patched value.");
        Scaling.Bind(Config);

        var harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(MatchmakingPatches));
        harmony.PatchAll(typeof(LevelSettingsPatches));
        harmony.PatchAll(typeof(EntityIdPatches));
        harmony.PatchAll(typeof(SaveSlotPatches));
        if (PatchSteamLobby.Value)
            harmony.PatchAll(typeof(SteamLobbyPatches));
        if (Scaling.Enabled.Value)
        {
            harmony.PatchAll(typeof(SpawnCountPatches));
            harmony.PatchAll(typeof(CapPatches));
            harmony.PatchAll(typeof(AmmoPatches));
            harmony.PatchAll(typeof(HordePatches));
            Log.LogInfo($"Difficulty scaling on: zombies x{Scaling.ZombieMultiplierPerPlayer.Value} and ammo x{Scaling.AmmoMultiplierPerPlayer.Value} per player above {Scaling.BaselinePlayers.Value}" +
                        $" (alive caps {(Scaling.ScaleAliveCaps.Value ? "scaled" : "unchanged")}, horde {(Scaling.ScaleHorde.Value ? "scaled" : "unchanged")}).");
        }

        Log.LogInfo($"{Name} v{Version} loaded. Campaign lobbies you host will allow {CampaignLobbySize.Value} players " +
                    $"(public lobbies: {(AllowPublicLobbies.Value ? "enlarged" : "kept at normal size")}).");
    }

    internal static bool IsCampaignMode(EMatchmakingGameMode mode) =>
        mode == EMatchmakingGameMode.Campaign || mode == EMatchmakingGameMode.DamnedDLC || mode == EMatchmakingGameMode.DeadmanDLC;

    internal static bool IsPublic(EFirebasePartyJoinability j) =>
        j == EFirebasePartyJoinability.Open || j == EFirebasePartyJoinability.PlatformGroupOnly;
}

/// <summary>Reads the party the local player is in. Shared by the level patches.</summary>
internal static class PartyInfo
{
    private static bool lookupBroken;

    /// <summary>Total player count of the current party, or 0 when not in a party (or the party is not a campaign/horde party).</summary>
    internal static int CurrentPartySize(out EMatchmakingGameMode mode, out string detail)
    {
        mode = EMatchmakingGameMode.None;
        detail = "no party";
        if (lookupBroken) { detail = "party lookup unavailable"; return -1; }
        try
        {
            if (!ClientGame.Exists) return 0;
            var game = ClientGame.Instance;
            if (game == null) return 0;
            var partySystem = game.GetSystem<PartyGameSystem>();
            if (partySystem == null || !partySystem.IsInParty) return 0;
            var party = partySystem.Party;
            if (party == null) return 0;
            mode = party.GameMode;
            int size = party.TeamSize * (party.TeamTwoEnabled ? 2 : 1);
            detail = $"party mode {mode}, team size {party.TeamSize}, two teams {party.TeamTwoEnabled}";
            return size;
        }
        catch (Exception e)
        {
            lookupBroken = true;
            Plugin.Logger.LogError($"Could not read the current party; falling back to the configured lobby size for levels. {e}");
            detail = "party lookup failed";
            return -1;
        }
    }
}

/// <summary>Host side: the player count that goes into the party and the Photon room.</summary>
[HarmonyPatch]
internal static class MatchmakingPatches
{
    // TryHostCampaign -> TryStartMatchmaking(config{GameMode, EnlistingType=Host}, joinability, playerCountPerTeam=2, ...)
    // Quick play uses EnlistingType=QuickPlay with the same count as a SEARCH criterion, so only touch hosting.
    [HarmonyPatch(typeof(MatchmakingSystem), nameof(MatchmakingSystem.TryStartMatchmaking))]
    [HarmonyPrefix]
    private static void TryStartMatchmaking_Prefix(EnlistingConfig config, EFirebasePartyJoinability joinability,
        ref int playerCountPerTeam, bool teamTwoEnabled)
    {
        try
        {
            var mode = config != null ? config.GameMode : EMatchmakingGameMode.None;
            var type = config != null ? config.EnlistingType : EMatchmakingEnlistingType.None;
            if (Plugin.Verbose.Value)
                Plugin.Logger.LogInfo($"TryStartMatchmaking: mode {mode}, type {type}, joinability {joinability}, players per team {playerCountPerTeam}, two teams {teamTwoEnabled}");

            if (type != EMatchmakingEnlistingType.Host || !Plugin.IsCampaignMode(mode)) return;
            int target = Plugin.CampaignLobbySize.Value;
            if (playerCountPerTeam >= target) return;

            if (Plugin.IsPublic(joinability) && !Plugin.AllowPublicLobbies.Value)
            {
                Plugin.Logger.LogInfo($"Public campaign lobby: keeping {playerCountPerTeam} players. Host a private lobby (or set AllowPublicLobbies = true) to play with more.");
                return;
            }
            if (joinability == EFirebasePartyJoinability.FriendsOnly && !Plugin.AllowFriendsOnlyLobbies.Value)
            {
                Plugin.Logger.LogInfo($"Friends-only campaign lobby: keeping {playerCountPerTeam} players (AllowFriendsOnlyLobbies = false).");
                return;
            }
            Plugin.Logger.LogInfo($"Hosting a {mode} lobby for {target} players (normally {playerCountPerTeam}).");
            playerCountPerTeam = target;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"TryStartMatchmaking patch failed: {e}");
        }
    }
}

/// <summary>
/// Level side: LevelSettingsTO.maxPlayers follows the size of the party the local player is in.
/// Original values are remembered so a level goes back to normal after a modded session.
/// </summary>
[HarmonyPatch]
internal static class LevelSettingsPatches
{
    private static readonly Dictionary<IntPtr, uint> originalMaxPlayers = new();
    private static readonly HashSet<IntPtr> sweptDatabases = new();
    private static string lastPartyDetail;

    internal static void Apply(LevelSettingsTO settings, string origin)
    {
        if (settings == null) return;
        try
        {
            var ptr = settings.Pointer;
            uint current = settings.maxPlayers;
            if (!originalMaxPlayers.TryGetValue(ptr, out uint original))
            {
                original = current;
                originalMaxPlayers[ptr] = original;
            }
            if (original < 2) return; // solo / menu / tutorial levels are never touched

            int partySize = PartyInfo.CurrentPartySize(out var partyMode, out var detail);
            uint desired = original;
            if (partySize < 0)
            {
                // party lookup is broken: fall back to the configured size for campaign levels
                if (settings.levelType == ELevelType.Campaign)
                    desired = Math.Max(original, (uint)Plugin.CampaignLobbySize.Value);
            }
            else if (partySize > original)
            {
                bool matches = (settings.levelType == ELevelType.Campaign && Plugin.IsCampaignMode(partyMode))
                            || (settings.levelType == ELevelType.Horde && partyMode == EMatchmakingGameMode.Horde);
                if (matches) desired = (uint)partySize;
            }

            if (Plugin.Verbose.Value && detail != lastPartyDetail)
            {
                lastPartyDetail = detail;
                Plugin.Logger.LogInfo($"Party: {detail}");
            }

            if (current == desired) return;
            settings.maxPlayers = desired;
            string name = null;
            try { name = settings.localizationKey; } catch { }
            Plugin.Logger.LogInfo($"Level '{name ?? "?"}' ({settings.levelType}): {desired} players (normally {original}) [{origin}; {detail}]");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Level settings patch failed: {e}");
        }
    }

    /// <summary>Record the original value of every LevelSettingsTO once, and apply the current party size to all of them.</summary>
    internal static void SweepDatabase(Database db, string origin)
    {
        if (db == null) return;
        var key = db.Pointer;
        if (sweptDatabases.Contains(key)) return;
        try
        {
            var table = db.GetTable(Il2CppType.Of<LevelSettingsTO>());
            if (table == null) return;
            var cells = table.Cells;
            if (cells == null) return;
            int count = table.CellCount;
            if (count == 0) return; // not loaded yet, try again on the next lookup
            for (int i = 0; i < count; i++)
            {
                var to = cells[i]?.TryCast<LevelSettingsTO>();
                if (to != null) Apply(to, origin + "/sweep");
            }
            sweptDatabases.Add(key);
            if (Plugin.Verbose.Value) Plugin.Logger.LogInfo($"Swept {count} LevelSettingsTO cells [{origin}]");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Database sweep failed (per-lookup patch still active): {e.Message}");
            sweptDatabases.Add(key);
        }
    }

    [HarmonyPatch(typeof(LevelSettingsTO), nameof(LevelSettingsTO.TryGetLevelSettings),
        new[] { typeof(Database), typeof(string), typeof(LevelSettingsTO) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out })]
    [HarmonyPostfix]
    private static void TryGetLevelSettings_Db_Postfix(Database database, string sceneName, ref LevelSettingsTO settings, bool __result)
    {
        SweepDatabase(database, "TryGetLevelSettings");
        if (__result) Apply(settings, "TryGetLevelSettings(" + sceneName + ")");
    }

    [HarmonyPatch(typeof(LevelSettingsTO), nameof(LevelSettingsTO.TryGetLevelSettings),
        new[] { typeof(string), typeof(LevelSettingsTO) },
        new[] { ArgumentType.Normal, ArgumentType.Out })]
    [HarmonyPostfix]
    private static void TryGetLevelSettings_Postfix(string sceneName, ref LevelSettingsTO settings, bool __result)
    {
        if (__result) Apply(settings, "TryGetLevelSettings(" + sceneName + ")");
    }

    [HarmonyPatch(typeof(LevelSettingsTO), nameof(LevelSettingsTO.TryGetCurrentLevelSettings))]
    [HarmonyPostfix]
    private static void TryGetCurrentLevelSettings_Postfix(ref LevelSettingsTO settings, bool __result)
    {
        if (__result) Apply(settings, "TryGetCurrentLevelSettings");
    }
}

/// <summary>
/// Safety net for the entity-id range reservation (client + server). It normally receives
/// LevelSettingsTO.maxPlayers, which is already handled above; this keeps it consistent even if a lookup was inlined.
/// </summary>
[HarmonyPatch]
internal static class EntityIdPatches
{
    [HarmonyPatch(typeof(EntitySystem), "ResetSequentialIdProviderForPlayerCount")]
    [HarmonyPrefix]
    private static void ResetSequentialIdProviderForPlayerCount_Prefix(ref uint maxPlayers, uint maxSpectators)
    {
        try
        {
            if (LevelSettingsTO.TryGetCurrentLevelSettings(out var settings) && settings != null)
            {
                LevelSettingsPatches.Apply(settings, "ResetSequentialIdProvider");
                if (settings.maxPlayers != maxPlayers)
                {
                    if (Plugin.Verbose.Value)
                        Plugin.Logger.LogInfo($"ResetSequentialIdProviderForPlayerCount: {maxPlayers} -> {settings.maxPlayers}");
                    maxPlayers = settings.maxPlayers;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"ResetSequentialIdProviderForPlayerCount patch failed: {e}");
        }
    }
}

/// <summary>Steam lobby member limit (the host creates it with SessionSettings.MaxPlayers).</summary>
[HarmonyPatch]
internal static class SteamLobbyPatches
{
    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.CreateLobby))]
    [HarmonyPrefix]
    private static void CreateLobby_Prefix(ELobbyType eLobbyType, ref int cMaxMembers)
    {
        int target = Math.Max(4, Plugin.CampaignLobbySize.Value);
        if (cMaxMembers > 0 && cMaxMembers < target)
        {
            if (Plugin.Verbose.Value)
                Plugin.Logger.LogInfo($"SteamMatchmaking.CreateLobby: cMaxMembers {cMaxMembers} -> {target}");
            cMaxMembers = target;
        }
    }
}
