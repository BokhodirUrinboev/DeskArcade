using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static DeskArcade.Games.SolitaireRules;

namespace DeskArcade.Games;

/// <summary>
/// A simple FreeCell player that looks ahead: a best-first search over positions, most promising first (cards home,
/// the next cards each suit needs near the top, few free cells in use, cascades in order), trying every legal move
/// from each and skipping positions it has seen. After each move the cards no longer needed go home by themselves, as
/// in the game (<see cref="FreeCellRules.SafeHome"/>). It gives up after a number of positions, so a deal it cannot
/// see through quickly counts as unsolved. Used by the demo and by the tests that play numbered deals through.
/// </summary>
public static class FreeCellSolver
{
    sealed class Node
    {
        public required byte[][] Cols;
        public required sbyte[] Cells;
        public required byte[] Home;
        public Node? Parent;
        public Move Move;
        public int Depth;
    }

    /// <summary>
    /// The moves that solve the deal from where it stands (each to be followed by the safe cards going home, as the game
    /// does), or null when none turned up within <paramref name="maxPositions"/> positions.
    /// </summary>
    public static List<Move>? Solve(FreeCellRules rules, int maxPositions = 60_000)
    {
        var start = new Node
        {
            Cols = Enumerable.Range(0, FreeCellRules.Cascades).Select(c => rules.Cards(Spot.Tableau(c)).Select(x => (byte)x).ToArray()).ToArray(),
            Cells = Enumerable.Range(0, FreeCellRules.FreeCells).Select(c => rules.Cards(Spot.Cell(c)) is { Count: 1 } cell ? (sbyte)cell[0] : (sbyte)-1).ToArray(),
            Home = Enumerable.Range(0, Suits).Select(s => (byte)rules.Cards(Spot.Foundation(s)).Count).ToArray(),
        };
        Settle(start);
        var seen = new HashSet<string> { Key(start) };
        var open = new PriorityQueue<Node, int>();
        open.Enqueue(start, Score(start));
        int expanded = 0;
        while (open.TryDequeue(out var node, out _))
        {
            if (Won(node)) return Path(node);
            if (++expanded > maxPositions) break;
            foreach (var child in Children(node))
            {
                if (!seen.Add(Key(child))) continue;
                if (Won(child)) return Path(child);
                open.Enqueue(child, Score(child));
            }
        }
        return null;
    }

    static bool Won(Node n) => n.Home.Sum(h => h) == DeckSize;

    static List<Move> Path(Node n)
    {
        var moves = new List<Move>();
        for (var at = n; at.Parent != null; at = at.Parent) moves.Add(at.Move);
        moves.Reverse();
        return moves;
    }

    /// <summary>Lower is better: cards still out, how deep the next card of each suit lies, cells in use, cards out of order.</summary>
    static int Score(Node n)
    {
        int home = n.Home.Sum(h => h), cells = n.Cells.Count(c => c >= 0), empty = n.Cols.Count(c => c.Length == 0);
        int depth = 0, disorder = 0;
        foreach (var col in n.Cols)
        {
            for (int i = 0; i < col.Length; i++)
            {
                int card = col[i];
                if (Rank(card) == n.Home[Suit(card)] + 1) depth += col.Length - 1 - i;
                if (i > 0 && Rank(card) >= Rank(col[i - 1])) disorder++;
            }
        }
        return (DeckSize - home) * 6 + depth * 2 + disorder * 3 + cells * 2 - empty * 3 + n.Depth / 4;
    }

    static string Key(Node n)
    {
        var cols = n.Cols.Select(c => Encoding.Latin1.GetString(c)).OrderBy(s => s, StringComparer.Ordinal);
        var sb = new StringBuilder(80);
        foreach (string c in cols) sb.Append(c).Append('/');
        foreach (var c in n.Cells.Where(c => c >= 0).OrderBy(c => c)) sb.Append((char)(c + 1));
        sb.Append('|');
        foreach (var h in n.Home) sb.Append((char)(h + 1));
        return sb.ToString();
    }

    static int RunLength(byte[] col)
    {
        if (col.Length == 0) return 0;
        int n = 1;
        while (n < col.Length && FreeCellRules.Builds(col[^n], col[^(n + 1)])) n++;
        return n;
    }

