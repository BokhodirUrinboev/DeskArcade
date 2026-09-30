using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using DeskArcade.Engine;
using DeskArcade.Games;
using Xunit;

namespace DeskArcade.Tests;

public class KeyChordTests
{
    static KeyChord P(string text) => KeyChord.Parse(text)!.Value;

    [Theory]
    [InlineData("Ctrl+Shift+P", KeyOs.Windows, "Ctrl+Shift+P")]
    [InlineData("Ctrl+Shift+P", KeyOs.Linux, "Ctrl+Shift+P")]
    [InlineData("Cmd+Shift+P", KeyOs.Mac, "⇧⌘P")]
    [InlineData("Shift+Alt+F", KeyOs.Windows, "Shift+Alt+F")]
    [InlineData("Option+Cmd+Down", KeyOs.Mac, "⌥⌘↓")]
    [InlineData("Ctrl+Option+O", KeyOs.Mac, "⌃⌥O")]
    [InlineData("Win+E", KeyOs.Windows, "Win+E")]
    [InlineData("Super+E", KeyOs.Linux, "Super+E")]
    [InlineData("Cmd+Backspace", KeyOs.Mac, "⌘⌫")]
    [InlineData("Ctrl+Backspace", KeyOs.Windows, "Ctrl+Backspace")]
    [InlineData("Shift+F12", KeyOs.Mac, "⇧F12")]
    [InlineData("Ctrl+`", KeyOs.Linux, "Ctrl+`")]
    [InlineData("Alt+Enter", KeyOs.Windows, "Alt+Enter")]
    [InlineData("Option+Enter", KeyOs.Mac, "⌥↩")]
    [InlineData("Ctrl+PageDown", KeyOs.Windows, "Ctrl+PgDn")]
    public void KeysAreNamedAsEachDesktopNamesThem(string keys, KeyOs os, string shown) => Assert.Equal(shown, P(keys).Text(os));

    [Fact]
    public void ModifiersReadInAnyOrderAndAnyName()
    {
        Assert.Equal(P("Ctrl+Shift+P"), P("shift+control+p"));
        Assert.Equal(P("Cmd+Option+L"), P("Meta+Alt+L"));
        Assert.Equal(new KeyChord(KeyMods.None, "F2"), P("f2"));
        Assert.Equal(new KeyChord(KeyMods.Ctrl, "Esc"), P("Ctrl+Escape"));
    }

