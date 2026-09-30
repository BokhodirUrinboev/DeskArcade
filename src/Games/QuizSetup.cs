using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Quiz Night's part of the room setup window: which pack the quiz asks from (the mixed bag, the four built in, the
/// packs in the quiz folder, or a file picked here) and how many questions. A pack of your own with mistakes is not
/// used; its mistakes are listed with their line numbers instead.
/// </summary>
public sealed partial class QuizGame
{
    static readonly int[] QuestionCounts = { 5, 10, 15, 20 };

    public Control OptionsPanel(Window owner)
    {
        var packs = new ComboBox { MinWidth = 220 };
        var count = new ComboBox { MinWidth = 70 };
        var note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Art.Brush("#AAB3C0") };
        var list = new List<QuizPack>();

        void Fill()
        {
            list.Clear();
            list.AddRange(Packs());
            packs.Items.Clear();
            foreach (var p in list) packs.Items.Add(p.Own ? L.F("{0} (your pack)", PackTitle(p)) : PackTitle(p));
            packs.SelectedIndex = Math.Max(0, list.FindIndex(p => p.Id == _packId));
        }

        void Describe(IReadOnlyList<QuizPackError>? errors = null)
        {
            if (errors is { Count: > 0 })
            {
                note.Foreground = Art.Brush("#FF8A8A");
                note.Text = L.T("That pack has mistakes, so it isn't used:") + "\n" + string.Join("\n", errors.Select(e => e.Message));
                return;
            }
            var pack = CurrentPack;
            note.Foreground = Art.Brush("#AAB3C0");
            note.Text = (pack.Own ? L.F("Your pack: {0} questions. The host sends them to everyone, one at a time.", pack.Items.Count) + "\n" : "") +
                        L.T("A pack of your own is a text file: \"Q:\" before each question, then its four answers, \"+\" before the right one and \"-\" before the others.");
        }

        Fill();
        foreach (int n in QuestionCounts) count.Items.Add(L.F("{0} questions", n));
        count.SelectedIndex = Math.Max(0, Array.IndexOf(QuestionCounts, _questionCount));
        packs.SelectionChanged += (_, _) =>
        {
            if (packs.SelectedIndex < 0 || packs.SelectedIndex >= list.Count) return;
            UsePack(list[packs.SelectedIndex]);
            Describe();
        };
        count.SelectionChanged += (_, _) =>
        {
            if (count.SelectedIndex < 0) return;
            _questionCount = QuestionCounts[count.SelectedIndex];
            Changed();
        };

        var open = new Button { Content = L.T("Open a pack file…") };
        open.Click += async (_, _) =>
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L.T("Pick a quiz pack"), AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(L.T("Quiz pack")) { Patterns = new[] { "*.txt" } }, FilePickerFileTypes.All },
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not string path) return;
            var (pack, errors) = QuizPack.LoadFile(path);
            if (pack == null)
            {
                Describe(errors);
                return;
            }
            UsePack(pack);
            Fill();
            Describe();
        };
        var folder = new Button { Content = L.T("Open the quiz folder") };
        folder.Click += (_, _) =>
        {
            QuizPack.EnsureFolder();
            UpdateChecker.OpenFolder(QuizPack.Folder);
        };
        var refresh = new Button { Content = L.T("Look again") };
        refresh.Click += (_, _) =>
        {
            Fill();
            // a pack in the folder with mistakes is left out of the list: say why
            var broken = QuizPack.FolderFiles().Select(f => (File: f, Result: QuizPack.LoadFile(f))).FirstOrDefault(x => x.Result.Pack == null);
            if (broken.File != null)
                Describe(new[] { new QuizPackError(0, System.IO.Path.GetFileName(broken.File)) }.Concat(broken.Result.Errors).ToList());
            else Describe();
        };
        Describe();

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Text = L.T("Questions from"), FontSize = 13, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center }, packs, count },
        });
        panel.Children.Add(new WrapPanel { Children = { open, folder, refresh }, ItemSpacing = 8, LineSpacing = 6 });
        panel.Children.Add(note);
        return panel;
    }
}
