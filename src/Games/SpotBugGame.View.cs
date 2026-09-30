using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Spot the Bug's board: the mode and language chips, the fuse, the code panel (line numbers, syntax colours, a hover
/// highlight, and after each snippet the fix as a diff: the buggy line struck in red, the fix in green under it) and the
/// text under the code. Rows slide and fade when the fix comes in; the board grows and shrinks with the snippet.
/// </summary>
public sealed partial class SpotBugGame
{
    const double W = 660, Pad = 12, Row1Y = 8, Row2Y = 36, FuseY = 66, FuseH = 4, CodeTop = 76;
    const double LineH = 20, CodePadY = 6, GutterW = 46, TextX = GutterW + 8, CodeW = W - Pad * 2, CardH = 176, InfoGap = 10;
    const double ButtonH = 26, LayoutSeconds = 0.34;

    /// <summary>A monospace face on every desktop, first choice first.</summary>
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, SF Mono, Menlo, JetBrains Mono, DejaVu Sans Mono, Liberation Mono, Ubuntu Mono, monospace");

    static readonly Color Red = Color.FromRgb(240, 84, 84), Green = Color.FromRgb(64, 196, 120);

    sealed class RowView
    {
        public required Canvas Root;
        public required Rectangle Back;
        public required TextBlock Mark;
        public required TranslateTransform Shift;
        /// <summary>The code line it shows (1-based), or 0 for a line of the fix.</summary>
        public int Line;
        /// <summary>Where it sits in the code panel now, and where the layout wants it.</summary>
        public double Y, To;
    }

