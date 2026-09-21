using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Desktop Pet: a little cat-like blob that lives on the taskbar and the tops of your windows. It wanders,
/// jumps between windows, naps when ignored and loves being petted. Click to pet it, drag to carry and throw it,
/// right-click for its trick. Each animal has its own voices, its own trick (see <see cref="TrickFor"/>), its own
/// gait and its own habits (see <see cref="HabitsOf"/>): cats groom, knead and stalk the cursor, dogs sniff, scratch
/// and wag, ducks preen and follow you around, bunnies thump and flop, penguins bray, foxes pounce.
/// It has moods that build up over time: it gets excited when played with (zoomies), bored when ignored
/// (it calls for you), and tired the longer it is awake or the more it runs (it yawns, then naps).
/// There is no score to chase, so it is built to sit perfectly still (zero CPU) most of the time.
/// </summary>
public sealed class PetGame : MiniGame
{
    const double Step = 1.0 / 240, Gravity = 1800, HalfW = 22, Height = 46, CenterLift = 20;
    const double HitR = 27, DragStart = 6, CarryCount = 30, DangleY = 18, MaxThrow = 2600;
    const double JumpSide = 280, JumpUp = 240, ApexExtra = 40, EdgeIn = 6;
    const double WallBounce = 0.5, FloorBounce = 0.4, BounceMin = 450, SlideMin = 350;
    const double SleepAfter = 60, DemoSleepAfter = 14, AttentionAfter = 25, NoticeR = 200;
    const double HappyTime = 0.55, SquashTime = 0.22, PupilReach = 1.6;
    const double SnoreFor = 120, HardThrow = 1100, ZoomSpeed = 3;

    static readonly Color[] Blush = { Color.FromRgb(255, 120, 160), Color.FromRgb(255, 200, 220), Colors.White };

    enum Mode { Sit, Walk, Air, Carried, Sleep }

    /// <summary>What the pet wants to say; each animal answers with its own calls (see <see cref="VoicesOf"/>).</summary>
    enum Say { Hello, Surprise, Call, Happy, Upset, Content, Hunt }

    /// <summary>How long each of the pet's little actions lasts, in seconds (tricks are in <see cref="TrickFor"/>).</summary>
    static readonly Dictionary<string, double> ActSeconds = new()
    {
        ["groom"] = 2.2, ["knead"] = 2.2, ["sniff"] = 1.4, ["scratch"] = 1.4, ["wag"] = 1.4, ["pant"] = 1.8, ["preen"] = 2.0,
        ["nod"] = 1.1, ["shake"] = 0.6, ["flop"] = 3.0, ["call"] = 2.9, ["yawn"] = 1.3, ["stalk"] = 1.3, ["chatter"] = 1.1,
        ["puff"] = 1.0, ["thump"] = 0.7, ["swat"] = 0.35,
    };

    /// <summary>The pet's drawing. The body canvas faces +x and is flipped/squashed as a whole.</summary>
    sealed class PetArt
    {
        public required Sprite Root;
        public required ScaleTransform BodyScale;
        public required TranslateTransform BodyShift;
        public required TranslateTransform PupilL, PupilR, FootA, FootB;
        public required RotateTransform Tail;
        public required Control EyesOpen, EyesSleep, EyesHappy, Shadow;
        public required TextBlock Zz;
    }

    sealed class Heart
    {
        public required Path El;
        public required TranslateTransform Tr;
        public required ScaleTransform Sc;
        public Vec2 P, V;
        public double Age, Life, Phase;
    }

    PetArt _art;
    readonly Canvas _heartLayer = new() { IsHitTestVisible = false };
    readonly List<Heart> _hearts = new();
    readonly Stack<Heart> _heartPool = new();
    readonly List<(double t, Vec2 p)> _trail = new();
    readonly Dictionary<string, double> _lastSound = new();
    readonly DispatcherTimer _brain = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly Stopwatch _clock = Stopwatch.StartNew();

    Mode _mode = Mode.Sit;
    Vec2 _pos, _vel, _pressPos, _carryStart, _demoStep;
    Vec2? _demoHand;
    IntPtr _hwnd;
    DeskArcade.Engine.Platform _surface;
    int _seenGen = -1, _pets, _demoTicks;
    double _face = 1, _walkLeft, _walkTargetX = double.NaN, _acc, _animT, _happyT, _squashT, _swing;
    double _lastStir, _sleptAt;
    bool _placed, _active, _pressed, _recheck, _decide, _hopAtEdge, _thrown, _demo;
    string _shownLine = "";
    string _act = "", _afterLand = "";       // the running action (a trick or a habit), and one to do on landing
    double _actT, _actLen, _actAngle, _actX;
    double _speed = 1;                       // walking speed factor: more than 1 during zoomies
    double _rested = 1, _awakeSince, _exertion, _excite, _excitedAt;
    double _lastCall, _lastPetAt, _yawnedAt = -99, _carryNag, _lastVoice = -99, _throwSpeed;
    int _petStreak;

    public PetGame(IGameHost host) : base(host)
    {
        _art = BuildPet(Kind);
        _art.Root.IsHitTestVisible = false;
        Layer.Children.Add(_art.Root);
        Layer.Children.Add(_heartLayer);
        _brain.Tick += (_, _) => Think();
    }

    public override string Id => "pet";
    public override string Title => "Desktop Pet";

    /// <summary>Settings.PetKind values.</summary>
    public static readonly string[] Kinds = { "cat", "dog", "duck", "bunny", "penguin", "fox" };

    string Kind => Array.IndexOf(Kinds, Host.Settings.PetKind) >= 0 ? Host.Settings.PetKind : "cat";

    /// <summary>
    /// Each animal's calls for each thing it wants to say, from the synthesized clips in <see cref="Sound"/>;
    /// one is picked at random. Empty means the animal keeps quiet (a fox does not announce a hunt).
    /// </summary>
    static string[] VoicesOf(string kind, Say say) => kind switch
    {
        "dog" => say switch
        {
            Say.Surprise => new[] { "bark1" }, Say.Call => new[] { "whine", "whine", "bark" }, Say.Happy => new[] { "bark", "bark1" },
            Say.Upset => new[] { "whine" }, Say.Content => new[] { "pant" }, Say.Hunt => new[] { "bark", "bark1" },
            _ => new[] { "bark1", "woof", "bark" },
        },
        "duck" => say switch
        {
            Say.Surprise => new[] { "quack" }, Say.Call or Say.Upset => new[] { "quacks" }, Say.Content => new[] { "chatter" },
            _ => new[] { "quack", "chatter" },
        },
        "bunny" => say switch
        {
            Say.Surprise or Say.Upset => new[] { "squeak", "squeak2" }, Say.Content => Array.Empty<string>(), Say.Hunt => new[] { "sniff" },
            _ => new[] { "grunt" },
        },
        "penguin" => say switch
        {
            Say.Call => new[] { "bray" }, Say.Hello or Say.Happy or Say.Upset => new[] { "honk", "honk", "peep" }, _ => new[] { "peep" },
        },
        "fox" => say switch
        {
            Say.Surprise => new[] { "yip1" }, Say.Call => new[] { "yip", "yip", "scream" }, Say.Happy or Say.Upset => new[] { "gekker" },
            Say.Content or Say.Hunt => Array.Empty<string>(), _ => new[] { "yip1", "gekker" },
        },
        _ => say switch
        {
            Say.Surprise => new[] { "mew", "mrrp" }, Say.Call => new[] { "meow", "meow2" }, Say.Happy => new[] { "mrrp", "mew" },
            Say.Upset => new[] { "hiss" }, Say.Content => new[] { "purr" }, Say.Hunt => new[] { "chirp" },
            _ => new[] { "meow", "meow2", "mew", "mrrp" },
        },
    };

    /// <summary>Every clip a pet can play (for tests).</summary>
    public static IEnumerable<string> ClipsUsed()
    {
        foreach (var kind in Kinds)
            foreach (var say in Enum.GetValues<Say>())
                foreach (var clip in VoicesOf(kind, say)) yield return clip;
        foreach (var clip in new[] { "purr", "snore", "yawn", "sniff", "lick", "pant", "thump", "flap", "hiss" }) yield return clip;
    }

    /// <summary>How long one of the pet's habits lasts, in seconds; 0 if there is no such habit.</summary>
    public static double ActLength(string act) => ActSeconds.TryGetValue(act, out double s) ? s : 0;

