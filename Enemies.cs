using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

// ═════════════════════════════════════════════════════════════════════════
//  Enemies.cs — the Enemy base class and every creature in the game.
// ═════════════════════════════════════════════════════════════════════════
//
//  Enemy holds the shared shape (HP, rolls, position, traits, status). Each
//  subclass sets its own numbers in its constructor; the AI that drives them
//  lives in Combat.cs (EnemyTurn), not here. So this file answers "what is
//  a troll", and Combat.cs answers "what does a troll do".
//
//  THE ONE RULE THAT BITES PEOPLE:
//    Alive is a COMPUTED property (HP > 0 && !Fled). You cannot assign it.
//    To remove something from a fight, set Fled = true (it ran) or reduce HP
//    (it fell). Writing `e.Alive = false` will not compile — that's on purpose.
//
//  THE FAMILIES (subclass to vary, don't clone):
//    Goblin      + SpellGoblin, RogueGoblin, GoblinWarrior, GoblinShaman
//    Hobgoblin   + Fighter, Thief, Cleric
//    Orc         + OrcBarbarian, OrcMonk, OrcPriestess, OrcRanger
//    Troll       + Warrior, Priest, Musician, Necromancer   (RandType rolls)
//    Ogre        + Warrior, Duelist, Berserker              (RandType rolls)
//    GiantEnemy  + Mage, Priest, Duelist                    (wave 51+)
//    Wildlife    Deer, Wolf, Boar, Bear — IsWildlife, live by instinct and
//                are excluded from victory checks (you can ignore them)
//
//  TRAITS: ApplyNpcTraits() gives every type a 0-4 spread across the eight
//  core traits, keyed on TypeName — brutes lean Strength/Constitution,
//  casters Intelligence/Wisdom, skirmishers Dexterity/Agility. It runs from
//  the constructor, so a new type without a case gets the modest default.
//
//  ABILITY POOLS: caster enemies get SpellUsesLeft / PrayerUsesLeft /
//  SongUsesLeft, set from the wave number in BuildGroup. When a pool empties
//  the creature must fall back on something physical — see the Troll
//  Musician drawing a hand axe once its songs run dry.
//
// ═════════════════════════════════════════════════════════════════════════

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
    public bool IsWildlife = false;   // neutral beasts: don't block wave victory
    public int SlideDir = 0;          // wall-following direction while pathing
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

    // ── Core traits (0-4, fitted to the creature) ──
    public int Strength = 0, Dexterity = 0, Intelligence = 0, Wisdom = 0;
    public int Constitution = 0, Smarts = 0, Charisma = 0, Agility = 0;
    public bool FearImmune = false;
    public bool Wading = false;       // mid-river: the next step is spent wading
    // ── Ecosystem state ──
    public Enemy? BeastTarget;        // what this creature is hunting / charging
    public Enemy? ProvokedBy;         // the last creature that injured it (retaliation)
    public bool ProvokedByPlayer;     // a player injured it — beasts hold grudges
    public int CorpseMeals = 0;       // wolf: quarter-corpses left to devour (4 per kill)
    // ── Late-game equipment (wave 71+, see OutfitLateGameHorde) ──
    public string ArmorWorn = "";     // what it wears — dropped as loot on death
    public int ArmorDR = 0;           // physical damage reduction from that armor
    public int SpellAbsorbPct = 0;    // robes: chance to shrug off player magic
    public int ChiLeft = 0;           // Orc Monks: chi pool (80% of lowest player level)
    public bool LichBound = false;    // necromancer trolls at 91+: touch heals them
    public int ThrowPotions = 0;      // giant mages at 110+: volatile flasks to hurl
    // Fear (rage/frenzy/DeathTone): fight = blindly attack the source,
    // flight = blindly run from it, for FearTurns turns.
    public int FearTurns = 0;
    public bool FearFight = false;
    public object? FearSource = null;

    public Enemy(string name, string typeName)
    {
        Name = name; TypeName = typeName;
        ApplyNpcTraits();
    }

    // Trait spreads per creature type: brutes lean Strength/Constitution,
    // casters Intelligence/Wisdom, skirmishers Dexterity/Agility.
    void ApplyNpcTraits()
    {
        void T(int s, int d, int c, int i, int w, int m, int ch, int a)
        { Strength = s; Dexterity = d; Constitution = c; Intelligence = i;
          Wisdom = w; Smarts = m; Charisma = ch; Agility = a; }
        switch (TypeName)
        {
            case "Goblin":           T(1, 3, 1, 0, 0, 1, 0, 3); break;
            case "SpellGoblin":      T(0, 2, 1, 4, 1, 3, 1, 2); break;
            case "GoblinShaman":     T(0, 1, 1, 2, 4, 2, 2, 1); break;
            case "Hobgoblin":        T(3, 2, 3, 0, 0, 1, 1, 2); break;
            case "Orc":              T(4, 1, 3, 0, 0, 0, 1, 1); break;
            case "OrcBarbarian":     T(4, 2, 4, 0, 0, 0, 2, 2); break;
            case "Troll":            T(4, 1, 4, 0, 1, 0, 0, 1); break;
            case "TrollWarrior":     T(4, 2, 4, 0, 0, 1, 1, 1); break;
            case "TrollPriest":      T(2, 1, 3, 1, 4, 2, 2, 1); break;
            case "TrollMusician":    T(2, 2, 3, 1, 2, 2, 4, 2); break;
            case "NecromancerTroll": T(2, 1, 3, 4, 2, 4, 2, 1); break;
            case "Ogre":             T(4, 0, 4, 0, 0, 0, 0, 0); break;
            case "Giant":            T(4, 1, 4, 0, 1, 1, 1, 0); break;
            case "GiantMage":        T(3, 1, 3, 4, 1, 3, 1, 1); break;
            case "GiantPriest":      T(3, 1, 3, 1, 4, 2, 2, 1); break;
            case "Deer":             T(0, 3, 1, 0, 1, 0, 0, 4); break;
            case "Wolf":             T(2, 3, 2, 0, 1, 1, 1, 4); break;
            case "Boar":             T(3, 2, 3, 0, 0, 0, 0, 2); break;
            case "Bear":             T(4, 1, 4, 0, 1, 0, 1, 2); break;
            default:                 T(1, 1, 1, 0, 0, 1, 0, 1); break;
        }
    }

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
    // One school per spell-granting feat (bar Lich Bound / Advanced Cantrips):
    // mage (the classic elemental trio), cantrips, necromancer, divination.
    public string School;
    public SpellGoblin(Random rng, string name) : base(rng, name)
    {
        TypeName = "Spell Goblin";
        School = new[] { "mage", "cantrips", "necromancer", "divination" }[rng.Next(4)];
        SpellName = School switch
        {
            "cantrips"    => "Spark Volley",
            "necromancer" => "Negative Touch",
            "divination"  => "Foretold Strike",
            _             => new[] { "Fire Blast", "Chain Lightning", "Frost Burst" }[rng.Next(3)],
        };
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
    public bool DrewAxe = false;                 // slung the drum for a hand axe
    public int FearSongCooldown = 0;             // won't spam the dread chord
    public List<Enemy> WarSongTargets = new();   // buffed allies — buff dies with the drummer
    public TrollMusician(Random rng, string name) : base(rng, name)
    {
        TypeName = "Troll Musician";
        EquippedAxes = 0; SpareAxes = 0;  // hands full of war drums — until the songs run out
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
    // Spawn pool: 2 parts regular ogre, 1 part each variant
    public static Ogre RandType(Random r, string name) => r.Next(1, 6) switch
    {
        3 => new OgreWarrior(r, name.Replace("Ogre", "Ogre Warrior")),
        4 => new OgreDuelist(r, name.Replace("Ogre", "Ogre Duelist")),
        5 => new OgreBerserker(r, name.Replace("Ogre", "Ogre Berserker")),
        _ => new Ogre(r, name)
    };

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
        ToughHideMin = 2; ToughHideMax = 2;   // flat -2 damage reduction (tough skin)
        OffhandMinAtk = 4; OffhandMaxAtk = 12;
        OffhandMinDmg = 2; OffhandMaxDmg = 8;
        XPValue = 50;
        Race = "Ogre"; MinDamage += 2; MaxDamage += 2;
        MinDodge = Math.Max(1, MinDodge / 2); MaxDodge = Math.Max(1, MaxDodge / 2);
    }
}