    readonly Canvas _board = new();
    readonly ScaleTransform _zoom = new(1, 1);
    readonly Rectangle _back = new() { RadiusX = 10, RadiusY = 10, StrokeThickness = 1.5, Width = W, IsHitTestVisible = false };
    readonly TextBlock _modeChip = Chip(), _newChip = Chip(), _clock = Label(14), _progress = Label(11.5);
    readonly Rectangle _modeBack = ChipBack(), _newBack = ChipBack();
    readonly (Rectangle Back, TextBlock Text)[] _langChips = new (Rectangle, TextBlock)[SpotBugDeck.Languages.Length + 1];
    readonly Rect[] _langRects = new Rect[SpotBugDeck.Languages.Length + 1];
    readonly Rectangle _fuseBack = new() { Height = FuseH, Width = CodeW, RadiusX = 2, RadiusY = 2, IsHitTestVisible = false };
    readonly Rectangle _fuse = new() { Height = FuseH, RadiusX = 2, RadiusY = 2, IsHitTestVisible = false };
    readonly Canvas _code = new() { Width = CodeW };
    readonly Rectangle _codeBack = new() { RadiusX = 7, RadiusY = 7, Width = CodeW, IsHitTestVisible = false };
    readonly Rectangle _gutterBack = new() { RadiusX = 7, RadiusY = 7, Width = GutterW, IsHitTestVisible = false };
    readonly Rectangle _gutterEdge = new() { Width = 8, IsHitTestVisible = false };
    readonly Rectangle _hover = new() { Height = LineH, Width = CodeW, IsHitTestVisible = false, IsVisible = false };
    readonly Rectangle _hoverEdge = new() { Height = LineH, Width = 3, IsHitTestVisible = false, IsVisible = false };
    readonly Canvas _rowsLayer = new() { IsHitTestVisible = false };
    // takes the pointer over the code, only to move the hover highlight; clicks still go through the overlay's hit shapes
    readonly Rectangle _catcher = new() { Fill = Brushes.Transparent, Width = CodeW, Cursor = new Cursor(StandardCursorType.Hand) };
    readonly TextBlock _cardTitle = new() { FontFamily = Fx.Font, FontSize = 21, FontWeight = FontWeight.Black, Width = CodeW, TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
    readonly TextBlock _cardText = new() { FontFamily = Fx.Font, FontSize = 13, Width = CodeW - 80, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, LineHeight = 19, IsHitTestVisible = false };
    readonly Canvas _info = new() { IsHitTestVisible = false };
    readonly TextBlock _infoTitle = new() { FontFamily = Fx.Font, FontSize = 15, FontWeight = FontWeight.Black, IsHitTestVisible = false };
    readonly TextBlock _infoTag = new() { FontFamily = Mono, FontSize = 11, IsHitTestVisible = false };
    readonly Rectangle _tagBack = new() { Height = 18, RadiusX = 9, RadiusY = 9, StrokeThickness = 1, IsHitTestVisible = false };
    readonly TextBlock _infoText = new() { FontFamily = Fx.Font, FontSize = 13, Width = CodeW, TextWrapping = TextWrapping.Wrap, LineHeight = 18.5, IsHitTestVisible = false };
    readonly TextBlock _infoNote = new() { FontFamily = Fx.Font, FontSize = 12, IsHitTestVisible = false };
    readonly Rectangle _buttonBack = new() { Height = ButtonH, RadiusX = 13, RadiusY = 13, StrokeThickness = 1.2, IsHitTestVisible = false };
    readonly TextBlock _buttonText = new() { FontFamily = Fx.Font, FontSize = 13, FontWeight = FontWeight.Bold, IsHitTestVisible = false };
    readonly List<RowView> _rows = new();
    readonly Dictionary<Tok, IBrush> _inks = new();
    DragHandle _handle = null!;
    SpotBugSyntax.Palette _palette = SpotBugSyntax.PaletteFor(Themes.Current);
    Snippet? _rowsSnippet;
    bool _rowsRevealed, _buttonShown, _placed;
    Rect _modeRect, _newRect, _buttonRect;
    /// <summary>Where the player wants the board, and where it is shown (kept inside the arena as it grows and shrinks).</summary>
    Vec2 _origin, _at;
    double _scale = 1, _codeH = CardH, _infoH, _boardH = CodeTop + CardH + InfoGap + ButtonH + Pad;
    int? _hoverLine;
    Anims.Tween? _layoutTween;

    static TextBlock Chip() => new() { FontFamily = Fx.Font, FontSize = 12, FontWeight = FontWeight.Bold, IsHitTestVisible = false };
    static TextBlock Label(double size) => new() { FontFamily = Fx.Font, FontSize = size, FontWeight = FontWeight.Black, IsHitTestVisible = false };
    static Rectangle ChipBack() => new() { RadiusX = 10, RadiusY = 10, Height = 22, StrokeThickness = 1, IsHitTestVisible = false };
    static string Clock(double seconds) => string.Create(CultureInfo.InvariantCulture, $"{(int)seconds / 60}:{(int)seconds % 60:00}");

    void BuildView()
    {
        _board.RenderTransformOrigin = RelativePoint.TopLeft;
        _board.RenderTransform = _zoom;
        foreach (var c in new Control[] { _back, _modeBack, _modeChip, _newBack, _newChip, _clock, _progress, _fuseBack, _fuse }) _board.Children.Add(c);
        for (int k = 0; k < _langChips.Length; k++)
        {
            _langChips[k] = (ChipBack(), Chip());
            _board.Children.Add(_langChips[k].Back);
            _board.Children.Add(_langChips[k].Text);
        }
        foreach (var c in new Control[] { _codeBack, _gutterBack, _gutterEdge, _hover, _hoverEdge, _rowsLayer, _cardTitle, _cardText, _catcher }) _code.Children.Add(c);
        Art.At(_code, Pad, CodeTop);
        Art.At(_fuseBack, Pad, FuseY);
        Art.At(_fuse, Pad, FuseY);
        Art.At(_gutterEdge, GutterW - 8, 0);
        Art.At(_catcher, 0, CodePadY);
        _board.Children.Add(_code);
        foreach (var c in new Control[] { _infoTitle, _tagBack, _infoTag, _infoText, _infoNote, _buttonBack, _buttonText }) _info.Children.Add(c);
        Art.At(_info, Pad, CodeTop + CardH + InfoGap);
        _board.Children.Add(_info);
        _catcher.PointerMoved += (_, e) => HoverAt(e.GetPosition(_code).Y);
        _catcher.PointerExited += (_, _) => SetHover(null);
        _handle = new DragHandle(Host, Id, Title);
        Layer.Children.Add(_board);
        Layer.Children.Add(_handle.Visual);
        Paint();
    }

    public override Sprite CreateIcon()
    {
        var s = new Sprite();
        var ink = Art.Brush("#1B1F2A");
        s.Rotor.Children.Add(Art.At(new Rectangle { Width = 17, Height = 20, RadiusX = 3, RadiusY = 3, Fill = Art.Brush(240, 30, 34, 46), Stroke = Art.Brush("#6B7488"), StrokeThickness = 1 }, -11, -10));
        foreach (var (y, w) in new[] { (-6.5, 10.0), (-3.0, 7.0), (0.5, 11.0), (4.0, 5.0) })
            s.Rotor.Children.Add(Art.At(new Rectangle { Width = w, Height = 1.6, Fill = Art.Brush("#9AB4D8") }, -8, y));
        s.Rotor.Children.Add(Art.PathOf("M2,3 L-0.5,1.5 M1.8,5.5 L-0.8,5.5 M2.4,8 L0.2,9.8 M9,3 L11.5,1.5 M9.2,5.5 L11.8,5.5 M8.6,8 L10.8,9.8", null, ink, 1.1));
        s.Rotor.Children.Add(Art.Circle(5.5, 6, 4.2, Art.Brush(Art.Safe(Color.FromRgb(232, 62, 62))), ink, 0.9));
        s.Rotor.Children.Add(Art.Circle(5.5, 1.6, 2.1, ink));
        s.Rotor.Children.Add(Art.PathOf("M5.5,2.5 L5.5,10.2", null, ink, 0.9));
        s.Rotor.Children.Add(Art.Circle(3.9, 6.2, 0.9, ink));
        s.Rotor.Children.Add(Art.Circle(7.1, 7.4, 0.9, ink));
        return s;
    }

    // ------------------------------------------------------------------ layout

    public override void Layout()
    {
        var a = Host.Arena;
        double tallest = CodeTop + 20 * LineH + CodePadY * 2 + InfoGap + 150 + Pad;
        _scale = Clamp(Math.Min(a.Width * 0.46 / W, a.Height * 0.9 / tallest), 0.6, 1.2);
        _scale = Math.Min(_scale, Math.Min((a.Width - 20) / W, (a.Height - 40) / tallest));
        if (!_placed)
        {
            _placed = true;
            _origin = _handle.Saved() ?? new Vec2(a.Center.X - W * _scale / 2, a.Top + Math.Max(DragHandle.Height + 16, (a.Height - tallest * _scale) / 2));
        }
        if (CheckSession() || LanOn && _mode == Mode.Daily) NewRound();
        if (_paintedColorBlind != Art.ColorBlind) ThemeChanged(); // the overlay lays the game out again when colour-blind mode is toggled
        Place();
        Host.HudChanged();
    }

    bool _paintedColorBlind;

    void Place()
    {
        var a = Host.Arena;
        double w = W * _scale, h = _boardH * _scale, top = a.Top + DragHandle.Height + 12;
        _at = new Vec2(Clamp(_origin.X, a.Left + 8, Math.Max(a.Left + 8, a.Right - w - 8)), Clamp(_origin.Y, top, Math.Max(top, a.Bottom - h - 8)));
        _zoom.ScaleX = _zoom.ScaleY = _scale;
        Canvas.SetLeft(_board, _at.X);
        Canvas.SetTop(_board, _at.Y);
        _handle.Show(new Rect(_at.X, _at.Y, w, h));
    }

    public override void PositionsReset() => _placed = false;

    public override void Summon(Vec2 p)
    {
        _origin = p - new Vec2(W * _scale / 2, 20);
        Place();
        _handle.Save(_at);
    }

    Vec2 BoardPoint(double x, double y) => _at + new Vec2(x * _scale, y * _scale);
    Vec2 LinePoint(int line) => BoardPoint(Pad + TextX + 140, CodeTop + CodePadY + (line - 0.5) * LineH);

    /// <summary>The code line under a point on the board, while a snippet is being looked at.</summary>
    int? LineAt(Point local)
    {
        if (_round.Phase != SpotPhase.Playing || local.X < Pad || local.X > Pad + CodeW) return null;
        int line = (int)Math.Floor((local.Y - CodeTop - CodePadY) / LineH) + 1;
        return line >= 1 && line <= _round.Current.Lines ? line : null;
    }

    // ------------------------------------------------------------------ what is shown

    /// <summary>Today's daily snippet has been tried: it stays on the board with its fix.</summary>
    bool DailyDoneView => Shown == Mode.Daily && DailyDone && _round.Phase == SpotPhase.Ready;

    Snippet? ShownSnippet => DailyDoneView ? _round.Current : _round.Phase is SpotPhase.Playing or SpotPhase.Revealed ? _round.Current : null;

    bool RevealedView => DailyDoneView || _round.Phase == SpotPhase.Revealed && !_revealing;

    /// <summary>Brings the board up to date with the round: the rows (with the fix when it is time), the texts and the layout.</summary>
    void ShowRound(bool animate)
    {
        SetHover(null);
        var snippet = ShownSnippet;
        bool revealed = RevealedView;
        if (snippet != null && snippet == _rowsSnippet && revealed && !_rowsRevealed) RevealRows();
        else if (snippet != _rowsSnippet || revealed != _rowsRevealed) BuildRows(snippet, revealed, animate);
        Draw();
        Relayout(animate);
    }

    /// <summary>Fresh rows for <paramref name="snippet"/> (none for the start and end cards), with its fix when revealed.</summary>
    void BuildRows(Snippet? snippet, bool revealed, bool fadeIn)
    {
        _rowsLayer.Children.Clear();
        _rows.Clear();
        _rowsSnippet = snippet;
        _rowsRevealed = revealed;
        if (snippet == null) return;
        var tokens = SpotBugSyntax.Lines(snippet.Lang, snippet.Code);
        for (int line = 1; line <= snippet.Lines; line++)
        {
            AddRow(tokens[line - 1], line, _rows.Count);
            if (revealed) AddFixRows(snippet, line, _rows.Count);
        }
        foreach (var r in _rows)
        {
            r.Y = r.To = CodePadY + _rows.IndexOf(r) * LineH;
            Canvas.SetTop(r.Root, r.Y);
            r.Root.Opacity = fadeIn ? 0 : 1;
        }
    }

    /// <summary>The fix comes in under the lines it replaces; the rows below slide down to make room (see <see cref="Relayout"/>).</summary>
    void RevealRows()
    {
        var snippet = _rowsSnippet!;
        _rowsRevealed = true;
        for (int line = snippet.Lines; line >= 1; line--)
        {
            int at = _rows.FindIndex(r => r.Line == line);
            int before = _rows.Count;
            AddFixRows(snippet, line, at + 1);
            for (int k = at + 1; k < at + 1 + _rows.Count - before; k++)
            {
                _rows[k].Y = _rows[at].Y;
                Canvas.SetTop(_rows[k].Root, _rows[k].Y);
                _rows[k].Root.Opacity = 0;
            }
        }
    }

    void AddFixRows(Snippet snippet, int line, int index)
    {
        if (snippet.FixFor(line) is not { Count: > 0 } fix) return;
        var tokens = SpotBugSyntax.Lines(snippet.Lang, fix);
        for (int k = 0; k < fix.Count; k++) AddRow(tokens[k], 0, index + k);
    }

    void AddRow(List<(string Text, Tok Kind)> tokens, int line, int index)
    {
        var shift = new TranslateTransform();
        var root = new Canvas { IsHitTestVisible = false, RenderTransform = shift };
        var back = new Rectangle { Width = CodeW, Height = LineH, IsHitTestVisible = false };
        var mark = new TextBlock { FontFamily = Mono, FontSize = 12.5, FontWeight = FontWeight.Bold, Width = 14, TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
        var number = new TextBlock { FontFamily = Mono, FontSize = 11.5, Width = 24, TextAlignment = TextAlignment.Right, IsHitTestVisible = false, Foreground = Art.Brush(_palette.LineNumber) };
        var text = new TextBlock { FontFamily = Mono, FontSize = 13, IsHitTestVisible = false };
        number.Text = line > 0 ? line.ToString(CultureInfo.InvariantCulture) : "";
        var runs = new InlineCollection();
        foreach (var (t, kind) in tokens) runs.Add(new Run(t) { Foreground = Ink(kind) });
        text.Inlines = runs;
        root.Children.Add(Art.At(back, 0, 0));
        root.Children.Add(Art.At(mark, 3, 1.5));
        root.Children.Add(Art.At(number, 16, 2.5));
        root.Children.Add(Art.At(text, TextX, 1.5));
        _rowsLayer.Children.Add(root);
        _rows.Insert(index, new RowView { Root = root, Back = back, Mark = mark, Shift = shift, Line = line });
    }

    IBrush Ink(Tok kind)
    {
        if (!_inks.TryGetValue(kind, out var brush)) _inks[kind] = brush = Art.Brush(_palette.Ink[kind]);
        return brush;
    }

    /// <summary>
    /// Moves everything to where the current content wants it: the rows, the code panel's height, the text under it and
    /// the board. Animated, rows slide, new rows fade in and the board eases to its new height.
    /// </summary>
    void Relayout(bool animate)
    {
        for (int k = 0; k < _rows.Count; k++) _rows[k].To = CodePadY + k * LineH;
        double codeTo = _rows.Count > 0 ? _rows.Count * LineH + CodePadY * 2 : CardH;
        MeasureInfo();
        double boardTo = CodeTop + codeTo + InfoGap + _infoH + Pad;
        _layoutTween?.Cancel();
        var from = _rows.Select(r => r.Y).ToArray();
        var fading = _rows.Where(r => r.Root.Opacity < 1).ToList();
        double codeFrom = _codeH, boardFrom = _boardH;
        void Apply(double k)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                r.Y = from[i] + (r.To - from[i]) * k;
                Canvas.SetTop(r.Root, r.Y);
            }
            foreach (var r in fading) r.Root.Opacity = k;
            _codeH = codeFrom + (codeTo - codeFrom) * k;
            _boardH = boardFrom + (boardTo - boardFrom) * k;
            SizePanels();
            _info.Opacity = animate ? Math.Min(1, k * 1.4) : 1;
            if (_placed) Place();
        }
        if (!animate)
        {
            Apply(1);
            return;
        }
        _info.Opacity = 0;
        _layoutTween = Anims.Add(LayoutSeconds, Apply, Ease.OutCubic);
    }

