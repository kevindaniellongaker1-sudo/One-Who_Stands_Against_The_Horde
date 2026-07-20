using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

// ═════════════════════════════════════════════════════════════════════════
//  CombatHelpers.cs — the shared pieces the rest of combat leans on.
// ═════════════════════════════════════════════════════════════════════════
//
//  Mostly short methods, but they are the seams where the rules meet the
//  dice. If a rule feels wrong across the board, it is usually in here.
//
//  REACH & RANGE   InMeleeReach   adjacent (further with Extended Grasp)
//                  CanReactToFlee who may take a parting shot: melee reach,
//                                 or a thrown weapon within 20ft. NOT the
//                                 whole map — that was a real bug once.
//                  HasThrowable   does it still hold something to throw
//  DEFENCE MATHS   PDodgeSize     folds together high ground, size, the
//                                 dodge-vs-large racial bonus and frenzy.
//                                 EVERY player dodge roll goes through it,
//                                 so it is the one place to touch dodge.
//  TRAITS          MeleeTraitBonus picks which core trait powers the weapon
//                  in hand: two-handed 1.5xStr, light Str/Dex, finesse
//                  Smarts, non-lethal Wis.
//  CHI             DoChi is the free-action menu; the reroll flags it sets
//                  are consumed at the dodge/block/parry sites via ChiReroll.
//  FEAR            RadiateFear (rage and frenzy terror) and DeathTonePulse —
//                  dice vs each foe's HP, then a 50/50 fight-or-flight coin.
//  SONGS           SlayerAtk / SlayerDmg sum across the WHOLE party's active
//                  songs; SweepWarRhythm kills a buff when its drummer dies.
//  MITIGATION      MitigateMagic — absorption (armor + innate racial), the
//                  Mirror Shield reflect, and typed damage reduction.
//
// ═════════════════════════════════════════════════════════════════════════

partial class CombatSession
{
    Enemy? Bystander(Enemy target) =>
        Active.FirstOrDefault(a => a.Alive && a != target && a.Position.IsCardinalAdjacent(target.Position));

    // ── ARMOR vs MAGIC ────────────────────────────────────────────────────
    // Applies rune absorption (damage → 0, +1 use), the lightning-vs-metal
    // rule (full damage + burns), then the armor's spell/prayer reduction.
    // Which element a robe drinks outright. "prayer" matches any hostile prayer
    // (Holy Vestments); "song" is reserved for the Bard Vestments.
    static string RobeElement(string robe) => robe switch
    {
        "Fire Robes"      => "fire",
        "Frost Robes"     => "ice",
        "Lightning Robes" => "lightning",
        "Air Robe"        => "air",
        "Unholy Robe"     => "negative",
        "Holy Vestments"  => "prayer",
        "Bard Vestments"  => "song",
        _ => "",
    };

