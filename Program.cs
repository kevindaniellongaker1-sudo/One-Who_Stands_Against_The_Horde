using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

// ── Graphics window (main thread) + game logic (background thread) ────────

var sharedState = new SharedGameState();

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
if (int.TryParse((Console.ReadLine() ?? "").Trim(), out int npInput) && npInput >= 2 && npInput <= 5)
    numPlayers = npInput;

for (int pi = 1; pi <= numPlayers; pi++)
{
    if (numPlayers > 1) Console.WriteLine($"\n══ Player {pi} ══");
    var p = new Player(rng);

    Console.WriteLine("[N]ew character  [L]oad saved character");
    Console.Write("Choice: ");
    string choice = (Console.ReadLine() ?? "n").Trim().ToLower();

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
            string pick = (Console.ReadLine() ?? "").Trim();
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
    allPlayers.Add(p);
}

var player = allPlayers[0];
groupsDefeated = allPlayers.Min(p => p.GroupsDefeated);

Console.WriteLine($"\nParty: {string.Join(", ", allPlayers.Select(p => p.Name))}");
if (allPlayers.Count > 1)
    Console.WriteLine($"Starting at Wave {groupsDefeated + 1} (lowest character's progress).");
Console.WriteLine($"HP: {player.HP}/{player.MaxHP}\n");

if (player.PendingFeats > 0) SelectFeats(player);
foreach (var ep in allPlayers.Skip(1).Where(p => p.PendingFeats > 0)) SelectFeats(ep);

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

    // Each player decides: move forward, rest, go home, or craft arrows
    var goingHome = new List<Player>();
    foreach (var pl in allPlayers.ToList())
    {
        Console.WriteLine($"\n{pl.Name} ({pl.CharacterType}, HP {pl.HP}/{pl.MaxHP}, Lv {pl.Level}):");
        Console.WriteLine("  [1] Move forward  [2] Rest  [3] Go home" + (pl.CharacterType == "Archer" ? "  [4] Craft arrows" : ""));
        Console.Write("  Choice: ");
        string next = (Console.ReadLine() ?? "1").Trim().ToLower();
        if (next is "3" or "home" or "quit" or "q" or "go home")
        {
            Console.WriteLine($"  {pl.Name} heads home. Well done!");
            SaveGame(pl, groupsDefeated);
            goingHome.Add(pl);
        }
        else if (next is "2" or "rest" or "heal")
        {
            int dice = 1 + pl.GetFeatStacks("Potion Brewer");
            int recovered = 0;
            for (int d = 0; d < dice; d++) recovered += rng.Next(pl.MinPotionHeal, pl.MaxPotionHeal + 1);
            recovered = Math.Min(recovered, pl.MaxHP - pl.HP);
            pl.HP += recovered;
            Console.WriteLine($"  {pl.Name} rests and recovers {recovered} HP. ({pl.HP}/{pl.MaxHP})");
            if (pl.CharacterType == "Duelist")
            {
                int maxPts = pl.Level < 2 ? 0 : (pl.Level <= 20 ? (pl.Level - 2) / 3 + 1 : 7 + 2 * ((pl.Level - 20) / 3));
                pl.DuelistPoints = maxPts;
                Console.WriteLine($"  Duelist Points restored to {maxPts}.");
            }
            if (pl.CanPray)     { pl.PrayerUses = pl.MaxPrayerUses(); Console.WriteLine($"  Prayers restored to {pl.PrayerUses}."); }
            if (pl.KnownSpells.Any()) { pl.SpellUses = pl.MaxSpellUses(); Console.WriteLine($"  Spell casts restored to {pl.SpellUses}."); }
            if (pl.CanSing)     { pl.SongTokens = pl.MaxSongTokens(); Console.WriteLine($"  Song tokens restored to {pl.SongTokens}."); }
        }
        else if (next is "4" or "craft" or "arrows")
        {
            if (pl.CharacterType == "Archer") { pl.ArrowCount += 50; Console.WriteLine($"  {pl.Name} crafts 50 arrows. Total: {pl.ArrowCount}."); }
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
        int slots = Math.Min(waveNum - 40, 10);
        int trolls = Math.Max(0, 10 - slots);
        for (int i = 0; i < trolls; i++) g.Add(Troll.RandType(r, $"Troll {i + 1}"));
        for (int i = 0; i < slots && i < 12; i++)
        {
            int crMax = waveNum >= 71 ? 13 : 11;
            int cr = r.Next(1, crMax);
            switch (cr)
            {
                case 1: case 2: g.Add(new Ogre(r, $"Ogre {i + 1}")); break;
                case 3: for (int j = 0; j < 3; j++) g.Add(Orc.RandType(r, $"Orc {i * 3 + j + 1}")); break;
                case 4: g.Add(Troll.RandType(r, $"Troll {i * 2 + 1}")); g.Add(Troll.RandType(r, $"Troll {i * 2 + 2}")); break;
                case 5: for (int j = 0; j < 4; j++) g.Add(Hobgoblin.RandType(r, $"Hobgoblin {i * 4 + j + 1}")); break;
                case 6: g.Add(new GoblinShaman(r, $"Goblin Shaman {i+1}")); g.Add(new GoblinWarrior(r, $"Goblin Warrior {i*2+1}")); g.Add(new GoblinWarrior(r, $"Goblin Warrior {i*2+2}")); for (int j = 0; j < 2; j++) g.Add(Goblin.RandType(r, $"Goblin {i * 2 + j + 1}")); break;
                case 7: if (waveNum >= 51) g.Add(new SpellGoblin(r, $"Spell Goblin {i + 1}")); else for (int j = 0; j < 3; j++) g.Add(Orc.RandType(r, $"Orc {i * 3 + j + 1}")); break;
                case 8: if (waveNum >= 51) { g.Add(new SpellGoblin(r, $"Spell Goblin {i*2 + 1}")); g.Add(new SpellGoblin(r, $"Spell Goblin {i*2 + 2}")); } else for (int j = 0; j < 4; j++) g.Add(Hobgoblin.RandType(r, $"Hobgoblin {i * 4 + j + 1}")); break;
                case 9: if (waveNum >= 61) g.Add(new OrcBarbarian(r, $"Orc Barbarian {i + 1}")); else g.Add(Troll.RandType(r, $"Troll {i + 1}")); break;
                case 10: g.Add(new RogueGoblin(r, $"Rogue Goblin {i*2+1}")); g.Add(new RogueGoblin(r, $"Rogue Goblin {i*2+2}")); break;
                case 11: g.Add(new NecromancerTroll(r, $"Necromancer Troll {i + 1}")); break;
                case 12: g.Add(new NecromancerTroll(r, $"Necromancer Troll {i + 1}")); g.Add(new Troll(r, $"Troll Thrall {i + 1}")); break;
                default: if (waveNum >= 61) { g.Add(new OrcBarbarian(r, $"Orc Barbarian {i*2 + 1}")); g.Add(new OrcBarbarian(r, $"Orc Barbarian {i*2 + 2}")); } else { g.Add(Troll.RandType(r, $"Troll {i*2 + 1}")); g.Add(Troll.RandType(r, $"Troll {i*2 + 2}")); } break;
            }
        }
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
    }
    return g;
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

void SelectFeats(Player p)
{
    while (p.PendingFeats > 0)
    {
        Console.WriteLine("\n═══ FEAT SELECTION ═══");
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

        Console.Write($"Select feat (1-{avail.Count}): ");
        if (int.TryParse(Console.ReadLine()?.Trim(), out int fi) && fi >= 1 && fi <= avail.Count)
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
                string el = (Console.ReadLine() ?? "").Trim().ToLower();
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
                    string ir = (Console.ReadLine() ?? "").Trim();
                    string pick = "Guitar";
                    if (int.TryParse(ir, out int ii2) && ii2 >= 1 && ii2 <= insts.Length) pick = insts[ii2 - 1];
                    else { var m2 = insts.FirstOrDefault(x => x.StartsWith(ir, StringComparison.OrdinalIgnoreCase)); if (m2 != null) pick = m2; }
                    p.MusicInstrument = pick;
                    Console.WriteLine($"  Instrument: {pick}!");
                }
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
        }
        else Console.WriteLine("Invalid. Try again.");
    }
}

void SpendStatPoints(Player p)
{
    while (p.SavedStatPoints > 0)
    {
        Console.WriteLine($"\n═══ STAT POINTS: {p.SavedStatPoints} available ═══");
        Console.WriteLine("  ── 1 point: max stat ──");
        Console.WriteLine($"  [1]  Max Dodge→{p.MaxDodge+1}     [2]  Max Attack→{p.MaxAttack+1}     [3]  Max Grapple→{p.MaxGrapple+1}    [4]  Max Block→{p.MaxBlock+1}");
        Console.WriteLine($"  [5]  Max Melee Dmg→{p.MaxDamage+1}  [6]  Max HP→{p.MaxHP+1}       [7]  Max Parry→{p.MaxParry+1}      [8]  Max Bard Song→{p.MaxBardSong+1}");
        Console.WriteLine($"  [9]  Max Grapple Dmg→{p.MaxGrappleDmg+1}  [10] Max Potion Heal→{p.MaxPotionHeal+1}  [11] Max Power Atk→{p.MaxPowerAtk+1}  [12] Max Limb Break→{p.MaxLimbBreak+1}");
        Console.WriteLine($"  [28] Max Move→{p.MaxMovement+1}    [29] Max Spell Atk→{p.MaxSpellAtk+1}  [30] Max Spell Dmg→{p.MaxSpellDmgBonus+1}");
        Console.WriteLine($"  [31] Max Ranged Atk→{p.MaxRangedAtk+1}  [32] Max Ranged Dmg→{p.MaxRangedDmgBonus+1}");
        if (p.SavedStatPoints >= 2)
        {
            Console.WriteLine("  ── 2 points: base stat ──");
            Console.WriteLine($"  [13] Base Dodge→{p.MinDodge+1}  [14] Base Attack→{p.MinAttack+1}  [15] Base Grapple→{p.MinGrapple+1}  [16] Base Block→{p.MinBlock+1}");
            Console.WriteLine($"  [17] Base Melee Dmg→{p.MinDamage+1}  [18] Base Parry→{p.MinParry+1}  [19] Base Bard Song→{p.MinBardSong+1}  [20] Base Grapple Dmg→{p.MinGrappleDmg+1}");
            Console.WriteLine($"  [21] Base Power Atk→{p.MinPowerAtk+1}  [22] Base Limb Break→{p.MinLimbBreak+1}  [23] Base Potion Heal→{p.MinPotionHeal+1}");
            Console.WriteLine($"  [33] Base Move→{p.MinMovement+1}  [34] Base Spell Atk→{p.MinSpellAtk+1}  [35] Base Spell Dmg→{p.MinSpellDmgBonus+1}");
            Console.WriteLine($"  [36] Base Ranged Atk→{p.MinRangedAtk+1}  [37] Base Ranged Dmg→{p.MinRangedDmgBonus+1}");
        }
        if (p.SavedStatPoints >= 3)
            Console.WriteLine("  ── 3 points: [24] Extra action/turn   [25] Gear point   [26] Keep saving (exit)");
        if (p.SavedStatPoints >= 4)
            Console.WriteLine("  ── 4 points: [27] Pick a feat");
        Console.Write("  Choice ([S]ave all for later): ");
        string raw = (Console.ReadLine() ?? "s").Trim().ToLower();
        if (raw == "s" || raw == "save") break;
        if (!int.TryParse(raw, out int ch)) { Console.WriteLine("  Invalid."); continue; }
        bool exitLoop = false;
        switch (ch)
        {
            case 1:  p.MaxDodge++;      p.SavedStatPoints--;  Console.WriteLine($"  Max Dodge → {p.MaxDodge}"); break;
            case 2:  p.MaxAttack++;     p.SavedStatPoints--;  Console.WriteLine($"  Max Attack → {p.MaxAttack}"); break;
            case 3:  p.MaxGrapple++;    p.SavedStatPoints--;  Console.WriteLine($"  Max Grapple → {p.MaxGrapple}"); break;
            case 4:  p.MaxBlock++;      p.SavedStatPoints--;  Console.WriteLine($"  Max Block → {p.MaxBlock}"); break;
            case 5:  p.MaxDamage++;     p.SavedStatPoints--;  Console.WriteLine($"  Max Damage → {p.MaxDamage}"); break;
            case 6:  p.MaxHP++;         p.SavedStatPoints--;  Console.WriteLine($"  Max HP → {p.MaxHP}"); break;
            case 7:  p.MaxParry++;      p.SavedStatPoints--;  Console.WriteLine($"  Max Parry → {p.MaxParry}"); break;
            case 8:  p.MaxBardSong++;   p.SavedStatPoints--;  Console.WriteLine($"  Max Bard Song → {p.MaxBardSong}"); break;
            case 9:  p.MaxGrappleDmg++; p.SavedStatPoints--;  Console.WriteLine($"  Max Grapple Dmg → {p.MaxGrappleDmg}"); break;
            case 10: p.MaxPotionHeal++; p.SavedStatPoints--;  Console.WriteLine($"  Max Potion Heal → {p.MaxPotionHeal}"); break;
            case 11: p.MaxPowerAtk++;   p.SavedStatPoints--;  Console.WriteLine($"  Max Power Atk → {p.MaxPowerAtk}"); break;
            case 12: p.MaxLimbBreak++;  p.SavedStatPoints--;  Console.WriteLine($"  Max Limb Break → {p.MaxLimbBreak}"); break;
            case 13 when p.SavedStatPoints >= 2: p.MinDodge++;      p.SavedStatPoints -= 2; Console.WriteLine($"  Min Dodge → {p.MinDodge}"); break;
            case 14 when p.SavedStatPoints >= 2: p.MinAttack++;     p.SavedStatPoints -= 2; Console.WriteLine($"  Min Attack → {p.MinAttack}"); break;
            case 15 when p.SavedStatPoints >= 2: p.MinGrapple++;    p.SavedStatPoints -= 2; Console.WriteLine($"  Min Grapple → {p.MinGrapple}"); break;
            case 16 when p.SavedStatPoints >= 2: p.MinBlock++;      p.SavedStatPoints -= 2; Console.WriteLine($"  Min Block → {p.MinBlock}"); break;
            case 17 when p.SavedStatPoints >= 2: p.MinDamage++;     p.SavedStatPoints -= 2; Console.WriteLine($"  Min Damage → {p.MinDamage}"); break;
            case 18 when p.SavedStatPoints >= 2: p.MinParry++;      p.SavedStatPoints -= 2; Console.WriteLine($"  Min Parry → {p.MinParry}"); break;
            case 19 when p.SavedStatPoints >= 2: p.MinBardSong++;   p.SavedStatPoints -= 2; Console.WriteLine($"  Min Bard Song → {p.MinBardSong}"); break;
            case 20 when p.SavedStatPoints >= 2: p.MinGrappleDmg++; p.SavedStatPoints -= 2; Console.WriteLine($"  Min Grapple Dmg → {p.MinGrappleDmg}"); break;
            case 21 when p.SavedStatPoints >= 2: p.MinPowerAtk++;   p.SavedStatPoints -= 2; Console.WriteLine($"  Min Power Atk → {p.MinPowerAtk}"); break;
            case 22 when p.SavedStatPoints >= 2: p.MinLimbBreak++;  p.SavedStatPoints -= 2; Console.WriteLine($"  Min Limb Break → {p.MinLimbBreak}"); break;
            case 23 when p.SavedStatPoints >= 2: p.MinPotionHeal++; p.SavedStatPoints -= 2; Console.WriteLine($"  Min Potion Heal → {p.MinPotionHeal}"); break;
            case 28: p.MaxMovement++;      p.SavedStatPoints--; Console.WriteLine($"  Max Move → {p.MaxMovement}"); break;
            case 29: p.MaxSpellAtk++;      p.SavedStatPoints--; Console.WriteLine($"  Max Spell Atk → {p.MaxSpellAtk}"); break;
            case 30: p.MaxSpellDmgBonus++; p.SavedStatPoints--; Console.WriteLine($"  Max Spell Dmg bonus → {p.MaxSpellDmgBonus}"); break;
            case 31: p.MaxRangedAtk++;     p.SavedStatPoints--; Console.WriteLine($"  Max Ranged Atk → {p.MaxRangedAtk}"); break;
            case 32: p.MaxRangedDmgBonus++;p.SavedStatPoints--; Console.WriteLine($"  Max Ranged Dmg bonus → {p.MaxRangedDmgBonus}"); break;
            case 33 when p.SavedStatPoints >= 2: p.MinMovement++;      p.SavedStatPoints -= 2; Console.WriteLine($"  Base Move → {p.MinMovement}"); break;
            case 34 when p.SavedStatPoints >= 2: p.MinSpellAtk++;      p.SavedStatPoints -= 2; Console.WriteLine($"  Base Spell Atk → {p.MinSpellAtk}"); break;
            case 35 when p.SavedStatPoints >= 2: p.MinSpellDmgBonus++; p.SavedStatPoints -= 2; Console.WriteLine($"  Base Spell Dmg bonus → {p.MinSpellDmgBonus}"); break;
            case 36 when p.SavedStatPoints >= 2: p.MinRangedAtk++;     p.SavedStatPoints -= 2; Console.WriteLine($"  Base Ranged Atk → {p.MinRangedAtk}"); break;
            case 37 when p.SavedStatPoints >= 2: p.MinRangedDmgBonus++;p.SavedStatPoints -= 2; Console.WriteLine($"  Base Ranged Dmg bonus → {p.MinRangedDmgBonus}"); break;
            case 24 when p.SavedStatPoints >= 3: p.AdditionalActions++; p.SavedStatPoints -= 3; Console.WriteLine($"  +1 action/turn! Bonus actions: {p.AdditionalActions}"); break;
            case 25 when p.SavedStatPoints >= 3: p.GearPointsAvailable++; p.SavedStatPoints -= 3; Console.WriteLine("  Gear point gained!"); SpendGearPoints(p); break;
            case 26 when p.SavedStatPoints >= 3: exitLoop = true; break;
            case 27 when p.SavedStatPoints >= 4: p.SavedStatPoints -= 4; p.PendingFeats++; SelectFeats(p); break;
            default: Console.WriteLine("  Invalid choice or insufficient points."); break;
        }
        if (exitLoop) break;
    }
    if (p.SavedStatPoints > 0) Console.WriteLine($"  {p.SavedStatPoints} stat point(s) saved for later.");
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
        if (int.TryParse(Console.ReadLine()?.Trim(), out int g) && g >= 1 && g <= avail.Count)
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
    if (int.TryParse(Console.ReadLine()?.Trim(), out int s) && s >= 1 && s <= available.Count)
    { p.KnownSpells.Add(available[s - 1]); Console.WriteLine($"  ✓ Learned: {available[s - 1]}!"); }
    else Console.WriteLine("  Invalid.");
}

void AskName(Player p)
{
    Console.Write("\nFirst name (or Enter for 'The Lone Warrior'): ");
    string first = (Console.ReadLine() ?? "").Trim();
    if (string.IsNullOrEmpty(first)) { SelectRace(p); return; }

    Console.Write("Middle name (or Enter to skip): ");
    string middle = (Console.ReadLine() ?? "").Trim();

    Console.Write("Last name: ");
    string last = (Console.ReadLine() ?? "").Trim();

    p.Name = string.IsNullOrEmpty(middle)
        ? $"{first} {last}".Trim()
        : $"{first} {middle} {last}".Trim();

    SelectRace(p);
}

void SelectRace(Player p)
{
    var races = new[]
    {
        "Moon Elf", "Human", "Stone Dwarf", "Light-Foot Hobbit",
        "Sun Elf", "Wood Elf", "Orc", "Goblin", "Troll", "Iron Dwarf", "Brave Minds Hobbit",
        "Gem Gnome", "Glass Gnome", "Hobgoblin", "Ogre"
    };
    Console.WriteLine("\nChoose your race:");
    Console.WriteLine("  [1]  Moon Elf          — +3 spell damage");
    Console.WriteLine("  [2]  Human             — pick a bonus feat");
    Console.WriteLine("  [3]  Stone Dwarf       — double starting HP");
    Console.WriteLine("  [4]  Light-Foot Hobbit — +3 dodge");
    Console.WriteLine("  [5]  Sun Elf           — +3 healing on prayers");
    Console.WriteLine("  [6]  Wood Elf          — +3 attack");
    Console.WriteLine("  [7]  Orc               — +3 melee damage");
    Console.WriteLine("  [8]  Goblin            — +1 dodge, +1 movement");
    Console.WriteLine("  [9]  Troll             — regenerate 2 HP per turn");
    Console.WriteLine("  [10] Iron Dwarf        — -3 damage taken");
    Console.WriteLine("  [11] Brave Minds Hobbit — +1 dodge, +1 attack, -1 damage taken");
    Console.WriteLine("  [12] Gem Gnome          — +1 movement, +2 to hit with spells");
    Console.WriteLine("  [13] Glass Gnome        — +1 min attack, +1 movement; spell targets may half-dodge");
    Console.WriteLine("  [14] Hobgoblin          — +1 movement, +1 base damage");
    Console.WriteLine("  [15] Ogre               — +2 base and max damage, double HP, half dodge, -1 movement");
    Console.Write("  Choice (1-15 or name): ");
    string raw = (Console.ReadLine() ?? "").Trim();
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

    switch (chosen)
    {
        case "Moon Elf":
            p.SpellDamageBonus = 3;
            Console.WriteLine("  [Race] Moon Elf: +3 spell damage.");
            break;
        case "Human":
        {
            Console.WriteLine("  [Race] Human: choose a bonus feat.");
            var available = FeatDef.All
                .Where(f => f.Prerequisite == null && !p.Feats.Contains(f.Name))
                .ToList();
            for (int fi = 0; fi < available.Count; fi++)
                Console.WriteLine($"    [{fi + 1}] {available[fi].Name} — {available[fi].Desc}");
            Console.Write($"  Choice (1-{available.Count}): ");
            if (int.TryParse((Console.ReadLine() ?? "").Trim(), out int fc) && fc >= 1 && fc <= available.Count)
            {
                p.AddFeat(available[fc - 1].Name);
                Console.WriteLine($"  Gained feat: {available[fc - 1].Name}!");
            }
            break;
        }
        case "Stone Dwarf":
            p.MaxHP *= 2;
            p.HP = p.MaxHP;
            Console.WriteLine($"  [Race] Stone Dwarf: HP doubled to {p.MaxHP}!");
            break;
        case "Light-Foot Hobbit":
            p.MaxDodge += 3;
            Console.WriteLine($"  [Race] Light-Foot Hobbit: +3 dodge (max {p.MaxDodge}).");
            break;
        case "Sun Elf":
            p.PrayerHealBonus = 3;
            Console.WriteLine("  [Race] Sun Elf: +3 to prayer healing.");
            break;
        case "Wood Elf":
            p.MaxAttack += 3;
            Console.WriteLine($"  [Race] Wood Elf: +3 attack (max {p.MaxAttack}).");
            break;
        case "Orc":
            p.MaxDamage += 3;
            Console.WriteLine($"  [Race] Orc: +3 melee damage (max {p.MaxDamage}).");
            break;
        case "Goblin":
            p.MaxDodge += 1;
            p.MovementBonus = 1;
            Console.WriteLine("  [Race] Goblin: +1 dodge, +1 movement per roll.");
            break;
        case "Troll":
            p.RegenPerTurn = 2;
            Console.WriteLine("  [Race] Troll: regenerate 2 HP per turn.");
            break;
        case "Iron Dwarf":
            p.ArmorDamageReduction += 3;
            Console.WriteLine($"  [Race] Iron Dwarf: -{p.ArmorDamageReduction} incoming damage.");
            break;
        case "Brave Minds Hobbit":
            p.MaxDodge += 1;
            p.MaxAttack += 1;
            p.ArmorDamageReduction += 1;
            Console.WriteLine("  [Race] Brave Minds Hobbit: +1 dodge, +1 attack, -1 damage taken.");
            break;
        case "Gem Gnome":
            p.MovementBonus += 1;
            p.SpellAttackBonus = 2;
            Console.WriteLine("  [Race] Gem Gnome: +1 movement, +2 to hit with spells.");
            break;
        case "Glass Gnome":
            p.MinAttack += 1;
            p.MovementBonus += 1;
            Console.WriteLine("  [Race] Glass Gnome: +1 min attack, +1 movement; spells can be half-dodged.");
            break;
        case "Hobgoblin":
            p.MovementBonus += 1;
            p.MinDamage += 1;
            Console.WriteLine($"  [Race] Hobgoblin: +1 movement per roll, +1 base damage (min {p.MinDamage}).");
            break;
        case "Ogre":
            p.MinDamage += 2;
            p.MaxDamage += 2;
            p.MaxHP *= 2;
            p.HP = p.MaxHP;
            p.MinDodge = Math.Max(1, p.MinDodge / 2);
            p.MaxDodge = Math.Max(1, p.MaxDodge / 2);
            p.MovementBonus -= 1;
            Console.WriteLine($"  [Race] Ogre: +2 damage ({p.MinDamage}-{p.MaxDamage}), HP doubled to {p.MaxHP}, dodge halved ({p.MinDodge}-{p.MaxDodge}), -1 movement.");
            break;
    }
}

void SelectCharacterType(Player p)
{
    var types = new[] { "Mage", "Priest", "Warrior", "Duelist", "Archer", "Martial Artist", "Berserker", "Musician" };
    Console.WriteLine("\nChoose your character type:");
    Console.WriteLine("  [1] Mage           — Wand + Staff; Air Blade (ranged) + Air Wave (knockback)");
    Console.WriteLine("  [2] Priest         — Prayers: Healing, Forgiveness, Lord's Prayer");
    Console.WriteLine("  [3] Warrior        — 2x Hand Axe; bonus actions + atk bonus scale with level");
    Console.WriteLine("  [4] Duelist        — Rapier + daggers; Duelist Points & special actions");
    Console.WriteLine("  [5] Archer         — Bow + 50 arrows + Short Sword backup");
    Console.WriteLine("  [6] Martial Artist — Pick a martial art; 1d6+2d4 scaling + grapple/throw");
    Console.WriteLine("  [7] Berserker      — Great Axe; Whirlwind spin + Rage (survive lethal hits)");
    Console.WriteLine("  [8] Musician       — Pick an instrument; songs buff the whole party (linger 1d4 turns)");
    Console.Write("  Choice (1-8 or name): ");
    string raw = (Console.ReadLine() ?? "").Trim();
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
        Console.WriteLine("  Starting weapon: Bow (50 arrows) + Short Sword backup");
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
        string araw = (Console.ReadLine() ?? "").Trim();
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
        string iraw = (Console.ReadLine() ?? "").Trim();
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

    // Set starting HP by class
    p.MaxHP = chosen switch
    {
        "Berserker"      => 12,
        "Warrior"        => 10,
        "Martial Artist" => 10,
        "Duelist"        => 8,
        "Archer"         => 8,
        "Musician"       => 8,
        "Priest"         => 6,
        "Mage"           => 6,
        _                => 8,
    };
    p.HP = p.MaxHP;
    Console.WriteLine($"  Starting HP: {p.HP}");

    // 12 points to spend on stats at character creation
    p.SavedStatPoints = 12;
    Console.WriteLine("\n  You have 12 points to spend on starting stats!");
    SpendStatPoints(p);
}

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

class Player
{
    public string Name = "The Lone Warrior";
    public int HP, MaxHP;
    public int MinAttack = 1, MaxAttack = 6;
    public int MinDamage = 1, MaxDamage = 9;
    public int MinDodge = 1, MaxDodge = 6;
    public int MinGrapple = 1, MaxGrapple = 6;
    public int MinGrappleDmg = 1, MaxGrappleDmg = 4;
    public int MinBlock = 1, MaxBlock = 6;
    public int MinParry = 1, MaxParry = 6;
    public int MinBardSong = 1, MaxBardSong = 6;
    public int MinPowerAtk = 4, MaxPowerAtk = 4;
    public int MinLimbBreak = 1, MaxLimbBreak = 6;
    public int MinPotionHeal = 1, MaxPotionHeal = 6;
    public int SavedStatPoints = 0;
    public int GearPointsAvailable = 0;
    public int AdditionalActions = 0;
    public string ElementalFocus = "";
    public int LichTouchTurns = 0;
    public int InvisibilityTurns = 0;
    public int PhantomImageTurns = 0;
    public int MagicHandTurns = 0;
    public bool EnlargeActive = false; public int EnlargeTurns = 0;
    public bool MageShieldActive = false; public int MageShieldTurns = 0, MageShieldBlockMin = 0, MageShieldBlockMax = 0;
    public int SpellweaveArmorTurns = 0;
    public int TrueSightTurns = 0, TrueSightBonus = 2; public string TrueSightStat = "attack"; public bool TrueSightIsMax = true;
    public int SprintPenalty = 0;
    public int ArmorDamageReduction = 0;
    public int RingletBonus = 0;
    public int ChainLightningUses = 0;
    public int Level = 1, XP = 0, PendingFeats = 1;
    public bool Defending = false;
    public bool HasGoblinSword = false;
    public int OffhandMaxDamage = 4;
    public bool IsGrappled = false;
    public Enemy? GrappledBy = null;
    public GridPos Position = new(-1, -1);   // per-player battlefield position
    public List<string> Feats = new();
    public Dictionary<string, int> FeatStacks = new();
    public Dictionary<string, int> GearCounts = new();
    public List<string> KnownSpells = new();
    public int BurningDmg = 0, BurningTurns = 0;
    public int FrostPenalty = 0, FrostTurns = 0;
    public int GrappleEscapePrePaid = 0;
    public Enemy? PostGrappleBreakTarget = null;
    public string? HeldWeapon = null;
    public string? SecondaryWeapon = null;
    public int ArrowCount = 0;
    public int DaggerCount = 0;
    public int AxeCount = 0;
    public int DuelistPoints = 0;
    public Dictionary<string, int> DuelistEffectTurns = new();
    public List<string> BrokenLimbs = new();
    public string CharacterType = "Warrior";
    public string Race = "Human";
    public int SpellDamageBonus = 0;
    public int SpellAttackBonus = 0;
    public int PrayerHealBonus = 0;
    public int RegenPerTurn = 0;
    public int MovementBonus = 0;
    public int MinMovement = 1, MaxMovement = 6;
    public int MinSpellAtk = 1, MaxSpellAtk = 6;
    public int MinSpellDmgBonus = 0, MaxSpellDmgBonus = 0;
    public int MinRangedAtk = 1, MaxRangedAtk = 6;
    public int MinRangedDmgBonus = 0, MaxRangedDmgBonus = 0;
    public int RagePoints = 0;
    public bool IsRaging = false;
    public int RageTurnsLeft = 0;
    public int RagePointsSpent = 0;
    public string MusicInstrument = "";
    public int SongTokens = 0;
    public string ActiveSong = "";
    public bool SongPlaying = false;
    public int SongLingerTurns = 0;
    public int SongEffectApplied = 0;
    public int WindBonusReceived = 0;   // song stat deltas currently on this player
    public int StoneBonusReceived = 0;  // (from any party musician's song)
    public int WarBonusReceived = 0;    // War Song deltas on this player
    public int BlessBonusReceived = 0;  // Prayer of Mass Blessings deltas
    public int BlessTurns = 0;
    public int PrayerUses = 0;          // prayers left until next rest
    public int SpellUses = 0;           // spell casts left until next rest
    public int SanctuaryTurns = 0;      // can't attack or be attacked
    public int RedemptionTurns = 0;     // doubled max HP countdown
    public int RedemptionExtraHP = 0;
    public int GroupsDefeated = 0;

    // Off-hand shield slot (dropped by enemies; bonuses persist in save file)
    public string? OffHandShieldName    = null;
    public int     OffHandShieldDefense = 0;   // added to ArmorDamageReduction at pickup
    public int     OffHandShieldBlock   = 0;   // added to MaxBlock at pickup

    public Player(Random rng)
    {
        MaxHP = rng.Next(3, 13);
        HP = MaxHP;
    }

    public bool HasFeat(string name) => Feats.Contains(name);
    public int GetFeatStacks(string name) => FeatStacks.GetValueOrDefault(name, HasFeat(name) ? 1 : 0);

    public void AddFeat(string name)
    {
        if (!Feats.Contains(name)) Feats.Add(name);
        FeatStacks[name] = GetFeatStacks(name) + (FeatDef.All.First(f => f.Name == name).Stackable ? 1 : 0);
        if (!FeatStacks.ContainsKey(name)) FeatStacks[name] = 1;
    }

    public void ClearRoundEffects() { Defending = false; ChainLightningUses = 0; }

    // ── Ability feat groups: taking one grants the matching resource pool ──
    public static readonly string[] PrayerFeats = { "Prayer of Sanctuary", "Prayer of the Most High", "Prayer of Redemption", "Prayer of Mass Blessings" };
    public static readonly string[] SongFeats   = { "War Song", "Silence Song", "Song of the Redeemer" };
    public static readonly string[] SpellFeats  = { "Cantrips", "Necromancer", "Lich Bound", "Divination", "Advanced Cantrips" };
    public bool CanPray => CharacterType == "Priest" || PrayerFeats.Any(HasFeat);
    public bool CanSing => CharacterType == "Musician" || SongFeats.Any(HasFeat);
    public int MaxPrayerUses() => 5 + (Level / 2) * 2;          // 5 uses, +2 every 2 levels
    public int MaxSpellUses()  => 6 + (Level / 2) * 2;          // 6 uses, +2 every 2 levels

    // ── Musician songs ──
    // Tier: +2 to song bonuses (and +1d6 fear dice) every 3rd level.
    public int SongTier() => Level >= 3 ? Level / 3 : 0;
    public int MaxSongTokens() => 5 + Level / 2;                // 5 plays, +1 every 2 levels
    public int SongBonusAmount() => 2 + 2 * SongTier();          // Slayer atk / Wind / Hardstone
    public int SlayerDmgBonus() => 1 + 2 * SongTier();
    public int FearDiceCount() => 2 + SongTier();                // DeathTone (2+tier)d6
    public bool SongActive(string s) => ActiveSong == s && (SongPlaying || SongLingerTurns > 0);

    // Wind Song / Hardstone are applied as temporary stat deltas to EVERY
    // party member (musician included) so all dodge/block/parry/damage-
    // reduction rolls in the game pick them up. Each recipient tracks the
    // received total so saves and removal stay exact even with two bards.
    public void ApplySongStats(List<Player> party)
    {
        if (ActiveSong == "Wind Song")
        {
            int b = SongBonusAmount();
            SongEffectApplied = b;
            foreach (var m in party)
            {
                m.MinDodge += b; m.MaxDodge += b; m.MinBlock += b; m.MaxBlock += b; m.MinParry += b; m.MaxParry += b;
                m.WindBonusReceived += b;
            }
        }
        else if (ActiveSong == "Hardstone Song")
        {
            int b = SongBonusAmount();
            SongEffectApplied = b;
            foreach (var m in party)
            {
                m.ArmorDamageReduction += b;
                m.StoneBonusReceived += b;
            }
        }
        else if (ActiveSong == "War Song")
        {
            SongEffectApplied = 1;
            foreach (var m in party)
            {
                m.MinDodge += 1; m.MaxDodge += 1;
                m.MinAttack += 1; m.MaxAttack += 1;
                m.MinRangedAtk += 1; m.MaxRangedAtk += 1;
                m.MinSpellAtk += 1; m.MaxSpellAtk += 1;
                m.MinGrapple += 1; m.MaxGrapple += 1;
                m.PrayerHealBonus += 2; m.MinPotionHeal += 2; m.MaxPotionHeal += 2;
                m.WarBonusReceived += 1;
            }
        }
    }

    public void EndSong(List<Player> party)
    {
        if (ActiveSong == "Wind Song")
        {
            int b = SongEffectApplied;
            foreach (var m in party)
            {
                m.MinDodge -= b; m.MaxDodge -= b; m.MinBlock -= b; m.MaxBlock -= b; m.MinParry -= b; m.MaxParry -= b;
                m.WindBonusReceived -= b;
            }
        }
        else if (ActiveSong == "Hardstone Song")
        {
            foreach (var m in party)
            {
                m.ArmorDamageReduction -= SongEffectApplied;
                m.StoneBonusReceived -= SongEffectApplied;
            }
        }
        else if (ActiveSong == "War Song" && SongEffectApplied > 0)
        {
            foreach (var m in party)
            {
                m.MinDodge -= 1; m.MaxDodge -= 1;
                m.MinAttack -= 1; m.MaxAttack -= 1;
                m.MinRangedAtk -= 1; m.MaxRangedAtk -= 1;
                m.MinSpellAtk -= 1; m.MaxSpellAtk -= 1;
                m.MinGrapple -= 1; m.MaxGrapple -= 1;
                m.PrayerHealBonus -= 2; m.MinPotionHeal -= 2; m.MaxPotionHeal -= 2;
                m.WarBonusReceived -= 1;
            }
        }
        SongEffectApplied = 0; ActiveSong = ""; SongPlaying = false; SongLingerTurns = 0;
    }

    // ── Prayer of Mass Blessings: +b to every roll stat, reversible ──
    public void ApplyBlessing(int b, int turns)
    {
        MinAttack += b; MaxAttack += b; MinDodge += b; MaxDodge += b;
        MinGrapple += b; MaxGrapple += b; MinBlock += b; MaxBlock += b;
        MinParry += b; MaxParry += b; MinRangedAtk += b; MaxRangedAtk += b;
        MinSpellAtk += b; MaxSpellAtk += b;
        BlessBonusReceived += b;
        BlessTurns = Math.Max(BlessTurns, turns);
    }

    public void ExpireBlessing()
    {
        int b = BlessBonusReceived;
        if (b != 0)
        {
            MinAttack -= b; MaxAttack -= b; MinDodge -= b; MaxDodge -= b;
            MinGrapple -= b; MaxGrapple -= b; MinBlock -= b; MaxBlock -= b;
            MinParry -= b; MaxParry -= b; MinRangedAtk -= b; MaxRangedAtk -= b;
            MinSpellAtk -= b; MaxSpellAtk -= b;
        }
        BlessBonusReceived = 0; BlessTurns = 0;
    }

    // ── Prayer of Redemption: doubled max HP for a few turns ──
    public void ExpireRedemption()
    {
        if (RedemptionExtraHP > 0)
        {
            MaxHP -= RedemptionExtraHP;
            HP = Math.Min(HP, MaxHP);
        }
        RedemptionExtraHP = 0; RedemptionTurns = 0;
    }
}

