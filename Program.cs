using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

// ═════════════════════════════════════════════════════════════════════════
//  OWSATH — One Who Stands Against The Horde
//  Program.cs — startup, character creation, and everything between waves.
// ═════════════════════════════════════════════════════════════════════════
//
//  WHERE THINGS LIVE (the project is five files):
//    Program.cs        this file — startup, creation, shops, the wave loop
//    Player.cs         the Player class: every stat, trait and pool
//    Enemies.cs        Enemy base class + every monster and animal
//    Rules.cs          SizeRules, Shop (prices/stock), FeatDef, BuyOpt
//    Combat.cs         CombatSession — the whole fight: turns, attacks, AI
//    GraphicsDisplay.cs the Raylib window, sprites and the clickable UI
//
//  HOW IT RUNS:
//    Raylib must own the main thread, so the game logic runs on a background
//    thread and the two talk through SharedGameState. Every Console.Write is
//    mirrored into the window (ConsoleMirror), and GameIO.ReadLine() accepts
//    input from EITHER the console or a click/keypress in the window. That's
//    why all game code prints with Console and reads with GameIO.ReadLine().
//
//  MAP OF THIS FILE (search for the ══ banners):
//    STARTUP            thread setup, asset loading, player count
//    CHARACTER CREATION race → class → core traits → feats → point buy
//    POINT BUY          the purchasable-upgrade catalogue
//    THE SHOP           storefronts, merchant stall, crafting
//    THE WAVE LOOP      spawn a group, fight it, then rest/shop/travel
//    SAVE / LOAD        one .sav file per character
//
//  EDITING TIPS:
//    - Enemy.Alive is computed (HP > 0 && !Fled) — never assign it. To make
//      something flee, set Fled = true.
//    - Adding a race or a feat = adding one entry to a table; the menus sort
//      and number themselves, so don't hardcode option numbers anywhere.
//    - Anything a character keeps between waves must be added to SaveGame
//      AND TryLoadGame, or it silently resets on load.
//
// ═════════════════════════════════════════════════════════════════════════

// ══ STARTUP ══════════════════════════════════════════════════════════════
// Graphics window (main thread) + game logic (background thread)

var sharedState = new SharedGameState();

// Mirror all console text into the graphics window and let it inject input
Console.SetOut(new ConsoleMirror(Console.Out, sharedState));
GameIO.State = sharedState;

var gameThread = new Thread(() =>
{
    try   { RunGameLogic(sharedState); }
    finally { sharedState.GameOver = true; }
});
gameThread.IsBackground = true;
gameThread.Start();

try
{
    var gfx = new GraphicsDisplay(sharedState);
    gfx.Run();
}
catch { sharedState.SignalAssetsReady(); /* no display available — game continues in console-only mode */ }

gameThread.Join();
return;