    /// <summary>What each animal does with itself when nothing is going on.</summary>
    public static string[] HabitsOf(string kind) => kind switch
    {
        "dog" => new[] { "sniff", "scratch", "wag" },
        "duck" => new[] { "preen", "nod", "shake" },
        "bunny" => new[] { "sniff", "groom", "flop" },
        "penguin" => new[] { "preen", "call", "shake" },
        "fox" => new[] { "sniff", "groom", "scratch" },
        _ => new[] { "groom", "knead", "sniff" },
    };

    /// <summary>Walking speed in px/s: a penguin waddles, a dog trots.</summary>
    static double WalkSpeedOf(string kind) => kind switch
    {
        "dog" => 88, "duck" => 52, "bunny" => 80, "penguin" => 40, "fox" => 80, _ => 72,
    };

    /// <summary>Pitch for the shared clips (yawn, snore, sniff, lick), so a bunny's yawn is smaller than a dog's.</summary>
    double SizePitch => Kind switch { "dog" => 0.85, "duck" => 1.15, "bunny" => 1.45, "penguin" => 0.95, "fox" => 1.1, _ => 1.05 };

    /// <summary>1 when fresh; drops the longer it is awake and the more it runs, and a nap fills it up again.</summary>
    double Energy => Math.Clamp(_rested - (Now - _awakeSince) / 900 - _exertion, 0, 1);

    /// <summary>Goes up when it is petted or thrown, and calms down over about 20 seconds.</summary>
    double Excitement => _excite * Math.Exp(-(Now - _excitedAt) / 20);

    void Thrill(double amount)
    {
        _excite = Math.Min(1.5, Excitement + amount);
        _excitedAt = Now;
    }

    bool IsTrick(string act) => act.Length > 0 && act == TrickFor(Kind).Name;

    /// <summary>A bunny moves in hops: 0 to 1 through each hop.</summary>
    double HopPhase => _animT * 3.2 * Math.Sqrt(_speed) % 1;

    /// <summary>
    /// Each animal's trick and how long it lasts: the cat stretches, the dog chases its tail, the duck
    /// flaps, the bunny does a twisting hop (a binky), the penguin belly-slides and the fox crouches and pounces.
    /// </summary>
    public static (string Name, double Seconds) TrickFor(string kind) => kind switch
    {
        "dog" => ("spin", 0.9), "duck" => ("flap", 1.0), "bunny" => ("binky", 0.75), "penguin" => ("slide", 1.4), "fox" => ("pounce", 0.4),
        _ => ("stretch", 1.4),
    };

    void Speak(Say say, double volume = 0.55, double pitch = 1)
    {
        var voices = VoicesOf(Kind, say);
        if (voices.Length == 0 || Now - _lastVoice < 0.3) return; // one thing at a time
        _lastVoice = Now;
        PlayThrottled(voices[Rng.Next(voices.Length)], volume, pitch * (0.94 + Rng.NextDouble() * 0.12));
    }

    /// <summary>Redraws the pet after the kind changes in the tray.</summary>
    public void Rebuild()
    {
        int at = Layer.Children.IndexOf(_art.Root);
        Layer.Children.Remove(_art.Root);
        _art = BuildPet(Kind);
        _art.Root.IsHitTestVisible = false;
        Layer.Children.Insert(Math.Max(0, at), _art.Root);
        Draw();
    }

    public override Sprite CreateIcon()
    {
        var icon = BuildPet(Kind);
        icon.Shadow.IsVisible = false;
        icon.BodyShift.Y = 4;
        icon.Root.Scale = 0.42;
        return icon.Root;
    }

    double Now => _clock.Elapsed.TotalSeconds;
    Vec2 Center => new(_pos.X, _pos.Y - CenterLift);
    bool Grounded => _mode is Mode.Sit or Mode.Walk or Mode.Sleep;

    public override HudInfo Hud => new(_pets.ToString(), StateLine(), L.F("Pets {0}", Host.Stats.Get("pet.pets")));

    string StateLine()
    {
        if (_mode == Mode.Sleep) return L.T("Sleeping · click to wake");
        if (_mode == Mode.Carried) return L.T("Wheee! · let go to throw");
        if (IsTrick(_act)) return L.T("Showing off");
        if (_act is "groom" or "preen") return L.T("Grooming");
        if (_act is "stalk" or "chatter") return L.T("Hunting the cursor");
        if (_act == "yawn") return L.T("Sleepy");
        if (_mode == Mode.Walk && _speed > 1) return L.T("Zoomies!");
        if (_thrown) return L.T("Wheee!");
        if (Now - _lastStir > AttentionAfter) return L.T("Wants attention · click to pet");
        return _hwnd != IntPtr.Zero ? L.T("Exploring the window tops") : L.T("Click to pet · right-click for a trick · drag to carry");
    }

