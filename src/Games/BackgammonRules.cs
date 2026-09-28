using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DeskArcade.Games;

/// <summary>
/// One checker move in the mover's own frame: points are counted 1–24 from the mover's home end, <see cref="BackgammonRules.BarSpot"/>
/// (25) is the bar and <see cref="BackgammonRules.OffSpot"/> (0) is borne off. <see cref="Die"/> is the die it uses.
/// </summary>
public readonly record struct BgStep(int From, int To, int Die);

/// <summary>
/// Backgammon: fifteen checkers a side race round the 24 points in opposite directions to their home boards and off.
/// Roll two dice and move a checker by each (four times on doubles); a point held by two or more of the other side's
/// checkers is closed, a lone one (a blot) is hit and goes to the bar, and must come in again on the other side's home
/// board before its owner moves anything else. With every checker home you bear off; a die higher than the furthest
/// checker takes it off. You must use both dice if you can (or the larger, when only one can be used). First to bear off
/// all fifteen wins: double (a gammon) if the other side has borne off none, triple (a backgammon) if one of theirs is
/// still on the bar or in the winner's home board. Stored by absolute point 0–23 (side 0 moves down to 0, side 1 up to
/// 23), with side 0's checkers positive. UI-free, so the rules and the computer player can be tested.
/// </summary>
public sealed class BackgammonRules
{
    public const int Points = 24, Checkers = 15, BarSpot = 25, OffSpot = 0, HomeSize = 6;

    readonly int[] _pts = new int[Points];
    readonly int[] _bar = new int[2], _off = new int[2];
    readonly List<int> _dice = new();
    List<List<BgStep>>? _plays;
    List<BgStep>? _first;

    public BackgammonRules()
    {
        // side 0: two on its 24-point, five on its 13, three on its 8, five on its 6; side 1 the mirror image
        Put(0, 24, 2);
        Put(0, 13, 5);
        Put(0, 8, 3);
        Put(0, 6, 5);
        Put(1, 24, 2);
        Put(1, 13, 5);
        Put(1, 8, 3);
        Put(1, 6, 5);
    }

    BackgammonRules(BackgammonRules o)
    {
        Array.Copy(o._pts, _pts, Points);
        Array.Copy(o._bar, _bar, 2);
        Array.Copy(o._off, _off, 2);
        _dice.AddRange(o._dice);
        Turn = o.Turn;
        Rolled = o.Rolled;
        Over = o.Over;
        Ply = o.Ply;
    }

    /// <summary>A position for tests: <paramref name="own"/> and <paramref name="other"/> map points (in each side's own frame) to checkers.</summary>
    public static BackgammonRules Setup(int turn, IReadOnlyDictionary<int, int> own, IReadOnlyDictionary<int, int> other, int ownBar = 0, int otherBar = 0)
    {
        var g = new BackgammonRules();
        Array.Clear(g._pts);
        foreach (var (p, n) in own) g.Put(turn, p, n);
        foreach (var (p, n) in other) g.Put(1 - turn, p, n);
        g._bar[turn] = ownBar;
        g._bar[1 - turn] = otherBar;
        g._off[turn] = Checkers - own.Values.Sum() - ownBar;
        g._off[1 - turn] = Checkers - other.Values.Sum() - otherBar;
        g.Turn = turn;
        return g;
    }

    public BackgammonRules Clone() => new(this);

    /// <summary>Who opens the game: only before anything has been rolled.</summary>
    public void Opens(int side)
    {
        if (Ply == 0 && !Rolled) Turn = side;
    }

    /// <summary>The side to move: 0 or 1.</summary>
    public int Turn { get; private set; }

    /// <summary>True once the side to move has rolled; its <see cref="Dice"/> are the ones still to play.</summary>
    public bool Rolled { get; private set; }

    public bool Over { get; private set; }

    /// <summary>Rolls and steps played, for keeping two screens in step.</summary>
    public int Ply { get; private set; }

    public IReadOnlyList<int> Dice => _dice;

    public int OnBar(int side) => _bar[side];
    public int BorneOff(int side) => _off[side];

    /// <summary>The winner once <see cref="Over"/>, else -1.</summary>
    public int Winner => !Over ? -1 : _off[0] == Checkers ? 0 : 1;

