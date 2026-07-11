using Raylib_cs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

// ── Shared state pushed by game thread, read by render thread ─────────────

class PartyStat
{
    public string Name = "", Weapon = "", Shield = "";
    public int HP, MaxHP, Level;
    public int SpellUses, SongTokens, PrayerUses;
    public int Arrows, Daggers, Axes;
    public int ArmorDR, Ringlet;
    public bool CanSpell, CanSong, CanPray, IsCurrent;
}

class RenderSnapshot
{
    public GridPos PlayerPos;
    public int PlayerHP, PlayerMaxHP, PlayerLevel, WaveNum;
    public List<(GridPos Pos, string TypeName, int HP, int MaxHP)> Enemies = new();
    public List<(GridPos Pos, string Initials)> OtherPlayers = new();
    public List<PartyStat> Party = new();
    public List<GridPos> GroundWeapons = new();
    public List<GridPos> Walls = new();
    public List<GridPos> Trees = new();
    public List<GridPos> Rocks = new();
    public List<GridPos> Caves = new();
}

class SharedGameState
{
    private readonly object _lock = new();
    private RenderSnapshot _snap = new();
    public volatile bool GameOver = false;
    private readonly System.Threading.ManualResetEventSlim _assetsReady = new(false);

    public void Push(RenderSnapshot snap)  { lock (_lock) _snap = snap; }
    public RenderSnapshot Pull()           { lock (_lock) return _snap; }

    // ── Console mirror + clickable input (drives the on-screen UI) ────────
    private readonly object _ioLock = new();
    private readonly List<string> _log = new();
    private string _partial = "";
    public volatile bool WaitingForInput = false;
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> InputQueue = new();

    public void AppendOutput(string text)
    {
        lock (_ioLock)
        {
            _partial += text;
            int idx;
            while ((idx = _partial.IndexOf('\n')) >= 0)
            {
                _log.Add(_partial[..idx].TrimEnd('\r'));
                _partial = _partial[(idx + 1)..];
            }
            if (_log.Count > 400) _log.RemoveRange(0, _log.Count - 400);
        }
    }

    public (List<string> Lines, string Prompt) SnapshotLog(int max)
    {
        lock (_ioLock)
        {
            int start = Math.Max(0, _log.Count - max);
            return (_log.GetRange(start, _log.Count - start), _partial);
        }
    }

    public void Inject(string s) => InputQueue.Enqueue(s);

    // Game thread holds its first console prompt until the render thread has
    // finished loading assets, so raylib's log spam doesn't bury the prompt.
    public void SignalAssetsReady()        => _assetsReady.Set();
    public void WaitAssetsReady(int ms)    => _assetsReady.Wait(ms);
}

// ── Console mirror: every printed line also feeds the graphics UI ─────────

class ConsoleMirror : System.IO.TextWriter
{
    private readonly System.IO.TextWriter _inner;
    private readonly SharedGameState _state;
    public ConsoleMirror(System.IO.TextWriter inner, SharedGameState state) { _inner = inner; _state = state; }
    public override System.Text.Encoding Encoding => _inner.Encoding;
    public override void Write(char value) { _inner.Write(value); _state.AppendOutput(value.ToString()); }
    public override void Write(string? value) { _inner.Write(value); if (!string.IsNullOrEmpty(value)) _state.AppendOutput(value); }
    public override void WriteLine(string? value) { _inner.WriteLine(value); _state.AppendOutput((value ?? "") + "\n"); }
    public override void Flush() => _inner.Flush();
}

// ── Input shim: console typing OR clicks in the graphics window ───────────

static class GameIO
{
    public static SharedGameState? State;

    public static string? ReadLine()
    {
        var st = State;
        if (st == null) return Console.In.ReadLine();
        st.WaitingForInput = true;
        try
        {
            bool redirected;
            try { redirected = Console.IsInputRedirected; } catch { redirected = true; }
            while (true)
            {
                if (st.InputQueue.TryDequeue(out var s))
                {
                    Console.WriteLine(s);   // echo the click into the log
                    return s;
                }
                if (redirected) return Console.In.ReadLine();
                try { if (Console.KeyAvailable) return Console.ReadLine(); }
                catch { return Console.In.ReadLine(); }
                System.Threading.Thread.Sleep(25);
            }
        }
        finally { st.WaitingForInput = false; }
    }
}

