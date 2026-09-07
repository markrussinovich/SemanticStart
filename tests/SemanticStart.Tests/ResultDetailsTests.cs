using SemanticStart.App;
using SemanticStart.Core.Model;
using Xunit;

namespace SemanticStart.Tests;

/// <summary>
/// Covers what the expanded row shows. The panel used to render the same one-line summary that was
/// already visible in the row above it, so for the many entities whose description is a single
/// short sentence, expanding produced a larger copy of what the user had just read.
/// </summary>
public sealed class ResultDetailsTests
{
    /// <summary>
    /// OneNote's summary is "Take notes and have them when you need them." Its harvested prose is
    /// about something else entirely - what OneNote is - so expanding the row is worth doing.
    /// </summary>
    [Fact]
    public void TheLongerProseIsShownWhenThereIsSome()
    {
        var item = new SearchResultItem(Hit(
            summary: "Take notes and have them when you need them.",
            details: "Freeform note-taking software. Microsoft OneNote is a note-taking software developed by Microsoft."));

        Assert.True(item.HasDetailSummary);
        Assert.StartsWith("Freeform note-taking software.", item.DetailSummary);
    }

    /// <summary>
    /// Harvested prose usually opens by restating the summary verbatim, because the summary was
    /// taken from its first sentence. Left alone, the panel reads that sentence back before saying
    /// anything new.
    /// </summary>
    [Fact]
    public void AProseOpeningThatRepeatsTheSummaryIsDropped()
    {
        var item = new SearchResultItem(Hit(
            summary: "Simple text editor included with Microsoft Windows.",
            details: "Simple text editor included with Microsoft Windows. Windows Notepad creates and edits plain text documents."));

        Assert.Equal("Windows Notepad creates and edits plain text documents.", item.DetailSummary);
    }

    /// <summary>The redundancy this exists to remove: nothing longer, and the row already said it.</summary>
    [Fact]
    public void AShortSummaryIsNotRepeatedUnderneathItself()
    {
        var item = new SearchResultItem(Hit(
            summary: "Take notes and have them when you need them.",
            details: null));

        Assert.False(item.HasDetailSummary);
        Assert.Equal(string.Empty, item.DetailSummary);
    }

    [Fact]
    public void ProseThatIsOnlyTheSummaryAgainCountsAsNoProse()
    {
        var item = new SearchResultItem(Hit(
            summary: "Freeware system monitor for Windows.",
            details: "Freeware system monitor for Windows."));

        Assert.False(item.HasDetailSummary);
    }

    /// <summary>
    /// The row is one line and ellipsizes, so a summary too long to fit is still worth showing in
    /// full even when nothing longer was harvested.
    /// </summary>
    [Fact]
    public void ASummaryTooLongForOneLineIsStillShownInFull()
    {
        const string summary = "Run untrusted applications in a lightweight isolated desktop environment that is discarded when closed. Safely test suspicious software.";

        var item = new SearchResultItem(Hit(summary, details: null));

        Assert.True(item.HasDetailSummary);
        Assert.Equal(summary, item.DetailSummary);
    }

    [Fact]
    public void ProvenanceNamesThePublisher()
    {
        var item = new SearchResultItem(Hit("Freeware system monitor for Windows.", null,
            publisher: "Sysinternals - www.sysinternals.com",
            category: "System Tools"));

        Assert.True(item.HasProvenance);
        Assert.Equal("Sysinternals - www.sysinternals.com · System Tools", item.Provenance);
    }

    /// <summary>
    /// The row already carries an "Application" badge, and the category for most applications is
    /// "Applications". Printing both says one thing twice.
    /// </summary>
    [Fact]
    public void ACategoryThatRestatesTheBadgeIsDropped()
    {
        var item = new SearchResultItem(Hit("Take notes.", null,
            publisher: "Microsoft Corporation",
            category: "Applications"));

        Assert.Equal("Microsoft Corporation", item.Provenance);
    }

    /// <summary>Most built-in Windows entities have no publisher recorded, so the line disappears.</summary>
    [Fact]
    public void ProvenanceIsAbsentWhenNothingIsKnown()
    {
        var item = new SearchResultItem(Hit("Open Resource Monitor.", null, publisher: null, category: "Applications"));

        Assert.False(item.HasProvenance);
        Assert.Equal(string.Empty, item.Provenance);
    }

    private static SearchHit Hit(
        string summary,
        string? details,
        string? publisher = null,
        string? category = null) => new()
        {
            Entity = new Entity
            {
                Id = "test:1",
                Kind = EntityKind.Application,
                DisplayName = "Test",
                LaunchKind = LaunchKind.Executable,
                LaunchTarget = @"C:\Windows\System32\test.exe",
                Source = "startmenu",
                Publisher = publisher,
                RawMetadata = new Dictionary<string, string>(StringComparer.Ordinal),
            },
            Score = 1,
            Summary = summary,
            Details = details,
            Category = category,
        };
}