abstract class Enemy
{
    public string Name;
    public string TypeName;
    public int HP, MaxHP;
    public int MinAttack, MaxAttack;
    public int MinDamage, MaxDamage;
    public int MinDodge, MaxDodge;
    public int XPValue;
    public bool Fled = false;
    public bool Alive => HP > 0 && !Fled;
    public bool KnockedDown = false;
    public bool KnockedOut = false;
    public bool OffBalance = false;
    public bool Disarmed = false;
    public bool Grappled = false;
    public bool CanMove = true;
    public bool Charmed = false;
    public bool HasFledBefore = false;
    public int ConsecutiveDmgTurns = 0;
    public int HpAtTurnStart = 0;
    public bool GrappleNextTurn = false;
    public int KOTurns = 0;
    public int KOCount = 0;
    public bool XpAwarded = false;
    public GridPos Position = new(-1, -1);
    public int BleedDmg = 0;
    public int BurningDmg = 0, BurningTurns = 0;
    public int FrostPenalty = 0, FrostTurns = 0;
    public int DodgePenalty = 0;
    public int AttackPenalty = 0;
    public int SprintPenalty = 0;
    public bool IsPlayerAlly = false;
    public int AllyTurnsLeft = 0;
    // Caster resource pools — scaled to wave level at spawn (same formulas as players)
    public int Level = 1;
    public int SpellUsesLeft = 6;
    public int PrayerUsesLeft = 5;
    public int SongUsesLeft = 5;
    public bool FrostBurned = false;
    public bool HalfMovement = false; public int HalfMovementTurns = 0; public bool HalfMovementBlock = false;
    public int WeaponDistance = 0;
    public int MinGrapple = 1, MaxGrapple = 6;
    public int GrappleDmgMin = 1, GrappleDmgMax = 6;
    public bool HasDoubleTap = false;
    public bool HasBlock = false;
    public bool HasParry = false;
    public int BlockMin = 1, BlockMax = 6;
    public bool MagicResistant = false;
    public bool HitBySpell = false;
    public bool HasKick = false;
    public int KickDmgMin = 1, KickDmgMax = 4;
    public bool HasArmBlock = false;
    public bool MagicVulnerable = false;
    public int ToughHideMin = 0, ToughHideMax = 0;
    public int OffhandMinAtk = 1, OffhandMaxAtk = 6;
    public int OffhandMinDmg = 1, OffhandMaxDmg = 4;
    public bool PowerAttackMode = false;
    public bool DroppedWeapon = false;
    public GridPos? WeaponPos = null;
    public int UnarmedMinDmg = 0, UnarmedMaxDmg = 0;
    public bool HasShield = false;
    public int ShieldBlockBonus = 0;
    public bool OffhandNonLethal = false;
    public bool IsUndead = false;
    public bool ShieldLost = false;
    public int ArrowsInBody = 0;
    public string Race = "";
    public int RegenPerTurn = 0;

    public Enemy(string name, string typeName) { Name = name; TypeName = typeName; }

    protected void RollRace(Random r)
    {
        var races = new[] {
            "Moon Elf", "Human", "Stone Dwarf", "Light-Foot Hobbit", "Sun Elf",
            "Wood Elf", "Orc", "Goblin", "Troll", "Iron Dwarf", "Brave Minds Hobbit",
            "Gem Gnome", "Glass Gnome", "Hobgoblin", "Ogre"
        };
        Race = races[r.Next(races.Length)];
        switch (Race)
        {
            case "Moon Elf":           MaxAttack += 2; break;
            case "Human":              MaxAttack++; break;
            case "Stone Dwarf":        ToughHideMin = Math.Max(ToughHideMin, 1); ToughHideMax += 2; break;
            case "Light-Foot Hobbit":  MaxDodge += 3; break;
            case "Sun Elf":            MaxDodge++; MaxAttack++; break;
            case "Wood Elf":           MaxAttack += 3; break;
            case "Orc":                MaxDamage += 3; break;
            case "Goblin":             MaxDodge++; break;
            case "Troll":              RegenPerTurn = 2; break;
            case "Iron Dwarf":         ToughHideMin = Math.Max(ToughHideMin, 1); ToughHideMax += 3; break;
            case "Brave Minds Hobbit": MaxDodge++; MaxAttack++; ToughHideMin = Math.Max(ToughHideMin, 1); ToughHideMax++; break;
            case "Gem Gnome":          MaxAttack += 2; break;
            case "Glass Gnome":        MinAttack++; break;
            case "Hobgoblin":          MinDamage++; break;
            case "Ogre":               MinDamage += 2; MaxDamage += 2;
                                       MinDodge = Math.Max(1, MinDodge / 2); MaxDodge = Math.Max(1, MaxDodge / 2); break;
        }
    }

    public string DisplayStatus()
    {
        var parts = new List<string> { $"{Name}: HP {HP}/{MaxHP}" };
        if (!string.IsNullOrEmpty(Race)) parts.Add($"[{Race}]");
        if (KnockedOut) parts.Add($"KO({KOTurns}t)");
        if (KnockedDown) parts.Add("Down");
        if (OffBalance) parts.Add("Off-balance");
        if (Disarmed) parts.Add(WeaponPos.HasValue ? $"Disarmed(weapon at {WeaponPos.Value.X},{WeaponPos.Value.Y})" : "Disarmed");
        if (ShieldLost) parts.Add("No Shield");
        if (Grappled) parts.Add("Grappled");
        if (BleedDmg > 0) parts.Add($"Bleed({BleedDmg})");
        if (BurningDmg > 0) parts.Add($"Burning({BurningDmg}×{BurningTurns}t)");
        if (FrostPenalty > 0) parts.Add($"Frozen(-{FrostPenalty}/{FrostTurns}t)");
        if (Charmed) parts.Add("Charmed");
        return string.Join(" ", parts);
    }

    public string ShortStatus() => $"{Name} HP:{HP}/{MaxHP}";

    public void EndOfRound()
    {
        OffBalance = false;
        HitBySpell = false;
        DodgePenalty = Math.Max(0, DodgePenalty - 2);
        AttackPenalty = Math.Max(0, AttackPenalty - 2);
        SprintPenalty = 0;
        Charmed = false;
    }
}

class Goblin : Enemy
{
    public Goblin(Random rng, string name) : base(name, "Goblin")
    {
        MaxHP = rng.Next(2, 9); HP = MaxHP;
        MinAttack = 1; MaxAttack = 6;
        MinDamage = 1; MaxDamage = 6;
        MinDodge = 1; MaxDodge = 6;
        UnarmedMinDmg = 1; UnarmedMaxDmg = 4;
        XPValue = 10;
        Race = "Goblin"; MaxDodge++;
    }

    public static Enemy RandType(Random r, string name) => r.Next(1, 6) switch
    {
        1 or 2 => new Goblin(r, name),
        3 => new GoblinWarrior(r, name),
        4 => new RogueGoblin(r, name),
        _ => new GoblinShaman(r, name)
    };
}

class SpellGoblin : Goblin
{
    public string SpellName;
    public SpellGoblin(Random rng, string name) : base(rng, name)
    {
        TypeName = "Spell Goblin";
        string[] spells = { "Fire Blast", "Chain Lightning", "Frost Burst" };
        SpellName = spells[rng.Next(3)];
        XPValue = 15;
    }
}

class RogueGoblin : Goblin
{
    public int DaggerCount = 6;
    public RogueGoblin(Random rng, string name) : base(rng, name)
    {
        TypeName = "Rogue Goblin";
        HasDoubleTap = true;
        OffhandMinAtk = 1; OffhandMaxAtk = 6;
        OffhandMinDmg = 1; OffhandMaxDmg = 6;
        XPValue = 15;
    }
}

class GoblinWarrior : Goblin
{
    public GoblinWarrior(Random rng, string name) : base(rng, name)
    {
        TypeName = "Goblin Warrior";
        MaxHP = rng.Next(4, 11); HP = MaxHP;  // slightly tougher
        MinDamage = 2; MaxDamage = 6;         // short sword
        HasBlock = true; BlockMin = 1; BlockMax = 6;
        HasShield = true; ShieldBlockBonus = 1; // buckler
        XPValue = 12;
    }
}

class GoblinShaman : Goblin
{
    public GoblinShaman(Random rng, string name) : base(rng, name)
    {
        TypeName = "Goblin Shaman";
        MaxHP = rng.Next(4, 9); HP = MaxHP;
        MinAttack = 1; MaxAttack = 4;  // weak unarmed
        MinDamage = 1; MaxDamage = 4;
        XPValue = 15;
    }
}

class Hobgoblin : Enemy
{
    public Hobgoblin(Random rng, string name) : base(name, "Hobgoblin")
    {
        MaxHP = 16; HP = MaxHP;
        MinAttack = 1; MaxAttack = 8;
        MinDamage = 1; MaxDamage = 8;
        MinDodge = 2; MaxDodge = 8;
        MinGrapple = 2; MaxGrapple = 8;
        GrappleDmgMin = 1; GrappleDmgMax = 4;
        XPValue = 20;
        Race = "Hobgoblin"; MinDamage++;
    }

    public static Enemy RandType(Random r, string name) => r.Next(1, 6) switch
    {
        1 or 2 => new Hobgoblin(r, name),
        3      => new HobgoblinFighter(r, name),
        4      => new HobgoblinThief(r, name),
        _      => new HobgoblinCleric(r, name)
    };
}

class HobgoblinFighter : Hobgoblin
{
    public int ArrowCount = 12;
    public HobgoblinFighter(Random rng, string name) : base(rng, name)
    {
        TypeName = "Hobgoblin Fighter";
        MaxHP = 20; HP = MaxHP;
        MinAttack = 2; MaxAttack = 9;
        MinDamage = 2; MaxDamage = 10;  // long sword
        MinDodge = 2; MaxDodge = 8;
        HasBlock = true; BlockMin = 2; BlockMax = 10;
        HasShield = true; ShieldBlockBonus = 2;  // kite shield
        XPValue = 28;
        MinDamage++;
    }
}

class HobgoblinThief : Hobgoblin
{
    public int DaggerCount = 8;
    public HobgoblinThief(Random rng, string name) : base(rng, name)
    {
        TypeName = "Hobgoblin Thief";
        MaxHP = 14; HP = MaxHP;
        MinAttack = 2; MaxAttack = 10;
        MinDamage = 1; MaxDamage = 6;   // short sword
        MinDodge = 3; MaxDodge = 10;
        MinGrapple = 2; MaxGrapple = 8;
        HasDoubleTap = true;
        OffhandMinAtk = 2; OffhandMaxAtk = 8;
        OffhandMinDmg = 1; OffhandMaxDmg = 6;  // second short sword
        XPValue = 26;
        MinDamage++;
    }
}

class HobgoblinCleric : Hobgoblin
{
    public HobgoblinCleric(Random rng, string name) : base(rng, name)
    {
        TypeName = "Hobgoblin Cleric";
        MaxHP = 16; HP = MaxHP;
        MinAttack = 1; MaxAttack = 10;  // Divination: +2 to max
        MinDamage = 1; MaxDamage = 8;   // mace 1d8
        MinDodge = 2; MaxDodge = 6;
        MinGrapple = 1; MaxGrapple = 6;
        HasBlock = true; BlockMin = 2; BlockMax = 10;
        HasShield = true; ShieldBlockBonus = 2;  // kite shield
        XPValue = 30;
        MinDamage++;
    }
}

class Orc : Enemy
{
    public Orc(Random rng, string name) : base(name, "Orc")
    {
        MaxHP = 25; HP = MaxHP;
        MinAttack = 3; MaxAttack = 9;
        MinDamage = 2; MaxDamage = 10;
        MinDodge = 2; MaxDodge = 8;
        MinGrapple = 3; MaxGrapple = 12;
        GrappleDmgMin = 2; GrappleDmgMax = 8;
        HasDoubleTap = true;
        HasBlock = true; BlockMin = 2; BlockMax = 10;
        HasShield = true; ShieldBlockBonus = 2; // medium round shield
        UnarmedMinDmg = 1; UnarmedMaxDmg = 6;
        XPValue = 30;
        Race = "Orc"; MaxDamage += 3;
    }

    public static Enemy RandType(Random r, string name) => r.Next(1, 6) switch
    {
        1 or 2 => new Orc(r, name),
        3      => new OrcMonk(r, name),
        4      => new OrcPriestess(r, name),
        _      => new OrcRanger(r, name)
    };
}

class Troll : Enemy
{
    public int EquippedAxes = 2;
    public int SpareAxes = 2;
    public List<GridPos> ThrownAxePositions = new();
    public Troll(Random rng, string name) : base(name, "Troll")
    {
        MaxHP = 28; HP = MaxHP;
        MinAttack = 2; MaxAttack = 12;
        MinDamage = 3; MaxDamage = 12;
        MinDodge = 1; MaxDodge = 6;
        MinGrapple = 2; MaxGrapple = 12;
        GrappleDmgMin = 2; GrappleDmgMax = 6;
        HasDoubleTap = true;
        HasParry = true; BlockMin = 2; BlockMax = 12;
        HasKick = true; KickDmgMin = 2; KickDmgMax = 6;
        MagicResistant = true;
        XPValue = 45;
        Race = "Troll";
    }

    // When a troll is rolled, it may spawn as a variant (like goblin/hobgoblin/orc types)
    public static Troll RandType(Random r, string name) => r.Next(1, 11) switch
    {
        7 or 8 => new TrollWarrior(r, name.Replace("Troll", "Troll Warrior")),
        9      => new TrollPriest(r, name.Replace("Troll", "Troll Priest")),
        10     => new TrollMusician(r, name.Replace("Troll", "Troll Musician")),
        _      => new Troll(r, name)
    };
}

class TrollWarrior : Troll
{
    public TrollWarrior(Random rng, string name) : base(rng, name)
    {
        TypeName = "Troll Warrior";
        MaxHP = 34; HP = MaxHP;
        MinAttack = 3; MaxAttack = 14;
        MinDamage = 4; MaxDamage = 13;
        MinGrapple = 3; MaxGrapple = 13;
        BlockMin = 3; BlockMax = 13;
        SpareAxes = 4;                    // carries extra throwing axes
        XPValue = 58;
    }
}

class TrollPriest : Troll
{
    public TrollPriest(Random rng, string name) : base(rng, name)
    {
        TypeName = "Troll Priest";
        EquippedAxes = 0; SpareAxes = 0;  // carries a war mace, chants dark prayers
        MinDamage = 2; MaxDamage = 10;
        XPValue = 58;
    }
}

class TrollMusician : Troll
{
    public bool PlayedSilence = false;
    public bool WarSongActive = false;
    public List<Enemy> WarSongTargets = new();   // buffed allies — buff dies with the drummer
    public TrollMusician(Random rng, string name) : base(rng, name)
    {
        TypeName = "Troll Musician";
        EquippedAxes = 0; SpareAxes = 0;  // hands full of war drums
        MinDamage = 2; MaxDamage = 9;
        XPValue = 58;
    }
}

class NecromancerTroll : Troll
{
    public NecromancerTroll(Random rng, string name) : base(rng, name)
    {
        TypeName = "Necromancer Troll";
        EquippedAxes = 0; SpareAxes = 0; // no axes — uses negative touch instead
        MinDamage = 2; MaxDamage = 8;    // negative touch is 2d4
        XPValue = 60;
    }
}

class Ogre : Enemy
{
    public Ogre(Random rng, string name) : base(name, "Ogre")
    {
        MaxHP = 45; HP = MaxHP;
        MinAttack = 4; MaxAttack = 16;
        MinDamage = 3; MaxDamage = 12;
        MinDodge = 1; MaxDodge = 6;
        MinGrapple = 4; MaxGrapple = 12;
        GrappleDmgMin = 3; GrappleDmgMax = 9;
        HasDoubleTap = true;
        HasArmBlock = true; BlockMin = 4; BlockMax = 12;
        MagicVulnerable = true;
        ToughHideMin = 1; ToughHideMax = 4;
        OffhandMinAtk = 4; OffhandMaxAtk = 12;
        OffhandMinDmg = 2; OffhandMaxDmg = 8;
        XPValue = 50;
        Race = "Ogre"; MinDamage += 2; MaxDamage += 2;
        MinDodge = Math.Max(1, MinDodge / 2); MaxDodge = Math.Max(1, MaxDodge / 2);
    }
}

class OrcBarbarian : Enemy
{
    public int HandAxeCount = 4;
    public int OrcRagePoints = 1;
    public bool OrcIsRaging = false;
    public OrcBarbarian(Random rng, string name) : base(name, "Orc Barbarian")
    {
        MaxHP = 35; HP = MaxHP;        // Orc base 25 + 10
        MinAttack = 3; MaxAttack = 9;
        MinDamage = 4; MaxDamage = 12; // Battle Axe
        MinDodge = 2; MaxDodge = 8;
        MinGrapple = 3; MaxGrapple = 12;
        GrappleDmgMin = 2; GrappleDmgMax = 8;
        HasDoubleTap = true;
        HasBlock = true; BlockMin = 2; BlockMax = 10;
        UnarmedMinDmg = 1; UnarmedMaxDmg = 6;
        OffhandMinAtk = 3; OffhandMaxAtk = 9;
        OffhandMinDmg = 4; OffhandMaxDmg = 14; // War Mace (non-lethal)
        OffhandNonLethal = true;
        XPValue = 50;
        Race = "Orc"; MaxDamage += 3;
    }
}

class OrcMonk : Enemy
{
    public string MartialStyle;
    public bool HasMartialStaff;
    public OrcMonk(Random rng, string name) : base(name, "Orc Monk")
    {
        MaxHP = 24; HP = MaxHP;
        MinAttack = 2; MaxAttack = 10;
        MinDamage = 1; MaxDamage = 8;
        MinDodge = 3; MaxDodge = 10;
        MinGrapple = 3; MaxGrapple = 12;
        GrappleDmgMin = 2; GrappleDmgMax = 8;
        XPValue = 38;
        Race = "Orc"; MaxDamage += 3;
        string[] styles = { "Striker", "Grappler", "Defender" };
        MartialStyle = styles[rng.Next(3)];
        switch (MartialStyle)
        {
            case "Striker":
                HasDoubleTap = true;
                OffhandMinAtk = 2; OffhandMaxAtk = 8;
                OffhandMinDmg = 1; OffhandMaxDmg = 6;
                HasKick = true; KickDmgMin = 1; KickDmgMax = 6;
                break;
            case "Grappler":
                MaxGrapple += 4; GrappleDmgMax += 4;
                break;
            case "Defender":
                HasBlock = true; BlockMin = 2; BlockMax = 10;
                HasParry = true; MaxDodge += 2;
                break;
        }
        HasMartialStaff = rng.Next(2) == 0;
        if (!HasMartialStaff) { MinDamage = 1; MaxDamage = 6; } // unarmed (1d6)
    }
}

class OrcPriestess : Enemy
{
    public bool HasHolyRoller = true;
    public OrcPriestess(Random rng, string name) : base(name, "Orc Priestess")
    {
        MaxHP = 22; HP = MaxHP;
        MinAttack = 2; MaxAttack = 8;
        MinDamage = 2; MaxDamage = 8;  // War Mace 2d4
        MinDodge = 1; MaxDodge = 6;
        MinGrapple = 1; MaxGrapple = 6;
        XPValue = 40;
        Race = "Orc"; MaxDamage += 3;
    }
}

class OrcRanger : Enemy
{
    public int ArrowCount = 20;
    public int BowMinAtk = 1, BowMaxAtk = 8;   // 1d8 to-hit
    public int BowMinDmg = 1, BowMaxDmg = 10;  // 1d10 damage
    public OrcRanger(Random rng, string name) : base(name, "Orc Ranger")
    {
        MaxHP = 22; HP = MaxHP;
        MinAttack = 2; MaxAttack = 8;   // kukuri
        MinDamage = 2; MaxDamage = 8;   // kukuri 2d4
        MinDodge = 2; MaxDodge = 10;
        MinGrapple = 2; MaxGrapple = 8;
        GrappleDmgMin = 1; GrappleDmgMax = 6;
        HasDoubleTap = true;
        OffhandMinAtk = 2; OffhandMaxAtk = 8;
        OffhandMinDmg = 2; OffhandMaxDmg = 8;  // second kukuri
        XPValue = 42;
        Race = "Orc"; MaxDamage += 3;
    }
}

class FeatDef
{
    public string Name, Desc;
    public string? Prerequisite;
    public bool Stackable;
    public int MaxStacks;
    public FeatDef(string n, string d, string? pre = null, bool stackable = false, int maxStacks = 0)
    { Name = n; Desc = d; Prerequisite = pre; Stackable = stackable; MaxStacks = maxStacks; }

    public static readonly List<FeatDef> All = new()
    {
        new("Block", "Roll 1-6 vs enemy attack. On success, enemy gets -2 dodge till their turn."),
        new("Parry", "After a successful block, roll 1-6 vs dodge. Success knocks enemy down.", "Block"),
        new("Counter Strike", "On successful dodge, block, or parry get a free attack on the attacker."),
        new("Double Tap", "Each attack action also makes an off-hand attack (1d6 atk, 1d4 dmg)."),
        new("Basic Combo", "Attacks include a kick (1d4 atk, 1d4 dmg).", "Double Tap"),
        new("Fury of Blows", "Attacks add another kick and a headbutt (1d6 atk, 1d4 dmg).", "Basic Combo"),
        new("Power Attack", "Per attack: -2 to attack roll, +4 damage."),
        new("Sunder", "Per attack: -1 atk; on hit roll 1-6: 4-5 = bleed, 6 = double bleed."),
        new("Disarm", "Per attack: target weapon instead of body; on hit, knock weapon 10 ft away."),
        new("Thin the Herd", "Killing an enemy grants a free attack on another adjacent enemy."),
        new("Slayer", "Enemy at low HP: get a free attack per turn against them.", "Thin the Herd"),
        new("Sap", "Per attack: -2 atk; on hit, 1d4 dmg + chance die special effect."),
        new("Opportunist", "Attack debuffed enemies (off-balance, down, KO, disarmed) at stacking -1 penalty.", "Counter Strike"),
        new("Toughness", "Double max HP (up to 3 times).", null, true, 3),
        new("Built", "+1 to minimum damage rolls.", null, true),
        new("Talented", "+1 to minimum attack rolls.", null, true),
        new("Potion Brewer", "Add another d6 to healing potion rolls.", null, true),
        new("Closeliner", "+1 min grapple and +1 min grapple damage rolls.", null, true),
        new("Judo", "On dodge/block/parry/enemy miss: free grapple. On success: hold, throw, or disarm."),
        new("Kehon", "Enemy enters/leaves range, KO, disarmed, or off-balance: instant free grapple."),
        new("Taekwondo", "Break limbs of grappled enemies (double damage; effects by limb)."),
        new("Judo Black Belt", "Grapple 2 enemies from missed attacks; extras auto-knocked down.", "Judo"),
        new("Kehon Black Belt", "Instantly KO grappled enemies (1 action); break free of grapple freely.", "Kehon"),
        new("Taekwondo Black Belt", "Grapple 2 adjacent enemies simultaneously.", "Taekwondo"),
        new("Chidia", "Unarmed: +2 actions per turn; all attacks are non-lethal (max KO 12-48 turns)."),
        new("Chidia Black Belt", "Break weapons/hands with blocks/parries.", "Chidia"),
        new("MMA", "Double min/max damage; +2 attacks and +2 grapples per action.", null),
        new("Bard Song", "Roll 1d6 + stacks vs enemy 2d4; on success, enemies attack each other.", null, true),
        new("Prayer of Sanctuary", "Ward an ally for 1d4 turns: they can't be attacked, nor attack (range 50ft). Grants prayers + a Mace."),
        new("Prayer of the Most High", "Holy wrath: 1d4 dmg per 3 levels to ALL enemies within 50ft. Grants prayers + a Mace."),
        new("Prayer of Redemption", "Fully heal an ally and double their max HP for 1d4 turns. Grants prayers + a Mace."),
        new("Prayer of Mass Blessings", "+1d4 to all allies' rolls for 1d4 turns. Grants prayers + a Mace."),
        new("War Song", "Party song: +1 dodge, +1 all attacks, +1 grapple, +2 healing while playing. Grants songs + an instrument."),
        new("Silence Song", "No spells, prayers or songs can be used by ANYONE for 1d4 turns. Grants songs + an instrument."),
        new("Song of the Redeemer", "Heals all allies 1d4 per 3 levels. Grants songs + an instrument."),
        new("Giant's Strength", "Can pick up and wield Ogre Club (club sweep 2 squares). +2 min damage, +1 max damage on all non-club weapons."),
        new("Giant's Grip", "Wield two-handed weapons as though they are one-handed, allowing dual-wielding of two-handed weapons."),
        new("Spell Focus", "Spells gain +2 to hit (min and max spell attack rolls)."),
        new("Magical Overflow", "Double min and max spell damage and prayer healing."),
        new("Elemental", "Pick an element (holy/negative/air/fire/lightning/frost): +2 to attack rolls and damage/healing for that element."),
        new("Cantrips", "Learn spells: Magic Hand (floating hand attacks 2d4 turns), Invisibility (1d4 turns), Phantom Image (fear aura 2d3 turns)."),
        new("Extended Magi", "All spell and prayer effect durations are doubled."),
        new("Necromancer", "Learn spells: Negative Touch (2d4 melee), Raise Dead (undead ally for 2-9 turns)."),
        new("Holy Roller", "Pray twice per action; Last Rites revives at 75% max HP."),
        new("Divination", "Learn spell: True Sight — give self or ally +2 to a chosen roll for 2d4 turns (range 30ft)."),
        new("Twin Caster", "After casting a spell, cast a second spell in the same action."),
        new("Advanced Cantrips", "Learn spells: Enlarge (double stats, half dodge, 2d3 turns), Mage Shield (auto-block spells + 2d4 block, 2d3 turns), Spellweave Armor (-2 damage taken, 2d3 turns).", "Cantrips"),
        new("OverReach Magic", "Double all spell and prayer range values."),
        new("Lich Bound", "Learn spells: Life Drain (2d5 neg. energy at 30ft, heals you), FrostBurn (fire+frost 2d4 at 30ft, halves movement, 1d6 burn/turn until extinguished), Lich Touch (heal=damage dealt for 2d4 turns; all damage becomes negative energy).", "Necromancer"),
    };
}

