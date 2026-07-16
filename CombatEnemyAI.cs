using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

// ═════════════════════════════════════════════════════════════════════════
//  CombatEnemyAI.cs — what the horde does on its turn.
// ═════════════════════════════════════════════════════════════════════════
//
//  Enemies.cs says what a creature IS. This file says what it DOES.
//
//  EnemyTurn() walks every living enemy, and THE ORDER OF CHECKS MATTERS:
//    1. Fear      a terrified creature does nothing else. Fight = charge the
//                 source blindly; flight = run from it blindly.
//    2. Wildlife  animals live by instinct (WildlifeTurn), not malice
//    3. Allies    raised dead attack the living
//    4. Own AI    flee-if-wounded, casters spend pools, then move / attack
//
//  Each creature gets 3 actions and spends them through `actions`, which is
//  passed by ref into the movement helpers so a move costs what it should.
//
//  CASTERS RUN DRY: SpellUsesLeft / PrayerUsesLeft / SongUsesLeft are finite,
//  and every caster needs a fallback for when its pool empties — the Troll
//  Musician slings its drum and draws a hand axe; others claw or cower. A
//  caster with no fallback just stands there, which reads as a freeze bug.
//
//  PER-TYPE ACTIONS: DoEnemySpell, DoGoblinShamanPray, GiantMageAction,
//  DoOrcPriestessAction, DoTrollAxeThrow and friends are each one creature's
//  signature move. Add a new type's behaviour as its own Do... method and
//  call it from EnemyTurn, rather than growing the main loop further.
//
// ═════════════════════════════════════════════════════════════════════════

partial class CombatSession
{
    void EnemyTurn()
    {
        Console.WriteLine("\n  --- Enemy Turn ---");
        foreach (var e in Active.Where(e => e.Alive).ToList())
        {
            if (!e.Alive) continue;
            _atkEnemy = e;   // size-based dodge context for the player

            // ── Fear overrides everything: blind fury or blind panic ──
            if (e.FearTurns > 0)
            {
                e.FearTurns--;
                if (e.FearFight)
                {
                    Console.WriteLine($"  [FEAR] {e.Name} charges the source of its terror in blind fury! ({e.FearTurns} turn(s) left)");
                    // Blindly closes on and attacks the fear's source, nothing else
                    if (!e.Position.IsCardinalAdjacent(PlayerPos))
                    {
                        var occF = new HashSet<(int,int)>(Active.Where(a => a.Alive && a != e).Select(a => (a.Position.X, a.Position.Y)));
                        var stF = StepToward(e.Position, PlayerPos);
                        if (!occF.Contains((stF.X, stF.Y))) e.Position = stF;
                    }
                    if (e.Position.IsCardinalAdjacent(PlayerPos)) EnemyAttack(e);
                }
                else
                {
                    Console.WriteLine($"  [FEAR] {e.Name} flees blindly! ({e.FearTurns} turn(s) left)");
                    for (int s = 0; s < 3; s++)
                    {
                        var away = new GridPos(
                            Math.Clamp(e.Position.X + Math.Sign(e.Position.X - PlayerPos.X), 0, 49),
                            Math.Clamp(e.Position.Y + Math.Sign(e.Position.Y - PlayerPos.Y), 0, 49));
                        if (Walls.Contains((away.X, away.Y))) break;
                        e.Position = away;
                    }
                }
                continue;
            }

            // ── Wildlife behave by instinct, not malice ──
            if (e.IsWildlife) { WildlifeTurn(e); continue; }

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
            // NPCs act 3 times per turn; goblins are quick (+1), ogres slow (-1)
            int actions = 3 + (e.Race == "Goblin" ? 1 : 0) - (e.Race == "Ogre" ? 1 : 0);
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
                            int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize();
                            Console.WriteLine($"  {e.Name} grapples! {gAtk} vs your dodge {pDdg}.");
                            if (gAtk >= pDdg) { int gDmg = Rng.Next(e.GrappleDmgMin, e.GrappleDmgMax + 1); P.HP -= gDmg; Console.WriteLine($"  Grappled! {gDmg} crush damage. HP:{P.HP}/{P.MaxHP}"); }
                            else Console.WriteLine($"  Grapple attempt failed!");
                            actions--;
                        }
                        AdvanceOnPlayer(e, ref actions);
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
                        AdvanceOnPlayer(e, ref actions);
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
                    int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize();
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
                AdvanceOnPlayer(e, ref actions);
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

                // HP >= 6: close the distance, then attack/maintain grapple
                AdvanceOnPlayer(e, ref actions);
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
                        int ncAtk = Rng.Next(1, 7), ncDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize();
                        Console.WriteLine($"  {e.Name}'s dark power is spent — it claws! Roll {ncAtk} vs dodge {ncDdg}.");
                        if (ncAtk >= ncDdg)
                        {
                            int ncDmg = Rng.Next(1, 5);
                            ncDmg = Math.Max(1, ncDmg - P.ArmorDamageReduction);
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
                        int smDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize();
                        Console.WriteLine($"  {e.Name} calls down a dark smite! Roll {smAtk} vs your dodge {smDdg}.");
                        if (smAtk >= smDdg)
                        {
                            int smDmg = MitigateMagic(Rng.Next(1, 5) + Rng.Next(1, 5), "prayer");
                            P.HP -= smDmg;
                            Console.WriteLine($"  Dark energy sears you for {smDmg}! HP:{P.HP}/{P.MaxHP}");
                        }
                        else Console.WriteLine("  You dodge the dark bolt!");
                        actions--;
                    }
                }

