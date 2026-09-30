using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DeskArcade.Engine;
using DeskArcade.Net;
using DeskArcade.Office;

namespace DeskArcade;

/// <summary>
/// Knock first: At work → Knock on… lists the co-workers heard lately and what they are up to; a knock carries a line
/// of text, typed in a small window. Someone focusing or in a meeting gets it at the break, on a card with Come over,
/// In 10 minutes and After lunch; someone free gets the card at once. The knocker hears the answer as a notice.
/// It goes over the invites channel, so it works only while invites are on, on both sides.
/// </summary>
public sealed partial class OfficeDesk
{
    const int KnockSlots = 8, KnocksOnCard = 4;

    static readonly Color Grey = Color.FromRgb(170, 180, 195);

    Knocking _knocking = null!;
    Border? _knockCard;
    Rect _knockCardRect;
    readonly List<HeldKnock> _knocksShown = new(); // on the card
    readonly List<HeldKnock> _knocksLater = new(); // put away for a presentation, or with the overlay
    bool _knocksAwayByHand;                         // the overlay was hidden: they wait until it is shown again

    void InitKnocks()
    {
        _knocking = new Knocking(_invites, PresenceNow);
        _invites.PresenceReceived += p => Dispatcher.UIThread.Post(() => _knocking.Receive(p, DateTime.UtcNow));
        _invites.KnockReceived += k => Dispatcher.UIThread.Post(() => _knocking.Receive(k, DateTime.UtcNow));
        _invites.KnockReplyReceived += r => Dispatcher.UIThread.Post(() => _knocking.Receive(r, DateTime.UtcNow));
        _knocking.Show += OnKnocksDue;
        _knocking.Replied += OnKnockReplied;
        _knocking.Unanswered += sent =>
            Say(L.F("No answer from {0}", sent.ToName), L.T("their Desk Arcade may be closed, or its invites off"), Grey, null);
        _knocking.CoworkersChanged += () => _w.RefreshTray();
    }

    /// <summary>What we tell co-workers: focusing or in a meeting and for how long, or free until the next meeting.</summary>
    (PresenceState, int) PresenceNow()
    {
        var utc = DateTime.UtcNow;
        if (_focus.Phase == FocusPhase.Focus) return (PresenceState.Focusing, (int)Math.Ceiling(_focus.Left(utc).TotalMinutes));
        if (_meetings.Current(utc) is Meeting now) return (PresenceState.Meeting, now.End > now.Start ? (int)Math.Ceiling((now.End - utc).TotalMinutes) : 0);
        if (_meetings.Next(utc) is Meeting next && next.Start - utc < TimeSpan.FromHours(12)) return (PresenceState.Free, (int)(next.Start - utc).TotalMinutes);
        return (PresenceState.Free, 0);
    }

    /// <summary>Once a second: presence, knocks let out at the break, and knocks put away that can come back.</summary>
    void StepKnocks(DateTime utc)
    {
        _knocking.Step(utc);
        if (_knocksLater.Count == 0 || _fullScreen) return;
        if (_knocksAwayByHand && (!_w.OverlayVisible || _w.IsPeeking)) return;
        var back = _knocksLater.ToList();
        _knocksLater.Clear();
        _knocksAwayByHand = false;
        ShowKnocks(back, "chat");
    }

    void OnKnocksDue(IReadOnlyList<HeldKnock> knocks)
    {
        if (_fullScreen)
        {
            _knocksLater.AddRange(knocks);
            return;
        }
        ShowKnocks(knocks, "chat");
    }

    static string StateLine(Coworker c, DateTime utc)
    {
        int left = c.MinutesLeftAt(utc);
        return c.State switch
        {
            PresenceState.Focusing => left > 0 ? L.F("in a focus block, {0} min left", left) : L.T("in a focus block"),
            PresenceState.Meeting => left > 0 ? L.F("in a meeting, {0} min left", left) : L.T("in a meeting"),
            _ => left > 0 ? L.F("available, next meeting in {0} min", left) : L.T("available"),
        };
    }

    // ------------------------------------------------------------------ knocking

    /// <summary>The co-workers heard lately, for the menu and the knock window.</summary>
    public IReadOnlyList<Coworker> CoworkersSeen => _knocking.Coworkers.Seen(DateTime.UtcNow);

    public string CoworkerLine(Coworker c) => L.F("{0} · {1}", c.Name, StateLine(c, DateTime.UtcNow));

    /// <summary>Opens the small window to type a knock for a co-worker.</summary>
    public void OpenKnock(Coworker c) => KnockWindow.ShowFor(_w, this, c);