struct GridPos
{
    public int X, Y;
    public GridPos(int x, int y) { X = x; Y = y; }
    public static GridPos PlayerStart => new(25, 25);
    public int ManhattanDist(GridPos o) => Math.Abs(X - o.X) + Math.Abs(Y - o.Y);
    public bool IsCardinalAdjacent(GridPos o) =>
        (Math.Abs(X - o.X) == 1 && Y == o.Y) || (X == o.X && Math.Abs(Y - o.Y) == 1);
    public float Feet(GridPos o) => ManhattanDist(o) * 2.5f;
    public bool SameAs(GridPos o) => X == o.X && Y == o.Y;
    public string CompassFrom(GridPos origin)
    {
        int dx = X - origin.X, dy = Y - origin.Y;
        string d = "";
        if (dy < 0) d += "N"; else if (dy > 0) d += "S";
        if (dx > 0) d += "E"; else if (dx < 0) d += "W";
        return string.IsNullOrEmpty(d) ? "here" : d;
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// COMBAT SESSION
// ═══════════════════════════════════════════════════════════════════════════

class CombatSession
{
    Player P;
    readonly List<Player> AllPlayers;
    List<Player> ActivePlayers;
    public List<Player> FledPlayers = new();
    readonly Random Rng;
    readonly Func<int, int> XpThreshold;
    readonly Action<int> GainXP;
    List<Enemy> Active;
    List<(List<Enemy> batch, int turns)> Pending = new();
    List<(GridPos Pos, string Type)> GroundWeapons = new();
    public bool PlayerFled = false;
    public bool ExitRequested = false;
    // Routes to the current player's own position so every party member
    // stands on (and moves from) their own square.
    GridPos PlayerPos { get => P.Position; set => P.Position = value; }
    SharedGameState? _displayState;
    int _waveNum;

    public CombatSession(Player p, List<Player> allPlayers, List<Enemy> enemies, Random rng, Func<int, int> xpFn, Action<int> gainXp, GridPos playerStart, SharedGameState? displayState = null, int waveNum = 0)
    {
        P = p; AllPlayers = allPlayers; ActivePlayers = allPlayers.ToList();
        Active = enemies; Rng = rng; XpThreshold = xpFn; GainXP = gainXp;
        foreach (var pl in allPlayers) pl.Position = playerStart;   // party starts together
        _displayState = displayState;
        _waveNum = waveNum;
        PlaceEnemies(enemies, nearEdge: false);
    }

    // Step-by-step movement: the player picks each square instead of
    // committing the whole roll to one direction. A single prompt can also
    // take a path string like "nnee". Enter or X stops early.
    void StepMovement(int squares)
    {
        Console.WriteLine("  Step with N/S/E/W (chain letters like 'nnee'); Enter or X to stop.");
        int used = 0;
        while (used < squares)
        {
            Console.Write($"  [{used}/{squares} moved] at ({PlayerPos.X},{PlayerPos.Y}) — step: ");
            string d = (Console.ReadLine() ?? "").Trim().ToLower();
            if (d == "" || d == "x" || d == "q" || d == "done" || d == "stop") break;
            bool badInput = false;
            foreach (char c in d)
            {
                if (used >= squares) break;
                int dx = c == 'e' ? 1 : c == 'w' ? -1 : 0;
                int dy = c == 's' ? 1 : c == 'n' ? -1 : 0;
                if (dx == 0 && dy == 0) { badInput = true; break; }
                int nx = Math.Clamp(PlayerPos.X + dx, 0, 49);
                int ny = Math.Clamp(PlayerPos.Y + dy, 0, 49);
                if (nx == PlayerPos.X && ny == PlayerPos.Y)
                {
                    Console.WriteLine("  You bump into the edge of the field.");
                    continue;
                }
                PlayerPos = new GridPos(nx, ny);
                used++;
                PushDisplay();
            }
            if (badInput) Console.WriteLine("  Use N/S/E/W letters only (or X to stop).");
        }
        Console.WriteLine($"  You end your move at ({PlayerPos.X},{PlayerPos.Y})." +
            (used < squares ? $"  ({squares - used} square(s) unused)" : ""));
    }

    static string PlayerInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string s = string.Concat(parts.Take(2).Select(w => char.ToUpper(w[0])));
        return s.Length > 0 ? s : "?";
    }

    void PushDisplay()
    {
        if (_displayState == null) return;
        _displayState.Push(new RenderSnapshot
        {
            PlayerPos = PlayerPos,
            PlayerHP = P.HP, PlayerMaxHP = P.MaxHP, PlayerLevel = P.Level,
            WaveNum = _waveNum,
            Enemies = Active.Where(e => e.Alive)
                            .Select(e => (e.Position, e.GetType().Name, e.HP, e.MaxHP))
                            .ToList(),
            OtherPlayers = ActivePlayers.Where(pl => pl != P && pl.HP > 0)
                                        .Select(pl => (pl.Position, PlayerInitials(pl.Name)))
                                        .ToList(),
            Party = AllPlayers.Select(pl => new PartyStat
            {
                Name = pl.Name, HP = pl.HP, MaxHP = pl.MaxHP, Level = pl.Level,
                SpellUses = pl.SpellUses, SongTokens = pl.SongTokens, PrayerUses = pl.PrayerUses,
                CanSpell = pl.KnownSpells.Any(), CanSong = pl.CanSing, CanPray = pl.CanPray,
                Arrows = pl.ArrowCount, Daggers = pl.DaggerCount, Axes = pl.AxeCount,
                ArmorDR = pl.ArmorDamageReduction, Ringlet = pl.RingletBonus,
                Weapon = pl.HeldWeapon ?? "Unarmed",
                Shield = pl.OffHandShieldName != null ? $"{pl.OffHandShieldName} +{pl.OffHandShieldBlock}" : "",
                IsCurrent = pl == P,
            }).ToList(),
            GroundWeapons = GroundWeapons.Select(w => w.Pos).ToList(),
        });
    }

    public bool Run()
    {
        int turnNum = 0;
        while (ActivePlayers.Any(p => p.HP > 0 || p.IsRaging))
        {
            var alive = Active.Where(e => e.Alive).ToList();
            if (!alive.Any() && !Pending.Any()) break;

            turnNum++;
            PushDisplay();
            Console.WriteLine($"\n━━━━ Turn {turnNum} ━━━━");

            // Burning / frost on all active players
            foreach (var ap in ActivePlayers.ToList())
            {
                if (ap.BurningDmg > 0)
                {
                    ap.HP -= ap.BurningDmg;
                    Console.WriteLine($"  {ap.Name} is BURNING! {ap.BurningDmg} damage. HP:{ap.HP}/{ap.MaxHP}");
                    ap.BurningTurns--;
                    if (ap.BurningTurns <= 0) { ap.BurningDmg = 0; Console.WriteLine($"  {ap.Name}'s flames die out."); }
                }
                if (ap.FrostTurns > 0)
                {
                    ap.FrostTurns--;
                    if (ap.FrostTurns <= 0) { ap.FrostPenalty = 0; Console.WriteLine($"  The frost clears from {ap.Name}'s limbs."); }
                }
                if (ap.RegenPerTurn > 0 && ap.HP > 0 && ap.HP < ap.MaxHP)
                {
                    int regen = Math.Min(ap.RegenPerTurn, ap.MaxHP - ap.HP);
                    ap.HP += regen;
                    Console.WriteLine($"  {ap.Name} regenerates {regen} HP. ({ap.HP}/{ap.MaxHP})");
                }
                if (ap.InvisibilityTurns > 0) { ap.InvisibilityTurns--; if (ap.InvisibilityTurns <= 0) Console.WriteLine($"  {ap.Name}'s invisibility fades."); }
                if (ap.PhantomImageTurns > 0) { ap.PhantomImageTurns--; if (ap.PhantomImageTurns <= 0) Console.WriteLine($"  The phantom image dissipates."); }
                if (ap.EnlargeActive) { ap.EnlargeTurns--; if (ap.EnlargeTurns <= 0) { ap.EnlargeActive = false; Console.WriteLine($"  {ap.Name} returns to normal size."); } }
                if (ap.MageShieldActive) { ap.MageShieldTurns--; if (ap.MageShieldTurns <= 0) { ap.MageShieldActive = false; Console.WriteLine($"  {ap.Name}'s mage shield shatters."); } }
                if (ap.SpellweaveArmorTurns > 0) { ap.SpellweaveArmorTurns--; if (ap.SpellweaveArmorTurns <= 0) Console.WriteLine($"  {ap.Name}'s spellweave armor fades."); }
                if (ap.TrueSightTurns > 0) { ap.TrueSightTurns--; if (ap.TrueSightTurns <= 0) Console.WriteLine($"  {ap.Name}'s true sight fades."); }
                if (ap.LichTouchTurns > 0) { ap.LichTouchTurns--; if (ap.LichTouchTurns <= 0) Console.WriteLine($"  {ap.Name}'s Lich Touch fades."); }
                if (ap.MagicHandTurns > 0)
                {
                    ap.MagicHandTurns--;
                    var mhTargets = Active.Where(e => e.Alive && !e.IsPlayerAlly).ToList();
                    if (mhTargets.Any())
                    {
                        var mhTarget = mhTargets.OrderBy(e => PlayerPos.ManhattanDist(e.Position)).First();
                        int mhAtk = Rng.Next(2, 5) + Rng.Next(2, 5);
                        int mhDdg = Rng.Next(mhTarget.MinDodge, mhTarget.MaxDodge + 1);
                        int mhDmg = Rng.Next(ap.MinDamage, ap.MaxDamage + 1);
                        Console.WriteLine($"  [Magic Hand] Strikes {mhTarget.Name}! Roll {mhAtk} vs dodge {mhDdg}.");
                        if (mhAtk >= mhDdg) { mhTarget.HP -= mhDmg; Console.WriteLine($"    HIT for {mhDmg}! HP:{mhTarget.HP}/{mhTarget.MaxHP}"); if (!mhTarget.Alive) HandleKill(mhTarget); }
                        else Console.WriteLine("    MISS!");
                    }
                    if (ap.MagicHandTurns <= 0) Console.WriteLine("  The magic hand dissipates.");
                }
            }
            // Raised dead ally turns countdown
            foreach (var e in Active.Where(e => e.IsPlayerAlly && e.Alive).ToList())
            {
                e.AllyTurnsLeft--;
                if (e.AllyTurnsLeft <= 0) { e.IsPlayerAlly = false; e.Fled = true; Console.WriteLine($"  {e.Name} crumbles back to dust."); }
            }

            // Arrive reinforcements
            var newPending = new List<(List<Enemy> batch, int turns)>();
            foreach (var (batch, turns) in Pending)
            {
                if (turns <= 0)
                {
                    Console.WriteLine("\n! REINFORCEMENTS ARRIVE !");
                    PlaceEnemies(batch, nearEdge: true);
                    foreach (var e in batch) { e.Fled = false; e.HP = e.MaxHP; Active.Add(e); Console.WriteLine($"  {e.Name} charges in! (HP:{e.HP})"); }
                }
                else newPending.Add((batch, turns - 1));
            }
            Pending = newPending;

            alive = Active.Where(e => e.Alive).ToList();

            // Bleed
            foreach (var e in alive.ToList())
            {
                if (e.BleedDmg > 0)
                {
                    e.HP -= e.BleedDmg;
                    Console.WriteLine($"  {e.Name} bleeds for {e.BleedDmg}. HP: {e.HP}/{e.MaxHP}");
                    if (!e.Alive) { Console.WriteLine($"  {e.Name} bleeds out!"); if (!e.XpAwarded) { e.XpAwarded = true; GainXP(e.XPValue); } }
                }
            }

            // Burning
            foreach (var e in alive.ToList())
            {
                if (e.BurningDmg > 0)
                {
                    e.HP -= e.BurningDmg;
                    Console.WriteLine($"  {e.Name} burns for {e.BurningDmg}. HP: {e.HP}/{e.MaxHP}");
                    if (e.FrostBurned)
                    {
                        // Roll to extinguish frostburn — needs to roll on the ground (5-6 on d6)
                        int rollOut = Rng.Next(1, 7);
                        if (rollOut >= 5)
                        {
                            e.BurningDmg = 0; e.BurningTurns = 0; e.FrostBurned = false;
                            Console.WriteLine($"  {e.Name} rolls on the ground and extinguishes the frostburn!");
                        }
                        else
                        {
                            e.BurningTurns--;
                            if (e.BurningTurns <= 0) { e.BurningDmg = 0; e.FrostBurned = false; Console.WriteLine($"  {e.Name}'s frostburn dies out."); }
                        }
                    }
                    else
                    {
                        e.BurningTurns--;
                        if (e.BurningTurns <= 0) { e.BurningDmg = 0; Console.WriteLine($"  {e.Name}'s flames die out."); }
                    }
                    if (!e.Alive) { Console.WriteLine($"  {e.Name} burns out!"); if (!e.XpAwarded) { e.XpAwarded = true; GainXP(e.XPValue); } }
                }
            }
            // Frost countdown
            foreach (var e in alive.ToList())
            {
                if (e.FrostTurns > 0)
                {
                    e.FrostTurns--;
                    if (e.FrostTurns <= 0) { e.FrostPenalty = 0; Console.WriteLine($"  {e.Name}'s frost clears."); }
                }
                if (e.HalfMovementTurns > 0)
                {
                    e.HalfMovementTurns--;
                    if (e.HalfMovementTurns <= 0) { e.HalfMovement = false; e.HalfMovementBlock = false; Console.WriteLine($"  {e.Name} regains full movement."); }
                }
            }

            alive = Active.Where(e => e.Alive).ToList();
            if (!alive.Any() && !Pending.Any()) break;

            if (AllPlayers.Count > 1)
            {
                Console.WriteLine();
                foreach (var pl in AllPlayers)
                    Console.WriteLine($"  {pl.Name}: HP {pl.HP}/{pl.MaxHP}  XP {pl.XP}  Lv {pl.Level}{(pl == P ? "  [active]" : "")}");
            }
            else
                Console.WriteLine($"\nHP: {P.HP}/{P.MaxHP}  XP: {P.XP}  Level: {P.Level}");
            if (P.BurningDmg > 0) Console.WriteLine($"  [BURNING {P.BurningDmg}/turn × {P.BurningTurns}t]");
            if (P.FrostPenalty > 0) Console.WriteLine($"  [FROZEN -{P.FrostPenalty} dodge × {P.FrostTurns}t]");
            ShowMap(alive);
            for (int i = 0; i < alive.Count; i++)
            {
                float ft = alive[i].Position.Feet(PlayerPos);
                string compass = alive[i].Position.CompassFrom(PlayerPos);
                Console.WriteLine($"  [{i + 1}] {alive[i].DisplayStatus()}  ({ft:F0}ft {compass})");
            }

            // Snapshot HP so consecutive-damage tracking works after player acts
            foreach (var e in Active.Where(x => x.Alive)) e.HpAtTurnStart = e.HP;

            // Each active player takes their turn in order
            foreach (var ap in ActivePlayers.ToList())
            {
                if (ap.HP <= 0 && !ap.IsRaging) continue;
                P = ap;
                alive = Active.Where(e => e.Alive).ToList();
                if (!alive.Any() && !Pending.Any()) break;
                if (AllPlayers.Count > 1) Console.WriteLine($"\n── {ap.Name}'s turn ──");
                bool fled = PlayerTurn();
                if (fled)
                {
                    Console.WriteLine($"  {ap.Name} escaped!");
                    FledPlayers.Add(ap);
                    ActivePlayers.Remove(ap);
                    AllPlayers.Remove(ap);
                    if (AllPlayers.Count == 0) { PlayerFled = true; return true; }
                }
                // Rage tick
                if (ap.IsRaging)
                {
                    ap.RageTurnsLeft--;
                    if (ap.RageTurnsLeft <= 0)
                    {
                        ap.IsRaging = false;
                        int healDice = ap.RagePointsSpent;
                        ap.RagePointsSpent = 0;
                        int rageHeal = 0;
                        for (int rd = 0; rd < healDice; rd++) rageHeal += Rng.Next(1, 5);
                        ap.HP = Math.Clamp(ap.HP + rageHeal, 0, ap.MaxHP);
                        Console.WriteLine($"  Rage fades! {ap.Name} recovered {rageHeal} HP ({healDice}d4). ({ap.HP}/{ap.MaxHP})");
                        int maxRage = 1 + (ap.Level >= 2 ? (ap.Level - 2) / 4 + 1 : 0);
                        if (ap.RagePoints < maxRage) ap.RagePoints = Math.Min(ap.RagePoints + 1, maxRage);
                    }
                }
                // Remove player from active turn order if dead
                if (ap.HP <= 0 && !ap.IsRaging)
                {
                    Console.WriteLine($"  {ap.Name} has fallen!");
                    ActivePlayers.Remove(ap);
                }
            }

            if (!ActivePlayers.Any()) break; // all players dead or fled

            alive = Active.Where(e => e.Alive).ToList();
            if (!alive.Any() && !Pending.Any()) break;

            // All alive enemies KO'd (and no reinforcements coming) → party wins
            if (!Pending.Any() && alive.Any() && alive.All(e => e.KnockedOut))
            {
                Console.WriteLine("\nAll enemies are knocked out! You stand victorious.");
                return true;
            }

            // Enemies target the first alive active player
            P = ActivePlayers.First(p => p.HP > 0);
            EnemyTurn();
            foreach (var ap in ActivePlayers) ap.ClearRoundEffects();

            if (SilenceTurns > 0)
            {
                SilenceTurns--;
                if (SilenceTurns <= 0) Console.WriteLine("\n  The silence lifts — voices and magic return.");
            }

            // Catch drummer deaths from any source (charm, allies, bleed, flee)
            SweepWarRhythm();

            foreach (var e in Active.Where(e => e.Alive)) e.EndOfRound();

            // Check again after enemy turn (e.g. charmed enemies KO each other)
            alive = Active.Where(e => e.Alive).ToList();
            if (!Pending.Any() && alive.Any() && alive.All(e => e.KnockedOut))
            {
                Console.WriteLine("\nAll enemies are knocked out! You stand victorious.");
                return true;
            }
        }
        return AllPlayers.Any(p => p.HP > 0);
    }

    // ── PLAYER TURN ──────────────────────────────────────────────────────

    bool PlayerTurn()
    {
        // Expire duelist effects at start of each turn
        if (P.CharacterType == "Duelist")
        {
            foreach (var k in P.DuelistEffectTurns.Keys.ToList())
            {
                P.DuelistEffectTurns[k]--;
                if (P.DuelistEffectTurns[k] <= 0) P.DuelistEffectTurns.Remove(k);
            }
        }

        if (SilenceTurns > 0)
            Console.WriteLine($"  [SILENCE — no spells, prayers or songs for {SilenceTurns} more turn(s)]");

        // Sanctuary ward countdown
        if (P.SanctuaryTurns > 0)
        {
            Console.WriteLine($"  [SANCTUARY — {P.SanctuaryTurns} turn(s): you can't be attacked, nor attack]");
            P.SanctuaryTurns--;
            if (P.SanctuaryTurns <= 0) Console.WriteLine("  [The sanctuary ward fades]");
        }

        // Redemption countdown
        if (P.RedemptionTurns > 0)
        {
            Console.WriteLine($"  [REDEEMED — doubled max HP, {P.RedemptionTurns} turn(s) left]");
            P.RedemptionTurns--;
            if (P.RedemptionTurns <= 0)
            {
                P.ExpireRedemption();
                Console.WriteLine($"  [Redemption fades — HP {P.HP}/{P.MaxHP}]");
            }
        }

        // Mass Blessings countdown
        if (P.BlessTurns > 0)
        {
            Console.WriteLine($"  [BLESSED — +{P.BlessBonusReceived} to rolls, {P.BlessTurns} turn(s) left]");
            P.BlessTurns--;
            if (P.BlessTurns <= 0)
            {
                P.ExpireBlessing();
                Console.WriteLine("  [The blessing fades]");
            }
        }

        // Musician song upkeep: pulse DeathTone, count down lingering echoes
        if (P.CanSing && P.ActiveSong != "")
        {
            if (P.SongPlaying)
            {
                Console.WriteLine($"  [♪ Playing {P.ActiveSong} on your {P.MusicInstrument} — tokens: {P.SongTokens}]");
                if (P.ActiveSong == "DeathTone") DeathTonePulse();
            }
            else
            {
                Console.WriteLine($"  [♪ {P.ActiveSong} echoes — {P.SongLingerTurns} turn(s) left]");
                if (P.ActiveSong == "DeathTone") DeathTonePulse();
                P.SongLingerTurns--;
                if (P.SongLingerTurns <= 0)
                {
                    Console.WriteLine($"  [♪ {P.ActiveSong} fades away]");
                    P.EndSong(AllPlayers);
                }
            }
        }

        if (P.IsRaging)
            Console.WriteLine($"  [RAGING — {P.RageTurnsLeft} turn(s) left, +{P.RagePointsSpent*2}d4 dmg/hit]");
        int actLeft = 2 + P.AdditionalActions;
        if (P.HasFeat("Chidia")) actLeft += 2;

        // Tier 2 grapple style: deduct actions pre-paid for breaking free on enemy's turn
        if (P.GrappleEscapePrePaid > 0 && actLeft > 0)
        {
            int cost = Math.Min(P.GrappleEscapePrePaid, actLeft);
            actLeft -= cost; P.GrappleEscapePrePaid -= cost;
            Console.WriteLine($"  [Grapple Escape Cost] {cost} action(s) spent on last turn's break-free.");
        }

        bool fled = false;
        bool justBlocked = false;
        Enemy? blockTarget = null;
        bool sprintPenaltyPending = false;

        while (actLeft > 0 && !fled && P.HP > 0)
        {
            var alive = Active.Where(e => e.Alive).ToList();
            if (!alive.Any()) break;

            Console.WriteLine($"\n  [Actions: {actLeft}]");

            // Clear grapple if grappler is dead
            if (P.IsGrappled && (P.GrappledBy == null || !P.GrappledBy.Alive))
            { P.IsGrappled = false; P.GrappledBy = null; }

            int gst = GrappleStyleTier();

            // Tier 4: free action after breaking free (grapple back or close to 5ft)
            if (P.PostGrappleBreakTarget != null && gst >= 4)
            {
                var pgbt = P.PostGrappleBreakTarget; P.PostGrappleBreakTarget = null;
                if (pgbt.Alive)
                {
                    Console.Write($"  [Tier 4] Free: [G]rapple {pgbt.Name} or [M]ove within 5ft? ");
                    string t4 = (Console.ReadLine() ?? "").Trim().ToLower();
                    if (t4.StartsWith("g")) DoGrapple(pgbt);
                    else if (t4.StartsWith("m"))
                    {
                        int steps = 2;
                        while (steps-- > 0 && PlayerPos.ManhattanDist(pgbt.Position) > 1)
                            PlayerPos = StepToward(PlayerPos, pgbt.Position);
                        Console.WriteLine($"  You close within 5ft of {pgbt.Name}. ({PlayerPos.X},{PlayerPos.Y})");
                    }
                }
            }

            // Tier 1+: auto break roll + free melee on grappler each action
            if (P.IsGrappled && P.GrappledBy != null && P.GrappledBy.Alive && gst >= 1)
            {
                var glr = P.GrappledBy;
                bool glOgre = glr is Ogre;
                int glMin = (glOgre ? 8 : P.MinGrapple + P.GetFeatStacks("Closeliner"))
                            + (gst >= 2 ? 1 : 0) + (gst >= 3 ? 1 : 0);
                int glMax = glOgre ? 12 : P.MaxGrapple;
                int glP = Rng.Next(glMin, glMax + 1), glE = Rng.Next(glr.MinGrapple, glr.MaxGrapple + 1);
                Console.WriteLine($"  [Grapple Style T{gst}] Auto break: your {glP} vs {glr.Name}'s {glE}.");
                if (glP >= glE)
                {
                    if (gst >= 4) P.PostGrappleBreakTarget = glr;
                    P.IsGrappled = false; P.GrappledBy = null;
                    Console.WriteLine("  You break free!");
                }
                else Console.WriteLine("  Still held.");

                // Free melee on grappler (if still held)
                if (P.IsGrappled && P.GrappledBy != null && P.GrappledBy.Alive)
                {
                    Console.Write($"  [Grapple Style] Free melee on {P.GrappledBy.Name}? (y/n): ");
                    if ((Console.ReadLine() ?? "").Trim().ToLower().StartsWith("y"))
                        DoAttack(P.GrappledBy);
                }
            }

            var opts = BuildOpts(justBlocked, alive, blockTarget);
            for (int i = 0; i < opts.Count; i++) Console.Write($"[{i + 1}]{opts[i]}  ");
            Console.WriteLine();
            Console.Write("  Action: ");
            string raw = (Console.ReadLine() ?? "").Trim().ToLower();

            string chosen;
            if (int.TryParse(raw, out int n) && n >= 1 && n <= opts.Count) chosen = opts[n - 1];
            else chosen = opts.FirstOrDefault(o => o.StartsWith(raw)) ?? raw;

            // Sanctuary: the warded player may not attack
            if (P.SanctuaryTurns > 0 && chosen is "attack" or "grapple" or "whirlwind" or "club sweep"
                or "throw dagger" or "throw axe" or "throw weapon" or "charge" or "duelist action" or "bard song")
            {
                Console.WriteLine("  The sanctuary's peace stays your hand — you cannot attack while warded.");
                continue;
            }

            Enemy? target = null;
            if (chosen is "attack" or "grapple" or "block" or "parry" or "sap" or "sunder" or "disarm")
            {
                target = PickTarget(alive);
                if (target == null) continue;
            }

            bool justSprinted = false;
            switch (chosen)
            {
                case "attack":
                    DoAttack(target!);
                    justBlocked = false;
                    break;

                case "grapple":
                    DoGrapple(target!);
                    justBlocked = false;
                    break;

                case "defend":
                    P.Defending = true;
                    Console.WriteLine("  Defensive stance. Incoming damage halved this round.");
                    justBlocked = false;
                    break;

                case "healing potion":
                    DoHeal();
                    justBlocked = false;
                    break;

                case "move":
                {
                    if (P.IsGrappled) { Console.WriteLine("  You can't move while grappled!"); continue; }
                    int moveRoll = Rng.Next(P.MinMovement, P.MaxMovement + 1) + P.MovementBonus;
                    Console.WriteLine($"  Move roll: {moveRoll} square(s).");
                    StepMovement(moveRoll);
                    justBlocked = false;
                    break;
                }

                case "sprint":
                {
                    if (P.IsGrappled) { Console.WriteLine("  You can't sprint while grappled!"); continue; }
                    int sprintRoll = Rng.Next(P.MinMovement, P.MaxMovement + 1) * 2 + P.MovementBonus;
                    Console.WriteLine($"  SPRINT! {sprintRoll} square(s). [-2 to next action roll]");
                    StepMovement(sprintRoll);
                    justSprinted = true;
                    justBlocked = false;
                    break;
                }

                case "charge":
                {
                    if (P.IsGrappled) { Console.WriteLine("  Can't charge while grappled!"); continue; }
                    var chargeAlive = Active.Where(e => e.Alive).ToList();
                    if (!chargeAlive.Any()) break;
                    Enemy chargeTarget;
                    if (chargeAlive.Count == 1) { chargeTarget = chargeAlive[0]; }
                    else
                    {
                        for (int ci = 0; ci < chargeAlive.Count; ci++) Console.Write($"[{ci+1}]{chargeAlive[ci].Name}  ");
                        Console.WriteLine();
                        Console.Write("  Charge target #: ");
                        if (!int.TryParse(Console.ReadLine()?.Trim(), out int cti) || cti < 1 || cti > chargeAlive.Count)
                        { Console.WriteLine("  Invalid."); continue; }
                        chargeTarget = chargeAlive[cti - 1];
                    }
                    int chargeRoll = Rng.Next(P.MinMovement, P.MaxMovement + 1) * 2 + P.MovementBonus;
                    Console.WriteLine($"  CHARGE! {chargeRoll} squares toward {chargeTarget.Name}.");
                    var chOccupied = new HashSet<(int,int)>(Active.Where(e => e.Alive && e != chargeTarget).Select(e => (e.Position.X, e.Position.Y)));
                    for (int step = 0; step < chargeRoll; step++)
                    {
                        if (PlayerPos.IsCardinalAdjacent(chargeTarget.Position)) break;
                        var next = StepToward(PlayerPos, chargeTarget.Position);
                        if (chOccupied.Contains((next.X, next.Y))) break;
                        PlayerPos = next;
                    }
                    Console.WriteLine($"  Now at ({PlayerPos.X},{PlayerPos.Y}).");
                    if (PlayerPos.IsCardinalAdjacent(chargeTarget.Position) && chargeTarget.Alive)
                    {
                        Console.Write($"  In range! Free [A]ttack or [G]rapple on {chargeTarget.Name}? ");
                        string cAct = (Console.ReadLine() ?? "").Trim().ToLower();
                        if (cAct.StartsWith("a")) DoAttack(chargeTarget);
                        else if (cAct.StartsWith("g")) DoGrapple(chargeTarget);
                    }
                    else Console.WriteLine($"  {chargeTarget.Name} is still out of melee range.");
                    justBlocked = false;
                    break;
                }

                case "break grapple":
                {
                    if (!P.IsGrappled || P.GrappledBy == null || !P.GrappledBy.Alive)
                    { P.IsGrappled = false; P.GrappledBy = null; justBlocked = false; break; }
                    bool bgOgre = P.GrappledBy is Ogre;
                    int bgMin = (bgOgre ? 8 : P.MinGrapple + P.GetFeatStacks("Closeliner"))
                                + (gst >= 2 ? 1 : 0) + (gst >= 3 ? 1 : 0);
                    int bgMax = bgOgre ? 12 : P.MaxGrapple;
                    int bgPGr = Rng.Next(bgMin, bgMax + 1);
                    int bgEGr = Rng.Next(P.GrappledBy.MinGrapple, P.GrappledBy.MaxGrapple + 1);
                    string bgType = bgOgre ? "Counter-grapple (8-12)" : "Break-free";
                    Console.WriteLine($"  [GRAPPLED by {P.GrappledBy.Name}] {bgType}: your {bgPGr} vs their {bgEGr}.");
                    if (bgPGr >= bgEGr)
                    {
                        var bgGrappler = P.GrappledBy;
                        P.IsGrappled = false; P.GrappledBy = null;
                        Console.WriteLine("  You break free!");
                        if (gst >= 4) P.PostGrappleBreakTarget = bgGrappler;
                    }
                    else Console.WriteLine("  Still held — you can act but cannot run.");
                    justBlocked = false;
                    if (gst >= 3) continue; // tier 3+: free action
                    break;
                }

                case "run away":
                {
                    if (P.IsGrappled) { Console.WriteLine("  You can't run while grappled!"); continue; }
                    Console.WriteLine("  You try to escape!");
                    bool blocked = false;
                    foreach (var e in alive)
                    {
                        if (blocked) break;
                        int eAtk = Rng.Next(e.MinAttack, e.MaxAttack + 1) - e.AttackPenalty;
                        int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) - 2;
                        Console.WriteLine($"  {e.Name} reacts! Roll {eAtk} vs your dodge-2 ({pDdg}).");
                        if (eAtk >= pDdg)
                        {
                            int dmg = Rng.Next(e.MinDamage, e.MaxDamage + 1);
                            if (P.Defending) dmg = Math.Max(1, dmg / 2);
                            Console.WriteLine($"  {e.Name} hits you for {dmg}! You fail to escape. HP:{P.HP - dmg}/{P.MaxHP}");
                            P.HP -= dmg;
                            blocked = true;
                        }
                        else Console.WriteLine($"  {e.Name} misses — you slip past!");
                    }
                    if (!blocked) { fled = true; Console.WriteLine("  (Game will auto-save.)"); }
                    justBlocked = false;
                    break;
                }

                case "exit":
                {
                    Console.WriteLine("  Saving and exiting...");
                    ExitRequested = true;
                    fled = true;
                    break;
                }

                case "rage":
                {
                    if (P.RagePoints <= 0 || P.IsRaging) { Console.WriteLine("  No rage points available."); continue; }
                    int maxSpend = P.RagePoints;
                    Console.Write($"  Spend how many rage points? (1-{maxSpend}): ");
                    if (!int.TryParse(Console.ReadLine()?.Trim(), out int rpts) || rpts < 1 || rpts > maxSpend)
                    { Console.WriteLine("  Invalid."); continue; }
                    P.RagePoints -= rpts;
                    P.RagePointsSpent = rpts;
                    P.IsRaging = true;
                    P.RageTurnsLeft = 3;
                    Console.WriteLine($"  RAGE! +{rpts*2}d4 damage for 3 turns!");
                    justBlocked = false;
                    break;
                }

                case "whirlwind":
                {
                    var wwAlive = Active.Where(e => e.Alive).ToList();
                    if (!wwAlive.Any()) { Console.WriteLine("  No enemies to hit."); continue; }
                    int numHits = 1 + (P.Level >= 2 ? (P.Level - 2) / 3 + 1 : 0);
                    Console.Write($"  Whirlwind ({numHits} swings)! [C]lockwise or [A]nti-clockwise? ");
                    string wwDir = (Console.ReadLine() ?? "c").Trim().ToLower();
                    bool cw = !wwDir.StartsWith("a");
                    // 8 directions cycling CW: N NE E SE S SW W NW
                    var cwDirs = new (int dx, int dy)[] { (0,-1),(1,-1),(1,0),(1,1),(0,1),(-1,1),(-1,0),(-1,-1) };
                    var ccwDirs = new (int dx, int dy)[] { (0,-1),(-1,-1),(-1,0),(-1,1),(0,1),(1,1),(1,0),(1,-1) };
                    var dirs = cw ? cwDirs : ccwDirs;
                    var hitSet = new HashSet<Enemy>();
                    Console.WriteLine($"  You spin {(cw ? "clockwise" : "counter-clockwise")}, striking {numHits} times!");
                    int minDmgW = P.HeldWeapon != null ? WeaponPickupStats(P.HeldWeapon).MinDmg : P.MinDamage;
                    int maxDmgW = P.HeldWeapon != null ? WeaponPickupStats(P.HeldWeapon).MaxDmg : P.MaxDamage;
                    for (int hi = 0; hi < numHits; hi++)
                    {
                        wwAlive = Active.Where(e => e.Alive).ToList();
                        if (!wwAlive.Any()) break;
                        var (dx, dy) = dirs[hi % 8];
                        int range = hi >= 4 ? 2 : 1;
                        bool swingHit = false;
                        for (int r = 1; r <= range; r++)
                        {
                            var sq = new GridPos(PlayerPos.X + dx * r, PlayerPos.Y + dy * r);
                            foreach (var te in wwAlive.Where(e => e.Position.SameAs(sq)).ToList())
                            {
                                if (hi < 4 && hitSet.Contains(te)) continue;
                                hitSet.Add(te);
                                swingHit = true;
                                int wAtk = Rng.Next(P.MinAttack, P.MaxAttack + 1);
                                int tDdg = Rng.Next(te.MinDodge, te.MaxDodge + 1) - te.DodgePenalty;
                                Console.WriteLine($"  Swing {hi+1}: {te.Name} — roll {wAtk} vs dodge {tDdg}.");
                                if (wAtk >= tDdg && !EnemyBlocks(te, wAtk))
                                {
                                    int dmgW = Rng.Next(minDmgW, maxDmgW + 1);
                                    if (P.IsRaging) for (int d = 0; d < P.RagePointsSpent * 2; d++) dmgW += Rng.Next(1, 5);
                                    dmgW = ReduceByToughHide(te, dmgW);
                                    Console.WriteLine($"  HIT! {dmgW} dmg → {te.Name} HP:{te.HP - dmgW}/{te.MaxHP}");
                                    te.HP -= dmgW;
                                    if (!te.Alive) ResolveDowned(te, IsNonLethalAttack());
                                }
                                else Console.WriteLine($"  Miss!");
                            }
                        }
                        if (!swingHit) Console.WriteLine($"  Swing {hi+1}: no target in that direction.");
                    }
                    justBlocked = false;
                    break;
                }

                case "cast spell":
                {
                    if (!P.KnownSpells.Any()) break;
                    if (P.SpellUses <= 0) { Console.WriteLine("  You have no spell casts left. Rest to recover."); continue; }
                    Console.WriteLine($"  Known spells (casts left: {P.SpellUses}):");
                    for (int si = 0; si < P.KnownSpells.Count; si++) Console.WriteLine($"  [{si+1}] {P.KnownSpells[si]}");
                    Console.Write("  Cast which: ");
                    if (int.TryParse(Console.ReadLine()?.Trim(), out int si2) && si2 >= 1 && si2 <= P.KnownSpells.Count)
                    {
                        if (P.SpellUses <= 0) { Console.WriteLine("  You have no spell casts left. Rest to recover."); continue; }
                        P.SpellUses--;
                        DoSpell(P.KnownSpells[si2 - 1], alive);
                        if (P.HasFeat("Twin Caster") && P.SpellUses > 0)
                        {
                            Console.Write("  [Twin Caster] Cast a second spell? [y/n]: ");
                            if ((Console.ReadLine() ?? "").Trim().ToLower().StartsWith("y"))
                            {
                                Console.WriteLine($"  Known spells (casts left: {P.SpellUses}):");
                                for (int tsi = 0; tsi < P.KnownSpells.Count; tsi++) Console.WriteLine($"  [{tsi+1}] {P.KnownSpells[tsi]}");
                                Console.Write("  Cast which: ");
                                if (int.TryParse(Console.ReadLine()?.Trim(), out int tsi2) && tsi2 >= 1 && tsi2 <= P.KnownSpells.Count)
                                {
                                    P.SpellUses--;
                                    DoSpell(P.KnownSpells[tsi2 - 1], alive);
                                }
                            }
                        }
                    }
                    else Console.WriteLine("  Invalid.");
                    justBlocked = false;
                    break;
                }

                case "block":
                {
                    if (target == null) break;
                    if (target is Ogre && target.PowerAttackMode)
                    {
                        int rawDmg = Math.Max(1, Rng.Next(target.MinDamage, target.MaxDamage + 1) / 2);
                        rawDmg = ReduceByToughHide(target, rawDmg);
                        Console.WriteLine($"  The ogre's power attack overwhelms your guard! You take {rawDmg} damage. HP:{P.HP - rawDmg}/{P.MaxHP}");
                        P.HP -= rawDmg;
                        justBlocked = false;
                        break;
                    }
                    int bRoll = Rng.Next(P.MinBlock, P.MaxBlock + 1);
                    int eAtk = Rng.Next(target.MinAttack, target.MaxAttack + 1);
                    Console.WriteLine($"  Block! Roll {bRoll} vs {target.Name}'s attack {eAtk}.");
                    if (bRoll >= eAtk)
                    {
                        Console.WriteLine($"  Blocked! {target.Name} is off-balance (-2 dodge).");
                        target.DodgePenalty += 2; target.OffBalance = true;
                        justBlocked = true; blockTarget = target;
                        FreeAttackPrompt("Counter Strike", target);
                        if (P.HasFeat("Judo")) JudoPrompt(target);
                        if (P.HasFeat("Chidia Black Belt") && bRoll == 6)
                            Console.WriteLine($"  Chidia Black Belt! {target.Name}'s weapon arm is damaged!");
                    }
                    else
                    {
                        int dmg = Math.Max(1, Rng.Next(target.MinDamage, target.MaxDamage + 1) / 2);
                        Console.WriteLine($"  Block failed! You take {dmg} damage. HP: {P.HP - dmg}/{P.MaxHP}");
                        P.HP -= dmg;
                        justBlocked = false;
                    }
                    break;
                }

                case "parry":
                {
                    if (blockTarget == null || !blockTarget.Alive) { Console.WriteLine("  No blocked target for parry."); continue; }
                    if (blockTarget is Ogre) { Console.WriteLine("  You can't parry an ogre — they're too massive!"); justBlocked = false; continue; }
                    int pRoll = Rng.Next(P.MinParry, P.MaxParry + 1);
                    int pDdg = Rng.Next(blockTarget.MinDodge, blockTarget.MaxDodge + 1);
                    Console.WriteLine($"  Parry! Roll {pRoll} vs {blockTarget.Name}'s dodge {pDdg}.");
                    if (pRoll >= pDdg)
                    {
                        Console.WriteLine($"  Parry! {blockTarget.Name} is knocked off their feet!");
                        blockTarget.KnockedDown = true; blockTarget.OffBalance = true;
                        FreeAttackPrompt("Counter Strike", blockTarget);
                        if (P.HasFeat("Judo")) JudoPrompt(blockTarget);
                    }
                    else Console.WriteLine("  Parry failed!");
                    justBlocked = false;
                    break;
                }

                case "play song":
                {
                    if (P.SongTokens <= 0) { Console.WriteLine("  No song tokens left. Rest to recover."); continue; }
                    int sb = P.SongBonusAmount(), sd = P.SlayerDmgBonus(), fd = P.FearDiceCount();
                    int redeemDice = Math.Max(1, P.Level / 3);
                    var songList = new List<(string Name, string Desc)>();
                    if (P.CharacterType == "Musician")
                    {
                        songList.Add(("Slayer",         $"+{sb} attack, +{sd} damage (melee, ranged, spells, grapple)"));
                        songList.Add(("Wind Song",      $"+{sb} dodge, block and parry"));
                        songList.Add(("Hardstone Song", $"take {sb} less damage"));
                        songList.Add(("DeathTone",      $"each turn, enemies with HP ≤ {fd}d6 roll flee"));
                    }
                    if (P.HasFeat("War Song"))            songList.Add(("War Song",            "+1 dodge, +1 all attacks, +1 grapple, +2 healing"));
                    if (P.HasFeat("Silence Song"))        songList.Add(("Silence Song",        "no spells, prayers or songs for ANYONE, 1d4 turns"));
                    if (P.HasFeat("Song of the Redeemer")) songList.Add(("Song of the Redeemer", $"heal all allies {redeemDice}d4"));
                    Console.WriteLine($"  Songs (tokens: {P.SongTokens}):");
                    for (int sli = 0; sli < songList.Count; sli++)
                        Console.WriteLine($"  [{sli + 1}] {songList[sli].Name,-20} — {songList[sli].Desc}");
                    Console.Write("  Song # (or [C]ancel): ");
                    string sraw = (Console.ReadLine() ?? "").Trim().ToLower();
                    string? song = null;
                    if (int.TryParse(sraw, out int si) && si >= 1 && si <= songList.Count) song = songList[si - 1].Name;
                    else song = songList.Select(s => s.Name).FirstOrDefault(s => s.ToLower().StartsWith(sraw));
                    if (song == null) { Console.WriteLine("  You lower your instrument."); continue; }

                    P.SongTokens--;
                    if (song == "Silence Song")
                    {
                        // One thunderous chord, then nothing — ends every active song too
                        foreach (var pl in AllPlayers) pl.EndSong(AllPlayers);
                        SilenceTurns = Rng.Next(1, 5);
                        Console.WriteLine($"  ♪♪ SILENCE! A crushing chord swallows all sound for {SilenceTurns} turn(s).");
                        Console.WriteLine("  No spells, prayers or songs can be used by anyone!");
                    }
                    else if (song == "Song of the Redeemer")
                    {
                        Console.WriteLine($"  ♪ The Song of the Redeemer washes over the party! ({redeemDice}d4 healing each)");
                        foreach (var pl in AllPlayers.Where(pl => pl.HP > 0))
                        {
                            int heal = 0;
                            for (int d = 0; d < redeemDice; d++) heal += Rng.Next(1, 5);
                            heal = Math.Min(heal, pl.MaxHP - pl.HP);
                            pl.HP += heal;
                            Console.WriteLine($"    {pl.Name} heals {heal}. ({pl.HP}/{pl.MaxHP})");
                        }
                    }
                    else
                    {
                        if (P.ActiveSong != "") P.EndSong(AllPlayers);   // switching songs cuts the old one off
                        P.ActiveSong = song;
                        P.SongPlaying = true;
                        P.SongLingerTurns = 0;
                        P.ApplySongStats(AllPlayers);
                        Console.WriteLine($"  ♪ You strike up {song} on your {P.MusicInstrument}! (Tokens left: {P.SongTokens})" +
                            (AllPlayers.Count > 1 ? "  The whole party is emboldened!" : ""));
                        if (song == "DeathTone") DeathTonePulse();
                    }
                    justBlocked = false;
                    break;
                }

                case "stop song":
                {
                    P.SongPlaying = false;
                    P.SongLingerTurns = Rng.Next(1, 5);
                    Console.WriteLine($"  ♪ You end {P.ActiveSong}; its echo lingers for {P.SongLingerTurns} turn(s).");
                    continue;   // stopping is a free action
                }

                case "bard song":
                {
                    int stacks = Math.Max(1, P.GetFeatStacks("Bard Song"));
                    int songRoll = Rng.Next(P.MinBardSong, P.MaxBardSong + 1) + stacks;
                    Console.WriteLine($"  Bard Song! Roll {songRoll}.");
                    foreach (var e in alive)
                    {
                        int res = Rng.Next(2, 9);
                        if (songRoll > res) { e.Charmed = true; Console.WriteLine($"  {e.Name} charmed!"); }
                        else Console.WriteLine($"  {e.Name} resists (rolled {res}).");
                    }
                    justBlocked = false;
                    break;
                }

                case "pray":
                {
                    if (P.PrayerUses <= 0) { Console.WriteLine("  You have no prayers left. Rest to recover."); continue; }
                    int L = P.Level;
                    bool isPriest  = P.CharacterType == "Priest";
                    int healDice   = 1 + (L >= 2 ? (L-2)/3+1 : 0);
                    int forgDice   = 2 + (L >= 2 ? (L-2)/3+1 : 0);
                    int forgThresh = 10 + 5 * (L >= 2 ? (L-2)/4+1 : 0); // % of max HP
                    int forgAoe    = 6  * (L >= 2 ? (L-2)/4+1 : 0);     // feet (0 = single target)
                    int lordMult   = Math.Max(1, L/4);
                    int lordD4s    = L >= 2 ? (L-2)/3+1 : 0;
                    int mostHighDice = Math.Max(1, L / 3);

                    Console.WriteLine($"  Prayers available (uses left: {P.PrayerUses}):");
                    if (isPriest)
                    {
                        Console.WriteLine($"  [1] Prayer of Healing  — heal {healDice}d6 (range 25ft, self or ally)");
                        Console.WriteLine($"  [2] Forgiveness        — convert enemy ≤{forgThresh}% HP; roll {forgDice}d4 vs HP{(forgAoe > 0 ? $"; AoE {forgAoe}ft" : "")} (range 30ft)");
                        Console.WriteLine($"  [3] Lord's Prayer      — {lordMult}d6{(lordD4s > 0 ? $"+{lordD4s}d4" : "")} dmg to all enemies within 6ft");
                        if (L >= 20) Console.WriteLine($"  [4] Last Rites         — revive a fallen party member (1d6 HP)");
                    }
                    if (P.HasFeat("Prayer of Sanctuary"))      Console.WriteLine($"  [5] Sanctuary          — ward an ally 1d4 turns: can't be attacked nor attack (50ft)");
                    if (P.HasFeat("Prayer of the Most High"))  Console.WriteLine($"  [6] The Most High      — {mostHighDice}d4 holy dmg to ALL enemies within 50ft");
                    if (P.HasFeat("Prayer of Redemption"))     Console.WriteLine($"  [7] Redemption         — fully heal an ally + double max HP for 1d4 turns");
                    if (P.HasFeat("Prayer of Mass Blessings")) Console.WriteLine($"  [8] Mass Blessings     — +1d4 to all allies' rolls for 1d4 turns");
                    Console.Write("  Choose prayer: ");
                    string pc = (Console.ReadLine() ?? "").Trim();

                    bool validPrayer = pc switch
                    {
                        "1" or "2" or "3" => isPriest,
                        "4" => isPriest && L >= 20,
                        "5" => P.HasFeat("Prayer of Sanctuary"),
                        "6" => P.HasFeat("Prayer of the Most High"),
                        "7" => P.HasFeat("Prayer of Redemption"),
                        "8" => P.HasFeat("Prayer of Mass Blessings"),
                        _ => false
                    };
                    if (!validPrayer) { Console.WriteLine("  You know no such prayer."); continue; }
                    P.PrayerUses--;

                    if (pc == "5") // ── Prayer of Sanctuary ───────────────
                    {
                        Player sancTarget = P;
                        if (AllPlayers.Count > 1)
                        {
                            var living = AllPlayers.Where(pl => pl.HP > 0).ToList();
                            for (int pi2 = 0; pi2 < living.Count; pi2++) Console.Write($"[{pi2 + 1}]{living[pi2].Name}  ");
                            Console.Write("\n  Ward who: ");
                            if (int.TryParse(Console.ReadLine()?.Trim(), out int wi) && wi >= 1 && wi <= living.Count)
                                sancTarget = living[wi - 1];
                        }
                        sancTarget.SanctuaryTurns = Rng.Next(1, 5);
                        Console.WriteLine($"  SANCTUARY! A divine ward surrounds {sancTarget.Name} for {sancTarget.SanctuaryTurns} turn(s).");
                        Console.WriteLine("  They cannot be attacked — nor raise a hand in anger.");
                        justBlocked = false;
                        break;
                    }
                    if (pc == "6") // ── Prayer of the Most High ───────────
                    {
                        int mhDmg = 0;
                        for (int d = 0; d < mostHighDice; d++) mhDmg += Rng.Next(1, 5);
                        Console.WriteLine($"  THE MOST HIGH ANSWERS! {mhDmg} ({mostHighDice}d4) holy damage sears all enemies within 50ft!");
                        foreach (var mh in alive.Where(e => PlayerPos.Feet(e.Position) <= 50f).ToList())
                        {
                            int dealt = mh.MagicResistant ? Math.Max(1, mhDmg / 2) : mhDmg;
                            mh.HP -= dealt;
                            Console.WriteLine($"    {mh.Name} takes {dealt}! HP:{mh.HP}/{mh.MaxHP}");
                            if (!mh.Alive) HandleKill(mh);
                        }
                        justBlocked = false;
                        break;
                    }
                    if (pc == "7") // ── Prayer of Redemption ──────────────
                    {
                        Player redTarget = P;
                        if (AllPlayers.Count > 1)
                        {
                            var living = AllPlayers.Where(pl => pl.HP > 0).ToList();
                            for (int pi3 = 0; pi3 < living.Count; pi3++) Console.Write($"[{pi3 + 1}]{living[pi3].Name}  ");
                            Console.Write("\n  Redeem who: ");
                            if (int.TryParse(Console.ReadLine()?.Trim(), out int ri) && ri >= 1 && ri <= living.Count)
                                redTarget = living[ri - 1];
                        }
                        redTarget.ExpireRedemption();   // don't stack with an existing redemption
                        redTarget.RedemptionExtraHP = redTarget.MaxHP;
                        redTarget.MaxHP *= 2;
                        redTarget.HP = redTarget.MaxHP;
                        redTarget.RedemptionTurns = Rng.Next(1, 5);
                        Console.WriteLine($"  REDEMPTION! {redTarget.Name} is fully healed and their vigor doubles for {redTarget.RedemptionTurns} turn(s)!");
                        Console.WriteLine($"    HP: {redTarget.HP}/{redTarget.MaxHP}");
                        justBlocked = false;
                        break;
                    }
                    if (pc == "8") // ── Prayer of Mass Blessings ──────────
                    {
                        int bless = Rng.Next(1, 5);
                        int blessTurns = Rng.Next(1, 5);
                        Console.WriteLine($"  MASS BLESSINGS! +{bless} to all rolls for every ally, {blessTurns} turn(s)!");
                        foreach (var pl in AllPlayers.Where(pl => pl.HP > 0))
                            pl.ApplyBlessing(bless, blessTurns);
                        justBlocked = false;
                        break;
                    }

                    if (pc == "1") // ── Prayer of Healing ─────────────────
                    {
                        int roll = P.PrayerHealBonus;
                        if (P.HasFeat("Elemental") && P.ElementalFocus == "holy") roll += 2;
                        for (int d = 0; d < healDice; d++) roll += Rng.Next(1, 7);

                        // Healing energy harms undead — offer to smite a nearby undead instead of self-heal
                        var undeadTargets = alive.Where(en => en.IsUndead && PlayerPos.Feet(en.Position) <= 25f).ToList();
                        Enemy? smiteTarget = null;
                        if (undeadTargets.Any())
                        {
                            Console.Write($"  Undead within 25ft! [S]mite an undead for {roll} radiant, or [H]eal self? ");
                            if ((Console.ReadLine() ?? "").Trim().ToLower().StartsWith("s"))
                                smiteTarget = undeadTargets.Count == 1 ? undeadTargets[0] : PickTarget(undeadTargets);
                        }

                        if (smiteTarget != null)
                        {
                            smiteTarget.HP -= roll;
                            Console.WriteLine($"  Prayer of Healing SEARS {smiteTarget.Name} for {roll} radiant damage! HP:{smiteTarget.HP}/{smiteTarget.MaxHP}");
                            if (!smiteTarget.Alive) HandleKill(smiteTarget);
                        }
                        else
                        {
                            int heal = Math.Min(roll, P.MaxHP - P.HP);
                            P.HP += heal;
                            Console.WriteLine($"  Prayer of Healing! Restored {heal} HP. ({P.HP}/{P.MaxHP})");
                        }
                    }
                    else if (pc == "2") // ── Forgiveness ───────────────────
                    {
                        var forgTargets = new List<Enemy>();
                        if (forgAoe > 0)
                        {
                            // AoE: all enemies within 30ft range and AoE radius who qualify
                            forgTargets = alive.Where(e =>
                                PlayerPos.Feet(e.Position) <= 30f &&
                                e.HP <= (int)Math.Ceiling(e.MaxHP * forgThresh / 100f)).ToList();
                            if (!forgTargets.Any())
                            { Console.WriteLine($"  No enemies within 30ft at ≤{forgThresh}% HP."); break; }
                            Console.WriteLine($"  Forgiveness AoE ({forgAoe}ft)! Targeting {forgTargets.Count} enemy(ies).");
                        }
                        else
                        {
                            var t = PickTarget(alive.Where(e =>
                                PlayerPos.Feet(e.Position) <= 30f &&
                                e.HP <= (int)Math.Ceiling(e.MaxHP * forgThresh / 100f)).ToList());
                            if (t == null) { Console.WriteLine($"  No enemy within 30ft at ≤{forgThresh}% HP."); break; }
                            forgTargets.Add(t);
                        }
                        foreach (var ft in forgTargets)
                        {
                            int roll = 0;
                            for (int d = 0; d < forgDice; d++) roll += Rng.Next(1, 5);
                            Console.WriteLine($"  Forgiveness on {ft.Name} (HP:{ft.HP})! Roll {forgDice}d4={roll} vs HP {ft.HP}.");
                            if (roll >= ft.HP)
                            {
                                Console.WriteLine($"  {ft.Name} is forgiven! They lay down their arms and depart.");
                                HandleKill(ft);
                            }
                            else Console.WriteLine($"  {ft.Name} resists the prayer.");
                        }
                    }
                    else if (pc == "3") // ── Lord's Prayer ─────────────────
                    {
                        var lordTargets = alive.Where(e => PlayerPos.Feet(e.Position) <= 6f).ToList();
                        if (!lordTargets.Any()) { Console.WriteLine("  No enemies within 6ft!"); break; }
                        Console.WriteLine($"  Lord's Prayer! ({lordMult}d6{(lordD4s > 0 ? $"+{lordD4s}d4" : "")} dmg to {lordTargets.Count} enemy(ies))");
                        foreach (var lt in lordTargets)
                        {
                            int dmg = 0;
                            for (int d = 0; d < lordMult; d++) dmg += Rng.Next(1, 7);
                            for (int d = 0; d < lordD4s; d++) dmg += Rng.Next(1, 5);
                            if (P.HasFeat("Elemental") && P.ElementalFocus == "holy") dmg += 2;
                            dmg = ReduceByToughHide(lt, dmg);
                            Console.WriteLine($"  {lt.Name} takes {dmg} holy dmg. HP:{lt.HP - dmg}/{lt.MaxHP}");
                            lt.HP -= dmg;
                            if (!lt.Alive) HandleKill(lt);
                        }
                    }
                    else if (pc == "4" && L >= 20) // ── Last Rites ────────────
                    {
                        var fallen = AllPlayers.Where(pl => pl != P && pl.HP <= 0).ToList();
                        if (!fallen.Any()) { Console.WriteLine("  No fallen party members nearby."); continue; }
                        Player? reviveTarget;
                        if (fallen.Count == 1) { reviveTarget = fallen[0]; }
                        else
                        {
                            for (int ri = 0; ri < fallen.Count; ri++)
                                Console.WriteLine($"  [{ri+1}] {fallen[ri].Name}");
                            Console.Write("  Revive whom? ");
                            reviveTarget = int.TryParse((Console.ReadLine() ?? "").Trim(), out int ri2) && ri2 >= 1 && ri2 <= fallen.Count
                                ? fallen[ri2 - 1] : null;
                        }
                        if (reviveTarget == null) { Console.WriteLine("  No one revived."); continue; }
                        int reviveHp = P.HasFeat("Holy Roller") ? reviveTarget.MaxHP * 3 / 4 : Rng.Next(1, 7);
                        reviveTarget.HP = reviveHp;
                        if (!ActivePlayers.Contains(reviveTarget)) ActivePlayers.Add(reviveTarget);
                        Console.WriteLine($"  Last Rites! {reviveTarget.Name} rises with {reviveHp} HP!");
                    }
                    else { Console.WriteLine("  No prayer chosen."); continue; }
                    // Holy Roller: offer a second prayer in the same action
                    if (P.HasFeat("Holy Roller"))
                    {
                        Console.Write("  [Holy Roller] Second prayer? [y/n]: ");
                        if ((Console.ReadLine() ?? "n").Trim().ToLower().StartsWith("y"))
                        {
                            Console.Write("  Choose prayer: ");
                            string pc2 = (Console.ReadLine() ?? "").Trim();
                            if (pc2 == "1")
                            {
                                int roll2 = P.PrayerHealBonus;
                                if (P.HasFeat("Elemental") && P.ElementalFocus == "holy") roll2 += 2;
                                for (int d = 0; d < healDice; d++) roll2 += Rng.Next(1, 7);
                                int heal2 = Math.Min(roll2, P.MaxHP - P.HP);
                                P.HP += heal2;
                                Console.WriteLine($"  [Holy Roller] Prayer of Healing! Restored {heal2} HP. ({P.HP}/{P.MaxHP})");
                            }
                            else if (pc2 == "3")
                            {
                                var alive2 = Active.Where(e => e.Alive).ToList();
                                var lordTargets2 = alive2.Where(e => PlayerPos.Feet(e.Position) <= 6f).ToList();
                                if (!lordTargets2.Any()) Console.WriteLine("  No enemies within 6ft!");
                                else
                                {
                                    Console.WriteLine($"  [Holy Roller] Lord's Prayer! ({lordTargets2.Count} enemy(ies))");
                                    foreach (var lt2 in lordTargets2)
                                    {
                                        int dmg2 = 0;
                                        for (int d = 0; d < lordMult; d++) dmg2 += Rng.Next(1, 7);
                                        for (int d = 0; d < lordD4s; d++) dmg2 += Rng.Next(1, 5);
                                        if (P.HasFeat("Elemental") && P.ElementalFocus == "holy") dmg2 += 2;
                                        dmg2 = ReduceByToughHide(lt2, dmg2);
                                        Console.WriteLine($"  {lt2.Name} takes {dmg2} holy dmg. HP:{lt2.HP - dmg2}/{lt2.MaxHP}");
                                        lt2.HP -= dmg2;
                                        if (!lt2.Alive) HandleKill(lt2);
                                    }
                                }
                            }
                            else Console.WriteLine("  [Holy Roller] No second prayer chosen.");
                        }
                    }
                    justBlocked = false;
                    break;
                }

                case "pick up goblin sword":
                    P.HasGoblinSword = true;
                    P.OffhandMaxDamage += 2;
                    Console.WriteLine($"  You grab a goblin sword! Off-hand max damage → {P.OffhandMaxDamage}.");
                    break;

                case "throw weapon":
                {
                    var throwTarget = PickTarget(alive);
                    if (throwTarget == null) continue;
                    float throwFeet = PlayerPos.Feet(throwTarget.Position);
                    int throwMaxFt = P.HeldWeapon == "Goblin Dagger" ? 20 : 15;
                    if (throwFeet > throwMaxFt)
                    {
                        Console.WriteLine($"  Too far! Max {throwMaxFt}ft for {P.HeldWeapon}.");
                        continue;
                    }
                    int thrAtk, thrDmgMin, thrDmgMax;
                    if (P.HeldWeapon == "Goblin Dagger")
                    {
                        thrAtk = Rng.Next(1, 7);
                        thrDmgMin = 1; thrDmgMax = 6;
                    }
                    else // Troll Axe
                    {
                        thrAtk = Rng.Next(2, 13);
                        if (throwFeet <= 7.5f) { thrDmgMin = 2; thrDmgMax = 6; }
                        else if (throwFeet <= 10f) { thrDmgMin = 1; thrDmgMax = 6; }
                        else { thrDmgMin = 1; thrDmgMax = 2; }
                    }
                    thrAtk += SlayerAtk();
                    int thrDdg = Rng.Next(throwTarget.MinDodge, throwTarget.MaxDodge + 1) - throwTarget.DodgePenalty;
                    Console.WriteLine($"  Throw {P.HeldWeapon}! ({throwFeet:F0}ft) Roll {thrAtk} vs {throwTarget.Name}'s dodge {thrDdg}.");
                    string thrWeap = P.HeldWeapon!;
                    P.HeldWeapon = null;
                    GridPos thrLand;
                    if (thrAtk >= thrDdg && !EnemyBlocks(throwTarget, thrAtk, isRanged: true))
                    {
                        int thrDmg = Rng.Next(thrDmgMin, thrDmgMax + 1) + SlayerDmg();
                        thrDmg = ReduceByToughHide(throwTarget, thrDmg);
                        Console.WriteLine($"  HIT! {thrDmg} dmg → {throwTarget.Name} HP:{throwTarget.HP - thrDmg}/{throwTarget.MaxHP}");
                        throwTarget.HP -= thrDmg;
                        if (!throwTarget.Alive) HandleKill(throwTarget);
                        thrLand = RandomAdjacent(throwTarget.Position);
                    }
                    else
                    {
                        Console.WriteLine("  MISS!");
                        thrLand = RandomAdjacent(throwTarget.Position);
                    }
                    GroundWeapons.Add((thrLand, thrWeap));
                    Console.WriteLine($"  {thrWeap} lands at ({thrLand.X},{thrLand.Y}).");
                    justBlocked = false;
                    break;
                }

                case "throw dagger":
                {
                    var throwTarget = PickTarget(alive);
                    if (throwTarget == null) continue;
                    float thrDaggerFeet = PlayerPos.Feet(throwTarget.Position);
                    if (thrDaggerFeet > 20f)
                    {
                        Console.WriteLine("  Too far! Max 20ft for thrown dagger.");
                        continue;
                    }
                    int tdAtk = Rng.Next(P.MinRangedAtk, P.MaxRangedAtk + 1) + SlayerAtk();
                    int tdDdg = Rng.Next(throwTarget.MinDodge, throwTarget.MaxDodge + 1) - throwTarget.DodgePenalty;
                    Console.WriteLine($"  Throw dagger! ({thrDaggerFeet:F0}ft) Roll {tdAtk} vs {throwTarget.Name}'s dodge {tdDdg}. ({P.DaggerCount - 1} daggers left)");
                    P.DaggerCount--;
                    GridPos tdLand;
                    if (tdAtk >= tdDdg && !EnemyBlocks(throwTarget, tdAtk, isRanged: true))
                    {
                        int tdDmg = Rng.Next(1, 7) + SlayerDmg();
                        tdDmg = ReduceByToughHide(throwTarget, tdDmg);
                        Console.WriteLine($"  HIT! {tdDmg} dmg → {throwTarget.Name} HP:{throwTarget.HP - tdDmg}/{throwTarget.MaxHP}");
                        throwTarget.HP -= tdDmg;
                        if (!throwTarget.Alive) HandleKill(throwTarget);
                        tdLand = RandomAdjacent(throwTarget.Position);
                    }
                    else
                    {
                        Console.WriteLine("  MISS!");
                        tdLand = RandomAdjacent(throwTarget.Position);
                    }
                    GroundWeapons.Add((tdLand, "Goblin Dagger"));
                    Console.WriteLine($"  Dagger lands at ({tdLand.X},{tdLand.Y}).");
                    // Double Tap: second dagger throw
                    if (P.HasFeat("Double Tap") && P.DaggerCount > 0 && throwTarget.Alive)
                    {
                        Console.WriteLine("  [Double Tap] Second dagger throw!");
                        int tdAtk2 = Rng.Next(P.MinRangedAtk, P.MaxRangedAtk + 1) + SlayerAtk();
                        int tdDdg2 = Rng.Next(throwTarget.MinDodge, throwTarget.MaxDodge + 1) - throwTarget.DodgePenalty;
                        Console.WriteLine($"  Throw dagger! ({thrDaggerFeet:F0}ft) Roll {tdAtk2} vs dodge {tdDdg2}. ({P.DaggerCount - 1} daggers left)");
                        P.DaggerCount--;
                        GridPos tdLand2;
                        if (tdAtk2 >= tdDdg2 && !EnemyBlocks(throwTarget, tdAtk2, isRanged: true))
                        {
                            int tdDmg2 = Rng.Next(1, 7) + SlayerDmg();
                            tdDmg2 = ReduceByToughHide(throwTarget, tdDmg2);
                            Console.WriteLine($"  HIT! {tdDmg2} dmg → {throwTarget.Name} HP:{throwTarget.HP - tdDmg2}/{throwTarget.MaxHP}");
                            throwTarget.HP -= tdDmg2;
                            if (!throwTarget.Alive) HandleKill(throwTarget);
                            tdLand2 = RandomAdjacent(throwTarget.Position);
                        }
                        else
                        {
                            Console.WriteLine("  MISS!");
                            tdLand2 = RandomAdjacent(throwTarget.Position);
                        }
                        GroundWeapons.Add((tdLand2, "Goblin Dagger"));
                        Console.WriteLine($"  Dagger lands at ({tdLand2.X},{tdLand2.Y}).");
                    }
                    justBlocked = false;
                    break;
                }

                case "throw axe":
                {
                    var throwTarget = PickTarget(alive);
                    if (throwTarget == null) continue;
                    float thrAxeFeet = PlayerPos.Feet(throwTarget.Position);
                    if (thrAxeFeet > 20f)
                    {
                        Console.WriteLine("  Too far! Max 20ft for thrown axe.");
                        continue;
                    }
                    int taAtk = Rng.Next(P.MinRangedAtk, P.MaxRangedAtk + 2) + SlayerAtk();
                    int taDdg = Rng.Next(throwTarget.MinDodge, throwTarget.MaxDodge + 1) - throwTarget.DodgePenalty;
                    Console.WriteLine($"  Throw axe! ({thrAxeFeet:F0}ft) Roll {taAtk} vs {throwTarget.Name}'s dodge {taDdg}. ({P.AxeCount - 1} axes left)");
                    P.AxeCount--;
                    GridPos taLand;
                    if (taAtk >= taDdg && !EnemyBlocks(throwTarget, taAtk, isRanged: true))
                    {
                        int taDmg = Rng.Next(2, 9) + SlayerDmg();
                        taDmg = ReduceByToughHide(throwTarget, taDmg);
                        Console.WriteLine($"  HIT! {taDmg} dmg → {throwTarget.Name} HP:{throwTarget.HP - taDmg}/{throwTarget.MaxHP}");
                        throwTarget.HP -= taDmg;
                        if (!throwTarget.Alive) HandleKill(throwTarget);
                        taLand = RandomAdjacent(throwTarget.Position);
                    }
                    else
                    {
                        Console.WriteLine("  MISS!");
                        taLand = RandomAdjacent(throwTarget.Position);
                    }
                    GroundWeapons.Add((taLand, "Hand Axe"));
                    Console.WriteLine($"  Axe lands at ({taLand.X},{taLand.Y}).");
                    // Double Tap: second throw
                    if (P.HasFeat("Double Tap") && P.AxeCount > 0 && throwTarget.Alive)
                    {
                        Console.WriteLine("  [Double Tap] Second axe throw!");
                        int taAtk2 = Rng.Next(1, 9) + SlayerAtk();
                        int taDdg2 = Rng.Next(throwTarget.MinDodge, throwTarget.MaxDodge + 1) - throwTarget.DodgePenalty;
                        Console.WriteLine($"  Throw axe! ({thrAxeFeet:F0}ft) Roll {taAtk2} vs dodge {taDdg2}. ({P.AxeCount - 1} axes left)");
                        P.AxeCount--;
                        GridPos taLand2;
                        if (taAtk2 >= taDdg2 && !EnemyBlocks(throwTarget, taAtk2, isRanged: true))
                        {
                            int taDmg2 = Rng.Next(2, 9) + SlayerDmg();
                            taDmg2 = ReduceByToughHide(throwTarget, taDmg2);
                            Console.WriteLine($"  HIT! {taDmg2} dmg → {throwTarget.Name} HP:{throwTarget.HP - taDmg2}/{throwTarget.MaxHP}");
                            throwTarget.HP -= taDmg2;
                            if (!throwTarget.Alive) HandleKill(throwTarget);
                            taLand2 = RandomAdjacent(throwTarget.Position);
                        }
                        else
                        {
                            Console.WriteLine("  MISS!");
                            taLand2 = RandomAdjacent(throwTarget.Position);
                        }
                        GroundWeapons.Add((taLand2, "Hand Axe"));
                        Console.WriteLine($"  Axe lands at ({taLand2.X},{taLand2.Y}).");
                    }
                    justBlocked = false;
                    break;
                }

                case "club sweep":
                {
                    Console.Write("  Club sweep direction [N/S/E/W]: ");
                    string swDir = (Console.ReadLine() ?? "").Trim().ToLower();
                    int swdx = swDir.StartsWith("e") ? 1 : swDir.StartsWith("w") ? -1 : 0;
                    int swdy = swDir.StartsWith("s") ? 1 : swDir.StartsWith("n") ? -1 : 0;
                    if (swdx == 0 && swdy == 0) { Console.WriteLine("  Invalid direction (N/S/E/W)."); continue; }
                    var swSquares = new[] { new GridPos(PlayerPos.X + swdx, PlayerPos.Y + swdy),
                                           new GridPos(PlayerPos.X + swdx * 2, PlayerPos.Y + swdy * 2) };
                    Console.WriteLine($"  You sweep the Ogre Club in a wide arc!");
                    foreach (var sq in swSquares)
                    {
                        if (sq.X < 0 || sq.X > 49 || sq.Y < 0 || sq.Y > 49) continue;
                        foreach (var swE in alive.Where(e => e.Alive && e.Position.SameAs(sq)).ToList())
                        {
                            int swDmg = Rng.Next(3, 13);
                            swDmg = ReduceByToughHide(swE, swDmg);
                            Console.WriteLine($"  Club hits {swE.Name} for {swDmg} dmg! HP:{swE.HP - swDmg}/{swE.MaxHP}");
                            swE.HP -= swDmg;
                            if (!swE.Alive) ResolveDowned(swE, true);
                        }
                    }
                    justBlocked = false;
                    break;
                }

                case "pick up weapon":
                {
                    var nearby = GroundWeapons.Where(w => PlayerPos.ManhattanDist(w.Pos) <= 1).ToList();
                    if (!nearby.Any()) { Console.WriteLine("  No weapon within reach."); continue; }
                    for (int wi = 0; wi < nearby.Count; wi++)
                        Console.WriteLine($"  [{wi + 1}] {nearby[wi].Type} at ({nearby[wi].Pos.X},{nearby[wi].Pos.Y})");
                    Console.Write("  Pick up #: ");
                    if (!int.TryParse(Console.ReadLine()?.Trim(), out int wpick) || wpick < 1 || wpick > nearby.Count)
                    { Console.WriteLine("  Invalid."); continue; }
                    var picked = nearby[wpick - 1];
                    if (picked.Type == "Ogre Club" && !P.HasFeat("Giant's Strength"))
                    {
                        Console.WriteLine("  The Ogre Club is far too heavy to wield! (Requires Giant's Strength feat)");
                        continue;
                    }
                    if (P.HeldWeapon != null)
                    {
                        GroundWeapons.Add((PlayerPos, P.HeldWeapon));
                        Console.WriteLine($"  You drop your {P.HeldWeapon} to pick up the {picked.Type}.");
                    }
                    GroundWeapons.Remove(picked);
                    P.HeldWeapon = picked.Type;
                    Console.WriteLine($"  You pick up the {picked.Type}!");
                    justBlocked = false;
                    break;
                }

                case "switch weapon":
                {
                    if (P.SecondaryWeapon == null) { Console.WriteLine("  No secondary weapon."); continue; }
                    string? tmp = P.HeldWeapon;
                    P.HeldWeapon = P.SecondaryWeapon;
                    P.SecondaryWeapon = tmp;
                    Console.WriteLine($"  You switch to {P.HeldWeapon ?? "unarmed"}" +
                        (P.SecondaryWeapon != null ? $" (stowed: {P.SecondaryWeapon})" : "") + ".");
                    justBlocked = false;
                    break;
                }

                case "duelist action":
                {
                    if (P.DuelistPoints <= 0) { Console.WriteLine("  No Duelist Points remaining."); continue; }
                    // Build list of available specials
                    var specials = new List<(string name, string desc, int reqLevel)>
                    {
                        ("Duelist Defence", "Auto-attack enemies entering/leaving melee range until next turn", 2),
                        ("Duelist Flurry",  "Three attacks this action (stacks with combos)", 5),
                        ("Duelist Fencing", "Auto-counter enemies who attack you until next turn", 8),
                        ("Duelist Fineness","Auto-disarm attackers (roll atk vs atk) until next turn", 11),
                        ("Duelist Heart",   "Pick 2 other specials for free until next turn", 14),
                        ("Duelist Stamina", "Pick 1 special lasting 2 turns (no Heart, until L20)", 17),
                        ("Duelist Game",    "Trip enemies entering/leaving range or attacking until next turn", 20),
                    };
                    var available = specials.Where(s => P.Level >= s.reqLevel).ToList();
                    if (!available.Any()) { Console.WriteLine("  No specials available yet (need L2)."); continue; }
                    Console.WriteLine($"  Duelist Points: {P.DuelistPoints}  Active effects: {(P.DuelistEffectTurns.Any() ? string.Join(", ", P.DuelistEffectTurns.Select(kv => $"{kv.Key}({kv.Value}t)")) : "none")}");
                    for (int si = 0; si < available.Count; si++)
                        Console.WriteLine($"  [{si+1}] {available[si].name} — {available[si].desc}");
                    Console.Write("  Choose (0=cancel): ");
                    if (!int.TryParse(Console.ReadLine()?.Trim(), out int sc2) || sc2 < 1 || sc2 > available.Count)
                    { Console.WriteLine("  Cancelled."); continue; }
                    var chosen2 = available[sc2 - 1];

                    // Handle Heart (pick 2 others)
                    if (chosen2.name == "Duelist Heart")
                    {
                        P.DuelistPoints--;
                        var others = available.Where(s => s.name != "Duelist Heart" && s.name != "Duelist Stamina").ToList();
                        Console.WriteLine($"  [Duelist Heart] Pick 2 specials (free):");
                        for (int si = 0; si < others.Count; si++) Console.WriteLine($"  [{si+1}] {others[si].name}");
                        for (int pick = 0; pick < 2; pick++)
                        {
                            Console.Write($"  Pick {pick+1}/2: ");
                            if (int.TryParse(Console.ReadLine()?.Trim(), out int hp) && hp >= 1 && hp <= others.Count)
                            {
                                string hName = others[hp - 1].name;
                                P.DuelistEffectTurns[hName] = Math.Max(P.DuelistEffectTurns.GetValueOrDefault(hName, 0), 1);
                                Console.WriteLine($"  {hName} active for 1 turn.");
                            }
                        }
                    }
                    // Handle Stamina (pick 1 other, lasts 2 turns)
                    else if (chosen2.name == "Duelist Stamina")
                    {
                        P.DuelistPoints--;
                        bool heartAllowed = P.Level >= 20;
                        var stamOthers = available.Where(s => s.name != "Duelist Stamina" && (heartAllowed || s.name != "Duelist Heart")).ToList();
                        Console.WriteLine($"  [Duelist Stamina] Pick 1 special (lasts 2 turns):");
                        for (int si = 0; si < stamOthers.Count; si++) Console.WriteLine($"  [{si+1}] {stamOthers[si].name}");
                        Console.Write("  Pick: ");
                        if (int.TryParse(Console.ReadLine()?.Trim(), out int sp) && sp >= 1 && sp <= stamOthers.Count)
                        {
                            string sName = stamOthers[sp - 1].name;
                            P.DuelistEffectTurns[sName] = Math.Max(P.DuelistEffectTurns.GetValueOrDefault(sName, 0), 2);
                            Console.WriteLine($"  {sName} active for 2 turns.");
                        }
                    }
                    else
                    {
                        P.DuelistPoints--;
                        P.DuelistEffectTurns[chosen2.name] = Math.Max(P.DuelistEffectTurns.GetValueOrDefault(chosen2.name, 0), 1);
                        Console.WriteLine($"  {chosen2.name} active until next turn.");
                        // Flurry is consumed immediately on next attack — no extra action needed
                    }
                    justBlocked = false;
                    break;
                }

                default:
                    Console.WriteLine($"  Unknown action '{chosen}'. Try again.");
                    continue;
            }

            actLeft--;
            if (sprintPenaltyPending)
            {
                if (chosen != "sprint" && chosen != "move") { P.SprintPenalty = 2; sprintPenaltyPending = false; }
                else if (!justSprinted) sprintPenaltyPending = false;
            }
            if (justSprinted) sprintPenaltyPending = true;
        }

        // Warrior bonus attack/grapple actions (every 4 levels from L2)
        if (P.CharacterType == "Warrior" && !fled && P.HP > 0)
        {
            int wBonus = P.Level >= 2 ? (P.Level - 2) / 4 + 1 : 0;
            var wAlive = Active.Where(e => e.Alive).ToList();
            for (int wb = 0; wb < wBonus && wAlive.Any() && P.HP > 0; wb++)
            {
                Console.Write($"\n  [Warrior Bonus {wb + 1}/{wBonus}] [A]ttack  [G]rapple  [skip]: ");
                string wc = (Console.ReadLine() ?? "").Trim().ToLower();
                wAlive = Active.Where(e => e.Alive).ToList();
                if (!wAlive.Any()) break;
                if (wc.StartsWith("a"))
                {
                    var wt = PickTarget(wAlive);
                    if (wt != null) DoAttack(wt);
                }
                else if (wc.StartsWith("g"))
                {
                    var wt = PickTarget(wAlive);
                    if (wt != null) DoGrapple(wt);
                }
            }
        }

        // Martial Artist bonus melee/throw actions (every 3 levels from L2)
        if (P.CharacterType == "Martial Artist" && !fled && P.HP > 0)
        {
            int maBonus = P.Level >= 2 ? (P.Level - 2) / 3 + 1 : 0;
            var maAlive = Active.Where(e => e.Alive).ToList();
            for (int mb = 0; mb < maBonus && maAlive.Any() && P.HP > 0; mb++)
            {
                Console.Write($"\n  [Martial Artist Bonus {mb + 1}/{maBonus}] [M]elee  [T]hrow  [skip]: ");
                string mc = (Console.ReadLine() ?? "").Trim().ToLower();
                maAlive = Active.Where(e => e.Alive).ToList();
                if (!maAlive.Any()) break;
                if (mc.StartsWith("m"))
                {
                    var mt = PickTarget(maAlive);
                    if (mt != null) DoAttack(mt);
                }
                else if (mc.StartsWith("t"))
                {
                    var mt = PickTarget(maAlive);
                    if (mt == null) continue;
                    int gst = GrappleStyleTier();
                    int gRoll = Rng.Next(P.MinGrapple + P.GetFeatStacks("Closeliner") + (gst >= 2 ? 1 : 0),
                                         P.MaxGrapple + (gst >= 3 ? 2 : 0) + 1);
                    int dRoll = Rng.Next(mt.MinDodge, mt.MaxDodge + 1);
                    Console.WriteLine($"  Throw! Roll {gRoll} vs {mt.Name}'s dodge {dRoll}.");
                    if (gRoll >= dRoll)
                    {
                        mt.KnockedDown = true; mt.OffBalance = true;
                        int throwDmg = Rng.Next(P.MinGrappleDmg + P.GetFeatStacks("Closeliner"), P.MaxGrappleDmg + 1);
                        throwDmg = ReduceByToughHide(mt, throwDmg);
                        mt.HP -= throwDmg;
                        Console.WriteLine($"  {mt.Name} is slammed to the ground for {throwDmg}! HP:{mt.HP}/{mt.MaxHP}");
                        if (!mt.Alive) HandleKill(mt);
                    }
                    else Console.WriteLine("  Throw failed!");
                }
            }
        }

        // End of player turn: Opportunist checks
        if (P.HasFeat("Opportunist"))
        {
            var debuffed = Active.Where(e => e.Alive && (e.OffBalance || e.KnockedDown || e.KnockedOut || e.Disarmed)).ToList();
            if (debuffed.Any()) OpportunistAttacks(debuffed);
        }

        P.SprintPenalty = 0;
        return fled;
    }

