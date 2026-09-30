using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;
using DeskArcade.Platform;

namespace DeskArcade;

/// <summary>
/// The cards the overlay shows by itself: the three-card tour on the first start (clicks go through, the menus, playing
/// while it builds), which the ☰ menu can show again, and "your year at the desk" from 15 December. One at a time, in
/// the middle of the screen; the game waits under it.
/// </summary>
public sealed partial class OverlayWindow
{
    InfoCard? _infoCard;

    /// <summary>The card on screen, for the hit shapes: it takes the mouse over its own area.</summary>
    void CollectCardShapes(List<HitShape> into)
    {
        if (_infoCard != null) into.Add(HitShape.Box(_infoCard.Area));
    }

    void ShowCard(InfoCard card)
    {
        CloseCard();
        _infoCard = card;
        _officeLayer.Children.Add(card.Root);
        card.Root.Opacity = 0;
        Fx.Anims.Add(0.25, k => card.Root.Opacity = k, Ease.OutQuad);
        PushHitShapes();
        Wake();
    }

    void CloseCard()
    {
        if (_infoCard == null) return;
        _officeLayer.Children.Remove(_infoCard.Root);
        _infoCard = null;
        PushHitShapes();
        Wake();
    }

    // ------------------------------------------------------------------ the tour

    /// <summary>Shows the tour from its first card (the first start, ☰ → Show the tour, or "--signal tour").</summary>
    public void ShowTour() => TourStep(0);

    void TourStep(int step)
    {
        if (!Shown) SetOverlayVisible(true);
        const int Steps = 3;
        string counter = L.F("{0} of {1}", step + 1, Steps);
        var actions = new List<CardAction>();
        if (step > 0) actions.Add(new CardAction(L.T("Back"), false, () => TourStep(step - 1)));
        else actions.Add(new CardAction(L.T("Skip"), false, CloseCard));
        actions.Add(step < Steps - 1
            ? new CardAction(L.T("Next"), true, () => TourStep(step + 1))
            : new CardAction(L.T("Let's play"), true, () =>
            {
                CloseCard();
                Stats.Add("tour.done");
            }));
        var (title, lines, art) = step switch
        {
            0 => (L.T("Clicks go through"), new[]
            {
                L.T("Only the game and the scoreboard take the mouse. Everywhere else your clicks and typing go to your windows, as if Desk Arcade were not there."),
                L.T("Window tops are platforms: balls, bugs and the pet land on them and ride along when you drag a window."),
            }, TourArt.ClickThrough()),
            1 => (L.T("Everything is in two menus"), new[]
            {
                L.T("Right-click the tray icon, or click ☰ on the scoreboard: games, the pet, At work, themes, and playing with co-workers."),
                L.F("{0} shows or hides the overlay · {1} next game · {2} brings the ball to the cursor",
                    Shortcuts.Label(HotkeyAction.ToggleOverlay), Shortcuts.Label(HotkeyAction.NextGame), Shortcuts.Label(HotkeyAction.Summon)),
            }, TourArt.Menus()),
            _ => (L.T("Play while it builds"), new[]
            {
                L.T("Put arcade in front of a command (arcade dotnet test) and the scoreboard shows it running, then chimes when it passes or fails."),
                L.T("Connect Claude Code and other coding agents under Coding agents & CI, and it tells you when they are done or need you."),
            }, TourArt.Terminal()),
        };
        ShowCard(new InfoCard(Arena, counter, title, lines, actions, art));
    }

    // ------------------------------------------------------------------ your year at the desk

    /// <summary>Checked on the stats timer: from 15 December the year's card comes once, while the overlay is up and nothing else is on screen.</summary>
    void CheckYearCard()
    {
        var now = DateTime.Now;
        SnapshotYear(now);
        if (!YearInReview.Due(now, Settings.YearCardShown) || !Shown || _infoCard != null || Office.FullScreen || Office.InMeeting || _demo) return;
        ShowYear();
    }