    [Theory]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+K")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+é")]
    [InlineData("Ctrl+Tabs")]
    public void NonsenseIsRefused(string text) => Assert.Null(KeyChord.Parse(text));

    [Fact]
    public void ASequenceIsChordsOneAfterAnother()
    {
        var zen = KeyChord.ParseSequence("Ctrl+K Z")!;
        Assert.Equal(new[] { P("Ctrl+K"), P("Z") }, zen);
        Assert.Equal("Ctrl+K Z", KeyChord.Text(zen, KeyOs.Windows));
        Assert.Equal("⌘K Z", KeyChord.Text(KeyChord.ParseSequence("Cmd+K Z")!, KeyOs.Mac));
        Assert.Null(KeyChord.ParseSequence("Ctrl+K Nope"));
    }

    [Fact]
    public void AvaloniaKeysBecomeLayoutFreeNames()
    {
        Assert.Equal(P("Ctrl+Shift+P"), KeyChord.From(Key.P, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal(P("Cmd+/"), KeyChord.From(Key.OemQuestion, KeyModifiers.Meta));
        Assert.Equal(P("Ctrl+`"), KeyChord.From(Key.OemTilde, KeyModifiers.Control));
        Assert.Equal(P("Ctrl+="), KeyChord.From(Key.OemPlus, KeyModifiers.Control));
        Assert.Equal(P("Alt+F7"), KeyChord.From(Key.F7, KeyModifiers.Alt));
        Assert.Equal(P("Ctrl+0"), KeyChord.From(Key.D0, KeyModifiers.Control));
        Assert.Equal(P("PageDown"), KeyChord.From(Key.PageDown, KeyModifiers.None));
        Assert.Null(KeyChord.From(Key.LeftCtrl, KeyModifiers.Control)); // a modifier on its own isn't a shortcut yet
        Assert.Null(KeyChord.From(Key.LWin, KeyModifiers.Meta));
    }
}

public class ShortcutDrillTests
{
    [Fact]
    public void EveryDrillReadsOnEveryDesktopItIsOn()
    {
        foreach (var d in ShortcutDrills.All)
            foreach (var os in Enum.GetValues<KeyOs>())
            {
                if (!d.On(os)) continue;
                if (d.Typed) Assert.All(d.Answers(os), a => Assert.False(string.IsNullOrWhiteSpace(a), d.Id));
                else Assert.All(d.Answers(os), a => Assert.True(KeyChord.ParseSequence(a) != null, $"{d.Id} on {os}: {a}"));
            }
    }

    [Fact]
    public void EverySetHasADrillsWorthOnEveryDesktop()
    {
        foreach (string set in ShortcutDrills.Sets)
            foreach (var os in Enum.GetValues<KeyOs>())
                Assert.True(ShortcutDrills.For(set, os).Count >= ShortcutRound.Size, $"{set} on {os}");
    }

    [Fact]
    public void NoDrillIsListedTwiceInASet() =>
        Assert.Equal(ShortcutDrills.All.Count, ShortcutDrills.All.Select(d => d.Id).Distinct().Count());

    [Fact]
    public void MacKeysUseCommandNotControl_WhereTheDesktopDoes()
    {
        var copy = ShortcutDrills.All.Single(d => d.Id == "desktop:Copy");
        Assert.Equal("Ctrl+C", copy.Shown(KeyOs.Windows));
        Assert.Equal("Ctrl+C", copy.Shown(KeyOs.Linux));
        Assert.Equal("⌘C", copy.Shown(KeyOs.Mac));
        var history = ShortcutDrills.All.Single(d => d.Id == "shell:Search the history");
        Assert.Equal("⌃R", history.Shown(KeyOs.Mac)); // the shell is Control everywhere
    }

    [Fact]
    public void KeysTheDesktopTakesForItselfAreLeftOut()
    {
        // the typing window never hears these: GNOME opens a terminal or locks the screen, macOS quits or hides the app
        var taken = new Dictionary<KeyOs, string[]>
        {
            [KeyOs.Windows] = new[] { "Alt+F4", "Alt+Tab", "Alt+Space", "Ctrl+Shift+Esc" },
            [KeyOs.Linux] = new[] { "Ctrl+Alt+T", "Ctrl+Alt+L", "Ctrl+Alt+Delete", "Alt+F4", "Alt+Tab" },
            [KeyOs.Mac] = new[] { "Cmd+Q", "Cmd+H", "Cmd+M", "Cmd+W", "Cmd+Tab", "Cmd+Space" },
        };
        foreach (var (os, keys) in taken)
        {
            var reserved = keys.Select(k => KeyChord.Parse(k)!.Value).ToHashSet();
            foreach (var d in ShortcutDrills.All.Where(d => d.On(os) && !d.Typed))
                Assert.All(d.Chords(os).SelectMany(a => a), c => Assert.DoesNotContain(c, reserved));
        }
    }

    [Fact]
    public void MissedDrillsComeBackMoreOften()
    {
        var pool = ShortcutDrills.For("vscode", KeyOs.Windows);
        var sore = pool[3];
        int picked = 0, trials = 2000;
        var rng = new Random(7);
        for (int i = 0; i < trials; i++)
            if (ShortcutDrills.Pick(pool, 1, d => d == sore ? 5 : 0, rng)[0] == sore) picked++;
        double expected = ShortcutDrills.Weight(5) / (ShortcutDrills.Weight(5) + pool.Count - 1);
        Assert.InRange(picked / (double)trials, expected - 0.05, expected + 0.05);
        Assert.True(expected > 3.0 / pool.Count); // several times the even share
    }

    [Fact]
    public void ADrillIsTenDifferentShortcuts()
    {
        var drill = ShortcutDrills.Pick(ShortcutDrills.For("vim", KeyOs.Linux), ShortcutRound.Size, _ => 0, new Random(1));
        Assert.Equal(ShortcutRound.Size, drill.Count);
        Assert.Equal(drill.Count, drill.Distinct().Count());
    }
}

public class ShortcutRoundTests
{
    static ShortcutDrill Drill(string id) => ShortcutDrills.All.Single(d => d.Id == id);
    static KeyChord P(string text) => KeyChord.Parse(text)!.Value;

    [Fact]
    public void ARightChordAtOnceScoresTheSpeedBonus()
    {
        var r = new ShortcutRound(new[] { Drill("vscode:Show all commands"), Drill("vscode:Rename symbol") }, KeyOs.Windows);
        r.Start();
        r.Tick(ShortcutRound.BonusSeconds / 2);
        Assert.Equal(ShortcutAnswer.Right, r.Press(P("Ctrl+Shift+P")));
        Assert.Equal(ShortcutRound.Base + ShortcutRound.SpeedBonus / 2, r.LastPoints);
        r.Next();
        r.Tick(ShortcutRound.BonusSeconds * 2);
        Assert.Equal(ShortcutAnswer.Right, r.Press(P("F2")));
        Assert.Equal(ShortcutRound.Base, r.LastPoints); // the bonus has run out, the base stays
        r.Next();
        Assert.Equal(ShortcutPhase.Over, r.Phase);
        Assert.True(r.Perfect);
    }

    [Fact]
    public void AMissShowsTheAnswerAndItMustBePressedForNothing()
    {
        var r = new ShortcutRound(new[] { Drill("vscode:Go to definition") }, KeyOs.Mac);
        r.Start();
        Assert.Equal(ShortcutAnswer.Wrong, r.Press(P("Cmd+B")));
        Assert.Equal(ShortcutPhase.Missed, r.Phase);
        Assert.Equal(ShortcutAnswer.Right, r.Press(P("F12")));
        Assert.Equal(0, r.LastPoints);
        Assert.True(r.MissedThis);
        r.Next();
        Assert.False(r.Perfect);
        Assert.Equal(0, r.FirstTry);
    }

    [Fact]
    public void ASequenceGoesChordByChordAndAWrongStepStartsOver()
    {
        var r = new ShortcutRound(new[] { Drill("vscode:Zen mode") }, KeyOs.Windows);
        r.Start();
        Assert.Equal(ShortcutAnswer.Progress, r.Press(P("Ctrl+K")));
        Assert.Single(r.Pressed);
        Assert.Equal(ShortcutAnswer.Wrong, r.Press(P("X")));
        Assert.Empty(r.Pressed);
        Assert.Equal(ShortcutAnswer.Progress, r.Press(P("Ctrl+K")));
        Assert.Equal(ShortcutAnswer.Right, r.Press(P("Z")));
    }

    [Fact]
    public void AnyOfTheAnswersWill_Do()
    {
        var redo = Drill("desktop:Redo");
        foreach (string keys in new[] { "Ctrl+Y", "Ctrl+Shift+Z" })
        {
            var r = new ShortcutRound(new[] { redo }, KeyOs.Windows);
            r.Start();
            Assert.Equal(ShortcutAnswer.Right, r.Press(P(keys)));
        }
        var linux = new ShortcutRound(new[] { redo }, KeyOs.Linux);
        linux.Start();
        Assert.Equal(ShortcutAnswer.Wrong, linux.Press(P("Ctrl+Y"))); // Linux has only the one
    }

    [Fact]
    public void ATypedDrillIsTypedAndCaseMatters()
    {
        var r = new ShortcutRound(new[] { Drill("vim:Go to the last line"), Drill("vim:Save and quit") }, KeyOs.Linux);
        r.Start();
        Assert.Equal(ShortcutAnswer.Wrong, r.Type("g"));
        Assert.Equal(ShortcutAnswer.Right, r.Type("G"));
        r.Next();
        Assert.Equal(ShortcutAnswer.Progress, r.Type(":"));
        Assert.Equal(ShortcutAnswer.Progress, r.Type("w"));
        r.Backspace();
        Assert.Equal(":", r.Buffer);
        Assert.Equal(ShortcutAnswer.Right, r.Type("x")); // :x does the same
        Assert.Equal(ShortcutAnswer.Ignored, r.Press(P("Ctrl+S"))); // already got: nothing more to press
    }

    [Fact]
    public void AChordForATypedDrillIsAMiss()
    {
        var r = new ShortcutRound(new[] { Drill("shell:Run the last command again") }, KeyOs.Windows);
        r.Start();
        Assert.Equal(ShortcutAnswer.Wrong, r.Press(P("Ctrl+P")));
        Assert.Equal(ShortcutAnswer.Right, r.Type("!!"));
    }

    [Fact]
    public void SkippingCountsAsAMiss()
    {
        var r = new ShortcutRound(new[] { Drill("jetbrains:Find usages") }, KeyOs.Windows);
        r.Start();
        r.Skip();
        Assert.Equal(ShortcutPhase.Got, r.Phase);
        Assert.True(r.MissedThis);
        Assert.Equal(0, r.Score);
    }
}
