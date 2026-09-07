using SemanticStart.App;
using Xunit;

namespace SemanticStart.Tests;

/// <summary>
/// Guards hotkey parsing, which became load-bearing once the chord could be typed rather than
/// picked from a fixed list. The parser it replaced ignored anything it did not recognise and fell
/// back to Alt+Space, so a typo silently produced a different working shortcut.
/// </summary>
public class HotKeySpecTests
{
    [Theory]
    [InlineData("Win+Alt+Space", "Win+Alt+Space")]
    [InlineData("win+alt+space", "Win+Alt+Space")]
    [InlineData("Alt+Win+Space", "Win+Alt+Space")]
    [InlineData("  Win + Alt + Space  ", "Win+Alt+Space")]
    [InlineData("Control+Alt+S", "Ctrl+Alt+S")]
    [InlineData("Win+Alt+F4", "Win+Alt+F4")]
    [InlineData("Ctrl+Alt+1", "Ctrl+Alt+1")]
    [InlineData("Windows+Shift+K", "Win+Shift+K")]
    [InlineData("Win+Alt+.", "Win+Alt+.")]
    [InlineData("  win + alt + .  ", "Win+Alt+.")]
    [InlineData("Ctrl+Alt+,", "Ctrl+Alt+,")]
    [InlineData("Win+Alt+/", "Win+Alt+/")]
    public void ValidChords_NormalizeToOneSpelling(string input, string expected)
    {
        Assert.True(HotKeySpec.TryParse(input, out var spec, out var error), error);
        Assert.Equal(expected, spec!.Normalized);
    }

    /// <summary>
    /// The default is a punctuation chord, so a chip has to read "." rather than "OemPeriod" -
    /// the WPF enum name, which is not printed on any keycap.
    /// </summary>
    [Fact]
    public void PunctuationKeys_AreShownAsTheCharacterOnTheKeycap()
    {
        Assert.True(HotKeySpec.TryParse("Win+Alt+OemPeriod", out var spec, out var error), error);
        Assert.Equal("Win+Alt+.", spec!.Normalized);
        Assert.Equal(HotKeyCapture.Chips("Win+Alt+."), ["Win", "Alt", "."]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Win+Alt")]          // modifiers with no main key
    [InlineData("Space")]            // no modifier: would swallow the key system-wide
    [InlineData("Shift+S")]          // Shift alone is ordinary typing
    [InlineData("Win+Alt+banana")]   // not a key name
    [InlineData("Win+Alt+S+K")]      // two main keys
    [InlineData("Win+L")]            // reserved by the shell
    public void InvalidChords_AreRejectedWithAReason(string? input)
    {
        Assert.False(HotKeySpec.TryParse(input, out var spec, out var error));
        Assert.Null(spec);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ModifierKeyNames_AreNotAcceptedAsTheMainKey()
    {
        // Enum.TryParse happily returns Key.LeftAlt for "LeftAlt", which would register a chord
        // with no usable key.
        Assert.False(HotKeySpec.TryParse("Win+Ctrl+LeftAlt", out _, out _));
    }

    [Fact]
    public void TheShippedDefault_IsValid()
    {
        Assert.True(HotKeySpec.TryParse(AppSettings.DefaultHotKey, out var spec, out var error), error);
        Assert.Equal(AppSettings.DefaultHotKey, spec!.Normalized);
    }

    /// <summary>
    /// A hotkey belongs to whichever process registers it first, so shipping a default that
    /// PowerToys' Command Palette already owns means never opening at all on a machine that has
    /// it installed. Win+Alt+Space must stay on the migrate-away list, and must not come back as
    /// the default.
    /// </summary>
    [Fact]
    public void TheShippedDefault_DoesNotCollideWithPowerToys()
    {
        Assert.NotEqual("Win+Alt+Space", AppSettings.DefaultHotKey);
        Assert.Contains("Win+Alt+Space", AppSettings.LegacyDefaultHotKeys);
        Assert.DoesNotContain(AppSettings.DefaultHotKey, AppSettings.LegacyDefaultHotKeys);
    }

    [Fact]
    public void DistinctChords_ProduceDistinctRegistrations()
    {
        Assert.True(HotKeySpec.TryParse("Win+Alt+S", out var first, out _));
        Assert.True(HotKeySpec.TryParse("Win+Alt+K", out var second, out _));

        // The old parser mapped every unrecognised key to Space, so different chords could collapse
        // onto the same registration.
        Assert.NotEqual(first!.VirtualKey, second!.VirtualKey);
    }
}