    void SizePanels()
    {
        _back.Height = _boardH;
        _codeBack.Height = _gutterBack.Height = _gutterEdge.Height = _codeH;
        _code.Height = _codeH;
        _catcher.Height = _round.Phase == SpotPhase.Playing && _rows.Count > 0 ? _round.Current.Lines * LineH : 0;
        _catcher.IsVisible = _catcher.Height > 0;
        bool card = _rows.Count == 0;
        _cardTitle.IsVisible = _cardText.IsVisible = card;
        _gutterBack.IsVisible = _gutterEdge.IsVisible = !card;
        if (card)
        {
            _cardTitle.Measure(Size.Infinity);
            _cardText.Measure(new Size(_cardText.Width, double.PositiveInfinity));
            double h = _cardTitle.DesiredSize.Height + 10 + _cardText.DesiredSize.Height, top = Math.Max(10, (_codeH - h) / 2);
            Art.At(_cardTitle, 0, top);
            Art.At(_cardText, 40, top + _cardTitle.DesiredSize.Height + 10);
        }
        Canvas.SetTop(_info, CodeTop + _codeH + InfoGap);
    }

    /// <summary>Lays out the text under the code (title and tag, the explanation, the note and the button) and measures it.</summary>
    void MeasureInfo()
    {
        double y = 0;
        bool title = _infoTitle.Text is { Length: > 0 };
        _infoTitle.IsVisible = title;
        if (title)
        {
            _infoTitle.Measure(Size.Infinity);
            Art.At(_infoTitle, 0, 0);
            bool tag = _infoTag.Text is { Length: > 0 };
            _infoTag.IsVisible = _tagBack.IsVisible = tag;
            if (tag)
            {
                _infoTag.Measure(Size.Infinity);
                double x = _infoTitle.DesiredSize.Width + 10;
                _tagBack.Width = _infoTag.DesiredSize.Width + 16;
                Art.At(_tagBack, x, 1.5);
                Art.At(_infoTag, x + 8, 3);
            }
            y = 26;
        }
        else _infoTag.IsVisible = _tagBack.IsVisible = false;
        bool text = _infoText.Text is { Length: > 0 };
        _infoText.IsVisible = text;
        if (text)
        {
            _infoText.Measure(new Size(CodeW, double.PositiveInfinity));
            Art.At(_infoText, 0, y);
            y += _infoText.DesiredSize.Height + 8;
        }
        bool note = _infoNote.Text is { Length: > 0 };
        _infoNote.IsVisible = note;
        _buttonBack.IsVisible = _buttonText.IsVisible = _buttonShown;
        if (_buttonShown)
        {
            _buttonText.Measure(Size.Infinity);
            double bw = _buttonText.DesiredSize.Width + 30;
            _buttonBack.Width = bw;
            Art.At(_buttonBack, CodeW - bw, y);
            Art.At(_buttonText, CodeW - bw + 15, y + 4.5);
            _buttonRect = new Rect(Pad + CodeW - bw, 0, bw, ButtonH); // the board's y is set below, once the code height is known
        }
        if (note) Art.At(_infoNote, 0, y + (_buttonShown ? 5 : 0));
        _buttonTop = y;
        if (_buttonShown || note) y += _buttonShown ? ButtonH : 18;
        _infoH = Math.Max(0, y);
    }

