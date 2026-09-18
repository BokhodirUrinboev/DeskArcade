using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using DeskArcade.Engine;
using DeskArcade.Platform;

namespace DeskArcade;

/// <summary>Pick the modifier keys and the letter for each global shortcut. Opened from the tray menu.</summary>
public sealed class ShortcutsWindow : Window
{
    static ShortcutsWindow? _open;

    readonly OverlayWindow _overlay;
    readonly ComboBox _mods = new() { MinWidth = 200 };
    readonly TextBox[] _letters = new TextBox[3];
    readonly TextBlock _status;

    public static void ShowFor(OverlayWindow overlay)
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        _open = new ShortcutsWindow(overlay);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    ShortcutsWindow(OverlayWindow overlay)
    {
        _overlay = overlay;
        Title = L.T("Shortcuts");
        Width = 420;
        Height = 360;
        CanResize = false;
        Topmost = true;
        RequestedThemeVariant = ThemeVariant.Dark;
        Background = Art.Brush("#16181F");
        Icon = overlay.Icon;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var current = Shortcuts.Current;
        foreach (var m in Enum.GetValues<ShortcutModifiers>())
            _mods.Items.Add(new ComboBoxItem { Content = HotkeySet.ModifierLabelFor(m).TrimEnd('+'), Tag = m });
        _mods.SelectedIndex = (int)current.Modifiers;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), RowSpacing = 10 };
        AddRow(grid, 0, L.T("Modifier keys"), _mods);
        string[] names = { L.T("Show or hide the overlay"), L.T("Next game"), L.T("Bring to cursor") };
        for (int i = 0; i < 3; i++)
        {
            _letters[i] = new TextBox { Text = current.Keys[i].ToString(), MaxLength = 1, Width = 48, TextAlignment = TextAlignment.Center };
            AddRow(grid, i + 1, names[i], _letters[i]);
        }

        _status = new TextBlock { FontSize = 12, Foreground = Art.Brush("#AAB3C0"), TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = L.T("Save") };
        save.Click += (_, _) => Save();
        var reset = new Button { Content = L.T("Defaults") };
        reset.Click += (_, _) =>
        {
            _mods.SelectedIndex = (int)HotkeySet.Default.Modifiers;
            for (int i = 0; i < 3; i++) _letters[i].Text = HotkeySet.Default.Keys[i].ToString();
        };

        var panel = new StackPanel { Margin = new Thickness(22, 18), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = L.T("Shortcuts"), FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
        panel.Children.Add(grid);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, reset } });
        panel.Children.Add(_status);
        Content = panel;
    }

    static void AddRow(Grid grid, int row, string label, Control editor)
    {
        var text = new TextBlock { Text = label, Foreground = Art.Brush("#E6EAF0"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row);
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(text);
        grid.Children.Add(editor);
    }

    void Save()
    {
        string keys = string.Concat(_letters.Select(t => (t.Text ?? "").Trim()));
        if (!HotkeySet.Valid(keys))
        {
            _status.Text = L.T("Use three different letters, A to Z.");
            return;
        }
        var set = new HotkeySet((ShortcutModifiers)((ComboBoxItem)_mods.SelectedItem!).Tag!, keys.ToUpperInvariant());
        _status.Text = _overlay.ApplyShortcuts(set);
    }
}
