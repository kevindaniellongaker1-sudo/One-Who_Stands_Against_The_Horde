using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

// ═════════════════════════════════════════════════════════════════════════
//  CombatTerrain.cs — the battlefield: grid, terrain, movement, the map.
// ═════════════════════════════════════════════════════════════════════════
//
//  The field is a 50x50 grid of GridPos. One square is 5 feet, and Feet()
//  returns ManhattanDist * 2.5 — so a "20ft throw" reaches 8 squares. Range
//  checks are in FEET, movement is in SQUARES. Mixing the two is an easy and
//  genuinely common bug.
//
//  GenerateTerrain() rolls 1-2 palisade camps on the right with a gate facing
//  the party, then scatters trees, rocks and caves. Walls block everyone;
//  trees and rocks can be climbed for high ground (+2 attack, +1 dodge).
//
//  StepToward() is the pathing, and it is wall-AWARE: when something walks
//  into a wall it follows along it (SlideDir) instead of grinding in place.
//  That state is per-mover, which is why `mover` is passed in — enemies once
//  froze solid against their own camp walls for exactly this reason.
//
//  PlaceEnemies() clusters spawns around _campCenter; in-combat
//  reinforcements arrive from the right edge instead.
//
// ═════════════════════════════════════════════════════════════════════════

partial class CombatSession
{
    // ── GRID HELPERS ─────────────────────────────────────────────────────

    // Random camp (palisade walls with a gate facing the players) on the right
    // side of the map, plus scattered climbable trees and rocks.
    void GenerateTerrain()
    {
        int camps = Rng.Next(1, 3);
        for (int c = 0; c < camps; c++)
        {
            int w = Rng.Next(6, 10), h = Rng.Next(5, 8);
            int x0 = Rng.Next(34, 49 - w);
            int y0 = Rng.Next(3, 46 - h);
            if (c == 0) _campCenter = (x0 + w / 2, y0 + h / 2);
            for (int x = x0; x <= x0 + w; x++) { Walls.Add((x, y0)); Walls.Add((x, y0 + h)); }
            for (int y = y0; y <= y0 + h; y++) { Walls.Add((x0, y)); Walls.Add((x0 + w, y)); }
            // Gate on the west wall, facing the players
            int gy = y0 + h / 2;
            Walls.Remove((x0, gy));
            Walls.Remove((x0, Math.Min(y0 + h - 1, gy + 1)));
        }
        int trees = Rng.Next(10, 18), rocks = Rng.Next(6, 12);
        for (int i = 0; i < trees; i++)
        {
            var p = (Rng.Next(2, 48), Rng.Next(1, 49));
            if (!Walls.Contains(p)) Trees.Add(p);
        }
        for (int i = 0; i < rocks; i++)
        {
            var p = (Rng.Next(2, 48), Rng.Next(1, 49));
            if (!Walls.Contains(p) && !Trees.Contains(p)) Rocks.Add(p);
        }
        int caves = Rng.Next(1, 4);
        for (int i = 0; i < caves; i++)
        {
            var p = (Rng.Next(4, 46), Rng.Next(2, 48));
            if (!Walls.Contains(p) && !Trees.Contains(p) && !Rocks.Contains(p)) Caves.Add(p);
        }

        // A river winds down the left-center of the field (clear of the camps).
        // It is crossable, but each river square costs DOUBLE movement — and
        // bears fish in it to heal. Not every battlefield has one.
        if (Rng.Next(100) < 70)
        {
            int rx = Rng.Next(8, 22);
            for (int y = 0; y < 50; y++)
            {
                rx = Math.Clamp(rx + Rng.Next(-1, 2), 6, 26);
                var p1 = (rx, y);
                if (!Walls.Contains(p1)) { Rivers.Add(p1); Trees.Remove(p1); Rocks.Remove(p1); Caves.Remove(p1); }
                if (Rng.Next(100) < 45)   // widen to two squares in places
                {
                    var p2 = (rx + 1, y);
                    if (!Walls.Contains(p2)) { Rivers.Add(p2); Trees.Remove(p2); Rocks.Remove(p2); Caves.Remove(p2); }
                }
            }
        }
    }

    bool IsRiver(int x, int y) => Rivers.Contains((x, y));

