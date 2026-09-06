using SemanticStart.Core.Enrichment;

namespace SemanticStart.Tests;

/// <summary>
/// Guards the scrubbing rules against both failure directions: addresses must go, and the prose
/// wrapped around them must survive. The second half matters more, because a scrub that is too
/// eager silently deletes the only description an entity has.
/// </summary>
public sealed class TextScrubbingTests
{
    [Theory]
    [InlineData("Sign-in options ms-settings:signinoptions Sync your settings", "ms-settings")]
    [InlineData("Open shell:AppsFolder to list apps", "shell:")]
    [InlineData("See https://learn.microsoft.com/windows/x for details", "https")]
    [InlineData("Mail us at support@example.com today", "@example")]
    [InlineData("Key {21EC2020-3AEA-1069-A2DD-08002B30309D} controls it", "21EC2020")]
    [InlineData(@"Stored under HKEY_LOCAL_MACHINE\Software\Contoso here", "HKEY_LOCAL_MACHINE")]
    [InlineData(@"Runs C:\Program Files\Contoso\app.exe on start", @"C:\Program")]
    [InlineData("Expands %ProgramFiles%\\Contoso before launching", "%ProgramFiles%")]
    [InlineData("Hash deadbeefdeadbeefcafe identifies it", "deadbeefdeadbeefcafe")]
    public void Scrub_RemovesAddressLikeTokens(string input, string removed)
    {
        Assert.DoesNotContain(removed, EnrichmentTextNormalizer.ScrubNonDescriptive(input), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Sign-in options ms-settings:signinoptions Sync your settings", "Sign-in options")]
    [InlineData("Sign-in options ms-settings:signinoptions Sync your settings", "Sync your settings")]
    [InlineData("See https://learn.microsoft.com/windows/x for details", "for details")]
    [InlineData(@"Runs C:\Program Files\Contoso\app.exe on start", "on start")]
    public void Scrub_KeepsSurroundingProse(string input, string kept)
    {
        Assert.Contains(kept, EnrichmentTextNormalizer.ScrubNonDescriptive(input), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Note: the following settings apply to every user.")]
    [InlineData("The meeting starts at 3:30 and covers backup policy.")]
    [InlineData("Chapter 2: Setup explains how to configure the service.")]
    [InlineData("Reports effective permissions for securable objects.")]
    [InlineData("Simple text editor included with Microsoft Windows.")]
    [InlineData("Choose which apps start automatically when signing in.")]
    public void Scrub_LeavesOrdinaryProseIntact(string prose)
    {
        Assert.Equal(prose, EnrichmentTextNormalizer.ScrubNonDescriptive(prose));
    }

    [Fact]
    public void Scrub_CollapsesResidueLeftBehind()
    {
        var scrubbed = EnrichmentTextNormalizer.ScrubNonDescriptive(
            "Repair token ms-settings:workplace-repairtoken Set up a kiosk ms-settings:assignedaccess Sign-in options");

        Assert.Equal("Repair token Set up a kiosk Sign-in options", scrubbed);
    }
}