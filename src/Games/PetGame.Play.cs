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

/// <summary>The pet keeping the player company in the other games (tray → Pet → Pet keeps me company in games).</summary>
public sealed partial class PetGame
{
    /// <summary>The pet that sits with the player while another game is up; the overlay draws and updates it.</summary>
    public static Companion CreateCompanion(IGameHost host) => new(host);

    /// <summary>
    /// The chosen pet, on the floor of whatever game is up (the taskbar, or the pier in Fishing), while "Pet keeps me
    /// company in games" is on. It joins in where a game lets it (<see cref="IPetPlayground"/>): it bats a Hoops ball that
    /// lies loose back toward the player, runs after the Pong ball, hides from the Whack-a-Bug bugs and steals a landed
    /// fish (which still counts). It never touches anything over the LAN or in a race (<see cref="PetPlay.MayTouch"/>). It
    /// jumps at a buzzer or a crash and dances at a cheer (<see cref="PetPlay.Ears"/>), dozes off when nobody plays, and
    /// then only lifts its head. Still, it costs nothing: frames run only while it moves.
    /// </summary>
    public sealed class Companion
    {
        const double SleepAfterIdle = 100, NightSleepAfterIdle = 50, NapBeforeWaking = 60, WakeR = 50;
        const double BatCooldown = 1.2, HideCooldown = 2.5, SwoopCooldown = 2.5, ChaseSpeed = 1.8, FleeSpeed = 2.4, PeekFor = 1.4, StealReach = 34, SwoopUp = 90;

        readonly IGameHost _host;
        readonly DispatcherTimer _brain = new() { Interval = TimeSpan.FromSeconds(4) };
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly PetPlay.Ears _ears = new();
        readonly Dictionary<string, double> _lastSound = new();
        readonly Sprite _fish;
        readonly ScaleTransform _fishFlip = new(1, 1);
        PetArt _art;
        string _kind = "cat";
        MiniGame? _game;
        Mode _mode = Mode.Sit;
        Vec2 _pos, _vel, _pointerAt;
        double _face = 1, _targetX = double.NaN, _speed = 1, _animT, _happyT, _squashT, _wingK;
        string _act = "", _afterWalk = "";
        double _actT, _actLen;
        double _lastActive, _sleptAt, _batAt = -99, _hideAt = -99, _swoopAt = -99, _peekT, _lastVoice = -99;
        int _batThrow = int.MinValue;
        bool _shown, _placed, _chasing, _carrying, _leaping, _fishLoose, _stealWanted, _sleepAfterAct;

        internal Companion(IGameHost host)
        {
            _host = host;
            RefreshKind();
            _art = BuildPet(_kind);
            _art.Root.IsHitTestVisible = false;
            Layer.Children.Add(_art.Root);
            _fish = BuildStolenFish(_fishFlip);
            _fish.IsVisible = false;
            Layer.Children.Add(_fish);
            _brain.Tick += (_, _) => Think();
        }

        /// <summary>The pet's own layer, drawn over the game and under the popups.</summary>
        public Canvas Layer { get; } = new() { IsHitTestVisible = false, IsVisible = false };

        /// <summary>True while it is on screen (the setting is on and a game other than the pet's own is up).</summary>
        public bool Shown => _shown;

        double Now => _clock.Elapsed.TotalSeconds;
        Vec2 Center => new(_pos.X, _pos.Y - CenterLift);
        Vec2 Mouth => Center + new Vec2(_face * 15, 3);
        bool Grounded => _mode is Mode.Sit or Mode.Walk or Mode.Sleep;
        bool Gliding => IsFlyer(_kind) && _mode == Mode.Air;
        double AirGravity => IsFlyer(_kind) ? Gravity * GlideGravity : Gravity;

        void RefreshKind() => _kind = Array.IndexOf(Kinds, _host.Settings.PetKind) >= 0 ? _host.Settings.PetKind : "cat";

        /// <summary>Another pet was picked, or the theme changed its accessories: redraw it.</summary>
        public void Rebuild()
        {
            RefreshKind();
            Layer.Children.Remove(_art.Root);
            _art = BuildPet(_kind);
            _art.Root.IsHitTestVisible = false;
            Layer.Children.Insert(0, _art.Root);
            _wingK = 0;
            if (_shown) Draw(0);
        }

