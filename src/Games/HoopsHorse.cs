using System;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade.Games;

/// <summary>
/// H-O-R-S-E over the LAN. Each player shoots on their own screen with their own ball, so lag never
/// touches a shot; only results cross the link. The setter shoots from anywhere: make it and the other
/// player must make the same shot (from the marked spot), or take a letter; miss it and the other player
/// sets. Spell H-O-R-S-E and you lose. Shot events are numbered and re-sent until acknowledged.
/// </summary>
public sealed partial class HoopsGame
{
    const double SpotRadius = 90, ShotTimeout = 4, HorseResend = 0.4;

    readonly Ellipse _spotRing = new()
    {
        Width = SpotRadius * 2, Height = SpotRadius * 2, Stroke = Art.Brush(200, 255, 209, 102), StrokeThickness = 2.5,
        StrokeDashArray = new AvaloniaList<double> { 6, 6 }, IsVisible = false, IsHitTestVisible = false,
    };
    readonly TextBlock _spotLabel = new() { FontFamily = Fx.Font, FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Art.Brush("#FFD166"), IsVisible = false, IsHitTestVisible = false };

    HorseMatch _horse = new(true);
    bool _wasHorse, _shotOpen, _shotCounts;
    Vec2 _spotN; // the shot to match: offset from the rim as fractions of the arena, x measured away from the board
    double _shotT, _hResendT;
    int _hGame = 1, _hSent, _hApplied;
    string? _hPending; // our last event, re-sent until acknowledged

    bool HorseOn => Host.Lan.Connected;
    bool Matching => _horse.Now == HorseMatch.Phase.Match;
    bool MyShot => _horse.MyShot;

    void HorseLayer()
    {
        Layer.Children.Add(_spotRing);
        Layer.Children.Add(_spotLabel);
    }

    HudInfo HorseHud => new(
        $"{Letters(_horse.MyLetters)} – {Letters(_horse.TheirLetters)}",
        _horse.Now switch
        {
            HorseMatch.Phase.Over => L.T("H-O-R-S-E over · grab the ball for a rematch"),
            HorseMatch.Phase.Match when !_horse.SetterIsMe => L.T("Match the shot from the marked spot, or take a letter"),
            HorseMatch.Phase.Match => L.F("{0} has to match your shot", Host.Lan.PeerName),
            _ when _horse.SetterIsMe => L.T("H-O-R-S-E · set a shot from anywhere"),
            _ => L.F("H-O-R-S-E · {0} is setting a shot", Host.Lan.PeerName),
        },
        L.F("vs {0}", Host.Lan.PeerName));

    static string Letters(int n) => n == 0 ? "—" : HorseMatch.Word[..n];

    /// <summary>Starts a fresh game when a LAN session starts or ends. The host sets first.</summary>
    void HorseCheckSession()
    {
        if (HorseOn == _wasHorse) return;
        _wasHorse = HorseOn;
        _hGame = 1;
        HorseNewGame();
    }

    void HorseNewGame()
    {
        _horse = new HorseMatch(iSetFirst: Host.Lan.Role != LanRole.Guest);
        _hSent = _hApplied = 0;
        _hPending = null;
        _shotOpen = false;
        HorseChanged();
    }

    void HorseChanged()
    {
        bool showSpot = HorseOn && Matching && !_horse.SetterIsMe;
        _spotRing.IsVisible = _spotLabel.IsVisible = showSpot;
        if (showSpot)
        {
            var at = SpotPosition();
            Canvas.SetLeft(_spotRing, at.X - SpotRadius);
            Canvas.SetTop(_spotRing, at.Y - SpotRadius);
            _spotLabel.Text = L.T("shoot from here");
            Canvas.SetLeft(_spotLabel, at.X - 50);
            Canvas.SetTop(_spotLabel, at.Y - SpotRadius - 22);
        }
        Host.HudChanged();
        Host.Wake();
    }

    Vec2 SpotPosition()
    {
        var a = Host.Arena;
        var c = RimCenter;
        return new Vec2(Clamp(c.X - _dir * _spotN.X * a.Width, a.Left + BallR, a.Right - BallR),
            Clamp(c.Y + _spotN.Y * a.Height, a.Top + BallR, a.Bottom - BallR));
    }

    /// <summary>False (with a hint) when it isn't this player's shot.</summary>
    bool HorseMayGrab()
    {
        if (!HorseOn) return true;
        if (_horse.Now == HorseMatch.Phase.Over)
        {
            _hGame++;
            HorseNewGame();
            Host.Lan.Send($"hn|{_hGame}");
            return true;
        }
        if (MyShot && !_shotOpen) return true;
        if (!MyShot) Host.Fx.Popup(_ball.Pos - new Vec2(0, 60), L.F("{0}'s shot", Host.Lan.PeerName), Colors.White, 20, 1.0);
        return false;
    }

    void HorseReleased(Vec2 from)
    {
        if (!HorseOn || !MyShot || _shotOpen) return;
        if (Matching && (from - SpotPosition()).Length > SpotRadius)
        {
            Host.Fx.Popup(from - new Vec2(0, 60), L.T("shoot from the marked spot"), Color.FromRgb(255, 209, 102), 20, 1.2);
            _shotCounts = false;
        }
        else _shotCounts = true;
        _shotOpen = true;
        _shotT = 0;
    }

    void HorseScored()
    {
        if (HorseOn && _shotOpen) CloseShot(true);
    }