    List<string> BuildOpts(bool justBlocked, List<Enemy> alive, Enemy? blockTarget = null)
    {
        var o = new List<string>();
        if (!P.IsGrappled && P.HeldWeapon != "Ogre Club") o.Add("attack");
        if (!P.IsGrappled) o.Add("grapple");
        if (!P.IsGrappled) o.Add("sprint");
        if (!P.IsGrappled && P.HasFeat("Charge")) o.Add("charge");
        o.AddRange(new[] { "move", "defend", "healing potion", "run away", "exit" });
        if (P.IsGrappled) o.Add("break grapple");
        if (P.HasFeat("Block")) o.Add("block");
        if (P.HasFeat("Parry") && justBlocked && !(blockTarget is Ogre)) o.Add("parry");
        if (P.HasFeat("Bard Song") && SilenceTurns <= 0) o.Add("bard song");
        if (P.KnownSpells.Any() && SilenceTurns <= 0) o.Add("cast spell");
        if (P.CanPray && SilenceTurns <= 0) o.Add("pray");
        bool deadGoblin = Active.Any(e => !e.Alive && e is Goblin);
        if (P.HasFeat("Double Tap") && deadGoblin && !P.HasGoblinSword) o.Add("pick up goblin sword");
        if (P.HeldWeapon is "Goblin Dagger" or "Troll Axe") o.Add("throw weapon");
        if (P.DaggerCount > 0 && !P.IsGrappled) o.Add("throw dagger");
        if (P.AxeCount > 0 && !P.IsGrappled) o.Add("throw axe");
        if (P.HeldWeapon == "Ogre Club" && P.HasFeat("Giant's Strength")) o.Add("club sweep");
        int dMaxPts = P.Level < 2 ? 0 : (P.Level <= 20 ? (P.Level-2)/3+1 : 7 + 2*((P.Level-20)/3));
        if (P.CharacterType == "Duelist" && P.DuelistPoints > 0 && dMaxPts > 0) o.Add("duelist action");
        if (P.SecondaryWeapon != null) o.Add("switch weapon");
        if (GroundWeapons.Any(w => PlayerPos.ManhattanDist(w.Pos) <= 1)) o.Add("pick up weapon");
        if (P.CharacterType == "Berserker") o.Add("whirlwind");
        if (P.CharacterType == "Berserker" && P.RagePoints > 0 && !P.IsRaging) o.Add("rage");
        if (P.CanSing && P.SongTokens > 0 && !P.SongPlaying && SilenceTurns <= 0) o.Add("play song");
        if (P.SongPlaying) o.Add("stop song");
        return o;
    }

    // ── MUSICIAN SONGS ────────────────────────────────────────────────────

    // Silence Song: nobody (players or enemies) may use spells/prayers/songs
    int SilenceTurns = 0;

    // Slayer song boosts melee, ranged, thrown, spell and grapple rolls for
    // the whole party — any member's active song empowers the current actor.
    int SlayerAtk() => AllPlayers.Where(pl => pl.SongActive("Slayer")).Sum(pl => pl.SongBonusAmount());
    int SlayerDmg() => AllPlayers.Where(pl => pl.SongActive("Slayer")).Sum(pl => pl.SlayerDmgBonus());

    // When a Troll Musician dies, flees or is knocked out, its war rhythm dies with it
    // (a woken drummer may spend another song use to start it up again)
    void SweepWarRhythm()
    {
        foreach (var tm in Active.OfType<TrollMusician>().Where(t => t.WarSongActive && (!t.Alive || t.KnockedOut)).ToList())
        {
            tm.WarSongActive = false;
            foreach (var al in tm.WarSongTargets)
            { al.MinAttack -= 1; al.MinDodge -= 1; }
            tm.WarSongTargets.Clear();
            Console.WriteLine($"  The war rhythm dies with {tm.Name} — the horde falters!");
        }
    }

    void DeathTonePulse()
    {
        int dice = P.FearDiceCount();
        int roll = 0;
        for (int d = 0; d < dice; d++) roll += Rng.Next(1, 7);
        Console.WriteLine($"  ♪ DEATHTONE! Dread chord: {roll} ({dice}d6). Enemies with {roll} HP or less flee!");
        foreach (var fe in Active.Where(e => e.Alive && !e.IsPlayerAlly && e.HP <= roll).ToList())
        {
            fe.Fled = true;
            Console.WriteLine($"  {fe.Name} ({fe.HP} HP) is gripped by mortal dread and flees!");
        }
    }

    Enemy? PickTarget(List<Enemy> alive)
    {
        if (alive.Count == 1) return alive[0];
        for (int i = 0; i < alive.Count; i++) Console.Write($"[{i + 1}]{alive[i].Name}  ");
        Console.WriteLine();
        Console.Write("  Target #: ");
        if (int.TryParse(Console.ReadLine()?.Trim(), out int ti) && ti >= 1 && ti <= alive.Count)
            return alive[ti - 1];
        Console.WriteLine("  Invalid target.");
        return null;
    }

    // ── ATTACK ────────────────────────────────────────────────────────────

    void DoAttack(Enemy target)
    {
        if (P.HeldWeapon == "Bow") { DoBowAttack(target); return; }
        if (P.HeldWeapon == "Wand") { DoWandAttack(target); return; }
        if (P.HeldWeapon == "Mace") { DoMaceAttack(target); return; }
        // Modifiers
        bool usePower = false, useSunder = false, useDisarm = false, useSap = false;
        var mods = new List<string>();
        if (P.HasFeat("Power Attack")) mods.Add("[P]ower(-2atk/+4dmg)");
        if (P.HasFeat("Sunder")) mods.Add("[S]under(-1atk/bleed)");
        if (P.HasFeat("Disarm")) mods.Add("[D]isarm");
        if (P.HasFeat("Sap")) mods.Add("[A]p(-2atk/effects)");
        if (mods.Any())
        {
            Console.Write($"  Modifier? {string.Join("  ", mods)}  [N]one: ");
            string m = (Console.ReadLine() ?? "n").Trim().ToLower();
            usePower = m.StartsWith("p") && P.HasFeat("Power Attack");
            useSunder = m.StartsWith("s") && P.HasFeat("Sunder");
            useDisarm = m.StartsWith("d") && P.HasFeat("Disarm");
            useSap = (m.StartsWith("a")) && P.HasFeat("Sap");
        }

        int atkPen = -P.SprintPenalty, dmgBonus = 0;
        P.SprintPenalty = 0;
        if (usePower) { atkPen -= 2; dmgBonus += Rng.Next(P.MinPowerAtk, P.MaxPowerAtk + 1); }
        if (useSunder) atkPen--;
        if (useSap) atkPen -= 2;

        int minAtk, maxAtk, minDmg, maxDmg;
        if (P.HeldWeapon != null && P.HeldWeapon != "Ogre Club")
        {
            var (wa, xA, wd, xD) = WeaponPickupStats(P.HeldWeapon);
            minAtk = wa + (P.HasFeat("Talented") ? P.GetFeatStacks("Talented") : 0);
            maxAtk = xA;
            minDmg = wd + (P.HasFeat("Built") ? P.GetFeatStacks("Built") : 0);
            maxDmg = xD;
        }
        else
        {
            minAtk = P.MinAttack + (P.HasFeat("Talented") ? P.GetFeatStacks("Talented") : 0);
            maxAtk = P.MaxAttack;
            minDmg = P.MinDamage + (P.HasFeat("Built") ? P.GetFeatStacks("Built") : 0);
            maxDmg = P.MaxDamage;
        }
        if (P.HasFeat("MMA")) { minDmg *= 2; maxDmg *= 2; }
        if (P.HasFeat("Giant's Strength") && P.HeldWeapon != "Ogre Club") { minDmg += 2; maxDmg += 1; }
        if (P.EnlargeActive) { minAtk *= 2; maxAtk *= 2; minDmg *= 2; maxDmg *= 2; }
        // Martial Artist unarmed: 1d6 base + 2d4 per set, +1 set every 3 levels from L2
        if (P.CharacterType == "Martial Artist" && P.HeldWeapon == null && P.Level >= 2)
        {
            int numSets = (P.Level - 2) / 3 + 1;
            int maBonusDmg = 0;
            for (int d = 0; d < numSets * 2; d++) maBonusDmg += Rng.Next(1, 5);
            dmgBonus += maBonusDmg;
            Console.WriteLine($"  Martial Artist: +{maBonusDmg} ({numSets * 2}d4) unarmed bonus!");
        }
        // Martial Artist + Staff: add half unarmed damage as bonus
        if (P.CharacterType == "Martial Artist" && P.HeldWeapon == "Staff")
        {
            int unarmedRoll = Rng.Next(P.MinDamage, P.MaxDamage + 1);
            if (P.Level >= 2)
            {
                int numSetsS = (P.Level - 2) / 3 + 1;
                for (int d = 0; d < numSetsS * 2; d++) unarmedRoll += Rng.Next(1, 5);
            }
            int staffBonus = Math.Max(1, unarmedRoll / 2);
            dmgBonus += staffBonus;
            Console.WriteLine($"  Martial Artist staff: +{staffBonus} (half unarmed) bonus!");
        }

        // Berserker rage: +2d4 per rage point spent
        if (P.IsRaging && P.RagePointsSpent > 0)
        {
            int rageDmg = 0;
            for (int d = 0; d < P.RagePointsSpent * 2; d++) rageDmg += Rng.Next(1, 5);
            dmgBonus += rageDmg;
            Console.WriteLine($"  RAGE: +{rageDmg} ({P.RagePointsSpent*2}d4) bonus damage!");
        }
        int brokenArmPenalty = P.BrokenLimbs.Count(l => l.Contains("Arm"));
        int warriorAtkBonus  = P.CharacterType == "Warrior"  && P.Level >= 2 ? (P.Level - 2) / 3 + 1 : 0;
        int trueSightAtkBonus = (P.TrueSightTurns > 0 && P.TrueSightStat == "attack") ? P.TrueSightBonus : 0;
        int songAtkBonus = SlayerAtk();
        if (songAtkBonus > 0)
        {
            int songDmg = SlayerDmg();
            dmgBonus += songDmg;
            Console.WriteLine($"  ♪ Slayer song: +{songAtkBonus} attack, +{songDmg} damage!");
        }

        // Duelist Flurry: 3 attacks this action
        int flurryCount = (P.CharacterType == "Duelist" && P.DuelistEffectTurns.GetValueOrDefault("Duelist Flurry") > 0) ? 3 : 1;
        for (int fi = 0; fi < flurryCount && target.Alive; fi++)
        {
            if (fi > 0) Console.WriteLine($"  [Flurry hit {fi + 1}]");
            int rawRoll = Rng.Next(minAtk, maxAtk + 1);
            PerformAttack(target, rawRoll + atkPen + warriorAtkBonus - brokenArmPenalty + trueSightAtkBonus + songAtkBonus, minDmg, maxDmg, fi == 0 ? dmgBonus : 0, useSunder, useDisarm, fi == 0 && useSap, rawRoll == maxAtk, rawRoll == minAtk);
        }

        // Off-hand (Double Tap)
        if (P.HasFeat("Double Tap") && target.Alive)
        {
            int ofAtk = Rng.Next(1, 7);
            int ofDdg = Rng.Next(target.MinDodge, target.MaxDodge + 1) - target.DodgePenalty;
            Console.WriteLine($"  Off-hand: roll {ofAtk} vs dodge {ofDdg}.");
            if (ofAtk >= ofDdg && !EnemyBlocks(target, ofAtk))
            {
                int ofDmg = Rng.Next(1, P.OffhandMaxDamage + 1);
                TryEnemyArmBlock(target, ofAtk, ref ofDmg);
                ofDmg = ReduceByToughHide(target, ofDmg);
                Console.WriteLine($"  Off-hand HIT! {ofDmg} dmg → {target.Name} HP:{target.HP - ofDmg}/{target.MaxHP}");
                target.HP -= ofDmg;
                if (!target.Alive) HandleKill(target);
            }
            else if (ofAtk < ofDdg) Console.WriteLine("  Off-hand MISS!");
        }

        // Kick (Basic Combo)
        if (P.HasFeat("Basic Combo") && target.Alive) DoKick(target);

        // Fury of Blows: extra kick + headbutt
        if (P.HasFeat("Fury of Blows") && target.Alive)
        {
            DoKick(target);
            if (target.Alive)
            {
                int hbA = Rng.Next(1, 7), hbD = Rng.Next(target.MinDodge, target.MaxDodge + 1) - target.DodgePenalty;
                Console.WriteLine($"  Headbutt: {hbA} vs {hbD}.");
                if (hbA >= hbD && !EnemyBlocks(target, hbA))
                {
                    int d = Rng.Next(1, 5) + P.RingletBonus;
                    TryEnemyArmBlock(target, hbA, ref d);
                    d = ReduceByToughHide(target, d);
                    target.HP -= d;
                    Console.WriteLine($"  Headbutt HIT! {d} dmg.");
                    if (!target.Alive) HandleKill(target);
                }
                else if (hbA < hbD) Console.WriteLine("  Headbutt MISS!");
            }
        }

        // MMA: 2 extra main attacks
        if (P.HasFeat("MMA"))
            for (int i = 0; i < 2 && target.Alive; i++)
            {
                Console.WriteLine($"  MMA bonus attack {i + 1}:");
                int mmaRaw = Rng.Next(minAtk, maxAtk + 1);
                PerformAttack(target, mmaRaw, minDmg, maxDmg, 0, false, false, false, mmaRaw == maxAtk, mmaRaw == minAtk);
            }
    }