        /// <summary>A game was picked, or the setting changed: show the pet on the game's floor, or put it away.</summary>
        public void GameChanged(MiniGame? game)
        {
            _game = game;
            bool show = _host.Settings.PetCompany && game != null && game is not PetGame;
            _shown = show;
            Layer.IsVisible = show;
            _mode = Mode.Sit;
            _vel = default;
            _act = _afterWalk = "";
            _targetX = double.NaN;
            _speed = 1;
            _chasing = _carrying = _leaping = _fishLoose = _stealWanted = _sleepAfterAct = false;
            _peekT = _happyT = _squashT = 0;
            _fish.IsVisible = false;
            if (!show)
            {
                _brain.Stop();
                return;
            }
            if (_kind != _host.Settings.PetKind) Rebuild();
            var f = Floor();
            // a short floor (the pier) has its spot at the far end, clear of the rod; on the taskbar it stays where it was
            double x = f.X2 - f.X1 < 400 ? f.X1 + (f.X2 - f.X1) * 0.8 : _placed ? Math.Clamp(_pos.X, f.X1, f.X2) : f.X1 + (f.X2 - f.X1) * 0.22;
            _pos = new Vec2(x, f.Y);
            _placed = true;
            _lastActive = Now;
            _pointerAt = _host.Pointer;
            _brain.Interval = TimeSpan.FromSeconds(3);
            _brain.Start();
            Draw(0);
            _host.Wake();
        }

        /// <summary>Where it may stand: the game's own floor when it has one, else the taskbar.</summary>
        PetFloor Floor()
        {
            if (_game is IPetPlayground pg)
            {
                var f = pg.PetFloor;
                if (f.X2 > f.X1) return f;
            }
            var a = _host.Arena;
            return new PetFloor(a.Left + HalfW, a.Right - HalfW, a.Bottom);
        }

        // ------------------------------------------------------------------ the frame

        /// <summary>One frame, while the overlay's loop runs; <paramref name="gameBusy"/> says the game is being played. True while the pet moves.</summary>
        public bool Update(double dt, bool gameBusy)
        {
            if (!_shown) return false;
            _animT += dt;
            var pointer = _host.Pointer;
            if ((pointer - _pointerAt).Length > 3)
            {
                _pointerAt = pointer;
                _lastActive = Now;
            }
            if (gameBusy) _lastActive = Now;
            var f = Floor();
            if (Grounded)
            {
                _pos.Y = f.Y;
                _pos.X = Math.Clamp(_pos.X, f.X1, f.X2);
            }

            if (_mode == Mode.Sleep)
            {
                // the cursor on it wakes it; otherwise it naps a while before play wakes it
                if ((pointer - Center).Length < WakeR || (gameBusy && Now - _sleptAt > NapBeforeWaking)) WakeUp();
                else
                {
                    _peekT = Math.Max(0, _peekT - dt);
                    Draw(dt);
                    return _peekT > 0;
                }
            }

            Play(f, pointer);
            Move(dt, f);
            bool acting = StepAct(dt);
            _happyT = Math.Max(0, _happyT - dt);
            _squashT = Math.Max(0, _squashT - dt);
            Draw(dt);
            return _mode is Mode.Walk or Mode.Air || acting || _happyT > 0 || _squashT > 0;
        }

