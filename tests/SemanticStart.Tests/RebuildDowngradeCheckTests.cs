using SemanticStart.App;
using SemanticStart.Core.Synthesis;

namespace SemanticStart.Tests;

/// <summary>
/// The local-model setting describes the *next* build, not the index on disk, so "synthesis is
/// off" and "these descriptions were written by heuristics" are different statements. These pin
/// the case that motivated the check: an index built with a local model, sitting under a setting
/// that has since been switched off.
/// </summary>
public class RebuildDowngradeCheckTests
{
    private static Dictionary<string, int> Counts(params (string Generator, int Count)[] entries) =>
        entries.ToDictionary(e => e.Generator, e => e.Count, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ModelWrittenIndexRebuiltWithSynthesisOff_IsFlagged()
    {
        var counts = Counts(("llm:qwen2.5-1.5b-instruct-trtrtx-gpu", 543), ("fallback", 10));

        Assert.Equal(543, RebuildDowngradeCheck.CountProfilesAtRisk(LocalLlmMode.Off, counts));
    }

    [Fact]
    public void HeuristicIndexRebuiltWithSynthesisOff_IsNotFlagged()
    {
        // Nothing to lose - this rebuild produces exactly what is already there, so prompting
        // would train the user to dismiss the dialog without reading it.
        var counts = Counts(("fallback", 553));

        Assert.Equal(0, RebuildDowngradeCheck.CountProfilesAtRisk(LocalLlmMode.Off, counts));
    }

    [Theory]
    [InlineData(LocalLlmMode.Auto)]
    [InlineData(LocalLlmMode.Custom)]
    public void ModelWrittenIndexRebuiltWithSynthesisOn_IsNotFlagged(LocalLlmMode mode)
    {
        var counts = Counts(("llm:qwen2.5-1.5b", 500));

        Assert.Equal(0, RebuildDowngradeCheck.CountProfilesAtRisk(mode, counts));
    }

    [Fact]
    public void EmptyIndex_IsNotFlagged()
    {
        Assert.Equal(0, RebuildDowngradeCheck.CountProfilesAtRisk(LocalLlmMode.Off, Counts()));
    }

    [Fact]
    public void ProfilesFromSeveralModels_AreAllCounted()
    {
        var counts = Counts(("llm:phi-4-mini", 100), ("llm:qwen2.5-1.5b", 50), ("fallback", 7));

        Assert.Equal(150, RebuildDowngradeCheck.CountProfilesAtRisk(LocalLlmMode.Off, counts));
    }

    [Fact]
    public void WarningNamesTheScaleOfTheLoss()
    {
        var warning = RebuildDowngradeCheck.BuildWarning(543, 553);

        Assert.Contains("543", warning);
        Assert.Contains("553", warning);
    }
}
