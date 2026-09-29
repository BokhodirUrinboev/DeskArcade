using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DeskArcade.Engine;
using DeskArcade.Net;

namespace DeskArcade;

/// <summary>Coffee, lunch and walk invites with co-workers on the local network, and "focusing" for the one you play with.</summary>
public sealed partial class OfficeDesk
{
    const double InviteCardSeconds = 120;

    OfficeInvites _invites = null!;
    readonly List<Invite> _waiting = new();        // arrived during a focus block, shown at the break
    readonly Dictionary<string, Sent> _sent = new(); // ours, by id
    Border? _inviteCard;
    Rect _inviteCardRect;
    Invite? _shownInvite;
    IDisposable? _inviteTimer;

    sealed class Sent
    {
        public required Invite Invite;
        public readonly List<string> Yes = new();
        public DateTime At = DateTime.UtcNow;
    }

    void InitInvites()
    {
        _invites = new OfficeInvites(OfficeBoard.InstanceId, () => LanLink.MyName);
        _invites.InviteReceived += i => Dispatcher.UIThread.Post(() => OnInvite(i));
        _invites.ReplyReceived += r => Dispatcher.UIThread.Post(() => OnReply(r));
        _invites.Cancelled += id => Dispatcher.UIThread.Post(() => OnCancelled(id));
        _w.Lan.FocusReceived += left => Dispatcher.UIThread.Post(() => OnPeerFocus(left));
    }

    public bool InvitesOn => S.OfficeInvites;

    public static string KindName(InviteKind kind) => kind switch
    {
        InviteKind.Lunch => L.T("Lunch"), InviteKind.Walk => L.T("A walk"), _ => L.T("Coffee"),
    };

    /// <summary>"coffee in 5 minutes", "lunch now".</summary>
    static string What(InviteKind kind, int minutes)
    {
        string what = kind switch { InviteKind.Lunch => L.T("lunch"), InviteKind.Walk => L.T("a walk"), _ => L.T("coffee") };
        return minutes <= 0 ? L.F("{0} now", what) : L.F("{0} in {1} minutes", what, minutes);
    }

    /// <summary>Tray → At work → Invites from co-workers: on starts listening (and lets ours go out), off stops both.</summary>
    public void SetInvites(bool on)
    {
        S.OfficeInvites = on;
        _w.SaveSettings();
        if (on) _invites.Start();
        else
        {
            _invites.Stop();
            HideInviteCard();
            _waiting.Clear();
        }
        _w.RefreshTray();
    }

    /// <summary>Invites everyone with invites on. Sending is saying yes to invites, so it turns them on.</summary>
    public void SendInvite(InviteKind kind, int minutes)
    {
        if (!S.OfficeInvites) SetInvites(true);
        if (_invites.Send(kind, minutes) is not Invite invite)
        {
            Say(L.T("Couldn't send the invite"), L.F("UDP port {0} is in use", OfficeInvites.Port), Red, null);
            return;
        }
        foreach (var old in _sent.Where(kv => DateTime.UtcNow - kv.Value.At > TimeSpan.FromHours(3)).Select(kv => kv.Key).ToList()) _sent.Remove(old);
        _sent[invite.Id] = new Sent { Invite = invite };
        Say(L.T("Invite sent"), L.F("{0} · to everyone with invites on", What(kind, minutes)), Gold, "score", 0.5);
        _w.RefreshTray();
    }

    /// <summary>The last invite sent and who said yes, for the tray.</summary>
    public string? LastInviteLine
    {
        get
        {
            var last = _sent.Values.OrderByDescending(s => s.At).FirstOrDefault();
            if (last == null || DateTime.UtcNow - last.At > TimeSpan.FromHours(2)) return null;
            string what = What(last.Invite.Kind, last.Invite.InMinutes);
            return last.Yes.Count == 0 ? L.F("{0} · no answers yet", what) : L.F("{0} · {1} coming", what, string.Join(", ", last.Yes));
        }
    }

