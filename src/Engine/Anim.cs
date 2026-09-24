using System;
using System.Collections.Generic;

namespace DeskArcade.Engine;

/// <summary>Easing curves for <see cref="Anims"/>: 0 at the start, 1 at the end.</summary>
public static class Ease
{
    public static double Linear(double t) => t;
    public static double InQuad(double t) => t * t;
    public static double OutQuad(double t) => 1 - (1 - t) * (1 - t);
    public static double InOutQuad(double t) => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
    public static double InCubic(double t) => t * t * t;
    public static double OutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    public static double InOutCubic(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    /// <summary>Overshoots a little and settles: for things that land or pop into place.</summary>
    public static double OutBack(double t)
    {
        const double c = 1.70158;
        return 1 + (c + 1) * Math.Pow(t - 1, 3) + c * Math.Pow(t - 1, 2);
    }

    public static double OutElastic(double t) =>
        t <= 0 ? 0 : t >= 1 ? 1 : Math.Pow(2, -10 * t) * Math.Sin((t * 10 - 0.75) * (2 * Math.PI / 3)) + 1;

    public static double OutBounce(double t)
    {
        const double n = 7.5625, d = 2.75;
        if (t < 1 / d) return n * t * t;
        if (t < 2 / d) return n * (t -= 1.5 / d) * t + 0.75;
        if (t < 2.5 / d) return n * (t -= 2.25 / d) * t + 0.9375;
        return n * (t -= 2.625 / d) * t + 0.984375;
    }

    /// <summary>Up and back down (0 → 1 → 0): a bump, a flash, a wobble.</summary>
    public static double Pulse(double t) => Math.Sin(Math.PI * t);
}

/// <summary>
/// The tweens that are running. Each one calls <c>apply(k)</c> with its eased progress (0 → 1) on every frame and
/// once more with 1 when it ends, then its <c>done</c> callback. With reduced motion a tween jumps straight to its end
/// on its first frame (after any delay), so nothing is skipped, only the movement in between. Games keep their own
/// list (<see cref="MiniGame.Anims"/>), advance it from <see cref="MiniGame.Update"/> and stay busy while it is.
/// </summary>
public sealed class Anims
{
    public sealed class Tween
    {
        internal double Age, Delay, Seconds;
        internal Func<double, double> Curve = Ease.OutCubic;
        internal Action<double> Apply = _ => { };
        internal Action? Done;

        public bool Finished { get; internal set; }

        /// <summary>Drops the tween at the next update, without its final frame or its callback.</summary>
        public void Cancel() => Finished = true;
    }

    readonly List<Tween> _tweens = new();

    public bool Busy => _tweens.Count > 0;

    /// <param name="seconds">How long the tween runs, after <paramref name="delay"/>.</param>
    /// <param name="apply">Receives the eased progress, 0 → 1.</param>
    /// <param name="ease">One of <see cref="Ease"/>; ease-out cubic by default.</param>
    /// <param name="done">Runs once the tween has ended.</param>
    /// <param name="delay">Seconds to wait first. Delays are kept even with reduced motion, so sequences keep their timing.</param>
    public Tween Add(double seconds, Action<double> apply, Func<double, double>? ease = null, Action? done = null, double delay = 0)
    {
        var t = new Tween
        {
            Seconds = Math.Max(0.0001, seconds), Apply = apply, Curve = ease ?? Ease.OutCubic, Done = done, Delay = Math.Max(0, delay),
        };
        _tweens.Add(t);
        return t;
    }

    /// <summary>Runs <paramref name="done"/> after <paramref name="seconds"/>: a timer that follows the game's frames.</summary>
    public Tween After(double seconds, Action done) => Add(0.0001, _ => { }, Ease.Linear, done, seconds);

    /// <summary>Advances every tween; true while any is still running.</summary>
    public bool Update(double dt)
    {
        int count = _tweens.Count; // a tween added by a callback starts on the next frame
        for (int i = 0; i < count; i++)
        {
            var t = _tweens[i];
            if (t.Finished)
            {
                _tweens.RemoveAt(i--);
                count--;
                continue;
            }
            double step = dt;
            if (t.Delay > 0)
            {
                t.Delay -= step;
                if (t.Delay > 0) continue;
                step = -t.Delay;
                t.Delay = 0;
            }
            t.Age += step;
            double k = Fx.ReducedMotion ? 1 : Math.Min(1, t.Age / t.Seconds);
            t.Apply(t.Curve(k));
            if (k < 1) continue;
            t.Finished = true;
            _tweens.RemoveAt(i--);
            count--;
            t.Done?.Invoke();
            if (count > _tweens.Count) count = _tweens.Count; // a callback may have cleared or finished the rest
        }
        return _tweens.Count > 0;
    }

    /// <summary>Ends every tween now: each gets its final frame and its callback (for leaving a game mid-animation).</summary>
    public void Finish()
    {
        var pending = _tweens.ToArray(); // a callback that adds a tween leaves it for the next update, rather than looping here
        _tweens.Clear();
        foreach (var t in pending)
        {
            if (t.Finished) continue;
            t.Finished = true;
            t.Apply(t.Curve(1));
            t.Done?.Invoke();
        }
    }

    /// <summary>Drops every tween without finishing it.</summary>
    public void Clear() => _tweens.Clear();
}
