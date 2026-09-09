using SemanticStart.App;

namespace SemanticStart.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void StartupCommandQuotesTheExecutableAndMarksTheLaunch()
    {
        Assert.Equal(
            "\"C:\\Program Files\\SemanticStart\\SemanticStart.App.exe\" --startup",
            AppSettingsService.BuildStartupCommand(
                "C:\\Program Files\\SemanticStart\\SemanticStart.App.exe"));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void StartupApprovalReflectsWindowsDisabledState(byte state, bool expected)
    {
        Assert.Equal(expected, AppSettingsService.IsStartupApproved(new byte[] { state, 0, 0, 0 }));
    }

    [Fact]
    public void MissingStartupApprovalMeansEnabled()
    {
        Assert.True(AppSettingsService.IsStartupApproved(null));
    }
}