    // Wildlife roams every battlefield: deer 1d9-1 (1d4-1 antlered), wolves
    // 1d6-1, boars 1d5-1, bears 1d4-1 (denned near caves or trees)
    void SpawnWildlife()
    {
        GridPos FreeSpot()
        {
            for (int a = 0; a < 60; a++)
            {
                var p = new GridPos(Rng.Next(3, 47), Rng.Next(2, 48));
                if (!IsWall(p.X, p.Y) && !Active.Any(e => e.Alive && e.Position.SameAs(p))) return p;
            }
            return new GridPos(25, 25);
        }

        int deer = Rng.Next(1, 10) - 1;
        int antlered = Rng.Next(1, 5) - 1;
        for (int i = 0; i < deer; i++)
        {
            var d = new Deer(Rng, $"Deer {i + 1}", i < antlered);
            d.Position = FreeSpot();
            Active.Add(d);
        }
        int wolves = Rng.Next(1, 7) - 1;
        for (int i = 0; i < wolves; i++)
        {
            var w = new Wolf(Rng, $"Wolf {i + 1}");
            w.Position = FreeSpot();
            Active.Add(w);
        }
        int boars = Rng.Next(1, 6) - 1;
        for (int i = 0; i < boars; i++)
        {
            var b = new Boar(Rng, $"Boar {i + 1}");
            b.Position = FreeSpot();
            Active.Add(b);
        }
        int bears = Rng.Next(1, 5) - 1;
        for (int i = 0; i < bears; i++)
        {
            var b = new Bear(Rng, $"Bear {i + 1}");
            var den = Caves.Concat(Trees).OrderBy(_ => Rng.Next()).FirstOrDefault();
            b.Position = den == default ? FreeSpot()
                       : new GridPos(Math.Clamp(den.X + Rng.Next(-1, 2), 0, 49), Math.Clamp(den.Y + Rng.Next(-1, 2), 0, 49));
            Active.Add(b);
        }
    }