    int MitigateMagic(int dmg, string channel, string element = "")
    {
        // Enchanted armor and shields can NEGATE hostile magic outright:
        // 2.5% per total enchant level
        int negatePct = (int)((P.ArmorEnchant + P.ShieldEnchant) * 2.5);
        if (negatePct > 0 && Rng.Next(100) < negatePct)
        {
            Console.WriteLine($"  Your enchanted gear FLARES — the {channel} is negated entirely!");
            return 0;
        }
        // An elemental robe drinks its own element completely
        foreach (var worn in new[] { P.MainArmor, P.UnderArmor, P.RobeWorn })
        {
            string re = RobeElement(worn ?? "");
            if (re.Length == 0) continue;
            bool match = re == element
                || (re == "prayer" && channel == "prayer")
                || (re == "ice" && element is "frost" or "ice")
                || (re == "air" && element is "wind" or "air");
            if (match)
            {
                if (channel == "spell") P.SpellUses++; else P.PrayerUses++;
                Console.WriteLine($"  Your {worn} drinks the {(element.Length > 0 ? element : channel)} harmlessly! (+1 {channel} use)");
                return 0;
            }
        }
        int absorbPct = Math.Min(100, P.ArmorAbsorbPct + P.RaceAbsorbPct);
        if (absorbPct > 0 && Rng.Next(100) < absorbPct)
        {
            // Monk Garbs turn absorbed magic into CHI for a monk
            if (P.IsMonk && (P.MainArmor == "Monk Garbs" || P.UnderArmor == "Monk Garbs" || P.RobeWorn == "Monk Garbs"))
            {
                P.ChiUses++;
                Console.WriteLine($"  Your Monk Garbs drink the {channel} — it flows into your CHI! ({P.ChiUses})");
            }
            else if (channel == "spell") { P.SpellUses++; Console.WriteLine($"  You ABSORB the {channel}! (+1 {channel} use)"); }
            else { P.PrayerUses++; Console.WriteLine($"  You ABSORB the {channel}! (+1 {channel} use)"); }
            return 0;
        }
        // Mirror Shield: 35% chance to hurl the magic back at its caster
        if (P.OffHandShieldName == "Mirror Shield" && _atkEnemy != null && Rng.Next(100) < 35)
        {
            _atkEnemy.HP -= dmg;
            Console.WriteLine($"  Your Mirror Shield REFLECTS the {channel} back at {_atkEnemy.Name} for {dmg}! HP:{_atkEnemy.HP}/{_atkEnemy.MaxHP}");
            if (!_atkEnemy.Alive) HandleKill(_atkEnemy);
            return 0;
        }
        if (channel == "spell" && element == "lightning" && P.ArmorMetal)
        {
            P.BurningDmg = Math.Max(P.BurningDmg, Rng.Next(1, 5));
            P.BurningTurns = Math.Max(P.BurningTurns, Rng.Next(1, 6));
            Console.WriteLine("  Lightning courses through your metal armor — full damage and searing burns!");
            return dmg;
        }
        int dr = channel == "spell" ? P.ArmorSpellDR : P.ArmorPrayerDR;
        return dr > 0 && dmg > 0 ? Math.Max(1, dmg - dr) : dmg;
    }

    void RecoverArrowLater(string type)
    {
        switch (type)
        {
            case "blunt":  P.RecoverBlunt++;  break;
            case "barbed": P.RecoverBarbed++; break;
            case "spiral": P.RecoverSpiral++; break;
            default:       P.RecoverRegular++; break;
        }
    }

    void ResolveThrownLanding(bool hit, Enemy target, string weaponType, int dmgMin, int dmgMax)
    {
        if (P.HasReturningAmmo)
        {
            // Magic-crafted: the weapon whips back to the thrower's hand
            Console.WriteLine($"  The {weaponType} whirls back to your hand!");
            switch (weaponType)
            {
                case "Goblin Dagger": P.DaggerCount++; break;
                case "Hand Axe": P.AxeCount++; break;
                default: if (P.HeldWeapon == null) P.HeldWeapon = weaponType; break;
            }
            return;
        }
        if (hit)
        {
            if (Rng.Next(100) < 25) { Console.WriteLine($"  The {weaponType} breaks on impact!"); return; }
        }
        else
        {
            var stray = Bystander(target);
            if (stray != null && Rng.Next(100) < 20)
            {
                int d = Rng.Next(dmgMin, dmgMax + 1);
                d = ReduceByToughHide(stray, d);
                stray.HP -= d;
                Console.WriteLine($"  The stray {weaponType} hits {stray.Name} for {d}! HP:{stray.HP}/{stray.MaxHP}");
                if (!stray.Alive) HandleKill(stray);
                if (Rng.Next(100) < 35) { Console.WriteLine($"  The {weaponType} breaks!"); return; }
            }
            else if (Rng.Next(100) < 50) { Console.WriteLine($"  The {weaponType} shatters on the ground!"); return; }
        }
        var land = RandomAdjacent(target.Position);
        GroundWeapons.Add((land, weaponType));
        Console.WriteLine($"  {weaponType} lands at ({land.X},{land.Y}).");
    }

    // ── HIGH GROUND & SIZE HELPERS ────────────────────────────────────────

    int HighGround() => P.Climbed ? 2 : 0;   // +2 to attack rolls from up high

