using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;
using static DeskArcade.Games.LoadBalancerRules;

namespace DeskArcade.Games;

/// <summary>
/// Load Balancer (see <see cref="LoadBalancerRules"/>): requests drop in from the top of the screen and land in the load
/// balancer's queue above the taskbar. Drag each one onto a server before it times out: two servers stand on window tops
/// (and ride along when the window is dragged), and the budget racks more in the three slots on the taskbar. Servers heat
/// up with their load; a hot one slows down and an overheated one reboots, throwing its requests back into the queue.
/// A game, from the first wave until the lives run out, is a race against the computer rival or a co-worker.
/// </summary>
public sealed class LoadBalancerGame : MiniGame
{
    const double FallSeconds = 5.5, ReqW = 46, ReqH = 24, HeavyW = 60, SlotW = 52, ServerW = 78, ServerH = 58, RackGap = 96;
    static readonly string[] KindNames = { "GET", "POST", "PUT", "DEL" };
    static readonly Color[] KindColors =
    {
        Color.FromRgb(61, 186, 120), Color.FromRgb(77, 163, 255), Color.FromRgb(240, 170, 40), Color.FromRgb(232, 86, 96),
    };

    readonly Canvas _back = new() { IsHitTestVisible = false }, _front = new() { IsHitTestVisible = false };
    readonly Dictionary<Request, (Canvas El, Border Timer)> _reqEls = new();
    readonly Dictionary<Request, Vec2> _pos = new();
    readonly Dictionary<Request, double> _fallFrom = new(); // the height each request's current fall started at
    readonly Dictionary<Server, ServerSpot> _spots = new();
    readonly Canvas _balancer = new() { IsHitTestVisible = false };
    readonly TextBlock _startText = new() { FontFamily = Fx.Font, FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
    LoadBalancerRules? _rules;
    Request? _held;
    Vec2 _grab;
    bool _racing, _demo, _over;
    double _demoT;

    sealed class ServerSpot
    {
        public Vec2 At;           // the middle of its base, on a window top or the taskbar
        public IntPtr On;
        public required Canvas El;
        public required Border Heat;
        public required Canvas Leds;
        public required TextBlock Label;
    }

    public LoadBalancerGame(IGameHost host) : base(host)
    {
        Layer.Children.Add(_back);
        Layer.Children.Add(_balancer);
        Layer.Children.Add(_front);
    }

    public override string Id => "servers";
    public override string Title => "Load Balancer";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        for (int i = 0; i < 3; i++)
        {
            s.Rotor.Children.Add(Art.At(new Rectangle { Width = 18, Height = 5, RadiusX = 1.5, RadiusY = 1.5, Fill = Art.Brush("#3A4254"), Stroke = Art.Brush("#8A93A6"), StrokeThickness = 0.8 }, -9, -8 + i * 6));
            s.Rotor.Children.Add(Art.Circle(6, -5.5 + i * 6, 1.2, Art.Brush(i == 1 ? "#FFB020" : "#3DDC84")));
        }
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("servers.best");
            string line = _rules == null ? L.T("Click the load balancer to start · drag requests to the servers")
                : _rules.Over ? L.F("Down at wave {0} · click the load balancer for another game", _rules.Wave)
                : L.F("Wave {0} · lives {1} · budget {2} · drag requests to a server", Math.Max(1, _rules.Wave), _rules.LivesLeft, _rules.Budget);
            return new HudInfo((_rules?.Score ?? 0).ToString(CultureInfo.InvariantCulture), line, best > 0 ? L.F("Best {0}", best) : L.T("Best —"));
        }
    }

    public override string? ShareText => _rules is { Over: true } r
        ? L.F("Load Balancer · wave {0}, {1} requests served, {2} points 🖥️", r.Wave, r.Served, r.Score) : null;

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_rules?.Score ?? 0, _racing);
    public override int RaceBaseline => 400;
    public override int RaceBest => (int)Host.Stats.Get("servers.best");
    public override double RaceSeconds => 150;

    public override void StartRace()
    {
        if (_racing) return;
        NewGame();
    }

    // ------------------------------------------------------------------ layout

    Rect Arena => Host.Arena;

    /// <summary>The load balancer's box over the taskbar on the left, where requests queue.</summary>
    Rect BalancerBox
    {
        get
        {
            var a = Arena;
            double w = QueueMax * SlotW + 24;
            return new Rect(a.Left + 24, a.Bottom - ReqH - 34, w, ReqH + 30);
        }
    }

    /// <summary>The line falling requests land on, just above the queue.</summary>
    double LandY => BalancerBox.Top - ReqH;

    Vec2 SlotCenter(int i) => new(BalancerBox.Left + 12 + SlotW * i + SlotW / 2, BalancerBox.Top + 6 + ReqH / 2);

    Vec2 RackAt(int slot) => new(Arena.Right - 90 - slot * RackGap, Arena.Bottom);

    public override void Layout()
    {
        DrawBalancer();
        if (_rules != null) foreach (var (server, spot) in _spots) PlaceServer(server, spot);
        Host.HudChanged();
    }

    public override void Summon(Vec2 p) { }

    public override void Deactivate() => DropHeld(Host.Pointer);

    void NewGame()
    {
        ClearElements();
        _rules = new LoadBalancerRules(Rng, FallSeconds, servers: 0);
        _over = false;
        PlaceStartServers();
        _racing = true;
        Host.RoundStarted();
        Host.Sound.Play("score", 0.5, 0.9);
        Layout();
        Host.Wake();
    }

    /// <summary>Two servers on window tops wide and low enough, else on the taskbar in the middle of the screen.</summary>
    void PlaceStartServers()
    {
        var a = Arena;
        var box = BalancerBox.Inflate(40);
        var tops = Host.Platforms.Items
            .Where(p => p.X2 - p.X1 > ServerW + 20 && p.Y > a.Top + a.Height * 0.3 && p.Y < a.Bottom - 120 && !box.Contains(new Point((p.X1 + p.X2) / 2, p.Y)))
            .OrderBy(_ => Rng.Next()).Take(2).ToList();
        for (int i = 0; i < 2; i++)
        {
            var server = _rules!.AddServer();
            Vec2 at;
            IntPtr on = IntPtr.Zero;
            if (i < tops.Count)
            {
                var p = tops[i];
                at = new Vec2((p.X1 + p.X2) / 2 + (Rng.NextDouble() - 0.5) * Math.Max(0, p.X2 - p.X1 - ServerW - 20), p.Y);
                on = p.Hwnd;
            }
            else at = new Vec2(a.Center.X + (i == 0 ? -1 : 1) * 110, a.Bottom);
            AddSpot(server, at, on);
        }
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(BalancerBox)); // starts a game, and holds the queue
        if (_rules == null || _rules.Over) return;
        foreach (var r in _rules.Live.Where(r => r.Waiting))
            if (_pos.TryGetValue(r, out var p)) into.Add(HitShape.Box(ReqRect(r, p).Inflate(4)));
        for (int slot = 0; slot < RackSlots; slot++)
            if (_rules.Servers.All(s => s.RackSlot != slot)) into.Add(HitShape.Box(RackRect(slot)));
    }

    Rect RackRect(int slot)
    {
        var at = RackAt(slot);
        return new Rect(at.X - ServerW / 2, at.Y - ServerH, ServerW, ServerH);
    }

    static Rect ReqRect(Request r, Vec2 p)
    {
        double w = r.Heavy ? HeavyW : ReqW;
        return new Rect(p.X - w / 2, p.Y - ReqH / 2, w, ReqH);
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_rules == null || _rules.Over)
        {
            if (BalancerBox.Contains(p.ToPoint())) NewGame();
            return false;
        }
        for (int slot = 0; slot < RackSlots; slot++)
        {
            if (!RackRect(slot).Contains(p.ToPoint()) || _rules.Servers.Any(s => s.RackSlot == slot)) continue;
            if (_rules.Rack(slot) is not { } racked)
            {
                Host.Fx.Popup(RackAt(slot) - new Vec2(0, ServerH + 20), _rules.Budget <= 0 ? L.T("no budget · clear a wave for more")
                    : L.F("cooling down · {0} s", (int)Math.Ceiling(_rules.RackCooldownLeft)), Colors.White, 16, 1.0);
                return false;
            }
            AddSpot(racked, RackAt(slot), IntPtr.Zero);
            DrawBalancer();
            Host.Sound.Play("thunk", 0.5);
            Host.Fx.Popup(RackAt(slot) - new Vec2(0, ServerH + 20), L.T("Server racked"), Themes.Themed(Themes.ClassicGold), 18, 1.0);
            Host.Stats.Add("servers.racked");
            Host.HudChanged();
            return false;
        }
        var hit = _rules.Live.Where(r => r.Waiting && _pos.ContainsKey(r) && ReqRect(r, _pos[r]).Inflate(4).Contains(p.ToPoint()))
            .OrderBy(r => r.Left).FirstOrDefault();
        if (hit == null) return false;
        _held = hit;
        hit.Held = true;
        _grab = _pos[hit] - p;
        Host.Sound.Play("click", 0.35, 1.2);
        return true;
    }

    public override void PointerUp(Vec2 p) => DropHeld(p);

    public override void PointerCancel() => DropHeld(null);

    /// <summary>Lets go of the held request: onto the server under it, or back to falling (or into its queue slot).</summary>
    void DropHeld(Vec2? p)
    {
        if (_held is not { } r || _rules == null) return;
        _held = null;
        var target = p is Vec2 at ? ServerUnder(at) : null;
        if (target != null && _rules.Assign(r, target))
        {
            Host.Sound.Play("pop", 0.45, r.Heavy ? 0.8 : 1.1);
            Host.ShareAction(_spots[target].At - new Vec2(0, ServerH), 0);
            return;
        }
        r.Held = false;
        if (target != null) Host.Fx.Popup(_spots[target].At - new Vec2(0, ServerH + 18), target.Down ? L.T("rebooting") : L.T("full"), Colors.White, 16, 0.8);
        if (r.State == State.Falling && _pos.TryGetValue(r, out var pos))
        {
            double share = Math.Clamp((LandY - pos.Y) / Math.Max(1, LandY - Arena.Top - 26), 0.05, 1);
            _fallFrom[r] = Math.Min(pos.Y, LandY - 1);
            _rules.FallFrom(r, FallSeconds * share);
        }
    }

    Server? ServerUnder(Vec2 p) => _spots
        .Where(kv => new Rect(kv.Value.At.X - ServerW / 2 - 18, kv.Value.At.Y - ServerH - 30, ServerW + 36, ServerH + 40).Contains(p.ToPoint()))
        .Select(kv => kv.Key).FirstOrDefault();

    // ------------------------------------------------------------------ time

    public override bool Update(double dt)
    {
        bool anim = Anims.Update(dt);
        if (_rules == null || _rules.Over) return anim;
        RideWindows();
        foreach (var e in _rules.Step(dt)) OnEvent(e);
        if (_demo) DemoPlay(dt);
        Move(dt);
        foreach (var (server, spot) in _spots) DrawServerState(server, spot);
        return true;
    }

    void OnEvent(Event e)
    {
        switch (e.Kind)
        {
            case "spawn":
                AddRequest(e.Request!);
                break;
            case "land":
                Host.Sound.Play("board", 0.18, 1.6);
                break;
            case "timeout" or "overflow":
                Lost(e.Request!, e.Kind == "timeout" ? L.T("timed out") : L.T("queue full"));
                break;
            case "served":
                Served(e);
                break;
            case "hot":
                Host.Sound.Play("attention", 0.25, 1.4);
                break;
            case "down":
                Host.Sound.Play("buzzer", 0.3);
                Host.Fx.Popup(_spots[e.Server!].At - new Vec2(0, ServerH + 24), L.T("OVERHEATED · rebooting"), Color.FromRgb(255, 107, 107), 18, 1.4);
                break;
            case "up":
                Host.Sound.Play("click", 0.3, 0.9);
                break;
            case "wave":
                Host.Fx.Popup(new Vec2(Arena.Center.X, Arena.Top + Arena.Height * 0.25), L.F("WAVE {0}", _rules!.Wave), Themes.Themed(Themes.ClassicGold), 36, 1.6,
                    L.F("{0} requests · timeouts {1} s", WaveSize(_rules.Wave), (int)TimeoutFor(_rules.Wave)));
                Host.Stats.Max("servers.wave", _rules.Wave);
                Host.HudChanged();
                break;
            case "spike":
                Host.Fx.Popup(new Vec2(Arena.Center.X, Arena.Top + Arena.Height * 0.25), L.T("TRAFFIC SPIKE!"), Color.FromRgb(255, 107, 107), 32, 1.4, L.T("rack a server if you can"));
                Host.Sound.Play("attention", 0.5);
                break;
            case "cleared":
                Host.Sound.Play("fire", 0.5);
                Host.Fx.Popup(new Vec2(Arena.Center.X, Arena.Top + Arena.Height * 0.3), L.T("WAVE CLEARED"), Themes.Themed(Themes.ClassicGold), 34, 1.6,
                    L.F("+{0} · the budget grows", e.Points) + (e.Clean ? " · " + L.T("no request lost") : ""));
                if (e.Clean) Host.Stats.Add("servers.clean");
                DrawBalancer(); // the budget grew
                Host.HudChanged();
                break;
            case "over":
                GameOver();
                break;
        }
    }

    void Served(Event e)
    {
        var r = e.Request!;
        var at = _spots.TryGetValue(e.Server!, out var spot) ? spot.At - new Vec2(0, ServerH + 8) : Arena.Center;
        Host.Fx.Popup(at, e.Cache ? L.F("+{0} cache hit", e.Points) : $"+{e.Points}", e.Cache ? Themes.Themed(Themes.ClassicGold) : Colors.White, e.Cache ? 18 : 15, 0.8);
        Host.Sound.Play("score", 0.2, 1.4);
        Host.Stats.Add("servers.served");
        Host.ShareAction(at, e.Points);
        RemoveRequest(r);
        Host.HudChanged();
    }

    void Lost(Request r, string why)
    {
        var at = _pos.TryGetValue(r, out var p) ? p : Arena.Center;
        Host.Fx.Popup(at - new Vec2(0, 24), why, Color.FromRgb(255, 107, 107), 16, 1.0);
        Host.Fx.Burst(at, new[] { KindColors[r.Kind], Colors.White }, 12, 220, 600, 4, 0.6);
        Host.Sound.Play("buzzer", 0.2, 1.3);
        if (_held == r) _held = null;
        RemoveRequest(r);
        Host.HudChanged();
    }

    void GameOver()
    {
        if (_over || _rules == null) return;
        _over = true;
        _held = null;
        long before = Host.Stats.Get("servers.best");
        Host.Stats.Max("servers.best", _rules.Score);
        if (_racing)
        {
            _racing = false;
            Host.RoundEnded(_rules.Score);
        }
        bool best = _rules.Score > before && _rules.Score > 0;
        var at = new Vec2(Arena.Center.X, Arena.Top + Arena.Height * 0.3);
        Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("SERVICE DOWN"), best ? Themes.Themed(Themes.ClassicGold) : Colors.White, 40, 2.6,
            L.F("wave {0} · {1} served · {2} points", _rules.Wave, _rules.Served, _rules.Score));
        if (best) Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.1);
        Host.Sound.Play(best ? "best" : "buzzer", 0.6);
        foreach (var r in _reqEls.Keys.ToList()) RemoveRequest(r);
        DrawBalancer();
        _demoT = 3;
        Host.HudChanged();
    }

    /// <summary>Requests glide to where they belong: falling by the clock, queued into their slot, held under the pointer.</summary>
    void Move(double dt)
    {
        var rules = _rules!;
        var a = Arena;
        for (int i = 0; i < rules.Queue.Count; i++) Target(rules.Queue[i], SlotCenter(i), dt);
        foreach (var r in rules.Live)
        {
            if (!_reqEls.TryGetValue(r, out var el)) continue;
            if (r == _held)
            {
                var p = Host.Pointer + _grab;
                _pos[r] = new Vec2(Clamp(p.X, a.Left + 30, a.Right - 30), Clamp(p.Y, a.Top + 14, a.Bottom - 14));
            }
            else if (r.State == State.Falling)
            {
                double k = r.FallTotal <= 0 ? 1 : 1 - r.FallLeft / r.FallTotal;
                double from = _fallFrom.TryGetValue(r, out double y0) ? y0 : a.Top + 26;
                double x = _pos.TryGetValue(r, out var was) ? was.X : a.Left + r.Lane * a.Width;
                _pos[r] = new Vec2(x, from + (LandY - from) * k);
            }
            else if (r.State == State.Serving && r.Server is { } s && _spots.TryGetValue(s, out var spot))
            {
                int index = s.Jobs.IndexOf(r);
                Target(r, spot.At + new Vec2(-ServerW / 2 + 16 + index * 24, -ServerH - 16), dt);
                el.El.Opacity = 0.9;
            }
            var pos = _pos[r];
            Canvas.SetLeft(el.El, pos.X);
            Canvas.SetTop(el.El, pos.Y);
            double left = r.Waiting ? r.Left / r.Ttl : r.Work / r.WorkTotal;
            el.Timer.Width = Math.Max(0, (r.Heavy ? HeavyW : ReqW) - 8) * left;
            el.Timer.Background = Art.Brush(r.Waiting && r.Left < 3 ? Color.FromRgb(255, 92, 92) : Color.FromArgb(220, 255, 255, 255));
        }
    }

    void Target(Request r, Vec2 target, double dt)
    {
        if (r == _held) return;
        var from = _pos.TryGetValue(r, out var p) ? p : target;
        _pos[r] = from + (target - from) * Math.Min(1, dt * 12);
    }

    /// <summary>Servers on a window ride along when it is dragged.</summary>
    void RideWindows()
    {
        foreach (var (_, spot) in _spots)
        {
            if (spot.On == IntPtr.Zero) continue;
            var d = Host.Platforms.DeltaOf(spot.On);
            if (d.X == 0 && d.Y == 0) continue;
            spot.At += d;
            Canvas.SetLeft(spot.El, spot.At.X);
            Canvas.SetTop(spot.El, spot.At.Y);
        }
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        _demo = true;
        if ((_rules == null || _rules.Over) && (_demoT -= 0.15) <= 0) NewGame();
    }

    /// <summary>The demo hands the oldest waiting request to the coolest server with room, and racks a server in a spike.</summary>
    void DemoPlay(double dt)
    {
        if ((_demoT -= dt) > 0) return;
        _demoT = 0.45 + Rng.NextDouble() * 0.4;
        var rules = _rules!;
        if (rules.Queue.Count >= 4)
            for (int slot = 0; slot < RackSlots; slot++)
                if (rules.Rack(slot) is { } racked)
                {
                    AddSpot(racked, RackAt(slot), IntPtr.Zero);
                    DrawBalancer();
                    break;
                }
        var r = rules.Queue.FirstOrDefault() ?? rules.Live.Where(x => x.State == State.Falling).OrderBy(x => x.Left).FirstOrDefault();
        if (r == null) return;
        var s = rules.Servers.Where(x => rules.CanTake(x, r) && !x.Hot).OrderBy(x => x.Heat).FirstOrDefault()
            ?? rules.Servers.Where(x => rules.CanTake(x, r)).OrderBy(x => x.Heat).FirstOrDefault();
        if (s != null && rules.Assign(r, s)) Host.Sound.Play("pop", 0.3, 1.1);
    }

    // ------------------------------------------------------------------ drawing

    void ClearElements()
    {
        _back.Children.Clear();
        _front.Children.Clear();
        _reqEls.Clear();
        _pos.Clear();
        _fallFrom.Clear();
        _spots.Clear();
        _held = null;
    }

    void AddRequest(Request r)
    {
        double w = r.Heavy ? HeavyW : ReqW;
        var color = Art.Safe(KindColors[r.Kind]);
        var el = new Canvas { IsHitTestVisible = false };
        el.Children.Add(Art.At(new Rectangle { Width = w, Height = ReqH, RadiusX = 6, RadiusY = 6, Fill = Art.Brush(color), Stroke = Art.Brush(r.Heavy ? "#1D2129" : "#FFFFFF"), StrokeThickness = r.Heavy ? 2 : 1 }, -w / 2, -ReqH / 2));
        var label = new TextBlock
        {
            Text = KindNames[r.Kind] + (r.Heavy ? " ×2" : ""), FontFamily = Fx.Font, FontSize = 11, FontWeight = FontWeight.Black, Foreground = Brushes.White,
            Width = w, TextAlignment = TextAlignment.Center,
        };
        el.Children.Add(Art.At(label, -w / 2, -ReqH / 2 + 3));
        var timer = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Width = w - 8 };
        el.Children.Add(Art.At(timer, -w / 2 + 4, ReqH / 2 - 5));
        _reqEls[r] = (el, timer);
        _pos[r] = new Vec2(Arena.Left + r.Lane * Arena.Width, Arena.Top + 26);
        _front.Children.Add(el);
        Host.Sound.Play("whoosh", 0.12, 1.8);
    }

    void RemoveRequest(Request r)
    {
        if (_reqEls.Remove(r, out var el)) _front.Children.Remove(el.El);
        _pos.Remove(r);
        _fallFrom.Remove(r);
    }

    void AddSpot(Server server, Vec2 at, IntPtr on)
    {
        var el = new Canvas { IsHitTestVisible = false };
        el.Children.Add(Art.At(new Rectangle { Width = ServerW, Height = ServerH, RadiusX = 5, RadiusY = 5, Fill = Art.Brush("#2B3140"), Stroke = Art.Brush("#8A93A6"), StrokeThickness = 1.2 }, -ServerW / 2, -ServerH));
        for (int i = 0; i < 3; i++)
            el.Children.Add(Art.At(new Rectangle { Width = ServerW - 16, Height = 8, RadiusX = 2, RadiusY = 2, Fill = Art.Brush("#3A4254") }, -ServerW / 2 + 8, -ServerH + 8 + i * 12));
        var leds = new Canvas();
        el.Children.Add(leds);
        var heatTrack = Art.At(new Border { Width = ServerW - 16, Height = 5, CornerRadius = new CornerRadius(2.5), Background = Art.Brush(60, 255, 255, 255) }, -ServerW / 2 + 8, -12);
        el.Children.Add(heatTrack);
        var heat = new Border { Height = 5, CornerRadius = new CornerRadius(2.5), Width = 0 };
        el.Children.Add(Art.At(heat, -ServerW / 2 + 8, -12));
        var label = new TextBlock { FontFamily = Fx.Font, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Art.Brush("#C9D1DC") };
        el.Children.Add(Art.At(label, -ServerW / 2 + 2, -ServerH - 44));
        var spot = new ServerSpot { At = at, On = on, El = el, Heat = heat, Leds = leds, Label = label };
        _spots[server] = spot;
        _back.Children.Add(el);
        PlaceServer(server, spot);
        if (!Fx.ReducedMotion)
        {
            var drop = new TranslateTransform(0, -30);
            el.RenderTransform = drop;
            el.Opacity = 0;
            Anims.Add(0.35, k =>
            {
                drop.Y = -30 * (1 - k);
                el.Opacity = k;
            }, Ease.OutBack, () => el.RenderTransform = null);
        }
    }

    static void PlaceServer(Server server, ServerSpot spot)
    {
        Canvas.SetLeft(spot.El, spot.At.X);
        Canvas.SetTop(spot.El, spot.At.Y);
    }

    void DrawServerState(Server s, ServerSpot spot)
    {
        spot.Heat.Width = (ServerW - 16) * s.Heat;
        spot.Heat.Background = Art.Brush(Art.Safe(s.Heat < 0.45 ? Color.FromRgb(61, 220, 132) : s.Heat < HotAt ? Color.FromRgb(255, 176, 32) : Color.FromRgb(255, 92, 92)));
        string text = s.Down ? L.F("rebooting {0}", (int)Math.Ceiling(s.DownFor)) : s.Hot ? L.T("hot · slow") : "";
        if (spot.Label.Text != text) spot.Label.Text = text;
        spot.El.Opacity = s.Down ? 0.55 : 1;
        int used = s.Used;
        if (spot.Leds.Tag is int shown && shown == used * 10 + (s.Down ? 1 : 0)) return;
        spot.Leds.Tag = used * 10 + (s.Down ? 1 : 0);
        spot.Leds.Children.Clear();
        for (int i = 0; i < Slots; i++)
            spot.Leds.Children.Add(Art.Circle(ServerW / 2 - 14, -ServerH + 12 + i * 12, 2.6,
                Art.Brush(s.Down ? Color.FromRgb(90, 90, 90) : i < used ? Color.FromRgb(255, 176, 32) : Color.FromRgb(61, 220, 132))));
    }

    /// <summary>The load balancer's box with its queue slots, and the rack slots on the taskbar with the budget.</summary>
    void DrawBalancer()
    {
        _balancer.Children.Clear();
        var box = BalancerBox;
        var t = Themes.Current;
        _balancer.Children.Add(Art.At(new Rectangle { Width = box.Width, Height = box.Height, RadiusX = 8, RadiusY = 8, Fill = Art.Brush(Color.FromArgb(225, t.HudBack.R, t.HudBack.G, t.HudBack.B)), Stroke = Art.Brush(t.Accent), StrokeThickness = 1.5 }, box.Left, box.Top));
        for (int i = 0; i < QueueMax; i++)
        {
            var c = SlotCenter(i);
            _balancer.Children.Add(Art.At(new Rectangle { Width = SlotW - 6, Height = ReqH + 2, RadiusX = 5, RadiusY = 5, Stroke = Art.Brush(60, 255, 255, 255), StrokeThickness = 1 }, c.X - SlotW / 2 + 3, c.Y - ReqH / 2 - 1));
        }
        string caption = _rules == null || _rules.Over ? L.T("LOAD BALANCER · click to start") : L.T("LOAD BALANCER");
        _balancer.Children.Add(Art.At(new TextBlock { Text = caption, FontFamily = Fx.Font, FontSize = 10, FontWeight = FontWeight.Black, Foreground = Art.Brush(t.Gold) }, box.Left + 10, box.Bottom - 15));
        if (_rules == null || _rules.Over) return;
        for (int slot = 0; slot < RackSlots; slot++)
        {
            if (_rules.Servers.Any(s => s.RackSlot == slot)) continue;
            var r = RackRect(slot);
            var outline = Art.At(new Rectangle { Width = r.Width, Height = r.Height, RadiusX = 5, RadiusY = 5, Stroke = Art.Brush(120, 255, 255, 255), StrokeThickness = 1.5 }, r.Left, r.Top);
            outline.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 };
            _balancer.Children.Add(outline);
            _balancer.Children.Add(Art.At(new TextBlock { Text = _rules.Budget > 0 ? L.T("+ rack") : L.T("budget 0"), FontFamily = Fx.Font, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Art.Brush("#C9D1DC") }, r.Left + 16, r.Top + 20));
        }
    }

    public override void ThemeChanged() => DrawBalancer();
}