// ── All original top-level logic lives here ───────────────────────────────
// The whole game, top to bottom. Everything below is a local function of
// this one, which is why they can all see rng / allPlayers / groupsDefeated
// without passing them around. It runs on the background thread.
void RunGameLogic(SharedGameState state)
{

var rng = new Random();
var allPlayers = new List<Player>();
int groupsDefeated = 0;

// Let the graphics window finish loading assets first so its log output
// doesn't bury the banner and the "How many players?" prompt.
state.WaitAssetsReady(10000);

Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("   One Who Stands Against The Horde  (OWSATH)       ");
Console.WriteLine("═══════════════════════════════════════════════════════");

ShowHiscores();

Console.Write("\nHow many players? (1-5, default 1): ");
int numPlayers = 1;
if (int.TryParse((GameIO.ReadLine() ?? "").Trim(), out int npInput) && npInput >= 2 && npInput <= 5)
    numPlayers = npInput;

for (int pi = 1; pi <= numPlayers; pi++)
{
    if (numPlayers > 1) Console.WriteLine($"\n══ Player {pi} ══");
    var p = new Player(rng);

    Console.WriteLine("[N]ew character  [L]oad saved character");
    Console.Write("Choice: ");
    string choice = (GameIO.ReadLine() ?? "n").Trim().ToLower();

    if (choice.StartsWith("l"))
    {
        var saves = ListSaves();
        if (!saves.Any())
        {
            Console.WriteLine("  No saves found. Creating new character.");
            AskName(p);
        }
        else
        {
            Console.WriteLine("\n── Saved Characters ──");
            for (int i = 0; i < saves.Count; i++)
                Console.WriteLine($"  [{i + 1}] {saves[i].name,-22}  Wave {saves[i].wave,3}  Level {saves[i].level}");
            Console.Write("Enter number or name (Enter = new character): ");
            string pick = (GameIO.ReadLine() ?? "").Trim();
            bool loaded = false;
            if (int.TryParse(pick, out int idx) && idx >= 1 && idx <= saves.Count)
                loaded = TryLoadGame(p, saves[idx - 1].path);
            else if (!string.IsNullOrEmpty(pick))
            {
                var match = saves.FirstOrDefault(s => s.name.Equals(pick, StringComparison.OrdinalIgnoreCase));
                if (match.path != null) loaded = TryLoadGame(p, match.path);
            }
            if (loaded)
                Console.WriteLine($"  Loaded {p.Name}! Level {p.Level}, Wave {p.GroupsDefeated + 1}");
            else
            {
                Console.WriteLine("  Starting new character.");
                AskName(p);
            }
        }
    }
    else
    {
        AskName(p);
    }
    // Pick the starting feat now, as part of this character's own creation,
    // so in multiplayer it's clear whose feat is being chosen.
    if (p.PendingFeats > 0) SelectFeats(p);
    allPlayers.Add(p);
}

var player = allPlayers[0];
groupsDefeated = allPlayers.Min(p => p.GroupsDefeated);

Console.WriteLine($"\nParty: {string.Join(", ", allPlayers.Select(p => p.Name))}");
if (allPlayers.Count > 1)
    Console.WriteLine($"Starting at Wave {groupsDefeated + 1} (lowest character's progress).");
Console.WriteLine($"HP: {player.HP}/{player.MaxHP}\n");

// Safety net: any character still owed feats (e.g. an old save) picks them now
foreach (var ep in allPlayers.Where(p => p.PendingFeats > 0)) SelectFeats(ep);

// ══ THE WAVE LOOP ═════════════════════════════════════════════════════════
// The game proper, and it never ends — you play until the party falls or
// retires. Each turn of this loop is one wave:
//   BuildGroup(wave)   roll the horde for this wave number (harder monsters
//                      unlock at thresholds: hobgoblins 11, orcs 21, trolls
//                      31, ogres 41, giants 51, necromancers 71)
//   CombatSession.Run  hand it to Combat.cs and fight
//   the stop menu      survivors travel on, rest (refills every pool),
//                      shop, gather, craft, or go home (save and retire)
// groupsDefeated is the party's progress and starts at the lowest member's.

while (true)
{
    int waveNum = groupsDefeated + 1;
    var group = BuildGroup(waveNum, rng);

    // Reset Berserker rage points to full at the start of each wave
    foreach (var pl in allPlayers.Where(pl => pl.CharacterType == "Berserker"))
    {
        int maxRage = 1 + (pl.Level >= 2 ? (pl.Level - 2) / 4 + 1 : 0);
        pl.RagePoints = maxRage;
        pl.IsRaging = false;
        pl.RageTurnsLeft = 0;
        pl.RagePointsSpent = 0;
    }

    // Make sure no song is still marked active at the start of a wave
    // (tokens no longer refresh per wave — they reset when you rest)
    foreach (var pl in allPlayers.Where(pl => pl.CanSing))
        pl.EndSong(allPlayers);

    Console.WriteLine($"\n──────────────────────────────────");
    Console.WriteLine($" GROUP {waveNum}: {DescribeGroup(group)}");
    Console.WriteLine($"──────────────────────────────────");

    var session = new CombatSession(player, allPlayers, group, rng, XpThreshold, GainXP,
        groupsDefeated == 0 ? new GridPos(1, 48) : new GridPos(1, 25),
        state, waveNum);
    bool survived = session.Run();

    // Songs, blessings, sanctuary and redemption end with the battle;
    // remove all temporary stat bonuses before anything saves.
    // Players who fled were removed from allPlayers mid-combat — include them.
    var everyone = allPlayers.Concat(session.FledPlayers).Distinct().ToList();
    foreach (var pl in everyone.Where(pl => pl.CanSing))
        pl.EndSong(everyone);
    foreach (var pl in everyone)
    {
        pl.SanctuaryTurns = 0;
        pl.Climbed = false;
        pl.ExpireBlessing();
        pl.ExpireRedemption();
    }

    if (session.ExitRequested)
    {
        foreach (var pl in everyone) SaveGame(pl, groupsDefeated);
        Console.WriteLine("  Game saved. Goodbye!");
        break;
    }

    if (!survived)
    {
        Console.WriteLine($"\n╔═══ YOU HAVE FALLEN ═══╗");
        Console.WriteLine($"  Reached wave {waveNum}  Groups defeated: {groupsDefeated}  Level: {player.Level}");
        UpdateHiscores(player.Name, waveNum, player.Level);
        break;
    }

    groupsDefeated++;
    Console.WriteLine($"\n✓ Group {groupsDefeated} cleared!  HP: {player.HP}/{player.MaxHP}  XP: {player.XP}  Level: {player.Level}");

    // ── Loot the fallen: coin by race, plus leftover gear sold at 80% ──
    {
        long lootCopper = 0;
        long armorCopper = 0;
        foreach (var en in group.Where(en => !en.Alive || en.KnockedOut).Where(en => !en.Fled))
        {
            lootCopper += en.Race switch
            {
                "Goblin"    => rng.Next(1, 5),                       // 1d4 copper
                "Hobgoblin" => rng.Next(1, 5) * Shop.Silver,         // 1d4 silver
                "Orc"       => (rng.Next(1, 5) + rng.Next(1, 5)) * Shop.Silver,   // 2d4 silver
                "Troll"     => rng.Next(1, 5) * Shop.Gold,           // 1d4 gold
                "Ogre"      => (rng.Next(1, 4) + rng.Next(1, 4)) * Shop.Gold,     // 2d3 gold
                "Giant"     => (rng.Next(1, 3) + rng.Next(1, 3) + rng.Next(1, 3) + rng.Next(1, 3)) * Shop.Gold, // 4d2 gold
                _           => 0,
            };
            // Strip the fallen of their armor — sold into the pot at 80%
            if (en.ArmorWorn.Length > 0)
                armorCopper += Shop.Sell(en.ArmorWorn.Split(" +")[0]);
            // Richer war chests as the campaign deepens (gear, feats, upgrades)
            lootCopper += waveNum switch
            {
                >= 101 => rng.Next(60, 121) * Shop.Silver,
                >= 91  => rng.Next(40, 81) * Shop.Silver,
                >= 81  => rng.Next(20, 51) * Shop.Silver,
                >= 71  => rng.Next(10, 31) * Shop.Silver,
                _      => 0,
            };
        }
        if (armorCopper > 0)
        {
            Console.WriteLine($"  You strip the dead of their armor — worth {Shop.Fmt(armorCopper)} (sold at 80%).");
            lootCopper += armorCopper;
        }
        long gearCopper = session.LeftoverGearCopper();
        if (gearCopper > 0)
            Console.WriteLine($"  You strip the battlefield of gear worth {Shop.Fmt(gearCopper)} (sold at 80%).");
        lootCopper += gearCopper;
        long weaponCopper = session.FallenGearCopper();
        if (weaponCopper > 0)
        {
            Console.WriteLine($"  The fallen yield weapons, spares and shields worth {Shop.Fmt(weaponCopper)} (sold at 80%).");
            lootCopper += weaponCopper;
        }
        if (lootCopper > 0)
        {
            long share = lootCopper / allPlayers.Count;
            foreach (var pl in allPlayers)
            {
                // Point-buy "money collected" boosts: base is guaranteed,
                // max adds a random top-up, both per wave
                long keenEye = pl.MoneyBonus + rng.Next(0, pl.MoneyMaxBonus + 1);
                pl.Copper += share + keenEye;
                if (keenEye > 0) Console.WriteLine($"  {pl.Name}'s keen eye turns up {Shop.Fmt(keenEye)} extra.");
            }
            Console.WriteLine($"  Loot: {Shop.Fmt(lootCopper)} — {Shop.Fmt(share)} per player." +
                (allPlayers.Count > 1 ? " (split evenly, rounded down)" : ""));
        }

        // A fallen dragon was sleeping on something worth dying for
        if (group.OfType<Dragon>().Any(d => (!d.Alive || d.KnockedOut) && !d.Fled))
            DragonHoard(rng);

        // Gather unbroken arrows from the battlefield
        foreach (var pl in allPlayers)
        {
            int recTotal = pl.RecoverRegular + pl.RecoverBlunt + pl.RecoverBarbed + pl.RecoverSpiral;
            if (recTotal > 0)
            {
                pl.ArrowCount += pl.RecoverRegular;
                pl.BluntArrows += pl.RecoverBlunt;
                pl.BarbedArrows += pl.RecoverBarbed;
                pl.SpiralArrows += pl.RecoverSpiral;
                Console.WriteLine($"  {pl.Name} gathers {recTotal} unbroken arrow(s) from the field.");
                pl.RecoverRegular = pl.RecoverBlunt = pl.RecoverBarbed = pl.RecoverSpiral = 0;
            }
        }
    }
    if (allPlayers.Count > 1)
        foreach (var pl in allPlayers.Skip(1))
            Console.WriteLine($"  {pl.Name}: XP {pl.XP}  Level {pl.Level}");
    while (player.PendingFeats > 0) SelectFeats(player);
    foreach (var ep in allPlayers.Skip(1).Where(p => p.PendingFeats > 0)) SelectFeats(ep);

    if (session.PlayerFled || session.FledPlayers.Any())
    {
        foreach (var pl in everyone) SaveGame(pl, groupsDefeated);
        Console.WriteLine("  (Auto-saved after fleeing.)");
    }

    // Post-combat: Priest level 20+ in party auto-revives all fallen members
    var priest20 = allPlayers.FirstOrDefault(pl => pl.CharacterType == "Priest" && pl.Level >= 20);
    var deadPlayers = allPlayers.Where(pl => pl.HP <= 0).ToList();
    if (priest20 != null && deadPlayers.Any())
    {
        Console.WriteLine($"\n{priest20.Name} calls upon divine grace — the fallen rise!");
        foreach (var dp in deadPlayers) { dp.HP = 1; Console.WriteLine($"  {dp.Name} revived at 1 HP."); }
    }
    else
    {
        foreach (var dp in deadPlayers)
        {
            Console.WriteLine($"  {dp.Name} has died and cannot continue.");
            allPlayers.Remove(dp);
        }
    }
    if (!allPlayers.Any()) break;
    player = allPlayers[0];

    // Each player decides: move forward, rest, shop, go home, or craft arrows
    var goingHome = new List<Player>();
    var gatheredThisStop = new HashSet<Player>();
    foreach (var pl in allPlayers.ToList())
    {
        bool decided = false;
        while (!decided)
        {
        decided = true;
        Console.WriteLine($"\n{pl.Name} ({pl.CharacterType}, HP {pl.HP}/{pl.MaxHP}, Lv {pl.Level}, Purse {Shop.Fmt(pl.Copper)}):");
        bool canGather = pl.CharacterType == "Artisan" || pl.HasFeat("Hunter") || pl.HasFeat("Gatherer");
        Console.WriteLine("  [1] Move forward  [2] Rest  [3] Go home  [5] Shop" +
            (pl.CharacterType == "Archer" ? "  [4] Craft arrows" : "") +
            (canGather ? "  [6] Gather" : "") +
            (pl.CharacterType == "Artisan" ? "  [7] Craft" : ""));
        Console.Write("  Choice: ");
        string next = (GameIO.ReadLine() ?? "1").Trim().ToLower();
        if (next is "5" or "shop" or "buy")
        {
            VisitShop(pl);
            decided = false;   // shopping doesn't end the stop — choose again
            continue;
        }
        if (next is "6" or "gather" && canGather)
        {
            if (gatheredThisStop.Contains(pl))
                Console.WriteLine("  You've already gathered this stop — hunt, mine, OR cut, not all three.");
            else
            {
                GatherAfterWave(pl);
                gatheredThisStop.Add(pl);
            }
            decided = false;
            continue;
        }
        if (next is "7" or "craft" && pl.CharacterType == "Artisan")
        {
            VisitCrafting(pl);
            decided = false;
            continue;
        }
        if (next is "3" or "home" or "quit" or "q" or "go home")
        {
            Console.WriteLine($"  {pl.Name} heads home. Well done!");
            SaveGame(pl, groupsDefeated);
            goingHome.Add(pl);
        }
        else if (next is "2" or "rest" or "heal")
        {
            // A proper rest: everything comes back
            pl.HP = pl.MaxHP;
            Console.WriteLine($"  {pl.Name} rests fully — HP restored to {pl.HP}/{pl.MaxHP}.");
            if (pl.CharacterType == "Duelist")
            {
                int maxPts = pl.Level < 2 ? 0 : (pl.Level <= 20 ? (pl.Level - 2) / 3 + 1 : 7 + 2 * ((pl.Level - 20) / 3));
                pl.DuelistPoints = maxPts;
                Console.WriteLine($"  Duelist Points restored to {maxPts}.");
            }
            if (pl.CharacterType == "Berserker")
            {
                int maxRage = 1 + (pl.Level >= 2 ? (pl.Level - 2) / 4 + 1 : 0);
                pl.RagePoints = maxRage;
                Console.WriteLine($"  Rage points restored to {maxRage}.");
            }
            if (pl.CanPray)     { pl.PrayerUses = pl.MaxPrayerUses(); Console.WriteLine($"  Prayers restored to {pl.PrayerUses}."); }
            if (pl.KnownSpells.Any()) { pl.SpellUses = pl.MaxSpellUses(); Console.WriteLine($"  Spell casts restored to {pl.SpellUses}."); }
            if (pl.CanSing)     { pl.SongTokens = pl.MaxSongTokens(); Console.WriteLine($"  Song tokens restored to {pl.SongTokens}."); }
            if (pl.IsMonk)      { pl.ChiUses = pl.MaxChiUses(); Console.WriteLine($"  Chi restored to {pl.ChiUses}."); }
            decided = false;   // rested and refreshed — keep choosing (shop, craft, gather...)
        }
        else if (next is "4" or "craft" or "arrows")
        {
            if (pl.CharacterType == "Archer") { int crafted = rng.Next(5, 26); pl.ArrowCount += crafted; Console.WriteLine($"  {pl.Name} crafts {crafted} arrows. Total: {pl.ArrowCount}."); }
        }
        }
    }
    foreach (var pl in goingHome) allPlayers.Remove(pl);
    if (!allPlayers.Any()) break;
    player = allPlayers[0];
}

Console.WriteLine("\nThanks for playing!");

// ── Helpers ───────────────────────────────────────────────────────────────

List<Enemy> BuildGroup(int waveNum, Random r)
{
    var g = new List<Enemy>();
    // ── Wave 130: THE DRAGON. Alone. It is enough. ──
    if (waveNum == 130)
    {
        g.Add(new Dragon(r, "The Dragon"));
        return g;
    }
    // ── Wave 140: after ten waves of hoard-geared horde, a dragon returns —
    // with a quarter of the war-band at its heels ──
    if (waveNum == 140)
    {
        var q = BuildGroup(139, r);
        g.AddRange(q.Where((qe, qi) => qi % 4 == 0));
        g.Add(new Dragon(r, "The Dragon's Get"));
        return g;
    }
    if (waveNum <= 10)
    {
        if (waveNum == 1)
        {
            g.Add(new Goblin(r, "Goblin 1"));
        }
        else
        {
            int gn = 1, gwn = 1, dgn = 1, gsn = 1;
            for (int i = 0; i < waveNum; i++)
            {
                switch (r.Next(1, 6))
                {
                    case 1: case 2: g.Add(Goblin.RandType(r, $"Goblin {gn++}")); break;
                    case 3: g.Add(new GoblinWarrior(r, $"Goblin Warrior {gwn++}")); break;
                    case 4: g.Add(new RogueGoblin(r, $"Rogue Goblin {dgn++}")); break;
                    case 5: g.Add(new GoblinShaman(r, $"Goblin Shaman {gsn++}")); break;
                }
            }
        }
    }
    else if (waveNum <= 20)
    {
        // Wave 11-20: each slot rolls — goblin variants, hobgoblins, or goblin warriors
        int slots = waveNum - 10;
        int gn = 1, hn = 1, gwn = 1, gsn = 1;
        for (int i = 0; i < slots; i++)
        {
            switch (r.Next(1, 9))
            {
                case 1: g.Add(Goblin.RandType(r, $"Goblin {gn++}")); break;
                case 2: g.Add(Goblin.RandType(r, $"Goblin {gn++}")); g.Add(Goblin.RandType(r, $"Goblin {gn++}")); break;
                case 3: g.Add(Hobgoblin.RandType(r, $"Hobgoblin {hn++}")); break;
                case 4: g.Add(Hobgoblin.RandType(r, $"Hobgoblin {hn++}")); break;
                case 5: g.Add(new GoblinWarrior(r, $"Goblin Warrior {gwn++}")); break;
                case 6: g.Add(new GoblinWarrior(r, $"Goblin Warrior {gwn++}")); g.Add(new GoblinWarrior(r, $"Goblin Warrior {gwn++}")); break;
                case 7: g.Add(new GoblinShaman(r, $"Goblin Shaman {gsn++}")); break;
                case 8: g.Add(new GoblinShaman(r, $"Goblin Shaman {gsn++}")); g.Add(Goblin.RandType(r, $"Goblin {gn++}")); g.Add(Goblin.RandType(r, $"Goblin {gn++}")); break;
            }
        }
    }
    else if (waveNum <= 30)
    {
        // Wave 21-30: adds 2 hobgoblins, 1 orc, or 2 orcs to the roll table
        int slots = 10;
        int gn = 1, hn = 1, on = 1, dgn = 1, gwn = 1, gsn = 1;
        for (int i = 0; i < slots; i++)
        {
            switch (r.Next(1, 13))
            {
                case 1: g.Add(Goblin.RandType(r, $"Goblin {gn++}")); break;
                case 2: g.Add(Goblin.RandType(r, $"Goblin {gn++}")); g.Add(Goblin.RandType(r, $"Goblin {gn++}")); break;
                case 3: g.Add(Hobgoblin.RandType(r, $"Hobgoblin {hn++}")); break;
                case 4: g.Add(Hobgoblin.RandType(r, $"Hobgoblin {hn++}")); g.Add(Hobgoblin.RandType(r, $"Hobgoblin {hn++}")); break;
                case 5: g.Add(Orc.RandType(r, $"Orc {on++}")); break;
                case 6: g.Add(Orc.RandType(r, $"Orc {on++}")); break;
                case 7: g.Add(new RogueGoblin(r, $"Rogue Goblin {dgn++}")); break;
                case 8: g.Add(new RogueGoblin(r, $"Rogue Goblin {dgn++}")); g.Add(new RogueGoblin(r, $"Rogue Goblin {dgn++}")); break;
                case 9: g.Add(new GoblinWarrior(r, $"Goblin Warrior {gwn++}")); break;
                case 10: g.Add(new GoblinWarrior(r, $"Goblin Warrior {gwn++}")); g.Add(new GoblinWarrior(r, $"Goblin Warrior {gwn++}")); break;
                case 11: g.Add(new GoblinShaman(r, $"Goblin Shaman {gsn++}")); break;
                case 12: g.Add(new GoblinShaman(r, $"Goblin Shaman {gsn++}")); g.Add(Goblin.RandType(r, $"Goblin {gn++}")); g.Add(Goblin.RandType(r, $"Goblin {gn++}")); break;
            }
        }
    }
    else if (waveNum <= 40)
    {
        // Wave 31-40: adds 2 orcs, 1 troll, or 2 trolls to the roll table
        int slots = 10;
        int gn = 1, hn = 1, on = 1, tn = 1, dgn = 1, gwn = 1, gsn = 1;
        for (int i = 0; i < slots; i++)
        {
            switch (r.Next(1, 15))
            {
                case 1: g.Add(Goblin.RandType(r, $"Goblin {gn++}")); break;
                case 2: g.Add(Goblin.RandType(r, $"Goblin {gn++}")); g.Add(Goblin.RandType(r, $"Goblin {gn++}")); break;
                case 3: g.Add(Hobgoblin.RandType(r, $"Hobgoblin {hn++}")); break;
                case 4: g.Add(Hobgoblin.RandType(r, $"Hobgoblin {hn++}")); g.Add(Hobgoblin.RandType(r, $"Hobgoblin {hn++}")); break;
                case 5: g.Add(Orc.RandType(r, $"Orc {on++}")); break;
                case 6: g.Add(Orc.RandType(r, $"Orc {on++}")); g.Add(Orc.RandType(r, $"Orc {on++}")); break;
                case 7: g.Add(Troll.RandType(r, $"Troll {tn++}")); break;
                case 8: g.Add(Troll.RandType(r, $"Troll {tn++}")); break;
                case 9: g.Add(new RogueGoblin(r, $"Rogue Goblin {dgn++}")); break;
                case 10: g.Add(new RogueGoblin(r, $"Rogue Goblin {dgn++}")); g.Add(new RogueGoblin(r, $"Rogue Goblin {dgn++}")); break;
                case 11: g.Add(new GoblinWarrior(r, $"Goblin Warrior {gwn++}")); break;
                case 12: g.Add(new GoblinWarrior(r, $"Goblin Warrior {gwn++}")); g.Add(new GoblinWarrior(r, $"Goblin Warrior {gwn++}")); break;
                case 13: g.Add(new GoblinShaman(r, $"Goblin Shaman {gsn++}")); break;
                case 14: g.Add(new GoblinShaman(r, $"Goblin Shaman {gsn++}")); g.Add(Goblin.RandType(r, $"Goblin {gn++}")); g.Add(Goblin.RandType(r, $"Goblin {gn++}")); break;
            }
        }
    }
    else
    {
        // Wave 41+: each slot rolls to determine what spawns (ogre is one result, not guaranteed)
        // Wave 121+ the drums beat harder: one extra roll per group
        int slots = Math.Min(waveNum - 40, 10) + (waveNum >= 121 ? 1 : 0);
        int trolls = Math.Max(0, 10 - slots);
        for (int i = 0; i < trolls; i++) g.Add(Troll.RandType(r, $"Troll {i + 1}"));
        for (int i = 0; i < slots && i < 12; i++)
        {
            // Wave 150+: a dragon may answer the drums (one per group at most)
            int crMax = waveNum >= 150 ? 14 : waveNum >= 71 ? 13 : 11;
            int cr = r.Next(1, crMax);
            if (cr == 13 && !g.Any(en => en is Dragon)) { g.Add(new Dragon(r, "Dragon")); continue; }
            if (cr == 13) cr = r.Next(1, 13);   // one dragon is plenty — reroll
            switch (cr)
            {
                case 1: case 2: g.Add(Ogre.RandType(r, $"Ogre {i + 1}")); break;
                case 3: for (int j = 0; j < 3; j++) g.Add(Orc.RandType(r, $"Orc {i * 3 + j + 1}")); break;
                case 4: g.Add(Troll.RandType(r, $"Troll {i * 2 + 1}")); g.Add(Troll.RandType(r, $"Troll {i * 2 + 2}")); break;
                case 5: for (int j = 0; j < 4; j++) g.Add(Hobgoblin.RandType(r, $"Hobgoblin {i * 4 + j + 1}")); break;
                case 6: g.Add(new GoblinShaman(r, $"Goblin Shaman {i+1}")); g.Add(new GoblinWarrior(r, $"Goblin Warrior {i*2+1}")); g.Add(new GoblinWarrior(r, $"Goblin Warrior {i*2+2}")); for (int j = 0; j < 2; j++) g.Add(Goblin.RandType(r, $"Goblin {i * 2 + j + 1}")); break;
                case 7: if (waveNum >= 51) { int sgc = waveNum >= 91 ? 3 : 1; for (int j = 0; j < sgc; j++) g.Add(new SpellGoblin(r, $"Spell Goblin {i * 3 + j + 1}")); } else for (int j = 0; j < 3; j++) g.Add(Orc.RandType(r, $"Orc {i * 3 + j + 1}")); break;
                case 8: if (waveNum >= 51) { int sgc = waveNum >= 91 ? 3 : 2; for (int j = 0; j < sgc; j++) g.Add(new SpellGoblin(r, $"Spell Goblin {i * 3 + j + 1}")); } else for (int j = 0; j < 4; j++) g.Add(Hobgoblin.RandType(r, $"Hobgoblin {i * 4 + j + 1}")); break;
                case 9: if (waveNum >= 61) g.Add(new OrcBarbarian(r, $"Orc Barbarian {i + 1}")); else g.Add(Troll.RandType(r, $"Troll {i + 1}")); break;
                case 10: g.Add(new RogueGoblin(r, $"Rogue Goblin {i*2+1}")); g.Add(new RogueGoblin(r, $"Rogue Goblin {i*2+2}")); break;
                case 11: g.Add(new NecromancerTroll(r, $"Necromancer Troll {i + 1}")); break;
                case 12: g.Add(new NecromancerTroll(r, $"Necromancer Troll {i + 1}")); g.Add(new Troll(r, $"Troll Thrall {i + 1}")); break;
                default: if (waveNum >= 61) { g.Add(new OrcBarbarian(r, $"Orc Barbarian {i*2 + 1}")); g.Add(new OrcBarbarian(r, $"Orc Barbarian {i*2 + 2}")); } else { g.Add(Troll.RandType(r, $"Troll {i*2 + 1}")); g.Add(Troll.RandType(r, $"Troll {i*2 + 2}")); } break;
            }
        }
    }

    // Giants join the horde 10 waves after Orc Barbarians first appear (wave 51+)
    if (waveNum >= 51)
    {
        int giantSlots = 1 + (waveNum - 51) / 15;
        for (int gi = 0; gi < giantSlots; gi++)
            if (r.Next(1, 4) == 1)
                g.Add(GiantEnemy.RandType(r, $"Giant {gi + 1}"));

        // Once giants march, every race musters harder: +1 of each, goblins +2
        g.Add(Goblin.RandType(r, "Goblin Muster 1"));
        g.Add(Goblin.RandType(r, "Goblin Muster 2"));
        g.Add(Hobgoblin.RandType(r, "Hobgoblin Muster"));
        g.Add(new Orc(r, "Orc Muster"));
        g.Add(Troll.RandType(r, "Troll Muster"));
        g.Add(Ogre.RandType(r, "Ogre Muster"));
        g.Add(GiantEnemy.RandType(r, "Giant Muster"));
    }

    // ── Wave 91+: the horde musters DEEPER — extra bodies of every kind ──
    if (waveNum >= 91)
    {
        for (int i = 1; i <= 3; i++) g.Add(Goblin.RandType(r, $"Goblin Reinforcement {i}"));
        for (int i = 1; i <= 2; i++) g.Add(Hobgoblin.RandType(r, $"Hobgoblin Reinforcement {i}"));
        for (int i = 1; i <= 2; i++) g.Add(Orc.RandType(r, $"Orc Reinforcement {i}"));
        for (int i = 1; i <= 2; i++) g.Add(Troll.RandType(r, $"Troll Reinforcement {i}"));
        if (g.Any(e => e is OrcBarbarian)) g.Add(new OrcBarbarian(r, "Orc Barbarian Reinforcement"));
        if (g.Any(e => e is NecromancerTroll)) g.Add(new NecromancerTroll(r, "Necromancer Reinforcement"));
        if (g.Any(e => e is Ogre)) g.Add(Ogre.RandType(r, "Ogre Reinforcement"));
        if (g.Any(e => e is GiantEnemy)) g.Add(GiantEnemy.RandType(r, "Giant Reinforcement"));
    }
    // Wave 101+: one more of every kind that marches
    if (waveNum >= 101)
    {
        if (g.Any(e => e is Goblin)) g.Add(Goblin.RandType(r, "Goblin Vanguard"));
        if (g.Any(e => e is Hobgoblin)) g.Add(Hobgoblin.RandType(r, "Hobgoblin Vanguard"));
        if (g.Any(e => e is Orc)) g.Add(Orc.RandType(r, "Orc Vanguard"));
        if (g.Any(e => e is Troll)) g.Add(Troll.RandType(r, "Troll Vanguard"));
        if (g.Any(e => e is Ogre)) g.Add(Ogre.RandType(r, "Ogre Vanguard"));
        if (g.Any(e => e is GiantEnemy)) g.Add(GiantEnemy.RandType(r, "Giant Vanguard"));
    }

    // Enemy casters: uses = floor(lowestPlayerLevel * 0.80), minimum 1
    int lowestPlayerLevel = allPlayers.Any() ? allPlayers.Min(pl => pl.Level) : waveNum;
    int enemyAbilityUses  = Math.Max(1, (int)(lowestPlayerLevel * 0.80));
    foreach (var e in g)
    {
        e.Level          = waveNum;
        e.SpellUsesLeft  = enemyAbilityUses;
        e.PrayerUsesLeft = enemyAbilityUses;
        e.SongUsesLeft   = enemyAbilityUses;
        // Orc Monks are martial artists — chi at 80% of the lowest player level
        if (e is OrcMonk) e.ChiLeft = enemyAbilityUses;
        OutfitLateGameHorde(e, waveNum, r);
        // Small creatures are slighter of frame: -1 max HP
        if (SizeRules.Of(e.Race) == 0)
        {
            e.MaxHP = Math.Max(1, e.MaxHP - 1);
            e.HP = Math.Min(e.HP, e.MaxHP);
        }
        // Goblins are quick but wild: -1 to attack (they gain +1 action in combat)
        if (e.Race == "Goblin")
        {
            e.MinAttack = Math.Max(1, e.MinAttack - 1);
            e.MaxAttack = Math.Max(1, e.MaxAttack - 1);
        }
    }
    return g;
}

// ══ LATE-GAME OUTFITTING (wave 71+) ═══════════════════════════════════════
// The horde arms up as the war grinds on. Armor at 71+, caster robes at 81+,
// heavy plate / Scribed Robes / Bard Vestments at 91+ plus deep stat scaling,
// a second armor layer (sometimes runed) at 101+. XP and loot rise to match:
// every piece an enemy wears is stripped and sold into the pot when it falls.
void OutfitLateGameHorde(Enemy e, int wave, Random r)
{
    if (e.IsWildlife) return;
    if (e is Dragon) return;   // the dragon needs no armor — it IS the tier

    // Giants are worth 85 XP from the day they first march
    if (e is GiantEnemy && e.XPValue < 85) e.XPValue = 85;
    if (wave < 71) return;

    bool priestly = e is TrollPriest or GiantPriest or OrcPriestess or HobgoblinCleric or GoblinShaman;
    bool arcane   = e is SpellGoblin or GiantMage or NecromancerTroll;
    bool musician = e is TrollMusician;
    bool caster   = priestly || arcane || musician;
    bool brute    = e is Ogre or GiantEnemy or TrollWarrior or OrcBarbarian;
    bool skirmish = e is RogueGoblin or HobgoblinThief or OrcRanger or OrcMonk;

    void Wear(string name, int dr, int absorb = 0)
    { e.ArmorWorn = name; e.ArmorDR = dr; e.SpellAbsorbPct = absorb; }

    // ── Armor by wave band and role ──
    if (wave >= 91)
    {
        if (musician)      Wear("Bard Vestments", 1, 20);
        else if (e is NecromancerTroll) Wear("Unholy Robe", 1, 20);
        else if (arcane)   Wear(r.Next(2) == 0 ? "Scribed Robes" : new[] { "Fire Robes", "Frost Robes", "Lightning Robes", "Air Robe" }[r.Next(4)], 1, 25);
        else if (priestly) Wear(r.Next(2) == 0 ? "Holy Vestments" : "Scribed Robes", 1, 25);
        else if (brute)    Wear(r.Next(2) == 0 ? "Full Plate" : "Half Plate", r.Next(2) == 0 ? 5 : 4);
        else if (skirmish) Wear("Hard Leather", 3);
        else               Wear(new[] { "Half Plate", "Hard Leather", "Padded Armor", "Soft Leather" }[r.Next(4)], r.Next(3, 5));
    }
    else if (wave >= 81 && caster)
    {
        if (e is NecromancerTroll) Wear("Unholy Robe", 1, 15);
        else if (priestly)         Wear("Holy Vestments", 1, 15);
        else                       Wear(new[] { "Fire Robes", "Frost Robes", "Lightning Robes", "Air Robe" }[r.Next(4)], 1, 15);
    }
    else
    {
        if (brute)         Wear(r.Next(2) == 0 ? "Chainmail" : "Breastplate", 3);
        else if (skirmish) Wear("Soft Leather", 2);
        else if (caster)   Wear(r.Next(2) == 0 ? "Padded Armor" : "Leather Vest", 1);
        else               Wear(r.Next(2) == 0 ? "Leather Armor" : "Padded Armor", 2);
    }

    // ── Wave 101+: a second layer, sometimes rune-worked ──
    if (wave >= 101)
    {
        if (r.Next(100) < 15)
        {
            e.ArmorWorn += " + Rune layer";
            e.SpellAbsorbPct = Math.Min(60, e.SpellAbsorbPct + 20);
            e.ArmorDR += 2;
        }
        else { e.ArmorWorn += " + under-layer"; e.ArmorDR += 2; }
    }

    // ── XP rises with the gear ──
    e.XPValue += r.Next(20, 36);                       // armored (71+)
    if (wave >= 91)  e.XPValue += r.Next(25, 41);      // veteran gear
    if (wave >= 101) e.XPValue += r.Next(30, 51);      // elite upgrades

    // ── Wave 91+: deep per-race scaling, fitting feats ──
    if (wave >= 91)
    {
        void Hp(int n)  { e.MaxHP += n; e.HP = e.MaxHP; }
        void Atk(int n) { e.MinAttack += n; e.MaxAttack += n; }
        void Dmg(int n) { e.MinDamage += n; e.MaxDamage += n; }

        if (e.Race == "Goblin" && e is not SpellGoblin)
        {
            int b = r.Next(1, 7);
            Hp(12); Atk(b);
            e.MinDodge += b; e.MaxDodge += b;   // (enemies fold block into dodge)
        }
        else if (e.Race == "Hobgoblin")
        {
            Hp(12); Atk(r.Next(2, 9)); Dmg(r.Next(1, 5));
            e.HasParry = true;                          // drilled veteran
        }
        else if (e.Race == "Orc")
        {
            Hp(15); Atk(r.Next(1, 7));
            if (e is OrcMonk) e.HasKick = true; else e.HasDoubleTap = true;
        }
        else if (e is NecromancerTroll)
        {
            Hp(15);
            if (e is Troll ncr) ncr.SpareAxes += 2;
            e.LichBound = true;                         // the lich pact
            e.SpellUsesLeft += 2;
        }
        else if (e.Race == "Troll")
        {
            Hp(15);
            if (e is Troll tr) tr.SpareAxes += 2;
            e.HasBlock = true;                          // scarred survivor
        }
        else if (e.Race == "Ogre")
        {
            Hp(10); e.HasArmBlock = true;
            if (e is OgreBerserker) Hp(4);              // the extra rage burns as vigour
            Dmg(1); Atk(1); Hp(2);                      // 6 points, spent simply
        }
        else if (e.Race == "Giant")
        {
            Hp(10);
            e.SpellUsesLeft++; e.PrayerUsesLeft++;
            e.HasParry = true;
            Atk(1); Dmg(1); Hp(2);                      // 6 points
        }

        // Wave 101+: elites — another feat, more points, honed weapons
        if (wave >= 101)
        {
            Hp(r.Next(10, 21));
            Atk(2); Dmg(2); Hp(4);                      // 12 points
            e.MinDodge += 1; e.MaxDodge += 1;
            e.HasKick = true;
            Dmg(2);                                     // honed weapon upgrade
        }

        // ── Wave 111+: seasoned campaigners — a fitting boost by calling,
        // bridging the elites of 101 and the masters of 121 ──
        if (wave >= 111)
        {
            Hp(r.Next(5, 11));
            Atk(1); Dmg(1); Hp(1);                      // 4 points, spent simply
            if (caster)
            { e.SpellUsesLeft++; e.PrayerUsesLeft++; e.SongUsesLeft++; }   // deeper wells
            else if (skirmish)
            { e.MinDodge++; e.MaxDodge++; }             // quicker on their feet
            else Dmg(1);                                // fighters just hit harder
            e.XPValue += r.Next(10, 21);
        }

        // ── Wave 121+: the horde's masters and specialists come into their own ──
        if (wave >= 121)
        {
            if (e is OrcMonk omk)
            {
                Wear("Monk Garbs", 3, 25);              // their signature garb
                e.HasDoubleTap = true;                  // second martial style
                Hp(10); Atk(1); Dmg(1); Hp(2);          // +10 HP and 6 points
                // A true monk weapon, matched to their style
                omk.MonkWeapon = omk.MartialStyle switch
                {
                    "Grappler" => r.Next(2) == 0 ? "Chain and Ball" : "Spike Chain",
                    "Defender" => r.Next(2) == 0 ? "Tetsubo" : "Staff",
                    _          => r.Next(2) == 0 ? "Nunchucks" : "Katana",   // Striker
                };
                Dmg(2);                                 // the weapon bites harder
            }
            else if (e is TrollMusician tmu)
            {
                e.SongUsesLeft += 2;                    // another song feat
                tmu.SpareAxes += 3;
                Hp(5); e.HasParry = true;               // fitting combat feat
            }
            else if (e is GoblinShaman)
            {
                e.PrayerUsesLeft += 2;                  // another prayer feat
                Hp(8); Atk(1); Dmg(1); Hp(2);           // 6 points
                e.HasBlock = true;
                Dmg(2);                                 // takes up a War Mace
            }
            else if (e is NecromancerTroll nct)
            {
                nct.EquippedAxes = 2;                   // two hand axes now
                e.SpellUsesLeft += 2;                   // another spell feat
                e.HasBlock = true;
                Atk(2); Dmg(1); Hp(3);                  // 8 points
                Hp(5);
            }
            else if (e is SpellGoblin)
            {
                e.SpellUsesLeft += 2;                   // deeper schooling
                Atk(1); Dmg(1); Hp(2);                  // 6 points
            }
            else if (e is GiantMage)
            {
                Hp(12); Atk(1); Dmg(1); Hp(1);          // 5 points
                e.SpellUsesLeft += 4;                   // two more spell feats
                Dmg(2);                                 // a long staff
                e.ThrowPotions = r.Next(1, 4);          // volatile flasks
            }
            else if (e.Race == "Goblin")
            {
                Hp(12); Atk(2); Dmg(2); Hp(4); e.MinDodge += 2; e.MaxDodge += 2;   // 12 points
                e.HasParry = true;
                e.HasDoubleTap = true;
                if (e is RogueGoblin rgd) rgd.DaggerCount += 4;
            }
            else if (e.Race == "Hobgoblin")
            {
                Hp(10); Atk(2); Dmg(1); Hp(2);          // +10 HP and 8 points
                e.HasDoubleTap = true; e.HasParry = true;   // two fitting feats
                Dmg(2);                                 // weapon upgrades
                e.HealPotions = 1;                      // one healing potion each
            }
            else if (e.Race == "Orc")                   // monks were handled above
            {
                Hp(10); Atk(1); Dmg(1); Hp(2);          // 6 points
                e.HasDoubleTap = true; e.HasBlock = true;   // two feats
                Dmg(2);                                 // weapon upgrade
                e.ArmorDR += 1;                         // shield upgrade
                e.ExtraActions += 1;                    // another action per turn
            }
            else if (e.Race == "Troll")                 // musicians/necromancers handled above
            {
                Hp(8); Atk(1); Dmg(1); Hp(2);           // 6 points
                e.HasBlock = true; e.HasParry = true;   // two fitting feats
                e.DoubleRegen = true;                   // regeneration runs twice as hot
            }
            else if (e.Race == "Ogre")
            {
                Hp(12); Atk(2); Dmg(1); Hp(3);          // 8 points
                Dmg(2);                                 // weapon upgrade
                e.HasDoubleTap = true;
            }
            else if (e.Race == "Giant")                 // mages handled above
            {
                Atk(2); Dmg(2); Hp(3); e.MinDodge++; e.MaxDodge++;   // 10 points
                e.HasParry = true; e.HasKick = true;    // two fitting feats
                Dmg(2); e.ArmorDR += 1;                 // upgraded weapon & shield
                e.HasBlock = true; e.HasArmBlock = true;   // +2 feats fitting that kit
            }
        }

        // ── Waves 131-140: the survivors raided a hoard of their own —
        // modest magical trinkets, two battle-draughts each, and 4 points ──
        // Wave 131 and up, forever after: every soldier carries two RANDOM
        // non-healing potions from the dragon-hoard roster, quaffed in battle
        if (wave >= 131) e.BuffPotions = 2;

        if (wave >= 131 && wave <= 140)
        {
            Atk(1); Dmg(1); Hp(2);                      // 4 points
            if (arcane)        { e.MagicTrinket = "Ember Focus"; e.SpellUsesLeft += 1; }
            else if (priestly) { e.MagicTrinket = "Blessed Talisman"; e.PrayerUsesLeft += 1; }
            else if (musician) { e.MagicTrinket = "Silver Tuning Fork"; e.SongUsesLeft += 1; }
            else if (skirmish) { e.MagicTrinket = "Swiftness Charm"; e.MinDodge += 1; e.MaxDodge += 1; }
            else if (brute)    { e.MagicTrinket = "Charmed Blade-Oil"; Dmg(1); }
            else               { e.MagicTrinket = "Soldier's Luckstone"; Atk(1); }
            // Ability-users learn the robes-under-armor trick too — a light
            // layer over the robe, deliberately not too strong
            if (caster && e.SpellAbsorbPct > 0) { e.ArmorWorn += " over robes"; e.ArmorDR += 2; }
        }

        // ── Wave 141+: back to plain steel at normal strength, but wiser —
        // one more fitting feat and 4 points ──
        if (wave >= 141)
        {
            Atk(1); Dmg(1); Hp(2);                      // 4 points
            if (e.Race == "Goblin")         e.HasKick = true;
            else if (e.Race == "Hobgoblin") e.HasArmBlock = true;
            else if (e.Race == "Orc")       e.HasParry = true;
            else if (e.Race == "Troll")     e.HasKick = true;
            else if (e.Race == "Ogre")      e.HasBlock = true;
            else if (e.Race == "Giant")     e.HasDoubleTap = true;
        }
    }
}

string DescribeGroup(List<Enemy> g) =>
    string.Join(", ", g.GroupBy(e => e.TypeName).Select(gr => $"{gr.Count()}x {gr.Key}"));

int XpThreshold(int level)
{
    if (level <= 1) return 0;
    int total = 0, gap = 55;
    for (int i = 1; i < level; i++)
    {
        total += gap;
        // Base rate = 10. Multiplier increases each 10-level tier.
        // ×1→×2→×2.5→×3→×4→×4.5→×5→×5.5→×6→×6.5 (per-tier inc rises by 0.5×10 each bracket past 50).
        // After level 100: double the rate (×13 = 130); holds through level 200.
        int inc = i < 10  ? 10
                : i < 20  ? 20
                : i < 30  ? 25
                : i < 40  ? 30
                : i < 50  ? 40
                : i < 60  ? 45
                : i < 70  ? 50
                : i < 80  ? 55
                : i < 90  ? 60
                : i < 100 ? 65
                : 130;
        gap += inc;
    }
    return total;
}

void GainXP(int xp)
{
    int adjusted = allPlayers.Count > 1 ? (int)(xp * 0.9) : xp;
    foreach (var pl in allPlayers)
    {
        pl.XP += adjusted;
        string tag = allPlayers.Count > 1 ? $" ({pl.Name})" : "";
        Console.WriteLine($"  +{adjusted} XP{tag}! (Total: {pl.XP}, Level: {pl.Level})");
        while (pl.XP >= XpThreshold(pl.Level + 1))
        {
            foreach (var sv in allPlayers) SaveGame(sv, groupsDefeated);
            pl.Level++;
            string who = allPlayers.Count > 1 ? pl.Name : "You";
            Console.WriteLine($"\n★★★ LEVEL UP! {who} {(allPlayers.Count > 1 ? "is" : "are")} now Level {pl.Level}! ★★★");

            // Class HP per level: Berserker 4, Archer/Warrior 3,
            // Duelist/Martial Artist 2, Priest/Mage/Musician 1
            int hpGain = pl.CharacterType switch
            {
                "Berserker" => 4,
                "Archer" or "Warrior" => 3,
                "Duelist" or "Martial Artist" => 2,
                _ => 1,
            };
            pl.MaxHP += hpGain;
            pl.HP += hpGain;
            Console.WriteLine($"  +{hpGain} max HP! ({pl.HP}/{pl.MaxHP})");

            if (pl.Level >= 2)
            {
                pl.SavedStatPoints++;
                Console.WriteLine($"  Stat point gained! (Total saved: {pl.SavedStatPoints})");
                SpendStatPoints(pl);
            }

            if (pl.Level % 5 == 0)
            {
                pl.GearPointsAvailable++;
                Console.WriteLine($"  Gear point earned! (Level {pl.Level} milestone)");
                SpendGearPoints(pl);
            }
            if (pl.CharacterType == "Berserker")
            {
                int maxRage = 1 + (pl.Level >= 2 ? (pl.Level - 2) / 4 + 1 : 0);
                if (pl.RagePoints < maxRage) pl.RagePoints = maxRage;
            }
            if (pl.CanSing)
            {
                if (pl.Level % 2 == 0)
                    Console.WriteLine($"  Bardic song token earned! (Max tokens: {pl.MaxSongTokens()})");
                if (pl.SongTokens < pl.MaxSongTokens()) pl.SongTokens = pl.MaxSongTokens();
                if (pl.CharacterType == "Musician" && pl.Level % 3 == 0)
                    Console.WriteLine($"  Songs grow stronger! Bonuses +{pl.SongBonusAmount()}, DeathTone fear {pl.FearDiceCount()}d6.");
            }
            if (pl.CanPray && pl.PrayerUses < pl.MaxPrayerUses())
            {
                pl.PrayerUses = pl.MaxPrayerUses();
                if (pl.Level % 2 == 0) Console.WriteLine($"  Prayer uses increased! (Max: {pl.MaxPrayerUses()})");
            }
            if (pl.KnownSpells.Any() && pl.SpellUses < pl.MaxSpellUses())
            {
                pl.SpellUses = pl.MaxSpellUses();
                if (pl.Level % 2 == 0) Console.WriteLine($"  Spell casts increased! (Max: {pl.MaxSpellUses()})");
            }
        }
    }
}

// ══ CHARACTER CREATION ════════════════════════════════════════════════════
// Order matters and is deliberate: race → class → core traits → feats →
// point buy → appearance. Traits and race bonuses (Human's +6) feed the
// stat-point pool, so the point buy has to come last or those are lost.
// All of it runs inside SelectRace, which AskName calls.

void SelectFeats(Player p)
{
    while (p.PendingFeats > 0)
    {
        Console.WriteLine($"\n═══ FEAT SELECTION — {p.Name} ({p.CharacterType}) ═══");
        var avail = FeatDef.All.Where(f =>
        {
            if (f.Prerequisite != null && !p.HasFeat(f.Prerequisite)) return false;
            if (!f.Stackable && p.HasFeat(f.Name)) return false;
            if (f.MaxStacks > 0 && p.GetFeatStacks(f.Name) >= f.MaxStacks) return false;
            return true;
        }).ToList();

        for (int i = 0; i < avail.Count; i++)
        {
            string pre = avail[i].Prerequisite != null ? $" (Req: {avail[i].Prerequisite})" : "";
            string stk = avail[i].Stackable ? " [stackable]" : "";
            Console.WriteLine($"  [{i + 1}] {avail[i].Name}{pre}{stk}");
            Console.WriteLine($"       {avail[i].Desc}");
        }

        Console.Write($"{p.Name} — select feat (1-{avail.Count}): ");
        if (int.TryParse(GameIO.ReadLine()?.Trim(), out int fi) && fi >= 1 && fi <= avail.Count)
        {
            var f = avail[fi - 1];
            p.AddFeat(f.Name);
            Console.WriteLine($"✓ Feat gained: {f.Name}");
            p.PendingFeats--;
            if (f.Name == "Toughness")
            {
                int extra = p.MaxHP;
                p.MaxHP *= 2;
                p.HP += extra;
                Console.WriteLine($"  Max HP doubled to {p.MaxHP}!");
            }
            if (f.Name == "Elemental")
            {
                Console.WriteLine("  Choose element: [1]holy  [2]negative  [3]air  [4]fire  [5]lightning  [6]frost");
                Console.Write("  Element: ");
                string el = (GameIO.ReadLine() ?? "").Trim().ToLower();
                p.ElementalFocus = el switch { "1" or "holy" => "holy", "2" or "negative" => "negative", "3" or "air" => "air", "4" or "fire" => "fire", "5" or "lightning" => "lightning", "6" or "frost" => "frost", _ => "air" };
                Console.WriteLine($"  Elemental focus: {p.ElementalFocus}!");
            }
            if (f.Name is "Cantrips" or "Necromancer" or "Divination" or "Advanced Cantrips" or "Lich Bound")
            {
                var newSpells = f.Name switch
                {
                    "Cantrips" => new[] { "Magic Hand", "Invisibility", "Phantom Image" },
                    "Necromancer" => new[] { "Negative Touch", "Raise Dead" },
                    "Lich Bound" => new[] { "Life Drain", "FrostBurn", "Lich Touch" },
                    "Divination" => new[] { "True Sight" },
                    "Advanced Cantrips" => new[] { "Enlarge", "Mage Shield", "Spellweave Armor" },
                    _ => Array.Empty<string>()
                };
                foreach (var s in newSpells) if (!p.KnownSpells.Contains(s)) { p.KnownSpells.Add(s); Console.WriteLine($"  Learned spell: {s}"); }
            }

            // Ability feats grant the matching resource pool and starting kit
            if (Player.PrayerFeats.Contains(f.Name))
            {
                if (p.PrayerUses <= 0) { p.PrayerUses = p.MaxPrayerUses(); Console.WriteLine($"  You can now pray! ({p.PrayerUses} prayers, reset on rest)"); }
                if (p.HeldWeapon != "Mace" && p.SecondaryWeapon != "Mace")
                {
                    if (p.HeldWeapon == null) { p.HeldWeapon = "Mace"; Console.WriteLine("  You receive a Mace (2d4 non-lethal)."); }
                    else if (p.SecondaryWeapon == null) { p.SecondaryWeapon = "Mace"; Console.WriteLine("  You receive a Mace (2d4 non-lethal, stowed)."); }
                }
            }
            if (Player.SongFeats.Contains(f.Name))
            {
                if (p.SongTokens <= 0 && p.CharacterType != "Musician") { p.SongTokens = p.MaxSongTokens(); Console.WriteLine($"  You can now play songs! ({p.SongTokens} tokens, reset on rest)"); }
                if (p.MusicInstrument == "")
                {
                    var insts = new[] { "Guitar", "Flute", "Violin", "Drum", "Cello", "Trumpet",
                                        "Saxophone", "Bagpipes", "Accordion", "Tambourine", "Harmonica", "Bells" };
                    Console.WriteLine("  Pick your instrument: " + string.Join(", ", insts.Select((s2, i2) => $"[{i2 + 1}]{s2}")));
                    Console.Write("  Choice (1-12 or name): ");
                    string ir = (GameIO.ReadLine() ?? "").Trim();
                    string pick = "Guitar";
                    if (int.TryParse(ir, out int ii2) && ii2 >= 1 && ii2 <= insts.Length) pick = insts[ii2 - 1];
                    else { var m2 = insts.FirstOrDefault(x => x.StartsWith(ir, StringComparison.OrdinalIgnoreCase)); if (m2 != null) pick = m2; }
                    p.MusicInstrument = pick;
                    Console.WriteLine($"  Instrument: {pick}!");
                }
            }
            if (f.Name == "Vow of Silence")
            {
                p.ChiUses = p.MaxChiUses();
                Console.WriteLine($"  You take the vow and become a monk. Chi: {p.ChiUses} (reset on rest).");
                // The vow binds you to monk weapons — stow anything else.
                if (p.HeldWeapon != null && !p.IsMonkWeapon(p.HeldWeapon))
                { Console.WriteLine($"  Your vow forbids the {p.HeldWeapon} — you set it aside and fight unarmed."); p.HeldWeapon = null; }
                if (p.SecondaryWeapon != null && !p.IsMonkWeapon(p.SecondaryWeapon))
                { Console.WriteLine($"  Your vow forbids the {p.SecondaryWeapon} — set aside."); p.SecondaryWeapon = null; }
            }
            if (Player.SpellFeats.Contains(f.Name))
            {
                if (p.SpellUses <= 0) { p.SpellUses = p.MaxSpellUses(); Console.WriteLine($"  You can now cast spells! ({p.SpellUses} casts, reset on rest)"); }
                if (p.HeldWeapon != "Wand" && p.SecondaryWeapon != "Wand")
                {
                    if (p.HeldWeapon == null) { p.HeldWeapon = "Wand"; Console.WriteLine("  You receive a Wand (3-4 dmg, 20-50ft)."); }
                    else if (p.SecondaryWeapon == null) { p.SecondaryWeapon = "Wand"; Console.WriteLine("  You receive a Wand (3-4 dmg, 20-50ft, stowed)."); }
                }
            }
            if (f.Name == "Gatherer" && p.GetFeatStacks("Gatherer") == 1)
            {
                if (p.HeldWeapon == null) p.HeldWeapon = "Pickaxe";
                else if (p.SecondaryWeapon == null) p.SecondaryWeapon = "Pickaxe";
                Console.WriteLine("  You receive a pickaxe and an axe — the wilds are yours to strip.");
            }
            if (f.Name == "Hunter" && p.GetFeatStacks("Hunter") == 1)
                Console.WriteLine("  You can now hunt at each wave's end (carry a dagger or knife to skin).");
            if (f.Name == "Alchemist")
                Console.WriteLine("  You can now brew potions in combat: boost, healing, poison, and restoration.");
            if (f.Name == "Weapon Specialist")
            {
                Console.Write("  Specialize in which weapon (exact name, e.g. Long Sword): ");
                string spec = (GameIO.ReadLine() ?? "").Trim();
                if (spec.Length == 0) spec = p.HeldWeapon ?? "Unarmed";
                p.WeaponSpec[spec] = p.WeaponSpec.GetValueOrDefault(spec) + 1;
                Console.WriteLine($"  Weapon Specialist: {spec} x{p.WeaponSpec[spec]} (+1d4 atk & dmg per stack).");
            }
        }
        else Console.WriteLine("Invalid. Try again.");
    }
}

// The full point-buy catalogue, ordered by price → type → name (the display
// sorts it, so entries can be listed here in any order).
// ══ POINT BUY ═════════════════════════════════════════════════════════════
// A catalogue, not a switch. Each BuyOpt knows its cost, its type, and who's
// allowed to see it. SpendStatPoints filters to affordable + usable, sorts
// price → type → name, and numbers what survives — so the option numbers
// shift per character. Never hardcode them.
// To add an upgrade: one Add(...) line below, plus the field on Player, plus
// save/load. Then wire the field somewhere it actually changes a roll.

List<BuyOpt> BuyCatalogue()
{
    bool Any(Player p) => true;
    bool Pray(Player p) => p.CanPray;
    bool Sing(Player p) => p.CanSing;
    bool Cast(Player p) => p.KnownSpells.Any() || Player.SpellFeats.Any(p.HasFeat) || p.CharacterType == "Mage";
    bool Craft(Player p) => p.CharacterType == "Artisan" || p.HasFeat("Gatherer") || p.HasFeat("Hunter/Gatherer") || p.HasFeat("Magic Crafting");
    bool Arrows(Player p) => p.CharacterType == "Archer" || p.CharacterType == "Artisan";
    bool Alch(Player p) => p.HasFeat("Alchemist");
    bool Gnome(Player p) => p.Race.Contains("Gnome");
    bool MagicCraft(Player p) => p.HasFeat("Magic Crafting");

    var L = new List<BuyOpt>();
    void Add(int c, string t, string n, Func<Player, bool> show, Action<Player> ap) => L.Add(new BuyOpt(c, t, n, show, ap));

    // ── 1 point: max (high end) ──
    Add(1, "Melee",    "Max Attack",         Any, p => p.MaxAttack++);
    Add(1, "Melee",    "Max Grapple",        Any, p => p.MaxGrapple++);
    Add(1, "Melee",    "Max Grapple Damage", Any, p => p.MaxGrappleDmg++);
    Add(1, "Melee",    "Max Limb Break",     Any, p => p.MaxLimbBreak++);
    Add(1, "Melee",    "Max Melee Damage",   Any, p => p.MaxDamage++);
    Add(1, "Melee",    "Max Power Attack",   Any, p => p.MaxPowerAtk++);
    Add(1, "Melee",    "Max Disarm Chance",  Any, p => p.DisarmMaxBonus++);
    Add(1, "Defense",  "Max Block",          Any, p => p.MaxBlock++);
    Add(1, "Defense",  "Max Dodge",          Any, p => p.MaxDodge++);
    Add(1, "Defense",  "Max Parry",          Any, p => p.MaxParry++);
    Add(1, "Vitality", "Max HP",             Any, p => p.MaxHP++);
    Add(1, "Movement", "Max Move",           Any, p => p.MaxMovement++);
    Add(1, "Movement", "Max Sprint (+2)",    Any, p => p.SprintMaxBonus += 2);
    Add(1, "Movement", "Max Run Away",       Any, p => p.RunAwayMaxBonus++);
    Add(1, "Ranged",   "Max Ranged Attack",  Any, p => p.MaxRangedAtk++);
    Add(1, "Ranged",   "Max Ranged Damage",  Any, p => p.MaxRangedDmgBonus++);
    Add(1, "Ranged",   "Ranged Range +5ft",  Any, p => p.RangedRangeBonusFt += 5);
    Add(1, "Spell",    "Max Spell Attack",   Cast, p => p.MaxSpellAtk++);
    Add(1, "Spell",    "Max Spell Damage",   Cast, p => p.MaxSpellDmgBonus++);
    Add(1, "Spell",    "Max Spell Duration", Cast, p => p.SpellDurMax++);
    Add(1, "Spell",    "Max Spell Roll Bonus", Cast, p => p.SpellRollMaxBonus++);
    Add(1, "Spell",    "Spell Range +5ft",   Cast, p => p.SpellRangeBonusFt += 5);
    Add(1, "Prayer",   "Max Prayer Ability Bonus", Pray, p => p.PrayerAbilMaxBonus++);
    Add(1, "Prayer",   "Max Prayer Damage",  Pray, p => p.PrayerDmgMaxBonus++);
    Add(1, "Prayer",   "Max Prayer Healing", Pray, p => p.PrayerHealMaxBonus++);
    Add(1, "Prayer",   "Max Prayer Roll vs HP", Pray, p => p.PrayerVsHpMax++);
    Add(1, "Prayer",   "Max Prayer Turns",   Pray, p => p.PrayerTurnsMax++);
    Add(1, "Prayer",   "Prayer Range +5ft",  Pray, p => p.PrayerRangeBonusFt += 5);
    Add(1, "Song",     "Max Song Duration",  Sing, p => p.SongDurMax++);
    Add(1, "Song",     "Max Song Fear vs HP", Sing, p => p.SongFearMax++);
    Add(1, "Song",     "Max Song Heal",      Sing, p => p.SongHealMax++);
    Add(1, "Song",     "Max Song Roll Bonus", Sing, p => p.MaxBardSong++);
    Add(1, "Song",     "Song Range +5ft",    Sing, p => p.SongRangeBonusFt += 5);
    Add(1, "Potion",   "Max Potion Bonus",   Alch, p => p.PotionMaxBonus++);
    Add(1, "Potion",   "Max Potion Duration", Alch, p => p.PotionDurMaxBonus++);
    Add(1, "Potion",   "Max Potion Heal",    Any, p => p.MaxPotionHeal++);
    Add(1, "Craft",    "Max Arrows Crafted (+2)", Arrows, p => p.ArrowsCraftedMax += 2);
    Add(1, "Craft",    "Max Crafted Armor Bonus", Craft, p => p.CraftedArmorMaxBonus++);
    Add(1, "Craft",    "Max Goods Collected", Craft, p => p.GoodsCollectedMax++);
    Add(1, "Special",  "Max Money Collected", Any, p => p.MoneyMaxBonus++);

    // ── 2 points: base (low end) ──
    Add(2, "Melee",    "Base Attack",        Any, p => p.MinAttack++);
    Add(2, "Melee",    "Base Grapple",       Any, p => p.MinGrapple++);
    Add(2, "Melee",    "Base Grapple Damage", Any, p => p.MinGrappleDmg++);
    Add(2, "Melee",    "Base Limb Break",    Any, p => p.MinLimbBreak++);
    Add(2, "Melee",    "Base Melee Damage",  Any, p => p.MinDamage++);
    Add(2, "Melee",    "Base Power Attack",  Any, p => p.MinPowerAtk++);
    Add(2, "Melee",    "Base Disarm Chance", Any, p => p.DisarmBonus++);
    Add(2, "Defense",  "Base Block",         Any, p => p.MinBlock++);
    Add(2, "Defense",  "Base Dodge",         Any, p => p.MinDodge++);
    Add(2, "Defense",  "Base Parry",         Any, p => p.MinParry++);
    Add(2, "Vitality", "Max HP (+3)",        Any, p => p.MaxHP += 3);
    Add(2, "Movement", "Base Move",          Any, p => p.MinMovement++);
    Add(2, "Movement", "Base Sprint (+2)",   Any, p => p.SprintBonus += 2);
    Add(2, "Movement", "Base Run Away",      Any, p => p.RunAwayBonus++);
    Add(2, "Ranged",   "Base Ranged Attack", Any, p => p.MinRangedAtk++);
    Add(2, "Ranged",   "Base Ranged Damage", Any, p => p.MinRangedDmgBonus++);
    Add(2, "Spell",    "Base Spell Attack",  Cast, p => p.MinSpellAtk++);
    Add(2, "Spell",    "Base Spell Damage",  Cast, p => p.MinSpellDmgBonus++);
    Add(2, "Spell",    "Base Spell Roll Bonus", Cast, p => p.SpellRollBonus++);
    Add(2, "Spell",    "Spell Uses +1",      Cast, p => { p.BonusSpellUses++; p.SpellUses++; });
    Add(2, "Prayer",   "Base Prayer Ability Bonus", Pray, p => p.PrayerAbilBonus++);
    Add(2, "Prayer",   "Base Prayer Damage", Pray, p => p.PrayerDmgBonus++);
    Add(2, "Prayer",   "Base Prayer Healing", Pray, p => p.PrayerHealBonus++);
    Add(2, "Prayer",   "Base Prayer Roll vs HP", Pray, p => p.PrayerVsHp++);
    Add(2, "Prayer",   "Base Prayer Turns",  Pray, p => p.PrayerDurBonus++);
    Add(2, "Prayer",   "Prayer Uses +1",     Pray, p => { p.BonusPrayerUses++; p.PrayerUses++; });
    Add(2, "Song",     "Base Song Duration", Sing, p => p.SongDurBonus++);
    Add(2, "Song",     "Base Song Fear vs HP", Sing, p => p.SongFear++);
    Add(2, "Song",     "Base Song Heal",     Sing, p => p.SongHeal++);
    Add(2, "Song",     "Base Song Roll Bonus", Sing, p => p.MinBardSong++);
    Add(2, "Song",     "Song Uses +1",       Sing, p => { p.BonusSongUses++; p.SongTokens++; });
    Add(2, "Potion",   "Base Potion Bonus",  Alch, p => p.PotionBonus++);
    Add(2, "Potion",   "Base Potion Duration", Alch, p => p.PotionDurBonus++);
    Add(2, "Potion",   "Base Potion Heal",   Any, p => p.MinPotionHeal++);
    Add(2, "Craft",    "Base Arrows Crafted (+2)", Arrows, p => p.ArrowsCrafted += 2);
    Add(2, "Craft",    "Base Goods Collected", Craft, p => p.GoodsCollected++);
    Add(2, "Special",  "Base Money Collected", Any, p => p.MoneyBonus++);

    // ── 3 points ──
    Add(3, "Vitality", "Hit Points (+5)",    Any, p => p.MaxHP += 5);
    Add(3, "Craft",    "Base Crafted Armor Bonus", Craft, p => p.CraftedArmorBonus++);
    Add(3, "Craft",    "Base Crafted Weapon Damage", Craft, p => p.CraftedWeaponDmg++);
    Add(3, "Defense",  "Shield Bonus",       Any, p => p.ShieldBonus++);
    Add(3, "Defense",  "Crafted Shield Reflect +5%", MagicCraft, p => p.ShieldReflectPct += 5);
    Add(3, "Defense",  "Crafted Armor/Robe Absorb +10%", MagicCraft, p => p.ArmorAbsorbPct += 10);
    Add(3, "Spell",    "Gnome Spell Absorb +5%", Gnome, p => p.RaceAbsorbPct += 5);

    // ── 5 points: core traits (each also grants what it gave at creation) ──
    Add(5, "Core Trait", "Agility +1",       Any, p => { p.Agility++; if (p.Agility % 2 == 0) p.AdditionalActions++; });
    Add(5, "Core Trait", "Charisma +1",      Any, p => p.Charisma++);
    Add(5, "Core Trait", "Constitution +1",  Any, p => { p.Constitution++; p.MaxHP++; p.HP++; p.RagePoints++; });
    Add(5, "Core Trait", "Dexterity +1",     Any, p => p.Dexterity++);
    Add(5, "Core Trait", "Intelligence +1",  Any, p => p.Intelligence++);
    Add(5, "Core Trait", "Smarts +1",        Any, p => { p.Smarts++; p.DuelistPoints++; });
    Add(5, "Core Trait", "Strength +1",      Any, p => p.Strength++);
    Add(5, "Core Trait", "Wisdom +1",        Any, p => p.Wisdom++);
    return L;
}

void SpendStatPoints(Player p)
{
    var all = BuyCatalogue();
    while (p.SavedStatPoints > 0)
    {
        // Only what they can afford AND can actually use, sorted price → type → name
        var menu = all.Where(o => o.Show(p) && o.Cost <= p.SavedStatPoints)
                      .OrderBy(o => o.Cost).ThenBy(o => o.Type).ThenBy(o => o.Name).ToList();
        Console.WriteLine($"\n═══ STAT POINTS: {p.SavedStatPoints} available ═══");
        int lastCost = -1; string lastType = "";
        for (int i = 0; i < menu.Count; i++)
        {
            var o = menu[i];
            if (o.Cost != lastCost) { Console.WriteLine($"  ── {o.Cost} point{(o.Cost > 1 ? "s" : "")} ──"); lastCost = o.Cost; lastType = ""; }
            if (o.Type != lastType) { Console.WriteLine($"   {o.Type}:"); lastType = o.Type; }
            Console.WriteLine($"      [{i + 1,2}] {o.Name}");
        }
        // Special purchases keep their own costs
        if (p.SavedStatPoints >= 3) Console.WriteLine($"  ── 3 points ──\n      [{menu.Count + 1}] Gear point");
        if (p.SavedStatPoints >= 4) Console.WriteLine($"  ── 4 points ──\n      [{menu.Count + 2}] Extra action per turn      [{menu.Count + 3}] Pick a feat");
        Console.Write("  Choice ([S]ave the rest for later): ");
        string raw = (GameIO.ReadLine() ?? "s").Trim().ToLower();
        if (raw is "s" or "save" or "") break;
        if (!int.TryParse(raw, out int ch) || ch < 1) { Console.WriteLine("  Invalid."); continue; }

        if (ch <= menu.Count)
        {
            var o = menu[ch - 1];
            o.Apply(p);
            p.SavedStatPoints -= o.Cost;
            Console.WriteLine($"  ✓ {o.Name} ({o.Cost} pt) — {p.SavedStatPoints} left.");
        }
        else if (ch == menu.Count + 1 && p.SavedStatPoints >= 3)
        { p.GearPointsAvailable++; p.SavedStatPoints -= 3; Console.WriteLine("  Gear point gained!"); SpendGearPoints(p); }
        else if (ch == menu.Count + 2 && p.SavedStatPoints >= 4)
        { p.AdditionalActions++; p.SavedStatPoints -= 4; Console.WriteLine($"  +1 action/turn! Bonus actions: {p.AdditionalActions}"); }
        else if (ch == menu.Count + 3 && p.SavedStatPoints >= 4)
        { p.SavedStatPoints -= 4; p.PendingFeats++; SelectFeats(p); }
        else Console.WriteLine("  Invalid choice or insufficient points.");
    }
    if (p.SavedStatPoints > 0) Console.WriteLine($"  Saving {p.SavedStatPoints} point(s) for later.");
}


void SpendGearPoints(Player p)
{
    while (p.GearPointsAvailable > 0)
    {
        Console.WriteLine($"\n═══ GEAR POINTS: {p.GearPointsAvailable} available (each item up to 3×) ═══");
        var items = new (string Key, string Desc, Action<Player> Apply)[]
        {
            ("Armor",         "-1 incoming damage per point",                           pl => { pl.ArmorDamageReduction++; Console.WriteLine($"  Armor: -{pl.ArmorDamageReduction} incoming dmg"); }),
            ("Staff",         "Main damage min and max +1 (non-lethal style)",          pl => { pl.MinDamage++; pl.MaxDamage++; Console.WriteLine($"  Staff: dmg {pl.MinDamage}-{pl.MaxDamage}"); }),
            ("Blade",         "Melee damage min and max +1",                            pl => { pl.MinDamage++; pl.MaxDamage++; Console.WriteLine($"  Blade: dmg {pl.MinDamage}-{pl.MaxDamage}"); }),
            ("Brass Ringlets","Non-weapon (kick/headbutt) damage bonus +1",             pl => { pl.RingletBonus++; Console.WriteLine($"  Brass Ringlets: unarmed bonus +{pl.RingletBonus}"); }),
            ("Instrument",    "Bard song min and max +1",                               pl => { pl.MinBardSong++; pl.MaxBardSong++; Console.WriteLine($"  Instrument: bard song {pl.MinBardSong}-{pl.MaxBardSong}"); }),
            ("Silk",          "Dodge min and max +1",                                   pl => { pl.MinDodge++; pl.MaxDodge++; Console.WriteLine($"  Silk: dodge {pl.MinDodge}-{pl.MaxDodge}"); }),
            ("Arm Band",      "Block min and max +1",                                   pl => { pl.MinBlock++; pl.MaxBlock++; Console.WriteLine($"  Arm Band: block {pl.MinBlock}-{pl.MaxBlock}"); }),
            ("Strength Band", "Grapple and grapple damage min and max +1",              pl => { pl.MinGrapple++; pl.MaxGrapple++; pl.MinGrappleDmg++; pl.MaxGrappleDmg++; Console.WriteLine($"  Strength Band: grapple {pl.MinGrapple}-{pl.MaxGrapple}, dmg {pl.MinGrappleDmg}-{pl.MaxGrappleDmg}"); }),
            ("Fencing",       "Parry min and max +1",                                   pl => { pl.MinParry++; pl.MaxParry++; Console.WriteLine($"  Fencing: parry {pl.MinParry}-{pl.MaxParry}"); }),
            ("Grimoire",      "Learn one spell (Fire Blast, Chain Lightning, Frost Burst)", pl => LearnSpell(pl)),
            ("Alchemy",       "Potion healing min and max +1",                          pl => { pl.MinPotionHeal++; pl.MaxPotionHeal++; Console.WriteLine($"  Alchemy: heal {pl.MinPotionHeal}-{pl.MaxPotionHeal}"); }),
        };
        var avail = items.Where(it => p.GearCounts.GetValueOrDefault(it.Key, 0) < 3).ToList();
        for (int i = 0; i < avail.Count; i++)
        {
            int taken = p.GearCounts.GetValueOrDefault(avail[i].Key, 0);
            Console.WriteLine($"  [{i+1}] {avail[i].Key} ({taken}/3) — {avail[i].Desc}");
        }
        Console.Write("  Choose: ");
        if (int.TryParse(GameIO.ReadLine()?.Trim(), out int g) && g >= 1 && g <= avail.Count)
        {
            var it = avail[g - 1];
            it.Apply(p);
            p.GearCounts[it.Key] = p.GearCounts.GetValueOrDefault(it.Key, 0) + 1;
            p.GearPointsAvailable--;
        }
        else Console.WriteLine("  Invalid.");
    }
}

void LearnSpell(Player p)
{
    var allSpells = new[] { "Fire Blast", "Chain Lightning", "Frost Burst" };
    var available = allSpells.Where(s => !p.KnownSpells.Contains(s)).ToList();
    if (!available.Any()) { Console.WriteLine("  You already know all spells!"); return; }
    Console.WriteLine("  Available spells:");
    Console.WriteLine("    Fire Blast       — 4-12 dmg to 2-4 enemies; burning 1-4/action for 4-8 actions");
    Console.WriteLine("    Chain Lightning  — 3-6 dmg to 5-11 enemies/action; self-damage if >3 actions");
    Console.WriteLine("    Frost Burst      — 2-8 dmg to 2-8 enemies; -2 to -8 on their rolls for 2-6 actions");
    for (int i = 0; i < available.Count; i++) Console.WriteLine($"  [{i+1}] {available[i]}");
    Console.Write("  Learn: ");
    if (int.TryParse(GameIO.ReadLine()?.Trim(), out int s) && s >= 1 && s <= available.Count)
    { p.KnownSpells.Add(available[s - 1]); Console.WriteLine($"  ✓ Learned: {available[s - 1]}!"); }
    else Console.WriteLine("  Invalid.");
}

void AskName(Player p)
{
    Console.Write("\nFirst name (or Enter for 'The Lone Warrior'): ");
    string first = (GameIO.ReadLine() ?? "").Trim();
    if (string.IsNullOrEmpty(first)) { SelectRace(p); return; }

    Console.Write("Middle name (or Enter to skip): ");
    string middle = (GameIO.ReadLine() ?? "").Trim();

    Console.Write("Last name: ");
    string last = (GameIO.ReadLine() ?? "").Trim();

    p.Name = string.IsNullOrEmpty(middle)
        ? $"{first} {last}".Trim()
        : $"{first} {middle} {last}".Trim();

    SelectRace(p);
}

void SelectRace(Player p)
{
    // Listed by size (small → medium → large), then kin-group, then alphabetically,
    // so relatives like the three elves always sit together.
    var raceInfo = new (string Name, string Kin, string Blurb)[]
    {
        ("Gem Gnome",          "Gnome",     "25% magic absorb, +2 spell hit; frail, takes more dmg"),
        ("Glass Gnome",        "Gnome",     "+2 spell/prayer power & hit; takes more damage"),
        ("Goblin",             "Goblinoid", "quick (+1 action), sharp attacks; frail & weak hits"),
        ("Hobgoblin",          "Goblinoid", "+1 melee/ranged, tough; poor caster, clumsy defense"),
        ("Brave Minds Hobbit", "Hobbit",    "+1 defenses & attacks, fearless; deals less damage"),
        ("Light-Foot Hobbit",  "Hobbit",    "+2 dodge/block/parry, nimble, -1 dmg taken"),
        ("Iron Dwarf",         "Dwarf",     "-2 dmg taken, +2 attacks, free combat feat"),
        ("Stone Dwarf",        "Dwarf",     "double HP, -2 dmg taken, +1 rage; slow & poor caster"),
        ("Moon Elf",           "Elf",       "spell/prayer power & attacks up; frail (-2 HP/dmg)"),
        ("Sun Elf",            "Elf",       "+2 attacks & healing; frail (-2 HP/dmg)"),
        ("Wood Elf",           "Elf",       "+2 all attacks & damage, fast; frail, poor grappler"),
        ("Human",              "Human",     "+6 stat points, +1 action, TWO bonus feats"),
        ("Orc",                "Orc",       "+2 melee/throw dmg, +2 HP, +1 rage; poor caster"),
        ("Troll",              "Troll",     "regen 2/turn; FRENZY at low HP; poor at magic"),
        ("Giant",              "Giant",     "+2 melee dmg, +4 HP, +1 move, free Giant's Strength; clumsy vs smaller foes"),
        ("Ogre",               "Ogre",      "+2 dmg, double HP, half dodge, -1 move, -2 dmg taken, free Giant's Strength"),
    };
    var sorted = raceInfo.OrderBy(r => SizeRules.Of(r.Name)).ThenBy(r => r.Kin).ThenBy(r => r.Name).ToArray();
    var races = sorted.Select(r => r.Name).ToArray();

    Console.WriteLine("\nChoose your race:");
    int lastSize = -1;
    for (int i = 0; i < sorted.Length; i++)
    {
        int sz = SizeRules.Of(sorted[i].Name);
        if (sz != lastSize)
        {
            Console.WriteLine($"  ── {(sz == 0 ? "Small" : sz == 2 ? "Large" : "Medium")} ──");
            lastSize = sz;
        }
        Console.WriteLine($"  [{i + 1,2}] {sorted[i].Name,-19} — {sorted[i].Blurb}");
    }
    Console.WriteLine("  (Size matters: small races get attack/dodge bonuses vs bigger foes; see race notes)");
    Console.Write($"  Choice (1-{races.Length} or name): ");
    string raw = (GameIO.ReadLine() ?? "").Trim();
    string chosen = "Human";
    if (int.TryParse(raw, out int ridx) && ridx >= 1 && ridx <= races.Length)
        chosen = races[ridx - 1];
    else
    {
        var match = races.FirstOrDefault(r => r.StartsWith(raw, StringComparison.OrdinalIgnoreCase));
        if (match != null) chosen = match;
    }
    p.Race = chosen;
    Console.WriteLine($"  You are a {chosen}!");

    // Class selection first so Stone Dwarf can double the class-set HP
    SelectCharacterType(p);

    // Helpers to apply symmetric (min+max) bonuses cleanly. Every roll stat is
    // clamped so a racial penalty can never drive a roll to zero/negative or
    // collapse min==max into a fixed result.
    void Rl(ref int mn, ref int mx, int d)
    { mn = Math.Max(1, mn + d); mx = Math.Max(mn, mx + d); }
    void Atk(int d)   { Rl(ref p.MinAttack, ref p.MaxAttack, d); }
    void Dmg(int d)   { Rl(ref p.MinDamage, ref p.MaxDamage, d); }
    void Dodge(int d) { Rl(ref p.MinDodge, ref p.MaxDodge, d); }
    void Block(int d) { Rl(ref p.MinBlock, ref p.MaxBlock, d); }
    void Parry(int d) { Rl(ref p.MinParry, ref p.MaxParry, d); }
    void Grap(int d)  { Rl(ref p.MinGrapple, ref p.MaxGrapple, d); }
    void RAtk(int d)  { Rl(ref p.MinRangedAtk, ref p.MaxRangedAtk, d); }
    // Ranged damage is a *bonus* (base 0/0), so it may sit negative by design;
    // the bow/throw sites floor the resulting damage at 1.
    void RDmg(int d)  { p.MinRangedDmgBonus += d; p.MaxRangedDmgBonus += d; }
    void Hp(int d)    { p.MaxHP = Math.Max(1, p.MaxHP + d); p.HP = p.MaxHP; }

    switch (chosen)
    {
        case "Moon Elf":
            p.SpellDamageBonus += 2; p.PrayerHealBonus += 2; p.SpellAttackBonus += 2;
            Atk(2); RAtk(2); Dodge(1); Dmg(-2); Hp(-2);
            Console.WriteLine("  [Race] Moon Elf: +2 spell/prayer damage & spell attack, +2 melee/ranged attack, +1 dodge (+1 vs large), +1 spell/prayer/song duration; -2 melee damage, -2 HP.");
            break;
        case "Sun Elf":
            Atk(2); RAtk(2); Dodge(1); p.PrayerHealBonus += 2; Dmg(-2); Hp(-2);
            Console.WriteLine("  [Race] Sun Elf: +2 melee/ranged attack, +1 dodge (+1 vs large), +2 all healing, +1 boost abilities; -2 melee damage, -2 HP.");
            break;
        case "Wood Elf":
            Atk(2); RAtk(2); p.SpellAttackBonus += 2; Dmg(2); RDmg(2); Dodge(1);
            p.MovementBonus += 1; Hp(-2); Grap(-2);
            Console.WriteLine("  [Race] Wood Elf: +2 all attacks, +2 melee/ranged damage, +1 dodge (+1 vs large), +1 move/+2 sprint (no sprint penalty); -2 HP, -2 grapple.");
            break;
        case "Human":
        {
            p.SavedStatPoints += 6;
            p.AdditionalActions += 1;
            Console.WriteLine("  [Race] Human: +6 starting stat points, +1 action, and TWO bonus feats.");
            for (int hf = 0; hf < 2; hf++)
            {
                var available = FeatDef.All
                    .Where(f => f.Prerequisite == null && !p.Feats.Contains(f.Name)).ToList();
                Console.WriteLine($"  Human bonus feat {hf + 1} of 2:");
                for (int fi = 0; fi < available.Count; fi++)
                    Console.WriteLine($"    [{fi + 1}] {available[fi].Name} — {available[fi].Desc}");
                Console.Write($"  Choice (1-{available.Count}): ");
                if (int.TryParse((GameIO.ReadLine() ?? "").Trim(), out int fc) && fc >= 1 && fc <= available.Count)
                {
                    p.AddFeat(available[fc - 1].Name);
                    Console.WriteLine($"  Gained feat: {available[fc - 1].Name}!");
                }
            }
            break;
        }
        case "Stone Dwarf":
            p.MaxHP *= 2; p.HP = p.MaxHP;
            p.ArmorDamageReduction += 2; p.RagePoints += 1;
            Dmg(1); Dodge(-1); Block(-1); Parry(-1); p.MovementBonus -= 1;
            p.SpellDamageBonus -= 1; p.PrayerHealBonus -= 1; RAtk(-1);
            Console.WriteLine($"  [Race] Stone Dwarf: HP doubled ({p.MaxHP}), -2 damage taken, +1 rage, +1 melee/throw damage; -1 dodge/block/parry, -1 move (+2 sprint, double penalty), -1 spell/prayer, -1 ranged attack.");
            break;
        case "Iron Dwarf":
            p.ArmorDamageReduction += 2; Atk(2); RAtk(2);
            Block(1); Dodge(-1); Parry(-1); Grap(1); p.MinGrappleDmg += 1; p.MaxGrappleDmg += 1;
            p.MovementBonus -= 1;
            Console.WriteLine("  [Race] Iron Dwarf: -2 damage taken, +2 melee/ranged attack, +1 block/grapple; -1 dodge/parry, -1 move/-2 sprint. Free non-magic feat:");
            {
                var combatFeats = FeatDef.All.Where(f => f.Prerequisite == null && !p.Feats.Contains(f.Name)
                    && !Player.PrayerFeats.Contains(f.Name) && !Player.SongFeats.Contains(f.Name)
                    && !Player.SpellFeats.Contains(f.Name) && f.Name != "Alchemist").ToList();
                for (int fi = 0; fi < combatFeats.Count; fi++)
                    Console.WriteLine($"    [{fi + 1}] {combatFeats[fi].Name} — {combatFeats[fi].Desc}");
                Console.Write($"  Choice (1-{combatFeats.Count}): ");
                if (int.TryParse((GameIO.ReadLine() ?? "").Trim(), out int ic) && ic >= 1 && ic <= combatFeats.Count)
                { p.AddFeat(combatFeats[ic - 1].Name); Console.WriteLine($"  Gained feat: {combatFeats[ic - 1].Name}!"); }
            }
            break;
        case "Light-Foot Hobbit":
            Dodge(2); Block(2); Parry(2); Atk(1); RAtk(1);
            p.MovementBonus += 1; p.ArmorDamageReduction += 1; Hp(-1);
            Console.WriteLine("  [Race] Light-Foot Hobbit: +2 dodge/block/parry (+1 dodge vs large), +1 melee/ranged attack, +1 move/+2 sprint, -1 damage taken; -1 HP.");
            break;
        case "Brave Minds Hobbit":
            Dodge(1); Block(1); Parry(1); Atk(1); RAtk(1); p.SpellAttackBonus += 1;
            p.ArmorDamageReduction += 1; Dmg(-2); RDmg(-2); Hp(-1);
            Console.WriteLine("  [Race] Brave Minds Hobbit: +1 dodge/block/parry & all attacks, immune to fear, -1 damage taken; -1 HP, -2 damage dealt.");
            break;
        case "Orc":
            Dmg(2); RDmg(2); Block(1); Parry(1); Hp(2); p.RagePoints += 1;
            Dodge(-1); Atk(-1); RAtk(-1); p.SpellAttackBonus -= 2; p.SpellDamageBonus -= 2; p.PrayerHealBonus -= 2;
            p.MovementBonus += 1;
            Console.WriteLine("  [Race] Orc: +2 melee/throw damage, +1 block/parry, +2 HP, +1 rage, +1 move/+1 sprint (no penalty); -1 dodge, -1 melee/ranged attack, -2 spell, -2 prayer healing.");
            break;
        case "Goblin":
            Dodge(1); Block(-1); Parry(-1); Atk(1); RAtk(1); p.SpellAttackBonus += 1;
            p.MovementBonus += 2; p.AdditionalActions += 1; Dmg(-1); RDmg(-1); Hp(-2);
            Console.WriteLine("  [Race] Goblin: +1 dodge, +1 melee/ranged & spell attack, +2 move/+4 sprint (double penalty), +1 action; -1 block/parry, -1 damage dealt, -2 HP.");
            break;
        case "Troll":
            p.RegenPerTurn = 2; Dmg(1); RDmg(1); p.SpellAttackBonus += 1;
            Block(-1); Parry(-1); Dodge(0);
            Console.WriteLine("  [Race] Troll: regen 2 HP/turn, +1 melee/ranged damage & throw/spell to-hit, +1 dodge vs spells, +1 sprint; -1 block/parry, -2 spell damage. FRENZY: rages at low HP (double damage, -2 to attacks & defenses).");
            break;
        case "Gem Gnome":
            p.MovementBonus += 1; p.SpellAttackBonus += 2;
            p.SpellDamageBonus += 1; p.PrayerHealBonus += 1; Hp(-2);
            p.ArmorDamageReduction -= 1;   // +1 damage taken (when armored)
            Console.WriteLine("  [Race] Gem Gnome: +1 move/+2 sprint, 25% spell/prayer absorption, +2 spell to-hit, +1 spell/song duration, +1 spell/prayer damage; -2 HP, +1 damage taken.");
            break;
        case "Glass Gnome":
            p.MovementBonus += 1; p.SpellDamageBonus += 2; p.PrayerHealBonus += 2; p.SpellAttackBonus += 2;
            p.ArmorDamageReduction -= 2;   // +2 damage taken (when armored)
            Console.WriteLine("  [Race] Glass Gnome: +1 move/+2 sprint, +2 spell/prayer damage, +2 spell to-hit, +1 spell/prayer/song/potion duration; +2 damage taken.");
            break;
        case "Hobgoblin":
            p.MovementBonus += 1; Hp(1); p.MinAttack += 1; p.MaxAttack += 1;
            Dmg(1); RAtk(1); RDmg(1); Dodge(-1); Block(-1); Parry(-1);
            p.SpellAttackBonus -= 1; p.PrayerHealBonus -= 2;
            Console.WriteLine("  [Race] Hobgoblin: +1 move/+2 sprint (double penalty), +1 HP, +1 melee attack & damage, +1 ranged attack & damage; -1 spell attack, -2 healing, -1 dodge/block/parry.");
            break;
        case "Ogre":
            p.MinDamage += 2;
            p.MaxDamage += 2;
            p.MaxHP *= 2;
            p.HP = p.MaxHP;
            p.MinDodge = Math.Max(1, p.MinDodge / 2);
            p.MaxDodge = Math.Max(1, p.MaxDodge / 2);
            p.MovementBonus -= 1;
            p.ArmorDamageReduction += 2;
            p.AdditionalActions -= 1;
            p.AddFeat("Giant's Strength");
            Console.WriteLine($"  [Race] Ogre: +2 damage ({p.MinDamage}-{p.MaxDamage}), HP doubled to {p.MaxHP}, dodge halved ({p.MinDodge}-{p.MaxDodge}), -1 movement, -1 action per turn.");
            Console.WriteLine("  [Race] Ogre tough skin: -2 incoming damage. +2/+3 dmg but -1/-2 attack and worse dodge vs medium/small foes.");
            Console.WriteLine("  [Race] Giant's Strength (free feat): wield the Ogre Club and two-handed weapons in one hand.");
            break;
        case "Giant":
            p.MinDamage += 2;
            p.MaxDamage += 2;
            p.MaxHP += 4;
            p.HP = p.MaxHP;
            p.MovementBonus += 1;
            p.AddFeat("Giant's Strength");
            Console.WriteLine($"  [Race] Giant: +2 melee damage ({p.MinDamage}-{p.MaxDamage}), +4 HP ({p.MaxHP}), +1 movement.");
            Console.WriteLine("  [Race] Giant clumsiness: -2 dodge vs medium/small; -1 block/parry vs medium, -2 vs small.");
            Console.WriteLine("  [Race] Giant's Strength (free feat): wield the Ogre Club and two-handed weapons in one hand.");
            break;
    }

    // Small races are slighter of frame: -1 max HP (size trade-off for their agility bonuses)
    if (SizeRules.Of(p.Race) == 0)
    {
        p.MaxHP = Math.Max(1, p.MaxHP - 1);
        p.HP = Math.Min(p.HP, p.MaxHP);
        Console.WriteLine($"  [Size] Small frame: -1 max HP ({p.MaxHP}). Bonus attack/dodge vs bigger foes.");
    }

    ApplyRaceTraits(p);

    // Creation order: core traits → feats → point buy
    SelectCoreTraits(p);
    if (p.PendingFeats > 0) SelectFeats(p);
    Console.WriteLine($"\n  You have {p.SavedStatPoints} points to spend on starting stats!");
    SpendStatPoints(p);
    SelectAppearance(p);
}

// Core traits: 16 points, 1 point per +1, cap +4. Dropping a trait to -1/-2
// refunds points to spend elsewhere. Chosen before feats and the point buy.
void SelectCoreTraits(Player p)
{
    string[] names = { "Strength", "Dexterity", "Constitution", "Intelligence",
                       "Wisdom", "Smarts", "Charisma", "Agility" };
    string[] blurb = {
        "melee & grapple attack/damage; two-handed weapons",
        "ranged attack/damage, bows, dodge, movement, sprint, parry/disarm",
        "hit points, rages, resist fire/frost/damage-over-time",
        "spell uses, spell damage/attack/duration, wands, stat points",
        "prayer uses, prayer healing/damage, chi, non-lethal weapons, stat points",
        "duelist points, finesse weapons, parry/disarm, chi, spell range, stat points",
        "song uses, song bonus/duration/healing, fear & convert, song range",
        "extra actions, dodge, movement, sprint, extra attacks/abilities"
    };
    int Get(int i) => i switch { 0 => p.Strength, 1 => p.Dexterity, 2 => p.Constitution,
        3 => p.Intelligence, 4 => p.Wisdom, 5 => p.Smarts, 6 => p.Charisma, _ => p.Agility };
    void Set(int i, int v)
    {
        switch (i) { case 0: p.Strength = v; break; case 1: p.Dexterity = v; break;
            case 2: p.Constitution = v; break; case 3: p.Intelligence = v; break;
            case 4: p.Wisdom = v; break; case 5: p.Smarts = v; break;
            case 6: p.Charisma = v; break; default: p.Agility = v; break; }
    }

    while (true)
    {
        int spent = 0;
        for (int i = 0; i < 8; i++) spent += Get(i);
        int left = 16 - spent;
        Console.WriteLine($"\n═══ CORE TRAITS — {p.Name} ({p.CharacterType}) ═══");
        Console.WriteLine("  Each +1 costs 1 point (max +4). Drop a trait to -1 or -2 to get points back.");
        for (int i = 0; i < 8; i++)
            Console.WriteLine($"  [{i + 1}] {names[i],-13} {Get(i),2}   — {blurb[i]}");
        Console.WriteLine($"  Points left: {left}");
        Console.Write("  Pick a trait to set (1-8), or [9] done: ");
        string raw = (GameIO.ReadLine() ?? "").Trim();
        if (raw is "9" or "done" or "") { if (left >= 0) break; Console.WriteLine("  You're over budget — lower something first."); continue; }
        int ti;
        if (int.TryParse(raw, out int tn) && tn >= 1 && tn <= 8) ti = tn - 1;
        else { int f = Array.FindIndex(names, n => n.StartsWith(raw, StringComparison.OrdinalIgnoreCase)); if (f < 0) continue; ti = f; }

        Console.Write($"  Set {names[ti]} to (-2 to 4): ");
        if (!int.TryParse((GameIO.ReadLine() ?? "").Trim(), out int nv)) continue;
        nv = Math.Clamp(nv, -2, 4);
        int wouldSpend = spent - Get(ti) + nv;
        if (wouldSpend > 16) { Console.WriteLine($"  Not enough points (that would need {wouldSpend}/16)."); continue; }
        Set(ti, nv);
        Console.WriteLine($"  {names[ti]} → {nv}");
    }

    // Traits that fold into the character sheet right away
    p.MaxHP = Math.Max(1, p.MaxHP + p.Constitution);
    p.HP = p.MaxHP;
    p.SavedStatPoints += p.TraitStatPoints();          // Int + Wis + Smarts
    p.AdditionalActions += p.ExtraActionsFromAgility(); // +0.5 actions per Agility
    if (p.Smarts > 0) p.DuelistPoints += p.Smarts;      // +1 duelist point per Smarts
    p.RagePoints += p.Constitution;                     // +Constitution rages
    p.SpellDamageBonus += p.Intelligence;               // Int: spell damage/bonus
    p.SpellAttackBonus += p.Intelligence;               // Int: spell attack
    p.SpellDurBonus += p.Intelligence;                  // Int: spell duration
    p.PrayerHealBonus += p.Wisdom;                      // Wis: prayer healing/damage
    p.PrayerDurBonus += p.Wisdom;                       // Wis: prayer duration
    p.SongDurBonus += p.Charisma;                       // Cha: song duration
    if (p.Charisma != 0)                                // Cha: song bonus rolls
    { p.MinBardSong = Math.Max(1, p.MinBardSong + p.Charisma); p.MaxBardSong = Math.Max(p.MinBardSong, p.MaxBardSong + p.Charisma); }
    if (p.IsMonk) p.ChiUses = p.MaxChiUses();           // Martial Artists: chi now that Wis/Smarts are known
    Console.WriteLine($"  Traits locked in. HP {p.MaxHP}, +{p.TraitStatPoints()} stat points, " +
                      $"+{p.ExtraActionsFromAgility()} action(s)." +
                      (p.IsMonk ? $" Chi: {p.ChiUses}." : ""));
}

// Behavioral flags derived purely from race. Numeric stat bonuses live in
// SelectRace (baked into saved stats); these flags are re-derived on load too.
void ApplyRaceTraits(Player p)
{
    p.DodgeVsLarge = 0; p.SprintBonus = 0;
    p.NoSprintPenalty = false; p.DoubleSprintPenalty = false;
    p.RaceAbsorbPct = 0; p.FearImmune = false;
    p.SpellDurBonus = 0; p.PrayerDurBonus = 0; p.SongDurBonus = 0;
    p.RaceFrenzy = false;
    switch (p.Race)
    {
        case "Moon Elf":
            p.DodgeVsLarge = 1; p.SpellDurBonus = 1; p.PrayerDurBonus = 1; p.SongDurBonus = 1; break;
        case "Sun Elf":
            p.DodgeVsLarge = 1; break;
        case "Wood Elf":
            p.DodgeVsLarge = 1; p.SprintBonus = 1; p.NoSprintPenalty = true; break;
        case "Light-Foot Hobbit":
            p.DodgeVsLarge = 1; p.SprintBonus = 1; break;
        case "Brave Minds Hobbit":
            p.FearImmune = true; break;
        case "Stone Dwarf":
            p.SprintBonus = 3; p.DoubleSprintPenalty = true; break;
        case "Iron Dwarf":
            p.SprintBonus = -1; break;
        case "Gem Gnome":
            p.SprintBonus = 1; p.RaceAbsorbPct = 25; p.SpellDurBonus = 1; p.SongDurBonus = 1; break;
        case "Glass Gnome":
            p.SprintBonus = 1; p.SpellDurBonus = 1; p.PrayerDurBonus = 1; p.SongDurBonus = 1; break;
        case "Orc":
            p.SprintBonus = 0; p.NoSprintPenalty = true; break;
        case "Goblin":
            p.SprintBonus = 2; p.DoubleSprintPenalty = true; break;
        case "Troll":
            p.RegenPerTurn = 2; p.SprintBonus = 1; p.RaceFrenzy = true; break;
        case "Hobgoblin":
            p.SprintBonus = 1; p.DoubleSprintPenalty = true; break;
    }
}

// Gender and full layered appearance, so the on-screen character is theirs.
void SelectAppearance(Player p)
{
    string Pick(string label, string[] opts, int dflt)
    {
        Console.Write($"\n{label}: ");
        for (int i = 0; i < opts.Length; i++) Console.Write($"[{i + 1}]{opts[i]}  ");
        Console.Write($"(default {opts[dflt]}): ");
        string r = (GameIO.ReadLine() ?? "").Trim();
        if (int.TryParse(r, out int idx) && idx >= 1 && idx <= opts.Length) return opts[idx - 1].ToLower();
        var m = opts.FirstOrDefault(o => o.StartsWith(r, StringComparison.OrdinalIgnoreCase) && r.Length > 0);
        return (m ?? opts[dflt]).ToLower();
    }

    Console.Write("\nGender: [1] Male  [2] Female  [3] Neutral (default): ");
    string g = (GameIO.ReadLine() ?? "").Trim().ToLower();
    p.Gender = g switch { "1" or "m" or "male" => "male", "2" or "f" or "female" => "female", _ => "neutral" };

    p.HairColor  = Pick("Hair color", new[] { "Black", "Blonde", "Brown", "Red", "White" }, 0);
    p.HairLength = Pick("Hair length", new[] { "Bald", "Short", "Medium Short", "Medium", "Medium Long", "Long" }, 1);
    p.EyeColor   = Pick("Eye color", new[] { "Black", "Blue", "Green", "Brown", "Hazel", "Red" }, 3);

    // Headwear is class-restricted; hood / top hat / nothing are universal
    var heads = new List<string>();
    if (p.CharacterType is "Duelist" or "Archer" or "Musician") heads.Add("Fedora");
    if (p.CharacterType == "Mage") heads.Add("Pointy Hat");
    if (p.CharacterType is "Berserker" or "Warrior" or "Martial Artist" or "Duelist") heads.Add("Mask");
    heads.Add("Hood");
    if (p.CharacterType is "Priest" or "Warrior" or "Artisan") heads.Add("Circlet");
    heads.Add("Top Hat");
    heads.Add("Nothing");
    p.Headwear = Pick("Headwear", heads.ToArray(), heads.Count - 1);

    p.ClothingColor = Pick("Clothing color",
        new[] { "Black", "Blue", "Red", "Green", "Yellow", "Purple", "Brown", "Turquoise", "White" }, 0);

    p.FacialHair = p.Gender == "male"
        ? Pick("Facial hair", new[] { "None", "Beard", "Goatee", "Mustache", "Fu Manchu", "Handlebars", "Soul Patch" }, 0)
        : "none";

    Console.WriteLine($"\n  {p.Gender} {p.Race} {p.CharacterType}: {p.HairLength} {p.HairColor} hair, {p.EyeColor} eyes, " +
        $"{p.ClothingColor} clothes, {p.Headwear}" + (p.FacialHair != "none" ? $", {p.FacialHair}" : "") + ".");
}

void SelectCharacterType(Player p)
{
    var types = new[] { "Mage", "Priest", "Warrior", "Duelist", "Archer", "Martial Artist", "Berserker", "Musician", "Artisan" };
    Console.WriteLine("\nChoose your character type:");
    Console.WriteLine("  [1] Mage           — Wand + Staff; Air Blade (ranged) + Air Wave (knockback)");
    Console.WriteLine("  [2] Priest         — Prayers: Healing, Forgiveness, Lord's Prayer");
    Console.WriteLine("  [3] Warrior        — 2x Hand Axe; bonus actions + atk bonus scale with level");
    Console.WriteLine("  [4] Duelist        — Rapier + daggers; Duelist Points & special actions");
    Console.WriteLine("  [5] Archer         — Bow + 50 arrows + Short Sword backup");
    Console.WriteLine("  [6] Martial Artist — Pick a martial art; 1d6+2d4 scaling + grapple/throw");
    Console.WriteLine("  [7] Berserker      — Great Axe; Whirlwind spin + Rage (survive lethal hits)");
    Console.WriteLine("  [8] Musician       — Pick an instrument; songs buff the whole party (linger 1d4 turns)");
    Console.WriteLine("  [9] Artisan        — Gathers wood/stone/ore/hides; crafts weapons, armor and arrows");
    Console.Write("  Choice (1-9 or name): ");
    string raw = (GameIO.ReadLine() ?? "").Trim();
    string chosen = "Warrior";
    if (int.TryParse(raw, out int cidx) && cidx >= 1 && cidx <= types.Length)
        chosen = types[cidx - 1];
    else
    {
        var match = types.FirstOrDefault(t => t.StartsWith(raw, StringComparison.OrdinalIgnoreCase));
        if (match != null) chosen = match;
    }
    p.CharacterType = chosen;
    Console.WriteLine($"  You are a {chosen}!");
    if (chosen == "Mage")
    {
        p.MaxDamage = 4; // 1-4 unarmed
        p.HeldWeapon = "Wand";
        p.SecondaryWeapon = "Staff";
        p.KnownSpells.Add("Air Blade");
        p.KnownSpells.Add("Air Wave");
        p.SpellUses = 6;
        Console.WriteLine("  Starting weapons: Wand (3-4 dmg, 20-50ft range) + Staff (2-6 dmg melee)");
        Console.WriteLine("  Starting spells: Air Blade, Air Wave  Unarmed: 1-4");
        Console.WriteLine("  Spell casts: 6 (+2 every 2 levels), reset on rest");
    }
    else if (chosen == "Warrior")
    {
        p.MaxDamage = 5; // 1-5 unarmed
        p.HeldWeapon = "Hand Axe";
        p.AxeCount = 2;
        Console.WriteLine("  Starting weapons: 2x Hand Axe (1-6 atk, 2-8 dmg, throwable 20ft)  Unarmed: 1-5");
        Console.WriteLine("  Bonus: attack/grapple free actions scale with level; +1 atk roll every 3 levels from L2");
    }
    else if (chosen == "Duelist")
    {
        p.MaxDamage = 4; // 1-4 unarmed
        p.HeldWeapon = "Rapier Sword";
        p.DaggerCount = 6;
        Console.WriteLine("  Starting weapon: Rapier Sword (3-6 dmg)  + 6 daggers (throwable, 20ft, 1-6 dmg)  Unarmed: 1-4");
        Console.WriteLine("  Bonus: Duelist Points (every 3 levels from L2) for special actions");
    }
    else if (chosen == "Archer")
    {
        p.MaxDamage = 4; // 1-4 unarmed
        p.HeldWeapon = "Bow";
        p.SecondaryWeapon = "Short Sword";
        p.ArrowCount = 50;
        p.QuiverCap = 50;   // archers start with a fitted quiver, free
        Console.WriteLine("  Starting weapon: Bow (50 arrows in a free quiver) + Short Sword backup");
        Console.WriteLine("  Bow range: 5-14ft=4-12dmg  15-45ft=2-10dmg  46-60ft=1-5dmg  Min 4ft  Max 60ft");
        Console.WriteLine("  Unarmed: 1-4");
    }
    else if (chosen == "Priest")
    {
        p.MinDamage = 1; p.MaxDamage = 4; // 1d4 unarmed
        p.HeldWeapon = "Mace";
        p.PrayerUses = 5;
        Console.WriteLine("  Starting: Mace (2d4 non-lethal) + unarmed 1d4");
        Console.WriteLine("  Prayer uses: 5 (+2 every 2 levels), reset on rest");
        Console.WriteLine("  Prayers: Prayer of Healing (25ft heal), Forgiveness (30ft convert), Lord's Prayer (6ft AoE dmg)");
        Console.WriteLine("  All prayers scale every 3 levels from L2; Forgiveness also gains AoE/threshold every 4 levels from L2");
    }
    else if (chosen == "Martial Artist")
    {
        p.MinDamage = 1; p.MaxDamage = 6; // unarmed 1d6 base
        var arts = new[] { "Kehon", "Judo", "Taekwondo", "Chidia" };
        Console.WriteLine("\n  Pick your martial art:");
        Console.WriteLine("  [1] Kehon      — enemy enters/leaves range, KO, disarmed, or off-balance: instant free grapple");
        Console.WriteLine("  [2] Judo       — on dodge/block/parry/enemy miss: free grapple (hold/throw/disarm)");
        Console.WriteLine("  [3] Taekwondo  — break limbs of grappled enemies (double damage, effects by limb)");
        Console.WriteLine("  [4] Chidia     — unarmed: +2 actions/turn; all attacks non-lethal (KO)");
        Console.Write("  Choice (1-4 or name): ");
        string araw = (GameIO.ReadLine() ?? "").Trim();
        string art = "Kehon";
        if (int.TryParse(araw, out int aidx) && aidx >= 1 && aidx <= arts.Length) art = arts[aidx - 1];
        else { var am = arts.FirstOrDefault(a => a.StartsWith(araw, StringComparison.OrdinalIgnoreCase)); if (am != null) art = am; }
        p.AddFeat(art);
        Console.WriteLine($"  Martial art: {art}!  Unarmed: 1d6 + 2d4 bonus every 3 levels from L2");
        Console.WriteLine("  Bonus: +1d4 grapple dmg every 4 levels from L2; bonus melee/throw action every 3 levels from L2");
    }
    else if (chosen == "Berserker")
    {
        p.MinDamage = 1; p.MaxDamage = 4; // 1d4 unarmed
        p.HeldWeapon = "Great Axe";
        p.RagePoints = 1;
        Console.WriteLine("  Starting: Great Axe (1d9) + unarmed 1d4");
        Console.WriteLine("  Whirlwind: spin CW/CCW hitting adjacent enemies (+1 hit per 3 levels from L2)");
        Console.WriteLine("  Rage: spend rage points (+1 per 4 levels from L2) for +2d4/pt damage for 3 turns");
        Console.WriteLine("  Rage: survive at 0 HP while raging, heal 1d4 per rage point spent when rage fades");
    }
    else if (chosen == "Musician")
    {
        p.MinDamage = 1; p.MaxDamage = 4; // 1d4 unarmed
        p.HeldWeapon = "Short Sword";
        p.SongTokens = 5;
        var instruments = new[] { "Guitar", "Flute", "Violin", "Drum", "Cello", "Trumpet",
                                  "Saxophone", "Bagpipes", "Accordion", "Tambourine", "Harmonica", "Bells" };
        Console.WriteLine("\n  Pick your instrument:");
        for (int ii = 0; ii < instruments.Length; ii++)
            Console.WriteLine($"  [{ii + 1,2}] {instruments[ii]}");
        Console.Write("  Choice (1-12 or name): ");
        string iraw = (GameIO.ReadLine() ?? "").Trim();
        string inst = "Guitar";
        if (int.TryParse(iraw, out int iidx) && iidx >= 1 && iidx <= instruments.Length) inst = instruments[iidx - 1];
        else { var im = instruments.FirstOrDefault(i => i.StartsWith(iraw, StringComparison.OrdinalIgnoreCase)); if (im != null) inst = im; }
        p.MusicInstrument = inst;
        Console.WriteLine($"  Instrument: {inst}!  Starting: Short Sword + unarmed 1d4");
        Console.WriteLine("  Songs buff you AND all allies (1 token each; effects last while playing + 1d4 turns after stopping):");
        Console.WriteLine("    Slayer         — +2 attack, +1 damage (melee, ranged, spells, grapple)");
        Console.WriteLine("    Wind Song      — +2 dodge, block and parry");
        Console.WriteLine("    Hardstone Song — take 2 less damage");
        Console.WriteLine("    DeathTone      — each turn, enemies with HP ≤ 2d6 roll flee in fear");
        Console.WriteLine("  Every 3rd level: song bonuses +2, fear +1d6. Every 2 levels: +1 song token.");
        Console.WriteLine("  Tokens start at 5 and reset when you rest.");
    }
    else if (chosen == "Artisan")
    {
        p.MinDamage = 1; p.MaxDamage = 4; // 1d4 unarmed
        p.HeldWeapon = "Dagger";
        p.SecondaryWeapon = "Axe";
        p.ArrowCount = 6;
        p.CarryCap = 50;
        p.HasWorkshopHammer = true;   // artisans start with their hammer, free
        Console.WriteLine("  Starting kit: Dagger + Axe + Pickaxe + Shortbow (6 arrows) + Workshop Hammer");
        Console.WriteLine("  After each wave, pick ONE: hunt, mine, or cut wood — gathering materials.");
        Console.WriteLine("  Craft arrows, weapons, shields and armor from wood/stone/ore/hides after combat.");
        Console.WriteLine("  Material needs shrink each level (min 1). Trade gear to allies; sell materials.");
        Console.WriteLine("  Wildlife roams the battlefield — deer, wolves, boars, bears. Hides await!");
    }

    // Set starting HP by class
    p.MaxHP = chosen switch
    {
        "Berserker"      => 14,
        "Warrior"        => 12,
        "Archer"         => 12,
        "Martial Artist" => 10,
        "Duelist"        => 10,
        "Musician"       => 10,
        "Priest"         => 8,
        "Mage"           => 8,
        "Artisan"        => 8,
        _                => 8,
    };
    p.HP = p.MaxHP;
    int hpPerLevel = chosen switch
    {
        "Berserker" => 4,
        "Archer" or "Warrior" => 3,
        "Duelist" or "Martial Artist" => 2,
        _ => 1,
    };
    Console.WriteLine($"  Starting HP: {p.HP}  (+{hpPerLevel} per level)");

    // 12 points at creation. The point buy itself runs later, after core
    // traits and feats (traits and Human/race bonuses add to the pool first).
    p.SavedStatPoints = 12;
}

// ── The travelling merchant: visits the party after every group ────────────
// ══ THE SHOP ══════════════════════════════════════════════════════════════
// Visited between waves. Six storefronts (Crafts / Weapon / Armor / Magic /
// Bags / Dojo) each stock a random 8 of their range, re-rolled every visit,
// plus the merchant's own stall for arrows, materials, potions and selling.
// Item definitions live in Rules.cs; this is just the buying.

// ── One storefront: stocks a random 8 of its range, fresh every visit ──
void BrowseStorefront(Player pl, string shop)
{
    while (true)
    {
        // Armor Store also carries the armor list; Magic Shop marks up by 3 gold
        IEnumerable<string>? extra = shop == "Armor Store" ? Shop.Armors.Keys : null;
        var stock = Shop.RollStock(shop, rng, extra);
        // Storefront prices are as listed — the robes etc. were priced as
        // final. (The old +3g magic markup lives only in the merchant's own
        // [9] magic stall, which predates the storefronts.)
        long Markup(string it) => Shop.CostOf(it);

        Console.WriteLine($"\n── {shop} ──  Purse: {Shop.Fmt(pl.Copper)}   (stock rotates each visit)");
        for (int i = 0; i < stock.Length; i++)
        {
            string it = stock[i];
            string tag = Shop.TwoHanded.Contains(it) ? " [2H]" : Shop.ReachWeapons.Contains(it) ? " [reach]" : "";
            string blurb = Shop.Armors.TryGetValue(it, out var ad) ? $" — {ad.Desc}" : "";
            Console.WriteLine($"  [{i + 1,2}] {it,-24}{tag} {Shop.Fmt(Markup(it))}{blurb}");
        }
        if (shop == "Magic Shop")
            Console.WriteLine("  [e] Enchant a weapon, armor or shield you own (+x atk/dmg or +x defence; armor magic-negate x*2.5%)");
        Console.Write("  Buy # (or Enter to leave): ");
        string raw = (GameIO.ReadLine() ?? "").Trim();
        if (raw.Length == 0) return;
        if (shop == "Magic Shop" && raw.ToLower() is "e" or "enchant") { EnchantService(pl); continue; }
        if (!int.TryParse(raw, out int bi) || bi < 1 || bi > stock.Length) continue;
        string item = stock[bi - 1];
        long cost = Markup(item);
        if (cost > pl.Copper) { Console.WriteLine($"  Not enough coin ({Shop.Fmt(cost)})."); continue; }
        if (pl.MonkWeaponsOnly && Shop.Price.ContainsKey(item) && !Shop.Armors.ContainsKey(item)
            && !pl.IsMonkWeapon(item) && !Shop.Shields.ContainsKey(item)
            && item is not ("Quiver" or "Potion Pouch" or "Throwing Band" or "Artisan's Bag" or "Bag of Holding"))
        { Console.WriteLine($"  Your Vow of Silence forbids the {item} — monk weapons only."); continue; }

        // ── Armor ──
        if (Shop.Armors.TryGetValue(item, out var armor))
        {
            // Ability-users wear robes as a TRUE bottom layer — under-armor
            // and plate both still fit on top
            if (Player.IsRobe(item) && pl.AbilityUser && pl.RobeWorn.Length == 0)
            {
                pl.Copper -= cost;
                pl.RobeWorn = item;
                pl.ArmorDamageReduction += armor.MeleeDR;
                pl.ArmorSpellDR += armor.SpellDR; pl.ArmorPrayerDR += armor.PrayerDR;
                pl.ArmorAbsorbPct = Math.Min(100, pl.ArmorAbsorbPct + armor.AbsorbPct);
                Console.WriteLine($"  You don the {item} beneath everything — armor and plate still fit over it. {armor.Desc}");
                continue;
            }
            if (armor.Under && pl.UnderArmor.Length > 0) { Console.WriteLine($"  You already wear {pl.UnderArmor} underneath."); continue; }
            if (!armor.Under && pl.MainArmor != "none") { Console.WriteLine($"  You already wear {pl.MainArmor}."); continue; }
            pl.Copper -= cost;
            if (armor.Under) pl.UnderArmor = item; else pl.MainArmor = item;
            pl.ArmorDamageReduction += armor.MeleeDR;
            pl.ArmorSpellDR += armor.SpellDR; pl.ArmorPrayerDR += armor.PrayerDR;
            pl.ArmorAbsorbPct = Math.Min(100, pl.ArmorAbsorbPct + armor.AbsorbPct);
            if (armor.Metal) pl.ArmorMetal = true;
            Console.WriteLine($"  You don the {item}. {armor.Desc}");
            continue;
        }
        // ── Shields ──
        if (Shop.Shields.TryGetValue(item, out var sh))
        {
            if (pl.OffHandShieldName != null) { Console.WriteLine($"  You already carry a {pl.OffHandShieldName}."); continue; }
            pl.Copper -= cost;
            pl.OffHandShieldName = item; pl.OffHandShieldBlock = sh.Block;
            pl.ArmorDamageReduction += sh.Def;
            Console.WriteLine($"  You strap on the {item} (+{sh.Block} block).");
            continue;
        }
        // ── Bags and carriers ──
        if (item is "Quiver" or "Potion Pouch" or "Throwing Band" or "Artisan's Bag" or "Bag of Holding" or "Quiver of Holding")
        {
            pl.Copper -= cost;
            switch (item)
            {
                case "Quiver":            pl.QuiverCap += 25; Console.WriteLine($"  A quiver — carries {pl.QuiverCap} arrows."); break;
                case "Potion Pouch":      pl.PotionPouchCap += 5; Console.WriteLine($"  A potion pouch — holds {pl.PotionPouchCap} potions."); break;
                case "Throwing Band":     pl.ThrowBandCap += 4; Console.WriteLine($"  A throwing band — holds {pl.ThrowBandCap} throwing weapons."); break;
                case "Artisan's Bag":     pl.CarryCap += 50; Console.WriteLine($"  An artisan's bag — pack space is now {pl.CarryCap}."); break;
                case "Quiver of Holding": pl.HasQuiverOfHolding = true; Console.WriteLine("  A quiver of holding — endless arrows!"); break;
                default:                  pl.HasBagOfHolding = true; Console.WriteLine("  A bag of holding — no carry limit at all!"); break;
            }
            continue;
        }
        // ── Weapons ──
        pl.Copper -= cost;
        if (item == "Workshop Hammer") pl.HasWorkshopHammer = true;   // unlocks Artisan crafting
        if (pl.HeldWeapon == null) { pl.HeldWeapon = item; Console.WriteLine($"  You wield the {item}."); }
        else if (pl.SecondaryWeapon == null) { pl.SecondaryWeapon = item; Console.WriteLine($"  You stow the {item}."); }
        else
        {
            Console.Write($"  Hands full — trade in [H]eld {pl.HeldWeapon}, [S]econdary {pl.SecondaryWeapon}, or [N]othing? ");
            string tr = (GameIO.ReadLine() ?? "n").Trim().ToLower();
            if (tr.StartsWith("h")) { pl.Copper += Shop.Sell(pl.HeldWeapon); pl.HeldWeapon = item; Console.WriteLine($"  You now wield the {item}."); }
            else if (tr.StartsWith("s")) { pl.Copper += Shop.Sell(pl.SecondaryWeapon); pl.SecondaryWeapon = item; Console.WriteLine($"  Stowed the {item}."); }
            else { pl.Copper += cost; Console.WriteLine("  Purchase cancelled."); }
        }
    }
}

// ── The enchanter's table (Magic Shop): +x for base cost + x gold ──
void EnchantService(Player pl)
{
    Console.WriteLine($"\n  ── ENCHANTER ──  Purse: {Shop.Fmt(pl.Copper)}");
    Console.WriteLine($"  [W] Weapon in hand: {pl.HeldWeapon ?? "(bare hands — nothing to enchant)"}" +
        (pl.WeaponEnchant() > 0 ? $"  (already +{pl.WeaponEnchant()})" : ""));
    Console.WriteLine($"  [A] Armor worn: {pl.MainArmor}" + (pl.ArmorEnchant > 0 ? $"  (already +{pl.ArmorEnchant})" : ""));
    Console.WriteLine($"  [S] Shield: {pl.OffHandShieldName ?? "(none)"}" + (pl.ShieldEnchant > 0 ? $"  (already +{pl.ShieldEnchant})" : ""));
    Console.Write("  Enchant which (or Enter to step back): ");
    string what = (GameIO.ReadLine() ?? "").Trim().ToLower();
    if (what.Length == 0) return;
    Console.Write("  Enchant to what level x (1-5)?  (cost = item's base price + x gold): ");
    if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int x) || x < 1 || x > 5) { Console.WriteLine("  The enchanter shrugs."); return; }

    if (what.StartsWith("w"))
    {
        if (pl.HeldWeapon == null) { Console.WriteLine("  Nothing in hand to enchant."); return; }
        if (pl.WeaponEnchant() >= x) { Console.WriteLine($"  Your {pl.HeldWeapon} already carries +{pl.WeaponEnchant()}."); return; }
        long cost = Shop.CostOf(pl.HeldWeapon) + x * Shop.Gold;
        if (cost > pl.Copper) { Console.WriteLine($"  Not enough coin ({Shop.Fmt(cost)})."); return; }
        pl.Copper -= cost;
        pl.EnchantedWeapons[pl.HeldWeapon] = x;
        Console.WriteLine($"  Runes crawl up the {pl.HeldWeapon} — it is now +{x} to attack AND damage! ({Shop.Fmt(cost)})");
    }
    else if (what.StartsWith("a"))
    {
        if (pl.MainArmor is "none" or "Cloth") { Console.WriteLine("  Wear real armor first."); return; }
        if (pl.ArmorEnchant >= x) { Console.WriteLine($"  Your {pl.MainArmor} already carries +{pl.ArmorEnchant}."); return; }
        long cost = Shop.CostOf(pl.MainArmor) + x * Shop.Gold;
        if (cost > pl.Copper) { Console.WriteLine($"  Not enough coin ({Shop.Fmt(cost)})."); return; }
        pl.Copper -= cost;
        pl.ArmorDamageReduction += x - pl.ArmorEnchant;
        pl.ArmorEnchant = x;
        Console.WriteLine($"  Your {pl.MainArmor} shimmers — +{x} protection, {(pl.ArmorEnchant + pl.ShieldEnchant) * 2.5:F1}% chance to NEGATE hostile magic! ({Shop.Fmt(cost)})");
    }
    else if (what.StartsWith("s"))
    {
        if (pl.OffHandShieldName == null) { Console.WriteLine("  You carry no shield."); return; }
        if (pl.ShieldEnchant >= x) { Console.WriteLine($"  Your {pl.OffHandShieldName} already carries +{pl.ShieldEnchant}."); return; }
        long cost = Shop.CostOf(pl.OffHandShieldName) + x * Shop.Gold;
        if (cost > pl.Copper) { Console.WriteLine($"  Not enough coin ({Shop.Fmt(cost)})."); return; }
        pl.Copper -= cost;
        pl.OffHandShieldBlock += x - pl.ShieldEnchant;
        pl.ShieldEnchant = x;
        Console.WriteLine($"  Your {pl.OffHandShieldName} hums — +{x} block, {(pl.ArmorEnchant + pl.ShieldEnchant) * 2.5:F1}% chance to NEGATE hostile magic! ({Shop.Fmt(cost)})");
    }
}

