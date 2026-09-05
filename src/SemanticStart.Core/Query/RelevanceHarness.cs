using System.Diagnostics;
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

    public string ToSummary()
    {
        var lines = new List<string>
        {
            $"Relevance: {Passed}/{Total} passed ({PassRate:P0})",
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
    /// Lenient comparison. Display names vary across machines and Windows versions ("Power &amp;
    /// battery" versus "Power Options"), so containment either way counts as a match.
    /// </summary>
    private static bool IsMatch(string actual, string expected)
    {
        var a = NameMatcher.Normalize(actual);
        var e = NameMatcher.Normalize(expected);

        return a.Contains(e, StringComparison.Ordinal) || e.Contains(a, StringComparison.Ordinal);
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
