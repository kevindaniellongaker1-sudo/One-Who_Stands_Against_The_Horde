using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

// ═════════════════════════════════════════════════════════════════════════
//  CombatAttacks.cs — resolving every way a player can hurt something.
// ═════════════════════════════════════════════════════════════════════════
//
//  THE MELEE PATH — DoAttack() gathers, PerformAttack() resolves:
//    DoAttack stacks every modifier additively into the attack roll and into
//    dmgBonus: weapon stats (WeaponPickupStats), size (SizeRules), core
//    traits (MeleeTraitBonus), high ground, Slayer song, rage, frenzy, chi,
//    Weapon Specialist, potions. PerformAttack then rolls it against the
//    target's dodge / block / parry and applies what gets through.
//
//    When you add a modifier, add it to BOTH the roll and the printed line.
//    A modifier the player cannot see is a balance bug waiting to happen.
//
//  THE OTHER PATHS: DoBowAttack (range bands, arrow types, shot modes),
//  DoWandAttack, DoMaceAttack, DoGrapple, and DoSpell (every spell in one
//  switch, gated on SpellUses).
//
//  NON-LETHAL: IsNonLethalAttack() decides KO versus kill; ResolveDowned()
//  and KnockOut() centralise it. Undead always take lethal damage. If you add
//  a blunt weapon, add it to IsNonLethalAttack or it will quietly start
//  killing people it was meant to knock out.
//
//  DEATH: HandleKill() is the single funnel for XP, loot drops and
//  necromancy. Route kills through it instead of zeroing HP by hand, or the
//  player silently loses the reward.
//
// ═════════════════════════════════════════════════════════════════════════

