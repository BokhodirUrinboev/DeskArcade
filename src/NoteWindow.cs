using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using DeskArcade.Engine;
using DeskArcade.Office;

namespace DeskArcade;

/// <summary>Writes or edits a sticky note, with an optional reminder. Opened from At work → New sticky note, or by clicking a note.</summary>
public sealed class NoteWindow : Window
{
    static NoteWindow? _open;

    /// <summary>The reminder choices: minutes from now, 0 for none, -1 for the end of the working day.</summary>
    static readonly int[] Reminders = { 0, 15, 30, 60, 120, -1 };

    readonly OfficeDesk _desk;
    readonly StickyNote? _note;
    readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 240, Height = 110, Watermark = "" };
    readonly ComboBox _remind = new() { MinWidth = 220 };

    public static void ShowFor(OverlayWindow overlay, OfficeDesk desk, StickyNote? note)
    {
        _open?.Close();
        _open = new NoteWindow(overlay, desk, note);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    NoteWindow(OverlayWindow overlay, OfficeDesk desk, StickyNote? note)
    {
        _desk = desk;
        _note = note;
        Title = note == null ? L.T("New sticky note") : L.T("Sticky note");
        Width = 380;
        Height = 330;
        CanResize = false;
        Topmost = true;
        RequestedThemeVariant = ThemeVariant.Dark;
        Background = Art.Brush("#16181F");
        Icon = overlay.Icon;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _text.Text = note?.Text ?? "";
        _text.Watermark = L.T("Call the bank back · send the report · buy milk");
        var end = EndOfDay.ParseTime(overlay.Settings.WorkEnd);
        foreach (int minutes in Reminders)
        {
            if (minutes == -1 && end == null) continue;
            string label = minutes switch
            {
                0 => L.T("No reminder"),
                -1 => L.F("At the end of the day ({0})", EndOfDay.FormatTime(end!.Value)),
                < 60 => L.F("In {0} minutes", minutes),
                _ => L.F("In {0} hours", minutes / 60),
            };
            _remind.Items.Add(new ComboBoxItem { Content = label, Tag = minutes });
        }
        _remind.SelectedIndex = 0;
        if (note?.Due is DateTime due)
        {
            _remind.Items.Insert(1, new ComboBoxItem
            {
                Content = L.F("As set ({0})", TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(due, DateTimeKind.Utc), TimeZoneInfo.Local).ToString("HH:mm")),
                Tag = int.MinValue,
            });
            _remind.SelectedIndex = 1;
        }

        var save = new Button { Content = L.T("Save"), IsDefault = true };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(save);
        if (note != null)
        {
            var remove = new Button { Content = L.T("Take it down") };
            remove.Click += (_, _) =>
            {
                _desk.DoneNote(note);
                Close();
            };
            buttons.Children.Add(remove);
        }
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20, 16), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
        panel.Children.Add(_text);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { new TextBlock { Text = L.T("Remind me"), Foreground = Art.Brush("#E6EAF0"), VerticalAlignment = VerticalAlignment.Center }, _remind },
        });
        panel.Children.Add(buttons);
        Content = panel;
        Opened += (_, _) => _text.Focus();
    }

    void Save()
    {
        DateTime? due = null;
        int choice = (_remind.SelectedItem as ComboBoxItem)?.Tag is int m ? m : 0;
        if (choice == int.MinValue) due = _note?.Due;
        else if (choice > 0) due = DateTime.UtcNow.AddMinutes(choice);
        else if (choice == -1 && EndOfDay.ParseTime(_desk.WorkEnd) is TimeOnly end)
        {
            var local = DateTime.Today + end.ToTimeSpan();
            if (local < DateTime.Now) local = local.AddDays(1);
            due = TimeZoneInfo.ConvertTimeToUtc(local);
        }
        string text = _text.Text ?? "";
        if (_note == null) _desk.AddNote(text, due);
        else _desk.UpdateNote(_note, text, due);
        Close();
    }
}
