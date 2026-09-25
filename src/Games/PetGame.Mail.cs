using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade.Games;

/// <summary>
/// Pet mail: a co-worker on the LAN posts the pet a treat or a toy (see <see cref="PetMail"/> and <see cref="PetMailer"/>).
/// A parcel with a ribbon floats down on a little parachute to land near the pet, tagged "from {name}". The pet notices
/// and runs over, and the parcel opens when it gets there or when the player clicks it: a treat to eat, the ball to
/// chase, or a toy (a ball of yarn, a chew bone) that it plays with for a while in its own way.
/// </summary>
public sealed partial class PetGame
{
    enum ParcelState { None, Falling, Landed, Opening }

    const double ParcelFall = 62, ParcelDrop = 420, ParcelHalf = 10, ParcelH = 16, ParcelReach = 18, ParcelRetry = 8, ParcelHitR = 16;
    const double ToyReach = 16, ToyLife = 45, ToyYarnR = 7, ToyBoneLift = 5;
    const int MaxToyPlays = 4;

    static readonly Color Ribbon = Color.FromRgb(230, 57, 70), Kraft = Color.FromRgb(217, 168, 102);

    /// <summary>A parcel was opened (by the pet or with a click): its id, so the sender can hear their pet loved it.</summary>
    public event Action<int>? ParcelOpened;

    /// <summary>The pet can take the next waiting parcel: it came on screen, or the last parcel has been opened.</summary>
    public event Action? ReadyForParcel;

    readonly Canvas _mailLayer = new() { IsHitTestVisible = false };
    ParcelState _parcelState;
    IncomingParcel _parcel;
    Vec2 _parcelP;                 // the bottom middle of the box
    IntPtr _parcelHwnd;
    double _parcelT, _parcelSway, _parcelTriedAt = -99;
    Sprite? _parcelSprite;
    Canvas? _chute;
    Border? _tag;
    int _mailGen = -1;
    bool _mailRecheck;
    // a toy out of a parcel
    PetGift _toyGift;
    Sprite? _toySprite;
    Vec2 _toyP;                    // where it touches the ground
    IntPtr _toyHwnd;
    double _toyAt, _toyHop, _toyNudge;
    int _toyPlays;
    bool _toyShown;
    string _toyAct = "";

    /// <summary>True while the pet is on screen with no parcel of its own: the mailer may hand it the next one.</summary>
    public bool CanTakeParcel => _active && _parcelState == ParcelState.None;

    /// <summary>
    /// How an animal plays with a toy, turn by turn, from the moves it already has: a cat bats and kneads it, a dog
    /// wags and shakes it, a hamster runs rings round it, a turtle cranes its neck and nibbles it, a dragon puffs smoke at it.
    /// </summary>
    public static string ToyActOf(string kind, PetGift gift, int turn)
    {
        string[] acts = kind switch
        {
            "dog" => new[] { "wag", "shake" },
            "duck" => new[] { "shake", "nod" },
            "bunny" => new[] { "thump", "sniff" },
            "penguin" => new[] { "shake", "preen" },
            "fox" => new[] { "scratch", "sniff" },
            "hamster" => new[] { "circle", "stuff" },
            "turtle" => new[] { "neck", "nibble" },
            "parrot" => new[] { "bob", "shriek" },
            "frog" => new[] { "throat", "blink" },
            "owl" => new[] { "swivel", "fluff" },
            "dragon" => new[] { "smoke", "scratch" },
            _ => new[] { "swat", "knead" },
        };
        return acts[(Math.Max(0, turn) + (gift == PetGift.Bone ? 1 : 0)) % acts.Length];
    }

    void BuildMail()
    {
        // behind the pet, so it stands in front of its parcel
        Layer.Children.Insert(Math.Max(0, Layer.Children.IndexOf(_art.Root)), _mailLayer);
    }

    // ------------------------------------------------------------------ the parcel

