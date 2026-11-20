using System.Collections.Concurrent;
using System.Diagnostics;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Indexing;
using SemanticStart.Core.Model;
using SemanticStart.Core.Synthesis;

namespace SemanticStart.Core.Enrichment;

public sealed record EnrichmentOptions
{
    public bool AllowNetwork { get; init; }
    public int MaxDegreeOfParallelism { get; init; } = 2;
    public TimeSpan NetworkTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Documents already held for this entity, available to carry forward.</summary>
    public IReadOnlyList<EnrichmentDocument> StoredDocuments { get; init; } = [];

    /// <summary>Providers whose stored documents are stale and must be gathered again.</summary>
    public IReadOnlySet<string> RefreshProviders { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Run no enricher at all beyond those named as stale.</summary>
    public bool ReuseStoredDocuments { get; init; }
}

public static class EnricherRegistry
{
    public static IReadOnlyList<IEnricher> CreateAll(HttpClient? http = null)
    {
        http ??= new HttpClient();
        return
        [
            new PeVersionEnricher(),
            new MsixManifestEnricher(),
            new ShortcutMetadataEnricher(),
            new AdjacentDocsEnricher(),
            new CliHelpEnricher(),
            new UiResourceEnricher(),
            new WingetManifestEnricher(),
            new LearnEnricher(http),
            new WikipediaEnricher(http),
            new PublisherSiteEnricher(http),
        ];
    }
}

public sealed class EnrichmentPipeline : IEntityProfiler
{
    private readonly IReadOnlyList<IEnricher> _enrichers;
    private readonly IProfileSynthesizer _synthesizer;
    private readonly int _maxDegreeOfParallelism;
    private readonly SemaphoreSlim _providerGate = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
    private readonly Action<string>? _diagnostic;

    public EnrichmentPipeline(IReadOnlyList<IEnricher>? enrichers = null, IProfileSynthesizer? synthesizer = null,
        int? maxDegreeOfParallelism = null, Action<string>? diagnostic = null)
    {
        _enrichers = enrichers ?? EnricherRegistry.CreateAll();
        _synthesizer = synthesizer ?? new HeuristicProfileSynthesizer();
        _maxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism ?? 2);
        _diagnostic = diagnostic;
    }

    public async Task<(IReadOnlyList<EnrichmentDocument> Documents, SynthesizedProfile Profile)> ProfileAsync(
        Entity entity,
        bool allowNetwork,
        CancellationToken cancellationToken = default)
    {
        var options = new EnrichmentOptions { AllowNetwork = allowNetwork, MaxDegreeOfParallelism = _maxDegreeOfParallelism };
        return await EnrichAndSynthesizeAsync(entity, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<EnrichmentDocument> Documents, SynthesizedProfile Profile)> ProfileAsync(
        Entity entity,
        bool allowNetwork,
        IReadOnlyList<EnrichmentDocument> storedDocuments,
        IReadOnlySet<string> refreshProviders,
        bool reuseAll,
        CancellationToken cancellationToken = default)
    {
        var options = new EnrichmentOptions
        {
            AllowNetwork = allowNetwork,
            MaxDegreeOfParallelism = _maxDegreeOfParallelism,
            StoredDocuments = storedDocuments,
            RefreshProviders = refreshProviders,
            ReuseStoredDocuments = reuseAll,
        };

        return await EnrichAndSynthesizeAsync(entity, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Which enrichers have to run, and which documents can simply be carried forward.
    ///
    /// A provider is re-run when it was named as stale, or when nothing is stored for it - the
    /// second case matters because it is what lets a newly added enricher fill itself in without
    /// a full rebuild, and what stops an entity that has never been enriched from being left
    /// empty. Everything else keeps the text already held.
    /// </summary>
    private (IEnricher[] ToRun, EnrichmentDocument[] Carried) PlanEnrichment(Entity entity, EnrichmentOptions options)
    {
        var candidates = _enrichers
            .Where(e => e.CanEnrich(entity) && (!e.RequiresNetwork || options.AllowNetwork))
            .ToArray();

        if (!options.ReuseStoredDocuments && options.RefreshProviders.Count == 0)
            return (candidates, []);

        var stored = options.StoredDocuments
            .Where(d => !options.RefreshProviders.Contains(d.Provider))
            .ToArray();

        var held = stored.Select(d => d.Provider).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toRun = candidates
            .Where(e => options.RefreshProviders.Contains(e.Provider)
                || (!options.ReuseStoredDocuments && !held.Contains(e.Provider)))
            .ToArray();

        return (toRun, stored);
    }

    public async Task<(IReadOnlyList<EnrichmentDocument> Documents, SynthesizedProfile Profile)> EnrichAndSynthesizeAsync(
        Entity entity,
        EnrichmentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new EnrichmentOptions();
        var documents = new ConcurrentBag<EnrichmentDocument>();
        var (candidates, carried) = PlanEnrichment(entity, options);

        foreach (var document in carried)
            documents.Add(document);

        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism), CancellationToken = cancellationToken },
            async (enricher, ct) =>
            {
                await _providerGate.WaitAsync(ct).ConfigureAwait(false);
                var timer = Stopwatch.StartNew();
                try
                {
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    if (enricher.RequiresNetwork)
                        budget.CancelAfter(options.NetworkTimeout);
                    var docs = await enricher.EnrichAsync(entity, budget.Token).ConfigureAwait(false);
                    budget.Token.ThrowIfCancellationRequested();
                    foreach (var doc in docs)
                    {
                        var text = EnrichmentTextNormalizer.ToPlainText(doc.Text);
                        if (!string.IsNullOrWhiteSpace(text))
                            documents.Add(doc with { Text = text });
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _diagnostic?.Invoke($"Enricher {enricher.Provider} timed out for {entity.Id} after {timer.Elapsed.TotalSeconds:F1}s.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    var message = $"Enricher {enricher.Provider} failed for {entity.Id}: {ex.Message}";
                    Debug.WriteLine(message);
                    _diagnostic?.Invoke(message);
                }
                finally
                {
                    if (timer.Elapsed >= TimeSpan.FromSeconds(2))
                        _diagnostic?.Invoke($"Enricher {enricher.Provider} for {entity.Id}: {timer.Elapsed.TotalSeconds:F1}s.");
                    _providerGate.Release();
                }
            }).ConfigureAwait(false);

        var ordered = documents
            .GroupBy(d => d.Provider + "|" + d.Text, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(d => d.IsOnline)
            .ThenBy(d => d.Provider, StringComparer.Ordinal)
            .ToArray();

        SynthesizedProfile profile;
        try
        {
            profile = await _synthesizer.SynthesizeAsync(entity, ordered, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"Synthesizer '{_synthesizer.Generator}' failed for {entity.Id}: {ex.Message}");
            profile = await new HeuristicProfileSynthesizer().SynthesizeAsync(entity, ordered, cancellationToken).ConfigureAwait(false);
        }

        return (ordered, profile);
    }
}
