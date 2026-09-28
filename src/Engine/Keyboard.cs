namespace DeskArcade.Engine;

/// <summary>The keys a typing game cares about beside the characters themselves.</summary>
public enum TypingKey { Backspace, WordBackspace, Enter, Escape }

/// <summary>
/// A game that takes typing. The overlay never takes the keyboard, so <see cref="IGameHost.CaptureKeyboard"/> opens a small
/// window beside the game that does, and hands what is typed there to the game: characters after the keyboard layout
/// (so Cyrillic and dead keys arrive as the letters they make), and the few keys in <see cref="TypingKey"/>.
/// </summary>
public interface IKeySink
{
    void TextTyped(string text);
    void KeyPressed(TypingKey key);

    /// <summary>The typing window closed or lost the focus to another window: typing has stopped for now.</summary>
    void KeyboardLost();
}