    /// <summary>At the turn of the year, the totals the next year's card counts from.</summary>
    void SnapshotYear(DateTime now)
    {
        if (Settings.YearBaseYear == now.Year) return;
        Settings.YearBaseYear = now.Year;
        Settings.YearBase = YearInReview.Counters.ToDictionary(c => c, Stats.Get);
        SaveSettings();
    }

    /// <summary>The year's card: the year so far (last year's in January), with a button to save it as a picture.</summary>
    public void ShowYear()
    {
        if (!Shown) SetOverlayVisible(true);
        var now = DateTime.Now;
        int year = YearInReview.YearOf(now);
        if (now.Month == 12) Settings.YearCardShown = now.Year;
        SaveSettings();
        var games = _games.Where(g => g.Id != "pet").Select(g => (g.Id, L.T(g.Title), Stats.SecondsPlayed(g.Id)));
        DateTime? adopted = Settings.PetAdopted.TryGetValue(Settings.PetKind, out var at) ? at : null;
        // in January the base was taken at the turn of the year, so the year just gone counts from its own start
        var lines = YearInReview.Lines(year, Stats.Get, now.Month == 1 ? null : Settings.YearBase, Waits, Rivals, games, adopted,
            Stats.UnlockedCount, Achievements.All.Length, DateTime.UtcNow);
        InfoCard? card = null;
        card = new InfoCard(Arena, L.T("Desk Arcade"), YearInReview.Title(year), lines, new[]
        {
            new CardAction(L.T("Close"), false, CloseCard),
            new CardAction(L.T("Save as a picture"), true, () => SaveCardPicture(card!)),
        }, TourArt.Calendar(year));
        ShowCard(card);
        Sound.Play("best", 0.6);
        Stats.Add("year.cards");
    }

    /// <summary>The card alone as a picture, on the clipboard and in Pictures/Desk Arcade.</summary>
    async void SaveCardPicture(InfoCard card)
    {
        try
        {
            using var bitmap = Render(card.Root, null);
            string? file = SavePicture(bitmap, "desk-arcade-year");
            await CopyPicture(bitmap);
            Notice(L.T("Picture copied · paste it into a chat"), file != null ? L.F("also saved as {0}", file) : "", Color.FromRgb(255, 209, 102));
        }
        catch (Exception e)
        {
            Notice(L.T("Couldn't copy the picture"), e.Message, Color.FromRgb(255, 107, 107));
        }
    }
}

/// <summary>The little pictures on the tour and year cards, drawn in code in the theme's colours.</summary>
static class TourArt
{
    static IBrush Ink => Art.Brush(Themes.Current.HudFront);
    static IBrush Accent => Art.Brush(Themes.Current.Accent);
    static IBrush Gold => Art.Brush(Themes.Current.Gold);
    static IBrush Soft => Art.Brush(Color.FromArgb(70, 255, 255, 255));