    /// <summary>1 for a plain win, 2 for a gammon, 3 for a backgammon (0 while the game goes on).</summary>
    public int WinKind
    {
        get
        {
            if (!Over) return 0;
            int loser = 1 - Winner;
            if (_off[loser] > 0) return 1;
            bool deep = _bar[loser] > 0 || Enumerable.Range(Points - HomeSize + 1, HomeSize).Any(r => Count(loser, r) > 0); // in the winner's home
            return deep ? 3 : 2;
        }
    }

    /// <summary>The absolute point (0–23) of <paramref name="side"/>'s point <paramref name="r"/> (1–24).</summary>
    public static int Abs(int side, int r) => side == 0 ? r - 1 : Points - r;

    /// <summary><paramref name="side"/>'s checkers on its point <paramref name="r"/> (1–24; 25 is the bar).</summary>
    public int Count(int side, int r)
    {
        if (r == BarSpot) return _bar[side];
        if (r < 1 || r > Points) return 0;
        int v = _pts[Abs(side, r)];
        return side == 0 ? Math.Max(0, v) : Math.Max(0, -v);
    }

    /// <summary>Checkers on absolute point <paramref name="i"/>: positive for side 0, negative for side 1.</summary>
    public int this[int i] => _pts[i];

    void Put(int side, int r, int n) => _pts[Abs(side, r)] += side == 0 ? n : -n;

    /// <summary>How far <paramref name="side"/> still has to go: the sum of its checkers' distances home and off.</summary>
    public int Pips(int side)
    {
        int sum = _bar[side] * BarSpot;
        for (int r = 1; r <= Points; r++) sum += Count(side, r) * r;
        return sum;
    }

    // ------------------------------------------------------------------ turns

    /// <summary>
    /// The side to move rolls <paramref name="a"/> and <paramref name="b"/> (doubles play four times). Returns false when
    /// nothing can be moved: the turn has then passed.
    /// </summary>
    public bool Roll(int a, int b)
    {
        if (Over || Rolled || a is < 1 or > 6 || b is < 1 or > 6) return false;
        _dice.Clear();
        _dice.AddRange(a == b ? new[] { a, a, a, a } : new[] { Math.Max(a, b), Math.Min(a, b) });
        Rolled = true;
        Ply++;
        Forget();
        if (LegalSteps().Count > 0) return true;
        Pass();
        return false;
    }

    void Pass()
    {
        _dice.Clear();
        Rolled = false;
        Forget();
        Turn = 1 - Turn;
    }

    /// <summary>What one step did, for the drawing.</summary>
    public readonly record struct StepResult(bool Hit, bool BoreOff, bool TurnOver, bool Won);

    /// <summary>Plays a step if it is legal (see <see cref="LegalSteps"/>); null otherwise. The turn passes when the dice are used or stuck.</summary>
    public StepResult? Play(BgStep step)
    {
        if (Over || !Rolled || !LegalSteps().Contains(step)) return null;
        int side = Turn;
        bool hit = Apply(side, step);
        _dice.Remove(step.Die);
        Ply++;
        Forget();
        if (_off[side] == Checkers)
        {
            Over = true;
            _dice.Clear();
            return new StepResult(hit, true, true, true);
        }
        bool over = _dice.Count == 0 || LegalSteps().Count == 0;
        if (over) Pass();
        return new StepResult(hit, step.To == OffSpot, over, false);
    }

    /// <summary>Moves a checker without asking whether it may: the search's and <see cref="Play"/>'s shared step.</summary>
    bool Apply(int side, BgStep s)
    {
        if (s.From == BarSpot) _bar[side]--;
        else Put(side, s.From, -1);
        if (s.To == OffSpot)
        {
            _off[side]++;
            return false;
        }
        bool hit = Count(1 - side, 25 - s.To) == 1;
        if (hit)
        {
            Put(1 - side, 25 - s.To, -1);
            _bar[1 - side]++;
        }
        Put(side, s.To, 1);
        return hit;
    }

    void Forget()
    {
        _plays = null;
        _first = null;
    }

    /// <summary>
    /// The steps the side to move may play now: every first step of the fullest plays its dice allow (in any order the
    /// dice can be taken: with 3-1, the 1 or the 3 first).
    /// </summary>
    public IReadOnlyList<BgStep> LegalSteps()
    {
        if (Over || !Rolled) return Array.Empty<BgStep>();
        if (_first == null) Explore();
        return _first!;
    }

