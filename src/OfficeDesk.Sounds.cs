using System;
using System.Collections.Generic;
using DeskArcade.Office;

namespace DeskArcade;

/// <summary>
/// Focus sounds: brown noise, pink noise, rain or a café, from At work → Focus sounds at any time, or by themselves
/// during focus blocks. They fade out for a meeting and while presenting, and come back after.
/// </summary>
public sealed partial class OfficeDesk
{
    static readonly (string Id, FocusSoundKind Kind)[] SoundChoices =
    {
        ("brown", FocusSoundKind.Brown), ("pink", FocusSoundKind.Pink), ("rain", FocusSoundKind.Rain), ("cafe", FocusSoundKind.Cafe),
    };

    /// <summary>The volumes to choose from: quiet, normal, loud (named as the pet's voice is).</summary>
    public static readonly double[] SoundVolumes = { 0.3, 0.5, 0.8 };

    bool _soundOn;      // started from the menu: plays until stopped
    bool _soundHushed;  // stopped during a focus block that plays it by itself: quiet until the block ends
    double _soundCarry; // seconds heard, not yet counted

    FocusSoundKind SoundKind => Array.Find(SoundChoices, c => c.Id == S.FocusSound) is { Id: not null } c ? c.Kind : FocusSoundKind.Brown;

    public static string SoundName(FocusSoundKind kind) => kind switch
    {
        FocusSoundKind.Pink => L.T("Pink noise"), FocusSoundKind.Rain => L.T("Rain"), FocusSoundKind.Cafe => L.T("Café"), _ => L.T("Brown noise"),
    };

    static string VolumeName(int i) => i switch { 0 => L.T("Quiet"), 1 => L.T("Normal"), _ => L.T("Loud") };

    /// <summary>A focus sound should be playing: started from the menu, or a focus block that plays one.</summary>
    public bool FocusSoundOn => _soundOn || S.FocusSoundInBlocks && _focus.Phase == FocusPhase.Focus && !_soundHushed;

    /// <summary>Once a second: what should play, faded down for a meeting or a presentation, and the minutes heard.</summary>
    void StepFocusSounds(double dt)
    {
        if (_focus.Phase != FocusPhase.Focus) _soundHushed = false; // the next block plays again
        _w.Sound.SetFocusSound(FocusSoundOn ? SoundKind : null, S.FocusSoundVolume, ducked: _fullScreen || InMeeting);
        if (!_w.Sound.FocusSoundAudible) return;
        _soundCarry += dt;
        if (_soundCarry < 60) return;
        _soundCarry -= 60;
        _w.Stats.Add("work.sounds"); // a minute of focus sound
    }

    /// <summary>At work → Focus sounds → a sound: it becomes the one chosen and plays now, until stopped.</summary>
    public void PlayFocusSound(FocusSoundKind kind)
    {
        S.FocusSound = Array.Find(SoundChoices, c => c.Kind == kind).Id ?? "brown";
        _w.SaveSettings();
        _soundOn = true;
        _soundHushed = false;
        StepFocusSounds(0);
        if (!_w.Sound.HasDevice) Say(L.T("No sound device"), L.T("focus sounds need speakers or headphones"), Red, null);
        else if (_fullScreen || InMeeting) Say(SoundName(kind), L.T("it starts when the meeting or the presentation is over"), Violet, null);
        _w.RefreshTray();
    }

    /// <summary>At work → Focus sounds → Stop: it fades out (and stays off for the rest of a focus block that plays it).</summary>
    public void StopFocusSound()
    {
        if (!FocusSoundOn) return;
        _soundOn = false;
        if (_focus.Phase == FocusPhase.Focus) _soundHushed = true;
        StepFocusSounds(0);
        _w.RefreshTray();
    }

    public void SetFocusSoundInBlocks(bool on)
    {
        S.FocusSoundInBlocks = on;
        _w.SaveSettings();
        StepFocusSounds(0);
        _w.RefreshTray();
    }

    public void SetFocusSoundVolume(double volume)
    {
        S.FocusSoundVolume = Math.Clamp(volume, 0, 1);
        _w.SaveSettings();
        StepFocusSounds(0);
        _w.RefreshTray();
    }

    /// <summary>"focus-sound:rain", "focus-sound:off" and the like, from --signal.</summary>
    bool SoundSignal(string arg)
    {
        if (arg is "off" or "stop")
        {
            StopFocusSound();
            return true;
        }
        if (Array.Find(SoundChoices, c => c.Id == arg) is not { Id: not null } choice) return false;
        PlayFocusSound(choice.Kind);
        return true;
    }

    /// <summary>At work → Focus sounds.</summary>
    public MenuNode FocusSoundsMenu()
    {
        var items = new List<MenuNode>
        {
            new()
            {
                Header = () => FocusSoundOn ? L.F("Playing: {0}", SoundName(SoundKind))
                    : S.FocusSoundInBlocks ? L.F("{0} in focus blocks", SoundName(SoundKind)) : L.T("Nothing playing"),
                Enabled = () => false,
            },
            MenuNode.Line(),
        };
        foreach (var (id, kind) in SoundChoices)
            items.Add(new() { Header = () => SoundName(kind), Click = () => PlayFocusSound(kind), Checked = () => S.FocusSound == id, Radio = true });
        items.Add(new() { Header = () => L.T("Stop the sound"), Click = StopFocusSound, Enabled = () => FocusSoundOn });
        items.Add(MenuNode.Line());
        items.Add(new() { Header = () => L.T("Play it during focus blocks"), Click = () => SetFocusSoundInBlocks(!S.FocusSoundInBlocks), Checked = () => S.FocusSoundInBlocks });
        items.Add(MenuNode.Line());
        for (int i = 0; i < SoundVolumes.Length; i++)
        {
            int level = i;
            items.Add(new()
            {
                Header = () => VolumeName(level), Click = () => SetFocusSoundVolume(SoundVolumes[level]),
                Checked = () => Math.Abs(S.FocusSoundVolume - SoundVolumes[level]) < 0.01, Radio = true,
            });
        }
        return new MenuNode { Header = () => L.T("Focus sounds"), Children = items };
    }
}
