using System.Collections.Generic;

namespace DeskArcade;

/// <summary>
/// Translations, keyed by the exact English text passed to <see cref="L.T"/> or <see cref="L.F"/>.
/// Keep {0}-style placeholders, "·" separators and "×" intact, and keep the number last in "Best {0}"-style
/// strings (the compact scoreboard shows only the last word). Missing entries show in English.
/// The tables live in Strings.Uzbek.cs and Strings.Russian.cs.
/// </summary>
public static partial class Strings
{
    public static IReadOnlyDictionary<string, string> Uzbek => UzbekTable;

    public static IReadOnlyDictionary<string, string> Russian => RussianTable;
}
