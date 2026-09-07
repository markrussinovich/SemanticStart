using SemanticStart.Core.Model;

namespace SemanticStart.Core.Query;

/// <summary>
/// Decides whether a profile's longer prose actually describes the entity it is attached to.
///
/// A fifth of the harvested details on a stock machine are one paragraph. Every <c>ms-settings:</c>
/// page links to the same Microsoft Learn article about the URI scheme, so 67 different settings
/// pages all carry prose explaining "how to launch Windows Settings from your Windows apps".
/// Displayed under Windows Sandbox that is worse than showing nothing, because it reads as a
/// description and is not one.
///
/// The test is simply whether other entities carry the same text: prose shared by many things
/// describes none of them in particular.
/// </summary>
internal static class SharedDetailFilter
{
    /// <summary>
    /// How much of the text identifies it. Two copies of a boilerplate article can diverge at the
    /// end - a trailing entity name, one extra sentence - while being the same text for this
    /// purpose, so only the opening is compared.
    /// </summary>
    private const int OpeningLength = 120;

    /// <summary>
    /// How many entities must share prose before it is treated as boilerplate. Three rather than
    /// two, because a pair usually means two records for the same program picked up by different
    /// collectors, and their shared description is correct for both.
    /// </summary>
    private const int SharedThreshold = 3;

    /// <summary>The openings that occur often enough to be boilerplate.</summary>
    public static HashSet<string> Build(IEnumerable<SynthesizedProfile?> profiles)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            if (profile?.Details is { Length: > 0 } details)
            {
                var opening = Opening(details);
                counts[opening] = counts.GetValueOrDefault(opening) + 1;
            }
        }

        return [.. counts.Where(p => p.Value >= SharedThreshold).Select(p => p.Key)];
    }

    /// <summary>The prose, or null when <paramref name="shared"/> shows it belongs to everything.</summary>
    public static string? Describing(HashSet<string> shared, SynthesizedProfile? profile) =>
        profile?.Details is { Length: > 0 } details && !shared.Contains(Opening(details))
            ? details
            : null;

    private static string Opening(string details) =>
        details.Length <= OpeningLength ? details : details[..OpeningLength];
}
