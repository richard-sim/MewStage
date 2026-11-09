namespace Aprillz.MewUI.Input;

/// <summary>
/// The characters a key types on a US QWERTY keyboard: what tells a platform which key typed a character, and what a
/// shortcut shows and is written with.
/// </summary>
internal static class KeyCharacters
{
    private static readonly (Key Key, char Unshifted, char Shifted)[] _punctuation =
    [
        (Key.Backquote, '`', '~'),
        (Key.Minus, '-', '_'),
        (Key.Equal, '=', '+'),
        (Key.BracketLeft, '[', '{'),
        (Key.BracketRight, ']', '}'),
        (Key.Backslash, '\\', '|'),
        (Key.Semicolon, ';', ':'),
        (Key.Quote, '\'', '"'),
        (Key.Comma, ',', '<'),
        (Key.Period, '.', '>'),
        (Key.Slash, '/', '?'),
    ];

    /// <summary>
    /// The key that types <paramref name="character"/>, shifted or not: a Latin letter, a digit or ASCII punctuation.
    /// <see cref="Key.None"/> for anything else, including the shifted characters of the digit keys.
    /// </summary>
    public static Key FromCharacter(char character)
    {
        if (character is >= 'a' and <= 'z')
        {
            return Key.A + (character - 'a');
        }

        if (character is >= 'A' and <= 'Z')
        {
            return Key.A + (character - 'A');
        }

        if (character is >= '0' and <= '9')
        {
            return Key.D0 + (character - '0');
        }

        foreach (var entry in _punctuation)
        {
            if (entry.Unshifted == character || entry.Shifted == character)
            {
                return entry.Key;
            }
        }

        return Key.None;
    }

    /// <summary>The unshifted character of a punctuation key.</summary>
    public static bool TryGetPunctuation(Key key, out char character)
    {
        foreach (var entry in _punctuation)
        {
            if (entry.Key == key)
            {
                character = entry.Unshifted;
                return true;
            }
        }

        character = default;
        return false;
    }
}
