// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Mikael Järvinen
// More Players for Arizona Sunshine Remake - https://github.com/mikael-jarvinen/azs-remake-more-players
// Licensed under GPL-3.0 with the additional terms in ADDITIONAL-TERMS.md (attribution, permission to run with the game).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;

namespace AZS1HD.MorePlayers;

/// <summary>
/// Which save entry belongs to which player account, per save. Host side only, stored in
/// BepInEx/config/azs1hd.moreplayers.saveentries.json. Entry 0 is always the host and is not stored.
/// </summary>
internal static class SaveEntryTable
{
    internal sealed class Owner
    {
        public int Index { get; set; }
        public string Name { get; set; }
    }

    private static Dictionary<string, Dictionary<string, Owner>> table;
    private static readonly object gate = new();
    private static string FilePath => Path.Combine(Paths.ConfigPath, "azs1hd.moreplayers.saveentries.json");

    private static void Load()
    {
        if (table != null) return;
        try
        {
            table = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Owner>>>(File.ReadAllText(FilePath))
                : null;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Save entry table unreadable, starting a new one: {e.Message}"); }
        table ??= new Dictionary<string, Dictionary<string, Owner>>();
    }

    private static void Store()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(table, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { Plugin.Logger.LogWarning($"Could not store the save entry table: {e.Message}"); }
    }

    /// <summary>
    /// Entry index of a player in a save. Unknown players get the lowest free index from 1 upwards, but only when
    /// it is below <paramref name="limit"/> (the save's current entry count); otherwise -1.
    /// </summary>
    internal static int GetOrAssign(string saveKey, string userId, string name, int limit)
    {
        lock (gate)
        {
            Load();
            if (!table.TryGetValue(saveKey, out var owners)) table[saveKey] = owners = new Dictionary<string, Owner>();
            if (owners.TryGetValue(userId, out var o))
            {
                if (!string.IsNullOrEmpty(name) && o.Name != name) { o.Name = name; Store(); }
                return o.Index;
            }
            var used = new HashSet<int>(owners.Values.Select(v => v.Index));
            int index = 1;
            while (used.Contains(index)) index++;
            if (index >= limit) return -1;
            owners[userId] = new Owner { Index = index, Name = name };
            Store();
            Plugin.Logger.LogInfo($"Save entry {index} now belongs to {name ?? userId} (save {saveKey}).");
            return index;
        }
    }
}
