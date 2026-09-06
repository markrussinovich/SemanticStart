using System.Diagnostics;
using SemanticStart.Core.Collectors;
using SemanticStart.Core.Embeddings;
using SemanticStart.Core.Enrichment;
using SemanticStart.Core.Indexing;
using SemanticStart.Core.Model;
using SemanticStart.Core.Query;
using SemanticStart.Core.Storage;
using SemanticStart.Core.Synthesis;

namespace SemanticStart.Cli;

/// <summary>
/// Development and diagnostic front end for the index. The overlay UI consumes the same Core
/// services; this exists so the pipeline can be built, measured, and regression tested without
/// any UI in the way.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        try
        {
            return command switch
            {
                "index" => await IndexAsync(args),
                "search" => await SearchAsync(args),
                "eval" => await EvalAsync(),
                "stats" => await StatsAsync(),
                "enrich" => await EnrichAsync(args),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            SemanticStart - semantic search over installed apps and Windows features

              index [--online] [--force]              Build or refresh the index
              search <query> [-n N]        Query the index
              eval                         Run the relevance harness
              stats                        Show index statistics
              enrich <name> [--online]     Show what each enricher produces for one entity
            """);
        return 0;
    }

    /// <summary>
    /// Runs the enrichment pipeline for a single entity with every failure printed. The pipeline
    /// deliberately swallows per-enricher exceptions so one bad network response cannot fail an
    /// index build, but that also makes a broken enricher indistinguishable from one that simply
    /// found nothing. This command is the way to tell those apart.
    /// </summary>
    private static async Task<int> EnrichAsync(string[] args)
    {
        var name = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("usage: enrich <name> [--online]");
            return 1;
        }

        var online = args.Contains("--online", StringComparer.OrdinalIgnoreCase);
        var entities = new List<Entity>();
        foreach (var collector in CollectorRegistry.CreateAll().Where(c => c.IsSupported))
        {
            await foreach (var entity in collector.CollectAsync().ConfigureAwait(false))
                entities.Add(entity);
        }

        var matches = entities
            .Where(e => e.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToArray();

        if (matches.Length == 0)
        {
            Console.Error.WriteLine($"no entity matching '{name}'");
            return 1;
        }

        using var http = new HttpClient();
        var enrichers = EnricherRegistry.CreateAll(http);

        foreach (var entity in matches)
        {
            Console.WriteLine($"=== {entity.DisplayName}  [{entity.Kind}]  {entity.Id}");
            foreach (var enricher in enrichers)
            {
                if (!enricher.CanEnrich(entity))
                    continue;
                if (enricher.RequiresNetwork && !online)
                {
                    Console.WriteLine($"  {enricher.Provider,-16} skipped (needs --online)");
                    continue;
                }

                try
                {
                    var docs = await enricher.EnrichAsync(entity).ConfigureAwait(false);
                    if (docs.Count == 0)
                    {
                        Console.WriteLine($"  {enricher.Provider,-16} no documents");
                        continue;
                    }

                    foreach (var doc in docs)
                    {
                        var text = doc.Text.Replace("\n", " ", StringComparison.Ordinal);
                        Console.WriteLine($"  {enricher.Provider,-16} {doc.SourceUri}");
                        Console.WriteLine($"  {"",-16} {text[..Math.Min(200, text.Length)]}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {enricher.Provider,-16} FAILED {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        return 0;
    }

    private static async Task<int> IndexAsync(string[] args)
    {
        var allowNetwork = args.Contains("--online");
        var force = args.Contains("--force");

        Console.WriteLine("Preparing embedding model...");
        var embeddings = await CreateEmbeddingModelAsync();

        using (embeddings)
        {
            using var store = new SqliteIndexStore();

            var profiler = new EnrichmentPipeline(EnricherRegistry.CreateAll(), new HeuristicProfileSynthesizer());
            var builder = new IndexBuilder(CollectorRegistry.CreateAll(), profiler, embeddings, store);

            var lastPhase = string.Empty;
            var progress = new Progress<IndexProgress>(p =>
            {
                if (p.Phase != lastPhase)
                {
                    lastPhase = p.Phase;
                    Console.WriteLine();
                }

                if (p.Total > 0)
                    Console.Write($"\r{p.Phase}... {p.Completed}/{p.Total}   ");
                else
                    Console.Write($"\r{p.Phase}... {p.CurrentItem}   ");
            });

            var result = await builder.BuildAsync(
                new IndexOptions { AllowNetwork = allowNetwork, ForceFullRebuild = force },
                progress);

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine($"Done: {result}");
            return 0;
        }
    }

    private static async Task<int> SearchAsync(string[] args)
    {
        var limit = 10;
        var terms = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "-n" or "--limit" && i + 1 < args.Length)
            {
                limit = int.Parse(args[++i]);
                continue;
            }

            terms.Add(args[i]);
        }

        var query = string.Join(' ', terms);
        if (string.IsNullOrWhiteSpace(query))
        {
            Console.Error.WriteLine("usage: search <query>");
            return 1;
        }

        var (engine, embeddings, store) = await CreateEngineAsync();

        using (embeddings)
        using (store)
        {
            var sw = Stopwatch.StartNew();
            var hits = await engine.SearchAsync(query, limit);
            sw.Stop();

            Console.WriteLine($"\"{query}\" -> {hits.Count} results in {sw.Elapsed.TotalMilliseconds:F1} ms");
            Console.WriteLine();

            var rank = 1;
            foreach (var hit in hits)
            {
                Console.WriteLine($"{rank++,2}. {hit.Entity.DisplayName}  [{hit.Entity.Kind}]  ({hit.Entity.Source})");

                if (!string.IsNullOrWhiteSpace(hit.Summary))
                    Console.WriteLine($"    {hit.Summary}");

                Console.WriteLine(
                    $"    score {hit.Score:F3}  vector {Format(hit.VectorScore)}  " +
                    $"lexical {Format(hit.LexicalScore)}  via {hit.MatchReason}");
            }

            return 0;
        }

        static string Format(double? value) => value.HasValue ? value.Value.ToString("F3") : "-";
    }

    private static async Task<int> EvalAsync()
    {
        var (engine, embeddings, store) = await CreateEngineAsync();

        using (embeddings)
        using (store)
        {
            Console.WriteLine(
                $"Evaluating {RelevanceCorpus.All.Count} relevance cases against {engine.Count} entities...");
            Console.WriteLine();

            var harness = new RelevanceHarness(engine);
            var report = await harness.RunAsync();

            foreach (var outcome in report.Outcomes)
            {
                var mark = outcome.Passed ? "PASS" : "FAIL";
                var rank = outcome.MatchedRank is { } r ? $"@{r}" : "  ";
                Console.WriteLine($"[{mark}]{rank} \"{outcome.Case.Query}\"");

                if (!outcome.Passed)
                    Console.WriteLine($"         got: {string.Join(" > ", outcome.ActualTop)}");
            }

            Console.WriteLine();
            Console.WriteLine(report.ToSummary());

            return report.PassRate >= 0.7 ? 0 : 2;
        }
    }

    private static async Task<int> StatsAsync()
    {
        using var store = new SqliteIndexStore();
        var embeddings = await CreateEmbeddingModelAsync();

        using (embeddings)
        {
            await store.InitializeAsync(embeddings.ModelId, embeddings.Dimensions);
            var all = await store.GetAllAsync();

            Console.WriteLine($"Entities:  {all.Count}");
            Console.WriteLine($"Embedded:  {all.Count(e => e.VectorOrdinal.HasValue)}");
            Console.WriteLine($"Profiled:  {all.Count(e => e.Profile is not null)}");
            Console.WriteLine();
            Console.WriteLine("By kind:");

            foreach (var group in all.GroupBy(e => e.Entity.Kind).OrderByDescending(g => g.Count()))
                Console.WriteLine($"  {group.Key,-20} {group.Count()}");

            Console.WriteLine();
            Console.WriteLine("By source:");

            foreach (var group in all.GroupBy(e => e.Entity.Source).OrderByDescending(g => g.Count()))
                Console.WriteLine($"  {group.Key,-20} {group.Count()}");

            Console.WriteLine();
            Console.WriteLine("By profile generator:");

            foreach (var group in all.GroupBy(e => e.Profile?.Generator ?? "none").OrderByDescending(g => g.Count()))
                Console.WriteLine($"  {group.Key,-20} {group.Count()}");

            return 0;
        }
    }

    private static async Task<MiniLmEmbeddingModel> CreateEmbeddingModelAsync()
    {
        var bootstrapper = new EmbeddingModelBootstrapper();

        var reported = -1;
        var progress = new Progress<double>(p =>
        {
            var percent = (int)(p * 100);
            if (percent / 10 > reported / 10)
            {
                reported = percent;
                Console.Write($"\rDownloading model... {percent}%   ");
            }
        });

        var files = await bootstrapper.EnsureAsync(progress);
        return new MiniLmEmbeddingModel(files.ModelPath, files.VocabPath);
    }

    private static async Task<(HybridSearchEngine Engine, MiniLmEmbeddingModel Embeddings, SqliteIndexStore Store)>
        CreateEngineAsync()
    {
        var embeddings = await CreateEmbeddingModelAsync();
        var store = new SqliteIndexStore();

        await store.InitializeAsync(embeddings.ModelId, embeddings.Dimensions);

        var engine = new HybridSearchEngine(store, embeddings);
        await engine.LoadAsync();

        return (engine, embeddings, store);
    }
}