    /// <summary>A parcel from the co-worker: it floats down on its parachute to land beside the pet.</summary>
    public void DropParcel(IncomingParcel parcel)
    {
        if (!CanTakeParcel) return;
        var a = Host.Arena;
        _parcel = parcel;
        double side = _pos.X < a.Center.X ? 1 : -1; // on the roomier side
        double x = Clamp(_pos.X + side * (50 + Rng.NextDouble() * 40), a.Left + 30, a.Right - 30);
        BuildParcelArt(parcel.From);
        _parcelT = _parcelSway = 0;
        _parcelHwnd = IntPtr.Zero;
        _mailGen = Host.Platforms.Generation;
        if (Fx.ReducedMotion)
        {
            // no floating about: it is simply there, where it would have landed
            bool onWindow = Host.Platforms.FindLanding(x, a.Top, a.Bottom, out var top) && top.Y - 40 >= a.Top;
            _parcelP = new Vec2(x, onWindow ? top.Y : a.Bottom);
            LandParcel(onWindow ? top.Hwnd : IntPtr.Zero);
        }
        else
        {
            _parcelP = new Vec2(x, a.Top - 2);
            _parcelState = ParcelState.Falling;
            PlayThrottled("whoosh", 0.18, 0.6);
        }
        DrawMail();
        UpdateHud();
        Host.Wake();
    }

    void BuildParcelArt(string from)
    {
        if (_parcelSprite != null) _mailLayer.Children.Remove(_parcelSprite);
        var s = new Sprite { IsHitTestVisible = false };
        var ink = Art.Brush("#8C5A2B");
        var ribbon = Art.Brush(Art.Safe(Ribbon));

        // the parachute is drawn from the top of the box up, so it folds down onto the box
        var chute = new Canvas();
        chute.Children.Add(Art.PathOf("M-24,-28 L-9,0 M-12,-28 L-5,0 M12,-28 L5,0 M24,-28 L9,0", null, Art.Brush("#6B6F78"), 0.8));
        chute.Children.Add(Art.PathOf("M-24,-28 Q-24,-52 0,-52 Q24,-52 24,-28 Q18,-32 12,-28 Q6,-32 0,-28 Q-6,-32 -12,-28 Q-18,-32 -24,-28 Z",
            Art.Brush("#F4F1EA"), Art.Brush("#8A8F9A"), 1));
        chute.Children.Add(Art.PathOf("M-8,-29.5 Q-8,-51 0,-52 Q8,-51 8,-29.5 Q4,-32 0,-28 Q-4,-32 -8,-29.5 Z", Art.Brush(Art.Safe(Color.FromRgb(77, 163, 255)))));
        chute.RenderTransform = new ScaleTransform(1, 1);
        chute.RenderTransformOrigin = RelativePoint.TopLeft;
        Art.At(chute, 0, -ParcelH);
        s.Children.Add(chute);

        s.Children.Add(Art.At(new Ellipse { Width = 26, Height = 5, Fill = Art.Brush(55, 0, 0, 0) }, -13, -2.5));
        s.Children.Add(Art.At(new Border
        {
            Width = ParcelHalf * 2, Height = ParcelH, CornerRadius = new CornerRadius(2), Background = Art.Brush(Kraft), BorderBrush = ink, BorderThickness = new Thickness(1.2),
        }, -ParcelHalf, -ParcelH));
        s.Children.Add(Art.At(new Rectangle { Width = 4, Height = ParcelH - 1.2, Fill = ribbon }, -2, -ParcelH + 0.6));
        s.Children.Add(Art.At(new Rectangle { Width = ParcelHalf * 2 - 1.2, Height = 3.5, Fill = ribbon }, -ParcelHalf + 0.6, -ParcelH / 2 - 1.75));
        // the bow: two loops and a knot
        s.Children.Add(Art.At(new Ellipse { Width = 8, Height = 5.5, Fill = ribbon, Stroke = ink, StrokeThickness = 0.6, RenderTransform = new RotateTransform(-20) }, -8, -ParcelH - 5));
        s.Children.Add(Art.At(new Ellipse { Width = 8, Height = 5.5, Fill = ribbon, Stroke = ink, StrokeThickness = 0.6, RenderTransform = new RotateTransform(20) }, 0, -ParcelH - 5));
        s.Children.Add(Art.Circle(0, -ParcelH - 1.5, 2, ribbon, ink, 0.6));

        var tag = new Border
        {
            Background = Art.Brush("#FFF6DC"), BorderBrush = ink, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Child = new TextBlock { Text = L.F("from {0}", from), FontFamily = Fx.Font, FontSize = 11, Foreground = Art.Brush("#3A2A14") },
        };
        tag.Measure(Size.Infinity);
        s.Children.Add(tag);

        _chute = chute;
        _tag = tag;
        _parcelSprite = s;
        _mailLayer.Children.Add(s);
    }

