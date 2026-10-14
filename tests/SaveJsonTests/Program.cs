// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Mikael Järvinen

using System.Text;
using AZS1HD.MorePlayers;

int failed = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failed++; }

// shape of a GameStateTO as the game serializes it (compact, players trimmed)
const string Save = "{\"name\":\"Campaign - Slot 1\",\"version\":2,\"slot\":1,\"saveTypeID\":4385517,\"difficulty\":2," +
    "\"levelCellID\":2300870,\"sessionGuid\":\"19932e3b\",\"checkpointGuid\":null,\"reachedCheckpointsGuids\":[]," +
    "\"levelCompletionFraction\":0.75,\"lastPlayedDate\":\"2026-10-14T20:40:44+03:00\",\"triggeredTutorials\":[]," +
    "\"playerIndexToLoad\":1,\"players\":[{\"hands\":null},{\"hands\":null},{\"hands\":null}]," +
    "\"triggeredSequenceGUIDs\":[],\"companionInventory\":null}";

var bytes = Encoding.UTF8.GetBytes(Save);
Check(SaveJson.TryReadHeader(bytes, out var h), "reads a compact save");
Check(h.Slot == 1 && h.SaveTypeId == 4385517 && h.PlayerCount == 3 && h.PlayerIndexToLoad == 1, "header fields");

var two = SaveJson.WithPlayerIndex(bytes, h, 2);
Check(SaveJson.TryReadHeader(two, out var h2) && h2.PlayerIndexToLoad == 2 && h2.PlayerCount == 3, "index rewritten to 2");
Check(Encoding.UTF8.GetString(bytes).Contains("\"playerIndexToLoad\":1"), "original buffer untouched");

var indented = Encoding.UTF8.GetBytes(Save.Replace(",\"", ",\n  \"").Replace("\":", "\": "));
Check(SaveJson.TryReadHeader(indented, out var hi) && hi.PlayerIndexToLoad == 1, "reads an indented save");

var padded = new byte[bytes.Length + 100];
bytes.CopyTo(padded, 0);
Check(SaveJson.TryReadHeader(padded, out _), "tolerates zero padding");

var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray();
Check(SaveJson.TryReadHeader(bom, out var hb) && SaveJson.WithPlayerIndex(bom, hb, 3)[hb.DigitPos] == (byte)'3', "BOM offsets");

Check(!SaveJson.TryReadHeader(Encoding.UTF8.GetBytes(Save.Replace("\"playerIndexToLoad\":1", "\"playerIndexToLoad\":12")), out _), "rejects two-digit index");
Check(!SaveJson.TryReadHeader(Encoding.UTF8.GetBytes("{\"playerIndexToLoad\":1}"), out _), "rejects other JSON");
Check(!SaveJson.TryReadHeader(new byte[] { 0x1F, 0x8B, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, out _), "rejects binary data");
Check(!SaveJson.TryReadHeader(Encoding.UTF8.GetBytes("{\"slot\":1,\"saveTypeID\":2,\"players\":[], \"playerIndexToLoad\":1"), out _), "rejects truncated JSON");

Console.WriteLine(failed == 0 ? "all passed" : $"{failed} failed");
return failed == 0 ? 0 : 1;
