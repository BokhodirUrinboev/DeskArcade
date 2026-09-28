using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Asteroids over the LAN, together: two ships in one field of rocks, five lives and one score between them. The host
/// runs the game (<see cref="AsteroidsRules"/> with two ships) and streams the field twenty times a second as
/// "as|seq|playing|wave|score|lives|x|y|hostSafe|guestSafe|rocks|shots" (positions as fractions of the host's screen,
/// rocks as "id:x:y:size" and shots as "x:y", each list joined with ';'). The guest flies its own ship and streams it as
/// "ag|x|y"; its click on a rock is "af|x|y", a shot the host fires from the guest's ship; "aq" asks the host for a game.
/// Each screen draws both ships; a rock the guest sees vanish turns to dust there too.
/// </summary>
public sealed partial class AsteroidsGame
{
    const double SendEvery = 0.05, Smooth = 18;

    sealed class CoopState
    {
        public int Seq = -1, Wave, Score, Lives;
        public bool Playing, HostSafe, GuestSafe;
        public Vec2 HostShip;
        public readonly Dictionary<int, (Vec2 Pos, int Size)> Rocks = new();
        public readonly List<Vec2> Shots = new();
    }

    CoopState _state = new();
    readonly Dictionary<int, Vec2> _shownRocks = new(); // the guest's rocks, easing toward the host's positions
    Vec2 _matePos;
    int _session = -1;
    double _sendT, _askT, _endT, _clock;
    bool _asking;

    void CoopSetup() { }

    /// <summary>A new pairing (or the end of one) grounds both ships.</summary>
    void CoopCheckSession()
    {
        int session = LanOn ? Host.Lan.Session : -1;
        if (session == _session) return;
        _session = session;
        _playing = _asking = false;
        _state = new CoopState();
        _rules = null;
        _shownRocks.Clear();
        DrawRocks(Array.Empty<(int, Vec2, int, double)>());
        DrawShots(Array.Empty<Vec2>());
        _me.El.IsVisible = _mate.El.IsVisible = false;
    }

    void CoopAskGame()
    {
        _asking = true;
        _askT = 0;
        Host.Lan.Send("aq|");
        Host.Fx.Popup(new Vec2(_padX, Host.Arena.Bottom - 70), L.F("asked {0} for a game", Host.Lan.PeerName), Colors.White, 18, 1.3);
    }

    void CoopFire(Vec2 target)
    {
        var (u, v) = Out(target);
        Host.Lan.Send(string.Create(CultureInfo.InvariantCulture, $"af|{u:0.####}|{v:0.####}"));
        Host.Sound.Play("pin-flipper", 0.3, 1.6);
    }

    /// <summary>Messages in, the field or the ship out; true while a co-op game needs frames.</summary>
    bool CoopUpdate(double dt)
    {
        if (!LanOn) return false;
        _clock += dt;
        while (Host.Lan.TryReceive(out var msg)) Handle(msg);
        if (_asking && !_state.Playing && (_askT += dt) >= 0.6)
        {
            _askT = 0;
            Host.Lan.Send("aq|");
        }
        if (!_playing && _endT > 0) _endT -= dt;
        if ((_sendT += dt) >= SendEvery)
        {
            _sendT = 0;
            if (IsGuest && _state.Playing)
            {
                var (u, v) = Out(_myPos);
                Host.Lan.Send(string.Create(CultureInfo.InvariantCulture, $"ag|{u:0.####}|{v:0.####}"));
            }
            else if (!IsGuest && (_playing || _endT > 0)) SendField();
        }
        if (IsGuest) DrawGuest(dt);
        return _playing || _state.Playing || _asking || _endT > 0;
    }

    void Handle(string msg)
    {
        var f = msg.Split('|');
        switch (f[0])
        {
            case "as" when IsGuest && f.Length == 12:
                ReadField(f);
                break;
            case "ag" when !IsGuest && f.Length == 3:
                _matePos = In(P(f[1]), P(f[2]));
                break;
            case "af" when !IsGuest && f.Length == 3 && _playing:
                if (_rules!.Fire(1, In(P(f[1]), P(f[2])))) Host.Sound.Play("pin-flipper", 0.2, 1.4);
                break;
            case "aq" when !IsGuest && !_playing:
                StartGame();
                _endT = 0;
                break;
        }
    }

    // ------------------------------------------------------------------ the host

    int _seq;

    void SendField()
    {
        var r = _rules;
        if (r == null) return;
        var (hx, hy) = Out(_myPos);
        string rocks = string.Join(";", r.Rocks.Select(x =>
        {
            var (u, v) = Out(x.Pos);
            return string.Create(CultureInfo.InvariantCulture, $"{x.Id}:{u:0.###}:{v:0.###}:{x.Size}");
        }));
        string shots = string.Join(";", r.Shots.Select(s =>
        {
            var (u, v) = Out(s.Pos);
            return string.Create(CultureInfo.InvariantCulture, $"{u:0.###}:{v:0.###}");
        }));
        Host.Lan.Send(string.Create(CultureInfo.InvariantCulture,
            $"as|{++_seq}|{(_playing ? 1 : 0)}|{r.Wave}|{r.Score}|{r.Lives}|{hx:0.####}|{hy:0.####}|{(r.Ships[0].Invulnerable > 0 ? 1 : 0)}|{(r.Ships[1].Invulnerable > 0 ? 1 : 0)}|{rocks}|{shots}"));
    }