    void OnInvite(Invite invite)
    {
        if (!S.OfficeInvites) return;
        if (Quiet)
        {
            _invites.Reply(invite, InviteAnswer.Focusing);
            if (_waiting.Count < 5) _waiting.Add(invite);
            return;
        }
        ShowInvite(invite);
    }

    void ShowHeldInvites()
    {
        var held = _waiting.ToList();
        _waiting.Clear();
        if (held.Count > 0) ShowInvite(held[^1]); // the latest; the older ones have likely gone for coffee already
    }

    void OnReply(InviteReply reply)
    {
        if (!_sent.TryGetValue(reply.InviteId, out var sent)) return;
        string what = What(sent.Invite.Kind, sent.Invite.InMinutes);
        switch (reply.Answer)
        {
            case InviteAnswer.Yes:
                if (!sent.Yes.Contains(reply.From)) sent.Yes.Add(reply.From);
                _w.Stats.Add("work.invites");
                Say(L.F("{0} is in!", reply.From), L.F("{0} · {1} coming", what, sent.Yes.Count), Green, "best", 0.6);
                break;
            case InviteAnswer.No:
                Say(L.F("{0} can't make it", reply.From), what, Color.FromRgb(170, 180, 195), null);
                break;
            case InviteAnswer.Focusing:
                Say(L.F("{0} is focusing", reply.From), L.T("they will see it at their break"), Violet, null);
                break;
        }
        _w.RefreshTray();
    }

    void OnCancelled(string id)
    {
        _waiting.RemoveAll(i => i.Id == id);
        if (_shownInvite?.Id == id) HideInviteCard();
    }

    void Answer(Invite invite, bool yes)
    {
        _invites.Reply(invite, yes ? InviteAnswer.Yes : InviteAnswer.No);
        HideInviteCard();
        if (!yes) return;
        _w.Stats.Add("work.invites");
        Say(L.T("See you there!"), L.F("{0} knows you're coming", invite.From), Green, "score", 0.5);
    }

