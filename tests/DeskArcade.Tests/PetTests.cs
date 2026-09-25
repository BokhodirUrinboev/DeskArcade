using System;
using System.Linq;
using DeskArcade;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

/// <summary>The desktop pets' static tables and the pure helpers behind the new behaviour: fetching, night-time, thought bubbles, LAN visits.</summary>
public class PetTests
{
    static readonly string[] NewKinds = { "hamster", "turtle", "parrot", "frog", "owl", "dragon" };

    [Fact]
    public void TwelveKindsAndTheTrayListsThemInOrder()
    {
        Assert.Equal(12, PetGame.Kinds.Length);
        Assert.Equal(PetGame.Kinds.Length, PetGame.Kinds.Distinct().Count());
        foreach (var kind in NewKinds) Assert.Contains(kind, PetGame.Kinds);
        Assert.Equal(PetGame.Kinds, Tray.PetChoices().Select(c => c.Kind).ToArray());
        Assert.All(Tray.PetChoices(), c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
    }

    [Fact]
    public void EveryKindHasVoicesForEverySay()
    {
        var clips = PetGame.ClipsUsed().ToHashSet();
        foreach (var kind in PetGame.Kinds)
        {
            foreach (var say in Enum.GetValues<PetGame.Say>())
            {
                var voices = PetGame.VoicesOf(kind, say);
                Assert.NotNull(voices);
                Assert.All(voices, v => Assert.Contains(v, clips));
                // an animal may keep quiet when content or hunting, but it always has a hello, a call and a complaint
                if (say is PetGame.Say.Hello or PetGame.Say.Surprise or PetGame.Say.Call or PetGame.Say.Happy or PetGame.Say.Upset)
                    Assert.True(voices.Length > 0, $"{kind} has no voice for {say}");
            }
        }
        // the new animals do not borrow the old ones' voices
        var old = PetGame.Kinds.Take(6).SelectMany(k => Enum.GetValues<PetGame.Say>().SelectMany(s => PetGame.VoicesOf(k, s))).ToHashSet();
        foreach (var kind in NewKinds)
        {
            var own = Enum.GetValues<PetGame.Say>().SelectMany(s => PetGame.VoicesOf(kind, s)).ToHashSet();
            Assert.True(own.Count >= 2, $"{kind} needs at least two calls of its own");
            Assert.Empty(own.Intersect(old));
        }
    }

    [Fact]
    public void EveryClipIsSynthesizedAudibleAndUnclipped()
    {
        using var sound = new Sound(new DeskArcade.Platform.NullPlatform());
        foreach (var name in PetGame.ClipsUsed().Distinct())
        {
            var clip = sound.Samples(name);
            Assert.True(clip != null, $"no clip named {name}");
            Assert.True(clip!.Length > 1000, $"{name} is too short");
            Assert.All(clip, x => Assert.True(float.IsFinite(x), $"{name} has a bad sample"));
            Assert.InRange(clip.Max(Math.Abs), 0.2f, 0.8f);
        }
        // the mixer's own short clips the pet borrows (a crunch, a pick-up, a bounce) exist too
        foreach (var name in new[] { "board", "pop", "whoosh", "bounce", "thunk", "star" }) Assert.NotNull(sound.Samples(name));
    }

    [Fact]
    public void EveryKindHasThreeHabitsWithLengths()
    {
        foreach (var kind in PetGame.Kinds)
        {
            var habits = PetGame.HabitsOf(kind);
            Assert.Equal(3, habits.Length);
            Assert.Equal(3, habits.Distinct().Count());
            Assert.All(habits, h => Assert.True(PetGame.ActLength(h) > 0, $"{kind}: {h} has no length"));
        }
        Assert.Equal(0, PetGame.ActLength("no-such-act"));
    }

    [Fact]
    public void EveryKindHasATrickOfItsOwn()
    {
        var names = PetGame.Kinds.Select(k => PetGame.TrickFor(k).Name).ToList();
        Assert.All(PetGame.Kinds, k =>
        {
            var (name, seconds) = PetGame.TrickFor(k);
            Assert.False(string.IsNullOrEmpty(name));
            Assert.True(seconds > 0);
            Assert.DoesNotContain(name, PetGame.HabitsOf(k)); // a trick is not one of the everyday habits
        });
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal("wheel", PetGame.TrickFor("hamster").Name);
        Assert.Equal("shell", PetGame.TrickFor("turtle").Name);
        Assert.Equal("loop", PetGame.TrickFor("parrot").Name);
        Assert.Equal("tongue", PetGame.TrickFor("frog").Name);
        Assert.Equal("headspin", PetGame.TrickFor("owl").Name);
        Assert.Equal("fire", PetGame.TrickFor("dragon").Name);
    }

    [Fact]
    public void WalkSpeedsSuitTheAnimal()
    {
        Assert.All(PetGame.Kinds, k => Assert.True(PetGame.WalkSpeedOf(k) > 0));
        Assert.Equal(PetGame.Kinds.Min(PetGame.WalkSpeedOf), PetGame.WalkSpeedOf("turtle"));
        Assert.Equal(PetGame.Kinds.Max(PetGame.WalkSpeedOf), PetGame.WalkSpeedOf("hamster"));
        Assert.True(PetGame.WalkSpeedOf("owl") < PetGame.WalkSpeedOf("parrot")); // an owl walks little
    }

    [Fact]
    public void FetchStylesFollowTheAnimal()
    {
        Assert.Equal(PetGame.FetchStyle.Fetch, PetGame.FetchStyleOf("dog"));
        Assert.Equal(PetGame.FetchStyle.Fetch, PetGame.FetchStyleOf("fox"));
        Assert.Equal(PetGame.FetchStyle.Sometimes, PetGame.FetchStyleOf("cat"));
        Assert.Equal(PetGame.FetchStyle.Fly, PetGame.FetchStyleOf("parrot"));
        Assert.Equal(PetGame.FetchStyle.Fly, PetGame.FetchStyleOf("owl"));
        Assert.Equal(PetGame.FetchStyle.Push, PetGame.FetchStyleOf("hamster"));
        Assert.Equal(PetGame.FetchStyle.Hop, PetGame.FetchStyleOf("frog"));
        Assert.Equal(PetGame.FetchStyle.Hop, PetGame.FetchStyleOf("bunny"));
        foreach (var watcher in new[] { "duck", "penguin", "turtle", "dragon" })
            Assert.Equal(PetGame.FetchStyle.Watch, PetGame.FetchStyleOf(watcher));
        Assert.Equal(PetGame.Kinds.Where(PetGame.IsFlyer).ToArray(), new[] { "parrot", "owl" });
    }

    [Fact]
    public void NightRunsFromTenToSixAndMorningToTen()
    {
        Assert.True(PetGame.IsNight(new TimeOnly(22, 0)));
        Assert.True(PetGame.IsNight(new TimeOnly(23, 59)));
        Assert.True(PetGame.IsNight(new TimeOnly(0, 0)));
        Assert.True(PetGame.IsNight(new TimeOnly(3, 30)));
        Assert.True(PetGame.IsNight(new TimeOnly(5, 59)));
        Assert.False(PetGame.IsNight(new TimeOnly(6, 0)));
        Assert.False(PetGame.IsNight(new TimeOnly(12, 0)));
        Assert.False(PetGame.IsNight(new TimeOnly(21, 59)));

        Assert.True(PetGame.IsMorning(new TimeOnly(6, 0)));
        Assert.True(PetGame.IsMorning(new TimeOnly(9, 59)));
        Assert.False(PetGame.IsMorning(new TimeOnly(10, 0)));
        Assert.False(PetGame.IsMorning(new TimeOnly(5, 59)));
        Assert.False(PetGame.IsMorning(new TimeOnly(15, 0)));
        // never both at once
        for (int h = 0; h < 24; h++) Assert.False(PetGame.IsNight(new TimeOnly(h, 30)) && PetGame.IsMorning(new TimeOnly(h, 30)));
    }

    [Fact]
    public void BubbleShowsTheMostPressingThought()
    {
        Assert.Equal(PetGame.Bubble.None, PetGame.ChooseBubble(false, false, false, false, false));
        Assert.Equal(PetGame.Bubble.Alarm, PetGame.ChooseBubble(true, true, true, true, true));
        Assert.Equal(PetGame.Bubble.Heart, PetGame.ChooseBubble(false, true, true, true, true));
        Assert.Equal(PetGame.Bubble.Treat, PetGame.ChooseBubble(false, false, true, true, true));
        Assert.Equal(PetGame.Bubble.Sleepy, PetGame.ChooseBubble(false, false, false, true, true));
        Assert.Equal(PetGame.Bubble.Ball, PetGame.ChooseBubble(false, false, false, false, true));
    }

    [Fact]
    public void VisitMessageRoundTrips()
    {
        var v = new PetGame.Visit("owl", 0.25, 0.9, -1, PetGame.Mode.Air, "fluff");
        string msg = PetGame.EncodeVisit(v);
        Assert.StartsWith("pt|owl|0.25|0.9|-1|air|fluff", msg);
        Assert.True(PetGame.TryDecodeVisit(msg, out var back));
        Assert.Equal(v, back);

        // fractions are rounded to four places and clamped into the arena; the act may be empty
        Assert.True(PetGame.TryDecodeVisit(PetGame.EncodeVisit(new PetGame.Visit("cat", 1.7, -0.2, 1, PetGame.Mode.Sit, "")), out var clamped));
        Assert.Equal(new PetGame.Visit("cat", 1, 0, 1, PetGame.Mode.Sit, ""), clamped);
        Assert.True(PetGame.TryDecodeVisit(PetGame.EncodeVisit(new PetGame.Visit("dragon", 0.123456, 0.5, 5, PetGame.Mode.Walk, "smoke")), out var rounded));
        Assert.Equal(0.1235, rounded.X);
        Assert.Equal(1, rounded.Face);
    }

    [Theory]
    [InlineData("rs|1|20|1")]                         // another game's message
    [InlineData("pt|unicorn|0.5|0.5|1|sit|")]         // no such animal
    [InlineData("pt|cat|1.5|0.5|1|sit|")]             // off the screen
    [InlineData("pt|cat|0.5|0.5|1|flying|")]          // no such mode
    [InlineData("pt|cat|0.5|0.5|1|sit|Groom")]        // acts are lower-case names
    [InlineData("pt|cat|0.5|0.5|1|sit")]              // a field short
    [InlineData("pt|cat|abc|0.5|1|sit|")]             // not a number
    [InlineData("")]
    public void MalformedVisitMessagesAreRejected(string message) => Assert.False(PetGame.TryDecodeVisit(message, out _));

    [Fact]
    public void PetAchievementsCoverFetchTreatsAndVisits()
    {
        var counters = Achievements.All.Where(a => a.GameId == "pet").Select(a => a.Counter).ToList();
        Assert.Contains("pet.fetches", counters);
        Assert.Contains("pet.treats", counters);
        Assert.Contains("pet.visits", counters);
        Assert.Equal(Achievements.All.Length, Achievements.All.Select(a => a.Id).Distinct().Count());
    }
}
