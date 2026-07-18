using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

// ═════════════════════════════════════════════════════════════════════════
//  Rules.cs — the lookup tables. Data, not behaviour.
// ═════════════════════════════════════════════════════════════════════════
//
//  Four things live here, and they're all "what the game knows" rather than
//  "what the game does". Most balance tweaks are a one-line edit in here.
//
//  SizeRules — small (0) / medium (1) / large (2), and what size means when
//    two creatures meet: attack, damage, dodge and block/parry adjustments.
//    A small foe is nimble but hits softly; a Giant is clumsy against
//    anything smaller. Add a race to Of() or it silently counts as medium.
//
//  Shop — the economy. Everything is copper (100c = 1 silver, 100s = 1 gold,
//    100g = 1 platinum; Fmt() prints it nicely).
//      Price          what things cost      Armors        wearables + effects
//      Shields        block/DR values       TwoHanded     needs Giant's Strength
//      ReachWeapons   strike a square out   ArmorPiercing ignores some DR
//      Storefronts    which shop sells what
//      RollStock()    picks a random 8 per visit, cheapest first — this is
//                     why stock changes every time you walk in
//    Adding an item = add a Price entry, a Storefronts entry, and (if it's a
//    weapon) stats in WeaponPickupStats over in Combat.cs.
//
//  FeatDef — every feat: name, description, prerequisite, stackable. The
//    feat menu builds itself from All, so adding one entry is enough.
//    Player.PrayerFeats / SongFeats / SpellFeats group the ones that hand
//    out a resource pool and a starting item.
//
//  BuyOpt — one purchasable point-buy upgrade. Show(p) decides whether a
//    character even sees it (no prayer options for a class that can't pray).
//    The catalogue itself is BuyCatalogue() in Program.cs; it sorts by
//    price -> type -> name and numbers itself, so never hardcode an index.
//
// ═════════════════════════════════════════════════════════════════════════

static class SizeRules
{
    public static int Of(string race) => race switch
    {
        "Goblin" or "Gem Gnome" or "Glass Gnome" or "Light-Foot Hobbit" or "Brave Minds Hobbit" => 0,
        "Ogre" or "Giant" => 2,
        _ => 1,
    };

    // Melee attack roll bonus for attacker race vs defender race
    public static int AtkBonus(string atkRace, string defRace)
    {
        int a = Of(atkRace), d = Of(defRace);
        int b = 0;
        if (a == 0) { if (d >= 1) b += 1; if (d == 2) b += 1; }        // small: +1 vs medium, +2 vs large
        if (atkRace == "Ogre") { if (d == 1) b -= 1; else if (d == 0) b -= 2; }
        return b;
    }

    // Melee damage bonus for attacker race vs defender race
    public static int DmgBonus(string atkRace, string defRace)
    {
        int a = Of(atkRace), d = Of(defRace);
        int b = 0;
        if (a == 0) { if (d >= 1) b -= 1; if (d == 2) b += 1; }        // small: -1 dmg, but +1 weak spots vs large
        if (atkRace == "Ogre") { if (d == 1) b += 2; else if (d == 0) b += 3; }
        return b;
    }

    // Dodge roll adjustment range (min, max) for defender race vs attacker race
    public static (int Min, int Max) DodgeBonus(string defRace, string atkRace)
    {
        int ds = Of(defRace), a = Of(atkRace);
        int mn = 0, mx = 0;
        if (ds == 0) { if (a >= 1) { mn += 1; mx += 2; } if (a == 2) { mn += 1; mx += 1; } }
        if (defRace == "Giant" && a <= 1) { mn -= 2; mx -= 2; }
        if (defRace == "Ogre" && a == 1) { mn -= 1; mx -= 1; }
        return (mn, mx);
    }

    // Block/parry roll adjustment for defender race vs attacker race (Giant clumsiness)
    public static int BlockParryBonus(string defRace, string atkRace)
    {
        if (defRace != "Giant") return 0;
        int a = Of(atkRace);
        return a == 0 ? -2 : a == 1 ? -1 : 0;
    }
}

