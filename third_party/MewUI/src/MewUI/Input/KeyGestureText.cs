using System.Text;

namespace Aprillz.MewUI.Input;

/// <summary>
/// The portable text form of key gestures and sequences: the same on every platform, unlike the display form.
/// </summary>
internal static class KeyGestureText
{
    private const char STROKE_SEPARATOR = ',';
    private const char PART_SEPARATOR = '+';

    public static bool TryParseGesture(ReadOnlySpan<char> text, out KeyGesture gesture)
    {
        int position = 0;
        return TryReadStroke(text, ref position, out gesture) && AtEnd(text, position);
    }

    public static bool TryParseSequence(ReadOnlySpan<char> text, out KeySequence sequence)
    {
        sequence = default;
        int position = 0;
        while (true)
        {
            if (sequence.Length == KeySequence.MAX_LENGTH || !TryReadStroke(text, ref position, out var stroke))
            {
                sequence = default;
                return false;
            }

            sequence = sequence.Length == 0 ? new KeySequence(stroke) : sequence.Append(stroke);
            if (AtEnd(text, position))
            {
                return true;
            }

            if (text[position] != STROKE_SEPARATOR)
            {
                sequence = default;
                return false;
            }

            position++;
        }
    }

    public static string Format(KeySequence sequence)
    {
        var builder = new StringBuilder();
        for (int index = 0; index < sequence.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(STROKE_SEPARATOR).Append(' ');
            }

            AppendGesture(builder, sequence[index]);
        }

        return builder.ToString();
    }

    private static void AppendGesture(StringBuilder builder, KeyGesture gesture)
    {
        var modifiers = gesture.Modifiers;
        AppendModifier(builder, modifiers, ModifierKeys.Primary, "Primary");
        AppendModifier(builder, modifiers, ModifierKeys.Control, "Ctrl");
        AppendModifier(builder, modifiers, ModifierKeys.Alt, "Alt");
        AppendModifier(builder, modifiers, ModifierKeys.Shift, "Shift");
        AppendModifier(builder, modifiers, ModifierKeys.Meta, "Meta");

        var key = gesture.Key;
        if (key is >= Key.D0 and <= Key.D9)
        {
            builder.Append((char)('0' + (key - Key.D0)));
        }
        else if (KeyCharacters.TryGetPunctuation(key, out char punctuation))
        {
            builder.Append(punctuation);
        }
        else
        {
            builder.Append(key.ToString());
        }
    }

    private static void AppendModifier(StringBuilder builder, ModifierKeys modifiers, ModifierKeys flag, string name)
    {
        if ((modifiers & flag) != 0)
        {
            builder.Append(name).Append(PART_SEPARATOR);
        }
    }

    /// <summary>Reads one stroke and leaves <paramref name="position"/> on the first character after it.</summary>
    private static bool TryReadStroke(ReadOnlySpan<char> text, ref int position, out KeyGesture gesture)
    {
        gesture = default;
        var modifiers = ModifierKeys.None;
        while (true)
        {
            SkipSpaces(text, ref position);
            if (position >= text.Length)
            {
                return false;
            }

            // A character that is not part of a name is a key written as what it types, so "Ctrl++" and "Ctrl+," read.
            int start = position;
            if (char.IsAsciiLetterOrDigit(text[position]))
            {
                while (position < text.Length && char.IsAsciiLetterOrDigit(text[position]))
                {
                    position++;
                }
            }
            else
            {
                position++;
            }

            var token = text[start..position];
            SkipSpaces(text, ref position);
            if (position < text.Length && text[position] == PART_SEPARATOR)
            {
                if (!TryGetModifier(token, out var modifier))
                {
                    return false;
                }

                modifiers |= modifier;
                position++;
            }
            else
            {
                var key = ParseKey(token);
                gesture = new KeyGesture(key, modifiers);
                return key != Key.None;
            }
        }
    }

    private static Key ParseKey(ReadOnlySpan<char> token)
    {
        if (token.Length == 1)
        {
            return KeyCharacters.FromCharacter(token[0]);
        }

        // Enum parsing would also accept a number, which names no key.
        if (!char.IsAsciiLetter(token[0]))
        {
            return Key.None;
        }

        return Enum.TryParse<Key>(token, ignoreCase: true, out var key) ? key : Key.None;
    }

    private static bool TryGetModifier(ReadOnlySpan<char> token, out ModifierKeys modifier)
    {
        if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase))
        {
            modifier = ModifierKeys.Control;
        }
        else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
        {
            modifier = ModifierKeys.Shift;
        }
        else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
        {
            modifier = ModifierKeys.Alt;
        }
        else if (token.Equals("Meta", StringComparison.OrdinalIgnoreCase))
        {
            modifier = ModifierKeys.Meta;
        }
        else if (token.Equals("Primary", StringComparison.OrdinalIgnoreCase))
        {
            modifier = ModifierKeys.Primary;
        }
        else
        {
            modifier = ModifierKeys.None;
        }

        return modifier != ModifierKeys.None;
    }

    private static bool AtEnd(ReadOnlySpan<char> text, int position)
    {
        SkipSpaces(text, ref position);
        return position >= text.Length;
    }

    private static void SkipSpaces(ReadOnlySpan<char> text, ref int position)
    {
        while (position < text.Length && text[position] == ' ')
        {
            position++;
        }
    }
}