    void PerformAttack(Enemy target, int atkRoll, int minDmg, int maxDmg, int dmgBonus, bool sunder, bool disarm, bool sap, bool isCrit = false, bool isFumble = false)
    {
        if (isFumble)
        {
            Console.WriteLine("  FUMBLE! You drop your weapon!");
            if (P.HeldWeapon != null)
            {
                var dropPos = RandomAdjacent(PlayerPos);
                GroundWeapons.Add((dropPos, P.HeldWeapon));
                P.HeldWeapon = null;
            }
            return;
        }

        int ddg = Rng.Next(target.MinDodge, target.MaxDodge + 1) - target.DodgePenalty - target.FrostPenalty;
        Console.WriteLine($"  Attack{(isCrit ? " [CRIT]" : "")}: {atkRoll} vs {target.Name}'s dodge {ddg}.");

        if (atkRoll < ddg)
        {
            Console.WriteLine("  MISS!");
            if (P.HasFeat("Judo")) JudoPrompt(target);
            return;
        }

        if (EnemyBlocks(target, atkRoll)) return;

        if (disarm)
        {
            bool canTargetShield = target.HasShield && !target.ShieldLost && !target.Disarmed;
            if (canTargetShield)
            {
                Console.Write($"  Disarm {target.Name}'s [W]eapon (longsword) or [S]hield? ");
                string dc = (Console.ReadLine() ?? "w").Trim().ToLower();
                if (dc.StartsWith("s"))
                {
                    target.ShieldLost = true;
                    Console.WriteLine($"  {target.Name}'s shield is knocked away! They can no longer block.");
                    if (P.HasFeat("Opportunist")) OpportunistPromptNote();
                    return;
                }
            }
            var disarmPos = RandomAdjacent(target.Position);
            target.Disarmed = true; target.WeaponPos = disarmPos;
            string disWpType = EnemyWeaponType(target);
            if (disWpType.Length > 0) GroundWeapons.Add((disarmPos, disWpType));
            Console.WriteLine($"  Disarm! {target.Name}'s weapon lands at ({disarmPos.X},{disarmPos.Y}).");
            if (P.HasFeat("Opportunist")) OpportunistPromptNote();
            return;
        }

        if (sap)
        {
            int sd = Rng.Next(1, 5);
            int cd = Rng.Next(1, 7);
            Console.WriteLine($"  Sap HIT! {sd} dmg, chance die: {cd}");
            target.HP -= sd;
            switch (cd)
            {
                case 1: Console.WriteLine("  Enemy shrugs off the worst (-2 dmg taken)."); break;
                case 3: case 4:
                    target.OffBalance = true; target.DodgePenalty += 2; target.AttackPenalty += 2;
                    Console.WriteLine($"  {target.Name} off-balance (-2 dodge/-2 atk)!"); break;
                case 5:
                    KnockOut(target); break;
                case 6:
                    KnockOut(target); target.BleedDmg++;
                    Console.WriteLine($"  {target.Name} is also BLEEDING!"); break;
            }
            if (!target.Alive) ResolveDowned(target, IsNonLethalAttack());
            return;
        }

        int dmg = Rng.Next(minDmg, maxDmg + 1);
        if (isCrit) { dmg *= 2; Console.WriteLine("  CRITICAL HIT! ×2 damage!"); }
        dmg += dmgBonus;
        TryEnemyArmBlock(target, atkRoll, ref dmg);
        dmg = ReduceByToughHide(target, dmg);
        Console.WriteLine($"  HIT! {dmg} dmg → {target.Name} HP:{target.HP - dmg}/{target.MaxHP}");
        target.HP -= dmg;
        LichTouchHeal(dmg);

        if (sunder && target.Alive)
        {
            int sd = Rng.Next(1, 7);
            if (sd >= 6) { target.BleedDmg += 2; Console.WriteLine($"  DOUBLE BLEED! ({target.BleedDmg}/turn)"); }
            else if (sd >= 4) { target.BleedDmg++; Console.WriteLine($"  BLEED! ({target.BleedDmg}/turn)"); }
            else Console.WriteLine("  No bleed.");
        }

        if (!target.Alive) { ResolveDowned(target, IsNonLethalAttack()); return; }

        // Slayer
        if (P.HasFeat("Slayer") && target.HP <= target.MaxHP / 3)
        {
            Console.Write($"  Slayer! {target.Name} is low HP. Free attack? (y/n): ");
            if ((Console.ReadLine() ?? "").Trim().ToLower() == "y") FreeAttack(target);
        }
    }

    void DoKick(Enemy target)
    {
        int kA = Rng.Next(1, 5), kD = Rng.Next(target.MinDodge, target.MaxDodge + 1) - target.DodgePenalty;
        Console.WriteLine($"  Kick: {kA} vs {kD}.");
        if (kA >= kD && !EnemyBlocks(target, kA))
        {
            int d = Rng.Next(1, 5) + P.RingletBonus;
            TryEnemyArmBlock(target, kA, ref d);
            d = ReduceByToughHide(target, d);
            target.HP -= d;
            Console.WriteLine($"  Kick HIT! {d} dmg → {target.Name} HP:{target.HP}/{target.MaxHP}");
            if (!target.Alive) HandleKill(target);
        }
        else if (kA < kD) Console.WriteLine("  Kick MISS!");
    }

    void FreeAttack(Enemy target)
    {
        if (!target.Alive) return;
        int a = Rng.Next(P.MinAttack, P.MaxAttack + 1);
        int d = Rng.Next(target.MinDodge, target.MaxDodge + 1) - target.DodgePenalty;
        Console.WriteLine($"  [Free attack] {a} vs {d}.");
        if (a >= d && !EnemyBlocks(target, a))
        {
            int dmg = Rng.Next(P.MinDamage, P.MaxDamage + 1) + (P.HasFeat("Built") ? P.GetFeatStacks("Built") : 0);
            TryEnemyArmBlock(target, a, ref dmg);
            dmg = ReduceByToughHide(target, dmg);
            target.HP -= dmg;
            Console.WriteLine($"  HIT! {dmg} dmg → {target.Name} HP:{target.HP}/{target.MaxHP}");
            if (!target.Alive) HandleKill(target);
        }
        else if (a < d) Console.WriteLine("  Miss!");
    }

    void FreeAttackPrompt(string feat, Enemy target)
    {
        if (!P.HasFeat(feat) || !target.Alive) return;
        Console.Write($"  {feat}! Free attack on {target.Name}? (y/n): ");
        if ((Console.ReadLine() ?? "").Trim().ToLower() == "y") FreeAttack(target);
    }

    int ReduceByToughHide(Enemy e, int dmg)
    {
        if (e.ToughHideMin <= 0) return dmg;
        int reduction = Rng.Next(e.ToughHideMin, e.ToughHideMax + 1);
        int result = Math.Max(1, dmg - reduction);
        Console.WriteLine($"  {e.Name}'s tough hide absorbs {reduction} damage! ({dmg}→{result})");
        return result;
    }

    bool TryEnemyArmBlock(Enemy target, int atkRoll, ref int dmg)
    {
        if (!target.HasArmBlock) return false;
        if (target.KnockedDown || target.KnockedOut || target.OffBalance) return false;
        int bRoll = Rng.Next(target.BlockMin, target.BlockMax + 1);
        Console.WriteLine($"  {target.Name} raises their arm! Block roll {bRoll} vs attack {atkRoll}.");
        if (bRoll >= atkRoll)
        {
            dmg = Math.Max(1, dmg / 2);
            Console.WriteLine($"  ARM BLOCK! Damage halved to {dmg}.");
            return true;
        }
        Console.WriteLine($"  Arm block failed! ({bRoll} < {atkRoll})");
        return false;
    }

    // Returns true if the enemy successfully blocks or parries the incoming attack.
    // isRanged=true: requires a shield; shields also add ShieldBlockBonus to the roll.
    bool EnemyBlocks(Enemy target, int atkRoll, bool isRanged = false)
    {
        if (!target.HasBlock && !target.HasParry) return false;
        if (isRanged && (!target.HasShield || target.ShieldLost)) return false;
        if (!isRanged && target.HasShield && target.ShieldLost) return false;
        if (target.KnockedDown || target.KnockedOut || target.OffBalance) return false;
        int bBonus = (target.HasShield && !target.ShieldLost) ? target.ShieldBlockBonus : 0;
        int bRoll = Rng.Next(target.BlockMin, target.BlockMax + 1) + bBonus;
        string verb = target.HasParry ? "parries" : "blocks";
        Console.WriteLine($"  {target.Name} {verb}! Roll {bRoll} vs your attack {atkRoll}.");
        if (bRoll >= atkRoll) { Console.WriteLine($"  {(target.HasParry ? "PARRIED" : "BLOCKED")}!"); return true; }
        Console.WriteLine($"  {(target.HasParry ? "Parry" : "Block")} failed! ({bRoll} < {atkRoll})");
        return false;
    }

    void KnockOut(Enemy e)
    {
        e.KOCount++;
        // 1st KO: 6-8t, 2nd: 8-12t, 3rd: 10-16t, Nth: (4+2N)-(4+4N)t
        int minT = 4 + 2 * e.KOCount;
        int maxT = 4 + 4 * e.KOCount;
        e.KOTurns = Rng.Next(minT, maxT + 1);
        e.KnockedOut = true;
        if (e.HP <= 0) e.HP = 1; // non-lethal: stays at 1 HP
        Console.WriteLine($"  {e.Name} KNOCKED OUT for {e.KOTurns} turns! (KO #{e.KOCount}: {minT}-{maxT}t range)");
        if (!e.XpAwarded) { e.XpAwarded = true; GainXP(e.XPValue); }
    }

    // Non-lethal weapons: unarmed, staff, club, mace, warhammer. These KO living
    // enemies, but deal regular (lethal) damage to undead.
    bool IsNonLethalAttack() =>
        P.HeldWeapon is null or "Staff" or "Ogre Club" or "Mace" or "War Mace" or "Warhammer";

    // Resolve a downed enemy: KO if the hit was non-lethal and the target is living,
    // otherwise a regular kill (undead are always killed by non-lethal damage).
    void ResolveDowned(Enemy target, bool nonLethal)
    {
        if (target.Alive) return;
        if (nonLethal && !target.IsUndead) KnockOut(target);
        else HandleKill(target);
    }

    void HandleKill(Enemy e)
    {
        Console.WriteLine($"  {e.Name} is defeated!");
        if (!e.XpAwarded) { e.XpAwarded = true; GainXP(e.XPValue); }
        SweepWarRhythm();
        if (P.IsGrappled && P.GrappledBy == e) { P.IsGrappled = false; P.GrappledBy = null; Console.WriteLine("  You are no longer grappled."); }
        var others = Active.Where(x => x.Alive && x != e).ToList();
        if (P.HasFeat("Thin the Herd") && others.Any())
        {
            Console.Write($"  Thin the Herd! Free attack on another enemy? (y/n): ");
            if ((Console.ReadLine() ?? "").Trim().ToLower() == "y")
            {
                var t = others.Count == 1 ? others[0] : PickTarget(others);
                if (t != null) FreeAttack(t);
            }
        }
        // Arrow recovery from enemy body
        if (P.CharacterType == "Archer" && e.ArrowsInBody > 0)
        {
            int recovered = 0;
            for (int ai = 0; ai < e.ArrowsInBody; ai++)
                if (Rng.Next(4) > 0) recovered++; // 3/4 chance each
            if (recovered > 0) { P.ArrowCount += recovered; Console.WriteLine($"  Recovered {recovered} arrow(s). Total: {P.ArrowCount}."); }
            if (recovered < e.ArrowsInBody) Console.WriteLine($"  {e.ArrowsInBody - recovered} arrow(s) broke.");
            e.ArrowsInBody = 0;
        }

        // ── Shield drop ─────────────────────────────────────────────────────
        if (e.HasShield && !e.ShieldLost)
        {
            e.ShieldLost = true; // consumed as loot
            string dropName = e.ShieldBlockBonus == 1 ? "Buckler"
                            : e.TypeName.StartsWith("Orc") ? "Round Shield"
                            : "Kite Shield";
            int dropDef   = 1;                  // +1 ArmorDamageReduction
            int dropBlock = e.ShieldBlockBonus; // +N MaxBlock
            Console.WriteLine($"  A {dropName} falls from {e.Name}! (Def +{dropDef}, Block +{dropBlock})");

            foreach (var candidate in AllPlayers.Where(pl => pl.HP > 0))
            {
                if (candidate.OffHandShieldName == null)
                {
                    Console.Write($"  {candidate.Name}: Pick up the {dropName}? (y/n): ");
                    if ((Console.ReadLine() ?? "").Trim().ToLower() == "y")
                    {
                        candidate.OffHandShieldName    = dropName;
                        candidate.OffHandShieldDefense = dropDef;
                        candidate.OffHandShieldBlock   = dropBlock;
                        candidate.ArmorDamageReduction += dropDef;
                        candidate.MaxBlock             += dropBlock;
                        Console.WriteLine($"  {candidate.Name} equips the {dropName} in their off-hand. "
                            + $"(Armor Reduction: +{candidate.ArmorDamageReduction}, Block Max: {candidate.MaxBlock})");
                        break;
                    }
                }
                else
                {
                    Console.Write($"  {candidate.Name}: Swap your {candidate.OffHandShieldName} for the {dropName}? (y/n): ");
                    if ((Console.ReadLine() ?? "").Trim().ToLower() == "y")
                    {
                        // Remove old shield bonuses
                        candidate.ArmorDamageReduction -= candidate.OffHandShieldDefense;
                        candidate.MaxBlock             -= candidate.OffHandShieldBlock;
                        // Equip new shield
                        candidate.OffHandShieldName    = dropName;
                        candidate.OffHandShieldDefense = dropDef;
                        candidate.OffHandShieldBlock   = dropBlock;
                        candidate.ArmorDamageReduction += dropDef;
                        candidate.MaxBlock             += dropBlock;
                        Console.WriteLine($"  {candidate.Name} equips the {dropName}. "
                            + $"(Armor Reduction: +{candidate.ArmorDamageReduction}, Block Max: {candidate.MaxBlock})");
                        break;
                    }
                }
            }
        }

        // ── Weapon drop ─────────────────────────────────────────────────────
        string dropWeapon = EnemyWeaponType(e);
        if (dropWeapon != "" && !e.Disarmed)
        {
            var (minA, maxA, minD, maxD) = WeaponPickupStats(dropWeapon);
            if (minA > 0)
            {
                Console.WriteLine($"  {e.Name}'s {dropWeapon} lies on the ground! (Atk {minA}-{maxA}, Dmg {minD}-{maxD})");
                bool weaponTaken = false;
                foreach (var candidate in AllPlayers.Where(pl => pl.HP > 0))
                {
                    Console.Write($"  {candidate.Name}: Pick up the {dropWeapon}? (y/n): ");
                    if ((Console.ReadLine() ?? "").Trim().ToLower() != "y") continue;

                    if (candidate.HeldWeapon == null)
                    {
                        candidate.HeldWeapon = dropWeapon;
                        Console.WriteLine($"  {candidate.Name} equips the {dropWeapon}. (Atk {minA}-{maxA}, Dmg {minD}-{maxD})");
                        weaponTaken = true;
                    }
                    else if (candidate.SecondaryWeapon == null)
                    {
                        Console.Write($"  Main hand (m) replaces [{candidate.HeldWeapon}] \u2192 off-hand, or add as off-hand (o), or skip (n)? ");
                        string slot = (Console.ReadLine() ?? "").Trim().ToLower();
                        if (slot == "m")
                        {
                            candidate.SecondaryWeapon = candidate.HeldWeapon;
                            candidate.HeldWeapon = dropWeapon;
                            Console.WriteLine($"  {candidate.Name} wields {dropWeapon}; {candidate.SecondaryWeapon} moved to off-hand.");
                            weaponTaken = true;
                        }
                        else if (slot == "o")
                        {
                            candidate.SecondaryWeapon = dropWeapon;
                            Console.WriteLine($"  {candidate.Name} adds {dropWeapon} to off-hand.");
                            weaponTaken = true;
                        }
                    }
                    else
                    {
                        Console.Write($"  Replace main [{candidate.HeldWeapon}] (m), off-hand [{candidate.SecondaryWeapon}] (o), or skip (n)? ");
                        string slot = (Console.ReadLine() ?? "").Trim().ToLower();
                        if (slot == "m")
                        {
                            Console.WriteLine($"  {candidate.Name} drops {candidate.HeldWeapon} and wields {dropWeapon}.");
                            candidate.HeldWeapon = dropWeapon;
                            weaponTaken = true;
                        }
                        else if (slot == "o")
                        {
                            Console.WriteLine($"  {candidate.Name} drops {candidate.SecondaryWeapon} and holsters {dropWeapon}.");
                            candidate.SecondaryWeapon = dropWeapon;
                            weaponTaken = true;
                        }
                    }
                    if (weaponTaken) break;
                }
            }
        }
    }

    // ── Necromancy ──────────────────────────────────────────────────────────

    void RaiseDead(Enemy corpse, Enemy necro)
    {
        if (SilenceTurns > 0) { Console.WriteLine($"  {necro.Name} chants over a corpse — but the silence smothers the ritual!"); return; }
        corpse.HP = corpse.MaxHP;
        corpse.Fled = false;
        corpse.IsUndead = true;
        // Clear status effects and strip all feats / special abilities
        corpse.KnockedOut = false; corpse.KnockedDown = false; corpse.OffBalance = false;
        corpse.Disarmed = false; corpse.Grappled = false; corpse.Charmed = false;
        corpse.CanMove = true; corpse.KOCount = 0; corpse.KOTurns = 0;
        corpse.BleedDmg = 0; corpse.BurningDmg = 0; corpse.BurningTurns = 0;
        corpse.FrostPenalty = 0; corpse.FrostTurns = 0;
        corpse.HasDoubleTap = false; corpse.HasParry = false; corpse.HasBlock = false;
        corpse.HasKick = false; corpse.HasArmBlock = false;
        corpse.MagicResistant = false; corpse.MagicVulnerable = false;
        corpse.ToughHideMin = 0; corpse.ToughHideMax = 0;
        corpse.XpAwarded = false; // can be defeated again for XP
        if (!corpse.Name.StartsWith("Undead ")) corpse.Name = "Undead " + corpse.Name;
        corpse.TypeName = "Undead";
        Console.WriteLine($"  {necro.Name} raises {corpse.Name} from the dead! (Undead — base stats, no special abilities. HP:{corpse.HP}/{corpse.MaxHP})");
    }

    Enemy MakeUndeadEnemy(Enemy e)
    {
        e.IsUndead = true;
        e.HasDoubleTap = false; e.HasParry = false; e.HasBlock = false;
        e.HasKick = false; e.HasArmBlock = false;
        e.MagicResistant = false; e.MagicVulnerable = false;
        e.ToughHideMin = 0; e.ToughHideMax = 0;
        if (!e.Name.StartsWith("Undead ")) e.Name = "Undead " + e.Name;
        e.TypeName = "Undead";
        return e;
    }

    void NecromancerHealUndead(Enemy necro, Enemy undead)
    {
        int heal = Rng.Next(1, 5) + Rng.Next(1, 5); // negative energy 2d4 heals undead
        undead.HP = Math.Min(undead.MaxHP, undead.HP + heal);
        Console.WriteLine($"  {necro.Name} channels negative energy into {undead.Name}: +{heal} HP ({undead.HP}/{undead.MaxHP}).");
    }

    void NecromancerTouchPlayer(Enemy necro)
    {
        int atk = Rng.Next(necro.MinAttack, necro.MaxAttack + 1) - necro.AttackPenalty - necro.FrostPenalty;
        int ddg = Rng.Next(P.MinDodge, P.MaxDodge + 1);
        Console.WriteLine($"  {necro.Name} reaches out with a NEGATIVE TOUCH! {atk} vs your dodge {ddg}.");
        if (atk >= ddg)
        {
            int dmg = Rng.Next(1, 5) + Rng.Next(1, 5); // 2d4 necrotic
            if (P.Defending) dmg = Math.Max(1, dmg / 2);
            P.HP -= dmg;
            Console.WriteLine($"  Negative touch HIT! {dmg} necrotic damage. HP:{P.HP}/{P.MaxHP}");
        }
        else Console.WriteLine("  Negative touch MISS!");
    }

    void OpportunistPromptNote() => Console.WriteLine("  (Opportunist: attack them at end of turn!)");

    void OpportunistAttacks(List<Enemy> debuffed)
    {
        Console.WriteLine("\n  [Opportunist] Attack debuffed enemies?");
        for (int i = 0; i < debuffed.Count; i++) Console.WriteLine($"    [{i + 1}] {debuffed[i].ShortStatus()}");
        Console.WriteLine("    [0] Skip");

        int pen = -1;
        while (debuffed.Any(e => e.Alive))
        {
            Console.Write($"  Target (penalty {pen}, 0=stop): ");
            if (!int.TryParse(Console.ReadLine()?.Trim(), out int idx) || idx == 0) break;
            if (idx < 1 || idx > debuffed.Count || !debuffed[idx - 1].Alive) { Console.WriteLine("  Invalid."); continue; }

            var t = debuffed[idx - 1];
            int a = Rng.Next(P.MinAttack, P.MaxAttack + 1) + pen;
            int d = Rng.Next(t.MinDodge, t.MaxDodge + 1) - 4 - t.DodgePenalty;
            Console.WriteLine($"  Opportunist: {a} vs {t.Name}'s dodge {d}.");
            if (a >= d && !EnemyBlocks(t, a))
            {
                int dmg = Rng.Next(P.MinDamage, P.MaxDamage + 1) + (d <= 0 ? 1 : 0);
                TryEnemyArmBlock(t, a, ref dmg);
                dmg = ReduceByToughHide(t, dmg);
                t.HP -= dmg;
                Console.WriteLine($"  HIT! {dmg} dmg → {t.Name} HP:{t.HP}/{t.MaxHP}");
                if (!t.Alive) HandleKill(t);
            }
            else if (a < d) Console.WriteLine("  Miss!");
            pen--;
        }
    }

    // ── WEAPON HELPERS ───────────────────────────────────────────────────

    GridPos RandomAdjacent(GridPos p)
    {
        int[] dx = { 0, 0, 1, -1 }, dy = { 1, -1, 0, 0 };
        int i = Rng.Next(4);
        return new GridPos(Math.Clamp(p.X + dx[i], 0, 49), Math.Clamp(p.Y + dy[i], 0, 49));
    }

    string EnemyWeaponType(Enemy e) => e switch
    {
        SpellGoblin => "",
        GoblinWarrior    => "Short Sword",
        Goblin => "Goblin Dagger",
        OrcBarbarian => "Battle Axe",
        OrcMonk om => om.HasMartialStaff ? "Staff" : "",
        OrcPriestess => "War Mace",
        OrcRanger => "Kukuri",
        Orc => "Orc Longsword",
        Troll => "Troll Axe",
        Ogre => "Ogre Club",
        HobgoblinFighter => "Long Sword",
        HobgoblinThief   => "Short Sword",
        HobgoblinCleric  => "Mace",
        _ => ""
    };

    (int MinAtk, int MaxAtk, int MinDmg, int MaxDmg) WeaponPickupStats(string w) => w switch
    {
        "Goblin Dagger"  => (1, 6, 1, 6),
        "Orc Longsword"  => (3, 9, 2, 10),
        "Troll Axe"      => (2, 12, 3, 12),
        "Bastard Sword"  => (2, 8, 2, 8),
        "Rapier Sword"   => (1, 6, 3, 6),
        "Short Sword"    => (1, 6, 1, 6),
        "Hand Axe"       => (1, 6, 2, 8),
        "Battle Axe"     => (3, 9, 4, 12),
        "War Mace"       => (3, 9, 4, 14),
        "Mace"           => (1, 6, 2, 8),
        "Great Axe"      => (2, 9, 1, 9),
        "Staff"          => (1, 6, 2, 6),
        "Kukuri"         => (2, 8, 2, 8),
        "Wand"           => (1, 6, 3, 4),
        "Long Sword"     => (2, 9, 2, 10),
        "Ogre Club"      => (2, 14, 4, 16),
        _ => (0, 0, 0, 0)
    };

    void DoMmaFreeAction(List<Enemy> alive)
    {
        if (!alive.Any()) return;
        Console.WriteLine("  [MMA] Slip free! Immediate free action:");
        Console.Write("  [A]ttack  [H]eal  [D]efend  [S]pell  [skip]: ");
        string ch = (Console.ReadLine() ?? "").Trim().ToLower();
        if (ch.StartsWith("a"))
        {
            var mt = PickTarget(alive);
            if (mt != null) DoAttack(mt);
        }
        else if (ch.StartsWith("h")) DoHeal();
        else if (ch.StartsWith("d")) { P.Defending = true; Console.WriteLine("  Defensive stance."); }
        else if (ch.StartsWith("s") && P.KnownSpells.Any())
        {
            for (int si = 0; si < P.KnownSpells.Count; si++) Console.WriteLine($"  [{si+1}] {P.KnownSpells[si]}");
            Console.Write("  Cast: ");
            if (int.TryParse(Console.ReadLine()?.Trim(), out int sc) && sc >= 1 && sc <= P.KnownSpells.Count)
                DoSpell(P.KnownSpells[sc - 1], alive);
        }
    }

    void DoBowAttack(Enemy target)
    {
        if (P.ArrowCount <= 0) { Console.WriteLine("  Out of arrows!"); return; }
        float feet = PlayerPos.Feet(target.Position);
        if (feet < 4f) { Console.WriteLine($"  Too close to use bow! ({feet:F1}ft, min 4ft)"); return; }
        if (feet > 60f) { Console.WriteLine($"  Too far! ({feet:F1}ft, max 60ft)"); return; }
        int dmgMin, dmgMax;
        if (feet <= 14f) { dmgMin = 4; dmgMax = 12; }
        else if (feet <= 45f) { dmgMin = 2; dmgMax = 10; }
        else { dmgMin = 1; dmgMax = 5; }
        dmgMin += P.MinRangedDmgBonus; dmgMax += P.MinRangedDmgBonus + P.MaxRangedDmgBonus;
        int atkRoll = Rng.Next(P.MinRangedAtk, P.MaxRangedAtk + 1) + SlayerAtk();
        int ddg = Rng.Next(target.MinDodge, target.MaxDodge + 1) - target.DodgePenalty;
        Console.WriteLine($"  BOW ({feet:F0}ft, dmg {dmgMin}-{dmgMax})! Roll {atkRoll} vs {target.Name}'s dodge {ddg}.");
        P.ArrowCount--;
        Console.WriteLine($"  Arrows remaining: {P.ArrowCount}");
        if (atkRoll >= ddg && !EnemyBlocks(target, atkRoll, isRanged: true))
        {
            int dmg = Rng.Next(dmgMin, dmgMax + 1) + SlayerDmg();
            dmg = ReduceByToughHide(target, dmg);
            Console.WriteLine($"  Arrow HIT! {dmg} dmg → {target.Name} HP:{target.HP - dmg}/{target.MaxHP}");
            target.HP -= dmg;
            target.ArrowsInBody++;
            if (!target.Alive) HandleKill(target);
        }
        else if (atkRoll < ddg) Console.WriteLine("  Arrow MISS!");

        // Double Tap: second arrow
        if (P.HasFeat("Double Tap") && P.ArrowCount > 0 && target.Alive)
        {
            Console.WriteLine("  [Double Tap] Second arrow!");
            P.ArrowCount--;
            Console.WriteLine($"  Arrows remaining: {P.ArrowCount}");
            int atk2 = Rng.Next(P.MinRangedAtk, P.MaxRangedAtk + 1) + SlayerAtk();
            int ddg2 = Rng.Next(target.MinDodge, target.MaxDodge + 1) - target.DodgePenalty;
            int d2Min, d2Max;
            if (feet <= 14f) { d2Min = 4; d2Max = 12; }
            else if (feet <= 45f) { d2Min = 2; d2Max = 10; }
            else { d2Min = 1; d2Max = 5; }
            d2Min += P.MinRangedDmgBonus; d2Max += P.MinRangedDmgBonus + P.MaxRangedDmgBonus;
            Console.WriteLine($"  BOW ({feet:F0}ft)! Roll {atk2} vs dodge {ddg2}.");
            if (atk2 >= ddg2)
            {
                int dmg2 = Rng.Next(d2Min, d2Max + 1) + SlayerDmg();
                dmg2 = ReduceByToughHide(target, dmg2);
                Console.WriteLine($"  Arrow HIT! {dmg2} dmg → {target.Name} HP:{target.HP - dmg2}/{target.MaxHP}");
                target.HP -= dmg2;
                target.ArrowsInBody++;
                if (!target.Alive) HandleKill(target);
            }
            else Console.WriteLine("  Arrow MISS!");
        }
    }

    void DoWandAttack(Enemy target)
    {
        float feet = PlayerPos.Feet(target.Position);
        if (feet < 20f) { Console.WriteLine($"  Too close for wand! ({feet:F1}ft, min 20ft)"); return; }
        if (feet > 50f) { Console.WriteLine($"  Too far for wand! ({feet:F1}ft, max 50ft)"); return; }
        int atkRoll = Rng.Next(P.MinRangedAtk, P.MaxRangedAtk + 1) + SlayerAtk() + P.SpellAttackBonus;
        int ddg = Rng.Next(target.MinDodge, target.MaxDodge + 1) - target.DodgePenalty;
        Console.WriteLine($"  WAND ({feet:F0}ft, dmg 3-4)! Roll {atkRoll} vs {target.Name}'s dodge {ddg}.");
        if (atkRoll >= ddg && !EnemyBlocks(target, atkRoll, isRanged: true))
        {
            int dmg = Rng.Next(3, 5) + SlayerDmg();
            dmg = ReduceByToughHide(target, dmg);
            Console.WriteLine($"  Wand HIT! {dmg} dmg → {target.Name} HP:{target.HP - dmg}/{target.MaxHP}");
            target.HP -= dmg;
            if (!target.Alive) HandleKill(target);
        }
        else Console.WriteLine("  Wand MISS!");
    }

    void DoMaceAttack(Enemy target)
    {
        int atkRoll = Rng.Next(P.MinAttack, P.MaxAttack + 1) + SlayerAtk();
        int ddg = Rng.Next(target.MinDodge, target.MaxDodge + 1) - target.DodgePenalty;
        Console.WriteLine($"  MACE (non-lethal 2d4)! Roll {atkRoll} vs {target.Name}'s dodge {ddg}.");
        if (atkRoll >= ddg && !EnemyBlocks(target, atkRoll))
        {
            int dmg = Rng.Next(1, 5) + Rng.Next(1, 5) + SlayerDmg();
            dmg = ReduceByToughHide(target, dmg);
            if (dmg >= target.HP)
            {
                if (target.IsUndead)
                {
                    Console.WriteLine($"  Mace HIT! {dmg} dmg — undead take lethal damage! {target.Name} defeated!");
                    target.HP -= dmg;
                    HandleKill(target);
                }
                else
                {
                    Console.WriteLine($"  Mace HIT! {dmg} dmg → {target.Name} KNOCKED OUT!");
                    target.HP -= dmg;
                    KnockOut(target);
                }
            }
            else
            {
                Console.WriteLine($"  Mace HIT! {dmg} dmg (non-lethal) → {target.Name} HP:{target.HP - dmg}/{target.MaxHP}");
                target.HP -= dmg;
            }
        }
        else Console.WriteLine("  Mace MISS!");
    }

    // ── GRAPPLE ───────────────────────────────────────────────────────────

    int GrappleStyleTier() =>
        new[] { "Kehon", "Judo", "Taekwondo", "Chidia" }.Count(f => P.HasFeat(f));

    void DoGrapple(Enemy target)
    {
        int gst2 = GrappleStyleTier();
        int minG = P.MinGrapple + P.GetFeatStacks("Closeliner") + (gst2 >= 2 ? 1 : 0);
        int maxG = P.MaxGrapple + (gst2 >= 3 ? 2 : 0);
        int gRoll = Rng.Next(minG, maxG + 1) - P.SprintPenalty + SlayerAtk();
        P.SprintPenalty = 0;
        if (P.EnlargeActive) gRoll *= 2;
        int dRoll = Rng.Next(target.MinDodge, target.MaxDodge + 1);
        Console.WriteLine($"  Grapple! Roll {gRoll} vs {target.Name}'s dodge {dRoll}.");
        if (gRoll < dRoll) { Console.WriteLine("  Grapple FAILED!"); return; }

        target.Grappled = true;
        Console.WriteLine($"  Grapple SUCCESS! {target.Name} is grappled.");

        string gOpts = P.HasFeat("Judo") ? "[H]old  [T]hrow  [D]isarm" : "[H]old  [T]hrow";
        Console.Write($"  Option: {gOpts}: ");
        string go = (Console.ReadLine() ?? "h").Trim().ToLower();

        if (go.StartsWith("t"))
        {
            target.Grappled = false; target.KnockedDown = true;
            Console.WriteLine($"  {target.Name} thrown to the ground!");
        }
        else if (go.StartsWith("d") && P.HasFeat("Judo"))
        {
            target.Grappled = false; target.Disarmed = true;
            var judoDrop = RandomAdjacent(target.Position);
            target.WeaponPos = judoDrop;
            string judoWpType = EnemyWeaponType(target);
            if (judoWpType.Length > 0) GroundWeapons.Add((judoDrop, judoWpType));
            Console.WriteLine($"  {target.Name}'s weapon wrenched to ({judoDrop.X},{judoDrop.Y})!");
        }
        else
        {
            int minGD = P.MinGrappleDmg + P.GetFeatStacks("Closeliner");
            int gDmg = Rng.Next(minGD, P.MaxGrappleDmg + 1) + SlayerDmg();
            // Martial Artist: +1d4 grapple damage every 4 levels from L2
            if (P.CharacterType == "Martial Artist" && P.Level >= 2)
            {
                int maDice = (P.Level - 2) / 4 + 1;
                int maBonus = 0;
                for (int d = 0; d < maDice; d++) maBonus += Rng.Next(1, 5);
                gDmg += maBonus;
                Console.WriteLine($"  Martial Artist grip: +{maBonus} ({maDice}d4) grapple damage!");
            }
            target.HP -= gDmg;
            LichTouchHeal(gDmg);
            Console.WriteLine($"  Grapple damage: {gDmg} → {target.Name} HP:{target.HP}/{target.MaxHP}");
            if (!target.Alive) { HandleKill(target); return; }

            // Taekwondo limb break
            if (P.HasFeat("Taekwondo"))
            {
                Console.Write("  Taekwondo limb break? [L]eg [A]rm [N]eck [B]ack [S]kip: ");
                string lb = (Console.ReadLine() ?? "s").Trim().ToLower();
                int bd = Rng.Next(P.MinLimbBreak, P.MaxLimbBreak + 1);
                switch (lb.Length > 0 ? lb[0] : 's')
                {
                    case 'l': target.HP -= bd; target.CanMove = false; Console.WriteLine($"  Leg broken! {bd} dmg. Can't walk."); break;
                    case 'a': target.HP -= bd; target.Disarmed = true; Console.WriteLine($"  Arm broken! {bd} dmg. Can't hold weapon."); break;
                    case 'n': target.HP = 0; Console.WriteLine("  Neck snapped! Instant kill!"); break;
                    case 'b': target.HP -= bd; target.CanMove = false; target.KnockedDown = true; Console.WriteLine($"  Back broken! {bd} dmg. Can't move."); break;
                }
                if (!target.Alive) HandleKill(target);
            }

            // Kehon Black Belt: instant KO
            if (P.HasFeat("Kehon Black Belt") && target.Alive)
            {
                Console.Write($"  Kehon Black Belt! Instant KO {target.Name}? (y/n): ");
                if ((Console.ReadLine() ?? "").Trim().ToLower() == "y")
                {
                    KnockOut(target);
                    target.Grappled = false;
                }
            }
        }
    }

    void JudoPrompt(Enemy target)
    {
        if (!target.Alive) return;
        Console.Write($"  Judo! Free grapple on {target.Name}? (y/n): ");
        if ((Console.ReadLine() ?? "").Trim().ToLower() == "y") DoGrapple(target);
    }

    void DoHeal()
    {
        int dice = 1 + P.GetFeatStacks("Potion Brewer");
        int h = 0;
        for (int d = 0; d < dice; d++) h += Rng.Next(P.MinPotionHeal, P.MaxPotionHeal + 1);
        h = Math.Min(h, P.MaxHP - P.HP);
        P.HP += h;
        Console.WriteLine($"  Healing potion! +{h} HP. ({P.HP}/{P.MaxHP})");
    }

