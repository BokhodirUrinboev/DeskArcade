using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Desktop Pet: a little animal that lives on the taskbar and the tops of your windows. It wanders,
/// jumps between windows, naps when ignored and loves being petted. Click to pet it, drag to carry and throw it,
/// right-click for its trick. Each of the twelve animals has its own voices, its own trick (see <see cref="TrickFor"/>),
/// its own gait and its own habits (see <see cref="HabitsOf"/>): cats groom, knead and stalk the cursor, dogs sniff,
/// scratch and wag, hamsters stuff their cheeks, turtles hide in their shells, parrots bob and shriek, frogs catch flies,
/// owls swivel their heads, dragons puff smoke. The owl and the parrot fly between windows with their wings out.
/// It has moods that build up over time: it gets excited when played with (zoomies), bored when ignored
/// (it calls for you), and tired the longer it is awake or the more it runs (it yawns, then naps), and it is drowsier
/// late at night. Once it has been petted twice a ball appears: throw it and the pet fetches it (each animal in its own
/// way; some only watch). A treat jar sits at the end of the taskbar: click it to toss a treat, and a hungry pet begs at
/// the jar. When the cursor rests for a while the pet comes to sit on the window nearest it. Over the LAN a co-worker's
/// pet visits as a faded ghost, and the two greet when they meet. A thought bubble shows what is on its mind.
/// There is no score to chase, so it is built to sit perfectly still (zero CPU) most of the time.
/// </summary>
public sealed class PetGame : MiniGame
{
    const double Step = 1.0 / 240, Gravity = 1800, HalfW = 22, Height = 46, CenterLift = 20;
    const double HitR = 27, DragStart = 6, CarryCount = 30, DangleY = 18, MaxThrow = 2600;
    const double JumpSide = 280, JumpUp = 240, ApexExtra = 40, EdgeIn = 6;
    const double WallBounce = 0.5, FloorBounce = 0.4, BounceMin = 450, SlideMin = 350;
    const double SleepAfter = 60, NightSleepAfter = 30, DemoSleepAfter = 14, AttentionAfter = 25, NoticeR = 200;
    const double HappyTime = 0.55, SquashTime = 0.22, PupilReach = 1.6;
    const double SnoreFor = 120, HardThrow = 1100, ZoomSpeed = 3;
    const double GlideGravity = 0.42;                                         // flyers fall this much slower with their wings out
    const double FlyUp = 2.4, FlySide = 2.2;                                  // how much further a flyer can jump, as a multiple
    // the ball
    const double BallR = 7, BallHitR = 13, BallBounce = 0.55, BallStop = 18, GrabReach = 16, MaxBallThrow = 2200;
    const int BallAfterPets = 2;
    const double ChaseGiveUp = 16, ReturnReach = 34;
    // treats and the jar
    const double TreatR = 5, TreatBegAfter = 480, DemoTreatBegAfter = 20, BegAgain = 45, BegFor = 25, TreatReach = 14, JarW = 24, JarH = 28;
    // goals (a place to get to), company and visits
    const double GoalGiveUp = 22, GoalStep = 0.35, CompanyAfter = 40, CompanyReach = 320, CompanyBreak = 60, CompanyRetry = 30;
    const double VisitR = 60, VisitFade = 1.5, VisitSendEvery = 0.1, GreetAgain = 12;

    static readonly Color[] Blush = { Color.FromRgb(255, 120, 160), Color.FromRgb(255, 200, 220), Colors.White };
    static readonly Color[] Crumbs = { Color.FromRgb(241, 222, 184), Color.FromRgb(176, 138, 90), Color.FromRgb(120, 90, 55) };
    static readonly Color[] Fire = { Color.FromRgb(255, 140, 26), Color.FromRgb(232, 60, 40), Color.FromRgb(255, 214, 64) };
    static readonly Color Smoke = Color.FromArgb(150, 160, 160, 170);

    /// <summary>What the pet is doing with its body; it crosses the LAN so a visiting pet moves the same way.</summary>
    public enum Mode { Sit, Walk, Air, Carried, Sleep }

    /// <summary>What the pet wants to say; each animal answers with its own calls (see <see cref="VoicesOf"/>).</summary>
    public enum Say { Hello, Surprise, Call, Happy, Upset, Content, Hunt }

    /// <summary>How an animal takes to a thrown ball (see <see cref="FetchStyleOf"/>).</summary>
    public enum FetchStyle { Fetch, Sometimes, Fly, Push, Hop, Watch }

    /// <summary>What is in the thought bubble above its head.</summary>
    public enum Bubble { None, Heart, Sleepy, Ball, Treat, Alarm }

    enum ThingState { Hidden, Resting, Air, Held, Carried }

    /// <summary>A co-worker's pet as it arrives over the LAN: kind, where it is (fractions of their arena), and its pose.</summary>
    public readonly record struct Visit(string Kind, double X, double Y, int Face, Mode Mode, string Act);

    /// <summary>How long each of the pet's little actions lasts, in seconds (tricks are in <see cref="TrickFor"/>).</summary>
    static readonly Dictionary<string, double> ActSeconds = new()
    {
        ["groom"] = 2.2, ["knead"] = 2.2, ["sniff"] = 1.4, ["scratch"] = 1.4, ["wag"] = 1.4, ["pant"] = 1.8, ["preen"] = 2.0,
        ["nod"] = 1.1, ["shake"] = 0.6, ["flop"] = 3.0, ["call"] = 2.9, ["yawn"] = 1.3, ["stalk"] = 1.3, ["chatter"] = 1.1,
        ["puff"] = 1.0, ["thump"] = 0.7, ["swat"] = 0.35,
        // the new animals' habits
        ["stuff"] = 1.8, ["circle"] = 2.0, ["hide"] = 2.6, ["neck"] = 1.8, ["nibble"] = 1.6, ["bob"] = 1.4, ["shriek"] = 1.0,
        ["throat"] = 1.6, ["fly"] = 0.9, ["blink"] = 0.5, ["swivel"] = 1.6, ["fluff"] = 1.2, ["slowblink"] = 1.4, ["smoke"] = 1.5,
        ["curl"] = 3.0,
        // shared: eating a treat, the morning stretch, begging at the jar, settling down for company
        ["eat"] = 1.2, ["morning"] = 2.6, ["beg"] = 1.6, ["settle"] = 1.0,
    };

    /// <summary>The words a parrot shouts after its loop.</summary>
    static readonly string[] ParrotWords = { "Hello!", "Pretty bird!", "Cracker?" };

    /// <summary>The pet's drawing. The body canvas faces +x and is flipped/squashed as a whole; the head can turn and shift on its own.</summary>
    sealed class PetArt
    {
        public required Sprite Root;
        public required ScaleTransform BodyScale, HeadScale;
        public required TranslateTransform BodyShift, HeadShift;
        public required RotateTransform HeadTurn;
        public required TranslateTransform PupilL, PupilR, FootA, FootB;
        public required RotateTransform Tail;
        public required Control EyesOpen, EyesSleep, EyesHappy, Shadow, Nightcap;
        public required TextBlock Zz;
        public required Vec2 HeadBase;
        public ScaleTransform? Cheeks, Throat, Tongue, WingSpread;
        public RotateTransform? WingFlap, WingFlapL;
        public Control? TongueEl, Wings;
    }

    sealed class Heart
    {
        public required Path El;
        public required TranslateTransform Tr;
        public required ScaleTransform Sc;
        public Vec2 P, V;
        public double Age, Life, Phase;
    }

    /// <summary>The ball or a treat: a small round thing with its own bit of physics in the same closed box.</summary>
    sealed class Thing
    {
        public required Sprite Sprite;
        public required Control Shadow;
        public required double R;
        public ThingState State = ThingState.Hidden;
        public Vec2 P, V;
        public IntPtr Hwnd;
        public double Spin;
        public bool Thrown, Rolling;
    }

    /// <summary>One frame of body language, from the mode and the running action; a visiting pet is posed the same way.</summary>
    struct Pose
    {
        public double Bob, Sx, Sy, Angle, Tail, StepA, StepB, FrontX, BackX, HeadX, HeadY, HeadAngle, HeadScale, Cheeks, Throat, Tongue, Wings, Flap;
        public string Eyes; // an action can close the eyes: "happy" or "sleep"

        public static Pose Rest => new() { Sx = 1, Sy = 1, HeadScale = 1, Cheeks = 1, Throat = 1, Eyes = "" };
    }

    PetArt _art;
    readonly Canvas _visitLayer = new() { IsHitTestVisible = false };
    readonly Canvas _thingLayer = new() { IsHitTestVisible = false };
    readonly Canvas _heartLayer = new() { IsHitTestVisible = false };
    readonly List<Heart> _hearts = new();
    readonly Stack<Heart> _heartPool = new();
    readonly List<(double t, Vec2 p)> _trail = new(), _ballTrail = new();
    readonly Dictionary<string, double> _lastSound = new();
    readonly DispatcherTimer _brain = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Thing _ball, _treat;
    readonly Thing[] _things; // the two, for the per-frame loops
    readonly Sprite _jar, _fly;
    readonly Canvas _bubbleBox = new() { IsHitTestVisible = false, IsVisible = false };
    readonly TranslateTransform _bubbleAt = new();
    readonly ScaleTransform _bubbleFlip = new(1, 1);
    readonly Dictionary<Bubble, Control> _bubbleItems = new();
    readonly TextBlock _visitLabel = new()
    {
        FontFamily = Fx.Font, FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Brushes.White, IsHitTestVisible = false, IsVisible = false,
    };

    Mode _mode = Mode.Sit;
    Vec2 _pos, _vel, _pressPos, _carryStart, _demoStep, _jarPos, _flyP, _pointerAt, _companyPointer, _goalP;
    Vec2? _demoHand;
    IntPtr _hwnd, _goalHwnd;
    DeskArcade.Engine.Platform _surface;
    int _seenGen = -1, _thingGen = -1, _pets, _demoTicks, _goalStuck;
    double _face = 1, _walkLeft, _walkTargetX = double.NaN, _acc, _thingAcc, _animT, _happyT, _squashT, _swing;
    double _lastStir, _sleptAt;
    bool _placed, _active, _pressed, _recheck, _recheckThings, _decide, _hopAtEdge, _thrown, _demo, _night;
    string _shownLine = "";
    HudState _hudState;
    string _kind = "cat";
    string _act = "", _afterLand = "";       // the running action (a trick or a habit), and one to do on landing
    double _actT, _actLen, _actX, _actMark;
    double _speed = 1;                       // walking speed factor: more than 1 during zoomies
    double _rested = 1, _awakeSince, _exertion, _excite, _excitedAt;
    double _lastCall, _lastPetAt, _yawnedAt = -99, _carryNag, _lastVoice = -99, _throwSpeed, _wingK;
    int _petStreak;
    // the ball and fetching
    string _fetch = "";                      // "", "chase" or "return"
    double _fetchSince;
    bool _ballHeld, _watchBall, _wantsPlay, _flyShown;
    double _wantsPlayAt = -99;
    // treats
    double _lastTreat, _lastBeg = -99, _begSince;
    bool _begging, _treatWanted;
    // a place to get to: the ball, a treat, the jar, or a window to keep you company from
    string _goal = "";
    double _goalReach, _goalSince, _goalStep;
    bool _company;
    double _pointerMovedAt, _companyTriedAt = -99;
    // time of day
    DateOnly _greetedOn;
    bool _morning;
    // the thought bubble
    Bubble _bubble;
    double _bubbleT, _bubbleLife;
    // a co-worker's pet on a visit
    PetArt? _visitArt;
    string _visitKind = "";
    Visit _visit;
    Vec2 _visitPos;
    double _visitSeen = VisitFade, _visitActAt, _visitAnimT, _lanSendT, _lastGreet = -99;
    bool _visiting, _metThisVisit;

    public PetGame(IGameHost host) : base(host)
    {
        RefreshKind();
        _art = BuildPet(Kind);
        _art.Root.IsHitTestVisible = false;
        Layer.Children.Add(_visitLayer);
        Layer.Children.Add(_art.Root);
        Layer.Children.Add(_thingLayer);
        Layer.Children.Add(_heartLayer);
        _visitLayer.Children.Add(_visitLabel);
        _jar = BuildJar();
        _thingLayer.Children.Add(_jar);
        _ball = BuildBall();
        _treat = BuildTreat();
        _things = new[] { _ball, _treat };
        _fly = BuildFly();
        _fly.IsVisible = false;
        _thingLayer.Children.Add(_fly);
        BuildBubble();
        _heartLayer.Children.Add(_bubbleBox);
        _brain.Tick += (_, _) => Think();
    }

    public override string Id => "pet";
    public override string Title => "Desktop Pet";
    public override bool SupportsLan => true;

    /// <summary>Settings.PetKind values.</summary>
    public static readonly string[] Kinds = { "cat", "dog", "duck", "bunny", "penguin", "fox", "hamster", "turtle", "parrot", "frog", "owl", "dragon" };

    string Kind => _kind;

    /// <summary>Reads the kind from the settings: at the start, in <see cref="Rebuild"/> (the tray changed it) and on <see cref="Layout"/>.</summary>
    void RefreshKind() => _kind = Array.IndexOf(Kinds, Host.Settings.PetKind) >= 0 ? Host.Settings.PetKind : "cat";

    /// <summary>The owl and the parrot fly: they jump further, fall slower with their wings out, and fly to the ball.</summary>
    public static bool IsFlyer(string kind) => kind is "parrot" or "owl";

    /// <summary>Animals that move in hops rather than steps: they only move while in the air.</summary>
    static bool IsHopper(string kind) => kind is "bunny" or "frog" or "parrot";

    /// <summary>
    /// Each animal's calls for each thing it wants to say, from the synthesized clips in <see cref="Sound"/>;
    /// one is picked at random. Empty means the animal keeps quiet (a fox does not announce a hunt).
    /// </summary>
    public static string[] VoicesOf(string kind, Say say) => kind switch
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
        "hamster" => say switch
        {
            Say.Surprise or Say.Upset => new[] { "hsqueak", "hsqueak2" }, Say.Call => new[] { "hsqueak2", "chitter" },
            Say.Happy or Say.Content => new[] { "chitter" }, Say.Hunt => Array.Empty<string>(), _ => new[] { "hsqueak", "chitter" },
        },
        "turtle" => say switch
        {
            Say.Surprise or Say.Upset => new[] { "thiss" }, Say.Content or Say.Hunt => Array.Empty<string>(), _ => new[] { "tgrunt" },
        },
        "parrot" => say switch
        {
            Say.Surprise or Say.Upset => new[] { "squawk" }, Say.Call => new[] { "squawk", "whistle" }, Say.Happy => new[] { "whistle", "hello" },
            Say.Content => new[] { "whistle" }, Say.Hunt => Array.Empty<string>(), _ => new[] { "hello", "whistle" },
        },
        "frog" => say switch
        {
            Say.Call or Say.Upset => new[] { "croak", "ribbit" }, Say.Content or Say.Hunt => Array.Empty<string>(), _ => new[] { "ribbit" },
        },
        "owl" => say switch
        {
            Say.Call => new[] { "hoots" }, Say.Happy or Say.Content => new[] { "trill" }, Say.Hunt => Array.Empty<string>(),
            Say.Upset => new[] { "hoot" }, _ => new[] { "hoot", "trill" },
        },
        "dragon" => say switch
        {
            Say.Surprise or Say.Upset => new[] { "roar" }, Say.Call => new[] { "rumble" }, Say.Happy => new[] { "huff", "rumble" },
            Say.Content => new[] { "rumble" }, Say.Hunt => Array.Empty<string>(), _ => new[] { "rumble", "huff" },
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
        foreach (var clip in new[] { "purr", "snore", "yawn", "sniff", "lick", "pant", "thump", "flap", "hiss", "huff", "croak", "thiss", "chitter" }) yield return clip;
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
        "hamster" => new[] { "stuff", "groom", "circle" },
        "turtle" => new[] { "hide", "neck", "nibble" },
        "parrot" => new[] { "preen", "bob", "shriek" },
        "frog" => new[] { "throat", "fly", "blink" },
        "owl" => new[] { "swivel", "fluff", "slowblink" },
        "dragon" => new[] { "smoke", "scratch", "curl" },
        _ => new[] { "groom", "knead", "sniff" },
    };

    /// <summary>Walking speed in px/s: a penguin waddles, a dog trots, a hamster scurries, a turtle crawls.</summary>
    public static double WalkSpeedOf(string kind) => kind switch
    {
        "dog" => 88, "duck" => 52, "bunny" => 80, "penguin" => 40, "fox" => 80, "hamster" => 120, "turtle" => 22, "parrot" => 60,
        "frog" => 70, "owl" => 34, "dragon" => 48, _ => 72,
    };