// ── THE HOARD: what a dead dragon was sleeping on ──
void DragonHoard(Random r)
{
    string[] elixirs = { "Bright Soul", "Rapid", "Oak Wood", "Iron Rock", "Red Rage", "Blue Sky",
                         "Holy Rights", "Notes Melody", "Pirate Booty", "Reflex of the Tiger", "Knight's", "Rogue's Rose" };
    string[] mundaneArmor = { "Padded Armor", "Leather Vest", "Leather Armor", "Chainmail", "Breastplate", "Half Plate", "Soft Leather", "Full Plate", "Hard Leather" };
    string[] weapons = Shop.Storefronts["Weapon Shop"];
    string[] magic = Shop.Storefronts["Magic Shop"];

    int chests = r.Next(1, 9);
    Console.WriteLine($"\n═══ THE DRAGON'S HOARD ═══  {chests} treasure chest(s) glitter in the wreckage!");
    foreach (var pl in allPlayers) pl.SpecialPotions["Training Potion"] = pl.SpecialPotions.GetValueOrDefault("Training Potion") + 1;
    Console.WriteLine("  Each of you claims a TRAINING POTION from the hoard's crown (+4 points when drunk).");

    long coinEach = 0;
    int who = 0;
    for (int c = 1; c <= chests; c++)
    {
        long coin = 500_000 + (long)(r.NextDouble() * 1_500_000);   // 0.5-2 platinum
        coinEach += coin;
        var items = new List<string>();
        int nA = r.Next(1, 6), nW = r.Next(2, 6), nM = r.Next(2, 4), nP = r.Next(0, 9);
        for (int i = 0; i < nA; i++) items.Add(mundaneArmor[r.Next(mundaneArmor.Length)]);
        for (int i = 0; i < nW; i++) items.Add(weapons[r.Next(weapons.Length)]);
        for (int i = 0; i < nM; i++) items.Add(magic[r.Next(magic.Length)]);
        Console.WriteLine($"\n  ── Chest {c}: {Shop.Fmt(coin)} in coin (EACH of you), {items.Count} item(s) ──");
        foreach (var it in items) ClaimChestItem(it, r);
        for (int i = 0; i < nP; i++)
        {
            string el = elixirs[r.Next(elixirs.Length)];
            var lucky = allPlayers[who++ % allPlayers.Count];
            lucky.SpecialPotions[el] = lucky.SpecialPotions.GetValueOrDefault(el) + 1;
            Console.WriteLine($"    Elixir: {el} → {lucky.Name}");
        }
    }
    foreach (var pl in allPlayers) pl.Copper += coinEach;
    Console.WriteLine($"\n  Every player pockets {Shop.Fmt(coinEach)} in dragon-gold. You are RICH.");
}

