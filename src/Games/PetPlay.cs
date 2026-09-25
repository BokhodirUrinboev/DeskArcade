using System;
using System.Collections.Generic;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>What a game shows the pet that keeps the player company (see <see cref="IPetPlayground"/>).</summary>
public enum PetToyKind
{
    None,
    /// <summary>A ball the pet may bat back once it lies loose on the floor (Hoops).</summary>
    Ball,
    /// <summary>A ball the pet may only run after and watch (Pong).</summary>
    Chase,
    /// <summary>Bugs to hide from (Whack-a-Bug).</summary>
    Bugs,
    /// <summary>A fish on the line; loose while it hangs from the rod on the way in (Fishing).</summary>
    Fish,
}

/// <summary>What the pet did to a game's toy.</summary>
public enum PetTouch { Bat, Steal }

/// <summary>Where the pet may sit and walk while a game is up: its feet on <see cref="Y"/>, between <see cref="X1"/> and <see cref="X2"/>.</summary>
public readonly record struct PetFloor(double X1, double X2, double Y);

/// <summary>
/// A game's toy for the pet this frame: where it is, how fast it goes, how big it is, whether the pet may touch it now,
/// how many times the player has thrown (so a ball is batted back at most once per throw) and, for Whack-a-Bug, where
/// the bugs are peeking out.
/// </summary>
public readonly record struct PetToy(PetToyKind Kind, Vec2 P = default, Vec2 V = default, double R = 0, bool Loose = false, int Throws = 0,
    IReadOnlyList<Vec2>? Bugs = null)
{
    public static PetToy Nothing => new(PetToyKind.None);
}

/// <summary>
/// A game the pet can join in (Hoops, Pong, Whack-a-Bug, Fishing). The game says where the pet may stand and what it
/// may play with; the pet asks the game to apply its touch, and the game may refuse (the ball was just grabbed, the
/// fish is already in the bucket).
/// </summary>
public interface IPetPlayground
{
    PetFloor PetFloor { get; }
    PetToy PetToy { get; }
    bool PetTouched(PetTouch touch, Vec2 v);
}

/// <summary>What the pet sets out to do about a game's toy (see <see cref="PetPlay.Choose"/>).</summary>
public enum PetMove { Idle, Watch, Chase, Swoop, Bat, Hide, Steal }

/// <summary>A move, with the x it heads for (or faces, when watching).</summary>
public readonly record struct PetIntent(PetMove Move, double X = 0);

/// <summary>How an animal takes to a ball in a game.</summary>
public enum PetBallStyle { Bat, Swoop, Watch }

/// <summary>How the pet takes a game sound.</summary>
public enum PetReaction { None, Startle, Cheer, LiftHead }

/// <summary>
/// The pure rules behind the pet joining the games: when it may touch anything, what it does about each toy, how each
/// animal plays, and which game sounds it reacts to (and how often). Nothing here draws or plays a sound.
/// </summary>
public static class PetPlay
{
    /// <summary>How close the ball must be (beyond its radius) for a paw to reach it.</summary>
    public const double PawReach = 24;
    /// <summary>A pet goes after a loose ball this far away; further than that it only watches.</summary>
    public const double ChaseRange = 520;
    /// <summary>A flyer swoops at a ball this close instead of walking.</summary>
    public const double SwoopRange = 300;
    /// <summary>With the cursor this close to the ball the player is reaching for it: the pet leaves it alone.</summary>
    public const double PlayerReach = 140;
    /// <summary>A bug peeking out this close sends the pet into hiding.</summary>
    public const double HideRange = 130;
    /// <summary>How far the pet scurries away from a bug before it hides.</summary>
    public const double HideStep = 110;
    /// <summary>How often a landed fish tempts the pet.</summary>
    public const double StealChance = 0.45;
    /// <summary>Game sounds: at least this long between any two reactions, and between two of the same kind.</summary>
    public const double ReactGap = 5, SameReactGap = 10;
    /// <summary>A thud or a bang this loud (0 to 1) makes the pet jump.</summary>
    public const double LoudCrash = 0.85;

    /// <summary>
    /// The pet may touch a game's toy only when that cannot decide a contest: never over the LAN (a duel or a race with a
    /// co-worker), and never in a round-based game while "race the computer" is on.
    /// </summary>
    public static bool MayTouch(bool lanConnected, bool raceGame, bool cpuRival) => !lanConnected && !(raceGame && cpuRival);

