# One Who Stands Against The Horde (OWSATH)

A turn-based console RPG for 1–5 players. Fight endless waves of goblins,
hobgoblins, orcs, trolls, ogres and necromancers on a tactical grid — with a
live graphics window showing the battlefield. Level up, pick feats, learn
spells, chant prayers, play bardic songs, and see how many waves you can
survive before joining the Hall of the Fallen.

## Features

- **8 classes** — Mage, Priest, Warrior, Duelist, Archer, Martial Artist, Berserker, Musician
- **15 races** — from Stone Dwarf (double HP) to Troll (regeneration) to Gem Gnome
- **Bardic songs, prayers and spells** with rest-based use pools — any class can learn them through feats
- **20+ enemy types** including caster enemies with their own resource pools
- **Local multiplayer** (1–5 players, hot-seat) with shared party buffs
- **Per-character save files** and a persistent high-score board

## Requirements

- Windows, macOS, or Linux
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (to build and run from source)

## Install & Run

### Option 1 — Quick play from source

```
git clone https://github.com/kevindaniellongaker1-sudo/One-Who_Stands_Against_The_Horde.git
cd One-Who_Stands_Against_The_Horde
dotnet run
```

### Option 2 — Install to your Desktop (Windows)

Run `publish_to_desktop.bat`. It builds a single self-contained `OWSATH.exe`
(no .NET needed to play), copies it to your Desktop along with the `assets`
folder, and you double-click the exe to play.

### Option 3 — Shell script (macOS / Linux)

```
./OWSATH.sh
```

## How to play

- The game runs in the **console window** (all prompts and choices) with a
  **graphics window** alongside showing the battle grid, sprites, HP bars and
  a legend of enemy types.
- Follow the prompts: pick player count, create or load a character (race,
  class, stat points, feats), then fight wave after wave.
- In combat, type the number or name of an action: `attack`, `move` (step
  square by square with N/S/E/W), `defend`, `cast spell`, `pray`, `play song`,
  `throw axe`, `run away`, and more depending on your class and feats.
- Between waves choose to **move forward**, **rest** (heal and restore
  spell/prayer/song uses), or **go home** (save and retire the character).
- Saves are written to a `Galaxy Sky` folder on your Desktop — one `.sav`
  file per character, plus the high-score board.

## Building a standalone exe manually

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The exe needs the `assets` folder next to it for sprites (it falls back to
colored tiles if missing).
