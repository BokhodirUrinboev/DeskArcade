using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Slingshot: pull the stone back and knock down a tower of wood and glass blocks standing on a window top
/// (or on a plinth on the floor). Clear a tower to get a bigger one; run out of stones with blocks still
/// standing and the game is over. Each tower is a race against the computer, or a co-worker over the LAN.
/// </summary>
public sealed class SlingshotGame : MiniGame
{
    const double Step = 1.0 / 240, Gravity = 1900, StoneR = 13, StoneMass = 2.4, GrabR = 40;
    const double MaxPull = 140, MinPull = 20, MinSpeed = 250, MaxSpeed = 2400;
    const double ForkH = 132, PouchH = 124, ForkHalf = 22;
    const double CrateS = 40, PostW = 16, PostH = 48, PlankW = 88, PlankH = 16, PlinthH = 46;
    const double StoneWake = 45, BlockWake = 120, GlassBreakStone = 520, GlassBreakBlock = 650, GlassBreakLand = 800;
    const double DemoPullTime = 0.45, SnapTime = 0.6, HopTime = 0.7;
    const int PointsPerBlock = 10, PointsPerStone = 50, MaxBlocks = 15, FairTower = 100;

    static readonly Color Gold = Color.FromRgb(255, 209, 102);
    static readonly Color[] Confetti = { Gold, Color.FromRgb(239, 71, 111), Color.FromRgb(6, 214, 160), Colors.White };
    static readonly Color[] Shards = { Color.FromRgb(200, 235, 255), Colors.White, Color.FromRgb(150, 205, 240) };

    sealed class Block
    {
        public required Sprite Sprite;
        public required Vec2[] Circles; // local centers of the round approximation used against other blocks
        public double W, H, R, Bound, Mass, Inertia, SurfaceY, DropY;
        public Vec2 Pos, Vel, RestPos;
        public double Angle, Spin, RestAngle, StillT, DynT, FadeT;
        public bool Glass, Dynamic, Resting, Down, Touching;
        public Anims.Tween? Drop; // while it is coming down into place at the start
        public bool Moving => Dynamic && !Resting;
    }

    readonly record struct Piece(double X, double Bottom, double W, double H, int Storey);

    readonly BallBody _stone = new(StoneR) { Gravity = Gravity, Restitution = 0.4, WallRestitution = 0.6, AirDrag = 0, RollFriction = 2.2 };
    readonly Sprite _stoneSprite = MakeStone();
    readonly Sprite _sling = MakeSling();
    readonly Sprite _pouch = MakePouch();
    readonly Line _bandBack = MakeBand(), _bandFront = MakeBand();
    readonly Canvas _blockLayer = new() { IsHitTestVisible = false };
    readonly Canvas _guideLayer = new() { IsHitTestVisible = false };
    readonly Rectangle _plinthBody = new()
    {
        Height = PlinthH - 8, Fill = Vertical(Color.FromRgb(150, 155, 163), Color.FromRgb(92, 97, 106)),
        Stroke = Art.Brush("#3A3E45"), StrokeThickness = 1, IsHitTestVisible = false,
    };
    readonly Rectangle _plinthTop = new()
    {
        Height = 10, RadiusX = 2, RadiusY = 2, Fill = Vertical(Color.FromRgb(196, 200, 207), Color.FromRgb(128, 133, 142)),
        Stroke = Art.Brush("#3A3E45"), StrokeThickness = 1, IsHitTestVisible = false,
    };
    readonly Ellipse[] _dots = new Ellipse[14];
    readonly List<Block> _blocks = new();
    readonly Dictionary<string, double> _lastSound = new();

    double _slingX, _moveOffset, _time, _acc, _sinceShot, _slowT, _stoneFade = -1, _clearIn = -1, _judgeIn = -1, _snapK = 1, _hop;
    double _demoHold = -1, _gameOverAt, _baseY, _baseX1, _baseX2, _towerX, _towerW;
    int _tower, _stones, _score, _towerBlocks, _seenGen = -1, _towerStart;
    long _bestAtStart;
    bool _placed, _loaded, _inFlight, _pulling, _moving, _gameOver, _collapsed, _onPlinth, _roundOn;
    IntPtr _baseHwnd;
    Vec2 _pull, _demoPull, _snapDir;

    public SlingshotGame(IGameHost host) : base(host)
    {
        for (int i = 0; i < _dots.Length; i++)
        {
            _dots[i] = new Ellipse
            {
                Width = 6, Height = 6, Fill = Brushes.White, IsVisible = false,
                RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = new TranslateTransform(),
            };
            _guideLayer.Children.Add(_dots[i]);
        }
        Layer.Children.Add(_plinthBody);
        Layer.Children.Add(_plinthTop);
        Layer.Children.Add(_blockLayer);
        Layer.Children.Add(_sling);
        Layer.Children.Add(_bandBack);
        Layer.Children.Add(_pouch);
        Layer.Children.Add(_stoneSprite);
        Layer.Children.Add(_bandFront);
        Layer.Children.Add(_guideLayer);
    }

    public override string Id => "slingshot";
    public override string Title => "Slingshot";