    /// <summary>
    /// Every complete play of the dice still to move, one per distinct resulting position: only the fullest (as many
    /// dice as can be used; the larger die when only one can).
    /// </summary>
    public IReadOnlyList<List<BgStep>> Plays()
    {
        if (Over || !Rolled) return Array.Empty<List<BgStep>>();
        if (_plays == null) Explore();
        return _plays!;
    }

    sealed class Walk
    {
        public readonly Dictionary<string, List<BgStep>> Found = new();
        public readonly Dictionary<int, HashSet<BgStep>> First = new();
        public readonly HashSet<string> Seen = new();
        public int Most;
    }

    void Explore()
    {
        var w = new Walk();
        Search(this, new List<BgStep>(), w);
        var plays = w.Found.Where(kv => kv.Value.Count == w.Most).Select(kv => kv.Value).ToList();
        var first = w.First.TryGetValue(w.Most, out var f) ? f.ToList() : new List<BgStep>();
        if (w.Most == 1 && _dice.Count == 2 && _dice[0] != _dice[1] && first.Any(st => st.Die == _dice[0]))
        {
            // only one die can be played: it must be the larger, if that one can move
            plays = plays.Where(pl => pl[0].Die == _dice[0]).ToList();
            first = first.Where(st => st.Die == _dice[0]).ToList();
        }
        _plays = plays;
        _first = first;
    }

    static void Search(BackgammonRules g, List<BgStep> path, Walk w)
    {
        bool any = false;
        foreach (int die in g._dice.Distinct().ToList())
        {
            foreach (var step in g.StepsFor(die).ToList())
            {
                any = true;
                var next = g.Clone();
                next.Apply(g.Turn, step);
                next._dice.Remove(die);
                path.Add(step);
                // the same position with the same dice left, reached from the same first step, plays out the same
                if (w.Seen.Add($"{path[0].From},{path[0].To},{path[0].Die}|{next._dice.Count}|{next.Key()}")) Search(next, path, w);
                path.RemoveAt(path.Count - 1);
            }
        }
        if (any || path.Count < w.Most) return;
        w.Most = path.Count;
        w.Found.TryAdd(g.Key() + "#" + path.Count, new List<BgStep>(path)); // off with a 6, or 1 then off: one place, two lengths
        if (path.Count == 0) return;
        if (!w.First.TryGetValue(path.Count, out var set)) w.First[path.Count] = set = new HashSet<BgStep>();
        set.Add(path[0]);
    }

    /// <summary>The single steps the side to move could make with <paramref name="die"/>, whatever the other dice.</summary>
    IEnumerable<BgStep> StepsFor(int die)
    {
        int side = Turn;
        if (_bar[side] > 0)
        {
            if (Open(side, BarSpot - die)) yield return new BgStep(BarSpot, BarSpot - die, die);
            yield break;
        }
        bool allHome = AllHome(side);
        for (int r = Points; r >= 1; r--)
        {
            if (Count(side, r) == 0) continue;
            int to = r - die;
            if (to >= 1)
            {
                if (Open(side, to)) yield return new BgStep(r, to, die);
            }
            else if (allHome && (to == 0 || !Enumerable.Range(r + 1, HomeSize - r).Any(k => Count(side, k) > 0)))
                yield return new BgStep(r, OffSpot, die); // exactly off, or a higher die from the furthest checker
        }
    }

    bool AllHome(int side)
    {
        if (_bar[side] > 0) return false;
        for (int r = HomeSize + 1; r <= Points; r++)
            if (Count(side, r) > 0) return false;
        return true;
    }

    /// <summary>True when <paramref name="side"/> may land on its point <paramref name="r"/>: fewer than two of the other side's there.</summary>
    bool Open(int side, int r) => Count(1 - side, 25 - r) < 2;

    string Key()
    {
        var sb = new StringBuilder(80);
        foreach (int v in _pts) sb.Append(v).Append(',');
        return sb.Append(_bar[0]).Append(',').Append(_bar[1]).Append(',').Append(_off[0]).Append(',').Append(_off[1]).ToString();
    }

    // ------------------------------------------------------------------ the computer