    // Melee / grapple reach: cardinally adjacent normally. Extended Grasp adds
    // the corner (diagonal) cells and one extra square straight out.
    bool InMeleeReach(Enemy e)
    {
        // The dragon: melee reaches its 3x3 body edge — never while it flies.
        // The swallowed are a special case: they are VERY much in reach.
        if (e is Dragon d3)
        {
            if (P.SwallowedBy == d3) return true;
            if (d3.Flying) return false;
            return d3.EdgeDist(PlayerPos) <= (P.HasFeat("Extended Grasp") ? 1 : 0);
        }
        int dx = Math.Abs(e.Position.X - PlayerPos.X);
        int dy = Math.Abs(e.Position.Y - PlayerPos.Y);
        if (P.HasFeat("Extended Grasp"))
            return Math.Max(dx, dy) <= 1 || (dx + dy == 2 && (dx == 0 || dy == 0));
        return dx + dy == 1;
    }

    // Dodge adjustment sampled from the (min,max) size window
    int SizeDodgeRoll(string defRace, string atkRace)
    {
        var (mn, mx) = SizeRules.DodgeBonus(defRace, atkRace);
        if (mn == mx) return mn;
        return Rng.Next(Math.Min(mn, mx), Math.Max(mn, mx) + 1);
    }

    // Player's dodge bonus vs whichever enemy is currently acting (+1 while climbed)
    // ── CHI (Martial Artists + Vow of Silence) ──────────────────────────────
    // Spending chi is a FREE action, usable any time — including in reaction to
    // an enemy's attack. Rerolls are handled by the flags below, which the
    // dodge/block/parry/attack sites consume.
    public bool ChiRerollDodge, ChiRerollBlock, ChiRerollParry, ChiRerollAttack, ChiRerollRunAway;
    public bool ChiDoubleMonkDmg, ChiDoubleNonLethal;

    // Every player dodge roll funnels through here so the chi dodge-reroll
    // has one place to fire, whatever prompted the dodge.
    int PDodgeRoll()
    {
        int r = Rng.Next(P.MinDodge, P.MaxDodge + 1);
        return ChiReroll(ref ChiRerollDodge, r, P.MinDodge, P.MaxDodge, "dodge");
    }

