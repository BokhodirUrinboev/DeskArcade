using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DeskArcade.Engine;
using DeskArcade.Office;

namespace DeskArcade;

/// <summary>
/// Join buttons: a meeting with a Teams, Zoom, Google Meet, Webex or Jitsi link gets its heads-up and its "starting
/// now" as a card with a Join button that opens the link in the browser. A meeting without one keeps the plain notice.
/// Cards due during a presentation wait for it to end, as notices do. Also the cards' shared look and placement.
/// </summary>
public sealed partial class OfficeDesk
{
    const double StartCardMinutes = 10;

    Border? _meetingCard;
    Rect _meetingCardRect;
    (MeetingCue Cue, Meeting Meeting)? _meetingShown, _meetingLater; // on screen; waiting for a presentation to end

    /// <summary>
    /// A meeting cue (from <see cref="StepMeetings"/>): a card with a Join button when the meeting has a link, otherwise
    /// the notice as before, with the same sound.
    /// </summary>
    void MeetingNotice(MeetingCue cue, Meeting m, string title, string sub, Color color, string? sound = "attention")
    {
        if (m.JoinUrl == null)
        {
            Say(title, sub, color, sound);
            return;
        }
        if (_fullScreen)
        {
            _meetingLater = (cue, m); // shown, with its sound, when the presentation is over
            return;
        }
        ShowMeetingCard(cue, m, sound);
    }

    /// <summary>A minute to go: the card comes too, quietly, when no heads-up card for the meeting is up.</summary>
    void MeetingSoon(Meeting m)
    {
        if (m.JoinUrl == null || _meetingShown?.Meeting == m) return;
        if (_fullScreen) _meetingLater = (MeetingCue.Soon, m);
        else ShowMeetingCard(MeetingCue.Soon, m, null);
    }

    /// <summary>Whether a card for this cue is still worth showing: before the start, or in the first minutes of the meeting.</summary>
    static bool StillDue(MeetingCue cue, Meeting m, DateTime utc) => cue == MeetingCue.Start
        ? utc < m.Start + TimeSpan.FromMinutes(StartCardMinutes) && (m.End <= m.Start || utc < m.End)
        : utc < m.Start;

    /// <summary>Once a second: a card that waited for a presentation, and a card whose moment has passed.</summary>
    void StepMeetingCards(DateTime utc)
    {
        if (_meetingLater is { } later && !_fullScreen)
        {
            var (cue, m) = later;
            _meetingLater = null;
            if (StillDue(cue, m, utc)) ShowMeetingCard(cue, m, cue == MeetingCue.Start ? "done" : "attention");
        }
        if (_meetingShown is { } up && !StillDue(up.Cue, up.Meeting, utc)) HideMeetingCard();
    }