// ── Raylib-based display window (runs on main thread) ─────────────────────

class GraphicsDisplay
{
    // Layout
    const int Cell    = 40;   // pixels per grid square
    const int View    = 13;   // squares visible in each direction from player
    const int ViewPx  = (View * 2 + 1) * Cell;
    const int Panel   = 260;
    const int UiH     = 236;  // bottom interaction panel (scene text + buttons)
    const int WinW    = ViewPx + Panel;
    const int WinH    = ViewPx + UiH;

    readonly SharedGameState _state;
    readonly Dictionary<string, Texture2D> _tex = new();

    public GraphicsDisplay(SharedGameState state) => _state = state;

    public void Run()
    {
        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow);
        Raylib.InitWindow(WinW, WinH, "One Who Stands Against The Horde");
        Raylib.SetWindowMinSize(Panel + Cell * 7, Cell * 10);
        Raylib.SetTargetFPS(30);

        LoadTextures();
        _state.SignalAssetsReady();

        while (!Raylib.WindowShouldClose() && !_state.GameOver)
        {
            var snap = _state.Pull();
            Raylib.BeginDrawing();
            Raylib.ClearBackground(Color.Black);
            DrawMap(snap);
            DrawUi();
            DrawPanel(snap);
            Raylib.EndDrawing();
        }

