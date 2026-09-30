using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>Where a quiz is: reading a question, answering it, the right answer shown, the leaderboard after it, the podium.</summary>
public enum QuizPhase { Read, Answer, Reveal, Board, Over }

/// <summary>
/// One quiz, run by whoever hosts it (or alone against the computer), without any UI. Each question is read for a moment,
/// then answered within <see cref="AnswerSeconds"/>: a right answer scores <see cref="MaxPoints"/> at once, falling
/// evenly to <see cref="MinPoints"/> at the last moment; a wrong one or none scores nothing. The question ends when
/// everyone taking part has answered or the time is up; the right answer is shown, then the leaderboard, then the next
/// question, and after the last one the podium. Computer players answer at their level (<see cref="CpuAnswer"/>).
/// Players are seats 0..n−1; a player who drops out is left out of the waiting (<see cref="SetActive"/>).
/// </summary>
public sealed class QuizMatch
{
    public const double AnswerSeconds = 20, RevealSeconds = 3.5, BoardSeconds = 4.5, FirstExtra = 2, QuickSeconds = 2;
    public const int MaxPoints = 1000, MinPoints = 500;

    readonly List<QuizRound> _rounds;
    readonly Random _rng;
    readonly double[] _cpuAt;
    readonly int[] _cpuPick;

    public QuizMatch(IReadOnlyList<QuizRound> rounds, bool[] cpu, int level, Random rng)
    {
        if (rounds.Count == 0) throw new ArgumentException("a quiz needs a question", nameof(rounds));
        _rounds = rounds.ToList();
        _rng = rng;
        Cpu = cpu.ToArray();
        Level = Math.Clamp(level, 1, 4);
        int n = Cpu.Length;
        Scores = new int[n];
        Gained = new int[n];
        Choice = Enumerable.Repeat(-1, n).ToArray();
        Took = new double[n];
        Rights = new int[n];
        Quick = new int[n];
        TotalTime = new double[n];
        Active = Enumerable.Repeat(true, n).ToArray();
        _cpuAt = new double[n];
        _cpuPick = new int[n];
        StartRead(first: true);
    }

    public bool[] Cpu { get; }
    public int Level { get; }
    public int Players => Cpu.Length;
    public int Count => _rounds.Count;
    public IReadOnlyList<QuizRound> Rounds => _rounds;
    public int Index { get; private set; }
    public QuizPhase Phase { get; private set; }
    /// <summary>Seconds into the phase, and how long it lasts.</summary>
    public double Clock { get; private set; }
    public double Length { get; private set; }
    public double Left => Math.Max(0, Length - Clock);
    public QuizRound Current => _rounds[Index];
    public int[] Scores { get; }
    /// <summary>What each player scored on the current question (0 until they answer right).</summary>
    public int[] Gained { get; }
    /// <summary>The place each player picked on the current question, −1 for none yet.</summary>
    public int[] Choice { get; }
    /// <summary>Seconds each player took on the current question.</summary>
    public double[] Took { get; }
    public int[] Rights { get; }
    /// <summary>Right answers within <see cref="QuickSeconds"/>.</summary>
    public int[] Quick { get; }
    /// <summary>Seconds taken over the questions so far, the whole answer time for a question left unanswered.</summary>
    public double[] TotalTime { get; }
    /// <summary>Players the quiz waits for: computer players, and people still in the room.</summary>
    public bool[] Active { get; }
    /// <summary>Goes up with every change a player could see.</summary>
    public int Version { get; private set; }
    public bool Over => Phase == QuizPhase.Over;

    /// <summary>The points for a right answer after <paramref name="seconds"/>: 1000 at once, 500 at the end of the answer time.</summary>
    public static int Points(double seconds) =>
        (int)Math.Round(MaxPoints - (MaxPoints - MinPoints) * Math.Clamp(seconds / AnswerSeconds, 0, 1), MidpointRounding.AwayFromZero);

    /// <summary>How long a question is shown before its answers: longer for a longer question, from 2 to 5 seconds.</summary>
    public static double ReadSeconds(string question) => Math.Clamp(1.5 + question.Length * 0.03, 2, 5);

