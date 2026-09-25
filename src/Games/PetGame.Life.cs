using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// The pet's life over the weeks (the rules are in <see cref="PetLife"/>): it grows up with age and play, a wise pet
/// knows a second trick, it goes back to nap on its favourite app's window, and its calls follow their own volume and
/// soften late in the evening.
/// </summary>
public sealed partial class PetGame
{
    const double NapRetry = 90, NapReach = 28, AwakeAfterPet = 10;

    double _lifeScale = 1;          // the body's size for its stage (tweened when it grows up)
    int _lifeStage;                 // the stage shown: it only moves up once the growing-up has been celebrated
    bool _showNewTrick;             // the first trick after growing wise is the new one
    double _napTriedAt = -NapRetry;
    static HashSet<string>? _voiceClips;

    /// <summary>A pet's age in days, its play points and its stage (for the stats window too).</summary>
    public static (int AgeDays, long Play, int Stage) LifeOf(Settings settings, Stats stats, string kind, DateTime now)
    {
        int age = settings.PetAdopted.TryGetValue(kind, out var adopted) ? PetLife.AgeDays(adopted, now) : 0;
        long play = stats.Get(PetLife.PlayCounter(kind));
        return (age, play, PetLife.StageOf(age, play));
    }

    // ------------------------------------------------------------------ growing up

    /// <summary>Adopts the kind on first sight (dating it), then sizes it for its stage: at the start and when the kind changes.</summary>
    void RefreshLife()
    {
        var settings = Host.Settings;
        string kind = Kind;
        if (!settings.PetAdopted.ContainsKey(kind))
        {
            var adopted = DateTime.Now;
            if (settings.PetAdopted.Count == 0)
            {
                // the pet from before pets kept their own ages: its first pet achievement dates it, and the play it has
                // had so far counts toward growing up
                foreach (var a in Achievements.All)
                    if (a.GameId == "pet" && Host.Stats.UnlockedAt(a.Id) is DateTime at && at.ToLocalTime() < adopted) adopted = at.ToLocalTime();
                long play = PetLife.PlayFromTotals(Host.Stats.Get("pet.pets"), Host.Stats.Get("pet.treats"), Host.Stats.Get("pet.fetches"));
                if (Host.Stats.Get(PetLife.PlayCounter(kind)) == 0) Host.Stats.Add(PetLife.PlayCounter(kind), play);
            }
            settings.PetAdopted[kind] = adopted.Date;
            Host.SaveSettings();
        }
        CheckGrowth(snap: true);
    }

    /// <summary>Play points for this kind: a pet, a treat, a ball brought back.</summary>
    void AddPlay(int points)
    {
        Host.Stats.Add(PetLife.PlayCounter(Kind), points);
        CheckGrowth(snap: false);
    }

    /// <summary>
    /// Works out the stage. A new one is celebrated once (after the current frame, and only while the pet is out);
    /// until then the pet keeps its old size, so it grows in front of you.
    /// </summary>
    void CheckGrowth(bool snap)
    {
        var (_, _, stage) = LifeOf(Host.Settings, Host.Stats, Kind, DateTime.Now);
        int seen = (int)Math.Min(stage, Host.Stats.Get(PetLife.StageCounter(Kind)));
        _lifeStage = seen;
        if (snap) _lifeScale = PetLife.ScaleOf(seen);
        if (stage <= seen) return;
        string kind = Kind;
        Dispatcher.UIThread.Post(() => GrowUp(kind, stage));
    }

    void GrowUp(string kind, int stage)
    {
        if (!_active || kind != Kind || Host.Stats.Get(PetLife.StageCounter(kind)) >= stage) return; // not out: celebrated next time
        Host.Stats.Max(PetLife.StageCounter(kind), stage);
        Host.Stats.Max(PetLife.StageAchievementCounter, stage);
        _lifeStage = stage;
        _showNewTrick = stage >= PetLife.Wise;
        double from = _lifeScale, to = PetLife.ScaleOf(stage);
        if (Fx.ReducedMotion) _lifeScale = to;
        else Anims.Add(0.9, k => _lifeScale = from + (to - from) * k, Ease.OutBack);

        string name = KindName(kind);
        Host.Fx.Popup(Center - new Vec2(0, 60), stage >= PetLife.Wise ? L.F("{0} is old and wise now!", name) : L.F("{0} is all grown up!", name),
            Color.FromRgb(255, 209, 102), 18, 2.6, stage >= PetLife.Wise ? L.T("A new trick · right-click to see it") : null);
        Hearts(6);
        ShowBubble(Bubble.Heart, 2);
        Host.Sound.Play("best", 0.45);
        Speak(Say.Happy, 0.5);
        Host.Wake();
    }

    /// <summary>Where the body is drawn: scaled for its stage about the feet, so it still stands on the same spot.</summary>
    Vec2 BodyCenter()
    {
        _art.Root.Scale = _lifeScale;
        return new Vec2(_pos.X, _pos.Y - CenterLift * _lifeScale);
    }

