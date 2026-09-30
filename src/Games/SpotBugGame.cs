using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using DeskArcade.Engine;

namespace DeskArcade.Games;

/// <summary>
/// Spot the Bug: a short piece of code with one bug in it, on a board over the desktop; click the line with the bug.
/// After each snippet the fix shows as a diff under the buggy line, with a sentence or two on why (see
/// <see cref="SpotBugDeck"/> for the snippets, <see cref="SpotBugRound"/> for the scoring). A round is ten snippets,
/// easy to hard, each against its own fuse: a find scores 10 plus up to 10 for speed, a wrong click costs 3 points of
/// that snippet and the third wrong click loses it. A round is a race against the computer rival at its CPU level, or
/// against a co-worker over the LAN on the same ten snippets. The daily snippet is the same for everyone that day, can be
/// tried once, and builds a streak. The chips pick the mode and the language (all, C#, TypeScript, Python, SQL); the grip
/// moves the board.
/// </summary>
public sealed partial class SpotBugGame : MiniGame
{
    enum Mode { Round, Daily }

    // the chips, the daily snippet's result and the streak, kept with the game's other small numbers in the settings
    const string LangKey = "spotbug.lang", ModeKey = "spotbug.daily", StartedKey = "spotbug.daystarted", DayKey = "spotbug.day", DayFoundKey = "spotbug.dayfound", DayClicksKey = "spotbug.dayclicks",
        DaySecondsKey = "spotbug.dayseconds", LastFoundKey = "spotbug.lastfound", StreakKey = "spotbug.streak";
    const double RevealDelay = 0.4;

    SpotBugRound _round = null!;
    Mode _mode;
    int _lang, _lanRound, _session = -1;
    bool _racing, _seeded, _revealing, _demo;
    (int Found, int Of, int Score)? _lastRound;

    public SpotBugGame(IGameHost host) : base(host)
    {
        _lang = Math.Clamp(Get(LangKey), 0, SpotBugDeck.Languages.Length);
        BuildView();
        if (Get(ModeKey) == 1) OpenDaily();
        else NewRound();
        L.Changed += () =>
        {
            Draw();
            Relayout(animate: false);
        };
    }

    public override string Id => "spotbug";
    public override string Title => "Spot the Bug";

    bool LanOn => Host.Lan.Connected;
    Mode Shown => LanOn ? Mode.Round : _mode;
    static int Today => DateOnly.FromDateTime(DateTime.Now).DayNumber;
    static Snippet DailySnippet => SpotBugDeck.Daily(DateOnly.FromDayNumber(Today));
    static int DailyNumber => SpotBugDeck.DailyNumber(DateOnly.FromDayNumber(Today));
    int Get(string key) => Host.Settings.Levels.TryGetValue(key, out int v) ? v : 0;
    void Set(string key, int value) => Host.Settings.Levels[key] = value;
    bool DailyDone => Get(DayKey) == Today;
    int Streak => SpotBugStreak.Showing(Get(LastFoundKey), Get(StreakKey), Today);
    string? LangFilter => LanOn || _lang == 0 ? null : SpotBugDeck.Languages[_lang - 1];

    public override HudInfo Hud
    {
        get
        {
            long best = Host.Stats.Get("spotbug.best");
            string bestText = best > 0 ? L.F("Best {0}", best) : L.T("Best —");
            if (Shown == Mode.Daily)
            {
                string line = DailyDone && _round.Phase != SpotPhase.Revealed
                    ? Streak > 0 ? L.F("Daily snippet done · streak {0}", Streak) : L.T("Daily snippet done · come back tomorrow")
                    : _round.Phase switch
                    {
                        SpotPhase.Ready => L.F("Daily snippet #{0} · the same for everyone today", DailyNumber),
                        SpotPhase.Playing => L.T("Click the line with the bug · one try a day"),
                        _ => _round.LastFound ? L.T("Found it · read the fix") : L.T("The bug got away · read the fix"),
                    };
                return new HudInfo("#" + DailyNumber.ToString(CultureInfo.InvariantCulture), line, bestText);
            }
            string roundLine = _round.Phase switch
            {
                SpotPhase.Ready => L.T("Click Start: ten snippets, one bug in each"),
                SpotPhase.Playing => L.F("Snippet {0}/{1} · click the line with the bug", _round.Index + 1, _round.Snippets.Count),
                SpotPhase.Revealed => _round.LastFound ? L.F("Found · +{0} · click Next", _round.LastPoints) : L.T("Missed · read the fix, then click Next"),
                _ => L.F("Round over · {0}/{1} found · {2} points", _round.Found, _round.Snippets.Count, _round.Score),
            };
            return new HudInfo(_round.Score.ToString(CultureInfo.InvariantCulture), roundLine, bestText);
        }
    }