// One item from a chest: whoever wants it claims it; rivals roll a d20 for
// it; unclaimed treasure is sold and the coin goes to everyone.
void ClaimChestItem(string item, Random r)
{
    long worth = Shop.Sell(item);
    if (allPlayers.Count == 1)
    {
        Console.Write($"    {item} (sells {Shop.Fmt(worth)}) — [K]eep or [Enter] sell: ");
        if ((GameIO.ReadLine() ?? "").Trim().ToLower().StartsWith("k")) ApplyChestItem(allPlayers[0], item);
        else { allPlayers[0].Copper += worth; Console.WriteLine($"      Sold for {Shop.Fmt(worth)}."); }
        return;
    }
    Console.Write($"    {item} (sells {Shop.Fmt(worth)}) — who wants it? (#s like 1,3 — party: " +
        string.Join(" ", allPlayers.Select((pp, i2) => $"[{i2 + 1}]{pp.Name.Split(' ')[0]}")) + ", Enter = sell): ");
    var picks = (GameIO.ReadLine() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => int.TryParse(s.Trim(), out int n) ? n : -1)
        .Where(n => n >= 1 && n <= allPlayers.Count).Distinct().ToList();
    if (picks.Count == 0)
    {
        foreach (var pl in allPlayers) pl.Copper += worth / allPlayers.Count;
        Console.WriteLine($"      Sold — {Shop.Fmt(worth / allPlayers.Count)} each.");
    }
    else if (picks.Count == 1)
        ApplyChestItem(allPlayers[picks[0] - 1], item);
    else
    {
        // Rivals: highest d20 takes it home
        int bestRoll = -1; Player winner = allPlayers[picks[0] - 1];
        foreach (var pi3 in picks)
        {
            int roll = r.Next(1, 21);
            Console.WriteLine($"      {allPlayers[pi3 - 1].Name} rolls {roll}!");
            if (roll > bestRoll) { bestRoll = roll; winner = allPlayers[pi3 - 1]; }
        }
        Console.WriteLine($"      {winner.Name} wins the {item}!");
        ApplyChestItem(winner, item);
    }
}

