using SemanticStart.App;
using SemanticStart.Core.Launching;
using SemanticStart.Core.Model;
using Xunit;

namespace SemanticStart.Tests;

/// <summary>
/// Covers the text behind the copy button on a result.
///
/// The interesting cases are the launch kinds whose <see cref="Entity.LaunchTarget"/> is not a
/// command at all. An applet name, a snap-in file and an AppUserModelId are all arguments to some
/// other program, so copying the target on its own would hand the user something that does nothing
/// when they paste it.
/// </summary>
public sealed class LaunchCommandLineTests
{
    [Fact]
    public void AnExecutableIsCopiedAsItsPath()
    {
        Assert.Equal(
            @"C:\Windows\System32\notepad.exe",
            LaunchCommandLine.For(Entity(LaunchKind.Executable, @"C:\Windows\System32\notepad.exe")));
    }

    /// <summary>A path with a space in it is not a command line until it is quoted.</summary>
    [Fact]
    public void APathWithASpaceIsQuoted()
    {
        Assert.Equal(
            "\"C:\\Program Files\\Git\\git-bash.exe\"",
            LaunchCommandLine.For(Entity(LaunchKind.Executable, @"C:\Program Files\Git\git-bash.exe")));
    }

    /// <summary>
    /// Start menu shortcuts routinely carry arguments that are the whole point of the entry:
    /// without them this is a different program.
    /// </summary>
    [Fact]
    public void ArgumentsRecordedByTheCollectorAreKept()
    {
        var entity = Entity(LaunchKind.Shortcut, @"C:\Windows\System32\cmd.exe") with
        {
            LaunchArguments = "/k \"C:\\tools\\env.bat\"",
        };

        Assert.Equal("C:\\Windows\\System32\\cmd.exe /k \"C:\\tools\\env.bat\"", LaunchCommandLine.For(entity));
    }

    /// <summary>
    /// An AppUserModelId is an opaque package identifier. Pasted on its own it is not runnable by
    /// anything; what makes it launch is the explorer.exe shell: prefix the launcher supplies.
    /// </summary>
    [Fact]
    public void APackagedAppIsCopiedAsTheCommandThatActivatesIt()
    {
        Assert.Equal(
            "explorer.exe shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            LaunchCommandLine.For(Entity(LaunchKind.AppsFolder, "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")));
    }

    [Fact]
    public void AControlPanelAppletIsCopiedWithItsHostProgram()
    {
        Assert.Equal(
            @"control.exe C:\Windows\System32\main.cpl",
            LaunchCommandLine.For(Entity(LaunchKind.ControlPanel, @"C:\Windows\System32\main.cpl")));
    }

    [Fact]
    public void AnMmcSnapInIsCopiedWithItsHostProgram()
    {
        Assert.Equal(
            @"mmc.exe C:\Windows\System32\eventvwr.msc",
            LaunchCommandLine.For(Entity(LaunchKind.Mmc, @"C:\Windows\System32\eventvwr.msc")));
    }

    /// <summary>
    /// A URI is already the thing you type into the Run dialog. Wrapping it in a host program
    /// would be right for exactly one of the shells it might be pasted into and wrong for the rest.
    /// </summary>
    [Fact]
    public void ASettingsUriIsCopiedUnchanged()
    {
        Assert.Equal("ms-settings:display", LaunchCommandLine.For(Entity(LaunchKind.Uri, "ms-settings:display")));
    }

    /// <summary>
    /// The button is hidden rather than copying an empty string, so a result with nothing to run
    /// has to report that it has no command line.
    /// </summary>
    [Fact]
    public void AnEntityWithNoTargetHasNoCommandLine()
    {
        var item = new SearchResultItem(new SearchHit
        {
            Entity = Entity(LaunchKind.Executable, "   "),
            Score = 1,
            Summary = "Nothing to run",
        });

        Assert.False(item.HasCommandLine);
        Assert.Equal(string.Empty, item.CommandLine);
    }

    [Fact]
    public void AResultExposesTheCommandLineToTheCopyButton()
    {
        var item = new SearchResultItem(new SearchHit
        {
            Entity = Entity(LaunchKind.Uri, "ms-settings:bluetooth"),
            Score = 1,
            Summary = "Bluetooth & devices",
        });

        Assert.True(item.HasCommandLine);
        Assert.Equal("ms-settings:bluetooth", item.CommandLine);
    }

    /// <summary>
    /// Writing the clipboard changes nothing the user can see, so the button acknowledges by
    /// swapping its glyph for a checkmark and then swapping back.
    /// </summary>
    [Fact]
    public void TheCopyButtonAcknowledgesACopyAndThenStopsShowingIt()
    {
        var item = new SearchResultItem(new SearchHit
        {
            Entity = Entity(LaunchKind.Uri, "ms-settings:display"),
            Score = 1,
            Summary = "Display",
        });

        var atRest = item.CopyGlyph;

        item.JustCopied = true;
        Assert.NotEqual(atRest, item.CopyGlyph);
        Assert.Equal("Copied", item.CopyToolTip);

        item.JustCopied = false;
        Assert.Equal(atRest, item.CopyGlyph);
        Assert.Contains("ms-settings:display", item.CopyToolTip, StringComparison.Ordinal);
    }

    private static Entity Entity(LaunchKind kind, string target) => new()
    {
        Id = "test:1",
        Kind = EntityKind.Application,
        DisplayName = "Test",
        LaunchKind = kind,
        LaunchTarget = target,
        Source = "startmenu",
        RawMetadata = new Dictionary<string, string>(StringComparer.Ordinal),
    };
}