    static IEnumerable<Node> Children(Node n)
    {
        int free = n.Cells.Count(c => c < 0), empties = n.Cols.Count(c => c.Length == 0);
        int firstCell = Array.IndexOf(n.Cells, (sbyte)-1);
        int firstEmpty = Array.FindIndex(n.Cols, c => c.Length == 0);

        for (int s = 0; s < n.Cols.Length; s++)
        {
            var col = n.Cols[s];
            if (col.Length == 0) continue;
            int top = col[^1], run = RunLength(col);
            if (Rank(top) == n.Home[Suit(top)] + 1) yield return Child(n, new Move(Spot.Tableau(s), 1, Spot.Foundation(Suit(top))));
            for (int t = 0; t < n.Cols.Length; t++)
            {
                if (t == s || n.Cols[t].Length == 0) continue;
                int under = n.Cols[t][^1];
                int k = Rank(under) - Rank(top); // the run's card that goes on "under" is k cards down
                if (k < 1 || k > run || k > FreeCellRules.MaxMoveFor(free, empties)) continue;
                if (!FreeCellRules.Builds(col[^k], under)) continue;
                yield return Child(n, new Move(Spot.Tableau(s), k, Spot.Tableau(t)));
            }
            if (firstEmpty >= 0)
            {
                int most = Math.Min(run, FreeCellRules.MaxMoveFor(free, empties - 1));
                for (int k = most; k >= 1; k--)
                    if (k < col.Length) yield return Child(n, new Move(Spot.Tableau(s), k, Spot.Tableau(firstEmpty)));
            }
            if (firstCell >= 0) yield return Child(n, new Move(Spot.Tableau(s), 1, Spot.Cell(firstCell)));
        }
        for (int c = 0; c < n.Cells.Length; c++)
        {
            int card = n.Cells[c];
            if (card < 0) continue;
            if (Rank(card) == n.Home[Suit(card)] + 1) yield return Child(n, new Move(Spot.Cell(c), 1, Spot.Foundation(Suit(card))));
            for (int t = 0; t < n.Cols.Length; t++)
                if (n.Cols[t].Length > 0 && FreeCellRules.Builds(card, n.Cols[t][^1])) yield return Child(n, new Move(Spot.Cell(c), 1, Spot.Tableau(t)));
            if (firstEmpty >= 0) yield return Child(n, new Move(Spot.Cell(c), 1, Spot.Tableau(firstEmpty)));
        }
    }

    static Node Child(Node n, Move m)
    {
        var child = new Node { Cols = (byte[][])n.Cols.Clone(), Cells = (sbyte[])n.Cells.Clone(), Home = (byte[])n.Home.Clone(), Parent = n, Move = m, Depth = n.Depth + 1 };
        var moving = Take(child, m.From, m.Count);
        Put(child, m.To, moving);
        Settle(child);
        return child;
    }

    static byte[] Take(Node n, Spot from, int count)
    {
        if (from.Zone == Zone.Cell)
        {
            var card = new[] { (byte)n.Cells[from.Index] };
            n.Cells[from.Index] = -1;
            return card;
        }
        var col = n.Cols[from.Index];
        n.Cols[from.Index] = col[..^count];
        return col[^count..];
    }

    static void Put(Node n, Spot to, byte[] cards)
    {
        switch (to.Zone)
        {
            case Zone.Cell:
                n.Cells[to.Index] = (sbyte)cards[0];
                break;
            case Zone.Foundation:
                n.Home[to.Index]++;
                break;
            default:
                n.Cols[to.Index] = n.Cols[to.Index].Concat(cards).ToArray();
                break;
        }
    }

    /// <summary>The safe cards go home, as <see cref="FreeCellRules.SafeHome"/> sends them.</summary>
    static void Settle(Node n)
    {
        var home = new int[Suits];
        bool moved = true;
        while (moved)
        {
            moved = false;
            for (int s = 0; s < Suits; s++) home[s] = n.Home[s];
            for (int c = 0; c < n.Cells.Length; c++)
            {
                int card = n.Cells[c];
                if (card < 0 || Rank(card) != n.Home[Suit(card)] + 1 || !FreeCellRules.Safe(card, home)) continue;
                n.Cells[c] = -1;
                n.Home[Suit(card)]++;
                moved = true;
                break;
            }
            if (moved) continue;
            for (int s = 0; s < n.Cols.Length; s++)
            {
                var col = n.Cols[s];
                if (col.Length == 0) continue;
                int card = col[^1];
                if (Rank(card) != n.Home[Suit(card)] + 1 || !FreeCellRules.Safe(card, home)) continue;
                n.Cols[s] = col[..^1];
                n.Home[Suit(card)]++;
                moved = true;
                break;
            }
        }
    }

