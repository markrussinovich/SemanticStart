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
    public void ValidChords_NormalizeToOneSpelling(string input, string expected)
    {
        Assert.True(HotKeySpec.TryParse(input, out var spec, out var error), error);
        Assert.Equal(expected, spec!.Normalized);
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