    void ShowMeetingCard(MeetingCue cue, Meeting m, string? sound)
    {
        HideMeetingCard();
        var t = Themes.Current;
        var utc = DateTime.UtcNow;
        Color color = cue == MeetingCue.Start ? Red : Orange;
        string big = cue switch
        {
            MeetingCue.Start => L.T("Starting now"),
            MeetingCue.Soon => L.T("In a minute"),
            _ => L.F("In {0} minutes", Math.Max(1, (int)Math.Round((m.Start - utc).TotalMinutes))),
        };
        string when = cue == MeetingCue.Start && m.End > m.Start ? L.F("until {0}", Clock(m.End)) : L.F("at {0}", Clock(m.Start));
        string service = MeetingLinks.Classify(m.JoinUrl!) is MeetingService s ? MeetingLinks.Name(s) : "";

        var panel = new StackPanel { Spacing = 6 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(CameraIcon(color));
        var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(CardText(Short(Title(m), 40), 12, FontWeight.Bold, color));
        words.Children.Add(CardText(big, 15, FontWeight.SemiBold, Colors.White));
        words.Children.Add(CardText(service.Length > 0 ? L.F("{0} · {1}", when, service) : when, 12, FontWeight.Normal, Color.FromRgb(170, 180, 195)));
        head.Children.Add(words);
        panel.Children.Add(head);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(CardButton(L.T("Join"), t.Accent, () => JoinMeeting(m)));
        buttons.Children.Add(CardButton("×", Color.FromRgb(90, 98, 112), HideMeetingCard));
        panel.Children.Add(buttons);

        var card = CardFrame(panel, color);
        _meetingCard = card;
        _meetingShown = (cue, m);
        _w.OfficeLayer.Children.Add(card);
        _meetingCardRect = PlaceCard(card);
        FadeIn(card);
        if (sound != null) _w.Sound.Play(sound, 0.7);
        UpdatePeek();
        _w.HitShapesChanged();
    }

    void HideMeetingCard()
    {
        _meetingShown = null;
        if (_meetingCard == null) return;
        _w.OfficeLayer.Children.Remove(_meetingCard);
        _meetingCard = null;
        UpdatePeek();
        _w.HitShapesChanged();
    }

    /// <summary>Join: the link opens in the browser (which hands it to the Teams or Zoom app when there is one).</summary>
    public void JoinMeeting(Meeting m)
    {
        HideMeetingCard();
        if (m.JoinUrl is not string url) return;
        UpdateChecker.OpenInBrowser(url);
        _w.Stats.Add("work.joined");
    }

    /// <summary>The meeting a Join entry in the menu opens: the one on now, else the next within 15 minutes, with a link.</summary>
    Meeting? JoinTarget
    {
        get
        {
            var utc = DateTime.UtcNow;
            if (_meetings.Current(utc) is { JoinUrl: not null } now) return now;
            return _meetings.Meetings.FirstOrDefault(m => m.JoinUrl != null && m.Start > utc && m.Start - utc <= TimeSpan.FromMinutes(15));
        }
    }

    /// <summary>At work → Meetings → Join …: the call on now or starting soon.</summary>
    public MenuNode JoinMenuItem() => new()
    {
        Header = () => JoinTarget is Meeting m
            ? L.F("Join {0}", Short(Title(m), 30)) + (MeetingLinks.Classify(m.JoinUrl!) is MeetingService s ? " (" + MeetingLinks.Name(s) + ")" : "")
            : L.T("No call to join now"),
        Click = () =>
        {
            if (JoinTarget is Meeting m) JoinMeeting(m);
        },
        Enabled = () => JoinTarget != null,
    };

    /// <summary>
    /// The signals of the programmer's day (hooked into <see cref="Signal"/>): "join" opens the call on now or next,
    /// "focus-sound:rain" (brown, pink, rain, cafe, off), "knock:text" and "knock-answer:come|ten|lunch".
    /// </summary>
    bool DaySignal(string msg)
    {
        int colon = msg.IndexOf(':');
        string verb = colon < 0 ? msg : msg[..colon], arg = colon < 0 ? "" : msg[(colon + 1)..].Trim();
        switch (verb)
        {
            case "join":
                if (JoinTarget is Meeting m) JoinMeeting(m);
                else Say(L.T("No call to join now"), L.T("a meeting's link shows here from 15 minutes before"), Grey, null);
                return true;
            case "focus-sound":
                return SoundSignal(arg);
            case "knock" or "knock-answer" when colon > 0:
                return KnockSignal(verb, arg);
        }
        return false;
    }

    // ------------------------------------------------------------------ the cards' look, shared with the knocks

    /// <summary>The frame every at-work card has, as the invite card: the theme's ink, a coloured edge, round corners.</summary>
    static Border CardFrame(Control content, Color edge)
    {
        var t = Themes.Current;
        var card = new Border
        {
            Child = content, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10), BorderThickness = new Thickness(1.5),
            Background = Art.Brush(Color.FromArgb(242, t.Ink.R, t.Ink.G, t.Ink.B)), BorderBrush = Art.Brush(edge),
        };
        card.PointerPressed += (_, e) => e.Handled = true; // clicks between the buttons stay on the card
        return card;
    }

