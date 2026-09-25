using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using DeskArcade.Platform.Linux;
using Xunit;

namespace DeskArcade.Tests;

/// <summary>The display-free parts of the Linux overlay: EWMH message building, the Wayland stale-pointer rule, packaging.</summary>
public class LinuxOverlayTests
{
    // ------------------------------------------------------------------ EWMH helpers

    [Fact]
    public void OverlayCarriesTheFourAlwaysOnTopStates() =>
        Assert.Equal(new[] { "_NET_WM_STATE_ABOVE", "_NET_WM_STATE_STICKY", "_NET_WM_STATE_SKIP_TASKBAR", "_NET_WM_STATE_SKIP_PAGER" }, Ewmh.OverlayStates);

    [Fact]
    public void MergeStatesKeepsTheManagersListAndAddsWhatIsMissing()
    {
        long[] existing = { 70, 71 }; // e.g. MAXIMIZED_VERT and FOCUSED, set by the manager
        long[] wanted = { 71, 80, 81 };
        Assert.Equal(new long[] { 70, 71, 80, 81 }, Ewmh.MergeStates(existing, wanted));
        Assert.Equal(wanted, Ewmh.MergeStates(Array.Empty<long>(), wanted));
        Assert.Equal(existing, Ewmh.MergeStates(existing, Array.Empty<long>()));
    }

    [Fact]
    public void PairsSendTwoStatesPerMessageAndPadTheLastOne()
    {
        Assert.Equal(new[] { (80L, 81L), (82L, 83L) }, Ewmh.Pairs(new long[] { 80, 81, 82, 83 }));
        Assert.Equal(new[] { (80L, 81L), (82L, 0L) }, Ewmh.Pairs(new long[] { 80, 81, 82 }));
        Assert.Empty(Ewmh.Pairs(Array.Empty<long>()));
    }

    [Fact]
    public void StateMessageIsActionStatesAndSourceIndication()
    {
        Assert.Equal(new long[] { 1, 80, 81, 1, 0 }, Ewmh.StateMessage(Ewmh.StateAdd, 80, 81));
        Assert.Equal(new long[] { 0, 80, 0, 1, 0 }, Ewmh.StateMessage(Ewmh.StateRemove, 80));
    }

    [Fact]
    public void DesktopMessageAsksForEveryWorkspace()
    {
        Assert.Equal(0xFFFFFFFFL, Ewmh.AllDesktops);
        Assert.Equal(new long[] { 0xFFFFFFFF, 1, 0, 0, 0 }, Ewmh.DesktopMessage(Ewmh.AllDesktops));
    }

    [Fact]
    public void ClientMessageHasTheLp64EventLayout()
    {
        long[] data = { 1, 80, 81, 1, 0 };
        long[] ev = Ewmh.ClientMessage(0x400001, 42, data);

        Assert.Equal(24, ev.Length); // sizeof(XEvent) / sizeof(long)
        Assert.Equal(33, ev[0]); // ClientMessage
        Assert.Equal(0, ev[1]); // serial
        Assert.Equal(0, ev[2]); // send_event
        Assert.Equal(0, ev[3]); // display
        Assert.Equal(0x400001, ev[4]); // window
        Assert.Equal(42, ev[5]); // message_type
        Assert.Equal(32, ev[6]); // format
        Assert.Equal(data, ev.Skip(7).Take(5)); // data.l
        Assert.All(ev.Skip(12), v => Assert.Equal(0, v));
    }

    [Fact]
    public void ClientMessageCarriesAtMostFiveLongs()
    {
        Assert.Equal(new long[] { 7 }, Ewmh.ClientMessage(1, 2, new long[] { 7 }).Skip(7).Take(1));
        Assert.Throws<ArgumentException>(() => Ewmh.ClientMessage(1, 2, new long[6]));
    }

    [Theory]
    [InlineData(0, false)] // None
    [InlineData(1, false)] // PointerRoot
    [InlineData(2, true)]
    [InlineData(0x400001, true)]
    public void OnlyRealWindowsGetFocusBack(long window, bool real) => Assert.Equal(real, Ewmh.IsRealWindow(window));

    [Fact]
    public void BypassCompositorIsNever() => Assert.Equal(2, Ewmh.BypassCompositorNever);

    // ------------------------------------------------------------------ Wayland stale pointer rule

    static readonly TimeSpan Grace = TimeSpan.FromSeconds(1.5);
    static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void AMovingPointerIsAlwaysTrusted()
    {
        var rule = new PointerStaleness(Grace);
        for (int i = 0; i < 50; i++)
            Assert.True(rule.Trust(new PixelPoint(i, 0), overOwnInput: false, At(i * 2.0)));
    }

    [Fact]
    public void AStillPointerIsTrustedThroughTheGracePeriod()
    {
        var rule = new PointerStaleness(Grace);
        var p = new PixelPoint(300, 200);
        Assert.True(rule.Trust(p, false, At(0)));
        Assert.True(rule.Trust(p, false, At(0.5)));
        Assert.True(rule.Trust(p, false, At(1.5)));
    }

    [Fact]
    public void AStillPointerOutsideOurInputGoesStaleAfterTheGracePeriod()
    {
        var rule = new PointerStaleness(Grace);
        var p = new PixelPoint(300, 200);
        Assert.True(rule.Trust(p, false, At(0)));
        Assert.False(rule.Trust(p, false, At(1.6)));
        Assert.False(rule.Trust(p, false, At(10)));
    }

