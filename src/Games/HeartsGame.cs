using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DeskArcade.Engine;
using static DeskArcade.Games.HeartsRules;

namespace DeskArcade.Games;

/// <summary>What one player sees of a Hearts table: their own hand, how many cards the others hold, the trick and the scores.</summary>
public sealed class HeartsView : CardRoomView
{
    public List<int> Hand { get; set; } = new();
    public int[] Counts { get; set; } = Array.Empty<int>();
    public int[] Trick { get; set; } = Array.Empty<int>();
    public int[] LastTrick { get; set; } = Array.Empty<int>();
    public int LastWinner { get; set; } = -1;
    public int Leader { get; set; }
    public int Turn { get; set; } = -1;
    public int Phase { get; set; }
    public int Pass { get; set; }
    public bool[] Chosen { get; set; } = Array.Empty<bool>();
    /// <summary>The three cards this player chose to pass, once chosen; the three they were passed, once the cards moved.</summary>
    public int[] MyChosen { get; set; } = Array.Empty<int>();
    public int[] Received { get; set; } = Array.Empty<int>();
    public int[] Scores { get; set; } = Array.Empty<int>();
    public int[] Taken { get; set; } = Array.Empty<int>();
    public int[] HandPoints { get; set; } = Array.Empty<int>();
    public int HandNumber { get; set; }
    public int TrickNumber { get; set; }
    public bool HeartsBroken { get; set; }
    public int Moon { get; set; } = -1;
    public int GameTo { get; set; }
    public List<int> Legal { get; set; } = new();

    public bool Passing => Phase == (int)Stage.Passing;
    public bool HandOver => Phase == (int)Stage.HandOver;
    public bool MyTurn => !Over && Phase == (int)Stage.Playing && Turn == Seat;
    public bool MyPass => !Over && Passing && Seat < Chosen.Length && !Chosen[Seat];
    public int PlaceOf(int seat) => 1 + Scores.Count(s => s < Scores[seat]);

    public static HeartsView Of(HeartsRules r, int seat) => new()
    {
        Hand = r.Hands[seat].OrderBy(SortKey).ToList(), Counts = r.Hands.Select(h => h.Count).ToArray(), Trick = r.Trick.ToArray(),
        LastTrick = r.LastTrick.ToArray(), LastWinner = r.LastWinner, Leader = r.Leader, Turn = r.Turn, Phase = (int)r.Phase, Pass = (int)r.Pass,
        Chosen = r.Chosen.Select(c => c != null).ToArray(), MyChosen = r.Chosen[seat]?.ToArray() ?? Array.Empty<int>(), Received = r.Received[seat].ToArray(),
        Scores = r.Scores.ToArray(), Taken = r.Taken.ToArray(), HandPoints = r.HandPoints.ToArray(), HandNumber = r.HandNumber, TrickNumber = r.TrickNumber,
        HeartsBroken = r.HeartsBroken, Moon = r.Moon, GameTo = r.GameTo, Over = r.Over, Legal = r.LegalCards(seat),
    };

    /// <summary>A hand in the order people hold it: clubs, diamonds, spades, hearts, low to high.</summary>
    public static int SortKey(int card) => new[] { 2, 0, 1, 3 }[Suit(card)] * 13 + Rank(card);
}

/// <summary>
/// Hearts (see <see cref="HeartsRules"/>), the Windows classic for four: pass three cards (left, right, across, then
/// none), follow suit, and duck the hearts and the queen of spades, or take them all and shoot the moon. The game ends
/// when someone reaches 100 and the lowest score wins. Against three computer players at four levels
/// (<see cref="HeartsAi"/>), or with co-workers in a room, computers filling the empty seats
/// (<see cref="CardRoomGame{TView}"/>). You sit at the bottom and play goes to your left: the seat on the left, across,
/// then the right. Click three cards and Pass; then click a card to play it (the ones the rules allow stand out). A
/// finished trick shows for a moment, then slides to whoever took it.
/// </summary>
public sealed class HeartsGame : CardRoomGame<HeartsView>
{
    const double CardW = 60, CardH = 86, SmallScale = 0.5;
    static readonly IBrush GoldBrush = Art.Brush(Gold);

    HeartsRules? _rules;
    readonly HashSet<int> _picked = new();
    (int Game, int Hand) _pickedFor = (-1, -1), _countedHand = (-1, -1);
    int _finishedGame = -1, _trickShownFor = -1;
    string? _share;