// ── Economy: all money stored in copper. 100c = 1s, 100s = 1g, 100g = 1p ──
static class Shop
{
    public const long Silver = 100, Gold = 10_000, Platinum = 1_000_000;

    // Buy prices in copper
    public static readonly Dictionary<string, long> Price = new()
    {
        // ammo
        ["Arrow"] = 1, ["Blunt Arrow"] = 1,          // blunt: 2 per copper (sold in pairs)
        ["Barbed Arrow"] = Silver, ["Spiral Arrow"] = 3 * Silver,
        // weapons
        ["Dagger"] = 50, ["Club"] = 50, ["Quarterstaff"] = 75,
        ["Staff"] = 3 * Silver, ["Short Sword"] = 10 * Silver, ["Hand Axe"] = 12 * Silver,
        ["Sword"] = 25 * Silver, ["Long Staff"] = 25 * Silver, ["Mace"] = 20 * Silver,
        ["Rapier Sword"] = 30 * Silver, ["Long Sword"] = 45 * Silver, ["Bow"] = 50 * Silver,
        ["Halberd"] = 75 * Silver, ["Great Sword"] = Gold, ["Battle Axe"] = 3 * Gold,
        ["War Mace"] = 3 * Gold + 50 * Silver, ["Axe"] = 4 * Gold, ["Pike"] = 4 * Gold,
        ["Claymore"] = 5 * Gold, ["Great Axe"] = 5 * Gold, ["Warhammer"] = 4 * Gold,
        ["Wand"] = 50 * Silver, ["Pickaxe"] = 15 * Silver, ["Shortbow"] = 30 * Silver,
        ["Hunting Bow"] = 75 * Silver,
        // craft & general goods
        ["Workshop Hammer"] = 10 * Silver, ["Knife"] = 1,
        // hammers, picks, polearms, bows, whips
        ["Throwing Hammer"] = 10 * Silver, ["Battle Hammer"] = 25 * Silver,
        ["War Pick"] = 50 * Silver, ["Maul"] = 2 * Gold, ["Composite Bow"] = Gold,
        ["Whip"] = 75 * Silver, ["Nine-Tails Whip"] = 2 * Gold,
        // monk weapons (the Dojo)
        ["Wakizashi"] = 60 * Silver, ["Tanto"] = 35 * Silver, ["Katana"] = 2 * Gold,
        ["Nodachi"] = 4 * Gold, ["Tetsubo"] = 70 * Silver, ["Kanabo"] = 90 * Silver,
        ["Nunchucks"] = 40 * Silver, ["Chain and Ball"] = 55 * Silver, ["Spike Chain"] = 65 * Silver,
        ["Screama Sticks"] = 45 * Silver, ["Foldable Fan Blade"] = 50 * Silver,
        ["Circle Throwing Blades"] = 55 * Silver, ["Shuriken"] = 5 * Silver, ["Smoke Pouch"] = 20 * Silver,
        // bags & carriers
        ["Quiver"] = 50, ["Potion Pouch"] = 10, ["Throwing Band"] = 12, ["Artisan's Bag"] = 3 * Gold,
        // Magic-crafted goods (base prices; the magic shop adds 3 gold)
        ["Mirror Shield"] = 35 * Silver, ["Returning Quiver"] = 60 * Silver, ["Bag of Holding"] = 5 * Gold,
        ["Quiver of Holding"] = 4 * Gold,
        ["Fire Robes"] = 5 * Gold, ["Frost Robes"] = 5 * Gold,
        ["Lightning Robes"] = 7 * Gold, ["Holy Vestments"] = 10 * Gold,
        ["Unholy Robe"] = 7 * Gold, ["Air Robe"] = 6 * Gold, ["Bard Vestments"] = 8 * Gold,
        ["Monk Garbs"] = 10 * Gold,
        // enemy gear (sellable loot)
        ["Goblin Dagger"] = 40, ["Orc Longsword"] = 40 * Silver, ["Troll Axe"] = 2 * Gold,
        ["Bastard Sword"] = 60 * Silver, ["Kukuri"] = 25 * Silver, ["Ogre Club"] = 2 * Gold,
        // shields
        ["Buckler"] = 75, ["Round Shield"] = 25 * Silver, ["Shield"] = 35 * Silver,
        ["Kite Shield"] = 45 * Silver, ["Tower Shield"] = 2 * Gold,
    };