    /// <summary>Knocks on a co-worker's door with a line of text; false when it could not go (invites off, they left).</summary>
    public bool KnockOn(string coworkerId, string text)
    {
        if (_knocking.Coworkers.Find(coworkerId) is not Coworker c)
        {
            Say(L.T("They have gone"), L.T("nobody by that name is on the network now"), Grey, null);
            return false;
        }
        if (_knocking.KnockOn(c, text.Trim(), DateTime.UtcNow) is null)
        {
            Say(L.T("Couldn't knock"), L.F("UDP port {0} is in use", OfficeInvites.Port), Red, null);
            return false;
        }
        Say(L.F("You knocked on {0}", c.Name), "“" + Short(text.Trim(), 40) + "”", Gold, "pop", 0.5);
        return true;
    }

    void OnKnockReplied(Knocking.Sent sent, KnockReply reply)
    {
        string about = L.F("about “{0}”", Short(sent.Knock.Text, 32));
        switch (reply.Answer)
        {
            case KnockAnswer.Seen:
                Say(L.F("{0} has your knock", sent.ToName), L.T("their answer comes here"), Blue, null);
                break;
            case KnockAnswer.Queued:
                bool meeting = _knocking.Coworkers.Find(reply.FromId)?.State == PresenceState.Meeting;
                Say(meeting ? L.F("{0} is in a meeting", sent.ToName) : L.F("{0} is focusing", sent.ToName),
                    reply.Minutes > 0 ? L.F("they will see your knock at their break, in about {0} min", reply.Minutes) : L.T("they will see your knock at their break"),
                    Violet, null);
                break;
            case KnockAnswer.ComeOver:
                Say(L.F("{0}: come over!", sent.ToName), about, Green, "best", 0.6);
                break;
            case KnockAnswer.InTen:
                Say(L.F("{0}: in 10 minutes", sent.ToName), about, Gold, "score", 0.5);
                break;
            case KnockAnswer.AfterLunch:
                Say(L.F("{0}: after lunch", sent.ToName), about, Blue, "score", 0.4);
                break;
        }
    }

    // ------------------------------------------------------------------ the card of knocks on our door

    void ShowKnocks(IEnumerable<HeldKnock> knocks, string? sound)
    {
        foreach (var k in knocks)
            if (!_knocksShown.Any(s => s.Knock.Id == k.Knock.Id && s.Knock.FromId == k.Knock.FromId)) _knocksShown.Add(k);
        if (_knocksShown.Count == 0) return;
        BuildKnockCard();
        if (sound != null) _w.Sound.Play(sound, 0.5);
    }

