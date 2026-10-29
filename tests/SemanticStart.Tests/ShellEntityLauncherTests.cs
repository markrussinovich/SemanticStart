using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Launching;
using SemanticStart.Core.Model;

namespace SemanticStart.Tests;

public sealed class ShellEntityLauncherTests
{
    [Theory]
    [InlineData(@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\cmd.exe")]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    public void AppsFolderElevationAppliesRunasToTheShellItem(string target)
    {
        var info = ShellEntityLauncher.AppsFolderStartInfo(
            Entity(target),
            new LaunchOptions { RunAsAdministrator = true });

        Assert.True(info.UseShellExecute);
        Assert.Equal($"shell:AppsFolder\\{target}", info.FileName);
        Assert.Equal(string.Empty, info.Arguments);
        Assert.Equal("runas", info.Verb);
    }

    [Fact]
    public void NormalAppsFolderLaunchStillUsesExplorer()
    {
        const string target = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
        var info = ShellEntityLauncher.AppsFolderStartInfo(Entity(target), default);

        Assert.True(info.UseShellExecute);
        Assert.Equal("explorer.exe", info.FileName);
        Assert.Equal($"shell:AppsFolder\\{target}", info.Arguments);
        Assert.Equal(string.Empty, info.Verb);
    }

    private static Entity Entity(string target) => new()
    {
        Id = "test:app",
        Kind = target.Contains('!') ? EntityKind.PackagedApp : EntityKind.Application,
        DisplayName = "Test app",
        LaunchKind = LaunchKind.AppsFolder,
        LaunchTarget = target,
        Source = "test",
    };
}