    /// <summary>The last result to paste: today's daily snippet once it has been tried, else the last round finished.</summary>
    public override string? ShareText =>
        DailyDone ? SpotBugStreak.ShareLine(DailyNumber, Get(DayFoundKey) == 1, Get(DayClicksKey), Get(DaySecondsKey))
        : _lastRound is { } r ? SpotBugStreak.RoundLine(r.Found, r.Of, r.Score)
        : null;

    // ------------------------------------------------------------------ races

    public override bool SupportsLan => true;
    public override (int Score, bool Active)? Race => (Shown == Mode.Round ? _round.Score : 0, _racing);
    /// <summary>A decent player: most bugs found, with some time to spare and the odd wrong click.</summary>
    public override int RaceBaseline => 110;
    public override int RaceMax => SpotBugRound.MaxScore(SpotBugDeck.RoundSize);
    public override int RaceBest => (int)Host.Stats.Get("spotbug.best");
    public override double RaceSeconds => 150;

    /// <summary>The rival started a round: a fresh seeded round (unless this one is fresh already), started now.</summary>
    public override void StartRace()
    {
        if (_racing) return;
        if (_mode == Mode.Daily || _round.Phase != SpotPhase.Ready || !_seeded) NewRound();
        Begin();
    }

    bool CheckSession()
    {
        int session = LanOn ? Host.Lan.Session : -1;
        if (session == _session) return false;
        _session = session;
        _lanRound = 0;
        return true;
    }

    // ------------------------------------------------------------------ rounds

    /// <summary>A new round of ten, not started yet (seeded over the LAN, so both screens get the same ten).</summary>
    void NewRound()
    {
        CheckSession();
        _mode = Mode.Round;
        if (!LanOn) RememberMode();
        _seeded = LanOn;
        var rng = LanOn ? new Random(MinesweeperRules.DailySeed(DateTime.Today, "spotbug-lan", ++_lanRound)) : Rng;
        _round = new SpotBugRound(SpotBugDeck.Round(rng, LangFilter));
        _racing = _revealing = false;
        ShowRound(animate: false);
    }

    /// <summary>Today's daily snippet: ready to try, or, once tried, shown with its fix.</summary>
    void OpenDaily()
    {
        _mode = Mode.Daily;
        RememberMode();
        _round = new SpotBugRound(new[] { DailySnippet });
        _racing = _revealing = false;
        // started today and never finished (the program was closed on it): that was the day's one try
        if (!DailyDone && Get(StartedKey) == Today) RecordDaily(found: false, clicks: 0, seconds: (int)_round.Fuse);
        ShowRound(animate: false);
    }

    /// <summary>The mode chip is remembered, so the next start opens on the daily snippet or a round as it was left.</summary>
    void RememberMode()
    {
        int mode = _mode == Mode.Daily ? 1 : 0;
        if (Get(ModeKey) == mode) return;
        Set(ModeKey, mode);
        Host.SaveSettings();
    }

    void Begin()
    {
        if (_round.Phase != SpotPhase.Ready) return;
        _round.Start();
        if (Shown == Mode.Round && !_racing)
        {
            _racing = true;
            Host.RoundStarted();
        }
        if (Shown == Mode.Daily)
        {
            Set(StartedKey, Today);
            Host.SaveSettings();
        }
        Host.Sound.Play("whoosh", 0.3, 1.2);
        ShowRound(animate: true);
    }

    /// <summary>The round ended (or was cut short by New): report it to the race and keep the records.</summary>
    void EndRound(bool finished)
    {
        if (finished)
        {
            long before = Host.Stats.Get("spotbug.best");
            Host.Stats.Add("spotbug.rounds");
            Host.Stats.Max("spotbug.best", _round.Score);
            if (_round.Perfect) Host.Stats.Add("spotbug.perfect");
            _lastRound = (_round.Found, _round.Snippets.Count, _round.Score);
            bool best = _round.Score > before && _round.Score > 0;
            var at = BoardPoint(W / 2, CodeTop + 60);
            Host.Fx.Popup(at, best ? L.T("NEW BEST!") : L.T("ROUND OVER"), Themes.Themed(Themes.ClassicGold), 40, 2.4,
                L.F("{0} of {1} found · {2} points", _round.Found, _round.Snippets.Count, _round.Score));
            if (best || _round.Perfect) Host.Fx.Burst(at, Themes.Current.Confetti, 44, 540, 700, 7, 1.1);
            Host.Sound.Play(best ? "best" : "done", 0.7);
        }
        if (!_racing) return;
        _racing = false;
        Host.RoundEnded(_round.Score);
    }

