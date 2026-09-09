using SemanticStart.Core.Enrichment;
using SemanticStart.Core.Model;
using Xunit;

namespace SemanticStart.Tests;

/// <summary>
/// Wikipedia titles articles by the full product name where the Start menu uses the short one, so
/// an article whose title is the entity's name with a vendor in front of it is accepted. That rule
/// is what has to be prevented from accepting a *different* product that merely ends with the same
/// words, and the vendor is therefore required to be corroborated by the machine's own metadata.
/// </summary>
public sealed class WikipediaTitleMatchTests
{
    /// <summary>
    /// The reported bug: Windows' File Explorer was described as "a file manager for Android
    /// devices... removed from the Google Play Store for committing click fraud, spyware, adware".
    /// The evidence was matched as raw text, so the prefix "ES" corroborated itself out of the
    /// middle of the word "files" in File Explorer's own description.
    /// </summary>
    [Fact]
    public void AVendorPrefixIsNotCorroboratedByTheMiddleOfAnotherWord()
    {
        var fileExplorer = Entity(
            "File Explorer",
            launchTarget: "Microsoft.Windows.Explorer",
            metadata: ("comment", "Displays the files and folders on your computer."));

        Assert.False(WikipediaEnricher.IsVendorPrefixed(
            key: "esfileexplorer",
            wanted: "fileexplorer",
            title: "ES File Explorer",
            entity: fileExplorer));
    }

    /// <summary>
    /// The case the rule exists for. Notepad's only claim to "Windows" is inside its package
    /// identifier, so whole-word matching has to see through run-together identifiers or it would
    /// reject the article it is meant to accept.
    /// </summary>
    [Fact]
    public void AVendorPrefixIsAcceptedFromInsideAPackageIdentifier()
    {
        var notepad = Entity(
            "Notepad",
            launchTarget: "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
            metadata: ("appUserModelId", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"));

        Assert.True(WikipediaEnricher.IsVendorPrefixed(
            key: "windowsnotepad",
            wanted: "notepad",
            title: "Windows Notepad",
            entity: notepad));
    }

    [Fact]
    public void AVendorPrefixIsAcceptedFromThePublisher()
    {
        var powerPoint = Entity("PowerPoint", publisher: "Microsoft Corporation");

        Assert.True(WikipediaEnricher.IsVendorPrefixed(
            key: "microsoftpowerpoint",
            wanted: "powerpoint",
            title: "Microsoft PowerPoint",
            entity: powerPoint));
    }

    /// <summary>
    /// Someone else's product that happens to end with the same word stays rejected: nothing on
    /// this machine says "Adobe".
    /// </summary>
    [Fact]
    public void AnUnrelatedVendorIsRejected()
    {
        var photoshop = Entity("Photoshop", publisher: "Contoso Ltd");

        Assert.False(WikipediaEnricher.IsVendorPrefixed(
            key: "adobephotoshop",
            wanted: "photoshop",
            title: "Adobe Photoshop",
            entity: photoshop));
    }

    [Fact]
    public void MultipleInternalCaseConflictsRejectADifferentProduct()
    {
        Assert.False(WikipediaEnricher.HasCompatibleTitleCasing("News", "NeWS"));
    }

    [Theory]
    [InlineData("GitHub", "Github")]
    [InlineData("NEWS", "News")]
    [InlineData("PowerShell", "Powershell")]
    public void OrdinaryDisplayNameCasingRemainsCompatible(string displayName, string title)
    {
        Assert.True(WikipediaEnricher.HasCompatibleTitleCasing(displayName, title));
    }

    private static Entity Entity(
        string displayName,
        string? publisher = null,
        string launchTarget = @"C:\Windows\System32\test.exe",
        params (string Key, string Value)[] metadata) => new()
        {
            Id = "test:1",
            Kind = EntityKind.Application,
            DisplayName = displayName,
            LaunchKind = LaunchKind.Executable,
            LaunchTarget = launchTarget,
            Source = "appsfolder",
            Publisher = publisher,
            RawMetadata = metadata.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal),
        };
}