    double _buttonTop;

    // ------------------------------------------------------------------ drawing

    /// <summary>Sets every text and colour from the state; positions only where the header lays out its chips.</summary>
    void Draw()
    {
        var t = Themes.Current;
        var snippet = ShownSnippet;
        bool daily = Shown == Mode.Daily;

        // header: mode, New, the language chips, the clock and where we are
        _modeChip.Text = LanOn ? L.T("LAN race") : daily ? L.T("Daily snippet") : L.T("Round of ten");
        _newChip.Text = L.T("New round");
        double x = Pad;
        _modeRect = PlaceChip(_modeBack, _modeChip, ref x, Row1Y);
        _modeBack.Opacity = _modeChip.Opacity = LanOn || CanSwitchMode ? 1 : 0.5;
        bool showNew = !daily;
        _newBack.IsVisible = _newChip.IsVisible = showNew;
        _newRect = showNew ? PlaceChip(_newBack, _newChip, ref x, Row1Y) : default;
        bool langLocked = LanOn || daily || _round.Phase is SpotPhase.Playing or SpotPhase.Revealed;
        x = Pad;
        for (int k = 0; k < _langChips.Length; k++)
        {
            var (back, text) = _langChips[k];
            text.Text = k == 0 ? L.T("All") : SpotBugDeck.LanguageName(SpotBugDeck.Languages[k - 1]);
            _langRects[k] = PlaceChip(back, text, ref x, Row2Y);
            bool on = LanOn ? k == 0 : k == _lang;
            back.Fill = Art.Brush(on ? Art.Blend(t.Accent, t.Ink, 0.3) : Color.FromArgb(40, t.Accent.R, t.Accent.G, t.Accent.B));
            back.Stroke = Art.Brush(Color.FromArgb(on ? (byte)230 : (byte)110, t.Accent.R, t.Accent.G, t.Accent.B));
            back.Opacity = text.Opacity = langLocked && !on ? 0.4 : 1;
            back.IsVisible = text.IsVisible = !daily; // the daily snippet is the same for everyone, whatever the filter
        }
        _progress.Text = snippet == null ? "" : (daily ? "#" + DailyNumber.ToString(CultureInfo.InvariantCulture) : L.F("{0}/{1}", _round.Index + 1, _round.Snippets.Count))
            + " · " + SpotBugDeck.LanguageName(snippet.Lang) + " · " + L.T(MiniGame.LevelNames[(int)snippet.Level]);
        _progress.Measure(Size.Infinity);
        Art.At(_progress, W - Pad - _progress.DesiredSize.Width, Row2Y + 4);
        DrawFuse();

        // the rows
        foreach (var r in _rows) PaintRow(r, _rowsSnippet!);

        // the card over the code at the start and the end, and the text under the code
        DrawTexts(snippet);
    }

