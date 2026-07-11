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

| Type | Starting HP | HP/level | Weapon | Special |
|---|---|---|---|---|
| Mage | 6 | +1 | Wand + Staff | Air Blade, Air Wave |
| Priest | 6 | +1 | Mace | Prayers: Healing, Forgiveness, Lord's Prayer |
| Warrior | 10 | +3 | 2× Hand Axe | Bonus actions scale with level |
| Duelist | 8 | +2 | Rapier + daggers | Duelist Points, special actions |
| Archer | 8 | +3 | Bow + Short Sword | 50 arrows, arrow crafting |
| Martial Artist | 10 | +2 | Unarmed | Martial art style, grapple/throw |
| Berserker | 12 | +4 | Great Axe | Whirlwind spin, Rage (survive lethal hits, heal after rage) |
| Musician | 10 | +1 | Short Sword | Instrument + songs buffing the WHOLE party: Slayer (+atk/dmg to melee/ranged/spells/grapple), Wind Song (+dodge/block/parry), Hardstone (damage reduction), DeathTone (fear by HP vs 2d6). Song tokens: 4 + 1 per 2 levels, refresh each wave; bonuses +2 and fear +1d6 every 3rd level; effects linger 1d4 turns after stopping. Wind/Hardstone = reversible stat deltas on every member (`WindBonusReceived`/`StoneBonusReceived`, subtracted in SaveGame); Slayer = `SlayerAtk()`/`SlayerDmg()` summed over all members' active songs |

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

## Terrain & size (SizeRules class)

- CombatSession generates 1-2 palisade camps (right side, west-facing gate) + random trees/rocks. `Walls`/`Trees`/`Rocks` sets; walls block players (StepMovement) and enemies (wall-aware StepToward). Enemies spawn clustered around `_campCenter`; reinforcements still enter from the right edge.
- Climb (action) on a tree/rock square: +2 attack rolls (`HighGround()`), +1 dodge; climb down = action, jump down = free + 1d4 fall damage. `Player.Climbed`, reset at combat start/end.
- Sizes: small = Goblin/gnomes/hobbits (0), large = Ogre/Giant (2), else medium. Small: +1 atk & +1/+2 dodge vs medium (stacks +1 more vs large), -1 melee dmg (+1 weak-spot vs large cancels), -1 MaxHP at creation/spawn. Giant race (+2 melee dmg, +4 HP, +1 move): -2 dodge vs med/small, -1/-2 block+parry. Ogre: flat -2 dmg taken (players +2 ArmorDR, enemies ToughHide 2/2), +2/+3 dmg but -1/-2 atk vs med/small, -1 dodge vs medium.
- Hooks: DoAttack/PerformAttack, EnemyAttack, block/parry cases, `PDodgeSize()` via `_atkEnemy`, `SizeDodgeRoll` at enemy-dodge sites.

## Actions, packs, feats (second expansion)

- Players get 3 base actions (+AdditionalActions). Goblin race +1 action / -1 attack (players AND NPC goblins); Ogre race -1 action (both sides). Extra action stat costs 4 points.
- Pack capacity: `CarryCap` (8, Artisan 50) limits total materials; shop sells +4 space (2c, +2c each), Artisan Bag +8 (Hunter/Gatherer only), Bag of Holding = limitless. Artisan crafts bags: +4 (+10 for artisans) costing hides = cap/2 (cap/10 artisans). Workshop asks who to craft FOR (`craftFor`).
- Rest = full HP + all pools (rage/duelist/prayers/spells/songs), loops the stop menu.
- Feats: Hunter (tiers: hunt → all deer → +boar → +wolves → +bears; needs dagger/knife), Gatherer (tiers: mine/cut → ALL one kind → both → +1d4 extra; grants pickaxe), Alchemist (brew 2 potions/action: boost/heal/poison AoE/restore; use potion drinks or throws 40ft), Multishot/Split Shot/Piercing Shot/Folly of Arrows (bow shot modes in DoBowAttack), Magic Crafting (returning ammo, rune/scribed armor, Mirror Shield reflect 35%, Bag of Holding), Flurry of Blows (5 attacks, req Fury of Blows), Weapon Specialist (stacking +1d4 atk/dmg per chosen weapon, `WeaponSpec` dict).
- Shop: [8] bag space, [9] magic shop (base+3g), [10] potions (1g x level). New weapons: Pickaxe (2d6, -2 hit), Shortbow, Hunting Bow (+2 hit, 2d3).
- Wildlife HP dice: deer 2d4, wolves 3d4, boars 4d3, bears 8d2.

## Artisan & wildlife

- Artisan class (8 HP +1/lvl; Dagger+Axe+Pickaxe+Shortbow/6 arrows). Materials on Player: Wood/Stone/Ore/Hides/Meat (persisted). In-combat: "mine rock"/"cut tree" actions on terrain squares. Post-wave: [6] Gather (ONE of hunt/mine/cut per stop, 1d3 nodes) and [7] Craft (`VisitCrafting`): arrows (price/1 materials), weapons+shields (price/25), armor (price/15, Rune/Scribed need spells or prayers), -1 material per level (min 1); trade gear/materials to allies; sell materials 1d4c × level each. Shop sells raw materials ([6] Materials). Archers craft 5-25 arrows/wave now.
- Wildlife spawns every wave (`SpawnWildlife`, IsWildlife=true, excluded from victory checks): deer 1d9-1 (1d4-1 antlered fight back when hurt), wolves 1d6-1 (hunt deer, hit-and-run bites within 4 sq), boars 1d5-1 (charge within 5 sq, wheel and re-charge), bears 1d4-1 (den at caves/trees, aggro 3 sq, Fury of Blows bite+4 claws or bear hug grapple). Kills auto-skin: hides 1d9-1, meat 2d6-2. Caves are terrain.
- Class HP/level: Berserker 4, Archer/Warrior 3, Duelist/MA 2, others 1. Starting HP: Berserker 14, Warrior/Archer 12, Duelist/MA/Musician 10, Priest/Mage/Artisan 8.

## Economy (Shop static class)

- Copper-based currency (`Player.Copper`; 100c=1s, 100s=1g, 100g=1p; `Shop.Fmt`). Wave loot by race + leftover gear at 80%, split between players. `VisitShop` = option [5] at the between-wave stop: arrows (blunt/barbed/spiral with bow-side effects), weapons (`Shop.Price`, `Shop.TwoHanded` gated on Giant's Strength), shields (equip into OffHandShield slot), armor (`Shop.Armors`: main + under layer; MeleeDR folds into ArmorDamageReduction, `ArmorSpellDR`/`ArmorPrayerDR` typed, `ArmorAbsorbPct` absorbs spells/prayers via `MitigateMagic` — also lightning-vs-metal burns), sell anything at 80%.
- Ammo breakage: hit 25% break, miss 20% strays to a bystander (35% break) else 50% break; unbroken arrows recovered at wave end (`RecoverRegular` etc.); thrown weapons land or break via `ResolveThrownLanding`.
- Enemy variants: `Ogre.RandType` (2 base:Warrior:Duelist:Berserker w/ 5 rages), `GiantEnemy.RandType` at wave 51+ (base bow+shield, Mage grimoire, Priest, Duelist).

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
