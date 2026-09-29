using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade;

/// <summary>
/// Sticky notes on the desktop: a few lines of text on a paper square, dragged anywhere, riding along on a window top
/// when dropped on one (and falling to the taskbar when that window goes), with an optional reminder. Click one to
/// edit it, tick it when it is done.
/// </summary>
public sealed partial class OfficeDesk
{
    const int MaxNotes = 8;
    const double NoteWidth = 172, SnapDistance = 18;

    static readonly Color Paper = Color.FromRgb(255, 231, 128), PaperEdge = Color.FromRgb(222, 190, 70), PaperInk = Color.FromRgb(60, 48, 24);

    sealed class NoteView
    {
        public required StickyNote Note;
        public required Border Root;
        public required TextBlock Due;
        public IntPtr Window;          // the window it rides on, or zero
        public Rect Area;
    }

    readonly List<NoteView> _notes = new();
    NoteView? _dragging;
    Vec2 _dragOffset, _dragFrom;
    bool _dragMoved, _notesMoved;
    int _platformGen = -1;

    public int NoteCount => S.Notes.Count;

    /// <summary>The overlay moved to another monitor or changed size: the notes go back to their places on it.</summary>
    public void Layout()
    {
        if (_dragging != null) return;
        foreach (var view in _notes)
            Place(view, new Vec2(_w.Arena.Left + view.Note.X, _w.Arena.Top + view.Note.Y)); // one on a window follows it from there
    }

    void BuildNotes()
    {
        foreach (var view in _notes) _w.OfficeLayer.Children.Remove(view.Root);
        _notes.Clear();
        foreach (var note in S.Notes.Take(MaxNotes)) _notes.Add(BuildNote(note));
        _w.HitShapesChanged();
    }

