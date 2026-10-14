// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Mikael Järvinen
// More Players for Arizona Sunshine Remake - https://github.com/mikael-jarvinen/azs-remake-more-players
// Licensed under GPL-3.0 with the additional terms in ADDITIONAL-TERMS.md (attribution, permission to run with the game).

using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Vertigo.AZS2;
using Vertigo.AZS2.Client;
using Vertigo.ECS;
using Vertigo.Firebase;
using Vertigo.Networking;

namespace AZS1HD.MorePlayers;

/// <summary>
/// Per-player save data for more than 2 players.
///
/// The checkpoint save (GameStateTO) has a PlayerTO[] of length 2. The game writes the local player into
/// players[0] and EVERY remote player into players[1], and the host tells every joiner to load players[1].
///
/// Fix: every save entry belongs to one player account (the Firebase user id, which never changes).
///  - Host, on save: each pawn is written to its account's entry. The table account -> entry is kept per save
///    in BepInEx/config/azs1hd.moreplayers.saveentries.json, so it survives restarts. The host is always entry 0.
///  - Host, when sending the save to a joiner: the save travels as JSON, once per recipient; the joiner's entry
///    index is written into its copy ("playerIndexToLoad").
///  - Host, on load: key items of players[2+] are moved to the host inventory, like the game does for players[1].
/// Network slots are NOT used: they follow the order players finish loading into a level and change between
/// levels (1.3.0 - 1.4.1 keyed entries by slot, which swapped loadouts).
/// </summary>
[HarmonyPatch]
internal static class SaveSlotPatches
{
    private static GameStateTO current;      // the GameStateTO being written right now (host side)
    private static bool loggedNoIdentity;

    // ---- identity --------------------------------------------------------------------------------------------------

    private static PlayerGameSystem Players()
    {
        try { return ClientGame.Exists ? ClientGame.Instance?.GetSystem<PlayerGameSystem>() : null; }
        catch { return null; }
    }

    private static FirebaseUserToNetworkMappingSystem UserMapping()
    {
        try { return ClientGame.Exists ? ClientGame.Instance?.GetSystem<FirebaseUserToNetworkMappingSystem>() : null; }
        catch { return null; }
    }

    private static bool IsRealPlayer(Entity pawn)
    {
        try
        {
            var id = pawn?.GetModule<IdentityModule>();
            return id != null && !id.IsAI && !id.IsSpectator;
        }
        catch { return false; }
    }