    static Rect PlaceChip(Rectangle back, TextBlock text, ref double x, double y)
    {
        text.Measure(Size.Infinity);
        double w = text.DesiredSize.Width + 18;
        var r = new Rect(x, y, w, 22);
        back.Width = w;
        Art.At(back, r.X, r.Y);
        Art.At(text, r.X + 9, r.Y + 3);
        x += w + 6;
        return r;
    }

    /// <summary>The fuse under the chips, and the seconds left beside the mode chip; red as it burns down.</summary>
    void DrawFuse()
    {
        var t = Themes.Current;
        bool on = _round.Phase is SpotPhase.Playing or SpotPhase.Revealed && !DailyDoneView;
        _fuse.IsVisible = _fuseBack.IsVisible = on;
        _clock.IsVisible = on;
        if (!on) return;
        double share = Math.Clamp(_round.Left / _round.Fuse, 0, 1);
        _fuse.Width = Math.Max(0.01, CodeW * share);
        var hot = Art.Safe(Red);
        var c = share > 0.5 ? t.Accent : share > 0.2 ? Art.Blend(t.Gold, t.Accent, (share - 0.2) / 0.3) : Art.Blend(hot, t.Gold, share / 0.2);
        _fuse.Fill = Art.Brush(c);
        _fuseBack.Fill = Art.Brush(Color.FromArgb(50, t.HudFront.R, t.HudFront.G, t.HudFront.B));
        string clock = Clock(Math.Ceiling(_round.Left));
        if (_clock.Text != clock)
        {
            _clock.Text = clock;
            _clock.Measure(Size.Infinity);
            Art.At(_clock, W - Pad - _clock.DesiredSize.Width, Row1Y + 2);
        }
        _clock.Foreground = Art.Brush(share > 0.2 ? t.Gold : hot);
    }

