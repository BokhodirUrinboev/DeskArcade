using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using DeskArcade.Engine;
using DeskArcade.Office;

namespace DeskArcade;

public enum CoachKind { Eye, Stretch, Water, Breathe, DayEnd }

/// <summary>How a card ended: done (the time ran out, or the button said so), put off, or skipped.</summary>
public enum CoachResult { Done, Later, Skipped }

/// <summary>
/// A card in the lower middle of the screen that walks the player through a short break: twenty seconds of looking far
/// away, a one-minute stretch shown by a little figure to copy, a minute of box breathing with a circle that grows and
/// shrinks with the breath, a glass of water, or the day's summary at the end of the working day. The game waits under
/// it; it closes by itself when its time is up.
/// </summary>
public sealed class CoachCard
{
    const double CardWidth = 400, ArtSize = 124;

    readonly CoachKind _kind;
    readonly double _length;
    readonly Canvas _art = new() { Width = ArtSize, Height = ArtSize, ClipToBounds = true };
    readonly TextBlock _title, _hint, _count, _step;
    readonly Border _barFill = new() { Height = 5, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
    readonly double _barWidth;
    readonly Theme _theme = Themes.Current;
    double _t;
    bool _closed;

    // the breathing circle, the water, the stretching figure
    Ellipse? _breath;
    Rectangle? _water;
    readonly Dictionary<string, Line> _limbs = new();
    Ellipse? _head;

    public event Action<CoachResult>? Finished;

    public Border Root { get; }
    public Rect Area { get; }

    public CoachCard(CoachKind kind, OfficeDesk desk, Rect arena, IReadOnlyList<string> summary)
    {
        _kind = kind;
        _length = kind switch
        {
            CoachKind.Eye => EyeBreak.Seconds,
            CoachKind.Stretch => Stretches.Seconds,
            CoachKind.Breathe => Breathing.Seconds,
            CoachKind.Water => 40,
            _ => 60,
        };
        var t = _theme;
        _title = Text("", 20, FontWeight.Bold, Colors.White);
        _hint = Text("", 13, FontWeight.Normal, Color.Parse("#C9D1DC"));
        _count = Text("", 30, FontWeight.Black, t.Gold);
        _step = Text("", 11, FontWeight.SemiBold, Art.Blend(t.HudFront, t.HudBack, 0.4));
        _title.TextWrapping = _hint.TextWrapping = TextWrapping.Wrap;

        var words = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(_step);
        words.Children.Add(_title);
        words.Children.Add(_hint);
        if (kind == CoachKind.DayEnd)
            foreach (string line in summary) words.Children.Add(Text("· " + line, 12.5, FontWeight.Normal, Color.Parse("#E6EAF0")));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var grey = Color.FromRgb(90, 98, 112);
        switch (kind)
        {
            case CoachKind.Water:
                buttons.Children.Add(OfficeDesk.CardButton(L.T("I drank a glass"), t.Accent, () => Finish(CoachResult.Done)));
                buttons.Children.Add(OfficeDesk.CardButton(L.T("Later"), grey, () => Finish(CoachResult.Later)));
                break;
            case CoachKind.Stretch:
                buttons.Children.Add(OfficeDesk.CardButton(L.T("Later"), grey, () => Finish(CoachResult.Later)));
                buttons.Children.Add(OfficeDesk.CardButton(L.T("Skip"), grey, () => Finish(CoachResult.Skipped)));
                break;
            case CoachKind.DayEnd:
                buttons.Children.Add(OfficeDesk.CardButton(L.T("See you tomorrow"), t.Accent, () => Finish(CoachResult.Done)));
                break;
            case CoachKind.Breathe:
                buttons.Children.Add(OfficeDesk.CardButton(L.T("Stop"), grey, () => Finish(CoachResult.Skipped)));
                break;
            default:
                buttons.Children.Add(OfficeDesk.CardButton(L.T("Skip"), grey, () => Finish(CoachResult.Skipped)));
                break;
        }
        words.Children.Add(buttons);

        _barWidth = CardWidth - 40;
        var track = new Border { Height = 5, CornerRadius = new CornerRadius(3), Background = Art.Brush(Color.FromArgb(50, 255, 255, 255)), Width = _barWidth };
        _barFill.Background = Art.Brush(t.Accent);
        _barFill.Width = 0;

        var artHost = new Panel { Width = ArtSize, Height = ArtSize, VerticalAlignment = VerticalAlignment.Top };
        artHost.Children.Add(_art);
        if (kind is CoachKind.Eye or CoachKind.Breathe)
        {
            _count.HorizontalAlignment = HorizontalAlignment.Center;
            _count.VerticalAlignment = VerticalAlignment.Center;
            artHost.Children.Add(_count);
        }
        var row = new DockPanel();
        DockPanel.SetDock(artHost, Dock.Left);
        artHost.Margin = new Thickness(0, 0, 16, 0);
        row.Children.Add(artHost);
        row.Children.Add(words);
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(row);
        body.Children.Add(new Panel { Children = { track, _barFill } });

        Root = new Border
        {
            Width = CardWidth, Padding = new Thickness(20, 16), CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1.5),
            Background = Art.Brush(Color.FromArgb(246, t.Ink.R, t.Ink.G, t.Ink.B)), BorderBrush = Art.Brush(Color.FromArgb(170, t.Accent.R, t.Accent.G, t.Accent.B)),
            BoxShadow = BoxShadows.Parse("0 8 30 0 #66000000"), Child = body,
        };
        Root.PointerPressed += (_, e) => e.Handled = true; // the card takes its clicks; the game under it waits

        DrawArt(kind);
        Show(0);
        Root.Measure(Size.Infinity);
        double h = Root.DesiredSize.Height;
        double x = arena.Center.X - CardWidth / 2, y = Math.Clamp(arena.Top + arena.Height * 0.56 - h / 2, arena.Top + 8, Math.Max(arena.Top + 8, arena.Bottom - h - 12));
        Canvas.SetLeft(Root, x);
        Canvas.SetTop(Root, y);
        Area = new Rect(x, y, CardWidth, h);
        Root.Opacity = 0;
    }