    void UpdateHud()
    {
        string line = StateLine();
        if (line == _shownLine) return;
        _shownLine = line;
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ life cycle

    public override void Layout()
    {
        var a = Host.Arena;
        if (!_placed)
        {
            _placed = true;
            _pos = new Vec2(a.Left + a.Width * 0.3, a.Bottom);
            _lastStir = _awakeSince = Now;
        }
        _pos.X = Clamp(_pos.X, a.Left + HalfW, a.Right - HalfW);
        _pos.Y = Clamp(_pos.Y, a.Top + Height, a.Bottom);
        if (Grounded && _hwnd == IntPtr.Zero) _pos.Y = a.Bottom;
        // windows may have moved while we were away: check the window top again without applying a stale delta
        _seenGen = Host.Platforms.Generation;
        _recheck = Grounded && _hwnd != IntPtr.Zero;
        _active = true;
        RunBrain();
        Draw();
        _shownLine = StateLine();
        Host.HudChanged();
    }

    public override void Deactivate()
    {
        _active = false;
        _brain.Stop();
        _decide = false;
        _pressed = false;
        _demoHand = null;
        if (_mode == Mode.Carried) Drop(default);
        foreach (var h in _hearts)
        {
            h.El.IsVisible = false;
            _heartPool.Push(h);
        }
        _hearts.Clear();
    }

    void RunBrain()
    {
        if (_active && !_brain.IsEnabled) _brain.Start();
    }

    /// <summary>
    /// The behaviour timer. The pointer is only fresh while frames run, so the timer just wakes the loop
    /// and the next frame picks what to do. Asleep, it only snores now and then, without drawing a frame.
    /// </summary>
    void Think()
    {
        if (_mode == Mode.Sleep)
        {
            _brain.Interval = TimeSpan.FromSeconds(5 + Rng.NextDouble() * 6);
            if (Now - _sleptAt > SnoreFor) _brain.Stop(); // deep asleep: quiet, and it costs nothing until someone clicks it
            else if (Rng.NextDouble() < 0.55)
                PlayThrottled(Kind == "cat" && Rng.NextDouble() < 0.3 ? "purr" : "snore", 0.14, SizePitch * (0.95 + Rng.NextDouble() * 0.1));
            return;
        }
        _brain.Interval = TimeSpan.FromSeconds(2 + Rng.NextDouble() * 4);
        if (!_active || _pressed || _mode != Mode.Sit) return;
        _decide = true;
        Host.Wake();
    }

    void Decide()
    {
        if (_mode != Mode.Sit || _pressed || _act.Length > 0) return;
        double idle = Now - _lastStir;
        if (idle > (_demo ? DemoSleepAfter : SleepAfter) || (Energy < 0.3 && idle > 15))
        {
            if (Now - _yawnedAt > 20)
            {
                _yawnedAt = Now;
                StartAct("yawn"); // a big yawn first; it lies down at the next thought
            }
            else
            {
                GoToSleep();
            }
            return;
        }

        string kind = Kind;
        if (Excitement > 0.6 && Energy > 0.4 && Rng.NextDouble() < 0.5)
        {
            if (kind == "bunny") Trick(); // a binky is how a happy rabbit lets off steam
            else if (kind is "cat" or "dog" or "fox") Zoomies();
            else Speak(Say.Happy);
            return;
        }

        var toCursor = Host.Pointer - Center;
        if (idle > AttentionAfter && Now - _lastCall > 8 + Rng.NextDouble() * 8)
        {
            // ignored for a while: it comes over and asks for you
            _lastCall = Now;
            Speak(Say.Call);
            if (toCursor.Length > 60) WalkTo(Host.Pointer.X);
            return;
        }
        if (toCursor.Length < NoticeR)
        {
            Notice(kind, toCursor);
            return;
        }

        double roll = Rng.NextDouble();
        if (roll < 0.06) Trick();
        else if (roll < 0.28) StartWalk(Rng.NextDouble() < 0.5 ? -1 : 1, 2 + Rng.NextDouble() * 4);
        else if (roll < 0.46 && TryJumpUp()) { }
        else if (roll < 0.56 && _hwnd != IntPtr.Zero) HopDown();
        else if (roll < 0.84) StartAct(HabitsOf(kind)[Rng.Next(3)]);
        else if (roll < 0.9) Speak(Say.Content, 0.35);
        else if (Rng.NextDouble() < 0.4) _face = -_face; // look around
    }

    /// <summary>The cursor is close: each animal takes an interest in its own way.</summary>
    void Notice(string kind, Vec2 d)
    {
        if (Math.Abs(d.X) > 6) _face = d.X > 0 ? 1 : -1;
        double r = Rng.NextDouble();
        bool level = d.Y > -70 && d.Y < 40, inReach = Math.Abs(d.X) > 50 && Math.Abs(d.X) < 240;
        switch (kind)
        {
            case "cat" or "fox" when level && inReach && r < 0.45:
                _actX = Host.Pointer.X; // crouch, wiggle, then pounce on it
                StartAct("stalk", 1.1 + Rng.NextDouble() * 0.6);
                return;
            case "cat" when d.Y < -90 && r < 0.4:
                StartAct("chatter"); // up there where it cannot get at it
                return;
            case "dog" when r < 0.45:
                StartAct("wag");
                if (Rng.NextDouble() < 0.4) Speak(Say.Hello);
                return;
            case "duck" when r < 0.75:
                WalkTo(Host.Pointer.X); // a duck follows whoever it has decided is its mother
                if (Rng.NextDouble() < 0.3) Speak(Say.Content, 0.4);
                return;
            case "bunny" when r < 0.4:
                StartAct("sniff");
                return;
            case "penguin" when r < 0.2:
                StartAct("call");
                return;
        }
        if (Math.Abs(d.X) > 40 && Rng.NextDouble() < 0.5) WalkTo(Host.Pointer.X);
    }

    void Zoomies()
    {
        StartWalk(Rng.NextDouble() < 0.5 ? -1 : 1, 2 + Rng.NextDouble() * 1.5);
        _speed = ZoomSpeed;
        Speak(Say.Happy, 0.4);
        UpdateHud();
    }

    void GoToSleep()
    {
        _rested = Energy;
        _exertion = 0;
        _act = "";
        _speed = 1;
        _mode = Mode.Sleep;
        _sleptAt = Now;
        _brain.Interval = TimeSpan.FromSeconds(3 + Rng.NextDouble() * 3); // snores for a while, then goes quiet
        RunBrain();
        _art.Zz.Text = L.T("z z");
        Canvas.SetTop(_art.Zz, Math.Max(-46, Host.Arena.Top + 2 - Center.Y)); // closed box: keep the label on screen
        Draw();
        UpdateHud();
    }

    void WakeUp()
    {
        _mode = Mode.Sit;
        _rested = Math.Min(1, _rested + (Now - _sleptAt) / 60); // a minute's nap fills it up
        _awakeSince = Now;
        _brain.Interval = TimeSpan.FromSeconds(2);
        RunBrain();
    }

    // ------------------------------------------------------------------ actions

    /// <summary>Starts one of the pet's actions (a trick or a habit), with its sound.</summary>
    void StartAct(string name, double seconds = 0)
    {
        if (_mode == Mode.Walk) StopWalking();
        _act = name;
        _actT = 0;
        _actLen = seconds > 0 ? seconds : ActLength(name);
        _actAngle = 0;
        string kind = Kind;
        switch (name)
        {
            case "groom": PlayThrottled("lick", 0.22, SizePitch); break;
            case "knead": PlayThrottled("purr", 0.45); break;
            case "sniff": PlayThrottled("sniff", 0.35, SizePitch); break;
            case "pant": PlayThrottled("pant", 0.35, SizePitch); break;
            case "yawn": PlayThrottled("yawn", 0.4, SizePitch * (0.95 + Rng.NextDouble() * 0.1)); break;
            case "thump": PlayThrottled("thump", 0.55); break;
            case "nod": Speak(Say.Content, 0.4); break;
            case "call": Speak(Say.Call, 0.5); break;
            case "chatter": Speak(Say.Hunt, 0.45); break;
            case "puff": Speak(Say.Upset, 0.5); break;
            case "flap": PlayThrottled("flap", 0.35); break;
            case "shake": PlayThrottled(kind is "duck" or "penguin" ? "flap" : "whoosh", 0.25, 1.5); break;
        }
        RunBrain();
        UpdateHud();
        Host.Wake();
    }

    /// <summary>What it does once it has landed after being thrown.</summary>
    void AfterThrow(bool hard)
    {
        switch (Kind)
        {
            case "cat":
                StartAct(hard ? "puff" : "groom"); // an outraged hiss, or grooming as if nothing happened
                break;
            case "bunny":
                StartAct("thump"); // a warning to every other rabbit
                if (hard) Speak(Say.Upset, 0.45);
                break;
            case "dog":
                StartAct("shake");
                Speak(Say.Happy); // again, again!
                Thrill(0.3);
                break;
            default:
                StartAct("shake");
                Speak(hard ? Say.Upset : Say.Happy, 0.5);
                break;
        }
    }

    // ------------------------------------------------------------------ behaviours

    void StartWalk(double dir, double seconds)
    {
        _mode = Mode.Walk;
        _face = dir;
        _walkLeft = seconds;
        _walkTargetX = double.NaN;
        _hopAtEdge = false;
    }

    (double lo, double hi) WalkRange()
    {
        var a = Host.Arena;
        if (_hwnd == IntPtr.Zero) return (a.Left + HalfW, a.Right - HalfW);
        return (Math.Max(_surface.X1 + EdgeIn, a.Left + HalfW), Math.Min(_surface.X2 - EdgeIn, a.Right - HalfW));
    }

    void WalkTo(double x)
    {
        var (lo, hi) = WalkRange();
        x = Clamp(x, lo, hi);
        if (Math.Abs(x - _pos.X) < 8) return;
        StartWalk(x > _pos.X ? 1 : -1, 6);
        _walkTargetX = x;
    }

    void StopWalking()
    {
        _mode = Mode.Sit;
        _walkTargetX = double.NaN;
        _hopAtEdge = false;
        _speed = 1;
    }

    /// <summary>Walk to the nearer real edge of the window top and hop off it.</summary>
    void HopDown()
    {
        var a = Host.Arena;
        bool leftOpen = _surface.X1 > a.Left + HalfW, rightOpen = _surface.X2 < a.Right - HalfW;
        if (!leftOpen && !rightOpen) return;
        double dir = !leftOpen ? 1 : !rightOpen ? -1 : _pos.X - _surface.X1 < _surface.X2 - _pos.X ? -1 : 1;
        StartWalk(dir, 6);
        _hopAtEdge = true;
    }

    bool TryJumpUp()
    {
        var a = Host.Arena;
        var hud = Host.HudBounds.Inflate(20);
        var options = new List<Vec2>();
        foreach (var p in Host.Platforms.Items)
        {
            double rise = _pos.Y - p.Y;
            if (rise < 24 || rise > JumpUp || p.Y - ApexExtra - Height < a.Top + 4) continue;
            double lo = Math.Max(p.X1 + EdgeIn + 8, a.Left + HalfW), hi = Math.Min(p.X2 - EdgeIn - 8, a.Right - HalfW);
            if (hi <= lo) continue;
            double near = Clamp(_pos.X, lo, hi);
            if (Math.Abs(near - _pos.X) > JumpSide) continue;
            double inward = near > _pos.X ? 1 : near < _pos.X ? -1 : Rng.NextDouble() < 0.5 ? -1 : 1;
            double x = Clamp(near + inward * Rng.NextDouble() * 60, lo, hi);
            if (hud.Contains(new Point(x, p.Y - CenterLift))) continue;
            options.Add(new Vec2(x, p.Y));
        }
        if (options.Count == 0) return false;
        JumpTo(options[Rng.Next(options.Count)]);
        return true;
    }

    /// <summary>A ballistic arc that peaks a little above the target window top and comes down onto it.</summary>
    void JumpTo(Vec2 target)
    {
        double up = _pos.Y - target.Y + ApexExtra;
        double vy = -Math.Sqrt(2 * Gravity * up);
        double time = -vy / Gravity + Math.Sqrt(2 * ApexExtra / Gravity);
        double vx = (target.X - _pos.X) / time;
        if (Math.Abs(vx) > 1) _face = vx > 0 ? 1 : -1;
        Drop(new Vec2(vx, vy));
        PlayThrottled("pop", 0.2, 1.9);
    }

    void Hop() => Drop(new Vec2(_face * 150, -260));

    /// <summary>The end of a stalk: a high pounce that comes down on <paramref name="x"/>, on the same window top.</summary>
    void Leap(double x)
    {
        var (lo, hi) = WalkRange();
        x = Clamp(x, lo, hi);
        const double up = 430;
        double vx = Clamp((x - _pos.X) / (2 * up / Gravity), -700, 700);
        if (Math.Abs(vx) > 1) _face = vx > 0 ? 1 : -1;
        Drop(new Vec2(vx, -up));
        _afterLand = Kind == "cat" && Rng.NextDouble() < 0.5 ? "groom" : "sniff"; // got it? let's have a look
        PlayThrottled("whoosh", 0.15, 1.4);
    }

    /// <summary>Leave the ground: jumping, hopping off, thrown, or the window underneath went away.</summary>
    void Drop(Vec2 v)
    {
        if (_mode == Mode.Sleep)
        {
            _lastStir = Now; // the fall woke it up
            WakeUp();
        }
        _mode = Mode.Air;
        _vel = v;
        _hwnd = IntPtr.Zero;
        _walkTargetX = double.NaN;
        _hopAtEdge = _recheck = false;
        RunBrain();
    }

    void Land(IntPtr hwnd, DeskArcade.Engine.Platform plat)
    {
        _mode = Mode.Sit;
        _vel = default;
        _hwnd = hwnd;
        _surface = plat;
        _seenGen = Host.Platforms.Generation;
        bool thrown = _thrown;
        _thrown = false;
        _swing = 0;
        _squashT = SquashTime;
        PlayThrottled("thunk", 0.2, 1.7);
        if (_afterLand.Length > 0)
        {
            string next = _afterLand;
            _afterLand = "";
            StartAct(next);
        }
        else if (thrown)
        {
            AfterThrow(_throwSpeed > HardThrow);
        }
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into) => into.Add(HitShape.Circle(Center - new Vec2(0, 3), HitR));

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_pressed || (p - (Center - new Vec2(0, 3))).Length > HitR) return false;
        if (right)
        {
            Trick();
            return false;
        }
        _pressed = true;
        _pressPos = p;
        return true; // capture until release: a still release pets it, a drag carries it
    }

    public override void PointerUp(Vec2 p)
    {
        if (!_pressed) return;
        _pressed = false;
        if (_mode == Mode.Carried) Throw(ThrowVelocity());
        else Pet();
    }

    void Pet()
    {
        _pets++;
        Host.Stats.Add("pet.pets");
        bool woke = _mode == Mode.Sleep;
        if (woke) WakeUp();
        _lastStir = Now;
        _petStreak = Now - _lastPetAt < 2.5 ? _petStreak + 1 : 1;
        _lastPetAt = Now;
        Thrill(0.12);
        if (_mode == Mode.Walk) StopWalking();
        if (Grounded && !IsTrick(_act)) _act = "";
        if (Math.Abs(Host.Pointer.X - _pos.X) > 6 && _mode == Mode.Sit) _face = Host.Pointer.X > _pos.X ? 1 : -1;

        string kind = Kind;
        if (kind == "cat" && _petStreak >= 9 && _mode == Mode.Sit && Rng.NextDouble() < 0.5)
        {
            // enough is enough: a quick swat, and it walks off
            _petStreak = 0;
            StartAct("swat");
            PlayThrottled("hiss", 0.2, 1.2);
            UpdateHud();
            Host.HudChanged();
            return;
        }

        _happyT = HappyTime;
        var top = Center - new Vec2(0, 26);
        for (int i = 0; i < 5; i++)
            SpawnHeart(top + new Vec2((Rng.NextDouble() - 0.5) * 30, Rng.NextDouble() * 8),
                new Vec2((Rng.NextDouble() - 0.5) * 90, -70 - Rng.NextDouble() * 60), 0.9 + Rng.NextDouble() * 0.5);
        Host.Fx.Burst(Center, Blush, 6, 150, 250, 4, 0.5);
        PlayThrottled("star", 0.3, 1.7 + Rng.NextDouble() * 0.2);

        if (woke) StartAct("yawn"); // a big stretchy yawn, then it is ready to play
        else if (_mode != Mode.Sit) Speak(Say.Hello);
        else if (kind == "cat" && _petStreak >= 3) Speak(Say.Content, 0.5); // keep going and it purrs
        else if (kind == "bunny" && _petStreak >= 5) StartAct("flop");     // a relaxed rabbit flops over on its side
        else if (kind is "dog" or "fox")
        {
            StartAct("wag");
            Speak(Say.Hello);
        }
        else Speak(Say.Hello);
        RunBrain();
        UpdateHud();
        Host.HudChanged(); // the score changed even if the line did not
    }

    void StartCarry()
    {
        if (_mode == Mode.Walk) StopWalking();
        if (_mode == Mode.Sleep) WakeUp();
        _mode = Mode.Carried;
        _hwnd = IntPtr.Zero;
        _thrown = _recheck = false;
        _carryStart = _pos;
        _trail.Clear();
        _lastStir = _carryNag = Now;
        _act = _afterLand = "";
        RunBrain();
        PlayThrottled("pop", 0.3, 1.8);
        Speak(Say.Surprise, 0.45, 1.1); // a surprised little noise
    }

    /// <summary>Starts the animal's trick (right-click, or now and then on its own).</summary>
    void Trick()
    {
        if (_mode is Mode.Air or Mode.Carried || IsTrick(_act)) return;
        if (_mode == Mode.Walk) StopWalking();
        if (_mode == Mode.Sleep) WakeUp();
        _mode = Mode.Sit;
        _lastStir = Now;
        var (name, seconds) = TrickFor(Kind);
        StartAct(name, seconds);
        Host.Stats.Add("pet.tricks");
        if (name == "binky") Drop(new Vec2(_face * 70, -560)); // straight up with a twist
        else if (name == "pounce") _squashT = 0;               // a crouch first, the leap comes after
        Speak(Say.Hello, 0.55, name == "pounce" ? 1.1 : 1);
    }

    /// <summary>Advances the running action; returns true while it runs.</summary>
    bool StepAct(double dt)
    {
        if (_act.Length == 0)
        {
            _actAngle = 0; // the action may have been cut short by a pet or a pick-up
            return false;
        }
        _actT += dt;
        double k = Math.Min(1, _actT / _actLen), on = Ease(k);
        _actAngle = 0;
        switch (_act)
        {
            case "spin":
                _actAngle = 360 * k * _face;
                break;
            case "binky":
                _actAngle = Math.Sin(k * Math.PI * 2) * 25; // a twist in mid-air
                break;
            case "slide":
                if (_mode == Mode.Sit)
                {
                    var (lo, hi) = WalkRange();
                    _pos.X = Clamp(_pos.X + _face * 220 * Math.Sin(Math.PI * k) * dt, lo, hi);
                }
                _actAngle = _face * 72 * Math.Sin(Math.PI * Math.Min(1, k * 1.3));
                break;
            case "sniff": // nose down to the ground
                _actAngle = _face * (10 + Math.Sin(_actT * 20) * 2) * on;
                break;
            case "nod":
                _actAngle = _face * 22 * Math.Abs(Math.Sin(k * Math.PI * 3));
                break;
            case "shake":
                _actAngle = Math.Sin(_actT * 50) * 14 * (1 - k);
                break;
            case "flop": // over on its side
                _actAngle = -_face * 80 * on;
                break;
            case "call": // head back, beak to the sky
                _actAngle = -_face * 28 * on;
                break;
            case "yawn":
                _actAngle = -_face * 10 * Math.Sin(Math.PI * k);
                break;
            case "scratch":
                _actAngle = -_face * 12 * on;
                break;
            case "preen":
                _actAngle = Math.Sin(_actT * 9) * 7 * on;
                break;
            case "stalk":
                if (Math.Abs(_actX - _pos.X) > 4) _face = _actX > _pos.X ? 1 : -1;
                if (k > 0.6) _actAngle = Math.Sin(_actT * 24) * 3; // the wiggle before the pounce
                break;
        }
        if (k < 1) return true;
        string done = _act;
        _act = "";
        _actAngle = 0;
        switch (done)
        {
            case "pounce" when _mode == Mode.Sit:
                Drop(new Vec2(_face * 330, -470));
                break;
            case "stalk" when _mode == Mode.Sit:
                Leap(_actX);
                break;
            case "swat" when _mode == Mode.Sit:
                StartWalk(-_face, 1.2 + Rng.NextDouble());
                break;
        }
        UpdateHud();
        return true;
    }

    /// <summary>0 to 1 over the first 15 % of an action, and back to 0 over the last 15 %.</summary>
    static double Ease(double k) => Math.Clamp(Math.Min(k, 1 - k) / 0.15, 0, 1);

    void Throw(Vec2 v)
    {
        if (v.Length > MaxThrow) v *= MaxThrow / v.Length;
        if ((_pos - _carryStart).Length > CarryCount) Host.Stats.Add("pet.carries");
        Drop(v);
        _thrown = v.Length > 250;
        _throwSpeed = v.Length;
        _exertion += 0.03;
        if (_thrown) Thrill(v.Length > 600 ? 0.25 : 0.1);
        _lastStir = Now;
        if (v.Length > 600) PlayThrottled("whoosh", Math.Min(0.5, v.Length / 4000));
    }

    Vec2 ThrowVelocity()
    {
        if (_trail.Count < 2) return default;
        var last = _trail[^1];
        var first = _trail[0];
        foreach (var s in _trail)
        {
            if (last.t - s.t <= 0.07)
            {
                first = s;
                break;
            }
        }
        double dt = last.t - first.t;
        return dt < 0.008 ? default : (last.p - first.p) / dt;
    }

    public override void Summon(Vec2 p)
    {
        if (_pressed) return;
        var a = Host.Arena;
        _pos = new Vec2(Clamp(p.X, a.Left + HalfW, a.Right - HalfW), Clamp(p.Y + CenterLift, a.Top + Height, a.Bottom));
        _lastStir = Now;
        Drop(default);
        Draw();
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _animT += dt;
        CheckSurface();
        if (_decide)
        {
            _decide = false;
            Decide();
        }
        if (_pressed && _mode != Mode.Carried && (Host.Pointer - _pressPos).Length > DragStart) StartCarry();
        if (_mode == Mode.Carried) Carry(dt);

        if (_mode is Mode.Walk or Mode.Air)
        {
            _acc += dt;
            while (_acc >= Step && (_mode is Mode.Walk or Mode.Air))
            {
                _acc -= Step;
                if (_mode == Mode.Walk) WalkStep(Step);
                else AirStep(Step);
            }
        }
        else
        {
            _acc = 0;
        }

        bool acting = StepAct(dt);
        if (_mode == Mode.Sit && !_pressed && !acting) FaceCursor();
        if (_mode != Mode.Carried) _swing *= Math.Max(0, 1 - dt * 8);
        _happyT = Math.Max(0, _happyT - dt);
        _squashT = Math.Max(0, _squashT - dt);
        bool hearts = UpdateHearts(dt);
        Draw();
        UpdateHud();
        // sitting and sleeping are still: no frames needed until the behaviour timer or the user wakes us
        return _pressed || (_mode is Mode.Walk or Mode.Air or Mode.Carried) || _happyT > 0 || _squashT > 0 || hearts || acting;
    }

    /// <summary>Ride along with the window underneath, or fall when it moved away, closed or got covered.</summary>
    void CheckSurface()
    {
        var plats = Host.Platforms;
        bool changed = plats.Generation != _seenGen;
        _seenGen = plats.Generation;
        if (!Grounded || _hwnd == IntPtr.Zero || (!changed && !_recheck)) return;
        if (changed) _pos += plats.DeltaOf(_hwnd);
        _recheck = false;
        foreach (var p in plats.Items)
        {
            if (p.Hwnd != _hwnd || Math.Abs(p.Y - _pos.Y) > 4 || _pos.X < p.X1 - EdgeIn || _pos.X > p.X2 + EdgeIn) continue;
            if (p.Y - Height < Host.Arena.Top) break; // too close to the top of the screen to fit
            _surface = p;
            _pos.Y = p.Y;
            var (lo, hi) = WalkRange();
            if (lo <= hi) _pos.X = Clamp(_pos.X, lo, hi);
            return;
        }
        Drop(default);
    }

    void WalkStep(double h)
    {
        if (!double.IsNaN(_walkTargetX) && (_walkTargetX - _pos.X) * _face <= 0)
        {
            StopWalking();
            return;
        }
        var a = Host.Arena;
        string kind = Kind;
        double pace = kind == "bunny" ? Math.Sin(Math.PI * HopPhase) * Math.PI / 2 : 1; // a bunny only moves while in the air
        _pos.X += _face * WalkSpeedOf(kind) * _speed * pace * h;
        _exertion += h * _speed / 400;
        if (_hwnd == IntPtr.Zero) _pos.Y = a.Bottom;

        var (lo, hi) = WalkRange();
        if (_pos.X < lo || _pos.X > hi)
        {
            bool atLeft = _pos.X < lo;
            // only a real window edge can be hopped off; where the window meets the screen edge, turn around
            bool windowEdge = _hwnd != IntPtr.Zero &&
                (atLeft ? _surface.X1 + EdgeIn >= a.Left + HalfW : _surface.X2 - EdgeIn <= a.Right - HalfW);
            _pos.X = atLeft ? lo : hi;
            if (windowEdge && (_hopAtEdge || Rng.NextDouble() < 0.3))
            {
                Hop();
                return;
            }
            _face = atLeft ? 1 : -1;
            if (!double.IsNaN(_walkTargetX) || _hopAtEdge)
            {
                StopWalking();
                return;
            }
        }
        if ((_walkLeft -= h) <= 0)
        {
            bool zoomed = _speed > 1;
            StopWalking();
            if (zoomed) StartAct(kind == "dog" ? "pant" : kind == "cat" ? "groom" : "sniff"); // catching its breath
        }
    }

    void AirStep(double h)
    {
        var a = Host.Arena;
        double prevY = _pos.Y;
        _vel.Y += Gravity * h;
        _pos += _vel * h;

        // closed box: bounce off the sides and the top of the screen
        if (_pos.X < a.Left + HalfW)
        {
            _pos.X = a.Left + HalfW;
            if (_vel.X < 0) { WallHit(-_vel.X); _vel.X = -_vel.X * WallBounce; }
        }
        else if (_pos.X > a.Right - HalfW)
        {
            _pos.X = a.Right - HalfW;
            if (_vel.X > 0) { WallHit(_vel.X); _vel.X = -_vel.X * WallBounce; }
        }
        if (_pos.Y - Height < a.Top)
        {
            _pos.Y = a.Top + Height;
            if (_vel.Y < 0) { WallHit(-_vel.Y); _vel.Y = -_vel.Y * WallBounce; }
        }
        if (_vel.Y < 0)
        {
            if (_pos.Y > a.Bottom) _pos.Y = a.Bottom;
            return;
        }

        double ground = double.NaN;
        IntPtr hwnd = IntPtr.Zero;
        DeskArcade.Engine.Platform plat = default;
        if (Host.Platforms.FindLanding(_pos.X, prevY, _pos.Y, out var top) && top.Y - Height >= a.Top)
        {
            ground = top.Y;
            hwnd = top.Hwnd;
            plat = top;
        }
        else if (_pos.Y >= a.Bottom)
        {
            ground = a.Bottom;
        }
        if (double.IsNaN(ground)) return;

        _pos.Y = ground;
        if (_vel.Y > BounceMin || Math.Abs(_vel.X) > SlideMin)
        {
            // a hard landing bounces; a fast sideways one skips along until it slows down
            PlayThrottled("bounce", Math.Min(0.5, (_vel.Y + Math.Abs(_vel.X)) / 4000), 1.5);
            _vel.Y = -Math.Max(_vel.Y * FloorBounce, Math.Abs(_vel.X) * 0.2);
            _vel.X *= 0.55;
            _squashT = SquashTime;
            return;
        }
        Land(hwnd, plat);
    }

    void WallHit(double speed)
    {
        if (speed > 300) PlayThrottled("bounce", Math.Min(0.5, speed / 3000), 1.3);
    }

    void Carry(double dt)
    {
        var a = Host.Arena;
        var hand = _demoHand ?? Host.Pointer;
        _pos = new Vec2(Clamp(hand.X, a.Left + HalfW, a.Right - HalfW), Clamp(hand.Y + DangleY + CenterLift, a.Top + Height, a.Bottom));
        _trail.Add((_animT, _pos));
        while (_trail.Count > 0 && _animT - _trail[0].t > 0.15) _trail.RemoveAt(0);
        // it dangles: the body swings behind the hand
        double lean = Clamp(ThrowVelocity().X * 0.02, -28, 28);
        _swing += (lean - _swing) * Math.Min(1, dt * 10);
        if (Now - _carryNag > 2.2 + Rng.NextDouble() * 1.5)
        {
            _carryNag = Now; // carried around for a while: it wants to be put down
            Speak(Say.Call, 0.4);
        }
    }

    void FaceCursor()
    {
        var d = Host.Pointer - Center;
        if (Math.Abs(d.X) > 10 && d.Length < NoticeR) _face = d.X > 0 ? 1 : -1;
    }

    // ------------------------------------------------------------------ visuals

    const string HeartPath = "M0,5 C-8,-1 -7,-8 -3,-8 C-1.5,-8 -0.4,-7 0,-5.8 C0.4,-7 1.5,-8 3,-8 C7,-8 8,-1 0,5 Z";
    static readonly IBrush HeartFill = Art.Brush("#FF5C8A");
    static readonly IBrush HeartInk = Art.Brush("#B8325A");

    void Draw()
    {
        double bob = 0, sx = 1, sy = 1, gaitAngle = 0, tail = 0, stepA = 0, stepB = 0, frontX = 0, backX = 0;
        string eyes = ""; // an action can close the eyes: "happy" or "sleep"
        if (_mode == Mode.Walk)
        {
            double pace = _animT * Math.Sqrt(_speed), s;
            switch (Kind)
            {
                case "bunny": // hop, hop
                    double hop = Math.Sin(Math.PI * HopPhase);
                    bob = -hop * 9;
                    stepA = stepB = -2 * hop;
                    break;
                case "duck" or "penguin": // a waddle, rocking side to side
                    s = Math.Sin(pace * 8);
                    bob = -Math.Abs(s) * 1.5;
                    gaitAngle = s * (Kind == "penguin" ? 10 : 7);
                    stepA = -2.5 * Math.Max(0, s);
                    stepB = -2.5 * Math.Max(0, -s);
                    break;
                case "dog": // a bouncy trot, tail going
                    s = Math.Sin(pace * 13);
                    bob = -Math.Abs(s) * 3.2;
                    tail = s * 10;
                    stepA = -2.4 * Math.Max(0, s);
                    stepB = -2.4 * Math.Max(0, -s);
                    break;
                default:
                    s = Math.Sin(pace * 11);
                    bob = -Math.Abs(s) * 2.5;
                    tail = Math.Sin(pace * 5.5) * 6;
                    stepA = -2.2 * Math.Max(0, s);
                    stepB = -2.2 * Math.Max(0, -s);
                    break;
            }
        }
        else if (_mode == Mode.Carried)
        {
            double s = Math.Sin(_animT * 16);
            stepA = 2 * s;
            stepB = -2 * s;
        }
        else if (_mode == Mode.Sleep)
        {
            sy = 0.9; // curled up
            sx = 1.06;
        }
        if (_happyT > 0)
        {
            double k = 1 - _happyT / HappyTime;
            bob -= Math.Sin(Math.PI * Math.Min(1, k / 0.7)) * 14;
        }
        if (_squashT > 0)
        {
            double k = _squashT / SquashTime;
            sy = 1 - 0.18 * k;
            sx = 1 + 0.12 * k;
        }
        else if (_mode == Mode.Carried)
        {
            sy = 1.06;
            sx = 0.95;
        }
        if (_act.Length > 0)
        {
            double k = Math.Min(1, _actT / _actLen), env = Math.Sin(Math.PI * k), on = Ease(k), t = _actT, c;
            switch (_act)
            {
                case "stretch": // long and low, then back
                    sx = 1 + 0.3 * env;
                    sy = 1 - 0.2 * env;
                    break;
                case "flap": // three little flaps up and down
                    bob -= Math.Abs(Math.Sin(k * Math.PI * 3)) * 11;
                    sx = 1 + 0.08 * Math.Abs(Math.Sin(k * Math.PI * 6));
                    break;
                case "pounce": // crouch down before the leap
                    sy = 1 - 0.28 * Math.Min(1, k * 1.4);
                    sx = 1 + 0.16 * Math.Min(1, k * 1.4);
                    break;
                case "slide": // flat on the belly
                    sy = 1 - 0.25 * env;
                    break;
                case "groom": // a paw up to the mouth, licked in little strokes
                    stepB = on * (-11 + Math.Sin(t * 10) * 2);
                    frontX = -2 * on;
                    bob += Math.Sin(t * 10) * 0.8 * on;
                    eyes = "happy";
                    break;
                case "knead": // making biscuits
                    stepA = -3 * Math.Max(0, Math.Sin(t * 8));
                    stepB = -3 * Math.Max(0, -Math.Sin(t * 8));
                    bob -= Math.Abs(Math.Sin(t * 8)) * 0.6;
                    eyes = "happy";
                    break;
                case "sniff":
                    bob += Math.Sin(t * 20) * 0.8 * on;
                    break;
                case "scratch": // a back foot up to the ear, going like mad
                    stepA = on * (-13 + Math.Sin(t * 32) * 3);
                    backX = -3 * on;
                    eyes = "happy";
                    break;
                case "wag":
                    tail = Math.Sin(t * 26) * 24 * on;
                    bob -= Math.Abs(Math.Sin(t * 13)) * 1.2;
                    if (k < 0.6) eyes = "happy";
                    break;
                case "pant":
                    bob += Math.Sin(t * 30) * 1.2;
                    sy = 1 + 0.025 * Math.Sin(t * 30);
                    break;
                case "preen":
                    stepB = -4 * on;
                    eyes = "happy";
                    break;
                case "shake":
                    sx = 1 + 0.06 * Math.Abs(Math.Sin(t * 50)) * (1 - k);
                    tail = Math.Sin(t * 50) * 20 * (1 - k);
                    break;
                case "flop":
                    sy = 1 - 0.1 * on;
                    eyes = "happy";
                    break;
                case "call": // stretched tall, braying
                    sy = 1 + 0.18 * on;
                    sx = 1 - 0.08 * on;
                    bob -= Math.Abs(Math.Sin(t * 9)) * 1.5 * on;
                    break;
                case "yawn": // stretched up tall, eyes squeezed shut
                    sy = 1 + 0.12 * env;
                    sx = 1 - 0.06 * env;
                    eyes = "sleep";
                    break;
                case "stalk": // low to the ground, tail tip twitching
                    c = Math.Min(1, k * 3);
                    sy = 1 - 0.2 * c;
                    sx = 1 + 0.12 * c;
                    tail = Math.Sin(t * 14) * 9;
                    break;
                case "chatter": // up on its toes, jaw going
                    sy = 1.05;
                    bob += Math.Sin(t * 55) * 0.8;
                    tail = Math.Sin(t * 12) * 8;
                    break;
                case "puff": // fur on end
                    c = Math.Min(1, k * 6) * (k > 0.8 ? (1 - k) / 0.2 : 1);
                    sx = sy = 1 + 0.18 * c;
                    tail = -25 * c;
                    break;
                case "thump": // one back foot slammed down
                    if (k < 0.3) stepA = -7 * Math.Sin(Math.PI * k / 0.3);
                    if (k > 0.25 && k < 0.45) sy = 1 - 0.1 * Math.Sin(Math.PI * (k - 0.25) / 0.2);
                    break;
                case "swat":
                    stepB = -12 * env;
                    frontX = 7 * env;
                    break;
            }
        }

        _art.Root.Set(Center, _swing + _actAngle + gaitAngle);
        _art.BodyScale.ScaleX = _face * sx;
        _art.BodyScale.ScaleY = sy;
        _art.BodyShift.Y = bob + CenterLift * (1 - sy); // squash toward the feet, not the middle
        _art.Tail.Angle = tail;
        _art.FootA.X = backX;
        _art.FootA.Y = stepA;
        _art.FootB.X = frontX;
        _art.FootB.Y = stepB;

        bool sleeping = _mode == Mode.Sleep || eyes == "sleep", happy = !sleeping && (_happyT > 0 || eyes == "happy");
        _art.EyesSleep.IsVisible = sleeping;
        _art.EyesHappy.IsVisible = happy;
        _art.EyesOpen.IsVisible = !sleeping && !happy;
        _art.Zz.IsVisible = _mode == Mode.Sleep;
        _art.Shadow.IsVisible = Grounded;
        if (sleeping || happy) return;

        var d = Host.Pointer - (Center + new Vec2(_face * 2.5, -3 + bob));
        double len = d.Length;
        var look = len < 1 ? default : d * (Math.Min(1, len / 60) * PupilReach / len);
        _art.PupilL.X = _art.PupilR.X = look.X * _face; // the body canvas is mirrored when facing left
        _art.PupilL.Y = _art.PupilR.Y = look.Y;
    }

    void SpawnHeart(Vec2 p, Vec2 v, double life)
    {
        if (_hearts.Count >= 16) return;
        Heart h;
        if (_heartPool.Count > 0)
        {
            h = _heartPool.Pop();
            h.El.IsVisible = true;
        }
        else
        {
            var tr = new TranslateTransform();
            var sc = new ScaleTransform(0.01, 0.01);
            var el = Art.PathOf(HeartPath, HeartFill, HeartInk, 0.8);
            el.IsHitTestVisible = false;
            el.RenderTransformOrigin = RelativePoint.TopLeft;
            el.RenderTransform = new TransformGroup { Children = { sc, tr } };
            _heartLayer.Children.Add(el);
            h = new Heart { El = el, Tr = tr, Sc = sc };
        }
        h.P = p;
        h.V = v;
        h.Age = 0;
        h.Life = life;
        h.Phase = Rng.NextDouble() * 6;
        h.Sc.ScaleX = h.Sc.ScaleY = 0.01;
        h.El.Opacity = 1;
        _hearts.Add(h);
    }

    bool UpdateHearts(double dt)
    {
        var a = Host.Arena;
        for (int i = _hearts.Count - 1; i >= 0; i--)
        {
            var h = _hearts[i];
            h.Age += dt;
            if (h.Age >= h.Life)
            {
                h.El.IsVisible = false;
                _hearts.RemoveAt(i);
                _heartPool.Push(h);
                continue;
            }
            h.V.X *= Math.Max(0, 1 - dt * 2);
            h.P += h.V * dt;
            h.P.X = Clamp(h.P.X, a.Left + 10, a.Right - 10);
            h.P.Y = Math.Max(h.P.Y, a.Top + 10); // closed box: hearts gather under the top edge
            double k = h.Age / h.Life;
            double s = Math.Min(1, h.Age / 0.15) * (0.85 + 0.15 * Math.Sin(h.Age * 12 + h.Phase));
            h.Sc.ScaleX = h.Sc.ScaleY = Math.Max(0.01, s);
            h.Tr.X = h.P.X + Math.Sin(h.Age * 5 + h.Phase) * 4;
            h.Tr.Y = h.P.Y;
            h.El.Opacity = k < 0.6 ? 1 : 1 - (k - 0.6) / 0.4;
        }
        return _hearts.Count > 0;
    }

    void PlayThrottled(string name, double vol, double pitch = 1)
    {
        double now = Now;
        double gap = name switch // long calls are not stacked on top of themselves
        {
            "bray" => 3, "purr" or "pant" or "snore" => 1.5, "whine" or "yawn" or "scream" or "quacks" or "meow" => 0.9, _ => 0.08,
        };
        if (_lastSound.TryGetValue(name, out double t) && now - t < gap) return;
        _lastSound[name] = now;
        Host.Sound.Play(name, vol, pitch);
    }

    /// <summary>A round cat-like blob facing +x, origin at the body center, feet at y = CenterLift.</summary>
    static PetArt BuildPet(string kind)
    {
        bool dog = kind == "dog", duck = kind == "duck", bunny = kind == "bunny", penguin = kind == "penguin", fox = kind == "fox";
        var ink = Art.Brush(dog ? "#5E3B22" : duck ? "#B7791F" : bunny ? "#7D7068" : penguin ? "#151A22" : fox ? "#7A3A12" : "#8A4F2A");
        var fur = Art.Brush(dog ? "#C98B55" : duck ? "#FFD54A" : bunny ? "#E6E1DC" : penguin ? "#2E3645" : fox ? "#E8742A" : "#F2A566");
        var innerEar = Art.Brush("#FF9FB0");
        var eyeInk = Art.Brush("#2B2320");
        var orange = Art.Brush("#FF8C1A");
        var s = new Sprite();

        var shadow = Art.At(new Ellipse { Width = 32, Height = 6, Fill = Art.Brush(50, 0, 0, 0) }, -16, CenterLift - 3);
        s.Children.Insert(0, shadow);

        var bodyScale = new ScaleTransform(1, 1);
        var bodyShift = new TranslateTransform();
        var body = new Canvas
        {
            RenderTransformOrigin = RelativePoint.TopLeft,
            RenderTransform = new TransformGroup { Children = { bodyScale, bodyShift } },
        };
        s.Rotor.Children.Add(body);

        // tail and ears first, so the body covers their roots; the tail turns about its root to wag and twitch
        var (tailX, tailY) = bunny ? (-19.0, 6.0) : penguin ? (-16.0, 10.0) : fox ? (-16.0, 8.0) : duck ? (-17.0, 7.0) : dog ? (-17.0, 6.0) : (-16.0, 9.0);
        var tailTurn = new RotateTransform { CenterX = tailX, CenterY = tailY };
        var tailBox = new Canvas { RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = tailTurn };
        body.Children.Add(tailBox);
        if (bunny)
        {
            tailBox.Children.Add(Art.Circle(-19, 6, 5.5, Brushes.White, ink, 1.1)); // cotton tail
            body.Children.Add(Art.PathOf("M-9,-13 C-14,-30 -11,-38 -6,-36 C-2,-34 -2,-24 -3,-14 Z", fur, ink, 1.3));
            body.Children.Add(Art.PathOf("M5,-14 C6,-30 11,-38 15,-34 C18,-30 13,-21 11,-12 Z", fur, ink, 1.3));
            body.Children.Add(Art.PathOf("M-7.5,-16 C-10,-27 -8.5,-32 -6,-31 C-4.5,-29 -4.5,-23 -5,-16 Z", innerEar));
            body.Children.Add(Art.PathOf("M7,-15 C8,-27 11,-31 13,-29 C14,-26 11,-20 9.5,-14 Z", innerEar));
        }
        else if (penguin)
        {
            tailBox.Children.Add(Art.PathOf("M-16,8 L-24,13 L-15,13 Z", fur, ink, 1.1)); // stubby tail
            body.Children.Add(Art.PathOf("M-18,-4 C-26,2 -25,10 -19,12", null, ink, 5)); // flipper
        }
        else if (fox)
        {
            const string tail = "M-16,8 C-30,10 -36,-4 -30,-15";
            tailBox.Children.Add(Art.PathOf(tail, null, ink, 10));
            tailBox.Children.Add(Art.PathOf(tail, null, fur, 7.5));
            tailBox.Children.Add(Art.Circle(-30, -15, 4, Brushes.White)); // white tip
            body.Children.Add(Art.PathOf("M-16,-6 L-13,-27 L-3,-15 Z", fur, ink, 1.3));
            body.Children.Add(Art.PathOf("M5,-15 L16,-27 L18,-6 Z", fur, ink, 1.3));
            body.Children.Add(Art.PathOf("M-14.5,-19 L-13,-27 L-9,-22 Z", Art.Brush("#3A1A08"))); // dark ear tips
            body.Children.Add(Art.PathOf("M12.5,-23 L16,-27 L16.8,-19 Z", Art.Brush("#3A1A08")));
        }
        else if (duck)
        {
            tailBox.Children.Add(Art.PathOf("M-17,4 L-27,-3 L-24,6 L-29,9 L-17,11 Z", fur, ink, 1.2)); // tail feathers
            body.Children.Add(Art.PathOf("M1,-16 C-1,-24 4,-26 6,-21 C7,-26 12,-25 10,-17", fur, ink, 1.2)); // tuft
        }
        else if (dog)
        {
            const string tail = "M-17,6 C-26,4 -28,-6 -24,-11";
            tailBox.Children.Add(Art.PathOf(tail, null, ink, 7));
            tailBox.Children.Add(Art.PathOf(tail, null, fur, 4.2));
        }
        else
        {
            const string tail = "M-16,9 C-29,9 -33,-4 -26,-14";
            tailBox.Children.Add(Art.PathOf(tail, null, ink, 6.5));
            tailBox.Children.Add(Art.PathOf(tail, null, fur, 3.8));
            body.Children.Add(Art.PathOf("M-16,-6 L-14,-25 L-3,-15 Z", fur, ink, 1.3));
            body.Children.Add(Art.PathOf("M5,-15 L15,-25 L18,-6 Z", fur, ink, 1.3));
            body.Children.Add(Art.PathOf("M-12.5,-11 L-12.5,-20 L-7,-14.5 Z", innerEar));
            body.Children.Add(Art.PathOf("M9,-14.5 L14,-20 L15,-10 Z", innerEar));
        }

        var furFill = new RadialGradientBrush
        {
            GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
            Center = new RelativePoint(0.45, 0.42, RelativeUnit.Relative),
        };
        string[] shades = dog ? new[] { "#F0D2B0", "#D39A63", "#A8703F" } : duck ? new[] { "#FFF6C2", "#FFD84D", "#F2B200" }
            : bunny ? new[] { "#FFFFFF", "#ECE7E2", "#C9C1B9" } : penguin ? new[] { "#5A6478", "#2E3645", "#191E28" }
            : fox ? new[] { "#FFC08A", "#EF8436", "#C4561A" } : new[] { "#FFE3C2", "#F7B37A", "#E08A4E" };
        furFill.GradientStops.Add(new GradientStop(Color.Parse(shades[0]), 0));
        furFill.GradientStops.Add(new GradientStop(Color.Parse(shades[1]), 0.6));
        furFill.GradientStops.Add(new GradientStop(Color.Parse(shades[2]), 1));
        body.Children.Add(Art.At(new Ellipse { Width = 40, Height = 34, Fill = furFill, Stroke = ink, StrokeThickness = 1.3 }, -20, -17));
        if (penguin) body.Children.Add(Art.At(new Ellipse { Width = 27, Height = 27, Fill = Art.Brush("#F4F6F8") }, -10, -12)); // white front
        else if (fox) body.Children.Add(Art.At(new Ellipse { Width = 24, Height = 15, Fill = Art.Brush(235, 255, 248, 238) }, -8, 1)); // white chin
        else body.Children.Add(Art.At(new Ellipse { Width = 22, Height = 12, Fill = Art.Brush(190, 255, 244, 228) }, -8, 3));
        body.Children.Add(Art.At(new Ellipse { Width = 9, Height = 5, Fill = Art.Brush(120, 255, 255, 255), RenderTransform = new RotateTransform(-30) }, -14, -12));

        var blush = Art.Brush(110, 255, 110, 150);
        body.Children.Add(Art.At(new Ellipse { Width = 7, Height = 4, Fill = blush }, -12.5, 3));
        body.Children.Add(Art.At(new Ellipse { Width = 7, Height = 4, Fill = blush }, 10.5, 3));
        if (duck) body.Children.Add(Art.PathOf("M-1,1 L22,3 L-1,8 Z", orange, Art.Brush("#C2610C"), 1.1)); // beak
        else if (penguin) body.Children.Add(Art.PathOf("M0,1 L13,3.5 L0,6.5 Z", orange, Art.Brush("#C2610C"), 1)); // small beak
        else
        {
            if (bunny) body.Children.Add(Art.PathOf("M0.5,0.5 L4.5,0.5 L2.5,3 Z", Art.Brush("#FF8FA8"))); // pink nose
            if (fox) body.Children.Add(Art.At(new Ellipse { Width = 5, Height = 4, Fill = Art.Brush("#2B2320") }, 0.5, 0)); // nose
            body.Children.Add(Art.PathOf("M-1,3.5 Q0.75,6 2.5,3.5 Q4.25,6 6,3.5", null, Art.Brush("#5A3320"), 1.1));
        }
        if (dog)
        {
            body.Children.Add(Art.At(new Ellipse { Width = 7, Height = 5, Fill = Art.Brush("#2B2320") }, -1, -1)); // nose
            // floppy ears hang over the sides of the head
            body.Children.Add(Art.PathOf("M-15,-13 C-24,-12 -24,2 -18,5 C-15,1 -13,-6 -12,-12 Z", Art.Brush("#7A4A2A"), ink, 1.2));
            body.Children.Add(Art.PathOf("M14,-13 C23,-12 23,2 17,5 C14,1 12,-6 11,-12 Z", Art.Brush("#7A4A2A"), ink, 1.2));
        }

        var eyes = new Canvas();
        var pupilL = new TranslateTransform();
        var pupilR = new TranslateTransform();
        foreach (var (x, tr) in new[] { (-4.0, pupilL), (9.0, pupilR) })
        {
            eyes.Children.Add(Art.At(new Ellipse { Width = 8.6, Height = 9.8, Fill = Brushes.White, Stroke = eyeInk, StrokeThickness = 0.9 }, x - 4.3, -7.9));
            var pupil = new Canvas { RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = tr };
            pupil.Children.Add(Art.Circle(x, -2.6, 2.4, eyeInk));
            pupil.Children.Add(Art.Circle(x - 0.9, -3.6, 0.85, Brushes.White));
            eyes.Children.Add(pupil);
        }
        body.Children.Add(eyes);
        var eyesSleep = Art.PathOf("M-7.5,-3 Q-4,0 -0.5,-3 M5.5,-3 Q9,0 12.5,-3", null, eyeInk, 1.4);
        var eyesHappy = Art.PathOf("M-7.5,-2 Q-4,-6.5 -0.5,-2 M5.5,-2 Q9,-6.5 12.5,-2", null, eyeInk, 1.5);
        eyesSleep.IsVisible = eyesHappy.IsVisible = false;
        body.Children.Add(eyesSleep);
        body.Children.Add(eyesHappy);

        var paw = duck || penguin ? orange : Art.Brush(dog ? "#F0D2B0" : bunny ? "#FFFFFF" : fox ? "#3A1A08" : "#FFE7CC");
        var footA = new TranslateTransform();
        var footB = new TranslateTransform();
        body.Children.Add(Art.At(new Ellipse { Width = 11, Height = 7, Fill = paw, Stroke = ink, StrokeThickness = 1, RenderTransform = footA }, -13.5, 13));
        body.Children.Add(Art.At(new Ellipse { Width = 11, Height = 7, Fill = paw, Stroke = ink, StrokeThickness = 1, RenderTransform = footB }, 2.5, 13));

        // outside the mirrored body, so the text never reads backwards
        var zz = new TextBlock
        {
            Text = L.T("z z"), FontFamily = Fx.Font, FontSize = 14, FontWeight = FontWeight.Bold,
            Foreground = Art.Brush("#8FA3D1"), IsVisible = false,
        };
        s.Children.Add(Art.At(zz, 8, -46));

        return new PetArt
        {
            Root = s, BodyScale = bodyScale, BodyShift = bodyShift, PupilL = pupilL, PupilR = pupilR, FootA = footA, FootB = footB, Tail = tailTurn,
            EyesOpen = eyes, EyesSleep = eyesSleep, EyesHappy = eyesHappy, Shadow = shadow, Zz = zz,
        };
    }

    // ------------------------------------------------------------------ demo

    public override void DemoTick()
    {
        _demo = true;
        if (_demoHand is Vec2 hand)
        {
            if (_demoTicks-- > 0)
            {
                _demoHand = hand + _demoStep; // carry it a short way, like a drag would
                return;
            }
            _demoHand = null;
            _pressed = false;
            Throw(new Vec2(_demoStep.X * 14, -300 - Rng.NextDouble() * 700));
            return;
        }
        if (_pressed || _mode is Mode.Air or Mode.Carried) return;
        if (_mode == Mode.Sleep)
        {
            if (Now - _sleptAt > 4) Pet();
            return;
        }

        double roll = Rng.NextDouble();
        if (roll < 0.012)
        {
            Pet();
        }
        else if (roll < 0.016)
        {
            Trick();
        }
        else if (roll < 0.02)
        {
            _pressed = true;
            _demoHand = Center - new Vec2(0, DangleY);
            _demoTicks = 4;
            _demoStep = new Vec2((Rng.NextDouble() < 0.5 ? -1 : 1) * (15 + Rng.NextDouble() * 20), -(12 + Rng.NextDouble() * 14));
            StartCarry();
        }
        // otherwise the behaviour timer lets it roam
    }
}
