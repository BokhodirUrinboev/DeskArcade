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

/// <summary>
/// The other side of a two-player game, for the scoreboard's chip: who it is, whether it is the computer (and at which
/// level, 1–4, or 0 when the game has no levels) and whose turn it is (null when the game has no turns).
/// </summary>
public sealed record Opponent(string Name, bool IsCpu, int Level, bool? MyTurn);

public abstract class MiniGame
{
    /// <summary>The computer opponent's levels, 1 to 4.</summary>
    public static readonly string[] LevelNames = { "Easy", "Medium", "Hard", "Expert" };

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

    /// <summary>Race games where fewer is better (darts thrown, moves made): the lower final score wins.</summary>
    public virtual bool RaceLowerIsBetter => false;

    /// <summary>A fair score for one round by a decent player, so the computer rival has something to aim at.</summary>
    public virtual int RaceBaseline => 20;

    /// <summary>The player's best round score, or 0 when there is none yet; the computer rival measures itself against it.</summary>
    public virtual int RaceBest => 0;

    /// <summary>About how long a round lasts, in seconds; the computer rival paces its scoring over this time.</summary>
    public virtual double RaceSeconds => 60;

    /// <summary>
    /// True when the game has a computer opponent whose strength can be set (tray → CPU difficulty): board games with
    /// levels, and every race game, where the computer rival plays at that level.
    /// </summary>
    public virtual bool HasCpuLevels => Race != null;

    /// <summary>The level a new player starts at: Medium for most games (board games start gently).</summary>
    protected virtual int DefaultCpuLevel => 2;

    /// <summary>The computer opponent's level, 1 (Easy) to 4 (Expert), kept per game in the settings.</summary>
    public int CpuLevel
    {
        get => Math.Clamp(Host.Settings.Levels.TryGetValue(Id, out int l) ? l : DefaultCpuLevel, 1, LevelNames.Length);
        set
        {
            Host.Settings.Levels[Id] = Math.Clamp(value, 1, LevelNames.Length);
            Host.SaveSettings();
            Host.HudChanged();
        }
    }

    /// <summary>Who this player is up against right now, for the scoreboard; null in a solo game (races supply their own).</summary>
    public virtual Opponent? Opponent => null;

    /// <summary>Tweens for the game's own animations: advance them from <see cref="Update"/> and stay busy while <see cref="Engine.Anims.Busy"/>.</summary>
    protected Anims Anims { get; } = new();

    /// <summary>A small (about 20 px) icon for the scoreboard, drawn around its origin. Called once per use.</summary>
    public abstract Sprite CreateIcon();

    /// <summary>Called on activation and whenever the arena changes size.</summary>
    public abstract void Layout();

    public virtual void Activate() => Layout();
    public virtual void Deactivate() { }

    /// <summary>Tray → Reset positions: forget where the board or table was dragged to (the next <see cref="Layout"/> re-centres it).</summary>
    public virtual void PositionsReset() { }

    /// <summary>Advance the simulation. Return true while anything is still moving.</summary>
    public abstract bool Update(double dt);

    /// <summary>Areas that should take the mouse (ball, hoop, bow...). Clicks anywhere else go to the desktop.</summary>
    public abstract void CollectHitShapes(List<HitShape> into);

    /// <summary>Mouse press inside a hit shape. Return true to capture the mouse (drag).</summary>
    public abstract bool PointerDown(Vec2 p, bool right);
    public virtual void PointerUp(Vec2 p) { }

    /// <summary>
    /// A captured press was cut off without a release (the overlay paused for Claude or was hidden). Drop what
    /// the press was doing (a held ball, an aim, a raised flipper) without acting on it: no throw, no shot.
    /// </summary>
    public virtual void PointerCancel() { }

    /// <summary>Bring the main object (ball, bow, ...) to the cursor.</summary>
    public abstract void Summon(Vec2 p);

    /// <summary>Called when <see cref="Themes.Current"/> changes: redraw the themed pieces.</summary>
    public virtual void ThemeChanged() { }

    /// <summary>Self-play step for --demo runs (smoke testing without touching the mouse).</summary>
    public virtual void DemoTick() { }

    protected static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    protected static readonly Random Rng = new();
}