partial class CombatSession
{
    void DoAttack(Enemy target)
    {
        if (P.HeldWeapon is "Bow" or "Shortbow" or "Hunting Bow") { DoBowAttack(target); return; }
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
            string m = (GameIO.ReadLine() ?? "n").Trim().ToLower();
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
        int sizeAtk = SizeRules.AtkBonus(P.Race, target.Race);
        int sizeDmg = SizeRules.DmgBonus(P.Race, target.Race);
        dmgBonus += sizeDmg;
        if (sizeAtk != 0 || sizeDmg != 0)
            Console.WriteLine($"  [Size] {sizeAtk:+0;-0} attack, {sizeDmg:+0;-0} damage vs {(target.Race.Length > 0 ? target.Race : "foe")}.");
        if (P.Climbed) Console.WriteLine("  [High ground] +2 attack!");
        // Core traits: Strength drives melee; light/unarmed use the better of
        // Strength/Dexterity (Wisdom for non-lethal); two-handed adds 1.5x Str.
        int traitAtk = MeleeTraitBonus();
        if (traitAtk != 0) Console.WriteLine($"  [Traits] {traitAtk:+0;-0} melee attack & damage.");
        dmgBonus += traitAtk;
        // Frenzy: -2 to hit, but the base damage roll is added again (doubled)
        int frenzyAtk = P.Frenzied ? -2 : 0;
        if (P.Frenzied) { dmgBonus += Rng.Next(minDmg, maxDmg + 1); Console.WriteLine("  [Frenzy] Reckless double-strength blow!"); }
        int specStacks = P.WeaponSpec.GetValueOrDefault(P.HeldWeapon ?? "Unarmed");
        int specAtk = 0;
        if (specStacks > 0)
        {
            int specDmg = 0;
            for (int s = 0; s < specStacks; s++) { specAtk += Rng.Next(1, 5); specDmg += Rng.Next(1, 5); }
            dmgBonus += specDmg;
            Console.WriteLine($"  [Weapon Specialist x{specStacks}] +{specAtk} attack, +{specDmg} damage!");
        }
        if (P.PotionBoostTurns > 0)
        {
            specAtk += P.PotionAtkBoost;
            Console.WriteLine($"  [Potion] +{P.PotionAtkBoost} attack!");
        }

        // Chi: double damage with a monk weapon / on non-lethal strikes
        if (ChiDoubleMonkDmg && P.IsMonkWeapon(P.HeldWeapon ?? ""))
        { dmgBonus += Rng.Next(minDmg, maxDmg + 1); Console.WriteLine("  [Chi] Monk weapon strikes double!"); }
        if (ChiDoubleNonLethal && IsNonLethalAttack())
        { dmgBonus += Rng.Next(minDmg, maxDmg + 1); Console.WriteLine("  [Chi] Non-lethal blow strikes double!"); }

        // Duelist Flurry: 3 attacks this action
        int flurryCount = (P.CharacterType == "Duelist" && P.DuelistEffectTurns.GetValueOrDefault("Duelist Flurry") > 0) ? 3 : 1;
        if (flurryCount == 1 && P.HasFeat("Flurry of Blows"))
        {
            Console.Write("  [Flurry of Blows] Unleash five attacks? (y/n): ");
            if ((GameIO.ReadLine() ?? "n").Trim().ToLower().StartsWith("y")) flurryCount = 5;
        }
        // A monk with a monk weapon lands an extra attack per attack — this
        // stacks on top of Fury/Flurry/Double Tap rather than replacing them.
        int monkExtra = P.MonkWeaponExtraAttacks(P.HeldWeapon ?? "");
        if (monkExtra > 0)
        {
            flurryCount += flurryCount * monkExtra;
            Console.WriteLine($"  [Monk] Your {(P.HeldWeapon is null or "" ? "bare hands" : P.HeldWeapon)} flow — {flurryCount} strikes.");
        }
        for (int fi = 0; fi < flurryCount && target.Alive; fi++)
        {
            if (fi > 0) Console.WriteLine($"  [Flurry hit {fi + 1}]");
            int rawRoll = Rng.Next(minAtk, maxAtk + 1);
            PerformAttack(target, rawRoll + atkPen + warriorAtkBonus - brokenArmPenalty + trueSightAtkBonus + songAtkBonus + sizeAtk + specAtk + frenzyAtk + traitAtk + HighGround(), minDmg, maxDmg, fi == 0 ? dmgBonus : 0, useSunder, useDisarm, fi == 0 && useSap, rawRoll == maxAtk, rawRoll == minAtk);
        }

        // Off-hand (Double Tap)
        if (P.HasFeat("Double Tap") && target.Alive)
        {
            int ofAtk = Rng.Next(1, 7);
            int ofDdg = Rng.Next(target.MinDodge, target.MaxDodge + 1) + SizeDodgeRoll(target.Race, P.Race) - target.DodgePenalty;
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
                int hbA = Rng.Next(1, 7), hbD = Rng.Next(target.MinDodge, target.MaxDodge + 1) + SizeDodgeRoll(target.Race, P.Race) - target.DodgePenalty;
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

        int ddg = Rng.Next(target.MinDodge, target.MaxDodge + 1) + SizeDodgeRoll(target.Race, P.Race) - target.DodgePenalty - target.FrostPenalty;
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
                string dc = (GameIO.ReadLine() ?? "w").Trim().ToLower();
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
            if ((GameIO.ReadLine() ?? "").Trim().ToLower() == "y") FreeAttack(target);
        }
    }