        foreach (var t in _tex.Values) Raylib.UnloadTexture(t);
        Raylib.CloseWindow();
    }

    // ── Asset loading ──────────────────────────────────────────────────────

    void LoadTextures()
    {
        // Resolve assets next to the executable first so the game finds its
        // sprites when launched by double-clicking the exe (cwd != exe dir).
        string assetDir = Path.Combine(AppContext.BaseDirectory, "assets");
        if (!Directory.Exists(assetDir))
            assetDir = "assets";

        var names = new[] { "Ogre", "OrcBarbarian", "OrcMonk", "OrcPriestess", "OrcRanger",
                            "Troll", "NecromancerTroll", "Orc",
                            "Hobgoblin", "HobgoblinFighter", "HobgoblinThief", "HobgoblinCleric",
                            "Goblin", "SpellGoblin", "RogueGoblin", "Player",
                            "Deer", "Wolf", "Boar", "Bear" };
        foreach (var n in names)
        {
            string path = Path.Combine(assetDir, n.ToLower() + ".png");
            if (File.Exists(path))
                _tex[n] = Raylib.LoadTexture(path);
        }

        // Assets with non-standard filenames
        var custom = new[]
        {
            ("GoblinWarrior", "wariorgoblin.png"),
            ("GoblinShaman", "shammangoblin.png"),
            ("OrcMonk", "monkorc.png"),
            ("OrcPriestess", "priestessorc.png"),
            ("OrcRanger", "rangerorc.png"),
            ("HobgoblinFighter", "fighterhobgoblin.png"),
            ("HobgoblinCleric", "clerichobgoblin.png"),
            ("HobgoblinThief", "thiefhobgoblin.png"),
            ("TrollWarrior", "warriortroll.png"),
            ("TrollPriest", "priesttroll.png"),
            ("TrollMusician", "musiciantroll.png"),
            ("OgreBerserker", "bersekerogre.png"),
            ("OgreDuelist", "duelistogre.png"),
            ("OgreWarrior", "warriorogre.png"),
            ("GiantEnemy", "giant.png"),
            ("GiantMage", "magegiant.png"),
            ("GiantPriest", "priestgiant.png"),
            ("GiantDuelist", "duelistgiant.png"),
        };
        foreach (var (key, file) in custom)
        {
            string path = Path.Combine(assetDir, file);
            if (File.Exists(path))
                _tex[key] = Raylib.LoadTexture(path);
        }
    }

    // ── Map rendering ──────────────────────────────────────────────────────

    void DrawMap(RenderSnapshot snap)
    {
        // Viewport is derived from the live window size: resize the window
        // and you see more (or less) of the battlefield.
        int screenW = Raylib.GetScreenWidth();
        int screenH = Raylib.GetScreenHeight() - UiH;   // bottom strip belongs to the UI
        int mapW    = Math.Max(Cell, screenW - Panel);
        int cellsX  = Math.Max(1, mapW / Cell);
        int cellsY  = Math.Max(1, screenH / Cell + 1);
        int ox = snap.PlayerPos.X - cellsX / 2;   // grid origin offset (player centered)
        int oy = snap.PlayerPos.Y - cellsY / 2;
        int mapPxW = cellsX * Cell;
        int mapPxH = cellsY * Cell;

        // Grid lines (subtle)
        for (int r = 0; r <= cellsY; r++)
        {
            int py = r * Cell;
            Raylib.DrawLine(0, py, mapPxW, py, new Color(30, 30, 30, 255));
        }
        for (int c = 0; c <= cellsX; c++)
        {
            int px = c * Cell;
            Raylib.DrawLine(px, 0, px, mapPxH, new Color(30, 30, 30, 255));
        }

        // Terrain: camp walls, trees, rocks
        foreach (var w in snap.Walls)
        {
            int sx = (w.X - ox) * Cell;
            int sy = (w.Y - oy) * Cell;
            if (sx < 0 || sy < 0 || sx >= mapPxW || sy >= mapPxH) continue;
            Raylib.DrawRectangle(sx, sy, Cell, Cell, new Color(115, 80, 48, 255));
            Raylib.DrawRectangleLines(sx, sy, Cell, Cell, new Color(70, 46, 25, 255));
        }
        foreach (var cv in snap.Caves)
        {
            int sx = (cv.X - ox) * Cell;
            int sy = (cv.Y - oy) * Cell;
            if (sx < 0 || sy < 0 || sx >= mapPxW || sy >= mapPxH) continue;
            Raylib.DrawCircle(sx + Cell / 2, sy + Cell / 2, Cell * 0.40f, new Color(45, 40, 38, 255));
            Raylib.DrawCircle(sx + Cell / 2, sy + Cell / 2 + 4, Cell * 0.24f, new Color(15, 12, 12, 255));
        }
        foreach (var t in snap.Trees)
        {
            int sx = (t.X - ox) * Cell;
            int sy = (t.Y - oy) * Cell;
            if (sx < 0 || sy < 0 || sx >= mapPxW || sy >= mapPxH) continue;
            Raylib.DrawCircle(sx + Cell / 2, sy + Cell / 2, Cell * 0.38f, new Color(20, 105, 35, 255));
            Raylib.DrawRectangle(sx + Cell / 2 - 3, sy + Cell - 12, 6, 10, new Color(100, 65, 30, 255));
        }
        foreach (var rk in snap.Rocks)
        {
            int sx = (rk.X - ox) * Cell;
            int sy = (rk.Y - oy) * Cell;
            if (sx < 0 || sy < 0 || sx >= mapPxW || sy >= mapPxH) continue;
            Raylib.DrawCircle(sx + Cell / 2, sy + Cell / 2 + 4, Cell * 0.32f, new Color(120, 120, 128, 255));
        }

        // Ground weapons (small yellow dot)
        foreach (var wp in snap.GroundWeapons)
        {
            int sx = (wp.X - ox) * Cell;
            int sy = (wp.Y - oy) * Cell;
            if (sx < 0 || sy < 0 || sx >= mapPxW || sy >= mapPxH) continue;
            Raylib.DrawRectangle(sx + 6, sy + 6, Cell - 12, Cell - 12, Color.Gold);
        }

        // Enemies
        foreach (var (pos, type, hp, maxHp) in snap.Enemies)
        {
            int sx = (pos.X - ox) * Cell;
            int sy = (pos.Y - oy) * Cell;
            if (sx < 0 || sy < 0 || sx >= mapPxW || sy >= mapPxH) continue;
            DrawEntity(sx, sy, type, hp, maxHp);
        }

        // Other party members (blue tile, black initials)
        foreach (var (pos, initials) in snap.OtherPlayers)
        {
            int sx = (pos.X - ox) * Cell;
            int sy = (pos.Y - oy) * Cell;
            if (sx < 0 || sy < 0 || sx >= mapPxW || sy >= mapPxH) continue;
            Raylib.DrawRectangle(sx + 1, sy + 1, Cell - 2, Cell - 2, new Color(30, 110, 255, 255));
            Raylib.DrawText(initials, sx + 6, sy + 10, 18, Color.Black);
        }

        // Player (always at the viewport center)
        {
            int sx = (snap.PlayerPos.X - ox) * Cell;
            int sy = (snap.PlayerPos.Y - oy) * Cell;
            if (_tex.TryGetValue("Player", out var ptex))
            {
                var dst = new Rectangle(sx, sy, Cell, Cell);
                Raylib.DrawTexturePro(ptex,
                    new Rectangle(0, 0, ptex.Width, ptex.Height),
                    dst, Vector2.Zero, 0, Color.White);
            }
            else
            {
                Raylib.DrawRectangle(sx + 1, sy + 1, Cell - 2, Cell - 2, new Color(0, 255, 255, 255));
                Raylib.DrawText("@", sx + 8, sy + 10, 20, Color.Black);
            }
        }
    }

    void DrawEntity(int sx, int sy, string type, int hp, int maxHp)
    {
        if (_tex.TryGetValue(type, out var tex))
        {
            var dst = new Rectangle(sx, sy, Cell, Cell);
            Raylib.DrawTexturePro(tex,
                new Rectangle(0, 0, tex.Width, tex.Height),
                dst, Vector2.Zero, 0, Color.White);
        }
        else
        {
            var col = EnemyColor(type);
            Raylib.DrawRectangle(sx + 1, sy + 1, Cell - 2, Cell - 2, col);
            string lbl = type switch
            {
                "Goblin"           => "G",
                "GoblinWarrior"    => "GW",
                "RogueGoblin"      => "RG",
                "GoblinShaman"     => "GS",
                "SpellGoblin"      => "SG",
                "Hobgoblin"        => "H",
                "HobgoblinFighter" => "HF",
                "HobgoblinThief"   => "HT",
                "HobgoblinCleric"  => "HC",
                "Orc"              => "O",
                "OrcBarbarian"     => "OB",
                "OrcMonk"          => "OM",
                "OrcPriestess"     => "OP",
                "OrcRanger"        => "OR",
                "Troll"            => "T",
                "TrollWarrior"     => "TW",
                "TrollPriest"      => "TP",
                "TrollMusician"    => "TM",
                "NecromancerTroll" => "NT",
                "Ogre"             => "Og",
                "OgreWarrior"      => "OW",
                "OgreDuelist"      => "OD",
                "OgreBerserker"    => "OZ",
                "GiantEnemy"       => "Gi",
                "GiantMage"        => "GM",
                "GiantPriest"      => "GP",
                "GiantDuelist"     => "GD",
                "Deer"             => "De",
                "Wolf"             => "Wf",
                "Boar"             => "Br",
                "Bear"             => "BE",
                _                  => "?"
            };
            Raylib.DrawText(lbl, sx + 4, sy + 10, 18, Color.Black);
        }

        // HP bar (4px strip at bottom of cell)
        if (maxHp > 0)
        {
            int barW = (int)((Cell - 2) * hp / (float)maxHp);
            Raylib.DrawRectangle(sx + 1, sy + Cell - 5, Cell - 2, 4, Color.DarkGray);
            Raylib.DrawRectangle(sx + 1, sy + Cell - 5, barW,     4, new Color(50, 205, 50, 255));
        }
    }

    // ── Status panel ───────────────────────────────────────────────────────

    static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ── Interaction panel: scene text, current prompt, clickable choices ──

    static readonly System.Text.RegularExpressions.Regex OptRx =
        new(@"\[([A-Za-z0-9]{1,3})\]\s?([A-Za-z0-9''/&+ -]{0,16})", System.Text.RegularExpressions.RegexOptions.Compiled);

    bool UiButton(int x, int y, int w, int h, string label, Color bg)
    {
        var mouse = Raylib.GetMousePosition();
        bool hover = mouse.X >= x && mouse.X <= x + w && mouse.Y >= y && mouse.Y <= y + h;
        Raylib.DrawRectangle(x, y, w, h, hover ? new Color(90, 110, 160, 255) : bg);
        Raylib.DrawRectangleLines(x, y, w, h, new Color(120, 130, 170, 255));
        int tw = Raylib.MeasureText(label, 12);
        Raylib.DrawText(label, x + Math.Max(3, (w - tw) / 2), y + (h - 12) / 2, 12, Color.White);
        return hover && Raylib.IsMouseButtonPressed(MouseButton.Left);
    }

    void DrawUi()
    {
        int screenW = Raylib.GetScreenWidth();
        int screenH = Raylib.GetScreenHeight();
        int uiW = Math.Max(200, screenW - Panel);
        int uiY = screenH - UiH;

        Raylib.DrawRectangle(0, uiY, uiW, UiH, new Color(14, 14, 20, 255));
        Raylib.DrawLine(0, uiY, uiW, uiY, new Color(60, 60, 80, 255));

        var (lines, prompt) = _state.SnapshotLog(9);
        int maxChars = Math.Max(20, uiW / 7);
        int y = uiY + 6;
        foreach (var ln in lines)
        {
            Raylib.DrawText(Trunc(ln, maxChars), 8, y, 12, new Color(190, 190, 200, 255));
            y += 14;
        }
        bool waiting = _state.WaitingForInput;
        Raylib.DrawText(Trunc(prompt, maxChars) + (waiting ? " _" : ""), 8, y, 13,
            waiting ? Color.Yellow : Color.Gray);

        // Parse clickable options from the recent scene text
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buttons = new List<(string Token, string Label)>();
        var searchLines = new List<string>(lines) { prompt };
        for (int li = searchLines.Count - 1; li >= 0 && buttons.Count < 18; li--)
        {
            foreach (System.Text.RegularExpressions.Match m in OptRx.Matches(searchLines[li]))
            {
                string token = m.Groups[1].Value;
                string label = m.Groups[2].Value.Trim();
                if (!seen.Add(token + "|" + label)) continue;
                buttons.Add((token, label.Length > 0 ? $"{token} {label}" : token));
                if (buttons.Count >= 18) break;
            }
            // Stop once we've collected options from a line that had several —
            // older lines are usually a previous, stale menu.
            if (buttons.Count >= 6) break;
        }

        int bx = 8, by = uiY + UiH - 84;
        foreach (var (token, label) in buttons)
        {
            int w = Math.Max(34, Raylib.MeasureText(label, 12) + 14);
            if (bx + w > uiW - 8) { bx = 8; by += 26; }
            if (by > screenH - 28) break;
            if (UiButton(bx, by, w, 24, label, new Color(50, 60, 95, 255)) && waiting)
                _state.Inject(token.ToLowerInvariant());
            bx += w + 6;
        }

        // Standard row: quick numbers, yes/no, Enter
        int sy = screenH - 28;
        int sx = 8;
        string[] std = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "y", "n" };
        foreach (var t in std)
        {
            int w = t.Length > 1 ? 30 : 24;
            if (UiButton(sx, sy, w, 22, t.ToUpper(), new Color(40, 42, 58, 255)) && waiting)
                _state.Inject(t);
            sx += w + 4;
        }
        if (UiButton(sx, sy, 58, 22, "ENTER", new Color(40, 42, 58, 255)) && waiting)
            _state.Inject("");
        sx += 62;
        Raylib.DrawText("click a choice — or type in the console for names/amounts", sx + 6, sy + 5, 11, Color.Gray);
    }

    void DrawPanel(RenderSnapshot snap)
    {
        int screenW = Raylib.GetScreenWidth();
        int screenH = Raylib.GetScreenHeight();
        int panelX = screenW - Panel;
        int px = panelX + 10;
        Raylib.DrawRectangle(panelX, 0, Panel, screenH, new Color(18, 18, 24, 255));
        Raylib.DrawLine(panelX, 0, panelX, screenH, new Color(60, 60, 80, 255));

        Raylib.DrawText("OWSATH", px, 10, 20, Color.White);
        Raylib.DrawText($"Wave {snap.WaveNum}", px + 110, 14, 15, Color.LightGray);
        Raylib.DrawText($"Enemies: {snap.Enemies.Count}", px, 34, 13, Color.Orange);

        // ── Party: HP, uses, ammo, gear, level for every player ──
        int y = 54;
        foreach (var m in snap.Party)
        {
            var nameCol = m.HP <= 0 ? Color.Gray : (m.IsCurrent ? Color.Yellow : Color.White);
            Raylib.DrawText($"{(m.IsCurrent ? "> " : "")}{Trunc(m.Name, 17)}  Lv{m.Level}", px, y, 13, nameCol);
            y += 15;

            bool crit = m.HP <= m.MaxHP / 4;
            var hpCol = m.HP <= 0 ? Color.Gray : (crit ? Color.Red : new Color(50, 205, 50, 255));
            Raylib.DrawText($"HP {m.HP}/{m.MaxHP}", px, y, 12, hpCol);
            int barW = m.MaxHP > 0 ? (int)((Panel - 110) * Math.Clamp(m.HP, 0, m.MaxHP) / (float)m.MaxHP) : 0;
            Raylib.DrawRectangle(px + 88, y + 2, Panel - 110, 8, new Color(50, 50, 50, 255));
            Raylib.DrawRectangle(px + 88, y + 2, barW, 8, hpCol);
            y += 14;

            var uses = new List<string>();
            if (m.CanSpell) uses.Add($"Spells {m.SpellUses}");
            if (m.CanPray)  uses.Add($"Prayers {m.PrayerUses}");
            if (m.CanSong)  uses.Add($"Songs {m.SongTokens}");
            if (uses.Count > 0)
            {
                Raylib.DrawText(string.Join("  ", uses), px, y, 11, new Color(120, 190, 255, 255));
                y += 13;
            }

            var ammo = new List<string>();
            if (m.Arrows > 0)  ammo.Add($"Arrows {m.Arrows}");
            if (m.Daggers > 0) ammo.Add($"Daggers {m.Daggers}");
            if (m.Axes > 0)    ammo.Add($"Axes {m.Axes}");
            if (ammo.Count > 0)
            {
                Raylib.DrawText(string.Join("  ", ammo), px, y, 11, Color.Gold);
                y += 13;
            }

            var gear = new List<string> { m.Weapon };
            if (m.Shield != "")  gear.Add($"Shld {m.Shield}");
            if (m.ArmorDR > 0)   gear.Add($"Armor -{m.ArmorDR}");
            if (m.Ringlet > 0)   gear.Add($"Ring +{m.Ringlet}");
            Raylib.DrawText(Trunc(string.Join("  ", gear), 36), px, y, 11, Color.LightGray);
            y += 19;
        }

        // Legend (anchored to the window bottom; hidden if the party list needs the room)
        int ly = screenH - 370;
        if (ly < y) return;
        Raylib.DrawText("Legend", px, ly, 13, Color.Gray);
        DrawLegend(px, ly + 336, "AB", "Ally player", new Color(30, 110, 255, 255));
        DrawLegend(px, ly +  16, "G",  "Goblin",       new Color(144, 238, 144, 255));
        DrawLegend(px, ly +  32, "GW", "Goblin War",   new Color(100, 200, 100, 255));
        DrawLegend(px, ly +  48, "RG", "Rogue Gob",    new Color(200, 220,  80, 255));
        DrawLegend(px, ly +  64, "GS", "Gob Shaman",   new Color(100, 180, 220, 255));
        DrawLegend(px, ly +  80, "SG", "Spell Goblin", new Color(180,  80, 220, 255));
        DrawLegend(px, ly +  96, "H",  "Hobgoblin",    Color.Green);
        DrawLegend(px, ly + 112, "HF", "Hob Fighter",  new Color( 60, 180,  60, 255));
        DrawLegend(px, ly + 128, "HT", "Hob Thief",    new Color(180, 200,  80, 255));
        DrawLegend(px, ly + 144, "HC", "Hob Cleric",   new Color(120, 200, 180, 255));
        DrawLegend(px, ly + 160, "O",  "Orc",          new Color(200, 100,  50, 255));
        DrawLegend(px, ly + 176, "OB", "Orc Barb",     new Color(220,  60,  20, 255));
        DrawLegend(px, ly + 192, "OM", "Orc Monk",     new Color(200,  60, 140, 255));
        DrawLegend(px, ly + 208, "OP", "Orc Priest",   new Color(180, 180, 220, 255));
        DrawLegend(px, ly + 224, "OR", "Orc Ranger",   new Color(100, 160,  60, 255));
        DrawLegend(px, ly + 240, "T",  "Troll",        new Color( 80, 130,  80, 255));
        DrawLegend(px, ly + 256, "TW", "Troll War",    new Color(110, 130,  60, 255));
        DrawLegend(px, ly + 272, "TP", "Troll Priest", new Color( 90, 110, 140, 255));
        DrawLegend(px, ly + 288, "TM", "Troll Music",  new Color(140, 110,  60, 255));
        DrawLegend(px, ly + 304, "NT", "Necro Troll",  new Color( 80,  60, 120, 255));
        DrawLegend(px, ly + 320, "Og", "Ogre",         new Color(180,  80,  80, 255));
    }

    void DrawLegend(int x, int y, string abbr, string label, Color col)
    {
        Raylib.DrawRectangle(x, y, 18, 14, col);
        Raylib.DrawText(abbr,   x + 2, y + 1,  9, Color.Black);
        Raylib.DrawText(label,  x + 22, y + 1, 11, Color.LightGray);
    }

    // ── Colour table for entities without sprite ────────────────────────────

    static Color EnemyColor(string type) => type switch
    {
        "Goblin"           => new Color(144, 238, 144, 255),
        "GoblinWarrior"    => new Color(100, 200, 100, 255),
        "RogueGoblin"      => new Color(200, 220,  80, 255),
        "GoblinShaman"     => new Color(100, 180, 220, 255),
        "SpellGoblin"      => new Color(180,  80, 220, 255),
        "Hobgoblin"        => Color.Green,
        "HobgoblinFighter" => new Color( 60, 180,  60, 255),
        "HobgoblinThief"   => new Color(180, 200,  80, 255),
        "HobgoblinCleric"  => new Color(120, 200, 180, 255),
        "Orc"              => new Color(200, 100,  50, 255),
        "OrcBarbarian"     => new Color(220,  60,  20, 255),
        "OrcMonk"          => new Color(200,  60, 140, 255),
        "OrcPriestess"     => new Color(180, 180, 220, 255),
        "OrcRanger"        => new Color(100, 160,  60, 255),
        "Troll"            => new Color( 80, 130,  80, 255),
        "TrollWarrior"     => new Color(110, 130,  60, 255),
        "TrollPriest"      => new Color( 90, 110, 140, 255),
        "TrollMusician"    => new Color(140, 110,  60, 255),
        "NecromancerTroll" => new Color( 80,  60, 120, 255),
        "Ogre"             => new Color(180,  80,  80, 255),
        "OgreWarrior"      => new Color(200,  90,  60, 255),
        "OgreDuelist"      => new Color(190,  70, 110, 255),
        "OgreBerserker"    => new Color(230,  50,  50, 255),
        "GiantEnemy"       => new Color(160, 140, 200, 255),
        "GiantMage"        => new Color(120,  90, 230, 255),
        "GiantPriest"      => new Color(200, 180, 120, 255),
        "GiantDuelist"     => new Color(220, 140, 180, 255),
        "Deer"             => new Color(210, 180, 140, 255),
        "Wolf"             => new Color(150, 150, 160, 255),
        "Boar"             => new Color(140,  95,  60, 255),
        "Bear"             => new Color(100,  60,  30, 255),
        _                  => Color.Gray
    };
}