    static TextBlock CardText(string text, double size, FontWeight weight, Color color, double maxWidth = 280) => new()
    {
        Text = text, FontFamily = Fx.Font, FontSize = size, FontWeight = weight, Foreground = Art.Brush(color),
        TextWrapping = TextWrapping.Wrap, MaxWidth = maxWidth,
    };

    void FadeIn(Control card)
    {
        card.Opacity = 0;
        _w.Fx.Anims.Add(0.25, k => card.Opacity = k, Ease.OutQuad);
    }

    /// <summary>
    /// Where a card goes: under the scoreboard, like the invite card (in the top corner when the overlay only peeks
    /// out), and below any other card already there.
    /// </summary>
    Rect PlaceCard(Border card)
    {
        card.Measure(Size.Infinity);
        double w = card.DesiredSize.Width, h = card.DesiredSize.Height;
        var a = _w.Arena;
        var b = _w.HudBounds;
        double x = Math.Clamp(b.Right - w, a.Left + 4, Math.Max(a.Left + 4, a.Right - w - 4));
        double y = b.Bottom + 44 + h < a.Bottom ? b.Bottom + 44 : Math.Max(a.Top + 4, b.Top - h - 10);
        if (_w.IsPeeking || !_w.OverlayVisible) (x, y) = (a.Right - w - 24, a.Top + 24);
        var others = new List<Rect>();
        if (_inviteCard != null) others.Add(_inviteCardRect);
        if (_meetingCard != null && _meetingCard != card) others.Add(_meetingCardRect);
        if (_knockCard != null && _knockCard != card) others.Add(_knockCardRect);
        for (int tries = 0; tries < 3; tries++)
        {
            var mine = new Rect(x, y, w, h);
            var hit = others.FirstOrDefault(o => o.Intersects(mine));
            if (hit == default) break;
            y = hit.Bottom + 8;
        }
        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, y);
        return new Rect(x, y, w, h);
    }

    /// <summary>The hit shapes of the meeting and knock cards (hooked into <see cref="CollectHitShapes"/>).</summary>
    void CollectCardShapes(List<HitShape> into)
    {
        if (_meetingCard != null) into.Add(HitShape.Box(_meetingCardRect));
        if (_knockCard != null) into.Add(HitShape.Box(_knockCardRect));
    }

    /// <summary>A meeting or knock card is up (the overlay peeks out for it when hidden).</summary>
    bool CardsUp => _meetingCard != null || _knockCard != null;

    /// <summary>
    /// A presentation started: the cards step down and wait for it to end, like notices (hooked into the full-screen
    /// check).
    /// </summary>
    void HoldCards()
    {
        if (_meetingShown is { } shown)
        {
            HideMeetingCard();
            _meetingLater = shown;
        }
        HoldKnockCard();
    }

    /// <summary>
    /// The overlay was hidden: the cards go with it, except the meeting's own card when it is the meeting that hid it
    /// (Hide the overlay during meetings), which comes back to peek out with its Join button.
    /// </summary>
    void CardsOverlayHidden()
    {
        if (_meetingShown is { } shown)
        {
            HideMeetingCard();
            if (_asideMeeting) Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowMeetingCard(shown.Cue, shown.Meeting, null));
        }
        HoldKnockCard();
    }

    /// <summary>A video camera, drawn in the card's colour.</summary>
    static Control CameraIcon(Color color)
    {
        var c = new Canvas { Width = 34, Height = 34 };
        var ink = Art.Brush(color);
        c.Children.Add(Art.PathOf("M4,11 Q4,8 7,8 L20,8 Q23,8 23,11 L23,23 Q23,26 20,26 L7,26 Q4,26 4,23 Z", ink));
        c.Children.Add(Art.PathOf("M24,15 L31,10 L31,24 L24,19 Z", ink));
        c.Children.Add(Art.Circle(9, 13, 1.8, Art.Brush(Color.FromArgb(200, 255, 255, 255))));
        return c;
    }
}
