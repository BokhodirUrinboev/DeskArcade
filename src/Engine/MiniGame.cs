using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace DeskArcade.Engine;

public interface IGameHost
{
    /// <summary>Playable area in overlay DIPs: walls = Left/Right, floor = Bottom (top of taskbar/dock).</summary>
    Rect Arena { get; }
    Platforms Platforms { get; }
    Sound Sound { get; }
    Fx Fx { get; }
    Settings Settings { get; }
    /// <summary>Live mouse position in overlay DIPs.</summary>
    Vec2 Pointer { get; }
    /// <summary>Area the HUD occupies, so games can avoid spawning things under it.</summary>
    Rect HudBounds { get; }
    void HudChanged();
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

    /// <summary>Self-play step for --demo runs (smoke testing without touching the mouse).</summary>
    public virtual void DemoTick() { }

    protected static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    protected static readonly Random Rng = new();
}