    void StepParcel(double dt)
    {
        var a = Host.Arena;
        _parcelT += dt;
        double prev = _parcelP.Y;
        bool chute = _chute is { IsVisible: true };
        _parcelP.Y += (chute ? ParcelFall : ParcelDrop) * dt;
        _parcelSway = chute ? Math.Sin(_parcelT * 1.7) * 12 : 0;
        double x = Clamp(_parcelP.X + _parcelSway, a.Left + ParcelHalf, a.Right - ParcelHalf);
        // a window top on the way down catches it, unless it is too near the top of the screen to hold it
        if (Host.Platforms.FindLanding(x, prev, _parcelP.Y, out var top) && top.Y - 40 >= a.Top)
        {
            _parcelP = new Vec2(Clamp(x, top.X1 + ParcelHalf, top.X2 - ParcelHalf), top.Y);
            _parcelSway = 0;
            LandParcel(top.Hwnd);
        }
        else if (_parcelP.Y >= a.Bottom)
        {
            _parcelP = new Vec2(x, a.Bottom);
            _parcelSway = 0;
            LandParcel(IntPtr.Zero);
        }
    }

    /// <summary>Down: the parachute folds away and the pet notices.</summary>
    void LandParcel(IntPtr hwnd)
    {
        _parcelState = ParcelState.Landed;
        _parcelHwnd = hwnd;
        _mailGen = Host.Platforms.Generation;
        if (_chute is { } chute && chute.IsVisible)
        {
            var fold = (ScaleTransform)chute.RenderTransform!;
            Anims.Add(0.45, k =>
            {
                fold.ScaleY = 1 - k;
                chute.Opacity = 1 - k;
            }, Ease.InQuad, () => chute.IsVisible = false);
        }
        PlayThrottled("thunk", 0.3, 1.2);
        if (!Fx.ReducedMotion) Host.Fx.Burst(_parcelP, Crumbs, 5, 90, 500, 3, 0.4); // a puff of dust
        if (Awake && _mode != Mode.Carried)
        {
            ShowBubble(Bubble.Alarm, 1.2);
            Speak(Say.Surprise, 0.45);
        }
        _parcelTriedAt = -99;
        TryGoToParcel();
        UpdateHud();
        Host.Wake();
    }

    /// <summary>Sends the pet over to the parcel when it is free to go; false when it is busy (carried, fetching, after a treat).</summary>
    bool TryGoToParcel()
    {
        if (_parcelState != ParcelState.Landed || _mode is Mode.Carried or Mode.Air || _pressed || _ballHeld) return false;
        if (_fetch.Length > 0 || _goal is "treat" or "parcel") return false;
        _parcelTriedAt = Now;
        _lastStir = Now;
        if (_hwnd == _parcelHwnd && Math.Abs(_pos.Y - _parcelP.Y) < 6 && Math.Abs(_pos.X - _parcelP.X) <= ParcelReach)
        {
            if (_mode == Mode.Sleep) WakeUp();
            OpenParcel();
        }
        else SetGoal("parcel", _parcelP, _parcelHwnd, ParcelReach);
        return true;
    }

    /// <summary>The ribbon comes off: a burst of colour, then the gift inside.</summary>
    void OpenParcel()
    {
        if (_parcelState != ParcelState.Landed || _parcelSprite is not { } sprite) return;
        _parcelState = ParcelState.Opening;
        if (_goal == "parcel") ClearGoal();
        var at = _parcelP;
        var gift = _parcel.Gift;
        if (_mode == Mode.Sleep)
        {
            WakeUp();
            _lastStir = Now;
        }
        if (_tag != null) _tag.IsVisible = false;
        if (_chute != null) _chute.IsVisible = false;
        PlayThrottled("pop", 0.4, 1.1);
        PlayThrottled("star", 0.3, 1.5);
        Host.Fx.Burst(at - new Vec2(0, ParcelH), new[] { Art.Safe(Ribbon), Kraft, Colors.White }, 16, 260, 600, 5, 0.7);
        Anims.Add(0.35, k =>
        {
            sprite.Scale = 1 + 0.3 * k;
            sprite.Opacity = 1 - k;
        }, Ease.OutCubic, () =>
        {
            sprite.IsVisible = false;
            _parcelState = ParcelState.None;
            UpdateHud();
            Dispatcher.UIThread.Post(() => ReadyForParcel?.Invoke()); // the next one, if more are waiting
        });
        Host.Stats.Add("pet.parcels");
        Thrill(0.25);
        ShowBubble(Bubble.Heart, 1.8);
        Release(gift, at);
        ParcelOpened?.Invoke(_parcel.Id);
        UpdateHud();
        Host.HudChanged();
        Host.Wake();
    }

