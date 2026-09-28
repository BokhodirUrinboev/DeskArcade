using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Blackjack against the house on a felt table over the desktop (see <see cref="BlackjackRules"/>). Pick a bet with the
/// chips, then Hit, Stand or Double; the cards slide out of the shoe, and after you stand the dealer's hole card turns
/// over and the dealer draws, one card at a time. A round is ten hands from 100 chips, and the chips at the end are the
/// score: a race against the computer or a co-worker over the LAN. The grip (or a right-drag) moves the table.
/// </summary>
public sealed class BlackjackGame : MiniGame
{
    const double TableW = 600, TableH = 380, CardW = 66, CardH = 94, CardGap = 22, DealTime = 0.3, DealerStep = 0.55;
    const double DealerY = 44, PlayerY = 184, BarY = TableH - 58;

    sealed class Button
    {
        public required string Id;
        public Rect Rect;
    }

    readonly Canvas _table = new();
    readonly TranslateTransform _move = new();
    readonly Canvas _cards = new() { IsHitTestVisible = false };
    readonly Canvas _bar = new() { IsHitTestVisible = false };
    readonly TextBlock _dealerValue = Label(15), _playerValue = Label(15), _status = Label(14), _info = Label(13);
    readonly List<Button> _buttons = new();
    readonly DragHandle _handle;
    BlackjackRules _rules;
    Vec2 _origin;
    bool _placed, _racing, _revealing;
    int _pick = 1, _dealerShown, _demoWait; // _pick: which bet chip is chosen; _dealerShown: dealer cards on the table