// Fit the claimed piece onto its new owner (or sell it if they can't use it)
void ApplyChestItem(Player pl, string item)
{
    if (Shop.Armors.TryGetValue(item, out var ad2))
    {
        if (Player.IsRobe(item) && pl.AbilityUser && pl.RobeWorn.Length == 0) pl.RobeWorn = item;
        else if (ad2.Under && pl.UnderArmor.Length == 0) pl.UnderArmor = item;
        else if (!ad2.Under && pl.MainArmor is "none" or "Cloth") pl.MainArmor = item;
        else { pl.Copper += Shop.Sell(item); Console.WriteLine($"      No room to wear it — {pl.Name} sells it for {Shop.Fmt(Shop.Sell(item))}."); return; }
        pl.ArmorDamageReduction += ad2.MeleeDR;
        pl.ArmorSpellDR += ad2.SpellDR; pl.ArmorPrayerDR += ad2.PrayerDR;
        pl.ArmorAbsorbPct = Math.Min(100, pl.ArmorAbsorbPct + ad2.AbsorbPct);
        if (ad2.Metal) pl.ArmorMetal = true;
        Console.WriteLine($"      {pl.Name} dons the {item}.");
        return;
    }
    if (Shop.Shields.TryGetValue(item, out var sh2))
    {
        if (pl.OffHandShieldName != null) { pl.Copper += Shop.Sell(item); Console.WriteLine($"      Shield arm full — sold for {Shop.Fmt(Shop.Sell(item))}."); return; }
        pl.OffHandShieldName = item; pl.OffHandShieldBlock = sh2.Block;
        pl.ArmorDamageReduction += sh2.Def;
        Console.WriteLine($"      {pl.Name} straps on the {item}.");
        return;
    }
    switch (item)
    {
        case "Bag of Holding":    pl.HasBagOfHolding = true; Console.WriteLine($"      {pl.Name} claims the Bag of Holding — no carry limit!"); return;
        case "Quiver of Holding": pl.HasQuiverOfHolding = true; Console.WriteLine($"      {pl.Name} claims the Quiver of Holding — endless arrows!"); return;
        case "Returning Quiver":  pl.HasReturningAmmo = true; Console.WriteLine($"      {pl.Name} claims the Returning Quiver — ammo flies back!"); return;
        case "Mirror Shield":
            if (pl.OffHandShieldName == null) { pl.OffHandShieldName = item; pl.OffHandShieldBlock = 2; Console.WriteLine($"      {pl.Name} raises the Mirror Shield!"); }
            else { pl.Copper += Shop.Sell(item); Console.WriteLine($"      Shield arm full — sold."); }
            return;
    }
    if (pl.MonkWeaponsOnly && !pl.IsMonkWeapon(item)) { pl.Copper += Shop.Sell(item); Console.WriteLine($"      The vow forbids it — {pl.Name} sells it."); return; }
    if (item == "Workshop Hammer") pl.HasWorkshopHammer = true;
    if (pl.HeldWeapon == null) { pl.HeldWeapon = item; Console.WriteLine($"      {pl.Name} wields the {item}."); }
    else if (pl.SecondaryWeapon == null) { pl.SecondaryWeapon = item; Console.WriteLine($"      {pl.Name} stows the {item}."); }
    else { pl.Copper += Shop.Sell(item); Console.WriteLine($"      Hands full — {pl.Name} sells it for {Shop.Fmt(Shop.Sell(item))}."); }
}

