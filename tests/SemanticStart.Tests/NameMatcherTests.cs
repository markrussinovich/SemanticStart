using SemanticStart.Core.Query;
using Xunit;

namespace SemanticStart.Tests;

/// <summary>
/// Guards literal name matching, which is what makes typing a name behave at least as well as the
/// stock Start menu. The regression these exist for: "vsc" found Visual Studio Code, but "vsco"
/// and "vscod" made it vanish entirely, so the target disappeared mid-word and came back only once
/// the user finished typing "vscode".
/// </summary>
public class NameMatcherTests
{
    private static readonly RankingOptions Options = RankingOptions.Default;

    [Theory]
    [InlineData("vsc")]
    [InlineData("vsco")]
    [InlineData("vscod")]
    [InlineData("vscode")]
    public void EveryPrefixOfAnAcronymAndItsContinuation_Matches(string query)
    {
        var (boost, _) = NameMatcher.Score(query, "Visual Studio Code", Options);

        // Must clear the bar to surface on the literal signal alone, not merely be non-zero: a
        // boost below MinLiteralSurfaceStrength cannot bring an entity into the results by itself.
        Assert.True(
            boost >= Options.MinLiteralSurfaceStrength,
            $"'{query}' scored {boost}, below the {Options.MinLiteralSurfaceStrength} surfacing bar.");
    }

    [Fact]
    public void VendorPrefixesAreSkipped()
    {
        var (boost, _) = NameMatcher.Score("vsco", "Microsoft Visual Studio Code", Options);
        Assert.True(boost >= Options.MinLiteralSurfaceStrength);
    }

    [Theory]
    // The result that wrongly won the query in the reported bug.
    [InlineData("vsco", "x86 Native Tools Command Prompt for VS 2022")]
    [InlineData("vsco", "Microsoft Visual C++ 2013 Redistributable (x86)")]
    [InlineData("vsc", "Windows Security")]
    public void UnrelatedNames_DoNotAcquireAnAcronymBoost(string query, string displayName)
    {
        var (boost, reason) = NameMatcher.Score(query, displayName, Options);

        Assert.True(
            boost < Options.MinLiteralSurfaceStrength,
            $"'{query}' should not surface '{displayName}' on a literal match, but scored {boost} ({reason}).");
    }

    [Fact]
    public void ExactAndPrefixMatchesStillOutrankAnAcronym()
    {
        var exact = NameMatcher.Score("notepad", "Notepad", Options).Boost;
        var prefix = NameMatcher.Score("notep", "Notepad", Options).Boost;
        var acronym = NameMatcher.Score("vsco", "Visual Studio Code", Options).Boost;

        Assert.True(exact > prefix);
        Assert.True(prefix > acronym);
    }
}
