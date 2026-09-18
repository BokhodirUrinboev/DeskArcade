using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DeskArcade.Games;

/// <summary>
/// "Get N in a row" rules, free of UI: Tic-tac-toe (3×3, 3 in a row) and Connect Four (7×6, 4 in a row,
/// pieces drop to the lowest empty square of a column). Moves are [square]. Side +1 moves first.
/// </summary>
public sealed class LineRules : IBoardRules
{
    public int Cols { get; }
    public int Rows { get; }
    public int Need { get; }
    public bool Gravity { get; }
    public sbyte[] Board { get; }
    public int Turn { get; private set; } = 1;
    public int Ply { get; private set; }
    /// <summary>Chance per CPU move of playing a random square instead, so the CPU can be beaten.</summary>
    public double Blunder { get; init; }
    public int Depth { get; init; } = 9;

    int? _winner;

    public LineRules(int cols, int rows, int need, bool gravity)
    {
        Cols = cols;
        Rows = rows;
        Need = need;
        Gravity = gravity;
        Board = new sbyte[cols * rows];
    }

    public static LineRules TicTacToe() => new(3, 3, 3, false) { Blunder = 0.2 };

    public static LineRules ConnectFour() => new(7, 6, 4, true) { Depth = 6 };

    public int Alert => -1;

    public int Count(int side) => Board.Count(p => p == side);

    public int Result => Winner() is int w and not 0 ? w : Board.All(p => p != 0) ? 2 : 0;

    public List<int[]> LegalMoves()
    {
        var moves = new List<int[]>();
        if (Winner() != 0) return moves;
        if (Gravity)
        {
            for (int c = 0; c < Cols; c++)
                for (int r = Rows - 1; r >= 0; r--)
                    if (Board[r * Cols + c] == 0)
                    {
                        moves.Add(new[] { r * Cols + c });
                        break;
                    }
        }
        else
            for (int sq = 0; sq < Board.Length; sq++)
                if (Board[sq] == 0) moves.Add(new[] { sq });
        return moves;
    }

    public List<int> Apply(int[] move)
    {
        Board[move[0]] = (sbyte)Turn;
        Turn = -Turn;
        Ply++;
        _winner = null;
        return new List<int>();
    }

    void Undo(int sq)
    {
        Board[sq] = 0;
        Turn = -Turn;
        Ply--;
        _winner = null;
    }

    static readonly (int, int)[] Dirs = { (0, 1), (1, 0), (1, 1), (1, -1) };

    /// <summary>The side with <see cref="Need"/> in a row, or 0.</summary>
    public int Winner()
    {
        if (_winner is int known) return known;
        for (int sq = 0; sq < Board.Length; sq++)
        {
            int side = Board[sq];
            if (side == 0) continue;
            foreach (var (dr, dc) in Dirs)
            {
                int n = 1, r = sq / Cols + dr, c = sq % Cols + dc;
                while (n < Need && r >= 0 && r < Rows && c >= 0 && c < Cols && Board[r * Cols + c] == side)
                {
                    n++;
                    r += dr;
                    c += dc;
                }
                if (n == Need) return (_winner = side).Value;
            }
        }
        return (_winner = 0).Value;
    }

    public string Encode()
    {
        var sb = new StringBuilder(Board.Length + 12);
        foreach (sbyte p in Board) sb.Append(p > 0 ? 'x' : p < 0 ? 'o' : '.');
        return sb.Append(Turn > 0 ? "|x|" : "|o|").Append(Ply).ToString();
    }

    /// <summary>Reads <see cref="Encode"/> output into a board shaped like <paramref name="shape"/>.</summary>
    public static LineRules? Decode(string text, LineRules shape)
    {
        var f = text.Split('|');
        if (f.Length != 3 || f[0].Length != shape.Board.Length || !int.TryParse(f[2], out int ply)) return null;
        var d = new LineRules(shape.Cols, shape.Rows, shape.Need, shape.Gravity) { Blunder = shape.Blunder, Depth = shape.Depth };
        d.Turn = f[1] == "o" ? -1 : 1;
        d.Ply = ply;
        for (int i = 0; i < d.Board.Length; i++) d.Board[i] = (sbyte)(f[0][i] == 'x' ? 1 : f[0][i] == 'o' ? -1 : 0);
        return d;
    }

    // ------------------------------------------------------------------ computer player

    public int[] BestMove(Random rng)
    {
        var moves = LegalMoves();
        if (rng.NextDouble() < Blunder) return moves[rng.Next(moves.Count)];
        int[] pick = moves[0];
        double best = double.NegativeInfinity;
        foreach (var m in Ordered(moves).OrderBy(_ => rng.Next()).OrderBy(m => Math.Abs(m[0] % Cols - Cols / 2)).ToList())
        {
            Apply(m);
            // a move that can't beat the best comes back as exactly -best, so only a strictly better one wins
            double score = -Search(Depth - 1, -1e9, -best);
            Undo(m[0]);
            if (score > best)
            {
                best = score;
                pick = m;
            }
        }
        return pick;
    }

    double Search(int depth, double alpha, double beta)
    {
        if (Winner() != 0) return -1000 - depth; // the side that just moved won; sooner is worse for us
        var moves = LegalMoves();
        if (moves.Count == 0) return 0;
        if (depth <= 0) return Evaluate();
        foreach (var m in Ordered(moves))
        {
            Apply(m);
            alpha = Math.Max(alpha, -Search(depth - 1, -beta, -alpha));
            Undo(m[0]);
            if (alpha >= beta) break;
        }
        return alpha;
    }

    IEnumerable<int[]> Ordered(List<int[]> moves) => moves.OrderBy(m => Math.Abs(m[0] % Cols - (Cols - 1) / 2.0));

    /// <summary>Open lines from the side to move's point of view: nearly-complete lines score most.</summary>
    double Evaluate()
    {
        double s = 0;
        for (int sq = 0; sq < Board.Length; sq++)
            foreach (var (dr, dc) in Dirs)
            {
                int endR = sq / Cols + dr * (Need - 1), endC = sq % Cols + dc * (Need - 1);
                if (endR < 0 || endR >= Rows || endC < 0 || endC >= Cols) continue;
                int mine = 0, theirs = 0;
                for (int k = 0; k < Need; k++)
                {
                    int p = Board[(sq / Cols + dr * k) * Cols + sq % Cols + dc * k];
                    if (p == Turn) mine++;
                    else if (p == -Turn) theirs++;
                }
                if (theirs == 0) s += mine == Need - 1 ? 5 : mine == Need - 2 ? 2 : 0;
                if (mine == 0) s -= theirs == Need - 1 ? 5 : theirs == Need - 2 ? 2 : 0;
            }
        return s;
    }
}