        /// <summary>What the game's toy calls for this frame (see <see cref="PetPlay.Choose"/>).</summary>
        void Play(PetFloor f, Vec2 pointer)
        {
            if (_game is not IPetPlayground pg || _carrying) return;
            var toy = pg.PetToy;
            if (toy.Kind == PetToyKind.Fish)
            {
                if (toy.Loose && !_fishLoose) _stealWanted = Random.Shared.NextDouble() < PetPlay.StealChance; // this one tempts it, or not
                _fishLoose = toy.Loose;
            }
            bool may = PetPlay.MayTouch(_host.Lan.Connected, _game.Race != null, _host.Settings.CpuRival);
            bool batReady = Now - _batAt > BatCooldown && toy.Throws != _batThrow;
            if (_mode == Mode.Air)
            {
                AirContact(pg, toy, may, batReady);
                return;
            }
            if (_act.Length > 0) return; // it finishes what it is doing first
            var intent = PetPlay.Choose(_kind, toy, f, Center, pointer, may, Fx.ReducedMotion, batReady, Now - _hideAt > HideCooldown, _stealWanted);
            switch (intent.Move)
            {
                case PetMove.Watch:
                    if (_chasing) StopWalking();
                    if (_mode == Mode.Sit) FaceX(intent.X);
                    break;
                case PetMove.Chase:
                    if (_mode == Mode.Sit || double.IsNaN(_targetX) || Math.Abs(_targetX - intent.X) > 12) WalkTo(intent.X, ChaseSpeed, f);
                    _chasing = true;
                    break;
                case PetMove.Swoop:
                    if (Now - _swoopAt < SwoopCooldown) goto case PetMove.Chase; // catching its breath: it walks
                    _swoopAt = Now;
                    _chasing = false;
                    Hop(new Vec2(intent.X, f.Y), SwoopUp);
                    PlayThrottled("flap", 0.3, 1);
                    break;
                case PetMove.Bat:
                    Bat(pg, toy, pointer);
                    break;
                case PetMove.Hide:
                    _hideAt = Now;
                    _chasing = false;
                    Speak(Say.Surprise, 0.35);
                    string hide = PetPlay.HideActOf(_kind);
                    if (Math.Abs(intent.X - _pos.X) > 8)
                    {
                        WalkTo(intent.X, FleeSpeed, f);
                        _afterWalk = hide;
                    }
                    else
                    {
                        if (toy.Bugs is { Count: > 0 } bugs) FaceX(bugs[0].X);
                        StartAct(hide);
                    }
                    break;
                case PetMove.Steal:
                    _chasing = false;
                    _stealWanted = false; // one leap per fish
                    _leaping = true;
                    FaceX(intent.X);
                    Leap(toy.P + new Vec2(0, CenterLift - 3));
                    PlayThrottled("whoosh", 0.25, 1.4);
                    break;
                default:
                    if (_chasing) StopWalking();
                    break;
            }
        }

        /// <summary>In the air: a swooping flyer bats the ball it passes, and a leap at a hanging fish takes it.</summary>
        void AirContact(IPetPlayground pg, PetToy toy, bool may, bool batReady)
        {
            if (!may) return;
            if (toy.Kind == PetToyKind.Ball && toy.Loose && batReady && (Center - toy.P).Length <= toy.R + PetPlay.PawReach)
                Bat(pg, toy, _host.Pointer);
            else if (toy.Kind == PetToyKind.Fish && _leaping && toy.Loose && (Mouth - toy.P).Length <= StealReach + toy.R && pg.PetTouched(PetTouch.Steal, default))
            {
                _leaping = false;
                _carrying = true;
                Speak(Say.Happy, 0.45);
                PlayThrottled("pop", 0.3, 1.5);
            }
        }

        void Bat(IPetPlayground pg, PetToy toy, Vec2 pointer)
        {
            _batAt = Now; // a refused bat waits too, rather than asking every frame
            var v = PetPlay.BatVelocity(toy.P, pointer.X, _host.Arena.Center.X);
            if (!pg.PetTouched(PetTouch.Bat, v)) return;
            _batThrow = toy.Throws;
            _chasing = false;
            if (_mode == Mode.Walk) StopWalking();
            FaceX(toy.P.X);
            StartAct("bat");
            PlayThrottled("bounce", 0.3, 1.4);
            Speak(Say.Happy, 0.4);
        }

        // ------------------------------------------------------------------ moving

        void Move(double dt, PetFloor f)
        {
            if (_mode == Mode.Walk)
            {
                double pace = IsHopper(_kind) ? Math.Sin(Math.PI * HopPhaseOf(_kind, _animT, _speed)) * Math.PI / 2 : 1; // a hopper moves in hops
                _pos.X += _face * WalkSpeedOf(_kind) * _speed * pace * dt;
                bool arrived = !double.IsNaN(_targetX) && (_targetX - _pos.X) * _face <= 0;
                if (arrived || _pos.X < f.X1 || _pos.X > f.X2)
                {
                    _pos.X = Math.Clamp(arrived ? _targetX : _pos.X, f.X1, f.X2);
                    StopWalking();
                    if (_afterWalk.Length > 0)
                    {
                        string next = _afterWalk;
                        _afterWalk = "";
                        StartAct(next);
                    }
                }
                return;
            }
            if (_mode != Mode.Air) return;
            var a = _host.Arena;
            _vel.Y += AirGravity * dt;
            _pos += _vel * dt;
            if (_pos.X < a.Left + HalfW || _pos.X > a.Right - HalfW)
            {
                _pos.X = Math.Clamp(_pos.X, a.Left + HalfW, a.Right - HalfW); // closed box
                _vel.X = -_vel.X * 0.4;
            }
            if (_pos.Y - Height < a.Top)
            {
                _pos.Y = a.Top + Height;
                if (_vel.Y < 0) _vel.Y = 0;
            }
            if (_vel.Y > 0 && _pos.Y >= f.Y) Land(f);
        }

