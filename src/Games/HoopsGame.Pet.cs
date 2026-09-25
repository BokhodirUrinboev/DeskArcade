using System;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// The pet in Hoops: once a ball lies loose on the floor (the shot is over, nobody holds it, no H-O-R-S-E game is on),
/// the pet may bat it back toward the player, once per throw. A batted ball can never score, and a missed shot's streak
/// ends as it would have when the ball was next picked up.
/// </summary>
public sealed partial class HoopsGame : IPetPlayground
{
    /// <summary>The player's throws so far: the pet bats a ball back at most once per throw.</summary>
    int _petThrows;

    public PetFloor PetFloor
    {
        get
        {
            var a = Host.Arena;
            return new PetFloor(a.Left + 26, a.Right - 26, a.Bottom);
        }
    }

    public PetToy PetToy => new(PetToyKind.Ball, _ball.Pos, _ball.Vel, BallR,
        !_holding && !HorseOn && (_ball.Asleep || _ball.Grounded) && _ball.GroundHwnd == IntPtr.Zero && _ball.Vel.Length < 300, _petThrows);

    public bool PetTouched(PetTouch touch, Vec2 v)
    {
        if (touch != PetTouch.Bat || _holding || HorseOn || !(_ball.Asleep || _ball.Grounded)) return false;
        if (_throwLive && !_scored) BreakStreak(); // the shot had missed: the streak was over anyway
        _throwLive = false;                        // a batted ball never scores
        _ball.Place(_ball.Pos - new Vec2(0, 2), v);
        _ball.Spin = -v.X * 0.25;
        PlayThrottled("bounce", 0.35, 1.2);
        Host.Wake();
        return true;
    }
}
