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
    public async Task Enrichment_ContainsNoHandWrittenPerProgramKnowledge()
    {
        // Generality contract. Every description, task phrase, and synonym must be derived at index
        // time from the entity's own metadata and its harvested documentation. Hand-written text for
        // named programs was removed because it can only describe the software someone happened to
        // think of while writing the code, which says nothing about the machine the tool installs
        // on. It also went stale silently: the removed catalog mapped "Resource Monitor" to Task
        // Manager's description, so the UI confidently showed the wrong summary for a real tool.
        //
        // This test fails if a per-program lookup is reintroduced, by asserting that entities the
        // old catalog covered get nothing at all from the offline enricher set.
        var pipeline = new EnrichmentPipeline(EnricherRegistry.CreateAll(), new HeuristicProfileSynthesizer(), maxDegreeOfParallelism: 1);

        var previouslyCurated = new[]
        {
            CreateEntity("Power & Battery", EntityKind.SettingsPage, "ms-settings:powersleep"),
            CreateEntity("Network Connections", EntityKind.ControlPanelApplet, @"C:\Windows\system32\ncpa.cpl"),
            CreateEntity("Disk Cleanup", EntityKind.Application, @"C:\Windows\system32\cleanmgr.exe"),
            CreateEntity("Device Manager", EntityKind.Application, @"C:\Windows\system32\devmgmt.msc"),
        };

        foreach (var entity in previouslyCurated)
        {
            var (documents, _) = await pipeline.EnrichAndSynthesizeAsync(entity, new EnrichmentOptions { AllowNetwork = false });
            Assert.DoesNotContain(documents, d => d.Provider == "windows-intent-catalog");
        }
    }

    [Fact]
    public async Task AdjacentDocs_IgnoresLicenseAndChangelogFiles()
    {
        // Visual Studio Code was summarised as 'THE SOFTWARE IS PROVIDED "AS IS"...' because its
        // LICENSE file was harvested and sorted ahead of its real description, leaving nothing in
        // the embedded text saying it is a code editor. Legal and changelog files are never a
        // description of what a program does, for any program.
        var dir = Path.Combine(Path.GetTempPath(), "ss-license-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "LICENSE.txt"), "MIT License. THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED.");
            await File.WriteAllTextAsync(Path.Combine(dir, "CHANGELOG.md"), "## 1.2.0 Fixed a crash on startup.");
            await File.WriteAllTextAsync(Path.Combine(dir, "README.md"), "A lightweight source code editor for building and debugging modern applications.");
            await File.WriteAllTextAsync(Path.Combine(dir, "app.exe"), string.Empty);

            var entity = CreateEntity("Test Editor", EntityKind.Application, Path.Combine(dir, "app.exe"));
            var documents = await new AdjacentDocsEnricher().EnrichAsync(entity);
            var text = string.Join(" ", documents.Select(d => d.Text));

            Assert.DoesNotContain("AS IS", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Fixed a crash", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("source code editor", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