    /// <summary>
    /// A computer player's answer at <paramref name="level"/> (1 Easy to 4 Expert): whether it is right (half the time on
    /// Easy, seven in eight on Expert) and after how long (around two thirds of the answer time on Easy, a quarter on Expert).
    /// </summary>
    public static (bool Right, double Seconds) CpuAnswer(int level, Random rng)
    {
        level = Math.Clamp(level, 1, 4);
        double accuracy = level switch { 1 => 0.5, 2 => 0.63, 3 => 0.76, _ => 0.87 };
        double pace = level switch { 1 => 0.62, 2 => 0.48, 3 => 0.36, _ => 0.26 };
        bool right = rng.NextDouble() < accuracy;
        double seconds = 0.8 + AnswerSeconds * pace * (0.55 + rng.NextDouble() * 0.9);
        return (right, Math.Clamp(seconds, 1.2, AnswerSeconds - 0.4));
    }

    void StartRead(bool first = false)
    {
        Phase = QuizPhase.Read;
        Clock = 0;
        Length = ReadSeconds(Current.Item.In("en").Question) + (first ? FirstExtra : 0);
        for (int p = 0; p < Players; p++)
        {
            Choice[p] = -1;
            Gained[p] = 0;
            Took[p] = 0;
        }
        Version++;
    }

    void StartAnswer()
    {
        Phase = QuizPhase.Answer;
        Clock = 0;
        Length = AnswerSeconds;
        int right = Current.RightPlace;
        for (int p = 0; p < Players; p++)
        {
            if (!Cpu[p]) continue;
            var (ok, seconds) = CpuAnswer(Level, _rng);
            _cpuAt[p] = seconds;
            var wrong = Enumerable.Range(0, 4).Where(i => i != right).ToList();
            _cpuPick[p] = ok ? right : wrong[_rng.Next(wrong.Count)];
        }
        Version++;
    }

    /// <summary>
    /// <paramref name="player"/> picks the answer shown in <paramref name="place"/> for question <paramref name="question"/>:
    /// only while that question's answers are open, and once. False when it doesn't count.
    /// </summary>
    public bool Answer(int player, int question, int place)
    {
        if (Phase != QuizPhase.Answer || question != Index || player < 0 || player >= Players || !Active[player] ||
            Choice[player] >= 0 || place is < 0 or > 3)
            return false;
        Choice[player] = place;
        Took[player] = Math.Min(Clock, AnswerSeconds);
        TotalTime[player] += Took[player];
        if (place == Current.RightPlace)
        {
            Gained[player] = Points(Took[player]);
            Scores[player] += Gained[player];
            Rights[player]++;
            if (Took[player] <= QuickSeconds) Quick[player]++;
        }
        Version++;
        if (AllAnswered) Reveal();
        return true;
    }

    /// <summary>True once everyone the quiz waits for has answered.</summary>
    public bool AllAnswered => Enumerable.Range(0, Players).All(p => !Active[p] || Choice[p] >= 0);

    /// <summary>A person left the room (false) or came back (true).</summary>
    public void SetActive(int player, bool active)
    {
        if (player < 0 || player >= Players || Cpu[player] || Active[player] == active) return;
        Active[player] = active;
        Version++;
        if (Phase == QuizPhase.Answer && AllAnswered) Reveal();
    }

    void Reveal()
    {
        for (int p = 0; p < Players; p++)
            if (Choice[p] < 0) TotalTime[p] += AnswerSeconds;
        Phase = QuizPhase.Reveal;
        Clock = 0;
        Length = RevealSeconds;
        Version++;
    }

    /// <summary>Moves the quiz on by <paramref name="dt"/> seconds: the computer players answer, and the phases change on time.</summary>
    public void Update(double dt)
    {
        if (Over) return;
        Clock += dt;
        switch (Phase)
        {
            case QuizPhase.Read when Clock >= Length:
                StartAnswer();
                break;
            case QuizPhase.Answer:
                for (int p = 0; p < Players && Phase == QuizPhase.Answer; p++)
                    if (Cpu[p] && Choice[p] < 0 && Clock >= _cpuAt[p])
                    {
                        double at = Clock;
                        Clock = _cpuAt[p]; // it answered at its moment, whatever the frame rate
                        Answer(p, Index, _cpuPick[p]);
                        if (Phase == QuizPhase.Answer) Clock = at;
                    }
                if (Phase == QuizPhase.Answer && Clock >= AnswerSeconds) Reveal();
                break;
            case QuizPhase.Reveal when Clock >= Length:
                if (Index + 1 >= Count)
                {
                    Phase = QuizPhase.Over;
                    Clock = Length = 0;
                    Version++;
                }
                else
                {
                    Phase = QuizPhase.Board;
                    Clock = 0;
                    Length = BoardSeconds;
                    Version++;
                }
                break;
            case QuizPhase.Board when Clock >= Length:
                Index++;
                StartRead();
                break;
        }
    }

