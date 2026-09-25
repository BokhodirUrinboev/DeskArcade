using System;
using System.Diagnostics;
using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using DeskArcade.Games;
using DeskArcade.Net;

namespace DeskArcade;

/// <summary>
/// Pet mail between the overlay and the LAN: the tray and ☰ menus post a gift to the co-worker's pet, parcels that
/// arrive wait (in <see cref="PetMail"/>) until the Desktop Pet is on screen and then drop in on a parachute, and the
/// sender hears when their parcel was opened. The resend timer runs only while a message waits for its receipt.
/// </summary>
public sealed class PetMailer
{
    static readonly Color SentColor = Color.FromRgb(170, 180, 195), GiftColor = Color.FromRgb(255, 209, 102), LovedColor = Color.FromRgb(255, 120, 160);

    readonly OverlayWindow _w;
    readonly PetMail _mail;
    readonly PetGame? _pet;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(PetMail.ResendSeconds / 2) };
    readonly Stopwatch _clock = Stopwatch.StartNew();
    int _session = -1;

    public PetMailer(OverlayWindow w)
    {
        _w = w;
        _mail = new PetMail(w.Lan.Send);
        _pet = w.Games.OfType<PetGame>().FirstOrDefault();
        _timer.Tick += (_, _) =>
        {
            _mail.Tick(Now);
            if (!_mail.Busy) _timer.Stop();
        };
        w.Lan.MailReceived += msg => Dispatcher.UIThread.Post(() => OnMail(msg));
        w.Lan.StateChanged += () => Dispatcher.UIThread.Post(OnLanState);
        if (_pet != null)
        {
            _pet.ParcelOpened += OnOpened;
            _pet.ReadyForParcel += Deliver;
        }
    }

    double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>The gifts, in menu order.</summary>
    public static PetGift[] Gifts => Enum.GetValues<PetGift>();

    public static string GiftName(PetGift gift) => gift switch
    {
        PetGift.Ball => L.T("A ball"),
        PetGift.Yarn => L.T("A ball of yarn"),
        PetGift.Bone => L.T("A chew bone"),
        _ => L.T("A treat"),
    };

    /// <summary>The menu entry: "Send Anna's pet…" while linked, greyed out otherwise.</summary>
    public string MenuHeader => _w.Lan.Connected && _w.Lan.PeerName.Length > 0 ? L.F("Send {0}'s pet…", _w.Lan.PeerName) : L.T("Send a co-worker's pet…");

    public bool CanSend => _w.Lan.Connected;

    public void Send(PetGift gift)
    {
        if (!_w.Lan.Connected) return;
        string peer = _w.Lan.PeerName;
        switch (_mail.Send(gift, Now, out double wait))
        {
            case PetMailSend.Sent:
                _w.Stats.Add("pet.giftssent");
                _w.Sound.Play("pop", 0.35, 1.3);
                _w.Notice(L.T("Sent!"), L.F("{0} for {1}'s pet", GiftName(gift), peer), SentColor);
                _timer.Start();
                break;
            case PetMailSend.TooSoon:
                _w.Notice(L.T("Wait a moment"), L.F("The next parcel can go in {0} s", (int)Math.Ceiling(wait)), SentColor);
                break;
            default:
                _w.Notice(L.T("Wait a moment"), L.T("The parcels on the way have to arrive first"), SentColor);
                break;
        }
    }

    void OnMail(string message)
    {
        if (!_w.Lan.Connected) return;
        var e = _mail.Handle(message, _w.Lan.PeerName, Now);
        if (_mail.Busy) _timer.Start();
        switch (e.Kind)
        {
            case PetMailEventKind.Parcel:
                Deliver();
                if (_mail.Waiting > 0 && _w.Current is not PetGame)
                {
                    _w.Sound.Play("pop", 0.3, 1.1);
                    _w.Notice(L.F("A parcel from {0}", _w.Lan.PeerName), L.T("It waits for your Desktop Pet"), GiftColor);
                }
                break;
            case PetMailEventKind.Loved:
                _w.Sound.Play("best", 0.3, 1.6);
                _w.Notice(L.F("{0}'s pet loved it", _w.Lan.PeerName), GiftName(e.Gift), LovedColor);
                break;
        }
    }

    void OnLanState()
    {
        if (!_w.Lan.Connected || _w.Lan.Session == _session) return;
        _session = _w.Lan.Session; // a new session, perhaps with someone else: start the ids afresh
        _mail.ResetLink();
    }

    /// <summary>Hands the waiting parcels to the pet, one at a time, while it is on screen and free to take one.</summary>
    void Deliver()
    {
        if (_pet == null) return;
        while (_mail.Waiting > 0 && _w.Current == _pet && _pet.CanTakeParcel && _mail.TryTake(out var parcel))
            _pet.DropParcel(parcel);
    }

    void OnOpened(int id)
    {
        _mail.Opened(id, Now);
        if (_mail.Busy) _timer.Start();
    }
}