    void DoChi(List<Enemy> alive)
    {
        if (P.ChiUses <= 0) { Console.WriteLine("  You have no chi left."); return; }
        Console.WriteLine($"\n  ── CHI ({P.ChiUses} left) — free action ──");
        var uses = new (string Key, string Desc)[]
        {
            ("attack",     "an extra attack right now"),
            ("flurry",     "a flurry of blows (five attacks)"),
            ("grapple",    "a free grapple attempt"),
            ("throw",      "a free grapple throw"),
            ("limb",       "a free limb break"),
            ("disarm",     "a free disarm"),
            ("move",       "a free move"),
            ("sprint",     "a free sprint (double distance, no penalty)"),
            ("charge",     "a free charge"),
            ("double",     "double damage with your monk weapon this turn"),
            ("nonlethal",  "double damage on non-lethal attacks this turn"),
            ("heal",       "heal yourself 2d3"),
            ("rrdodge",    "reroll your next dodge"),
            ("rrblock",    "reroll your next block"),
            ("rrparry",    "reroll your next parry"),
            ("rrattack",   "reroll your next attack"),
            ("rrflee",     "reroll your next run away"),
        };
        for (int i = 0; i < uses.Length; i++)
            Console.WriteLine($"  [{i + 1,2}] {uses[i].Key,-10} — {uses[i].Desc}");
        Console.Write($"  Spend chi on (1-{uses.Length}, or [c]ancel): ");
        string r = (GameIO.ReadLine() ?? "").Trim().ToLower();
        if (r.StartsWith("c") || r.Length == 0) return;
        string key;
        if (int.TryParse(r, out int ci) && ci >= 1 && ci <= uses.Length) key = uses[ci - 1].Key;
        else { var m = uses.FirstOrDefault(u => u.Key.StartsWith(r)); if (m.Key == null) return; key = m.Key; }

        // The point is charged only once the action is fully CONFIRMED —
        // no reach, no target, or a cancelled pick all cost nothing.
        bool needsReach = key is "attack" or "flurry" or "grapple" or "throw" or "limb" or "disarm";
        if (needsReach && !alive.Any(InMeleeReach))
        { Console.WriteLine("  Nobody within reach — your chi is not spent."); return; }

        Enemy? t = null;
        if (needsReach)
        {
            t = PickTarget(alive.Where(InMeleeReach).ToList());
            if (t == null) { Console.WriteLine("  No target chosen — your chi is not spent."); return; }
        }
        else if (key == "charge")
        {
            t = PickTarget(alive);
            if (t == null) { Console.WriteLine("  No target chosen — your chi is not spent."); return; }
        }

        void Spend() => Console.WriteLine($"  You focus your chi. ({--P.ChiUses} left)");
        switch (key)
        {
            case "attack":
                Spend();
                DoAttack(t!);
                break;
            case "flurry":
                Spend();
                Console.WriteLine("  CHI FLURRY — five strikes!");
                for (int i = 0; i < 5 && t!.Alive; i++) DoAttack(t);
                break;
            case "grapple":
                Spend();
                DoGrapple(t!);
                break;
            case "throw":
            case "limb":
            case "disarm":
                Spend();
                Console.WriteLine($"  Chi-powered {key}!");
                DoAttack(t!);
                break;
            case "move":
            {
                Spend();
                int mv = Rng.Next(P.MinMovement, P.MaxMovement + 1) + P.MovementBonus + P.MoveTrait();
                Console.WriteLine($"  Chi step: {mv} square(s).");
                StepMovement(mv);
                break;
            }
            case "sprint":
            {
                Spend();
                int sp = Math.Max(1, (Rng.Next(1, 7) + Rng.Next(1, 7) + P.MovementBonus + P.SprintBonus + P.SprintTrait()) * 2);
                Console.WriteLine($"  CHI SPRINT: {sp} square(s), no penalty!");
                StepMovement(sp);
                P.SprintPenalty = 0;
                break;
            }
            case "charge":
                Spend();
                while (PlayerPos.ManhattanDist(t!.Position) > 1) PlayerPos = StepToward(PlayerPos, t.Position);
                Console.WriteLine($"  Chi charge into {t.Name}!");
                DoAttack(t);
                break;
            case "double":     Spend(); ChiDoubleMonkDmg = true;  Console.WriteLine("  Your monk weapon hums — double damage this turn."); break;
            case "nonlethal":  Spend(); ChiDoubleNonLethal = true; Console.WriteLine("  Your non-lethal strikes hit double this turn."); break;
            case "heal":
            {
                Spend();
                int h = Rng.Next(1, 4) + Rng.Next(1, 4);
                P.HP = Math.Min(P.MaxHP, P.HP + h);
                Console.WriteLine($"  Inner calm mends {h} HP. HP:{P.HP}/{P.MaxHP}");
                break;
            }
            case "rrdodge":  Spend(); ChiRerollDodge = true;  Console.WriteLine("  Ready to reroll your next dodge."); break;
            case "rrblock":  Spend(); ChiRerollBlock = true;  Console.WriteLine("  Ready to reroll your next block."); break;
            case "rrparry":  Spend(); ChiRerollParry = true;  Console.WriteLine("  Ready to reroll your next parry."); break;
            case "rrattack": Spend(); ChiRerollAttack = true; Console.WriteLine("  Ready to reroll your next attack."); break;
            case "rrflee":   Spend(); ChiRerollRunAway = true; Console.WriteLine("  Ready to reroll your next escape."); break;
        }
    }