    /// <summary>Each player's place, 1 for the most points; equal scores share a place.</summary>
    public static int[] Places(int[] scores) => scores.Select(s => 1 + scores.Count(o => o > s)).ToArray();
}

/// <summary>
/// What one player sees of a quiz: everyone's names and scores, the question in their own language with its answers in
/// the order they are shown, their own pick, and from the reveal on the right answer and how many picked each one. The
/// host sends it to every player about four times a second ("qv|json"); <see cref="Left"/> is the time left in the phase
/// when it was sent, which the player counts down from.
/// </summary>
public sealed class QuizView
{
    public int Game { get; set; }
    public int Seat { get; set; }
    public string[] Names { get; set; } = Array.Empty<string>();
    public bool[] Cpu { get; set; } = Array.Empty<bool>();
    /// <summary>People who have left the room.</summary>
    public bool[] Away { get; set; } = Array.Empty<bool>();
    public int[] Scores { get; set; } = Array.Empty<int>();
    public int[] Gained { get; set; } = Array.Empty<int>();
    public bool[] Answered { get; set; } = Array.Empty<bool>();
    public int[] Rights { get; set; } = Array.Empty<int>();
    public int Phase { get; set; }
    public int Index { get; set; }
    public int Count { get; set; }
    public double Left { get; set; }
    public double Length { get; set; }
    public string Pack { get; set; } = "";
    /// <summary>The language the question is in; a pack of your own is <see cref="QuizPack.AnyLanguage"/>.</summary>
    public string Lang { get; set; } = "";
    public string Question { get; set; } = "";
    public string[] Answers { get; set; } = Array.Empty<string>();
    /// <summary>My pick (a place, 0–3), −1 for none.</summary>
    public int Mine { get; set; } = -1;
    /// <summary>The right place, from the reveal on (−1 before).</summary>
    public int Right { get; set; } = -1;
    /// <summary>How many picked each place, from the reveal on.</summary>
    public int[] Picks { get; set; } = Array.Empty<int>();
    /// <summary>My seconds on the question, and over the quiz so far.</summary>
    public double Took { get; set; }
    public double Time { get; set; }
    public bool Daily { get; set; }
    public int Version { get; set; }
    /// <summary>The last action number the host has handled from this player.</summary>
    public int Ack { get; set; }

    public QuizPhase Stage => (QuizPhase)Phase;
    public int Players => Names.Length;

    /// <summary>
    /// What <paramref name="seat"/> sees, the question in <paramref name="lang"/>. Until the reveal the points of the
    /// question being answered are left out of everyone's score, so nobody can tell who is right before it is shown.
    /// </summary>
    public static QuizView Of(QuizMatch m, int seat, string lang, int game, string[] names, string pack, bool daily, int ack)
    {
        var shown = m.Current.Shown(lang);
        bool revealed = m.Phase is QuizPhase.Reveal or QuizPhase.Board or QuizPhase.Over;
        return new QuizView
        {
            Game = game, Seat = seat, Names = names, Cpu = m.Cpu.ToArray(), Away = m.Active.Select(a => !a).ToArray(),
            Scores = m.Scores.Select((s, p) => revealed ? s : s - m.Gained[p]).ToArray(),
            Gained = revealed ? m.Gained.ToArray() : new int[m.Players],
            Answered = m.Choice.Select(c => c >= 0).ToArray(),
            Rights = m.Rights.Select((r, p) => revealed || m.Gained[p] == 0 ? r : r - 1).ToArray(),
            Phase = (int)m.Phase, Index = m.Index, Count = m.Count, Left = m.Left, Length = m.Length,
            Pack = pack, Lang = m.Current.Item.Texts.ContainsKey(lang) ? lang : m.Current.Item.Texts.Keys.First(),
            Question = shown.Question, Answers = shown.Answers, Mine = m.Choice[seat],
            Right = revealed ? m.Current.RightPlace : -1,
            Picks = revealed ? Enumerable.Range(0, 4).Select(i => m.Choice.Count(c => c == i)).ToArray() : Array.Empty<int>(),
            Took = m.Took[seat], Time = m.TotalTime[seat], Daily = daily, Version = m.Version, Ack = ack,
        };
    }
}