void VisitShop(Player pl)
{
    while (true)
    {
        Console.WriteLine($"\n═══ TRAVELLING MERCHANT ═══  Purse: {Shop.Fmt(pl.Copper)}");
        Console.WriteLine($"  Wearing: {pl.MainArmor}{(pl.UnderArmor.Length > 0 ? $" over {pl.UnderArmor}" : "")}");
        Console.WriteLine("  ── Storefronts (stock rotates each visit) ──");
        Console.WriteLine("  [c] Crafts Store   [w] Weapon Shop   [a] Armor Store");
        Console.WriteLine("  [m] Magic Shop     [b] Bags Shop     [j] Dojo (monk weapons)");
        Console.WriteLine("  ── Merchant's own stall ──");
        Console.WriteLine("  [1] Arrows   [2] Weapons   [3] Shields   [4] Sell gear   [5] Armor   [6] Materials");
        Console.WriteLine("  [8] Bag space   [9] Magic shop   [10] Potion shop   [7] Leave");
        Console.Write("  Browse: ");
        string c = (GameIO.ReadLine() ?? "7").Trim().ToLower();
        if (c is "7" or "leave" or "exit" or "x" or "") break;
        if (c is "c" or "crafts") { BrowseStorefront(pl, "Crafts Store"); continue; }
        if (c is "w" or "weapon") { BrowseStorefront(pl, "Weapon Shop"); continue; }
        if (c is "a" or "armor")  { BrowseStorefront(pl, "Armor Store"); continue; }
        if (c is "m" or "magic")  { BrowseStorefront(pl, "Magic Shop"); continue; }
        if (c is "b" or "bags")   { BrowseStorefront(pl, "Bags Shop"); continue; }
        if (c is "j" or "dojo")   { BrowseStorefront(pl, "Dojo"); continue; }

        if (c is "8" or "bag" or "bags")
        {
            long bcost = 2 + 2 * pl.BagUpgrades;
            Console.WriteLine($"  [1] Loose sack — +4 pack space for {Shop.Fmt(bcost)} (price climbs 2c per purchase)");
            bool bagGate = pl.HasFeat("Hunter") || pl.HasFeat("Gatherer");
            if (bagGate) Console.WriteLine("  [2] Artisan Bag — +8 pack space for 20c (Hunter/Gatherer only)");
            Console.Write("  Buy (or Enter to go back): ");
            string bc = (GameIO.ReadLine() ?? "").Trim();
            if (bc == "1")
            {
                if (bcost > pl.Copper) { Console.WriteLine("  Not enough coin."); continue; }
                pl.Copper -= bcost; pl.BagUpgrades++; pl.CarryCap += 4;
                Console.WriteLine($"  Pack space is now {pl.CarryCap}. Purse: {Shop.Fmt(pl.Copper)}");
            }
            else if (bc == "2" && bagGate)
            {
                if (20 > pl.Copper) { Console.WriteLine("  Not enough coin."); continue; }
                pl.Copper -= 20; pl.CarryCap += 8;
                Console.WriteLine($"  A proper artisan bag! Pack space is now {pl.CarryCap}. Purse: {Shop.Fmt(pl.Copper)}");
            }
            continue;
        }

        if (c is "9" or "magic")
        {
            Console.WriteLine("  Magic-crafted wares (base price + 3 gold):");
            Console.WriteLine($"  [1] Returning Quiver — {Shop.Fmt(Shop.Price["Returning Quiver"] + 3 * Shop.Gold)}  (your ammo flies back unbroken)");
            Console.WriteLine($"  [2] Mirror Shield    — {Shop.Fmt(Shop.Price["Mirror Shield"] + 3 * Shop.Gold)}  (+2 block; reflects spells/prayers 35%)");
            Console.WriteLine($"  [3] Bag of Holding   — {Shop.Fmt(Shop.Price["Bag of Holding"] + 3 * Shop.Gold)}  (carry everything, forever)");
            Console.Write("  Buy (or Enter to go back): ");
            string mg = (GameIO.ReadLine() ?? "").Trim();
            long mcost2 = mg switch
            {
                "1" => Shop.Price["Returning Quiver"] + 3 * Shop.Gold,
                "2" => Shop.Price["Mirror Shield"] + 3 * Shop.Gold,
                "3" => Shop.Price["Bag of Holding"] + 3 * Shop.Gold,
                _ => -1,
            };
            if (mcost2 < 0) continue;
            if (mcost2 > pl.Copper) { Console.WriteLine($"  Not enough coin ({Shop.Fmt(mcost2)})."); continue; }
            pl.Copper -= mcost2;
            switch (mg)
            {
                case "1":
                    pl.HasReturningAmmo = true;
                    Console.WriteLine("  Your arrows and thrown weapons now return to your hand, unbroken!");
                    break;
                case "2":
                    if (pl.OffHandShieldName != null)
                    {
                        pl.Copper += Shop.Sell(pl.OffHandShieldName);
                        pl.ArmorDamageReduction -= pl.OffHandShieldDefense;
                        pl.MaxBlock -= pl.OffHandShieldBlock;
                        Console.WriteLine($"  Traded in your {pl.OffHandShieldName}.");
                    }
                    pl.OffHandShieldName = "Mirror Shield";
                    pl.OffHandShieldBlock = 2; pl.OffHandShieldDefense = 0;
                    pl.MaxBlock += 2;
                    Console.WriteLine("  The Mirror Shield gleams — spells and prayers may bounce back at their casters!");
                    break;
                case "3":
                    pl.HasBagOfHolding = true;
                    Console.WriteLine("  The Bag of Holding swallows everything you own with room to spare. Carry capacity: limitless.");
                    break;
            }
            Console.WriteLine($"  Purse: {Shop.Fmt(pl.Copper)}");
            continue;
        }

        if (c is "10" or "potion" or "potions")
        {
            long pcost = pl.Level * Shop.Gold;
            Console.WriteLine($"  Potions ({Shop.Fmt(pcost)} each — brewed to your level {pl.Level}):");
            Console.WriteLine($"  [1] Boost x{pl.PotionsBoost}   [2] Heal x{pl.PotionsHeal}   [3] Poison x{pl.PotionsPoison}   [4] Restore x{pl.PotionsRestore}");
            Console.Write("  Buy (or Enter to go back): ");
            string pp = (GameIO.ReadLine() ?? "").Trim();
            if (pp is not ("1" or "2" or "3" or "4")) continue;
            if (pcost > pl.Copper) { Console.WriteLine($"  Not enough coin ({Shop.Fmt(pcost)})."); continue; }
            pl.Copper -= pcost;
            switch (pp)
            {
                case "1": pl.PotionsBoost++; break;
                case "2": pl.PotionsHeal++; break;
                case "3": pl.PotionsPoison++; break;
                default: pl.PotionsRestore++; break;
            }
            Console.WriteLine($"  Bought. Purse: {Shop.Fmt(pl.Copper)}");
            continue;
        }

        if (c is "6" or "materials")
        {
            Console.WriteLine($"  [1] Wood — 5c   (you have {pl.Wood})");
            Console.WriteLine($"  [2] Stone — 8c  (you have {pl.Stone})");
            Console.WriteLine($"  [3] Ore — 25c   (you have {pl.Ore})");
            Console.WriteLine($"  [4] Hide — 20c  (you have {pl.Hides})");
            Console.Write("  Which (or Enter to go back): ");
            string mc = (GameIO.ReadLine() ?? "").Trim();
            if (mc is not ("1" or "2" or "3" or "4")) continue;
            Console.Write("  How many: ");
            if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int mq) || mq <= 0) continue;
            if (mq > pl.PackRoom)
            {
                mq = pl.PackRoom;
                if (mq <= 0) { Console.WriteLine("  Your pack is full!"); continue; }
                Console.WriteLine($"  Your pack only fits {mq} more.");
            }
            long unit = mc switch { "1" => 5, "2" => 8, "3" => 25, _ => 20 };
            long mcost = unit * mq;
            if (mcost > pl.Copper) { Console.WriteLine($"  Not enough coin ({Shop.Fmt(mcost)})."); continue; }
            pl.Copper -= mcost;
            switch (mc)
            {
                case "1": pl.Wood += mq; break;
                case "2": pl.Stone += mq; break;
                case "3": pl.Ore += mq; break;
                default: pl.Hides += mq; break;
            }
            Console.WriteLine($"  Bought {mq} for {Shop.Fmt(mcost)}. Purse: {Shop.Fmt(pl.Copper)}");
            continue;
        }

        if (c is "5" or "armor" or "armour")
        {
            var alist = Shop.Armors.Keys.ToArray();
            for (int i = 0; i < alist.Length; i++)
            {
                var a = Shop.Armors[alist[i]];
                Console.WriteLine($"  [{i + 1,2}] {alist[i],-14}{(a.Under ? " [under]" : "        ")} — {Shop.Fmt(a.Cost),-8} {a.Desc}");
            }
            Console.WriteLine("  ([under] pieces layer beneath a main armor — you can wear one of each)");
            Console.Write("  Buy # (or Enter to go back): ");
            if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int ai2) || ai2 < 1 || ai2 > alist.Length) continue;
            string aname = alist[ai2 - 1];
            var def = Shop.Armors[aname];
            if (def.Cost > pl.Copper) { Console.WriteLine($"  Not enough coin ({Shop.Fmt(def.Cost)})."); continue; }

            // Unequip whatever occupies the slot (trade-in at 80%)
            string oldName = def.Under ? pl.UnderArmor : pl.MainArmor;
            if (oldName.Length > 0 && oldName != "Cloth" && Shop.Armors.TryGetValue(oldName, out var oldDef))
            {
                long tradeIn = Shop.Sell(oldName);
                pl.Copper += tradeIn;
                pl.ArmorDamageReduction -= oldDef.MeleeDR;
                pl.ArmorSpellDR -= oldDef.SpellDR;
                pl.ArmorPrayerDR -= oldDef.PrayerDR;
                Console.WriteLine($"  Traded in your {oldName} for {Shop.Fmt(tradeIn)}.");
            }
            pl.Copper -= def.Cost;
            if (def.Under) pl.UnderArmor = aname; else pl.MainArmor = aname;
            pl.ArmorDamageReduction += def.MeleeDR;
            pl.ArmorSpellDR += def.SpellDR;
            pl.ArmorPrayerDR += def.PrayerDR;
            // Absorption chances of both layers combine as independent rolls
            int pctMain = Shop.Armors.TryGetValue(pl.MainArmor, out var am) ? am.AbsorbPct : 0;
            int pctUnder = Shop.Armors.TryGetValue(pl.UnderArmor, out var au) ? au.AbsorbPct : 0;
            pl.ArmorAbsorbPct = 100 - (100 - pctMain) * (100 - pctUnder) / 100;
            pl.ArmorMetal = (Shop.Armors.TryGetValue(pl.MainArmor, out var m1) && m1.Metal)
                         || (Shop.Armors.TryGetValue(pl.UnderArmor, out var m2) && m2.Metal);
            Console.WriteLine($"  You don the {aname}! Damage taken -{pl.ArmorDamageReduction}, spells -{pl.ArmorSpellDR}, prayers -{pl.ArmorPrayerDR}" +
                (pl.ArmorAbsorbPct > 0 ? $", {pl.ArmorAbsorbPct}% magic absorption" : "") +
                (pl.ArmorMetal ? "  [metal — lightning conducts!]" : "") + $".  Purse: {Shop.Fmt(pl.Copper)}");
            continue;
        }

        if (c is "1" or "arrows")
        {
            Console.WriteLine($"  [1] Arrow — 1c each          (you have {pl.ArrowCount})");
            Console.WriteLine($"  [2] Blunt Arrow — 2 for 1c, non-lethal   (you have {pl.BluntArrows})");
            Console.WriteLine($"  [3] Barbed Arrow — 1s, +1d4 damage       (you have {pl.BarbedArrows})");
            Console.WriteLine($"  [4] Spiral Arrow — 3s, +1d4 atk & +1d4 dmg (you have {pl.SpiralArrows})");
            Console.Write("  Which (or Enter to go back): ");
            string ac = (GameIO.ReadLine() ?? "").Trim();
            if (ac is not ("1" or "2" or "3" or "4")) continue;
            Console.Write("  How many: ");
            if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int n) || n <= 0) continue;
            long cost = ac switch
            {
                "1" => n,
                "2" => (n + 1) / 2,          // 2 per copper
                "3" => n * Shop.Silver,
                _   => n * 3 * Shop.Silver,
            };
            if (cost > pl.Copper) { Console.WriteLine($"  Not enough coin ({Shop.Fmt(cost)})."); continue; }
            pl.Copper -= cost;
            switch (ac)
            {
                case "1": pl.ArrowCount += n; break;
                case "2": pl.BluntArrows += n; break;
                case "3": pl.BarbedArrows += n; break;
                case "4": pl.SpiralArrows += n; break;
            }
            Console.WriteLine($"  Bought {n} for {Shop.Fmt(cost)}. Purse: {Shop.Fmt(pl.Copper)}");
        }
        else if (c is "2" or "weapons")
        {
            var stock = new[] { "Dagger", "Club", "Quarterstaff", "Staff", "Short Sword", "Hand Axe",
                                "Mace", "Sword", "Long Staff", "Rapier Sword", "Long Sword", "Bow",
                                "Halberd", "Great Sword", "Battle Axe", "War Mace", "Axe", "Pike",
                                "Claymore", "Great Axe", "Warhammer", "Wand", "Pickaxe", "Shortbow", "Hunting Bow" };
            for (int i = 0; i < stock.Length; i++)
            {
                string w = stock[i];
                string th = Shop.TwoHanded.Contains(w) ? " [2H]" : "";
                Console.WriteLine($"  [{i + 1,2}] {w,-13}{th} — {Shop.Fmt(Shop.Price[w])}");
            }
            Console.WriteLine("  ([2H] = two-handed; needs Giant's Strength to wield in one hand)");
            Console.Write("  Buy # (or Enter to go back): ");
            if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int wi) || wi < 1 || wi > stock.Length) continue;
            string wname = stock[wi - 1];
            long wcost = Shop.Price[wname];
            if (wcost > pl.Copper) { Console.WriteLine($"  Not enough coin ({Shop.Fmt(wcost)})."); continue; }
            if (pl.MonkWeaponsOnly && !pl.IsMonkWeapon(wname))
            { Console.WriteLine($"  Your Vow of Silence forbids the {wname} — monk weapons only."); continue; }
            // Where does it go?
            if (pl.HeldWeapon == null) { pl.HeldWeapon = wname; Console.WriteLine($"  You wield the {wname}."); }
            else if (pl.SecondaryWeapon == null) { pl.SecondaryWeapon = wname; Console.WriteLine($"  You stow the {wname} as your secondary weapon."); }
            else
            {
                Console.Write($"  Hands full! Trade in [H]eld {pl.HeldWeapon} or [S]econdary {pl.SecondaryWeapon} at 80%? ([N]o): ");
                string tr = (GameIO.ReadLine() ?? "n").Trim().ToLower();
                if (tr.StartsWith("h"))
                {
                    long tradeIn = Shop.Sell(pl.HeldWeapon);
                    pl.Copper += tradeIn;
                    Console.WriteLine($"  Sold your {pl.HeldWeapon} for {Shop.Fmt(tradeIn)}.");
                    pl.HeldWeapon = wname;
                }
                else if (tr.StartsWith("s"))
                {
                    long tradeIn = Shop.Sell(pl.SecondaryWeapon!);
                    pl.Copper += tradeIn;
                    Console.WriteLine($"  Sold your {pl.SecondaryWeapon} for {Shop.Fmt(tradeIn)}.");
                    pl.SecondaryWeapon = wname;
                }
                else continue;
            }
            pl.Copper -= wcost;
            if (Shop.TwoHanded.Contains(wname) && !pl.HasFeat("Giant's Strength"))
                Console.WriteLine($"  (The {wname} takes both hands — Giant's Strength would free your off-hand.)");
            Console.WriteLine($"  Purse: {Shop.Fmt(pl.Copper)}");
        }
        else if (c is "3" or "shields")
        {
            var shields = Shop.Shields.Keys.ToArray();
            for (int i = 0; i < shields.Length; i++)
            {
                var st = Shop.Shields[shields[i]];
                Console.WriteLine($"  [{i + 1}] {shields[i],-12} — {Shop.Fmt(Shop.Price[shields[i]])}  (+{st.Block} block{(st.Def > 0 ? $", -{st.Def} dmg taken" : "")})");
            }
            Console.WriteLine("  (Shields also block ranged attacks.)");
            Console.Write("  Buy # (or Enter to go back): ");
            if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int si) || si < 1 || si > shields.Length) continue;
            string sname = shields[si - 1];
            long scost = Shop.Price[sname];
            if (scost > pl.Copper) { Console.WriteLine($"  Not enough coin ({Shop.Fmt(scost)})."); continue; }
            // Replace any current shield (removing its bonuses, trading it in at 80%)
            if (pl.OffHandShieldName != null)
            {
                long tradeIn = Shop.Sell(pl.OffHandShieldName);
                pl.Copper += tradeIn;
                pl.ArmorDamageReduction -= pl.OffHandShieldDefense;
                pl.MaxBlock -= pl.OffHandShieldBlock;
                Console.WriteLine($"  Traded in your {pl.OffHandShieldName} for {Shop.Fmt(tradeIn)}.");
            }
            var stats = Shop.Shields[sname];
            pl.Copper -= scost;
            pl.OffHandShieldName = sname;
            pl.OffHandShieldBlock = stats.Block;
            pl.OffHandShieldDefense = stats.Def;
            pl.MaxBlock += stats.Block;
            pl.ArmorDamageReduction += stats.Def;
            Console.WriteLine($"  You strap on the {sname}! Block {pl.MinBlock}-{pl.MaxBlock}" +
                (stats.Def > 0 ? $", -{stats.Def} damage taken" : "") + $".  Purse: {Shop.Fmt(pl.Copper)}");
        }
        else if (c is "4" or "sell")
        {
            var sellable = new List<(string Label, string Item, Action Remove)>();
            if (pl.HeldWeapon != null)
            {
                string hw = pl.HeldWeapon;
                sellable.Add(($"Held: {hw}", hw, () => pl.HeldWeapon = null));
            }
            if (pl.SecondaryWeapon != null)
            {
                string sw = pl.SecondaryWeapon;
                sellable.Add(($"Secondary: {sw}", sw, () => pl.SecondaryWeapon = null));
            }
            if (pl.OffHandShieldName != null)
            {
                string sh = pl.OffHandShieldName;
                sellable.Add(($"Shield: {sh}", sh, () =>
                {
                    pl.ArmorDamageReduction -= pl.OffHandShieldDefense;
                    pl.MaxBlock -= pl.OffHandShieldBlock;
                    pl.OffHandShieldName = null; pl.OffHandShieldBlock = 0; pl.OffHandShieldDefense = 0;
                }));
            }
            if (!sellable.Any()) { Console.WriteLine("  Nothing to sell."); continue; }
            for (int i = 0; i < sellable.Count; i++)
                Console.WriteLine($"  [{i + 1}] {sellable[i].Label} — sells for {Shop.Fmt(Shop.Sell(sellable[i].Item))}");
            Console.Write("  Sell # (or Enter to go back): ");
            if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int gi) || gi < 1 || gi > sellable.Count) continue;
            long val = Shop.Sell(sellable[gi - 1].Item);
            sellable[gi - 1].Remove();
            pl.Copper += val;
            Console.WriteLine($"  Sold for {Shop.Fmt(val)}. Purse: {Shop.Fmt(pl.Copper)}");
        }
    }
}