    public BlackjackGame(IGameHost host) : base(host)
    {
        _rules = new BlackjackRules(Rng);
        _table.RenderTransform = _move;
        Layer.Children.Add(_table);
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_handle.Visual);
        DrawTable();
    }

    public override string Id => "blackjack";
    public override string Title => "Blackjack";

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 11, Height = 15, RadiusX = 2, RadiusY = 2, Fill = Brushes.White, Stroke = Art.Brush("#3B3B45"), StrokeThickness = 1, RenderTransform = new RotateTransform(-12) }, -10, -8));
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 11, Height = 15, RadiusX = 2, RadiusY = 2, Fill = Brushes.White, Stroke = Art.Brush("#3B3B45"), StrokeThickness = 1, RenderTransform = new RotateTransform(10) }, -1, -7));
        s.Rotor.Children.Add(Art.At(new TextBlock { Text = "A", FontSize = 8, FontWeight = FontWeight.Black, Foreground = Art.Brush("#D02834") }, 1, -6));
        return s;
    }

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("blackjack.best");
            string line = _rules.Phase switch
            {
                BlackjackPhase.Playing => L.F("Hand {0}/{1} · Hit, Stand or Double", _rules.Hand, BlackjackRules.Hands),
                BlackjackPhase.RoundOver => L.F("Round over with {0} chips · click New round", _rules.Chips),
                _ => L.F("Hand {0}/{1} · pick a bet and Deal", Math.Min(_rules.Hand + 1, BlackjackRules.Hands), BlackjackRules.Hands),
            };
            return new HudInfo(L.F("{0} chips", _rules.Chips), line, best > 0 ? L.F("Best {0}", best) : L.T("Best —"));
        }
    }

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (_rules.Chips, _racing);
    public override int RaceBaseline => 120;
    public override int RaceBest => (int)Host.Stats.Get("blackjack.best");
    public override double RaceSeconds => 110;

    public override void StartRace()
    {
        if (_racing) return;
        if (_rules.Hand > 0) NewRound();
        BeginRound();
    }

    void BeginRound()
    {
        if (_racing) return;
        _racing = true;
        Host.RoundStarted();
    }

    void NewRound()
    {
        _rules.NewRound();
        _dealerShown = 0;
        _revealing = false;
        Anims.Clear();
        _cards.Children.Clear();
        Refresh();
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? new Vec2(a.Center.X - TableW / 2, a.Center.Y - TableH / 2);
        }
        Place();
        Refresh();
    }

    void Place()
    {
        var a = Host.Arena;
        _origin = new Vec2(Clamp(_origin.X, a.Left + 8, Math.Max(a.Left + 8, a.Right - TableW - 8)),
            Clamp(_origin.Y, a.Top + DragHandle.Height + 12, Math.Max(a.Top + DragHandle.Height + 12, a.Bottom - TableH - 8)));
        _move.X = _origin.X;
        _move.Y = _origin.Y;
        _handle.Show(new Rect(_origin.X, _origin.Y, TableW, TableH));
    }

    public override void PositionsReset() => _placed = false;

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(TableW / 2, TableH / 2);
        Place();
        _handle.Save(_origin);
    }

    public override void Deactivate()
    {
        _handle.Cancel();
        Anims.Finish();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(new Rect(_origin.X, _origin.Y, TableW, TableH)));
        into.Add(_handle.Hit);
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (right || _handle.Contains(p))
        {
            _handle.Begin(p, _origin, anywhere: true);
            return true;
        }
        var local = (p - _origin).ToPoint();
        var button = _buttons.FirstOrDefault(b => b.Rect.Contains(local));
        if (button != null) Press(button.Id);
        return false;
    }

    public override void PointerUp(Vec2 p) => _handle.End(_origin);
    public override void PointerCancel() => _handle.Cancel();

    void Press(string id)
    {
        if (_revealing) return;
        switch (id)
        {
            case "bet0" or "bet1" or "bet2":
                _pick = id[3] - '0';
                Host.Sound.Play("click", 0.35, 1.4);
                break;
            case "deal":
                int bet = Math.Min(BlackjackRules.Bets[_pick], _rules.Chips);
                if (bet < BlackjackRules.Bets[0]) break;
                if (_rules.Hand == 0 && !_racing) BeginRound();
                if (_rules.Deal(bet))
                {
                    _cards.Children.Clear();
                    _dealerShown = 0;
                    Host.Stats.Add("blackjack.hands");
                    Host.Sound.Play("whoosh", 0.3, 1.3);
                    DealCards();
                    if (_rules.Phase != BlackjackPhase.Playing) Reveal(); // a blackjack on the deal
                }
                break;
            case "hit":
                if (_rules.Hit())
                {
                    DealCards();
                    if (_rules.Phase != BlackjackPhase.Playing) Reveal();
                }
                break;
            case "stand":
                if (_rules.Stand()) Reveal();
                break;
            case "double":
                if (_rules.Double())
                {
                    Host.Sound.Play("click", 0.5, 0.9);
                    DealCards();
                    Reveal();
                }
                break;
            case "round":
                NewRound();
                break;
        }
        Refresh();
    }

    // ------------------------------------------------------------------ cards

    /// <summary>Slides the player's new cards (and on a deal the dealer's two) out of the shoe to their places.</summary>
    void DealCards()
    {
        int drawnPlayer = _cards.Children.OfType<Control>().Count(c => c.Tag is "p");
        double delay = 0;
        for (int i = drawnPlayer; i < _rules.Player.Count; i++)
        {
            AddCard(_rules.Player[i], CardSpot(i, _rules.Player.Count, PlayerY), false, "p", delay);
            delay += DealTime * 0.6;
            if (_dealerShown < 2 && i < 2)
            {
                AddCard(_rules.Dealer[_dealerShown], CardSpot(_dealerShown, 2, DealerY), _dealerShown == 1, "d", delay);
                _dealerShown++;
                delay += DealTime * 0.6;
            }
        }
        Relayout();
    }

    /// <summary>After the player is done: the hole card turns over, then the dealer's draws come in one at a time, then the result.</summary>
    void Reveal()
    {
        _revealing = true;
        double t = 0.35;
        var hole = _cards.Children.OfType<Control>().FirstOrDefault(c => c.Tag is "hole");
        if (hole != null)
            Anims.After(t, () =>
            {
                // the face takes the hole card's place in the row
                int at = _cards.Children.IndexOf(hole);
                var where = hole.RenderTransform is TranslateTransform tr ? new Vec2(tr.X, tr.Y) : CardSpot(1, 2, DealerY);
                _cards.Children.Remove(hole);
                AddCard(_rules.Dealer[1], where, false, "d", 0, fly: false);
                var face = _cards.Children[^1];
                _cards.Children.RemoveAt(_cards.Children.Count - 1);
                _cards.Children.Insert(Math.Max(0, at), face);
                Host.Sound.Play("click", 0.35, 1.2);
                Refresh();
            });
        for (int i = 2; i < _rules.Dealer.Count; i++)
        {
            int k = i;
            t += DealerStep;
            Anims.After(t, () =>
            {
                AddCard(_rules.Dealer[k], CardSpot(k, _rules.Dealer.Count, DealerY), false, "d", 0);
                _dealerShown = k + 1;
                Relayout();
                Refresh();
            });
        }
        Anims.After(t + 0.4, () =>
        {
            _revealing = false;
            _dealerShown = _rules.Dealer.Count;
            Result();
            Refresh();
        });
        Host.Wake();
    }

    void Result()
    {
        var at = _origin + new Vec2(TableW / 2, PlayerY - 18);
        var gold = Themes.Themed(Themes.ClassicGold);
        switch (_rules.Outcome)
        {
            case BlackjackOutcome.Blackjack:
                Host.Stats.Add("blackjack.blackjacks");
                Host.Stats.Add("blackjack.wins");
                Host.Fx.Popup(at, L.T("BLACKJACK!"), gold, 36, 1.8, L.F("+{0} chips", _rules.Paid));
                Host.Fx.Burst(at, Themes.Current.Confetti, 30, 420, 600, 6, 0.9);
                Host.Sound.Play("best", 0.7);
                break;
            case BlackjackOutcome.Win:
                Host.Stats.Add("blackjack.wins");
                Host.Fx.Popup(at, Value(_rules.Dealer) > 21 ? L.T("DEALER BUSTS!") : L.T("YOU WIN!"), gold, 32, 1.5, L.F("+{0} chips", _rules.Paid));
                Host.Sound.Play("score", 0.6);
                break;
            case BlackjackOutcome.Push:
                Host.Fx.Popup(at, L.T("PUSH"), Colors.White, 30, 1.4, L.T("your bet comes back"));
                Host.Sound.Play("board", 0.4);
                break;
            case BlackjackOutcome.Bust:
                Host.Fx.Popup(at, L.T("BUST"), Colors.White, 32, 1.5, L.F("−{0} chips", -_rules.Paid));
                Host.Sound.Play("buzzer", 0.35);
                break;
            default:
                Host.Fx.Popup(at, L.T("DEALER WINS"), Colors.White, 30, 1.5, L.F("−{0} chips", -_rules.Paid));
                Host.Sound.Play("buzzer", 0.3);
                break;
        }
        if (_rules.Phase == BlackjackPhase.RoundOver) RoundOver();
    }

    void RoundOver()
    {
        long before = Host.Stats.Get("blackjack.best");
        Host.Stats.Max("blackjack.best", _rules.Chips);
        if (_racing)
        {
            _racing = false;
            Host.RoundEnded(_rules.Chips);
        }
        if (_rules.Chips > before && _rules.Chips > BlackjackRules.StartChips)
        {
            var at = _origin + new Vec2(TableW / 2, -20);
            Host.Fx.Popup(at, L.T("NEW BEST!"), Themes.Themed(Themes.ClassicGold), 40, 2.4, L.F("{0} chips after ten hands", _rules.Chips));
        }
    }

    static int Value(IEnumerable<int> cards) => BlackjackRules.Value(cards).Total;

    Vec2 CardSpot(int i, int count, double y)
    {
        double w = count * CardW + (count - 1) * CardGap * 0.4;
        return new Vec2(TableW / 2 - w / 2 + i * (CardW + CardGap * 0.4), y);
    }

    void AddCard(int card, Vec2 at, bool faceDown, string tag, double delay, bool fly = true)
    {
        var el = faceDown ? DurakGame.CardBack(CardW, CardH) : DurakGame.CardFace(BlackjackRules.Suit(card), BlackjackRules.Rank(card), CardW, CardH);
        el.Tag = faceDown ? "hole" : tag;
        el.RenderTransformOrigin = RelativePoint.TopLeft;
        var move = new TranslateTransform(at.X, at.Y);
        el.RenderTransform = move;
        _cards.Children.Add(el);
        if (!fly) return;
        var shoe = new Vec2(TableW - CardW - 22, 20);
        move.X = shoe.X;
        move.Y = shoe.Y;
        el.Opacity = 0;
        Anims.Add(DealTime, k =>
        {
            el.Opacity = 1;
            move.X = shoe.X + (at.X - shoe.X) * k;
            move.Y = shoe.Y + (at.Y - shoe.Y) * k;
        }, Ease.OutCubic, () => Host.Sound.Play("click", 0.25, 1.5 + Rng.NextDouble() * 0.2), delay);
    }

    /// <summary>Spreads each row again when a card joins it, so the hand stays centred.</summary>
    void Relayout()
    {
        foreach (var (tag, y) in new[] { ("p", PlayerY), ("d", DealerY) })
        {
            var row = _cards.Children.OfType<Control>().Where(c => c.Tag is string t && (t == tag || tag == "d" && t == "hole")).ToList();
            for (int i = 0; i < row.Count; i++)
            {
                var spot = CardSpot(i, row.Count, y);
                if (row[i].RenderTransform is not TranslateTransform move) continue;
                double fx = move.X, fy = move.Y;
                Anims.Add(0.2, k =>
                {
                    move.X = fx + (spot.X - fx) * k;
                    move.Y = fy + (spot.Y - fy) * k;
                }, Ease.OutCubic, null, DealTime);
            }
        }
    }

    public override bool Update(double dt)
    {
        if (_handle.Dragging)
        {
            _origin = _handle.Move(Host.Pointer, new Size(TableW, TableH));
            Place();
        }
        return Anims.Update(dt) || _handle.Dragging || _revealing;
    }

    // ------------------------------------------------------------------ demo

    /// <summary>The demo player bets 10 and follows a simple basic strategy.</summary>
    public override void DemoTick()
    {
        if (_revealing || Anims.Busy || _demoWait-- > 0) return;
        _demoWait = 4;
        switch (_rules.Phase)
        {
            case BlackjackPhase.Betting or BlackjackPhase.Settled:
                _pick = 1;
                Press("deal");
                break;
            case BlackjackPhase.Playing:
                Press(BlackjackRules.Advice(_rules.Player, _rules.Dealer[0], _rules.CanDouble) switch { 'D' => "double", 'S' => "stand", _ => "hit" });
                break;
            case BlackjackPhase.RoundOver:
                if (_demoWait < -20) Press("round");
                break;
        }
    }

    // ------------------------------------------------------------------ drawing

    static TextBlock Label(double size) => new() { FontFamily = Fx.Font, FontSize = size, FontWeight = FontWeight.Bold, IsHitTestVisible = false };

    void DrawTable()
    {
        _table.Children.Clear();
        var felt = Themes.Current.Felt ?? Color.FromRgb(22, 110, 66);
        _table.Children.Add(Art.At(new Rectangle
        {
            Width = TableW, Height = TableH, RadiusX = 26, RadiusY = 26, Stroke = Art.Brush(Art.Blend(felt, Colors.Black, 0.5)), StrokeThickness = 6,
            Fill = new RadialGradientBrush { GradientStops = { new GradientStop(Art.Blend(felt, Colors.White, 0.08), 0), new GradientStop(Art.Blend(felt, Colors.Black, 0.22), 1) } },
        }, 0, 0));
        var line = Art.Brush(Color.FromArgb(90, 255, 255, 255));
        _table.Children.Add(Art.PathOf($"M60,{Art.F(PlayerY - 20)} Q{Art.F(TableW / 2)},{Art.F(PlayerY + 10)} {Art.F(TableW - 60)},{Art.F(PlayerY - 20)}", null, line, 1.5));
        var motto = new TextBlock { Text = L.T("DEALER STANDS ON SOFT 17 · BLACKJACK PAYS 3 TO 2"), FontFamily = Fx.Font, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = line, IsHitTestVisible = false };
        motto.Measure(Size.Infinity);
        _table.Children.Add(Art.At(motto, TableW / 2 - motto.DesiredSize.Width / 2, PlayerY - 40));
        for (int k = 3; k >= 0; k--) // the shoe
            _table.Children.Add(Art.At(DurakGame.CardBack(CardW, CardH), TableW - CardW - 22 + k * 1.5, 20 - k * 1.5));
        _table.Children.Add(_cards);
        _table.Children.Add(_bar);
        foreach (var t in new[] { _dealerValue, _playerValue, _status, _info }) _table.Children.Add(t);
    }

    /// <summary>The totals, the chips and the buttons for this moment of the hand.</summary>
    void Refresh()
    {
        _buttons.Clear();
        _bar.Children.Clear();
        var t = Themes.Current;
        // the dealer's total counts the cards face up on the table, and says so while one is still face down
        bool holeDown = _cards.Children.OfType<Control>().Any(c => c.Tag is "hole");
        int faceUp = _cards.Children.OfType<Control>().Count(c => c.Tag is "d");
        var shownDealer = _rules.Dealer.Take(Math.Min(faceUp, _rules.Dealer.Count)).ToList();
        if (holeDown && _rules.Dealer.Count > 0) shownDealer = _rules.Dealer.Take(1).ToList();
        _dealerValue.Text = _rules.Dealer.Count == 0 ? L.T("Dealer") : L.F("Dealer {0}", Value(shownDealer)) + (holeDown ? " + ?" : "");
        _playerValue.Text = _rules.Player.Count == 0 ? L.T("You") : L.F("You {0}", Value(_rules.Player));
        foreach (var label in new[] { _dealerValue, _playerValue }) label.Foreground = Brushes.White;
        Art.At(_dealerValue, 24, DealerY + CardH / 2 - 10);
        Art.At(_playerValue, 24, PlayerY + CardH / 2 - 10);
        _info.Text = L.F("Chips {0} · hand {1}/{2}", _rules.Chips, _rules.Hand, BlackjackRules.Hands);
        _info.Foreground = Art.Brush(t.Gold);
        Art.At(_info, 22, BarY + 18);

        double x = 250;
        if (_rules.Phase is BlackjackPhase.Betting or BlackjackPhase.Settled && !_revealing)
        {
            for (int k = 0; k < BlackjackRules.Bets.Length; k++)
            {
                var r = new Rect(x, BarY + 6, 44, 44);
                DrawChip(BlackjackRules.Bets[k], r, k == _pick, BlackjackRules.Bets[k] <= _rules.Chips);
                _buttons.Add(new Button { Id = "bet" + k, Rect = r });
                x += 50;
            }
            AddButton("deal", L.T("Deal"), ref x, true);
        }
        else if (_rules.Phase == BlackjackPhase.Playing && !_revealing)
        {
            AddButton("hit", L.T("Hit"), ref x, true);
            AddButton("stand", L.T("Stand"), ref x, true);
            AddButton("double", L.T("Double"), ref x, _rules.CanDouble);
        }
        else if (_rules.Phase == BlackjackPhase.RoundOver && !_revealing) AddButton("round", L.T("New round"), ref x, true);
        Host.HudChanged();
    }

    void AddButton(string id, string text, ref double x, bool enabled)
    {
        var t = Themes.Current;
        var label = new TextBlock { Text = text, FontFamily = Fx.Font, FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Art.Brush(enabled ? t.HudFront : Art.Blend(t.HudFront, t.Ink, 0.6)) };
        label.Measure(Size.Infinity);
        double w = label.DesiredSize.Width + 28;
        var r = new Rect(x, BarY + 11, w, 34);
        _bar.Children.Add(Art.At(new Rectangle { Width = w, Height = 34, RadiusX = 17, RadiusY = 17, Fill = Art.Brush(Color.FromArgb(enabled ? (byte)230 : (byte)120, t.Ink.R, t.Ink.G, t.Ink.B)), Stroke = Art.Brush(t.Accent), StrokeThickness = enabled ? 1.5 : 0.5 }, r.X, r.Y));
        _bar.Children.Add(Art.At(label, r.X + 14, r.Y + 7));
        if (enabled) _buttons.Add(new Button { Id = id, Rect = r });
        x += w + 8;
    }

    void DrawChip(int value, Rect r, bool picked, bool affordable)
    {
        var color = value switch { 5 => Color.FromRgb(210, 50, 60), 10 => Color.FromRgb(40, 110, 210), _ => Color.FromRgb(40, 150, 80) };
        if (!affordable) color = Art.Blend(color, Colors.Gray, 0.7);
        var c = r.Center;
        if (picked) _bar.Children.Add(Art.Circle(c.X, c.Y, 24, null, Art.Brush(Themes.Current.Gold), 3));
        _bar.Children.Add(Art.Circle(c.X, c.Y + 2, 20, Art.Brush(70, 0, 0, 0)));
        _bar.Children.Add(Art.Circle(c.X, c.Y, 20, Art.Brush(color), Brushes.White, 2));
        _bar.Children.Add(Art.Circle(c.X, c.Y, 13, null, Art.Brush(Color.FromArgb(180, 255, 255, 255)), 1.5));
        var text = new TextBlock { Text = value.ToString(CultureInfo.InvariantCulture), FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Black, Foreground = Brushes.White };
        text.Measure(Size.Infinity);
        _bar.Children.Add(Art.At(text, c.X - text.DesiredSize.Width / 2, c.Y - text.DesiredSize.Height / 2));
    }

    public override void ThemeChanged()
    {
        DrawTable();
        foreach (var card in _cards.Children.OfType<Border>())
            if (card.Tag is "hole") DurakGame.PaintBack(card);
        Refresh();
    }
}