    /// <summary>What was in the parcel: a treat or the ball pop out, a toy stays for the pet to play with.</summary>
    void Release(PetGift gift, Vec2 at)
    {
        double side = _pos.X < at.X ? -1 : 1; // out toward the pet
        switch (gift)
        {
            case PetGift.Treat:
                _treat.State = ThingState.Air;
                _treat.P = at - new Vec2(0, ParcelH + TreatR);
                _treat.V = new Vec2(side * 60, -260);
                _treat.Hwnd = IntPtr.Zero;
                _treat.Sprite.IsVisible = true;
                _treatWanted = true;
                _begging = false;
                _lastStir = Now;
                break;
            case PetGift.Ball:
                if (_ballHeld) break; // the player has it in hand: the parcel was the ball's box, then
                if (_ball.State == ThingState.Carried) ReleaseBall(default);
                if (_fetch.Length > 0) EndFetch();
                _ball.State = ThingState.Air;
                _ball.P = at - new Vec2(0, ParcelH + BallR);
                _ball.V = new Vec2(-side * 240, -420); // away from the pet, for it to chase
                _ball.Hwnd = IntPtr.Zero;
                _ball.Thrown = true;
                _ball.Sprite.IsVisible = true;
                _watchBall = false;
                PlayThrottled("whoosh", 0.25, 1.3);
                OnBallThrown();
                Host.HudChanged();
                break;
            default:
                ShowToy(gift, at, _parcelHwnd);
                break;
        }
    }

    // ------------------------------------------------------------------ a toy

    void ShowToy(PetGift gift, Vec2 at, IntPtr hwnd)
    {
        if (_toySprite != null) _mailLayer.Children.Remove(_toySprite);
        _toyGift = gift;
        _toySprite = BuildToy(gift);
        _mailLayer.Children.Add(_toySprite);
        _toyP = at;
        _toyHwnd = hwnd;
        _toyAt = Now;
        _toyPlays = 0;
        _toyHop = _toyNudge = 0;
        _toyShown = true;
        _toyAct = "";
        var toy = _toySprite;
        toy.Scale = 0.3;
        Anims.Add(0.35, k => toy.Scale = 0.3 + 0.7 * k, Ease.OutBack);
        Anims.After(0.4, GoToToy);
    }

    /// <summary>A ball of yarn with a loose end, or a big white chew bone; drawn around the spot where it sits.</summary>
    static Sprite BuildToy(PetGift gift)
    {
        var s = new Sprite { IsHitTestVisible = false };
        s.Children.Add(Art.At(new Ellipse { Width = 18, Height = 4, Fill = Art.Brush(55, 0, 0, 0) }, -9, -2));
        if (gift == PetGift.Yarn)
        {
            var wool = Color.FromRgb(176, 132, 245);
            var ink = Art.Brush(Art.Blend(wool, Colors.Black, 0.4));
            s.Rotor.Children.Add(Art.PathOf("M5,-3 Q11,-1 14,-3 Q17,-5 19,-1", null, ink, 1.1));
            s.Rotor.Children.Add(Art.Circle(0, -ToyYarnR, ToyYarnR, Art.Brush(wool), ink, 1.1));
            s.Rotor.Children.Add(Art.PathOf("M-5,-11 Q0,-6 5,-11 M-6.5,-6 Q0,-1 6.5,-7 M-3,-13.5 Q3,-8 6,-4 M-6,-9 Q-2,-4 1,-0.5", null, ink, 0.8));
        }
        else
        {
            s.Rotor.Children.Add(Art.At(new Path
            {
                Data = Geometry.Parse("M-9.6,-3.2 C-12.8,-7.2 -6.4,-9.6 -4.8,-4.8 L4.8,-4.8 C6.4,-9.6 12.8,-7.2 9.6,-3.2 C11.2,-1.6 11.2,1.6 9.6,3.2 " +
                                      "C12.8,7.2 6.4,9.6 4.8,4.8 L-4.8,4.8 C-6.4,9.6 -12.8,7.2 -9.6,3.2 C-11.2,1.6 -11.2,-1.6 -9.6,-3.2 Z"),
                Fill = Art.Brush("#FAF7F0"), Stroke = Art.Brush("#9A9486"), StrokeThickness = 1.1, StrokeJoin = PenLineJoin.Round,
            }, 0, -ToyBoneLift));
        }
        return s;
    }