    /// <summary>The invite as a card under the scoreboard: who asks, what, and two buttons.</summary>
    void ShowInvite(Invite invite)
    {
        if (_fullScreen) return;
        HideInviteCard();
        var t = Themes.Current;
        var panel = new StackPanel { Spacing = 6 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(InviteIcon(invite.Kind, t.Gold));
        var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = invite.From, FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Art.Brush(t.Gold) });
        string ask = What(invite.Kind, invite.InMinutes);
        words.Children.Add(new TextBlock
        {
            Text = char.ToUpperInvariant(ask[0]) + ask[1..] + "?", FontFamily = Fx.Font, FontSize = 15, FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 250,
        });
        head.Children.Add(words);
        panel.Children.Add(head);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(CardButton(L.T("I'm in"), t.Accent, () => Answer(invite, true)));
        buttons.Children.Add(CardButton(L.T("Not now"), Color.FromRgb(90, 98, 112), () => Answer(invite, false)));
        panel.Children.Add(buttons);
        var card = new Border
        {
            Child = panel, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10), BorderThickness = new Thickness(1.5),
            Background = Art.Brush(Color.FromArgb(242, t.Ink.R, t.Ink.G, t.Ink.B)), BorderBrush = Art.Brush(t.Accent),
        };
        card.PointerPressed += (_, e) => e.Handled = true; // clicks between the buttons stay on the card
        _inviteCard = card;
        _shownInvite = invite;
        _w.OfficeLayer.Children.Add(card);
        card.Measure(Size.Infinity);
        double w = card.DesiredSize.Width, h = card.DesiredSize.Height;
        var a = _w.Arena;
        var b = _w.HudBounds;
        double x = Math.Clamp(b.Right - w, a.Left + 4, Math.Max(a.Left + 4, a.Right - w - 4));
        double y = b.Bottom + 44 + h < a.Bottom ? b.Bottom + 44 : Math.Max(a.Top + 4, b.Top - h - 10);
        if (_w.IsPeeking || !_w.OverlayVisible) (x, y) = (a.Right - w - 24, a.Top + 24);
        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, y);
        _inviteCardRect = new Rect(x, y, w, h);
        card.Opacity = 0;
        _w.Fx.Anims.Add(0.25, k => card.Opacity = k, Ease.OutQuad);
        _w.Sound.Play("chat", 0.5);
        _inviteTimer = DispatcherTimer.RunOnce(HideInviteCard, TimeSpan.FromSeconds(InviteCardSeconds));
        UpdatePeek();
        _w.HitShapesChanged();
    }

    void HideInviteCard()
    {
        _inviteTimer?.Dispose();
        _inviteTimer = null;
        _shownInvite = null;
        if (_inviteCard == null) return;
        _w.OfficeLayer.Children.Remove(_inviteCard);
        _inviteCard = null;
        UpdatePeek();
        _w.HitShapesChanged();
    }

    /// <summary>A button drawn for the overlay's cards: a rounded label that takes the press itself.</summary>
    internal static Border CardButton(string text, Color color, Action click)
    {
        var button = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 5), Background = Art.Brush(color),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock { Text = text, FontFamily = Fx.Font, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White },
        };
        button.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return button;
    }

    /// <summary>A cup, a plate with a fork, or a footprint, drawn in the theme's gold.</summary>
    static Control InviteIcon(InviteKind kind, Color color)
    {
        var c = new Canvas { Width = 34, Height = 34 };
        var ink = Art.Brush(color);
        switch (kind)
        {
            case InviteKind.Lunch:
                c.Children.Add(Art.Circle(15, 18, 11, null, ink, 2.2));
                c.Children.Add(Art.Circle(15, 18, 6, null, ink, 1.4));
                c.Children.Add(Art.PathOf("M30,6 L30,30 M27,6 L27,12 Q27,15 30,15 Q33,15 33,12 L33,6", null, ink, 2));
                break;
            case InviteKind.Walk:
                c.Children.Add(Art.PathOf("M9,22 Q6,14 10,9 Q14,6 15,12 Q16,18 13,23 Z", ink));
                c.Children.Add(Art.Circle(11.5, 26.5, 2.6, ink));
                c.Children.Add(Art.PathOf("M22,16 Q19,8 23,4 Q27,2 28,8 Q29,14 26,18 Z", ink));
                c.Children.Add(Art.Circle(24.5, 21.5, 2.6, ink));
                break;
            default:
                c.Children.Add(Art.PathOf("M6,12 L26,12 L24,28 Q23,31 20,31 L12,31 Q9,31 8,28 Z", ink));
                c.Children.Add(Art.PathOf("M25,15 Q32,15 31,21 Q30,25 24,24", null, ink, 2.2));
                c.Children.Add(Art.PathOf("M12,9 Q10,6 12,3 M17,9 Q15,6 17,3 M22,9 Q20,6 22,3", null, ink, 1.6));
                break;
        }
        return c;
    }

    // ------------------------------------------------------------------ focusing, for the co-worker you play with

    void BroadcastFocus()
    {
        if (!_w.Lan.Connected) return;
        _w.Lan.SendFocus(_focus.Focusing ? Math.Max(1, (int)Math.Ceiling(_focus.Left(DateTime.UtcNow).TotalMinutes)) : 0);
    }

    void OnPeerFocus(int minutesLeft)
    {
        if (!_w.Lan.Connected) return;
        if (minutesLeft > 0)
            Say(L.F("{0} is focusing", _w.Lan.PeerName), L.F("for {0} more minutes · your chat waits for their break", minutesLeft), Violet, null);
        else
            Say(L.F("{0} is on a break", _w.Lan.PeerName), L.T("a good moment for a game"), Green, null);
    }
}