    void DoSpell(string spell, List<Enemy> alive)
    {
        // Spell dodge: enemy rolls dodge vs 1d6; success → half damage
        int SpellDodgeCheck(Enemy e, int dmg)
        {
            int sAtk = Rng.Next(1, 7);
            int eDdg = Rng.Next(e.MinDodge, e.MaxDodge + 1) - e.DodgePenalty;
            if (eDdg >= sAtk) { dmg = Math.Max(1, dmg / 2); Console.WriteLine($"    (Spell dodged! {e.Name} takes half: {dmg})"); }
            return dmg;
        }

        int SpellAtk(string element) => P.SpellAttackBonus + (P.HasFeat("Spell Focus") ? 2 : 0) + (P.HasFeat("Elemental") && P.ElementalFocus == element ? 2 : 0);
        bool lastSpellCrit = false, lastSpellFumble = false;
        int SpellAtkRoll() { int raw = Rng.Next(P.MinSpellAtk, P.MaxSpellAtk + 1); lastSpellCrit = raw == P.MaxSpellAtk; lastSpellFumble = raw == P.MinSpellAtk; return raw + SlayerAtk(); }
        int SpellDmg(int dmg, string element) { if (P.HasFeat("Magical Overflow")) dmg *= 2; if (P.HasFeat("Elemental") && P.ElementalFocus == element) dmg += 2; dmg += P.MinSpellDmgBonus + P.MaxSpellDmgBonus + SlayerDmg(); return dmg; }
        int ExtDur(int turns) => P.HasFeat("Extended Magi") ? turns * 2 : turns;
        float SpellRange(float range) => P.HasFeat("OverReach Magic") ? range * 2f : range;

        switch (spell)
        {
            case "Fire Blast":
            {
                // 5×5 foot area (2×2 squares): hits all enemies within Manhattan dist ≤ 1 of chosen target
                var primary = PickTarget(alive);
                if (primary == null) break;
                var targets = alive.Where(e => e.Position.ManhattanDist(primary.Position) <= 1).ToList();
                Console.WriteLine($"  FIRE BLAST! 5×5 area centered on {primary.Name}. {targets.Count} enemy(ies) hit.");
                // Friendly fire: player in blast area?
                if (PlayerPos.ManhattanDist(primary.Position) <= 1)
                {
                    int selfFire = Rng.Next(4, 13);
                    Console.WriteLine($"  [!] You're in the blast area! {selfFire} fire damage. HP:{P.HP - selfFire}/{P.MaxHP}");
                    P.HP -= selfFire;
                }
                int burnDmg = Rng.Next(1, 5);
                int burnTurns = ExtDur(Rng.Next(4, 9));
                foreach (var e in targets)
                {
                    int dmg = Rng.Next(4, 13) + P.SpellDamageBonus;
                    dmg = SpellDmg(dmg, "fire");
                    if (e.MagicResistant) { dmg = Math.Max(1, dmg / 2); Console.WriteLine($"    (Magic resistant!)"); }
                    else if (e.MagicVulnerable) { dmg = (int)(dmg * 1.5); Console.WriteLine($"    (Magic vulnerable! ×1.5)"); }
                    dmg = SpellDodgeCheck(e, dmg);
                    e.HP -= dmg; e.HitBySpell = true;
                    Console.WriteLine($"    {e.Name} takes {dmg} fire damage! HP:{e.HP}/{e.MaxHP}");
                    if (!e.Alive) { HandleKill(e); continue; }
                    int eBurnDmg = e.MagicResistant ? Math.Max(1, burnDmg / 2) : e.MagicVulnerable ? (int)(burnDmg * 1.5) : burnDmg;
                    e.BurningDmg = Math.Max(e.BurningDmg, eBurnDmg);
                    e.BurningTurns = Math.Max(e.BurningTurns, burnTurns);
                    Console.WriteLine($"    {e.Name} BURNING! ({eBurnDmg}/action × {burnTurns} actions)");
                }
                break;
            }
            case "Chain Lightning":
            {
                // Starts at chosen target, jumps to next enemy within 5 feet (2 squares), continues
                var firstTarget = PickTarget(alive);
                if (firstTarget == null) break;
                Console.WriteLine($"  CHAIN LIGHTNING! Starting at {firstTarget.Name}.");
                var hit = new HashSet<Enemy>();
                var cur = firstTarget;
                int jumpCount = 0;
                while (cur != null && jumpCount < 20)
                {
                    int dmg = Rng.Next(3, 7) + P.SpellDamageBonus;
                    dmg = SpellDmg(dmg, "lightning");
                    if (cur.MagicResistant) { dmg = Math.Max(1, dmg / 2); Console.WriteLine($"    (Magic resistant!)"); }
                    else if (cur.MagicVulnerable) { dmg = (int)(dmg * 1.5); Console.WriteLine($"    (Magic vulnerable! ×1.5)"); }
                    dmg = SpellDodgeCheck(cur, dmg);
                    cur.HP -= dmg; cur.HitBySpell = true; hit.Add(cur);
                    Console.WriteLine($"    {cur.Name} struck for {dmg} lightning! HP:{cur.HP}/{cur.MaxHP}");
                    if (!cur.Alive) HandleKill(cur);
                    // Find next unhit enemy within 2 squares (caster exempt; friendly fire applies to all others)
                    cur = alive.Where(e => !hit.Contains(e) && e.Alive && e.Position.ManhattanDist(cur.Position) <= 2)
                               .OrderBy(e => e.Position.ManhattanDist(cur.Position))
                               .FirstOrDefault();
                    jumpCount++;
                }
                Console.WriteLine($"  Chain Lightning hit {hit.Count} enemies.");
                P.ChainLightningUses++;
                if (P.ChainLightningUses > 3)
                {
                    int selfDmg = Rng.Next(1, 5);
                    P.HP -= selfDmg;
                    Console.WriteLine($"  Channeling backlash! You take {selfDmg} damage. HP:{P.HP}/{P.MaxHP}");
                }
                break;
            }
            case "Frost Burst":
            {
                // 7.5×7.5 area (3×3 squares): cone or square around caster
                Console.Write("  FROST BURST! [S]quare (3×3 around you) or cone [N/S/E/W]: ");
                string fb = (Console.ReadLine() ?? "s").Trim().ToLower();
                List<Enemy> targets;
                if (fb.StartsWith("n") || fb.StartsWith("s") || fb.StartsWith("e") || fb.StartsWith("w"))
                {
                    // Cone: 3×3 block in chosen direction from player
                    int cx = PlayerPos.X + (fb.StartsWith("e") ? 1 : fb.StartsWith("w") ? -1 : 0);
                    int cy = PlayerPos.Y + (fb.StartsWith("s") ? 1 : fb.StartsWith("n") ? -1 : 0);
                    targets = alive.Where(e =>
                        Math.Abs(e.Position.X - cx) <= 1 && Math.Abs(e.Position.Y - cy) <= 1).ToList();
                    Console.WriteLine($"  Cone {fb.ToUpper()}: {targets.Count} enemies caught!");
                }
                else
                {
                    // Square: Chebyshev distance ≤ 1 from player (3×3)
                    targets = alive.Where(e =>
                        Math.Abs(e.Position.X - PlayerPos.X) <= 1 && Math.Abs(e.Position.Y - PlayerPos.Y) <= 1).ToList();
                    // Friendly fire: player is in own Frost Burst square
                    Console.WriteLine($"  Frost Burst square: {targets.Count} enemies caught!");
                    int selfFrost = Rng.Next(2, 9);
                    Console.WriteLine($"  [!] The frost engulfs you too! {selfFrost} cold damage. HP:{P.HP - selfFrost}/{P.MaxHP}");
                    P.HP -= selfFrost;
                }
                int frostPen = Rng.Next(2, 9);
                int frostTurns = ExtDur(Rng.Next(2, 7));
                foreach (var e in targets)
                {
                    int dmg = Rng.Next(2, 9) + P.SpellDamageBonus;
                    dmg = SpellDmg(dmg, "frost");
                    if (e.MagicResistant) { dmg = Math.Max(1, dmg / 2); Console.WriteLine($"    (Magic resistant!)"); }
                    else if (e.MagicVulnerable) { dmg = (int)(dmg * 1.5); Console.WriteLine($"    (Magic vulnerable! ×1.5)"); }
                    dmg = SpellDodgeCheck(e, dmg);
                    e.HP -= dmg; e.HitBySpell = true;
                    Console.WriteLine($"    {e.Name} takes {dmg} frost! HP:{e.HP}/{e.MaxHP}");
                    if (!e.Alive) { HandleKill(e); continue; }
                    int eFrostPen = e.MagicResistant ? Math.Max(1, frostPen / 2) : e.MagicVulnerable ? (int)(frostPen * 1.5) : frostPen;
                    e.FrostPenalty = Math.Max(e.FrostPenalty, eFrostPen);
                    e.FrostTurns = Math.Max(e.FrostTurns, frostTurns);
                    Console.WriteLine($"    {e.Name} FROZEN! (-{eFrostPen} on rolls for {frostTurns} turns)");
                }
                break;
            }
            case "Air Blade":
            {
                int upgrades = P.Level >= 2 ? (P.Level - 2) / 4 + 1 : 0;
                float maxRange = SpellRange(30f + upgrades * 5f);
                var inRange = alive.Where(e => PlayerPos.Feet(e.Position) <= maxRange).ToList();
                if (!inRange.Any())
                {
                    Console.WriteLine($"  No enemies within Air Blade range ({maxRange:F0}ft).");
                    break;
                }
                Console.WriteLine($"  Air Blade range: {maxRange:F0}ft{(upgrades > 0 ? $"  (+{upgrades}d6 bonus dmg)" : "")}");
                var abTarget = PickTarget(inRange);
                if (abTarget == null) break;
                float abFeet = PlayerPos.Feet(abTarget.Position);
                int abDmg = Rng.Next(2, 7) + P.SpellDamageBonus;
                for (int ui = 0; ui < upgrades; ui++) abDmg += Rng.Next(1, 7);
                abDmg = SpellDmg(abDmg, "air");
                if (abTarget.MagicResistant) { abDmg = Math.Max(1, abDmg / 2); Console.WriteLine("    (Magic resistant!)"); }
                else if (abTarget.MagicVulnerable) { abDmg = (int)(abDmg * 1.5); Console.WriteLine("    (Magic vulnerable! ×1.5)"); }
                abDmg = SpellDodgeCheck(abTarget, abDmg);
                int abAtkRoll = SpellAtkRoll() + SpellAtk("air");
                if (lastSpellFumble) { Console.WriteLine($"  SPELL FUMBLE! Air Blade whips back! You take {abDmg} damage! HP:{P.HP - abDmg}/{P.MaxHP}"); P.HP -= abDmg; break; }
                if (lastSpellCrit) { abDmg *= 2; Console.WriteLine($"  SPELL CRITICAL! ×2 slashing damage!"); }
                if (EnemyBlocks(abTarget, abAtkRoll, isRanged: true)) break;
                abTarget.HP -= abDmg; abTarget.HitBySpell = true;
                Console.WriteLine($"  AIR BLADE! ({abFeet:F0}ft) {abTarget.Name} struck for {abDmg} slashing damage! HP:{abTarget.HP}/{abTarget.MaxHP}");
                if (!abTarget.Alive) HandleKill(abTarget);
                break;
            }
            case "Air Wave":
            {
                float waveRange = SpellRange(20f); // 8 squares — nearby enemies
                var waveTargets = alive.Where(e => PlayerPos.Feet(e.Position) <= waveRange).ToList();
                Console.WriteLine($"  AIR WAVE! Pushing enemies within {waveRange:F0}ft ({waveTargets.Count} in range).");
                if (!waveTargets.Any()) { Console.WriteLine("  No enemies nearby!"); break; }
                foreach (var e in waveTargets)
                {
                    int wRoll = Rng.Next(1, 7);
                    float wFeet = PlayerPos.Feet(e.Position);
                    Console.Write($"  {e.Name} ({wFeet:F0}ft) — roll {wRoll}: ");
                    if (wRoll == 1)
                    {
                        Console.WriteLine("resists the wave!");
                        continue;
                    }
                    // Push direction: away from player
                    int pdx = e.Position.X - PlayerPos.X;
                    int pdy = e.Position.Y - PlayerPos.Y;
                    int sdx, sdy;
                    if (pdx == 0 && pdy == 0) { sdx = 0; sdy = -1; }
                    else if (Math.Abs(pdx) >= Math.Abs(pdy)) { sdx = pdx > 0 ? 1 : -1; sdy = 0; }
                    else { sdx = 0; sdy = pdy > 0 ? 1 : -1; }
                    e.Position = new GridPos(
                        Math.Clamp(e.Position.X + sdx * 4, 0, 49),
                        Math.Clamp(e.Position.Y + sdy * 4, 0, 49));
                    if (wRoll >= 5)
                    {
                        e.KnockedDown = true; e.OffBalance = true;
                        Console.WriteLine($"knocked off feet + pushed! ({e.Position.X},{e.Position.Y})");
                    }
                    else
                    {
                        Console.WriteLine($"pushed back 10ft. ({e.Position.X},{e.Position.Y})");
                    }
                }
                break;
            }
            case "Magic Hand":
            {
                int mhTurns = ExtDur(Rng.Next(2, 5));
                P.MagicHandTurns = mhTurns;
                Console.WriteLine($"  MAGIC HAND! A floating hand appears for {mhTurns} turns, wielding your weapon's power.");
                break;
            }
            case "Invisibility":
            {
                int invTurns = ExtDur(Rng.Next(1, 5));
                P.InvisibilityTurns = invTurns;
                Console.WriteLine($"  INVISIBILITY! You fade from sight for {invTurns} turn(s). Enemies can't target you.");
                break;
            }
            case "Phantom Image":
            {
                int piTurns = ExtDur(Rng.Next(2, 4));
                P.PhantomImageTurns = piTurns;
                Console.WriteLine($"  PHANTOM IMAGE! A terrifying false creature appears for {piTurns} turn(s).");
                Console.WriteLine($"  Enemies within {SpellRange(10f):F0}ft must make a morale check or flee!");
                var fearTargets = alive.Where(e => PlayerPos.Feet(e.Position) <= SpellRange(30f) && !(e is Ogre) && !(e is Troll)).ToList();
                foreach (var fe in fearTargets)
                {
                    int fRoll = Rng.Next(1, 7);
                    if (fRoll <= 3) { fe.Fled = true; Console.WriteLine($"  {fe.Name} is terrified and flees!"); }
                    else Console.WriteLine($"  {fe.Name} sees through the illusion (roll {fRoll}).");
                }
                break;
            }
            case "Negative Touch":
            {
                if (!alive.Where(e => e.Position.IsCardinalAdjacent(PlayerPos)).Any())
                { Console.WriteLine("  No enemy in melee range for Negative Touch!"); break; }
                var ntTarget = PickTarget(alive.Where(e => e.Position.IsCardinalAdjacent(PlayerPos)).ToList());
                if (ntTarget == null) break;
                int ntAtk = Rng.Next(P.MinAttack, P.MaxAttack + 1) + (P.HasFeat("Elemental") && P.ElementalFocus == "negative" ? 2 : 0);
                int ntDdg = Rng.Next(ntTarget.MinDodge, ntTarget.MaxDodge + 1);
                Console.WriteLine($"  NEGATIVE TOUCH! Roll {ntAtk} vs {ntTarget.Name}'s dodge {ntDdg}.");
                if (ntAtk >= ntDdg)
                {
                    int ntDmg = Rng.Next(2, 5) + Rng.Next(2, 5);
                    ntDmg = SpellDmg(ntDmg, "negative");
                    if (ntTarget.MagicResistant) ntDmg = Math.Max(1, ntDmg / 2);
                    Console.WriteLine($"  NECROTIC HIT! {ntTarget.Name} takes {ntDmg} negative damage! HP:{ntTarget.HP - ntDmg}/{ntTarget.MaxHP}");
                    ntTarget.HP -= ntDmg; ntTarget.HitBySpell = true;
                    if (!ntTarget.Alive) HandleKill(ntTarget);
                }
                else Console.WriteLine("  Negative Touch missed!");
                break;
            }
            case "Raise Dead":
            {
                var corpses = Active.Where(e => !e.Alive && !e.IsPlayerAlly).ToList();
                if (!corpses.Any()) { Console.WriteLine("  No dead enemies to raise!"); break; }
                for (int ci = 0; ci < corpses.Count; ci++) Console.Write($"[{ci+1}]{corpses[ci].Name}  ");
                Console.WriteLine();
                Console.Write("  Raise which: ");
                if (!int.TryParse(Console.ReadLine()?.Trim(), out int rdi) || rdi < 1 || rdi > corpses.Count) { Console.WriteLine("  Invalid."); break; }
                var corpse = corpses[rdi - 1];
                int rdTurns = ExtDur(Rng.Next(2, 10));
                corpse.HP = Math.Max(1, corpse.MaxHP / 2);
                corpse.IsUndead = true; corpse.IsPlayerAlly = true; corpse.AllyTurnsLeft = rdTurns;
                corpse.Fled = false;
                Console.WriteLine($"  RAISE DEAD! {corpse.Name} rises as an undead ally for {rdTurns} turns! HP:{corpse.HP}/{corpse.MaxHP}");
                break;
            }
            case "True Sight":
            {
                int tsTurns = ExtDur(Rng.Next(2, 5) + Rng.Next(2, 5));
                Console.WriteLine("  TRUE SIGHT! Choose stat to boost: [1]attack [2]dodge [3]block [4]parry [5]grapple [6]spell");
                Console.Write("  Stat: ");
                string tsStat = (Console.ReadLine() ?? "1").Trim();
                P.TrueSightStat = tsStat switch { "1" or "attack" => "attack", "2" or "dodge" => "dodge", "3" or "block" => "block", "4" or "parry" => "parry", "5" or "grapple" => "grapple", "6" or "spell" => "spell", _ => "attack" };
                Console.Write("  Boost [M]in or Ma[X]? ");
                string tsMinMax = (Console.ReadLine() ?? "x").Trim().ToLower();
                P.TrueSightIsMax = !tsMinMax.StartsWith("m");
                P.TrueSightTurns = tsTurns;
                string which = P.TrueSightIsMax ? "max" : "min";
                Console.WriteLine($"  TRUE SIGHT! +2 to {which} {P.TrueSightStat} rolls for {tsTurns} turns.");
                break;
            }
            case "Enlarge":
            {
                int enlTurns = ExtDur(Rng.Next(2, 4));
                P.EnlargeActive = true; P.EnlargeTurns = enlTurns;
                Console.WriteLine($"  ENLARGE! You grow to massive size for {enlTurns} turns!");
                Console.WriteLine("  Attack, grapple, block, parry, and damage doubled; dodge halved.");
                break;
            }
            case "Mage Shield":
            {
                int msTurns = ExtDur(Rng.Next(2, 4));
                int msBlock = Rng.Next(2, 5) + Rng.Next(2, 5);
                P.MageShieldActive = true; P.MageShieldTurns = msTurns;
                P.MageShieldBlockMin = 1; P.MageShieldBlockMax = msBlock;
                Console.WriteLine($"  MAGE SHIELD! Magical barrier for {msTurns} turns. Auto-blocks spells; +{msBlock} max block.");
                break;
            }
            case "Spellweave Armor":
            {
                int swTurns = ExtDur(Rng.Next(2, 4));
                P.SpellweaveArmorTurns = swTurns;
                Console.WriteLine($"  SPELLWEAVE ARMOR! Magical weave reduces all incoming damage by 2 for {swTurns} turns.");
                break;
            }
            case "Life Drain":
            {
                float ldRange = SpellRange(30f);
                var ldTargets = alive.Where(e => PlayerPos.Feet(e.Position) <= ldRange).ToList();
                if (!ldTargets.Any()) { Console.WriteLine($"  No enemies within Life Drain range ({ldRange:F0}ft)!"); break; }
                var ldTarget = PickTarget(ldTargets);
                if (ldTarget == null) break;
                int ldAtk = SpellAtkRoll() + SpellAtk("negative");
                int ldDdg = Rng.Next(ldTarget.MinDodge, ldTarget.MaxDodge + 1);
                Console.WriteLine($"  LIFE DRAIN! ({PlayerPos.Feet(ldTarget.Position):F0}ft) Roll {ldAtk} vs {ldTarget.Name} dodge {ldDdg}.");
                if (lastSpellFumble) { int selfDmg = Rng.Next(2, 6) + Rng.Next(2, 6); Console.WriteLine($"  SPELL FUMBLE! Life Drain backfires! You take {selfDmg} negative energy! HP:{P.HP - selfDmg}/{P.MaxHP}"); P.HP -= selfDmg; break; }
                if (ldAtk >= ldDdg)
                {
                    int ldDmg = Rng.Next(2, 6) + Rng.Next(2, 6); // 2d5
                    if (lastSpellCrit) { ldDmg *= 2; Console.WriteLine("  SPELL CRITICAL! ×2 drain!"); }
                    ldDmg = SpellDmg(ldDmg, "negative");
                    if (ldTarget.MagicResistant) ldDmg = Math.Max(1, ldDmg / 2);
                    ldTarget.HP -= ldDmg; ldTarget.HitBySpell = true;
                    int ldHeal = Math.Min(ldDmg, P.MaxHP - P.HP);
                    P.HP += ldHeal;
                    Console.WriteLine($"  DRAINED! {ldTarget.Name} loses {ldDmg} HP. You gain {ldHeal} HP. ({P.HP}/{P.MaxHP})");
                    if (!ldTarget.Alive) HandleKill(ldTarget);
                }
                else Console.WriteLine("  Life Drain missed!");
                break;
            }
            case "FrostBurn":
            {
                float fbRange = SpellRange(30f);
                var fbCandidates = alive.Where(e => PlayerPos.Feet(e.Position) <= fbRange).ToList();
                if (!fbCandidates.Any()) { Console.WriteLine($"  No enemies within FrostBurn range ({fbRange:F0}ft)!"); break; }
                var fbTarget = PickTarget(fbCandidates);
                if (fbTarget == null) break;
                int fbAtk = SpellAtkRoll() + SpellAtk("fire");
                int fbDdg = Rng.Next(fbTarget.MinDodge, fbTarget.MaxDodge + 1);
                Console.WriteLine($"  FROSTBURN! ({PlayerPos.Feet(fbTarget.Position):F0}ft) Roll {fbAtk} vs {fbTarget.Name} dodge {fbDdg}.");
                if (lastSpellFumble) { int selfDmg = Rng.Next(2, 5) + Rng.Next(2, 5); Console.WriteLine($"  SPELL FUMBLE! FrostBurn erupts on you! {selfDmg} damage! HP:{P.HP - selfDmg}/{P.MaxHP}"); P.HP -= selfDmg; break; }
                if (fbAtk >= fbDdg)
                {
                    int fireDmg = Rng.Next(2, 5) + Rng.Next(2, 5); // 2d4
                    int frostDmg = Rng.Next(2, 5) + Rng.Next(2, 5); // 2d4
                    if (lastSpellCrit) { fireDmg *= 2; frostDmg *= 2; Console.WriteLine("  SPELL CRITICAL! ×2 fire and frost!"); }
                    int totalDmg = fireDmg + frostDmg;
                    if (fbTarget.MagicResistant) totalDmg = Math.Max(1, totalDmg / 2);
                    fbTarget.HP -= totalDmg; fbTarget.HitBySpell = true;
                    Console.WriteLine($"  HIT! {fireDmg} fire + {frostDmg} frost = {totalDmg} dmg! HP:{fbTarget.HP}/{fbTarget.MaxHP}");
                    LichTouchHeal(totalDmg);
                    if (!fbTarget.Alive) { HandleKill(fbTarget); break; }
                    int fbBurnDmg = Rng.Next(1, 7);
                    fbTarget.BurningDmg = Math.Max(fbTarget.BurningDmg, fbBurnDmg);
                    fbTarget.BurningTurns = 99; // burns until extinguished by rolling on ground
                    fbTarget.FrostBurned = true;
                    fbTarget.HalfMovement = true;
                    fbTarget.HalfMovementTurns = ExtDur(Rng.Next(2, 5));
                    Console.WriteLine($"  {fbTarget.Name} is FROSTBURNED! {fbBurnDmg} fire/turn until they roll out; movement halved for {fbTarget.HalfMovementTurns} turns.");
                }
                else Console.WriteLine("  FrostBurn missed!");
                break;
            }
            case "Lich Touch":
            {
                int ltTurns = ExtDur(Rng.Next(2, 5) + Rng.Next(2, 5)); // 2d4
                P.LichTouchTurns = ltTurns;
                Console.WriteLine($"  LICH TOUCH! For {ltTurns} turns all damage you deal heals you equally. All damage becomes negative energy.");
                break;
            }
        }
    }

    void LichTouchHeal(int dmg)
    {
        if (P.LichTouchTurns > 0 && dmg > 0)
        {
            int heal = Math.Min(dmg, P.MaxHP - P.HP);
            if (heal > 0) { P.HP += heal; Console.WriteLine($"  [Lich Touch] Absorbed {heal} HP! ({P.HP}/{P.MaxHP})"); }
        }
    }

    // ── ENEMY TURN ────────────────────────────────────────────────────────

    void EnemyTurn()
    {
        Console.WriteLine("\n  --- Enemy Turn ---");
        foreach (var e in Active.Where(e => e.Alive).ToList())
        {
            if (!e.Alive) continue;

            // Raised dead ally: attacks nearest living non-ally enemy
            if (e.IsPlayerAlly)
            {
                var allyTargets = Active.Where(a => a.Alive && !a.IsPlayerAlly).ToList();
                if (!allyTargets.Any()) continue;
                var allyTarget = allyTargets.OrderBy(a => e.Position.ManhattanDist(a.Position)).First();
                if (!e.Position.IsCardinalAdjacent(allyTarget.Position))
                {
                    var occ2 = new HashSet<(int,int)>(Active.Where(a => a.Alive && a != e).Select(a => (a.Position.X, a.Position.Y)));
                    var step2 = StepToward(e.Position, allyTarget.Position);
                    if (!occ2.Contains((step2.X, step2.Y))) e.Position = step2;
                }
                if (e.Position.IsCardinalAdjacent(allyTarget.Position))
                {
                    int alAtk = Rng.Next(e.MinAttack, e.MaxAttack + 1);
                    int alDdg = Rng.Next(allyTarget.MinDodge, allyTarget.MaxDodge + 1);
                    Console.WriteLine($"  [ALLY] {e.Name} attacks {allyTarget.Name}! Roll {alAtk} vs dodge {alDdg}.");
                    if (alAtk >= alDdg) { int alDmg = Rng.Next(e.MinDamage, e.MaxDamage + 1); allyTarget.HP -= alDmg; Console.WriteLine($"    HIT for {alDmg}! HP:{allyTarget.HP}/{allyTarget.MaxHP}"); if (!allyTarget.Alive) { GainXP(allyTarget.XPValue); Console.WriteLine($"    {allyTarget.Name} slain by your undead ally!"); } }
                    else Console.WriteLine("    MISS!");
                }
                continue;
            }

            // Charmed: attack another enemy
            if (e.Charmed)
            {
                var others = Active.Where(x => x.Alive && x != e).ToList();
                if (others.Any())
                {
                    var victim = others[Rng.Next(others.Count)];
                    int dmg = Rng.Next(e.MinDamage, e.MaxDamage + 1);
                    victim.HP -= dmg;
                    Console.WriteLine($"  {e.Name} (charmed) attacks {victim.Name} for {dmg}! HP:{victim.HP}/{victim.MaxHP}");
                    if (!victim.Alive) { Console.WriteLine($"  {victim.Name} falls to {e.Name}!"); if (!victim.XpAwarded) { victim.XpAwarded = true; GainXP(victim.XPValue); } }
                }
                continue;
            }

            // Sanctuary: the divine ward turns enemies away from the player
            if (P.SanctuaryTurns > 0)
            {
                Console.WriteLine($"  {e.Name} is turned aside by the sanctuary ward around {P.Name}.");
                continue;
            }

            // Hobgoblins: update consecutive-damage counter before acting
            if (e is Hobgoblin)
            {
                if (e.HP < e.HpAtTurnStart) e.ConsecutiveDmgTurns++;
                else e.ConsecutiveDmgTurns = 0;
                if (e.ConsecutiveDmgTurns >= 3) { e.GrappleNextTurn = true; e.ConsecutiveDmgTurns = 0; }
            }

            // Stand up if knocked down
            int actions = (e is Hobgoblin || e is Orc || e is Troll) ? 3 : 2;
            if (e.KnockedDown)
            {
                Console.WriteLine($"  {e.Name} stands up (1 action used).");
                e.KnockedDown = false;
                actions--;
                if (actions <= 0) continue;
            }

            // KO
            if (e.KnockedOut)
            {
                e.KOTurns--;
                Console.WriteLine($"  {e.Name} is knocked out ({Math.Max(0, e.KOTurns)} turns left).");
                if (e.KOTurns <= 0) { e.KnockedOut = false; Console.WriteLine($"  {e.Name} wakes up!"); }
                continue;
            }

            // Retrieve weapon if disarmed
            if (e.Disarmed && e.WeaponPos.HasValue && e.CanMove)
            {
                if (actions >= 2)
                {
                    Console.WriteLine($"  {e.Name} retrieves their weapon.");
                    var rPos = e.WeaponPos.Value;
                    GroundWeapons.RemoveAll(w => w.Pos.SameAs(rPos));
                    e.Disarmed = false; e.WeaponPos = null; actions -= 2;
                }
                else { Console.WriteLine($"  {e.Name} moves toward their weapon."); actions = 0; }
                if (actions <= 0) continue;
            }

            // ── Goblin flee (HP <= 20%) ────────────────────────────────────
            if (!e.HasFledBefore && e is Goblin && e.HP * 5 <= e.MaxHP)
            {
                int fleeRoll = Rng.Next(1, 7);
                if (fleeRoll >= 4)
                {
                    Console.WriteLine($"  {e.Name} tries to flee!");
                    bool stopped = PlayerFreeActionOnFleeingEnemy(e);
                    if (!stopped)
                    {
                        e.HasFledBefore = true;
                        e.Fled = true;
                        var returnedGoblin = new Goblin(Rng, e.Name);
                        Pending.Add((new List<Enemy>
                        {
                            returnedGoblin,
                            Goblin.RandType(Rng, "Goblin Reinforcement A"),
                            Goblin.RandType(Rng, "Goblin Reinforcement B")
                        }, 2));
                    }
                    continue;
                }
            }

            // ── Hobgoblin AI ───────────────────────────────────────────────
            if (e is Hobgoblin)
            {
                // HP <= 20%: all types try to flee
                if (e.HP * 5 <= e.MaxHP && !e.HasFledBefore)
                {
                    Console.WriteLine($"  {e.Name} is badly wounded and tries to flee!");
                    bool stopped = PlayerFreeActionOnFleeingEnemy(e);
                    if (!stopped)
                    {
                        e.HasFledBefore = true;
                        e.Fled = true;
                        int healAmount = Rng.Next(1, 5);
                        var returnedHob = Hobgoblin.RandType(Rng, e.Name);
                        returnedHob.HP = healAmount;
                        Pending.Add((new List<Enemy> { returnedHob, Hobgoblin.RandType(Rng, "Hobgoblin Reinforcement") }, 2));
                        Console.WriteLine($"  {e.Name} escapes! Will return healed with a friend.");
                    }
                    continue;
                }

                // ── HobgoblinFighter AI ─────────────────────────────────────
                if (e is HobgoblinFighter hbf)
                {
                    float hbfFeet = hbf.Position.Feet(PlayerPos);
                    if (hbf.ArrowCount > 0 && hbfFeet > 5f)
                    {
                        DoHobgoblinFighterBowShot(hbf, hbfFeet);
                        actions--;
                        // Use remaining actions to close in if still far
                        if (actions > 0) MoveTowardPlayer(e, ref actions, suppressCost: true);
                    }
                    else
                    {
                        if (e.GrappleNextTurn && actions > 0)
                        {
                            e.GrappleNextTurn = false;
                            int gAtk = Rng.Next(e.MinGrapple, e.MaxGrapple + 1) - e.AttackPenalty;
                            int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1);
                            Console.WriteLine($"  {e.Name} grapples! {gAtk} vs your dodge {pDdg}.");
                            if (gAtk >= pDdg) { int gDmg = Rng.Next(e.GrappleDmgMin, e.GrappleDmgMax + 1); P.HP -= gDmg; Console.WriteLine($"  Grappled! {gDmg} crush damage. HP:{P.HP}/{P.MaxHP}"); }
                            else Console.WriteLine($"  Grapple attempt failed!");
                            actions--;
                        }
                        MoveTowardPlayer(e, ref actions);
                        for (int i = 0; i < actions && P.HP > 0; i++) EnemyAttack(e);
                    }
                    continue;
                }

                // ── HobgoblinThief AI ───────────────────────────────────────
                if (e is HobgoblinThief hbt)
                {
                    float hbtFeet = hbt.Position.Feet(PlayerPos);
                    if (hbt.DaggerCount > 0 && hbtFeet > 5f && hbtFeet <= 20f)
                    {
                        // Throw daggers from range, use each action
                        for (int i = 0; i < actions && hbt.DaggerCount > 0 && hbt.Position.Feet(PlayerPos) > 5f && P.HP > 0; i++)
                            DoHobgoblinThiefDaggerThrow(hbt);
                    }
                    else if (hbt.DaggerCount > 0 && hbtFeet > 20f)
                    {
                        // Move closer to get in throwing range
                        MoveTowardPlayer(e, ref actions);
                        if (hbt.DaggerCount > 0 && actions > 0 && hbt.Position.Feet(PlayerPos) <= 20f)
                            DoHobgoblinThiefDaggerThrow(hbt);
                    }
                    else
                    {
                        // In melee range or out of daggers — attack
                        MoveTowardPlayer(e, ref actions);
                        for (int i = 0; i < actions && P.HP > 0; i++) EnemyAttack(e);
                    }
                    continue;
                }

                // ── HobgoblinCleric AI ──────────────────────────────────────
                if (e is HobgoblinCleric hbc)
                {
                    var clericAlive = Active.Where(en => en.Alive).ToList();
                    float clericFeet = hbc.Position.Feet(PlayerPos);
                    // Each action: prioritize healing/praying then attack if in range
                    for (int i = 0; i < actions && P.HP > 0; i++)
                        DoHobgoblinClericAction(hbc, clericAlive, clericFeet);
                    continue;
                }

                // ── Regular Hobgoblin AI ────────────────────────────────────
                // Grapple triggered by 3 consecutive turns of taking damage
                if (e.GrappleNextTurn && actions > 0)
                {
                    e.GrappleNextTurn = false;
                    int gAtk = Rng.Next(e.MinGrapple, e.MaxGrapple + 1) - e.AttackPenalty;
                    int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1);
                    Console.WriteLine($"  {e.Name} grapples! {gAtk} vs your dodge {pDdg}.");
                    if (gAtk >= pDdg)
                    {
                        int gDmg = Rng.Next(e.GrappleDmgMin, e.GrappleDmgMax + 1);
                        P.HP -= gDmg;
                        Console.WriteLine($"  Grappled! {gDmg} crush damage. HP:{P.HP}/{P.MaxHP}");
                    }
                    else Console.WriteLine($"  Grapple attempt failed!");
                    actions--;
                }
                MoveTowardPlayer(e, ref actions);
                for (int i = 0; i < actions && P.HP > 0; i++)
                    EnemyAttack(e);

                continue;
            }

            // ── Orc AI ─────────────────────────────────────────────────────
            if (e is Orc)
            {
                // Update consecutive-damage counter (4 turns triggers grapple)
                if (e.HP < e.HpAtTurnStart) e.ConsecutiveDmgTurns++;
                else e.ConsecutiveDmgTurns = 0;
                if (e.ConsecutiveDmgTurns >= 4) { e.GrappleNextTurn = true; e.ConsecutiveDmgTurns = 0; }

                // HP <= 20%: roll 1-2 → run (1) or grapple (2)
                if (e.HP * 5 <= e.MaxHP && !e.HasFledBefore)
                {
                    int roll = Rng.Next(1, 3);
                    if (roll == 1)
                    {
                        Console.WriteLine($"  {e.Name} tries to run!");
                        bool stopped = PlayerFreeActionOnFleeingEnemy(e);
                        if (!stopped)
                        {
                            e.HasFledBefore = true;
                            e.Fled = true;
                            int healAmt = Rng.Next(2, 7);
                            int reinfRoll = Rng.Next(1, 7); // 1-2 = orc, 3-5 = two hobgoblins, 6 = spell goblin
                            var returnedOrc = new Orc(Rng, e.Name);
                            returnedOrc.HP = healAmt;
                            var orcBatch = new List<Enemy> { returnedOrc };
                            if (reinfRoll <= 2)
                            {
                                orcBatch.Add(new Orc(Rng, "Orc Reinforcement"));
                                Console.WriteLine($"  {e.Name} will return with another Orc in 3 turns!");
                            }
                            else if (reinfRoll <= 5)
                            {
                                orcBatch.Add(Hobgoblin.RandType(Rng, "Hobgoblin A")); orcBatch.Add(Hobgoblin.RandType(Rng, "Hobgoblin B"));
                                Console.WriteLine($"  {e.Name} will return with two Hobgoblins in 3 turns!");
                            }
                            else
                            {
                                orcBatch.Add(new SpellGoblin(Rng, "Spell Goblin"));
                                Console.WriteLine($"  {e.Name} will return with a Spell Goblin in 3 turns!");
                            }
                            Pending.Add((orcBatch, 3));
                        }
                        continue;
                    }
                    else
                    {
                        // Grapple attempt
                        OrcGrappleAction(e);
                        actions--;
                        if (actions <= 0) continue;
                    }
                }

                // Grapple triggered by 4 consecutive damage turns
                if (e.GrappleNextTurn && actions > 0)
                {
                    e.GrappleNextTurn = false;
                    OrcGrappleAction(e);
                    actions--;
                }

                // HP >= 6: move if needed, then attack/maintain grapple
                MoveTowardPlayer(e, ref actions);
                for (int i = 0; i < actions && P.HP > 0; i++)
                {
                    if (P.IsGrappled && P.GrappledBy == e)
                        OrcMaintainGrapple(e);
                    else if (e.HP >= 6)
                        EnemyAttack(e);
                }

                continue;
            }