        void Land(PetFloor f)
        {
            _mode = Mode.Sit;
            _vel = default;
            _pos = new Vec2(Math.Clamp(_pos.X, f.X1, f.X2), f.Y);
            _squashT = SquashTime;
            _leaping = false;
            PlayThrottled("thunk", 0.15, 1.7);
            if (_carrying)
            {
                // off to the far end with it, away from the rod, for a quiet meal
                double away = f.X2 - _pos.X < _pos.X - f.X1 ? f.X1 : f.X2;
                if (Math.Abs(away - _pos.X) > 12)
                {
                    WalkTo(away, ChaseSpeed, f);
                    _afterWalk = "eat";
                }
                else StartAct("eat");
            }
        }

        void WalkTo(double x, double speed, PetFloor f)
        {
            x = Math.Clamp(x, f.X1, f.X2);
            if (Math.Abs(x - _pos.X) < 6) return;
            _act = "";
            _mode = Mode.Walk;
            _face = x > _pos.X ? 1 : -1;
            _targetX = x;
            _speed = speed;
        }

        void StopWalking()
        {
            if (_mode == Mode.Walk) _mode = Mode.Sit;
            _targetX = double.NaN;
            _speed = 1;
            _chasing = false;
        }

        /// <summary>A hop that lands on <paramref name="target"/> after rising <paramref name="up"/> px (a glide for a flyer).</summary>
        void Hop(Vec2 target, double up)
        {
            double g = AirGravity;
            double vy = -Math.Sqrt(2 * g * up);
            double time = -vy / g + Math.Sqrt(2 * Math.Max(1, up + target.Y - _pos.Y) / g);
            TakeOff(new Vec2((target.X - _pos.X) / time, vy));
        }

        /// <summary>A leap whose top is at <paramref name="apex"/> (the feet), for a fish hanging from the rod.</summary>
        void Leap(Vec2 apex)
        {
            double g = AirGravity;
            double vy = -Math.Sqrt(2 * g * Math.Max(20, _pos.Y - apex.Y));
            TakeOff(new Vec2((apex.X - _pos.X) / (-vy / g), vy));
        }

        void TakeOff(Vec2 v)
        {
            if (_mode == Mode.Walk) StopWalking();
            _mode = Mode.Air;
            _vel = v;
            if (Math.Abs(v.X) > 1) _face = v.X > 0 ? 1 : -1;
        }

        void FaceX(double x)
        {
            if (Math.Abs(x - _pos.X) > 10) _face = x > _pos.X ? 1 : -1;
        }

        // ------------------------------------------------------------------ actions

        static double LengthOf(string act, string kind) => act switch
        {
            "bat" => 0.4,
            "startle" => 0.9,
            "cower" => 1.6,
            "dance" => 1.6,
            _ when act == TrickFor(kind).Name => TrickFor(kind).Seconds,
            _ => ActLength(act) > 0 ? ActLength(act) : 1,
        };

        void StartAct(string act)
        {
            if (_mode == Mode.Walk) StopWalking();
            _act = act;
            _actT = 0;
            _actLen = LengthOf(act, _kind);
            switch (act)
            {
                case "yawn": PlayThrottled("yawn", 0.3, 1); break;
                case "puff" or "hide": Speak(Say.Upset, 0.35); break;
                case "smoke": PlayThrottled("huff", 0.3, 1.2); break;
                case "eat": PlayThrottled("board", 0.35, 0.55); break;
                case "fly": PlayThrottled("lick", 0.2, 1.8); break;
            }
            _host.Wake();
        }

        bool StepAct(double dt)
        {
            if (_act.Length == 0) return false;
            _actT += dt;
            if (_actT < _actLen) return true;
            string done = _act;
            _act = "";
            if (done == "eat" && _carrying)
            {
                _carrying = false;
                _fish.IsVisible = false;
                _host.Fx.Burst(Mouth, Crumbs, 6, 110, 700, 3, 0.45);
                Speak(Say.Content, 0.35);
            }
            if (_sleepAfterAct)
            {
                _sleepAfterAct = false;
                GoToSleep();
            }
            return true;
        }

        /// <summary>A buzzer or a crash: a jump with the ears back (the turtle pulls into its shell, the cat puffs up).</summary>
        void Startle()
        {
            if (_mode == Mode.Air || _carrying) return;
            StopWalking();
            _sleepAfterAct = false;
            StartAct(PetPlay.StartleActOf(_kind));
            Speak(Say.Surprise, 0.4);
            if (PetPlay.Jumps(_kind, Fx.ReducedMotion)) TakeOff(new Vec2(0, -300));
        }