    void HorseUpdate(double dt)
    {
        if (!HorseOn) return;
        if (_shotOpen && ((_shotT += dt) > ShotTimeout || _shotT > 0.5 && (_ball.Asleep || _ball.Grounded))) CloseShot(false);

        while (Host.Lan.TryReceive(out var msg))
        {
            var f = msg.Split('|');
            if (f.Length < 2 || !int.TryParse(f[1], out int game)) continue;
            if (f[0] == "hn" && game > _hGame)
            {
                _hGame = game;
                HorseNewGame();
                continue;
            }
            if (game != _hGame) continue;
            if (f[0] == "ha" && f.Length == 3 && int.TryParse(f[2], out int acked) && acked == _hSent) _hPending = null;
            else if (f[0] == "hr" && f.Length == 7 && int.TryParse(f[2], out int seq))
            {
                if (seq == _hApplied + 1)
                {
                    _hApplied = seq;
                    var spot = new Vec2(double.Parse(f[5], System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(f[6], System.Globalization.CultureInfo.InvariantCulture));
                    Apply(byMe: false, f[4] == "1", spot);
                }
                if (seq <= _hApplied) Host.Lan.Send($"ha|{_hGame}|{seq}");
            }
        }
        if (_hPending != null && (_hResendT += dt) >= HorseResend)
        {
            _hResendT = 0;
            Host.Lan.Send(_hPending);
        }
    }

    /// <summary>Our shot is decided: send it to the rival and apply it here.</summary>
    void CloseShot(bool made)
    {
        _shotOpen = false;
        if (!_shotCounts) return; // not from the spot: shoot again
        var a = Host.Arena;
        var spot = new Vec2((RimCenter.X - _releasePos.X) * _dir / a.Width, (_releasePos.Y - RimCenter.Y) / a.Height);
        _hSent++;
        string F(double v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        _hPending = $"hr|{_hGame}|{_hSent}|{(Matching ? "m" : "s")}|{(made ? 1 : 0)}|{F(spot.X)}|{F(spot.Y)}";
        Host.Lan.Send(_hPending);
        _hResendT = 0;
        Apply(byMe: true, made, spot);
    }

    /// <summary>Advances the match the same way on both screens and shows what happened.</summary>
    void Apply(bool byMe, bool made, Vec2 spot)
    {
        bool setting = _horse.Now == HorseMatch.Phase.Set;
        int letter = _horse.Apply(byMe, made);
        if (setting && made)
        {
            _spotN = spot;
            if (!byMe) Host.Fx.Popup(RimCenter + new Vec2(0, 90), L.F("{0} made it — your turn to match", Host.Lan.PeerName), Color.FromRgb(255, 209, 102), 22, 1.8);
        }
        if (letter != 0)
        {
            int letters = letter > 0 ? _horse.MyLetters : _horse.TheirLetters;
            Host.Fx.Popup(RimCenter + new Vec2(0, 90), HorseMatch.Word[letters - 1].ToString(), letter > 0 ? Color.FromRgb(255, 92, 108) : Color.FromRgb(255, 209, 102), 44, 1.6,
                letter > 0 ? L.T("you take a letter") : L.F("{0} takes a letter", Host.Lan.PeerName));
            if (_horse.Now == HorseMatch.Phase.Over) HorseGameOver(won: letter < 0);
        }
        HorseChanged();
    }

    void HorseGameOver(bool won)
    {
        var a = Host.Arena;
        var at = new Vec2(a.Center.X, a.Top + a.Height * 0.3);
        if (won)
        {
            Host.Stats.Add("hoops.horsewins");
            Host.Stats.Add("lan.wins");
            Host.Fx.Popup(at, L.T("YOU WIN!"), Color.FromRgb(255, 209, 102), 42, 2.6, L.F("vs {0}", Host.Lan.PeerName));
            Host.Sound.Play("best", 0.8);
        }
        else
        {
            Host.Fx.Popup(at, L.F("{0} WINS", Host.Lan.PeerName), Colors.White, 38, 2.4, L.T("grab the ball for a rematch"));
            Host.Sound.Play("buzzer", 0.45);
        }
    }
}

/// <summary>
/// The H-O-R-S-E turn rules, free of UI. The setter shoots from anywhere: a make means the other player
/// must match it (a miss costs them a letter) and the setter sets again; a miss passes the setting to the
/// other player. Spelling the whole word loses.
/// </summary>
public sealed class HorseMatch
{
    public const string Word = "HORSE";

    public enum Phase { Set, Match, Over }

    public HorseMatch(bool iSetFirst) => SetterIsMe = iSetFirst;

    public Phase Now { get; private set; }
    public bool SetterIsMe { get; private set; }
    public int MyLetters { get; private set; }
    public int TheirLetters { get; private set; }
    public bool MyShot => Now == Phase.Set ? SetterIsMe : Now == Phase.Match && !SetterIsMe;

    /// <summary>Records a shot; returns +1 if I took a letter, −1 if the other player did, else 0.</summary>
    public int Apply(bool byMe, bool made)
    {
        if (Now == Phase.Set)
        {
            if (made) Now = Phase.Match;
            else SetterIsMe = !byMe;
            return 0;
        }
        if (Now != Phase.Match) return 0;
        Now = Phase.Set;
        if (made) return 0;
        int letters = byMe ? ++MyLetters : ++TheirLetters;
        if (letters == Word.Length) Now = Phase.Over;
        return byMe ? 1 : -1;
    }
}