            // ── Orc Barbarian AI ───────────────────────────────────────────
            if (e is OrcBarbarian ob)
            {
                // Spend rage point if HP drops below 10%
                if (ob.OrcRagePoints > 0 && !ob.OrcIsRaging && ob.HP <= ob.MaxHP / 10)
                {
                    ob.OrcRagePoints--;
                    ob.OrcIsRaging = true;
                    ob.MinDamage += Rng.Next(1, 5) + Rng.Next(1, 5); // +2d4 flat to main hand damage
                    ob.MaxDamage += 8;
                    Console.WriteLine($"  {ob.Name} RAGES! Battle fury!");
                }
                if (e.HP < e.HpAtTurnStart) e.ConsecutiveDmgTurns++;
                else e.ConsecutiveDmgTurns = 0;
                if (e.ConsecutiveDmgTurns >= 4) { e.GrappleNextTurn = true; e.ConsecutiveDmgTurns = 0; }

                // Wave 61+: flee and return when critically wounded (HP <= 20%)
                if (_waveNum >= 61 && e.HP * 5 <= e.MaxHP && !e.HasFledBefore)
                {
                    Console.WriteLine($"  {e.Name} tries to flee!");
                    bool stopped = PlayerFreeActionOnFleeingEnemy(e);
                    if (!stopped)
                    {
                        e.HasFledBefore = true;
                        e.Fled = true;
                        int totalHeal = Rng.Next(2, 5) + Rng.Next(2, 5) + Rng.Next(1, 5);
                        var returnedOb = new OrcBarbarian(Rng, e.Name);
                        returnedOb.HP = Math.Min(e.HP + totalHeal, returnedOb.MaxHP);
                        Console.WriteLine($"  {e.Name} escapes! Returns in 2 turns healed {totalHeal} HP.");
                        var batch = new List<Enemy> { returnedOb };
                        int reinfRoll = Rng.Next(1, _waveNum >= 71 ? 7 : 6);
                        switch (reinfRoll)
                        {
                            case 1:
                                batch.Add(new OrcBarbarian(Rng, "Orc Barbarian Backup"));
                                Console.WriteLine($"  ...with another Orc Barbarian!");
                                break;
                            case 2: case 3:
                                batch.Add(new Orc(Rng, "Orc A")); batch.Add(new Orc(Rng, "Orc B"));
                                Console.WriteLine($"  ...with two Orcs!");
                                break;
                            case 4:
                                batch.Add(new Troll(Rng, "Troll Backup"));
                                Console.WriteLine($"  ...with a Troll!");
                                break;
                            case 5:
                                for (int gi = 0; gi < 3; gi++) batch.Add(Hobgoblin.RandType(Rng, $"Hobgoblin {gi + 1}"));
                                Console.WriteLine($"  ...with three Hobgoblins!");
                                break;
                            case 6: // wave 71+ only
                                batch.Add(new NecromancerTroll(Rng, "Necromancer Troll"));
                                Console.WriteLine($"  ...with a Necromancer Troll!");
                                break;
                        }
                        Pending.Add((batch, 2));
                    }
                    continue;
                }

                if (e.GrappleNextTurn && actions > 0)
                {
                    e.GrappleNextTurn = false;
                    OrcGrappleAction(e);
                    actions--;
                }

                MoveTowardPlayer(e, ref actions);
                for (int i = 0; i < actions && P.HP > 0; i++)
                {
                    if (P.IsGrappled && P.GrappledBy == e)
                        OrcMaintainGrapple(e);
                    else if (e.Position.IsCardinalAdjacent(PlayerPos))
                        EnemyAttack(e);
                    else
                    {
                        float obFeet = e.Position.Feet(PlayerPos);
                        if (obFeet <= 20f && ob.HandAxeCount > 0)
                            DoOrcBarbAxeThrow(ob);
                        else
                            MoveTowardPlayer(e, ref actions, suppressCost: true);
                    }
                }
                continue;
            }

            // ── Orc Monk AI ────────────────────────────────────────────────
            if (e is OrcMonk om)
            {
                if (e.HP < e.HpAtTurnStart) e.ConsecutiveDmgTurns++;
                else e.ConsecutiveDmgTurns = 0;
                if (e.ConsecutiveDmgTurns >= 3) { e.GrappleNextTurn = true; e.ConsecutiveDmgTurns = 0; }

                if (om.MartialStyle == "Grappler" && e.GrappleNextTurn && e.Position.IsCardinalAdjacent(PlayerPos))
                {
                    e.GrappleNextTurn = false;
                    OrcGrappleAction(e);
                    actions--;
                }
                MoveTowardPlayer(e, ref actions);
                for (int i = 0; i < actions && P.HP > 0; i++)
                {
                    if (P.IsGrappled && P.GrappledBy == e)
                        OrcMaintainGrapple(e);
                    else
                        EnemyAttack(e);
                }
                continue;
            }

            // ── Orc Priestess AI ───────────────────────────────────────────
            if (e is OrcPriestess op)
            {
                var priestAlive = Active.Where(en => en.Alive).ToList();
                float opFeet = e.Position.Feet(PlayerPos);
                DoOrcPriestessAction(op, priestAlive, opFeet);
                continue;
            }

            // ── Orc Ranger AI ──────────────────────────────────────────────
            if (e is OrcRanger orr)
            {
                float orrFeet = e.Position.Feet(PlayerPos);
                if (orrFeet > 10f && orr.ArrowCount > 0)
                {
                    for (int i = 0; i < actions && P.HP > 0; i++)
                        DoOrcRangerBowShot(orr, orrFeet);
                }
                else
                {
                    MoveTowardPlayer(e, ref actions);
                    for (int i = 0; i < actions && P.HP > 0; i++)
                        EnemyAttack(e);
                }
                continue;
            }