    /// <summary>A row's background and mark: a tried line crossed out, and after the reveal the diff's red and green.</summary>
    void PaintRow(RowView r, Snippet snippet)
    {
        bool light = _palette.Light;
        var red = Art.Safe(Red);
        var green = Art.Safe(Green);
        Color? fill = null;
        string mark = "";
        Color markColor = red;
        if (_rowsRevealed)
        {
            if (r.Line == 0)
            {
                fill = Color.FromArgb(light ? (byte)60 : (byte)58, green.R, green.G, green.B);
                mark = "+";
                markColor = green;
            }
            else if (snippet.Fix.ContainsKey(r.Line))
            {
                fill = Color.FromArgb(light ? (byte)55 : (byte)62, red.R, red.G, red.B);
                mark = "−";
            }
            else if (snippet.IsBug(r.Line))
            {
                fill = Color.FromArgb(light ? (byte)36 : (byte)40, red.R, red.G, red.B);
                mark = "!";
            }
        }
        else if (_round.Tried.Contains(r.Line))
        {
            fill = Color.FromArgb(light ? (byte)34 : (byte)40, red.R, red.G, red.B);
            mark = "✗";
        }
        r.Back.Fill = fill is { } f ? Art.Brush(f) : null;
        r.Mark.Text = mark;
        r.Mark.Foreground = Art.Brush(SpotBugSyntax.Readable(markColor, _palette.Back, 3));
    }