    /// <summary>A window with a ball on its top edge and the pointer passing through the empty desktop beside it.</summary>
    public static Control ClickThrough()
    {
        var c = new Canvas { Width = 96, Height = 96 };
        c.Children.Add(Art.At(new Rectangle { Width = 60, Height = 44, RadiusX = 4, RadiusY = 4, Fill = Soft, Stroke = Ink, StrokeThickness = 1.5 }, 4, 42));
        c.Children.Add(Art.At(new Rectangle { Width = 60, Height = 8, Fill = Accent }, 4, 42));
        c.Children.Add(Art.Circle(30, 34, 8, Art.Brush(Themes.Current.Ball)));
        c.Children.Add(Art.PathOf("M70,10 L70,34 L76,28 L81,39 L85,37 L80,26 L88,26 Z", Brushes.White, Art.Brush("#1D2129"), 1.2));
        var dash = Art.PathOf("M78,44 L78,90", null, Gold, 1.5);
        dash.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 3 };
        c.Children.Add(dash);
        return c;
    }

    /// <summary>The scoreboard's pill with its ☰ button, and a tray icon's menu under it.</summary>
    public static Control Menus()
    {
        var c = new Canvas { Width = 96, Height = 96 };
        c.Children.Add(Art.At(new Rectangle { Width = 80, Height = 24, RadiusX = 12, RadiusY = 12, Fill = Art.Brush(Themes.Current.HudBack), Stroke = Accent, StrokeThickness = 1.2 }, 4, 8));
        c.Children.Add(Art.At(new TextBlock { Text = "42", FontFamily = Fx.Font, FontSize = 13, FontWeight = FontWeight.Black, Foreground = Ink }, 14, 11));
        c.Children.Add(Art.At(new TextBlock { Text = "☰", FontFamily = Fx.Font, FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Gold }, 64, 10));
        c.Children.Add(Art.At(new Rectangle { Width = 60, Height = 52, RadiusX = 6, RadiusY = 6, Fill = Art.Brush(Themes.Current.HudBack), Stroke = Soft, StrokeThickness = 1 }, 30, 38));
        for (int i = 0; i < 4; i++)
            c.Children.Add(Art.At(new Rectangle { Width = i == 1 ? 36 : 44, Height = 4, RadiusX = 2, RadiusY = 2, Fill = i == 1 ? Accent : Soft }, 38, 46 + i * 10));
        return c;
    }

    /// <summary>A terminal running "arcade dotnet test", and the scoreboard's green dot.</summary>
    public static Control Terminal()
    {
        var c = new Canvas { Width = 96, Height = 96 };
        c.Children.Add(Art.At(new Rectangle { Width = 88, Height = 60, RadiusX = 6, RadiusY = 6, Fill = Art.Brush("#0E1116"), Stroke = Soft, StrokeThickness = 1.2 }, 4, 16));
        c.Children.Add(Art.At(new TextBlock { Text = "$ arcade", FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, Menlo, monospace"), FontSize = 11, Foreground = Gold }, 10, 24));
        c.Children.Add(Art.At(new TextBlock { Text = "dotnet test", FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, Menlo, monospace"), FontSize = 11, Foreground = Ink }, 10, 40));
        c.Children.Add(Art.Circle(80, 84, 6, Art.Brush("#3DDC84")));
        c.Children.Add(Art.At(new TextBlock { Text = "✓", FontFamily = Fx.Font, FontSize = 9, FontWeight = FontWeight.Black, Foreground = Art.Brush("#113A24") }, 76.5, 78));
        return c;
    }

    /// <summary>A desk calendar with the year on it.</summary>
    public static Control Calendar(int year)
    {
        var c = new Canvas { Width = 96, Height = 96 };
        c.Children.Add(Art.At(new Rectangle { Width = 76, Height = 70, RadiusX = 8, RadiusY = 8, Fill = Brushes.White }, 10, 16));
        c.Children.Add(Art.At(new Rectangle { Width = 76, Height = 22, RadiusX = 8, RadiusY = 8, Fill = Art.Brush("#D8333F") }, 10, 16));
        c.Children.Add(Art.At(new Rectangle { Width = 76, Height = 10, Fill = Art.Brush("#D8333F") }, 10, 28));
        c.Children.Add(Art.At(new Rectangle { Width = 4, Height = 14, RadiusX = 2, RadiusY = 2, Fill = Art.Brush("#8E1B26") }, 28, 10));
        c.Children.Add(Art.At(new Rectangle { Width = 4, Height = 14, RadiusX = 2, RadiusY = 2, Fill = Art.Brush("#8E1B26") }, 64, 10));
        var text = new TextBlock { Text = year.ToString(System.Globalization.CultureInfo.InvariantCulture), FontFamily = Fx.Font, FontSize = 22, FontWeight = FontWeight.Black, Foreground = Art.Brush("#1D2129"), Width = 76, TextAlignment = TextAlignment.Center };
        c.Children.Add(Art.At(text, 10, 46));
        return c;
    }
}