// ── Ogre variants (Ogre.RandType: 2 parts base ogre, 1 each variant) ──────
// All ogres carry Giant's Strength: two-handed weapons in one hand.

class OgreWarrior : Ogre
{
    public OgreWarrior(Random rng, string name) : base(rng, name)
    {
        TypeName = "Ogre Warrior";
        MinDamage = 4 + 2; MaxDamage = 12 + 2;   // Great Sword 4-12 (+2 ogre race dmg)
        HasBlock = true;
        HasShield = true; ShieldBlockBonus = 3;  // tower shield: blocks melee AND ranged
        HasDoubleTap = false;                    // sword + shield, no off-hand attack
        XPValue = 65;
    }
}

class OgreDuelist : Ogre
{
    public OgreDuelist(Random rng, string name) : base(rng, name)
    {
        TypeName = "Ogre Duelist";
        MinDamage = 2 + 2; MaxDamage = 12 + 2;    // Battle Axe 2-12 (+2 ogre race dmg)
        HasDoubleTap = true;                      // off-hand Ogre Club
        OffhandMinAtk = 4; OffhandMaxAtk = 12;
        OffhandMinDmg = 3; OffhandMaxDmg = 12;    // Ogre Club
        XPValue = 65;
    }
}

class OgreBerserker : Ogre
{
    public int OgreRagePoints = 5;
    public int OgreRageTurns = 0;                 // +2 dmg while raging
    public OgreBerserker(Random rng, string name) : base(rng, name)
    {
        TypeName = "Ogre Berserker";
        MinDamage = 2 + 2; MaxDamage = 12 + 2;    // Battle Axe x2
        HasDoubleTap = true;
        OffhandMinAtk = 4; OffhandMaxAtk = 12;
        OffhandMinDmg = 2; OffhandMaxDmg = 12;    // second Battle Axe
        XPValue = 70;
    }
}