    void DrawTexts(Snippet? snippet)
    {
        bool daily = Shown == Mode.Daily;
        string title = "", tag = "", text = "", note = "", button = "", cardTitle = "", cardText = "";
        var titleColor = Themes.Current.HudFront;
        if (DailyDoneView || _round.Phase == SpotPhase.Revealed && !_revealing)
        {
            bool found = DailyDoneView ? Get(DayFoundKey) == 1 : _round.LastFound;
            if (DailyDoneView) title = found ? L.T("✓ You found today's bug") : L.T("✗ Today's bug got away");
            else title = found ? L.F("✓ Found it · +{0}", _round.LastPoints) : _round.LastTimedOut ? L.T("✗ Time's up") : L.T("✗ Three wrong clicks");
            titleColor = SpotBugSyntax.Readable(Art.Safe(found ? Green : Red), Themes.Current.Ink, 4.5);
            tag = SpotBugDeck.KindName(snippet!.Kind);
            text = snippet.WhyIn(L.Code);
            if (daily)
            {
                int clicks = Get(DayClicksKey), seconds = Get(DaySecondsKey);
                note = found ? L.F("found in {0}, {1} s · streak {2}", clicks == 1 ? L.T("1 click") : L.F("{0} clicks", clicks), seconds, Streak)
                    : L.T("the streak starts again with tomorrow's snippet");
                button = L.T("Play a round ▸");
            }
            else
            {
                note = L.F("{0} points so far · snippet {1} of {2}", _round.Score, _round.Index + 1, _round.Snippets.Count);
                button = _round.IsLast ? L.T("See the result ▸") : L.T("Next ▸");
            }
        }
        else if (_round.Phase is SpotPhase.Playing or SpotPhase.Revealed)
        {
            title = L.T("Which line has the bug?");
            if (snippet!.Bug.Count > 1) text = L.T("This bug spans two lines: either one counts.");
            int left = SpotBugRound.MaxWrong - _round.Wrong;
            note = _revealing ? "" : left == 1 ? L.F("last try · a wrong click costs {0} points", SpotBugRound.WrongPenalty)
                : L.F("{0} tries left · a wrong click costs {1} points", left, SpotBugRound.WrongPenalty);
        }
        else if (_round.Phase == SpotPhase.Over)
        {
            cardTitle = _round.Perfect ? L.T("A clean review!") : L.T("Round over");
            cardText = L.F("{0} of {1} bugs found, {2} at the first click · {3} points in {4}", _round.Found, _round.Snippets.Count, _round.FirstClicks, _round.Score, Clock(_round.Seconds));
            long best = Host.Stats.Get("spotbug.best");
            note = best > 0 ? L.F("Best {0}", best) : "";
            button = L.T("New round ▸");
        }
        else if (daily)
        {
            cardTitle = L.F("Daily snippet #{0}", DailyNumber);
            cardText = L.T("The same snippet for everyone today, and one try at it. Find the bug in as few clicks and seconds as you can.");
            note = Streak > 0 ? L.F("streak {0} · keep it going", Streak) : L.T("find it to start a streak");
            button = L.T("Start ▸");
        }
        else
        {
            cardTitle = L.T("Ten snippets, one bug in each");
            cardText = L.T("Click the line with the bug before the fuse burns down. Three wrong clicks and the snippet is gone; the fix and the reason come after each one.");
            note = L.F("a find scores {0}, and up to {1} more for speed", SpotBugRound.FoundPoints, SpotBugRound.SpeedPoints);
            button = L.T("Start ▸");
        }

        var t = Themes.Current;
        _infoTitle.Text = title;
        _infoTitle.Foreground = Art.Brush(titleColor);
        _infoTag.Text = tag;
        _infoTag.Foreground = Art.Brush(Art.Blend(t.HudFront, t.Ink, 0.12));
        _tagBack.Fill = Art.Brush(Color.FromArgb(46, t.Accent.R, t.Accent.G, t.Accent.B));
        _tagBack.Stroke = Art.Brush(Color.FromArgb(140, t.Accent.R, t.Accent.G, t.Accent.B));
        _infoText.Text = text;
        _infoText.Foreground = Art.Brush(Art.Blend(t.HudFront, t.Ink, 0.08));
        _infoNote.Text = note;
        _infoNote.Foreground = Art.Brush(Art.Blend(t.HudFront, t.Ink, 0.3));
        _buttonShown = button.Length > 0 && !_revealing;
        _buttonText.Text = button;
        _buttonText.Foreground = Art.Brush(t.HudFront);
        _buttonBack.Fill = Art.Brush(Art.Blend(t.Accent, t.Ink, 0.35));
        _buttonBack.Stroke = Art.Brush(t.Accent);
        _cardTitle.Text = cardTitle;
        _cardTitle.Foreground = Art.Brush(SpotBugSyntax.Readable(t.Gold, _palette.Back, 3));
        _cardText.Text = cardText;
        _cardText.Foreground = Art.Brush(_palette.Ink[Tok.Plain]);
    }

    /// <summary>The button's place on the board, once the code panel's height is known.</summary>
    Rect ButtonOnBoard => new(_buttonRect.X, CodeTop + _codeH + InfoGap + _buttonTop, _buttonRect.Width, ButtonH);

