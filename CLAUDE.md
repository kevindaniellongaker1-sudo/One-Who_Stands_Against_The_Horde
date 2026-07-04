# OWSATH — One Who Stands Against The Horde

Console combat game built in C# .NET 8. Single file `Program.cs` (~4000 lines).
Raylib-cs 6.1.1 graphics on main thread; game logic on background thread.
Development branch: `claude/todo-implementation-ymd2ro`

## Project structure

- `Program.cs` — all game logic (top-level statements + local functions + classes at the bottom)
- `GraphicsDisplay.cs` — Raylib rendering, reads from `SharedGameState`
- `goblinminigame.csproj` — .NET 8, Raylib-cs 6.1.1

## Architecture notes

- `RunGameLogic(SharedGameState)` is the master local function — everything lives inside it
- `CombatSession` class holds a single combat loop; player is `P`, enemies are `Active`
- `Enemy.Alive` is a **computed property** (`HP > 0 && !Fled`) — never set it directly
- `Enemy.Fled = true` to make an enemy flee (not `Alive = false`)
- `GainXP(int xp)` iterates `allPlayers`; XP is × 0.9 per player when party > 1
- Wave spawn makes at most **12 companion rolls** at group start; the group can still grow beyond 12 enemies from those rolls, and in-combat reinforcements (Pending) are unlimited

## Character types

| Type | Starting HP | Weapon | Special |
|---|---|---|---|
| Mage | 6 | Wand + Staff | Air Blade, Air Wave |
| Priest | 6 | Unarmed | Prayers: Healing, Forgiveness, Lord's Prayer |
| Warrior | 10 | 2× Hand Axe | Bonus actions scale with level |
| Duelist | 8 | Rapier + daggers | Duelist Points, special actions |
| Archer | 8 | Bow + Short Sword | 50 arrows, arrow crafting |
| Martial Artist | 10 | Unarmed | Martial art style, grapple/throw |
| Berserker | 12 | Great Axe | Whirlwind spin, Rage (survive lethal hits, heal after rage) |
| Musician | 8 | Short Sword | Instrument + songs buffing the WHOLE party: Slayer (+atk/dmg to melee/ranged/spells/grapple), Wind Song (+dodge/block/parry), Hardstone (damage reduction), DeathTone (fear by HP vs 2d6). Song tokens: 4 + 1 per 2 levels, refresh each wave; bonuses +2 and fear +1d6 every 3rd level; effects linger 1d4 turns after stopping. Wind/Hardstone = reversible stat deltas on every member (`WindBonusReceived`/`StoneBonusReceived`, subtracted in SaveGame); Slayer = `SlayerAtk()`/`SlayerDmg()` summed over all members' active songs |

## Races

| Race | Bonus |
|---|---|
| Moon Elf | +3 spell damage per hit |
| Human | Pick one bonus feat at creation |
| Stone Dwarf | Double starting HP |
| Light-Foot Hobbit | +3 max dodge |
| Sun Elf | +3 to Prayer of Healing rolls |
| Wood Elf | +3 max attack |
| Orc | +3 max melee damage |
| Goblin | +1 max dodge, +1 movement per roll |
| Troll | Regenerate 2 HP per combat turn |
| Iron Dwarf | -3 incoming damage (stacks with armor) |
| Brave Minds Hobbit | +1 dodge, +1 attack, -1 damage taken |

Race is chosen at character creation (`SelectRace` → calls `SelectCharacterType`). Stats are saved/loaded via `Race`, `SpellDamageBonus`, `PrayerHealBonus`, `RegenPerTurn`, `MovementBonus` fields on `Player`.

## Enemy roster

| Enemy | First appears | Notes |
|---|---|---|
| Goblin | Wave 1 | Basic |
| Hobgoblin | Wave 11 | Tougher |
| Orc | Wave 21 | |
| OrcBarbarian | Wave 41+ (roll) | Double-tap |
| Troll | Wave 31 | Regenerates 2-4 HP/turn; flees at HP≤4, returns healed with companions. `Troll.RandType` rolls variants: 20% Warrior, 10% Priest, 10% Musician |
| TrollWarrior | Wave 31+ (roll) | Beefier troll (34 HP, harder hits, 4 spare axes) |
| TrollPriest | Wave 31+ (roll) | Dark prayers (pool-limited): heals wounded allies 3d4 (30ft) or dark smite 2d4 (25ft) |
| TrollMusician | Wave 31+ (roll) | Songs (pool-limited): Silence (1d4 turns, only if player has magic) then war rhythm (+1 atk/dodge all enemies, once) |
| SpellGoblin | Wave 51+ (roll) | Magic attacks |
| Ogre | Wave 41 | Club sweep, companions per roll |
| NecromancerTroll | Wave 71+ (roll) | Raises dead, negative touch 2d4, flees + returns with undead army |

