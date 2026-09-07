using SemanticStart.Core.Model;
using SemanticStart.Core.Synthesis;
using Xunit;

namespace SemanticStart.Tests;

/// <summary>
/// The features column exists to say what the entity's other fields cannot. Both halves of that
/// are load-bearing and pull in opposite directions, so both are asserted here: a word the name
/// already carries must go, and a word nothing else carries must stay.
///
/// The first half is not tidiness. "edit a file" must not return Registry Editor, and the reason
/// it did was that "-editor" yields the verb "edit"; regedit's Edit menu then hands the ranker
/// back the match it had learned to refuse. The second half is the entire point of harvesting
/// interface labels at all - nothing written about Process Explorer mentions memory, while its
/// View menu offers "Physical Memory History".
/// </summary>
public sealed class FeatureVocabularyTests
{
    private static string? Features(string captions, string name, string summary) =>
        ProfileText.Features(
            [
                new EnrichmentDocument
                {
                    EntityId = "e",
                    Provider = "ui-resources",
                    IsOnline = false,
                    Text = "Interface labels: " + captions,
                },
            ],
            name,
            summary);

    [Fact]
    public void Features_DropsWordsTheNameAlreadyCarries()
    {
        var text = Features(
            "Edit String Import File Load Hive",
            "Registry Editor",
            "Open Registry Editor.");

        Assert.NotNull(text);
        Assert.DoesNotContain("Edit", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Registry", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hive", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Features_DropsWordsTheSummaryAlreadyCarries()
    {
        var text = Features(
            "System Information Physical Memory History",
            "Process Explorer",
            "Freeware system monitor for Windows.");

        Assert.NotNull(text);
        Assert.DoesNotContain("System", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Memory", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Features_KeepsCapabilityWordsNothingElseStates()
    {
        var text = Features(
            "Physical Memory History Own Memory Usage Commit Charge",
            "Process Explorer",
            "Freeware system monitor for Windows.");

        Assert.NotNull(text);
        Assert.Contains("Memory", text, StringComparison.Ordinal);
        Assert.Contains("Usage", text, StringComparison.Ordinal);
        Assert.Contains("Commit", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Features_KeepsEachWordOnce()
    {
        // BM25 reads a menu that names one capability four times over as four times the evidence.
        var text = Features(
            "Edit String Edit Binary Value Edit DWORD Value Edit Multi String",
            "Some Tool",
            "Does something.");

        Assert.NotNull(text);
        var words = text!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(words, w => w.Equals("Edit", StringComparison.OrdinalIgnoreCase));
        Assert.Single(words, w => w.Equals("Value", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Features_ShortWordsAreComparedByEqualityNotPrefix()
    {
        // "On" prefixes "Online", so a prefix rule without a floor would delete the second.
        var text = Features("Online Backup", "On", "Turns things on.");

        Assert.NotNull(text);
        Assert.Contains("Online", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Features_IsNullWhenEverythingRestatesTheName()
    {
        Assert.Null(Features("Registry Editor", "Registry Editor", "Open Registry Editor."));
    }

    /// <summary>
    /// The cap is what keeps one matching label meaningful. BM25 divides term frequency by field
    /// length, and de-duplication leaves each label here exactly once, so an uncapped column lets a
    /// large program's whole interface bury the one word that answers the query. Process Explorer
    /// sat at rank 10 for "view memory usage" - outside the eight rows the overlay shows - on a
    /// "Physical Memory History" diluted across two hundred words.
    /// </summary>
    [Fact]
    public void Features_AreCappedSoOneMatchingLabelStillCounts()
    {
        var captions = string.Join(" ", Enumerable.Range(0, 400).Select(i => $"Label{i}"));
        var text = Features(captions, "Thing", "Does things.");

        Assert.NotNull(text);

        var words = text!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.StartsWith("Label", StringComparison.Ordinal))
            .ToArray();

        Assert.InRange(words.Length, 1, 175);
        Assert.Equal(words.Length, words.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
