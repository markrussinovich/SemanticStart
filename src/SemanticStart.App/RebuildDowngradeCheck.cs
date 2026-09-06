using SemanticStart.Core.Synthesis;

namespace SemanticStart.App;

/// <summary>
/// Decides whether rebuilding would quietly make the index worse.
///
/// The local-model setting only governs the *next* build; it says nothing about the index already
/// on disk. So a user can be running an index whose descriptions were written by a local model,
/// see synthesis switched off in Settings, and press Rebuild expecting no change in quality - and
/// silently get shorter, weaker descriptions written by the built-in heuristics instead. The
/// descriptions are what the overlay shows under each result, so this is a visible regression that
/// no error surfaces and that only a full re-run with the model can undo.
/// </summary>
public static class RebuildDowngradeCheck
{
    /// <summary>Marks generators backed by a local model. Set by <c>LocalLlmProfileSynthesizer.Generator</c>.</summary>
    private const string LlmGeneratorPrefix = "llm:";

    /// <summary>
    /// Number of profiles a rebuild under <paramref name="modeForNextBuild"/> would demote from
    /// model-written to heuristic. Zero means the rebuild is safe and must not prompt.
    /// </summary>
    /// <param name="modeForNextBuild">The mode that will be in force for the rebuild, not the one the index was built with.</param>
    /// <param name="generatorCounts">Profile counts keyed by generator, as stored with the index.</param>
    public static int CountProfilesAtRisk(
        LocalLlmMode modeForNextBuild,
        IReadOnlyDictionary<string, int> generatorCounts)
    {
        ArgumentNullException.ThrowIfNull(generatorCounts);

        // Auto and Custom both still attempt the model, so neither guarantees a downgrade.
        // Only an explicit Off means the heuristics will write every profile.
        if (modeForNextBuild != LocalLlmMode.Off)
            return 0;

        return generatorCounts
            .Where(kv => kv.Key.StartsWith(LlmGeneratorPrefix, StringComparison.OrdinalIgnoreCase))
            .Sum(kv => kv.Value);
    }

    public static string BuildWarning(int profilesAtRisk, int total) =>
        $"""
         {profilesAtRisk} of {total} descriptions in the current index were written by a local model.

         Local model synthesis is currently turned off, so rebuilding will replace them with the
         built-in heuristic descriptions, which are shorter and less accurate. Search results will
         change.

         Turn synthesis back on first if you want to keep the current quality.

         Rebuild anyway?
         """;
}