    /// <summary>Plays out the safe cards on a real deal the way the game does after each move.</summary>
    public static void Settle(FreeCellRules rules)
    {
        while (rules.SafeHome() is { } m) rules.Autoplay(m);
    }
}

/// <summary>
/// A simple Spider player that plays each stretch between deals as well as it can see: a best-first search over the
/// positions reachable without dealing (suits taken off, few face-down cards, long runs of one suit, empty columns),
/// then it deals from the best of them that has no empty column, and never goes back past a deal. It sees the
/// face-down cards, as a player with unlimited undo would learn them, and gives up after a number of positions.
/// </summary>
public static class SpiderSolver
{
    sealed class Node
    {
        public required byte[][] Cols;
        public required int[] Hidden;
        public int Dealt, Done, Depth;
        public Node? Parent;
        public Move? Move; // null for a deal
    }

    /// <summary>The moves that win from where the game stands (a null entry is a click on the stock), or null.</summary>
    public static List<Move?>? Solve(SpiderRules rules, int positionsPerDeal = 4_000)
    {
        int suits = rules.Suits;
        var stock = rules.Stock.Select(c => (byte)c).ToArray(); // dealt from the end
        var node = new Node
        {
            Cols = Enumerable.Range(0, SpiderRules.Columns).Select(c => rules.Cards(Spot.Tableau(c)).Select(x => (byte)x).ToArray()).ToArray(),
            Hidden = Enumerable.Range(0, SpiderRules.Columns).Select(c => rules.Hidden(Spot.Tableau(c))).ToArray(),
            Done = rules.SuitsDone,
        };
        while (true)
        {
            var (won, best) = Stretch(node, suits, stock, positionsPerDeal);
            if (won != null) return Path(won);
            if (best == null || (best.Dealt + 1) * SpiderRules.Columns > stock.Length) return null;
            node = Deal(best, suits, stock);
        }
    }

    /// <summary>Searches the positions reachable without a deal: a win if there is one, else the best to deal from.</summary>
    static (Node? Won, Node? Best) Stretch(Node start, int suits, byte[] stock, int budget)
    {
        if (start.Done == SpiderRules.Suits13) return (start, null);
        var seen = new HashSet<string> { Key(start, suits) };
        var open = new PriorityQueue<Node, int>();
        open.Enqueue(start, Score(start, suits));
        Node? best = null;
        int bestValue = int.MaxValue, expanded = 0;
        while (open.TryDequeue(out var node, out int score) && expanded++ < budget)
        {
            if (!node.Cols.Any(c => c.Length == 0) && score < bestValue)
            {
                bestValue = score;
                best = node;
            }
            foreach (var child in Moves(node, suits))
            {
                if (!seen.Add(Key(child, suits))) continue;
                if (child.Done == SpiderRules.Suits13) return (child, null);
                open.Enqueue(child, Score(child, suits));
            }
        }
        return (null, best);
    }

    static List<Move?> Path(Node n)
    {
        var moves = new List<Move?>();
        for (var at = n; at.Parent != null; at = at.Parent) moves.Add(at.Move);
        moves.Reverse();
        return moves;
    }

    static int Suit(int suits, int card) => SpiderRules.SuitOf(suits, card);

    /// <summary>Lower is better: suits still out, face-down cards, cards not on their next rank in suit, and empty columns count for it.</summary>
    static int Score(Node n, int suits)
    {
        int hidden = n.Hidden.Sum(), breaks = 0, loose = 0, empty = 0;
        for (int c = 0; c < n.Cols.Length; c++)
        {
            var col = n.Cols[c];
            if (col.Length == 0) { empty++; continue; }
            for (int i = n.Hidden[c] + 1; i < col.Length; i++)
            {
                bool rank = SpiderRules.RankOf(col[i - 1]) == SpiderRules.RankOf(col[i]) + 1;
                if (!rank) breaks++;
                else if (Suit(suits, col[i]) != Suit(suits, col[i - 1])) loose++;
            }
        }
        return (SpiderRules.Suits13 - n.Done) * 60 + hidden * 8 + breaks * 4 + loose * 2 - empty * 5 + n.Depth / 8;
    }