    NoteView BuildNote(StickyNote note)
    {
        var text = new TextBlock
        {
            Text = note.Text, FontFamily = Fx.Font, FontSize = 13.5, Foreground = Art.Brush(PaperInk), TextWrapping = TextWrapping.Wrap,
            MaxHeight = 150, TextTrimming = TextTrimming.WordEllipsis, Margin = new Thickness(0, 6, 0, 0),
        };
        var due = new TextBlock { FontFamily = Fx.Font, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Art.Brush(Color.FromRgb(150, 96, 20)), VerticalAlignment = VerticalAlignment.Center };
        var done = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Art.Brush(Color.FromArgb(40, 60, 48, 24)),
            Cursor = new Cursor(StandardCursorType.Hand), HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock { Text = "✓", FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Art.Brush(PaperInk), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        ToolTip.SetTip(done, L.T("Done: take the note down"));
        var bottom = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(done, Dock.Right);
        bottom.Children.Add(done);
        bottom.Children.Add(due);
        var body = new StackPanel();
        body.Children.Add(text);
        body.Children.Add(bottom);
        var tape = new Border { Width = 46, Height = 12, Background = Art.Brush(Color.FromArgb(120, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -16, 0, 0) };
        var root = new Border
        {
            Width = NoteWidth, Padding = new Thickness(11, 10, 9, 8), CornerRadius = new CornerRadius(2, 2, 10, 2),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Paper, 0), new GradientStop(Art.Blend(Paper, PaperEdge, 0.45), 1) },
            },
            BoxShadow = BoxShadows.Parse("2 4 10 0 #55000000"), Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new Panel { Children = { body, tape } },
            RenderTransformOrigin = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            RenderTransform = new RotateTransform((note.Id[0] % 5 - 2) * 0.7), // not quite straight, like a real one
        };
        var view = new NoteView { Note = note, Root = root, Due = due };
        ShowDue(view);
        done.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            DoneNote(note);
        };
        root.PointerPressed += (_, e) => NotePressed(view, e);
        root.PointerMoved += (_, e) => NoteMoved(view, e);
        root.PointerReleased += (_, e) => NoteReleased(view, e);
        root.PointerCaptureLost += (_, _) => EndDrag(view, dropped: true);
        _w.OfficeLayer.Children.Add(root);
        Place(view, new Vec2(_w.Arena.Left + note.X, _w.Arena.Top + note.Y));
        return view;
    }

    static void ShowDue(NoteView view)
    {
        view.Due.Text = view.Note.Due is DateTime due
            ? "⏰ " + TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(due, DateTimeKind.Utc), TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture)
            : "";
    }

    /// <summary>Puts a note at a spot (its top-left corner), kept inside the arena.</summary>
    void Place(NoteView view, Vec2 at)
    {
        view.Root.Measure(Size.Infinity);
        double w = NoteWidth, h = Math.Max(60, view.Root.DesiredSize.Height);
        var a = _w.Arena;
        double x = Math.Clamp(at.X, a.Left, Math.Max(a.Left, a.Right - w)), y = Math.Clamp(at.Y, a.Top, Math.Max(a.Top, a.Bottom - h));
        Canvas.SetLeft(view.Root, x);
        Canvas.SetTop(view.Root, y);
        view.Area = new Rect(x, y, w, h);
    }

    void CollectNoteShapes(List<HitShape> into)
    {
        foreach (var view in _notes) into.Add(HitShape.Box(view.Area));
    }

    /// <summary>Notes riding on window tops follow their windows; one whose window went away drops to the taskbar.</summary>
    bool UpdateNotes(double dt)
    {
        var platforms = _w.Platforms;
        if (platforms.Generation == _platformGen || !platforms.Enabled) return false;
        _platformGen = platforms.Generation;
        bool moved = false;
        foreach (var view in _notes)
        {
            if (view.Window == IntPtr.Zero || view == _dragging) continue;
            if (!platforms.Items.Any(p => p.Hwnd == view.Window))
            {
                view.Window = IntPtr.Zero;
                Place(view, new Vec2(view.Area.X, _w.Arena.Bottom - view.Area.Height));
                Remember(view);
                moved = true;
                continue;
            }
            var d = platforms.DeltaOf(view.Window);
            if (d.X == 0 && d.Y == 0) continue;
            Place(view, new Vec2(view.Area.X + d.X, view.Area.Y + d.Y));
            Remember(view);
            moved = true;
        }
        if (moved) _w.HitShapesChanged();
        return false;
    }

    /// <summary>Notes where the note is now; the settings are written once a second at most (a window with notes on it can be dragged).</summary>
    void Remember(NoteView view)
    {
        view.Note.X = view.Area.X - _w.Arena.Left;
        view.Note.Y = view.Area.Y - _w.Arena.Top;
        _notesMoved = true;
    }

    void SaveNotesIfMoved()
    {
        if (!_notesMoved) return;
        _notesMoved = false;
        _w.SaveSettings();
    }

    /// <summary>The overlay went away mid-drag: the note stays where it got to, and the overlay stops taking the whole mouse.</summary>
    void CancelDrag()
    {
        if (_dragging is not NoteView view) return;
        _dragging = null;
        if (_dragMoved) Drop(view);
        _w.HitShapesChanged();
    }

    void NotePressed(NoteView view, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(view.Root).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        _dragging = view;
        _dragMoved = false;
        _dragFrom = e.GetPosition(_w.OfficeLayer);
        _dragOffset = new Vec2(view.Area.X, view.Area.Y) - _dragFrom;
        e.Pointer.Capture(view.Root);
        _w.HitShapesChanged();
    }

    void NoteMoved(NoteView view, PointerEventArgs e)
    {
        if (_dragging != view) return;
        Vec2 p = e.GetPosition(_w.OfficeLayer);
        if (!_dragMoved && (p - _dragFrom).Length > 4) _dragMoved = true;
        if (_dragMoved) Place(view, p + _dragOffset);
    }

    void NoteReleased(NoteView view, PointerReleasedEventArgs e)
    {
        if (_dragging != view) return;
        e.Handled = true;
        bool click = !_dragMoved;
        e.Pointer.Capture(null);
        EndDrag(view, dropped: true);
        if (click) EditNote(view.Note);
    }

    void EndDrag(NoteView view, bool dropped)
    {
        if (_dragging != view) return;
        _dragging = null;
        if (dropped && _dragMoved) Drop(view);
        _w.HitShapesChanged();
    }

    /// <summary>Let go near a window top, the note sits on it and rides along; anywhere else it stays where it is.</summary>
    void Drop(NoteView view)
    {
        view.Window = IntPtr.Zero;
        var platforms = _w.Platforms;
        if (platforms.Enabled)
        {
            double bottom = view.Area.Bottom, cx = view.Area.Center.X;
            foreach (var p in platforms.Items)
            {
                if (cx < p.X1 || cx > p.X2 || Math.Abs(bottom - p.Y) > SnapDistance) continue;
                view.Window = p.Hwnd;
                Place(view, new Vec2(view.Area.X, p.Y - view.Area.Height));
                break;
            }
        }
        _platformGen = platforms.Generation;
        Remember(view);
    }

    // ------------------------------------------------------------------ adding, editing, done

    public void NewNote()
    {
        if (!_w.OverlayVisible) _w.SetOverlayVisible(true);
        NoteWindow.ShowFor(_w, this, null);
    }

    void EditNote(StickyNote note) => NoteWindow.ShowFor(_w, this, note);

    /// <summary>Adds a note (from the note window or "deskarcade --note"), cascading from beside the scoreboard.</summary>
    public void AddNote(string text, DateTime? dueUtc)
    {
        text = Clean(text);
        if (text.Length == 0) return;
        if (S.Notes.Count >= MaxNotes)
        {
            Say(L.F("{0} notes is the most", MaxNotes), L.T("tick one off to make room"), Gold, null);
            return;
        }
        var a = _w.Arena;
        var hud = _w.HudBounds;
        int i = S.Notes.Count;
        double x = Math.Clamp(hud.Left - NoteWidth - 30 - i * 22, 0, Math.Max(0, a.Width - NoteWidth)), y = Math.Clamp(hud.Top + 30 + i * 26, 0, Math.Max(0, a.Height - 120));
        var note = new StickyNote { Text = text, Due = dueUtc, X = x, Y = y };
        S.Notes.Add(note);
        _w.SaveSettings();
        var view = BuildNote(note);
        _notes.Add(view);
        if (!_w.OverlayVisible) _w.SetOverlayVisible(true);
        PopIn(view);
        _w.Sound.Play("pop", 0.5);
        _w.HitShapesChanged();
        _w.RefreshTray();
    }

    public void UpdateNote(StickyNote note, string text, DateTime? dueUtc)
    {
        text = Clean(text);
        if (text.Length == 0)
        {
            DoneNote(note, quietly: true);
            return;
        }
        note.Text = text;
        note.Due = dueUtc;
        _w.SaveSettings();
        if (_notes.FirstOrDefault(v => v.Note == note) is { } view)
        {
            _w.OfficeLayer.Children.Remove(view.Root);
            _notes.Remove(view);
            var fresh = BuildNote(note);
            fresh.Window = view.Window;
            _notes.Add(fresh);
        }
        _w.HitShapesChanged();
    }

    static string Clean(string text)
    {
        var chars = text.Trim().Where(c => c == '\n' || !char.IsControl(c)).Take(240).ToArray();
        return new string(chars).Replace("\r", "").Trim();
    }

    /// <summary>Takes a note down: it tips over and fades; a ticked-off note counts toward "Note to self".</summary>
    public void DoneNote(StickyNote note, bool quietly = false)
    {
        S.Notes.Remove(note);
        _w.SaveSettings();
        if (!quietly) _w.Stats.Add("work.notes");
        if (_notes.FirstOrDefault(v => v.Note == note) is not { } view) return;
        _notes.Remove(view);
        if (_dragging == view) _dragging = null;
        var root = view.Root;
        var turn = new RotateTransform();
        var drop = new TranslateTransform();
        root.RenderTransform = new TransformGroup { Children = { turn, drop } };
        if (!quietly) _w.Sound.Play("whoosh", 0.4);
        _w.Fx.Anims.Add(Fx.ReducedMotion ? 0.01 : 0.45, k =>
        {
            turn.Angle = 25 * k;
            drop.Y = 60 * k * k;
            root.Opacity = 1 - k;
        }, Ease.InQuad, () => _w.OfficeLayer.Children.Remove(root));
        _w.HitShapesChanged();
        _w.RefreshTray();
    }

    void PopIn(NoteView view)
    {
        if (Fx.ReducedMotion) return;
        var root = view.Root;
        var baseTransform = root.RenderTransform;
        var scale = new ScaleTransform(0.3, 0.3);
        root.RenderTransform = new TransformGroup { Children = { scale } };
        root.Opacity = 0;
        _w.Fx.Anims.Add(0.3, k =>
        {
            scale.ScaleX = scale.ScaleY = 0.3 + 0.7 * Ease.OutBack(k);
            root.Opacity = Math.Min(1, k * 2);
        }, Ease.Linear, () =>
        {
            root.RenderTransform = baseTransform;
            root.Opacity = 1;
        });
    }

    void Wiggle(NoteView view)
    {
        if (Fx.ReducedMotion) return;
        var root = view.Root;
        var baseTransform = root.RenderTransform;
        var turn = new RotateTransform();
        root.RenderTransform = turn;
        _w.Fx.Anims.Add(1.2, k => turn.Angle = Math.Sin(k * Math.PI * 8) * 6 * (1 - k), Ease.Linear, () => root.RenderTransform = baseTransform);
    }

    void StepNotes(DateTime utc)
    {
        foreach (var note in S.Notes.ToList())
        {
            if (note.Due is not DateTime due || DateTime.SpecifyKind(due, DateTimeKind.Utc) > utc) continue;
            note.Due = null;
            _w.SaveSettings();
            Say(L.T("Reminder"), Short(note.Text.Replace('\n', ' '), 60), Gold, "attention", 0.8);
            if (_notes.FirstOrDefault(v => v.Note == note) is { } view)
            {
                ShowDue(view);
                Wiggle(view);
            }
        }
    }
}