    static TextBlock Text(string text, double size, FontWeight weight, Color color) => new()
    {
        Text = text, FontFamily = Fx.Font, FontSize = size, FontWeight = weight, Foreground = Art.Brush(color),
    };

    /// <summary>One frame; false once the card's time is up (it then finishes as done).</summary>
    public bool Update(double dt)
    {
        if (_closed) return false;
        _t += dt;
        Root.Opacity = Math.Min(1, _t / 0.25);
        if (_t >= _length)
        {
            Finish(_kind == CoachKind.Water ? CoachResult.Later : CoachResult.Done);
            return false;
        }
        Show(_t);
        return true;
    }

    /// <summary>Takes the card away; <paramref name="result"/> says how, when it is not finished already.</summary>
    public void Close(CoachResult? result)
    {
        if (result is CoachResult r) Finish(r);
        _closed = true;
    }

    void Finish(CoachResult result)
    {
        if (_closed) return;
        _closed = true;
        Finished?.Invoke(result);
    }

    // ------------------------------------------------------------------ what the card says, second by second

    void Show(double t)
    {
        _barFill.Width = _barWidth * Math.Clamp(t / _length, 0, 1);
        switch (_kind)
        {
            case CoachKind.Eye:
                _step.Text = L.T("EYE BREAK · 20-20-20");
                _title.Text = L.T("Look far away");
                _hint.Text = L.T("find something at least 6 metres (20 feet) off, out of a window if you can, and rest your eyes on it");
                _count.Text = ((int)Math.Ceiling(EyeBreak.Seconds - t)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case CoachKind.Breathe:
                var b = Breathing.At(t);
                _step.Text = L.F("BREATHING · ROUND {0} OF {1}", b.Round, Breathing.Rounds);
                _title.Text = L.T(Breathing.Phases[b.Phase]);
                _hint.Text = L.T("in, hold, out, hold: four seconds each, slowly, through the nose");
                _count.Text = b.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (_breath != null)
                {
                    double r = 20 + 38 * b.Size;
                    _breath.Width = _breath.Height = r * 2;
                    Canvas.SetLeft(_breath, ArtSize / 2 - r);
                    Canvas.SetTop(_breath, ArtSize / 2 - r);
                }
                break;
            case CoachKind.Stretch:
                var (step, k) = Stretches.At(t);
                _step.Text = L.F("STRETCH · {0} OF {1}", step + 1, Stretches.Steps.Length);
                _title.Text = L.T(Stretches.Steps[step].Title);
                _hint.Text = L.T(Stretches.Steps[step].Hint);
                Pose(step, k, t);
                break;
            case CoachKind.Water:
                _step.Text = L.T("WATER");
                _title.Text = L.T("Time for a glass of water");
                _hint.Text = L.T("a little and often keeps the headache and the afternoon slump away");
                if (_water != null)
                {
                    double level = 0.25 + 0.55 * (0.5 - 0.5 * Math.Cos(Math.Min(1, t / 2) * Math.PI));
                    double top = 34 + 70 * (1 - level);
                    _water.Height = 104 - top;
                    Canvas.SetTop(_water, top);
                }
                break;
            case CoachKind.DayEnd:
                _step.Text = L.T("END OF THE DAY");
                _title.Text = L.T("That's the day!");
                _hint.Text = L.T("well done · leave the rest for tomorrow");
                break;
        }
    }

    // ------------------------------------------------------------------ pictures

    void DrawArt(CoachKind kind)
    {
        var t = _theme;
        switch (kind)
        {
            case CoachKind.Eye:
                // a window onto hills far away
                _art.Children.Add(Art.At(new Rectangle
                {
                    Width = ArtSize, Height = ArtSize, RadiusX = 14, RadiusY = 14,
                    Fill = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        GradientStops = { new GradientStop(Color.Parse("#2E5E8C"), 0), new GradientStop(Color.Parse("#7FB2D9"), 1) },
                    },
                }, 0, 0));
                _art.Children.Add(Art.PathOf("M0,90 L22,70 L40,82 L64,58 L88,80 L104,68 L124,84 L124,124 L0,124 Z", Art.Brush(Color.Parse("#3F6E4F"))));
                _art.Children.Add(Art.PathOf("M0,104 L30,92 L58,100 L90,88 L124,98 L124,124 L0,124 Z", Art.Brush(Color.Parse("#2F5540"))));
                _art.Children.Add(Art.Circle(92, 30, 10, Art.Brush(Color.FromArgb(200, 255, 236, 170))));
                _art.Children.Add(Art.At(new Rectangle { Width = ArtSize, Height = ArtSize, RadiusX = 14, RadiusY = 14, Stroke = Art.Brush(Color.Parse("#DDE3EA")), StrokeThickness = 4 }, 0, 0));
                _art.Children.Add(Art.PathOf("M62,2 L62,122 M2,62 L122,62", null, Art.Brush(Color.FromArgb(150, 221, 227, 234)), 3));
                _art.Children.Add(Art.Circle(ArtSize / 2, ArtSize / 2, 24, Art.Brush(Color.FromArgb(150, 12, 20, 34)))); // behind the count
                _count.Foreground = Brushes.White;
                break;
            case CoachKind.Breathe:
                _art.Children.Add(Art.Circle(ArtSize / 2, ArtSize / 2, 58, null, Art.Brush(Color.FromArgb(90, t.Accent.R, t.Accent.G, t.Accent.B)), 2));
                _breath = Art.Circle(ArtSize / 2, ArtSize / 2, 20, Art.Brush(Color.FromArgb(120, t.Accent.R, t.Accent.G, t.Accent.B)));
                _art.Children.Add(_breath);
                break;
            case CoachKind.Water:
                var glass = Art.Brush(Color.FromArgb(200, 225, 235, 245));
                _water = new Rectangle { Width = 60, Height = 40, Fill = Art.Brush(Color.FromArgb(170, 90, 170, 255)) };
                var clip = new Canvas { Width = ArtSize, Height = ArtSize, Clip = Geometry.Parse("M34,24 L90,24 L84,104 L40,104 Z") };
                clip.Children.Add(Art.At(_water, 32, 60));
                _art.Children.Add(clip);
                _art.Children.Add(Art.PathOf("M34,24 L90,24 L84,104 L40,104 Z", null, glass, 3));
                _art.Children.Add(Art.PathOf("M44,34 L48,94", null, Art.Brush(Color.FromArgb(110, 255, 255, 255)), 3));
                break;
            case CoachKind.DayEnd:
                _art.Children.Add(Art.At(new Rectangle { Width = ArtSize, Height = ArtSize, RadiusX = 14, RadiusY = 14, Fill = Art.Brush(Color.Parse("#1B2340")) }, 0, 0));
                _art.Children.Add(Art.Circle(78, 44, 22, Art.Brush(Color.Parse("#F6E7A8"))));
                _art.Children.Add(Art.Circle(88, 36, 20, Art.Brush(Color.Parse("#1B2340"))));
                foreach (var (x, y, r) in new[] { (24.0, 28.0, 2.0), (40, 60, 1.5), (18, 88, 2), (100, 90, 1.5), (60, 20, 1.2), (104, 70, 1.8) })
                    _art.Children.Add(Art.Circle(x, y, r, Brushes.White));
                _art.Children.Add(Art.PathOf("M0,104 L124,104 L124,124 L0,124 Z", Art.Brush(Color.Parse("#131931"))));
                break;
            case CoachKind.Stretch:
                _art.Children.Add(Art.At(new Rectangle { Width = ArtSize, Height = ArtSize, RadiusX = 14, RadiusY = 14, Fill = Art.Brush(Color.FromArgb(40, 255, 255, 255)) }, 0, 0));
                var chair = Art.Brush(Color.FromArgb(90, 255, 255, 255));
                _art.Children.Add(Art.PathOf("M40,92 L80,92 M44,92 L44,118 M76,92 L76,118", null, chair, 4));
                var ink = Art.Brush(t.Accent);
                foreach (string limb in new[] { "torso", "shoulders", "armL", "foreL", "armR", "foreR", "fingers", "thighL", "shinL", "thighR", "shinR" })
                {
                    var line = new Line { Stroke = ink, StrokeThickness = limb == "fingers" ? 3 : 5, StrokeLineCap = PenLineCap.Round };
                    _limbs[limb] = line;
                    _art.Children.Add(line);
                }
                _head = new Ellipse { Width = 22, Height = 22, Fill = ink };
                _art.Children.Add(_head);
                break;
        }
    }

