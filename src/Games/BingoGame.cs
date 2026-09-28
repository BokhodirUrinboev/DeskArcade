using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Bingo of Work: a paper 3×3 card on the desktop (see <see cref="BingoCard"/>) whose squares are everyday desk events
/// (be playing when Claude finishes, beat the computer in a race, give the pet a treat, play before 10 AM…) and small
/// goals in the other games. The card watches the stats all the time, whichever game is on, and dabs a square the
/// moment it happens, with a notice when the card isn't showing. Three in a line is a bingo; all nine is a full house,
/// and a new card comes. Click a game square to go and play it; right-click an open square to swap it (twice a card).
/// The grip moves the card.
/// </summary>
public sealed class BingoGame : MiniGame
{
    const double SqW = 134, SqH = 82, Gap = 6, HeaderH = 44, Pad = 10;
    const double BoardW = Pad * 2 + BingoCard.Side * SqW + (BingoCard.Side - 1) * Gap;
    const double BoardH = HeaderH + Pad + BingoCard.Side * SqH + (BingoCard.Side - 1) * Gap;
    static readonly Color Paper = Color.FromRgb(255, 248, 230), InkText = Color.FromRgb(32, 35, 44);
    static readonly Color DeskFill = Color.FromRgb(228, 239, 255), GameFill = Color.FromRgb(255, 239, 200), FreeFill = Color.FromRgb(255, 226, 226);

    sealed class SquareView
    {
        public required Canvas Root;
        public required Rectangle Tile;
        public required TextBlock Text;
        public required TextBlock Progress;
        public required TextBlock Play;
        public required Ellipse Stamp;
        public required ScaleTransform StampScale;
        public required ScaleTransform Flip;
    }

    readonly Canvas _board = new();
    readonly ScaleTransform _zoom = new(1, 1);
    readonly Rectangle _back = new() { Width = BoardW, Height = BoardH, RadiusX = 12, RadiusY = 12, StrokeThickness = 1.5, IsHitTestVisible = false };
    readonly Rectangle _band = new() { Width = BoardW, Height = HeaderH - 6, RadiusX = 12, RadiusY = 12, IsHitTestVisible = false };
    readonly TextBlock _title = new() { FontFamily = Fx.Font, FontSize = 24, FontWeight = FontWeight.Black, IsHitTestVisible = false };
    readonly TextBlock _subtitle = new() { FontFamily = Fx.Font, FontSize = 13, FontWeight = FontWeight.Bold, IsHitTestVisible = false };
    readonly Rectangle _newBack = new() { RadiusX = 10, RadiusY = 10, Height = 22, StrokeThickness = 1, IsHitTestVisible = false };
    readonly TextBlock _newChip = new() { FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Bold, IsHitTestVisible = false };
    readonly Canvas _squares = new() { IsHitTestVisible = false };
    readonly Canvas _lines = new() { IsHitTestVisible = false };
    readonly DragHandle _handle;
    readonly SquareView[] _views = new SquareView[BingoCard.Count];
    readonly HashSet<int> _fresh = new(); // dabbed while the card was not showing: stamped when it shows again
    readonly HashSet<int> _pendingLines = new(); // bingos not called on the card yet: drawn when their popup comes
    BingoCard _card;
    bool _placed, _active, _sure, _dealing, _checking;
    double _sureFor, _demoWait;
    Vec2 _origin;
    double _scale = 1;
    Rect _newRect;

    public BingoGame(IGameHost host) : base(host)
    {
        _card = BingoCard.Load(host.Settings.Bingo) ?? BingoCard.Deal(Rng, Value, Has);
        _board.RenderTransformOrigin = RelativePoint.TopLeft;
        _board.RenderTransform = _zoom;
        foreach (var c in new Control[] { _back, _band, _title, _subtitle, _newBack, _newChip, _squares, _lines }) _board.Children.Add(c);
        for (int i = 0; i < BingoCard.Count; i++) _views[i] = BuildSquare(i);
        Layer.Children.Add(_board);
        _handle = new DragHandle(host, Id, Title);
        Layer.Children.Add(_handle.Visual);
        ThemeChanged();
        // the card listens whichever game is on: a counter that moves is play, and may dab a square
        host.Stats.CounterChanged += counter =>
        {
            if (!counter.StartsWith("bingo.", StringComparison.Ordinal)) Dispatcher.UIThread.Post(() => Check(true));
        };
        host.Stats.Unlocked += _ => Dispatcher.UIThread.Post(() => Check(true));
    }

