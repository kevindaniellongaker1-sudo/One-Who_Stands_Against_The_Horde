using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

// ═════════════════════════════════════════════════════════════════════════
//  Combat.cs — CombatSession: the heart of a fight. START HERE.
// ═════════════════════════════════════════════════════════════════════════
//
//  CombatSession is ONE class split across six files with `partial` (C#
//  stitches them back together at compile time — there is no runtime cost,
//  and any part can call any other freely). It was a single 6,000-line file;
//  this is the same code, filed by job:
//
//    Combat.cs             this file — fields, constructor, Run()
//    CombatPlayerTurn.cs   PlayerTurn: upkeep, the action menu, each action
//    CombatAttacks.cs      DoAttack/PerformAttack, bows, spells, grapples
//    CombatEnemyAI.cs      EnemyTurn and every creature's behaviour
//    CombatTerrain.cs      the grid, camps, pathing, the map view
//    CombatHelpers.cs      reach, dodge maths, chi, fear, songs, mitigation
//
//  A CombatSession is built per wave and thrown away after, so anything that
//  must outlive the fight belongs on Player (and in SaveGame) — not here.
//
//  THE SHAPE OF A FIGHT:
//    Run()          the round loop — player turns, then EnemyTurn(), until
//                   one side is gone. Returns false if the party died.
//    PlayerTurn()   upkeep (regen, songs, frenzy, fear, countdowns), then
//                   spends actions on choices from BuildOpts()
//    EnemyTurn()    every living enemy acts. Fear is checked FIRST, then
//                   wildlife instinct, then each type's own AI.
//
//  KEY NAMES (shared by all six files):
//    P              the player whose turn it is (swaps in multiplayer)
//    AllPlayers     the whole party
//    Active         enemies on the field  |  Pending  reinforcements
//    PlayerPos      P's square on the 50x50 grid (GridPos; Feet() = dist*2.5)
//    _atkEnemy      who is attacking right now — the size/dodge maths reads
//                   it, so it must be set before any player dodge roll
//
//  HOW AN ATTACK RESOLVES (DoAttack -> PerformAttack):
//    Modifiers stack additively into the attack roll and into dmgBonus:
//    weapon stats, size (SizeRules), core traits (MeleeTraitBonus), high
//    ground, songs, rage, frenzy, chi, Weapon Specialist. Then the target
//    dodges / blocks / parries. When adding a modifier, add it to BOTH the
//    roll and the printed line, so the player can see where the number came
//    from — silent modifiers are how balance bugs hide.
//
//  A WARNING FROM EXPERIENCE:
//    Never let a roll's min and max meet. Rng.Next(min, max+1) with min==max
//    returns that number every single time, which reads as "the dice are
//    broken". Penalties should clamp (see the Rl() helper in SelectRace).
//
// ═════════════════════════════════════════════════════════════════════════

partial class CombatSession
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
    // ── Terrain: camp walls (impassable) + climbable trees and rocks ──
    HashSet<(int X, int Y)> Walls = new();
    HashSet<(int X, int Y)> Trees = new();
    HashSet<(int X, int Y)> Rocks = new();
    HashSet<(int X, int Y)> Caves = new();   // bears den here
    (int X, int Y) _campCenter = (42, 25);
    Enemy? _atkEnemy;   // enemy currently taking its turn (for size-based dodge)
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
        foreach (var pl in allPlayers)
        {
            pl.Climbed = false;
            pl.Frenzied = false;
            pl.FearTurns = 0;
            pl.RecoverRegular = pl.RecoverBlunt = pl.RecoverBarbed = pl.RecoverSpiral = 0;
        }
        GenerateTerrain();
        _displayState = displayState;
        _waveNum = waveNum;
        PlaceEnemies(enemies, nearEdge: false);
        SpawnWildlife();
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
            string d = (GameIO.ReadLine() ?? "").Trim().ToLower();
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
                if (IsWall(nx, ny))
                {
                    Console.WriteLine("  A palisade wall blocks your way!");
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

    // Full layer descriptor for the sprite compositor. Order matters — see
    // GraphicsDisplay.DrawComposite. Every field lowercased & squished to a slug.
    static string SpriteDesc(Player pl)
    {
        static string Slug(string s) => new string((s ?? "").ToLower().Where(char.IsLetterOrDigit).ToArray());
        return string.Join("|", new[]
        {
            Slug(pl.Race), Slug(pl.CharacterType), Slug(pl.Gender),
            Slug(pl.HairColor), Slug(pl.HairLength), Slug(pl.EyeColor),
            Slug(pl.Headwear), Slug(pl.ClothingColor), Slug(pl.FacialHair),
            Slug(pl.HeldWeapon ?? "unarmed"), Slug(pl.MainArmor),
        });
    }

    static string PlayerInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string s = string.Concat(parts.Take(2).Select(w => char.ToUpper(w[0])));
        return s.Length > 0 ? s : "?";
    }

    // 80% of shop value for every weapon still lying on the battlefield
    public long LeftoverGearCopper() => GroundWeapons.Sum(w => Shop.Sell(w.Type));

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
                                        .Select(pl => (pl.Position, PlayerInitials(pl.Name), SpriteDesc(pl)))
                                        .ToList(),
            PlayerSprite = SpriteDesc(P),
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
            Walls = Walls.Select(w => new GridPos(w.X, w.Y)).ToList(),
            Trees = Trees.Select(t => new GridPos(t.X, t.Y)).ToList(),
            Rocks = Rocks.Select(r => new GridPos(r.X, r.Y)).ToList(),
            Caves = Caves.Select(cv => new GridPos(cv.X, cv.Y)).ToList(),
        });
    }

    public bool Run()
    {
        int turnNum = 0;
        while (ActivePlayers.Any(p => p.HP > 0 || p.IsRaging))
        {
            var alive = Active.Where(e => e.Alive).ToList();
            if (!alive.Any(e => !e.IsWildlife && !e.IsPlayerAlly) && !Pending.Any()) break;

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
            if (!alive.Any(e => !e.IsWildlife && !e.IsPlayerAlly) && !Pending.Any()) break;

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
                if (!alive.Any(e => !e.IsWildlife && !e.IsPlayerAlly) && !Pending.Any()) break;
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
            if (!alive.Any(e => !e.IsWildlife && !e.IsPlayerAlly) && !Pending.Any()) break;

            // All alive enemies KO'd (and no reinforcements coming) → party wins
            if (!Pending.Any() && alive.Any(e => !e.IsWildlife && !e.IsPlayerAlly)
                && alive.Where(e => !e.IsWildlife && !e.IsPlayerAlly).All(e => e.KnockedOut))
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
            if (!Pending.Any() && alive.Any(e => !e.IsWildlife && !e.IsPlayerAlly)
                && alive.Where(e => !e.IsWildlife && !e.IsPlayerAlly).All(e => e.KnockedOut))
            {
                Console.WriteLine("\nAll enemies are knocked out! You stand victorious.");
                return true;
            }
        }
        return AllPlayers.Any(p => p.HP > 0);
    }


}