    /// <summary>
    /// The computer's play for the side to move, at <paramref name="level"/> 1 (Easy) to 4 (Expert): every complete play
    /// is scored by <see cref="Value"/>, with less noise the higher the level; Easy also plays at random now and then.
    /// </summary>
    public List<BgStep> BestPlay(int level, Random rng)
    {
        var plays = Plays();
        if (plays.Count == 0) return new List<BgStep>();
        if (level <= 1 && rng.NextDouble() < 0.4) return plays[rng.Next(plays.Count)];
        double noise = level switch { <= 1 => 6, 2 => 2.5, 3 => 0.6, _ => 0 };
        int side = Turn;
        List<BgStep> best = plays[0];
        double bestValue = double.NegativeInfinity;
        foreach (var play in plays)
        {
            var g = Clone();
            foreach (var s in play) g.Apply(side, s);
            double v = Value(g, side, level >= 4) + (noise > 0 ? (rng.NextDouble() - 0.5) * noise : 0);
            if (v > bestValue)
            {
                bestValue = v;
                best = play;
            }
        }
        return best;
    }

    /// <summary>
    /// How good a position is for <paramref name="me"/>: the race (pips), checkers off, the other side on the bar, points
    /// made (home points and a prime more), and minus the blots it could hit, costed by how far back a hit would send them.
    /// Once the two sides have passed each other it is a pure race.
    /// </summary>
    public static double Value(BackgammonRules g, int me, bool expert = false)
    {
        int them = 1 - me;
        double pips = g.Pips(them) - g.Pips(me);
        // contact: one of mine still behind one of theirs (my point r faces their point 25 - r)
        int myBack = g._bar[me] > 0 ? BarSpot : Enumerable.Range(1, Points).Where(r => g.Count(me, r) > 0).DefaultIfEmpty(0).Max();
        int theirBack = g._bar[them] > 0 ? BarSpot : Enumerable.Range(1, Points).Where(r => g.Count(them, r) > 0).DefaultIfEmpty(0).Max();
        bool contact = myBack + theirBack > 25;
        if (!contact) return pips * 1.0 + g._off[me] * 4;

        double v = pips * 0.12 + g._off[me] * 0.6 + g._bar[them] * 2.2 - g._bar[me] * 2.2;
        int run = 0, prime = 0;
        for (int r = 1; r <= Points; r++)
        {
            int n = g.Count(me, r);
            if (n >= 2)
            {
                v += r <= HomeSize ? 1.1 : r == 7 ? 0.9 : r >= 19 ? 0.5 : 0.35; // home points; the bar point; anchors in their home
                prime = Math.Max(prime, ++run);
                if (expert && n > 4) v -= 0.25 * (n - 4); // stacks waste checkers
            }
            else run = 0;
        }
        v += Math.Max(0, prime - 2) * 0.9;
        for (int r = 1; r <= Points; r++)
        {
            if (g.Count(me, r) != 1) continue;
            double shots = Shots(g, me, r);
            if (shots <= 0) continue;
            double chance = Math.Min(0.9, shots / 36.0);
            v -= chance * (1.2 + (BarSpot - r) * 0.12 * (expert ? 1.1 : 1));
        }
        return v;
    }

    /// <summary>
    /// Rolls (of 36) on which the other side could hit <paramref name="me"/>'s blot on its point <paramref name="r"/>: with
    /// one die from 1 to 6 points away, or both dice together up to 12 (counted roughly, points in between not checked).
    /// </summary>
    static double Shots(BackgammonRules g, int me, int r)
    {
        int them = 1 - me;
        var hits = new bool[37];
        // a checker of theirs on their point q moves down to reach their point 25 - r: it is (q - (25 - r)) away
        var distances = new List<int>();
        int target = BarSpot - r;
        if (g._bar[them] > 0) distances.Add(BarSpot - target);
        for (int q = target + 1; q <= Points; q++)
            if (g.Count(them, q) > 0) distances.Add(q - target);
        int count = 0;
        for (int a = 1; a <= 6; a++)
        for (int b = 1; b <= 6; b++)
        {
            bool hit = distances.Any(d => d == a || d == b || d == a + b || (a == b && (d == 3 * a || d == 4 * a)));
            if (hit) count++;
        }
        return count;
    }
}
