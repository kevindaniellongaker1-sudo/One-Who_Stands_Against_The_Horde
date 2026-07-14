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
    public List<(GridPos Pos, string Initials, string Sprite)> OtherPlayers = new();
    public string PlayerSprite = "";   // race|class|gender|variant of the acting player
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
    const int UiH     = 288;  // bottom interaction panel (scene text + buttons)
    const int WinW    = ViewPx + Panel;
    const int WinH    = ViewPx + UiH;

    readonly SharedGameState _state;
    readonly Dictionary<string, Texture2D> _tex = new();
    int _cellSize = Cell;   // live map zoom (pixels per square); +/- buttons adjust it
    float _uiScale = 1.0f;  // UI text zoom (A- / A+ buttons)
    float _optScroll = 0;   // scroll offset (px) for the option-button list
    int _uiH = UiH;         // current panel height (drag the top edge to resize)
    int _logScroll = 0;     // scene-text history scroll (lines back from latest)
    bool _dragUi = false;   // dragging the panel divider
    string _typeBuf = "";   // in-window typed text (names, amounts, any input)

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
                            "Goblin", "SpellGoblin", "RogueGoblin",
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

        // Player sprites + compositor layers: load every file whose name starts
        // with a known layer prefix, keyed by filename stem. "player_*" are flat
        // full-character overrides; the rest are stackable, tintable layers.
        if (Directory.Exists(assetDir))
            foreach (var prefix in new[] { "player", "body", "clothing", "armor",
                                           "eyes", "hair", "facial", "head", "weapon" })
                foreach (var f in Directory.GetFiles(assetDir, prefix + "*.png"))
                {
                    string stem = Path.GetFileNameWithoutExtension(f).ToLower();
                    if (!_tex.ContainsKey(stem)) _tex[stem] = Raylib.LoadTexture(f);
                }
    }

    // ── Layered character compositor ─────────────────────────────────────────
    // Layers stack back-to-front. Anything whose PNG is missing simply doesn't
    // draw, so art can be added one layer at a time.

    static Color TintFor(string name) => name switch
    {
        "black"     => new Color(48, 48, 52, 255),
        "blonde"    => new Color(232, 200, 120, 255),
        "brown"     => new Color(122, 78, 44, 255),
        "red"       => new Color(184, 46, 38, 255),
        "white"     => new Color(236, 236, 238, 255),
        "blue"      => new Color(58, 100, 205, 255),
        "green"     => new Color(56, 166, 78, 255),
        "hazel"     => new Color(150, 120, 72, 255),
        "yellow"    => new Color(226, 206, 66, 255),
        "purple"    => new Color(146, 66, 186, 255),
        "turquoise" => new Color(60, 200, 190, 255),
        _           => Color.White,
    };

    static string At(string[] a, int i) => i >= 0 && i < a.Length ? a[i] : "";

    Texture2D? FirstTex(params string[] keys)
    {
        foreach (var k in keys)
            if (k.Length > 0 && _tex.TryGetValue(k, out var t)) return t;
        return null;
    }

    void DrawLayer(int sx, int sy, Texture2D? tex, Color tint)
    {
        if (tex is Texture2D t)
            Raylib.DrawTexturePro(t, new Rectangle(0, 0, t.Width, t.Height),
                new Rectangle(sx, sy, _cellSize, _cellSize), Vector2.Zero, 0, tint);
    }

    // Build a character from tinted layers. Returns false if not even a body
    // exists (caller then falls back to the letter tile).
    bool DrawComposite(int sx, int sy, string[] f)
    {
        string race = At(f, 0), cls = At(f, 1), gen = At(f, 2);
        string hairColor = At(f, 3), hairLen = At(f, 4), eyeColor = At(f, 5);
        string headwear = At(f, 6), clothColor = At(f, 7), facial = At(f, 8);
        string weapon = At(f, 9), armor = At(f, 10);

        var body = FirstTex($"body_{race}_{gen}", $"body_{race}", $"body_{gen}", "body");
        if (body is null) return false;
        DrawLayer(sx, sy, body, Color.White);

        // Clothing, or armor sprite when something better than cloth is worn
        if (armor.Length > 0 && armor != "cloth")
            DrawLayer(sx, sy, FirstTex($"armor_{armor}_{race}_{gen}", $"armor_{armor}", $"armor_{race}", "armor"), Color.White);
        else
            DrawLayer(sx, sy, FirstTex($"clothing_{cls}_{race}_{gen}", $"clothing_{cls}", $"clothing_{race}", "clothing"), TintFor(clothColor));

        DrawLayer(sx, sy, FirstTex($"eyes_{race}_{gen}", $"eyes_{race}", "eyes"), TintFor(eyeColor));

        if (hairLen.Length > 0 && hairLen != "bald")
            DrawLayer(sx, sy, FirstTex($"hair_{hairLen}_{race}_{gen}", $"hair_{hairLen}", $"hair_{race}", "hair"), TintFor(hairColor));

        if (facial.Length > 0 && facial != "none")
            DrawLayer(sx, sy, FirstTex($"facial_{facial}_{race}", $"facial_{facial}", "facial"), TintFor(hairColor));

        if (headwear.Length > 0 && headwear != "nothing")
            DrawLayer(sx, sy, FirstTex($"head_{headwear}_{race}_{gen}", $"head_{headwear}", "head"), Color.White);

        if (weapon.Length > 0 && weapon != "unarmed")
            DrawLayer(sx, sy, FirstTex($"weapon_{weapon}", "weapon"), Color.White);

        return true;
    }

    // Optional flat, hand-drawn full-character override (takes priority over the
    // compositor). Keyed on race/class/gender, most specific to least.
    Texture2D? PlayerTexture(string desc)
    {
        var f = (desc ?? "").Split('|');
        string r = At(f, 0), c = At(f, 1), g = At(f, 2);
        return FirstTex(
            $"player_{r}_{c}_{g}", $"player_{r}_{c}", $"player_{r}_{g}", $"player_{r}",
            $"player_{c}_{g}", $"player_{c}", $"player_{g}", "player");
    }

    // ── Map rendering ──────────────────────────────────────────────────────

    void DrawMap(RenderSnapshot snap)
    {
        // Effective square size = the zoom level (shadows the const for this
        // method so all the map math below scales with +/- zoom).
        int Cell = _cellSize;

        // Viewport is derived from the live window size: resize the window
        // and you see more (or less) of the battlefield.
        int screenW = Raylib.GetScreenWidth();
        int screenH = Raylib.GetScreenHeight() - _uiH;   // bottom strip belongs to the UI
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

        // Other party members: flat override → composited layers → blue tile
        foreach (var (pos, initials, sprite) in snap.OtherPlayers)
        {
            int sx = (pos.X - ox) * Cell;
            int sy = (pos.Y - oy) * Cell;
            if (sx < 0 || sy < 0 || sx >= mapPxW || sy >= mapPxH) continue;
            var flat = PlayerTexture(sprite);
            if (flat is Texture2D at)
            {
                DrawLayer(sx, sy, at, Color.White);
                Raylib.DrawRectangleLines(sx, sy, Cell, Cell, new Color(30, 110, 255, 255));
            }
            else if (DrawComposite(sx, sy, sprite.Split('|')))
            {
                Raylib.DrawRectangleLines(sx, sy, Cell, Cell, new Color(30, 110, 255, 255));
            }
            else
            {
                Raylib.DrawRectangle(sx + 1, sy + 1, Cell - 2, Cell - 2, new Color(30, 110, 255, 255));
                Raylib.DrawText(initials, sx + 6, sy + 10, 18, Color.Black);
            }
        }

        // Player (always at the viewport center): flat → composite → @ tile
        {
            int sx = (snap.PlayerPos.X - ox) * Cell;
            int sy = (snap.PlayerPos.Y - oy) * Cell;
            var flat = PlayerTexture(snap.PlayerSprite);
            if (flat is Texture2D pt)
                DrawLayer(sx, sy, pt, Color.White);
            else if (!DrawComposite(sx, sy, snap.PlayerSprite.Split('|')))
            {
                Raylib.DrawRectangle(sx + 1, sy + 1, Cell - 2, Cell - 2, new Color(0, 255, 255, 255));
                Raylib.DrawText("@", sx + 8, sy + 10, 20, Color.Black);
            }
        }

        // Zoom controls (top-left of the map): + zooms in, - zooms out
        if (UiButton(6, 6, 28, 28, "+", new Color(40, 42, 58, 220)))
            _cellSize = Math.Min(96, _cellSize + 8);
        if (UiButton(38, 6, 28, 28, "-", new Color(40, 42, 58, 220)))
            _cellSize = Math.Max(16, _cellSize - 8);
        Raylib.DrawText($"{_cellSize}px", 70, 13, 12, new Color(180, 180, 190, 255));
    }

    void DrawEntity(int sx, int sy, string type, int hp, int maxHp)
    {
        int Cell = _cellSize;   // scale sprites/labels with the map zoom
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

    int FS(int baseSize) => Math.Max(8, (int)(baseSize * _uiScale));

    bool UiButton(int x, int y, int w, int h, string label, Color bg)
    {
        var mouse = Raylib.GetMousePosition();
        bool hover = mouse.X >= x && mouse.X <= x + w && mouse.Y >= y && mouse.Y <= y + h;
        Raylib.DrawRectangle(x, y, w, h, hover ? new Color(90, 110, 160, 255) : bg);
        Raylib.DrawRectangleLines(x, y, w, h, new Color(120, 130, 170, 255));
        int fs = FS(12);
        int tw = Raylib.MeasureText(label, fs);
        Raylib.DrawText(label, x + Math.Max(3, (w - tw) / 2), y + (h - fs) / 2, fs, Color.White);
        return hover && Raylib.IsMouseButtonPressed(MouseButton.Left);
    }

    void DrawUi()
    {
        int screenW = Raylib.GetScreenWidth();
        int screenH = Raylib.GetScreenHeight();
        int uiW = Math.Max(200, screenW - Panel);
        bool waiting = _state.WaitingForInput;
        var mp = Raylib.GetMousePosition();

        // Any click or typed submission clears the typed buffer as it's sent
        void Send(string s) { _state.Inject(s); _typeBuf = ""; }

        // ── Physical keyboard works in the game window now ──
        // Drain typed characters into a line buffer; Backspace edits, Enter sends.
        int gc;
        while ((gc = Raylib.GetCharPressed()) > 0)
            if (waiting && gc >= 32 && gc < 127 && _typeBuf.Length < 48) _typeBuf += (char)gc;
        if (waiting && Raylib.IsKeyPressed(KeyboardKey.Backspace) && _typeBuf.Length > 0)
            _typeBuf = _typeBuf[..^1];
        if (waiting && Raylib.IsKeyPressed(KeyboardKey.Enter))
            Send(_typeBuf);

        // ── Drag the top edge to resize the panel (read all the text you need) ──
        int dividerY = screenH - _uiH;
        bool overDivider = mp.X < uiW && Math.Abs(mp.Y - dividerY) <= 4;
        if (overDivider && Raylib.IsMouseButtonPressed(MouseButton.Left)) _dragUi = true;
        if (!Raylib.IsMouseButtonDown(MouseButton.Left)) _dragUi = false;
        if (_dragUi) _uiH = screenH - (int)mp.Y;
        _uiH = Math.Clamp(_uiH, 110, Math.Max(140, screenH - 140));

        int uiY = screenH - _uiH;
        Raylib.DrawRectangle(0, uiY, uiW, _uiH, new Color(14, 14, 20, 255));
        // Resize grip on the top edge (highlights on hover/drag)
        var gripCol = (overDivider || _dragUi) ? new Color(120, 140, 190, 255) : new Color(60, 60, 80, 255);
        Raylib.DrawRectangle(0, uiY - 2, uiW, 3, gripCol);
        Raylib.DrawRectangle(uiW / 2 - 18, uiY - 1, 36, 1, new Color(200, 210, 230, 255));

        // Text-size controls (top-right of the strip)
        if (UiButton(uiW - 62, uiY + 4, 27, 22, "A-", new Color(40, 42, 58, 255)))
            _uiScale = Math.Max(0.7f, _uiScale - 0.1f);
        if (UiButton(uiW - 32, uiY + 4, 27, 22, "A+", new Color(40, 42, 58, 255)))
            _uiScale = Math.Min(1.9f, _uiScale + 0.1f);

        int body = FS(12), lineH = FS(12) + 4;
        int maxChars = Math.Max(16, (int)((uiW - 96) / (body * 0.62)));
        int btnH = FS(12) + 10, rowStep = btnH + 4;
        int stdY = screenH - (FS(12) + 12);
        int scrollColW = 18;
        int contentRight = uiW - 8 - scrollColW;
        var barBg = new Color(30, 32, 44, 255);
        var barThumb = new Color(90, 110, 160, 255);
        var arrowBg = new Color(40, 42, 58, 255);

        var (lines, prompt) = _state.SnapshotLog(200);

        // Free-text prompts (names, player count): the answer must be typed, so
        // suppress leftover choice buttons from the previous menu to avoid the
        // stale "New/Load character" buttons showing during name entry.
        string pp = prompt.TrimStart();
        bool textEntry = pp.StartsWith("First name") || pp.StartsWith("Middle name")
            || pp.StartsWith("Last name") || pp.StartsWith("How many players");

        // ── Layout: scene-text history (top ~42%), prompt, options (rest) ──
        int regionTop = uiY + 30;
        int available = (stdY - 6) - regionTop;
        int promptH = lineH + 4;
        int sceneH = Math.Max(lineH, (int)((available - promptH) * 0.42f));
        int sceneTop = regionTop, sceneBottom = sceneTop + sceneH;

        // Scene text is scrollable so you can read back through everything
        int visLines = Math.Max(1, sceneH / lineH);
        int maxLog = Math.Max(0, lines.Count - visLines);
        bool overScene = mp.X < uiW - scrollColW && mp.Y >= sceneTop && mp.Y < sceneBottom;
        if (overScene && maxLog > 0)
        {
            float wheel = Raylib.GetMouseWheelMove();
            if (wheel != 0) _logScroll += (int)wheel;   // wheel up = back in history
        }
        _logScroll = Math.Clamp(_logScroll, 0, maxLog);
        int startLine = Math.Max(0, lines.Count - visLines - _logScroll);
        Raylib.BeginScissorMode(0, sceneTop, uiW - scrollColW, sceneH);
        int ly = sceneTop;
        for (int i = startLine; i < lines.Count && ly + lineH <= sceneBottom + 2; i++)
        {
            Raylib.DrawText(Trunc(lines[i], maxChars), 8, ly, body, new Color(190, 190, 200, 255));
            ly += lineH;
        }
        Raylib.EndScissorMode();
        if (maxLog > 0)
        {
            int gx = uiW - scrollColW - 2;
            if (UiButton(gx, sceneTop, scrollColW, 18, "^", arrowBg)) _logScroll = Math.Min(maxLog, _logScroll + 1);
            if (UiButton(gx, sceneBottom - 18, scrollColW, 18, "v", arrowBg)) _logScroll = Math.Max(0, _logScroll - 1);
            int tTop = sceneTop + 20, tBot = sceneBottom - 20, tH = Math.Max(4, tBot - tTop);
            Raylib.DrawRectangle(gx + 5, tTop, scrollColW - 10, tH, barBg);
            int thumbH = Math.Max(10, (int)(tH * (float)visLines / lines.Count));
            int thumbY = tTop + (int)((tH - thumbH) * (1f - (float)_logScroll / maxLog));
            Raylib.DrawRectangle(gx + 5, thumbY, scrollColW - 10, thumbH, barThumb);
        }

        // Prompt line, between scene and options
        Raylib.DrawText(Trunc(prompt, maxChars) + (waiting ? " _" : ""), 8, sceneBottom + 2, FS(13),
            waiting ? Color.Yellow : Color.Gray);

        // Collect EVERY option token from the recent menu (scan only the recent
        // lines so we stay tied to the current menu, not scrolled-back history).
        // Skipped entirely on free-text prompts so you just type your answer.
        var opts = new List<(string Token, string Label)>();
        if (!textEntry)
        {
            var recent = lines.Count > 26 ? lines.GetRange(lines.Count - 26, 26) : lines;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var searchLines = new List<string>(recent) { prompt };
            for (int li = searchLines.Count - 1; li >= 0 && opts.Count < 60; li--)
                foreach (System.Text.RegularExpressions.Match m in OptRx.Matches(searchLines[li]))
                {
                    string token = m.Groups[1].Value;
                    string label = m.Groups[2].Value.Trim();
                    if (!seen.Add(token)) continue;
                    opts.Add((token, label.Length > 0 ? $"{token} {label}" : token));
                }
            opts.Reverse();
        }

        // Option buttons: a scrollable grid below the prompt
        int bandTop = sceneBottom + promptH;
        int bandBottom = stdY - 6;
        int bandH = Math.Max(btnH, bandBottom - bandTop);

        var placed = new List<(string Token, string Label, int Cx, int Cy, int W)>();
        int cx = 8, cy = 0;
        foreach (var (token, label) in opts)
        {
            int w = Math.Max(30, Raylib.MeasureText(label, FS(12)) + 12);
            if (cx + w > contentRight) { cx = 8; cy += rowStep; }
            placed.Add((token, label, cx, cy, w));
            cx += w + 6;
        }
        int contentH = placed.Count > 0 ? placed[^1].Cy + btnH : 0;
        int maxScroll = Math.Max(0, contentH - bandH);

        // Mouse-wheel scroll while hovering the options band
        bool overBand = mp.X >= 0 && mp.X < uiW && mp.Y >= bandTop && mp.Y <= bandBottom;
        if (overBand && maxScroll > 0)
        {
            float wheel = Raylib.GetMouseWheelMove();
            if (wheel != 0) _optScroll -= wheel * rowStep;
        }
        _optScroll = Math.Clamp(_optScroll, 0, maxScroll);
        int scroll = (int)_optScroll;

        // Draw the visible buttons, clipped to the band
        Raylib.BeginScissorMode(0, bandTop, contentRight + 2, bandH);
        foreach (var (token, label, pcx, pcy, w) in placed)
        {
            int drawY = bandTop + pcy - scroll;
            if (drawY + btnH < bandTop || drawY > bandBottom) continue;
            bool fully = drawY >= bandTop && drawY + btnH <= bandBottom;
            if (UiButton(pcx, drawY, w, btnH, label, new Color(50, 60, 95, 255)) && waiting && fully)
                Send(token.ToLowerInvariant());
        }
        Raylib.EndScissorMode();

        // Scroll controls + bar (only when the list overflows)
        if (maxScroll > 0)
        {
            int gx = uiW - scrollColW - 2;
            if (UiButton(gx, bandTop, scrollColW, btnH, "^", new Color(40, 42, 58, 255)))
                _optScroll = Math.Max(0, _optScroll - rowStep);
            if (UiButton(gx, bandBottom - btnH, scrollColW, btnH, "v", new Color(40, 42, 58, 255)))
                _optScroll = Math.Min(maxScroll, _optScroll + rowStep);
            int trackTop = bandTop + btnH + 2, trackBot = bandBottom - btnH - 2;
            int trackH = Math.Max(4, trackBot - trackTop);
            Raylib.DrawRectangle(gx + 5, trackTop, scrollColW - 10, trackH, new Color(30, 32, 44, 255));
            int thumbH = Math.Max(10, (int)(trackH * (float)bandH / contentH));
            int thumbY = trackTop + (int)((trackH - thumbH) * (_optScroll / maxScroll));
            Raylib.DrawRectangle(gx + 5, thumbY, scrollColW - 10, thumbH, new Color(90, 110, 160, 255));
        }

        // Standard row: quick numbers, yes/no, then the typed-text field
        int rowH = FS(12) + 10;
        int sx = 8;
        foreach (var t in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "y", "n" })
        {
            int w = FS(12) + (t.Length > 1 ? 16 : 10);
            if (UiButton(sx, stdY, w, rowH, t.ToUpper(), new Color(40, 42, 58, 255)) && waiting)
                Send(t);
            sx += w + 4;
        }
        // Backspace + typed-text field + Enter (Enter sends whatever you've typed)
        if (UiButton(sx, stdY, FS(12) + 20, rowH, "DEL", new Color(40, 42, 58, 255)) && waiting && _typeBuf.Length > 0)
            _typeBuf = _typeBuf[..^1];
        sx += FS(12) + 24;
        int fieldW = Math.Max(80, uiW - sx - 8 - (FS(12) + 40));
        Raylib.DrawRectangle(sx, stdY, fieldW, rowH, new Color(24, 26, 36, 255));
        Raylib.DrawRectangleLines(sx, stdY, fieldW, rowH, new Color(80, 90, 120, 255));
        string shown = _typeBuf.Length == 0 ? "type here…" : _typeBuf;
        Raylib.DrawText(shown + (waiting && _typeBuf.Length > 0 ? "_" : ""), sx + 6, stdY + 5, FS(12),
            _typeBuf.Length == 0 ? Color.Gray : Color.SkyBlue);
        sx += fieldW + 4;
        if (UiButton(sx, stdY, FS(12) + 36, rowH, "ENTER", new Color(50, 60, 95, 255)) && waiting)
            Send(_typeBuf);
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