    /// <summary>The trick to do: a wise pet sometimes does its second one (and right after growing wise, always).</summary>
    (string Name, double Seconds) PickTrick()
    {
        if (_lifeStage < PetLife.Wise) return TrickFor(Kind);
        bool second = _showNewTrick || Rng.NextDouble() < 0.45;
        _showNewTrick = false;
        return second ? PetLife.SecondTrickFor(Kind) : TrickFor(Kind);
    }

    /// <summary>The sound that starts a second trick.</summary>
    void StartSecondTrick(string name)
    {
        switch (name)
        {
            case "rings": PlayThrottled("huff", 0.35, 0.9); break;
            case "bigcroak": PlayThrottled("croak", 0.4, 0.8); break;
            case "wingstretch" or "dance": PlayThrottled("flap", 0.3); break;
            case "roll" or "mousing": PlayThrottled("whoosh", 0.18, 1.4); break;
        }
    }

    /// <summary>The side effects of a running second trick (the pose is in <see cref="SecondTrickPose"/>).</summary>
    void StepSecondTrick(double k, double dt)
    {
        switch (_act)
        {
            case "roll" when _mode == Mode.Sit && !Fx.ReducedMotion: // rolled up, it trundles a little way
            {
                var (lo, hi) = WalkRange();
                _pos.X = Clamp(_pos.X + _face * 90 * Math.Sin(Math.PI * k) * dt, lo, hi);
                break;
            }
            case "rings":
                if (_actT - _actMark > 0.4 && k < 0.85)
                {
                    _actMark = _actT; // a smoke ring that widens as it drifts off
                    Host.Fx.Marker(Mouth + new Vec2(_face * 12, -8), Smoke, 6, 26, 1.1);
                    Host.Fx.Spawn(Mouth + new Vec2(_face * 6, -4), new Vec2(_face * 30, -30), Smoke, 5, 0.8, -30);
                }
                break;
        }
    }

    /// <summary>Each second trick's body language, from the parts every drawing already has.</summary>
    static void SecondTrickPose(ref Pose p, string act, double kk, double env, double on, double t, double face)
    {
        double c;
        switch (act)
        {
            case "wave": // up on its haunches, a paw waving
                p.Sy = 1 + 0.14 * on;
                p.Sx = 1 - 0.05 * on;
                p.StepB = -13 * on;
                p.FrontX = (3 + Math.Sin(t * 14) * 3.5) * on;
                p.HeadAngle = -6 * on;
                if (kk > 0.3) p.Eyes = "happy";
                break;
            case "playdead": // over onto its back, paws in the air, eyes shut
                c = kk < 0.2 ? kk / 0.2 : kk > 0.85 ? (1 - kk) / 0.15 : 1;
                p.Angle = -face * 170 * c;
                p.StepA = (4 + Math.Sin(t * 3) * 0.6) * c;
                p.StepB = 4 * c;
                p.Tail = -20 * c;
                if (c > 0.8) p.Eyes = "sleep";
                break;
            case "dabble": // head down, tail up, feet paddling
                c = kk < 0.25 ? kk / 0.25 : kk > 0.8 ? (1 - kk) / 0.2 : 1;
                p.Angle = face * 75 * c;
                p.Tail = Math.Sin(t * 20) * 15 * c;
                p.StepA = 3 * Math.Max(0, Math.Sin(t * 16)) * c;
                p.StepB = 3 * Math.Max(0, -Math.Sin(t * 16)) * c;
                break;
            case "lookout": // up on its hind legs, tall and still, having a look round
                p.Sy = 1 + 0.22 * on;
                p.Sx = 1 - 0.1 * on;
                p.StepB = -6 * on;
                p.HeadAngle = Math.Sin(t * 3) * 8 * on;
                break;
            case "bow": // a formal bow
                p.Angle = face * 38 * env;
                if (env > 0.5) p.Eyes = "happy";
                break;
            case "mousing": // up on its toes, then nose first into the snow
                if (kk < 0.4)
                {
                    c = kk / 0.4;
                    p.Sy = 1 + 0.18 * c;
                    p.Sx = 1 - 0.08 * c;
                    p.Bob -= 14 * c;
                }
                else if (kk < 0.8)
                {
                    c = (kk - 0.4) / 0.4;
                    p.Angle = face * 85 * Math.Sin(Math.PI / 2 * Math.Min(1, c * 2));
                    p.Bob -= 14 * (1 - c);
                    p.Tail = 30;
                    p.Eyes = "happy";
                }
                else
                {
                    c = (1 - kk) / 0.2;
                    p.Angle = face * 85 * c;
                    p.Tail = 30 * c;
                }
                break;
            case "roll": // curled into a ball, rolling (with reduced motion it only curls up)
                if (!Fx.ReducedMotion) p.Angle = face * 360 * kk;
                p.Sx = p.Sy = 1 - 0.12 * on;
                p.StepA = p.StepB = -3 * on;
                p.Eyes = "happy";
                break;
            case "sunbathe": // stretched right out, eyes closed
                p.HeadX = 7 * on;
                p.HeadY = 2 * on;
                p.FrontX = 5 * on;
                p.BackX = -5 * on;
                p.Sy = 1 - 0.08 * on;
                p.Sx = 1 + 0.06 * on;
                if (on > 0.6) p.Eyes = "sleep";
                break;
            case "dance": // swaying and bobbing, wings half out
                p.Angle = Math.Sin(t * 8) * 12 * on;
                p.HeadY = -Math.Abs(Math.Sin(t * 16)) * 4 * on;
                p.Bob -= Math.Abs(Math.Sin(t * 8)) * 2 * on;
                p.Wings = 0.5 * on;
                p.Flap = Math.Sin(t * 30) * 12 * on;
                p.StepA = -2 * Math.Max(0, Math.Sin(t * 8)) * on;
                p.StepB = -2 * Math.Max(0, -Math.Sin(t * 8)) * on;
                break;
            case "bigcroak": // the throat sac blown up as big as its head
                p.Throat = 1 + 1.6 * env;
                p.Sy = 1 + 0.06 * env;
                if (env > 0.6) p.Eyes = "happy";
                break;
            case "wingstretch": // both wings out wide, held
                p.Wings = on;
                p.Flap = Math.Sin(t * 4) * 6 * on;
                p.Sy = 1 + 0.08 * on;
                p.HeadAngle = -6 * on;
                break;
            case "rings": // nose up, puffing
                p.HeadAngle = -14 * on;
                p.Sy = 1 + 0.05 * Math.Max(0, Math.Sin(t * 9)) * on;
                break;
        }
    }