    /// <summary>A click on a line of the code (1-based) at <paramref name="at"/> (overlay DIPs).</summary>
    void ClickLine(int line, Vec2 at)
    {
        if (_revealing) return;
        var result = _round.Click(line);
        switch (result)
        {
            case SpotClick.Wrong:
                Host.Sound.Play("key-bad", 0.5, 0.9);
                Host.Sound.Play("thunk", 0.35, 1.2);
                Host.Fx.Popup(at, "−" + SpotBugRound.WrongPenalty.ToString(CultureInfo.InvariantCulture), Art.Safe(Color.FromRgb(255, 96, 96)), 22, 0.9);
                Host.ShareAction(at, -SpotBugRound.WrongPenalty);
                FlashWrong(line);
                break;
            case SpotClick.Right:
                Host.Stats.Add("spotbug.found");
                Host.Sound.Play("score", 0.6);
                Host.Fx.Popup(at, "+" + _round.LastPoints.ToString(CultureInfo.InvariantCulture), Themes.Themed(Themes.ClassicGold), 26, 1.0);
                Host.Fx.Burst(at, new[] { Art.Safe(Color.FromRgb(90, 220, 140)), Themes.ClassicGold, Colors.White }, 16, 260, 500, 5, 0.7);
                Host.ShareAction(at, _round.LastPoints);
                FlashRight(line);
                RevealSoon();
                break;
            case SpotClick.Missed:
                Host.Fx.Popup(at, "−" + SpotBugRound.WrongPenalty.ToString(CultureInfo.InvariantCulture), Art.Safe(Color.FromRgb(255, 96, 96)), 22, 0.9);
                Host.ShareAction(at, -SpotBugRound.WrongPenalty);
                FlashWrong(line);
                Missed();
                break;
            default:
                return;
        }
        Draw();
        Host.HudChanged();
    }

    /// <summary>Three wrong clicks or the fuse out: the bug lines flash and the fix follows.</summary>
    void Missed()
    {
        Host.Sound.Play("buzzer", 0.3);
        FlashBug();
        RevealSoon();
    }

    /// <summary>The fix and the reason come in a moment after the verdict, so the flash can be seen first.</summary>
    void RevealSoon()
    {
        _revealing = true;
        if (Shown == Mode.Daily) RecordDaily(_round.LastFound, _round.Clicks, Math.Max(1, (int)Math.Round(_round.LastSeconds)));
        Anims.After(RevealDelay, () =>
        {
            _revealing = false;
            ShowRound(animate: true);
            Host.HudChanged();
        });
    }

    void Next()
    {
        if (_round.Phase != SpotPhase.Revealed || _revealing) return;
        _round.Next();
        if (_round.Phase == SpotPhase.Over) EndRound(finished: true);
        else Host.Sound.Play("whoosh", 0.3, 1.2);
        ShowRound(animate: true);
        Host.HudChanged();
    }

    /// <summary>The daily snippet was tried: keep the result for today and move the streak.</summary>
    void RecordDaily(bool found, int clicks, int seconds)
    {
        if (DailyDone) return;
        int today = Today;
        Set(DayKey, today);
        Set(DayFoundKey, found ? 1 : 0);
        Set(DayClicksKey, clicks);
        Set(DaySecondsKey, seconds);
        int streak = SpotBugStreak.After(Get(LastFoundKey), Get(StreakKey), today, found);
        Set(StreakKey, streak);
        if (found)
        {
            Set(LastFoundKey, today);
            Host.Stats.Add("spotbug.daily");
            Host.Stats.Max("spotbug.streak", streak);
        }
        Host.SaveSettings();
        if (found) Host.Sound.Play("done", 0.6);
    }

    // ------------------------------------------------------------------ input

    public override void CollectHitShapes(List<HitShape> into)
    {
        into.Add(HitShape.Box(new Rect(_at.X, _at.Y, W * _scale, _boardH * _scale)));
        into.Add(_handle.Hit);
    }

    public override bool PointerDown(Vec2 p, bool right)
    {
        if (_handle.Contains(p))
        {
            _handle.Begin(p, _at);
            return true;
        }
        var local = new Point((p.X - _at.X) / _scale, (p.Y - _at.Y) / _scale);
        if (_modeRect.Contains(local) && CanSwitchMode)
        {
            Host.Sound.Play("key", 0.3, 1.2);
            if (Shown == Mode.Daily) NewRound();
            else OpenDaily();
            Host.HudChanged();
            return false;
        }
        if (_newRect.Contains(local) && Shown == Mode.Round)
        {
            if (LanOn && _seeded && _round.Phase == SpotPhase.Ready) return false; // already a fresh round of the race
            EndRound(finished: false);
            NewRound();
            Host.Sound.Play("board", 0.3, 1.3);
            Host.HudChanged();
            return false;
        }
        for (int k = 0; k < _langRects.Length; k++)
        {
            if (!_langRects[k].Contains(local)) continue;
            if (LanOn || Shown == Mode.Daily || _round.Phase is SpotPhase.Playing or SpotPhase.Revealed || k == _lang) return false;
            _lang = k;
            Set(LangKey, k);
            Host.SaveSettings();
            Host.Sound.Play("key", 0.3, 1.1);
            NewRound();
            Host.HudChanged();
            return false;
        }
        if (_buttonShown && ButtonOnBoard.Contains(local))
        {
            PressButton();
            return false;
        }
        if (_round.Phase == SpotPhase.Playing && LineAt(local) is int line) ClickLine(line, p);
        return false;
    }

