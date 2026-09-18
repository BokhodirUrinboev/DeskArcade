namespace DeskArcade.Games;

/// <summary>
/// Mini Golf match play over the LAN, free of UI. Each player putts on their own screen (the courses
/// differ, since cups sit on each person's windows), taking turns stroke by stroke; a player who has holed
/// out waits for the other. A hole goes to whoever finished it with the better score against their own
/// par; after <see cref="Holes"/> holes the most holes won takes the match, then the better total.
/// </summary>
public sealed class GolfMatch
{
    public const int Holes = 9;

    readonly bool _iStartOdd;
    bool _myTurn;

    /// <param name="iStartOddHoles">Whether this player putts first on holes 1, 3, 5…; the other starts the even ones.</param>
    public GolfMatch(bool iStartOddHoles)
    {
        _iStartOdd = iStartOddHoles;
        _myTurn = iStartOddHoles;
    }

    public int Hole { get; private set; } = 1;
    public bool Over { get; private set; }
    public bool MySunk { get; private set; }
    public bool TheirSunk { get; private set; }
    public int MyStrokes { get; private set; }
    public int TheirStrokes { get; private set; }
    public int MyHoles { get; private set; }
    public int TheirHoles { get; private set; }
    /// <summary>Totals against par over the holes played so far (lower is better).</summary>
    public int MyTotal { get; private set; }
    public int TheirTotal { get; private set; }

    public bool MyTurn => !Over && !MySunk && (_myTurn || TheirSunk);

    /// <summary>True if I won, false if they did, null for a tie (or while the match is on).</summary>
    public bool? Won => !Over ? null
        : MyHoles != TheirHoles ? MyHoles > TheirHoles
        : MyTotal != TheirTotal ? MyTotal < TheirTotal
        : null;

    int _myPar, _theirPar;

    /// <summary>
    /// Records a finished stroke: the player's stroke count on this hole so far, whether it went in, and
    /// the par of that player's hole. Returns the hole's result once both players have holed out
    /// (+1 I won it, −1 they did, 0 halved), else null.
    /// </summary>
    public int? Stroke(bool byMe, int strokes, bool sunk, int par)
    {
        if (Over) return null;
        if (byMe)
        {
            MyStrokes = strokes;
            MySunk |= sunk;
            _myPar = par;
            _myTurn = TheirSunk; // over to them, unless they are already in
        }
        else
        {
            TheirStrokes = strokes;
            TheirSunk |= sunk;
            _theirPar = par;
            _myTurn = !MySunk;
        }
        if (!MySunk || !TheirSunk) return null;

        int mine = MyStrokes - _myPar, theirs = TheirStrokes - _theirPar;
        MyTotal += mine;
        TheirTotal += theirs;
        int result = mine < theirs ? 1 : mine > theirs ? -1 : 0;
        if (result > 0) MyHoles++;
        else if (result < 0) TheirHoles++;

        if (Hole >= Holes) Over = true;
        else
        {
            Hole++;
            MySunk = TheirSunk = false;
            MyStrokes = TheirStrokes = 0;
            _myTurn = (Hole % 2 == 1) == _iStartOdd;
        }
        return result;
    }
}

/// <summary>
/// Archery over the LAN, free of UI: the players take turns, one arrow each, <see cref="Arrows"/> arrows
/// apiece, at their own targets in the same wind. The higher total wins.
/// </summary>
public sealed class ArcheryMatch
{
    public const int Arrows = 10;

    bool _myTurn;

    public ArcheryMatch(bool iShootFirst) => _myTurn = iShootFirst;

    public int MyLeft { get; private set; } = Arrows;
    public int TheirLeft { get; private set; } = Arrows;
    public int MyScore { get; private set; }
    public int TheirScore { get; private set; }

    public bool Over => MyLeft == 0 && TheirLeft == 0;
    public bool MyTurn => !Over && MyLeft > 0 && (_myTurn || TheirLeft == 0);

    /// <summary>True if I won, false if they did, null for a tie (or while the match is on).</summary>
    public bool? Won => !Over || MyScore == TheirScore ? null : MyScore > TheirScore;

    /// <summary>Records one arrow and what it scored; the turn passes to the other player.</summary>
    public void Arrow(bool byMe, int points)
    {
        if (byMe)
        {
            if (MyLeft == 0) return;
            MyLeft--;
            MyScore += points;
            _myTurn = false;
        }
        else
        {
            if (TheirLeft == 0) return;
            TheirLeft--;
            TheirScore += points;
            _myTurn = true;
        }
    }
}