    public HeartsGame(IGameHost host) : base(host) { }

    public override string Id => "hearts";
    public override string Title => "Hearts";
    public override int MinPlayers => Players;
    public override int MaxPlayers => Players;
    protected override string Tag => "ht";
    protected override int DemoCpus => 3;
    protected override Size TableSize => new(960, 620);
    protected override string Tagline => L.T("Duck the hearts and the queen of spades · the lowest score wins");
    protected override IEnumerable<(string Text, int Cpus)> SoloChoices => new[] { (L.T("Play against 3 computers"), 3) };

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.PathOf("M0,8 C-12,0 -10,-9 -4,-9 C-1,-9 0,-6 0,-5 C0,-6 1,-9 4,-9 C10,-9 12,0 0,8 Z", Art.Brush("#D8333F"), Brushes.White, 1.2));
        return s;
    }

    public override HudInfo Hud => new(
        View == null ? "—" : View.Scores[View.Seat].ToString(CultureInfo.InvariantCulture),
        Status(),
        L.F("Wins {0}", Host.Stats.Get("hearts.wins")));

    public override string? ShareText => _share;

    // ------------------------------------------------------------------ the rules

    protected override bool HasRules => _rules != null;
    protected override int RulesVersion => _rules?.Version ?? -1;
    protected override void DropRules() => _rules = null;
    protected override void NewRules(int players) => _rules = new HeartsRules(Rng);
    protected override bool RulesAct(int seat, RoomMove move) => _rules != null && _rules.Act(seat, move.Kind, move.A);
    protected override HeartsView ViewOf(int seat) => HeartsView.Of(_rules!, seat);

    protected override TableDue? NextDue()
    {
        if (_rules is not { Over: false } r) return null;
        if (r.Phase == Stage.HandOver) return new TableDue(-1, 3.6);
        if (r.Phase == Stage.Passing)
        {
            for (int s = 0; s < Players; s++)
                if (r.Chosen[s] == null && IsComputer(s)) return new TableDue(s, 0.5);
            return null;
        }
        if (r.Turn < 0 || !IsComputer(r.Turn)) return null;
        // a trick that just ended stays in sight a moment before the next card
        bool justTaken = r.TrickNumber > 0 && r.Trick.All(c => c < 0);
        return new TableDue(r.Turn, justTaken ? 1.5 : 0.7 + r.Version % 3 * 0.12);
    }

    protected override bool AutoStep() => _rules != null && _rules.NextHand();

    protected override Task<RoomMove?> Think(int seat)
    {
        var r = _rules!;
        int level = CpuLevel;
        var move = r.Phase == Stage.Passing
            ? new RoomMove("pass", Pack(HeartsAi.ChoosePass(r, seat, level, Rng)))
            : new RoomMove("play", HeartsAi.ChoosePlay(r, seat, level, Rng));
        return Task.FromResult<RoomMove?>(move);
    }

    protected override RoomMove? GuestDemoMove(HeartsView v)
    {
        if (v.MyPass && v.Hand.Count >= 3) return new RoomMove("pass", Pack(v.Hand.OrderByDescending(Rank).Take(3).ToList()));
        if (v.MyTurn && v.Legal.Count > 0) return new RoomMove("play", v.Legal.OrderBy(Rank).First());
        return null;
    }

    protected override int TurnOf(HeartsView v) => v.Passing ? -1 : v.Turn;
    protected override bool MyMove(HeartsView v) => v.MyTurn || v.MyPass;

    protected override void Moved(int seat, RoomMove move) =>
        Host.Sound.Play(move.Kind == "pass" ? "whoosh" : "board", move.Kind == "pass" ? 0.35 : 0.4, 1.4);

    // ------------------------------------------------------------------ status and results

    static string Direction(int pass) => (PassDirection)pass switch
    {
        PassDirection.Left => L.T("to the left"),
        PassDirection.Right => L.T("to the right"),
        PassDirection.Across => L.T("across"),
        _ => "",
    };

    string Status()
    {
        if (RoomStatus() is { } room) return room;
        if (View is not { } v) return L.T("Pick a table to start a game");
        int me = v.Seat;
        if (v.Over) return v.PlaceOf(me) == 1 ? L.F("You win with {0} points · click New game", v.Scores[me]) : L.F("{0} wins · click New game", Name(Array.IndexOf(v.Scores, v.Scores.Min())));
        if (v.HandOver) return v.Moon >= 0 ? (v.Moon == me ? L.T("You shot the moon!") : L.F("{0} shot the moon!", Name(v.Moon))) : L.F("Hand {0} over · the next one is coming", v.HandNumber);
        if (v.MyPass) return _picked.Count == 3 ? L.F("Pass these three {0}", Direction(v.Pass)) : L.F("Pick three cards to pass {0} ({1}/3)", Direction(v.Pass), _picked.Count);
        if (v.Passing) return L.T("Waiting for the others to pass");
        if (v.MyTurn) return v.Trick.All(c => c < 0) ? (v.TrickNumber == 0 ? L.T("Your lead · the two of clubs") : L.T("Your lead")) : L.T("Your turn · follow suit if you can");
        return v.Turn >= 0 ? L.F("{0}'s turn", Name(v.Turn)) : "";
    }

    protected override void Announce(HeartsView v)
    {
        int me = v.Seat;
        if (v.HandOver && (v.Game, v.HandNumber) != _countedHand)
        {
            _countedHand = (v.Game, v.HandNumber);
            Host.Stats.Add("hearts.hands");
            if (v.Moon == me) Host.Stats.Add("hearts.moon");
            else if (v.HandPoints[me] == 0) Host.Stats.Add("hearts.clean");
            var at = new Vec2(Area.Center.X, Area.Top + Area.Height * 0.32);
            if (v.Moon >= 0)
            {
                Host.Fx.Popup(at, v.Moon == me ? L.T("YOU SHOT THE MOON!") : L.F("{0} SHOT THE MOON", Name(v.Moon).ToUpperInvariant()), v.Moon == me ? Gold : Colors.White, 36, 2.6,
                    v.Moon == me ? L.T("26 points to everyone else") : L.T("26 points to everyone else"));
                if (v.Moon == me) Host.Fx.Burst(at, Themes.Current.Confetti, 40, 520, 700, 7, 1.0);
                Host.Sound.Play(v.Moon == me ? "best" : "buzzer", 0.6);
            }
            else Host.Sound.Play(v.HandPoints[me] == 0 ? "score" : "board", 0.5);
        }
        if (!v.Over || _finishedGame == v.Game) return;
        _finishedGame = v.Game;
        int place = v.PlaceOf(me);
        var mid = new Vec2(Area.Center.X, Area.Top + Area.Height * 0.3);
        Host.Stats.Add("hearts.games");
        if (place == 1)
        {
            Host.Stats.Add("hearts.wins");
            if (!Solo) Host.Stats.Add("lan.wins");
            Host.Fx.Popup(mid, L.T("YOU WIN!"), Gold, 42, 2.8, L.F("{0} points after {1} hands", v.Scores[me], v.HandNumber));
            Host.Fx.Burst(mid, Themes.Current.Confetti, 50, 540, 700, 7, 1.1);
            Host.Sound.Play("best", 0.8);
            Cards.Wave();
            _share = L.F("Hearts · won with {0} points after {1} hands ♥", v.Scores[me], v.HandNumber);
        }
        else
        {
            Host.Fx.Popup(mid, L.F("PLACE {0} OF {1}", place, Players), Colors.White, 36, 2.6, L.T("better luck next deal"));
            Host.Sound.Play("buzzer", 0.35);
            _share = L.F("Hearts · place {0} of 4 with {1} points", place, v.Scores[me]);
        }
        RecordRivals(v, s => -v.Scores[s]);
    }

    // ------------------------------------------------------------------ places on the table (table coordinates)

    Vec2 Middle => new(Table.Width / 2, Table.Height / 2 - 30);

    /// <summary>Where a seat sits, from this player's side: 0 bottom (me), 1 left, 2 top, 3 right.</summary>
    static int Side(HeartsView v, int seat) => (seat - v.Seat + Players) % Players;

    Vec2 PlatePos(HeartsView v, int seat) => Side(v, seat) switch
    {
        0 => new Vec2(Table.Width / 2 - 300, Table.Height - 50),
        1 => new Vec2(96, Middle.Y),
        2 => new Vec2(Table.Width / 2, 50),
        _ => new Vec2(Table.Width - 96, Middle.Y),
    };

    /// <summary>A seat's card in the trick: a little towards its side of the table.</summary>
    Vec2 TrickPos(HeartsView v, int seat) => Middle + Side(v, seat) switch
    {
        0 => new Vec2(0, 52),
        1 => new Vec2(-66, 0),
        2 => new Vec2(0, -52),
        _ => new Vec2(66, 0),
    };

    /// <summary>The others' cards, face down in a small fan beside their plate.</summary>
    Vec2 BackPos(HeartsView v, int seat, int i, int count)
    {
        double spread = (i - (count - 1) / 2.0) * 9;
        return Side(v, seat) switch
        {
            1 => new Vec2(186, Middle.Y + spread),
            2 => new Vec2(Table.Width / 2 + spread, 136),
            _ => new Vec2(Table.Width - 186, Middle.Y + spread),
        };
    }

    Vec2 HandPos(int i, int count)
    {
        double step = Math.Min(CardW * 0.62, (Table.Width - 380) / Math.Max(1, count));
        return new Vec2(Table.Width / 2 + 70 + (i - (count - 1) / 2.0) * step, Table.Height - CardH / 2 - 24);
    }

    // ------------------------------------------------------------------ drawing

    Border Face(int card) => DurakGame.CardFace(Suit(card), Rank(card), CardW, CardH);

    static int BackKey(int seat, int i) => -1 - (seat * 13 + i);

    protected override void DrawTable(HeartsView v, HeartsView? prev, bool newDeal)
    {
        if ((v.Game, v.HandNumber) != _pickedFor)
        {
            _pickedFor = (v.Game, v.HandNumber);
            _picked.Clear();
        }
        if (!v.MyPass) _picked.Clear();
        bool newHand = newDeal || prev == null || prev.HandNumber != v.HandNumber;
        if (newHand && !newDeal)
        {
            Cards.Clear();
            Cards.Begin();
        }
        for (int s = 0; s < Players; s++) DrawPlate(v, s);
        DrawOthers(v, newHand);
        DrawTrick(v);
        DrawHand(v, newHand);
        DrawInfo(v);
    }

    void DrawPlate(HeartsView v, int s)
    {
        var p = PlatePos(v, s);
        bool turn = v.Turn == s && !v.HandOver && !v.Over;
        double w = 150, h = 50;
        var ink = Themes.Current.Ink;
        Place(new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(12), IsHitTestVisible = false,
            Background = Art.Brush(Color.FromArgb(210, ink.R, ink.G, ink.B)),
            BorderBrush = turn ? GoldBrush : Art.Brush(Color.FromArgb(90, 255, 255, 255)), BorderThickness = new Thickness(turn ? 2.5 : 1),
        }, p.X - w / 2, p.Y - h / 2);
        string name = s == v.Seat ? L.T("You") : v.Names[s] + (v.Cpu[s] && v.Human[s] && !Solo ? " · " + L.T("CPU") : "");
        Label(name.Length > 18 ? name[..17] + "…" : name, p.X, p.Y - h / 2 + 5, 14, Colors.White, center: true);
        string score = v.HandOver && v.HandPoints[s] > 0 ? L.F("{0} points (+{1})", v.Scores[s], v.HandPoints[s])
            : v.Taken[s] > 0 ? L.F("{0} points · {1} this hand", v.Scores[s], v.Taken[s]) : L.F("{0} points", v.Scores[s]);
        Label(score, p.X, p.Y - h / 2 + 26, 12.5, Gold, center: true);
        if (v.Passing && s != v.Seat && v.Chosen[s]) Label("✓", p.X + w / 2 - 14, p.Y - h / 2 + 4, 14, Color.FromRgb(61, 220, 132));
        if (turn && s != v.Seat) Label(L.T("thinking…"), p.X, p.Y - h / 2 - 18, 12, Gold, center: true, into: Top);
    }

    void DrawOthers(HeartsView v, bool newHand)
    {
        for (int s = 0; s < Players; s++)
        {
            if (s == v.Seat) continue;
            int count = v.Counts[s];
            for (int i = 0; i < count; i++)
            {
                int key = BackKey(s, i);
                var at = BackPos(v, s, i, count);
                double angle = Side(v, s) == 2 ? 0 : 90;
                if (Cards.Has(key)) Cards.Place(key, () => DurakGame.CardBack(CardW, CardH), at, angle, SmallScale);
                else Cards.Place(key, () => DurakGame.CardBack(CardW, CardH), at, angle, SmallScale, from: Middle, fromScale: 0.2,
                    seconds: CardTable.DealSeconds, ease: Ease.OutCubic, delay: newHand ? Stagger(CardTable.DealDelay(i, Side(v, s), 13)) : 0);
            }
        }
    }

    /// <summary>The cards on the trick; once it is taken, the last trick stays in view until the next card, then goes to its taker.</summary>
    void DrawTrick(HeartsView v)
    {
        bool empty = v.Trick.All(c => c < 0);
        var shown = empty && v.LastWinner >= 0 && v.TrickNumber > 0 && !v.HandOver && _trickShownFor != TrickId(v) ? v.LastTrick : v.Trick;
        for (int s = 0; s < Players; s++)
        {
            int card = shown[s];
            if (card < 0) continue;
            var at = TrickPos(v, s);
            double angle = (Side(v, s) - 1.5) * 4;
            if (Cards.Has(card)) Cards.Place(card, () => Face(card), at, angle);
            else Cards.Place(card, () => Face(card), at, angle, from: s == v.Seat ? null : BackPos(v, s, 0, 1), fromScale: s == v.Seat ? 1 : SmallScale,
                seconds: 0.3, ease: Ease.OutCubic);
        }
    }

    static int TrickId(HeartsView v) => v.Game * 1000 + v.HandNumber * 20 + v.TrickNumber;

    void DrawHand(HeartsView v, bool newHand)
    {
        int count = v.Hand.Count;
        for (int i = 0; i < count; i++)
        {
            int card = v.Hand[i];
            var at = HandPos(i, count);
            bool picked = _picked.Contains(card) || v.Passing && !v.MyPass && v.MyChosen.Contains(card);
            bool received = v.Received.Contains(card) && v.TrickNumber == 0 && !v.Passing;
            bool playable = v.MyTurn && v.Legal.Contains(card) && !Sending;
            bool dim = v.MyTurn && !v.Legal.Contains(card);
            var c = Cards.Has(card) ? Cards.Place(card, () => Face(card), at)
                : Cards.Place(card, () => Face(card), at, from: Middle, fromScale: SmallScale, seconds: CardTable.DealSeconds, ease: Ease.OutCubic,
                    delay: newHand ? Stagger(CardTable.DealDelay(i, 0, 13)) : 0);
            c.Lift = picked ? 22 : playable ? 8 : received ? 10 : 0;
            c.Apply();
            if (c.Visual is Border face)
            {
                face.Opacity = dim ? 0.55 : 1;
                face.BorderBrush = received ? GoldBrush : Art.Brush("#3B3B45");
                face.BorderThickness = new Thickness(received ? 2.5 : 1.2);
            }
            double step = Math.Min(CardW * 0.62, (Table.Width - 380) / Math.Max(1, count));
            var box = new Rect(at.X - CardW / 2, at.Y - CardH / 2 - 22, i == count - 1 ? CardW : step, CardH + 22);
            if (v.MyPass) Clickable(box, () => Pick(card));
            else if (playable) Clickable(box, () => Act(new RoomMove("play", card)));
        }
    }

    void Pick(int card)
    {
        if (!_picked.Remove(card))
        {
            if (_picked.Count >= 3) return;
            _picked.Add(card);
        }
        Host.Sound.Play("click", 0.3, 1.5);
        Draw();
        Host.HudChanged();
    }

    void DrawInfo(HeartsView v)
    {
        var t = Table;
        Label(Status(), t.Width / 2, t.Height - CardH - 70, 15, Gold, center: true, into: Top);
        string info = L.F("Hand {0}", v.HandNumber) + " · " + L.F("game to {0}", v.GameTo) + (v.HeartsBroken ? " · " + L.T("hearts broken") : "");
        Label(info, 26, t.Bottom - 26, 12, Soft);
        if (v.MyPass && _picked.Count == 3)
            Button(L.F("Pass {0}", Direction(v.Pass)), Middle.X, Middle.Y - 20, 220, () =>
            {
                Act(new RoomMove("pass", Pack(_picked.ToList())));
                _picked.Clear();
            }, hot: true, height: 42);
        if (v.Over) DrawEndButtons(Middle.Y - 20);
    }

    /// <summary>A card no longer placed: the cards of a taken trick slide to whoever took it, once they have been seen.</summary>
    protected override void Leave(TableCards.Card card, HeartsView v, HeartsView? prev)
    {
        if (card.Key is >= 0 and < 52 && prev != null && v.LastWinner >= 0 && v.LastTrick.Contains(card.Key))
        {
            _trickShownFor = TrickId(v);
            Cards.Remove(card.Key, PlatePos(v, v.LastWinner), 0.35, 0.45, Ease.InCubic, delay: Stagger(0.15));
            return;
        }
        base.Leave(card, v, prev);
    }
}