    // ── ELIXIRS: the dragon-hoard potions. Each lasts a rolled number of
    // turns; when it expires its gifts are taken back. Returns extra actions
    // granted by the draught (Rapid and friends).
    int DrinkElixir()
    {
        var carried = P.SpecialPotions.Where(kv => kv.Value > 0).Select(kv => kv.Key).OrderBy(n => n).ToList();
        if (!carried.Any()) { Console.WriteLine("  You carry no elixirs."); return 0; }
        Console.WriteLine("\n  ── ELIXIRS ──");
        for (int i = 0; i < carried.Count; i++)
            Console.WriteLine($"  [{i + 1,2}] {carried[i]} x{P.SpecialPotions[carried[i]]}");
        Console.Write($"  Drink which (1-{carried.Count}, or Enter to cancel): ");
        if (!int.TryParse(GameIO.ReadLine()?.Trim(), out int pi2) || pi2 < 1 || pi2 > carried.Count) return 0;
        string name = carried[pi2 - 1];

        if (name == "Bright Soul")
        { Console.WriteLine("  Save it. It knows its moment — it wakes on its own when death takes you."); return 0; }

        P.SpecialPotions[name]--;
        int extra = 0;
        void Timed(int turns, Action<Player> expire) => P.ActiveElixirs.Add((name, turns, expire));
        switch (name)
        {
            case "Training Potion":
                P.SavedStatPoints += 4;
                Console.WriteLine($"  Knowledge settles into your bones — +4 stat points ({P.SavedStatPoints} banked).");
                break;
            case "Rapid":
            {
                extra = Rng.Next(2, 5);
                P.ElixirDoubleMove = true;
                int t = Rng.Next(3, 7);
                Timed(t, p => p.ElixirDoubleMove = false);
                Console.WriteLine($"  The world SLOWS — +{extra} actions now, double movement for {t} turns!");
                break;
            }
            case "Oak Wood":
            {
                int dr = Rng.Next(2, 4), t = 5;
                P.ElixirHealMin = 2; P.ElixirHealMax = 10;
                P.ArmorDamageReduction += dr; P.ElixirDR += dr;
                Timed(t, p => { p.ElixirHealMin = p.ElixirHealMax = 0; p.ArmorDamageReduction -= dr; p.ElixirDR -= dr; });
                Console.WriteLine($"  Your skin grains like heartwood — heal 2-10/turn, -{dr} damage taken, {t} turns. Trees welcome you.");
                break;
            }
            case "Iron Rock":
            {
                int dr = Rng.Next(3, 6), t = 6;
                P.ArmorDamageReduction += dr; P.ElixirDR += dr;
                Timed(t, p => { p.ArmorDamageReduction -= dr; p.ElixirDR -= dr; });
                Console.WriteLine($"  You harden to living iron — -{dr} damage taken for {t} turns (but lightning will find you, and rivers are misery).");
                break;
            }
            case "Red Rage":
                P.RagePoints = Math.Max(P.RagePoints, 1 + (Math.Max(0, P.Level - 2)) / 4 + 1);
                extra = 1;
                Console.WriteLine($"  Fury refills your veins — rages restored ({P.RagePoints}), +1 action!");
                break;
            case "Blue Sky":
                P.SpellUses = P.MaxSpellUses(); extra = 1;
                Console.WriteLine($"  The heavens open in your mind — spells restored ({P.SpellUses}), +1 action!");
                break;
            case "Holy Rights":
                P.PrayerUses = P.MaxPrayerUses(); extra = 1;
                Console.WriteLine($"  Grace refills the well — prayers restored ({P.PrayerUses}), +1 action!");
                break;
            case "Notes Melody":
            {
                P.SongTokens = P.MaxSongTokens();
                P.ElixirSongDouble = true;
                int t = 6;
                Timed(t, p => p.ElixirSongDouble = false);
                Console.WriteLine($"  Music floods back — songs restored ({P.SongTokens}) and they LINGER double for {t} turns!");
                break;
            }
            case "Pirate Booty":
                P.DuelistPoints += 3; extra = 1;
                Console.WriteLine($"  Swagger and cunning — +3 duelist points ({P.DuelistPoints}), +1 action!");
                break;
            case "Reflex of the Tiger":
            {
                int t = 4; extra = 1;
                P.ElixirAtk += 3; P.ElixirDodge += 4; P.ElixirDmg += 2;
                Timed(t, p => { p.ElixirAtk -= 3; p.ElixirDodge -= 4; p.ElixirDmg -= 2; });
                Console.WriteLine($"  Muscles coil like a great cat's — +3 attack, +4 dodge, +2 damage for {t} turns, +1 action!");
                break;
            }
            case "Knight's":
            {
                int a = Rng.Next(1, 6), d = Rng.Next(2, 4), t = 5;
                int heal = Rng.Next(2, 7) + Rng.Next(8, 15);
                P.ElixirAtk += a; P.ElixirDmg += d;
                P.HP = Math.Min(P.MaxHP, P.HP + heal);
                Timed(t, p => { p.ElixirAtk -= a; p.ElixirDmg -= d; });
                Console.WriteLine($"  Valor like plate around the heart — +{a} attack, +{d} damage for {t} turns, {heal} HP restored. HP:{P.HP}/{P.MaxHP}");
                break;
            }
            case "Rogue's Rose":
            {
                int dg = Rng.Next(2, 7), a = Rng.Next(2, 5), t = 4; extra = 1;
                P.ElixirDodge += dg; P.ElixirAtk += a; P.ElixirDoubleDmgPct = 25;
                Timed(t, p => { p.ElixirDodge -= dg; p.ElixirAtk -= a; p.ElixirDoubleDmgPct = 0; });
                Console.WriteLine($"  A thorned sweetness — +{dg} dodge, +{a} attack, 25% double strikes for {t} turns, +1 action!");
                break;
            }
            default:
                Console.WriteLine("  It tastes of nothing. (Unknown elixir — no effect.)");
                P.SpecialPotions[name]++;
                break;
        }
        return extra;
    }