    /// <summary>Dogs, cats, foxes, hamsters, bunnies and frogs bat a ball; the owl and the parrot swoop at it; the rest watch it (the turtle just watches).</summary>
    public static PetBallStyle BallStyleOf(string kind) => PetGame.FetchStyleOf(kind) switch
    {
        PetGame.FetchStyle.Fly => PetBallStyle.Swoop,
        PetGame.FetchStyle.Watch => PetBallStyle.Watch,
        _ => PetBallStyle.Bat,
    };

    /// <summary>The fish-eaters (and the dragon, which hoards anything shiny) steal a fish; the rest only look.</summary>
    public static bool StealsFish(string kind) => kind is "cat" or "dog" or "fox" or "penguin" or "duck" or "owl" or "dragon";

    /// <summary>
    /// What it does when a bug peeks out beside it: the turtle pulls into its shell, the cat puffs up, the owl fluffs its
    /// feathers, the dragon puffs smoke at it, the frog flicks its tongue at it (a bug is lunch), the rest cower.
    /// </summary>
    public static string HideActOf(string kind) => kind switch
    {
        "turtle" => "hide",
        "cat" => "puff",
        "owl" => "fluff",
        "dragon" => "smoke",
        "frog" => "fly",
        _ => "cower",
    };

    /// <summary>Only the frog stands its ground in front of a bug; everyone else scurries away first.</summary>
    public static bool FleesBugs(string kind) => kind != "frog" && kind != "turtle";

    /// <summary>
    /// A cheer for a best score or a win: the dog chases its tail, the hamster runs its wheel, the parrot bobs to the
    /// music, the frog puffs its throat, the owl fluffs up, the duck flaps, the penguin stretches up and brays, the dragon
    /// puffs smoke, the bunny twists, the turtle stretches its neck out for a look, and the cat and the fox dance.
    /// </summary>
    public static string CheerActOf(string kind) => kind switch
    {
        "dog" => "spin",
        "hamster" => "wheel",
        "parrot" => "bob",
        "frog" => "throat",
        "owl" => "fluff",
        "duck" => "flap",
        "penguin" => "call",
        "dragon" => "smoke",
        "bunny" => "binky",
        "turtle" => "neck",
        _ => "dance",
    };

    /// <summary>A fright: the turtle pulls into its shell, the cat puffs up, the rest jump with their ears back.</summary>
    public static string StartleActOf(string kind) => kind switch
    {
        "turtle" => "hide",
        "cat" => "puff",
        _ => "startle",
    };

    /// <summary>Whether a startled or cheering animal leaves the ground: the turtle never does, and nobody does with reduced motion.</summary>
    public static bool Jumps(string kind, bool reducedMotion) => !reducedMotion && kind != "turtle";

