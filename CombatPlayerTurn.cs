using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

// ═════════════════════════════════════════════════════════════════════════
//  CombatPlayerTurn.cs — one player's turn: upkeep, menu, every action.
// ═════════════════════════════════════════════════════════════════════════
//
//  PlayerTurn() is the longest method in the game. It runs in two halves:
//
//  1. UPKEEP (before you choose anything) — troll regen, rage countdown,
//     sanctuary / redemption / blessing / potion timers, song upkeep and
//     DeathTone pulses, the frenzy trigger, fear countdown. Each effect
//     ticks itself down and announces when it fades.
//
//  2. THE ACTION LOOP — you get 3 actions (+AdditionalActions, +Agility/2).
//     BuildOpts() decides what is offered based on class, feats, gear and
//     state; the big switch runs whatever was chosen. Some choices cost no
//     action and `continue` rather than falling through — chi is the clear
//     example, since spending it is a free action by design.
//
//  CONSTRAINTS THAT REWRITE THE MENU:
//    Sanctuary   warded players may not attack at all
//    Fear        blind fury allows only attacking; panic only fleeing
//    Silence     hides cast / pray / song entirely
//    Grappled    no moving, sprinting or fleeing until you break free
//
//  ADDING AN ACTION: add its name in BuildOpts (gated however it should be)
//  and a matching case in the switch. The graphical UI builds its buttons by
//  parsing the printed "[n]name" list, so it picks up new actions for free.
//
// ═════════════════════════════════════════════════════════════════════════

partial class CombatSession
{
    bool PlayerTurn()
    {
        _atkEnemy = null;
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

        // Boost potion countdown
        if (P.PotionBoostTurns > 0)
        {
            P.PotionBoostTurns--;
            if (P.PotionBoostTurns <= 0) { P.PotionAtkBoost = 0; Console.WriteLine("  [The boost potion wears off]"); }
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

        // Troll frenzy: at a quarter HP or less, go berserk for the rest of combat
        if (P.RaceFrenzy && !P.Frenzied && P.HP > 0 && P.HP * 4 <= P.MaxHP)
        {
            P.Frenzied = true;
            Console.WriteLine("  ⚔ FRENZY! Wounded and wild — melee damage DOUBLES, but -2 to attacks and defenses!");
            RadiateFear("Your frenzy");
        }
        if (P.Frenzied) Console.WriteLine("  [FRENZIED — double melee damage, -2 attack/dodge/block/parry]");

        int actLeft = 3 + P.AdditionalActions;
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
                    string t4 = (GameIO.ReadLine() ?? "").Trim().ToLower();
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
                    if ((GameIO.ReadLine() ?? "").Trim().ToLower().StartsWith("y"))
                        DoAttack(P.GrappledBy);
                }
            }

            var opts = BuildOpts(justBlocked, alive, blockTarget);
            // Fear grips you: blind fury can only swing, panic can only run.
            if (P.FearTurns > 0)
            {
                Console.WriteLine(P.FearFight
                    ? $"  [TERRIFIED — blind fury: you can only attack, {P.FearTurns} turn(s) left]"
                    : $"  [TERRIFIED — panic: you can only flee, {P.FearTurns} turn(s) left]");
                opts = P.FearFight
                    ? opts.Where(o => o is "attack" or "chi").ToList()
                    : opts.Where(o => o is "move" or "sprint" or "run away" or "chi").ToList();
                if (!opts.Any()) { Console.WriteLine("  ...but there is nothing you can do. You cower."); break; }
            }
            for (int i = 0; i < opts.Count; i++) Console.Write($"[{i + 1}]{opts[i]}  ");
            Console.WriteLine();
            Console.Write("  Action: ");
            string raw = (GameIO.ReadLine() ?? "").Trim().ToLower();

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
                // Ranged weapons can target anyone in range; melee and grapple
                // only reach adjacent foes (further with Extended Grasp).
                bool rangedAttack = chosen == "attack" && P.HeldWeapon is "Bow" or "Shortbow" or "Hunting Bow" or "Wand";
                var cands = rangedAttack ? alive : alive.Where(InMeleeReach).ToList();
                if (!cands.Any())
                {
                    Console.WriteLine(chosen == "grapple"
                        ? "  No enemy within reach to grapple — move closer first."
                        : "  No enemy within reach — move closer or use a ranged attack.");
                    continue;
                }
                target = PickTarget(cands);
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

