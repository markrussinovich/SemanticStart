using System.Windows.Input;

namespace SemanticStart.App;

/// <summary>
/// A parsed activation chord, and the single definition of what a hotkey string may contain.
///
/// This exists because the hotkey is typed rather than picked from a list, so the text has to be
/// judged before it is stored. The previous parser could not do that: it ignored any part it did
/// not recognise and fell back to Alt+Space, so "Win+Alt+K" and "Win+Alt+Kk" and "Win+Alt+banana"
/// all silently became different things than the user asked for, with nothing reported.
/// </summary>
public sealed record HotKeySpec(int Modifiers, int VirtualKey, string Normalized)
{
    public const int ModAlt = 0x0001;
    public const int ModControl = 0x0002;
    public const int ModShift = 0x0004;
    public const int ModWin = 0x0008;

    /// <summary>
    /// Parses a chord such as "Win+Alt+Space". Returns false with a reason the user can act on.
    /// </summary>
    public static bool TryParse(string? text, out HotKeySpec? spec, out string? error)
    {
        spec = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Enter a shortcut, for example Win+Alt+Space.";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "Enter a shortcut, for example Win+Alt+Space.";
            return false;
        }

        var modifiers = 0;
        Key? key = null;
        var modifierNames = new List<string>();

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "alt":
                    modifiers |= ModAlt;
                    continue;
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    continue;
                case "shift":
                    modifiers |= ModShift;
                    continue;
                case "win":
                case "windows":
                    modifiers |= ModWin;
                    continue;
            }

            if (key is not null)
            {
                error = $"Use one main key. '{part}' is a second one.";
                return false;
            }

            if (!TryParseKey(part, out var parsed))
            {
                error = $"'{part}' is not a key name. Try a letter, digit, function key, or Space.";
                return false;
            }

            key = parsed;
        }

        if (key is null)
        {
            error = "Add a main key after the modifiers, for example Space or S.";
            return false;
        }

        // Windows will happily register a bare letter as a global hotkey, which then swallows that
        // key everywhere in the system, including inside text boxes. Requiring a modifier is not a
        // style preference: without one the machine becomes difficult to use and the settings
        // window needed to undo it is hard to reach.
        if (modifiers == 0)
        {
            error = "Add at least one modifier: Win, Ctrl, Alt, or Shift.";
            return false;
        }

        // Shift alone reaches ordinary typing: Shift+S is a capital S.
        if (modifiers == ModShift)
        {
            error = "Shift by itself is not enough. Combine it with Win, Ctrl, or Alt.";
            return false;
        }

        if (ReservedChords.Contains(Describe(modifiers, key.Value)))
        {
            error = $"{Describe(modifiers, key.Value)} is reserved by Windows and cannot be captured.";
            return false;
        }

        // Order modifiers consistently so a stored setting is comparable by string, however it was
        // typed: "alt+win+s" and "Win+Alt+S" are the same chord.
        if ((modifiers & ModWin) != 0) modifierNames.Add("Win");
        if ((modifiers & ModControl) != 0) modifierNames.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) modifierNames.Add("Alt");
        if ((modifiers & ModShift) != 0) modifierNames.Add("Shift");

        var normalized = string.Join("+", modifierNames.Append(KeyDisplayName(key.Value)));
        spec = new HotKeySpec(modifiers, KeyInterop.VirtualKeyFromKey(key.Value), normalized);
        return true;
    }

    private static bool TryParseKey(string part, out Key key)
    {
        if (Enum.TryParse(part, ignoreCase: true, out key) && key != Key.None)
        {
            // Enum.TryParse accepts the modifier keys themselves ("LeftAlt") and numeric values
            // ("42"), neither of which is a main key a user means to type.
            if (!IsModifierKey(key) && !char.IsDigit(part[0]))
                return true;

            if (char.IsDigit(part[0]) && part.Length == 1)
            {
                // "1".."9" parse as Key.D1..Key.D9 only via the D-prefixed names.
                return Enum.TryParse("D" + part, ignoreCase: true, out key);
            }

            key = Key.None;
            return false;
        }

        if (part.Length == 1 && char.IsDigit(part[0]))
            return Enum.TryParse("D" + part, ignoreCase: true, out key);

        key = Key.None;
        return false;
    }

    private static bool IsModifierKey(Key key) => key
        is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
        or Key.System;

    private static string KeyDisplayName(Key key)
    {
        var name = key.ToString();
        return name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]) ? name[1..] : name;
    }

    private static string Describe(int modifiers, Key key)
    {
        var names = new List<string>();
        if ((modifiers & ModWin) != 0) names.Add("Win");
        if ((modifiers & ModControl) != 0) names.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) names.Add("Alt");
        if ((modifiers & ModShift) != 0) names.Add("Shift");
        return string.Join("+", names.Append(KeyDisplayName(key)));
    }

    /// <summary>
    /// Chords the shell claims before any application sees them. Accepting one would appear to
    /// work and then never fire, which is worse than refusing it.
    /// </summary>
    private static readonly HashSet<string> ReservedChords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Win+L", "Win+G", "Win+Tab", "Ctrl+Alt+Delete",
    };
}