    /// <summary>Round and daily can be swapped between rounds, and after the daily snippet; never mid-snippet or over the LAN.</summary>
    bool CanSwitchMode => !LanOn && !_revealing && (_round.Phase is SpotPhase.Ready or SpotPhase.Over || Shown == Mode.Daily && _round.Phase == SpotPhase.Revealed);

    /// <summary>The button under the text: Start, Next, New round, or a round after the daily snippet.</summary>
    void PressButton()
    {
        Host.Sound.Play("key", 0.3, 1.0);
        if (Shown == Mode.Daily)
        {
            if (DailyDone) NewRound();
            else Begin();
        }
        else if (_round.Phase == SpotPhase.Ready) Begin();
        else if (_round.Phase == SpotPhase.Revealed) Next();
        else if (_round.Phase == SpotPhase.Over)
        {
            NewRound();
            if (LanOn) Begin(); // over the LAN a new round is the next race: it starts at once
        }
        Host.HudChanged();
    }

    public override void PointerUp(Vec2 p) => _handle.End(_at);
    public override void PointerCancel() => _handle.Cancel();

    // ------------------------------------------------------------------ frames

    public override bool Update(double dt)
    {
        if (_handle.Dragging)
        {
            _origin = _handle.Move(Host.Pointer, new Size(W * _scale, _boardH * _scale));
            Place();
        }
        bool ticking = _round.Phase == SpotPhase.Playing && !_revealing;
        if (ticking)
        {
            if (_round.Tick(dt))
            {
                Host.Fx.Popup(BoardPoint(W / 2, CodeTop + 30), L.T("TIME!"), Art.Safe(Color.FromRgb(255, 110, 110)), 32, 1.4);
                Missed();
                Draw();
                Host.HudChanged();
            }
            DrawFuse();
        }
        return Anims.Update(dt) || _handle.Dragging || ticking;
    }

    public override void Deactivate()
    {
        _handle.Cancel();
        Anims.Finish();
        SetHover(null);
    }

    // ------------------------------------------------------------------ demo

    double _demoT, _demoWait = 1;
    int _demoTarget;

    /// <summary>
    /// Plays by itself: starts a round, looks at a line for a moment (the hover follows), clicks it (the right one about
    /// three times in four), reads the fix and moves on; after a round it picks another language.
    /// </summary>
    public override void DemoTick()
    {
        _demo = true;
        if ((_demoT += 0.15) < _demoWait) return;
        _demoT = 0;
        _demoWait = 0.6 + Rng.NextDouble() * 0.6;
        if (_revealing) return;
        switch (_round.Phase)
        {
            case SpotPhase.Ready:
                if (Shown == Mode.Daily && DailyDone) NewRound();
                else PressButton();
                break;
            case SpotPhase.Playing:
                if (_demoTarget == 0)
                {
                    var s = _round.Current;
                    var open = Enumerable.Range(1, s.Lines).Where(l => !_round.Tried.Contains(l) && !s.IsBug(l) && IsCode(s.Code[l - 1])).ToList();
                    if (open.Count == 0) open = s.Bug.ToList();
                    _demoTarget = Rng.NextDouble() < 0.72 ? s.Bug[Rng.Next(s.Bug.Count)] : open[Rng.Next(open.Count)];
                    SetHover(_demoTarget);
                    _demoWait = 0.9 + Rng.NextDouble() * 1.2;
                }
                else
                {
                    int line = _demoTarget;
                    _demoTarget = 0;
                    ClickLine(line, LinePoint(line));
                }
                break;
            default:
                // a few seconds to read the fix (or the round's result) first
                if (!_demoRead)
                {
                    _demoRead = true;
                    _demoWait = _round.Phase == SpotPhase.Over ? 5 : 3.2;
                    break;
                }
                _demoRead = false;
                if (_round.Phase == SpotPhase.Over)
                {
                    _lang = (_lang + 1) % (SpotBugDeck.Languages.Length + 1);
                    NewRound();
                }
                else PressButton();
                break;
        }
    }

    bool _demoRead;

    /// <summary>A line a player might suspect: not blank, not a comment, not a lone bracket.</summary>
    static bool IsCode(string line)
    {
        string t = line.Trim();
        return t.Length > 2 && !t.StartsWith("//", StringComparison.Ordinal) && !t.StartsWith('#') && !t.StartsWith("--", StringComparison.Ordinal);
    }
}