    // ------------------------------------------------------------------ the guest

    void ReadField(string[] f)
    {
        if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seq) || seq <= _state.Seq && seq > _state.Seq - 1000) return; // old or doubled
        var s = _state;
        bool was = s.Playing;
        int lives = s.Lives, wave = s.Wave, score = s.Score;
        bool guestSafe = s.GuestSafe;
        s.Seq = seq;
        s.Playing = f[2] == "1";
        s.Wave = I(f[3]);
        s.Score = I(f[4]);
        s.Lives = I(f[5]);
        s.HostShip = In(P(f[6]), P(f[7]));
        s.HostSafe = f[8] == "1";
        s.GuestSafe = f[9] == "1";
        var before = new Dictionary<int, (Vec2 Pos, int Size)>(s.Rocks);
        s.Rocks.Clear();
        foreach (var part in f[10].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var r = part.Split(':');
            if (r.Length == 4) s.Rocks[I(r[0])] = (In(P(r[1]), P(r[2])), Math.Clamp(I(r[3]), 1, 3));
        }
        s.Shots.Clear();
        foreach (var part in f[11].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Split(':');
            if (p.Length == 2) s.Shots.Add(In(P(p[0]), P(p[1])));
        }
        _matePos = s.HostShip;

        if (!was && s.Playing)
        {
            // the host started a game: take off from the pad
            _asking = false;
            _myPos = new Vec2(_padX, Host.Arena.Bottom - PadH);
            _shownRocks.Clear();
            Host.Stats.Add("asteroids.coop");
            Host.Sound.Play("whoosh", 0.4, 0.8);
            ShowPad();
        }
        if (was)
        {
            foreach (var (id, (pos, size)) in before)
                if (!s.Rocks.ContainsKey(id)) Dust(pos, size, 1);
            if (s.Lives < lives) ShipHit(mine: s.GuestSafe && !guestSafe); // the ship that just started blinking
            if (s.Wave > wave && s.Wave > 1)
            {
                Host.Fx.Popup(new Vec2(Host.Arena.Center.X, Host.Arena.Top + Host.Arena.Height * 0.3), L.F("WAVE {0}", s.Wave), Themes.Themed(Themes.ClassicGold), 44, 1.6);
                Host.Sound.Play("fire", 0.5);
            }
        }
        if (was && !s.Playing)
        {
            long best = Host.Stats.Get("asteroids.best");
            Host.Stats.Max("asteroids.best", s.Score);
            Host.Stats.Max("asteroids.wave", s.Wave);
            _over = true;
            ShowResult(s.Score, s.Wave, s.Score > best && s.Score > 0);
            ShowPad();
        }
        if (s.Score != score || s.Lives != lives || s.Wave != wave || was != s.Playing) Host.HudChanged();
    }

    /// <summary>The guest's view: the host's rocks eased into place, the shots, and both ships.</summary>
    void DrawGuest(double dt)
    {
        var s = _state;
        if (!s.Playing)
        {
            if (_shownRocks.Count > 0)
            {
                _shownRocks.Clear();
                DrawRocks(Array.Empty<(int, Vec2, int, double)>());
                DrawShots(Array.Empty<Vec2>());
            }
            _me.El.IsVisible = _mate.El.IsVisible = false;
            return;
        }
        double k = 1 - Math.Exp(-dt * Smooth);
        foreach (var id in _shownRocks.Keys.Where(id => !s.Rocks.ContainsKey(id)).ToList()) _shownRocks.Remove(id);
        foreach (var (id, (pos, _)) in s.Rocks)
            _shownRocks[id] = _shownRocks.TryGetValue(id, out var shown) && (shown - pos).Length < 200 ? shown + (pos - shown) * k : pos;
        DrawRocks(s.Rocks.Select(x => (x.Key, _shownRocks[x.Key], x.Value.Size, SpinOf(x.Key) * _clock)));
        DrawShots(s.Shots);
        DrawShip(_me, _myPos, s.GuestSafe ? 1 : 0, true);
        DrawShip(_mate, _matePos, s.HostSafe ? 1 : 0, true);
    }

    /// <summary>A rock's spin on the guest's screen, made up from its id (the host's spin never crosses the network).</summary>
    static double SpinOf(int id) => ((id * 2654435761L % 1000) / 1000.0 - 0.5) * 1.6;

    // ------------------------------------------------------------------ helpers

    /// <summary>A point as fractions of this screen's play area, for the other screen.</summary>
    (double U, double V) Out(Vec2 p)
    {
        var a = Host.Arena;
        return (Math.Clamp((p.X - a.Left) / Math.Max(1, a.Width), 0, 1), Math.Clamp((p.Y - a.Top) / Math.Max(1, a.Height), 0, 1));
    }

    Vec2 In(double u, double v)
    {
        var a = Host.Arena;
        return new Vec2(a.Left + Math.Clamp(u, 0, 1) * a.Width, a.Top + Math.Clamp(v, 0, 1) * a.Height);
    }

    static double P(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
    static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
}
