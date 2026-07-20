using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

// ═════════════════════════════════════════════════════════════════════════
//  Player.cs — one playable character: stats, traits, gear, pools.
// ═════════════════════════════════════════════════════════════════════════
//
//  Everything a character IS lives here. Combat state that dies with the
//  fight (Climbed, Frenzied, FearTurns) also lives here for convenience, but
//  is reset at the start of each CombatSession.
//
//  ROLLS COME IN MIN/MAX PAIRS:
//    Most stats are a range rolled as Rng.Next(MinX, MaxX + 1). "Base" in
//    the point buy raises the Min, "Max" raises the Max. Keep Min <= Max and
//    Min >= 1 — if they ever meet, that roll returns a fixed number forever.
//
//  THE EIGHT CORE TRAITS:
//    Strength, Dexterity, Constitution, Intelligence, Wisdom, Smarts,
//    Charisma, Agility — chosen at creation (16 points, each -2..4). Several
//    rules say "+X if higher than Y", which is why the derived helpers below
//    (LightWeaponTrait, DodgeTrait, ParryTrait, ChiTrait...) exist: they
//    resolve those comparisons in one place instead of at every roll site.
//
//  RESOURCE POOLS (all refill on REST, not per wave):
//    SpellUses / PrayerUses / SongTokens / ChiUses / RagePoints / DuelistPoints
//    Each has a Max...() method — put permanent bonuses in there, not in the
//    refill code, or they'll be lost on the next rest.
//
//  BUFFS MUST BE REVERSIBLE:
//    Songs, blessings and redemption apply deltas to stats and remember what
//    they gave (WindBonusReceived, BlessBonusReceived, ...), because SaveGame
//    subtracts them before writing. A buff that can't be undone will bake
//    itself into the save permanently.
//
//  IF YOU ADD A FIELD: add it to SaveGame AND TryLoadGame (in Program.cs),
//  guarded with dict.ContainsKey so older saves still load.
//
// ═════════════════════════════════════════════════════════════════════════

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
    public bool Climbed = false;             // on top of a tree/rock (high ground)
    public string Gender = "neutral";        // male / female / neutral (sprite key)
    public int Variant = 1;                  // player-chosen appearance variant
    // Layered appearance (rendered by the future compositor; saved now)
    public string HairColor = "black";       // black/blonde/brown/red/white
    public string HairLength = "short";      // bald/short/medium short/medium/medium long/long
    public string EyeColor = "brown";        // black/blue/green/brown/hazel/red
    public string Headwear = "nothing";      // fedora/pointy hat/mask/hood/circlet/top hat/nothing
    public string ClothingColor = "black";   // 9 colors
    public string FacialHair = "none";       // males: beard/goatee/mustache/fu manchu/handlebars/soul patch/none
    // ── Carriers (bought at the Bags Shop; overflow goes into the pack) ──
    public int QuiverCap = 0;            // arrows carried outside the pack (+15 per upgrade)
    public int PotionPouchCap = 2;       // everyone carries 2 potions free of the pack
    public int ThrowBandCap = 0;         // throwing weapons on the band
    public bool HasQuiverOfHolding = false;

    // ── Point-buy investments (base = the low end of a roll, max = the high end) ──
    public int PrayerHealMaxBonus = 0;                    // base lives in PrayerHealBonus
    public int PrayerDmgBonus = 0, PrayerDmgMaxBonus = 0;
    public int PrayerVsHp = 0, PrayerVsHpMax = 0;         // prayer roll vs enemy HP
    public int PrayerAbilBonus = 0, PrayerAbilMaxBonus = 0;
    public int PrayerTurnsMax = 0;                        // base lives in PrayerDurBonus
    public int SongDurMax = 0;                            // base lives in SongDurBonus
    public int SongHeal = 0, SongHealMax = 0;
    public int SongFear = 0, SongFearMax = 0;             // song fear roll vs enemy HP
    public int SpellRollBonus = 0, SpellRollMaxBonus = 0;
    public int SpellDurMax = 0;                           // base lives in SpellDurBonus
    public int SprintMaxBonus = 0;                        // base lives in SprintBonus
    public int RunAwayBonus = 0, RunAwayMaxBonus = 0;
    public int ArrowsCrafted = 0, ArrowsCraftedMax = 0;
    public int PotionDurBonus = 0, PotionDurMaxBonus = 0;
    public int PotionBonus = 0, PotionMaxBonus = 0;
    public int GoodsCollected = 0, GoodsCollectedMax = 0;
    public int CraftedArmorBonus = 0, CraftedArmorMaxBonus = 0;
    public int DisarmBonus = 0, DisarmMaxBonus = 0;
    public int MoneyBonus = 0, MoneyMaxBonus = 0;
    public int SpellRangeBonusFt = 0, PrayerRangeBonusFt = 0, SongRangeBonusFt = 0, RangedRangeBonusFt = 0;
    public int CraftedWeaponDmg = 0;
    public int ShieldBonus = 0, ShieldReflectPct = 0;
    public int BonusSongUses = 0, BonusSpellUses = 0, BonusPrayerUses = 0;

    // ── Core traits (creation: 16 points, each -2..4) ──
    public int Strength = 0, Dexterity = 0, Intelligence = 0, Wisdom = 0;
    public int Constitution = 0, Smarts = 0, Charisma = 0, Agility = 0;

    // Derived trait helpers. Where the rules say "+X if higher than Y", the
    // higher of the two applies (a tie yields that same value either way).
    public int LightWeaponTrait()                        // unarmed / light / monk weapons
    {
        int best = Math.Max(Strength, Dexterity);
        if (Wisdom > Strength && Wisdom > Dexterity) best = Math.Max(best, Wisdom);
        return best;
    }
    public int NonLethalTrait() =>                        // non-lethal / monk weapons (Wis)
        (Wisdom > Strength && Wisdom > Dexterity) ? Wisdom : Math.Max(Strength, Dexterity);
    public int DodgeTrait() => Math.Max(Dexterity, Agility);
    public int ParryTrait() => Math.Max(Dexterity, Smarts);
    public int DisarmTrait() => Math.Max(Dexterity, Smarts);
    public int FinesseTrait() => Smarts;                  // spike chain/whip/pick/dagger/wand/knife/rapier
    public int ChiTrait() => Math.Max(Wisdom, Smarts);
    public int MoveTrait() => Dexterity + Agility;
    public int SprintTrait() => (int)(Dexterity * 1.5) + (int)(Agility * 1.5);
    public int BowTrait() => (int)(Dexterity * 1.5);
    public int WandTrait() => (int)(Intelligence * 1.5);
    public int TwoHandTrait() => (int)(Strength * 1.5);
    public int ExtraActionsFromAgility() => Agility / 2;  // +0.5 actions per point
    public int FearTrait() => (int)(Charisma * 1.5);
    public int SpellRangeFeet() => (int)(Smarts * 1.5);
    public int PrayerRangeFeet() => (int)(Wisdom * 1.5);
    public int SongRangeFeet() => Charisma * 5;
    // Songs carry 15 squares (+1 per Charisma, i.e. +5ft each). Anyone outside
    // the radius hears nothing — so a musician has to be near the fighting.
    public int SongRadiusSquares() => 15 + Charisma;
    public int RangedRangeFeet() => (int)(Dexterity * 1.5);
    public int DotResistPct() => Constitution * 5;
    public int TraitStatPoints() => Intelligence + Wisdom + Smarts;

    // ── Racial behavioral traits (re-derived from Race via ApplyRaceTraits) ──
    public int DodgeVsLarge = 0;             // extra dodge vs large attackers
    public int SprintBonus = 0;              // extra sprint distance beyond movement
    public bool NoSprintPenalty = false;     // ignore the -2 after sprinting
    public bool DoubleSprintPenalty = false; // -4 after sprinting instead of -2
    public int RaceAbsorbPct = 0;            // innate spell/prayer absorption %
    public bool FearImmune = false;
    public int SpellDurBonus = 0, PrayerDurBonus = 0, SongDurBonus = 0;
    public bool RaceFrenzy = false;          // Troll: enter a rage at low HP
    public bool Frenzied = false;            // currently frenzied (combat-scoped)
    // Fear worked on the player (troll fear song): fight = you may only attack
    // the source, flight = you may only flee from it, for FearTurns turns.
    public int FearTurns = 0;
    public bool FearFight = false;
    public Enemy? FearSource;         // what terrified you (fight it or flee it)

    // ── Dragon combat state (combat-scoped, reset each CombatSession) ──
    public Enemy? SwallowedBy;        // inside the dragon: triple damage out, acid in
    public int ProneTurns = 0;        // knocked off your feet: stand up costs an action
    public int BleedTurns = 0;        // claw gashes: 1 HP/turn

    // ── Elixirs (the dragon-hoard potions; named inventory persists) ──
    public Dictionary<string, int> SpecialPotions = new();
    // Combat-scoped elixir effects (reset each CombatSession; ElixirDR is also
    // subtracted out in SaveGame so it never bakes into a save)
    public int ElixirAtk = 0, ElixirDodge = 0, ElixirDmg = 0, ElixirDR = 0;
    public int ElixirHealMin = 0, ElixirHealMax = 0;
    public int ElixirDoubleDmgPct = 0;
    public bool ElixirDoubleMove = false, ElixirSongDouble = false;
    // Each active elixir counts down in turns; Expire undoes what it gave
    public List<(string Name, int TurnsLeft, Action<Player> Expire)> ActiveElixirs = new();

    // ── Enchanting (persistent; bought at the Magic Shop) ──
    public Dictionary<string, int> EnchantedWeapons = new();   // weapon name → +x atk & dmg
    public int ArmorEnchant = 0;      // +x to armor; negate chance with shield
    public int ShieldEnchant = 0;     // +x to shield block
    public int WeaponEnchant() => EnchantedWeapons.GetValueOrDefault(HeldWeapon ?? "");

    // Robes/vestments/garbs count as a bottom layer for ability-users
    public static bool IsRobe(string n) =>
        n.EndsWith("Robes") || n.EndsWith("Robe") || n.EndsWith("Vestments") || n == "Monk Garbs";
    public bool AbilityUser => CanSing || CanPray || KnownSpells.Any() || IsMonk;
    public long Copper = 0;                  // purse (100c=1s, 100s=1g, 100g=1p)
    public int BluntArrows = 0;              // non-lethal
    public int BarbedArrows = 0;             // +1d4 damage
    public int SpiralArrows = 0;             // +1d4 attack, +1d4 damage
    // Unbroken arrows awaiting end-of-wave recovery (combat-scoped)
    public int RecoverRegular = 0, RecoverBlunt = 0, RecoverBarbed = 0, RecoverSpiral = 0;
    // Crafting materials (Artisan economy)
    public int Wood = 0, Stone = 0, Ore = 0, Hides = 0, Meat = 0;
    public int CarryCap = 8;        // material capacity (Artisan 50; bags add more)
    public int BagUpgrades = 0;     // shop upgrades bought (price climbs 2c each)
    public Dictionary<string, int> WeaponSpec = new();   // Weapon Specialist stacks per weapon
    public int PotionsBoost = 0, PotionsHeal = 0, PotionsPoison = 0, PotionsRestore = 0;
    public int PotionAtkBoost = 0, PotionBoostTurns = 0;
    public bool HasReturningAmmo = false;   // Magic Crafting: ammo flies back unbroken
    public bool HasBagOfHolding = false;    // no carry limit at all
    public bool HasWorkshopHammer = false;  // required to craft as an Artisan
    public int MaterialLoad => Wood + Stone + Ore + Hides + Meat;

    // ── Carriers: arrows fill the quiver, potions the pouch, throwing
    // weapons the band. Whatever overflows spills into the pack (arrows
    // bundle 10 to a slot). The Bag of Holding removes the limit entirely.
    public int TotalArrows => ArrowCount + BluntArrows + BarbedArrows + SpiralArrows;
    public int TotalPotions => PotionsBoost + PotionsHeal + PotionsPoison + PotionsRestore;
    public int ThrowingLoad => DaggerCount + AxeCount;
    public int ArrowOverflow  => HasQuiverOfHolding ? 0 : Math.Max(0, TotalArrows - QuiverCap);
    public int PotionOverflow => Math.Max(0, TotalPotions - PotionPouchCap);
    public int ThrowOverflow  => Math.Max(0, ThrowingLoad - ThrowBandCap);
    public int PackLoad => MaterialLoad + (ArrowOverflow + 9) / 10 + PotionOverflow + ThrowOverflow;
    public int PackRoom => HasBagOfHolding ? int.MaxValue / 4 : Math.Max(0, CarryCap - PackLoad);
    // Armor: one main suit + one under-layer. Cloth = unarmored.
    // Ability-users (spells/songs/prayers/martial arts) may ALSO wear a robe,
    // vestment or garb as a true bottom layer beneath both.
    public string MainArmor = "Cloth";
    public string UnderArmor = "";
    public string RobeWorn = "";
    public int ArmorSpellDR = 0;      // reduction vs enemy spells
    public int ArmorPrayerDR = 0;     // reduction vs enemy prayers
    public int ArmorAbsorbPct = 0;    // chance to absorb spells/prayers (+1 use)
    public bool ArmorMetal = false;   // lightning conducts through metal
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

    // ── Snapshot / restore: lets creation and level-up "go back" and undo ──
    // Snapshot is a deep clone; RestoreFrom copies a snapshot's every field
    // back into this SAME object (so allPlayers' reference stays valid).
    public Player Snapshot()
    {
        var c = (Player)MemberwiseClone();
        c.Feats = new List<string>(Feats);
        c.FeatStacks = new Dictionary<string, int>(FeatStacks);
        c.GearCounts = new Dictionary<string, int>(GearCounts);
        c.KnownSpells = new List<string>(KnownSpells);
        c.WeaponSpec = new Dictionary<string, int>(WeaponSpec);
        c.DuelistEffectTurns = new Dictionary<string, int>(DuelistEffectTurns);
        c.BrokenLimbs = new List<string>(BrokenLimbs);
        c.SpecialPotions = new Dictionary<string, int>(SpecialPotions);
        c.EnchantedWeapons = new Dictionary<string, int>(EnchantedWeapons);
        c.ActiveElixirs = new List<(string, int, Action<Player>)>(ActiveElixirs);
        return c;
    }

    public void RestoreFrom(Player s)
    {
        foreach (var f in typeof(Player).GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (!f.IsInitOnly) f.SetValue(this, f.GetValue(s));
        // Re-wrap the mutable collections so this and the snapshot stay separate
        Feats = new List<string>(s.Feats);
        FeatStacks = new Dictionary<string, int>(s.FeatStacks);
        GearCounts = new Dictionary<string, int>(s.GearCounts);
        KnownSpells = new List<string>(s.KnownSpells);
        WeaponSpec = new Dictionary<string, int>(s.WeaponSpec);
        DuelistEffectTurns = new Dictionary<string, int>(s.DuelistEffectTurns);
        BrokenLimbs = new List<string>(s.BrokenLimbs);
        SpecialPotions = new Dictionary<string, int>(s.SpecialPotions);
        EnchantedWeapons = new Dictionary<string, int>(s.EnchantedWeapons);
        ActiveElixirs = new List<(string, int, Action<Player>)>(s.ActiveElixirs);
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

    // ── Monk / chi ──
    // Martial Artists are monks by training; Vow of Silence makes anyone a monk,
    // but they may then wield ONLY monk weapons.
    public bool IsMonk => CharacterType == "Martial Artist" || HasFeat("Vow of Silence");
    public bool MonkWeaponsOnly => HasFeat("Vow of Silence") && CharacterType != "Martial Artist";
    public int ChiUses = 0;
    public int MaxChiUses() => !IsMonk ? 0 : Math.Max(1, 1 + Level / 2 + ChiTrait());
    public static readonly HashSet<string> MonkWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        // Existing gear that qualifies
        "unarmed", "", "Short Sword", "Knife", "Dagger", "Staff", "Bow", "Shortbow",
        "Hunting Bow", "Composite Bow", "Halberd",
        // Dedicated monk weapons
        "Wakizashi", "Shuriken", "Chain and Ball", "Screama Sticks", "Foldable Fan Blade",
        "Smoke Pouch", "Spike Chain", "Nunchucks", "Tanto", "Katana", "Nodachi",
        "Tetsubo", "Kanabo", "Circle Throwing Blades",
    };
    public bool IsMonkWeapon(string w) => MonkWeapons.Contains(w ?? "");
    // Monk weapons grant monks an extra attack per attack (stacks with
    // Multishot / Split Shot / Folly of Arrows / Double Tap / Fury / Flurry).
    public int MonkWeaponExtraAttacks(string w) => IsMonk && IsMonkWeapon(w) ? 1 : 0;
    public int MaxPrayerUses() => Math.Max(1, 5 + (Level / 2) * 2 + Wisdom + BonusPrayerUses);
    public int MaxSpellUses()  => Math.Max(1, 6 + (Level / 2) * 2 + Intelligence + BonusSpellUses);

    // ── Musician songs ──
    // Tier: +2 to song bonuses (and +1d6 fear dice) every 3rd level.
    public int SongTier() => Level >= 3 ? Level / 3 : 0;
    public int MaxSongTokens() => Math.Max(1, 5 + Level / 2 + Charisma + BonusSongUses);
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