    /// <summary>
    /// How each animal takes to a thrown ball: dogs and foxes fetch every time, cats sometimes (and may lose interest
    /// halfway), parrots and owls fly to it, hamsters push it along, bunnies and frogs hop after it, and ducks,
    /// penguins, turtles and dragons only watch it.
    /// </summary>
    public static FetchStyle FetchStyleOf(string kind) => kind switch
    {
        "dog" or "fox" => FetchStyle.Fetch,
        "cat" => FetchStyle.Sometimes,
        "parrot" or "owl" => FetchStyle.Fly,
        "hamster" => FetchStyle.Push,
        "bunny" or "frog" => FetchStyle.Hop,
        _ => FetchStyle.Watch,
    };

    /// <summary>Between 22:00 and 06:00 the pet is drowsier and sleeps in a nightcap.</summary>
    public static bool IsNight(TimeOnly t) => t >= new TimeOnly(22, 0) || t < new TimeOnly(6, 0);

    /// <summary>06:00 to 10:00: the first time it is seen, it stretches and says good morning.</summary>
    public static bool IsMorning(TimeOnly t) => t >= new TimeOnly(6, 0) && t < new TimeOnly(10, 0);

    /// <summary>What goes in the thought bubble when several things are on its mind: a fright first, then affection, hunger, sleep, play.</summary>
    public static Bubble ChooseBubble(bool startled, bool petted, bool begging, bool sleepy, bool wantsPlay) =>
        startled ? Bubble.Alarm : petted ? Bubble.Heart : begging ? Bubble.Treat : sleepy ? Bubble.Sleepy : wantsPlay ? Bubble.Ball : Bubble.None;

    /// <summary>The LAN message for this pet's state: "pt|kind|x|y|face|mode|act", x and y as fractions of the arena.</summary>
    public static string EncodeVisit(Visit v) => string.Create(CultureInfo.InvariantCulture,
        $"pt|{v.Kind}|{Math.Clamp(v.X, 0, 1):0.####}|{Math.Clamp(v.Y, 0, 1):0.####}|{(v.Face < 0 ? -1 : 1)}|{v.Mode.ToString().ToLowerInvariant()}|{v.Act}");

    /// <summary>Reads a "pt|…" message back; false for anything else or anything malformed.</summary>
    public static bool TryDecodeVisit(string message, out Visit visit)
    {
        visit = default;
        var f = message.Split('|');
        if (f.Length != 7 || f[0] != "pt" || Array.IndexOf(Kinds, f[1]) < 0) return false;
        if (!double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
            !double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
            !double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1 ||
            !int.TryParse(f[4], out int face) || !Enum.TryParse<Mode>(f[5], true, out var mode))
            return false;
        string act = f[6];
        if (act.Length > 16) return false;
        foreach (char c in act) if (!char.IsAsciiLetterLower(c)) return false;
        visit = new Visit(f[1], x, y, face < 0 ? -1 : 1, mode, act);
        return true;
    }

    /// <summary>The display name of a kind, from the tray's list.</summary>
    static string KindName(string kind)
    {
        foreach (var (k, name) in Tray.PetChoices())
            if (k == kind) return name;
        return kind;
    }

    /// <summary>Pitch for the shared clips (yawn, snore, sniff, lick), so a hamster's yawn is tiny and a dragon's is deep.</summary>
    double SizePitch => Kind switch
    {
        "dog" => 0.85, "duck" => 1.15, "bunny" => 1.45, "penguin" => 0.95, "fox" => 1.1, "hamster" => 1.6, "turtle" => 0.8, "parrot" => 1.3,
        "frog" => 1.0, "owl" => 0.9, "dragon" => 0.6, _ => 1.05,
    };

    /// <summary>1 when fresh; drops the longer it is awake and the more it runs, and a nap or a treat fills it up again.</summary>
    double Energy => Math.Clamp(_rested - (Now - _awakeSince) / 900 - _exertion, 0, 1);

    /// <summary>Goes up when it is petted or thrown, and calms down over about 20 seconds.</summary>
    double Excitement => _excite * Math.Exp(-(Now - _excitedAt) / 20);

    void Thrill(double amount)
    {
        _excite = Math.Min(1.5, Excitement + amount);
        _excitedAt = Now;
    }

    bool IsTrick(string act) => act.Length > 0 && act == TrickFor(Kind).Name;

    /// <summary>A hopper moves in hops: 0 to 1 through each hop (a frog's are longer and higher than a bunny's).</summary>
    static double HopPhaseOf(string kind, double animT, double speed) => animT * (kind == "frog" ? 2.4 : kind == "parrot" ? 3.6 : 3.2) * Math.Sqrt(speed) % 1;

    double HopPhase => HopPhaseOf(Kind, _animT, _speed);

    /// <summary>Flyers fall gently with their wings out, except on the way up from a throw.</summary>
    bool Gliding => IsFlyer(Kind) && _mode == Mode.Air && !(_thrown && _vel.Y < 0);

    double AirGravity => Gliding ? Gravity * GlideGravity : Gravity;

