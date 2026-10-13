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

namespace AZS1HD.MorePlayers;

/// <summary>
/// Per-player save data for more than 2 players.
///
/// The checkpoint save (GameStateTO) has a PlayerTO[] of length 2. When the host saves, every savable
/// (hands, inventory, health, resources) writes the local player into players[0] and EVERY remote player
/// into players[1], so with 3+ players the remote players overwrite each other. When the host sends the
/// save to the clients it sets playerIndexToLoad = 1 for all of them, so every client loads the same
/// entry. Result: a client loses its loadout and gets another player's.
///
/// Fix: use the player's network slot id (IdentityModule.SlotId: host 0, joiners 1, 2, ...) as the index.
///  - Host, on save: grow the players array to fit every slot and redirect each UpdateSaveData call to
///    players[slot] instead of players[1].
///  - Everyone, on load: set playerIndexToLoad to the local player's slot before the savables read it.
///  - Host, on load: also move key items of players[2+] to the host inventory, like the game does for players[1].
/// </summary>
[HarmonyPatch]
internal static class SaveSlotPatches
{
    private static GameStateTO current;      // the GameStateTO being written right now (host side)
    private static int lastLoggedLoadSlot = -2;

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static int SlotOf(Entity pawn)
    {
        try
        {
            if (pawn == null) return -1;
            var id = pawn.GetModule<IdentityModule>();
            if (id == null || id.IsAI || id.IsSpectator) return -1;
            return id.SlotId;
        }
        catch { return -1; }
    }

    private static PlayerGameSystem Players()
    {
        try
        {
            if (!ClientGame.Exists) return null;
            return ClientGame.Instance?.GetSystem<PlayerGameSystem>();
        }
        catch { return null; }
    }

    private static int LocalSlot()
    {
        var ps = Players();
        return ps == null ? -1 : SlotOf(ps.LocalPawn);
    }

    /// <summary>Make sure data.players has an entry for every connected player's slot.</summary>
    private static void EnsureCapacity(GameStateTO data)
    {
        var ps = Players();
        if (ps == null || data == null) return;
        int maxSlot = SlotOf(ps.LocalPawn);
        var remotes = ps.remotePawns;
        if (remotes != null)
            for (int i = 0; i < remotes.Count; i++)
                maxSlot = Math.Max(maxSlot, SlotOf(remotes[i]));
        int needed = maxSlot + 1;
        var players = data.players;
        int have = players != null ? players.Length : 0;
        if (needed <= have) return;

        var grown = new Il2CppReferenceArray<PlayerTO>(needed);
        for (int i = 0; i < needed; i++)
            grown[i] = i < have && players[i] != null ? players[i] : new PlayerTO();
        data.players = grown;
        Plugin.Logger.LogInfo($"Save: player array grown from {have} to {needed} entries (slots in use up to {maxSlot}).");
    }

    /// <summary>Index in data.players that this pawn's data belongs to, or -1 to leave the game's choice alone.</summary>
    private static int RedirectIndex(Entity pawn)
    {
        var data = current;
        if (data == null) return -1;
        int slot = SlotOf(pawn);
        var players = data.players;
        if (slot < 0 || players == null || slot >= players.Length || players[slot] == null) return -1;
        return slot;
    }

    // ---- host: save ------------------------------------------------------------------------------------------------

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
    // Redirect the target to players[slot].x.
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
        if (i >= 0)
        {
            data = current.players[i];
            if (Plugin.Verbose.Value) Plugin.Logger.LogInfo($"Save: pawn slot {i} written to players[{i}]");
        }
    }

    // ---- everyone: load --------------------------------------------------------------------------------------------

    private static void FixLoadIndex(GameStateTO data, string who)
    {
        try
        {
            if (data == null) return;
            int slot = LocalSlot();
            var players = data.players;
            if (slot < 0 || players == null) return;
            if (slot >= players.Length)
            {
                if (lastLoggedLoadSlot != slot)
                {
                    lastLoggedLoadSlot = slot;
                    Plugin.Logger.LogWarning($"Load: local slot {slot} but the save only has {players.Length} player entries (old save?). Using the game's index {data.playerIndexToLoad}.");
                }
                return;
            }
            if (data.playerIndexToLoad != slot)
            {
                Plugin.Logger.LogInfo($"Load ({who}): playerIndexToLoad {data.playerIndexToLoad} -> {slot} (local player slot)");
                data.playerIndexToLoad = slot;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Load index fix failed: {e}");
        }
    }

    [HarmonyPatch(typeof(HandsSavable), nameof(HandsSavable.OnLoadData))]         [HarmonyPrefix] private static void Hands_Load_Pre(GameStateTO data) => FixLoadIndex(data, "hands");
    [HarmonyPatch(typeof(InventorySavable), nameof(InventorySavable.OnLoadData))] [HarmonyPrefix] private static void Inv_Load_Pre(GameStateTO data) => FixLoadIndex(data, "inventory");
    [HarmonyPatch(typeof(HealthSavable), nameof(HealthSavable.OnLoadData))]       [HarmonyPrefix] private static void Health_Load_Pre(GameStateTO data) => FixLoadIndex(data, "health");
    [HarmonyPatch(typeof(ResourcesSavable), nameof(ResourcesSavable.OnLoadData))] [HarmonyPrefix] private static void Res_Load_Pre(GameStateTO data) => FixLoadIndex(data, "resources");

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