    /// <summary>The card: who knocked and when, their line, and the three answers for each (the latest few).</summary>
    void BuildKnockCard()
    {
        RemoveKnockCard();
        var t = Themes.Current;
        var panel = new StackPanel { Spacing = 6 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(DoorIcon(t.Gold));
        string title = _knocksShown.Count == 1 ? L.F("{0} is knocking", _knocksShown[0].Knock.From) : L.F("{0} knocks on your door", _knocksShown.Count);
        var heading = CardText(title, 15, FontWeight.SemiBold, Colors.White, 260);
        heading.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(heading);
        panel.Children.Add(head);
        foreach (var held in _knocksShown.TakeLast(KnocksOnCard))
        {
            var row = new StackPanel { Spacing = 3, Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(CardText(L.F("{0} · {1}", held.Knock.From, Clock(held.AtUtc)), 12, FontWeight.Bold, t.Gold));
            row.Children.Add(CardText("“" + held.Knock.Text + "”", 14, FontWeight.Normal, Colors.White, 320));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            buttons.Children.Add(CardButton(L.T("Come over"), t.Accent, () => AnswerKnock(held, KnockAnswer.ComeOver)));
            buttons.Children.Add(CardButton(L.T("In 10 minutes"), Color.FromRgb(70, 110, 160), () => AnswerKnock(held, KnockAnswer.InTen)));
            buttons.Children.Add(CardButton(L.T("After lunch"), Color.FromRgb(90, 98, 112), () => AnswerKnock(held, KnockAnswer.AfterLunch)));
            row.Children.Add(buttons);
            panel.Children.Add(row);
        }
        if (_knocksShown.Count > KnocksOnCard)
            panel.Children.Add(CardText(L.F("and {0} more after these", _knocksShown.Count - KnocksOnCard), 12, FontWeight.Normal, Grey));
        var close = CardButton("×", Color.FromRgb(90, 98, 112), CloseKnockCard);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Children.Add(close);

        var card = CardFrame(panel, t.Gold);
        _knockCard = card;
        _w.OfficeLayer.Children.Add(card);
        _knockCardRect = PlaceCard(card);
        FadeIn(card);
        UpdatePeek();
        _w.HitShapesChanged();
    }

    void AnswerKnock(HeldKnock held, KnockAnswer answer)
    {
        _knocking.Answer(held.Knock, answer);
        _knocksShown.Remove(held);
        _w.Stats.Add("work.knocks");
        string said = answer switch { KnockAnswer.ComeOver => L.T("come over"), KnockAnswer.InTen => L.T("in 10 minutes"), _ => L.T("after lunch") };
        Say(L.F("{0} knows", held.Knock.From), said, Green, "score", 0.4);
        if (_knocksShown.Count > 0) BuildKnockCard();
        else CloseKnockCard();
    }

    /// <summary>Close: the knocks on the card are seen and go, unanswered.</summary>
    void CloseKnockCard()
    {
        _knocksShown.Clear();
        RemoveKnockCard();
    }

    void RemoveKnockCard()
    {
        if (_knockCard == null) return;
        _w.OfficeLayer.Children.Remove(_knockCard);
        _knockCard = null;
        UpdatePeek();
        _w.HitShapesChanged();
    }

    /// <summary>The card steps down (a presentation, the overlay hidden) and its knocks wait to come back.</summary>
    void HoldKnockCard(bool byHand = false)
    {
        if (_knockCard == null) return;
        _knocksLater.AddRange(_knocksShown);
        _knocksShown.Clear();
        _knocksAwayByHand |= byHand;
        RemoveKnockCard();
    }

    /// <summary>"knock:text" knocks on the co-worker heard last; "knock-answer:come|ten|lunch" answers the first knock on the card.</summary>
    bool KnockSignal(string verb, string arg)
    {
        switch (verb)
        {
            case "knock" when CoworkersSeen.OrderByDescending(c => c.SeenUtc).FirstOrDefault() is Coworker c:
                KnockOn(c.Id, arg);
                return true;
            case "knock":
                Say(L.T("Nobody to knock on"), L.T("co-workers show up here when their invites are on"), Grey, null);
                return true;
            case "knock-answer" when _knocksShown.Count > 0:
                AnswerKnock(_knocksShown[0], arg switch { "ten" => KnockAnswer.InTen, "lunch" => KnockAnswer.AfterLunch, _ => KnockAnswer.ComeOver });
                return true;
        }
        return false;
    }

    /// <summary>At work → Knock on…: the co-workers heard lately and what they are up to.</summary>
    public MenuNode KnockMenu()
    {
        var items = new List<MenuNode>
        {
            new()
            {
                Header = () => !S.OfficeInvites ? L.T("Turn on invites from co-workers to knock")
                    : CoworkersSeen.Count == 0 ? L.T("Nobody heard yet: their invites need to be on too") : "",
                Click = () =>
                {
                    if (!S.OfficeInvites) SetInvites(true);
                },
                Enabled = () => !S.OfficeInvites,
            },
        };
        for (int i = 0; i < KnockSlots; i++)
        {
            int slot = i;
            items.Add(new()
            {
                Header = () => S.OfficeInvites && CoworkersSeen.ElementAtOrDefault(slot) is Coworker c ? CoworkerLine(c) : "",
                Click = () =>
                {
                    if (CoworkersSeen.ElementAtOrDefault(slot) is Coworker c) OpenKnock(c);
                },
                Enabled = () => S.OfficeInvites && slot < CoworkersSeen.Count,
            });
        }
        return new MenuNode { Header = () => L.T("Knock on…"), Children = items };
    }

    /// <summary>A door with a knuckle's rings beside it, in the theme's gold.</summary>
    static Control DoorIcon(Color color)
    {
        var c = new Canvas { Width = 34, Height = 34 };
        var ink = Art.Brush(color);
        c.Children.Add(Art.PathOf("M6,31 L6,5 Q6,3 8,3 L20,3 Q22,3 22,5 L22,31", null, ink, 2.2));
        c.Children.Add(Art.PathOf("M3,31 L25,31", null, ink, 2.2));
        c.Children.Add(Art.Circle(18, 18, 1.8, ink));
        c.Children.Add(Art.PathOf("M26,12 Q28,15 26,18 M29,9 Q33,15 29,21", null, ink, 1.6));
        return c;
    }
}