    // Which core trait powers the melee weapon in hand (rules as written):
    // two-handed → 1.5×Strength; unarmed/light → better of Str/Dex (Wis for
    // non-lethal); finesse weapons → Smarts when it beats the others.
    int MeleeTraitBonus()
    {
        string w = P.HeldWeapon ?? "";
        string lw = w.ToLowerInvariant();
        bool light = lw is "" or "unarmed" or "dagger" or "knife" or "short sword" or "rapier" or "staff";
        bool finesse = lw is "dagger" or "knife" or "rapier" or "wand" or "whip" or "pickaxe" or "war pick" or "spike chain";
        bool nonLethal = lw is "" or "unarmed" or "staff" or "ogre club" or "mace" or "war mace" or "warhammer";
        int best = light ? P.LightWeaponTrait() : P.Strength;
        if (nonLethal) best = Math.Max(best, P.NonLethalTrait());
        if (finesse && P.Smarts > best) best = P.Smarts;
        if (Shop.TwoHanded.Contains(w)) best = Math.Max(best, P.TwoHandTrait());
        return best;
    }

    // Does this foe still hold something it can throw?
    bool HasThrowable(Enemy e) => e switch
    {
        Troll t          => t.EquippedAxes > 0,
        RogueGoblin r    => r.DaggerCount > 0,
        HobgoblinThief h => h.DaggerCount > 0,
        GiantDuelist g   => g.HandAxes > 0,
        OrcBarbarian o   => o.HandAxeCount > 0,
        _                => false,
    };

    // A fleeing player only provokes foes that can actually reach them: melee
    // range (2 squares for an Ogre's club sweep), or a thrown weapon's 20ft
    // for anyone still carrying one. Not from clear across the map.
    bool CanReactToFlee(Enemy e)
    {
        int d = e.Position.ManhattanDist(PlayerPos);
        if (d <= 1) return true;                       // adjacent: melee
        if (e is Ogre && d <= 2) return true;          // club sweep reach
        return HasThrowable(e) && e.Position.Feet(PlayerPos) <= 20f;
    }

    // Chi reroll of a defensive roll: take the better of two rolls.
    int ChiReroll(ref bool flag, int roll, int min, int max, string what)
    {
        if (!flag) return roll;
        flag = false;
        int again = Rng.Next(min, max + 1);
        Console.WriteLine($"  [Chi] Reroll {what}: {roll} → {again} (keeping the better).");
        return Math.Max(roll, again);
    }