    void GoToToy()
    {
        if (!_toyShown || _mode != Mode.Sit || _pressed || _goal.Length > 0 || _fetch.Length > 0) return;
        if (_hwnd == _toyHwnd && Math.Abs(_pos.Y - _toyP.Y) < 6 && Math.Abs(_pos.X - _toyP.X) <= ToyReach + 12) PlayWithToy();
        else SetGoal("toy", _toyP, _toyHwnd, ToyReach);
    }

    /// <summary>One turn of play: the animal's own move, and the toy gets a little hop and a nudge.</summary>
    void PlayWithToy()
    {
        if (!_toyShown || _mode != Mode.Sit || _pressed) return;
        if (Math.Abs(_toyP.X - _pos.X) > 2) _face = _toyP.X > _pos.X ? 1 : -1;
        _toyAct = ToyActOf(Kind, _toyGift, _toyPlays++);
        StartAct(_toyAct);
        _lastStir = Now;
        Thrill(0.15);
        Hearts(2);
        Speak(Say.Happy, 0.4);
        double dir = _face, from = _toyP.X;
        var a = Host.Arena;
        Anims.Add(0.45, k =>
        {
            _toyHop = Math.Sin(Math.PI * k) * 10;
            _toyNudge = dir * 7 * k;
        }, Ease.Linear, () =>
        {
            _toyHop = _toyNudge = 0;
            _toyP.X = Clamp(from + dir * 7, a.Left + 10, a.Right - 10);
            _mailRecheck = true; // nudged off the edge of a window top? it drops to the taskbar
        }, 0.15);
        UpdateHud();
    }

    /// <summary>Played out: the toy fades away.</summary>
    void RetireToy()
    {
        if (!_toyShown || _toySprite is not { } toy) return;
        _toyShown = false;
        if (_goal == "toy") ClearGoal();
        Anims.Add(0.6, k => toy.Opacity = 1 - k, Ease.Linear, () =>
        {
            _mailLayer.Children.Remove(toy);
            if (_toySprite == toy) _toySprite = null;
        });
        UpdateHud();
        Host.Wake();
    }

    // ------------------------------------------------------------------ hooks from the pet's own code

    /// <summary>At a thought: go and open a parcel that is still waiting, or have another turn with the toy. True when it did something.</summary>
    bool MailDecide()
    {
        if (_parcelState == ParcelState.Landed && Now - _parcelTriedAt > ParcelRetry && TryGoToParcel()) return true;
        if (!_toyShown) return false;
        if (Now - _toyAt > ToyLife || _toyPlays >= MaxToyPlays)
        {
            RetireToy();
            return false;
        }
        if (Rng.NextDouble() > 0.6) return false;
        GoToToy();
        return _goal.Length > 0 || _act.Length > 0;
    }

    /// <summary>The goals this file sets: true when <paramref name="goal"/> was one of them.</summary>
    bool MailArrive(string goal)
    {
        switch (goal)
        {
            case "parcel": OpenParcel(); return true;
            case "toy": PlayWithToy(); return true;
        }
        return false;
    }

    string? MailLine()
    {
        if (_goal == "parcel") return L.T("Off to open the parcel");
        if (_parcelState == ParcelState.Falling) return L.T("A parcel is coming down!");
        if (_parcelState == ParcelState.Landed) return L.T("A parcel! · click it to open");
        if (_toyShown && (_goal == "toy" || (_act.Length > 0 && _act == _toyAct))) return L.T("Playing with its new toy");
        return null;
    }

    void MailHitShapes(System.Collections.Generic.List<HitShape> into)
    {
        if (_parcelState == ParcelState.Landed) into.Add(HitShape.Circle(_parcelP - new Vec2(0, ParcelH / 2), ParcelHitR));
    }