        /// <summary>A best score or a win: its own little dance, with a hop.</summary>
        void Cheer()
        {
            if (_mode == Mode.Air || _carrying) return;
            StopWalking();
            _sleepAfterAct = false;
            bool still = Fx.ReducedMotion;
            StartAct(still ? "dance" : PetPlay.CheerActOf(_kind));
            Speak(Say.Happy, 0.45);
            if (still) return;
            _happyT = HappyTime;
            if (PetPlay.Jumps(_kind, false) && _act != "spin" && _act != "wheel") TakeOff(new Vec2(0, _kind == "bunny" ? -480 : -320));
        }

        /// <summary>A game sound reached it (see <see cref="PetPlay.Ears"/>).</summary>
        public void Heard(string clip, double volume)
        {
            if (!_shown) return;
            switch (_ears.Hear(clip, volume, _mode == Mode.Sleep, Now))
            {
                case PetReaction.Startle: Startle(); break;
                case PetReaction.Cheer: Cheer(); break;
                case PetReaction.LiftHead: _peekT = PeekFor; break; // a sleepy pet only lifts its head
                default: return;
            }
            _host.Wake();
        }

        void GoToSleep()
        {
            _mode = Mode.Sleep;
            _sleptAt = Now;
            _act = "";
            _brain.Stop(); // asleep, it costs nothing until play or the cursor wakes it
            _art.Zz.Text = L.T("z z");
            Canvas.SetTop(_art.Zz, Math.Max(-46, _host.Arena.Top + 2 - Center.Y));
            Draw(0);
        }

        void WakeUp()
        {
            _mode = Mode.Sit;
            _peekT = 0;
            _lastActive = Now;
            StartAct("yawn");
            _brain.Interval = TimeSpan.FromSeconds(3);
            _brain.Start();
        }

        /// <summary>
        /// The behaviour timer: now and then a habit, a little walk or a look round; after a long spell with nobody playing,
        /// a yawn and a nap. It wakes the frame loop only when it starts something.
        /// </summary>
        void Think()
        {
            if (!_shown || _mode == Mode.Sleep)
            {
                _brain.Stop();
                return;
            }
            _brain.Interval = TimeSpan.FromSeconds(3 + Random.Shared.NextDouble() * 4);
            if (_mode != Mode.Sit || _act.Length > 0 || _carrying) return;
            bool night = IsNight(TimeOnly.FromDateTime(DateTime.Now));
            if (Now - _lastActive > (night ? NightSleepAfterIdle : SleepAfterIdle))
            {
                StartAct("yawn");
                _sleepAfterAct = true;
                return;
            }
            if (WantsToPlay())
            {
                _host.Wake();
                return;
            }
            double roll = Random.Shared.NextDouble();
            if (roll < 0.25) StartAct(HabitsOf(_kind)[Random.Shared.Next(3)]);
            else if (roll < 0.4)
            {
                var f = Floor();
                double step = (Random.Shared.NextDouble() < 0.5 ? -1 : 1) * (60 + Random.Shared.NextDouble() * 100);
                WalkTo(_pos.X + step, 1, f);
                _host.Wake();
            }
            else if (roll < 0.5)
            {
                _face = -_face; // a look round
                Draw(0);
            }
        }

        /// <summary>Whether the game's toy would get it moving now (a resting ball it could bat, a bug beside it).</summary>
        bool WantsToPlay()
        {
            if (_game is not IPetPlayground pg) return false;
            var toy = pg.PetToy;
            bool may = PetPlay.MayTouch(_host.Lan.Connected, _game.Race != null, _host.Settings.CpuRival);
            var move = PetPlay.Choose(_kind, toy, Floor(), Center, _host.Pointer, may, Fx.ReducedMotion,
                Now - _batAt > BatCooldown && toy.Throws != _batThrow, Now - _hideAt > HideCooldown, false).Move;
            return move is PetMove.Chase or PetMove.Swoop or PetMove.Bat or PetMove.Hide;
        }

        void Speak(Say say, double volume)
        {
            var voices = VoicesOf(_kind, say);
            if (voices.Length == 0 || Now - _lastVoice < 0.4) return;
            _lastVoice = Now;
            PlayThrottled(voices[Random.Shared.Next(voices.Length)], volume * 0.8, 0.94 + Random.Shared.NextDouble() * 0.12);
        }

