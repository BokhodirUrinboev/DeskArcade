using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using DeskArcade.Net;

namespace DeskArcade.Engine;

public interface IGameHost
{
    /// <summary>
    /// Playable area in overlay DIPs. It is a closed box: walls = Left/Right, ceiling = Top,
    /// floor = Bottom (top of taskbar/dock). Nothing should leave it.
    /// </summary>
    Rect Arena { get; }
    Platforms Platforms { get; }
    Sound Sound { get; }
    Fx Fx { get; }
    Settings Settings { get; }
    /// <summary>Counters and achievements. Report events like Stats.Add("hoops.baskets") or Stats.Max("hoops.streak", n).</summary>
    Stats Stats { get; }
    /// <summary>Live mouse position in overlay DIPs.</summary>
    Vec2 Pointer { get; }
    /// <summary>The local-network link to a second player. Games that support it check <c>Lan.Connected</c>.</summary>
    LanLink Lan { get; }
    /// <summary>Area the HUD occupies, so games can avoid spawning things under it.</summary>
    Rect HudBounds { get; }
    void HudChanged();
    /// <summary>Round-based games report rounds, so race mode can start the rival's and compare scores.</summary>
    void RoundStarted();
    void RoundEnded(int score);
    /// <summary>
    /// Over the LAN, shows the other player a ghost marker where this player just clicked, popped or whacked
    /// something (<paramref name="points"/>: what it scored, 0 for a miss, negative for a penalty).
    /// </summary>
    void ShareAction(Vec2 at, int points);
    void Wake();
    void SaveSettings();
}

public sealed record HudInfo(string Score, string Line, string Best);

public abstract class MiniGame
{
    protected MiniGame(IGameHost host) => Host = host;

    protected IGameHost Host { get; }
    public Canvas Layer { get; } = new();

    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract HudInfo Hud { get; }

    /// <summary>True if two players can share this game over <see cref="IGameHost.Lan"/>.</summary>
    public virtual bool SupportsLan => false;

    /// <summary>Round-based games that can be raced over the LAN: the round's score and whether it's running.</summary>
    public virtual (int Score, bool Active)? Race => null;

    /// <summary>Race mode: start a round now, because the other player just started theirs.</summary>
    public virtual void StartRace() { }

    /// <summary>A small (about 20 px) icon for the scoreboard, drawn around its origin. Called once per use.</summary>
    public abstract Sprite CreateIcon();

    /// <summary>Called on activation and whenever the arena changes size.</summary>
    public abstract void Layout();

    public virtual void Activate() => Layout();
    public virtual void Deactivate() { }

    /// <summary>Advance the simulation. Return true while anything is still moving.</summary>
    public abstract bool Update(double dt);

    /// <summary>Areas that should take the mouse (ball, hoop, bow...). Clicks anywhere else go to the desktop.</summary>
    public abstract void CollectHitShapes(List<HitShape> into);

    /// <summary>Mouse press inside a hit shape. Return true to capture the mouse (drag).</summary>
    public abstract bool PointerDown(Vec2 p, bool right);
    public virtual void PointerUp(Vec2 p) { }

    /// <summary>Bring the main object (ball, bow, ...) to the cursor.</summary>
    public abstract void Summon(Vec2 p);

    /// <summary>Called when <see cref="Themes.Current"/> changes: redraw the themed pieces.</summary>
    public virtual void ThemeChanged() { }

    /// <summary>Self-play step for --demo runs (smoke testing without touching the mouse).</summary>
    public virtual void DemoTick() { }

    protected static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    protected static readonly Random Rng = new();
}