    /// <summary>The figure's pose for a move of the stretch, <paramref name="k"/> through it, at time <paramref name="t"/>.</summary>
    void Pose(int step, double k, double t)
    {
        if (_head == null) return;
        bool standing = step == 3;
        Vec2 hips = new(62, standing ? 78 : 88), neck = new(62, standing ? 38 : 48);
        Vec2 ls = new(48, neck.Y + 4), rs = new(76, neck.Y + 4);
        double headTilt = 0;
        Vec2 le, lh, re, rh, fingerFrom = default, fingerTo = default;
        bool fingers = false;
        switch (step)
        {
            case 0: // shoulder rolls: the shoulders go round, the arms hang
                double a = t * Math.PI * 2 / 3;
                var roll = new Vec2(Math.Cos(a) * 2.5, Math.Sin(a) * 4 - 2);
                ls += roll;
                rs += new Vec2(-roll.X, roll.Y);
                le = ls + new Vec2(-4, 17); lh = le + new Vec2(1, 17);
                re = rs + new Vec2(4, 17); rh = re + new Vec2(-1, 17);
                break;
            case 1: // head tilts, one side then the other
                headTilt = Math.Sin(t * Math.PI * 2 / 6) * 0.5;
                le = ls + new Vec2(-4, 17); lh = le + new Vec2(1, 17);
                re = rs + new Vec2(4, 17); rh = re + new Vec2(-1, 17);
                break;
            case 2: // an arm out, palm up, the other hand pulling the fingers back; sides swap halfway
                bool right = k < 0.5;
                double pull = 0.5 + 0.5 * Math.Sin(t * Math.PI);
                Vec2 s = right ? rs : ls, o = right ? ls : rs;
                double dir = right ? 1 : -1;
                Vec2 elbow = s + new Vec2(dir * 17, 0), hand = s + new Vec2(dir * 33, 0);
                fingerFrom = hand;
                fingerTo = hand + new Vec2(dir * (3 - 3 * pull), -9 - 3 * pull);
                fingers = true;
                Vec2 helper = hand + new Vec2(-dir * 3, -7 - 2 * pull), helperElbow = o + new Vec2(dir * 12, 14);
                if (right) { re = elbow; rh = hand; le = helperElbow; lh = helper; }
                else { le = elbow; lh = hand; re = helperElbow; rh = helper; }
                break;
            default: // standing, reaching up and letting the arms fall
                double up = 0.5 - 0.5 * Math.Cos(t * Math.PI * 2 / 5);
                le = ls + new Vec2(-6, 17 - 34 * up); lh = ls + new Vec2(-7, 34 - 70 * up);
                re = rs + new Vec2(6, 17 - 34 * up); rh = rs + new Vec2(7, 34 - 70 * up);
                break;
        }
        var head = neck + new Vec2(Math.Sin(headTilt) * 13, -Math.Cos(headTilt) * 13);
        Canvas.SetLeft(_head, head.X - 11);
        Canvas.SetTop(_head, head.Y - 11);
        Set("torso", neck, hips);
        Set("shoulders", ls, rs);
        Set("armL", ls, le);
        Set("foreL", le, lh);
        Set("armR", rs, re);
        Set("foreR", re, rh);
        _limbs["fingers"].IsVisible = fingers;
        if (fingers) Set("fingers", fingerFrom, fingerTo);
        if (standing)
        {
            Set("thighL", hips, hips + new Vec2(-7, 22));
            Set("shinL", hips + new Vec2(-7, 22), hips + new Vec2(-8, 44));
            Set("thighR", hips, hips + new Vec2(7, 22));
            Set("shinR", hips + new Vec2(7, 22), hips + new Vec2(8, 44));
        }
        else
        {
            Set("thighL", hips, hips + new Vec2(-12, 4));
            Set("shinL", hips + new Vec2(-12, 4), hips + new Vec2(-13, 28));
            Set("thighR", hips, hips + new Vec2(12, 4));
            Set("shinR", hips + new Vec2(12, 4), hips + new Vec2(13, 28));
        }
    }

    void Set(string limb, Vec2 from, Vec2 to)
    {
        var line = _limbs[limb];
        line.StartPoint = from.ToPoint();
        line.EndPoint = to.ToPoint();
    }
}