                case "chi":
                    DoChi(alive);
                    continue;   // free action — spends no action

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
                    if (P.Climbed) { Console.WriteLine("  You're up high — climb down (action) or jump down first!"); continue; }
                    int moveRoll = Rng.Next(P.MinMovement, P.MaxMovement + 1) + P.MovementBonus + P.MoveTrait();
                    Console.WriteLine($"  Move roll: {moveRoll} square(s).");
                    StepMovement(moveRoll);
                    justBlocked = false;
                    break;
                }

                case "sprint":
                {
                    if (P.IsGrappled) { Console.WriteLine("  You can't sprint while grappled!"); continue; }
                    if (P.Climbed) { Console.WriteLine("  You're up high — climb down (action) or jump down first!"); continue; }
                    // Sprint is 2d6 (+ movement/sprint bonuses)
                    int sprintRoll = Math.Max(1, Rng.Next(1, 7) + Rng.Next(1 + P.SprintMaxBonus, 7 + P.SprintMaxBonus) + P.MovementBonus + P.SprintBonus + P.SprintTrait());
                    string spNote = P.NoSprintPenalty ? "[no penalty]" : P.DoubleSprintPenalty ? "[-4 to next action]" : "[-2 to next action]";
                    Console.WriteLine($"  SPRINT! {sprintRoll} square(s). {spNote}");
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
                        if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int cti) || cti < 1 || cti > chargeAlive.Count)
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
                        string cAct = (GameIO.ReadLine() ?? "").Trim().ToLower();
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
                    // Only foes in melee reach, or holding a throwable within
                    // 20ft, get a parting shot — distant enemies can't touch you.
                    var reactors = alive.Where(CanReactToFlee).ToList();
                    if (!reactors.Any()) Console.WriteLine("  Nobody is close enough to stop you!");
                    foreach (var e in reactors)
                    {
                        if (blocked) break;
                        bool thrown = e.Position.ManhattanDist(PlayerPos) > 1 && !(e is Ogre && e.Position.ManhattanDist(PlayerPos) <= 2);
                        int eAtk = Rng.Next(e.MinAttack, e.MaxAttack + 1) - e.AttackPenalty;
                        int pDdg = ChiReroll(ref ChiRerollRunAway, Rng.Next(P.MinDodge, P.MaxDodge + 1), P.MinDodge, P.MaxDodge, "escape")
                                   + PDodgeSize() - 2;
                        Console.WriteLine($"  {e.Name} {(thrown ? $"hurls a weapon ({e.Position.Feet(PlayerPos):F0}ft)" : "reacts")}! Roll {eAtk} vs your dodge-2 ({pDdg}).");
                        if (eAtk >= pDdg)
                        {
                            int dmg = Rng.Next(e.MinDamage, e.MaxDamage + 1);
                            if (P.Defending) dmg = Math.Max(1, dmg / 2);
                            dmg = Math.Max(1, dmg - P.ArmorDamageReduction);
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
                    if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int rpts) || rpts < 1 || rpts > maxSpend)
                    { Console.WriteLine("  Invalid."); continue; }
                    P.RagePoints -= rpts;
                    P.RagePointsSpent = rpts;
                    P.IsRaging = true;
                    P.RageTurnsLeft = 3;
                    Console.WriteLine($"  RAGE! +{rpts*2}d4 damage for 3 turns!");
                    RadiateFear("Your rage");
                    justBlocked = false;
                    break;
                }

                case "whirlwind":
                {
                    var wwAlive = Active.Where(e => e.Alive).ToList();
                    if (!wwAlive.Any()) { Console.WriteLine("  No enemies to hit."); continue; }
                    int numHits = 1 + (P.Level >= 2 ? (P.Level - 2) / 3 + 1 : 0);
                    Console.Write($"  Whirlwind ({numHits} swings)! [C]lockwise or [A]nti-clockwise? ");
                    string wwDir = (GameIO.ReadLine() ?? "c").Trim().ToLower();
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
                        int range = (hi >= 4 || P.HasFeat("Extended Grasp")) ? 2 : 1;
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
                    if (int.TryParse(GameIO.ReadLine()?.Trim(), out int si2) && si2 >= 1 && si2 <= P.KnownSpells.Count)
                    {
                        if (P.SpellUses <= 0) { Console.WriteLine("  You have no spell casts left. Rest to recover."); continue; }
                        P.SpellUses--;
                        DoSpell(P.KnownSpells[si2 - 1], alive);
                        if (P.HasFeat("Twin Caster") && P.SpellUses > 0)
                        {
                            Console.Write("  [Twin Caster] Cast a second spell? [y/n]: ");
                            if ((GameIO.ReadLine() ?? "").Trim().ToLower().StartsWith("y"))
                            {
                                Console.WriteLine($"  Known spells (casts left: {P.SpellUses}):");
                                for (int tsi = 0; tsi < P.KnownSpells.Count; tsi++) Console.WriteLine($"  [{tsi+1}] {P.KnownSpells[tsi]}");
                                Console.Write("  Cast which: ");
                                if (int.TryParse(GameIO.ReadLine()?.Trim(), out int tsi2) && tsi2 >= 1 && tsi2 <= P.KnownSpells.Count)
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
                    int bRoll = ChiReroll(ref ChiRerollBlock, Rng.Next(P.MinBlock, P.MaxBlock + 1), P.MinBlock, P.MaxBlock, "block") + SizeRules.BlockParryBonus(P.Race, target!.Race) - (P.Frenzied ? 2 : 0);
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
                    int pRoll = ChiReroll(ref ChiRerollParry, Rng.Next(P.MinParry, P.MaxParry + 1), P.MinParry, P.MaxParry, "parry") + P.ParryTrait() + SizeRules.BlockParryBonus(P.Race, target!.Race) - (P.Frenzied ? 2 : 0);
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
                    string sraw = (GameIO.ReadLine() ?? "").Trim().ToLower();
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
                    P.SongLingerTurns = Rng.Next(1, 5) + P.SongDurBonus;
                    Console.WriteLine($"  ♪ You end {P.ActiveSong}; its echo lingers for {P.SongLingerTurns} turn(s).");
                    continue;   // stopping is a free action
                }

                case "climb":
                {
                    string what = Trees.Contains((PlayerPos.X, PlayerPos.Y)) ? "tree" : "rock";
                    P.Climbed = true;
                    Console.WriteLine($"  You scramble up the {what}! HIGH GROUND: +2 attack rolls, +1 dodge.");
                    Console.WriteLine("  (Climbing down costs an action; jumping down is free but deals 1d4 falling damage.)");
                    justBlocked = false;
                    break;
                }

                case "climb down":
                {
                    P.Climbed = false;
                    Console.WriteLine("  You climb carefully back down.");
                    justBlocked = false;
                    break;
                }

                case "mine rock":
                {
                    int oreGot = Math.Min(Rng.Next(1, 6) - 1, P.PackRoom);
                    P.Ore += oreGot;
                    int stoneGot = Math.Min(Rng.Next(1, 5) + Rng.Next(1, 5) - 1, P.PackRoom);
                    P.Stone += stoneGot;
                    Rocks.Remove((PlayerPos.X, PlayerPos.Y));
                    Console.WriteLine($"  Your pickaxe shatters the rock: +{oreGot} ore, +{stoneGot} stone. (Ore {P.Ore}, Stone {P.Stone})" +
                        (P.PackRoom == 0 ? "  [Pack FULL]" : ""));
                    justBlocked = false;
                    break;
                }

                case "cut tree":
                {
                    int woodGot = Math.Min(Rng.Next(1, 4) + Rng.Next(1, 4) + Rng.Next(1, 4), P.PackRoom);
                    P.Wood += woodGot;
                    Trees.Remove((PlayerPos.X, PlayerPos.Y));
                    Console.WriteLine($"  Your axe fells the tree: +{woodGot} wood. (Wood {P.Wood})" +
                        (P.PackRoom == 0 ? "  [Pack FULL]" : ""));
                    justBlocked = false;
                    break;
                }

                case "jump down":
                {
                    P.Climbed = false;
                    int fall = Rng.Next(1, 5);
                    P.HP -= fall;
                    Console.WriteLine($"  You leap down! {fall} falling damage. HP:{P.HP}/{P.MaxHP}");
                    if (P.IsRaging && P.HP < 0) { P.HP = 0; Console.WriteLine("  RAGE keeps you standing!"); }
                    continue;   // jumping is a free action
                }

                case "brew potion":
                {
                    Console.WriteLine("  Brew: [1] Boost (+1d4/lvl attack, 3 turns)  [2] Heal (1d6/lvl)  [3] Poison (AoE)  [4] Restore (uses)");
                    Console.Write("  Which (brews TWO): ");
                    string bp = (GameIO.ReadLine() ?? "").Trim();
                    switch (bp)
                    {
                        case "1": P.PotionsBoost += 2; Console.WriteLine($"  Brewed 2 Boost potions. ({P.PotionsBoost})"); break;
                        case "2": P.PotionsHeal += 2; Console.WriteLine($"  Brewed 2 Healing potions. ({P.PotionsHeal})"); break;
                        case "3": P.PotionsPoison += 2; Console.WriteLine($"  Brewed 2 Poison flasks. ({P.PotionsPoison})"); break;
                        case "4": P.PotionsRestore += 2; Console.WriteLine($"  Brewed 2 Restoration potions. ({P.PotionsRestore})"); break;
                        default: Console.WriteLine("  The mixture fizzles — nothing brewed."); continue;
                    }
                    justBlocked = false;
                    break;
                }

                case "use potion":
                {
                    var pOpts = new List<string>();
                    if (P.PotionsBoost > 0)   pOpts.Add($"[1] Boost x{P.PotionsBoost}");
                    if (P.PotionsHeal > 0)    pOpts.Add($"[2] Heal x{P.PotionsHeal}");
                    if (P.PotionsPoison > 0)  pOpts.Add($"[3] Poison x{P.PotionsPoison}");
                    if (P.PotionsRestore > 0) pOpts.Add($"[4] Restore x{P.PotionsRestore}");
                    Console.WriteLine($"  Potions: {string.Join("  ", pOpts)}");
                    Console.Write("  Use which: ");
                    string up = (GameIO.ReadLine() ?? "").Trim();
                    if (up == "1" && P.PotionsBoost > 0)
                    {
                        P.PotionsBoost--;
                        int boost = 0; for (int d = 0; d < P.Level; d++) boost += Rng.Next(1, 5);
                        P.PotionAtkBoost = boost; P.PotionBoostTurns = 3;
                        Console.WriteLine($"  You drink the boost potion: +{boost} to attack rolls for 3 turns!");
                    }
                    else if (up == "2" && P.PotionsHeal > 0)
                    {
                        P.PotionsHeal--;
                        int heal = 0; for (int d = 0; d < P.Level; d++) heal += Rng.Next(1, 7);
                        heal = Math.Min(heal, P.MaxHP - P.HP);
                        P.HP += heal;
                        Console.WriteLine($"  You drink the healing potion: +{heal} HP. ({P.HP}/{P.MaxHP})");
                    }
                    else if (up == "3" && P.PotionsPoison > 0)
                    {
                        var inRange = alive.Where(en => PlayerPos.Feet(en.Position) <= 40f).ToList();
                        if (!inRange.Any()) { Console.WriteLine("  No enemy within 40ft to splash!"); continue; }
                        var pt = inRange.Count == 1 ? inRange[0] : PickTarget(inRange);
                        if (pt == null) continue;
                        P.PotionsPoison--;
                        int perTurn = Rng.Next(1, 5) + Math.Max(1, P.Level / 2);
                        var splash = alive.Where(en => Math.Abs(en.Position.X - pt.Position.X) <= 1
                                                    && Math.Abs(en.Position.Y - pt.Position.Y) <= 1).ToList();
                        Console.WriteLine($"  The poison flask SHATTERS on {pt.Name}! {perTurn}/turn poison seeps into {splash.Count} target(s).");
                        foreach (var sp in splash) sp.BleedDmg += perTurn;
                        justBlocked = false;
                        break;
                    }
                    else if (up == "4" && P.PotionsRestore > 0)
                    {
                        P.PotionsRestore--;
                        Console.Write("  Restore: [1] Spells  [2] Rage  [3] Prayers  [4] Duelist points: ");
                        string rp = (GameIO.ReadLine() ?? "").Trim();
                        switch (rp)
                        {
                            case "2": P.RagePoints += P.Level; Console.WriteLine($"  +{P.Level} rage points ({P.RagePoints})."); break;
                            case "3": P.PrayerUses += P.Level; Console.WriteLine($"  +{P.Level} prayer uses ({P.PrayerUses})."); break;
                            case "4": P.DuelistPoints += P.Level; Console.WriteLine($"  +{P.Level} duelist points ({P.DuelistPoints})."); break;
                            default: P.SpellUses += P.Level; Console.WriteLine($"  +{P.Level} spell casts ({P.SpellUses})."); break;
                        }
                    }
                    else continue;
                    justBlocked = false;
                    break;
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
                    string pc = (GameIO.ReadLine() ?? "").Trim();

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
                            if (int.TryParse(GameIO.ReadLine()?.Trim(), out int wi) && wi >= 1 && wi <= living.Count)
                                sancTarget = living[wi - 1];
                        }
                        sancTarget.SanctuaryTurns = Rng.Next(1, 5) + P.PrayerDurBonus;
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
                            if (int.TryParse(GameIO.ReadLine()?.Trim(), out int ri) && ri >= 1 && ri <= living.Count)
                                redTarget = living[ri - 1];
                        }
                        redTarget.ExpireRedemption();   // don't stack with an existing redemption
                        redTarget.RedemptionExtraHP = redTarget.MaxHP;
                        redTarget.MaxHP *= 2;
                        redTarget.HP = redTarget.MaxHP;
                        redTarget.RedemptionTurns = Rng.Next(1, 5) + P.PrayerDurBonus;
                        Console.WriteLine($"  REDEMPTION! {redTarget.Name} is fully healed and their vigor doubles for {redTarget.RedemptionTurns} turn(s)!");
                        Console.WriteLine($"    HP: {redTarget.HP}/{redTarget.MaxHP}");
                        justBlocked = false;
                        break;
                    }
                    if (pc == "8") // ── Prayer of Mass Blessings ──────────
                    {
                        int bless = Rng.Next(1, 5);
                        int blessTurns = Rng.Next(1, 5) + P.PrayerDurBonus;
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
                        for (int d = 0; d < healDice; d++) roll += Rng.Next(1, 7 + P.PrayerHealMaxBonus);

                        // Healing energy harms undead — offer to smite a nearby undead instead of self-heal
                        var undeadTargets = alive.Where(en => en.IsUndead && PlayerPos.Feet(en.Position) <= 25f).ToList();
                        Enemy? smiteTarget = null;
                        if (undeadTargets.Any())
                        {
                            Console.Write($"  Undead within 25ft! [S]mite an undead for {roll} radiant, or [H]eal self? ");
                            if ((GameIO.ReadLine() ?? "").Trim().ToLower().StartsWith("s"))
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
                            if (P.ArmorAbsorbPct > 0)
                            {
                                roll *= 2;
                                Console.WriteLine("  Your rune-worked armor drinks in the prayer — healing doubled!");
                            }
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
                            reviveTarget = int.TryParse((GameIO.ReadLine() ?? "").Trim(), out int ri2) && ri2 >= 1 && ri2 <= fallen.Count
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
                        if ((GameIO.ReadLine() ?? "n").Trim().ToLower().StartsWith("y"))
                        {
                            Console.Write("  Choose prayer: ");
                            string pc2 = (GameIO.ReadLine() ?? "").Trim();
                            if (pc2 == "1")
                            {
                                int roll2 = P.PrayerHealBonus;
                                if (P.HasFeat("Elemental") && P.ElementalFocus == "holy") roll2 += 2;
                                for (int d = 0; d < healDice; d++) roll2 += Rng.Next(1, 7 + P.PrayerHealMaxBonus);
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
                    int thrDdg = Rng.Next(throwTarget.MinDodge, throwTarget.MaxDodge + 1) + SizeDodgeRoll(throwTarget.Race, P.Race) - throwTarget.DodgePenalty;
                    Console.WriteLine($"  Throw {P.HeldWeapon}! ({throwFeet:F0}ft) Roll {thrAtk} vs {throwTarget.Name}'s dodge {thrDdg}.");
                    string thrWeap = P.HeldWeapon!;
                    P.HeldWeapon = null;
                    if (thrAtk >= thrDdg && !EnemyBlocks(throwTarget, thrAtk, isRanged: true))
                    {
                        int thrDmg = Rng.Next(thrDmgMin, thrDmgMax + 1) + SlayerDmg();
                        thrDmg = ReduceByToughHide(throwTarget, thrDmg);
                        Console.WriteLine($"  HIT! {thrDmg} dmg → {throwTarget.Name} HP:{throwTarget.HP - thrDmg}/{throwTarget.MaxHP}");
                        throwTarget.HP -= thrDmg;
                        if (!throwTarget.Alive) HandleKill(throwTarget);
                        ResolveThrownLanding(true, throwTarget, thrWeap, thrDmgMin, thrDmgMax);
                    }
                    else
                    {
                        Console.WriteLine("  MISS!");
                        ResolveThrownLanding(false, throwTarget, thrWeap, thrDmgMin, thrDmgMax);
                    }
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
                    int tdAtk = Rng.Next(P.MinRangedAtk, P.MaxRangedAtk + 1) + SlayerAtk() + HighGround();
                    int tdDdg = Rng.Next(throwTarget.MinDodge, throwTarget.MaxDodge + 1) + SizeDodgeRoll(throwTarget.Race, P.Race) - throwTarget.DodgePenalty;
                    Console.WriteLine($"  Throw dagger! ({thrDaggerFeet:F0}ft) Roll {tdAtk} vs {throwTarget.Name}'s dodge {tdDdg}. ({P.DaggerCount - 1} daggers left)");
                    P.DaggerCount--;
                    if (tdAtk >= tdDdg && !EnemyBlocks(throwTarget, tdAtk, isRanged: true))
                    {
                        int tdDmg = Rng.Next(1, 7) + SlayerDmg();
                        tdDmg = ReduceByToughHide(throwTarget, tdDmg);
                        Console.WriteLine($"  HIT! {tdDmg} dmg → {throwTarget.Name} HP:{throwTarget.HP - tdDmg}/{throwTarget.MaxHP}");
                        throwTarget.HP -= tdDmg;
                        if (!throwTarget.Alive) HandleKill(throwTarget);
                        ResolveThrownLanding(true, throwTarget, "Goblin Dagger", 1, 6);
                    }
                    else
                    {
                        Console.WriteLine("  MISS!");
                        ResolveThrownLanding(false, throwTarget, "Goblin Dagger", 1, 6);
                    }
                    // Double Tap: second dagger throw
                    if (P.HasFeat("Double Tap") && P.DaggerCount > 0 && throwTarget.Alive)
                    {
                        Console.WriteLine("  [Double Tap] Second dagger throw!");
                        int tdAtk2 = Rng.Next(P.MinRangedAtk, P.MaxRangedAtk + 1) + SlayerAtk() + HighGround();
                        int tdDdg2 = Rng.Next(throwTarget.MinDodge, throwTarget.MaxDodge + 1) + SizeDodgeRoll(throwTarget.Race, P.Race) - throwTarget.DodgePenalty;
                        Console.WriteLine($"  Throw dagger! ({thrDaggerFeet:F0}ft) Roll {tdAtk2} vs dodge {tdDdg2}. ({P.DaggerCount - 1} daggers left)");
                        P.DaggerCount--;
                        if (tdAtk2 >= tdDdg2 && !EnemyBlocks(throwTarget, tdAtk2, isRanged: true))
                        {
                            int tdDmg2 = Rng.Next(1, 7) + SlayerDmg();
                            tdDmg2 = ReduceByToughHide(throwTarget, tdDmg2);
                            Console.WriteLine($"  HIT! {tdDmg2} dmg → {throwTarget.Name} HP:{throwTarget.HP - tdDmg2}/{throwTarget.MaxHP}");
                            throwTarget.HP -= tdDmg2;
                            if (!throwTarget.Alive) HandleKill(throwTarget);
                            ResolveThrownLanding(true, throwTarget, "Goblin Dagger", 1, 6);
                        }
                        else
                        {
                            Console.WriteLine("  MISS!");
                            ResolveThrownLanding(false, throwTarget, "Goblin Dagger", 1, 6);
                        }
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
                    int taAtk = Rng.Next(P.MinRangedAtk, P.MaxRangedAtk + 2) + SlayerAtk() + HighGround();
                    int taDdg = Rng.Next(throwTarget.MinDodge, throwTarget.MaxDodge + 1) + SizeDodgeRoll(throwTarget.Race, P.Race) - throwTarget.DodgePenalty;
                    Console.WriteLine($"  Throw axe! ({thrAxeFeet:F0}ft) Roll {taAtk} vs {throwTarget.Name}'s dodge {taDdg}. ({P.AxeCount - 1} axes left)");
                    P.AxeCount--;
                    if (taAtk >= taDdg && !EnemyBlocks(throwTarget, taAtk, isRanged: true))
                    {
                        int taDmg = Rng.Next(2, 9) + SlayerDmg();
                        taDmg = ReduceByToughHide(throwTarget, taDmg);
                        Console.WriteLine($"  HIT! {taDmg} dmg → {throwTarget.Name} HP:{throwTarget.HP - taDmg}/{throwTarget.MaxHP}");
                        throwTarget.HP -= taDmg;
                        if (!throwTarget.Alive) HandleKill(throwTarget);
                        ResolveThrownLanding(true, throwTarget, "Hand Axe", 2, 8);
                    }
                    else
                    {
                        Console.WriteLine("  MISS!");
                        ResolveThrownLanding(false, throwTarget, "Hand Axe", 2, 8);
                    }
                    // Double Tap: second throw
                    if (P.HasFeat("Double Tap") && P.AxeCount > 0 && throwTarget.Alive)
                    {
                        Console.WriteLine("  [Double Tap] Second axe throw!");
                        int taAtk2 = Rng.Next(1, 9) + SlayerAtk() + HighGround();
                        int taDdg2 = Rng.Next(throwTarget.MinDodge, throwTarget.MaxDodge + 1) + SizeDodgeRoll(throwTarget.Race, P.Race) - throwTarget.DodgePenalty;
                        Console.WriteLine($"  Throw axe! ({thrAxeFeet:F0}ft) Roll {taAtk2} vs dodge {taDdg2}. ({P.AxeCount - 1} axes left)");
                        P.AxeCount--;
                        if (taAtk2 >= taDdg2 && !EnemyBlocks(throwTarget, taAtk2, isRanged: true))
                        {
                            int taDmg2 = Rng.Next(2, 9) + SlayerDmg();
                            taDmg2 = ReduceByToughHide(throwTarget, taDmg2);
                            Console.WriteLine($"  HIT! {taDmg2} dmg → {throwTarget.Name} HP:{throwTarget.HP - taDmg2}/{throwTarget.MaxHP}");
                            throwTarget.HP -= taDmg2;
                            if (!throwTarget.Alive) HandleKill(throwTarget);
                            ResolveThrownLanding(true, throwTarget, "Hand Axe", 2, 8);
                        }
                        else
                        {
                            Console.WriteLine("  MISS!");
                            ResolveThrownLanding(false, throwTarget, "Hand Axe", 2, 8);
                        }
                    }
                    justBlocked = false;
                    break;
                }

                case "club sweep":
                {
                    Console.Write("  Club sweep direction [N/S/E/W]: ");
                    string swDir = (GameIO.ReadLine() ?? "").Trim().ToLower();
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
                    if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int wpick) || wpick < 1 || wpick > nearby.Count)
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
                    if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int sc2) || sc2 < 1 || sc2 > available.Count)
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
                            if (int.TryParse(GameIO.ReadLine()?.Trim(), out int hp) && hp >= 1 && hp <= others.Count)
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
                        if (int.TryParse(GameIO.ReadLine()?.Trim(), out int sp) && sp >= 1 && sp <= stamOthers.Count)
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
                if (chosen != "sprint" && chosen != "move") { P.SprintPenalty = P.NoSprintPenalty ? 0 : P.DoubleSprintPenalty ? 4 : 2; sprintPenaltyPending = false; }
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
                string wc = (GameIO.ReadLine() ?? "").Trim().ToLower();
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
                string mc = (GameIO.ReadLine() ?? "").Trim().ToLower();
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
                        int throwDmg = Rng.Next(P.MinGrappleDmg + P.GetFeatStacks("Closeliner"), P.MaxGrappleDmg + 1) + P.Strength;
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
        ChiDoubleMonkDmg = ChiDoubleNonLethal = false;   // chi damage boosts last one turn
        if (P.FearTurns > 0 && --P.FearTurns <= 0) Console.WriteLine("  The terror loosens its grip — your nerve returns.");
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
        if (P.IsMonk && P.ChiUses > 0) o.Add("chi");   // free action — costs no action
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
        if (!P.IsGrappled && !P.Climbed && IsClimbable(PlayerPos)) o.Add("climb");
        if (P.Climbed) { o.Add("climb down"); o.Add("jump down"); }
        if ((P.CharacterType == "Artisan" || P.HasFeat("Gatherer")) && !P.IsGrappled && !P.Climbed)
        {
            if (Rocks.Contains((PlayerPos.X, PlayerPos.Y))) o.Add("mine rock");
            if (Trees.Contains((PlayerPos.X, PlayerPos.Y))) o.Add("cut tree");
        }
        if (P.HasFeat("Alchemist") && !P.IsGrappled) o.Add("brew potion");
        if (P.PotionsBoost + P.PotionsHeal + P.PotionsPoison + P.PotionsRestore > 0) o.Add("use potion");
        return o;
    }

    // ── AMMO BREAKAGE ─────────────────────────────────────────────────────
    // Hit: 25% break. Miss: 20% chance to strike someone adjacent (35% break
    // there); hitting nobody = 50% break. Unbroken arrows are gathered by
    // their owner at the end of the wave; thrown weapons land on the ground.

}