    // ------------------------------------------------------------------ nap spots

    /// <summary>A pillow for the thought bubble: off to nap somewhere nice.</summary>
    void BuildLifeBubbles()
    {
        var ink = Art.Brush("#5B74B8");
        var pillow = new Canvas { IsVisible = false, IsHitTestVisible = false };
        pillow.Children.Add(Art.At(new Rectangle { Width = 20, Height = 11, RadiusX = 5, RadiusY = 5, Fill = Art.Brush("#EEF2FF"), Stroke = ink, StrokeThickness = 1.2 }, -10, -2));
        pillow.Children.Add(Art.PathOf("M-6,4 Q0,1.5 6,4", null, Art.Brush("#9AA8D6"), 1)); // the dent where the head goes
        pillow.Children.Add(Art.PathOf("M-1,-9 L3,-9 L-1,-5 L3,-5", null, ink, 1.4));       // and a little z
        _bubbleBox.Children.Add(pillow);
        _bubbleItems[Bubble.Pillow] = pillow;
    }

    /// <summary>
    /// Sleepy: when its favourite app has a window top in view with room on it, it thinks of a pillow and heads there
    /// to nap. False when there is no favourite yet, it is already on it, or the window is not there (it naps where it is).
    /// </summary>
    bool TryGoNap()
    {
        if (_demo || Now - _napTriedAt < NapRetry) return false;
        _napTriedAt = Now;
        string? favourite = PetLife.FavouriteSpot(Host.Settings.PetNapSpots);
        if (favourite == null) return false;
        var plats = Host.Platforms;
        if (_hwnd != IntPtr.Zero && plats.AppOf(_hwnd) == favourite) return false; // already there
        if (!PetLife.TryNapSpot(plats.Items, plats.AppOf, favourite, Host.Arena, Host.HudBounds.Inflate(12), _pos.X, Height * _lifeScale, HalfW,
                out var top, out double x))
            return false;
        ShowBubble(Bubble.Pillow, 2.6);
        SetGoal("nap", new Vec2(x, top.Y), top.Hwnd, NapReach);
        return true;
    }

    /// <summary>Arrived at the nap spot, or could not get there: it sleeps where it is, unless someone has just played with it.</summary>
    void NapHere()
    {
        if (_mode != Mode.Sit || _pressed || Now - _lastStir < AwakeAfterPet) return;
        GoToSleep();
    }

    /// <summary>Counts a nap on a window top toward that app's tally (by app name; the taskbar does not count).</summary>
    void RememberNap()
    {
        if (_demo || _hwnd == IntPtr.Zero || Host.Platforms.AppOf(_hwnd) is not { } spot) return;
        PetLife.RecordNap(Host.Settings.PetNapSpots, spot);
        Host.SaveSettings();
    }

    // ------------------------------------------------------------------ the pet's voice

    /// <summary>How loud one of the pet's own calls plays: its volume level, softened in the evening. Other sounds play as they are.</summary>
    double VoiceGain(string clip)
    {
        _voiceClips ??= new HashSet<string>(ClipsUsed());
        return _voiceClips.Contains(clip) ? PetLife.VoiceGain(Host.Settings.PetVolume, TimeOnly.FromDateTime(DateTime.Now)) : 1;
    }
}