    void DoKick(Enemy target)
    {
        int kA = Rng.Next(1, 5), kD = Rng.Next(target.MinDodge, target.MaxDodge + 1) + SizeDodgeRoll(target.Race, P.Race) - target.DodgePenalty;
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
        int d = Rng.Next(target.MinDodge, target.MaxDodge + 1) + SizeDodgeRoll(target.Race, P.Race) - target.DodgePenalty;
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
        if ((GameIO.ReadLine() ?? "").Trim().ToLower() == "y") FreeAttack(target);
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
        P.HeldWeapon is null or "Staff" or "Ogre Club" or "Mace" or "War Mace" or "Warhammer" or "Club" or "Quarterstaff";

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
        // Felled wildlife is skinned on the spot: hides 1d9-1, meat 2d6-2
        if (e.IsWildlife)
        {
            int hides = Math.Min(Rng.Next(1, 10) - 1, P.PackRoom);
            P.Hides += hides;
            int meat = Math.Min(Math.Max(0, Rng.Next(1, 7) + Rng.Next(1, 7) - 2), P.PackRoom);
            P.Meat += meat;
            Console.WriteLine($"  You skin the {e.TypeName}: +{hides} hide(s), +{meat} meat." +
                (P.PackRoom == 0 ? "  [Pack FULL]" : ""));
        }
        if (P.IsGrappled && P.GrappledBy == e) { P.IsGrappled = false; P.GrappledBy = null; Console.WriteLine("  You are no longer grappled."); }
        var others = Active.Where(x => x.Alive && x != e).ToList();
        if (P.HasFeat("Thin the Herd") && others.Any())
        {
            Console.Write($"  Thin the Herd! Free attack on another enemy? (y/n): ");
            if ((GameIO.ReadLine() ?? "").Trim().ToLower() == "y")
            {
                var t = others.Count == 1 ? others[0] : PickTarget(others);
                if (t != null) FreeAttack(t);
            }
        }
        // (Arrow recovery now happens at the end of the wave — see breakage rules)

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
                    if ((GameIO.ReadLine() ?? "").Trim().ToLower() == "y")
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
                    if ((GameIO.ReadLine() ?? "").Trim().ToLower() == "y")
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
                    if ((GameIO.ReadLine() ?? "").Trim().ToLower() != "y") continue;

                    if (candidate.HeldWeapon == null)
                    {
                        candidate.HeldWeapon = dropWeapon;
                        Console.WriteLine($"  {candidate.Name} equips the {dropWeapon}. (Atk {minA}-{maxA}, Dmg {minD}-{maxD})");
                        weaponTaken = true;
                    }
                    else if (candidate.SecondaryWeapon == null)
                    {
                        Console.Write($"  Main hand (m) replaces [{candidate.HeldWeapon}] \u2192 off-hand, or add as off-hand (o), or skip (n)? ");
                        string slot = (GameIO.ReadLine() ?? "").Trim().ToLower();
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
                        string slot = (GameIO.ReadLine() ?? "").Trim().ToLower();
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
        int ddg = Rng.Next(P.MinDodge, P.MaxDodge + 1) + PDodgeSize();
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
            if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int idx) || idx == 0) break;
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
        "Great Axe"      => (2, 9, 4, 12),   // 4d3
        "Staff"          => (1, 6, 2, 6),
        "Kukuri"         => (2, 8, 2, 8),
        "Wand"           => (1, 6, 3, 4),
        "Long Sword"     => (2, 9, 2, 10),
        "Ogre Club"      => (2, 14, 4, 16),
        "Sword"          => (1, 7, 2, 8),
        "Great Sword"    => (2, 9, 4, 12),
        "Claymore"       => (2, 9, 4, 16),   // 4d4
        "Axe"            => (2, 8, 3, 12),   // 3d4
        "Club"           => (1, 6, 1, 6),    // non-lethal
        "Quarterstaff"   => (1, 6, 1, 6),    // non-lethal, monk weapon
        "Long Staff"     => (1, 7, 1, 10),   // monk weapon
        "Halberd"        => (2, 8, 2, 10),   // reach
        "Pike"           => (2, 8, 2, 12),   // reach
        "Warhammer"      => (2, 8, 3, 12),   // non-lethal
        "Dagger"         => (1, 6, 1, 6),
        "Pickaxe"        => (1, 4, 2, 12),   // 2d6 damage, -2 to hit
        // ── Craft & general goods ──
        "Workshop Hammer" => (1, 6, 2, 8),    // 2d4 non-lethal; needed to craft as an Artisan
        "Knife"           => (1, 6, 1, 2),    // 1d2; skins animals; throwable 1d4
        // ── Hammers (all non-lethal) ──
        "Throwing Hammer" => (1, 6, 2, 8),    // 2d4 melee, 3d3 thrown
        "Battle Hammer"   => (2, 8, 3, 9),    // 3d3 melee, 4-8 thrown
        "Maul"            => (2, 8, 4, 24),   // 4d6, two-handed
        // ── Picks & polearms ──
        "War Pick"        => (1, 7, 2, 12),   // 2d6, -1 to hit, ignores 2 armor
        // ── Bows ──
        "Composite Bow"   => (3, 8, 4, 16),   // +2 to hit, 4d4
        // ── Whips (reach 2) ──
        "Whip"            => (1, 6, 2, 8),    // 2d4, +2 disarm, needs Disarm
        "Nine-Tails Whip" => (1, 6, 1, 2),    // nine 1d2 lashes, may stun
        // ── Monk weapons ──
        "Wakizashi"       => (2, 8, 4, 12),   // 4d3 one-handed / 4d4 two-handed
        "Tanto"           => (1, 6, 2, 8),    // 2d4, light + monk
        "Katana"          => (2, 8, 5, 15),   // 5d3 one-handed / 6d4 two-handed
        "Nodachi"         => (1, 8, 8, 32),   // 8d4, two-handed, reach, wide swings
        "Tetsubo"         => (2, 8, 4, 12),   // 4d3 non-lethal, ignores 2d3 armor
        "Kanabo"          => (2, 8, 5, 15),   // 5d3 non-lethal, ignores 3d3 armor
        "Nunchucks"       => (1, 6, 1, 4),    // 1d4 x consecutive attack number
        "Chain and Ball"  => (2, 8, 3, 12),   // 3d4 non-lethal, reach, +2 disarm/grapple
        "Spike Chain"     => (2, 8, 3, 9),    // 3d3 lethal, reach, +2 disarm/grapple
        "Screama Sticks"  => (2, 8, 2, 8),    // pair 2d4 each; combine into a 3d4 staff
        "Foldable Fan Blade" => (2, 8, 2, 8), // 2d4 thrown lethal, returns
        "Circle Throwing Blades" => (2, 8, 2, 6), // pair, 2d3 melee, returns on a hit
        "Shuriken"        => (2, 8, 2, 6),    // 2d3, thrown only
        "Smoke Pouch"     => (1, 6, 1, 2),    // utility: free action escape
        "Shortbow"       => (1, 6, 1, 6),
        _ => (0, 0, 0, 0)
    };

    void DoMmaFreeAction(List<Enemy> alive)
    {
        if (!alive.Any()) return;
        Console.WriteLine("  [MMA] Slip free! Immediate free action:");
        Console.Write("  [A]ttack  [H]eal  [D]efend  [S]pell  [skip]: ");
        string ch = (GameIO.ReadLine() ?? "").Trim().ToLower();
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
            if (int.TryParse(GameIO.ReadLine()?.Trim(), out int sc) && sc >= 1 && sc <= P.KnownSpells.Count)
                DoSpell(P.KnownSpells[sc - 1], alive);
        }
    }

    void DoBowAttack(Enemy target)
    {
        if (P.ArrowCount + P.BluntArrows + P.BarbedArrows + P.SpiralArrows <= 0) { Console.WriteLine("  Out of arrows!"); return; }

        // Choose arrow type when specials are stocked
        string arrowType = "regular";
        if (P.BluntArrows > 0 || P.BarbedArrows > 0 || P.SpiralArrows > 0)
        {
            var opts2 = new List<string>();
            if (P.ArrowCount > 0)   opts2.Add($"[R]egular x{P.ArrowCount}");
            if (P.BluntArrows > 0)  opts2.Add($"[B]lunt x{P.BluntArrows}");
            if (P.BarbedArrows > 0) opts2.Add($"bar[E]d x{P.BarbedArrows}");
            if (P.SpiralArrows > 0) opts2.Add($"[S]piral x{P.SpiralArrows}");
            Console.Write($"  Arrow? {string.Join("  ", opts2)}: ");
            string apick = (GameIO.ReadLine() ?? "r").Trim().ToLower();
            arrowType = apick switch
            {
                "b" or "blunt" when P.BluntArrows > 0   => "blunt",
                "e" or "barbed" when P.BarbedArrows > 0 => "barbed",
                "s" or "spiral" when P.SpiralArrows > 0 => "spiral",
                _ => P.ArrowCount > 0 ? "regular"
                     : P.BluntArrows > 0 ? "blunt" : P.BarbedArrows > 0 ? "barbed" : "spiral",
            };
        }
        int arrowAtkBonus = arrowType == "spiral" ? Rng.Next(1, 5) : 0;
        int arrowDmgBonus = arrowType switch
        {
            "barbed" => Rng.Next(1, 5),
            "spiral" => Rng.Next(1, 5),
            _ => 0,
        };
        if (arrowType != "regular")
            Console.WriteLine($"  [{char.ToUpper(arrowType[0])}{arrowType[1..]} arrow]{(arrowAtkBonus > 0 ? $" +{arrowAtkBonus} atk" : "")}{(arrowDmgBonus > 0 ? $" +{arrowDmgBonus} dmg" : "")}{(arrowType == "blunt" ? " (non-lethal)" : "")}");
        void SpendArrow()
        {
            switch (arrowType)
            {
                case "blunt": P.BluntArrows--; break;
                case "barbed": P.BarbedArrows--; break;
                case "spiral": P.SpiralArrows--; break;
                default: P.ArrowCount--; break;
            }
        }
        float feet = PlayerPos.Feet(target.Position);
        if (feet < 4f) { Console.WriteLine($"  Too close to use bow! ({feet:F1}ft, min 4ft)"); return; }
        if (feet > 60f) { Console.WriteLine($"  Too far! ({feet:F1}ft, max 60ft)"); return; }
        bool huntingBow = P.HeldWeapon == "Hunting Bow";

        bool HasAmmo() => arrowType switch
        {
            "blunt" => P.BluntArrows > 0,
            "barbed" => P.BarbedArrows > 0,
            "spiral" => P.SpiralArrows > 0,
            _ => P.ArrowCount > 0,
        };

        // A single quick shot at any target (used by the special shot feats)
        void QuickShot(Enemy tgt, bool costsArrow)
        {
            float f2 = PlayerPos.Feet(tgt.Position);
            if (f2 > 60f) { Console.WriteLine($"  {tgt.Name} is beyond bow range."); return; }
            if (costsArrow) { if (!HasAmmo()) return; SpendArrow(); }
            int dMin, dMax;
            if (f2 <= 14f) { dMin = 4; dMax = 12; }
            else if (f2 <= 45f) { dMin = 2; dMax = 10; }
            else { dMin = 1; dMax = 5; }
            if (huntingBow) { dMin = 2; dMax = 6; }
            dMin = Math.Max(1, dMin + P.MinRangedDmgBonus + P.Dexterity + P.BowTrait());
            dMax = Math.Max(dMin, dMax + P.MinRangedDmgBonus + P.MaxRangedDmgBonus);
            int a2 = Rng.Next(P.MinRangedAtk, P.MaxRangedAtk + 1) + SlayerAtk() + HighGround() + arrowAtkBonus + (huntingBow ? 2 : 0) + P.Dexterity + P.BowTrait();
            int d2 = Rng.Next(tgt.MinDodge, tgt.MaxDodge + 1) + SizeDodgeRoll(tgt.Race, P.Race) - tgt.DodgePenalty;
            Console.WriteLine($"  Arrow at {tgt.Name} ({f2:F0}ft): {a2} vs dodge {d2}.");
            if (a2 >= d2 && !EnemyBlocks(tgt, a2, isRanged: true))
            {
                int dm = Rng.Next(dMin, dMax + 1) + SlayerDmg() + arrowDmgBonus;
                dm = ReduceByToughHide(tgt, dm);
                tgt.HP -= dm;
                Console.WriteLine($"  HIT! {dm} dmg → {tgt.Name} HP:{tgt.HP}/{tgt.MaxHP}");
                if (!P.HasReturningAmmo && Rng.Next(100) < 25) Console.WriteLine("  The arrow snaps!");
                else RecoverArrowLater(arrowType);
                if (!tgt.Alive)
                {
                    if (arrowType == "blunt" && !tgt.IsUndead) KnockOut(tgt);
                    else HandleKill(tgt);
                }
            }
            else
            {
                Console.WriteLine("  MISS!");
                if (!P.HasReturningAmmo && Rng.Next(100) < 50) Console.WriteLine("  The arrow shatters.");
                else RecoverArrowLater(arrowType);
            }
        }

        // Special shot modes from feats
        var shotModes = new List<string>();
        if (P.HasFeat("Multishot")) shotModes.Add("[M]ultishot x4");
        if (P.HasFeat("Split Shot")) shotModes.Add("s[P]lit 2 targets");
        if (P.HasFeat("Piercing Shot")) shotModes.Add("p[I]ercing line");
        if (P.HasFeat("Folly of Arrows")) shotModes.Add("[F]olly 4d4 rain");
        if (shotModes.Count > 0)
        {
            Console.Write($"  Shot? [N]ormal  {string.Join("  ", shotModes)}: ");
            string sm = (GameIO.ReadLine() ?? "n").Trim().ToLower();
            if (sm.StartsWith("m") && P.HasFeat("Multishot"))
            {
                for (int i = 0; i < 4 && HasAmmo() && target.Alive; i++) QuickShot(target, costsArrow: true);
                return;
            }
            if (sm.StartsWith("p") && P.HasFeat("Split Shot"))
            {
                var others = Active.Where(en => en.Alive && en != target).ToList();
                QuickShot(target, costsArrow: true);
                if (others.Any() && HasAmmo())
                {
                    var t2 = others.Count == 1 ? others[0] : PickTarget(others);
                    if (t2 != null) QuickShot(t2, costsArrow: true);
                }
                return;
            }
            if (sm.StartsWith("i") && P.HasFeat("Piercing Shot"))
            {
                QuickShot(target, costsArrow: true);
                float tDist = PlayerPos.Feet(target.Position);
                var lineTargets = Active.Where(en => en.Alive && en != target
                    && target.Position.Feet(en.Position) <= 20f
                    && PlayerPos.Feet(en.Position) > tDist).ToList();
                foreach (var lt in lineTargets)
                {
                    Console.WriteLine($"  The arrow punches through toward {lt.Name}!");
                    QuickShot(lt, costsArrow: false);   // same arrow keeps flying
                }
                return;
            }
            if (sm.StartsWith("f") && P.HasFeat("Folly of Arrows"))
            {
                int volley = Rng.Next(1, 5) + Rng.Next(1, 5) + Rng.Next(1, 5) + Rng.Next(1, 5);
                Console.WriteLine($"  You loose {volley} arrows into the sky above {target.Name}!");
                for (int i = 0; i < volley && HasAmmo(); i++)
                {
                    var area = Active.Where(en => en.Alive
                        && Math.Abs(en.Position.X - target.Position.X) <= 2
                        && Math.Abs(en.Position.Y - target.Position.Y) <= 2).ToList();
                    if (!area.Any()) { Console.WriteLine("  The remaining arrows thud into empty ground."); break; }
                    QuickShot(area[Rng.Next(area.Count)], costsArrow: true);
                }
                return;
            }
        }

        int dmgMin, dmgMax;
        if (feet <= 14f) { dmgMin = 4; dmgMax = 12; }
        else if (feet <= 45f) { dmgMin = 2; dmgMax = 10; }
        else { dmgMin = 1; dmgMax = 5; }
        if (huntingBow) { dmgMin = 2; dmgMax = 6; }
        dmgMin = Math.Max(1, dmgMin + P.MinRangedDmgBonus + P.Dexterity + P.BowTrait());
        dmgMax = Math.Max(dmgMin, dmgMax + P.MinRangedDmgBonus + P.MaxRangedDmgBonus);
        int atkRoll = Rng.Next(P.MinRangedAtk, P.MaxRangedAtk + 1) + SlayerAtk() + HighGround() + arrowAtkBonus + (huntingBow ? 2 : 0) + P.Dexterity + P.BowTrait();
        int ddg = Rng.Next(target.MinDodge, target.MaxDodge + 1) + SizeDodgeRoll(target.Race, P.Race) - target.DodgePenalty;
        Console.WriteLine($"  BOW ({feet:F0}ft, dmg {dmgMin}-{dmgMax})! Roll {atkRoll} vs {target.Name}'s dodge {ddg}.");
        SpendArrow();
        Console.WriteLine($"  Arrows remaining: {P.ArrowCount} regular{(P.BluntArrows + P.BarbedArrows + P.SpiralArrows > 0 ? $" +{P.BluntArrows}b/{P.BarbedArrows}bar/{P.SpiralArrows}s" : "")}");
        if (atkRoll >= ddg && !EnemyBlocks(target, atkRoll, isRanged: true))
        {
            int dmg = Rng.Next(dmgMin, dmgMax + 1) + SlayerDmg() + arrowDmgBonus;
            dmg = ReduceByToughHide(target, dmg);
            Console.WriteLine($"  Arrow HIT! {dmg} dmg → {target.Name} HP:{target.HP - dmg}/{target.MaxHP}");
            target.HP -= dmg;
            target.ArrowsInBody++;
            if (!P.HasReturningAmmo && Rng.Next(100) < 25) Console.WriteLine("  The arrow snaps in the wound!");
            else RecoverArrowLater(arrowType);
            if (!target.Alive)
            {
                if (arrowType == "blunt" && !target.IsUndead) KnockOut(target);
                else HandleKill(target);
            }
        }
        else if (atkRoll < ddg)
        {
            Console.WriteLine("  Arrow MISS!");
            var stray = Bystander(target);
            if (stray != null && Rng.Next(100) < 20)
            {
                int strayDmg = Rng.Next(dmgMin, dmgMax + 1);
                strayDmg = ReduceByToughHide(stray, strayDmg);
                stray.HP -= strayDmg;
                Console.WriteLine($"  The stray arrow strikes {stray.Name} for {strayDmg}! HP:{stray.HP}/{stray.MaxHP}");
                if (!stray.Alive) HandleKill(stray);
                if (!P.HasReturningAmmo && Rng.Next(100) < 35) Console.WriteLine("  The arrow breaks.");
                else RecoverArrowLater(arrowType);
            }
            else if (!P.HasReturningAmmo && Rng.Next(100) < 50) Console.WriteLine("  The arrow shatters against the ground.");
            else RecoverArrowLater(arrowType);
        }

        // Double Tap: second arrow
        if (P.HasFeat("Double Tap") && P.ArrowCount > 0 && target.Alive)
        {
            Console.WriteLine("  [Double Tap] Second arrow!");
            P.ArrowCount--;
            Console.WriteLine($"  Arrows remaining: {P.ArrowCount}");
            int atk2 = Rng.Next(P.MinRangedAtk, P.MaxRangedAtk + 1) + SlayerAtk() + HighGround();
            int ddg2 = Rng.Next(target.MinDodge, target.MaxDodge + 1) + SizeDodgeRoll(target.Race, P.Race) - target.DodgePenalty;
            int d2Min, d2Max;
            if (feet <= 14f) { d2Min = 4; d2Max = 12; }
            else if (feet <= 45f) { d2Min = 2; d2Max = 10; }
            else { d2Min = 1; d2Max = 5; }
            d2Min = Math.Max(1, d2Min + P.MinRangedDmgBonus + P.Dexterity + P.BowTrait());
            d2Max = Math.Max(d2Min, d2Max + P.MinRangedDmgBonus + P.MaxRangedDmgBonus);
            Console.WriteLine($"  BOW ({feet:F0}ft)! Roll {atk2} vs dodge {ddg2}.");
            if (atk2 >= ddg2)
            {
                int dmg2 = Rng.Next(d2Min, d2Max + 1) + SlayerDmg();
                dmg2 = ReduceByToughHide(target, dmg2);
                Console.WriteLine($"  Arrow HIT! {dmg2} dmg → {target.Name} HP:{target.HP - dmg2}/{target.MaxHP}");
                target.HP -= dmg2;
                target.ArrowsInBody++;
                if (Rng.Next(100) < 25) Console.WriteLine("  The arrow snaps in the wound!");
                else RecoverArrowLater("regular");
                if (!target.Alive) HandleKill(target);
            }
            else
            {
                Console.WriteLine("  Arrow MISS!");
                if (Rng.Next(100) < 50) Console.WriteLine("  The arrow shatters against the ground.");
                else RecoverArrowLater("regular");
            }
        }
    }

    void DoWandAttack(Enemy target)
    {
        float feet = PlayerPos.Feet(target.Position);
        if (feet < 20f) { Console.WriteLine($"  Too close for wand! ({feet:F1}ft, min 20ft)"); return; }
        if (feet > 50f) { Console.WriteLine($"  Too far for wand! ({feet:F1}ft, max 50ft)"); return; }
        int atkRoll = Rng.Next(P.MinRangedAtk, P.MaxRangedAtk + 1) + SlayerAtk() + HighGround() + P.SpellAttackBonus;
        int ddg = Rng.Next(target.MinDodge, target.MaxDodge + 1) + SizeDodgeRoll(target.Race, P.Race) - target.DodgePenalty;
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
        int atkRoll = Rng.Next(P.MinAttack, P.MaxAttack + 1) + SlayerAtk() + HighGround();
        int ddg = Rng.Next(target.MinDodge, target.MaxDodge + 1) + SizeDodgeRoll(target.Race, P.Race) - target.DodgePenalty;
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
        int gRoll = Rng.Next(minG, maxG + 1) - P.SprintPenalty + SlayerAtk() + HighGround();
        P.SprintPenalty = 0;
        if (P.EnlargeActive) gRoll *= 2;
        int dRoll = Rng.Next(target.MinDodge, target.MaxDodge + 1);
        Console.WriteLine($"  Grapple! Roll {gRoll} vs {target.Name}'s dodge {dRoll}.");
        if (gRoll < dRoll) { Console.WriteLine("  Grapple FAILED!"); return; }

        target.Grappled = true;
        Console.WriteLine($"  Grapple SUCCESS! {target.Name} is grappled.");

        string gOpts = P.HasFeat("Judo") ? "[H]old  [T]hrow  [D]isarm" : "[H]old  [T]hrow";
        Console.Write($"  Option: {gOpts}: ");
        string go = (GameIO.ReadLine() ?? "h").Trim().ToLower();

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
                string lb = (GameIO.ReadLine() ?? "s").Trim().ToLower();
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
                if ((GameIO.ReadLine() ?? "").Trim().ToLower() == "y")
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
        if ((GameIO.ReadLine() ?? "").Trim().ToLower() == "y") DoGrapple(target);
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
        int SpellAtkRoll() { int raw = Rng.Next(P.MinSpellAtk, P.MaxSpellAtk + 1); lastSpellCrit = raw == P.MaxSpellAtk; lastSpellFumble = raw == P.MinSpellAtk; return raw + SlayerAtk() + HighGround() + P.Intelligence + P.Smarts; }
        int SpellDmg(int dmg, string element) { if (P.HasFeat("Magical Overflow")) dmg *= 2; if (P.HasFeat("Elemental") && P.ElementalFocus == element) dmg += 2; dmg += P.MinSpellDmgBonus + P.MaxSpellDmgBonus + SlayerDmg(); return dmg; }
        int ExtDur(int turns) => (P.HasFeat("Extended Magi") ? turns * 2 : turns) + P.SpellDurBonus;
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
                string fb = (GameIO.ReadLine() ?? "s").Trim().ToLower();
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
                if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int rdi) || rdi < 1 || rdi > corpses.Count) { Console.WriteLine("  Invalid."); break; }
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
                string tsStat = (GameIO.ReadLine() ?? "1").Trim();
                P.TrueSightStat = tsStat switch { "1" or "attack" => "attack", "2" or "dodge" => "dodge", "3" or "block" => "block", "4" or "parry" => "parry", "5" or "grapple" => "grapple", "6" or "spell" => "spell", _ => "attack" };
                Console.Write("  Boost [M]in or Ma[X]? ");
                string tsMinMax = (GameIO.ReadLine() ?? "x").Trim().ToLower();
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

}