## Ability use pools (reset on REST, not per wave)

- Prayers: 5 + 2 per 2 levels (`PrayerUses`/`MaxPrayerUses()`)
- Spells: 6 + 2 per 2 levels (`SpellUses`/`MaxSpellUses()`) — gates "cast spell" (Twin Caster costs 2)
- Songs: 5 + 1 per 2 levels (`SongTokens`/`MaxSongTokens()`)
- Ability feats grant the pool + kit: prayer feats → Mace; spell feats (Cantrips etc.) → Wand; song feats → pick an instrument. `Player.PrayerFeats/SongFeats/SpellFeats`, `CanPray`/`CanSing`.

## Ability feats

- Prayer of Sanctuary — ward a player 1d4 turns: can't be attacked (enemies skip turn) nor attack (action block)
- Prayer of the Most High — max(1, L/3)d4 holy dmg to all enemies within 50ft
- Prayer of Redemption — full heal + double MaxHP 1d4 turns (`RedemptionExtraHP`, `ExpireRedemption()`)
- Prayer of Mass Blessings — +1d4 to all roll stats of all allies 1d4 turns (`ApplyBlessing`/`ExpireBlessing`)
- War Song — party: +1 dodge/attacks/grapple, +2 healing while playing (persistent song, `WarBonusReceived`)
- Silence Song — `CombatSession.SilenceTurns` = 1d4: hides cast/pray/song options, smothers DoEnemySpell / DoGoblinShamanPray / RaiseDead, ends all active songs
- Song of the Redeemer — instant: heal all allies max(1, L/3)d4

SaveGame subtracts ALL received buff deltas (wind/war/bless/stone/redemption) so mid-combat autosaves stay clean.

Enemy casters have pools too (set in BuildGroup from waveNum with the player formulas):
`Enemy.SpellUsesLeft` (SpellGoblin, NecromancerTroll raise/heal/touch), `PrayerUsesLeft`
(GoblinShaman, TrollPriest), `SongUsesLeft` (TrollMusician). When spent they claw/cower.

## Key systems

### Non-lethal damage
Weapons: unarmed, Staff, Ogre Club, Mace, War Mace, Warhammer → KO living enemies.
Undead always take lethal damage. `IsNonLethalAttack()` + `ResolveDowned()` centralize this.

### Multiplayer (1-4 players)
- Asked at startup; each player picks load or new character independently
- All players get XP when any enemy dies/is KO'd (×0.9 per player when >1)
- Wave starts at lowest `GroupsDefeated` across all players
- All player saves are written on flee or go-home

### Save system
- Per-character `.sav` files in the game save directory
- File name = alphanumeric characters of player name
- `TryLoadGame` sets `p.GroupsDefeated`; `SaveGame` writes all fields including `GroupsDefeated`

### Necromancy
- `RaiseDead(corpse, necro)` — restores HP, sets `IsUndead=true`, strips all feats
- `MakeUndeadEnemy(e)` — same transformation for freshly constructed enemies
- Undead trolls (`Troll` with `IsUndead=true`) skip the regeneration step
- NecromancerTroll flees at HP≤4; returns in 3 turns with 1-3 undead Orc/Troll/Ogre companions

### Berserker rage
- Rage points reset to full (`1 + (level-2)/4+1` from level 2) at the start of each wave
- `IsRaging`, `RageTurnsLeft`, `RagePointsSpent` also reset at wave start
- Rage ends naturally: heal 1d4 × rage points spent

## Suggestions for next session

- **Race display**: show race name next to HP in the combat status line
- **Race-restricted gear/feats**: e.g. Troll race can't wear certain armor; Hobbit races get small-size bonuses to hiding
- **Undead weaknesses**: holy/fire deals bonus damage to undead (Priest prayers already do radiant smite)
- **Troll ally**: after wave 50+, could a reformed troll join the party as a temporary ally?
- **Goblin variants**: sniper goblin (ranged), shaman goblin (buffs allies), bomb goblin (AoE on death)
- **Difficulty scaling after wave 70**: group composition feels thin — elite versions of existing enemies with bonus feats
- **Equipment drops**: enemies could drop usable items (potions, weapons) with some probability
- **Berserker whirlwind**: verify hit count scales correctly every 3 levels starting at level 2