// ── Post-wave gathering: Artisans, Hunters, and Gatherers (pick ONE) ────────
void GatherAfterWave(Player pl)
{
    bool isArt = pl.CharacterType == "Artisan";
    int huntTier = pl.GetFeatStacks("Hunter") + (isArt ? 1 : 0);
    int gathTier = pl.GetFeatStacks("Gatherer") + (isArt ? 1 : 0);
    bool hasKnife = isArt
        || (pl.HeldWeapon ?? "").Contains("Dagger") || (pl.SecondaryWeapon ?? "").Contains("Dagger")
        || (pl.HeldWeapon ?? "").Contains("Knife") || (pl.SecondaryWeapon ?? "").Contains("Knife");

    var gopts = new List<string>();
    if (huntTier > 0) gopts.Add("[1] Hunt animals");
    if (gathTier > 0) { gopts.Add("[2] Mine rocks"); gopts.Add("[3] Cut trees"); }
    if (gathTier >= 3) gopts.Add("[4] Mine AND cut");
    if (!gopts.Any()) { Console.WriteLine("  You have no gathering skills (Hunter or Gatherer feats grant them)."); return; }
    Console.WriteLine($"  Gather: {string.Join("  ", gopts)}");
    Console.Write("  Choice: ");
    string gc = (GameIO.ReadLine() ?? "").Trim().ToLower();

    if (gc is "1" or "hunt" && huntTier > 0 && !hasKnife)
    {
        Console.WriteLine("  You need a dagger or knife to skin your kills!");
        return;
    }
    if (gc is "1" or "hunt" && huntTier >= 2)
    {
        // Tiered hunts: all deer → +boar → +wolves → +bears
        int animals = rng.Next(1, 10) - 1;                       // all the deer
        string bag = $"{animals} deer";
        if (huntTier >= 3) { int b = rng.Next(1, 6) - 1; animals += b; bag += $", {b} boar"; }
        if (huntTier >= 4) { int wv = rng.Next(1, 7) - 1; animals += wv; bag += $", {wv} wolves"; }
        if (huntTier >= 5) { int br = rng.Next(1, 5) - 1; animals += br; bag += $", {br} bears"; }
        int hHides = 0, hMeat = 0;
        for (int i = 0; i < animals; i++)
        {
            hHides += rng.Next(1, 10) - 1;
            hMeat += Math.Max(0, rng.Next(1, 7) + rng.Next(1, 7) - 2);
        }
        hHides = Math.Min(hHides, pl.PackRoom); pl.Hides += hHides;
        hMeat = Math.Min(hMeat, pl.PackRoom); pl.Meat += hMeat;
        Console.WriteLine($"  Great hunt! You bring down {bag}: +{hHides} hides, +{hMeat} meat." +
            (pl.PackRoom == 0 ? "  [Pack FULL]" : ""));
        return;
    }
    if (gc is "4" or "both" && gathTier >= 3)
    {
        int nodes = rng.Next(1, 7) + 4;      // every rock
        int trees2 = rng.Next(1, 7) + 4;     // every tree
        int extra = gathTier >= 4 ? 1 : 0;   // +1d4 bonus per node at final tier
        int tOre = 0, tStone = 0, tWood = 0;
        for (int i = 0; i < nodes; i++)
        {
            tOre += rng.Next(1, 6) - 1 + (extra > 0 ? rng.Next(1, 5) : 0);
            tStone += rng.Next(1, 5) + rng.Next(1, 5) - 1 + (extra > 0 ? rng.Next(1, 5) : 0);
        }
        for (int i = 0; i < trees2; i++)
            tWood += rng.Next(1, 4) + rng.Next(1, 4) + rng.Next(1, 4) + (extra > 0 ? rng.Next(1, 5) : 0);
        tOre = Math.Min(tOre, pl.PackRoom); pl.Ore += tOre;
        tStone = Math.Min(tStone, pl.PackRoom); pl.Stone += tStone;
        tWood = Math.Min(tWood, pl.PackRoom); pl.Wood += tWood;
        Console.WriteLine($"  You strip the land bare: {nodes} rocks and {trees2} trees — +{tOre} ore, +{tStone} stone, +{tWood} wood." +
            (pl.PackRoom == 0 ? "  [Pack FULL]" : ""));
        return;
    }
    int count = gathTier >= 2 && gc is "2" or "3" or "mine" or "cut" ? rng.Next(1, 7) + 4   // ALL of one kind
              : rng.Next(1, 4);
    int extraYield = gathTier >= 4 ? 1 : 0;
    if (gc is "1" or "hunt" && huntTier > 0)
    {
        int hides = 0, meat = 0;
        for (int i = 0; i < count; i++)
        {
            hides += rng.Next(1, 10) - 1;
            meat += Math.Max(0, rng.Next(1, 7) + rng.Next(1, 7) - 2);
        }
        hides = Math.Min(hides, pl.PackRoom); pl.Hides += hides;
        meat = Math.Min(meat, pl.PackRoom); pl.Meat += meat;
        Console.WriteLine($"  You bring down {count} animal(s): +{hides} hides, +{meat} meat. (Hides {pl.Hides}, Meat {pl.Meat})" +
            (pl.PackRoom == 0 ? "  [Pack FULL]" : ""));
    }
    else if (gc is "2" or "mine" && gathTier > 0)
    {
        int ore = 0, stone = 0;
        for (int i = 0; i < count; i++)
        {
            ore += rng.Next(1, 6) - 1 + (extraYield > 0 ? rng.Next(1, 5) : 0);
            stone += rng.Next(1, 5) + rng.Next(1, 5) - 1 + (extraYield > 0 ? rng.Next(1, 5) : 0);
        }
        ore = Math.Min(ore, pl.PackRoom); pl.Ore += ore;
        stone = Math.Min(stone, pl.PackRoom); pl.Stone += stone;
        Console.WriteLine($"  You break down {count} rock(s): +{ore} ore, +{stone} stone. (Ore {pl.Ore}, Stone {pl.Stone})" +
            (pl.PackRoom == 0 ? "  [Pack FULL]" : ""));
    }
    else if (gc is "3" or "cut" && gathTier > 0)
    {
        int wood = 0;
        for (int i = 0; i < count; i++)
            wood += rng.Next(1, 4) + rng.Next(1, 4) + rng.Next(1, 4) + (extraYield > 0 ? rng.Next(1, 5) : 0);
        wood = Math.Min(wood, pl.PackRoom); pl.Wood += wood;
        Console.WriteLine($"  You fell {count} tree(s): +{wood} wood. (Wood {pl.Wood})" +
            (pl.PackRoom == 0 ? "  [Pack FULL]" : ""));
    }
    else Console.WriteLine("  You decide to save your strength.");
}

// ── Artisan workshop: craft, trade, and sell materials ─────────────────────
void VisitCrafting(Player pl)
{
    // Total materials needed = price / per, shrinking 1 per level (min 1),
    // split across material types by recipe percentages.
    bool TryCraft(string item, long price, int per, int wPct, int sPct, int oPct, int hPct)
    {
        int total = (int)Math.Max(1, price / per - (pl.Level - 1));
        int sN = total * sPct / 100, oN = total * oPct / 100, hN = total * hPct / 100;
        int wN = Math.Max(0, total - sN - oN - hN);
        Console.WriteLine($"  {item} needs {total} materials: {wN} wood, {sN} stone, {oN} ore, {hN} hides.");
        if (pl.Wood < wN || pl.Stone < sN || pl.Ore < oN || pl.Hides < hN)
        {
            Console.WriteLine($"  Not enough materials. (Wood {pl.Wood}, Stone {pl.Stone}, Ore {pl.Ore}, Hides {pl.Hides})");
            return false;
        }
        pl.Wood -= wN; pl.Stone -= sN; pl.Ore -= oN; pl.Hides -= hN;
        return true;
    }

    // The artisan chooses who receives each crafted item
    Player craftFor = pl;
    if (allPlayers.Count > 1)
    {
        for (int i = 0; i < allPlayers.Count; i++) Console.Write($"[{i + 1}]{allPlayers[i].Name}{(allPlayers[i] == pl ? " (you)" : "")}  ");
        Console.Write("\n  Craft for whom (Enter = yourself): ");
        if (int.TryParse(GameIO.ReadLine()?.Trim(), out int cf) && cf >= 1 && cf <= allPlayers.Count)
            craftFor = allPlayers[cf - 1];
        Console.WriteLine($"  Crafting for {craftFor.Name}.");
    }

    while (true)
    {
        Console.WriteLine($"\n═══ ARTISAN WORKSHOP ═══  Wood {pl.Wood}  Stone {pl.Stone}  Ore {pl.Ore}  Hides {pl.Hides}  Meat {pl.Meat}" +
            (craftFor != pl ? $"  (crafting for {craftFor.Name})" : ""));
        Console.WriteLine("  [1] Craft arrows  [2] Craft weapon  [3] Craft shield  [4] Craft armor  [5] Trade to ally  [6] Sell materials  [7] Craft bag" +
            (pl.HasFeat("Magic Crafting") ? "  [8] Magic crafting" : "") + "  [9] Leave");
        Console.Write("  Choice: ");
        string c = (GameIO.ReadLine() ?? "9").Trim().ToLower();
        if (c is "9" or "leave" or "x" or "") break;

        if (c is "7" or "bag")
        {
            bool artisanTarget = craftFor.CharacterType == "Artisan";
            int gain = artisanTarget ? 10 : 4;
            int cost = Math.Max(2, craftFor.CarryCap / (artisanTarget ? 10 : 2));
            Console.WriteLine($"  Bag for {craftFor.Name}: +{gain} pack space, costs {cost} hide(s). (You have {pl.Hides}.)");
            Console.Write("  Craft it? (y/n): ");
            if (!(GameIO.ReadLine() ?? "n").Trim().ToLower().StartsWith("y")) continue;
            if (pl.Hides < cost) { Console.WriteLine("  Not enough hides."); continue; }
            pl.Hides -= cost;
            craftFor.CarryCap += gain;
            Console.WriteLine($"  Stitched and strapped! {craftFor.Name}'s pack space is now {craftFor.CarryCap}.");
            continue;
        }

        if (c is "8" or "magic" && pl.HasFeat("Magic Crafting"))
        {
            Console.WriteLine("  Magic crafting (materials = price/15, -1 per level):");
            Console.WriteLine("  [1] Returning Quiver (ammo flies back)   [2] Mirror Shield (reflects spells 35%)   [3] Bag of Holding (limitless)");
            Console.Write("  Craft (or Enter to go back): ");
            string mci = (GameIO.ReadLine() ?? "").Trim();
            (string Name, long BasePrice) mdef = mci switch
            {
                "1" => ("Returning Quiver", Shop.Price["Returning Quiver"]),
                "2" => ("Mirror Shield", Shop.Price["Mirror Shield"]),
                "3" => ("Bag of Holding", Shop.Price["Bag of Holding"]),
                _ => ("", 0),
            };
            if (mdef.Name == "") continue;
            if (!TryCraft(mdef.Name, mdef.BasePrice, 15, 20, 20, 40, 20)) continue;
            switch (mdef.Name)
            {
                case "Returning Quiver":
                    craftFor.HasReturningAmmo = true;
                    Console.WriteLine($"  {craftFor.Name}'s ammo now returns unbroken!");
                    break;
                case "Mirror Shield":
                    if (craftFor.OffHandShieldName != null)
                    {
                        craftFor.ArmorDamageReduction -= craftFor.OffHandShieldDefense;
                        craftFor.MaxBlock -= craftFor.OffHandShieldBlock;
                        Console.WriteLine($"  {craftFor.Name} sets aside the {craftFor.OffHandShieldName}.");
                    }
                    craftFor.OffHandShieldName = "Mirror Shield";
                    craftFor.OffHandShieldBlock = 2; craftFor.OffHandShieldDefense = 0;
                    craftFor.MaxBlock += 2;
                    Console.WriteLine($"  {craftFor.Name} raises the gleaming Mirror Shield!");
                    break;
                case "Bag of Holding":
                    craftFor.HasBagOfHolding = true;
                    Console.WriteLine($"  {craftFor.Name}'s Bag of Holding is bottomless. Carry capacity: limitless.");
                    break;
            }
            continue;
        }

        if (c is "1" or "arrows")
        {
            Console.WriteLine("  [1] Arrow  [2] Blunt  [3] Barbed (needs ore)  [4] Spiral (needs ore)");
            Console.Write("  Type: ");
            string at = (GameIO.ReadLine() ?? "").Trim();
            (string Name, long Price, int oPct) def = at switch
            {
                "2" => ("Blunt Arrow", 1, 0),
                "3" => ("Barbed Arrow", Shop.Silver, 30),
                "4" => ("Spiral Arrow", 3 * Shop.Silver, 30),
                _ => ("Arrow", 1, 0),
            };
            Console.Write("  How many: ");
            if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int n) || n <= 0) continue;
            int made = 0;
            for (int i = 0; i < n; i++)
            {
                if (!TryCraft(def.Name, def.Price, 1, 50 - def.oPct + 20, 15, def.oPct, 15)) break;
                made++;
            }
            if (made > 0)
            {
                switch (def.Name)
                {
                    case "Blunt Arrow": craftFor.BluntArrows += made; break;
                    case "Barbed Arrow": craftFor.BarbedArrows += made; break;
                    case "Spiral Arrow": craftFor.SpiralArrows += made; break;
                    default: craftFor.ArrowCount += made; break;
                }
                Console.WriteLine($"  Crafted {made}x {def.Name} for {craftFor.Name}!");
            }
        }
        else if (c is "2" or "weapon")
        {
            var stock = new[] { "Dagger", "Club", "Quarterstaff", "Staff", "Short Sword", "Hand Axe",
                                "Mace", "Sword", "Long Staff", "Rapier Sword", "Long Sword", "Bow",
                                "Halberd", "Great Sword", "Battle Axe", "War Mace", "Axe", "Pike",
                                "Claymore", "Great Axe", "Warhammer" };
            for (int i = 0; i < stock.Length; i++)
                Console.WriteLine($"  [{i + 1,2}] {stock[i],-13} — {Math.Max(1, Shop.Price[stock[i]] / 25 - (pl.Level - 1))} materials");
            Console.Write("  Craft # (or Enter to go back): ");
            if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int wi) || wi < 1 || wi > stock.Length) continue;
            string wname = stock[wi - 1];
            if (!TryCraft(wname, Shop.Price[wname], 25, 15, 0, 60, 25)) continue;
            if (craftFor.HeldWeapon == null) { craftFor.HeldWeapon = wname; Console.WriteLine($"  {craftFor.Name} wields the freshly forged {wname}!"); }
            else if (craftFor.SecondaryWeapon == null) { craftFor.SecondaryWeapon = wname; Console.WriteLine($"  {craftFor.Name} stows the forged {wname}."); }
            else
            {
                Console.Write($"  {craftFor.Name}'s hands are full! Scrap [H]eld {craftFor.HeldWeapon} or [S]econdary {craftFor.SecondaryWeapon}? ([N]either cancels): ");
                string sc = (GameIO.ReadLine() ?? "n").Trim().ToLower();
                if (sc.StartsWith("h")) { Console.WriteLine($"  The {craftFor.HeldWeapon} is scrapped."); craftFor.HeldWeapon = wname; }
                else if (sc.StartsWith("s")) { Console.WriteLine($"  The {craftFor.SecondaryWeapon} is scrapped."); craftFor.SecondaryWeapon = wname; }
                else { Console.WriteLine("  The materials are set aside... and honestly, lost in the pile."); }
                Console.WriteLine($"  {craftFor.Name} now carries: {craftFor.HeldWeapon} + {craftFor.SecondaryWeapon}.");
            }
        }
        else if (c is "3" or "shield")
        {
            var shields = Shop.Shields.Keys.ToArray();
            for (int i = 0; i < shields.Length; i++)
                Console.WriteLine($"  [{i + 1}] {shields[i],-12} — {Math.Max(1, Shop.Price[shields[i]] / 25 - (pl.Level - 1))} materials");
            Console.Write("  Craft # (or Enter to go back): ");
            if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int si) || si < 1 || si > shields.Length) continue;
            string sname = shields[si - 1];
            if (!TryCraft(sname, Shop.Price[sname], 25, 50, 0, 30, 20)) continue;
            if (craftFor.OffHandShieldName != null)
            {
                craftFor.ArmorDamageReduction -= craftFor.OffHandShieldDefense;
                craftFor.MaxBlock -= craftFor.OffHandShieldBlock;
                Console.WriteLine($"  {craftFor.Name} sets aside the {craftFor.OffHandShieldName}.");
            }
            var st = Shop.Shields[sname];
            craftFor.OffHandShieldName = sname;
            craftFor.OffHandShieldBlock = st.Block;
            craftFor.OffHandShieldDefense = st.Def;
            craftFor.MaxBlock += st.Block;
            craftFor.ArmorDamageReduction += st.Def;
            Console.WriteLine($"  {craftFor.Name} straps on the crafted {sname}! Block {craftFor.MinBlock}-{craftFor.MaxBlock}.");
        }
        else if (c is "4" or "armor" or "armour")
        {
            var alist = Shop.Armors.Keys.ToArray();
            for (int i = 0; i < alist.Length; i++)
            {
                var a = Shop.Armors[alist[i]];
                bool magicNeeded = alist[i] is "Rune Armor" or "Scribed Robes";
                Console.WriteLine($"  [{i + 1,2}] {alist[i],-14}{(a.Under ? " [under]" : "        ")} — {Math.Max(1, a.Cost / 15 - (pl.Level - 1))} materials{(magicNeeded ? " (needs magic or prayers)" : "")}");
            }
            Console.Write("  Craft # (or Enter to go back): ");
            if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int ai) || ai < 1 || ai > alist.Length) continue;
            string aname = alist[ai - 1];
            if (aname is "Rune Armor" or "Scribed Robes" && !pl.KnownSpells.Any() && !pl.CanPray && !pl.HasFeat("Magic Crafting"))
            {
                Console.WriteLine("  Rune-work demands magic, prayers, or the Magic Crafting feat.");
                continue;
            }
            var adef = Shop.Armors[aname];
            if (!TryCraft(aname, adef.Cost, 15, 10, 25, 40, 25)) continue;
            string oldA = adef.Under ? craftFor.UnderArmor : craftFor.MainArmor;
            if (oldA.Length > 0 && oldA != "Cloth" && Shop.Armors.TryGetValue(oldA, out var oldDef))
            {
                craftFor.ArmorDamageReduction -= oldDef.MeleeDR;
                craftFor.ArmorSpellDR -= oldDef.SpellDR;
                craftFor.ArmorPrayerDR -= oldDef.PrayerDR;
                Console.WriteLine($"  {craftFor.Name} sets aside the {oldA}.");
            }
            if (adef.Under) craftFor.UnderArmor = aname; else craftFor.MainArmor = aname;
            craftFor.ArmorDamageReduction += adef.MeleeDR;
            craftFor.ArmorSpellDR += adef.SpellDR;
            craftFor.ArmorPrayerDR += adef.PrayerDR;
            int pM = Shop.Armors.TryGetValue(craftFor.MainArmor, out var am2) ? am2.AbsorbPct : 0;
            int pU = Shop.Armors.TryGetValue(craftFor.UnderArmor, out var au2) ? au2.AbsorbPct : 0;
            craftFor.ArmorAbsorbPct = 100 - (100 - pM) * (100 - pU) / 100;
            craftFor.ArmorMetal = (Shop.Armors.TryGetValue(craftFor.MainArmor, out var mm) && mm.Metal)
                         || (Shop.Armors.TryGetValue(craftFor.UnderArmor, out var mu) && mu.Metal);
            Console.WriteLine($"  {craftFor.Name} dons the crafted {aname}!");
        }
        else if (c is "5" or "trade")
        {
            var others = allPlayers.Where(o2 => o2 != pl).ToList();
            if (!others.Any()) { Console.WriteLine("  No allies to trade with."); continue; }
            for (int i = 0; i < others.Count; i++) Console.Write($"[{i + 1}]{others[i].Name}  ");
            Console.Write("\n  Trade to whom: ");
            if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int ti) || ti < 1 || ti > others.Count) continue;
            var ally = others[ti - 1];
            Console.WriteLine($"  Give: [1] Held ({pl.HeldWeapon ?? "nothing"})  [2] Secondary ({pl.SecondaryWeapon ?? "nothing"})  [3] Arrows  [4] Materials");
            Console.Write("  Choice: ");
            string tc = (GameIO.ReadLine() ?? "").Trim();
            if (tc == "1" && pl.HeldWeapon != null)
            {
                if (ally.HeldWeapon == null) ally.HeldWeapon = pl.HeldWeapon;
                else if (ally.SecondaryWeapon == null) ally.SecondaryWeapon = pl.HeldWeapon;
                else { Console.WriteLine($"  {ally.Name}'s hands are full."); continue; }
                Console.WriteLine($"  You hand your {pl.HeldWeapon} to {ally.Name}.");
                pl.HeldWeapon = null;
            }
            else if (tc == "2" && pl.SecondaryWeapon != null)
            {
                if (ally.HeldWeapon == null) ally.HeldWeapon = pl.SecondaryWeapon;
                else if (ally.SecondaryWeapon == null) ally.SecondaryWeapon = pl.SecondaryWeapon;
                else { Console.WriteLine($"  {ally.Name}'s hands are full."); continue; }
                Console.WriteLine($"  You hand your {pl.SecondaryWeapon} to {ally.Name}.");
                pl.SecondaryWeapon = null;
            }
            else if (tc == "3")
            {
                Console.Write($"  How many arrows (you have {pl.ArrowCount}): ");
                if (int.TryParse(GameIO.ReadLine()?.Trim(), out int na) && na > 0 && na <= pl.ArrowCount)
                { pl.ArrowCount -= na; ally.ArrowCount += na; Console.WriteLine($"  Gave {na} arrows to {ally.Name}."); }
            }
            else if (tc == "4")
            {
                Console.Write("  Which ([W]ood/[S]tone/[O]re/[H]ides) and how many, e.g. 'w 5': ");
                var parts = (GameIO.ReadLine() ?? "").Trim().ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[1], out int qm) && qm > 0)
                {
                    switch (parts[0][0])
                    {
                        case 'w' when pl.Wood >= qm: pl.Wood -= qm; ally.Wood += qm; Console.WriteLine($"  Gave {qm} wood."); break;
                        case 's' when pl.Stone >= qm: pl.Stone -= qm; ally.Stone += qm; Console.WriteLine($"  Gave {qm} stone."); break;
                        case 'o' when pl.Ore >= qm: pl.Ore -= qm; ally.Ore += qm; Console.WriteLine($"  Gave {qm} ore."); break;
                        case 'h' when pl.Hides >= qm: pl.Hides -= qm; ally.Hides += qm; Console.WriteLine($"  Gave {qm} hides."); break;
                        default: Console.WriteLine("  You don't have that many."); break;
                    }
                }
            }
        }
        else if (c is "6" or "sell")
        {
            Console.Write("  Sell which ([W]ood/[S]tone/[O]re/[H]ides/[M]eat) and how many, e.g. 'o 3': ");
            var parts = (GameIO.ReadLine() ?? "").Trim().ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[1], out int qs) || qs <= 0) continue;
            ref int stockRef = ref pl.Wood;
            string what = "wood";
            switch (parts[0][0])
            {
                case 's': stockRef = ref pl.Stone; what = "stone"; break;
                case 'o': stockRef = ref pl.Ore; what = "ore"; break;
                case 'h': stockRef = ref pl.Hides; what = "hides"; break;
                case 'm': stockRef = ref pl.Meat; what = "meat"; break;
            }
            if (stockRef < qs) { Console.WriteLine($"  You only have {stockRef} {what}."); continue; }
            long gain = 0;
            for (int i = 0; i < qs; i++) gain += rng.Next(1, 5) * pl.Level;   // 1d4 copper per level each
            stockRef -= qs;
            pl.Copper += gain;
            Console.WriteLine($"  Sold {qs} {what} for {Shop.Fmt(gain)}. Purse: {Shop.Fmt(pl.Copper)}");
        }
    }
}

// ══ SAVE / LOAD ═══════════════════════════════════════════════════════════
// One .sav per character (plain key=value lines), named from the character's
// name, in a "Galaxy Sky" folder on the Desktop. Plus a shared high-score
// board for the fallen.
//
// TWO THINGS TO REMEMBER:
//  1. SaveGame subtracts every active buff before writing (song/blessing/
//     redemption deltas), so a mid-combat autosave doesn't bake temporary
//     bonuses in permanently. New reversible buffs must be unwound here too.
//  2. TryLoadGame guards newer fields with dict.ContainsKey, so old saves
//     still load and simply default. Keep doing that — people have characters.

string GameSaveDir()
{
    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
        desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    string dir = Path.Combine(desktop, "Galaxy Sky");
    Directory.CreateDirectory(dir);
    return dir;
}

string SaveFilePath(string name)
{
    string safe = new string(name.Where(c => char.IsLetterOrDigit(c)).ToArray());
    if (string.IsNullOrEmpty(safe)) safe = "default";
    return Path.Combine(GameSaveDir(), $"{safe}.sav");
}

