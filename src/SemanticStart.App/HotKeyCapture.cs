using System.Windows.Input;

namespace SemanticStart.App;

/// <summary>
/// Turns a live key press into a chord, and a chord into the individual keys shown on screen.
///
/// The hotkey is set by pressing the combination rather than typing its name, so this is the
/// bridge between a WPF key event and <see cref="HotKeySpec"/>. Keeping it free of any control
/// means the awkward parts - a press that is only modifiers, AltGr arriving as Ctrl+Alt, Alt
/// reporting itself as <see cref="Key.System"/> - are testable without rendering a window.
/// </summary>
public static class HotKeyCapture
{
    /// <summary>
    /// True while the user is still holding down modifiers and has not yet pressed a main key.
    /// A recorder must ignore these rather than reject them: every chord begins with them, so
    /// reporting an error here would make the field flash a complaint during normal use.
    /// </summary>
    public static bool IsModifierOnly(Key key) => key
        is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    /// <summary>
    /// Builds a chord from a key press. Returns false with a reason to display when the press
    /// cannot become a hotkey, and null <paramref name="error"/> when the press was merely
    /// incomplete and should be ignored silently.
    /// </summary>
    public static bool TryCapture(Key key, ModifierKeys modifiers, out string? chord, out string? error)
    {
        chord = null;
        error = null;

        // WPF reports Alt-modified presses as Key.System, carrying the real key in SystemKey.
        // Callers pass SystemKey when that happens; a bare System here means Alt alone.
        if (key is Key.System or Key.None || IsModifierOnly(key))
            return false;

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");

        parts.Add(HotKeySpec.KeyDisplayName(key));

        // HotKeySpec owns every rule about what is allowed, so a chord that is captured here is
        // judged by exactly the same code as one restored from settings.
        if (!HotKeySpec.TryParse(string.Join("+", parts), out var spec, out error) || spec is null)
            return false;

        chord = spec.Normalized;
        return true;
    }

    /// <summary>
    /// Splits a chord into the keys to draw, one chip each, in the order they are pressed.
    /// </summary>
    public static IReadOnlyList<string> Chips(string? chord) =>
        string.IsNullOrWhiteSpace(chord)
            ? Array.Empty<string>()
            : chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
