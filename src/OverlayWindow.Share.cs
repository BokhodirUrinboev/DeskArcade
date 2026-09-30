using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DeskArcade;

/// <summary>
/// ☰ or tray → Share: a line about the last result for Teams, Slack or Telegram (the game's own
/// <see cref="Engine.MiniGame.ShareText"/>, else the daily challenge, else the best score), or a picture of the game,
/// both put on the clipboard. Nothing is sent anywhere: pasting is up to the player.
/// </summary>
public sealed partial class OverlayWindow
{
    /// <summary>Where the pictures go too, so there is a file to attach when the clipboard will not paste one.</summary>
    static string PicturesFolder
    {
        get
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.Create);
            return Path.Combine(string.IsNullOrEmpty(pictures) ? Settings.DataDirectory : pictures, "Desk Arcade");
        }
    }

    /// <summary>The line to share now, signed with where Desk Arcade comes from.</summary>
    public string ShareLine()
    {
        string line = Current?.ShareText ?? DailyShareLine() ?? BestShareLine() ?? L.T("Playing Desk Arcade while it builds");
        return line.TrimEnd() + "\n" + L.T("Desk Arcade · games on your desktop while it builds") + " · github.com/BokhodirUrinboev/DeskArcade";
    }

    /// <summary>The daily challenge, once it is done today: "Desk Arcade daily · 2026-10-12: Make 15 baskets in Hoops ✓ · 4 days in a row".</summary>
    string? DailyShareLine()
    {
        var day = Daily.Today;
        if (!Daily.Done(day)) return null;
        var c = Daily.Current(day);
        string line = L.F("Desk Arcade daily · {0}: {1} ✓", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), L.F(c.Text, c.Target));
        int streak = Daily.Streak(day);
        return streak > 1 ? line + " · " + L.F("{0} days in a row", streak) : line;
    }

    /// <summary>The current game's best, as the scoreboard shows it ("Hoops · Best 48"), when there is one.</summary>
    string? BestShareLine()
    {
        if (Current == null) return null;
        string best = Current.Hud.Best;
        return best.IndexOfAny("0123456789".ToCharArray()) < 0 ? null : L.F("{0} · {1}", L.T(Current.Title), best);
    }

    /// <summary>☰ or tray → Share → Copy the result as text.</summary>
    public async void ShareResult()
    {
        string text = ShareLine();
        try
        {
            if (Clipboard == null) return;
            await Clipboard.SetTextAsync(text);
            Stats.Add("share.lines");
            SetOverlayVisible(true);
            Notice(L.T("Copied · paste it into Teams, Slack or Telegram"), text.Split('\n')[0], Color.FromRgb(255, 209, 102));
        }
        catch (Exception)
        {
            // the clipboard is busy: nothing to do but try again
        }
    }

    /// <summary>
    /// ☰ or tray → Share → Copy a picture of the game: the overlay drawn over a plain desktop blue, as --snapshot does, put
    /// on the clipboard as an image and saved in Pictures/Desk Arcade as a PNG.
    /// </summary>
    public async void SharePicture()
    {
        if (!Shown) SetOverlayVisible(true);
        await Task.Delay(120); // let the menu close first, so it is not in the picture
        try
        {
            using var bitmap = Render(_root, new SolidColorBrush(Color.FromRgb(40, 78, 120)));
            string? file = SavePicture(bitmap, "desk-arcade");
            await CopyPicture(bitmap);
            Stats.Add("share.pictures");
            Notice(L.T("Picture copied · paste it into a chat"), file != null ? L.F("also saved as {0}", file) : "", Color.FromRgb(255, 209, 102));
        }
        catch (Exception e)
        {
            Notice(L.T("Couldn't copy the picture"), e.Message, Color.FromRgb(255, 107, 107));
        }
    }

    /// <summary>Draws <paramref name="visual"/> (the whole overlay, or a card) into a bitmap, over <paramref name="backdrop"/> when given.</summary>
    RenderTargetBitmap Render(Control visual, IBrush? backdrop)
    {
        var size = visual == _root ? Bounds.Size : visual.Bounds.Size;
        var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)size.Width), Math.Max(1, (int)size.Height)), new Vector(96, 96));
        var before = _root.Background;
        if (backdrop != null && visual == _root) _root.Background = backdrop;
        try
        {
            UpdateLayout();
            bitmap.Render(visual);
        }
        finally
        {
            _root.Background = before;
        }
        return bitmap;
    }

    /// <summary>Saves a picture in Pictures/Desk Arcade with the date and time in its name; null when it cannot be saved.</summary>
    static string? SavePicture(Bitmap bitmap, string name)
    {
        try
        {
            Directory.CreateDirectory(PicturesFolder);
            string path = Path.Combine(PicturesFolder, $"{name}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.png");
            bitmap.Save(path);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    Bitmap? _clipboardPicture; // what the clipboard holds: some platforms read it only when someone pastes

    /// <summary>Puts a picture on the clipboard as an image, the way the platform's chat apps paste one.</summary>
    async Task CopyPicture(Bitmap bitmap)
    {
        if (Clipboard == null) return;
        using var png = new MemoryStream();
        bitmap.Save(png);
        png.Position = 0;
        var copy = new Bitmap(png); // outlives the rendered bitmap, which is disposed when this returns
        var item = new DataTransferItem();
        item.SetBitmap(copy);
        var transfer = new DataTransfer();
        transfer.Add(item);
        await Clipboard.SetDataAsync(transfer);
        var previous = _clipboardPicture;
        _clipboardPicture = copy;
        previous?.Dispose();
    }
}