    // One tower is the race: its blocks and the spare stones score, and the computer or the co-worker takes on a tower alongside.
    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_score - _towerStart, _roundOn);
    public override int RaceBaseline => FairTower;
    public override int RaceBest => (int)Host.Stats.Get("slingshot.tower");
    public override double RaceSeconds => 22;

    public override void StartRace()
    {
        if (_gameOver) NewGame();
        BeginRound();
    }

    /// <summary>The tower's round starts with the first stone (or with the rival's start, in a race).</summary>
    void BeginRound()
    {
        if (_roundOn) return;
        _roundOn = true;
        _towerStart = _score;
        Host.RoundStarted();
    }

    /// <summary>The tower is cleared or the stones are gone: what this tower scored is the round's result.</summary>
    void EndRound()
    {
        if (!_roundOn) return;
        _roundOn = false;
        int points = _score - _towerStart;
        Host.Stats.Max("slingshot.tower", points);
        Host.RoundEnded(points);
    }

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        const string frame = "M0,10 L0,2 M0,3 C0,-3 -6,-4 -6,-10 M0,3 C0,-3 6,-4 6,-10";
        s.Rotor.Children.Add(Art.PathOf(frame, null, Art.Brush("#3E2410"), 4));
        s.Rotor.Children.Add(Art.PathOf(frame, null, Art.Brush("#C08A50"), 2.2));
        s.Rotor.Children.Add(Art.PathOf("M-6,-9 L0,-4 L6,-9", null, Art.Brush("#B0452F"), 1.3));
        s.Rotor.Children.Add(Art.Circle(0, -4, 3, Art.Brush("#9AA0A8"), Art.Brush("#3A3E45"), 0.8));
        return s;
    }

    int BlocksLeft => _blocks.Count(b => !b.Down);
    Vec2 PouchRest => new(_slingX, Host.Arena.Bottom - PouchH);

    public override HudInfo Hud => new(
        _score.ToString(),
        _gameOver ? L.T("Game over · pull the stone to play again") : L.F("Tower {0} · stones {1} · blocks {2}", _tower, _stones, BlocksLeft),
        L.F("Best {0}", Host.Stats.Get("slingshot.best")));

    // ------------------------------------------------------------------ game flow

    public override void Layout()
    {
        var a = Host.Arena;
        if (!_placed)
        {
            _placed = true;
            _slingX = a.Left + a.Width * 0.15;
            ClampSling();
            NewGame();
        }
        else
        {
            ClampSling();
            bool misplaced = _onPlinth
                ? Math.Abs(_baseY - (a.Bottom - PlinthH)) > 0.5 || _baseX1 < a.Left || _baseX2 > a.Right
                : _blocks.Any(b => !b.Down && !a.Contains(b.Pos.ToPoint()));
            if (misplaced) RebuildTower(); // the screen changed: same tower again, placed for the new size
        }
        if (_loaded) _stone.Place(PouchRest);
        Draw();
        Host.HudChanged();
    }

    public override void Deactivate()
    {
        _pulling = _moving = false;
        _demoHold = -1;
        _pull = default;
        Anims.Finish(); // blocks still coming down land at once
        HideGuide();
        Draw();
    }

    void NewGame()
    {
        _score = 0;
        _tower = 0;
        _gameOver = false;
        _bestAtStart = Host.Stats.Get("slingshot.best");
        NextTower();
    }

    void NextTower()
    {
        _tower++;
        BuildTower();
        _stones = _towerBlocks >= 12 ? 4 : 3;
        Reload();
        Host.HudChanged();
    }

    void RebuildTower()
    {
        BuildTower();
        if (!_gameOver)
        {
            _stones = _towerBlocks >= 12 ? 4 : 3;
            Reload();
        }
        Host.HudChanged();
    }

    void Reload()
    {
        _inFlight = false;
        _stoneFade = -1;
        _judgeIn = -1;
        _loaded = true;
        _stone.Place(PouchRest);
        _stoneSprite.Opacity = 1;
    }

    void BuildTower()
    {
        foreach (var b in _blocks)
        {
            b.Drop?.Cancel();
            _blockLayer.Children.Remove(b.Sprite);
        }
        _blocks.Clear();
        _collapsed = false;
        _clearIn = _judgeIn = -1;
        _towerBlocks = Math.Min(MaxBlocks, 6 + (int)((_tower - 1) * 1.5));
        var plan = PlanTower(_towerBlocks, out double width, out double height);
        PlaceBase(width, height);

        double glass = Math.Min(0.45, 0.22 + 0.03 * _tower);
        foreach (var piece in plan)
        {
            var b = NewBlock(piece.W, piece.H, Rng.NextDouble() < glass);
            b.Pos = b.RestPos = new Vec2(_towerX + piece.X, _baseY - piece.Bottom - piece.H / 2);
            b.SurfaceY = _baseY - piece.Bottom;
            DropIn(b, piece.Storey);
        }
    }

    /// <summary>A block comes down from above and lands with a bounce, the lower storeys first.</summary>
    void DropIn(Block b, int storey)
    {
        double from = SlingshotMaths.DropHeight(storey);
        b.DropY = -from;
        b.Sprite.IsVisible = false; // out of sight until its turn comes
        b.Sprite.Set(b.Pos + new Vec2(0, b.DropY), 0);
        b.Drop = Anims.Add(0.45, k =>
        {
            b.Sprite.IsVisible = true;
            b.DropY = -from * (1 - k);
        }, Ease.OutBounce, () =>
        {
            b.Drop = null;
            b.DropY = 0;
        }, storey * 0.09);
    }

    /// <summary>Storeys of posts with a plank on top, or crates side by side; wide at the bottom for big towers.</summary>
    static List<Piece> PlanTower(int count, out double width, out double height)
    {
        var pieces = new List<Piece>();
        double y = 0;
        int left = count, storey = 0;
        bool wide = count >= 11;
        width = wide ? 2 * PlankW : PlankW;
        while (left > 0)
        {
            if (wide && left >= 8)
            {
                if (Rng.NextDouble() < 0.55)
                {
                    foreach (double x in new[] { -80.0, 0, 80 }) pieces.Add(new Piece(x, y, PostW, PostH, storey));
                    foreach (double x in new[] { -44.0, 44 }) pieces.Add(new Piece(x, y + PostH, PlankW, PlankH, storey));
                    y += PostH + PlankH;
                    left -= 5;
                }
                else
                {
                    foreach (double x in new[] { -63.0, -21, 21, 63 }) pieces.Add(new Piece(x, y, CrateS, CrateS, storey));
                    y += CrateS;
                    left -= 4;
                }
            }
            else if (left >= 3 && Rng.NextDouble() < 0.6)
            {
                pieces.Add(new Piece(-36, y, PostW, PostH, storey));
                pieces.Add(new Piece(36, y, PostW, PostH, storey));
                pieces.Add(new Piece(0, y + PostH, PlankW, PlankH, storey));
                y += PostH + PlankH;
                left -= 3;
            }
            else if (left >= 2)
            {
                pieces.Add(new Piece(-21, y, CrateS, CrateS, storey));
                pieces.Add(new Piece(21, y, CrateS, CrateS, storey));
                y += CrateS;
                left -= 2;
            }
            else
            {
                pieces.Add(new Piece(0, y, CrateS, CrateS, storey));
                y += CrateS;
                left--;
            }
            storey++;
        }
        height = y;
        return pieces;
    }

    /// <summary>The widest window top far from the slingshot, or a plinth on the floor on the far side.</summary>
    void PlaceBase(double width, double height)
    {
        var a = Host.Arena;
        var hud = Host.HudBounds.Inflate(20);
        double minDist = Math.Max(320, a.Width * 0.3), bestW = 0;
        bool found = false;
        foreach (var p in Host.Platforms.Items)
        {
            double w = p.X2 - p.X1;
            if (w < width + 24 || w <= bestW || p.Y - height < a.Top + 50 || p.Y > a.Bottom - 80) continue;
            // stand near the far end so knocked blocks can fall off the edge
            double cx = (p.X1 + p.X2) / 2 > _slingX ? p.X2 - width / 2 - 12 : p.X1 + width / 2 + 12;
            if (Math.Abs(cx - _slingX) < minDist) continue;
            if (new Rect(cx - width / 2, p.Y - height - 10, width, height + 10).Intersects(hud)) continue;
            found = true;
            bestW = w;
            _baseHwnd = p.Hwnd;
            _baseY = p.Y;
            _baseX1 = p.X1;
            _baseX2 = p.X2;
            _towerX = cx;
        }

        _onPlinth = !found;
        _plinthBody.IsVisible = _plinthTop.IsVisible = _onPlinth;
        _towerW = width;
        if (found)
        {
            _seenGen = Host.Platforms.Generation;
            return;
        }

        _baseHwnd = IntPtr.Zero;
        double pw = width + 24;
        for (int tries = 0; tries < 12; tries++)
        {
            double off = a.Width * (0.1 + Rng.NextDouble() * (tries < 6 ? 0.08 : 0.3));
            bool farSide = tries < 8; // after that, try the slingshot's side before giving up
            double tx = (_slingX < a.Center.X) == farSide ? a.Right - off : a.Left + off;
            _towerX = Clamp(tx, a.Left + pw / 2 + 20, a.Right - pw / 2 - 20);
            if (!new Rect(_towerX - pw / 2, a.Bottom - PlinthH - height - 10, pw, height + PlinthH + 10).Intersects(hud)) break;
        }
        _baseY = a.Bottom - PlinthH;
        _baseX1 = _towerX - pw / 2;
        _baseX2 = _towerX + pw / 2;
        _plinthTop.Width = pw + 10;
        Canvas.SetLeft(_plinthTop, _baseX1 - 5);
        Canvas.SetTop(_plinthTop, _baseY);
        _plinthBody.Width = pw - 8;
        Canvas.SetLeft(_plinthBody, _baseX1 + 4);
        Canvas.SetTop(_plinthBody, _baseY + 8);
    }

    Block NewBlock(double w, double h, bool glass)
    {
        double r;
        Vec2[] circles;
        if (Math.Abs(w - h) < 1)
        {
            r = w / 4;
            circles = new[] { new Vec2(-r, -r), new Vec2(r, -r), new Vec2(-r, r), new Vec2(r, r) };
        }
        else
        {
            r = Math.Min(w, h) / 2;
            double span = Math.Max(w, h) - 2 * r;
            int k = (int)Math.Ceiling(span / r) + 1;
            circles = new Vec2[k];
            for (int i = 0; i < k; i++)
            {
                double t = -span / 2 + span * i / (k - 1);
                circles[i] = w > h ? new Vec2(t, 0) : new Vec2(0, t);
            }
        }
        double mass = w * h / 1600 * (glass ? 0.8 : 1);
        var b = new Block
        {
            Sprite = MakeBlock(w, h, glass), Circles = circles, W = w, H = h, R = r, Glass = glass, Mass = mass,
            Inertia = mass * (w * w + h * h) / 12, Bound = Math.Sqrt(w * w + h * h) / 2,
        };
        _blockLayer.Children.Add(b.Sprite);
        _blocks.Add(b);
        return b;
    }

    void AddScore(int pts)
    {
        _score += pts;
        Host.Stats.Max("slingshot.best", _score);
    }

    void KnockDown(Block b)
    {
        if (b.Down) return;
        b.Down = true;
        if (_collapsed) return; // the window took the tower away: nobody earned these
        AddScore(PointsPerBlock);
        Host.Stats.Add("slingshot.blocks");
        Host.ShareAction(b.Pos, PointsPerBlock);
        Host.Fx.Popup(b.Pos - new Vec2(0, 30), L.F("+{0}", PointsPerBlock), b.Glass ? Color.FromRgb(190, 230, 255) : Colors.White, 22, 0.8);
        Host.HudChanged();
        if (_clearIn < 0 && BlocksLeft == 0) _clearIn = 1.0;
    }

    void Shatter(Block b)
    {
        KnockDown(b);
        Wake(b);
        b.Resting = true;
        b.Vel = default;
        b.Spin = 0;
        b.FadeT = 1.3; // glass is gone at once instead of lying around
        b.Sprite.Opacity = 0.4;
        Host.Fx.Burst(b.Pos, Shards, 16, 380, 900, 5, 0.6);
        PlayThrottled("pop", 0.8, 1.6 + Rng.NextDouble() * 0.3);
        PlayThrottled("rim", 0.5, 2.2);
    }

    void TowerCleared()
    {
        int bonus = _stones * PointsPerStone;
        AddScore(bonus);
        Host.Stats.Add("slingshot.cleared");
        var at = new Vec2(_towerX, Math.Max(Host.Arena.Top + 80, _baseY - 160));
        if (bonus > 0) Host.ShareAction(at, bonus);
        Host.Fx.Popup(at, L.T("CLEAR!"), Gold, 40, 1.8, bonus > 0 ? L.F("+{0} · spare stones {1}", bonus, _stones) : L.T("next tower"));
        Host.Fx.Burst(at, Confetti, 36, 500, 700, 7, 1.0);
        Host.Fx.Burst(new Vec2(_slingX, Host.Arena.Bottom - ForkH), Confetti, 18, 360, 700, 6, 0.9);
        Anims.Add(HopTime, k => _hop = SlingshotMaths.Hop(k), Ease.Linear); // the slingshot jumps for joy
        Host.Sound.Play("fire", 0.8);
        EndRound();
        NextTower();
    }

    void GameOver()
    {
        _gameOver = true;
        _gameOverAt = _time;
        bool best = _score > _bestAtStart;
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("GAME OVER"), best ? Gold : Colors.White, 38, 2.4, L.F("{0} points · tower {1}", _score, _tower));
        if (best) Host.Fx.Burst(at, Confetti, 40, 520, 700, 7, 1.1);
        Host.Sound.Play(best ? "best" : "buzzer", best ? 0.8 : 0.4);
        EndRound();
        Reload(); // a stone in the pouch invites the next game
        Host.HudChanged();
    }

    void Collapse()
    {
        if (_collapsed) return;
        _collapsed = true;
        _clearIn = _judgeIn = -1;
        foreach (var b in _blocks)
        {
            if (b.Down) continue;
            b.Down = true;
            Wake(b);
            b.Vel = new Vec2((Rng.NextDouble() - 0.5) * 120, -Rng.NextDouble() * 80);
            b.Spin = (Rng.NextDouble() - 0.5) * 240;
        }
        Host.Fx.Popup(new Vec2(_towerX, _baseY - 60), L.T("Tower collapsed"), Color.FromRgb(255, 170, 120), 26, 1.4, L.T("building a new one"));
        Host.Sound.Play("thunk", 0.6, 0.8);
        Host.HudChanged();
    }

    /// <summary>Towers on a window top ride along with it and fall when the window moves away or closes.</summary>
    void FollowWindow()
    {
        var plats = Host.Platforms;
        if (_onPlinth || _collapsed || _baseHwnd == IntPtr.Zero || plats.Generation == _seenGen) return;
        _seenGen = plats.Generation;
        var d = plats.DeltaOf(_baseHwnd);
        if (d != default)
        {
            _baseY += d.Y;
            _baseX1 += d.X;
            _baseX2 += d.X;
            _towerX += d.X;
            foreach (var b in _blocks)
            {
                b.SurfaceY += d.Y;
                if (b.Down || b.Moving) continue;
                b.Pos += d;
                b.RestPos += d;
            }
        }
        double left = _towerX - _towerW / 2, right = _towerX + _towerW / 2;
        foreach (var p in plats.Items)
        {
            if (p.Hwnd != _baseHwnd || Math.Abs(p.Y - _baseY) > 3 || p.X1 > left + 4 || p.X2 < right - 4) continue;
            _baseY = p.Y;
            _baseX1 = p.X1;
            _baseX2 = p.X2;
            return;
        }
        Collapse();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into) => into.Add(HitShape.Circle(PouchRest, GrabR));

    public override bool PointerDown(Vec2 p, bool right)
    {
        if ((p - PouchRest).Length > GrabR || _demoHold >= 0) return false;
        if (right)
        {
            _moving = true;
            _moveOffset = _slingX - p.X;
            return true;
        }
        if (_gameOver) NewGame();
        if (!_loaded) return false;
        _pulling = true;
        _pull = default;
        return true;
    }

    public override void PointerUp(Vec2 p)
    {
        if (_moving)
        {
            _moving = false;
            return;
        }
        if (_pulling) Release();
    }

    void Release()
    {
        _pulling = false;
        _demoHold = -1;
        HideGuide();
        double len = _pull.Length;
        if (len < MinPull || !_loaded)
        {
            _pull = default;
            Draw();
            return;
        }
        var dir = -_pull / len;
        var from = PouchRest + _pull;
        _pull = default;
        _loaded = false;
        _inFlight = true;
        _sinceShot = _slowT = 0;
        BeginRound();
        _stones--;
        _snapDir = dir;
        Anims.Add(SnapTime, k => _snapK = k, Ease.Linear); // the bands snap forward and twang
        _stone.Place(from, dir * LaunchSpeed(len / MaxPull));
        _stone.Spin = dir.X * 400;
        Host.Sound.Play("twang", 0.5 + 0.4 * len / MaxPull);
        Host.Sound.Play("whoosh", 0.35 * len / MaxPull);
        Host.HudChanged();
    }

    static double LaunchSpeed(double power) => MinSpeed + (MaxSpeed - MinSpeed) * power;

    public override void Summon(Vec2 p)
    {
        if (_pulling || _moving) return;
        _slingX = p.X;
        ClampSling();
        if (_loaded) _stone.Place(PouchRest);
        Draw();
    }

    void ClampSling()
    {
        var a = Host.Arena;
        _slingX = Clamp(_slingX, a.Left + GrabR + 10, a.Right - GrabR - 10);
    }

    /// <summary>Limits the stretch and keeps the pouch inside the closed box.</summary>
    Vec2 LimitPull(Vec2 pull)
    {
        if (pull.Length > MaxPull) pull *= MaxPull / pull.Length;
        var a = Host.Arena;
        var rest = PouchRest;
        var p = rest + pull;
        p.X = Clamp(p.X, a.Left + StoneR, a.Right - StoneR);
        p.Y = Clamp(p.Y, a.Top + StoneR, a.Bottom - StoneR - 1);
        return p - rest;
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _time += dt;
        bool busy = _pulling || _moving;

        if (_moving)
        {
            _slingX = Host.Pointer.X + _moveOffset;
            ClampSling();
        }
        if (_pulling)
        {
            if (_demoHold >= 0)
            {
                _demoHold -= dt;
                _pull = LimitPull(_demoPull * Math.Min(1, (DemoPullTime - _demoHold) / (DemoPullTime * 0.7)));
                if (_demoHold <= 0) Release();
            }
            else
            {
                _pull = LimitPull(Host.Pointer - PouchRest);
            }
            if (_pulling) UpdateGuide();
        }

        FollowWindow();

        _acc += dt;
        while (_acc >= Step)
        {
            _acc -= Step;
            SimStep(Step);
        }

        if (_inFlight)
        {
            busy = true;
            _sinceShot += dt;
            _slowT = _stone.Vel.Length < 25 ? _slowT + dt : 0;
            if ((_stone.Asleep && _sinceShot > 0.5) || _slowT > 1 || _sinceShot > 5)
            {
                _inFlight = false;
                _stoneFade = 0;
            }
        }
        else if (_stoneFade >= 0)
        {
            busy = true;
            _stoneFade += dt;
            _stoneSprite.Opacity = Math.Max(0, 1 - _stoneFade / 0.3);
            if (_stoneFade >= 0.3)
            {
                _stoneFade = -1;
                if (_clearIn < 0 && !_collapsed && !_gameOver)
                {
                    if (_stones > 0) Reload();
                    else _judgeIn = 1.5; // let the blocks settle before deciding
                }
            }
        }

        for (int i = _blocks.Count - 1; i >= 0; i--)
        {
            var b = _blocks[i];
            if (b.Moving)
            {
                b.DynT += dt;
                busy = true;
            }
            if (!b.Down || !(b.Resting || b.DynT > 3)) continue;
            busy = true;
            b.FadeT += dt;
            if (b.FadeT > 1) b.Sprite.Opacity = Math.Min(b.Sprite.Opacity, Math.Max(0, 1 - (b.FadeT - 1) / 0.5));
            if (b.FadeT >= 1.5)
            {
                _blockLayer.Children.Remove(b.Sprite);
                _blocks.RemoveAt(i);
            }
        }

        if (_clearIn >= 0)
        {
            busy = true;
            if ((_clearIn -= dt) < 0)
            {
                _clearIn = -1;
                TowerCleared();
            }
        }
        if (_judgeIn >= 0)
        {
            busy = true;
            if (_blocks.Any(b => b.Moving && !b.Down)) _judgeIn = Math.Max(_judgeIn, 0.4);
            if ((_judgeIn -= dt) < 0)
            {
                _judgeIn = -1;
                if (_clearIn < 0 && !_collapsed && BlocksLeft > 0) GameOver();
            }
        }
        if (_collapsed && _blocks.Count == 0) RebuildTower();
        busy |= Anims.Update(dt);

        Draw();
        return busy;
    }

    void SimStep(double h)
    {
        if (_inFlight)
        {
            var imp = new Impacts();
            _stone.Step(h, Host, ref imp);
            if (_onPlinth)
            {
                var c = new Vec2(Clamp(_stone.Pos.X, _baseX1 - 5, _baseX2 + 5), Clamp(_stone.Pos.Y, _baseY, Host.Arena.Bottom));
                double hit = _stone.CollidePoint(c, 0, 0.35);
                if (hit > 150) PlayThrottled("thunk", Math.Min(0.6, hit / 2000), 0.7);
            }
            if (imp.Floor > 250) PlayThrottled("bounce", Math.Min(0.6, imp.Floor / 2200), 1.4);
            if (imp.Wall > 250) PlayThrottled("bounce", Math.Min(0.5, imp.Wall / 2500), 1.2);
        }

        foreach (var b in _blocks)
        {
            b.Touching = false;
            if (b.Moving && b.FadeT == 0) StepBlock(b, h);
        }
        if (_inFlight)
            foreach (var b in _blocks)
                ResolveStone(b);
        for (int i = 0; i < _blocks.Count; i++)
            for (int j = i + 1; j < _blocks.Count; j++)
                ResolveBlocks(_blocks[i], _blocks[j]);

        foreach (var b in _blocks)
        {
            if (b.FadeT > 0) continue;
            if (b.Moving)
            {
                if (b.Touching) Settle(b);
                if (!b.Down && (b.Pos.Y > b.SurfaceY + 2 || b.Pos.X < _baseX1 || b.Pos.X > _baseX2 || b.DynT > 6)) KnockDown(b);
                TryRest(b, h);
            }
            else if (!b.Down && !Supported(b))
            {
                Wake(b);
                b.Vel = new Vec2((Rng.NextDouble() - 0.5) * 40, 0);
            }
        }
    }

    static void Wake(Block b)
    {
        b.Dynamic = true;
        b.Resting = false;
        b.StillT = 0;
        b.DynT = 0;
    }

    static double HalfX(Block b)
    {
        double r = b.Angle * Math.PI / 180;
        return Math.Abs(b.W / 2 * Math.Cos(r)) + Math.Abs(b.H / 2 * Math.Sin(r));
    }

    static double HalfY(Block b)
    {
        double r = b.Angle * Math.PI / 180;
        return Math.Abs(b.W / 2 * Math.Sin(r)) + Math.Abs(b.H / 2 * Math.Cos(r));
    }

    void StepBlock(Block b, double h)
    {
        var a = Host.Arena;
        double prevBottom = b.Pos.Y + HalfY(b);
        b.Vel.Y += Gravity * h;
        b.Vel *= 1 - 0.1 * h;
        b.Pos += b.Vel * h;
        b.Spin = Clamp(b.Spin * (1 - 0.4 * h), -720, 720);
        b.Angle += b.Spin * h;

        double hx = HalfX(b), hy = HalfY(b);
        if (b.Pos.X - hx < a.Left) { b.Pos.X = a.Left + hx; b.Vel.X = Math.Abs(b.Vel.X) * 0.4; }
        else if (b.Pos.X + hx > a.Right) { b.Pos.X = a.Right - hx; b.Vel.X = -Math.Abs(b.Vel.X) * 0.4; }
        if (b.Pos.Y - hy < a.Top) { b.Pos.Y = a.Top + hy; b.Vel.Y = Math.Abs(b.Vel.Y) * 0.4; }
        if (b.Vel.Y < 0) return;

        double bottom = b.Pos.Y + hy, ground = double.NaN;
        if (_onPlinth && prevBottom <= _baseY + 2 && bottom >= _baseY && b.Pos.X >= _baseX1 && b.Pos.X <= _baseX2) ground = _baseY;
        else if (Host.Platforms.FindLanding(b.Pos.X, prevBottom, bottom, out var top)) ground = top.Y;
        else if (bottom >= a.Bottom) ground = a.Bottom;
        if (double.IsNaN(ground)) return;

        b.Pos.Y = ground - hy;
        if (b.Glass && b.Vel.Y > GlassBreakLand)
        {
            Shatter(b);
            return;
        }
        if (b.Vel.Y > 250) PlayThrottled(b.Glass ? "rim" : "thunk", Math.Min(0.5, b.Vel.Y / 2500), b.Glass ? 1.8 : 1.1 + Rng.NextDouble() * 0.2);
        b.Vel.Y = -b.Vel.Y * 0.2;
        if (Math.Abs(b.Vel.Y) < 60) b.Vel.Y = 0;
        b.Vel.X *= 1 - Math.Min(1, 5 * h);
        b.Touching = true;
    }

    /// <summary>Turns a resting block toward the face it will lie on, tipping over once past its balance point.</summary>
    static void Settle(Block b)
    {
        double baseAng = Math.Floor(b.Angle / 90) * 90, phi = b.Angle - baseAng;
        bool upright = Math.Abs((long)Math.Round(baseAng / 90)) % 2 == 0;
        double w0 = upright ? b.W / 2 : b.H / 2, h0 = upright ? b.H / 2 : b.W / 2;
        double tip = Math.Atan2(w0, h0) * 180 / Math.PI;
        double target = phi > tip ? baseAng + 90 : baseAng;
        b.Spin = (target - b.Angle) * 8;
    }

    static void TryRest(Block b, double h)
    {
        double target = Math.Round(b.Angle / 90) * 90;
        if (!b.Touching || b.Vel.Length > 15 || Math.Abs(target - b.Angle) > 2.5)
        {
            b.StillT = 0;
            return;
        }
        if ((b.StillT += h) < 0.3) return;
        b.Resting = true;
        b.Vel = default;
        b.Spin = 0;
        b.Angle = b.RestAngle = target;
        b.RestPos = b.Pos;
        b.DynT = 0;
    }

    static Rect BoxAt(Vec2 p, Block b, double angle)
    {
        bool turned = Math.Abs((long)Math.Round(angle / 90)) % 2 == 1;
        double w = turned ? b.H : b.W, h = turned ? b.W : b.H;
        return new Rect(p.X - w / 2, p.Y - h / 2, w, h);
    }

    /// <summary>A block stays put while its center sits over the base or over blocks that are still in place.</summary>
    bool Supported(Block b)
    {
        var box = BoxAt(b.Pos, b, b.Angle);
        if (Math.Abs(box.Bottom - _baseY) <= 4 && b.Pos.X >= _baseX1 && b.Pos.X <= _baseX2) return !_collapsed;
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var o in _blocks)
        {
            if (o == b || o.Down || o.FadeT > 0) continue;
            if (o.Moving && ((o.Pos - o.RestPos).Length > 5 || Math.Abs(o.Angle - o.RestAngle) > 8)) continue;
            var ob = BoxAt(o.RestPos, o, o.RestAngle);
            if (Math.Abs(ob.Top - box.Bottom) > 4) continue;
            double x1 = Math.Max(ob.Left, box.Left), x2 = Math.Min(ob.Right, box.Right);
            if (x2 - x1 < 3) continue;
            lo = Math.Min(lo, x1);
            hi = Math.Max(hi, x2);
        }
        return lo <= b.Pos.X + 1 && hi >= b.Pos.X - 1;
    }

    /// <summary>Circle against the block's rotated box. The normal points from the block toward the circle.</summary>
    static bool CircleHitsBlock(Vec2 c, double r, Block b, out Vec2 n, out double depth, out Vec2 at)
    {
        n = at = default;
        depth = 0;
        Vec2 d = c - b.Pos;
        if (d.LengthSquared > (b.Bound + r) * (b.Bound + r)) return false;
        double rad = b.Angle * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        double lx = d.X * cos + d.Y * sin, ly = -d.X * sin + d.Y * cos;
        double hw = b.W / 2, hh = b.H / 2;
        double qx = Math.Clamp(lx, -hw, hw), qy = Math.Clamp(ly, -hh, hh), nx, ny;
        if (qx == lx && qy == ly)
        {
            // center inside the box: push out through the nearest face
            double px = hw - Math.Abs(lx), py = hh - Math.Abs(ly);
            if (px < py) { nx = lx < 0 ? -1 : 1; ny = 0; depth = px + r; qx = nx * hw; }
            else { nx = 0; ny = ly < 0 ? -1 : 1; depth = py + r; qy = ny * hh; }
        }
        else
        {
            double ex = lx - qx, ey = ly - qy, dist = Math.Sqrt(ex * ex + ey * ey);
            if (dist >= r) return false;
            nx = ex / dist;
            ny = ey / dist;
            depth = r - dist;
        }
        n = new Vec2(nx * cos - ny * sin, nx * sin + ny * cos);
        at = b.Pos + new Vec2(qx * cos - qy * sin, qx * sin + qy * cos);
        return true;
    }

    static Vec2 PointVel(Block b, Vec2 at)
    {
        if (!b.Moving) return default;
        double w = b.Spin * Math.PI / 180;
        return b.Vel + new Vec2(-w * (at.Y - b.Pos.Y), w * (at.X - b.Pos.X));
    }

    void ResolveStone(Block b)
    {
        if (b.FadeT > 0 || !CircleHitsBlock(_stone.Pos, StoneR, b, out var n, out double depth, out var at)) return;
        double rel = -Vec2.Dot(_stone.Vel - PointVel(b, at), n);
        if (b.Glass && rel > GlassBreakStone)
        {
            Shatter(b);
            _stone.Vel *= 0.75; // the stone smashes straight through
            return;
        }
        if (!b.Moving)
        {
            if (rel < StoneWake)
            {
                _stone.Pos += n * depth; // a gentle touch just rests against the block
                if (rel > 0) _stone.Vel += n * (rel * 1.3);
                return;
            }
            Wake(b);
        }

        double total = StoneMass + b.Mass;
        _stone.Pos += n * (depth * b.Mass / total);
        b.Pos -= n * (depth * StoneMass / total);
        if (rel <= 0) return;
        var arm = at - b.Pos;
        double armN = arm.X * n.Y - arm.Y * n.X;
        double j = 1.3 * rel / (1 / StoneMass + 1 / b.Mass + armN * armN / b.Inertia);
        _stone.Vel += n * (j / StoneMass);
        b.Vel -= n * (j / b.Mass);
        b.Spin -= armN * j / b.Inertia * 180 / Math.PI;
        _stone.Wake();
        PlayThrottled(b.Glass ? "rim" : "thunk", Math.Min(0.9, rel / 1500), b.Glass ? 1.7 : 1 + Rng.NextDouble() * 0.2);
    }

    void ResolveBlocks(Block a, Block b)
    {
        if (a.FadeT > 0 || b.FadeT > 0) return;
        bool am = a.Moving, bm = b.Moving;
        if (!am && !bm) return;
        double reach = a.Bound + b.Bound;
        if ((a.Pos - b.Pos).LengthSquared > reach * reach) return;

        // deepest contact of either block's round approximation against the other's box; n points from b to a
        Vec2 n = default, at = default;
        double depth = 0;
        FindContact(a, b, 1, ref n, ref depth, ref at);
        FindContact(b, a, -1, ref n, ref depth, ref at);
        if (depth <= 0) return;

        double rel = -Vec2.Dot(PointVel(a, at) - PointVel(b, at), n);
        if (rel > GlassBreakBlock && (a.Glass || b.Glass))
        {
            if (a.Glass) Shatter(a);
            if (b.Glass) Shatter(b);
            return;
        }
        if (rel > BlockWake)
        {
            if (!am) { Wake(a); am = true; }
            if (!bm) { Wake(b); bm = true; }
        }

        double ima = am ? 1 / a.Mass : 0, imb = bm ? 1 / b.Mass : 0;
        double push = depth / (ima + imb);
        a.Pos += n * (push * ima * 0.8);
        b.Pos -= n * (push * imb * 0.8);
        if (n.Y < -0.5 && am) { a.Touching = true; a.Vel.X += (b.Vel.X - a.Vel.X) * Math.Min(1, 5 * Step); }
        if (n.Y > 0.5 && bm) { b.Touching = true; b.Vel.X += (a.Vel.X - b.Vel.X) * Math.Min(1, 5 * Step); }
        if (rel <= 0) return;

        var ra = at - a.Pos;
        var rb = at - b.Pos;
        double ca = ra.X * n.Y - ra.Y * n.X, cb = rb.X * n.Y - rb.Y * n.X;
        double iia = am ? 1 / a.Inertia : 0, iib = bm ? 1 / b.Inertia : 0;
        double jn = 1.2 * rel / (ima + imb + ca * ca * iia + cb * cb * iib);
        a.Vel += n * (jn * ima);
        b.Vel -= n * (jn * imb);
        a.Spin += ca * jn * iia * 180 / Math.PI;
        b.Spin -= cb * jn * iib * 180 / Math.PI;
        if (rel > 200) PlayThrottled(a.Glass || b.Glass ? "rim" : "thunk", Math.Min(0.5, rel / 2500), 1.3 + Rng.NextDouble() * 0.3);
    }

    static void FindContact(Block round, Block box, double sign, ref Vec2 n, ref double depth, ref Vec2 at)
    {
        double rad = round.Angle * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        foreach (var c in round.Circles)
        {
            var w = round.Pos + new Vec2(c.X * cos - c.Y * sin, c.X * sin + c.Y * cos);
            if (!CircleHitsBlock(w, round.R, box, out var cn, out double cd, out var cat) || cd <= depth) continue;
            depth = cd;
            n = cn * sign;
            at = cat;
        }
    }

    // ------------------------------------------------------------------ visuals

    void Draw()
    {
        double floor = Host.Arena.Bottom - _hop; // the whole slingshot hops when a tower is cleared
        _sling.Set(new Vec2(_slingX, floor));
        var rest = new Vec2(_slingX, floor - PouchH);
        var pouch = _pulling ? rest + _pull : rest;
        if (!_pulling) pouch += _snapDir * SlingshotMaths.BandWobble(_snapK); // the bands snap past the rest and twang
        if (_loaded) _stone.Pos = pouch;

        _stoneSprite.IsVisible = _loaded || _inFlight || _stoneFade >= 0;
        _stoneSprite.Set(_stone.Pos, _stone.Angle);
        _bandBack.StartPoint = new Point(_slingX - ForkHalf, floor - ForkH + 3);
        _bandFront.StartPoint = new Point(_slingX + ForkHalf, floor - ForkH + 3);
        _bandBack.EndPoint = _bandFront.EndPoint = pouch.ToPoint();
        double thick = 5 - 2.2 * Math.Min(1, (pouch - rest).Length / MaxPull);
        _bandBack.StrokeThickness = _bandFront.StrokeThickness = thick;
        var toFork = new Vec2(_slingX, floor - ForkH) - pouch;
        double angle = toFork.LengthSquared > 1 ? Math.Atan2(toFork.Y, toFork.X) * 180 / Math.PI + 90 : 0;
        _pouch.Set(pouch, angle);

        foreach (var b in _blocks)
        {
            if (b.Drop != null && b.Moving)
            {
                b.Drop.Cancel(); // hit on the way down: it lands where the physics says
                b.Drop = null;
                b.DropY = 0;
                b.Sprite.IsVisible = true;
            }
            b.Sprite.Set(b.Pos + new Vec2(0, b.DropY), b.Angle);
        }
    }

    void UpdateGuide()
    {
        double len = _pull.Length;
        if (len < MinPull)
        {
            HideGuide();
            return;
        }
        var a = Host.Arena;
        var start = PouchRest + _pull;
        var v = -_pull / len * LaunchSpeed(len / MaxPull);
        bool inside = true;
        for (int i = 0; i < _dots.Length; i++)
        {
            double t = (i + 1) * 0.045;
            var p = start + v * t + new Vec2(0, Gravity) * (0.5 * t * t);
            inside &= p.X > a.Left && p.X < a.Right && p.Y > a.Top && p.Y < a.Bottom;
            _dots[i].IsVisible = inside;
            if (!inside) continue;
            var tr = (TranslateTransform)_dots[i].RenderTransform!;
            tr.X = p.X - 3;
            tr.Y = p.Y - 3;
            _dots[i].Opacity = 0.8 * (1 - (double)i / _dots.Length);
        }
    }

    void HideGuide()
    {
        foreach (var d in _dots) d.IsVisible = false;
    }

    void PlayThrottled(string name, double vol, double pitch = 1)
    {
        if (_lastSound.TryGetValue(name, out double t) && _time - t < 0.05) return;
        _lastSound[name] = _time;
        Host.Sound.Play(name, vol, pitch);
    }

    static LinearGradientBrush Gradient(Color from, Color to, double endX, double endY)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(endX, endY, RelativeUnit.Relative),
        };
        brush.GradientStops.Add(new GradientStop(from, 0));
        brush.GradientStops.Add(new GradientStop(to, 1));
        return brush;
    }

    static LinearGradientBrush Vertical(Color top, Color bottom) => Gradient(top, bottom, 0, 1);

    static Sprite MakeBlock(double w, double h, bool glass)
    {
        string F(double v) => Art.F(v);
        var s = new Sprite { IsHitTestVisible = false };
        double l = -w / 2, t = -h / 2;
        if (glass)
        {
            s.Rotor.Children.Add(Art.At(new Rectangle
            {
                Width = w, Height = h, RadiusX = 2, RadiusY = 2,
                Fill = Gradient(Color.FromArgb(150, 210, 240, 255), Color.FromArgb(95, 110, 180, 230), 1, 1),
                Stroke = Art.Brush(235, 225, 245, 255), StrokeThickness = 1.5,
            }, l, t));
            double k = Math.Min(w, h) * 0.45;
            s.Rotor.Children.Add(Art.PathOf(
                $"M{F(l + 4)},{F(t + 4 + k)} L{F(l + 4 + k)},{F(t + 4)} M{F(l + 4)},{F(t + 9 + k * 0.6)} L{F(l + 9 + k * 0.6)},{F(t + 4)}",
                null, Art.Brush(190, 255, 255, 255), 1.5));
            return s;
        }

        var wood = Color.FromRgb(196, 138, 78);
        s.Rotor.Children.Add(Art.At(new Rectangle
        {
            Width = w, Height = h, RadiusX = 2, RadiusY = 2,
            Fill = Gradient(Art.Blend(wood, Colors.White, 0.2), Art.Blend(wood, Colors.Black, 0.28), 1, 1),
            Stroke = Art.Brush("#4E3116"), StrokeThickness = 1.2,
        }, l, t));
        var ink = Art.Brush(130, 78, 49, 22);
        if (Math.Abs(w - h) < 1)
        {
            s.Rotor.Children.Add(Art.At(new Rectangle { Width = w - 10, Height = h - 10, Stroke = ink, StrokeThickness = 1.4 }, l + 5, t + 5));
            s.Rotor.Children.Add(Art.PathOf($"M{F(l + 6)},{F(-t - 6)} L{F(-l - 6)},{F(t + 6)}", null, ink, 2.2));
        }
        else if (w > h)
        {
            s.Rotor.Children.Add(Art.PathOf($"M{F(l + 6)},-2 L{F(-l - 12)},-2 M{F(l + 16)},3 L{F(-l - 6)},3", null, ink, 1));
            s.Rotor.Children.Add(Art.Circle(l + 5, 0, 1.4, ink));
            s.Rotor.Children.Add(Art.Circle(-l - 5, 0, 1.4, ink));
        }
        else
        {
            s.Rotor.Children.Add(Art.PathOf($"M-2,{F(t + 6)} L-2,{F(-t - 12)} M3,{F(t + 14)} L3,{F(-t - 6)}", null, ink, 1));
        }
        return s;
    }

    static Sprite MakeSling()
    {
        string F(double v) => Art.F(v);
        var s = new Sprite { IsHitTestVisible = false };
        s.Children.Insert(0, Art.At(new Ellipse { Width = 46, Height = 9, Fill = Art.Brush(60, 0, 0, 0) }, -23, -5));
        string frame = $"M0,-5 L0,-62 M0,-58 C0,-86 {F(-ForkHalf)},-94 {F(-ForkHalf)},{F(-ForkH)} M0,-58 C0,-86 {F(ForkHalf)},-94 {F(ForkHalf)},{F(-ForkH)}";
        s.Rotor.Children.Add(Art.PathOf(frame, null, Art.Brush("#3E2410"), 12));
        s.Rotor.Children.Add(Art.PathOf(frame, null, Art.Brush("#A8703A"), 7));
        for (int i = 0; i < 3; i++)
            s.Rotor.Children.Add(Art.At(new Rectangle { Width = 13, Height = 4, RadiusX = 1, RadiusY = 1, Fill = Art.Brush("#2B2B30") }, -6.5, -40 + i * 8));
        s.Rotor.Children.Add(Art.Circle(-ForkHalf, -ForkH + 3, 4, Art.Brush("#6E2A1C")));
        s.Rotor.Children.Add(Art.Circle(ForkHalf, -ForkH + 3, 4, Art.Brush("#6E2A1C")));
        return s;
    }

    static Sprite MakePouch()
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Rotor.Children.Add(Art.At(new Rectangle
        {
            Width = 30, Height = 11, RadiusX = 5, RadiusY = 5, Fill = Art.Brush("#5A3A22"), Stroke = Art.Brush("#2E1C10"), StrokeThickness = 1,
        }, -15, -5.5));
        return s;
    }

    static Line MakeBand() => new()
    {
        Stroke = Art.Brush("#8A3424"), StrokeThickness = 5, StrokeLineCap = PenLineCap.Round, IsHitTestVisible = false,
    };

    static Sprite MakeStone()
    {
        var s = new Sprite { IsHitTestVisible = false };
        var body = new RadialGradientBrush { GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative) };
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0xD3, 0xD6, 0xDB), 0));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0x8D, 0x93, 0x9C), 0.65));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0x55, 0x5A, 0x62), 1));
        s.Rotor.Children.Add(Art.Circle(0, 0, StoneR, body, Art.Brush("#34383E"), 1.2));
        var speck = Art.Brush(120, 40, 42, 48);
        s.Rotor.Children.Add(Art.Circle(-4, 3, 1.6, speck));
        s.Rotor.Children.Add(Art.Circle(5, -2, 1.2, speck));
        s.Rotor.Children.Add(Art.Circle(2, 6, 1, speck));
        return s;
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        if (_pulling || _moving) return;
        if (_gameOver)
        {
            if (_time - _gameOverAt > 2.5) NewGame();
            return;
        }
        if (!_loaded || _inFlight || _clearIn >= 0 || _judgeIn >= 0 || _collapsed) return;
        if (_blocks.Any(b => (b.Moving && !b.Down) || b.Drop != null)) return;
        var target = _blocks.Where(b => !b.Down).OrderByDescending(b => b.Pos.Y).ThenBy(b => Math.Abs(b.Pos.X - _slingX)).FirstOrDefault();
        if (target == null) return;

        var aim = target.Pos + new Vec2((Rng.NextDouble() - 0.5) * 12, (Rng.NextDouble() - 0.5) * 8);
        if (!SolveShot(aim, out var pull))
            pull = LimitPull(new Vec2(aim.X > _slingX ? -1 : 1, 0.8).Normalized() * MaxPull * 0.75);
        _demoPull = pull;
        _pull = default;
        _pulling = true;
        _demoHold = DemoPullTime;
    }

    /// <summary>Ballistic pull toward a point: the flattest flight that the slingshot can reach without touching the ceiling.</summary>
    bool SolveShot(Vec2 aim, out Vec2 pull)
    {
        var a = Host.Arena;
        var rest = PouchRest;
        double jitter = 1 + (Rng.NextDouble() - 0.5) * 0.05;
        for (double T = 0.35; T <= 2.2; T += 0.05)
        {
            var from = rest;
            Vec2 v = default;
            for (int k = 0; k < 3; k++)
            {
                v = new Vec2((aim.X - from.X) / T, (aim.Y - from.Y - 0.5 * Gravity * T * T) / T);
                double power = Math.Clamp((v.Length - MinSpeed) / (MaxSpeed - MinSpeed), 0, 1);
                from = rest - v.Normalized() * (power * MaxPull);
            }
            double speed = v.Length;
            if (speed > MaxSpeed * 0.98 || speed < MinSpeed + 60) continue;
            double apex = v.Y < 0 ? from.Y - v.Y * v.Y / (2 * Gravity) : from.Y;
            if (apex < a.Top + StoneR + 10) continue;
            var candidate = (from - rest) * jitter;
            if ((LimitPull(candidate) - candidate).Length > 0.5) continue;
            pull = candidate;
            return true;
        }
        pull = default;
        return false;
    }
}

/// <summary>The bits of the slingshot's motion that are plain arithmetic, so they can be tested.</summary>
public static class SlingshotMaths
{
    /// <summary>
    /// How far the pouch is from its rest, along the shot, for progress <paramref name="k"/> (0 → 1) of the twang
    /// after a shot: it snaps forward first, then swings back and forth with less and less each time.
    /// </summary>
    public static double BandWobble(double k)
    {
        if (k <= 0 || k >= 1) return 0;
        return Math.Sin(k * 4.5 * Math.PI) * 9 * (1 - k) * (1 - k);
    }

    /// <summary>How high the slingshot is off the floor for progress <paramref name="k"/> of its celebration: two hops, the second smaller.</summary>
    public static double Hop(double k)
    {
        if (k <= 0 || k >= 1) return 0;
        return Math.Abs(Math.Sin(k * 2 * Math.PI)) * 16 * (1 - k);
    }

    /// <summary>How far above its place a block of the given storey starts when the tower assembles.</summary>
    public static double DropHeight(int storey) => 150 + Math.Max(0, storey) * 24;
}