    int PDodgeSize()
    {
        int b = P.Climbed ? 1 : 0;
        b += P.DodgeTrait() + P.ElixirDodge;   // traits + any elixir coursing through you
        if (_atkEnemy != null)
        {
            b += SizeDodgeRoll(P.Race, _atkEnemy.Race);
            if (SizeRules.Of(_atkEnemy.Race) == 2) b += P.DodgeVsLarge;   // extra dodge vs large
        }
        if (P.Frenzied) b -= 2;   // Troll frenzy: reckless defense
        return b;
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

    // ── FEAR: rage and Troll frenzy terrify nearby foes ─────────────────────
    // Roll 2d4 (+1.5x Charisma) against each foe's HP. Those at or under it are
    // gripped: a 50/50 coin decides fight (blindly attack the source) or
    // flight (blindly run from it) for 1d4 turns.
    void RadiateFear(string sourceName)
    {
        int roll = Rng.Next(1, 5) + Rng.Next(1, 5) + P.FearTrait() + P.SongFear;
        Console.WriteLine($"  ☠ {sourceName} radiates terror! Dread roll {roll} vs nearby foes' HP.");
        foreach (var e in Active.Where(e => e.Alive && !e.IsPlayerAlly
                                            && PlayerPos.ManhattanDist(e.Position) <= 15).ToList())
        {
            if (e.FearTurns > 0) continue;                  // already gripped
            // Among animals only the skittish fear a war cry — deer and boar.
            // Wolves and bears do not rattle.
            if (e.IsWildlife && e is not Deer && e is not Boar) continue;
            if (e.FearImmune) { Console.WriteLine($"  {e.Name} is unshaken."); continue; }
            if (e.HP > roll) continue;                      // too tough to rattle
            e.FearTurns = Rng.Next(1, 5);
            e.FearFight = Rng.Next(2) == 0;                 // 50/50 fight or flight
            e.FearSource = P;
            Console.WriteLine($"  {e.Name} ({e.HP} HP) is {(e.FearFight ? "driven to blind FURY — it attacks recklessly" : "seized by PANIC — it flees blindly")} for {e.FearTurns} turn(s)!");
        }
    }

    void DeathTonePulse()
    {
        int dice = P.FearDiceCount();
        int roll = P.SongFear;                                    // base song fear vs HP
        // "+1 max fear" purchases widen only the first die, not every die
        for (int d = 0; d < dice; d++) roll += Rng.Next(1, 7);
        roll += Rng.Next(0, P.SongFearMax + 1);   // "max fear" raises the overall top
        int radius = P.SongRadiusSquares();
        Console.WriteLine($"  ♪ DEATHTONE! Dread chord: {roll} ({dice}d6) within {radius} squares. Enemies with {roll} HP or less flee!");
        foreach (var fe in Active.Where(e => e.Alive && !e.IsPlayerAlly && e.HP <= roll
                                             && PlayerPos.ManhattanDist(e.Position) <= radius).ToList())
        {
            if (fe.IsWildlife && fe is not Deer && fe is not Boar) continue;   // wolves/bears don't rattle
            if (fe.FearImmune) { Console.WriteLine($"  {fe.Name} is unshaken by the dread chord."); continue; }
            fe.Fled = true;
            Console.WriteLine($"  {fe.Name} ({fe.HP} HP) is gripped by mortal dread and flees!");
        }
    }

    Enemy? PickTarget(List<Enemy> alive)
    {
        if (alive.Count == 0) return null;   // nothing to aim at — never prompt
        if (alive.Count == 1) return alive[0];
        for (int i = 0; i < alive.Count; i++) Console.Write($"[{i + 1}]{alive[i].Name}  ");
        Console.WriteLine();
        Console.Write("  Target #: ");
        if (int.TryParse(GameIO.ReadLine()?.Trim(), out int ti) && ti >= 1 && ti <= alive.Count)
            return alive[ti - 1];
        Console.WriteLine("  Invalid target.");
        return null;
    }

    // ── ATTACK ────────────────────────────────────────────────────────────

}