        void PlayThrottled(string name, double volume, double pitch)
        {
            if (_lastSound.TryGetValue(name, out double t) && Now - t < 0.5) return;
            _lastSound[name] = Now;
            _host.Sound.Play(name, volume, pitch);
        }

        // ------------------------------------------------------------------ drawing

        void Draw(double dt)
        {
            bool still = Fx.ReducedMotion;
            var pose = PoseOf(_kind, _mode, _act, _actT, _actLen, _animT, _speed, still ? 0 : _happyT, _squashT, _face, Gliding);
            PoseCompanionAct(ref pose, _act, _actT, _actLen, _face, still);
            bool peeking = _mode == Mode.Sleep && _peekT > 0;
            if (peeking)
            {
                double k = Math.Min(1, Math.Min(PeekFor - _peekT, _peekT) / 0.25);
                pose.HeadY -= 5 * k; // the head comes up for a look, then goes back down
                pose.HeadAngle -= 8 * k;
            }
            _wingK += (pose.Wings - _wingK) * (dt <= 0 ? 1 : Math.Min(1, dt * 12));
            pose.Wings = _wingK;
            bool asleep = _mode == Mode.Sleep && !peeking;
            bool nightcap = _mode == Mode.Sleep && IsNight(TimeOnly.FromDateTime(DateTime.Now));
            Apply(_art, pose, _face, Center, 0, asleep, _happyT > 0, Grounded, nightcap, _host.Pointer);
            if (_carrying)
            {
                _fishFlip.ScaleX = _face;
                _fish.Set(Mouth + new Vec2(_face * 9, 5));
            }
            _fish.IsVisible = _carrying;
        }

        /// <summary>The companion's own actions, on top of the shared poses: a paw swipe, a fright, cowering, a dance.</summary>
        static void PoseCompanionAct(ref Pose p, string act, double actT, double actLen, double face, bool still)
        {
            if (act is not ("bat" or "startle" or "cower" or "dance")) return;
            double kk = actLen > 0 ? Math.Min(1, actT / actLen) : 1, env = Math.Sin(Math.PI * kk), on = Fade(kk), t = actT;
            double big = still ? 0.35 : 1;
            switch (act)
            {
                case "bat": // a paw out at the ball
                    p.StepB = -12 * env;
                    p.FrontX = 8 * env;
                    p.Angle = -face * 6 * env;
                    break;
                case "startle": // up tall, head back, ears flat, tail down
                    p.Sy = 1 + 0.12 * on;
                    p.Sx = 1 - 0.06 * on;
                    p.HeadX = -3 * on;
                    p.HeadAngle = -14 * on;
                    p.Tail = -20 * on;
                    p.Eyes = "";
                    break;
                case "cower": // flat to the floor, head tucked in, eyes squeezed shut
                    p.Sy = 1 - 0.18 * on;
                    p.Sx = 1 + 0.1 * on;
                    p.HeadX = -5 * on;
                    p.HeadY = 4 * on;
                    p.Tail = -15 * on;
                    if (kk > 0.15 && kk < 0.85) p.Eyes = "sleep";
                    break;
                case "dance": // a wiggle from side to side, stepping in time
                    double s = Math.Sin(t * 10);
                    p.Angle = s * 14 * on * big;
                    p.Bob -= Math.Abs(s) * 5 * on * big;
                    p.StepA = -3 * Math.Max(0, s) * on;
                    p.StepB = -3 * Math.Max(0, -s) * on;
                    p.Tail = Math.Sin(t * 20) * 20 * on;
                    p.Eyes = "happy";
                    break;
            }
        }

        /// <summary>A small silvery fish for the pet to run off with, drawn around its middle and facing +x.</summary>
        static Sprite BuildStolenFish(ScaleTransform flip)
        {
            var s = new Sprite();
            var body = new Canvas { RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = flip };
            body.Children.Add(Art.PathOf("M-7,0 L-13,-5 L-12,0 L-13,5 Z", Art.Brush("#8FA9BF"), Art.Brush("#4E6478"), 1));
            body.Children.Add(Art.At(new Ellipse { Width = 16, Height = 8, Fill = Art.Brush("#B8CCDD"), Stroke = Art.Brush("#4E6478"), StrokeThickness = 1 }, -8, -4));
            body.Children.Add(Art.Circle(4, -1, 1.2, Art.Brush("#1C2530")));
            s.Children.Add(body);
            s.IsHitTestVisible = false;
            return s;
        }
    }
}
