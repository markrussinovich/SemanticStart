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
    public int MaxDegreeOfParallelism { get; init; } = Math.Max(2, Environment.ProcessorCount / 2);
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
            new WingetManifestEnricher(),
            new LearnEnricher(http),
            new PublisherSiteEnricher(http),
        ];
    }
}

public sealed class EnrichmentPipeline : IEntityProfiler
{
    private readonly IReadOnlyList<IEnricher> _enrichers;
    private readonly IProfileSynthesizer _synthesizer;
    private readonly int _maxDegreeOfParallelism;

    public EnrichmentPipeline(IReadOnlyList<IEnricher>? enrichers = null, IProfileSynthesizer? synthesizer = null, int? maxDegreeOfParallelism = null)
    {
        _enrichers = enrichers ?? EnricherRegistry.CreateAll();
        _synthesizer = synthesizer ?? new CompositeProfileSynthesizer();
        _maxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism ?? Math.Max(2, Environment.ProcessorCount / 2));
    }

    public async Task<(IReadOnlyList<EnrichmentDocument> Documents, SynthesizedProfile Profile)> ProfileAsync(
        Entity entity,
        bool allowNetwork,
        CancellationToken cancellationToken = default)
    {
        var options = new EnrichmentOptions { AllowNetwork = allowNetwork, MaxDegreeOfParallelism = _maxDegreeOfParallelism };
        return await EnrichAndSynthesizeAsync(entity, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<EnrichmentDocument> Documents, SynthesizedProfile Profile)> EnrichAndSynthesizeAsync(
        Entity entity,
        EnrichmentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new EnrichmentOptions();
        var documents = new ConcurrentBag<EnrichmentDocument>();
        var candidates = _enrichers.Where(e => e.CanEnrich(entity) && (!e.RequiresNetwork || options.AllowNetwork)).ToArray();

        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism), CancellationToken = cancellationToken },
            async (enricher, ct) =>
            {
                try
                {
                    var docs = await enricher.EnrichAsync(entity, ct).ConfigureAwait(false);
                    foreach (var doc in docs.Where(d => !string.IsNullOrWhiteSpace(d.Text)))
                        documents.Add(doc);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Debug.WriteLine($"Enricher '{enricher.Provider}' failed for {entity.Id}: {ex.Message}"); }
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
