using System.Diagnostics;
using System.Text;
using SemanticStart.Core.Abstractions;

namespace SemanticStart.Core.Query;

public sealed record RelevanceOutcome
{
    public required RelevanceCase Case { get; init; }
    public required bool Passed { get; init; }
    public required string[] ActualTop { get; init; }
    public int? MatchedRank { get; init; }
    public double ElapsedMs { get; init; }
}

public sealed record RelevanceReport
{
    public required IReadOnlyList<RelevanceOutcome> Outcomes { get; init; }

    public int Passed => Outcomes.Count(o => o.Passed);
    public int Total => Outcomes.Count;
    public double PassRate => Total == 0 ? 0 : (double)Passed / Total;

    /// <summary>p95 latency. The overlay updates per keystroke, so the tail is what users feel.</summary>
    public double P95LatencyMs
    {
        get
        {
            if (Outcomes.Count == 0)
                return 0;

            var sorted = Outcomes.Select(o => o.ElapsedMs).OrderBy(x => x).ToArray();
            var index = (int)Math.Ceiling(sorted.Length * 0.95) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
        }
    }

    public double MedianLatencyMs
    {
        get
        {
            if (Outcomes.Count == 0)
                return 0;

            var sorted = Outcomes.Select(o => o.ElapsedMs).OrderBy(x => x).ToArray();
            return sorted[sorted.Length / 2];
        }
    }

    /// <summary>
    /// Mean reciprocal rank over the cases that expect a specific result. Pass/fail only asks
    /// whether the right entry landed inside the allowed window, so two configurations can tie on
    /// it while one consistently places the answer first and the other consistently places it
    /// third. A case that finds nothing contributes zero.
    /// </summary>
    public double MeanReciprocalRank
    {
        get
        {
            var ranked = Outcomes.Where(o => o.Case.AcceptableResults.Length > 0).ToArray();
            if (ranked.Length == 0)
                return 0;

            return ranked.Sum(o => o.MatchedRank is { } rank ? 1.0 / rank : 0.0) / ranked.Length;
        }
    }

    /// <summary>How often the expected result is the very first thing shown.</summary>
    public double TopOneRate
    {
        get
        {
            var ranked = Outcomes.Where(o => o.Case.AcceptableResults.Length > 0).ToArray();
            return ranked.Length == 0 ? 0 : (double)ranked.Count(o => o.MatchedRank == 1) / ranked.Length;
        }
    }

    public string ToSummary()
    {
        var lines = new List<string>
        {
            $"Relevance: {Passed}/{Total} passed ({PassRate:P0})",
            $"Ranking:   MRR {MeanReciprocalRank:F3}, top-1 {TopOneRate:P0}",
            $"Latency:   median {MedianLatencyMs:F1} ms, p95 {P95LatencyMs:F1} ms",
        };

        var failures = Outcomes.Where(o => !o.Passed).ToArray();
        if (failures.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Failures:");
            foreach (var f in failures)
            {
                lines.Add($"  \"{f.Case.Query}\"");
                if (f.Case.ExpectNoResults)
                    lines.Add("    expected: no results");
                else if (f.Case.AcceptableResults.Length > 0)
                    lines.Add($"    expected within top {f.Case.WithinTopN}: {string.Join(" | ", f.Case.AcceptableResults)}");

                if (f.Case.ForbiddenResults.Length > 0)
                    lines.Add($"    forbidden: {string.Join(" | ", f.Case.ForbiddenResults)}");

                if (f.Case.MaxResults is { } max)
                    lines.Add($"    max results: {max}");

                lines.Add($"    actual: {(f.ActualTop.Length == 0 ? "(no results)" : string.Join(" > ", f.ActualTop))}");
                if (f.Case.Rationale is { } r)
                    lines.Add($"    guards: {r}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Runs the relevance corpus against a live search engine and measures both correctness and
/// latency. Intended to be run after every ranking change, since ranking work is otherwise
/// impossible to evaluate objectively.
/// </summary>
public sealed class RelevanceHarness(ISearchEngine engine)
{
    private readonly ISearchEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public async Task<RelevanceReport> RunAsync(
        IReadOnlyList<RelevanceCase>? cases = null,
        CancellationToken cancellationToken = default)
    {
        cases ??= RelevanceCorpus.All;
        var outcomes = new List<RelevanceOutcome>(cases.Count);

        // One warm-up query so JIT and model load do not distort the first measurement.
        await _engine.SearchAsync("warmup", 5, cancellationToken).ConfigureAwait(false);

        foreach (var testCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sw = Stopwatch.StartNew();
            var hits = await _engine
                .SearchAsync(testCase.Query, Math.Max(testCase.WithinTopN, 10), cancellationToken)
                .ConfigureAwait(false);
            sw.Stop();

            var names = hits.Select(h => h.Entity.DisplayName).ToArray();

            int? matchedRank = null;
            for (var i = 0; i < Math.Min(names.Length, testCase.WithinTopN); i++)
            {
                if (testCase.AcceptableResults.Any(a => IsMatch(names[i], a)))
                {
                    matchedRank = i + 1;
                    break;
                }
            }

            var recallPassed = testCase.AcceptableResults.Length == 0 || matchedRank.HasValue;
            var noResultsPassed = !testCase.ExpectNoResults || names.Length == 0;
            var maxResultsPassed = !testCase.MaxResults.HasValue || names.Length <= testCase.MaxResults.Value;
            var forbiddenPassed = !names.Any(n => testCase.ForbiddenResults.Any(f => IsForbiddenMatch(n, f)));

            outcomes.Add(new RelevanceOutcome
            {
                Case = testCase,
                Passed = recallPassed && noResultsPassed && maxResultsPassed && forbiddenPassed,
                ActualTop = [.. names.Take(5)],
                MatchedRank = matchedRank,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
            });
        }

        return new RelevanceReport { Outcomes = outcomes };
    }

    /// <summary>
    /// Lenient comparison, but only across whole words. Display names vary across machines and
    /// Windows versions, and an entry may legitimately be listed under a shorter name than the one
    /// the machine reports ("Clipchamp" for "Microsoft Clipchamp"), so a name whose words are all
    /// present in the other still counts as a match.
    ///
    /// Raw substring containment was wrong and was silently passing cases. Normalization removes
    /// spaces, so any short generic name matched anything built on it: a case asserting that
    /// AccessChk is found was reported as passing at rank 1 by the unrelated database app
    /// "Access", and the tool it was written to demand is not even in the index. A test that
    /// cannot fail is worse than no test, because it is counted as evidence.
    /// </summary>
    private static bool IsMatch(string actual, string expected)
    {
        var a = Words(actual);
        var e = Words(expected);

        if (a.Count == 0 || e.Count == 0)
            return false;

        return a.IsSubsetOf(e) || e.IsSubsetOf(a);
    }

    private static HashSet<string> Words(string value)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var current = new StringBuilder(value.Length);

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            words.Add(current.ToString());

        return words;
    }

    /// <summary>
    /// Strict comparison, deliberately different from <see cref="IsMatch"/>. A recall assertion is
    /// lenient because it only has to recognise the right answer under a different name, but a
    /// prohibition must be precise: with containment, forbidding the "Services" console also
    /// forbids "Internet Information Services (IIS)", failing a query the engine answered well.
    /// Prohibitions therefore require the whole normalized name to be equal.
    /// </summary>
    private static bool IsForbiddenMatch(string actual, string forbidden) =>
        NameMatcher.Normalize(actual).Equals(NameMatcher.Normalize(forbidden), StringComparison.Ordinal);
}