    [Fact]
    public void AStillPointerOverOurOwnInputNeverGoesStale()
    {
        var rule = new PointerStaleness(Grace);
        var p = new PixelPoint(300, 200);
        Assert.True(rule.Trust(p, true, At(0)));
        Assert.True(rule.Trust(p, true, At(5)));
        // leaving our input restarts the clock from the last sighting there
        Assert.True(rule.Trust(p, false, At(6)));
        Assert.False(rule.Trust(p, false, At(6.6)));
    }

    [Fact]
    public void MovementRestoresTrustAtOnce()
    {
        var rule = new PointerStaleness(Grace);
        Assert.True(rule.Trust(new PixelPoint(1, 1), false, At(0)));
        Assert.False(rule.Trust(new PixelPoint(1, 1), false, At(3)));
        Assert.True(rule.Trust(new PixelPoint(2, 1), false, At(3.1)));
        Assert.True(rule.Trust(new PixelPoint(2, 1), false, At(4.5)));
        Assert.False(rule.Trust(new PixelPoint(2, 1), false, At(4.7)));
    }

    [Fact]
    public void ResetForgetsTheHistory()
    {
        var rule = new PointerStaleness(Grace);
        Assert.True(rule.Trust(new PixelPoint(1, 1), false, At(0)));
        Assert.False(rule.Trust(new PixelPoint(1, 1), false, At(3)));
        rule.Reset();
        Assert.True(rule.Trust(new PixelPoint(1, 1), false, At(3)));
    }

    // ------------------------------------------------------------------ packaging

    static string Packaging(params string[] parts) => Path.Combine(new[] { TranslationCoverageTests.RepoRoot(), "packaging" }.Concat(parts).ToArray());

    /// <summary>The package names of a Debian control relationship field, alternatives and all, without version constraints.</summary>
    static string[] ControlField(string control, string field)
    {
        var line = Regex.Match(control, $@"^{field}:(.*)$", RegexOptions.Multiline);
        if (!line.Success) return Array.Empty<string>();
        return line.Groups[1].Value.Split(',')
            .SelectMany(entry => entry.Split('|'))
            .Select(name => Regex.Replace(name, @"\(.*?\)", "").Trim())
            .Where(name => name.Length > 0)
            .ToArray();
    }

    [Fact]
    public void ControlFieldParserHandlesVersionsAndAlternatives()
    {
        const string control = "Package: x\nDepends: libc6 (>= 2.34), libssl3t64 | libssl3, procps\nRecommends: libgl1\n";
        Assert.Equal(new[] { "libc6", "libssl3t64", "libssl3", "procps" }, ControlField(control, "Depends"));
        Assert.Equal(new[] { "libgl1" }, ControlField(control, "Recommends"));
        Assert.Empty(ControlField(control, "Suggests"));
    }

    [Fact]
    public void DebPackageDependsOnEveryNativeLibraryTheAppLoads()
    {
        string[] depends = ControlField(File.ReadAllText(Packaging("linux", "control.in")), "Depends");
        // Avalonia.X11 imports X11, Xext, Xi, Xrandr, Xcursor, Xfixes, ICE and SM; SkiaSharp links fontconfig;
        // sound goes through libpulse-simple (libpulse0); the .NET runtime needs libc, libgcc_s and libstdc++
        foreach (string lib in new[]
        {
            "libc6", "libgcc-s1", "libstdc++6", "libx11-6", "libxext6", "libxi6", "libxrandr2", "libxcursor1", "libxfixes3",
            "libice6", "libsm6", "libfontconfig1", "libpulse0",
        })
            Assert.Contains(lib, depends);
        Assert.Equal(depends.Length, depends.Distinct().Count());
    }

    [Fact]
    public void DebPackageRecommendsTheAppIndicatorExtension()
    {
        string[] recommends = ControlField(File.ReadAllText(Packaging("linux", "control.in")), "Recommends");
        Assert.Contains("gnome-shell-extension-appindicator", recommends);
        Assert.Contains("libgl1", recommends);
    }

    [Theory]
    [InlineData("linux", "deskarcade.desktop")]
    [InlineData("flatpak", "com.imperiumgames.DeskArcade.desktop")]
    public void DesktopEntriesDoNotWaitForStartupNotificationAndHaveKeywords(string folder, string name)
    {
        var lines = File.ReadAllLines(Packaging(folder, name));
        Assert.Contains("StartupNotify=false", lines);
        string keywords = Assert.Single(lines, l => l.StartsWith("Keywords=", StringComparison.Ordinal));
        Assert.EndsWith(";", keywords);
        Assert.Contains("overlay", keywords.Split('=')[1].Split(';'));
        Assert.Contains("Exec=deskarcade", lines);
    }

    [Fact]
    public void FlatpakMayTalkToTheTrayWatcherItChecksFor()
    {
        string manifest = File.ReadAllText(Packaging("flatpak", "com.imperiumgames.DeskArcade.yml"));
        Assert.Contains("--talk-name=" + X11Platform.TrayWatcher, manifest);
        Assert.Equal("org.kde.StatusNotifierWatcher", X11Platform.TrayWatcher);
    }
}