    /// <summary>
    /// The pet's move for this frame. <paramref name="pet"/> is the middle of its body; <paramref name="batReady"/> says a
    /// bat is allowed (not just batted, and not batted since the player's last throw), <paramref name="hideReady"/> that it
    /// has not just hidden, and <paramref name="stealWanted"/> that this fish tempted it.
    /// </summary>
    public static PetIntent Choose(string kind, PetToy toy, PetFloor floor, Vec2 pet, Vec2 pointer, bool mayTouch, bool reducedMotion,
        bool batReady, bool hideReady, bool stealWanted)
    {
        var style = BallStyleOf(kind);
        switch (toy.Kind)
        {
            case PetToyKind.Ball:
            {
                var watch = new PetIntent(PetMove.Watch, toy.P.X);
                if (!mayTouch || style == PetBallStyle.Watch || !toy.Loose || !batReady) return watch;
                if (toy.P.X < floor.X1 - toy.R || toy.P.X > floor.X2 + toy.R) return watch;   // not where the pet can get at it
                if ((pointer - toy.P).Length < PlayerReach) return watch;                  // the player is going for it
                double dx = toy.P.X - pet.X, reach = toy.R + PawReach, side = dx >= 0 ? 1 : -1;
                if (Math.Abs(dx) <= reach) return new PetIntent(PetMove.Bat, toy.P.X);
                if (style == PetBallStyle.Swoop && !reducedMotion && Math.Abs(dx) <= SwoopRange)
                    return new PetIntent(PetMove.Swoop, Math.Clamp(toy.P.X - side * reach * 0.5, floor.X1, floor.X2));
                if (Math.Abs(dx) <= ChaseRange) return new PetIntent(PetMove.Chase, Math.Clamp(toy.P.X - side * (reach - 6), floor.X1, floor.X2));
                return watch;
            }
            case PetToyKind.Chase:
            {
                double x = Math.Clamp(toy.P.X, floor.X1, floor.X2);
                if (!mayTouch || style == PetBallStyle.Watch || Math.Abs(x - pet.X) < 30) return new PetIntent(PetMove.Watch, toy.P.X);
                // a flyer takes off after a ball coming low past it; the others run along underneath
                if (style == PetBallStyle.Swoop && !reducedMotion && toy.P.Y > floor.Y - 200 && Math.Abs(x - pet.X) <= SwoopRange)
                    return new PetIntent(PetMove.Swoop, x);
                return new PetIntent(PetMove.Chase, x);
            }
            case PetToyKind.Bugs:
            {
                if (toy.Bugs is not { Count: > 0 } bugs) return new PetIntent(PetMove.Idle);
                var near = bugs[0];
                foreach (var b in bugs)
                    if ((b - pet).LengthSquared < (near - pet).LengthSquared) near = b;
                if ((near - pet).Length > HideRange || !hideReady) return new PetIntent(PetMove.Watch, near.X);
                if (!FleesBugs(kind)) return new PetIntent(PetMove.Hide, pet.X);
                double away = near.X > pet.X ? -1 : 1;
                double to = pet.X + away * HideStep;
                if (to < floor.X1 || to > floor.X2) to = pet.X - away * HideStep; // backed into a corner: past it the other way
                return new PetIntent(PetMove.Hide, Math.Clamp(to, floor.X1, floor.X2));
            }
            case PetToyKind.Fish:
                if (toy.Loose && mayTouch && stealWanted && StealsFish(kind) && !reducedMotion) return new PetIntent(PetMove.Steal, toy.P.X);
                return new PetIntent(PetMove.Watch, toy.P.X);
            default:
                return new PetIntent(PetMove.Idle);
        }
    }

    /// <summary>
    /// A bat sends the ball back toward the player (the cursor's side of it) in a gentle lob that carries further the
    /// further away the player is; with the cursor right above it, it goes toward the middle of the screen.
    /// </summary>
    public static Vec2 BatVelocity(Vec2 ball, double pointerX, double arenaCenterX)
    {
        double dx = pointerX - ball.X;
        if (Math.Abs(dx) < 30) dx = arenaCenterX - ball.X;
        double dir = dx >= 0 ? 1 : -1;
        return new Vec2(dir * Math.Clamp(Math.Abs(dx) * 0.9, 260, 700), -760);
    }

    /// <summary>What a game sound is to a pet: a buzzer or a loud crash startles it, a cheer or a fanfare makes it dance.</summary>
    public static PetReaction Classify(string clip, double volume) => clip switch
    {
        "buzzer" => PetReaction.Startle,
        "thunk" or "kick" or "twang" or "bounce" or "board" or "pin-bumper" when volume >= LoudCrash => PetReaction.Startle,
        "best" or "done" => PetReaction.Cheer,
        "fire" when volume >= 0.5 => PetReaction.Cheer,
        _ => PetReaction.None,
    };

    /// <summary>
    /// Listens to the game sounds and says how the pet takes each one, at most one reaction every <see cref="ReactGap"/>
    /// seconds and the same one every <see cref="SameReactGap"/>, so a string of buzzers does not keep it jumping. A
    /// sleeping pet only lifts its head.
    /// </summary>
    public sealed class Ears
    {
        double _last = double.NegativeInfinity;
        readonly Dictionary<PetReaction, double> _lastOf = new();

        public PetReaction Hear(string clip, double volume, bool asleep, double now)
        {
            var r = Classify(clip, volume);
            if (r == PetReaction.None) return r;
            if (asleep) r = PetReaction.LiftHead;
            if (now - _last < ReactGap) return PetReaction.None;
            if (_lastOf.TryGetValue(r, out double same) && now - same < SameReactGap) return PetReaction.None;
            _last = now;
            _lastOf[r] = now;
            return r;
        }
    }
}