void SaveGame(Player p, int groups)
{
    string path = SaveFilePath(p.Name);
    // Strip temporary buff stat deltas so a mid-combat save (e.g. on level-up)
    // doesn't bake songs/blessings/redemption into the character permanently.
    int windAdj  = p.WindBonusReceived;
    int stoneAdj = p.StoneBonusReceived;
    int warAdj   = p.WarBonusReceived;
    int blessAdj = p.BlessBonusReceived;
    int redAdj   = p.RedemptionExtraHP;
    var lines = new List<string>
    {
        $"Name={p.Name}",
        $"CharacterType={p.CharacterType}",
        $"HP={Math.Min(p.HP, p.MaxHP - redAdj)}", $"MaxHP={p.MaxHP - redAdj}",
        $"MinAttack={p.MinAttack - warAdj - blessAdj}", $"MaxAttack={p.MaxAttack - warAdj - blessAdj}",
        $"MinDamage={p.MinDamage}", $"MaxDamage={p.MaxDamage}",
        $"MinDodge={p.MinDodge - windAdj - warAdj - blessAdj}", $"MaxDodge={p.MaxDodge - windAdj - warAdj - blessAdj}",
        $"MinGrapple={p.MinGrapple - warAdj - blessAdj}", $"MaxGrapple={p.MaxGrapple - warAdj - blessAdj}",
        $"MinGrappleDmg={p.MinGrappleDmg}", $"MaxGrappleDmg={p.MaxGrappleDmg}",
        $"MinBlock={p.MinBlock - windAdj - blessAdj}", $"MaxBlock={p.MaxBlock - windAdj - blessAdj}",
        $"MinParry={p.MinParry - windAdj - blessAdj}", $"MaxParry={p.MaxParry - windAdj - blessAdj}",
        $"MinBardSong={p.MinBardSong}", $"MaxBardSong={p.MaxBardSong}",
        $"MinPowerAtk={p.MinPowerAtk}", $"MaxPowerAtk={p.MaxPowerAtk}",
        $"MinLimbBreak={p.MinLimbBreak}", $"MaxLimbBreak={p.MaxLimbBreak}",
        $"MinPotionHeal={p.MinPotionHeal - 2 * warAdj}", $"MaxPotionHeal={p.MaxPotionHeal - 2 * warAdj}",
        $"SavedStatPoints={p.SavedStatPoints}",
        $"GearPointsAvailable={p.GearPointsAvailable}",
        $"AdditionalActions={p.AdditionalActions}",
        $"ArmorDamageReduction={p.ArmorDamageReduction - stoneAdj}",
        $"RingletBonus={p.RingletBonus}",
        $"Level={p.Level}", $"XP={p.XP}", $"PendingFeats={p.PendingFeats}",
        $"HasGoblinSword={p.HasGoblinSword}",
        $"OffhandMaxDamage={p.OffhandMaxDamage}",
        $"Feats={string.Join("|", p.Feats)}",
        $"FeatStacks={string.Join("|", p.FeatStacks.Select(kv => $"{kv.Key}:{kv.Value}"))}",
        $"GearCounts={string.Join("|", p.GearCounts.Select(kv => $"{kv.Key}:{kv.Value}"))}",
        $"KnownSpells={string.Join("|", p.KnownSpells)}",
        $"DuelistPoints={p.DuelistPoints}",
        $"ArrowCount={p.ArrowCount}",
        $"DaggerCount={p.DaggerCount}",
        $"AxeCount={p.AxeCount}",
        $"RagePoints={p.RagePoints}",
        $"MusicInstrument={p.MusicInstrument}",
        $"SongTokens={p.SongTokens}",
        $"PrayerUses={p.PrayerUses}",
        $"SpellUses={p.SpellUses}",
        $"Copper={p.Copper}",
        $"BluntArrows={p.BluntArrows}",
        $"BarbedArrows={p.BarbedArrows}",
        $"SpiralArrows={p.SpiralArrows}",
        $"Wood={p.Wood}", $"Stone={p.Stone}", $"Ore={p.Ore}", $"Hides={p.Hides}", $"Meat={p.Meat}",
        $"CarryCap={p.CarryCap}", $"BagUpgrades={p.BagUpgrades}",
        $"Gender={p.Gender}", $"Variant={p.Variant}",
        $"HairColor={p.HairColor}", $"HairLength={p.HairLength}", $"EyeColor={p.EyeColor}",
        $"Headwear={p.Headwear}", $"ClothingColor={p.ClothingColor}", $"FacialHair={p.FacialHair}",
        $"WeaponSpec={string.Join("|", p.WeaponSpec.Select(kv => $"{kv.Key}:{kv.Value}"))}",
        $"PotionsBoost={p.PotionsBoost}", $"PotionsHeal={p.PotionsHeal}",
        $"PotionsPoison={p.PotionsPoison}", $"PotionsRestore={p.PotionsRestore}",
        $"HasReturningAmmo={p.HasReturningAmmo}",
        $"MainArmor={p.MainArmor}",
        $"UnderArmor={p.UnderArmor}",
        $"ArmorSpellDR={p.ArmorSpellDR}",
        $"ArmorPrayerDR={p.ArmorPrayerDR}",
        $"ArmorAbsorbPct={p.ArmorAbsorbPct}",
        $"ArmorMetal={p.ArmorMetal}",
        $"HeldWeapon={p.HeldWeapon ?? ""}",
        $"SecondaryWeapon={p.SecondaryWeapon ?? ""}",
        $"Race={p.Race}",
        $"ElementalFocus={p.ElementalFocus}",
        $"SpellDamageBonus={p.SpellDamageBonus}",
        $"SpellAttackBonus={p.SpellAttackBonus}",
        $"PrayerHealBonus={p.PrayerHealBonus - 2 * warAdj}",
        $"RegenPerTurn={p.RegenPerTurn}",
        $"MovementBonus={p.MovementBonus}",
        $"MinMovement={p.MinMovement}", $"MaxMovement={p.MaxMovement}",
        $"MinSpellAtk={p.MinSpellAtk - warAdj - blessAdj}", $"MaxSpellAtk={p.MaxSpellAtk - warAdj - blessAdj}",
        $"MinSpellDmgBonus={p.MinSpellDmgBonus}", $"MaxSpellDmgBonus={p.MaxSpellDmgBonus}",
        $"MinRangedAtk={p.MinRangedAtk - warAdj - blessAdj}", $"MaxRangedAtk={p.MaxRangedAtk - warAdj - blessAdj}",
        $"MinRangedDmgBonus={p.MinRangedDmgBonus}", $"MaxRangedDmgBonus={p.MaxRangedDmgBonus}",
        $"ChiUses={p.ChiUses}",
        $"QuiverCap={p.QuiverCap}", $"PotionPouchCap={p.PotionPouchCap}",
        $"ThrowBandCap={p.ThrowBandCap}", $"HasQuiverOfHolding={p.HasQuiverOfHolding}",
        $"HasBagOfHolding={p.HasBagOfHolding}", $"HasWorkshopHammer={p.HasWorkshopHammer}",
        $"SpecialPotions={string.Join("|", p.SpecialPotions.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}:{kv.Value}"))}",
        $"EnchantedWeapons={string.Join("|", p.EnchantedWeapons.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}:{kv.Value}"))}",
        $"ArmorEnchant={p.ArmorEnchant}", $"ShieldEnchant={p.ShieldEnchant}", $"RobeWorn={p.RobeWorn}",
        $"PrayerHealMaxBonus={p.PrayerHealMaxBonus}", $"PrayerDmgBonus={p.PrayerDmgBonus}",
        $"PrayerDmgMaxBonus={p.PrayerDmgMaxBonus}", $"PrayerVsHp={p.PrayerVsHp}", $"PrayerVsHpMax={p.PrayerVsHpMax}",
        $"PrayerAbilBonus={p.PrayerAbilBonus}", $"PrayerAbilMaxBonus={p.PrayerAbilMaxBonus}", $"PrayerTurnsMax={p.PrayerTurnsMax}",
        $"SongDurMax={p.SongDurMax}", $"SongHeal={p.SongHeal}", $"SongHealMax={p.SongHealMax}",
        $"SongFear={p.SongFear}", $"SongFearMax={p.SongFearMax}",
        $"SpellRollBonus={p.SpellRollBonus}", $"SpellRollMaxBonus={p.SpellRollMaxBonus}", $"SpellDurMax={p.SpellDurMax}",
        $"SprintMaxBonus={p.SprintMaxBonus}", $"RunAwayBonus={p.RunAwayBonus}", $"RunAwayMaxBonus={p.RunAwayMaxBonus}",
        $"ArrowsCrafted={p.ArrowsCrafted}", $"ArrowsCraftedMax={p.ArrowsCraftedMax}",
        $"PotionDurBonus={p.PotionDurBonus}", $"PotionDurMaxBonus={p.PotionDurMaxBonus}",
        $"PotionBonus={p.PotionBonus}", $"PotionMaxBonus={p.PotionMaxBonus}",
        $"GoodsCollected={p.GoodsCollected}", $"GoodsCollectedMax={p.GoodsCollectedMax}",
        $"CraftedArmorBonus={p.CraftedArmorBonus}", $"CraftedArmorMaxBonus={p.CraftedArmorMaxBonus}",
        $"DisarmBonus={p.DisarmBonus}", $"DisarmMaxBonus={p.DisarmMaxBonus}",
        $"MoneyBonus={p.MoneyBonus}", $"MoneyMaxBonus={p.MoneyMaxBonus}",
        $"SpellRangeBonusFt={p.SpellRangeBonusFt}", $"PrayerRangeBonusFt={p.PrayerRangeBonusFt}",
        $"SongRangeBonusFt={p.SongRangeBonusFt}", $"RangedRangeBonusFt={p.RangedRangeBonusFt}",
        $"CraftedWeaponDmg={p.CraftedWeaponDmg}", $"ShieldBonus={p.ShieldBonus}", $"ShieldReflectPct={p.ShieldReflectPct}",
        $"BonusSongUses={p.BonusSongUses}", $"BonusSpellUses={p.BonusSpellUses}", $"BonusPrayerUses={p.BonusPrayerUses}",
        $"Strength={p.Strength}", $"Dexterity={p.Dexterity}", $"Intelligence={p.Intelligence}",
        $"Wisdom={p.Wisdom}", $"Constitution={p.Constitution}", $"Smarts={p.Smarts}",
        $"Charisma={p.Charisma}", $"Agility={p.Agility}",
        $"GroupsDefeated={groups}",
        $"OffHandShieldName={p.OffHandShieldName ?? ""}",
        $"OffHandShieldDefense={p.OffHandShieldDefense}",
        $"OffHandShieldBlock={p.OffHandShieldBlock}",
    };
    File.WriteAllLines(path, lines);
    Console.WriteLine($"  ✓ Game saved ({p.Name}).");
}

bool TryLoadGame(Player p, string filePath)
{
    try
    {
        var dict = File.ReadAllLines(filePath)
            .Where(l => l.Contains('='))
            .ToDictionary(
                l => l[..l.IndexOf('=')],
                l => l[(l.IndexOf('=') + 1)..]);

        string G(string k) => dict.GetValueOrDefault(k, "");
        int I(string k, int def = 0) => int.TryParse(G(k), out int v) ? v : def;
        bool B(string k) => G(k) == "True";

        p.Name = G("Name");
        p.CharacterType = G("CharacterType") is { Length: > 0 } ct ? ct : "Warrior";
        p.HP = I("HP"); p.MaxHP = I("MaxHP");
        p.MinAttack = I("MinAttack"); p.MaxAttack = I("MaxAttack");
        p.MinDamage = I("MinDamage"); p.MaxDamage = I("MaxDamage");
        p.MinDodge = I("MinDodge"); p.MaxDodge = I("MaxDodge");
        p.MinGrapple = I("MinGrapple"); p.MaxGrapple = I("MaxGrapple");
        p.MinGrappleDmg = I("MinGrappleDmg"); p.MaxGrappleDmg = I("MaxGrappleDmg");
        p.MinBlock = I("MinBlock"); p.MaxBlock = I("MaxBlock");
        p.MinParry = I("MinParry"); p.MaxParry = I("MaxParry");
        p.MinBardSong = I("MinBardSong"); p.MaxBardSong = I("MaxBardSong");
        p.MinPowerAtk = I("MinPowerAtk"); p.MaxPowerAtk = I("MaxPowerAtk");
        p.MinLimbBreak = I("MinLimbBreak"); p.MaxLimbBreak = I("MaxLimbBreak");
        p.MinPotionHeal = I("MinPotionHeal"); p.MaxPotionHeal = I("MaxPotionHeal");
        p.SavedStatPoints = I("SavedStatPoints");
        p.GearPointsAvailable = I("GearPointsAvailable");
        p.AdditionalActions = I("AdditionalActions");
        p.ArmorDamageReduction = I("ArmorDamageReduction");
        p.RingletBonus = I("RingletBonus");
        p.Level = I("Level"); p.XP = I("XP"); p.PendingFeats = I("PendingFeats");
        p.HasGoblinSword = B("HasGoblinSword");
        p.OffhandMaxDamage = I("OffhandMaxDamage");

        p.Feats = G("Feats").Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
        p.FeatStacks = G("FeatStacks").Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Split(':'))
            .Where(a => a.Length == 2 && int.TryParse(a[1], out _))
            .ToDictionary(a => a[0], a => int.Parse(a[1]));
        p.GearCounts = G("GearCounts").Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Split(':'))
            .Where(a => a.Length == 2 && int.TryParse(a[1], out _))
            .ToDictionary(a => a[0], a => int.Parse(a[1]));
        p.KnownSpells = G("KnownSpells").Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
        p.ElementalFocus = G("ElementalFocus");
        p.DuelistPoints = I("DuelistPoints");
        p.ArrowCount = I("ArrowCount");
        p.DaggerCount = I("DaggerCount");
        p.AxeCount = I("AxeCount");
        p.RagePoints = I("RagePoints");
        p.MusicInstrument = G("MusicInstrument");
        p.SongTokens = I("SongTokens");
        // Older saves lack use pools — grant full pools to classes that had the ability
        p.PrayerUses = dict.ContainsKey("PrayerUses") ? I("PrayerUses") : (p.CharacterType == "Priest" ? p.MaxPrayerUses() : 0);
        p.SpellUses  = dict.ContainsKey("SpellUses")  ? I("SpellUses")  : (p.KnownSpells.Any() ? p.MaxSpellUses() : 0);
        p.Copper = long.TryParse(G("Copper"), out long cop) ? cop : 0;
        p.BluntArrows = I("BluntArrows");
        p.BarbedArrows = I("BarbedArrows");
        p.SpiralArrows = I("SpiralArrows");
        p.Wood = I("Wood"); p.Stone = I("Stone"); p.Ore = I("Ore"); p.Hides = I("Hides"); p.Meat = I("Meat");
        p.CarryCap = dict.ContainsKey("CarryCap") ? I("CarryCap") : (p.CharacterType == "Artisan" ? 50 : 8);
        p.BagUpgrades = I("BagUpgrades");
        p.Gender = G("Gender") is { Length: > 0 } gd ? gd : "neutral";
        p.Variant = dict.ContainsKey("Variant") ? Math.Max(1, I("Variant")) : 1;
        p.HairColor = G("HairColor") is { Length: > 0 } appHc ? appHc : "black";
        p.HairLength = G("HairLength") is { Length: > 0 } appHl ? appHl : "short";
        p.EyeColor = G("EyeColor") is { Length: > 0 } appEc ? appEc : "brown";
        p.Headwear = G("Headwear") is { Length: > 0 } appHw ? appHw : "nothing";
        p.ClothingColor = G("ClothingColor") is { Length: > 0 } appCc ? appCc : "black";
        p.FacialHair = G("FacialHair") is { Length: > 0 } appFh ? appFh : "none";
        p.WeaponSpec = G("WeaponSpec").Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Split(':'))
            .Where(a => a.Length == 2 && int.TryParse(a[1], out _))
            .ToDictionary(a => a[0], a => int.Parse(a[1]));
        p.PotionsBoost = I("PotionsBoost"); p.PotionsHeal = I("PotionsHeal");
        p.PotionsPoison = I("PotionsPoison"); p.PotionsRestore = I("PotionsRestore");
        p.HasReturningAmmo = B("HasReturningAmmo");
        p.MainArmor = G("MainArmor") is { Length: > 0 } ma ? ma : "Cloth";
        p.UnderArmor = G("UnderArmor");
        p.ArmorSpellDR = I("ArmorSpellDR");
        p.ArmorPrayerDR = I("ArmorPrayerDR");
        p.ArmorAbsorbPct = I("ArmorAbsorbPct");
        p.ArmorMetal = B("ArmorMetal");
        if (dict.ContainsKey("HeldWeapon"))
            p.HeldWeapon = G("HeldWeapon") is { Length: > 0 } hw ? hw : null;
        else
            // Older saves never stored the held weapon — restore the class default
            p.HeldWeapon = p.CharacterType switch
            {
                "Mage"      => "Wand",
                "Warrior"   => "Hand Axe",
                "Duelist"   => "Rapier Sword",
                "Archer"    => "Bow",
                "Priest"    => "Mace",
                "Berserker" => "Great Axe",
                "Musician"  => "Short Sword",
                _           => null   // Martial Artist fights unarmed
            };
        p.SecondaryWeapon = G("SecondaryWeapon") is { Length: > 0 } sw2 ? sw2 : null;
        p.Race = G("Race") is { Length: > 0 } rc ? rc : "Human";
        p.SpellDamageBonus = I("SpellDamageBonus");
        p.SpellAttackBonus = I("SpellAttackBonus");
        p.PrayerHealBonus = I("PrayerHealBonus");
        p.RegenPerTurn = I("RegenPerTurn");
        p.MovementBonus = I("MovementBonus");
        p.MinMovement = Math.Max(1, I("MinMovement") is 0 ? 1 : I("MinMovement"));
        p.MaxMovement = I("MaxMovement") is 0 ? 6 : I("MaxMovement");
        p.MinSpellAtk = Math.Max(1, I("MinSpellAtk") is 0 ? 1 : I("MinSpellAtk"));
        p.MaxSpellAtk = I("MaxSpellAtk") is 0 ? 6 : I("MaxSpellAtk");
        p.MinSpellDmgBonus = I("MinSpellDmgBonus");
        p.MaxSpellDmgBonus = I("MaxSpellDmgBonus");
        p.MinRangedAtk = Math.Max(1, I("MinRangedAtk") is 0 ? 1 : I("MinRangedAtk"));
        p.MaxRangedAtk = I("MaxRangedAtk") is 0 ? 6 : I("MaxRangedAtk");
        p.MinRangedDmgBonus = I("MinRangedDmgBonus");
        p.MaxRangedDmgBonus = I("MaxRangedDmgBonus");

        p.GroupsDefeated = I("GroupsDefeated");
        if (dict.ContainsKey("ChiUses")) p.ChiUses = I("ChiUses");
        if (dict.ContainsKey("QuiverCap"))
        {
            p.QuiverCap = I("QuiverCap"); p.PotionPouchCap = I("PotionPouchCap");
            p.ThrowBandCap = I("ThrowBandCap");
            p.HasQuiverOfHolding = G("HasQuiverOfHolding") == "True";
        }
        if (dict.ContainsKey("HasBagOfHolding"))
        {
            p.HasBagOfHolding = G("HasBagOfHolding") == "True";
            p.HasWorkshopHammer = G("HasWorkshopHammer") == "True";
        }
        // Elixirs and enchantments (name:count|name:count)
        if (dict.ContainsKey("SpecialPotions"))
        {
            foreach (var part in G("SpecialPotions").Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                int ci = part.LastIndexOf(':');
                if (ci > 0 && int.TryParse(part[(ci + 1)..], out int cnt)) p.SpecialPotions[part[..ci]] = cnt;
            }
            foreach (var part in G("EnchantedWeapons").Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                int ci = part.LastIndexOf(':');
                if (ci > 0 && int.TryParse(part[(ci + 1)..], out int lvl)) p.EnchantedWeapons[part[..ci]] = lvl;
            }
            p.ArmorEnchant = I("ArmorEnchant");
            p.ShieldEnchant = I("ShieldEnchant");
            if (dict.ContainsKey("RobeWorn")) p.RobeWorn = G("RobeWorn");
        }
        else
        {
            // Older saves used a CarryCap sentinel for the Bag of Holding
            if (p.CarryCap >= 9999) p.HasBagOfHolding = true;
            if (p.CharacterType == "Artisan") p.HasWorkshopHammer = true;
        }
        // Point-buy investments — absent in older saves, so they default to 0
        if (dict.ContainsKey("PrayerHealMaxBonus"))
        {
            p.PrayerHealMaxBonus = I("PrayerHealMaxBonus"); p.PrayerDmgBonus = I("PrayerDmgBonus");
            p.PrayerDmgMaxBonus = I("PrayerDmgMaxBonus"); p.PrayerVsHp = I("PrayerVsHp"); p.PrayerVsHpMax = I("PrayerVsHpMax");
            p.PrayerAbilBonus = I("PrayerAbilBonus"); p.PrayerAbilMaxBonus = I("PrayerAbilMaxBonus"); p.PrayerTurnsMax = I("PrayerTurnsMax");
            p.SongDurMax = I("SongDurMax"); p.SongHeal = I("SongHeal"); p.SongHealMax = I("SongHealMax");
            p.SongFear = I("SongFear"); p.SongFearMax = I("SongFearMax");
            p.SpellRollBonus = I("SpellRollBonus"); p.SpellRollMaxBonus = I("SpellRollMaxBonus"); p.SpellDurMax = I("SpellDurMax");
            p.SprintMaxBonus = I("SprintMaxBonus"); p.RunAwayBonus = I("RunAwayBonus"); p.RunAwayMaxBonus = I("RunAwayMaxBonus");
            p.ArrowsCrafted = I("ArrowsCrafted"); p.ArrowsCraftedMax = I("ArrowsCraftedMax");
            p.PotionDurBonus = I("PotionDurBonus"); p.PotionDurMaxBonus = I("PotionDurMaxBonus");
            p.PotionBonus = I("PotionBonus"); p.PotionMaxBonus = I("PotionMaxBonus");
            p.GoodsCollected = I("GoodsCollected"); p.GoodsCollectedMax = I("GoodsCollectedMax");
            p.CraftedArmorBonus = I("CraftedArmorBonus"); p.CraftedArmorMaxBonus = I("CraftedArmorMaxBonus");
            p.DisarmBonus = I("DisarmBonus"); p.DisarmMaxBonus = I("DisarmMaxBonus");
            p.MoneyBonus = I("MoneyBonus"); p.MoneyMaxBonus = I("MoneyMaxBonus");
            p.SpellRangeBonusFt = I("SpellRangeBonusFt"); p.PrayerRangeBonusFt = I("PrayerRangeBonusFt");
            p.SongRangeBonusFt = I("SongRangeBonusFt"); p.RangedRangeBonusFt = I("RangedRangeBonusFt");
            p.CraftedWeaponDmg = I("CraftedWeaponDmg"); p.ShieldBonus = I("ShieldBonus"); p.ShieldReflectPct = I("ShieldReflectPct");
            p.BonusSongUses = I("BonusSongUses"); p.BonusSpellUses = I("BonusSpellUses"); p.BonusPrayerUses = I("BonusPrayerUses");
        }
        // Core traits — absent in older saves, so they default to 0
        if (dict.ContainsKey("Strength"))
        {
            p.Strength = I("Strength"); p.Dexterity = I("Dexterity");
            p.Intelligence = I("Intelligence"); p.Wisdom = I("Wisdom");
            p.Constitution = I("Constitution"); p.Smarts = I("Smarts");
            p.Charisma = I("Charisma"); p.Agility = I("Agility");
        }
        ApplyRaceTraits(p);   // re-derive racial behavioral flags for loaded characters
        // Off-hand shield — only present in newer saves
        if (dict.ContainsKey("OffHandShieldName"))
        {
            string sn           = G("OffHandShieldName");
            p.OffHandShieldName    = sn.Length > 0 ? sn : null;
            p.OffHandShieldDefense = I("OffHandShieldDefense");
            p.OffHandShieldBlock   = I("OffHandShieldBlock");
            // ArmorDamageReduction and MaxBlock were saved with the shield bonus
            // already baked in, so we do NOT re-add them here.
        }
        return true;
    }
    catch
    {
        Console.WriteLine("  Save file corrupted. Starting fresh.");
        return false;
    }
}

List<(string name, int wave, int level, string path)> ListSaves()
{
    var result = new List<(string, int, int, string)>();
    string dir = GameSaveDir();
    foreach (var f in Directory.GetFiles(dir, "*.sav").Where(f => !f.Contains("hiscores")))
    {
        try
        {
            var dict = File.ReadAllLines(f)
                .Where(l => l.Contains('='))
                .ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
            string name = dict.GetValueOrDefault("Name", "Unknown");
            int gd = int.TryParse(dict.GetValueOrDefault("GroupsDefeated", "0"), out int g) ? g : 0;
            int level = int.TryParse(dict.GetValueOrDefault("Level", "1"), out int lv) ? lv : 1;
            result.Add((name, gd + 1, level, f));
        }
        catch { }
    }
    return result.OrderByDescending(s => s.Item2).ThenByDescending(s => s.Item3).ToList();
}

void ShowHiscores()
{
    string scorePath = Path.Combine(GameSaveDir(), "owsath_hiscores.sav");
    if (!File.Exists(scorePath)) return;
    try
    {
        var scores = new List<(string name, int wave, int level)>();
        foreach (var line in File.ReadAllLines(scorePath))
        {
            string nm = "Unknown"; int wv = 0; int lv = 1;
            foreach (var part in line.Split('|'))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                string k = part[..eq], v = part[(eq + 1)..];
                if (k == "Name") nm = v;
                else if (k == "Wave" && int.TryParse(v, out int w)) wv = w;
                else if (k == "Level" && int.TryParse(v, out int l)) lv = l;
            }
            if (wv > 0) scores.Add((nm, wv, lv));
        }
        var top = scores.OrderByDescending(s => s.wave).ThenByDescending(s => s.level).Take(3).ToList();
        if (!top.Any()) return;
        Console.WriteLine("\n── Hall of the Fallen ── Top Waves ──");
        for (int i = 0; i < top.Count; i++)
            Console.WriteLine($"  #{i + 1}  {top[i].name,-22}  Wave {top[i].wave,3}  Level {top[i].level,3}");
    }
    catch { }
}

void UpdateHiscores(string name, int wave, int level)
{
    string scorePath = Path.Combine(GameSaveDir(), "owsath_hiscores.sav");
    var scores = new List<(string name, int wave, int level)>();
    if (File.Exists(scorePath))
    {
        try
        {
            foreach (var line in File.ReadAllLines(scorePath))
            {
                string nm = "Unknown"; int wv = 0; int lv = 1;
                foreach (var part in line.Split('|'))
                {
                    int eq = part.IndexOf('=');
                    if (eq < 0) continue;
                    string k = part[..eq], v = part[(eq + 1)..];
                    if (k == "Name") nm = v;
                    else if (k == "Wave" && int.TryParse(v, out int w)) wv = w;
                    else if (k == "Level" && int.TryParse(v, out int l)) lv = l;
                }
                if (wv > 0) scores.Add((nm, wv, lv));
            }
        }
        catch { }
    }
    scores.Add((name, wave, level));
    scores = scores.OrderByDescending(s => s.wave).ThenByDescending(s => s.level).Take(10).ToList();
    File.WriteAllLines(scorePath, scores.Select(s => $"Name={s.name}|Wave={s.wave}|Level={s.level}"));
    Console.WriteLine($"  Score recorded: {name}  Wave {wave}  Level {level}");
}

} // ── end RunGameLogic ─────────────────────────────────────────────────────

// ═══════════════════════════════════════════════════════════════════════════
// CLASSES
// ═══════════════════════════════════════════════════════════════════════════

// ── Size categories: 0 = small, 1 = medium, 2 = large ──────────────────────
// Small (goblins, gnomes, hobbits): nimble underdogs — bonus attack/dodge vs
// bigger foes, weak-spot damage vs large, but -1 HP and -1 melee dmg vs medium+.
// Giants and Ogres are large with their own clumsiness tables. Bonuses stack.
