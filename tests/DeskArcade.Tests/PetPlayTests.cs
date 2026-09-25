using System;
using System.Collections.Generic;
using System.Linq;
using DeskArcade;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

/// <summary>
/// The pet joining the games: when it may touch anything (never over the LAN or in a race), what it does about each
/// game's toy, how each animal plays, and which game sounds it reacts to, how, and how often.
/// </summary>
public class PetPlayTests
{
    static readonly PetFloor Floor = new(26, 1894, 1040);
    static readonly Vec2 FarPointer = new(1800, 100);

    static PetIntent Choose(string kind, PetToy toy, double petX, Vec2? pointer = null, bool may = true, bool reduced = false,
        bool batReady = true, bool hideReady = true, bool steal = true) =>
        PetPlay.Choose(kind, toy, Floor, new Vec2(petX, Floor.Y - 20), pointer ?? FarPointer, may, reduced, batReady, hideReady, steal);

    static PetToy RestingBall(double x, int throws = 1) => new(PetToyKind.Ball, new Vec2(x, Floor.Y - 26), default, 26, true, throws);

    // ------------------------------------------------------------------ fairness

    [Theory]
    [InlineData(false, false, false, true)]  // solo, no race game
    [InlineData(false, false, true, true)]   // solo, "race the computer" on, but the game has no races (Hoops, Pong)
    [InlineData(false, true, false, true)]   // a race game played alone with the computer rival off
    [InlineData(false, true, true, false)]   // a race game against the computer
    [InlineData(true, false, false, false)]  // any LAN link: a duel with a co-worker
    [InlineData(true, true, true, false)]
    public void ThePetNeverTouchesAnythingInARaceOrOverTheLan(bool lan, bool raceGame, bool cpuRival, bool may) =>
        Assert.Equal(may, PetPlay.MayTouch(lan, raceGame, cpuRival));

    [Fact]
    public void WithoutLeaveToTouchItOnlyWatches()
    {
        foreach (var kind in PetGame.Kinds)
        {
            Assert.Equal(PetMove.Watch, Choose(kind, RestingBall(500), 480, may: false).Move);
            Assert.Equal(PetMove.Watch, Choose(kind, new PetToy(PetToyKind.Chase, new Vec2(900, 500)), 300, may: false).Move);
            Assert.Equal(PetMove.Watch, Choose(kind, new PetToy(PetToyKind.Fish, new Vec2(150, 900), R: 15, Loose: true), 130, may: false).Move);
        }
    }

    // ------------------------------------------------------------------ Hoops: batting the ball back

    [Fact]
    public void ABatterBatsALooseBallWithinReachAndChasesOneFurtherOff()
    {
        Assert.Equal(PetMove.Bat, Choose("dog", RestingBall(500), 460).Move);
        var chase = Choose("dog", RestingBall(500), 200);
        Assert.Equal(PetMove.Chase, chase.Move);
        Assert.InRange(chase.X, 500 - 26 - PetPlay.PawReach, 500); // stops beside the ball, on the near side
        Assert.Equal(PetMove.Watch, Choose("dog", RestingBall(1500), 200).Move); // too far to bother
    }

    [Fact]
    public void TheBallIsLeftAloneWhenItIsInPlayJustBattedOrTheCursorIsOnIt()
    {
        var inPlay = RestingBall(500) with { Loose = false };
        Assert.Equal(PetMove.Watch, Choose("cat", inPlay, 480).Move);
        Assert.Equal(PetMove.Watch, Choose("cat", RestingBall(500), 480, batReady: false).Move);
        Assert.Equal(PetMove.Watch, Choose("cat", RestingBall(500), 480, pointer: new Vec2(520, 980)).Move); // the player is going for it
        Assert.Equal(PetMove.Watch, Choose("cat", RestingBall(3000), 1880).Move);                           // off the pet's floor
    }

