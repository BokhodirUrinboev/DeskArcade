using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Cannon Castles (see <see cref="CannonRules"/>): two castles, yours on the left, stand on window tops (or at the
/// two ends of the taskbar) with the desk between them. Take turns: drag back from your cannon to set the angle and
/// the power (a short dotted hint shows the start of the arc), let go to fire. The windsock between the castles shows
/// the wind, which changes after every shot; windows in between stop a ball. A ball knocks out the block it hits and
/// everything above it; the first flag to go down loses. Against the computer at the tray's CPU level, or a
/// co-worker over the LAN (see CannonDuel.cs).
/// </summary>
public sealed partial class CannonGame : MiniGame
{
    const double GrabR = 46, MaxPull = 150, MinPull = 18, SockHeight = 78, GuideSeconds = 0.4, RecoilPx = 7;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color Stone = Color.FromRgb(150, 154, 162), Iron = Color.FromRgb(47, 52, 60), Wood = Color.FromRgb(122, 82, 48);
    static readonly Color[] Smoke = { Color.FromRgb(210, 210, 214), Color.FromRgb(170, 172, 178), Colors.White };
    static readonly Color[] Dust = { Color.FromRgb(196, 186, 164), Color.FromRgb(150, 140, 120) };

    readonly Canvas _castleLayer = new() { IsHitTestVisible = false };
    readonly Canvas _fxLayer = new() { IsHitTestVisible = false };
    readonly Canvas _guideLayer = new() { IsHitTestVisible = false };
    readonly Rectangle[][] _blocks = { new Rectangle[Castle.Blocks], new Rectangle[Castle.Blocks] };
    readonly Path[][] _cracks = { new Path[Castle.Blocks], new Path[Castle.Blocks] };
    readonly Sprite[] _flags = { new() { IsHitTestVisible = false }, new() { IsHitTestVisible = false } };
    readonly Sprite[] _cannons = { new() { IsHitTestVisible = false }, new() { IsHitTestVisible = false } };
    readonly Sprite _ball = new() { IsHitTestVisible = false, IsVisible = false };
    readonly Sprite _sock = new() { IsHitTestVisible = false };
    readonly Line _sockPole = new() { StrokeThickness = 3, Stroke = Art.Brush("#C9CDD4"), IsHitTestVisible = false };
    readonly TextBlock _windLabel = new() { FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Brushes.White, IsHitTestVisible = false };
    readonly TextBlock _aimLabel = new() { FontFamily = Fx.Font, FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Brushes.White, IsHitTestVisible = false, IsVisible = false };
    readonly Ellipse[] _dots = new Ellipse[10];
    readonly IBrush[] _stoneBrush = new IBrush[2], _keepBrush = new IBrush[2];
    // the computer's gunner on side 1; on side 0 only in --demo runs
    readonly CannonCpu?[] _gunners = new CannonCpu?[2];
    readonly Vec2[] _targets = new Vec2[2];
    readonly double[] _maxSpeed = { 900, 900 }, _barrel = { 40, 40 }, _recoil = new double[2], _flagAngle = new double[2];
    readonly IntPtr[] _hwnd = new IntPtr[2];

    CannonRules _rules = new(0);
    CannonField _field = new(default, Array.Empty<Rect>());
    Flight? _flight;
    int _shooter, _gameNo, _wins, _losses, _seenGen = -1;
    double _s = 22, _acc, _cpuIn = -1, _aimAngle, _aimPower, _sockAngle = 80, _spin;
    bool _aiming, _placed;
    Rect _placedFor;
    Vec2 _pull;

    public CannonGame(IGameHost host) : base(host)
    {
        for (int side = 0; side < 2; side++)
            for (int b = 0; b < Castle.Blocks; b++)
            {
                _castleLayer.Children.Add(_blocks[side][b] = new Rectangle { RadiusX = 1.5, RadiusY = 1.5, StrokeThickness = 1, IsHitTestVisible = false });
                _castleLayer.Children.Add(_cracks[side][b] = new Path
                {
                    Stroke = Art.Brush(200, 30, 32, 38), StrokeThickness = 1.6, StrokeJoin = PenLineJoin.Round, IsVisible = false, IsHitTestVisible = false,
                });
            }
        for (int i = 0; i < _dots.Length; i++)
        {
            _dots[i] = new Ellipse
            {
                Width = 6, Height = 6, Fill = Brushes.White, IsVisible = false,
                RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = new TranslateTransform(),
            };
            _guideLayer.Children.Add(_dots[i]);
        }
        Layer.Children.Add(_sockPole);
        Layer.Children.Add(_sock);
        Layer.Children.Add(_windLabel);
        Layer.Children.Add(_castleLayer);
        foreach (var f in _flags) Layer.Children.Add(f);
        foreach (var c in _cannons) Layer.Children.Add(c);
        Layer.Children.Add(_fxLayer);
        Layer.Children.Add(_ball);
        Layer.Children.Add(_guideLayer);
        Layer.Children.Add(_aimLabel);
        DuelSetup();
    }

    public override string Id => "cannons";
    public override string Title => "Cannon Castles";
    public override bool SupportsLan => true;
    public override bool HasCpuLevels => true;

    public override Opponent? Opponent => new(Rival, !DuelOn, DuelOn ? 0 : CpuLevel, _rules.Over ? null : _rules.Turn == 0);

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        var stone = Art.Brush(Stone);
        foreach (var (x, y, w, h) in new[] { (-9.0, -2.0, 5.0, 11.0), (-4.0, -6.0, 5.0, 15.0), (1.0, 3.0, 8.0, 6.0) })
            s.Rotor.Children.Add(Art.At(new Rectangle { Width = w, Height = h, Fill = stone, Stroke = Art.Brush("#4A4E56"), StrokeThickness = 0.8 }, x, y));
        s.Rotor.Children.Add(Art.PathOf("M-1.5,-6 L-1.5,-12", null, Art.Brush("#D9DCE2"), 1));
        s.Rotor.Children.Add(Art.PathOf("M-1.5,-12 L4,-10.5 L-1.5,-9 Z", Art.Brush(Themes.Current.Rival)));
        s.Rotor.Children.Add(Art.Circle(8, -4, 2.6, Art.Brush(Themes.Current.Puck), Art.Brush(Themes.Current.PuckRim), 0.8));
        return s;
    }

    string Rival => DuelOn ? Host.Lan.PeerName : L.T("CPU");
    string WindText => CannonRules.WindText(_rules.Wind);

    public override HudInfo Hud => new(
        $"{_wins}–{_losses}",
        _rules.Over
            ? DuelOn && !DuelHost ? L.F("Game over · click your cannon to ask {0} for a rematch", Rival) : L.T("Game over · click your cannon to play again")
            : _rules.Turn == 0 ? L.F("Your shot · wind {0}", WindText)
            : DuelOn ? L.F("{0} is aiming · wind {1}", Rival, WindText)
            : L.F("The CPU ({0}) is aiming · wind {1}", L.T(LevelNames[CpuLevel - 1]), WindText),
        L.F("Wins {0}", Host.Stats.Get("cannons.wins")));

    void Changed() => Host.HudChanged();

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        _s = Math.Clamp(Math.Round(a.Height * 0.026), 16, 26);
        Dress();
        DuelCheckSession();
        if (_gameNo == 0) NewGame();
        else if (!_placed || _placedFor != a) PlaceCastles();
        Draw();
        Changed();
    }

    public override void PositionsReset() => _placed = false;

    public override void Summon(Vec2 p)
    {
        if (_flight != null || _aiming) return;
        PlaceCastles();
        Draw();
    }

    /// <summary>
    /// Your castle on the leftmost window top in the left half with room above it, the rival's on the rightmost one
    /// in the right half; a side without one stands at its end of the taskbar. Castles too far apart for a ball to
    /// reach, or too close for a duel, both go to the taskbar, brought within range.
    /// </summary>
    void PlaceCastles()
    {
        var a = Host.Arena;
        _placed = true;
        _placedFor = a;
        _seenGen = Host.Platforms.Generation;
        double w = Castle.Cols * _s, h = (Castle.FullHeights[Castle.KeepCol] + Castle.FlagHeight + 0.6) * _s;
        var hud = Host.HudBounds.Inflate(8);
        var tops = Host.Platforms.Items.Where(p => p.X2 - p.X1 >= w + 16 && p.Y - h - a.Height * 0.25 >= a.Top).ToList();
        var left = tops.OrderBy(p => p.X1).Where(p => (p.X1 + p.X2) / 2 < a.Center.X)
            .Select(p => ((double X, double Y, IntPtr Hwnd)?)(p.X1 + 8, p.Y, p.Hwnd)).FirstOrDefault(s => !new Rect(s!.Value.X, s.Value.Y - h, w, h).Intersects(hud));
        var right = tops.OrderByDescending(p => p.X2).Where(p => (p.X1 + p.X2) / 2 >= a.Center.X)
            .Select(p => ((double X, double Y, IntPtr Hwnd)?)(p.X2 - 8 - w, p.Y, p.Hwnd)).FirstOrDefault(s => !new Rect(s!.Value.X, s.Value.Y - h, w, h).Intersects(hud));
        var floorLeft = (a.Left + a.Width * 0.06, a.Bottom, IntPtr.Zero);
        var floorRight = (a.Right - a.Width * 0.06 - w, a.Bottom, IntPtr.Zero);
        Put(0, left ?? floorLeft);
        Put(1, right ?? floorRight);
        if (!InRange() || Gap() < a.Width * 0.3)
        {
            Put(0, floorLeft);
            Put(1, floorRight);
            // an ultrawide taskbar: walk both castles in until a ball can make it across
            for (int i = 0; i < 200 && !InRange() && Gap() > a.Width * 0.3; i++)
            {
                _rules.Castles[0].Move(new Vec2(_s, 0));
                _rules.Castles[1].Move(new Vec2(-_s, 0));
            }
        }
        // a castle on the taskbar steps aside for the scoreboard
        for (int side = 0; side < 2; side++)
        {
            var c = _rules.Castles[side];
            for (int i = 0; i < 40 && _hwnd[side] == IntPtr.Zero && new Rect(c.Left, c.BaseY - h, w, h).Intersects(hud); i++)
                c.Move(new Vec2(side == 0 ? _s : -_s, 0));
        }
        BuildField();

        void Put(int side, (double X, double Y, IntPtr Hwnd) spot)
        {
            _rules.Castles[side].Place(spot.X, spot.Y, _s);
            _hwnd[side] = spot.Hwnd;
        }
    }

    double Gap() => _rules.Castles[1].Left - (_rules.Castles[0].Left + _rules.Castles[0].Width);

    bool InRange()
    {
        var a = Host.Arena;
        var p0 = _rules.Castles[0].FullPivot;
        var p1 = _rules.Castles[1].FullPivot;
        double v = Math.Min(CannonRules.MaxSpeedFor(p0.Y, a.Top), CannonRules.MaxSpeedFor(p1.Y, a.Top));
        return p1.X - p0.X <= 0.85 * v * v / CannonRules.Gravity;
    }

    /// <summary>The windows between the castles, from their top edges down to the floor; and how hard each cannon may fire.</summary>
    void BuildField()
    {
        var a = Host.Arena;
        var c0 = _rules.Castles[0];
        var c1 = _rules.Castles[1];
        double from = c0.Left + c0.Width + _s * 0.5, to = c1.Left - _s * 0.5;
        var windows = new List<Rect>();
        foreach (var p in Host.Platforms.Items)
        {
            double x1 = Math.Max(p.X1, from), x2 = Math.Min(p.X2, to);
            if (x2 - x1 > 4 && p.Y < a.Bottom - 2) windows.Add(new Rect(x1, p.Y, x2 - x1, a.Bottom - p.Y));
        }
        _field = new CannonField(a, windows);
        for (int side = 0; side < 2; side++) _maxSpeed[side] = CannonRules.MaxSpeedFor(_rules.Castles[side].FullPivot.Y, a.Top);
    }

    /// <summary>A castle on a window top rides along with it; when the window goes, the castle drops to the taskbar.</summary>
    void FollowWindows()
    {
        var plats = Host.Platforms;
        if (plats.Generation == _seenGen || !_placed) return;
        _seenGen = plats.Generation;
        var a = Host.Arena;
        for (int side = 0; side < 2; side++)
        {
            if (_hwnd[side] == IntPtr.Zero) continue;
            var c = _rules.Castles[side];
            c.Move(plats.DeltaOf(_hwnd[side]));
            bool stands = plats.Items.Any(p => p.Hwnd == _hwnd[side] && Math.Abs(p.Y - c.BaseY) < 3 && p.X1 <= c.Left + 2 && p.X2 >= c.Left + c.Width - 2);
            if (stands) continue;
            _hwnd[side] = IntPtr.Zero;
            c.Place(Clamp(c.Left, a.Left + 4, a.Right - c.Width - 4), a.Bottom, _s);
        }
        BuildField();
        Draw();
    }

    // ------------------------------------------------------------------ game flow

    /// <param name="gameNo">The duel's game number from the host; by default the next one.</param>
    void NewGame(int? gameNo = null)
    {
        _gameNo = gameNo ?? _gameNo + 1;
        // the first game opens with your shot; after that the first shot alternates (in a duel the host opens the odd games)
        int starter = DuelOn ? ((_gameNo % 2 == 1) == DuelHost ? 0 : 1) : (_gameNo % 2 == 1 ? 0 : 1);
        _rules = new CannonRules(starter);
        Anims.Finish(); // after the swap, so a computer shot still on its way can see its game is gone
        _gunners[0] = null;
        _gunners[1] = DuelOn ? null : new CannonCpu(CpuLevel, Rng);
        _flight = null;
        _ball.IsVisible = false;
        _aiming = false;
        _cpuIn = -1;
        _rematchAsked = false;
        _barrel[0] = _barrel[1] = 40;
        _flagAngle[0] = _flagAngle[1] = 0;
        foreach (var f in _flags) f.Opacity = 1;
        _fxLayer.Children.Clear();
        HideGuide();
        PlaceCastles();
        Draw();
        SwingSock();
        if (_rules.Turn == 0) TurnCue();
        else ScheduleCpu();
        Changed();
    }

    void ScheduleCpu()
    {
        if (!DuelOn && !_rules.Over && _rules.Turn == 1) _cpuIn = CannonCpu.ThinkSeconds[CpuLevel - 1] * (0.85 + Rng.NextDouble() * 0.3);
    }

    /// <summary>The computer has thought it over: its barrel turns to the angle it picked, and it fires.</summary>
    void CpuShoot()
    {
        if (DuelOn || _rules.Over || _rules.Turn != 1 || _flight != null) return;
        var cpu = _gunners[1] ??= new CannonCpu(CpuLevel, Rng);
        var castles = _rules.Castles;
        var (angle, power) = cpu.Aim(castles[1], castles[0], _rules.Wind, _maxSpeed[1], _field, castles);
        _targets[1] = CannonCpu.TargetOf(castles[0]);
        var rules = _rules;
        double from = _barrel[1];
        Anims.Add(0.5, k =>
        {
            _barrel[1] = from + (angle - from) * k;
            DrawCannon(1);
        }, Ease.InOutCubic, () =>
        {
            if (_rules == rules && !rules.Over && rules.Turn == 1 && _flight == null) Fire(1, angle, power);
        });
    }

    void Fire(int side, double angle, double power)
    {
        var c = _rules.Castles[side];
        _barrel[side] = angle;
        _flight = CannonRules.Launch(c, angle, power, _maxSpeed[side]);
        _shooter = side;
        _acc = 0;
        _ball.IsVisible = true;
        _ball.Set(_flight.Pos, 0);
        Host.Sound.Play("kick", 0.75, 0.55);
        Host.Sound.Play("whoosh", 0.25 + 0.3 * power, 0.8);
        Host.Fx.Burst(_flight.Pos, Smoke, 9, 110, -40, 8, 0.7);
        Anims.Add(0.35, k =>
        {
            _recoil[side] = RecoilPx * (1 - k);
            DrawCannon(side);
        }, Ease.OutCubic);
        if (side == 0) DuelFired();
        HideGuide();
        Changed();
    }

    /// <summary>The ball on this screen stopped: in a duel only our own shots land here; the rival's arrive as messages.</summary>
    void Landed(Impact impact)
    {
        _flight = null;
        _ball.IsVisible = false;
        int side = _shooter;
        var castles = _rules.Castles;
        var mine = castles[side];
        var theirs = castles[1 - side];
        if (_gunners[side] is { } gunner)
            gunner.Learn(CannonCpu.LongBy(mine, _targets[side], impact), CannonCpu.Blocked(mine, theirs, _targets[side], impact));
        if (side == 0 && !impact.HitCastle && impact.Kind != ImpactKind.Window)
        {
            // tell the player how far off it was, measured against the rival's keep
            double off = CannonCpu.LongBy(mine, CannonCpu.TargetOf(theirs), impact);
            if (Math.Abs(off) > theirs.Width)
                Host.Fx.Popup(impact.At - new Vec2(0, 30), off < 0 ? L.T("Too short") : L.T("Too long"), Colors.White, 18, 0.9);
        }
        double next = CannonRules.RandomWind(Rng);
        if (DuelOn) DuelShotLanded(impact, next);
        Apply(side, impact.Castle, impact.Block, next, impact.At, impact.Kind);
    }

    /// <summary>Records a shot (ours, the computer's, or the co-worker's from a message) and plays what it did.</summary>
    void Apply(int shooter, int castle, int block, double nextWind, Vec2? at, ImpactKind kind)
    {
        var rules = _rules;
        if (rules.Over || rules.Turn != shooter) return;
        var falling = new List<(Rect Box, bool Keep)>();
        Vec2 where = at ?? _ghostAt;
        bool[] flagWas = { rules.Castles[0].FlagDown, rules.Castles[1].FlagDown };
        bool struck = castle is 0 or 1 && rules.Castles[castle].Has(block);
        if (struck)
        {
            var c = rules.Castles[castle];
            where = at ?? Center(c.BlockRect(block));
            int col = block % Castle.Cols, crack = c.CrackIn(col);
            if (crack >= 0)
            {
                // the second hit on a cracked column: it gives way from the lower of the two, and all above goes with it
                for (int row = Math.Min(crack, block) / Castle.Cols; row < c.Height(col); row++)
                    falling.Add((c.BlockRect(Castle.IndexOf(col, row)), col == Castle.KeepCol));
            }
        }
        int fell = rules.Land(shooter, castle, block, nextWind);

        if (struck)
        {
            Tumble(castle, falling, shooter == 0 ? 1 : -1);
            Host.Fx.Burst(where, new[] { Stone, Color.FromRgb(110, 114, 122), Colors.White }, 10 + 4 * fell, 300, 700, 6, 0.8);
            Host.Sound.Play("thunk", 0.7, fell > 0 ? 0.6 : 0.8);
            if (fell > 0) Host.Sound.Play("bounce", 0.5, 0.8);
            if (castle != shooter)
                Host.Fx.Popup(where - new Vec2(0, 34), fell == 0 ? L.T("Cracked!") : fell == 1 ? L.T("Block down!") : L.F("{0} blocks down", fell),
                    shooter == 0 ? Gold : Colors.White, fell >= 2 ? 24 : 20, 1.0);
            if (shooter == 0 && castle == 1 && fell > 0) Host.Stats.Add("cannons.blocks", fell);
        }
        else if (kind == ImpactKind.Window)
        {
            Host.Fx.Burst(where, Dust, 10, 220, 600, 5, 0.6);
            Host.Sound.Play("board", 0.5, 0.8);
            if (shooter == 0) Host.Fx.Popup(where - new Vec2(0, 30), L.T("Hit a window"), Colors.White, 18, 0.9);
        }
        else if (kind != ImpactKind.Out && castle < 0)
        {
            Host.Fx.Burst(where, Dust, 10, 200, 600, 5, 0.6);
            Host.Sound.Play("bounce", 0.45, 0.7);
        }
        for (int i = 0; i < 2; i++)
            if (!flagWas[i] && rules.Castles[i].FlagDown) FlagFalls(i);

        Draw();
        SwingSock();
        if (rules.Over) GameOver();
        else if (rules.Turn == 0) TurnCue();
        else ScheduleCpu();
        Changed();
    }

    static Vec2 Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    void GameOver()
    {
        _cpuIn = -1;
        bool won = _rules.Winner == 0;
        Host.Stats.Add("cannons.games");
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        string sub = DuelOn ? L.T("click your cannon for a rematch") : L.T("click your cannon to play again");
        if (won)
        {
            _wins++;
            Host.Stats.Add("cannons.wins");
            bool flawless = _rules.Castles[0].Standing == Castle.WholeCount;
            if (flawless) Host.Stats.Add("cannons.flawless");
            if (DuelOn)
            {
                Host.Stats.Add("cannons.duelwins");
                Host.Stats.Add("lan.wins");
            }
            else if (CpuLevel >= 3) Host.Stats.Add("cannons.hardwins");
            Host.Fx.Popup(at, L.T("YOU WIN!"), Gold, 40, 3.0, flawless ? L.T("not a block lost!") + " · " + sub : sub);
            Host.Fx.Burst(at, Themes.Current.Confetti, 44, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            _losses++;
            Host.Fx.Popup(at, L.F("{0} WINS", Rival), Colors.White, 36, 3.0, L.T("your flag is down") + " · " + sub);
            Host.Sound.Play("buzzer", 0.45);
        }
    }

    /// <summary>The shot has come round to this player: the cannon swells once, with a soft sound.</summary>
    void TurnCue()
    {
        Host.Sound.Play("pop", 0.4, 1.4);
        Anims.Add(0.5, k => _cannons[0].Scale = 1 + 0.2 * k, Ease.Pulse, () => _cannons[0].Scale = 1);
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into) => into.Add(HitShape.Circle(_rules.Castles[0].CannonPivot, GrabR));

    public override bool PointerDown(Vec2 p, bool right)
    {
        var pivot = _rules.Castles[0].CannonPivot;
        if (right || (p - pivot).Length > GrabR) return false;
        if (_rules.Over)
        {
            Rematch();
            return false;
        }
        if (_flight != null) return false;
        if (_rules.Turn != 0)
        {
            Host.Fx.Popup(pivot - new Vec2(0, GrabR + 24), L.F("{0}'s turn", Rival), Colors.White, 20, 1.0);
            return false;
        }
        _aiming = true;
        _pull = default;
        return true;
    }

    public override void PointerUp(Vec2 p)
    {
        if (!_aiming) return;
        _aiming = false;
        HideGuide();
        if (_pull.Length >= MinPull && _rules.Turn == 0 && !_rules.Over && _flight == null) Fire(0, _aimAngle, _aimPower);
        else Draw();
    }

    public override void PointerCancel()
    {
        _aiming = false;
        HideGuide();
    }

    public override void Deactivate()
    {
        _aiming = false;
        HideGuide();
        Anims.Finish();
    }

    /// <summary>Dragging back from the cannon: the pull's direction turns the barrel, its length sets the power.</summary>
    void UpdateAim()
    {
        var c = _rules.Castles[0];
        var pivot = c.CannonPivot;
        var pull = Host.Pointer - pivot;
        if (pull.Length > MaxPull) pull *= MaxPull / pull.Length;
        _pull = pull;
        double len = pull.Length;
        if (len < MinPull)
        {
            HideGuide();
            return;
        }
        var dir = -pull / len;
        _aimAngle = Math.Clamp(Math.Atan2(-dir.Y, dir.X * c.Facing) * 180 / Math.PI, CannonRules.MinAngle, CannonRules.MaxAngle);
        _aimPower = Math.Clamp((len - MinPull) / (MaxPull - MinPull), 0, 1);
        _barrel[0] = _aimAngle;
        DrawCannon(0);

        // the first stretch of the arc only, without the wind: judging the rest is the game
        var shot = CannonRules.Launch(c, _aimAngle, _aimPower, _maxSpeed[0]);
        var a = Host.Arena;
        bool inside = true;
        for (int i = 0; i < _dots.Length; i++)
        {
            double t = (i + 1) * GuideSeconds / _dots.Length;
            var p = shot.Pos + shot.Vel * t + new Vec2(0, CannonRules.Gravity) * (0.5 * t * t);
            inside &= a.Contains(p.ToPoint());
            _dots[i].IsVisible = inside;
            if (!inside) continue;
            var tr = (TranslateTransform)_dots[i].RenderTransform!;
            tr.X = p.X - 3;
            tr.Y = p.Y - 3;
            _dots[i].Opacity = 0.85 * (1 - (double)i / _dots.Length);
        }
        _aimLabel.IsVisible = true;
        _aimLabel.Text = string.Create(CultureInfo.InvariantCulture, $"{_aimAngle:0}° · {_aimPower * 100:0}%");
        Canvas.SetLeft(_aimLabel, pivot.X - 30);
        Canvas.SetTop(_aimLabel, pivot.Y + GrabR * 0.55);
    }

    void HideGuide()
    {
        foreach (var d in _dots) d.IsVisible = false;
        _aimLabel.IsVisible = false;
    }

    void Rematch()
    {
        if (!DuelOn)
        {
            NewGame();
            return;
        }
        if (DuelHost)
        {
            NewGame();
            DuelNewGameSent();
            return;
        }
        DuelAskRematch();
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        FollowWindows();
        bool busy = _aiming;
        if (_aiming) UpdateAim();
        if (_flight is { } f)
        {
            busy = true;
            _acc += Math.Min(dt, 0.1);
            Impact? impact = null;
            while (_acc >= Flight.Step && impact == null)
            {
                _acc -= Flight.Step;
                impact = f.Advance(Flight.Step, _rules.Wind, _field, _rules.Castles);
            }
            _spin += f.Vel.X * dt * 2;
            _ball.Set(f.Pos, _spin);
            if (impact is { } hit) Landed(hit);
        }
        if (_cpuIn >= 0)
        {
            busy = true;
            if ((_cpuIn -= dt) < 0)
            {
                _cpuIn = -1;
                CpuShoot();
            }
        }
        busy |= DuelUpdate(dt);
        busy |= Anims.Update(dt);
        return busy;
    }

    // ------------------------------------------------------------------ demo

    /// <summary>Plays your side with a Hard gunner; starts a new game when one is over.</summary>
    public override void DemoTick()
    {
        if (_rules.Over)
        {
            if (!Anims.Busy) NewGame();
            return;
        }
        if (_rules.Turn != 0 || _flight != null || _aiming || Anims.Busy) return;
        var castles = _rules.Castles;
        var g = _gunners[0] ??= new CannonCpu(3, Rng);
        var (angle, power) = g.Aim(castles[0], castles[1], _rules.Wind, _maxSpeed[0], _field, castles);
        _targets[0] = CannonCpu.TargetOf(castles[1]);
        Fire(0, angle, power);
    }

    // ------------------------------------------------------------------ drawing

    void Draw()
    {
        for (int side = 0; side < 2; side++)
        {
            var c = _rules.Castles[side];
            for (int b = 0; b < Castle.Blocks; b++)
            {
                var r = _blocks[side][b];
                bool show = c.Has(b);
                if (r.IsVisible != show) r.IsVisible = show;
                if (!show) continue;
                var box = c.BlockRect(b);
                Canvas.SetLeft(r, box.X + 0.5);
                Canvas.SetTop(r, box.Y + 0.5);
                var crack = _cracks[side][b];
                bool cracked = c.Cracked(b);
                if (crack.IsVisible != cracked) crack.IsVisible = cracked;
                if (!cracked) continue;
                Canvas.SetLeft(crack, box.X);
                Canvas.SetTop(crack, box.Y);
            }
            for (int b = 0; b < Castle.Blocks; b++)
                if (!c.Has(b) && _cracks[side][b].IsVisible) _cracks[side][b].IsVisible = false;
            _flags[side].Set(c.FlagFoot, _flagAngle[side]);
            DrawCannon(side);
        }
        _cannons[0].Opacity = !_rules.Over && _rules.Turn != 0 ? 0.7 : 1; // dimmed while the rival shoots
        DrawSock();
    }

    void DrawCannon(int side)
    {
        var c = _rules.Castles[side];
        var dir = CannonRules.Direction(c.Facing, _barrel[side]);
        _cannons[side].Set(c.CannonPivot - dir * _recoil[side], c.Facing > 0 ? -_barrel[side] : 180 + _barrel[side]);
    }

    /// <summary>The windsock stands on the taskbar halfway between the cannons; it stretches out with the wind and droops in a calm.</summary>
    void DrawSock()
    {
        var a = Host.Arena;
        double x = (_rules.Castles[0].CannonPivot.X + _rules.Castles[1].CannonPivot.X) / 2, top = a.Bottom - SockHeight;
        _sockPole.StartPoint = new Point(x, a.Bottom);
        _sockPole.EndPoint = new Point(x, top - 4);
        _sock.Set(new Vec2(x, top), _sockAngle);
        _windLabel.Text = L.F("wind {0}", WindText);
        _windLabel.Measure(Size.Infinity);
        Canvas.SetLeft(_windLabel, x - _windLabel.DesiredSize.Width / 2);
        Canvas.SetTop(_windLabel, top - 28);
    }

    static double SockAngleFor(double wind)
    {
        double droop = 80 * (1 - Math.Min(1, Math.Abs(wind) / CannonRules.MaxWind));
        return wind >= 0 ? droop : 180 - droop;
    }

    void SwingSock()
    {
        double from = _sockAngle, to = SockAngleFor(_rules.Wind);
        Anims.Add(0.8, k =>
        {
            _sockAngle = from + (to - from) * k;
            DrawSock();
        }, Ease.OutBack);
        DrawSock();
    }

    /// <summary>The knocked-out block and everything above it tumble away from the shot and fade.</summary>
    void Tumble(int castle, List<(Rect Box, bool Keep)> falling, int push)
    {
        foreach (var (box, keep) in falling)
        {
            var rot = new RotateTransform();
            var r = new Rectangle
            {
                Width = box.Width - 1, Height = box.Height - 1, RadiusX = 1.5, RadiusY = 1.5, IsHitTestVisible = false,
                Fill = keep ? _keepBrush[castle] : _stoneBrush[castle], Stroke = Art.Brush(90, 0, 0, 0), StrokeThickness = 1,
                RenderTransformOrigin = RelativePoint.Center, RenderTransform = rot,
            };
            _fxLayer.Children.Add(r);
            double vx = push * (50 + Rng.NextDouble() * 110), spin = (Rng.NextDouble() - 0.5) * 420, drop = 180 + Rng.NextDouble() * 120;
            Anims.Add(0.8, k =>
            {
                Canvas.SetLeft(r, box.X + vx * k);
                Canvas.SetTop(r, box.Y + drop * k * k);
                rot.Angle = spin * k;
                r.Opacity = 1 - k;
            }, Ease.Linear, () => _fxLayer.Children.Remove(r));
        }
    }

    /// <summary>A flag that goes down topples backwards, away from the shot.</summary>
    void FlagFalls(int side)
    {
        var c = _rules.Castles[side];
        Host.Fx.Popup(c.FlagFoot - new Vec2(0, 60), L.T("FLAG DOWN!"), side == 1 ? Gold : Colors.White, 30, 1.6);
        Host.Sound.Play(side == 1 ? "fire" : "thunk", 0.6);
        Anims.Add(0.7, k =>
        {
            _flagAngle[side] = -c.Facing * 95 * k;
            _flags[side].Opacity = 1 - 0.5 * k;
            Draw();
        }, Ease.OutBounce);
    }

    // ------------------------------------------------------------------ art

    /// <summary>Dresses the castles in the theme: stone tinted with each side's colour (the keep more so), the side's colour on its flag and barrel band, the theme's puck colours on the cannonball.</summary>
    void Dress()
    {
        var t = Themes.Current;
        for (int side = 0; side < 2; side++)
        {
            var team = Art.Safe(side == 0 ? t.Mine : t.Rival);
            _stoneBrush[side] = Art.Brush(Art.Blend(Stone, team, 0.14));
            _keepBrush[side] = Art.Brush(Art.Blend(Stone, team, 0.42));
            var mortar = Art.Brush(110, 20, 22, 28);
            for (int b = 0; b < Castle.Blocks; b++)
            {
                var r = _blocks[side][b];
                r.Width = r.Height = _s - 1;
                r.Fill = b % Castle.Cols == Castle.KeepCol ? _keepBrush[side] : _stoneBrush[side];
                r.Stroke = mortar;
                // a jagged crack from the top edge down across the block, a little different on every block
                double k = (b * 7 % 5) / 5.0;
                _cracks[side][b].Data = Geometry.Parse(string.Create(CultureInfo.InvariantCulture,
                    $"M{_s * (0.3 + 0.3 * k):0.#},1 L{_s * 0.45:0.#},{_s * 0.35:0.#} L{_s * (0.3 + 0.2 * k):0.#},{_s * 0.55:0.#} L{_s * 0.62:0.#},{_s - 2:0.#} M{_s * 0.45:0.#},{_s * 0.35:0.#} L{_s * 0.8:0.#},{_s * 0.4:0.#}"));
            }
            MakeFlag(_flags[side], team, _s, _rules.Castles[side].Facing);
            MakeCannon(_cannons[side], team, _s, side == 0);
        }
        MakeBall(_ball, CannonRules.BallRadius(_s));
        MakeSock();
    }

    static void Clear(Sprite s)
    {
        s.Rotor.Children.Clear();
        for (int i = s.Children.Count - 1; i >= 0; i--)
            if (s.Children[i] != s.Rotor) s.Children.RemoveAt(i);
    }

    static void MakeFlag(Sprite s, Color team, double size, int facing)
    {
        Clear(s);
        double h = Castle.FlagHeight * size, w = Castle.ClothWidth * size * facing, cloth = Castle.ClothHeight * size;
        s.Rotor.Children.Add(Art.PathOf($"M0,0 L0,{Art.F(-h)}", null, Art.Brush("#D9DCE2"), 2));
        s.Rotor.Children.Add(Art.PathOf($"M0,{Art.F(-h)} L{Art.F(w)},{Art.F(-h + cloth / 2)} L0,{Art.F(-h + cloth)} Z", Art.Brush(team), Art.Brush(120, 0, 0, 0), 1));
        s.Rotor.Children.Add(Art.Circle(0, -h, 2.2, Art.Brush("#FFD166")));
    }

    static void MakeCannon(Sprite s, Color team, double size, bool mine)
    {
        Clear(s);
        if (mine)
        {
            var halo = Art.Circle(0, 0, GrabR, Art.Brush(12, 255, 255, 255), Art.Brush(60, 255, 255, 255), 1.5);
            halo.StrokeDashArray = new AvaloniaList<double> { 4, 6 };
            s.Children.Insert(0, halo);
        }
        double len = size * 1.25, thick = size * 0.42;
        s.Rotor.Children.Add(Art.At(new Rectangle
        {
            Width = len, Height = thick, RadiusX = thick * 0.35, RadiusY = thick * 0.35, Fill = Art.Brush(Iron), Stroke = Art.Brush("#15181D"), StrokeThickness = 1,
        }, -size * 0.3, -thick / 2));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = size * 0.16, Height = thick, Fill = Art.Brush(team) }, size * 0.1, -thick / 2));
        s.Rotor.Children.Add(Art.At(new Rectangle
        {
            Width = size * 0.14, Height = thick * 1.2, RadiusX = 1.5, RadiusY = 1.5, Fill = Art.Brush("#3B4048"), Stroke = Art.Brush("#15181D"), StrokeThickness = 1,
        }, len - size * 0.44, -thick * 0.6));
        double wheel = size * 0.36;
        s.Children.Add(Art.Circle(0, size * 0.14, wheel, Art.Brush(Wood), Art.Brush("#3A2412"), 1.5));
        s.Children.Add(Art.Circle(0, size * 0.14, wheel * 0.3, Art.Brush("#3A2412")));
    }

    static void MakeBall(Sprite s, double r)
    {
        Clear(s);
        var t = Themes.Current;
        s.Rotor.Children.Add(Art.Circle(0, 0, r, Art.Brush(t.Puck), Art.Brush(t.PuckRim), 1.2));
        s.Rotor.Children.Add(Art.Circle(-r * 0.35, -r * 0.35, r * 0.28, Art.Brush(110, 255, 255, 255)));
    }

    static Sprite NewBall(double r)
    {
        var s = new Sprite();
        MakeBall(s, r);
        return s;
    }

    /// <summary>A striped cone pointing along +x from the top of the pole; its angle shows the wind.</summary>
    void MakeSock()
    {
        Clear(_sock);
        var orange = Art.Brush(Art.Safe(Color.FromRgb(255, 122, 26)));
        var white = Art.Brush(Color.FromRgb(245, 245, 245));
        const double len = 48, h0 = 9, h1 = 4;
        for (int i = 0; i < 3; i++)
        {
            double x0 = len * i / 3, x1 = len * (i + 1) / 3;
            double y0 = h0 + (h1 - h0) * i / 3, y1 = h0 + (h1 - h0) * (i + 1) / 3;
            _sock.Rotor.Children.Add(Art.PathOf(
                $"M{Art.F(x0)},{Art.F(-y0)} L{Art.F(x1)},{Art.F(-y1)} L{Art.F(x1)},{Art.F(y1)} L{Art.F(x0)},{Art.F(y0)} Z",
                i % 2 == 0 ? orange : white, Art.Brush(90, 0, 0, 0), 0.8));
        }
        _sock.Children.Add(Art.Circle(0, 0, 3, Art.Brush("#C9CDD4")));
    }

    public override void ThemeChanged()
    {
        Dress();
        Draw();
    }
}