    // Two-handed weapons: one-handed only with Giant's Strength
    public static readonly HashSet<string> TwoHanded = new()
    { "Great Sword", "War Mace", "Battle Axe", "Ogre Club", "Great Axe", "Claymore",
      "Staff", "Long Staff", "Halberd", "Pike",
      "Maul", "Nodachi", "Chain and Ball", "Spike Chain" };

    // Weapons with reach: strike one square further and into the corners
    public static readonly HashSet<string> ReachWeapons = new()
    { "Halberd", "Pike", "Whip", "Chain and Ball", "Spike Chain", "Nodachi" };

    // Weapons that ignore some armor: (dice, sides) rolled and subtracted from DR
    public static readonly Dictionary<string, (int Dice, int Sides)> ArmorPiercing = new()
    { ["Tetsubo"] = (2, 3), ["Kanabo"] = (3, 3) };

    // ── Storefronts: each visit stocks a random 8 of the shop's range ──
    public static readonly Dictionary<string, string[]> Storefronts = new()
    {
        ["Crafts Store"] = new[]
        { "Workshop Hammer", "Pickaxe", "Axe", "Hunting Bow", "Knife", "Artisan's Bag",
          "Dagger", "Quarterstaff", "Club", "Staff" },
        ["Weapon Shop"] = new[]
        { "Hunting Bow", "Knife", "Axe", "Throwing Hammer", "Battle Hammer", "War Pick",
          "Maul", "Composite Bow", "Whip", "Nine-Tails Whip", "Short Sword", "Hand Axe",
          "Sword", "Long Sword", "Rapier Sword", "Great Sword", "Battle Axe", "Great Axe",
          "Claymore", "Mace", "War Mace", "Warhammer", "Halberd", "Pike", "Bow", "Shortbow",
          "Dagger", "Staff", "Long Staff", "Wand" },
        ["Armor Store"] = new[]
        { "Buckler", "Round Shield", "Shield", "Kite Shield", "Tower Shield" },   // armors appended at runtime
        ["Magic Shop"] = new[]
        { "Mirror Shield", "Returning Quiver", "Bag of Holding", "Quiver of Holding",
          "Fire Robes", "Frost Robes", "Lightning Robes", "Holy Vestments",
          "Unholy Robe", "Air Robe", "Bard Vestments", "Monk Garbs" },
        ["Bags Shop"] = new[]
        { "Quiver", "Potion Pouch", "Throwing Band", "Artisan's Bag", "Bag of Holding" },
        ["Dojo"] = new[]
        { "Wakizashi", "Tanto", "Katana", "Nodachi", "Tetsubo", "Kanabo", "Nunchucks",
          "Chain and Ball", "Spike Chain", "Screama Sticks", "Foldable Fan Blade",
          "Circle Throwing Blades", "Shuriken", "Smoke Pouch", "Short Sword", "Knife",
          "Halberd", "Staff", "Long Staff", "Hunting Bow", "Composite Bow", "Quarterstaff" },
    };

    // What a thing costs, whether it lives in Price or in Armors
    public static long CostOf(string item) =>
        Price.TryGetValue(item, out var c) ? c
        : Armors.TryGetValue(item, out var a) ? a.Cost : 0;

    // Roll this visit's stock: a random 8 (or fewer if the range is small),
    // listed cheapest first.
    public static string[] RollStock(string shop, Random rng, IEnumerable<string>? extra = null)
    {
        var pool = Storefronts[shop].ToList();
        if (extra != null) pool.AddRange(extra);
        return pool.Distinct().OrderBy(_ => rng.Next()).Take(8).OrderBy(CostOf).ToArray();
    }

