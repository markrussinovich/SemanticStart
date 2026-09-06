using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;
using SemanticStart.Core.Synthesis;
using Xunit;

namespace SemanticStart.Tests;

/// <summary>
/// A documentation page that opens with a reference table and then explains itself passes a
/// whole-document prose test as a unit - and the summary is taken from the opening, which is the
/// table. That put "Default browser settings Manage optional features Offline Maps Startup apps"
/// on 56 unrelated Windows features at once, every one of those phrases a real page title and none
/// of them about the feature being described.
///
/// The text in these cases is the harvested article verbatim, so the rule is measured against what
/// actually broke rather than against a paraphrase of it.
/// </summary>
public sealed class ListingRejectionTests
{
    private const string UriTable =
        "Default browser settings (Deprecated in Windows 11) Manage optional features Offline Maps " +
        "(Download maps) Startup apps Video playback Control Center Settings page URI Control center. " +
        "Learn how to launch Windows Settings from your Windows apps using the ms-settings URI scheme. " +
        "This topic describes the URI scheme and lists the pages it can open.";

    [Fact]
    public void Listing_IsRecognisedInARunOfPageTitles()
    {
        Assert.True(ProfileText.IsListing(UriTable.Split('.')[0]));
    }

    [Theory]
    [InlineData("Identifies and displays programs configured to run automatically on Windows startup.")]
    [InlineData("Browse the web.")]
    [InlineData("Choose which apps start automatically when signing in.")]
    [InlineData("Reports effective permissions for securable objects, showing the account rights that apply.")]
    public void Listing_DoesNotRejectSomethingWrittenToBeRead(string prose)
    {
        Assert.False(ProfileText.IsListing(prose));
    }

    /// <summary>
    /// Short lines cannot be judged this way: a real description has no room for function words and
    /// needs none, so the rule must only apply once a line is long enough to be a column.
    /// </summary>
    [Theory]
    [InlineData("Disk Cleanup")]
    [InlineData("Registry Editor Local Group Policy Editor Event Viewer")]
    public void Listing_LeavesShortLinesAlone(string line)
    {
        Assert.False(ProfileText.IsListing(line));
    }

    /// <summary>
    /// The end-to-end consequence: the harvested article must not become the entity's description.
    /// This is the assertion that would have caught the regression in the index rather than in a
    /// helper, and it deliberately checks the summary a user reads.
    /// </summary>
    [Fact]
    public async Task Synthesis_DoesNotDescribeAFeatureWithAPageOfOtherPagesTitles()
    {
        var entity = new Entity
        {
            Id = "optionalfeature:directx.configuration.database",
            Kind = EntityKind.OptionalFeature,
            DisplayName = "Direct X Configuration Database",
            LaunchKind = LaunchKind.Uri,
            LaunchTarget = "ms-settings:optionalfeatures",
            Source = "optionalfeature",
        };

        var documents = new[]
        {
            new EnrichmentDocument
            {
                EntityId = entity.Id,
                Provider = "learn",
                IsOnline = true,
                Text = UriTable,
                SourceUri = "https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings",
            },
        };

        var profile = await new HeuristicProfileSynthesizer().SynthesizeAsync(entity, documents);

        Assert.DoesNotContain("Offline Maps", profile.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Startup apps", profile.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Offline Maps", profile.Details ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
