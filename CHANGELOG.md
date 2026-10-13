# Changelog

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
