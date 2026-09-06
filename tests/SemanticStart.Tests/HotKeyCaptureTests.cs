using System.Windows.Input;
using SemanticStart.App;
using Xunit;

namespace SemanticStart.Tests;

/// <summary>
/// The hotkey is set by pressing the combination, so these cover the translation from a WPF key
/// event to a stored chord. They run without a window because the awkward cases - a press that is
/// only modifiers, Alt arriving as Key.System, digits reported as Key.D1 - are all in this logic
/// rather than in the control.
/// </summary>
public class HotKeyCaptureTests
{
    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightCtrl)]
    [InlineData(Key.LeftAlt)]
    [InlineData(Key.RightAlt)]
    [InlineData(Key.LeftShift)]
    [InlineData(Key.RightShift)]
    [InlineData(Key.LWin)]
    [InlineData(Key.RWin)]
    public void HoldingAModifierIsNotYetAChord(Key key)
    {
        // Every chord starts this way. Reporting an error here would make the field complain
        // during normal use, so these must be ignored silently: no chord and no message.
        Assert.False(HotKeyCapture.TryCapture(key, ModifierKeys.Control, out var chord, out var error));
        Assert.Null(chord);
        Assert.Null(error);
    }

    [Fact]
    public void PressingTheCombinationProducesTheChord()
    {
        Assert.True(HotKeyCapture.TryCapture(
            Key.Space,
            ModifierKeys.Windows | ModifierKeys.Alt,
            out var chord,
            out _));

        Assert.Equal("Win+Alt+Space", chord);
    }

    [Fact]
    public void ModifierOrderIsNormalized_SoTheSameChordIsAlwaysStoredTheSameWay()
    {
        HotKeyCapture.TryCapture(Key.S, ModifierKeys.Alt | ModifierKeys.Windows, out var chord, out _);

        Assert.Equal("Win+Alt+S", chord);
    }

    [Fact]
    public void DigitsAreShownAsDigits_NotAsTheirKeyEnumName()
    {
        HotKeyCapture.TryCapture(Key.D1, ModifierKeys.Control | ModifierKeys.Alt, out var chord, out _);

        Assert.Equal("Ctrl+Alt+1", chord);
    }

    [Fact]
    public void AKeyWithNoModifierIsRejectedWithAReason()
    {
        // Registering a bare key swallows it system-wide, including inside text boxes.
        Assert.False(HotKeyCapture.TryCapture(Key.S, ModifierKeys.None, out var chord, out var error));
        Assert.Null(chord);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ShiftAloneIsRejected_BecauseItIsOrdinaryTyping()
    {
        Assert.False(HotKeyCapture.TryCapture(Key.S, ModifierKeys.Shift, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void AChordTheShellClaimsIsRejected()
    {
        // Win+L reaches the lock screen before any application sees it, so accepting it would
        // appear to work and then never fire.
        Assert.False(HotKeyCapture.TryCapture(Key.L, ModifierKeys.Windows, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ABareSystemKeyIsIgnored_BecauseItMeansAltWithNothingElse()
    {
        Assert.False(HotKeyCapture.TryCapture(Key.System, ModifierKeys.Alt, out var chord, out var error));
        Assert.Null(chord);
        Assert.Null(error);
    }

    [Fact]
    public void ChipsSplitTheChordIntoOneKeyEach()
    {
        Assert.Equal(new[] { "Win", "Ctrl", "Z" }, HotKeyCapture.Chips("Win+Ctrl+Z"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ChipsForNoChordAreEmpty_NotASingleBlankKey(string? chord)
    {
        Assert.Empty(HotKeyCapture.Chips(chord));
    }

    [Fact]
    public void EveryCapturedChordSurvivesAReload()
    {
        // What the recorder stores is what settings will parse on the next launch, so a chord that
        // captures but does not parse back would reappear as an empty field.
        HotKeyCapture.TryCapture(Key.F4, ModifierKeys.Control | ModifierKeys.Shift, out var chord, out _);

        Assert.True(HotKeySpec.TryParse(chord, out var spec, out var error), error);
        Assert.Equal(chord, spec!.Normalized);
    }
}