    // Shield stats: (block bonus, damage reduction)
    public static readonly Dictionary<string, (int Block, int Def)> Shields = new()
    {
        ["Buckler"] = (1, 0), ["Round Shield"] = (2, 0), ["Shield"] = (2, 0),
        ["Kite Shield"] = (2, 1), ["Tower Shield"] = (3, 1),
    };

    // Armor: Price, worn-under flag, metal flag, flat DR vs melee/ranged,
    // DR vs spells, DR vs prayers, spell/prayer absorb % (adds a use), blurb.
    public record ArmorDef(long Cost, bool Under, bool Metal, int MeleeDR, int SpellDR, int PrayerDR, int AbsorbPct, string Desc);
    public static readonly Dictionary<string, ArmorDef> Armors = new()
    {
        ["Padded Armor"]  = new(75 * Silver,  true,  false, 1, 0, 0, 0,  "-2 non-lethal; wearable under other armor"),
        ["Leather Vest"]  = new(85 * Silver,  true,  false, 1, 0, 0, 0,  "-1 melee; wearable with other armor"),
        ["Leather Armor"] = new(Gold,         true,  false, 2, 2, 2, 0,  "1d4 less from all sources; wearable under metal"),
        ["Chainmail"]     = new(5 * Gold,     true,  true,  4, 0, 0, 0,  "metal; -2d3 lethal melee & ranged; under plate"),
        ["Breastplate"]   = new(10 * Gold,    false, true,  5, 0, 0, 0,  "plated; -2d4 all but magic and prayer"),
        ["Half Plate"]    = new(25 * Gold,    false, true,  6, 2, 0, 0,  "plated; -3d3 physical, -1d4 spells"),
        ["Soft Leather"]  = new(40 * Gold,    true,  false, 3, 2, 5, 0,  "-2d4 NL, -1d4 lethal, -2d4 prayer, -1d4 non-fire spells"),
        ["Full Plate"]    = new(50 * Gold,    false, true,  10, 6, 2, 0, "plated; -4d4 physical, -3d3 spells, -1d4 prayers"),
        ["Oak Armor"]     = new(50 * Gold,    false, false, 8, 7, 7, 0,  "non-metal plate; strong all-round, wary of fire"),
        ["Hard Leather"]  = new(75 * Gold,    true,  false, 4, 5, 5, 0,  "broad lethal/elemental/prayer reduction; under plate"),
        ["Studded Armor"] = new(85 * Gold,    true,  true,  5, 4, 5, 0,  "metal; broad reduction, +1 extra; under plate"),
        ["Scale Armor"]   = new(100 * Gold,   false, true,  10, 7, 5, 0, "plated; -4d4 melee, -3d6 ranged, -3d4 spell, -2d4 prayer"),
        ["Rune Armor"]    = new(5 * Platinum, false, false, 10, 9, 8, 45, "absorbs 45% of spells/prayers (+1 use); prayers heal x2 on you"),
        ["Scribed Robes"] = new(5 * Platinum, true,  false, 0, 4, 4, 55, "absorbs 55% of spells/prayers (+1 use); prayers heal x2 on you"),
        // ── Elemental robes: absorb their own element outright, 15% of anything
        // else, burn/chill/shock nearby foes, and empower matching spells ──
        ["Fire Robes"]      = new(5 * Gold,  true, false, 0, 1, 1, 15, "fire spells absorbed 100%, 15% of anything else; burns nearby foes 1d4 (25% ignite); +2d2 fire spell damage & to-hit"),
        ["Frost Robes"]     = new(5 * Gold,  true, false, 0, 1, 1, 15, "ice spells absorbed 100%, 15% of anything else; chills nearby foes 1d2 (may cost actions/movement); +2d2 ice spell damage & to-hit"),
        ["Lightning Robes"] = new(7 * Gold,  true, false, 0, 1, 1, 15, "lightning absorbed 100%, 15% of anything else; shocks nearby foes 2-3 (metal armor burns 1d3/turn); +2d2 lightning spell damage & to-hit"),
        ["Holy Vestments"]  = new(10 * Gold, true, false, 0, 1, 1, 15, "negative prayers absorbed 100%, 15% of spells; heals allies & sears undead 1d4/turn (25% full heal / destroy); +2d2 healing, +1d2 damage prayers"),
        ["Unholy Robe"]     = new(7 * Gold,  true, false, 0, 1, 1, 15, "negative-energy spells absorbed 100%, 15% of anything else; your raised undead get +10 HP; negative energy heals you/undead +2d2 and deals +2d2 to the living"),
        ["Air Robe"]        = new(6 * Gold,  true, false, 0, 1, 1, 15, "wind/air spells absorbed 100%, 15% of anything else; your wind spells push 5ft further and gain +2d2 damage & to-hit"),
        ["Bard Vestments"]  = new(8 * Gold,  true, false, 0, 1, 1, 15, "fear/negative songs, prayers & spells absorbed 100% (become song uses), 15% of damage magic; songs last 1d2 longer and hit 2d2 harder"),
        ["Monk Garbs"]      = new(10 * Gold, true, false, 3, 1, 1, 25, "25% absorb of hostile magic — becomes CHI for monks; -4 non-lethal / -2d2 lethal taken; wearable over anything but plate"),
    };