    [Fact]
    public void FlyersSwoopTurtlesAndOtherWatchersOnlyWatch()
    {
        Assert.Equal(PetMove.Swoop, Choose("owl", RestingBall(500), 300).Move);
        Assert.Equal(PetMove.Swoop, Choose("parrot", RestingBall(500), 700).Move);
        Assert.Equal(PetMove.Chase, Choose("owl", RestingBall(500), 300, reduced: true).Move); // reduced motion: no swooping
        foreach (var watcher in new[] { "turtle", "duck", "penguin", "dragon" })
        {
            Assert.Equal(PetBallStyle.Watch, PetPlay.BallStyleOf(watcher));
            Assert.Equal(PetMove.Watch, Choose(watcher, RestingBall(500), 480).Move);
        }
        foreach (var batter in new[] { "cat", "dog", "fox", "hamster", "bunny", "frog" })
            Assert.Equal(PetBallStyle.Bat, PetPlay.BallStyleOf(batter));
    }

    [Theory]
    [InlineData(500, 1200, 1)]  // the player is to the right
    [InlineData(1200, 300, -1)] // to the left
    public void ABatSendsTheBallUpAndBackTowardThePlayer(double ballX, double pointerX, int dir)
    {
        var v = PetPlay.BatVelocity(new Vec2(ballX, 1000), pointerX, 960);
        Assert.Equal(dir, Math.Sign(v.X));
        Assert.True(v.Y < 0, "a lob goes up");
        Assert.InRange(Math.Abs(v.X), 260, 700);
    }

    [Fact]
    public void WithTheCursorRightAboveTheBallItGoesTowardTheMiddle()
    {
        Assert.True(PetPlay.BatVelocity(new Vec2(300, 1000), 310, 960).X > 0);
        Assert.True(PetPlay.BatVelocity(new Vec2(1600, 1000), 1590, 960).X < 0);
    }

    // ------------------------------------------------------------------ Pong: chasing the ball

    [Fact]
    public void PetsRunUnderThePongBallAndWatchersTurnTheirHeads()
    {
        var ball = new PetToy(PetToyKind.Chase, new Vec2(900, 300));
        var chase = Choose("fox", ball, 300);
        Assert.Equal(PetMove.Chase, chase.Move);
        Assert.Equal(900, chase.X);
        Assert.Equal(PetMove.Watch, Choose("fox", ball, 890).Move);   // already underneath it
        Assert.Equal(PetMove.Watch, Choose("turtle", ball, 300).Move);
        // a flyer takes off after a ball coming low past it, and walks after one high up
        Assert.Equal(PetMove.Swoop, Choose("parrot", ball with { P = new Vec2(500, 950) }, 300).Move);
        Assert.Equal(PetMove.Chase, Choose("parrot", ball, 300).Move);
        // the target stays on the pet's floor
        Assert.Equal(Floor.X2, Choose("dog", ball with { P = new Vec2(1919, 500) }, 300).X);
    }

    // ------------------------------------------------------------------ Whack-a-Bug: hiding

    [Fact]
    public void ABugPeekingOutBesideThePetSendsItScurryingAwayToHide()
    {
        var bugs = new PetToy(PetToyKind.Bugs, Bugs: new List<Vec2> { new(1400, 700), new(560, 1000) });
        var hide = Choose("hamster", bugs, 500);
        Assert.Equal(PetMove.Hide, hide.Move);
        Assert.Equal(500 - PetPlay.HideStep, hide.X); // away from the near bug, not toward it
        Assert.Equal("cower", PetPlay.HideActOf("hamster"));
        // backed into the corner, it scurries past the bug instead
        var cornered = Choose("dog", new PetToy(PetToyKind.Bugs, Bugs: new List<Vec2> { new(90, 1000) }), 60);
        Assert.Equal(PetMove.Hide, cornered.Move);
        Assert.True(cornered.X > 60);
    }

    [Fact]
    public void TheTurtleHidesInItsShellAndTheFrogStandsItsGround()
    {
        var bug = new PetToy(PetToyKind.Bugs, Bugs: new List<Vec2> { new(520, 1000) });
        Assert.Equal(new PetIntent(PetMove.Hide, 500), Choose("turtle", bug, 500));
        Assert.Equal("hide", PetPlay.HideActOf("turtle"));
        Assert.Equal(new PetIntent(PetMove.Hide, 500), Choose("frog", bug, 500));
        Assert.Equal("fly", PetPlay.HideActOf("frog")); // a flick of the tongue: a bug is lunch
    }