    /// <summary>
    /// Each animal's trick and how long it lasts: the cat stretches, the dog chases its tail, the duck flaps, the bunny
    /// does a twisting hop (a binky), the penguin belly-slides, the fox crouches and pounces, the hamster spins like a
    /// wheel, the turtle pops into its shell, the parrot flies a loop and shouts a word, the frog catches a fly with a
    /// long tongue, the owl turns its head right round and the dragon breathes a little fire.
    /// </summary>
    public static (string Name, double Seconds) TrickFor(string kind) => kind switch
    {
        "dog" => ("spin", 0.9), "duck" => ("flap", 1.0), "bunny" => ("binky", 0.75), "penguin" => ("slide", 1.4), "fox" => ("pounce", 0.4),
        "hamster" => ("wheel", 1.6), "turtle" => ("shell", 1.8), "parrot" => ("loop", 1.1), "frog" => ("tongue", 1.0), "owl" => ("headspin", 1.5),
        "dragon" => ("fire", 1.2),
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
        RefreshKind();
        int at = Layer.Children.IndexOf(_art.Root);
        Layer.Children.Remove(_art.Root);
        _art = BuildPet(Kind);
        _art.Root.IsHitTestVisible = false;
        Layer.Children.Insert(Math.Max(0, at), _art.Root);
        _wingK = 0;
        Draw(0);
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
    Vec2 Mouth => Center + new Vec2(_face * 15, 3);
    bool Grounded => _mode is Mode.Sit or Mode.Walk or Mode.Sleep;
    bool Awake => _mode != Mode.Sleep;

    public override HudInfo Hud => new(_pets.ToString(), StateLine(),
        L.F("Pets {0}", Host.Stats.Get("pet.pets"))); // the pill shows the last number, and the board has no room for three

    string StateLine()
    {
        if (_mode == Mode.Sleep) return L.T("Sleeping · click to wake");
        if (_mode == Mode.Carried) return L.T("Wheee! · let go to throw");
        if (_visiting && Host.Lan.Connected) return L.F("Visiting {0}'s {1}", Host.Lan.PeerName, KindName(_visit.Kind));
        if (_act == "morning") return L.T("Good morning!");
        if (_act == "eat") return L.T("Nom nom nom");
        if (IsTrick(_act)) return L.T("Showing off");
        if (_fetch == "return") return L.T("Bringing the ball back");
        if (_fetch == "chase") return L.T("After the ball!");
        if (_goal == "treat") return L.T("Off to get a treat");
        if (_begging || _act == "beg") return L.T("Begging for a treat · click the jar");
        if (_act is "groom" or "preen") return L.T("Grooming");
        if (_act is "stalk" or "chatter") return L.T("Hunting the cursor");
        if (_act == "yawn") return L.T("Sleepy");
        if (_act == "stuff") return L.T("Stuffing its cheeks");
        if (_act is "hide" or "shell") return L.T("Safe in its shell");
        if (_act == "neck") return L.T("Having a look around");
        if (_act is "fly" or "tongue") return L.T("Catching flies");
        if (_act is "swivel" or "headspin") return L.T("Watching you");
        if (_act == "smoke") return L.T("Puffing smoke");
        if (_act == "curl") return L.T("Curled up");
        if (_act == "circle" || (_mode == Mode.Walk && _speed > 1)) return L.T("Zoomies!");
        if (Gliding) return L.T("Flying");
        if (_thrown) return L.T("Wheee!");
        if (_watchBall) return L.T("Watching the ball");
        if (_company) return L.T("Keeping you company");
        if (_goal == "company") return L.T("Coming over to sit with you");
        if (_wantsPlay && Now - _wantsPlayAt < 20) return L.T("Wants to play · throw the ball");
        if (Now - _lastStir > AttentionAfter) return L.T("Wants attention · click to pet");
        if (_hwnd != IntPtr.Zero) return L.T("Exploring the window tops");
        return _ball.State == ThingState.Hidden
            ? L.T("Click to pet · right-click for a trick · drag to carry")
            : L.T("Click to pet · right-click for a trick · throw the ball · click the jar for a treat");
    }

    /// <summary>Everything <see cref="StateLine"/> looks at, so a frame can tell whether the line could have changed without building it.</summary>
    readonly record struct HudState(Mode Mode, string Kind, string Act, string Fetch, string Goal, bool Begging, bool Zoomies, bool Gliding,
        bool Thrown, bool WatchBall, bool Company, bool WantsPlay, bool WantsAttention, IntPtr Hwnd, ThingState Ball, bool Visiting, string Peer, string VisitKind);

    HudState HudNow() => new(_mode, _kind, _act, _fetch, _goal, _begging, _speed > 1, Gliding, _thrown, _watchBall, _company,
        _wantsPlay && Now - _wantsPlayAt < 20, Now - _lastStir > AttentionAfter, _hwnd, _ball.State,
        _visiting && Host.Lan.Connected, Host.Lan.PeerName, _visit.Kind);

    void UpdateHud()
    {
        _hudState = HudNow();
        string line = StateLine();
        if (line == _shownLine) return;
        _shownLine = line;
        Host.HudChanged();
    }

    /// <summary>Once a frame: the line is built again only when one of the things it depends on changed (the timed ones included).</summary>
    void UpdateHudIfChanged()
    {
        if (HudNow() != _hudState) UpdateHud();
    }

    // ------------------------------------------------------------------ life cycle

    public override void Layout()
    {
        RefreshKind();
        var a = Host.Arena;
        if (!_placed)
        {
            _placed = true;
            _pos = new Vec2(a.Left + a.Width * 0.3, a.Bottom);
            _lastStir = _awakeSince = _lastTreat = _pointerMovedAt = Now;
            _pointerAt = Host.Pointer;
        }
        _pos.X = Clamp(_pos.X, a.Left + HalfW, a.Right - HalfW);
        _pos.Y = Clamp(_pos.Y, a.Top + Height, a.Bottom);
        if (Grounded && _hwnd == IntPtr.Zero) _pos.Y = a.Bottom;
        // windows may have moved while we were away: check the window top again without applying a stale delta
        _seenGen = _thingGen = Host.Platforms.Generation;
        _recheck = Grounded && _hwnd != IntPtr.Zero;
        _recheckThings = true;
        foreach (var o in _things)
        {
            o.P.X = Clamp(o.P.X, a.Left + o.R, a.Right - o.R);
            o.P.Y = Clamp(o.P.Y, a.Top + o.R, a.Bottom - o.R);
            if (o.State == ThingState.Resting && o.Hwnd == IntPtr.Zero) o.P.Y = a.Bottom - o.R;
        }
        PlaceJar();
        var now = DateTime.Now;
        _night = IsNight(TimeOnly.FromDateTime(now));
        if (IsMorning(TimeOnly.FromDateTime(now)) && _greetedOn != DateOnly.FromDateTime(now))
        {
            _greetedOn = DateOnly.FromDateTime(now);
            _morning = true; // a long stretch and a hello, once it is on its feet
        }
        _active = true;
        RunBrain();
        Draw(0);
        _shownLine = StateLine();
        Host.HudChanged();
    }

    /// <summary>The treat jar stands at the far end of the taskbar, away from the scoreboard.</summary>
    void PlaceJar()
    {
        var a = Host.Arena;
        var hud = Host.HudBounds.Inflate(12);
        var right = new Vec2(a.Right - 30, a.Bottom);
        var left = new Vec2(a.Left + 30, a.Bottom);
        _jarPos = hud.Contains(new Point(right.X, right.Y - JarH / 2)) ? left : right;
        _jar.Set(_jarPos);
    }

    public override void Deactivate()
    {
        _active = false;
        _brain.Stop();
        _decide = false;
        _pressed = false;
        _demoHand = null;
        if (_mode == Mode.Carried) Drop(default);
        if (_ballHeld)
        {
            _ballHeld = false;
            ThrowBall(default);
        }
        foreach (var h in _hearts)
        {
            h.El.IsVisible = false;
            _heartPool.Push(h);
        }
        _hearts.Clear();
        HideBubble();
        HideVisitor();
        _fly.IsVisible = _flyShown = false;
    }

    public override void PointerCancel()
    {
        _pressed = false;
        if (_mode == Mode.Carried) Drop(default);
        if (_ballHeld)
        {
            _ballHeld = false;
            ThrowBall(default);
        }
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
        if (_mode != Mode.Sit || _pressed || _act.Length > 0 || _goal.Length > 0 || _fetch.Length > 0) return;
        TrackPointer();
        _night = IsNight(TimeOnly.FromDateTime(DateTime.Now));
        double idle = Now - _lastStir;
        double sleepAfter = _demo ? DemoSleepAfter : _night ? NightSleepAfter : SleepAfter;
        if (idle > sleepAfter || (Energy < 0.3 && idle > 15))
        {
            if (Now - _yawnedAt > 20)
            {
                _yawnedAt = Now;
                StartAct("yawn"); // a big yawn first; it lies down at the next thought
                ShowBubble(Bubble.Sleepy, 1.8);
            }
            else
            {
                GoToSleep();
            }
            return;
        }
        if (_night && Energy < 0.7 && Now - _yawnedAt > 40 && Rng.NextDouble() < 0.2)
        {
            _yawnedAt = Now; // late: it yawns more
            StartAct("yawn");
            ShowBubble(Bubble.Sleepy, 1.8);
            return;
        }

        string kind = Kind;
        if (_begging)
        {
            if (Now - _begSince > BegFor) _begging = false;
            else if (Rng.NextDouble() < 0.6)
            {
                _face = _jarPos.X > _pos.X ? 1 : -1;
                StartAct("beg");
                ShowBubble(Bubble.Treat, 2.5);
            }
            UpdateHud();
            return;
        }
        if (Now - _lastTreat > (_demo ? DemoTreatBegAfter : TreatBegAfter) && Now - _lastBeg > BegAgain)
        {
            Beg();
            return;
        }
        if (_company)
        {
            double glance = Rng.NextDouble(); // settled: a look at you now and then, otherwise gazing about
            if (glance < 0.5) FaceCursor();
            else if (glance < 0.75) _face = -_face;
            else if (glance < 0.85) Speak(Say.Content, 0.3);
            return;
        }
        if (TryCompany()) return;
        if (Host.Lan.Connected && _visitSeen < VisitFade && !_visiting && _hwnd == IntPtr.Zero && Math.Abs(_visitPos.X - _pos.X) > VisitR * 0.8 && Rng.NextDouble() < 0.5)
        {
            WalkTo(_visitPos.X); // a visitor: go over and say hello
            return;
        }

        if (Excitement > 0.6 && Energy > 0.4 && Rng.NextDouble() < 0.5)
        {
            if (kind is "bunny" or "frog" or "parrot") Trick(); // a binky is how a happy rabbit lets off steam
            else if (kind is "cat" or "dog" or "fox" or "hamster") Zoomies();
            else if (_ball.State == ThingState.Resting && Rng.NextDouble() < 0.5) WantPlay();
            else Speak(Say.Happy);
            return;
        }

        var toCursor = Host.Pointer - Center;
        if (idle > AttentionAfter && Now - _lastCall > 8 + Rng.NextDouble() * 8)
        {
            _lastCall = Now;
            if (_ball.State == ThingState.Resting && Now - _wantsPlayAt > 40 && Rng.NextDouble() < 0.5)
            {
                WantPlay(); // bored: it would like a game of fetch
                if (Math.Abs(_ball.P.X - _pos.X) > 40 && _ball.Hwnd == _hwnd) WalkTo(_ball.P.X);
                return;
            }
            // ignored for a while: it comes over and asks for you
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

    /// <summary>It would like a game: the ball in its thoughts, and a call.</summary>
    void WantPlay()
    {
        _wantsPlay = true;
        _wantsPlayAt = Now;
        ShowBubble(Bubble.Ball, 3);
        Speak(Say.Happy, 0.45);
        UpdateHud();
    }

    /// <summary>Hungry: it goes to the jar and asks.</summary>
    void Beg()
    {
        _begging = true;
        _begSince = _lastBeg = Now;
        Speak(Say.Call);
        ShowBubble(Bubble.Treat, 3);
        var a = Host.Arena;
        double side = _jarPos.X > a.Center.X ? -1 : 1;
        SetGoal("jar", new Vec2(_jarPos.X + side * 34, a.Bottom), IntPtr.Zero, 30);
    }

    /// <summary>The cursor has rested a while: go and sit on the window nearest it, if one is within reach.</summary>
    bool TryCompany()
    {
        if (Now - _pointerMovedAt < CompanyAfter || Now - _companyTriedAt < CompanyRetry) return false;
        _companyTriedAt = Now;
        var a = Host.Arena;
        var cursor = Host.Pointer;
        double best = CompanyReach;
        DeskArcade.Engine.Platform found = default;
        double atX = 0;
        foreach (var p in Host.Platforms.Items)
        {
            if (p.Y - Height < a.Top + 4) continue;
            double lo = Math.Max(p.X1 + EdgeIn + 8, a.Left + HalfW), hi = Math.Min(p.X2 - EdgeIn - 8, a.Right - HalfW);
            if (hi <= lo) continue;
            double x = Clamp(cursor.X, lo, hi);
            double d = Math.Abs(x - cursor.X) + Math.Abs(p.Y - cursor.Y) * 0.5;
            if (d >= best) continue;
            best = d;
            found = p;
            atX = x;
        }
        if (found.Hwnd == IntPtr.Zero) return false;
        if (found.Hwnd == _hwnd && Math.Abs(atX - _pos.X) < 60)
        {
            Settle();
            return true;
        }
        SetGoal("company", new Vec2(atX, found.Y), found.Hwnd, 24);
        return true;
    }

    void Settle()
    {
        _company = true;
        _companyPointer = Host.Pointer;
        FaceCursor();
        StartAct("settle");
        Speak(Say.Content, 0.3);
        UpdateHud();
    }

    /// <summary>Remembers when the cursor last moved (for keeping company), and gets up when it moves again.</summary>
    void TrackPointer()
    {
        var p = Host.Pointer;
        if ((p - _pointerAt).Length > 3)
        {
            _pointerAt = p;
            _pointerMovedAt = Now;
        }
        if (_company && (p - _companyPointer).Length > CompanyBreak)
        {
            _company = false;
            UpdateHud();
        }
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
            case "hamster" when r < 0.35:
                StartAct("circle"); // too excited to keep still
                return;
            case "turtle" when r < 0.5:
                StartAct("neck"); // a long slow look
                return;
            case "parrot" when r < 0.5:
                StartAct("bob");
                if (Rng.NextDouble() < 0.5) Speak(Say.Hello);
                return;
            case "frog" when level && Math.Abs(d.X) < 90 && r < 0.4:
                StartAct("fly"); // a flick of the tongue at whatever that is
                return;
            case "owl" when r < 0.6:
                StartAct("swivel"); // it does not move; its head does
                return;
            case "dragon" when r < 0.35:
                StartAct("smoke");
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
        _night = IsNight(TimeOnly.FromDateTime(DateTime.Now));
        _brain.Interval = TimeSpan.FromSeconds(3 + Rng.NextDouble() * 3); // snores for a while, then goes quiet
        RunBrain();
        _art.Zz.Text = L.T("z z");
        Canvas.SetTop(_art.Zz, Math.Max(-46, Host.Arena.Top + 2 - Center.Y)); // closed box: keep the label on screen
        Draw(0);
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
        _actMark = 0;
        string kind = Kind;
        switch (name)
        {
            case "groom": PlayThrottled("lick", 0.22, SizePitch); break;
            case "knead": PlayThrottled("purr", 0.45); break;
            case "sniff": PlayThrottled("sniff", 0.35, SizePitch); break;
            case "pant": PlayThrottled("pant", 0.35, SizePitch); break;
            case "yawn": PlayThrottled("yawn", 0.4, SizePitch * (0.95 + Rng.NextDouble() * 0.1)); break;
            case "thump": PlayThrottled("thump", 0.55); break;
            case "nod" or "bob" or "curl": Speak(Say.Content, 0.4); break;
            case "call": Speak(Say.Call, 0.5); break;
            case "shriek": Speak(Say.Call, 0.7, 1.1); break;
            case "chatter": Speak(Say.Hunt, 0.45); break;
            case "puff": Speak(Say.Upset, 0.5); break;
            case "flap": PlayThrottled("flap", 0.35); break;
            case "fluff": PlayThrottled("flap", 0.25, 0.9); break;
            case "shake": PlayThrottled(kind is "duck" or "penguin" ? "flap" : "whoosh", 0.25, 1.5); break;
            case "stuff": PlayThrottled("lick", 0.25, 1.4); break;
            case "nibble": PlayThrottled("lick", 0.2, 0.8); break;
            case "circle":
                _actX = _pos.X;
                Speak(Say.Happy, 0.35);
                break;
            case "hide": PlayThrottled("thiss", 0.3); break;
            case "throat": PlayThrottled("croak", 0.2, 1.2); break;
            case "fly":
                PlayThrottled("lick", 0.2, 1.8);
                ShowFly(30);
                break;
            case "tongue": ShowFly(46); break;
            case "smoke": PlayThrottled("huff", 0.3, 1.2); break;
            case "wheel":
                PlayThrottled("whoosh", 0.25, 1.6);
                Speak(Say.Happy, 0.4);
                break;
            case "shell": PlayThrottled("pop", 0.25, 0.9); break;
            case "loop": PlayThrottled("flap", 0.4); break;
            case "eat": PlayThrottled("board", 0.4, 0.55); break;
            case "beg": Speak(Say.Call, 0.45); break;
            case "morning": PlayThrottled("yawn", 0.35, SizePitch * 0.9); break;
        }
        RunBrain();
        UpdateHud();
        Host.Wake();
    }

    /// <summary>A fly to catch, hovering in front of the frog.</summary>
    void ShowFly(double ahead)
    {
        var a = Host.Arena;
        _flyP = new Vec2(Clamp(Center.X + _face * ahead, a.Left + 6, a.Right - 6), Math.Max(a.Top + 6, Center.Y - 8));
        _flyShown = true;
        _fly.IsVisible = true;
    }

    /// <summary>What it does once it has landed after being thrown.</summary>
    void AfterThrow(bool hard)
    {
        ShowBubble(Bubble.Alarm, 1.2);
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
            case "hamster":
                StartAct("shake");
                Speak(hard ? Say.Upset : Say.Happy, 0.5);
                break;
            case "turtle":
                StartAct(hard ? "hide" : "neck"); // into the shell, or a slow look at who did that
                if (hard) Speak(Say.Upset, 0.4);
                break;
            case "parrot":
                StartAct("shake"); // ruffled feathers
                Speak(hard ? Say.Upset : Say.Happy, 0.5);
                Thrill(0.2);
                break;
            case "frog":
                StartAct("throat");
                Speak(Say.Happy, 0.45);
                break;
            case "owl":
                StartAct("fluff");
                Speak(hard ? Say.Upset : Say.Content, 0.45);
                break;
            case "dragon":
                StartAct("smoke"); // a big huff
                Speak(hard ? Say.Upset : Say.Happy, 0.5);
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

    /// <summary>Hop off the window top on the side nearer <paramref name="x"/>, if that edge is open; false when neither is.</summary>
    bool HopDownToward(double x)
    {
        var a = Host.Arena;
        bool leftOpen = _surface.X1 > a.Left + HalfW, rightOpen = _surface.X2 < a.Right - HalfW;
        if (!leftOpen && !rightOpen) return false;
        double dir = leftOpen && rightOpen ? (x < _pos.X ? -1 : 1) : leftOpen ? -1 : 1;
        StartWalk(dir, 8);
        _hopAtEdge = true;
        return true;
    }

    double MaxJumpUp => IsFlyer(Kind) ? JumpUp * FlyUp : JumpUp;
    double MaxJumpSide => IsFlyer(Kind) ? JumpSide * FlySide : JumpSide;

    bool TryJumpUp()
    {
        var a = Host.Arena;
        var hud = Host.HudBounds.Inflate(20);
        var options = new List<Vec2>();
        foreach (var p in Host.Platforms.Items)
        {
            double rise = _pos.Y - p.Y;
            if (rise < 24 || rise > MaxJumpUp || p.Y - ApexExtra - Height < a.Top + 4) continue;
            double lo = Math.Max(p.X1 + EdgeIn + 8, a.Left + HalfW), hi = Math.Min(p.X2 - EdgeIn - 8, a.Right - HalfW);
            if (hi <= lo) continue;
            double near = Clamp(_pos.X, lo, hi);
            if (Math.Abs(near - _pos.X) > MaxJumpSide) continue;
            double inward = near > _pos.X ? 1 : near < _pos.X ? -1 : Rng.NextDouble() < 0.5 ? -1 : 1;
            double x = Clamp(near + inward * Rng.NextDouble() * 60, lo, hi);
            if (hud.Contains(new Point(x, p.Y - CenterLift))) continue;
            options.Add(new Vec2(x, p.Y));
        }
        if (options.Count == 0) return false;
        JumpTo(options[Rng.Next(options.Count)]);
        return true;
    }

    /// <summary>Jump onto the window top <paramref name="hwnd"/>, as near <paramref name="x"/> as it goes; false when it is out of reach from here.</summary>
    bool JumpToPlatform(IntPtr hwnd, double x)
    {
        var a = Host.Arena;
        foreach (var p in Host.Platforms.Items)
        {
            if (p.Hwnd != hwnd) continue;
            double rise = _pos.Y - p.Y;
            if (rise < 24 || rise > MaxJumpUp || p.Y - ApexExtra - Height < a.Top + 4) continue;
            double lo = Math.Max(p.X1 + EdgeIn + 8, a.Left + HalfW), hi = Math.Min(p.X2 - EdgeIn - 8, a.Right - HalfW);
            if (hi <= lo) continue;
            double at = Clamp(x, lo, hi);
            if (Math.Abs(at - _pos.X) > MaxJumpSide) continue;
            JumpTo(new Vec2(at, p.Y));
            return true;
        }
        return false;
    }

    /// <summary>A ballistic arc that peaks a little above the target window top and comes down onto it (a glide for a flyer).</summary>
    void JumpTo(Vec2 target)
    {
        bool flyer = IsFlyer(Kind);
        double g = flyer ? Gravity * GlideGravity : Gravity;
        double up = _pos.Y - target.Y + ApexExtra;
        double vy = -Math.Sqrt(2 * g * up);
        double time = -vy / g + Math.Sqrt(2 * ApexExtra / g);
        double vx = (target.X - _pos.X) / time;
        if (Math.Abs(vx) > 1) _face = vx > 0 ? 1 : -1;
        Drop(new Vec2(vx, vy));
        PlayThrottled(flyer ? "flap" : "pop", flyer ? 0.35 : 0.2, flyer ? 1 : 1.9);
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
        _company = false;
        if (_ball.State == ThingState.Carried && FetchStyleOf(Kind) == FetchStyle.Push) ReleaseBall(default); // it cannot push a ball through the air
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

    // ------------------------------------------------------------------ goals: somewhere to get to

    /// <summary>Head for a spot on a window top (or the floor when <paramref name="hwnd"/> is zero) and do <paramref name="what"/> on arrival.</summary>
    void SetGoal(string what, Vec2 p, IntPtr hwnd, double reach)
    {
        _goal = what;
        _goalP = p;
        _goalHwnd = hwnd;
        _goalReach = reach;
        _goalSince = Now;
        _goalStep = 0;
        _goalStuck = 0;
        if (_mode == Mode.Sleep) WakeUp();
        if (_mode == Mode.Walk) StopWalking();
        if (!IsTrick(_act)) _act = "";
        RunBrain();
        UpdateHud();
        Host.Wake();
    }

    void ClearGoal() => _goal = "";

    /// <summary>One step toward the goal: walk along the surface, jump up to the goal's window, or hop down toward it.</summary>
    void StepGoal()
    {
        if (_goal.Length == 0 || _mode != Mode.Sit || _act.Length > 0 || _pressed || Now - _goalStep < GoalStep) return;
        _goalStep = Now;
        if (Now - _goalSince > GoalGiveUp || _goalStuck > 8)
        {
            GiveUpGoal();
            return;
        }
        var (lo, hi) = WalkRange();
        if (_goalHwnd == _hwnd && Math.Abs(_goalP.Y - _pos.Y) < 6)
        {
            if (Math.Abs(_goalP.X - _pos.X) <= _goalReach)
            {
                Arrive();
                return;
            }
            if (Math.Abs(Clamp(_goalP.X, lo, hi) - _pos.X) < 4) _goalStuck++; // this is as near as the surface goes
            else WalkTo(_goalP.X);
            return;
        }
        if (_goalP.Y < _pos.Y - 6)
        {
            if (JumpToPlatform(_goalHwnd, _goalP.X)) return;
            if (Math.Abs(Clamp(_goalP.X, lo, hi) - _pos.X) > 8) WalkTo(_goalP.X); // get underneath it first
            else if (!TryJumpUp()) _goalStuck++;                                     // or up onto whatever is on the way
            return;
        }
        if (_hwnd == IntPtr.Zero || !HopDownToward(_goalP.X)) _goalStuck++;
    }

    void GiveUpGoal()
    {
        string g = _goal;
        ClearGoal();
        switch (g)
        {
            case "ball":
                EndFetch();
                Speak(Say.Upset, 0.35);
                break;
            case "return": // could not get it back to you: put the ball down here rather than keep it in its mouth for good
                if (_ballHeld) ReleaseBall(default);
                else EndFetch();
                Speak(Say.Upset, 0.35);
                break;
            case "treat": _treatWanted = false; break;
            case "company": _companyTriedAt = Now; break;
            case "jar": _begging = false; break;
        }
        UpdateHud();
    }

    void Arrive()
    {
        string g = _goal;
        ClearGoal();
        switch (g)
        {
            case "ball": GrabBall(); break;
            case "return": DropBall(); break;
            case "treat": EatTreat(); break;
            case "company": Settle(); break;
            case "jar":
                _face = _jarPos.X > _pos.X ? 1 : -1;
                StartAct("beg");
                ShowBubble(Bubble.Treat, 3);
                break;
        }
        UpdateHud();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        if (_ball.State is ThingState.Resting or ThingState.Air or ThingState.Held) into.Add(HitShape.Circle(_ball.P, BallHitR));
        into.Add(HitShape.Circle(Center - new Vec2(0, 3), HitR));
        into.Add(HitShape.Box(JarRect()));
    }

    Rect JarRect() => new(_jarPos.X - JarW / 2 - 4, _jarPos.Y - JarH - 6, JarW + 8, JarH + 8);

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_pressed || _ballHeld) return false;
        if (_ball.State is ThingState.Resting or ThingState.Air && (p - _ball.P).Length <= BallHitR)
        {
            if (right) return false;
            _ballHeld = true;
            _ballTrail.Clear();
            _ball.State = ThingState.Held;
            _ball.Thrown = _ball.Rolling = false;
            _ball.Hwnd = IntPtr.Zero;
            _watchBall = false;
            if (_fetch.Length > 0) EndFetch();
            PlayThrottled("pop", 0.2, 1.5);
            return true;
        }
        if ((p - (Center - new Vec2(0, 3))).Length <= HitR)
        {
            if (right)
            {
                Trick();
                return false;
            }
            _pressed = true;
            _pressPos = p;
            return true; // capture until release: a still release pets it, a drag carries it
        }
        if (!right && JarRect().Contains(p.ToPoint()))
        {
            TossTreat();
            return false;
        }
        return false;
    }

    public override void PointerUp(Vec2 p)
    {
        if (_ballHeld)
        {
            _ballHeld = false;
            ThrowBall(TrailVelocity(_ballTrail));
            return;
        }
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
        if (_pets >= BallAfterPets && _ball.State == ThingState.Hidden && Grounded) PlaceBall();

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
        Hearts(5);
        Host.Fx.Burst(Center, Blush, 6, 150, 250, 4, 0.5);
        PlayThrottled("star", 0.3, 1.7 + Rng.NextDouble() * 0.2);
        ShowBubble(Bubble.Heart, 1.6);

        if (woke) StartAct("yawn"); // a big stretchy yawn, then it is ready to play
        else if (_mode != Mode.Sit) Speak(Say.Hello);
        else if (kind == "cat" && _petStreak >= 3) Speak(Say.Content, 0.5); // keep going and it purrs
        else if (kind == "bunny" && _petStreak >= 5) StartAct("flop");     // a relaxed rabbit flops over on its side
        else if (kind == "turtle" && _petStreak >= 4) StartAct("neck");    // it comes right out of its shell for you
        else if (kind == "hamster" && _petStreak >= 3) StartAct("stuff");
        else if (kind == "owl" && _petStreak >= 3) StartAct("slowblink");  // an owl's way of saying it trusts you
        else if (kind == "dragon" && _petStreak >= 4) StartAct("curl");
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

    void Hearts(int count)
    {
        var top = Center - new Vec2(0, 26);
        for (int i = 0; i < count; i++)
            SpawnHeart(top + new Vec2((Rng.NextDouble() - 0.5) * 30, Rng.NextDouble() * 8),
                new Vec2((Rng.NextDouble() - 0.5) * 90, -70 - Rng.NextDouble() * 60), 0.9 + Rng.NextDouble() * 0.5);
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
        _company = false;
        ClearGoal();
        if (_fetch.Length > 0) EndFetch();
        if (_ball.State == ThingState.Carried) ReleaseBall(default);
        _fly.IsVisible = _flyShown = false;
        RunBrain();
        PlayThrottled("pop", 0.3, 1.8);
        Speak(Say.Surprise, 0.45, 1.1); // a surprised little noise
        ShowBubble(Bubble.Alarm, 1.2);
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
        switch (name)
        {
            case "binky": Drop(new Vec2(_face * 70, -560)); break; // straight up with a twist
            case "pounce": _squashT = 0; break;                     // a crouch first, the leap comes after
            case "loop":
                Drop(new Vec2(_face * 120, -520));                  // up, over and round
                Host.Fx.Popup(Center - new Vec2(0, 44), L.T(ParrotWords[Rng.Next(ParrotWords.Length)]), Color.FromRgb(255, 209, 102), 16, 1.4);
                break;
        }
        Speak(Say.Hello, 0.55, name == "pounce" ? 1.1 : 1);
    }

    /// <summary>Advances the running action (its side effects; the pose is in <see cref="PoseOf"/>); returns true while it runs.</summary>
    bool StepAct(double dt)
    {
        if (_act.Length == 0) return false; // the action may have been cut short by a pet or a pick-up
        _actT += dt;
        double k = Math.Min(1, _actT / _actLen);
        switch (_act)
        {
            case "slide" when _mode == Mode.Sit:
            {
                var (lo, hi) = WalkRange();
                _pos.X = Clamp(_pos.X + _face * 220 * Math.Sin(Math.PI * k) * dt, lo, hi);
                break;
            }
            case "stalk":
                if (Math.Abs(_actX - _pos.X) > 4) _face = _actX > _pos.X ? 1 : -1;
                break;
            case "circle" when _mode == Mode.Sit: // round in a ring: out, back and out again, turning at each end
            {
                var (lo, hi) = WalkRange();
                double turn = k * Math.PI * 2;
                _pos.X = Clamp(_actX + Math.Sin(turn) * 34, lo, hi);
                if (k < 0.97) _face = Math.Cos(turn) >= 0 ? 1 : -1;
                break;
            }
            case "smoke":
                if (_actT - _actMark > 0.16 && k < 0.8)
                {
                    _actMark = _actT; // little grey puffs drift up from its nostrils
                    Host.Fx.Spawn(Mouth + new Vec2(_face * 4, -2), new Vec2(_face * (20 + Rng.NextDouble() * 20), -45 - Rng.NextDouble() * 20),
                        Smoke, 5 + Rng.NextDouble() * 5, 0.9, -40);
                }
                break;
            case "fire":
                if (_actMark == 0 && k >= 0.3)
                {
                    _actMark = 1; // the breath itself
                    Host.Fx.Burst(Mouth + new Vec2(_face * 10, 0), Fire, 24, 260, -120, 6, 0.5);
                    PlayThrottled("huff", 0.5, 0.8);
                }
                else if (_actMark == 1 && k >= 0.5)
                {
                    _actMark = 2;
                    Host.Fx.Burst(Mouth + new Vec2(_face * 14, -2), Fire, 12, 200, -120, 5, 0.4);
                }
                break;
            case "tongue" or "fly":
                if (_flyShown && k >= (_act == "tongue" ? 0.35 : 0.5))
                {
                    _flyShown = false; // got it
                    _fly.IsVisible = false;
                    Host.Fx.Burst(_flyP, new[] { Color.FromRgb(60, 60, 70), Colors.White }, 5, 90, 300, 2.5, 0.35);
                    PlayThrottled("pop", 0.25, 2.2);
                }
                break;
            case "eat":
                if (_actMark == 0 && k >= 0.5)
                {
                    _actMark = 1;
                    PlayThrottled("board", 0.3, 0.5); // a second crunch
                }
                break;
        }
        if (k < 1) return true;
        string done = _act;
        _act = "";
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
            case "morning":
                Speak(Say.Hello);
                Hearts(3);
                ShowBubble(Bubble.Heart, 1.6);
                break;
            case "tongue" or "fly":
                _fly.IsVisible = _flyShown = false;
                break;
        }
        UpdateHud();
        return true;
    }

    /// <summary>0 to 1 over the first 15 % of an action, and back to 0 over the last 15 %.</summary>
    static double Fade(double k) => Math.Clamp(Math.Min(k, 1 - k) / 0.15, 0, 1);

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

    Vec2 ThrowVelocity() => TrailVelocity(_trail);

    /// <summary>The speed of the hand over the last 70 ms of a drag trail.</summary>
    static Vec2 TrailVelocity(List<(double t, Vec2 p)> trail)
    {
        if (trail.Count < 2) return default;
        var last = trail[^1];
        var first = trail[0];
        foreach (var s in trail)
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
        Draw(0);
    }

    // ------------------------------------------------------------------ the ball and fetching

    /// <summary>The ball appears beside the pet once it has been petted twice.</summary>
    void PlaceBall()
    {
        var (lo, hi) = WalkRange();
        _ball.State = ThingState.Resting;
        _ball.Hwnd = _hwnd;
        _ball.P = new Vec2(Clamp(_pos.X + _face * 34, lo, hi), _pos.Y - BallR);
        _ball.V = default;
        _ball.Sprite.IsVisible = true;
        PlayThrottled("pop", 0.3, 1.2);
        ShowBubble(Bubble.Ball, 2.5);
        Host.HudChanged();
    }

    void HoldBall()
    {
        var a = Host.Arena;
        _ball.P = new Vec2(Clamp(Host.Pointer.X, a.Left + BallR, a.Right - BallR), Clamp(Host.Pointer.Y, a.Top + BallR, a.Bottom - BallR));
        _ballTrail.Add((_animT, _ball.P));
        while (_ballTrail.Count > 0 && _animT - _ballTrail[0].t > 0.15) _ballTrail.RemoveAt(0);
    }

    /// <summary>The player let go of the ball: it flies with the speed of the drag, and the pet takes an interest.</summary>
    void ThrowBall(Vec2 v)
    {
        if (v.Length > MaxBallThrow) v *= MaxBallThrow / v.Length;
        _ball.State = ThingState.Air;
        _ball.V = v;
        _ball.Hwnd = IntPtr.Zero;
        _ball.Thrown = v.Length > 120;
        _watchBall = false;
        if (!_ball.Thrown) return;
        if (v.Length > 500) PlayThrottled("whoosh", Math.Min(0.4, v.Length / 4000), 1.2);
        OnBallThrown();
    }

    /// <summary>The pet lets go of the ball (picked up, thrown, or a push cut short by a jump): it drops where it is.</summary>
    void ReleaseBall(Vec2 v)
    {
        _ball.State = ThingState.Air;
        _ball.V = v;
        _ball.Hwnd = IntPtr.Zero;
        _ball.Thrown = false;
        if (_fetch.Length > 0) EndFetch();
    }

    /// <summary>A thrown ball: fetchers go after it, the rest turn to watch and say something about it.</summary>
    void OnBallThrown()
    {
        _lastStir = Now;
        _wantsPlay = false;
        if (_mode == Mode.Carried || _pressed) return;
        var style = FetchStyleOf(Kind);
        if (style == FetchStyle.Watch || (style == FetchStyle.Sometimes && Rng.NextDouble() < 0.35))
        {
            _watchBall = true;
            if (_mode == Mode.Sleep) WakeUp();
            if (_mode == Mode.Sit && !IsTrick(_act)) _act = "";
            FaceBall();
            Speak(Say.Surprise, 0.4);
            UpdateHud();
            return;
        }
        if (_mode == Mode.Sleep) WakeUp();
        if (_mode == Mode.Walk) StopWalking();
        if (!IsTrick(_act)) _act = "";
        _fly.IsVisible = _flyShown = false;
        _company = false;
        _begging = false;
        ClearGoal();
        _fetch = "chase";
        _fetchSince = Now;
        Thrill(0.2);
        Speak(style == FetchStyle.Fetch ? Say.Happy : Say.Hunt, 0.45);
        RunBrain();
        UpdateHud();
        Host.Wake();
    }

    void EndFetch()
    {
        _fetch = "";
        if (_goal is "ball" or "return") ClearGoal();
        _ball.Thrown = false;
        UpdateHud();
    }

    /// <summary>Chasing: wait for the ball to stop, then go to it; returning: bring it to the cursor. Cats may wander off halfway.</summary>
    void StepFetch(double dt)
    {
        if (_fetch.Length == 0) return;
        if (_ball.State is ThingState.Held or ThingState.Hidden || _mode == Mode.Carried || _pressed)
        {
            EndFetch();
            return;
        }
        if (Now - _fetchSince > ChaseGiveUp + (_fetch == "return" ? 10 : 0))
        {
            if (_ball.State == ThingState.Carried) ReleaseBall(default);
            EndFetch();
            Speak(Say.Upset, 0.35);
            return;
        }
        if (_fetch == "chase")
        {
            if (FetchStyleOf(Kind) == FetchStyle.Sometimes && Rng.NextDouble() < dt * 0.06)
            {
                EndFetch(); // a cat loses interest and washes instead
                if (_mode == Mode.Sit && _act.Length == 0) StartAct("groom");
                return;
            }
            if (_ball.State == ThingState.Air)
            {
                if (_mode == Mode.Sit && _act.Length == 0) FaceBall(); // wait and watch where it goes
                return;
            }
            if (_goal != "ball") SetGoal("ball", GroundOf(_ball), _ball.Hwnd, GrabReach);
            else
            {
                _goalP = GroundOf(_ball); // it may have rolled on
                _goalHwnd = _ball.Hwnd;
            }
            return;
        }
        var (lo, hi) = WalkRange();
        double x = Clamp(Host.Pointer.X, lo, hi);
        if (_goal != "return") SetGoal("return", new Vec2(x, _pos.Y), _hwnd, ReturnReach);
        else
        {
            _goalP = new Vec2(x, _pos.Y);
            _goalHwnd = _hwnd;
        }
    }

    void GrabBall()
    {
        if (_ball.State != ThingState.Resting) return;
        _ball.State = ThingState.Carried;
        _ball.Thrown = _ball.Rolling = false;
        _ball.V = default;
        PlayThrottled("pop", 0.25, 1.6);
        Speak(Say.Happy, 0.45);
        Thrill(0.15);
        _fetch = "return";
        _fetchSince = Now;
        UpdateHud();
    }

    /// <summary>Brought back: the ball is set down at the cursor's side and the pet asks for another go.</summary>
    void DropBall()
    {
        var a = Host.Arena;
        var (lo, hi) = WalkRange();
        _ball.State = ThingState.Resting;
        _ball.Hwnd = _hwnd;
        _ball.P = new Vec2(Clamp(Clamp(_pos.X + _face * 20, lo, hi), a.Left + BallR, a.Right - BallR), _pos.Y - BallR);
        _ball.V = default;
        _ball.Thrown = false;
        EndFetch();
        Host.Stats.Add("pet.fetches");
        Thrill(0.2);
        Hearts(3);
        ShowBubble(Bubble.Ball, 3);
        _wantsPlay = true;
        _wantsPlayAt = Now;
        switch (Kind)
        {
            case "dog" or "fox": StartAct("wag"); break;
            case "parrot": StartAct("bob"); break;
            case "owl": StartAct("fluff"); break;
            case "hamster": StartAct("stuff"); break;
            case "frog": StartAct("throat"); break;
            default: Speak(Say.Happy, 0.5); break;
        }
        _face = Host.Pointer.X > _pos.X ? 1 : -1;
        Host.HudChanged();
    }

    /// <summary>Where a resting thing's feet would be: the spot on its surface the pet heads for.</summary>
    static Vec2 GroundOf(Thing o) => new(o.P.X, o.P.Y + o.R);

    void FaceBall()
    {
        if (Math.Abs(_ball.P.X - _pos.X) > 10) _face = _ball.P.X > _pos.X ? 1 : -1;
    }

    // ------------------------------------------------------------------ treats

    /// <summary>A click on the jar: a treat arcs out toward the pet's side of the screen.</summary>
    void TossTreat()
    {
        if (_treat.State is ThingState.Air or ThingState.Resting)
        {
            Anims.Add(0.3, k => _jar.Scale = 1 + 0.12 * Math.Sin(Math.PI * k), Ease.Linear); // one at a time: the jar just wobbles
            Host.Wake();
            return;
        }
        double side = Math.Sign(_pos.X - _jarPos.X);
        if (side == 0) side = _jarPos.X > Host.Arena.Center.X ? -1 : 1;
        _treat.State = ThingState.Air;
        _treat.P = new Vec2(_jarPos.X, _jarPos.Y - JarH - 6);
        _treat.V = new Vec2(side * (260 + Rng.NextDouble() * 220), -(380 + Rng.NextDouble() * 160));
        _treat.Hwnd = IntPtr.Zero;
        _treat.Sprite.IsVisible = true;
        _treatWanted = true;
        _begging = false;
        if (_goal == "jar") ClearGoal();
        _lastStir = Now;
        Anims.Add(0.35, k => _jar.Scale = 1 + 0.15 * Math.Sin(Math.PI * k), Ease.Linear);
        PlayThrottled("pop", 0.3, 1.4);
        if (_mode == Mode.Sleep)
        {
            WakeUp(); // the smell of it
            _lastStir = Now;
            Speak(Say.Surprise, 0.4);
        }
        Host.Wake();
    }

    /// <summary>A treat has come to rest: the pet drops everything to go and get it.</summary>
    void WantTreat()
    {
        if (!_treatWanted || _treat.State != ThingState.Resting || _goal == "treat" || _mode == Mode.Carried || _pressed || _mode == Mode.Sleep) return;
        if (_fetch.Length > 0)
        {
            if (_ball.State == ThingState.Carried) ReleaseBall(default);
            EndFetch();
        }
        _company = false;
        _begging = false;
        _watchBall = false;
        SetGoal("treat", GroundOf(_treat), _treat.Hwnd, TreatReach);
    }

    void EatTreat()
    {
        _treatWanted = false;
        if (_treat.State != ThingState.Resting) return;
        _face = _treat.P.X > _pos.X + 2 ? 1 : _treat.P.X < _pos.X - 2 ? -1 : _face;
        Host.Fx.Burst(_treat.P, Crumbs, 8, 120, 700, 3, 0.5);
        _treat.State = ThingState.Hidden;
        _treat.Sprite.IsVisible = false;
        StartAct("eat");
        Hearts(3);
        Thrill(0.35);
        _rested = Math.Min(1.2, _rested + 0.3); // a burst of energy
        _exertion = Math.Max(0, _exertion - 0.2);
        _lastTreat = _lastStir = Now;
        Host.Stats.Add("pet.treats");
        ShowBubble(Bubble.Heart, 1.8);
        Host.HudChanged();
    }

    // ------------------------------------------------------------------ things: the ball and the treat in the box

    /// <summary>Resting things ride along with their window, or fall when it moved away or closed.</summary>
    void CheckThings()
    {
        var plats = Host.Platforms;
        bool changed = plats.Generation != _thingGen;
        _thingGen = plats.Generation;
        var a = Host.Arena;
        foreach (var o in _things)
        {
            if (o.State != ThingState.Resting) continue;
            if (o.Hwnd == IntPtr.Zero)
            {
                o.P.Y = a.Bottom - o.R;
                continue;
            }
            if (!changed && !_recheckThings) continue;
            if (changed) o.P += plats.DeltaOf(o.Hwnd);
            bool found = false;
            foreach (var p in plats.Items)
            {
                if (p.Hwnd != o.Hwnd || Math.Abs(p.Y - (o.P.Y + o.R)) > 5 || o.P.X < p.X1 - 2 || o.P.X > p.X2 + 2) continue;
                o.P.Y = p.Y - o.R;
                found = true;
                break;
            }
            if (found) continue;
            o.State = ThingState.Air; // the window went: it falls
            o.V = default;
            o.Hwnd = IntPtr.Zero;
            Host.Wake();
        }
        _recheckThings = false;
    }

    /// <summary>Flies, bounces, rolls and settles the ball and the treat; true while either is moving.</summary>
    bool StepThings(double dt)
    {
        bool moving = _ball.State == ThingState.Air || _treat.State == ThingState.Air;
        if (!moving)
        {
            _thingAcc = 0;
            return false;
        }
        _thingAcc += dt;
        while (_thingAcc >= Step)
        {
            _thingAcc -= Step;
            if (_ball.State == ThingState.Air) ThingStep(_ball, Step, BallBounce, true);
            if (_treat.State == ThingState.Air) ThingStep(_treat, Step, 0.3, false);
        }
        return _ball.State == ThingState.Air || _treat.State == ThingState.Air;
    }

    void ThingStep(Thing o, double h, double bounce, bool ball)
    {
        var a = Host.Arena;
        double r = o.R, prevBottom = o.P.Y + r;
        o.V.Y += Gravity * h;
        o.P += o.V * h;
        o.Rolling = false;
        if (o.P.X < a.Left + r)
        {
            o.P.X = a.Left + r;
            if (o.V.X < 0) { Tick(o); o.V.X = -o.V.X * bounce; }
        }
        else if (o.P.X > a.Right - r)
        {
            o.P.X = a.Right - r;
            if (o.V.X > 0) { Tick(o); o.V.X = -o.V.X * bounce; }
        }
        if (o.P.Y < a.Top + r)
        {
            o.P.Y = a.Top + r;
            if (o.V.Y < 0) { Tick(o); o.V.Y = -o.V.Y * bounce; }
        }
        if (ball) o.Spin += o.V.X / r * h * 57.3;
        if (o.V.Y < 0) return;

        double ground = double.NaN;
        IntPtr hwnd = IntPtr.Zero;
        double bottom = o.P.Y + r;
        if (Host.Platforms.FindLanding(o.P.X, prevBottom, bottom, out var top) && top.Y - r * 2 >= a.Top)
        {
            ground = top.Y;
            hwnd = top.Hwnd;
        }
        else if (bottom >= a.Bottom)
        {
            ground = a.Bottom;
        }
        if (double.IsNaN(ground)) return;
        o.P.Y = ground - r;
        if (o.V.Y > 120)
        {
            Tick(o);
            o.V.Y = -o.V.Y * bounce;
            o.V.X *= 0.8;
            return;
        }
        o.V.Y = 0;
        o.Rolling = true;
        o.V.X *= ball ? 0.985 : 0.9;
        if (Math.Abs(o.V.X) >= BallStop) return;
        o.V = default;
        o.State = ThingState.Resting;
        o.Hwnd = hwnd;
        if (ball && o.Thrown && _fetch.Length == 0 && _watchBall)
        {
            _watchBall = false; // the watchers' verdict on where it ended up
            o.Thrown = false;
            FaceBall();
            Speak(Say.Call, 0.4);
            UpdateHud();
        }
    }

    void Tick(Thing o)
    {
        double speed = o.V.Length;
        if (speed > 200) PlayThrottled("bounce", Math.Min(0.35, speed / 5000), o == _ball ? 1.8 : 2.4);
    }

    /// <summary>A carried ball rides in the mouth (or ahead of a hamster's nose); the things are drawn where they are.</summary>
    void DrawThings()
    {
        if (_ball.State == ThingState.Carried)
            _ball.P = FetchStyleOf(Kind) == FetchStyle.Push ? new Vec2(_pos.X + _face * 21, _pos.Y - BallR) : Mouth + new Vec2(_face * 4, 2);
        foreach (var o in _things)
        {
            bool shown = o.State != ThingState.Hidden;
            o.Sprite.IsVisible = shown;
            if (!shown) continue;
            o.Sprite.Set(o.P, o.Spin);
            o.Shadow.IsVisible = o.State == ThingState.Resting || o.Rolling || (o.State == ThingState.Carried && FetchStyleOf(Kind) == FetchStyle.Push);
        }
        if (_flyShown) _fly.Set(_flyP + new Vec2(Math.Sin(_animT * 40) * 1.5, Math.Cos(_animT * 33) * 1.5));
    }

    // ------------------------------------------------------------------ simulation

    public override bool Update(double dt)
    {
        _animT += dt;
        TrackPointer();
        CheckSurface();
        CheckThings();
        if (_decide)
        {
            _decide = false;
            Decide();
        }
        if (_morning && Grounded && !_pressed && _act.Length == 0 && _goal.Length == 0)
        {
            _morning = false;
            if (_mode == Mode.Sleep) WakeUp();
            if (_mode == Mode.Walk) StopWalking();
            _lastStir = Now;
            StartAct("morning");
        }
        if (_pressed && _mode != Mode.Carried && (Host.Pointer - _pressPos).Length > DragStart) StartCarry();
        if (_mode == Mode.Carried) Carry(dt);
        if (_ballHeld) HoldBall();
        StepFetch(dt);
        WantTreat();
        StepGoal();

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
        bool things = StepThings(dt);
        if (_watchBall && _ball.State != ThingState.Air) _watchBall = false; // stopped some other way than landing

        bool acting = StepAct(dt);
        TidyFly();
        if (_mode == Mode.Sit && !_pressed && !acting)
        {
            if (_watchBall && _ball.State == ThingState.Air) FaceBall();
            else FaceCursor();
        }
        if (_mode != Mode.Carried) _swing *= Math.Max(0, 1 - dt * 8);
        _happyT = Math.Max(0, _happyT - dt);
        _squashT = Math.Max(0, _squashT - dt);
        bool hearts = UpdateHearts(dt);
        bool bubble = UpdateBubble(dt);
        bool visit = UpdateVisit(dt);
        bool tweens = Anims.Update(dt);
        DrawThings();
        Draw(dt);
        UpdateHudIfChanged();
        // sitting and sleeping are still: no frames needed until the behaviour timer or the user wakes us
        return _pressed || _ballHeld || (_mode is Mode.Walk or Mode.Air or Mode.Carried) || _happyT > 0 || _squashT > 0 || hearts || acting
            || things || bubble || visit || tweens || _goal.Length > 0 || _fetch.Length > 0 || _flyShown || Host.Lan.Connected;
    }

    /// <summary>A fly only hovers while the frog is after it; an act cut short by a click must not leave it buzzing for ever.</summary>
    void TidyFly()
    {
        if (_flyShown && _act is not ("fly" or "tongue")) _fly.IsVisible = _flyShown = false;
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
        ShowBubble(Bubble.Alarm, 1.2); // the window vanished from under it
        Speak(Say.Surprise, 0.4);
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
        double pace = IsHopper(kind) ? Math.Sin(Math.PI * HopPhase) * Math.PI / 2 : 1; // a hopper only moves while in the air
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
            if (zoomed) StartAct(kind == "dog" ? "pant" : kind == "cat" ? "groom" : kind == "hamster" ? "stuff" : "sniff"); // catching its breath
        }
    }

    void AirStep(double h)
    {
        var a = Host.Arena;
        double prevY = _pos.Y;
        _vel.Y += AirGravity * h;
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

    // ------------------------------------------------------------------ the thought bubble

    void BuildBubble()
    {
        _bubbleBox.RenderTransformOrigin = RelativePoint.TopLeft;
        _bubbleBox.RenderTransform = _bubbleAt;
        var cloud = Art.Brush(235, 255, 255, 255);
        var cloudInk = Art.Brush("#8A97B0");
        var trail = new Canvas { RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = _bubbleFlip };
        trail.Children.Add(Art.Circle(-9, 13, 3, cloud, cloudInk, 1));
        trail.Children.Add(Art.Circle(-14, 19, 1.8, cloud, cloudInk, 0.8));
        _bubbleBox.Children.Add(trail);
        _bubbleBox.Children.Add(Art.At(new Ellipse { Width = 34, Height = 26, Fill = cloud, Stroke = cloudInk, StrokeThickness = 1.2 }, -17, -13));

        var heart = Art.PathOf(HeartPath, HeartFill, HeartInk, 0.8);
        heart.RenderTransform = new ScaleTransform(1.15, 1.15);
        var z = Art.PathOf("M-4,-4 L4,-4 L-4,4 L4,4", null, Art.Brush("#5B74B8"), 2);
        var ball = new Canvas();
        ball.Children.Add(Art.Circle(0, 0, 6, Art.Brush("#E8505B"), Art.Brush("#8A2430"), 1));
        ball.Children.Add(Art.PathOf("M-5.5,0 Q0,3 5.5,0", null, Brushes.White, 2));
        var treat = Art.PathOf(BonePath, BoneFill, BoneInk, 1);
        var alarm = new Canvas();
        alarm.Children.Add(Art.PathOf("M0,-7 L0,2", null, Art.Brush("#E0506A"), 2.6));
        alarm.Children.Add(Art.Circle(0, 6, 1.7, Art.Brush("#E0506A")));
        foreach (var (kind, el) in new (Bubble, Control)[] { (Bubble.Heart, heart), (Bubble.Sleepy, z), (Bubble.Ball, ball), (Bubble.Treat, treat), (Bubble.Alarm, alarm) })
        {
            el.IsVisible = false;
            el.IsHitTestVisible = false;
            _bubbleBox.Children.Add(el);
            _bubbleItems[kind] = el;
        }
    }

    void ShowBubble(Bubble b, double life)
    {
        if (b == Bubble.None) return;
        _bubble = b;
        _bubbleT = 0;
        _bubbleLife = life;
        Host.Wake();
    }

    void HideBubble()
    {
        _bubble = Bubble.None;
        _bubbleBox.IsVisible = false;
    }

    /// <summary>Fades the bubble in and out above the head, kept inside the arena; true while it shows.</summary>
    bool UpdateBubble(double dt)
    {
        if (_bubble == Bubble.None) return false;
        _bubbleT += dt;
        if (_bubbleT >= _bubbleLife)
        {
            HideBubble();
            return false;
        }
        foreach (var (kind, el) in _bubbleItems) el.IsVisible = kind == _bubble;
        var a = Host.Arena;
        var p = Center + new Vec2(_face * 16, -48);
        p.X = Clamp(p.X, a.Left + 20, a.Right - 20);
        p.Y = Math.Max(a.Top + 16, p.Y);
        _bubbleAt.X = p.X;
        _bubbleAt.Y = p.Y;
        _bubbleFlip.ScaleX = _face;
        _bubbleBox.Opacity = Math.Min(1, _bubbleT / 0.25) * Math.Min(1, (_bubbleLife - _bubbleT) / 0.4);
        _bubbleBox.IsVisible = true;
        return true;
    }

    // ------------------------------------------------------------------ LAN: a co-worker's pet on a visit

    /// <summary>
    /// Sends this pet's state ten times a second and draws the other player's pet, faded, at the mirrored spot on our
    /// screen, posed the way theirs is. When the two are close the local pet says hello. True while a visitor shows.
    /// </summary>
    bool UpdateVisit(double dt)
    {
        if (!Host.Lan.Connected)
        {
            if (_visitSeen < VisitFade) HideVisitor();
            return false;
        }
        var a = Host.Arena;
        if ((_lanSendT += dt) >= VisitSendEvery && a.Width > 0 && a.Height > 0)
        {
            _lanSendT = 0;
            Host.Lan.Send(EncodeVisit(new Visit(Kind, (_pos.X - a.Left) / a.Width, (_pos.Y - a.Top) / a.Height, _face < 0 ? -1 : 1, _mode, _act)));
        }
        while (Host.Lan.TryReceive(out var msg))
        {
            if (!TryDecodeVisit(msg, out var v)) continue;
            if (v.Act != _visit.Act || v.Mode != _visit.Mode) _visitActAt = Now;
            _visit = v;
            _visitSeen = 0;
        }
        _visitSeen += dt;
        _visitAnimT += dt;
        if (_visitSeen >= VisitFade || _visit.Kind.Length == 0)
        {
            HideVisitor();
            return false;
        }
        if (_visitArt == null || _visitKind != _visit.Kind)
        {
            if (_visitArt != null) _visitLayer.Children.Remove(_visitArt.Root);
            _visitArt = BuildPet(_visit.Kind);
            _visitKind = _visit.Kind;
            _visitArt.Root.IsHitTestVisible = false;
            _visitLayer.Children.Insert(0, _visitArt.Root);
            _visitPos = default;
        }
        var feet = new Vec2(Clamp(a.Left + (1 - _visit.X) * a.Width, a.Left + HalfW, a.Right - HalfW), Clamp(a.Top + _visit.Y * a.Height, a.Top + Height, a.Bottom));
        _visitPos = _visitPos == default ? feet : _visitPos + (feet - _visitPos) * Math.Min(1, dt * 14); // ten updates a second, smoothed
        double face = -_visit.Face;
        var center = _visitPos - new Vec2(0, CenterLift);
        string kind = _visit.Kind, act = _visit.Act;
        double len = act == TrickFor(kind).Name ? TrickFor(kind).Seconds : ActLength(act);
        var pose = PoseOf(kind, _visit.Mode, act, Now - _visitActAt, len, _visitAnimT, 1, 0, 0, face, _visit.Mode == Mode.Air && IsFlyer(kind));
        Apply(_visitArt, pose, face, center, 0, _visit.Mode == Mode.Sleep, false, _visit.Mode is Mode.Sit or Mode.Walk or Mode.Sleep,
            _visit.Mode == Mode.Sleep && _night, center);
        double opacity = 0.55 * Math.Min(1, (VisitFade - _visitSeen) / 0.5);
        _visitArt.Root.Opacity = _visitLabel.Opacity = opacity;
        _visitArt.Root.IsVisible = _visitLabel.IsVisible = true;
        _visitLabel.Text = Host.Lan.PeerName;
        Canvas.SetLeft(_visitLabel, center.X - 20);
        Canvas.SetTop(_visitLabel, Math.Max(a.Top + 2, center.Y - 58));

        double dist = (center - Center).Length;
        if (dist < VisitR)
        {
            if (!_visiting)
            {
                _visiting = true;
                UpdateHud();
            }
            if (Now - _lastGreet > GreetAgain && _mode == Mode.Sit && !_pressed)
            {
                _lastGreet = Now; // a hello for the visitor
                if (Math.Abs(center.X - _pos.X) > 6) _face = center.X > _pos.X ? 1 : -1;
                Speak(Say.Hello);
                Hearts(3);
                ShowBubble(Bubble.Heart, 1.6);
                if (!_metThisVisit)
                {
                    _metThisVisit = true;
                    Host.Stats.Add("pet.visits");
                }
            }
        }
        else if (_visiting && dist > VisitR * 1.5)
        {
            _visiting = false;
            UpdateHud();
        }
        return true;
    }

    void HideVisitor()
    {
        _visitSeen = VisitFade;
        _visitPos = default; // the next visitor starts afresh rather than gliding in from where the last one stood
        if (_visitArt != null) _visitArt.Root.IsVisible = false;
        _visitLabel.IsVisible = false;
        _metThisVisit = false;
        if (_visiting)
        {
            _visiting = false;
            UpdateHud();
        }
    }

    // ------------------------------------------------------------------ visuals

    const string HeartPath = "M0,5 C-8,-1 -7,-8 -3,-8 C-1.5,-8 -0.4,-7 0,-5.8 C0.4,-7 1.5,-8 3,-8 C7,-8 8,-1 0,5 Z";
    const string BonePath = "M-6,-2 C-8,-4.5 -4,-6 -3,-3 L3,-3 C4,-6 8,-4.5 6,-2 C7,-1 7,1 6,2 C8,4.5 4,6 3,3 L-3,3 C-4,6 -8,4.5 -6,2 C-7,1 -7,-1 -6,-2 Z";
    static readonly IBrush HeartFill = Art.Brush("#FF5C8A");
    static readonly IBrush HeartInk = Art.Brush("#B8325A");
    static readonly IBrush BoneFill = Art.Brush("#F1DEB8");
    static readonly IBrush BoneInk = Art.Brush("#A07A48");

    /// <summary>The body language for one frame: the gait, the mood and the running action, for this kind of animal.</summary>
    static Pose PoseOf(string kind, Mode mode, string act, double actT, double actLen, double animT, double speed, double happyT, double squashT, double face, bool gliding)
    {
        var p = Pose.Rest;
        if (mode == Mode.Walk)
        {
            double pace = animT * Math.Sqrt(speed), s, hop;
            switch (kind)
            {
                case "bunny": // hop, hop
                    hop = Math.Sin(Math.PI * HopPhaseOf(kind, animT, speed));
                    p.Bob = -hop * 9;
                    p.StepA = p.StepB = -2 * hop;
                    break;
                case "frog": // long, high hops, legs tucked at the top
                    hop = Math.Sin(Math.PI * HopPhaseOf(kind, animT, speed));
                    p.Bob = -hop * 14;
                    p.StepA = p.StepB = -3 * hop;
                    p.Sy = 1 + 0.06 * hop;
                    p.Sx = 1 - 0.04 * hop;
                    break;
                case "parrot": // little hops with a flutter at the top of each
                    hop = Math.Sin(Math.PI * HopPhaseOf(kind, animT, speed));
                    p.Bob = -hop * 6;
                    p.StepA = p.StepB = -2 * hop;
                    p.Wings = 0.6 * hop;
                    p.Flap = Math.Sin(animT * 30) * 20 * hop;
                    break;
                case "duck" or "penguin": // a waddle, rocking side to side
                    s = Math.Sin(pace * 8);
                    p.Bob = -Math.Abs(s) * 1.5;
                    p.Angle = s * (kind == "penguin" ? 10 : 7);
                    p.StepA = -2.5 * Math.Max(0, s);
                    p.StepB = -2.5 * Math.Max(0, -s);
                    break;
                case "owl": // a slow, upright waddle
                    s = Math.Sin(pace * 7);
                    p.Bob = -Math.Abs(s) * 1.5;
                    p.Angle = s * 5;
                    p.StepA = -2 * Math.Max(0, s);
                    p.StepB = -2 * Math.Max(0, -s);
                    break;
                case "dog": // a bouncy trot, tail going
                    s = Math.Sin(pace * 13);
                    p.Bob = -Math.Abs(s) * 3.2;
                    p.Tail = s * 10;
                    p.StepA = -2.4 * Math.Max(0, s);
                    p.StepB = -2.4 * Math.Max(0, -s);
                    break;
                case "hamster": // a scurry: quick little steps, low to the ground
                    s = Math.Sin(pace * 22);
                    p.Bob = -Math.Abs(s) * 1.2;
                    p.StepA = -1.6 * Math.Max(0, s);
                    p.StepB = -1.6 * Math.Max(0, -s);
                    p.Cheeks = 1 + 0.03 * s;
                    break;
                case "turtle": // a crawl, head nodding along
                    s = Math.Sin(pace * 4.5);
                    p.Bob = -Math.Abs(s) * 0.8;
                    p.Angle = s * 2;
                    p.HeadY = s * 1.5;
                    p.HeadX = 1.5 * Math.Max(0, s);
                    p.StepA = -1.5 * Math.Max(0, s);
                    p.StepB = -1.5 * Math.Max(0, -s);
                    break;
                case "dragon": // a stomp, tail swaying behind
                    s = Math.Sin(pace * 7);
                    p.Bob = -Math.Abs(s) * 3.5;
                    p.Angle = s * 2.5;
                    p.Tail = Math.Sin(pace * 3.5) * 12;
                    p.StepA = -3 * Math.Max(0, s);
                    p.StepB = -3 * Math.Max(0, -s);
                    break;
                default:
                    s = Math.Sin(pace * 11);
                    p.Bob = -Math.Abs(s) * 2.5;
                    p.Tail = Math.Sin(pace * 5.5) * 6;
                    p.StepA = -2.2 * Math.Max(0, s);
                    p.StepB = -2.2 * Math.Max(0, -s);
                    break;
            }
        }
        else if (mode == Mode.Carried)
        {
            double s = Math.Sin(animT * 16);
            p.StepA = 2 * s;
            p.StepB = -2 * s;
        }
        else if (mode == Mode.Sleep)
        {
            p.Sy = 0.9; // curled up
            p.Sx = 1.06;
        }
        else if (mode == Mode.Air)
        {
            if (gliding)
            {
                p.Wings = 1; // wings out, a slow beat
                p.Flap = Math.Sin(animT * (kind == "owl" ? 7 : 12)) * (kind == "owl" ? 14 : 22);
            }
            else if (kind == "dragon")
            {
                p.Wings = 0.7; // it flaps, for all the good it does
                p.Flap = Math.Sin(animT * 9) * 18;
            }
        }
        if (happyT > 0)
        {
            double k = 1 - happyT / HappyTime;
            p.Bob -= Math.Sin(Math.PI * Math.Min(1, k / 0.7)) * 14;
        }
        if (squashT > 0)
        {
            double k = squashT / SquashTime;
            p.Sy = 1 - 0.18 * k;
            p.Sx = 1 + 0.12 * k;
        }
        else if (mode == Mode.Carried)
        {
            p.Sy = 1.06;
            p.Sx = 0.95;
        }
        if (act.Length == 0) return p;

        double kk = actLen > 0 ? Math.Min(1, actT / actLen) : 1, env = Math.Sin(Math.PI * kk), on = Fade(kk), t = actT, c;
        switch (act)
        {
            case "stretch": // long and low, then back
                p.Sx = 1 + 0.3 * env;
                p.Sy = 1 - 0.2 * env;
                break;
            case "spin": // round after its tail
                p.Angle = 360 * kk * face;
                break;
            case "binky": // a twist in mid-air
                p.Angle = Math.Sin(kk * Math.PI * 2) * 25;
                break;
            case "flap": // three little flaps up and down
                p.Bob -= Math.Abs(Math.Sin(kk * Math.PI * 3)) * 11;
                p.Sx = 1 + 0.08 * Math.Abs(Math.Sin(kk * Math.PI * 6));
                break;
            case "pounce": // crouch down before the leap
                p.Sy = 1 - 0.28 * Math.Min(1, kk * 1.4);
                p.Sx = 1 + 0.16 * Math.Min(1, kk * 1.4);
                break;
            case "slide": // flat on the belly, nose first
                p.Sy = 1 - 0.25 * env;
                p.Angle = face * 72 * Math.Sin(Math.PI * Math.Min(1, kk * 1.3));
                break;
            case "groom": // a paw up to the mouth, licked in little strokes
                p.StepB = on * (-11 + Math.Sin(t * 10) * 2);
                p.FrontX = -2 * on;
                p.Bob += Math.Sin(t * 10) * 0.8 * on;
                p.Eyes = "happy";
                break;
            case "knead": // making biscuits
                p.StepA = -3 * Math.Max(0, Math.Sin(t * 8));
                p.StepB = -3 * Math.Max(0, -Math.Sin(t * 8));
                p.Bob -= Math.Abs(Math.Sin(t * 8)) * 0.6;
                p.Eyes = "happy";
                break;
            case "sniff": // nose down to the ground
                p.Bob += Math.Sin(t * 20) * 0.8 * on;
                p.Angle = face * (10 + Math.Sin(t * 20) * 2) * on;
                break;
            case "scratch": // a back foot up to the ear, going like mad
                p.StepA = on * (-13 + Math.Sin(t * 32) * 3);
                p.BackX = -3 * on;
                p.Angle = -face * 12 * on;
                p.Eyes = "happy";
                break;
            case "wag":
                p.Tail = Math.Sin(t * 26) * 24 * on;
                p.Bob -= Math.Abs(Math.Sin(t * 13)) * 1.2;
                if (kk < 0.6) p.Eyes = "happy";
                break;
            case "pant":
                p.Bob += Math.Sin(t * 30) * 1.2;
                p.Sy = 1 + 0.025 * Math.Sin(t * 30);
                break;
            case "preen":
                p.StepB = -4 * on;
                p.Angle = Math.Sin(t * 9) * 7 * on;
                p.Eyes = "happy";
                break;
            case "nod":
                p.Angle = face * 22 * Math.Abs(Math.Sin(kk * Math.PI * 3));
                break;
            case "shake":
                p.Sx = 1 + 0.06 * Math.Abs(Math.Sin(t * 50)) * (1 - kk);
                p.Tail = Math.Sin(t * 50) * 20 * (1 - kk);
                p.Angle = Math.Sin(t * 50) * 14 * (1 - kk);
                break;
            case "flop": // over on its side
                p.Sy = 1 - 0.1 * on;
                p.Angle = -face * 80 * on;
                p.Eyes = "happy";
                break;
            case "call": // stretched tall, head back, braying
                p.Sy = 1 + 0.18 * on;
                p.Sx = 1 - 0.08 * on;
                p.Bob -= Math.Abs(Math.Sin(t * 9)) * 1.5 * on;
                p.Angle = -face * 28 * on;
                break;
            case "yawn": // stretched up tall, eyes squeezed shut
                p.Sy = 1 + 0.12 * env;
                p.Sx = 1 - 0.06 * env;
                p.Angle = -face * 10 * env;
                p.Eyes = "sleep";
                break;
            case "stalk": // low to the ground, tail tip twitching, then the wiggle before the pounce
                c = Math.Min(1, kk * 3);
                p.Sy = 1 - 0.2 * c;
                p.Sx = 1 + 0.12 * c;
                p.Tail = Math.Sin(t * 14) * 9;
                if (kk > 0.6) p.Angle = Math.Sin(t * 24) * 3;
                break;
            case "chatter": // up on its toes, jaw going
                p.Sy = 1.05;
                p.Bob += Math.Sin(t * 55) * 0.8;
                p.Tail = Math.Sin(t * 12) * 8;
                break;
            case "puff": // fur on end
                c = Math.Min(1, kk * 6) * (kk > 0.8 ? (1 - kk) / 0.2 : 1);
                p.Sx = p.Sy = 1 + 0.18 * c;
                p.Tail = -25 * c;
                break;
            case "thump": // one back foot slammed down
                if (kk < 0.3) p.StepA = -7 * Math.Sin(Math.PI * kk / 0.3);
                if (kk > 0.25 && kk < 0.45) p.Sy = 1 - 0.1 * Math.Sin(Math.PI * (kk - 0.25) / 0.2);
                break;
            case "swat":
                p.StepB = -12 * env;
                p.FrontX = 7 * env;
                break;
            // hamster
            case "stuff": // the cheeks fill up in three gulps
                p.Cheeks = 1 + 0.45 * Math.Min(1, Math.Floor(kk * 3.999) / 3) * on + 0.04 * Math.Sin(t * 30) * on;
                p.Bob += Math.Sin(t * 14) * 0.6 * on;
                if (kk > 0.7) p.Eyes = "happy";
                break;
            case "circle": // a scurry round in a ring (the run itself is in StepAct)
                p.Bob = -Math.Abs(Math.Sin(t * 24)) * 1.5;
                p.Angle = Math.Sin(kk * Math.PI * 2) * 8;
                p.StepA = -1.6 * Math.Max(0, Math.Sin(t * 24));
                p.StepB = -1.6 * Math.Max(0, -Math.Sin(t * 24));
                break;
            case "wheel": // spinning like a wheel, feet going
                p.Angle = face * 720 * kk;
                p.Bob -= Math.Abs(Math.Sin(kk * Math.PI * 4)) * 2;
                p.StepA = -2 * Math.Max(0, Math.Sin(t * 40));
                p.StepB = -2 * Math.Max(0, -Math.Sin(t * 40));
                break;
            // turtle
            case "hide" or "shell": // head and legs pulled into the shell (the trick is quicker and wobbles)
                c = act == "hide" ? (kk < 0.25 ? kk / 0.25 : kk > 0.8 ? (1 - kk) / 0.2 : 1) : (kk < 0.3 ? kk / 0.3 : kk > 0.7 ? (1 - kk) / 0.3 : 1);
                p.HeadX = -12 * c;
                p.HeadScale = 1 - 0.68 * c;
                p.FrontX = -7 * c;
                p.BackX = 7 * c;
                p.Sy = 1 - 0.09 * c;
                p.Sx = 1 + 0.05 * c;
                if (act == "shell") p.Angle = Math.Sin(t * 20) * 4 * c;
                if (c > 0.9) p.Eyes = "sleep";
                break;
            case "neck": // the neck stretched out and up for a look
                p.HeadX = 9 * on;
                p.HeadY = -6 * on;
                p.HeadAngle = -12 * on;
                break;
            case "nibble": // head down, chewing
                p.HeadY = 4 * on + Math.Max(0, Math.Sin(t * 12)) * 1.5 * on;
                p.HeadAngle = 10 * on;
                if (kk > 0.4) p.Eyes = "happy";
                break;
            // parrot
            case "bob": // head bobbing to a beat only it can hear
                p.HeadY = -Math.Abs(Math.Sin(t * 9)) * 5 * on;
                p.Bob -= Math.Abs(Math.Sin(t * 9)) * 0.8 * on;
                break;
            case "shriek": // stretched up, wings half out, head back
                p.Sy = 1 + 0.15 * on;
                p.Sx = 1 - 0.06 * on;
                p.HeadAngle = -20 * on;
                p.Wings = 0.5 * on;
                p.Flap = Math.Sin(t * 40) * 10 * on;
                p.Bob -= Math.Abs(Math.Sin(t * 22)) * 1;
                break;
            case "loop": // over and round in the air
                p.Angle = -face * 360 * (kk * kk * (3 - 2 * kk));
                p.Wings = 1;
                p.Flap = Math.Sin(t * 24) * 25;
                break;
            // frog
            case "throat": // the throat sac puffing in and out
                p.Throat = 1 + 0.9 * Math.Abs(Math.Sin(t * 4)) * on;
                break;
            case "fly": // a quick flick of the tongue
                p.Tongue = (kk < 0.5 ? kk / 0.5 : (1 - kk) / 0.5) * 2.6;
                if (kk > 0.6) p.Eyes = "happy";
                break;
            case "tongue": // the long one: out, held on the fly, and back
                p.Tongue = (kk < 0.35 ? kk / 0.35 : kk < 0.55 ? 1 : (1 - kk) / 0.45) * 4.6;
                if (kk > 0.6) p.Eyes = "happy";
                break;
            case "blink":
                if (kk > 0.2 && kk < 0.8) p.Eyes = "sleep";
                break;
            // owl
            case "swivel": // the head turns, the body does not
                p.HeadAngle = Math.Sin(kk * Math.PI * 2) * 38;
                break;
            case "fluff": // feathers puffed out, a shuffle of the wings
                p.Sx = 1 + 0.16 * env;
                p.Sy = 1 + 0.1 * env;
                p.Wings = 0.5 * env;
                p.Flap = Math.Sin(t * 26) * 12 * env;
                p.Eyes = "happy";
                break;
            case "slowblink":
                if (env > 0.55) p.Eyes = "sleep";
                p.Bob += 0.5 * env;
                break;
            case "headspin": // right round
                p.HeadAngle = 360 * (kk * kk * (3 - 2 * kk));
                break;
            // dragon
            case "smoke": // a snort, nose up
                p.Sy = 1 + 0.08 * Math.Max(0, Math.Sin(t * 6)) * on;
                p.HeadAngle = -8 * on;
                break;
            case "curl": // curled up round its tail
                c = Math.Min(1, kk * 2.5) * (kk > 0.85 ? (1 - kk) / 0.15 : 1);
                p.Sy = 1 - 0.15 * c;
                p.Sx = 1 + 0.1 * c;
                p.Tail = -45 * c;
                p.HeadY = 3 * c;
                if (c > 0.6) p.Eyes = "happy";
                break;
            case "fire": // rears back, wings out, and lets go
                c = kk < 0.3 ? kk / 0.3 : kk > 0.75 ? (1 - kk) / 0.25 : 1;
                p.Sy = 1 + 0.1 * c;
                p.Sx = 1 - 0.05 * c;
                p.HeadAngle = -14 * c;
                p.Wings = c;
                p.Flap = Math.Sin(t * 10) * 8 * c;
                p.Angle = -face * 6 * c;
                if (kk > 0.3 && kk < 0.7) p.Eyes = "happy";
                break;
            // shared
            case "eat": // chewing
                p.Bob += Math.Abs(Math.Sin(t * 18)) * 1.2 * on;
                p.Sx = 1 + 0.04 * Math.Sin(t * 18) * on;
                p.HeadY = 3 * on;
                p.Eyes = "happy";
                break;
            case "morning": // the long stretch, front paws forward, eyes shut, then a happy look
                c = Math.Sin(Math.PI * kk);
                p.Sx = 1 + 0.35 * c;
                p.Sy = 1 - 0.22 * c;
                p.FrontX = 6 * c;
                p.BackX = -4 * c;
                p.Tail = 20 * c;
                p.Eyes = kk < 0.7 ? "sleep" : "happy";
                break;
            case "beg": // up on its haunches, front paws up
                p.Sy = 1 + 0.16 * on;
                p.Sx = 1 - 0.06 * on;
                p.StepB = -9 * on + Math.Sin(t * 10) * 1.5 * on;
                p.FrontX = 3 * on;
                p.HeadAngle = -8 * on;
                break;
            case "settle": // a little wriggle down into place
                p.Sy = 1 - 0.08 * env;
                p.Sx = 1 + 0.06 * env;
                p.Eyes = "happy";
                break;
        }
        return p;
    }

    void Draw(double dt)
    {
        var pose = PoseOf(Kind, _mode, _act, _actT, _actLen, _animT, _speed, _happyT, _squashT, _face, Gliding);
        _wingK += (pose.Wings - _wingK) * (dt <= 0 ? 1 : Math.Min(1, dt * 12));
        pose.Wings = _wingK;
        Apply(_art, pose, _face, Center, _swing, _mode == Mode.Sleep, _happyT > 0, Grounded, _mode == Mode.Sleep && _night, Host.Pointer);
    }

    /// <summary>Puts a pose onto a drawing: the body, the head, the feet, the extras each animal has, and the eyes.</summary>
    static void Apply(PetArt art, Pose p, double face, Vec2 center, double swing, bool asleep, bool pleased, bool grounded, bool nightcap, Vec2 pointer)
    {
        art.Root.Set(center, swing + p.Angle);
        art.BodyScale.ScaleX = face * p.Sx;
        art.BodyScale.ScaleY = p.Sy;
        art.BodyShift.Y = p.Bob + CenterLift * (1 - p.Sy); // squash toward the feet, not the middle
        art.Tail.Angle = p.Tail;
        art.FootA.X = p.BackX;
        art.FootA.Y = p.StepA;
        art.FootB.X = p.FrontX;
        art.FootB.Y = p.StepB;
        art.HeadShift.X = art.HeadBase.X + p.HeadX;
        art.HeadShift.Y = art.HeadBase.Y + p.HeadY;
        art.HeadTurn.Angle = p.HeadAngle;
        art.HeadScale.ScaleX = art.HeadScale.ScaleY = p.HeadScale;
        if (art.Cheeks != null) art.Cheeks.ScaleX = art.Cheeks.ScaleY = p.Cheeks;
        if (art.Throat != null) art.Throat.ScaleX = art.Throat.ScaleY = p.Throat;
        if (art.Tongue != null && art.TongueEl != null)
        {
            art.Tongue.ScaleX = Math.Max(0.01, p.Tongue);
            art.TongueEl.IsVisible = p.Tongue > 0.05;
        }
        if (art.Wings != null && art.WingSpread != null && art.WingFlap != null && art.WingFlapL != null)
        {
            art.Wings.IsVisible = p.Wings > 0.03;
            art.WingSpread.ScaleY = Math.Max(0.01, p.Wings);
            art.WingFlap.Angle = p.Flap;
            art.WingFlapL.Angle = -p.Flap;
        }

        bool sleeping = asleep || p.Eyes == "sleep", happy = !sleeping && (pleased || p.Eyes == "happy");
        art.EyesSleep.IsVisible = sleeping;
        art.EyesHappy.IsVisible = happy;
        art.EyesOpen.IsVisible = !sleeping && !happy;
        art.Zz.IsVisible = asleep;
        art.Shadow.IsVisible = grounded;
        art.Nightcap.IsVisible = nightcap;
        if (sleeping || happy) return;

        var d = pointer - (center + new Vec2(face * 2.5, -3 + p.Bob));
        double len = d.Length;
        var look = len < 1 ? default : d * (Math.Min(1, len / 60) * PupilReach / len);
        art.PupilL.X = art.PupilR.X = look.X * face; // the body canvas is mirrored when facing left
        art.PupilL.Y = art.PupilR.Y = look.Y;
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
            "bray" => 3, "purr" or "pant" or "snore" => 1.5, "rumble" or "croak" or "hoots" or "thiss" => 1.2,
            "whine" or "yawn" or "scream" or "quacks" or "meow" or "hello" or "roar" => 0.9, _ => 0.08,
        };
        if (_lastSound.TryGetValue(name, out double t) && now - t < gap) return;
        _lastSound[name] = now;
        Host.Sound.Play(name, vol, pitch);
    }

    // ------------------------------------------------------------------ the drawings

    /// <summary>A round animal facing +x, origin at the body center, feet at y = CenterLift. Each kind gets its own silhouette.</summary>
    static PetArt BuildPet(string kind)
    {
        bool dog = kind == "dog", duck = kind == "duck", bunny = kind == "bunny", penguin = kind == "penguin", fox = kind == "fox";
        bool hamster = kind == "hamster", turtle = kind == "turtle", parrot = kind == "parrot", frog = kind == "frog", owl = kind == "owl", dragon = kind == "dragon";
        var ink = Art.Brush(kind switch
        {
            "dog" => "#5E3B22", "duck" => "#B7791F", "bunny" => "#7D7068", "penguin" => "#151A22", "fox" => "#7A3A12", "hamster" => "#8A5A2B",
            "turtle" => "#4A6A2E", "parrot" => "#1F5A3A", "frog" => "#2F6B2A", "owl" => "#5A3E23", "dragon" => "#2A5C6B", _ => "#8A4F2A",
        });
        var fur = Art.Brush(kind switch
        {
            "dog" => "#C98B55", "duck" => "#FFD54A", "bunny" => "#E6E1DC", "penguin" => "#2E3645", "fox" => "#E8742A", "hamster" => "#F2B96B",
            "turtle" => "#8FBF5A", "parrot" => "#3FBF6B", "frog" => "#6CC24A", "owl" => "#B08A5A", "dragon" => "#4FB3A8", _ => "#F2A566",
        });
        var innerEar = Art.Brush("#FF9FB0");
        var eyeInk = Art.Brush("#2B2320");
        var orange = Art.Brush("#FF8C1A");
        var beakInk = Art.Brush("#C2610C");
        var s = new Sprite();

        bool wide = dragon || turtle;
        var shadow = Art.At(new Ellipse { Width = wide ? 38 : 32, Height = 6, Fill = Art.Brush(50, 0, 0, 0) }, wide ? -19 : -16, CenterLift - 3);
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
        var (tailX, tailY) = kind switch
        {
            "bunny" => (-19.0, 6.0), "penguin" => (-16.0, 10.0), "fox" => (-16.0, 8.0), "duck" => (-17.0, 7.0), "dog" => (-17.0, 6.0),
            "hamster" => (-19.0, 8.0), "turtle" => (-19.0, 9.0), "parrot" => (-17.0, 7.0), "owl" => (-16.0, 8.0), "dragon" => (-16.0, 8.0), _ => (-16.0, 9.0),
        };
        var tailTurn = new RotateTransform { CenterX = tailX, CenterY = tailY };
        var tailBox = new Canvas { RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = tailTurn };
        body.Children.Add(tailBox);

        // wings for the birds and the dragon: behind the body, folded flat until they spread; each beats about its own shoulder
        ScaleTransform? wingSpread = null;
        RotateTransform? wingFlap = null, wingFlapL = null;
        Canvas? wings = null;
        if (parrot || owl || dragon)
        {
            wingSpread = new ScaleTransform(1, 0.01);
            wingFlap = new RotateTransform { CenterX = 8, CenterY = -8 };
            wingFlapL = new RotateTransform { CenterX = -6, CenterY = -8 };
            wings = new Canvas
            {
                RenderTransformOrigin = RelativePoint.TopLeft, IsVisible = false,
                RenderTransform = new TransformGroup { Children = { new TranslateTransform(0, 8), wingSpread, new TranslateTransform(0, -8) } },
            };
            var wingFill = dragon ? Art.Brush("#2A7F80") : parrot ? Art.Brush("#2E9E5A") : Art.Brush("#8C6A40");
            var left = new Canvas { RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = wingFlapL };
            var right = new Canvas { RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = wingFlap };
            if (dragon)
            {
                // bat wings: a spine with a scalloped membrane
                left.Children.Add(Art.PathOf("M-6,-8 C-16,-30 -40,-30 -44,-14 C-38,-18 -32,-16 -30,-8 C-26,-14 -20,-14 -18,-6 C-14,-10 -10,-9 -6,-8 Z", wingFill, ink, 1.2));
                right.Children.Add(Art.PathOf("M8,-8 C18,-30 42,-30 46,-14 C40,-18 34,-16 32,-8 C28,-14 22,-14 20,-6 C16,-10 12,-9 8,-8 Z", wingFill, ink, 1.2));
            }
            else
            {
                // feathered wings: a few long flight feathers each
                left.Children.Add(Art.PathOf("M-6,-8 C-20,-26 -42,-22 -46,-10 L-38,-12 C-40,-6 -34,-4 -28,-6 C-30,-2 -22,0 -14,-4 C-14,0 -8,0 -6,-8 Z", wingFill, ink, 1.2));
                right.Children.Add(Art.PathOf("M8,-8 C22,-26 44,-22 48,-10 L40,-12 C42,-6 36,-4 30,-6 C32,-2 24,0 16,-4 C16,0 10,0 8,-8 Z", wingFill, ink, 1.2));
            }
            wings.Children.Add(left);
            wings.Children.Add(right);
            body.Children.Add(wings);
        }

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
        else if (hamster)
        {
            tailBox.Children.Add(Art.Circle(-19, 8, 2.8, fur, ink, 1.1)); // a nub of a tail
            body.Children.Add(Art.Circle(-11, -16, 5.5, fur, ink, 1.2)); // round ears, pink inside
            body.Children.Add(Art.Circle(13, -16, 5.5, fur, ink, 1.2));
            body.Children.Add(Art.Circle(-11, -16, 3, innerEar));
            body.Children.Add(Art.Circle(13, -16, 3, innerEar));
        }
        else if (turtle)
        {
            tailBox.Children.Add(Art.PathOf("M-18,8 L-28,11 L-18,14 Z", fur, ink, 1.1)); // a pointed little tail
        }
        else if (parrot)
        {
            var red = Art.Brush("#E23D3D");
            tailBox.Children.Add(Art.PathOf("M-17,5 L-36,2 L-33,8 L-38,12 L-17,12 Z", red, ink, 1.2)); // long tail feathers
            tailBox.Children.Add(Art.PathOf("M-24,7 L-34,9 L-25,11 Z", Art.Brush("#3A6FD8")));
            body.Children.Add(Art.PathOf("M-3,-16 C-6,-27 2,-32 4,-25 C6,-32 13,-30 10,-19 C14,-24 18,-21 14,-15 Z", red, ink, 1.2)); // crest
        }
        else if (owl)
        {
            tailBox.Children.Add(Art.PathOf("M-16,6 L-25,15 L-13,14 Z", fur, ink, 1.1));
            body.Children.Add(Art.PathOf("M-15,-11 L-17,-27 L-5,-16 Z", fur, ink, 1.3)); // ear tufts
            body.Children.Add(Art.PathOf("M10,-16 L20,-27 L19,-11 Z", fur, ink, 1.3));
        }
        else if (dragon)
        {
            const string tail = "M-16,8 C-32,12 -40,-2 -32,-16";
            var spike = Art.Brush("#E8D9A0");
            tailBox.Children.Add(Art.PathOf(tail, null, ink, 9));
            tailBox.Children.Add(Art.PathOf(tail, null, fur, 6.2));
            tailBox.Children.Add(Art.PathOf("M-27,4 L-31,-4 L-24,-2 Z M-35,-6 L-41,-13 L-33,-12 Z M-31,-15 L-33,-24 L-27,-19 Z", spike, ink, 1)); // spikes down the tail
            body.Children.Add(Art.PathOf("M-13,-13 C-17,-24 -12,-31 -8,-24 L-7,-14 Z", spike, ink, 1.2)); // horns
            body.Children.Add(Art.PathOf("M9,-14 C11,-25 17,-30 16,-21 L14,-12 Z", spike, ink, 1.2));
        }
        else if (!frog)
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
        string[] shades = kind switch
        {
            "dog" => new[] { "#F0D2B0", "#D39A63", "#A8703F" }, "duck" => new[] { "#FFF6C2", "#FFD84D", "#F2B200" },
            "bunny" => new[] { "#FFFFFF", "#ECE7E2", "#C9C1B9" }, "penguin" => new[] { "#5A6478", "#2E3645", "#191E28" },
            "fox" => new[] { "#FFC08A", "#EF8436", "#C4561A" }, "hamster" => new[] { "#FFF0D6", "#F4B860", "#D28A3A" },
            "turtle" => new[] { "#D9BE86", "#9C7A48", "#5E4424" }, "parrot" => new[] { "#B8F2C6", "#3FBF6B", "#1F8F4A" },
            "frog" => new[] { "#D6F5B8", "#6CC24A", "#3E8F2E" }, "owl" => new[] { "#F0DFC3", "#B08A5A", "#7A5A36" },
            "dragon" => new[] { "#C5F1E8", "#4FB3A8", "#2A7F80" }, _ => new[] { "#FFE3C2", "#F7B37A", "#E08A4E" },
        };
        furFill.GradientStops.Add(new GradientStop(Color.Parse(shades[0]), 0));
        furFill.GradientStops.Add(new GradientStop(Color.Parse(shades[1]), 0.6));
        furFill.GradientStops.Add(new GradientStop(Color.Parse(shades[2]), 1));
        body.Children.Add(Art.At(new Ellipse { Width = 40, Height = 34, Fill = furFill, Stroke = ink, StrokeThickness = 1.3 }, -20, -17));
        if (turtle)
        {
            // the body is the shell: plates on top and a rim underneath
            var shellInk = Art.Brush("#5E4424");
            body.Children.Add(Art.PathOf("M-8,-9 L0,-13 L8,-9 L8,-1 L0,3 L-8,-1 Z M0,-13 L0,-17 M8,-9 L15,-12 M8,-1 L16,2 M0,3 L0,10 M-8,-1 L-16,2 M-8,-9 L-15,-12", null, shellInk, 1.1));
            body.Children.Add(Art.At(new Ellipse { Width = 42, Height = 12, Fill = Art.Brush("#7A5A36"), Stroke = ink, StrokeThickness = 1.2 }, -21, 6));
        }
        else if (penguin) body.Children.Add(Art.At(new Ellipse { Width = 27, Height = 27, Fill = Art.Brush("#F4F6F8") }, -10, -12)); // white front
        else if (fox) body.Children.Add(Art.At(new Ellipse { Width = 24, Height = 15, Fill = Art.Brush(235, 255, 248, 238) }, -8, 1)); // white chin
        else if (dragon) body.Children.Add(Art.At(new Ellipse { Width = 22, Height = 14, Fill = Art.Brush("#F5E6A8") }, -9, 3)); // belly plate
        else if (parrot) body.Children.Add(Art.At(new Ellipse { Width = 20, Height = 12, Fill = Art.Brush("#F5D65A") }, -7, 4)); // yellow chest
        else if (frog) body.Children.Add(Art.At(new Ellipse { Width = 24, Height = 12, Fill = Art.Brush(210, 234, 247, 208) }, -9, 5)); // pale belly
        else if (owl)
        {
            body.Children.Add(Art.At(new Ellipse { Width = 32, Height = 24, Fill = Art.Brush(200, 240, 223, 195) }, -13.5, -15)); // facial disc
            body.Children.Add(Art.PathOf("M-8,8 L-5,11 L-2,8 M0,10 L3,13 L6,10 M8,8 L11,11 L14,8", null, Art.Brush(150, 90, 62, 35), 1)); // chest feathers
        }
        else body.Children.Add(Art.At(new Ellipse { Width = 22, Height = 12, Fill = Art.Brush(190, 255, 244, 228) }, -8, 3));
        body.Children.Add(Art.At(new Ellipse { Width = 9, Height = 5, Fill = Art.Brush(120, 255, 255, 255), RenderTransform = new RotateTransform(-30) }, -14, -12));

        // the head: everything on the face, so it can turn (owl), shift (turtle, parrot) or shrink into the shell
        var headScale = new ScaleTransform(1, 1);
        var headTurn = new RotateTransform { CenterX = 2.5, CenterY = -3 };
        var headShift = new TranslateTransform();
        var headBase = turtle ? new Vec2(16, 3) : default;
        headShift.X = headBase.X;
        headShift.Y = headBase.Y;
        var head = new Canvas { RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = new TransformGroup { Children = { headScale, headTurn, headShift } } };
        body.Children.Add(head);
        if (turtle) head.Children.Add(Art.Circle(2.5, -2, 9.5, fur, ink, 1.2)); // the head itself, out in front of the shell
        if (frog)
        {
            head.Children.Add(Art.Circle(-4, -12, 6.8, fur, ink, 1.2)); // eyes on top, on bumps
            head.Children.Add(Art.Circle(9, -12, 6.8, fur, ink, 1.2));
        }

        var blush = Art.Brush(110, 255, 110, 150);
        if (turtle)
        {
            head.Children.Add(Art.At(new Ellipse { Width = 5, Height = 3, Fill = blush }, -5, 1));
            head.Children.Add(Art.At(new Ellipse { Width = 5, Height = 3, Fill = blush }, 6.5, 1));
        }
        else
        {
            head.Children.Add(Art.At(new Ellipse { Width = 7, Height = 4, Fill = blush }, -12.5, 3));
            head.Children.Add(Art.At(new Ellipse { Width = 7, Height = 4, Fill = blush }, 10.5, 3));
        }
        ScaleTransform? cheeks = null, throat = null, tongue = null;
        Control? tongueEl = null;
        if (hamster)
        {
            // cheek pouches either side of the face, scaled about the face when it stuffs them
            cheeks = new ScaleTransform(1, 1);
            var cheekBox = new Canvas
            {
                RenderTransformOrigin = RelativePoint.TopLeft,
                RenderTransform = new TransformGroup { Children = { new TranslateTransform(-2.5, -4), cheeks, new TranslateTransform(2.5, 4) } },
            };
            var cheek = Art.Brush("#FFE1B3");
            cheekBox.Children.Add(Art.At(new Ellipse { Width = 13, Height = 11, Fill = cheek, Stroke = ink, StrokeThickness = 1.1 }, -18, -1));
            cheekBox.Children.Add(Art.At(new Ellipse { Width = 13, Height = 11, Fill = cheek, Stroke = ink, StrokeThickness = 1.1 }, 10, -1));
            head.Children.Add(cheekBox);
        }
        if (duck) head.Children.Add(Art.PathOf("M-1,1 L22,3 L-1,8 Z", orange, beakInk, 1.1)); // beak
        else if (penguin) head.Children.Add(Art.PathOf("M0,1 L13,3.5 L0,6.5 Z", orange, beakInk, 1)); // small beak
        else if (parrot)
        {
            head.Children.Add(Art.PathOf("M0,-2 C10,-4 15,3 10,8 C8,5 5,4 1,4 Z", Art.Brush("#D9A441"), beakInk, 1)); // hooked beak
            head.Children.Add(Art.Circle(5, 0, 0.9, ink));
        }
        else if (owl) head.Children.Add(Art.PathOf("M0,0 L5,0 L2.5,5.5 Z", Art.Brush("#E8B23A"), beakInk, 0.8));
        else if (frog)
        {
            throat = new ScaleTransform(1, 1);
            var sac = Art.At(new Ellipse { Width = 16, Height = 9, Fill = Art.Brush("#EAF7D0"), Stroke = ink, StrokeThickness = 1 }, -5.5, 4);
            sac.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            sac.RenderTransform = throat;
            head.Children.Add(sac);
            head.Children.Add(Art.PathOf("M-9,4 Q2.5,11 14,4", null, Art.Brush("#2F6B2A"), 1.4)); // the wide mouth
            tongue = new ScaleTransform(0.01, 1);
            var tongueLine = Art.PathOf("M4,6 L13,6", null, Art.Brush("#E0506A"), 3);
            tongueLine.RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative);
            tongueLine.RenderTransform = tongue;
            tongueLine.IsVisible = false;
            head.Children.Add(tongueLine);
            tongueEl = tongueLine;
        }
        else if (turtle) head.Children.Add(Art.PathOf("M3,3 Q7,6 11,2.5", null, Art.Brush("#3F5A2A"), 1.1));
        else if (dragon)
        {
            head.Children.Add(Art.PathOf("M-2,4 Q4,8.5 11,4", null, Art.Brush("#1E4650"), 1.2));
            head.Children.Add(Art.PathOf("M1,4.5 L2,7.5 L3,4.5 Z M8,4.5 L9,7.5 L10,4.5 Z", Brushes.White)); // two little fangs
            head.Children.Add(Art.Circle(6, 0.5, 1.2, ink)); // nostrils
            head.Children.Add(Art.Circle(9.5, 1, 1.2, ink));
        }
        else
        {
            if (bunny) head.Children.Add(Art.PathOf("M0.5,0.5 L4.5,0.5 L2.5,3 Z", Art.Brush("#FF8FA8"))); // pink nose
            if (fox) head.Children.Add(Art.At(new Ellipse { Width = 5, Height = 4, Fill = Art.Brush("#2B2320") }, 0.5, 0)); // nose
            if (hamster)
            {
                head.Children.Add(Art.At(new Ellipse { Width = 4, Height = 3, Fill = Art.Brush("#FF8FA8") }, 0.5, 1)); // pink nose
                head.Children.Add(Art.PathOf("M-6,2 L-16,0 M-6,4 L-16,6 M11,2 L21,0 M11,4 L21,6", null, Art.Brush(140, 90, 60, 40), 0.8)); // whiskers
            }
            head.Children.Add(Art.PathOf("M-1,3.5 Q0.75,6 2.5,3.5 Q4.25,6 6,3.5", null, Art.Brush("#5A3320"), 1.1));
        }
        if (dog)
        {
            head.Children.Add(Art.At(new Ellipse { Width = 7, Height = 5, Fill = Art.Brush("#2B2320") }, -1, -1)); // nose
            // floppy ears hang over the sides of the head
            body.Children.Add(Art.PathOf("M-15,-13 C-24,-12 -24,2 -18,5 C-15,1 -13,-6 -12,-12 Z", Art.Brush("#7A4A2A"), ink, 1.2));
            body.Children.Add(Art.PathOf("M14,-13 C23,-12 23,2 17,5 C14,1 12,-6 11,-12 Z", Art.Brush("#7A4A2A"), ink, 1.2));
        }

        // eyes: an owl's are big, a frog's sit on top, a turtle's are small
        double eyeK = owl ? 1.35 : turtle ? 0.75 : hamster ? 0.95 : 1, eyeY = frog ? -9 : owl ? -1 : 0;
        var eyeBox = new Canvas
        {
            RenderTransformOrigin = RelativePoint.TopLeft,
            RenderTransform = new TransformGroup { Children = { new ScaleTransform(eyeK, eyeK), new TranslateTransform(2.5 * (1 - eyeK), -3 * (1 - eyeK) + eyeY) } },
        };
        var eyes = new Canvas();
        var pupilL = new TranslateTransform();
        var pupilR = new TranslateTransform();
        foreach (var (x, tr) in new[] { (-4.0, pupilL), (9.0, pupilR) })
        {
            eyes.Children.Add(Art.At(new Ellipse { Width = 8.6, Height = 9.8, Fill = Brushes.White, Stroke = eyeInk, StrokeThickness = 0.9 }, x - 4.3, -7.9));
            var pupil = new Canvas { RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = tr };
            pupil.Children.Add(Art.Circle(x, -2.6, owl ? 3 : 2.4, owl ? Art.Brush("#3A2A16") : eyeInk));
            pupil.Children.Add(Art.Circle(x - 0.9, -3.6, 0.85, Brushes.White));
            eyes.Children.Add(pupil);
        }
        eyeBox.Children.Add(eyes);
        var eyesSleep = Art.PathOf("M-7.5,-3 Q-4,0 -0.5,-3 M5.5,-3 Q9,0 12.5,-3", null, eyeInk, 1.4);
        var eyesHappy = Art.PathOf("M-7.5,-2 Q-4,-6.5 -0.5,-2 M5.5,-2 Q9,-6.5 12.5,-2", null, eyeInk, 1.5);
        eyesSleep.IsVisible = eyesHappy.IsVisible = false;
        eyeBox.Children.Add(eyesSleep);
        eyeBox.Children.Add(eyesHappy);
        head.Children.Add(eyeBox);

        // a nightcap for sleeping late, pulled down over the head
        double capY = frog ? -9 : owl ? -5 : hamster ? -4 : dragon ? -3 : 0;
        var cap = new Canvas { IsVisible = false, RenderTransform = new TranslateTransform(0, capY) };
        var capInk = Art.Brush("#2F4380");
        cap.Children.Add(Art.PathOf("M-9,-13 L11,-13 L3,-31 Z", Art.Brush("#4A63B8"), capInk, 1));
        cap.Children.Add(Art.PathOf("M-10,-13 L12,-13", null, Brushes.White, 3.5));
        cap.Children.Add(Art.Circle(3, -31, 3, Brushes.White, capInk, 0.8));
        head.Children.Add(cap);

        var paw = duck || penguin || parrot || owl ? orange : Art.Brush(kind switch
        {
            "dog" => "#F0D2B0", "bunny" => "#FFFFFF", "fox" => "#3A1A08", "hamster" => "#FFC9C0", "turtle" => "#8FBF5A", "frog" => "#6CC24A", "dragon" => "#4FB3A8", _ => "#FFE7CC",
        });
        double fw = frog ? 14 : turtle ? 9 : hamster ? 8 : dragon ? 13 : 11, fh = frog || turtle ? 6 : hamster ? 5 : 7;
        var footA = new TranslateTransform();
        var footB = new TranslateTransform();
        body.Children.Add(Art.At(new Ellipse { Width = fw, Height = fh, Fill = paw, Stroke = ink, StrokeThickness = 1, RenderTransform = footA }, -8 - fw / 2, 13));
        body.Children.Add(Art.At(new Ellipse { Width = fw, Height = fh, Fill = paw, Stroke = ink, StrokeThickness = 1, RenderTransform = footB }, 8 - fw / 2, 13));

        // outside the mirrored body, so the text never reads backwards
        var zz = new TextBlock
        {
            Text = L.T("z z"), FontFamily = Fx.Font, FontSize = 14, FontWeight = FontWeight.Bold,
            Foreground = Art.Brush("#8FA3D1"), IsVisible = false,
        };
        s.Children.Add(Art.At(zz, 8, -46));

        return new PetArt
        {
            Root = s, BodyScale = bodyScale, BodyShift = bodyShift, HeadScale = headScale, HeadShift = headShift, HeadTurn = headTurn, HeadBase = headBase,
            PupilL = pupilL, PupilR = pupilR, FootA = footA, FootB = footB, Tail = tailTurn,
            EyesOpen = eyes, EyesSleep = eyesSleep, EyesHappy = eyesHappy, Shadow = shadow, Zz = zz, Nightcap = cap,
            Cheeks = cheeks, Throat = throat, Tongue = tongue, TongueEl = tongueEl, Wings = wings, WingSpread = wingSpread, WingFlap = wingFlap, WingFlapL = wingFlapL,
        };
    }

    /// <summary>The treat jar, origin at the bottom center: glass, a wooden lid and a few treats inside.</summary>
    static Sprite BuildJar()
    {
        var s = new Sprite();
        var glass = Art.Brush(120, 200, 225, 245);
        var rim = Art.Brush("#6F86A8");
        s.Children.Add(Art.At(new Ellipse { Width = JarW + 8, Height = 6, Fill = Art.Brush(50, 0, 0, 0) }, -(JarW + 8) / 2, -3));
        s.Children.Add(Art.At(new Border
        {
            Width = JarW, Height = JarH - 6, CornerRadius = new CornerRadius(5), Background = glass, BorderBrush = rim, BorderThickness = new Thickness(1.2),
        }, -JarW / 2, -(JarH - 6)));
        for (int i = 0; i < 3; i++)
        {
            var bone = Art.PathOf(BonePath, BoneFill, BoneInk, 0.8);
            bone.RenderTransform = new TransformGroup
            {
                Children = { new ScaleTransform(0.7, 0.7), new RotateTransform(-20 + i * 25), new TranslateTransform(-2 + i * 3, -6 - i * 5) },
            };
            s.Children.Add(bone);
        }
        s.Children.Add(Art.At(new Border
        {
            Width = JarW + 4, Height = 7, CornerRadius = new CornerRadius(2), Background = Art.Brush("#B0653A"), BorderBrush = Art.Brush("#6E3C1E"), BorderThickness = new Thickness(1),
        }, -(JarW + 4) / 2, -JarH));
        s.Children.Add(Art.At(new Ellipse { Width = 4, Height = 12, Fill = Art.Brush(110, 255, 255, 255) }, -JarW / 2 + 3, -JarH + 10));
        s.IsHitTestVisible = false;
        return s;
    }

    /// <summary>A little red ball with a white band, drawn around its center.</summary>
    Thing BuildBall()
    {
        var s = new Sprite();
        var shadow = Art.At(new Ellipse { Width = 16, Height = 4, Fill = Art.Brush(55, 0, 0, 0) }, -8, BallR - 1);
        s.Children.Insert(0, shadow);
        s.Rotor.Children.Add(Art.Circle(0, 0, BallR, Art.Brush("#E8505B"), Art.Brush("#8A2430"), 1.2));
        var band = Art.PathOf($"M{Art.F(-BallR)},-1.5 L{Art.F(BallR)},-1.5 L{Art.F(BallR)},1.5 L{Art.F(-BallR)},1.5 Z", Brushes.White);
        band.Clip = new EllipseGeometry(new Rect(-BallR + 0.6, -BallR + 0.6, BallR * 2 - 1.2, BallR * 2 - 1.2));
        s.Rotor.Children.Add(band);
        s.Children.Add(Art.At(new Ellipse { Width = 4, Height = 2.5, Fill = Art.Brush(140, 255, 255, 255), RenderTransform = new RotateTransform(-30) }, -4.5, -5));
        s.IsHitTestVisible = false;
        s.IsVisible = false;
        _thingLayer.Children.Add(s);
        return new Thing { Sprite = s, Shadow = shadow, R = BallR };
    }

    /// <summary>A treat: a biscuit shaped like a bone.</summary>
    Thing BuildTreat()
    {
        var s = new Sprite();
        var shadow = Art.At(new Ellipse { Width = 12, Height = 3, Fill = Art.Brush(55, 0, 0, 0) }, -6, TreatR - 0.5);
        s.Children.Insert(0, shadow);
        s.Rotor.Children.Add(Art.PathOf(BonePath, BoneFill, BoneInk, 1));
        s.IsHitTestVisible = false;
        s.IsVisible = false;
        _thingLayer.Children.Add(s);
        return new Thing { Sprite = s, Shadow = shadow, R = TreatR };
    }

    /// <summary>A fly for the frog: a dot with two little wings.</summary>
    static Sprite BuildFly()
    {
        var s = new Sprite();
        var wing = Art.Brush(150, 255, 255, 255);
        s.Children.Add(Art.At(new Ellipse { Width = 4.5, Height = 2.6, Fill = wing, RenderTransform = new RotateTransform(-25) }, -4.5, -3.5));
        s.Children.Add(Art.At(new Ellipse { Width = 4.5, Height = 2.6, Fill = wing, RenderTransform = new RotateTransform(25) }, 0, -3.5));
        s.Children.Add(Art.Circle(0, 0, 2.2, Art.Brush("#3A3A44")));
        s.IsHitTestVisible = false;
        return s;
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
        if (roll < 0.012 || (_pets < BallAfterPets && roll < 0.05))
        {
            Pet(); // petted often enough early on for the ball to appear
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
        else if (roll < 0.034 && _ball.State == ThingState.Resting && _fetch.Length == 0)
        {
            // a throw of the ball from where it lies: the pet fetches, or watches, as its kind does
            _ball.Hwnd = IntPtr.Zero;
            ThrowBall(new Vec2((Rng.NextDouble() < 0.5 ? -1 : 1) * (300 + Rng.NextDouble() * 500), -(250 + Rng.NextDouble() * 450)));
        }
        else if (roll < 0.046 && _treat.State == ThingState.Hidden)
        {
            TossTreat();
        }
        // otherwise the behaviour timer lets it roam
    }
}