    bool IsWall(int x, int y) => Walls.Contains((x, y));
    bool IsClimbable(GridPos p) => Trees.Contains((p.X, p.Y)) || Rocks.Contains((p.X, p.Y));

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
                int x, y;
                if (nearEdge)
                {
                    // Reinforcements still pour in from the right edge
                    x = Rng.Next(44, 50);
                    y = Rng.Next(1, 49);
                }
                else
                {
                    // The horde musters in and around its camp
                    x = Math.Clamp(_campCenter.X + Rng.Next(-7, 8), 20, 49);
                    y = Math.Clamp(_campCenter.Y + Rng.Next(-7, 8), 1, 48);
                }
                pos = new GridPos(x, y);
                attempts++;
            } while ((occupied.Contains((pos.X, pos.Y)) || IsWall(pos.X, pos.Y)) && attempts < 100);
            occupied.Add((pos.X, pos.Y));
            e.Position = pos;
        }
    }

    GridPos StepToward(GridPos from, GridPos target, Enemy? mover = null, HashSet<(int, int)>? occupied = null)
    {
        bool Blocked(int x, int y) => IsWall(x, y) || (occupied != null && occupied.Contains((x, y)));
        int dx = Math.Sign(target.X - from.X);
        int dy = Math.Sign(target.Y - from.Y);
        int adx = Math.Abs(target.X - from.X);
        int ady = Math.Abs(target.Y - from.Y);
        if (adx == 0 && ady == 0) return from;

        // Preferred step: close the larger gap first
        (int cx, int cy) primary = (adx >= ady && dx != 0) ? (dx, 0) : (0, dy);
        if (primary == (0, 0)) primary = (dx, 0);
        var pCell = new GridPos(Math.Clamp(from.X + primary.cx, 0, 49), Math.Clamp(from.Y + primary.cy, 0, 49));
        if (!pCell.SameAs(from) && !Blocked(pCell.X, pCell.Y))
        {
            if (mover != null) mover.SlideDir = 0;
            return pCell;
        }

        // Primary blocked: wall-follow along the perpendicular axis with a
        // remembered direction, so creatures walk around obstacles (and out
        // through camp gates) instead of oscillating in place.
        bool primaryIsX = primary.cx != 0;
        int slide = mover?.SlideDir ?? 0;
        if (slide == 0)
            slide = primaryIsX ? (dy != 0 ? dy : (Rng.Next(2) == 0 ? 1 : -1))
                               : (dx != 0 ? dx : (Rng.Next(2) == 0 ? 1 : -1));
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var sCell = primaryIsX
                ? new GridPos(from.X, Math.Clamp(from.Y + slide, 0, 49))
                : new GridPos(Math.Clamp(from.X + slide, 0, 49), from.Y);
            if (!sCell.SameAs(from) && !Blocked(sCell.X, sCell.Y))
            {
                if (mover != null) mover.SlideDir = slide;
                return sCell;
            }
            slide = -slide;   // hit a corner — reverse along the wall
        }

        // Boxed in on both sides: try backing off the wall
        var back = new GridPos(Math.Clamp(from.X - primary.cx, 0, 49), Math.Clamp(from.Y - primary.cy, 0, 49));
        if (!back.SameAs(from) && !Blocked(back.X, back.Y)) return back;
        return from;
    }

    // Spend as many actions as needed closing on the player (leaving at least
    // the remaining actions for attacks once adjacent).
    void AdvanceOnPlayer(Enemy e, ref int actions)
    {
        while (actions > 0 && !e.Position.IsCardinalAdjacent(PlayerPos))
        {
            var before = e.Position;
            MoveTowardPlayer(e, ref actions);
            if (e.Position.SameAs(before)) break;   // boxed in — stop burning actions
        }
    }

    // ── One enemy step with the shared terrain rules ──
    // Rivers: stepping into a river square sets Wading; the NEXT step is spent
    // struggling out (double cost). Collisions: walking into another enemy
    // knocks down whichever of the two has the lower best-of-Strength/Dex.
    // Returns true if the mover actually advanced.
    bool TryBeastStep(Enemy mover, GridPos target)
    {
        if (mover.Wading)
        {
            mover.Wading = false;
            Console.WriteLine($"  {mover.Name} struggles through the river.");
            return false;
        }
        var step = StepToward(mover.Position, target, mover);
        if (step.SameAs(mover.Position)) return false;
        var blocker = Active.FirstOrDefault(o => o.Alive && o != mover && o.Position.SameAs(step));
        if (blocker != null)
        {
            CollideEnemies(mover, blocker);
            return false;
        }
        mover.Position = step;
        if (IsRiver(step.X, step.Y)) mover.Wading = true;
        return true;
    }

    // Two bodies, one square: the stronger/nimbler one keeps its feet.
    void CollideEnemies(Enemy mover, Enemy blocker)
    {
        int a = Math.Max(mover.Strength, mover.Dexterity);
        int b = Math.Max(blocker.Strength, blocker.Dexterity);
        var down = a > b ? blocker : b > a ? mover : (Rng.Next(2) == 0 ? mover : blocker);
        down.KnockedDown = true; down.OffBalance = true;
        Console.WriteLine($"  {mover.Name} barrels into {blocker.Name} — {down.Name} is knocked DOWN!");
    }

    void MoveTowardPlayer(Enemy e, ref int actions, bool suppressCost = false)
    {
        if (e.Position.IsCardinalAdjacent(PlayerPos)) return;
        // Wading out of a river costs this action's movement
        if (e.Wading)
        {
            e.Wading = false;
            Console.WriteLine($"  {e.Name} wades through the river — slow going.");
            if (!suppressCost) actions--;
            return;
        }
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
            e.Position = StepToward(e.Position, PlayerPos, e, occupied);
            // Second step
            if (!e.Position.IsCardinalAdjacent(PlayerPos))
                e.Position = StepToward(e.Position, PlayerPos, e, occupied);
            e.SprintPenalty = 2;
            Console.WriteLine($"  {e.Name} sprints! (-2 to next action roll)");
        }
        else
        {
            e.Position = StepToward(e.Position, PlayerPos, e, occupied);
        }
        if (IsRiver(e.Position.X, e.Position.Y)) e.Wading = true;   // pays next action
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
        Console.WriteLine("  Map (@ you  g goblin  s spell-goblin  h hob  o orc  B orc-barb  t troll  O ogre  x axe  w weapon  # wall  T tree  r rock):");
        for (int y = PlayerPos.Y - hh; y <= PlayerPos.Y + hh; y++)
        {
            Console.Write("  ");
            for (int x = PlayerPos.X - hw; x <= PlayerPos.X + hw; x++)
            {
                if (x < 0 || x > 49 || y < 0 || y > 49) { Console.Write('#'); continue; }
                var pos = new GridPos(x, y);
                if (pos.SameAs(PlayerPos)) { Console.Write('@'); continue; }
                if (IsWall(x, y)) { Console.Write('#'); continue; }
                bool isAxe = Active.OfType<Troll>().Any(tr => tr.ThrownAxePositions.Any(ap => ap.SameAs(pos)));
                if (isAxe) { Console.Write('x'); continue; }
                var en = alive.FirstOrDefault(a => a.Position.SameAs(pos));
                if (en != null) { Console.Write(EnemyChar(en)); continue; }
                bool isWeapon = GroundWeapons.Any(w => w.Pos.SameAs(pos));
                if (isWeapon) { Console.Write('w'); continue; }
                if (Trees.Contains((x, y))) { Console.Write('T'); continue; }
                if (Rocks.Contains((x, y))) { Console.Write('r'); continue; }
                if (Caves.Contains((x, y))) { Console.Write('C'); continue; }
                if (Rivers.Contains((x, y))) { Console.Write('~'); continue; }
                Console.Write('.');
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
        GiantEnemy => 'G',
        Deer => 'd',
        Wolf => 'f',
        Boar => 'p',
        Bear => 'b',
        _ => '?'
    };

    // ── WILDLIFE AI ───────────────────────────────────────────────────────

}