    [Fact]
    public void FarBugsAreWatchedAndAPetThatJustHidWaits()
    {
        var far = new PetToy(PetToyKind.Bugs, Bugs: new List<Vec2> { new(1400, 1000) });
        Assert.Equal(new PetIntent(PetMove.Watch, 1400), Choose("cat", far, 500));
        var near = new PetToy(PetToyKind.Bugs, Bugs: new List<Vec2> { new(540, 1000) });
        Assert.Equal(PetMove.Watch, Choose("cat", near, 500, hideReady: false).Move);
        Assert.Equal(PetMove.Idle, Choose("cat", new PetToy(PetToyKind.Bugs, Bugs: new List<Vec2>()), 500).Move);
        // hiding is not touching: the pet still gets out of the way in a race
        Assert.Equal(PetMove.Hide, Choose("cat", near, 500, may: false).Move);
    }

    // ------------------------------------------------------------------ Fishing: stealing a fish

    [Fact]
    public void OnlyTheFishEatersStealALooseFishTheyFancy()
    {
        var fish = new PetToy(PetToyKind.Fish, new Vec2(150, 900), R: 15, Loose: true);
        foreach (var kind in PetGame.Kinds)
            Assert.Equal(PetPlay.StealsFish(kind) ? PetMove.Steal : PetMove.Watch, Choose(kind, fish, 130).Move);
        Assert.True(PetPlay.StealsFish("cat"));
        Assert.True(PetPlay.StealsFish("penguin"));
        Assert.False(PetPlay.StealsFish("turtle"));
        Assert.False(PetPlay.StealsFish("hamster"));

        Assert.Equal(PetMove.Watch, Choose("cat", fish with { Loose = false }, 130).Move); // still in the water, or already stolen this round
        Assert.Equal(PetMove.Watch, Choose("cat", fish, 130, steal: false).Move);         // this one did not tempt it
        Assert.Equal(PetMove.Watch, Choose("cat", fish, 130, reduced: true).Move);        // no leaping with reduced motion
        Assert.InRange(PetPlay.StealChance, 0.1, 0.6);
    }

    [Fact]
    public void NoToyNoMove() => Assert.Equal(PetMove.Idle, Choose("dog", PetToy.Nothing, 500).Move);

    // ------------------------------------------------------------------ the animals

    [Fact]
    public void EveryAnimalsReactionsArePosesItHas()
    {
        var own = new HashSet<string> { "bat", "startle", "cower", "dance" }; // the companion's own poses
        foreach (var kind in PetGame.Kinds)
        {
            foreach (var act in new[] { PetPlay.HideActOf(kind), PetPlay.CheerActOf(kind), PetPlay.StartleActOf(kind) })
                Assert.True(own.Contains(act) || PetGame.ActLength(act) > 0 || PetGame.TrickFor(kind).Name == act, $"{kind}: no pose for {act}");
        }
        Assert.Equal("spin", PetGame.TrickFor("dog").Name);
        Assert.Equal("spin", PetPlay.CheerActOf("dog"));       // round after its tail
        Assert.Equal("bob", PetPlay.CheerActOf("parrot"));     // bobbing to the music
        Assert.Equal("neck", PetPlay.CheerActOf("turtle"));    // it just watches
        Assert.Equal("hide", PetPlay.StartleActOf("turtle"));
        Assert.Equal("puff", PetPlay.StartleActOf("cat"));
        Assert.False(PetPlay.Jumps("turtle", false));
        Assert.False(PetPlay.Jumps("cat", true));
        Assert.True(PetPlay.Jumps("cat", false));
    }

    // ------------------------------------------------------------------ sound reactions