    public static long Sell(string item) =>
        Price.TryGetValue(item, out long p) ? p * 8 / 10 :
        Armors.TryGetValue(item, out var a) ? a.Cost * 8 / 10 : 0;

    public static string Fmt(long c)
    {
        if (c <= 0) return "0c";
        var parts = new List<string>();
        if (c >= Platinum) { parts.Add($"{c / Platinum}p"); c %= Platinum; }
        if (c >= Gold)     { parts.Add($"{c / Gold}g");     c %= Gold; }
        if (c >= Silver)   { parts.Add($"{c / Silver}s");   c %= Silver; }
        if (c > 0)         parts.Add($"{c}c");
        return string.Join(" ", parts);
    }
}

record BuyOpt(int Cost, string Type, string Name, Func<Player, bool> Show, Action<Player> Apply);

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
        new("Hunter", "Hunt animals at wave end (dagger/knife to skin). Stacks: gain hunting → ALL deer → ALL boar → ALL wolves → ALL bears.", null, true, 5),
        new("Gatherer", "Gain pickaxe + axe; mine/cut at wave end. Stacks: gain gathering → ALL of one kind → mine AND cut everything → +1d4 extra yield.", null, true, 4),
        new("Alchemist", "Craft 2 potions per action (boost / heal / poison AoE / restore uses); throw 40ft or drink."),
        new("Multishot", "Fire or throw 4 arrows/weapons at one target, rolling attack and damage for each."),
        new("Split Shot", "Shoot two targets at once, spending two arrows or throwing weapons."),
        new("Piercing Shot", "Your arrow punches through, hitting every target in a line 20ft past the first."),
        new("Folly of Arrows", "Loose 4d4 arrows skyward, raining on the target and everything within a 4x4 area."),
        new("Magic Crafting", "Craft returning ammo, Rune/Scribed armor, the spell-reflecting Mirror Shield, and the Bag of Holding."),
        new("Flurry of Blows", "Five attacks with your melee or thrown weapon in one action.", "Fury of Blows"),
        new("Weapon Specialist", "+1d4 attack and +1d4 damage with a chosen weapon. Take multiple times.", null, true),
        new("Extended Grasp", "Melee, weapon and grapple reach +1 square and can strike foes on the corners (diagonals); extends Whirlwind's circle too."),
        new("Vow of Silence", "You become a monk: monk weapons grant an extra attack per attack, and you gain Chi (1 + 1 per 2 levels, +Wis/Smarts). The vow binds you — you may wield ONLY monk weapons."),
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