// ── Giants: join the spawn pool 10 waves after Orc Barbarians (wave 51+) ──
// All giants have Giant's Strength: two-handed weapons in one hand.

class GiantEnemy : Enemy
{
    public int GiantArrows = 12;   // composite bow, 2d4
    public GiantEnemy(Random rng, string name) : base(name, "Giant")
    {
        MaxHP = 50; HP = MaxHP;
        MinAttack = 2; MaxAttack = 12;           // 2d6-style attack rolls
        MinDamage = 4; MaxDamage = 14;           // Great Sword in one hand
        MinDodge = 1; MaxDodge = 6;
        MinGrapple = 4; MaxGrapple = 14;
        GrappleDmgMin = 3; GrappleDmgMax = 10;
        HasBlock = true; BlockMin = 3; BlockMax = 12;
        HasShield = true; ShieldBlockBonus = 3;  // tower shield — blocks ranged too
        XPValue = 85;
        Race = "Giant";
    }

    // Spawn pool: 3 parts base giant, 1 part each specialist
    public static GiantEnemy RandType(Random r, string name) => r.Next(1, 7) switch
    {
        4 => new GiantMage(r, name.Replace("Giant", "Giant Mage")),
        5 => new GiantPriest(r, name.Replace("Giant", "Giant Priest")),
        6 => new GiantDuelist(r, name.Replace("Giant", "Giant Duelist")),
        _ => new GiantEnemy(r, name)
    };
}

class GiantMage : GiantEnemy
{
    public string Grimoire;          // lightning / fire / ice / negative / boost
    public GiantMage(Random rng, string name) : base(rng, name)
    {
        TypeName = "Giant Mage";
        HasShield = false; ShieldBlockBonus = 0;
        MinDamage = 2; MaxDamage = 10;           // long staff
        GiantArrows = 0;
        Grimoire = new[] { "lightning", "fire", "ice", "negative", "boost" }[rng.Next(5)];
        XPValue = 95;
    }
}

