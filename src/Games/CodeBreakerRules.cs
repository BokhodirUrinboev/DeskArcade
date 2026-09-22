using System;
using System.Collections.Generic;

namespace DeskArcade.Games;

/// <summary>
/// The rules of Code Breaker (Mastermind), free of UI: a hidden code of <see cref="Pegs"/> pegs in
/// <see cref="Colors"/> colours (repeats allowed) and <see cref="MaxGuesses"/> guesses. Each guess is scored
/// with black pins (right colour, right place) and white pins (right colour, wrong place).
/// </summary>
public sealed class CodeBreakerRules
{
    public const int Pegs = 4, Colors = 6, MaxGuesses = 10;
    /// <summary>Every possible code: 6⁴.</summary>
    public const int Codes = 1296;

    readonly List<int[]> _guesses = new();
    readonly List<(int Black, int White)> _feedback = new();

    public CodeBreakerRules(Random rng) : this(RandomCode(rng)) { }

    public CodeBreakerRules(int[] code)
    {
        Check(code);
        Code = (int[])code.Clone();
    }

    public int[] Code { get; }
    public IReadOnlyList<int[]> Guesses => _guesses;
    public IReadOnlyList<(int Black, int White)> Feedback => _feedback;
    public bool Won { get; private set; }
    public bool Lost => !Won && _guesses.Count >= MaxGuesses;
    public bool Over => Won || Lost;

    public static int[] RandomCode(Random rng)
    {
        var code = new int[Pegs];
        for (int i = 0; i < Pegs; i++) code[i] = rng.Next(Colors);
        return code;
    }

    /// <summary>
    /// The standard count: blacks are exact positions; whites are, per colour, the smaller of how often it is
    /// left over in the code and in the guess, so a repeated colour never scores more pins than it has.
    /// </summary>
    public static (int Black, int White) Score(int[] code, int[] guess)
    {
        int black = 0, white = 0;
        Span<int> inCode = stackalloc int[Colors], inGuess = stackalloc int[Colors];
        for (int i = 0; i < Pegs; i++)
        {
            if (code[i] == guess[i]) black++;
            else
            {
                inCode[code[i]]++;
                inGuess[guess[i]]++;
            }
        }
        for (int c = 0; c < Colors; c++) white += Math.Min(inCode[c], inGuess[c]);
        return (black, white);
    }

    /// <summary>Scores a full guess and records it; the game is won on four blacks.</summary>
    public (int Black, int White) Submit(int[] guess)
    {
        if (Over) throw new InvalidOperationException("the game is over");
        Check(guess);
        var fb = Score(Code, guess);
        _guesses.Add((int[])guess.Clone());
        _feedback.Add(fb);
        if (fb.Black == Pegs) Won = true;
        return fb;
    }

    public static int Encode(int[] code)
    {
        int n = 0;
        for (int i = 0; i < Pegs; i++) n = n * Colors + code[i];
        return n;
    }

    public static int[] Decode(int n)
    {
        var code = new int[Pegs];
        for (int i = Pegs - 1; i >= 0; i--, n /= Colors) code[i] = n % Colors;
        return code;
    }

    static void Check(int[] code)
    {
        if (code.Length != Pegs) throw new ArgumentException($"a code has {Pegs} pegs", nameof(code));
        foreach (int c in code)
            if (c < 0 || c >= Colors) throw new ArgumentOutOfRangeException(nameof(code), "unknown colour");
    }
}

/// <summary>
/// Knuth's minimax solver: keep every code still consistent with the pins so far, and guess the code whose
/// worst-case answer leaves the fewest of them (preferring one that could itself be the answer). It never
/// needs more than five guesses.
/// </summary>
public sealed class CodeBreakerSolver
{
    const int Codes = CodeBreakerRules.Codes, Answers = (CodeBreakerRules.Pegs + 1) * (CodeBreakerRules.Pegs + 1);

    /// <summary>The answer for every pair of codes, as black × 5 + white; built once, shared.</summary>
    static readonly Lazy<byte[]> Table = new(() =>
    {
        var t = new byte[Codes * Codes];
        var codes = new int[Codes][];
        for (int i = 0; i < Codes; i++) codes[i] = CodeBreakerRules.Decode(i);
        for (int a = 0; a < Codes; a++)
            for (int b = 0; b < Codes; b++)
            {
                var (black, white) = CodeBreakerRules.Score(codes[a], codes[b]);
                t[a * Codes + b] = (byte)(black * (CodeBreakerRules.Pegs + 1) + white);
            }
        return t;
    });

    readonly List<int> _left = new();
    readonly bool[] _possible = new bool[Codes];
    int _guesses;

    public CodeBreakerSolver()
    {
        for (int i = 0; i < Codes; i++)
        {
            _left.Add(i);
            _possible[i] = true;
        }
    }

    /// <summary>How many codes still fit every answer so far.</summary>
    public int Candidates => _left.Count;

    public int[] NextGuess()
    {
        if (_guesses == 0) return new[] { 0, 0, 1, 1 }; // Knuth's opening
        if (_left.Count == 0) return new[] { 0, 0, 1, 1 }; // only after answers no code could give
        if (_left.Count <= 2) return CodeBreakerRules.Decode(_left[0]);
        var table = Table.Value;
        Span<int> counts = stackalloc int[Answers];
        int best = -1, bestWorst = int.MaxValue;
        bool bestPossible = false;
        for (int g = 0; g < Codes; g++)
        {
            counts.Clear();
            int worst = 0, row = g * Codes;
            foreach (int c in _left)
            {
                int n = ++counts[table[row + c]];
                if (n > worst) worst = n;
            }
            if (worst < bestWorst || worst == bestWorst && _possible[g] && !bestPossible)
            {
                best = g;
                bestWorst = worst;
                bestPossible = _possible[g];
            }
        }
        return CodeBreakerRules.Decode(best);
    }

    /// <summary>Keeps only the codes that would have given this answer to this guess.</summary>
    public void Record(int[] guess, int black, int white)
    {
        _guesses++;
        int g = CodeBreakerRules.Encode(guess), answer = black * (CodeBreakerRules.Pegs + 1) + white;
        var table = Table.Value;
        _left.RemoveAll(c =>
        {
            if (table[g * Codes + c] == answer) return false;
            _possible[c] = false;
            return true;
        });
    }
}
