using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Enrichment;
using SemanticStart.Core.Model;
using SemanticStart.Core.Synthesis;

namespace SemanticStart.Tests;

public sealed class EnrichmentTests
{
    [Fact]
    public async Task Pipeline_StripsHtmlBeforeSynthesis()
    {
        var entity = CreateEntity("Python 3.12 Manuals (64-bit)", EntityKind.Application, "python-docs");
        var html = "html: <!DOCTYPE html><html><head><style>.x{color:red}</style><script>alert(1)</script></head><body><main><h1>Python Manuals</h1><p>Read Python language documentation and library reference.</p></main></body></html>";
        var pipeline = new EnrichmentPipeline([new FixedEnricher("local-docs", html)], new HeuristicProfileSynthesizer(), maxDegreeOfParallelism: 1);

        var (documents, profile) = await pipeline.EnrichAndSynthesizeAsync(entity, new EnrichmentOptions());

        Assert.DoesNotContain("<!DOCTYPE", documents[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<html", documents[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", documents[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Read Python language documentation and library reference.", profile.Summary);
    }

    [Fact]
    public async Task HeuristicSynthesizer_DoesNotSelectMarkupLineAsSummary()
    {
        var entity = CreateEntity("Python 3.12 Manuals (64-bit)", EntityKind.Application, "python-docs");
        var profile = await new HeuristicProfileSynthesizer().SynthesizeAsync(
            entity,
            [new EnrichmentDocument { EntityId = entity.Id, Provider = "local-docs", IsOnline = false, Text = "html: <!DOCTYPE html> <html lang=\"en\"><body>Python documentation helps developers learn the language.</body></html>" }]);

        Assert.DoesNotContain("html:", profile.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<!DOCTYPE", profile.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<body>", profile.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CuratedWindowsIntentCatalog_AddsUserIntentVocabulary()
    {
        var entity = CreateEntity("Power & Battery", EntityKind.SettingsPage, "ms-settings:powersleep") with
        {
            LaunchKind = LaunchKind.Uri,
            LaunchTarget = "ms-settings:powersleep"
        };
        var pipeline = new EnrichmentPipeline([new CuratedWindowsIntentEnricher()], new HeuristicProfileSynthesizer(), maxDegreeOfParallelism: 1);

        var (_, profile) = await pipeline.EnrichAndSynthesizeAsync(entity, new EnrichmentOptions());

        Assert.Contains(profile.Tasks, t => t.Contains("battery is draining", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(profile.Synonyms, s => s.Equals("battery", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CuratedWindowsIntentCatalog_DoesNotDescribeThirdPartyApplications()
    {
        // The catalog is hand-written, so it may only cover components that ship with Windows and
        // are therefore identical on every machine. Application entries could only ever describe
        // the programs that happened to be known when the catalog was written, which is not a
        // property any installed machine can rely on; those descriptions come from online
        // enrichment and local synthesis instead.
        var pipeline = new EnrichmentPipeline([new CuratedWindowsIntentEnricher()], new HeuristicProfileSynthesizer(), maxDegreeOfParallelism: 1);

        foreach (var name in new[] { "PowerPoint", "Microsoft Word", "Google Chrome", "Microsoft Teams" })
        {
            var entity = CreateEntity(name, EntityKind.Application, name);
            var (documents, _) = await pipeline.EnrichAndSynthesizeAsync(entity, new EnrichmentOptions());

            Assert.DoesNotContain(documents, d => d.Provider == "windows-intent-catalog");
        }
    }

    [Fact]
    public async Task CuratedWindowsIntentCatalog_StillCoversInboxWindowsComponents()
    {
        var entity = CreateEntity("Network Connections", EntityKind.ControlPanelApplet, @"C:\Windows\system32\ncpa.cpl");
        var pipeline = new EnrichmentPipeline([new CuratedWindowsIntentEnricher()], new HeuristicProfileSynthesizer(), maxDegreeOfParallelism: 1);

        var (_, profile) = await pipeline.EnrichAndSynthesizeAsync(entity, new EnrichmentOptions());

        Assert.Contains(profile.Tasks, t => t.Contains("internet not working", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HeuristicSynthesizer_DoesNotApplyNotepadKnowledgeToOptionalFeatureContainingNotepad()
    {
        var entity = CreateEntity("Microsoft Windows Notepad System", EntityKind.OptionalFeature, "ms-settings:optionalfeatures") with
        {
            RawMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["featureName"] = "Microsoft-Windows-Notepad-System",
            }
        };

        var profile = await new HeuristicProfileSynthesizer().SynthesizeAsync(entity, []);

        Assert.DoesNotContain("plain text notes", profile.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(profile.Tasks, t => t.Contains("quick notes", StringComparison.OrdinalIgnoreCase));
    }

    private static Entity CreateEntity(string name, EntityKind kind, string target) => new()
    {
        Id = "test:" + target,
        Kind = kind,
        DisplayName = name,
        LaunchKind = LaunchKind.Executable,
        LaunchTarget = target,
        Source = "test",
    };

    private sealed class FixedEnricher(string provider, string text) : IEnricher
    {
        public string Provider => provider;
        public bool RequiresNetwork => false;
        public bool CanEnrich(Entity entity) => true;

        public Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<EnrichmentDocument>>([new EnrichmentDocument { EntityId = entity.Id, Provider = provider, IsOnline = false, Text = text }]);
    }
}