                // Out of songs? Sling the drum and draw a hand axe.
                if (e is TrollMusician tmDry && tmDry.SongUsesLeft <= 0 && !tmDry.DrewAxe)
                {
                    tmDry.DrewAxe = true;
                    tmDry.EquippedAxes = 1; tmDry.SpareAxes = 1;
                    Console.WriteLine($"  {e.Name}'s songs run dry — it slings the drum and draws a hand axe!");
                }

                // Troll Musician: silences the party's magic, then drums the horde into a frenzy
                if (e is TrollMusician tmus && tmus.SongUsesLeft > 0 && SilenceTurns <= 0 && actions > 0)
                {
                    bool playerHasMagic = P.KnownSpells.Any() || P.CanPray || P.CanSing;
                    tmus.FearSongCooldown = Math.Max(0, tmus.FearSongCooldown - 1);
                    // Someone's in its face — strike up the dread chord first
                    if (tmus.Position.ManhattanDist(PlayerPos) <= 5 && tmus.FearSongCooldown <= 0 && P.FearTurns <= 0)
                    {
                        tmus.SongUsesLeft--;
                        tmus.FearSongCooldown = 4;
                        actions--;
                        int fRoll = Rng.Next(1, 5) + Rng.Next(1, 5) + (int)(tmus.Charisma * 1.5);
                        Console.WriteLine($"  {e.Name} hammers a DREAD CHORD as you close! Fear roll {fRoll} vs your {P.HP} HP.");
                        if (P.FearImmune) Console.WriteLine("  Your mind is fearless — the chord washes over you.");
                        else if (P.HP > fRoll) Console.WriteLine("  You steel yourself against the dread.");
                        else
                        {
                            P.FearTurns = Rng.Next(1, 5);
                            P.FearFight = Rng.Next(2) == 0;
                            Console.WriteLine($"  Terror takes you! {(P.FearFight ? "Blind FURY — you can only attack the drummer" : "PANIC — you can only flee")} for {P.FearTurns} turn(s)!");
                        }
                    }
                    else if (!tmus.PlayedSilence && playerHasMagic)
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

            // ── Giant AI ───────────────────────────────────────────────────
            if (e is GiantEnemy giant)
            {
                for (int i = 0; i < actions && P.HP > 0 && e.Alive; i++)
                {
                    if (giant is GiantMage gmage) { GiantMageAction(gmage); continue; }

                    // Priest: booming dark prayer heals the most wounded ally
                    if (giant is GiantPriest gpr && gpr.PrayerUsesLeft > 0 && SilenceTurns <= 0)
                    {
                        var wounded = Active.Where(a => a.Alive && !a.IsPlayerAlly && a != e &&
                                                        a.HP < a.MaxHP && a.Position.Feet(e.Position) <= 30f)
                                            .OrderBy(a => (float)a.HP / a.MaxHP).FirstOrDefault();
                        if (wounded != null)
                        {
                            gpr.PrayerUsesLeft--;
                            int gh = Rng.Next(1, 5) + Rng.Next(1, 5) + Rng.Next(1, 5);
                            wounded.HP = Math.Min(wounded.HP + gh, wounded.MaxHP);
                            Console.WriteLine($"  {e.Name} booms a prayer — {wounded.Name} heals {gh}! (HP:{wounded.HP}/{wounded.MaxHP})");
                            continue;
                        }
                    }

                    // Duelist: hurls hand axes when not in melee
                    if (giant is GiantDuelist gdu && !e.Position.IsCardinalAdjacent(PlayerPos)
                        && gdu.HandAxes > 0 && e.Position.Feet(PlayerPos) <= 25f)
                    {
                        gdu.HandAxes--;
                        int haAtk = Rng.Next(e.MinAttack, e.MaxAttack + 1);
                        int haDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize();
                        Console.WriteLine($"  {e.Name} hurls a hand axe! Roll {haAtk} vs your dodge {haDdg}. ({gdu.HandAxes} left)");
                        if (haAtk >= haDdg)
                        {
                            int haDmg = Rng.Next(2, 9);
                            haDmg = Math.Max(1, haDmg - P.ArmorDamageReduction);
                            P.HP -= haDmg;
                            Console.WriteLine($"  Axe HIT for {haDmg}! HP:{P.HP}/{P.MaxHP}");
                        }
                        else Console.WriteLine("  You dodge the whirling axe!");
                        continue;
                    }

                    // Base giant: composite bow (2d4, 12 arrows) at range
                    if (giant.GiantArrows > 0 && !e.Position.IsCardinalAdjacent(PlayerPos)
                        && e.Position.Feet(PlayerPos) is > 5f and <= 60f)
                    {
                        giant.GiantArrows--;
                        int gbAtk = Rng.Next(e.MinAttack, e.MaxAttack + 1);
                        int gbDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize();
                        Console.WriteLine($"  {e.Name} looses a giant arrow! Roll {gbAtk} vs your dodge {gbDdg}. ({giant.GiantArrows} arrows left)");
                        if (gbAtk >= gbDdg)
                        {
                            int gbDmg = Rng.Next(1, 5) + Rng.Next(1, 5);
                            gbDmg = Math.Max(1, gbDmg - P.ArmorDamageReduction);
                            P.HP -= gbDmg;
                            Console.WriteLine($"  Arrow HIT for {gbDmg}! HP:{P.HP}/{P.MaxHP}");
                        }
                        else Console.WriteLine("  The huge arrow thuds into the earth beside you!");
                        continue;
                    }

                    // Melee / close in
                    if (e.Position.IsCardinalAdjacent(PlayerPos))
                    {
                        EnemyAttack(e);
                        if (giant is GiantDuelist && e.Alive && P.HP > 0) EnemyKick(e);   // Fury of Blows
                    }
                    else MoveTowardPlayer(e, ref actions, suppressCost: true);
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

                // Ogre Berserker: rages at low HP instead of fleeing (5 rages,
                // each heals 1d4 and grants +2 damage for 3 turns)
                if (e is OgreBerserker obz)
                {
                    if (obz.OgreRageTurns > 0 && --obz.OgreRageTurns == 0)
                    {
                        obz.MinDamage -= 2; obz.MaxDamage -= 2;
                        Console.WriteLine($"  {e.Name}'s rage subsides.");
                    }
                    if (ogrePct <= 25 && obz.OgreRagePoints > 0 && obz.OgreRageTurns <= 0)
                    {
                        obz.OgreRagePoints--;
                        obz.OgreRageTurns = 3;
                        obz.MinDamage += 2; obz.MaxDamage += 2;
                        int rageHeal = Rng.Next(1, 5);
                        e.HP = Math.Min(e.HP + rageHeal, e.MaxHP);
                        Console.WriteLine($"  {e.Name} flies into a RAGE instead of fleeing! Heals {rageHeal} HP, +2 damage for 3 turns. ({obz.OgreRagePoints} rage(s) left)");
                        ogrePct = e.HP * 100 / e.MaxHP;
                    }
                }

                // ≤ 20% HP: roll 1d4 per action (Berserkers with rage left never flee)
                if (ogrePct <= 20 && !e.HasFledBefore
                    && !(e is OgreBerserker obf && (obf.OgreRagePoints > 0 || obf.OgreRageTurns > 0)))
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
                AdvanceOnPlayer(e, ref actions);
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

            // Normal goblin: close the distance, then attack
            AdvanceOnPlayer(e, ref actions);
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
            int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize();
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
            swDmg = Math.Max(1, swDmg - P.ArmorDamageReduction);
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
            int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize();
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
        int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize();
        Console.WriteLine($"  {e.Name} kicks! Roll {kAtk} vs your dodge {pDdg}.");
        if (kAtk >= pDdg)
        {
            int kDmg = Rng.Next(e.KickDmgMin, e.KickDmgMax + 1);
            if (P.Defending) kDmg = Math.Max(1, kDmg / 2);
            kDmg = Math.Max(1, kDmg - P.ArmorDamageReduction);
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
        string choice = (GameIO.ReadLine() ?? "").Trim().ToLower();

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
        int eAtk = rawEAtk - e.AttackPenalty - e.FrostPenalty - e.SprintPenalty + SizeRules.AtkBonus(e.Race, P.Race);
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
        int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize() - P.FrostPenalty - brokenLegPenalty;
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
                dmg = Math.Max(1, dmg + SizeRules.DmgBonus(e.Race, P.Race));
            }
            if (P.Defending) dmg = Math.Max(1, dmg / 2);
            dmg = Math.Max(1, dmg - P.ArmorDamageReduction);
            if (P.SpellweaveArmorTurns > 0) dmg = Math.Max(1, dmg - 2);
            Console.WriteLine($"  HIT! You take {dmg} damage. HP: {P.HP - dmg}/{P.MaxHP}");
            P.HP -= dmg;
            if (P.IsRaging && P.HP < 0) { P.HP = 0; Console.WriteLine("  RAGE keeps you standing!"); }
            // Double Tap off-hand
            if (e.HasDoubleTap && P.HP > 0 && !e.DroppedWeapon)
            {
                int ofAtk = Rng.Next(e.OffhandMinAtk, e.OffhandMaxAtk + 1) - e.AttackPenalty;
                int ofDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize();
                string offhandLabel = e.OffhandNonLethal ? "War Mace (non-lethal)" : "off-hand";
                Console.WriteLine($"  {e.Name} {offhandLabel}! Roll {ofAtk} vs your dodge {ofDdg}.");
                if (ofAtk >= ofDdg)
                {
                    int ofDmg = Rng.Next(e.OffhandMinDmg, e.OffhandMaxDmg + 1);
                    if (P.Defending) ofDmg = Math.Max(1, ofDmg / 2);
                    ofDmg = Math.Max(1, ofDmg - P.ArmorDamageReduction);
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
                if ((GameIO.ReadLine() ?? "").Trim().ToLower() == "y") DoGrapple(e);
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

    void WildAttackPlayer(Enemy w, string label, int atkMin, int atkMax, int dmgMin, int dmgMax)
    {
        int atk = Rng.Next(atkMin, atkMax + 1);
        int ddg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize();
        Console.WriteLine($"  {w.Name} {label}! Roll {atk} vs your dodge {ddg}.");
        if (atk >= ddg)
        {
            int dmg = Rng.Next(dmgMin, dmgMax + 1);
            dmg = Math.Max(1, dmg - P.ArmorDamageReduction);
            P.HP -= dmg;
            Console.WriteLine($"  HIT for {dmg}! HP:{P.HP}/{P.MaxHP}");
        }
        else Console.WriteLine("  You evade!");
    }

    void WildWander(Enemy w)
    {
        var dirs = new (int dx, int dy)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        var (dx, dy) = dirs[Rng.Next(4)];
        int nx = Math.Clamp(w.Position.X + dx, 0, 49);
        int ny = Math.Clamp(w.Position.Y + dy, 0, 49);
        if (!IsWall(nx, ny)) w.Position = new GridPos(nx, ny);
    }

    void WildRetreat(Enemy w, int squares)
    {
        for (int i = 0; i < squares; i++)
        {
            int dx = Math.Sign(w.Position.X - PlayerPos.X);
            int dy = Math.Sign(w.Position.Y - PlayerPos.Y);
            int nx = Math.Clamp(w.Position.X + (dx != 0 ? dx : 1), 0, 49);
            int ny = Math.Clamp(w.Position.Y + dy, 0, 49);
            if (!IsWall(nx, ny)) w.Position = new GridPos(nx, ny);
        }
    }

    void WildlifeTurn(Enemy w)
    {
        int sq = w.Position.ManhattanDist(PlayerPos);

        if (w is Deer deer)
        {
            // Antlered deer gore their attacker; the rest just graze and wander
            if (deer.Antlered && deer.HP < deer.MaxHP && w.Position.IsCardinalAdjacent(PlayerPos))
                WildAttackPlayer(w, "lowers its antlers and charges", 2, 8, 2, 12);
            else
                WildWander(w);
            return;
        }

        if (w is Wolf)
        {
            // Wolves hunt deer first, then circle human prey within 4 squares
            var prey = Active.OfType<Deer>().FirstOrDefault(d => d.Alive && d.Position.ManhattanDist(w.Position) <= 4);
            if (prey != null)
            {
                if (!w.Position.IsCardinalAdjacent(prey.Position))
                    w.Position = StepToward(w.Position, prey.Position);
                if (w.Position.IsCardinalAdjacent(prey.Position))
                {
                    int bite = Rng.Next(2, 9);
                    prey.HP -= bite;
                    Console.WriteLine($"  {w.Name} savages {prey.Name} for {bite}!" + (prey.HP <= 0 ? $" {prey.Name} is brought down." : ""));
                }
                return;
            }
            if (sq <= 1)
            {
                // Hit and run: bite, then back off two squares
                WildAttackPlayer(w, "darts in and BITES", 3, 12, 2, 8);
                WildRetreat(w, 2);
                Console.WriteLine($"  {w.Name} circles away, hackles raised.");
            }
            else if (sq <= 4)
            {
                w.Position = StepToward(w.Position, PlayerPos);
                if (!w.Position.IsCardinalAdjacent(PlayerPos))
                    w.Position = StepToward(w.Position, PlayerPos);
                if (w.Position.IsCardinalAdjacent(PlayerPos))
                    WildAttackPlayer(w, "lunges and BITES", 3, 12, 2, 8);
            }
            else WildWander(w);
            return;
        }

        if (w is Boar boar)
        {
            if (boar.JustCharged)
            {
                // Wheel around after the charge, ready for another pass
                boar.JustCharged = false;
                WildRetreat(w, 2);
                Console.WriteLine($"  {w.Name} thunders past and wheels around, snorting.");
                return;
            }
            if (sq <= 5)
            {
                for (int s = 0; s < 3 && !w.Position.IsCardinalAdjacent(PlayerPos); s++)
                    w.Position = StepToward(w.Position, PlayerPos);
                if (w.Position.IsCardinalAdjacent(PlayerPos))
                {
                    WildAttackPlayer(w, "CHARGES with slashing tusks", 2, 6, 3, 12);
                    boar.JustCharged = true;
                }
            }
            else WildWander(w);
            return;
        }

        if (w is Bear)
        {
            if (sq <= 3 || w.HP < w.MaxHP)
            {
                if (!w.Position.IsCardinalAdjacent(PlayerPos))
                    w.Position = StepToward(w.Position, PlayerPos);
                if (w.Position.IsCardinalAdjacent(PlayerPos))
                {
                    if (!P.IsGrappled && Rng.Next(100) < 30)
                    {
                        // Bear hug: claws in the back, teeth at the neck
                        int hugGrap = Rng.Next(w.MinGrapple, w.MaxGrapple + 1);
                        int pGrap = Rng.Next(P.MinGrapple, P.MaxGrapple + 1) + P.Strength;
                        Console.WriteLine($"  {w.Name} rears up for a BEAR HUG! {hugGrap} vs your {pGrap}.");
                        if (hugGrap >= pGrap)
                        {
                            P.IsGrappled = true; P.GrappledBy = w;
                            int clawDmg = 0; for (int d = 0; d < 6; d++) clawDmg += Rng.Next(1, 3);   // 6d2
                            int biteDmg = 0; for (int d = 0; d < 4; d++) biteDmg += Rng.Next(1, 4);   // 4d3
                            int total = clawDmg + biteDmg;
                            total = Math.Max(2, total - P.ArmorDamageReduction);
                            P.HP -= total;
                            Console.WriteLine($"  Claws dig into your back ({clawDmg}) as it BITES ({biteDmg})! {total} damage. HP:{P.HP}/{P.MaxHP}");
                        }
                        else Console.WriteLine("  You twist free of its grasp!");
                    }
                    else
                    {
                        // Fury of Blows: one bite and four claw swipes in one action
                        WildAttackPlayer(w, "BITES", 3, 12, 2, 8);
                        for (int c = 0; c < 4 && P.HP > 0; c++)
                            WildAttackPlayer(w, $"rakes with its claws ({c + 1}/4)", 2, 12, 3, 12);
                    }
                }
            }
            else if (Rng.Next(100) < 40) WildWander(w);   // lumber about the den
            return;
        }

        WildWander(w);
    }

    // ── GIANT MAGE GRIMOIRE ───────────────────────────────────────────────

    void GiantMageAction(GiantMage gm)
    {
        if (SilenceTurns > 0) { Console.WriteLine($"  {gm.Name} thunders arcane words — but the silence smothers them!"); return; }
        if (gm.SpellUsesLeft <= 0)
        {
            // Out of magic: staff melee or lumber closer
            if (gm.Position.IsCardinalAdjacent(PlayerPos)) { EnemyAttack(gm); return; }
            int unused = 0;
            MoveTowardPlayer(gm, ref unused, suppressCost: true);
            return;
        }

        float feet = gm.Position.Feet(PlayerPos);

        // Boosting grimoire: empowers the horde instead of attacking
        if (gm.Grimoire == "boost")
        {
            var ally = Active.Where(a => a.Alive && !a.IsPlayerAlly && a != gm).OrderBy(_ => Rng.Next()).FirstOrDefault();
            if (ally != null)
            {
                gm.SpellUsesLeft--;
                ally.MinAttack += 1; ally.MinDodge += 1;
                Console.WriteLine($"  {gm.Name} chants from its grimoire — {ally.Name} surges with power! (+1 attack, +1 dodge)");
                return;
            }
        }

        // Negative grimoire: mend the walking dead first
        if (gm.Grimoire == "negative")
        {
            var undead = Active.FirstOrDefault(u => u != gm && u.IsUndead && u.Alive && u.HP < u.MaxHP && u.Position.Feet(gm.Position) <= 25f);
            if (undead != null)
            {
                gm.SpellUsesLeft--;
                int nh = Rng.Next(1, 5) + Rng.Next(1, 5);
                undead.HP = Math.Min(undead.HP + nh, undead.MaxHP);
                Console.WriteLine($"  {gm.Name} pours negative energy into {undead.Name} — heals {nh}! (HP:{undead.HP}/{undead.MaxHP})");
                return;
            }
        }

        // Attack spell chosen by range: close = big burst, far = bolt
        if (feet > 50f)
        {
            int unused = 0;
            MoveTowardPlayer(gm, ref unused, suppressCost: true);
            return;
        }
        gm.SpellUsesLeft--;
        int sAtk = Rng.Next(gm.MinAttack, gm.MaxAttack + 1);
        int sDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize();
        string elem = gm.Grimoire == "boost" ? "lightning" : gm.Grimoire;   // boost fallback when no allies
        Console.WriteLine($"  {gm.Name} hurls {elem} from its grimoire! Roll {sAtk} vs your dodge {sDdg}.");
        if (sAtk >= sDdg)
        {
            int sDmg = Rng.Next(1, 7) + Rng.Next(1, 7) + gm.Level / 10;
            if (feet <= 10f) sDmg += Rng.Next(1, 5);   // point-blank burst
            if (P.SpellweaveArmorTurns > 0) sDmg = Math.Max(1, sDmg - 2);
            sDmg = MitigateMagic(sDmg, "spell", elem == "ice" ? "frost" : elem);
            P.HP -= sDmg;
            Console.WriteLine($"  {elem.ToUpper()} HIT for {sDmg}! HP:{P.HP}/{P.MaxHP}");
            switch (elem)
            {
                case "fire":
                    P.BurningDmg = Rng.Next(1, 5); P.BurningTurns = Rng.Next(1, 5);
                    Console.WriteLine($"  You catch FIRE! {P.BurningDmg}/turn for {P.BurningTurns} turn(s).");
                    break;
                case "ice":
                    P.FrostPenalty = Math.Max(P.FrostPenalty, 2); P.FrostTurns = Math.Max(P.FrostTurns, 2);
                    Console.WriteLine("  Frost crackles over you! -2 to rolls for 2 turns.");
                    break;
                case "negative":
                    gm.HP = Math.Min(gm.HP + sDmg / 2, gm.MaxHP);
                    Console.WriteLine($"  {gm.Name} drinks your life force! (heals {sDmg / 2})");
                    break;
            }
        }
        else Console.WriteLine("  You dive aside — the blast scorches the ground!");
    }

    // ── SPELL GOBLIN ENEMY SPELL ──────────────────────────────────────────

    void DoEnemySpell(SpellGoblin sg)
    {
        if (SilenceTurns > 0) { Console.WriteLine($"  {sg.Name} mouths a spell — but the silence smothers it!"); return; }
        if (sg.SpellUsesLeft <= 0)
        {
            Console.WriteLine($"  {sg.Name} is out of magic!");
            if (sg.Position.IsCardinalAdjacent(PlayerPos))
            {
                int cAtk = Rng.Next(1, 7), cDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize();
                Console.WriteLine($"  {sg.Name} claws at you! Roll {cAtk} vs dodge {cDdg}.");
                if (cAtk >= cDdg)
                {
                    int cDmg = Rng.Next(1, 5);
                    cDmg = Math.Max(1, cDmg - P.ArmorDamageReduction);
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
                int dmg = MitigateMagic(Math.Max(1, Rng.Next(4, 13) - mageShieldAbsorb), "spell", "fire");
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
                int dmg = MitigateMagic(Math.Max(1, Rng.Next(3, 7) - mageShieldAbsorb), "spell", "lightning");
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
                int dmg = MitigateMagic(Math.Max(1, Rng.Next(2, 9) - mageShieldAbsorb), "spell", "frost");
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
        int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize() - P.FrostPenalty - P.BrokenLimbs.Count(l => l.Contains("Leg"));
        Console.WriteLine($"  {ob.Name} hurls a hand axe! ({feet:F0}ft) Roll {atkRoll} vs your dodge {pDdg}. ({ob.HandAxeCount - 1} axes left)");
        ob.HandAxeCount--;
        if (atkRoll >= pDdg)
        {
            int dmg = Rng.Next(2, 9); // hand axe throw: 2-8
            if (P.Defending) dmg = Math.Max(1, dmg / 2);
            dmg = Math.Max(1, dmg - P.ArmorDamageReduction);
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
        int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize() - P.FrostPenalty - P.BrokenLimbs.Count(l => l.Contains("Leg"));
        rg.DaggerCount--;
        int daggersLeft = rg.DaggerCount;
        Console.WriteLine($"  {rg.Name} hurls a dagger! ({feet:F0}ft) Roll {atkRoll} vs your dodge {pDdg}. ({daggersLeft} daggers left)");
        if (atkRoll >= pDdg)
        {
            int dmg = Rng.Next(rg.MinDamage, rg.MaxDamage + 1);
            if (P.Defending) dmg = Math.Max(1, dmg / 2);
            dmg = Math.Max(1, dmg - P.ArmorDamageReduction);
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
            int dmg = MitigateMagic(Rng.Next(1, 7), "prayer");
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
        int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize() - P.FrostPenalty;
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
            dmg = Math.Max(1, dmg - P.ArmorDamageReduction);
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
        int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize() - P.FrostPenalty;
        hbt.DaggerCount--;
        Console.WriteLine($"  {hbt.Name} hurls a dagger! ({feet:F0}ft) Roll {atkRoll} vs your dodge {pDdg}. ({hbt.DaggerCount} daggers left)");
        if (atkRoll >= pDdg)
        {
            int dmg = Rng.Next(1, 7);
            if (P.Defending) dmg = Math.Max(1, dmg / 2);
            dmg = Math.Max(1, dmg - P.ArmorDamageReduction);
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
            int dmg = MitigateMagic(Rng.Next(1, 7), "prayer");
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
            dmg = MitigateMagic(dmg, "prayer");
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
        int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize() - P.FrostPenalty;
        Console.WriteLine($"  {orr.Name} draws long bow! ({feet:F0}ft, dmg {orr.BowMinDmg}-{orr.BowMaxDmg}) Roll {rawAtk} vs your dodge {pDdg}.");
        orr.ArrowCount--;
        if (isFumble) { Console.WriteLine($"  FUMBLE! {orr.Name}'s bow string snaps!"); return; }
        if (rawAtk >= pDdg)
        {
            int dmg = Rng.Next(orr.BowMinDmg, orr.BowMaxDmg + 1);
            if (isCrit) { dmg *= 2; Console.WriteLine($"  CRITICAL! Arrow strikes true!"); }
            if (P.Defending) dmg = Math.Max(1, dmg / 2);
            dmg = Math.Max(1, dmg - P.ArmorDamageReduction);
            Console.WriteLine($"  Arrow HIT! {dmg} damage. HP:{P.HP - dmg}/{P.MaxHP}");
            P.HP -= dmg;
            // Double Tap: second arrow
            if (orr.HasDoubleTap && orr.ArrowCount > 0 && P.HP > 0)
            {
                int raw2 = Rng.Next(orr.BowMinAtk, orr.BowMaxAtk + 1);
                int pDdg2 = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize();
                Console.WriteLine($"  {orr.Name} double-taps! Roll {raw2} vs dodge {pDdg2}.");
                orr.ArrowCount--;
                if (raw2 >= pDdg2)
                {
                    int dmg2 = Rng.Next(orr.BowMinDmg, orr.BowMaxDmg + 1);
                    if (raw2 == orr.BowMaxAtk) { dmg2 *= 2; Console.WriteLine($"  CRITICAL second arrow!"); }
                    dmg2 = Math.Max(1, dmg2 - P.ArmorDamageReduction);
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
        int pDdg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize() - P.FrostPenalty;
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
            dmg = Math.Max(1, dmg - P.ArmorDamageReduction);
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