    [Theory]
    [InlineData("buzzer", 0.3, PetReaction.Startle)]
    [InlineData("thunk", 0.9, PetReaction.Startle)]      // a loud crash
    [InlineData("thunk", 0.4, PetReaction.None)]         // an ordinary knock
    [InlineData("kick", 0.8, PetReaction.None)]
    [InlineData("best", 0.7, PetReaction.Cheer)]
    [InlineData("done", 0.5, PetReaction.Cheer)]
    [InlineData("fire", 0.6, PetReaction.Cheer)]
    [InlineData("fire", 0.3, PetReaction.None)]
    [InlineData("score", 0.6, PetReaction.None)]
    [InlineData("swish", 0.9, PetReaction.None)]
    public void GameSoundsAreSortedIntoFrightsAndCheers(string clip, double volume, PetReaction reaction) =>
        Assert.Equal(reaction, PetPlay.Classify(clip, volume));

    [Fact]
    public void ThePetsOwnVoicesNeverSetItOff()
    {
        foreach (var clip in PetGame.ClipsUsed().Distinct())
            Assert.Equal(PetReaction.None, PetPlay.Classify(clip, 1));
    }

    [Fact]
    public void EverySoundItListensForIsARealClip()
    {
        using var sound = new Sound(new DeskArcade.Platform.NullPlatform());
        foreach (var clip in new[] { "buzzer", "thunk", "kick", "twang", "bounce", "board", "pin-bumper", "best", "done", "fire" })
        {
            Assert.NotNull(sound.Samples(clip));
            Assert.NotEqual(PetReaction.None, PetPlay.Classify(clip, 1));
        }
    }

    [Fact]
    public void SoundTellsThePetWhatPlayedOnlyWhileSoundIsOn()
    {
        using var sound = new Sound(new DeskArcade.Platform.NullPlatform());
        var heard = new List<(string, double)>();
        sound.Played += (clip, volume) => heard.Add((clip, volume));
        sound.Play("buzzer", 0.4);
        sound.Enabled = false;
        sound.Play("best", 0.8);
        Assert.Equal(new[] { ("buzzer", 0.4) }, heard);
    }

    [Fact]
    public void ReactionsAreRationedSoTheyDoNotRunTogether()
    {
        Assert.Equal(5, PetPlay.ReactGap);
        Assert.Equal(10, PetPlay.SameReactGap);
        var ears = new PetPlay.Ears();
        Assert.Equal(PetReaction.Startle, ears.Hear("buzzer", 0.4, false, 0));
        Assert.Equal(PetReaction.None, ears.Hear("best", 0.8, false, 2));      // too soon after the last one
        Assert.Equal(PetReaction.None, ears.Hear("buzzer", 0.4, false, 5));    // the same fright again, too soon
        Assert.Equal(PetReaction.Cheer, ears.Hear("best", 0.8, false, 6));
        Assert.Equal(PetReaction.None, ears.Hear("buzzer", 0.4, false, 10.5)); // too soon after the cheer
        Assert.Equal(PetReaction.Startle, ears.Hear("buzzer", 0.4, false, 11));
        Assert.Equal(PetReaction.None, ears.Hear("score", 1, false, 1000));    // not something it reacts to
    }

    [Fact]
    public void ASleepingPetOnlyLiftsItsHead()
    {
        var ears = new PetPlay.Ears();
        Assert.Equal(PetReaction.LiftHead, ears.Hear("buzzer", 0.4, true, 10));
        Assert.Equal(PetReaction.None, ears.Hear("best", 0.8, true, 12));
        Assert.Equal(PetReaction.None, ears.Hear("best", 0.8, true, 10 + PetPlay.ReactGap));     // the same lift of the head again, too soon
        Assert.Equal(PetReaction.Cheer, ears.Hear("best", 0.8, false, 10 + PetPlay.ReactGap));   // awake now: a cheer
    }

    // ------------------------------------------------------------------ the setting

    [Fact]
    public void KeepingCompanyIsOffUntilChosenAndItsStringsAreTranslated()
    {
        Assert.False(new Settings().PetCompany);
        foreach (var key in new[] { "Pet keeps me company in games", "Fish thief!", "your pet ran off with it · it still counts" })
        {
            Assert.True(Strings.Russian.ContainsKey(key), $"no Russian for {key}");
            Assert.True(Strings.Uzbek.ContainsKey(key), $"no Uzbek for {key}");
        }
    }
}
