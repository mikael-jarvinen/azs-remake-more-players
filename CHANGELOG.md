# Changelog

## 1.5.1
- Fixed: 1.5.0 crashed the host's game when a co-op level started (right after "Sending savegame to players") and at
  checkpoints with joiners. The generated interop wrapper for the game's account lookup reserves 8 bytes for a 16-byte
  `User` struct, so the game overwrote the stack. The lookup is now called with a properly sized buffer.
- The log shows `Player accounts: lookup works` at the first save of a session (once your account is linked to a
  network session).
- Hardening from a code review: an error in the save redirect falls back to the game's default instead of skipping
  that player's save data; the ownership table is written atomically and tolerates broken entries; non-save network
  transfers are no longer copied.

## 1.5.0
- Fixed: with 3 or 4 players, joiners' loadouts could swap after a level change. 1.3.0 - 1.4.1 tied save entries to
  network slots, which follow the order in which players finish loading into a level. Save entries now belong to the
  player's account; the host remembers who owns which entry per save slot
  (`BepInEx\config\azs1hd.moreplayers.saveentries.json`) and sends every joiner its own entry.
- Everyone must update: 1.3.0 - 1.4.1 joiners still pick their entry by network slot. The first save after updating
  assigns the entries in join order, so an already swapped loadout may stay swapped once; from then on it sticks.

## 1.4.1
- Foveated rendering moved into its own mod ([azs-remake-foveated-rendering](https://github.com/mikael-jarvinen/azs-remake-foveated-rendering)).

## 1.3.0
- Per-player save data: with 3 or 4 players every player now keeps their own loadout across checkpoints and level
  changes. The game's checkpoint save only had two player entries; the host wrote every joiner into the same entry
  and every joiner loaded it back. Save data is now keyed by each player's network slot.
- Key items of players 3 and 4 are moved to the host's inventory on load, like the game already does for player 2.

## 1.2.1
- Difficulty scaling off by default (a first playtest found it too hard).

## 1.2.0
- Optional difficulty scaling: zombie counts, alive-zombie caps and dynamic ammo scale per extra player.

## 1.1.0
- Joiners take the level's player limit from the lobby they are in, so a player with the mod can still join normal
  2-player games.
- Only private and friends-only campaign lobbies you host are enlarged; public lobbies stay at 2 unless enabled.
- Cap limited to 4 (the in-game party screen is built for 4).

## 1.0.0
- First version: campaign lobby size, per-level player limit, Steam lobby member limit.