    /// <summary>A click on a landed parcel opens it.</summary>
    bool MailPointerDown(Vec2 p, bool right)
    {
        if (right || _parcelState != ParcelState.Landed || (p - (_parcelP - new Vec2(0, ParcelH / 2))).Length > ParcelHitR) return false;
        _lastStir = Now;
        OpenParcel();
        return true;
    }

    /// <summary>The arena changed or the pet came back on screen: keep the parcel and the toy inside, and take the next parcel.</summary>
    void MailLayout()
    {
        var a = Host.Arena;
        if (_parcelState != ParcelState.None)
        {
            _parcelP.X = Clamp(_parcelP.X, a.Left + ParcelHalf, a.Right - ParcelHalf);
            _parcelP.Y = Clamp(_parcelP.Y, a.Top - 2, a.Bottom);
        }
        if (_toyShown) _toyP = new Vec2(Clamp(_toyP.X, a.Left + 10, a.Right - 10), Clamp(_toyP.Y, a.Top, a.Bottom));
        _mailGen = Host.Platforms.Generation; // windows may have moved while we were away: check again without a stale delta
        _mailRecheck = true;
        DrawMail();
        Dispatcher.UIThread.Post(() => ReadyForParcel?.Invoke());
    }

    /// <summary>Once a frame: the falling parcel, and a parcel or toy riding along with its window. True while the parcel falls.</summary>
    bool UpdateMail(double dt)
    {
        if (_parcelState == ParcelState.None && !_toyShown) return false;
        var plats = Host.Platforms;
        bool changed = plats.Generation != _mailGen;
        _mailGen = plats.Generation;
        bool busy = false;
        if (_parcelState == ParcelState.Falling)
        {
            StepParcel(dt);
            busy = _parcelState == ParcelState.Falling;
        }
        else if (_parcelState == ParcelState.Landed && Ride(ref _parcelP, ref _parcelHwnd, ParcelHalf, changed) && _goal == "parcel")
        {
            _goalP = _parcelP;
            _goalHwnd = _parcelHwnd;
        }
        if (_toyShown)
        {
            if (Ride(ref _toyP, ref _toyHwnd, 8, changed) && _goal == "toy")
            {
                _goalP = _toyP;
                _goalHwnd = _toyHwnd;
            }
            if (Now - _toyAt > ToyLife + 10 && _goal != "toy" && _act != _toyAct) RetireToy(); // asleep or busy through all its turns
        }
        _mailRecheck = false;
        DrawMail();
        return busy;
    }

    /// <summary>Keeps something resting on a window top with that window, or drops it to the taskbar when the window goes; true if it moved.</summary>
    bool Ride(ref Vec2 p, ref IntPtr hwnd, double half, bool changed)
    {
        var a = Host.Arena;
        if (hwnd == IntPtr.Zero)
        {
            if (p.Y == a.Bottom) return false;
            p.Y = a.Bottom;
            return true;
        }
        if (!changed && !_mailRecheck) return false;
        var plats = Host.Platforms;
        if (changed) p += plats.DeltaOf(hwnd);
        foreach (var plat in plats.Items)
        {
            if (plat.Hwnd != hwnd || Math.Abs(plat.Y - p.Y) > 5 || p.X < plat.X1 - 2 || p.X > plat.X2 + 2) continue;
            p = new Vec2(Clamp(p.X, Math.Min(plat.X1 + half, plat.X2), Math.Max(plat.X2 - half, plat.X1)), plat.Y);
            return true;
        }
        hwnd = IntPtr.Zero; // the window closed or moved away: down to the taskbar
        p = new Vec2(Clamp(p.X, a.Left + half, a.Right - half), a.Bottom);
        return true;
    }

    void DrawMail()
    {
        if (_parcelSprite != null && _parcelState != ParcelState.None)
        {
            var at = _parcelP + new Vec2(_parcelSway, 0);
            _parcelSprite.Set(at);
            if (_tag is { IsVisible: true } tag)
            {
                // the tag hangs on the side with room for it
                double w = tag.DesiredSize.Width;
                bool left = at.X + 14 + w > Host.Arena.Right - 4;
                Canvas.SetLeft(tag, left ? -14 - w : 14);
                Canvas.SetTop(tag, -ParcelH - 2);
            }
        }
        if (_toySprite != null) _toySprite.Set(_toyP + new Vec2(_toyNudge, -_toyHop));
    }
}