    static string Key(Node n, int suits)
    {
        var sb = new StringBuilder(160);
        sb.Append((char)(n.Dealt + 1)).Append((char)(n.Done + 1));
        for (int c = 0; c < n.Cols.Length; c++)
        {
            sb.Append('/').Append((char)(n.Hidden[c] + 1));
            for (int i = n.Hidden[c]; i < n.Cols[c].Length; i++) // cards alike in suit and rank are alike
                sb.Append((char)(Suit(suits, n.Cols[c][i]) * 13 + SpiderRules.RankOf(n.Cols[c][i]) + 1));
        }
        return sb.ToString();
    }

    static int Run(Node n, int suits, int c)
    {
        var col = n.Cols[c];
        int up = col.Length - n.Hidden[c];
        if (up <= 0) return 0;
        int k = 1;
        while (k < up && Suit(suits, col[^k]) == Suit(suits, col[^(k + 1)]) && SpiderRules.RankOf(col[^(k + 1)]) == SpiderRules.RankOf(col[^k]) + 1) k++;
        return k;
    }

    static IEnumerable<Node> Moves(Node n, int suits)
    {
        int firstEmpty = Array.FindIndex(n.Cols, c => c.Length == 0);
        for (int s = 0; s < n.Cols.Length; s++)
        {
            var col = n.Cols[s];
            int run = Run(n, suits, s);
            if (run == 0) continue;
            for (int t = 0; t < n.Cols.Length; t++)
            {
                if (t == s || n.Cols[t].Length == 0) continue;
                int k = SpiderRules.RankOf(n.Cols[t][^1]) - SpiderRules.RankOf(col[^1]);
                if (k < 1 || k > run) continue;
                // moving part of a run off a card it already follows in suit gains nothing
                if (k < col.Length - n.Hidden[s] && Suit(suits, col[^(k + 1)]) == Suit(suits, col[^k]) && SpiderRules.RankOf(col[^(k + 1)]) == SpiderRules.RankOf(col[^k]) + 1
                    && Suit(suits, n.Cols[t][^1]) != Suit(suits, col[^k])) continue;
                yield return Child(n, suits, s, k, t);
            }
            if (firstEmpty >= 0)
                for (int k = run; k >= 1; k--)
                    if (k < col.Length) yield return Child(n, suits, s, k, firstEmpty);
        }
    }

    /// <summary>A click on the stock: a card onto every column, dealt from the stock's end.</summary>
    static Node Deal(Node n, int suits, byte[] stock)
    {
        var child = Copy(n);
        child.Move = null;
        for (int c = 0; c < SpiderRules.Columns; c++)
            child.Cols[c] = child.Cols[c].Append(stock[stock.Length - 1 - n.Dealt * SpiderRules.Columns - c]).ToArray();
        child.Dealt++;
        for (int c = 0; c < SpiderRules.Columns; c++) Collect(child, suits, c);
        return child;
    }

    static Node Copy(Node n) => new() { Cols = (byte[][])n.Cols.Clone(), Hidden = (int[])n.Hidden.Clone(), Dealt = n.Dealt, Done = n.Done, Depth = n.Depth + 1, Parent = n };

    static Node Child(Node n, int suits, int s, int k, int t)
    {
        var child = Copy(n);
        child.Move = new Move(Spot.Tableau(s), k, Spot.Tableau(t));
        var col = child.Cols[s];
        child.Cols[t] = child.Cols[t].Concat(col[^k..]).ToArray();
        child.Cols[s] = col[..^k];
        if (child.Hidden[s] > 0 && child.Hidden[s] >= child.Cols[s].Length) child.Hidden[s] = child.Cols[s].Length - 1;
        Collect(child, suits, t);
        return child;
    }

    static void Collect(Node n, int suits, int c)
    {
        if (Run(n, suits, c) < Ranks) return;
        n.Cols[c] = n.Cols[c][..^Ranks];
        n.Done++;
        if (n.Hidden[c] > 0 && n.Hidden[c] >= n.Cols[c].Length) n.Hidden[c] = n.Cols[c].Length - 1;
    }
}
