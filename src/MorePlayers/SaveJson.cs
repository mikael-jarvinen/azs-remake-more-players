// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Mikael Järvinen
// More Players for Arizona Sunshine Remake - https://github.com/mikael-jarvinen/azs-remake-more-players
// Licensed under GPL-3.0 with the additional terms in ADDITIONAL-TERMS.md (attribution, permission to run with the game).

using System;
using System.Text;
using System.Text.Json;

namespace AZS1HD.MorePlayers;

/// <summary>
/// The save game as it is sent to joiners: the JSON of one GameStateTO, UTF-8.
/// Pure helpers without game dependencies (tested by tests/SaveJsonTests).
/// </summary>
internal static class SaveJson
{
    internal readonly struct Header
    {
        public Header(int slot, int saveTypeId, int playerCount, int playerIndexToLoad, int digitPos)
        { Slot = slot; SaveTypeId = saveTypeId; PlayerCount = playerCount; PlayerIndexToLoad = playerIndexToLoad; DigitPos = digitPos; }
        public int Slot { get; }
        public int SaveTypeId { get; }
        public int PlayerCount { get; }
        public int PlayerIndexToLoad { get; }
        /// <summary>Byte offset of the single digit of playerIndexToLoad.</summary>
        public int DigitPos { get; }
    }

    private static readonly byte[] IndexKey = Encoding.ASCII.GetBytes("\"playerIndexToLoad\"");

    /// <summary>Reads the fields needed to pick a player's entry. False when the bytes are not a save game.</summary>
    internal static bool TryReadHeader(byte[] bytes, out Header header)
    {
        header = default;
        if (bytes == null || bytes.Length < 16) return false;
        int start = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) start = 3;   // BOM
        while (start < bytes.Length && (bytes[start] == ' ' || bytes[start] == '\r' || bytes[start] == '\n' || bytes[start] == '\t')) start++;
        if (start >= bytes.Length || bytes[start] != (byte)'{') return false;
        int keyPos = bytes.AsSpan().IndexOf(IndexKey);
        if (keyPos < 0) return false;
        try
        {
            int end = bytes.Length;   // tolerate zero padding / trailing whitespace
            while (end > start && (bytes[end - 1] == 0 || bytes[end - 1] == ' ' || bytes[end - 1] == '\r' || bytes[end - 1] == '\n' || bytes[end - 1] == '\t')) end--;
            using var doc = JsonDocument.Parse(bytes.AsMemory(start, end - start));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("slot", out var slot) || slot.ValueKind != JsonValueKind.Number ||
                !root.TryGetProperty("saveTypeID", out var type) || type.ValueKind != JsonValueKind.Number ||
                !root.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("playerIndexToLoad", out var index) || index.ValueKind != JsonValueKind.Number)
                return false;

            // the value must be a single digit right after the key, e.g. "playerIndexToLoad": 1
            int p = keyPos + IndexKey.Length;
            while (p < bytes.Length && (bytes[p] == ' ' || bytes[p] == ':' || bytes[p] == '\t')) p++;
            if (p >= bytes.Length || !IsDigit(bytes[p]) || (p + 1 < bytes.Length && IsDigit(bytes[p + 1]))) return false;
            if (bytes[p] - '0' != index.GetInt32()) return false;

            header = new Header(slot.GetInt32(), type.GetInt32(), players.GetArrayLength(), index.GetInt32(), p);
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>A copy of the save bytes with playerIndexToLoad set to <paramref name="index"/> (0-9).</summary>
    internal static byte[] WithPlayerIndex(byte[] bytes, Header header, int index)
    {
        if (index < 0 || index > 9) throw new ArgumentOutOfRangeException(nameof(index));
        var copy = (byte[])bytes.Clone();
        copy[header.DigitPos] = (byte)('0' + index);
        return copy;
    }

    private static bool IsDigit(byte b) => b >= '0' && b <= '9';
}