    private static bool UserForHost(Host host, out string userId, out string name)
    {
        userId = null; name = null;
        try
        {
            var map = UserMapping();
            if (map == null || host == null) return false;
            if (!map.TryGetFirebaseUserForHost(host, out User user) || string.IsNullOrEmpty(user.UserId)) return false;
            userId = user.UserId;
            try { name = user.DisplayName.ToString(); } catch { }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Account id of a remote pawn (null when unknown).</summary>
    private static string UserOfPawn(Entity pawn, out string name)
    {
        name = null;
        try
        {
            var net = Players()?.PlayerNetworking;
            if (net == null || !net.TryGetHostForPawn(pawn, out Host host)) return null;
            return UserForHost(host, out string id, out name) ? id : null;
        }
        catch { return null; }
    }

    private static string SaveKey(int slot, int saveTypeId) => $"{saveTypeId}:{slot}";

    // ---- host: save ------------------------------------------------------------------------------------------------

    /// <summary>Entry index for a pawn in the save being written, or -1 to leave the game's choice alone.</summary>
    private static int EntryFor(GameStateTO data, Entity pawn)
    {
        var ps = Players();
        if (ps == null || data == null || pawn == null || !IsRealPlayer(pawn)) return -1;
        if (ps.LocalPawn != null && ps.LocalPawn.Pointer == pawn.Pointer) return 0;
        string user = UserOfPawn(pawn, out string name);
        if (user == null)
        {
            if (!loggedNoIdentity) { loggedNoIdentity = true; Plugin.Logger.LogWarning("Save: could not identify a remote player's account; using the game's default save entry for them."); }
            return -1;
        }
        return SaveEntryTable.GetOrAssign(SaveKey(data.slot, data.saveTypeID), user, name, int.MaxValue);
    }

    /// <summary>Grow data.players so every connected player's entry exists.</summary>
    private static void EnsureCapacity(GameStateTO data)
    {
        var ps = Players();
        if (ps == null || data == null) return;
        int needed = 2;
        var remotes = ps.remotePawns;
        if (remotes != null)
            for (int i = 0; i < remotes.Count; i++)
                needed = Math.Max(needed, EntryFor(data, remotes[i]) + 1);
        var players = data.players;
        int have = players != null ? players.Length : 0;
        if (needed <= have) return;

        var grown = new Il2CppReferenceArray<PlayerTO>(needed);
        for (int i = 0; i < needed; i++)
            grown[i] = i < have && players[i] != null ? players[i] : new PlayerTO();
        data.players = grown;
        Plugin.Logger.LogInfo($"Save: player array grown from {have} to {needed} entries.");
    }

    private static int RedirectIndex(Entity pawn)
    {
        var data = current;
        if (data == null) return -1;
        int i = EntryFor(data, pawn);
        var players = data.players;
        if (i < 0 || players == null || i >= players.Length || players[i] == null) return -1;
        return i;
    }

    private static void BeginSave(GameStateTO data)
    {
        try { EnsureCapacity(data); } catch (Exception e) { Plugin.Logger.LogError($"Save capacity: {e}"); }
        current = data;
    }
    private static void EndSave() => current = null;

    [HarmonyPatch(typeof(HandsSavable), nameof(HandsSavable.OnSaveData))] [HarmonyPrefix]  private static void Hands_Save_Pre(GameStateTO data) => BeginSave(data);
    [HarmonyPatch(typeof(HandsSavable), nameof(HandsSavable.OnSaveData))] [HarmonyPostfix] private static void Hands_Save_Post() => EndSave();
    [HarmonyPatch(typeof(InventorySavable), nameof(InventorySavable.OnSaveData))] [HarmonyPrefix]  private static void Inv_Save_Pre(GameStateTO data) => BeginSave(data);
    [HarmonyPatch(typeof(InventorySavable), nameof(InventorySavable.OnSaveData))] [HarmonyPostfix] private static void Inv_Save_Post() => EndSave();
    [HarmonyPatch(typeof(HealthSavable), nameof(HealthSavable.OnSaveData))] [HarmonyPrefix]  private static void Health_Save_Pre(GameStateTO data) => BeginSave(data);
    [HarmonyPatch(typeof(HealthSavable), nameof(HealthSavable.OnSaveData))] [HarmonyPostfix] private static void Health_Save_Post() => EndSave();
    [HarmonyPatch(typeof(ResourcesSavable), nameof(ResourcesSavable.OnSaveData))] [HarmonyPrefix]  private static void Res_Save_Pre(GameStateTO data) => BeginSave(data);
    [HarmonyPatch(typeof(ResourcesSavable), nameof(ResourcesSavable.OnSaveData))] [HarmonyPostfix] private static void Res_Save_Post() => EndSave();

    // The game calls these once for the local pawn with players[0].x and once per remote pawn with players[1].x.
    // Redirect the target to the entry of the pawn's account.
    [HarmonyPatch(typeof(HandsSavable), "UpdateSaveData")]
    [HarmonyPrefix]
    private static void Hands_Update_Pre(ref HandsTO saveData, Entity pawn)
    {
        int i = RedirectIndex(pawn);
        if (i >= 0 && current.players[i].hands != null) saveData = current.players[i].hands;
    }

    [HarmonyPatch(typeof(InventorySavable), "UpdateSaveData")]
    [HarmonyPrefix]
    private static void Inv_Update_Pre(ref InventoryTO saveData, Entity pawn)
    {
        int i = RedirectIndex(pawn);
        if (i >= 0 && current.players[i].inventory != null) saveData = current.players[i].inventory;
    }

    [HarmonyPatch(typeof(ResourcesSavable), "UpdateSaveData")]
    [HarmonyPrefix]
    private static void Res_Update_Pre(ref ResourcesTO saveData, Entity pawn)
    {
        int i = RedirectIndex(pawn);
        if (i >= 0 && current.players[i].resources != null) saveData = current.players[i].resources;
    }

    [HarmonyPatch(typeof(HealthSavable), "UpdateSaveData")]
    [HarmonyPrefix]
    private static void Health_Update_Pre(ref PlayerTO data, Entity entity)
    {
        int i = RedirectIndex(entity);
        if (i >= 0) data = current.players[i];
    }

    // ---- host: send each joiner its own entry -------------------------------------------------------------------
    // ClientSaveGameSystem serializes the save once (with playerIndexToLoad = 1) and queues one transfer per remote
    // player, also for players joining later. Each recipient gets its own copy with its own entry index; the
    // shared buffer is never modified, because transfers are compressed asynchronously from it.
    [HarmonyPatch(typeof(LargeDataTransferSystem), nameof(LargeDataTransferSystem.QueueTransfer),
        new[] { typeof(LocalHost), typeof(RemoteHost), typeof(Il2CppStructArray<byte>), typeof(bool) })]
    [HarmonyPrefix]
    private static void QueueTransfer_Pre(RemoteHost receivingHost, ref Il2CppStructArray<byte> data)
    {
        try
        {
            if (data == null || data.Length < 64) return;
            byte[] bytes = data;   // managed copy
            if (!SaveJson.TryReadHeader(bytes, out var header)) return;
            if (!UserForHost(receivingHost, out string user, out string name))
            {
                Plugin.Logger.LogWarning($"Load: unknown account for a joining player, sending the game's default entry {header.PlayerIndexToLoad}.");
                return;
            }
            int index = SaveEntryTable.GetOrAssign(SaveKey(header.Slot, header.SaveTypeId), user, name, header.PlayerCount);
            if (index < 1 || index >= header.PlayerCount || index > 9)
            {
                Plugin.Logger.LogInfo($"Load: {name ?? user} has no entry in this save yet; they get the default entry {header.PlayerIndexToLoad} until the next save.");
                return;
            }
            if (index != header.PlayerIndexToLoad)
                data = new Il2CppStructArray<byte>(SaveJson.WithPlayerIndex(bytes, header, index));
            Plugin.Logger.LogInfo($"Load: sending save to {name ?? user}: entry {index} of {header.PlayerCount}.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Load index rewrite failed: {e}");
        }
    }

    // ---- host: key items of players 3 and 4 ------------------------------------------------------------------------
    // The game moves key items from players[1] into the host's inventory when a save is loaded. Do the same for players[2+].
    [HarmonyPatch(typeof(ClientSaveGameSystem), "MoveKeyItemsToHostPlayerInventory")]
    [HarmonyPostfix]
    private static void MoveKeyItems_Post(ClientSaveGameSystem __instance)
    {
        try
        {
            var save = __instance.loadingSavegame;
            var players = save?.players;
            if (players == null || players.Length < 3) return;
            var hostKeys = players[0]?.inventory?.KeyItemsData;
            if (hostKeys == null) return;
            for (int i = 2; i < players.Length; i++)
            {
                var keys = players[i]?.inventory?.KeyItemsData;
                if (keys == null || keys.Count == 0) continue;
                Plugin.Logger.LogInfo($"Load: moving {keys.Count} key item(s) from players[{i}] to the host inventory");
                for (int k = 0; k < keys.Count; k++) hostKeys.Add(keys[k]);
                keys.Clear();
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Key item move failed: {e}");
        }
    }
}