            // ── Necromancer Troll AI ───────────────────────────────────────
            if (e is NecromancerTroll)
            {
                // Troll regeneration
                int nRegen = Rng.Next(2, 5);
                e.HP = Math.Min(e.HP + nRegen, e.MaxHP);
                Console.WriteLine($"  {e.Name} regenerates {nRegen} HP! (HP:{e.HP}/{e.MaxHP})");

                // HP <= 20%: flee and return with an undead army
                if (e.HP * 5 <= e.MaxHP && !e.HasFledBefore)
                {
                    int dieRoll = Rng.Next(1, 5);
                    if (dieRoll == 1)
                    {
                        Console.WriteLine($"  {e.Name} (desperate) grapples!");
                        OrcGrappleAction(e);
                    }
                    else
                    {
                        Console.WriteLine($"  {e.Name} tries to flee!");
                        bool stopped = PlayerFreeActionOnFleeingEnemy(e);
                        if (!stopped)
                        {
                            e.HasFledBefore = true;
                            e.Fled = true;
                            int totalHeal = Rng.Next(2, 5) + Rng.Next(2, 5) + Rng.Next(2, 5);
                            var returnedNecro = new NecromancerTroll(Rng, e.Name);
                            returnedNecro.HP = Math.Min(e.HP + totalHeal, returnedNecro.MaxHP);
                            Console.WriteLine($"  {e.Name} escapes! Returns in 3 turns with an undead army!");
                            var batch = new List<Enemy> { returnedNecro };
                            int companionCount = Rng.Next(1, 4); // 1-3 undead companions
                            for (int ci = 0; ci < companionCount; ci++)
                            {
                                Enemy undead = Rng.Next(3) switch
                                {
                                    0 => MakeUndeadEnemy(new Orc(Rng, $"Undead Orc {ci + 1}")),
                                    1 => MakeUndeadEnemy(new Troll(Rng, $"Undead Troll {ci + 1}")),
                                    _ => MakeUndeadEnemy(new Ogre(Rng, $"Undead Ogre {ci + 1}"))
                                };
                                batch.Add(undead);
                                Console.WriteLine($"  ...with {undead.Name}!");
                            }
                            Pending.Add((batch, 3));
                        }
                    }
                    continue;
                }

                for (int i = 0; i < actions && P.HP > 0; i++)
                {
                    if (e.SpellUsesLeft > 0)
                    {
                        // 1. Raise a nearby corpse (within 20ft) as undead
                        var corpse = Active.FirstOrDefault(c =>
                            c != e && !c.IsUndead && c.HP <= 0 &&
                            c.Position.Feet(e.Position) <= 20f);
                        if (corpse != null) { e.SpellUsesLeft--; RaiseDead(corpse, e); continue; }

                        // 2. Heal an adjacent injured undead with negative energy
                        var woundedUndead = Active.FirstOrDefault(u =>
                            u != e && u.IsUndead && u.HP < u.MaxHP &&
                            u.Position.IsCardinalAdjacent(e.Position));
                        if (woundedUndead != null) { e.SpellUsesLeft--; NecromancerHealUndead(e, woundedUndead); continue; }

                        // 3. Negative touch the player if adjacent
                        if (e.Position.IsCardinalAdjacent(PlayerPos)) { e.SpellUsesLeft--; NecromancerTouchPlayer(e); continue; }
                    }
                    else if (e.Position.IsCardinalAdjacent(PlayerPos))
                    {
                        // Dark power spent — reduced to clawing
                        int ncAtk = Rng.Next(1, 7), ncDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1);
                        Console.WriteLine($"  {e.Name}'s dark power is spent — it claws! Roll {ncAtk} vs dodge {ncDdg}.");
                        if (ncAtk >= ncDdg)
                        {
                            int ncDmg = Rng.Next(1, 5);
                            if (P.ArmorDamageReduction > 0) ncDmg = Math.Max(1, ncDmg - P.ArmorDamageReduction);
                            P.HP -= ncDmg;
                            Console.WriteLine($"  Clawed for {ncDmg}! HP:{P.HP}/{P.MaxHP}");
                        }
                        else Console.WriteLine("  You dodge!");
                        continue;
                    }

                    // 4. Otherwise close in
                    MoveTowardPlayer(e, ref actions, suppressCost: true);
                }
                continue;
            }

            // ── Troll AI ───────────────────────────────────────────────────
            if (e is Troll)
            {
                // Update consecutive-damage counter (4 turns triggers grapple)
                if (e.HP < e.HpAtTurnStart) e.ConsecutiveDmgTurns++;
                else e.ConsecutiveDmgTurns = 0;
                if (e.ConsecutiveDmgTurns >= 4) { e.GrappleNextTurn = true; e.ConsecutiveDmgTurns = 0; }
                // Spell hit also triggers grapple
                if (e.HitBySpell) { e.GrappleNextTurn = true; }

                // Free action: regenerate 2-4 HP (undead trolls do not regenerate)
                if (!e.IsUndead)
                {
                    int regen = Rng.Next(2, 5);
                    e.HP = Math.Min(e.HP + regen, e.MaxHP);
                    Console.WriteLine($"  {e.Name} regenerates {regen} HP! (HP:{e.HP}/{e.MaxHP})");
                }

                // HP <= 20%: roll 1d4 — 1=grapple, 2-4=flee
                if (e.HP * 5 <= e.MaxHP && !e.HasFledBefore)
                {
                    int dieRoll = Rng.Next(1, 5);
                    if (dieRoll == 1)
                    {
                        Console.WriteLine($"  {e.Name} (desperate) grapples!");
                        OrcGrappleAction(e);
                    }
                    else
                    {
                        Console.WriteLine($"  {e.Name} tries to flee!");
                        bool stopped = PlayerFreeActionOnFleeingEnemy(e);
                        if (!stopped)
                        {
                            e.HasFledBefore = true;
                            e.Fled = true;
                            int totalHeal = Rng.Next(2, 5) + Rng.Next(2, 5) + Rng.Next(2, 5) + Rng.Next(1, 5);
                            var returnedTroll = new Troll(Rng, e.Name);
                            returnedTroll.HP = Math.Min(e.HP + totalHeal, returnedTroll.MaxHP);
                            Console.WriteLine($"  {e.Name} escapes! Returns in 3 turns healed {totalHeal} HP.");
                            var batch = new List<Enemy> { returnedTroll };
                            int reinfRoll = Rng.Next(1, 8);
                            switch (reinfRoll)
                            {
                                case 1:
                                    batch.Add(new Troll(Rng, "Troll Backup"));
                                    Console.WriteLine($"  ...with another Troll!");
                                    break;
                                case 2: case 3:
                                    batch.Add(new Orc(Rng, "Orc A")); batch.Add(new Orc(Rng, "Orc B"));
                                    Console.WriteLine($"  ...with two Orcs!");
                                    break;
                                case 4: case 5:
                                    for (int hi = 0; hi < 3; hi++) batch.Add(Hobgoblin.RandType(Rng, $"Hobgoblin {hi+1}"));
                                    Console.WriteLine($"  ...with three Hobgoblins!");
                                    break;
                                case 6:
                                    for (int gi = 0; gi < 5; gi++) batch.Add(Goblin.RandType(Rng, $"Goblin {gi+1}"));
                                    Console.WriteLine($"  ...with five Goblins!");
                                    break;
                                default:
                                    batch.Add(new SpellGoblin(Rng, "Spell Goblin"));
                                    Console.WriteLine($"  ...with a Spell Goblin!");
                                    break;
                            }
                            Pending.Add((batch, 3));
                        }
                    }
                    continue;
                }

                // Troll Priest: dark prayers — heal wounded allies or smite at range
                if (e is TrollPriest tpri && tpri.PrayerUsesLeft > 0 && SilenceTurns <= 0 && actions > 0)
                {
                    var wounded = Active.Where(a => a.Alive && !a.IsPlayerAlly && a != e &&
                                                    a.HP < a.MaxHP && a.Position.Feet(e.Position) <= 30f)
                                        .OrderBy(a => (float)a.HP / a.MaxHP).FirstOrDefault();
                    if (wounded != null)
                    {
                        tpri.PrayerUsesLeft--;
                        int tpHeal = Rng.Next(1, 5) + Rng.Next(1, 5) + Rng.Next(1, 5);
                        wounded.HP = Math.Min(wounded.HP + tpHeal, wounded.MaxHP);
                        Console.WriteLine($"  {e.Name} chants a dark prayer — {wounded.Name} heals {tpHeal}! (HP:{wounded.HP}/{wounded.MaxHP})  [{tpri.PrayerUsesLeft} prayers left]");
                        actions--;
                    }
                    else if (e.Position.Feet(PlayerPos) <= 25f && !e.Position.IsCardinalAdjacent(PlayerPos))
                    {
                        tpri.PrayerUsesLeft--;
                        int smAtk = Rng.Next(2, 11);
                        int smDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1);
                        Console.WriteLine($"  {e.Name} calls down a dark smite! Roll {smAtk} vs your dodge {smDdg}.");
                        if (smAtk >= smDdg)
                        {
                            int smDmg = Rng.Next(1, 5) + Rng.Next(1, 5);
                            if (P.ArmorDamageReduction > 0) smDmg = Math.Max(1, smDmg - P.ArmorDamageReduction);
                            P.HP -= smDmg;
                            Console.WriteLine($"  Dark energy sears you for {smDmg}! HP:{P.HP}/{P.MaxHP}");
                        }
                        else Console.WriteLine("  You dodge the dark bolt!");
                        actions--;
                    }
                }

                // Troll Musician: silences the party's magic, then drums the horde into a frenzy
                if (e is TrollMusician tmus && tmus.SongUsesLeft > 0 && SilenceTurns <= 0 && actions > 0)
                {
                    bool playerHasMagic = P.KnownSpells.Any() || P.CanPray || P.CanSing;
                    if (!tmus.PlayedSilence && playerHasMagic)
                    {
                        tmus.SongUsesLeft--;
                        tmus.PlayedSilence = true;
                        SilenceTurns = Rng.Next(1, 5);
                        Console.WriteLine($"  {e.Name} pounds a crushing rhythm — SILENCE falls for {SilenceTurns} turn(s)!");
                        Console.WriteLine("  No spells, prayers or songs can be used by anyone!");
                        actions--;
                    }
                    else if (!tmus.WarSongActive)
                    {
                        tmus.SongUsesLeft--;
                        tmus.WarSongActive = true;
                        foreach (var al in Active.Where(a => a.Alive && !a.IsPlayerAlly))
                        {
                            al.MinAttack += 1; al.MinDodge += 1;
                            tmus.WarSongTargets.Add(al);
                        }
                        Console.WriteLine($"  {e.Name} beats a war rhythm — the horde fights harder! (+1 attack, +1 dodge while the drummer lives)");
                        actions--;
                    }
                }

                // HP >= 5: grapple if triggered, then attack/throw/move
                if (e.GrappleNextTurn && actions > 0)
                {
                    e.GrappleNextTurn = false;
                    OrcGrappleAction(e);
                    actions--;
                }

                var tr = (Troll)e;
                for (int i = 0; i < actions && P.HP > 0; i++)
                {
                    if (P.IsGrappled && P.GrappledBy == e)
                        OrcMaintainGrapple(e);
                    else if (e.Position.IsCardinalAdjacent(PlayerPos))
                    {
                        EnemyAttack(e);
                        if (e.Alive && P.HP > 0) EnemyKick(e);
                    }
                    else
                    {
                        float trollFeet = e.Position.Feet(PlayerPos);
                        if (trollFeet <= 15f && tr.EquippedAxes > 0)
                            DoTrollAxeThrow(tr, trollFeet);
                        else
                            MoveTowardPlayer(e, ref actions, suppressCost: true);
                    }
                }

                continue;
            }

            // ── Ogre AI ────────────────────────────────────────────────────
            if (e is Ogre)
            {
                // Track consecutive damage turns (5 triggers grapple attempt)
                if (e.HP < e.HpAtTurnStart) e.ConsecutiveDmgTurns++;
                else e.ConsecutiveDmgTurns = 0;
                if (e.ConsecutiveDmgTurns >= 5) { e.GrappleNextTurn = true; e.ConsecutiveDmgTurns = 0; }

                int ogrePct = e.HP * 100 / e.MaxHP;

                // ≤ 20% HP: roll 1d4 per action
                if (ogrePct <= 20 && !e.HasFledBefore)
                {
                    e.PowerAttackMode = false;
                    for (int i = 0; i < actions && P.HP > 0; i++)
                    {
                        int dieRoll = Rng.Next(1, 5);
                        if (dieRoll == 1)
                        {
                            e.DroppedWeapon = true;
                            Console.WriteLine($"  {e.Name} drops their club and grabs with BOTH HANDS!");
                            OgreGrappleAction(e, bothHands: true);
                        }
                        else if (dieRoll == 2)
                        {
                            e.PowerAttackMode = true;
                            EnemyAttack(e);
                        }
                        else
                        {
                            Console.WriteLine($"  {e.Name} tries to flee due to their wounds!");
                            bool stopped = PlayerFreeActionOnFleeingEnemy(e);
                            if (!stopped)
                            {
                                e.HasFledBefore = true;
                                e.Fled = true;
                                int totalHeal = Rng.Next(1, 5) + Rng.Next(1, 5) + Rng.Next(1, 5) + Rng.Next(1, 5);
                                var returnedOgre = new Ogre(Rng, e.Name);
                                returnedOgre.HP = Math.Min(e.HP + totalHeal, returnedOgre.MaxHP);
                                Console.WriteLine($"  {e.Name} escapes! Returns in 4 turns.");
                                var batch = new List<Enemy> { returnedOgre };
                                int reinfRoll = Rng.Next(1, 10);
                                switch (reinfRoll)
                                {
                                    case 1: batch.Add(new Ogre(Rng, "Ogre Backup")); Console.WriteLine("  ...with another Ogre!"); break;
                                    case 2: batch.Add(new Troll(Rng, "Troll A")); batch.Add(new Troll(Rng, "Troll B")); Console.WriteLine("  ...with two Trolls!"); break;
                                    case 3: for (int j = 0; j < 3; j++) batch.Add(new Orc(Rng, $"Orc {j+1}")); Console.WriteLine("  ...with three Orcs!"); break;
                                    case 4: for (int j = 0; j < 4; j++) batch.Add(Hobgoblin.RandType(Rng, $"Hobgoblin {j+1}")); Console.WriteLine("  ...with four Hobgoblins!"); break;
                                    case 5: for (int j = 0; j < 6; j++) batch.Add(Goblin.RandType(Rng, $"Goblin {j+1}")); Console.WriteLine("  ...with six Goblins!"); break;
                                    case 6: for (int j = 0; j < 3; j++) batch.Add(new Troll(Rng, $"Troll {j+1}")); Console.WriteLine("  ...with three Trolls!"); break;
                                    case 7: for (int j = 0; j < 4; j++) batch.Add(new Orc(Rng, $"Orc {j+1}")); Console.WriteLine("  ...with four Orcs!"); break;
                                    case 8: for (int j = 0; j < 5; j++) batch.Add(Hobgoblin.RandType(Rng, $"Hobgoblin {j+1}")); Console.WriteLine("  ...with five Hobgoblins!"); break;
                                    default: batch.Add(new SpellGoblin(Rng, "Spell Goblin")); Console.WriteLine("  ...with a Spell Goblin!"); break;
                                }
                                Pending.Add((batch, 4));
                            }
                            break;
                        }
                    }
                    continue;
                }

                // 10%-24% HP: power attack with club (action 1), grapple with off-hand (action 2)
                if (ogrePct <= 24)
                {
                    e.PowerAttackMode = true;
                    MoveTowardPlayer(e, ref actions);
                    if (actions > 0 && P.HP > 0)
                    {
                        Console.WriteLine($"  {e.Name} winds up for a massive power attack!");
                        EnemyAttack(e);
                        actions--;
                    }
                    if (actions > 0 && P.HP > 0)
                    {
                        Console.WriteLine($"  {e.Name} grabs with their free hand!");
                        OgreGrappleAction(e, bothHands: false);
                    }
                    continue;
                }

                // 25%-49% HP: power attack double tap (can't be blocked or parried)
                if (ogrePct <= 49)
                {
                    e.PowerAttackMode = true;
                    if (e.GrappleNextTurn && actions > 0)
                    {
                        e.GrappleNextTurn = false;
                        OgreGrappleAction(e, bothHands: false);
                        actions--;
                    }
                    MoveTowardPlayer(e, ref actions);
                    for (int i = 0; i < actions && P.HP > 0; i++)
                    {
                        if (P.IsGrappled && P.GrappledBy == e)
                            OgreMaintainGrapple(e);
                        else if (!e.DroppedWeapon && !e.Disarmed && Rng.Next(3) == 0)
                            DoOgreClubSweep(e);
                        else
                            EnemyAttack(e);
                    }
                    continue;
                }

                // ≥ 50% HP: normal double tap; grapple if triggered
                e.PowerAttackMode = false;
                if (e.GrappleNextTurn && actions > 0)
                {
                    e.GrappleNextTurn = false;
                    OgreGrappleAction(e, bothHands: false);
                    actions--;
                }
                MoveTowardPlayer(e, ref actions);
                for (int i = 0; i < actions && P.HP > 0; i++)
                {
                    if (P.IsGrappled && P.GrappledBy == e)
                        OgreMaintainGrapple(e);
                    else if (!e.DroppedWeapon && !e.Disarmed && Rng.Next(3) == 0)
                        DoOgreClubSweep(e);
                    else
                        EnemyAttack(e);
                }
                continue;
            }

            // ── SpellGoblin AI ─────────────────────────────────────────────
            if (e is SpellGoblin sg)
            {
                float sgFeet = e.Position.Feet(PlayerPos);
                if (sgFeet > 25f)
                {
                    MoveTowardPlayer(e, ref actions);
                }
                else
                {
                    for (int i = 0; i < actions && P.HP > 0; i++)
                        DoEnemySpell(sg);
                }
                continue;
            }

            // ── Goblin Shaman AI ───────────────────────────────────────────
            if (e is GoblinShaman shamanE)
            {
                var aliveForPray = Active.Where(x => x.Alive).ToList();
                for (int i = 0; i < actions && P.HP > 0; i++)
                {
                    float gsFeet = e.Position.Feet(PlayerPos);
                    if (e.Position.IsCardinalAdjacent(PlayerPos))
                    {
                        // Back away from player — shaman has no weapon
                        int rdx = e.Position.X - PlayerPos.X;
                        int rdy = e.Position.Y - PlayerPos.Y;
                        int sdx = Math.Abs(rdx) >= Math.Abs(rdy) ? (rdx > 0 ? 1 : -1) : 0;
                        int sdy = Math.Abs(rdx) < Math.Abs(rdy) ? (rdy > 0 ? 1 : -1) : 0;
                        var newPos = new GridPos(Math.Clamp(e.Position.X + sdx, 0, 49), Math.Clamp(e.Position.Y + sdy, 0, 49));
                        var occ = new HashSet<(int, int)>(Active.Where(en => en.Alive && en != e).Select(en => (en.Position.X, en.Position.Y)));
                        occ.Add((PlayerPos.X, PlayerPos.Y));
                        if (!occ.Contains((newPos.X, newPos.Y)))
                        {
                            e.Position = newPos;
                            Console.WriteLine($"  {e.Name} retreats!");
                        }
                        else DoGoblinShamanPray(shamanE, aliveForPray);
                    }
                    else if (gsFeet > 30f)
                        MoveTowardPlayer(e, ref actions, suppressCost: true);
                    else
                        DoGoblinShamanPray(shamanE, aliveForPray);
                }
                continue;
            }

            // ── Goblin Warrior AI ──────────────────────────────────────────
            if (e is GoblinWarrior)
            {
                MoveTowardPlayer(e, ref actions);
                for (int i = 0; i < actions && P.HP > 0; i++)
                    EnemyAttack(e);
                continue;
            }

            // ── Rogue Goblin AI ────────────────────────────────────────────
            if (e is RogueGoblin rg)
            {
                MoveTowardPlayer(e, ref actions);
                for (int i = 0; i < actions && P.HP > 0; i++)
                {
                    float rgFeet = e.Position.Feet(PlayerPos);
                    if (rg.DaggerCount > 2 && rgFeet >= 5f && rgFeet <= 20f)
                        DoRogueGoblinThrow(rg);
                    else if (e.Position.IsCardinalAdjacent(PlayerPos))
                        EnemyAttack(e);
                    else
                        MoveTowardPlayer(e, ref actions, suppressCost: true);
                }
                continue;
            }

            // Normal goblin: move if needed, then attack
            MoveTowardPlayer(e, ref actions);
            for (int i = 0; i < actions && P.HP > 0; i++)
                EnemyAttack(e);
        }
    }

    void OrcGrappleAction(Enemy e)
    {
        if (P.IsGrappled && P.GrappledBy == e)
        {
            OrcMaintainGrapple(e);
        }
        else
        {
            int gAtk = Rng.Next(e.MinGrapple, e.MaxGrapple + 1) - e.AttackPenalty;
            int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1);
            Console.WriteLine($"  {e.Name} goes for a grapple! {gAtk} vs your dodge {pDdg}.");
            if (gAtk >= pDdg)
            {
                P.IsGrappled = true; P.GrappledBy = e;
                Console.WriteLine($"  {e.Name} grabs you!");
                if (P.HeldWeapon != null)
                {
                    var wDrop = RandomAdjacent(PlayerPos);
                    GroundWeapons.Add((wDrop, P.HeldWeapon));
                    Console.WriteLine($"  You drop your {P.HeldWeapon}! ({wDrop.X},{wDrop.Y})");
                    P.HeldWeapon = null;
                }
                if (P.HasFeat("MMA"))
                {
                    P.IsGrappled = false; P.GrappledBy = null;
                    DoMmaFreeAction(Active.Where(e2 => e2.Alive).ToList());
                }
            }
            else Console.WriteLine($"  Grapple missed!");
        }
    }

    void OrcMaintainGrapple(Enemy e)
    {
        int gDmg = Rng.Next(e.GrappleDmgMin, e.GrappleDmgMax + 1);
        P.HP -= gDmg;
        Console.WriteLine($"  {e.Name} crushes you for {gDmg} damage! HP:{P.HP}/{P.MaxHP}");
        int tier = GrappleStyleTier();
        if (tier >= 1)
        {
            int minG = P.MinGrapple + P.GetFeatStacks("Closeliner") + (tier >= 2 ? 1 : 0) + (tier >= 3 ? 1 : 0);
            int pGr = Rng.Next(minG, P.MaxGrapple + 1);
            int eGr = Rng.Next(e.MinGrapple, e.MaxGrapple + 1);
            Console.WriteLine($"  [Grapple Style T{tier}] Break-free on enemy action: your {pGr} vs {e.Name}'s {eGr}.");
            if (pGr >= eGr)
            {
                if (tier >= 4) P.PostGrappleBreakTarget = e;
                P.IsGrappled = false; P.GrappledBy = null;
                Console.WriteLine("  You break free!");
                if (tier == 2) { P.GrappleEscapePrePaid++; Console.WriteLine("  [Tier 2] Costs 1 action next turn."); }
                // tier 3+: free (no cost)
            }
            else Console.WriteLine("  Still held!");
        }
    }

    void DoOgreClubSweep(Enemy e)
    {
        int dx = PlayerPos.X - e.Position.X;
        int dy = PlayerPos.Y - e.Position.Y;
        int sdx, sdy;
        if (Math.Abs(dx) >= Math.Abs(dy)) { sdx = dx > 0 ? 1 : dx < 0 ? -1 : 0; sdy = 0; }
        else { sdx = 0; sdy = dy > 0 ? 1 : dy < 0 ? -1 : 0; }
        Console.WriteLine($"  {e.Name} sweeps the club in a wide arc!");
        var swSquares = new[] { new GridPos(e.Position.X + sdx, e.Position.Y + sdy),
                                new GridPos(e.Position.X + sdx * 2, e.Position.Y + sdy * 2) };
        if (swSquares.Any(sq => sq.SameAs(PlayerPos)))
        {
            int swDmg = Rng.Next(3, 13);
            if (P.Defending) swDmg = Math.Max(1, swDmg / 2);
            if (P.ArmorDamageReduction > 0) swDmg = Math.Max(1, swDmg - P.ArmorDamageReduction);
            Console.WriteLine($"  Club sweep hits you for {swDmg}! HP:{P.HP - swDmg}/{P.MaxHP}");
            P.HP -= swDmg;
        }
        else Console.WriteLine("  Club sweep misses!");
    }

    void OgreGrappleAction(Enemy e, bool bothHands)
    {
        if (P.IsGrappled && P.GrappledBy == e)
        {
            OgreMaintainGrapple(e);
        }
        else
        {
            int gAtk = Rng.Next(e.MinGrapple, e.MaxGrapple + 1) - e.AttackPenalty;
            int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1);
            Console.WriteLine($"  {e.Name} lunges to grapple{(bothHands ? " with both hands" : "")}! {gAtk} vs your dodge {pDdg}.");
            Console.WriteLine($"  (You can only counter-grapple 8-12 or dodge to escape an Ogre's grab!)");
            if (gAtk >= pDdg)
            {
                P.IsGrappled = true; P.GrappledBy = e;
                Console.WriteLine($"  {e.Name} seizes you{(bothHands ? " with crushing force" : "")}!");
                if (P.HeldWeapon != null)
                {
                    var wDrop = RandomAdjacent(PlayerPos);
                    GroundWeapons.Add((wDrop, P.HeldWeapon));
                    Console.WriteLine($"  You drop your {P.HeldWeapon}! ({wDrop.X},{wDrop.Y})");
                    P.HeldWeapon = null;
                }
                if (P.HasFeat("MMA"))
                {
                    P.IsGrappled = false; P.GrappledBy = null;
                    DoMmaFreeAction(Active.Where(e2 => e2.Alive).ToList());
                }
            }
            else Console.WriteLine($"  Ogre's grapple missed!");
        }
    }

    void OgreMaintainGrapple(Enemy e)
    {
        int gDmg = e.DroppedWeapon
            ? Rng.Next(e.GrappleDmgMin * 2, e.GrappleDmgMax * 2 + 1)
            : Rng.Next(e.GrappleDmgMin, e.GrappleDmgMax + 1);
        P.HP -= gDmg;
        Console.WriteLine($"  {e.Name} crushes you for {gDmg} damage! HP:{P.HP}/{P.MaxHP}");
        int tier = GrappleStyleTier();
        int pGrMin = 8 + (tier >= 2 ? 1 : 0) + (tier >= 3 ? 1 : 0);
        int pGr = Rng.Next(pGrMin, 13);
        int eGr = Rng.Next(e.MinGrapple, e.MaxGrapple + 1);
        Console.WriteLine($"  Counter-grapple ({pGrMin}-12): your {pGr} vs {e.Name}'s {eGr}.");
        if (pGr >= eGr)
        {
            if (tier >= 4) P.PostGrappleBreakTarget = e;
            P.IsGrappled = false; P.GrappledBy = null;
            Console.WriteLine("  You break the ogre's grip!");
            if (tier == 2) { P.GrappleEscapePrePaid++; Console.WriteLine("  [Tier 2] Costs 1 action next turn."); }
        }
        else Console.WriteLine("  Still held!");
    }

    void EnemyKick(Enemy e)
    {
        if (!e.HasKick || !e.Alive || P.HP <= 0) return;
        int kAtk = Rng.Next(1, 7);
        int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1);
        Console.WriteLine($"  {e.Name} kicks! Roll {kAtk} vs your dodge {pDdg}.");
        if (kAtk >= pDdg)
        {
            int kDmg = Rng.Next(e.KickDmgMin, e.KickDmgMax + 1);
            if (P.Defending) kDmg = Math.Max(1, kDmg / 2);
            if (P.ArmorDamageReduction > 0) kDmg = Math.Max(1, kDmg - P.ArmorDamageReduction);
            P.HP -= kDmg;
            Console.WriteLine($"  Kick HIT! {kDmg} damage. HP:{P.HP}/{P.MaxHP}");
        }
        else Console.WriteLine("  Kick missed!");
    }

    // Player gets one free action when an enemy tries to flee.
    // Returns true if the enemy was stopped (flee fails), false if they escape.
    bool PlayerFreeActionOnFleeingEnemy(Enemy e)
    {
        float feet = PlayerPos.Feet(e.Position);
        bool canMelee = e.Position.IsCardinalAdjacent(PlayerPos);
        bool canBow = P.HeldWeapon == "Bow" && P.ArrowCount > 0 && feet >= 4f && feet <= 60f;
        bool canWand = P.HeldWeapon == "Wand" && feet >= 20f && feet <= 50f;
        bool canRanged = canBow || canWand;

        if (!canMelee && !canRanged)
        {
            Console.WriteLine($"  {e.Name} is out of range ({feet:F0}ft) — escapes!");
            return false;
        }

        var opts = new List<string>();
        if (canMelee) { opts.Add("[A]ttack"); opts.Add("[G]rapple"); }
        if (canRanged) opts.Add("[R]anged");
        Console.Write($"  Free response! {string.Join(" / ", opts)} on {e.Name}? ");
        string choice = (Console.ReadLine() ?? "").Trim().ToLower();

        bool doGrapple = choice.StartsWith("g") && canMelee;
        bool doRanged = !doGrapple && (choice.StartsWith("r") ? canRanged : !canMelee && canRanged);
        bool doMelee = !doGrapple && !doRanged && canMelee;

        if (doGrapple)
        {
            bool vsOgre = e is Ogre;
            int minG = vsOgre ? 8 : P.MinGrapple + P.GetFeatStacks("Closeliner");
            int maxG = vsOgre ? 12 : P.MaxGrapple;
            int gRoll = Rng.Next(minG, maxG + 1);
            int gDdg = Rng.Next(e.MinDodge, e.MaxDodge + 1) - 2;
            Console.WriteLine($"  Grapple{(vsOgre ? " (counter 8-12)" : "")}: {gRoll} vs {e.Name}'s dodge-2 ({gDdg}).");
            if (gRoll >= gDdg)
            {
                Console.WriteLine($"  You grab {e.Name} — flee stopped!");
                e.Grappled = true;
                return true;
            }
            Console.WriteLine($"  Grapple missed — {e.Name} slips away.");
            return false;
        }
        else if (doRanged)
        {
            if (canBow)
            {
                int dmgMin, dmgMax;
                if (feet <= 14f) { dmgMin = 4; dmgMax = 12; }
                else if (feet <= 45f) { dmgMin = 2; dmgMax = 10; }
                else { dmgMin = 1; dmgMax = 5; }
                int aRoll = Rng.Next(P.MinAttack, P.MaxAttack + 1);
                int aDdg = Rng.Next(e.MinDodge, e.MaxDodge + 1) - 2;
                Console.WriteLine($"  BOW response ({feet:F0}ft, {dmgMin}-{dmgMax} dmg)! Roll {aRoll} vs {e.Name}'s dodge-2 ({aDdg}).");
                P.ArrowCount--;
                Console.WriteLine($"  Arrows remaining: {P.ArrowCount}");
                if (aRoll >= aDdg)
                {
                    int dmg = Rng.Next(dmgMin, dmgMax + 1);
                    dmg = ReduceByToughHide(e, dmg);
                    e.HP -= dmg;
                    e.ArrowsInBody++;
                    Console.WriteLine($"  Arrow HIT! {dmg} dmg — flee stopped! {e.Name} HP:{e.HP}/{e.MaxHP}");
                    if (!e.Alive) HandleKill(e);
                    return true;
                }
                Console.WriteLine($"  Arrow MISS — {e.Name} escapes.");
                return false;
            }
            else // wand
            {
                int aRoll = Rng.Next(P.MinAttack, P.MaxAttack + 1) + P.SpellAttackBonus;
                int aDdg = Rng.Next(e.MinDodge, e.MaxDodge + 1) - 2;
                Console.WriteLine($"  WAND response ({feet:F0}ft, dmg 3-4)! Roll {aRoll} vs {e.Name}'s dodge-2 ({aDdg}).");
                if (aRoll >= aDdg)
                {
                    int dmg = Rng.Next(3, 5);
                    dmg = ReduceByToughHide(e, dmg);
                    e.HP -= dmg;
                    Console.WriteLine($"  Wand HIT! {dmg} dmg — flee stopped! {e.Name} HP:{e.HP}/{e.MaxHP}");
                    if (!e.Alive) HandleKill(e);
                    return true;
                }
                Console.WriteLine($"  Wand MISS — {e.Name} escapes.");
                return false;
            }
        }
        else if (doMelee)
        {
            int aRoll = Rng.Next(P.MinAttack, P.MaxAttack + 1);
            int aDdg = Rng.Next(e.MinDodge, e.MaxDodge + 1) - 2;
            Console.WriteLine($"  Attack: {aRoll} vs {e.Name}'s dodge-2 ({aDdg}).");
            if (aRoll >= aDdg)
            {
                // Ogre arm block on flee: if arm block succeeds, ogre escapes anyway (too big)
                if (e is Ogre && e.HasArmBlock && !e.KnockedDown && !e.KnockedOut && !e.OffBalance)
                {
                    int bRoll = Rng.Next(e.BlockMin, e.BlockMax + 1);
                    Console.WriteLine($"  {e.Name} deflects with their arm! Block roll {bRoll} vs {aRoll}.");
                    if (bRoll >= aRoll)
                    {
                        int glanceDmg = Math.Max(1, Rng.Next(P.MinDamage, P.MaxDamage + 1) / 2);
                        glanceDmg = ReduceByToughHide(e, glanceDmg);
                        e.HP -= glanceDmg;
                        Console.WriteLine($"  ARM BLOCK! Glancing blow {glanceDmg} dmg — too massive to stop! {e.Name} HP:{e.HP}/{e.MaxHP}");
                        return false;
                    }
                    Console.WriteLine($"  Arm block failed!");
                }
                if (!EnemyBlocks(e, aRoll))
                {
                    int dmg = Rng.Next(P.MinDamage, P.MaxDamage + 1);
                    dmg = ReduceByToughHide(e, dmg);
                    e.HP -= dmg;
                    Console.WriteLine($"  HIT! {dmg} dmg — flee stopped! {e.Name} HP:{e.HP}/{e.MaxHP}");
                    if (!e.Alive) HandleKill(e);
                    return true;
                }
            }
            if (aRoll < aDdg) Console.WriteLine($"  Missed — {e.Name} gets away.");
            else Console.WriteLine($"  Blocked — {e.Name} gets away.");
            return false;
        }

        Console.WriteLine($"  {e.Name} escapes!");
        return false;
    }

    void EnemyAttack(Enemy e)
    {
        if (!e.Alive || e.KnockedOut) return;
        if (!e.Position.IsCardinalAdjacent(PlayerPos)) return; // out of melee range
        if (P.InvisibilityTurns > 0) { Console.WriteLine($"  {e.Name} can't see you! (Invisible)"); return; }
        int rawEAtk = Rng.Next(e.MinAttack, e.MaxAttack + 1);
        bool eCrit = rawEAtk == e.MaxAttack;
        bool eFumble = rawEAtk == e.MinAttack;
        int eAtk = rawEAtk - e.AttackPenalty - e.FrostPenalty - e.SprintPenalty;
        e.SprintPenalty = 0;
        if (eFumble && !e.Disarmed)
        {
            Console.WriteLine($"  {e.Name} FUMBLES and drops their weapon!");
            e.Disarmed = true;
            var fumDropPos = RandomAdjacent(e.Position);
            string fumWpType = EnemyWeaponType(e);
            if (fumWpType.Length > 0) GroundWeapons.Add((fumDropPos, fumWpType));
            e.WeaponPos = fumDropPos;
            return;
        }
        if (e.PowerAttackMode) eAtk = Math.Max(1, eAtk - 2); // power attack penalty
        int brokenLegPenalty = P.BrokenLimbs.Count(l => l.Contains("Leg"));
        int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) - P.FrostPenalty - brokenLegPenalty;
        if (P.EnlargeActive) pDdg = Math.Max(1, pDdg / 2);
        if (P.TrueSightTurns > 0 && P.TrueSightStat == "dodge") pDdg += P.TrueSightBonus;
        Console.WriteLine($"  {e.Name} attacks{(e.PowerAttackMode ? " (POWER)" : "")}! Roll {eAtk} vs your dodge {pDdg}.");
        if (eAtk >= pDdg)
        {
            int dmg;
            if (e.Disarmed && e.UnarmedMinDmg > 0)
                dmg = Rng.Next(e.UnarmedMinDmg, e.UnarmedMaxDmg + 1);
            else if (e.Disarmed && e is Ogre ogDisarmed)
                dmg = Rng.Next(ogDisarmed.OffhandMinDmg, ogDisarmed.OffhandMaxDmg + 1);
            else
            {
                dmg = Rng.Next(e.MinDamage, e.MaxDamage + 1);
                if (eCrit) { dmg *= 2; Console.WriteLine($"  CRITICAL HIT! {e.Name} strikes true! (×2)"); }
                if (e.PowerAttackMode) dmg += 4;
                if (e.Disarmed) dmg = Math.Max(1, dmg / 2);
            }
            if (P.Defending) dmg = Math.Max(1, dmg / 2);
            if (P.ArmorDamageReduction > 0) dmg = Math.Max(1, dmg - P.ArmorDamageReduction);
            if (P.SpellweaveArmorTurns > 0) dmg = Math.Max(1, dmg - 2);
            Console.WriteLine($"  HIT! You take {dmg} damage. HP: {P.HP - dmg}/{P.MaxHP}");
            P.HP -= dmg;
            if (P.IsRaging && P.HP < 0) { P.HP = 0; Console.WriteLine("  RAGE keeps you standing!"); }
            // Double Tap off-hand
            if (e.HasDoubleTap && P.HP > 0 && !e.DroppedWeapon)
            {
                int ofAtk = Rng.Next(e.OffhandMinAtk, e.OffhandMaxAtk + 1) - e.AttackPenalty;
                int ofDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1);
                string offhandLabel = e.OffhandNonLethal ? "War Mace (non-lethal)" : "off-hand";
                Console.WriteLine($"  {e.Name} {offhandLabel}! Roll {ofAtk} vs your dodge {ofDdg}.");
                if (ofAtk >= ofDdg)
                {
                    int ofDmg = Rng.Next(e.OffhandMinDmg, e.OffhandMaxDmg + 1);
                    if (P.Defending) ofDmg = Math.Max(1, ofDmg / 2);
                    if (P.ArmorDamageReduction > 0) ofDmg = Math.Max(1, ofDmg - P.ArmorDamageReduction);
                    Console.WriteLine($"  Off-hand HIT! {ofDmg} damage. HP:{P.HP - ofDmg}/{P.MaxHP}");
                    P.HP -= ofDmg;
                    if (P.IsRaging && P.HP < 0) { P.HP = 0; Console.WriteLine("  RAGE keeps you standing!"); }
                    if (e.OffhandNonLethal && ofDmg >= e.OffhandMaxDmg)
                    {
                        string[] limbs = { "Left Arm", "Right Arm", "Left Leg", "Right Leg" };
                        string brokenLimb = limbs[Rng.Next(4)];
                        P.BrokenLimbs.Add(brokenLimb);
                        Console.WriteLine($"  WAR MACE MAX! Your {brokenLimb} is BROKEN! (-1 atk per broken arm, -1 dodge per broken leg)");
                    }
                }
                else Console.WriteLine("  Off-hand miss!");
            }
        }
        else
        {
            Console.WriteLine("  MISS!");
            FreeAttackPrompt("Counter Strike", e);
            if (P.HasFeat("Judo")) JudoPrompt(e);
            if (P.HasFeat("Kehon"))
            {
                Console.Write($"  Kehon! Instant grapple on {e.Name}? (y/n): ");
                if ((Console.ReadLine() ?? "").Trim().ToLower() == "y") DoGrapple(e);
            }
        }
        // Duelist Fencing: counter-attack after any enemy attack
        if (P.HP > 0 && P.CharacterType == "Duelist" && P.DuelistEffectTurns.GetValueOrDefault("Duelist Fencing") > 0 && e.Alive)
        {
            Console.WriteLine($"  [Duelist Fencing] Counter-attack {e.Name}!");
            DoAttack(e);
        }
        // Duelist Fineness: auto-disarm on attack (whether or not it hit)
        if (P.HP > 0 && P.CharacterType == "Duelist" && P.DuelistEffectTurns.GetValueOrDefault("Duelist Fineness") > 0 && e.Alive && !e.Disarmed)
        {
            int fAtk = Rng.Next(P.MinAttack, P.MaxAttack + 1);
            int eAtk2 = Rng.Next(e.MinAttack, e.MaxAttack + 1);
            Console.WriteLine($"  [Duelist Fineness] Disarm attempt: your {fAtk} vs {e.Name}'s {eAtk2}.");
            if (fAtk >= eAtk2)
            {
                var finPos = RandomAdjacent(e.Position);
                e.Disarmed = true; e.WeaponPos = finPos;
                string finWpType = EnemyWeaponType(e);
                if (finWpType.Length > 0) GroundWeapons.Add((finPos, finWpType));
                Console.WriteLine($"  [Duelist Fineness] Disarmed! Weapon at ({finPos.X},{finPos.Y}).");
            }
            else Console.WriteLine("  [Duelist Fineness] Disarm failed.");
        }
    }

    // ── GRID HELPERS ─────────────────────────────────────────────────────

    void PlaceEnemies(List<Enemy> enemies, bool nearEdge)
    {
        var occupied = new HashSet<(int, int)>(Active.Where(e => e.Alive).Select(e => (e.Position.X, e.Position.Y)));
        occupied.Add((PlayerPos.X, PlayerPos.Y));
        foreach (var e in enemies)
        {
            GridPos pos;
            int attempts = 0;
            do
            {
                // Enemies always come from the right side
                int x = Rng.Next(nearEdge ? 44 : 30, 50);
                int y = Rng.Next(1, 49);
                pos = new GridPos(x, y);
                attempts++;
            } while (occupied.Contains((pos.X, pos.Y)) && attempts < 100);
            occupied.Add((pos.X, pos.Y));
            e.Position = pos;
        }
    }

    GridPos StepToward(GridPos from, GridPos target)
    {
        int dx = Math.Sign(target.X - from.X);
        int dy = Math.Sign(target.Y - from.Y);
        int adx = Math.Abs(target.X - from.X);
        int ady = Math.Abs(target.Y - from.Y);
        if (adx == 0) return new GridPos(from.X, Math.Clamp(from.Y + dy, 0, 49));
        if (ady == 0) return new GridPos(Math.Clamp(from.X + dx, 0, 49), from.Y);
        return adx >= ady
            ? new GridPos(Math.Clamp(from.X + dx, 0, 49), from.Y)
            : new GridPos(from.X, Math.Clamp(from.Y + dy, 0, 49));
    }

    void MoveTowardPlayer(Enemy e, ref int actions, bool suppressCost = false)
    {
        if (e.Position.IsCardinalAdjacent(PlayerPos)) return;
        if (e.HalfMovement)
        {
            if (e.HalfMovementBlock) { e.HalfMovementBlock = false; if (!suppressCost) actions--; return; }
            e.HalfMovementBlock = true;
        }
        var occupied = new HashSet<(int, int)>(Active.Where(en => en.Alive && en != e).Select(en => (en.Position.X, en.Position.Y)));
        occupied.Add((PlayerPos.X, PlayerPos.Y));
        bool doSprint = !suppressCost && PlayerPos.ManhattanDist(e.Position) > 8;
        if (doSprint)
        {
            // First step
            var s1 = StepToward(e.Position, PlayerPos);
            if (!occupied.Contains((s1.X, s1.Y))) { occupied.Remove((e.Position.X, e.Position.Y)); e.Position = s1; occupied.Add((e.Position.X, e.Position.Y)); }
            // Second step
            if (!e.Position.IsCardinalAdjacent(PlayerPos))
            {
                var s2 = StepToward(e.Position, PlayerPos);
                if (!occupied.Contains((s2.X, s2.Y))) e.Position = s2;
            }
            e.SprintPenalty = 2;
            Console.WriteLine($"  {e.Name} sprints! (-2 to next action roll)");
        }
        else
        {
            var newPos = StepToward(e.Position, PlayerPos);
            if (!occupied.Contains((newPos.X, newPos.Y))) e.Position = newPos;
        }
        if (!suppressCost) actions--;
        if (e.Position.IsCardinalAdjacent(PlayerPos))
            Console.WriteLine($"  {e.Name} closes to melee range!");
        // Duelist Defence: free attack when enemy enters melee range
        if (P.CharacterType == "Duelist" && P.DuelistEffectTurns.GetValueOrDefault("Duelist Defence") > 0
            && e.Position.IsCardinalAdjacent(PlayerPos))
        {
            Console.WriteLine($"  [Duelist Defence] {e.Name} entered range — free attack!");
            DoAttack(e);
        }
        // Duelist Game: trip attempt when enemy enters range
        if (P.CharacterType == "Duelist" && P.DuelistEffectTurns.GetValueOrDefault("Duelist Game") > 0
            && e.Position.IsCardinalAdjacent(PlayerPos) && e.Alive)
        {
            int gAtk = Rng.Next(P.MinAttack, P.MaxAttack + 1);
            int eDdg = Rng.Next(e.MinDodge, e.MaxDodge + 1);
            Console.WriteLine($"  [Duelist Game] Trip! Roll {gAtk} vs {e.Name}'s dodge {eDdg}.");
            if (gAtk >= eDdg) { e.KnockedDown = true; e.OffBalance = true; Console.WriteLine($"  {e.Name} TRIPPED!"); }
            else Console.WriteLine("  Trip failed.");
        }
    }

    void ShowMap(List<Enemy> alive)
    {
        int hw = 10, hh = 5;
        Console.WriteLine("  Map (@ you  g goblin  s spell-goblin  h hob  o orc  B orc-barb  t troll  O ogre  x axe  w weapon):");
        for (int y = PlayerPos.Y - hh; y <= PlayerPos.Y + hh; y++)
        {
            Console.Write("  ");
            for (int x = PlayerPos.X - hw; x <= PlayerPos.X + hw; x++)
            {
                if (x < 0 || x > 49 || y < 0 || y > 49) { Console.Write('#'); continue; }
                var pos = new GridPos(x, y);
                if (pos.SameAs(PlayerPos)) { Console.Write('@'); continue; }
                bool isAxe = Active.OfType<Troll>().Any(tr => tr.ThrownAxePositions.Any(ap => ap.SameAs(pos)));
                if (isAxe) { Console.Write('x'); continue; }
                var en = alive.FirstOrDefault(a => a.Position.SameAs(pos));
                if (en != null) { Console.Write(EnemyChar(en)); continue; }
                bool isWeapon = GroundWeapons.Any(w => w.Pos.SameAs(pos));
                Console.Write(isWeapon ? 'w' : '.');
            }
            Console.WriteLine();
        }
    }

    char EnemyChar(Enemy e) => e switch
    {
        SpellGoblin => 's',
        Goblin => 'g',
        Hobgoblin => 'h',
        OrcBarbarian => 'B',
        OrcMonk => 'M',
        OrcPriestess => 'P',
        OrcRanger => 'R',
        Orc => 'o',
        Troll => 't',
        Ogre => 'O',
        _ => '?'
    };

    // ── SPELL GOBLIN ENEMY SPELL ──────────────────────────────────────────

    void DoEnemySpell(SpellGoblin sg)
    {
        if (SilenceTurns > 0) { Console.WriteLine($"  {sg.Name} mouths a spell — but the silence smothers it!"); return; }
        if (sg.SpellUsesLeft <= 0)
        {
            Console.WriteLine($"  {sg.Name} is out of magic!");
            if (sg.Position.IsCardinalAdjacent(PlayerPos))
            {
                int cAtk = Rng.Next(1, 7), cDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1);
                Console.WriteLine($"  {sg.Name} claws at you! Roll {cAtk} vs dodge {cDdg}.");
                if (cAtk >= cDdg)
                {
                    int cDmg = Rng.Next(1, 5);
                    if (P.ArmorDamageReduction > 0) cDmg = Math.Max(1, cDmg - P.ArmorDamageReduction);
                    P.HP -= cDmg;
                    Console.WriteLine($"  Clawed for {cDmg}! HP:{P.HP}/{P.MaxHP}");
                }
                else Console.WriteLine("  You dodge!");
            }
            return;
        }
        sg.SpellUsesLeft--;
        int sgRaw = Rng.Next(1, 7); // d6 concentration roll
        bool sgCrit = sgRaw == 6;
        bool sgFumble = sgRaw == 1;
        if (sgFumble)
        {
            int backfire = Rng.Next(4, 13);
            Console.WriteLine($"  {sg.Name} tries to cast {sg.SpellName} but FUMBLES! Backfire deals {backfire} to themselves! HP:{sg.HP - backfire}/{sg.MaxHP}");
            sg.HP -= backfire;
            if (!sg.Alive) { Console.WriteLine($"  {sg.Name} is destroyed by their own magic!"); if (!sg.XpAwarded) { sg.XpAwarded = true; GainXP(sg.XPValue); } }
            return;
        }
        if (sgCrit) Console.WriteLine($"  {sg.Name} channels CRITICAL power into {sg.SpellName}!");
        else Console.WriteLine($"  {sg.Name} casts {sg.SpellName}!");
        int mageShieldAbsorb = 0;
        if (P.MageShieldActive)
        {
            mageShieldAbsorb = Rng.Next(P.MageShieldBlockMin, P.MageShieldBlockMax + 1);
            Console.WriteLine($"  [Mage Shield] Auto-blocks! Absorbs up to {mageShieldAbsorb} damage from the spell.");
        }
        int burnDmg, burnTurns, frostPen, frostTurns;
        switch (sg.SpellName)
        {
            case "Fire Blast":
            {
                // 5×5 area centered on player: hits player + any enemies in Manhattan dist ≤ 1
                int dmg = Math.Max(1, Rng.Next(4, 13) - mageShieldAbsorb);
                if (sgCrit) { dmg *= 2; Console.WriteLine("    CRITICAL! Double fire damage!"); }
                burnDmg = Rng.Next(1, 5); burnTurns = Rng.Next(4, 9);
                Console.WriteLine($"    Fire erupts around you! {dmg} fire damage. HP:{P.HP - dmg}/{P.MaxHP}");
                P.HP -= dmg;
                P.BurningDmg = Math.Max(P.BurningDmg, burnDmg);
                P.BurningTurns = Math.Max(P.BurningTurns, burnTurns);
                Console.WriteLine($"    You are BURNING! ({burnDmg}/turn × {burnTurns} turns)");
                // Friendly fire: nearby enemies also hit
                foreach (var e in Active.Where(e => e.Alive && e != sg && e.Position.ManhattanDist(PlayerPos) <= 1).ToList())
                {
                    int eDmg = Rng.Next(4, 13);
                    if (e.MagicResistant) eDmg = Math.Max(1, eDmg / 2);
                    else if (e.MagicVulnerable) eDmg = (int)(eDmg * 1.5);
                    e.HP -= eDmg; e.HitBySpell = true;
                    Console.WriteLine($"    {e.Name} caught in friendly fire! {eDmg} dmg. HP:{e.HP}/{e.MaxHP}");
                    if (!e.Alive) { Console.WriteLine($"    {e.Name} burns out!"); if (!e.XpAwarded) { e.XpAwarded = true; GainXP(e.XPValue); } }
                }
                break;
            }
            case "Chain Lightning":
            {
                // Hits player first, then jumps to enemies within 2 squares
                int dmg = Math.Max(1, Rng.Next(3, 7) - mageShieldAbsorb);
                if (sgCrit) { dmg *= 2; Console.WriteLine("    CRITICAL! Double lightning damage!"); }
                Console.WriteLine($"    Lightning strikes you for {dmg}! HP:{P.HP - dmg}/{P.MaxHP}");
                P.HP -= dmg;
                var lastPos = PlayerPos;
                var hitSet = new HashSet<Enemy>();
                int jumps = 0;
                Enemy? next = Active.Where(e => e.Alive && e != sg && !hitSet.Contains(e) && e.Position.ManhattanDist(lastPos) <= 2)
                                    .OrderBy(e => e.Position.ManhattanDist(lastPos)).FirstOrDefault();
                while (next != null && jumps < 15)
                {
                    int jDmg = Rng.Next(3, 7);
                    if (next.MagicResistant) jDmg = Math.Max(1, jDmg / 2);
                    else if (next.MagicVulnerable) jDmg = (int)(jDmg * 1.5);
                    next.HP -= jDmg; next.HitBySpell = true; hitSet.Add(next);
                    Console.WriteLine($"    Lightning jumps to {next.Name} for {jDmg}! HP:{next.HP}/{next.MaxHP}");
                    if (!next.Alive) { Console.WriteLine($"    {next.Name} is destroyed!"); if (!next.XpAwarded) { next.XpAwarded = true; GainXP(next.XPValue); } }
                    lastPos = next.Position;
                    next = Active.Where(e => e.Alive && e != sg && !hitSet.Contains(e) && e.Position.ManhattanDist(lastPos) <= 2)
                                 .OrderBy(e => e.Position.ManhattanDist(lastPos)).FirstOrDefault();
                    jumps++;
                }
                break;
            }
            case "Frost Burst":
            {
                // 7.5×7.5 cone aimed at player: all in 3×3 around player hit
                int dmg = Math.Max(1, Rng.Next(2, 9) - mageShieldAbsorb);
                if (sgCrit) { dmg *= 2; Console.WriteLine("    CRITICAL! Double frost damage!"); }
                frostPen = Rng.Next(2, 9); frostTurns = Rng.Next(2, 7);
                Console.WriteLine($"    Frost cone! {dmg} cold damage. HP:{P.HP - dmg}/{P.MaxHP}");
                P.HP -= dmg;
                P.FrostPenalty = Math.Max(P.FrostPenalty, frostPen);
                P.FrostTurns = Math.Max(P.FrostTurns, frostTurns);
                Console.WriteLine($"    You are FROZEN! (-{frostPen} dodge for {frostTurns} turns)");
                foreach (var e in Active.Where(e => e.Alive && e != sg &&
                    Math.Abs(e.Position.X - PlayerPos.X) <= 1 && Math.Abs(e.Position.Y - PlayerPos.Y) <= 1).ToList())
                {
                    int eDmg = Rng.Next(2, 9);
                    if (e.MagicResistant) eDmg = Math.Max(1, eDmg / 2);
                    else if (e.MagicVulnerable) eDmg = (int)(eDmg * 1.5);
                    e.HP -= eDmg; e.HitBySpell = true;
                    Console.WriteLine($"    {e.Name} caught in frost! {eDmg} dmg. HP:{e.HP}/{e.MaxHP}");
                    if (!e.Alive) { Console.WriteLine($"    {e.Name} freezes solid!"); if (!e.XpAwarded) { e.XpAwarded = true; GainXP(e.XPValue); } }
                }
                break;
            }
        }
    }

    // ── ORC BARBARIAN AXE THROW ───────────────────────────────────────────

    void DoOrcBarbAxeThrow(OrcBarbarian ob)
    {
        float feet = ob.Position.Feet(PlayerPos);
        int atkRoll = Rng.Next(ob.MinAttack, ob.MaxAttack + 1) - ob.AttackPenalty;
        int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) - P.FrostPenalty - P.BrokenLimbs.Count(l => l.Contains("Leg"));
        Console.WriteLine($"  {ob.Name} hurls a hand axe! ({feet:F0}ft) Roll {atkRoll} vs your dodge {pDdg}. ({ob.HandAxeCount - 1} axes left)");
        ob.HandAxeCount--;
        if (atkRoll >= pDdg)
        {
            int dmg = Rng.Next(2, 9); // hand axe throw: 2-8
            if (P.Defending) dmg = Math.Max(1, dmg / 2);
            if (P.ArmorDamageReduction > 0) dmg = Math.Max(1, dmg - P.ArmorDamageReduction);
            Console.WriteLine($"  Hand axe HIT! {dmg} damage. HP:{P.HP - dmg}/{P.MaxHP}");
            P.HP -= dmg;
        }
        else Console.WriteLine("  Hand axe MISS!");
        GroundWeapons.Add((RandomAdjacent(PlayerPos), "Hand Axe"));
    }

    // ── ROGUE GOBLIN DAGGER THROW ─────────────────────────────────────────

    void DoRogueGoblinThrow(RogueGoblin rg)
    {
        float feet = rg.Position.Feet(PlayerPos);
        int atkRoll = Rng.Next(rg.MinAttack, rg.MaxAttack + 1) - rg.AttackPenalty;
        int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) - P.FrostPenalty - P.BrokenLimbs.Count(l => l.Contains("Leg"));
        rg.DaggerCount--;
        int daggersLeft = rg.DaggerCount;
        Console.WriteLine($"  {rg.Name} hurls a dagger! ({feet:F0}ft) Roll {atkRoll} vs your dodge {pDdg}. ({daggersLeft} daggers left)");
        if (atkRoll >= pDdg)
        {
            int dmg = Rng.Next(rg.MinDamage, rg.MaxDamage + 1);
            if (P.Defending) dmg = Math.Max(1, dmg / 2);
            if (P.ArmorDamageReduction > 0) dmg = Math.Max(1, dmg - P.ArmorDamageReduction);
            Console.WriteLine($"  Dagger HIT! {dmg} damage. HP:{P.HP - dmg}/{P.MaxHP}");
            P.HP -= dmg;
        }
        else Console.WriteLine("  Dagger MISS!");
        if (daggersLeft == 2)
            Console.WriteLine($"  {rg.Name} draws two daggers — switching to dual wield!");
        GroundWeapons.Add((RandomAdjacent(PlayerPos), "Goblin Dagger"));
    }

    // ── GOBLIN SHAMAN PRAYERS ─────────────────────────────────────────────

    void DoGoblinShamanPray(GoblinShaman gs, List<Enemy> allAlive)
    {
        if (SilenceTurns > 0) { Console.WriteLine($"  {gs.Name} tries to chant — but the silence smothers the prayer!"); return; }
        if (gs.PrayerUsesLeft <= 0) { Console.WriteLine($"  {gs.Name} has no prayers left — it cowers!"); return; }
        gs.PrayerUsesLeft--;
        float feet = gs.Position.Feet(PlayerPos);

        // Lord's Prayer: player within 6ft — 1d6 holy damage
        if (feet <= 6f)
        {
            int dmg = Rng.Next(1, 7);
            Console.WriteLine($"  {gs.Name} chants Lord's Prayer! {dmg} holy damage. HP:{P.HP - dmg}/{P.MaxHP}");
            P.HP -= dmg;
            return;
        }

        // Forgiveness: player at ≤25% HP and within 30ft — roll 2d4 vs player HP
        if (P.HP * 4 <= P.MaxHP && feet <= 30f)
        {
            int roll = Rng.Next(1, 5) + Rng.Next(1, 5);
            Console.WriteLine($"  {gs.Name} prays Forgiveness! Roll {roll} vs your HP {P.HP}.");
            if (roll >= P.HP)
            {
                Console.WriteLine($"  Your will to fight breaks — you are forced to flee!");
                PlayerFled = true;
            }
            else Console.WriteLine($"  You resist the prayer.");
            return;
        }

        // Prayer of Healing: most wounded goblin ally within 25ft
        var wounded = allAlive
            .Where(a => a != gs && a is Goblin && a.HP < a.MaxHP && gs.Position.Feet(a.Position) <= 25f)
            .OrderBy(a => (float)a.HP / a.MaxHP)
            .FirstOrDefault();
        if (wounded != null)
        {
            int heal = Rng.Next(1, 7);
            int actual = Math.Min(heal, wounded.MaxHP - wounded.HP);
            wounded.HP += actual;
            Console.WriteLine($"  {gs.Name} prays for {wounded.Name}! +{actual} HP. ({wounded.HP}/{wounded.MaxHP})");
            return;
        }

        // Heal self if injured
        if (gs.HP < gs.MaxHP)
        {
            int heal = Rng.Next(1, 7);
            int actual = Math.Min(heal, gs.MaxHP - gs.HP);
            gs.HP += actual;
            Console.WriteLine($"  {gs.Name} prays for self! +{actual} HP. ({gs.HP}/{gs.MaxHP})");
            return;
        }

        Console.WriteLine($"  {gs.Name} chants quietly...");
    }

    // ── HOBGOBLIN FIGHTER BOW SHOT ────────────────────────────────────────

    void DoHobgoblinFighterBowShot(HobgoblinFighter hbf, float feet)
    {
        if (hbf.ArrowCount <= 0) return;
        int atkRoll = Rng.Next(hbf.MinAttack, hbf.MaxAttack + 1) - hbf.AttackPenalty;
        int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) - P.FrostPenalty;
        int dmgMin, dmgMax;
        if (feet <= 14f) { dmgMin = 3; dmgMax = 8; }
        else if (feet <= 40f) { dmgMin = 2; dmgMax = 6; }
        else { dmgMin = 1; dmgMax = 4; }
        Console.WriteLine($"  {hbf.Name} fires bow! ({feet:F0}ft, dmg {dmgMin}-{dmgMax}) Roll {atkRoll} vs your dodge {pDdg}.");
        hbf.ArrowCount--;
        if (atkRoll >= pDdg)
        {
            int dmg = Rng.Next(dmgMin, dmgMax + 1);
            if (P.Defending) dmg = Math.Max(1, dmg / 2);
            if (P.ArmorDamageReduction > 0) dmg = Math.Max(1, dmg - P.ArmorDamageReduction);
            Console.WriteLine($"  Arrow HIT! {dmg} damage. HP:{P.HP - dmg}/{P.MaxHP}");
            P.HP -= dmg;
        }
        else Console.WriteLine("  Arrow MISS!");
        Console.WriteLine($"  ({hbf.ArrowCount} arrows left)");
    }

    // ── HOBGOBLIN THIEF DAGGER THROW ─────────────────────────────────────

    void DoHobgoblinThiefDaggerThrow(HobgoblinThief hbt)
    {
        float feet = hbt.Position.Feet(PlayerPos);
        int atkRoll = Rng.Next(hbt.MinAttack, hbt.MaxAttack + 1) - hbt.AttackPenalty;
        int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) - P.FrostPenalty;
        hbt.DaggerCount--;
        Console.WriteLine($"  {hbt.Name} hurls a dagger! ({feet:F0}ft) Roll {atkRoll} vs your dodge {pDdg}. ({hbt.DaggerCount} daggers left)");
        if (atkRoll >= pDdg)
        {
            int dmg = Rng.Next(1, 7);
            if (P.Defending) dmg = Math.Max(1, dmg / 2);
            if (P.ArmorDamageReduction > 0) dmg = Math.Max(1, dmg - P.ArmorDamageReduction);
            Console.WriteLine($"  Dagger HIT! {dmg} damage. HP:{P.HP - dmg}/{P.MaxHP}");
            P.HP -= dmg;
        }
        else Console.WriteLine("  Dagger MISS!");
    }

    // ── HOBGOBLIN CLERIC ACTION ────────────────────────────────────────────

    void DoHobgoblinClericAction(HobgoblinCleric hbc, List<Enemy> allAlive, float feet)
    {
        // Forgiveness: player at ≤25% HP and within 30ft
        if (P.HP * 4 <= P.MaxHP && feet <= 30f)
        {
            int roll = Rng.Next(1, 5) + Rng.Next(1, 5);
            Console.WriteLine($"  {hbc.Name} prays Forgiveness (True Sight)! Roll {roll} vs your HP {P.HP}.");
            if (roll >= P.HP) { Console.WriteLine("  Your will breaks — you flee!"); PlayerFled = true; }
            else Console.WriteLine("  You resist the prayer.");
            return;
        }

        // Lord's Prayer: player within 6ft — 1d6 holy damage
        if (feet <= 6f)
        {
            int dmg = Rng.Next(1, 7);
            Console.WriteLine($"  {hbc.Name} chants Lord's Prayer! {dmg} holy damage. HP:{P.HP - dmg}/{P.MaxHP}");
            P.HP -= dmg;
            return;
        }

        // Prayer of Healing: most wounded Hobgoblin ally within 30ft
        var wounded = allAlive
            .Where(a => a != hbc && a is Hobgoblin && a.HP < a.MaxHP && hbc.Position.Feet(a.Position) <= 30f)
            .OrderBy(a => (float)a.HP / a.MaxHP)
            .FirstOrDefault();
        if (wounded != null)
        {
            int heal = Rng.Next(1, 9); // 1d8 (mace doubles as holy conduit)
            int actual = Math.Min(heal, wounded.MaxHP - wounded.HP);
            wounded.HP += actual;
            Console.WriteLine($"  {hbc.Name} lays hands on {wounded.Name}! +{actual} HP ({wounded.HP}/{wounded.MaxHP})");
            return;
        }

        // Heal self if injured
        if (hbc.HP < hbc.MaxHP)
        {
            int heal = Rng.Next(1, 9);
            int actual = Math.Min(heal, hbc.MaxHP - hbc.HP);
            hbc.HP += actual;
            Console.WriteLine($"  {hbc.Name} prays for self! +{actual} HP ({hbc.HP}/{hbc.MaxHP})");
            return;
        }

        // Move and attack if nothing else to do
        int stubActions = 1;
        MoveTowardPlayer(hbc, ref stubActions);
        if (hbc.Position.IsCardinalAdjacent(PlayerPos) && P.HP > 0) EnemyAttack(hbc);
    }

    // ── ORC PRIESTESS ACTION ─────────────────────────────────────────────

    void DoOrcPriestessAction(OrcPriestess op, List<Enemy> allAlive, float feet)
    {
        // Prayer of Forgiveness: player ≤25% HP and within 30ft
        if (P.HP * 4 <= P.MaxHP && feet <= 30f)
        {
            int roll = Rng.Next(1, 5) + Rng.Next(1, 5);
            Console.WriteLine($"  {op.Name} prays Forgiveness! Roll {roll} vs your HP {P.HP}.");
            if (roll >= P.HP) { Console.WriteLine("  Your will breaks — you flee!"); PlayerFled = true; }
            else Console.WriteLine("  You resist the prayer.");
            return;
        }

        // Lord's Prayer: within 6ft — 1d6 holy damage
        if (feet <= 6f)
        {
            int dmg = Rng.Next(1, 7);
            if (op.HasHolyRoller && dmg == 6) { int bonus = Rng.Next(1, 7); dmg += bonus; Console.WriteLine($"  HOLY ROLLER! +{bonus} divine surge!"); }
            Console.WriteLine($"  {op.Name} chants Lord's Prayer! {dmg} holy damage. HP:{P.HP - dmg}/{P.MaxHP}");
            P.HP -= dmg;
            return;
        }

        // Heal most-wounded Orc ally within 30ft
        var orcWounded = allAlive
            .Where(a => a != op && (a is Orc || a is OrcMonk || a is OrcRanger || a is OrcBarbarian) && a.HP < a.MaxHP && op.Position.Feet(a.Position) <= 30f)
            .OrderBy(a => (float)a.HP / a.MaxHP)
            .FirstOrDefault();
        if (orcWounded != null)
        {
            int heal = Rng.Next(1, 9);
            if (op.HasHolyRoller && heal == 8) { int bonus = Rng.Next(1, 5); heal += bonus; Console.WriteLine($"  HOLY ROLLER! +{bonus} divine surge!"); }
            int actual = Math.Min(heal, orcWounded.MaxHP - orcWounded.HP);
            orcWounded.HP += actual;
            Console.WriteLine($"  {op.Name} lays hands on {orcWounded.Name}! +{actual} HP ({orcWounded.HP}/{orcWounded.MaxHP})");
            return;
        }

        // Heal self if injured
        if (op.HP < op.MaxHP)
        {
            int heal = Rng.Next(1, 9);
            if (op.HasHolyRoller && heal == 8) { int bonus = Rng.Next(1, 5); heal += bonus; Console.WriteLine($"  HOLY ROLLER! +{bonus} divine surge!"); }
            int actual = Math.Min(heal, op.MaxHP - op.HP);
            op.HP += actual;
            Console.WriteLine($"  {op.Name} prays for self! +{actual} HP ({op.HP}/{op.MaxHP})");
            return;
        }

        // Move toward player and attack
        int stubAct = 1;
        MoveTowardPlayer(op, ref stubAct);
        if (op.Position.IsCardinalAdjacent(PlayerPos) && P.HP > 0) EnemyAttack(op);
    }

    // ── ORC RANGER BOW SHOT ───────────────────────────────────────────────

    void DoOrcRangerBowShot(OrcRanger orr, float feet)
    {
        if (orr.ArrowCount <= 0) return;
        int rawAtk = Rng.Next(orr.BowMinAtk, orr.BowMaxAtk + 1) - orr.AttackPenalty;
        bool isCrit = (rawAtk + orr.AttackPenalty) == orr.BowMaxAtk;
        bool isFumble = (rawAtk + orr.AttackPenalty) == orr.BowMinAtk;
        int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) - P.FrostPenalty;
        Console.WriteLine($"  {orr.Name} draws long bow! ({feet:F0}ft, dmg {orr.BowMinDmg}-{orr.BowMaxDmg}) Roll {rawAtk} vs your dodge {pDdg}.");
        orr.ArrowCount--;
        if (isFumble) { Console.WriteLine($"  FUMBLE! {orr.Name}'s bow string snaps!"); return; }
        if (rawAtk >= pDdg)
        {
            int dmg = Rng.Next(orr.BowMinDmg, orr.BowMaxDmg + 1);
            if (isCrit) { dmg *= 2; Console.WriteLine($"  CRITICAL! Arrow strikes true!"); }
            if (P.Defending) dmg = Math.Max(1, dmg / 2);
            if (P.ArmorDamageReduction > 0) dmg = Math.Max(1, dmg - P.ArmorDamageReduction);
            Console.WriteLine($"  Arrow HIT! {dmg} damage. HP:{P.HP - dmg}/{P.MaxHP}");
            P.HP -= dmg;
            // Double Tap: second arrow
            if (orr.HasDoubleTap && orr.ArrowCount > 0 && P.HP > 0)
            {
                int raw2 = Rng.Next(orr.BowMinAtk, orr.BowMaxAtk + 1);
                int pDdg2 = Rng.Next(P.MinDodge, P.MaxDodge + 1);
                Console.WriteLine($"  {orr.Name} double-taps! Roll {raw2} vs dodge {pDdg2}.");
                orr.ArrowCount--;
                if (raw2 >= pDdg2)
                {
                    int dmg2 = Rng.Next(orr.BowMinDmg, orr.BowMaxDmg + 1);
                    if (raw2 == orr.BowMaxAtk) { dmg2 *= 2; Console.WriteLine($"  CRITICAL second arrow!"); }
                    if (P.ArmorDamageReduction > 0) dmg2 = Math.Max(1, dmg2 - P.ArmorDamageReduction);
                    Console.WriteLine($"  Second arrow HIT! {dmg2} damage. HP:{P.HP - dmg2}/{P.MaxHP}");
                    P.HP -= dmg2;
                }
                else Console.WriteLine("  Second arrow MISS!");
            }
        }
        else Console.WriteLine("  Arrow MISS!");
    }

    // ── TROLL AXE THROW ───────────────────────────────────────────────────

    void DoTrollAxeThrow(Troll tr, float feet)
    {
        int atkRoll = Rng.Next(tr.MinAttack, tr.MaxAttack + 1) - tr.AttackPenalty;
        int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) - P.FrostPenalty;
        int dmgMin, dmgMax;
        if (feet <= 7.5f) { dmgMin = 2; dmgMax = 6; }
        else if (feet <= 10f) { dmgMin = 1; dmgMax = 6; }
        else { dmgMin = 1; dmgMax = 2; }
        Console.WriteLine($"  {tr.Name} hurls an axe! ({feet:F0}ft, dmg {dmgMin}-{dmgMax}) Roll {atkRoll} vs your dodge {pDdg}.");
        tr.EquippedAxes--;
        if (atkRoll >= pDdg)
        {
            int dmg = Rng.Next(dmgMin, dmgMax + 1);
            if (P.Defending) dmg = Math.Max(1, dmg / 2);
            if (P.ArmorDamageReduction > 0) dmg = Math.Max(1, dmg - P.ArmorDamageReduction);
            Console.WriteLine($"  Axe HIT! {dmg} damage. HP:{P.HP - dmg}/{P.MaxHP}");
            P.HP -= dmg;
            tr.ThrownAxePositions.Add(PlayerPos); // axe near player
        }
        else
        {
            Console.WriteLine("  Axe MISS! Clatters nearby.");
            // Axe lands in a random adjacent square to player
            int[] offX = { 0, 1, 0, -1 };
            int[] offY = { -1, 0, 1, 0 };
            int dir = Rng.Next(4);
            tr.ThrownAxePositions.Add(new GridPos(
                Math.Clamp(PlayerPos.X + offX[dir], 0, 49),
                Math.Clamp(PlayerPos.Y + offY[dir], 0, 49)));
        }
        // Troll picks up axe if adjacent to it (any remaining action will handle it)
        TrollTryPickupAxe(tr);
        // Equip spare if available and equipped < 2
        if (tr.EquippedAxes < 2 && tr.SpareAxes > 0)
        {
            tr.SpareAxes--; tr.EquippedAxes++;
            Console.WriteLine($"  {tr.Name} equips a spare axe. ({tr.EquippedAxes} equipped, {tr.SpareAxes} spare)");
        }
        if (tr.EquippedAxes == 0)
            Console.WriteLine($"  {tr.Name} has no more axes! Unarmed (1-6 dmg).");
    }

    void TrollTryPickupAxe(Troll tr)
    {
        var toPickup = tr.ThrownAxePositions.Where(ap => ap.IsCardinalAdjacent(tr.Position) || ap.SameAs(tr.Position)).ToList();
        foreach (var ap in toPickup)
        {
            tr.ThrownAxePositions.Remove(ap);
            if (tr.SpareAxes < 2) tr.SpareAxes++;
            Console.WriteLine($"  {tr.Name} retrieves an axe. ({tr.SpareAxes} spare)");
            break; // 1 per action
        }
    }
}