    public override string Id => "bingo";
    public override string Title => "Bingo of Work";

    long Value(string counter) => counter == BingoCard.Unlocked ? Host.Stats.UnlockedCount : Host.Stats.Get(counter);

    /// <summary>Squares that need something only come when the player has it: Claude's hooks, --while, a co-worker.</summary>
    bool Has(string need) => need switch
    {
        "claude" => Host.Stats.Get("claude.done") > 0,
        "task" => Host.Stats.Get("task.done") > 0,
        "lan" => Host.Lan.Connected || Host.Stats.Get("lan.wins") > 0 || Host.Stats.Get("chat.sent") > 0,
        _ => true,
    };

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 22, Height = 22, RadiusX = 3, RadiusY = 3, Fill = Art.Brush(Paper), Stroke = Art.Brush("#20232C"), StrokeThickness = 1.4 }, -11, -11));
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
            s.Rotor.Children.Add(Art.Circle(-6.5 + c * 6.5, -6.5 + r * 6.5, 1.4, Art.Brush("#20232C")));
        s.Rotor.Children.Add(Art.Circle(-6.5, -6.5, 3.6, Art.Brush(Color.FromArgb(170, 255, 92, 108))));
        s.Rotor.Children.Add(Art.Circle(0, 0, 3.6, Art.Brush(Color.FromArgb(170, 255, 92, 108))));
        s.Rotor.Children.Add(Art.Circle(6.5, 6.5, 3.6, Art.Brush(Color.FromArgb(170, 255, 92, 108))));
        return s;
    }

    public override HudInfo Hud => new(
        _card.Marked.ToString(CultureInfo.InvariantCulture) + "/" + BingoCard.Count.ToString(CultureInfo.InvariantCulture),
        _card.FullHouse ? L.T("FULL HOUSE! · a new card is on its way")
            : L.F("Squares dab themselves as you play · click a game square to play it · right-click swaps one ({0} left)", _card.SwapsLeft),
        L.F("Bingos {0}", Host.Stats.Get("bingo.lines")));

    // ------------------------------------------------------------------ the card

    long Amount(int i) => _card[i].Amount;

    string Label(int i) => _card[i] == BingoCard.FreeSquare ? L.T("FREE") : L.F(_card[i].Text, Amount(i));

    void Deal()
    {
        _card = BingoCard.Deal(Rng, Value, Has);
        _fresh.Clear();
        _pendingLines.Clear();
        _dealing = false;
        Save();
        foreach (var v in _views)
        {
            v.StampScale.ScaleX = v.StampScale.ScaleY = 1;
            v.Stamp.Opacity = 1;
        }
        DrawCard();
        if (_active)
        {
            for (int i = 0; i < BingoCard.Count; i++)
            {
                var flip = _views[i].Flip;
                Anims.Add(0.35, k => flip.ScaleX = k, Ease.OutBack, delay: i * 0.04);
            }
            Host.Sound.Play("board", 0.35, 1.1);
        }
        Host.HudChanged();
    }

    void Save()
    {
        Host.Settings.Bingo = _card.Save();
        Host.SaveSettings();
    }

    /// <summary>Dabs whatever has happened since the card last looked. <paramref name="playing"/>: someone is at the desk now.</summary>
    void Check(bool playing)
    {
        if (_checking || _dealing) return;
        _checking = true;
        try
        {
            Dab(_card.Mark(Value, DateTime.Now, playing));
        }
        finally
        {
            _checking = false;
        }
    }

    void Dab(List<int> ticked)
    {
        if (ticked.Count == 0) return;
        var lines = _card.CallLines();
        bool full = _card.FullHouse;
        Save();
        Host.Stats.Add("bingo.squares", ticked.Count);
        if (lines.Count > 0) Host.Stats.Add("bingo.lines", lines.Count);
        if (full) Host.Stats.Add("bingo.full");
        foreach (int l in lines) _pendingLines.Add(l);
        if (_active)
        {
            DrawCard();
            Celebrate(ticked, lines, full);
        }
        else if (full)
        {
            // nobody is looking at the card: say so, and have the next one ready
            Host.Notice(L.T("FULL HOUSE!"), L.T("Bingo of Work · a new card is ready"), Themes.Themed(Themes.ClassicGold));
            Host.Sound.Play("best", 0.6);
            Deal();
        }
        else
        {
            foreach (int i in ticked)
            {
                _fresh.Add(i);
                _views[i].Stamp.Opacity = 0; // stamped when the card shows again
            }
            DrawCard();
            string what = "✓ " + Label(ticked[0]);
            if (lines.Count > 0) Host.Notice(L.T("BINGO!"), what, Themes.Themed(Themes.ClassicGold));
            else Host.Notice(L.T("Bingo of Work"), what, Color.FromRgb(255, 140, 150));
            Host.Sound.Play("ding", 0.5);
        }
        Host.HudChanged();
    }

    /// <summary>Stamps the squares with a dab, calls the lines, and after a full house deals the next card.</summary>
    void Celebrate(IReadOnlyCollection<int> ticked, IReadOnlyCollection<int> lines, bool full)
    {
        double delay = 0;
        foreach (int i in ticked)
        {
            StampIn(i, delay);
            delay += 0.25;
        }
        foreach (int l in lines)
        {
            var mid = Center(BingoCard.Lines[l][1]);
            _pendingLines.Add(l);
            Anims.After(delay + 0.2, () =>
            {
                _pendingLines.Remove(l);
                DrawCard();
                Host.Fx.Popup(mid - new Vec2(0, 20), L.T("BINGO!"), Themes.Themed(Themes.ClassicGold), 40, 2.4);
                Host.Fx.Burst(mid, Themes.Current.Confetti, 36, 480, 600, 6, 1.0);
                Host.Sound.Play("star", 0.7);
            });
            delay += 0.5;
        }
        if (!full) return;
        _dealing = true;
        var at = _origin + new Vec2(BoardW * _scale / 2, BoardH * _scale * 0.45);
        Anims.After(delay + 0.5, () =>
        {
            Host.Fx.Popup(at, L.T("FULL HOUSE!"), Themes.Themed(Themes.ClassicGold), 46, 2.8, L.T("a new card is on its way"));
            Host.Fx.Burst(at, Themes.Current.Confetti, 60, 620, 700, 7, 1.3);
            Host.Sound.Play("best", 0.8);
        });
        Anims.After(delay + 3.2, Deal);
    }

    void StampIn(int i, double delay)
    {
        var v = _views[i];
        v.Stamp.Opacity = 0;
        Anims.Add(0.3, k =>
        {
            v.Stamp.Opacity = k;
            v.StampScale.ScaleX = v.StampScale.ScaleY = 1.8 - 0.8 * k;
        }, Ease.OutQuad, () => Host.Sound.Play("thunk", 0.45, 1.4), delay);
    }

    // ------------------------------------------------------------------ layout

    public override void Activate()
    {
        _active = true;
        base.Activate();
        Check(true); // opening the card is play too
        if (_fresh.Count > 0)
        {
            var fresh = _fresh.OrderBy(i => i).ToList();
            _fresh.Clear();
            Celebrate(fresh, _pendingLines.ToList(), _card.FullHouse);
        }
        else if (_card.FullHouse && !_dealing) Celebrate(Array.Empty<int>(), Array.Empty<int>(), true);
    }

    public override void Layout()
    {
        var a = Host.Arena;
        _scale = Clamp(Math.Min(a.Width * 0.42 / BoardW, a.Height * 0.5 / BoardH), 0.7, 1.35);
        _scale = Math.Min(_scale, Math.Min((a.Width - 20) / BoardW, (a.Height - 40) / BoardH));
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? new Vec2(a.Center.X - BoardW * _scale / 2, a.Center.Y - BoardH * _scale / 2);
        }
        Place();
        DrawCard();
        Host.HudChanged();
    }

    void Place()
    {
        var a = Host.Arena;
        double w = BoardW * _scale, h = BoardH * _scale;
        _origin = new Vec2(Clamp(_origin.X, a.Left + 8, Math.Max(a.Left + 8, a.Right - w - 8)),
            Clamp(_origin.Y, a.Top + DragHandle.Height + 12, Math.Max(a.Top + DragHandle.Height + 12, a.Bottom - h - 8)));
        _zoom.ScaleX = _zoom.ScaleY = _scale;
        Canvas.SetLeft(_board, _origin.X);
        Canvas.SetTop(_board, _origin.Y);
        _handle.Show(new Rect(_origin.X, _origin.Y, w, h));
    }

    public override void PositionsReset() => _placed = false;

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(BoardW * _scale / 2, 20);
        Place();
        _handle.Save(_origin);
    }

    public override void Deactivate()
    {
        _active = false;
        _handle.Cancel();
        Anims.Clear(); // the dabs and bingos still to come would pop up over the next game
        _pendingLines.Clear();
        foreach (var v in _views)
        {
            v.Stamp.Opacity = 1;
            v.StampScale.ScaleX = v.StampScale.ScaleY = 1;
            v.Flip.ScaleX = 1;
            v.Root.RenderTransform = null;
        }
        if (_dealing) Deal(); // a full house the card was celebrating: the new card is ready when it comes back
        else DrawCard();
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(new Rect(_origin.X, _origin.Y, BoardW * _scale, BoardH * _scale)));
        into.Add(_handle.Hit);
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_handle.Contains(p))
        {
            _handle.Begin(p, _origin);
            return true;
        }
        var local = new Point((p.X - _origin.X) / _scale, (p.Y - _origin.Y) / _scale);
        if (_newRect.Contains(local))
        {
            if (_dealing) return false;
            if (!_sure && _card.Marked > 1)
            {
                _sure = true; // a card with dabs on it takes a second click to throw away
                _sureFor = 2.5;
                PaintHeader();
                Host.Sound.Play("board", 0.25, 1.6);
                return false;
            }
            _sure = false;
            Deal();
            return false;
        }
        int col = (int)Math.Floor((local.X - Pad) / (SqW + Gap)), row = (int)Math.Floor((local.Y - HeaderH) / (SqH + Gap));
        if (col < 0 || col >= BingoCard.Side || row < 0 || row >= BingoCard.Side) return false;
        int i = row * BingoCard.Side + col;
        if (right) SwapSquare(i);
        else Open(i);
        return false;
    }

    public override void PointerUp(Vec2 p) => _handle.End(_origin);
    public override void PointerCancel() => _handle.Cancel();

    /// <summary>A game square takes you to its game; a desk square wiggles (it happens on its own).</summary>
    void Open(int i)
    {
        var s = _card[i];
        if (_card.IsMarked(i) || _dealing) return;
        if (s.GameId != null)
        {
            Host.Sound.Play("board", 0.3, 1.3);
            string game = s.GameId;
            Dispatcher.UIThread.Post(() => Host.SwitchGame(game));
            return;
        }
        var root = _views[i].Root;
        Anims.Add(0.4, k => root.RenderTransform = new RotateTransform(Math.Sin(k * Math.PI * 4) * 4 * (1 - k)), Ease.Linear,
            () => root.RenderTransform = null);
        Host.Sound.Play("board", 0.2, 0.8);
    }

    void SwapSquare(int i)
    {
        if (i == BingoCard.Free || _card.IsMarked(i) || _dealing) return;
        if (!_card.Swap(i, Rng, Value, Has))
        {
            Host.Fx.Popup(Center(i), _card.SwapsLeft == 0 ? L.T("No swaps left on this card") : L.T("Nothing to swap it for"),
                Color.FromRgb(255, 140, 150), 18, 1.6);
            Host.Sound.Play("board", 0.2, 0.7);
            return;
        }
        Save();
        var flip = _views[i].Flip;
        Anims.Add(0.16, k => flip.ScaleX = 1 - k, Ease.InQuad, () =>
        {
            DrawCard();
            Anims.Add(0.2, k => flip.ScaleX = k, Ease.OutBack);
        });
        Host.Sound.Play("swish", 0.3, 1.2);
        Host.HudChanged();
    }

    Vec2 Center(int i) => _origin + new Vec2((Pad + i % BingoCard.Side * (SqW + Gap) + SqW / 2) * _scale,
        (HeaderH + i / BingoCard.Side * (SqH + Gap) + SqH / 2) * _scale);

    public override bool Update(double dt)
    {
        if (_handle.Dragging)
        {
            _origin = _handle.Move(Host.Pointer, new Size(BoardW * _scale, BoardH * _scale));
            Place();
        }
        if (_sure)
        {
            _sureFor -= dt;
            if (_sureFor <= 0)
            {
                _sure = false;
                PaintHeader();
            }
        }
        return Anims.Update(dt) || _handle.Dragging || _sure;
    }

    // ------------------------------------------------------------------ demo

    /// <summary>Dabs a square now and then, as if the working day were going by quickly.</summary>
    public override void DemoTick()
    {
        if ((_demoWait -= 0.15) > 0 || _dealing || Anims.Busy) return;
        _demoWait = 1.5;
        var open = Enumerable.Range(0, BingoCard.Count).Where(i => !_card.IsMarked(i)).ToList();
        if (open.Count == 0) return;
        int pick = open[Rng.Next(open.Count)];
        if (_card.Tick(pick)) Dab(new List<int> { pick });
    }

    // ------------------------------------------------------------------ drawing

    SquareView BuildSquare(int i)
    {
        double x = Pad + i % BingoCard.Side * (SqW + Gap), y = HeaderH + i / BingoCard.Side * (SqH + Gap);
        var flip = new ScaleTransform(1, 1);
        var root = new Canvas { Width = SqW, Height = SqH, IsHitTestVisible = false, RenderTransformOrigin = RelativePoint.Center };
        var holder = new Canvas { Width = SqW, Height = SqH, RenderTransform = flip, RenderTransformOrigin = RelativePoint.Center, IsHitTestVisible = false };
        var tile = new Rectangle { Width = SqW, Height = SqH, RadiusX = 8, RadiusY = 8, StrokeThickness = 1.2, IsHitTestVisible = false };
        var text = new TextBlock
        {
            FontFamily = Fx.Font, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Width = SqW - 16, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, IsHitTestVisible = false,
        };
        var progress = new TextBlock { FontFamily = Fx.Font, FontSize = 10.5, FontWeight = FontWeight.Bold, Width = SqW, TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
        var play = new TextBlock { FontFamily = Fx.Font, FontSize = 10, FontWeight = FontWeight.Black, Text = "▶", IsHitTestVisible = false };
        var scale = new ScaleTransform(1, 1);
        // a dauber's dab: not quite centred, as a hand would put it
        double dx = (i * 37 % 9) - 4, dy = (i * 53 % 7) - 3;
        var stamp = new Ellipse { Width = 58, Height = 58, RenderTransform = scale, RenderTransformOrigin = RelativePoint.Center, IsHitTestVisible = false };
        Art.At(stamp, SqW / 2 - 29 + dx, SqH / 2 - 29 + dy);
        Art.At(progress, 0, SqH - 17);
        Art.At(play, SqW - 15, SqH - 16);
        foreach (var c in new Control[] { tile, stamp, text, progress, play }) holder.Children.Add(c); // the ink under the words, as on paper
        root.Children.Add(holder);
        Art.At(root, x, y);
        _squares.Children.Add(root);
        return new SquareView { Root = root, Tile = tile, Text = text, Progress = progress, Play = play, Stamp = stamp, StampScale = scale, Flip = flip };
    }

    void PaintHeader()
    {
        _title.Text = L.T("BINGO");
        _subtitle.Text = L.T("of Work");
        _title.Measure(Size.Infinity);
        Art.At(_title, Pad + 4, 5);
        Art.At(_subtitle, Pad + 10 + _title.DesiredSize.Width, 15);
        _newChip.Text = _sure ? L.T("Throw this card away?") : L.T("New card");
        _newChip.Measure(Size.Infinity);
        double w = _newChip.DesiredSize.Width + 18;
        _newRect = new Rect(BoardW - Pad - w, 9, w, 22);
        _newBack.Width = w;
        Art.At(_newBack, _newRect.X, _newRect.Y);
        Art.At(_newChip, _newRect.X + 9, _newRect.Y + 3);
    }

    void DrawCard()
    {
        var t = Themes.Current;
        var dab = Art.Brush(Color.FromArgb(105, t.Rival.R, t.Rival.G, t.Rival.B));
        var ink = Art.Brush(InkText);
        for (int i = 0; i < BingoCard.Count; i++)
        {
            var v = _views[i];
            var s = _card[i];
            bool free = s == BingoCard.FreeSquare, game = s.GameId != null, marked = _card.IsMarked(i);
            var fill = free ? FreeFill : game ? GameFill : DeskFill;
            v.Tile.Fill = Art.Brush(fill);
            v.Tile.Stroke = Art.Brush(Art.Blend(fill, InkText, 0.35));
            v.Text.Text = Label(i);
            v.Text.FontSize = free ? 22 : 12.5;
            v.Text.FontWeight = free ? FontWeight.Black : FontWeight.SemiBold;
            v.Text.Foreground = ink;
            v.Text.Measure(new Size(SqW - 16, double.PositiveInfinity));
            bool counted = !marked && s.Amount > 1;
            Art.At(v.Text, 8, Math.Max(4, (SqH - v.Text.DesiredSize.Height) / 2 - (counted ? 6 : 0)));
            v.Progress.IsVisible = counted;
            if (counted)
            {
                v.Progress.Text = _card.Progress(i, Value).ToString(CultureInfo.InvariantCulture) + " / " + s.Amount.ToString(CultureInfo.InvariantCulture);
                v.Progress.Foreground = Art.Brush(Art.Blend(InkText, fill, 0.35));
            }
            v.Play.IsVisible = game && !marked;
            v.Play.Foreground = Art.Brush(Art.Blend(InkText, fill, 0.3));
            v.Stamp.Fill = dab;
            v.Stamp.IsVisible = marked;
        }
        _lines.Children.Clear();
        var marker = Art.Brush(Color.FromArgb(140, t.Rival.R, t.Rival.G, t.Rival.B));
        for (int l = 0; l < BingoCard.Lines.Length; l++)
        {
            if (!_card.IsCalled(l) || _pendingLines.Contains(l)) continue;
            var line = BingoCard.Lines[l];
            Point a = LocalCenter(line[0]), b = LocalCenter(line[^1]);
            var d = new Vector(b.X - a.X, b.Y - a.Y);
            d /= Math.Max(1, Math.Sqrt(d.X * d.X + d.Y * d.Y));
            a -= d * 26;
            b += d * 26;
            _lines.Children.Add(new Line { StartPoint = a, EndPoint = b, Stroke = marker, StrokeThickness = 5, StrokeLineCap = PenLineCap.Round, IsHitTestVisible = false });
        }
        PaintHeader();
    }

    static Point LocalCenter(int i) =>
        new(Pad + i % BingoCard.Side * (SqW + Gap) + SqW / 2, HeaderH + i / BingoCard.Side * (SqH + Gap) + SqH / 2);

    public override void ThemeChanged()
    {
        var t = Themes.Current;
        _back.Fill = Art.Brush(Paper);
        _back.Stroke = Art.Brush(Art.Blend(t.Accent, InkText, 0.3));
        _band.Fill = Art.Brush(Art.Blend(t.Ink, t.Accent, 0.3));
        _title.Foreground = Art.Brush(t.Gold);
        _subtitle.Foreground = Art.Brush(t.HudFront);
        _newBack.Fill = Art.Brush(Color.FromArgb(70, t.Accent.R, t.Accent.G, t.Accent.B));
        _newBack.Stroke = Art.Brush(t.Accent);
        _newChip.Foreground = Art.Brush(t.HudFront);
        DrawCard();
    }
}