class GiantPriest : GiantEnemy
{
    public GiantPriest(Random rng, string name) : base(rng, name)
    {
        TypeName = "Giant Priest";
        HasShield = false; ShieldBlockBonus = 0;
        MinDamage = 2; MaxDamage = 8;            // war mace 2d4 (non-lethal)
        HasDoubleTap = true;
        OffhandMinAtk = 2; OffhandMaxAtk = 12;
        OffhandMinDmg = 2; OffhandMaxDmg = 8;    // second war mace
        OffhandNonLethal = true;
        GiantArrows = 0;
        XPValue = 95;
    }
}

class GiantDuelist : GiantEnemy
{
    public int HandAxes = 6;
    public GiantDuelist(Random rng, string name) : base(rng, name)
    {
        TypeName = "Giant Duelist";
        HasShield = false; ShieldBlockBonus = 0;
        MinDamage = 4; MaxDamage = 14;           // great sword
        HasDoubleTap = true;                     // war mace off-hand
        OffhandMinAtk = 2; OffhandMaxAtk = 12;
        OffhandMinDmg = 2; OffhandMaxDmg = 8;
        OffhandNonLethal = true;
        HasKick = true; KickDmgMin = 2; KickDmgMax = 8;   // Fury of Blows
        GiantArrows = 0;
        XPValue = 100;
    }
}

// ── Wildlife: neutral beasts that roam the battlefield ─────────────────────
// They don't count toward wave victory. Killing one yields hides and meat.

class Deer : Enemy
{
    public bool Antlered;   // antlered deer fight back when wounded
    public Deer(Random rng, string name, bool antlered) : base(name, "Deer")
    {
        IsWildlife = true; Antlered = antlered;
        MaxHP = rng.Next(1, 5) + rng.Next(1, 5); HP = MaxHP;   // 2d4
        MinAttack = 2; MaxAttack = 8;    // antlers 2d4
        MinDamage = 2; MaxDamage = 12;   // antlers 2d6
        MinDodge = 3; MaxDodge = 10;
        XPValue = 4;
        Race = "Beast";
    }
}

class Wolf : Enemy
{
    public Wolf(Random rng, string name) : base(name, "Wolf")
    {
        IsWildlife = true;
        MaxHP = rng.Next(1, 5) + rng.Next(1, 5) + rng.Next(1, 5); HP = MaxHP;   // 3d4
        MinAttack = 3; MaxAttack = 12;   // bite 3d4
        MinDamage = 2; MaxDamage = 8;    // bite 2d4
        MinDodge = 2; MaxDodge = 9;
        XPValue = 10;
        Race = "Beast";
    }
}

class Boar : Enemy
{
    public bool JustCharged;   // charge past, wheel around, charge again
    public Boar(Random rng, string name) : base(name, "Boar")
    {
        IsWildlife = true;
        MaxHP = rng.Next(1, 4) + rng.Next(1, 4) + rng.Next(1, 4) + rng.Next(1, 4); HP = MaxHP;   // 4d3
        MinAttack = 2; MaxAttack = 6;    // tusks 2d3
        MinDamage = 3; MaxDamage = 12;   // tusks 3d4
        MinDodge = 1; MaxDodge = 7;
        XPValue = 12;
        Race = "Beast";
    }
}

class Bear : Enemy
{
    public Bear(Random rng, string name) : base(name, "Bear")
    {
        IsWildlife = true;
        MaxHP = 0; for (int d = 0; d < 8; d++) MaxHP += rng.Next(1, 3); HP = MaxHP;   // 8d2
        MinAttack = 3; MaxAttack = 12;   // bite 3d4
        MinDamage = 2; MaxDamage = 8;    // bite 2d4
        MinDodge = 1; MaxDodge = 6;
        MinGrapple = 4; MaxGrapple = 12;
        XPValue = 28;
        Race = "Beast";
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
    public string MonkWeapon = "";   // wave 110+: a true monk weapon by style
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

// One purchasable point-buy upgrade. Show(p) hides anything the character
// can't actually use (no prayer options for a non-praying class, etc).