    // ------------------------------------------------------------------ hover and flashes

    void HoverAt(double y)
    {
        if (_round.Phase != SpotPhase.Playing || _revealing || _demo)
        {
            SetHover(null);
            return;
        }
        int line = (int)Math.Floor((y - CodePadY) / LineH) + 1;
        SetHover(line >= 1 && line <= _round.Current.Lines && !_round.Tried.Contains(line) ? line : null);
    }

    void SetHover(int? line)
    {
        if (_hoverLine == line) return;
        _hoverLine = line;
        var row = line is int l ? _rows.FirstOrDefault(r => r.Line == l) : null;
        _hover.IsVisible = _hoverEdge.IsVisible = row != null;
        if (row != null)
        {
            Canvas.SetTop(_hover, row.To);
            Canvas.SetTop(_hoverEdge, row.To);
        }
        Host.Wake();
    }

    /// <summary>A wrong line flashes red and shakes, then stays crossed out.</summary>
    void FlashWrong(int line)
    {
        if (_rows.FirstOrDefault(r => r.Line == line) is not { } row) return;
        SetHover(null);
        var red = Art.Safe(Red);
        byte rest = _palette.Light ? (byte)34 : (byte)40;
        Anims.Add(0.38, k =>
        {
            row.Back.Fill = Art.Brush(Color.FromArgb((byte)(170 - (170 - rest) * k), red.R, red.G, red.B));
            row.Shift.X = 7 * Math.Sin(k * Math.PI * 6) * (1 - k);
        }, Ease.Linear, () => row.Shift.X = 0);
    }

    /// <summary>The right line lights up green with a tick before the fix comes in.</summary>
    void FlashRight(int line)
    {
        if (_rows.FirstOrDefault(r => r.Line == line) is not { } row) return;
        SetHover(null);
        var green = Art.Safe(Green);
        row.Mark.Text = "✓";
        row.Mark.Foreground = Art.Brush(SpotBugSyntax.Readable(green, _palette.Back, 3));
        Anims.Add(RevealDelay - 0.02, k => row.Back.Fill = Art.Brush(Color.FromArgb((byte)(90 + 110 * Ease.Pulse(k)), green.R, green.G, green.B)), Ease.Linear);
    }

    /// <summary>Missed: the bug's lines pulse red twice, so the eye goes there before the fix comes in.</summary>
    void FlashBug()
    {
        SetHover(null);
        var red = Art.Safe(Red);
        foreach (var row in _rows.Where(r => r.Line > 0 && _round.Current.IsBug(r.Line)))
            Anims.Add(RevealDelay - 0.02, k => row.Back.Fill = Art.Brush(Color.FromArgb((byte)(40 + 150 * Ease.Pulse(k * 2 % 1)), red.R, red.G, red.B)), Ease.Linear);
    }

    // ------------------------------------------------------------------ theme

    void Paint()
    {
        var t = Themes.Current;
        _palette = SpotBugSyntax.PaletteFor(t);
        _paintedColorBlind = Art.ColorBlind;
        _inks.Clear();
        _back.Fill = Art.Brush(Color.FromArgb(240, t.Ink.R, t.Ink.G, t.Ink.B));
        _back.Stroke = Art.Brush(Color.FromArgb(150, t.Accent.R, t.Accent.G, t.Accent.B));
        foreach (var back in new[] { _modeBack, _newBack })
        {
            back.Fill = Art.Brush(Color.FromArgb(70, t.Accent.R, t.Accent.G, t.Accent.B));
            back.Stroke = Art.Brush(t.Accent);
        }
        foreach (var text in _langChips.Select(c => c.Text).Append(_modeChip).Append(_newChip)) text.Foreground = Art.Brush(t.HudFront);
        _progress.Foreground = Art.Brush(Art.Blend(t.HudFront, t.Ink, 0.25));
        _codeBack.Fill = Art.Brush(_palette.Back);
        _codeBack.Stroke = Art.Brush(Color.FromArgb(90, t.Accent.R, t.Accent.G, t.Accent.B));
        _codeBack.StrokeThickness = 1;
        _gutterBack.Fill = _gutterEdge.Fill = Art.Brush(_palette.Gutter);
        _hover.Fill = Art.Brush(Color.FromArgb(_palette.Light ? (byte)40 : (byte)48, t.Accent.R, t.Accent.G, t.Accent.B));
        _hoverEdge.Fill = Art.Brush(SpotBugSyntax.Readable(t.Accent, _palette.Back, 3));
    }

    public override void ThemeChanged()
    {
        Paint();
        _rowsSnippet = null; // the rows' colours come from the palette: build them again
        ShowRound(animate: false);
    }
}
