using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Enrichment;
using SemanticStart.Core.Model;
using SemanticStart.Core.Synthesis;

namespace SemanticStart.Tests;

public sealed class EnrichmentTests
{
    /// <summary>
    /// A console tool asked to describe itself either prints usage or complains about the switch.
    /// The complaint used to be stored as though it were documentation, which both failed to
    /// document the tool and spent its one piece of evidence on the words of an error message.
    /// </summary>
    [Theory]
    // Rejections: one line, and short.
    [InlineData(@"C:\tools\node.exe: bad option: -?", 9, false)]
    [InlineData("Unknown option '-?'", 1, false)]
    [InlineData("", 0, false)]
    // Long, but still a single line - a tool echoing a path and a complaint.
    [InlineData(@"C:\Program Files\Some Vendor\With A Long Installation Path\thetool.exe: unrecognized option '-?'. Try --help.", 1, false)]
    // Multi-line, but with nothing in it.
    [InlineData("error\nbad option", 1, false)]
    // Real usage text: many lines, hundreds of characters. Robocopy prints this and exits 16.
    [InlineData("Usage: robocopy source destination [file [file]...] [options]\n\n  source :: Source Directory (drive:\\path or \\\\server\\share\\path).\n  destination :: Destination Dir (drive:\\path or \\\\server\\share\\path).\n  /S :: copy Subdirectories, but not empty ones.\n  /E :: copy subdirectories, including Empty ones.", 16, true)]
    public void CliHelp_StoresUsageTextButNotSwitchRejections(string output, int exitCode, bool expected) =>
        Assert.Equal(expected, CliHelpEnricher.LooksLikeHelp(output, exitCode));

    /// <summary>
    /// A help probe must never perform an action. The Git family reads "--help" as a request to
    /// open the documentation, so probing git-lfs, scalar or git-receive-pack with it launches a
    /// web browser onto the user's desktop - which it did, before this was pinned down. No switch
    /// whose meaning is "show the user something" belongs in the ladder, and inspecting the reply
    /// cannot undo it, because the browser is already open by then.
    /// </summary>
    [Fact]
    public void CliHelp_NeverProbesWithASwitchThatOpensDocumentation()
    {
        var switches = CliHelpEnricher.HelpSwitchLadder.SelectMany(s => s).ToArray();

        Assert.DoesNotContain("--help", switches);
        Assert.DoesNotContain("-help", switches);
        Assert.NotEmpty(switches);
    }

    /// <summary>
    /// A tool that writes UTF-16 to a redirected pipe arrives as its characters interleaved with
    /// NULs. Recovering it is what turns six Sysinternals tools from an empty document into their
    /// real help. Correctly decoded output must pass through untouched.
    /// </summary>
    [Fact]
    public void CliHelp_RecoversTextFromAToolThatWroteUtf16()
    {
        var wide = "\0 \0 \0Y\0o\0u\0 \0m\0u\0s\0t\0 \0b\0e\0 \0a\0n\0 \0a\0d\0m\0i\0n";
        Assert.Equal("  You must be an admin", CliHelpEnricher.RepairWideOutput(wide));

        const string clean = "Usage: tool [options]\n  -a  does a thing\n  -b  does another";
        Assert.Same(clean, CliHelpEnricher.RepairWideOutput(clean));
    }

    /// <summary>
    /// A stray NUL carries no meaning in console output, so it is dropped wherever it appears.
    /// Sysmon pads only part of its output and came out 13% NUL, which a density threshold set
    /// for fully interleaved UTF-16 would have missed, leaving it to store nothing at all.
    /// </summary>
    [Fact]
    public void CliHelp_DropsNulsEvenWhenOnlyPartOfTheOutputIsPadded()
    {
        var text = "Usage: tool [options]\0 and a great deal more ordinary help text follows here";
        Assert.Equal("Usage: tool [options] and a great deal more ordinary help text follows here", CliHelpEnricher.RepairWideOutput(text));
    }

    /// <summary>
    /// A crash is not documentation however long it is. The pip console shims on PATH answer "-?"
    /// with a stack trace, which clears every length bar and then matches queries by way of
    /// interpreter paths and generic runtime vocabulary.
    /// </summary>
    [Theory]
    [InlineData("Error:Unknown Usage: tqdm [ help | options]\nTraceback (most recent call last):\n  File \"<frozen runpy>\", line 198, in _run_module_as_main\n  File \"runpy.py\", line 88, in _run_code\nKeyError: '?'\nDuring handling of the above exception, another exception occurred:\n  more interpreter frames here to pad the length out past the bar")]
    [InlineData("Unhandled exception. System.ArgumentException: the argument was not recognised\n   at Some.Namespace.Program.Main(String[] args)\n   at Some.Namespace.Runner.Invoke()\n   more frames follow here to pad this comfortably past three hundred characters so that only the crash rule is able to reject it, and never the length rule on its own")]
    public void CliHelp_RejectsACrashDumpHoweverLongItIs(string output)
    {
        Assert.True(output.Length >= 300, "the case must clear the length bar so it tests the crash rule");
        Assert.False(CliHelpEnricher.LooksLikeHelp(output, 1));
        Assert.False(CliHelpEnricher.LooksLikeHelp(output, 0));
    }

    /// <summary>
    /// The exit code moves the bar rather than deciding the outcome. The same argument-parser
    /// complaint - several lines and about 200 characters, which is more than a terse rejection
    /// but far less than a usage screen - is believed from a tool that exited cleanly and
    /// disbelieved from one that failed.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(2, false)]
    public void CliHelp_DemandsMoreEvidenceFromAToolThatFailed(int exitCode, bool expected)
    {
        var borderline = "usage: idna [-h] [--version] name\n" + new string('x', 80) + "\nidna: error: unrecognized arguments: -?";
        Assert.InRange(borderline.Length, 120, 299);
        Assert.Equal(expected, CliHelpEnricher.LooksLikeHelp(borderline, exitCode));
    }

    /// <summary>
    /// A failing tool is not silenced, only held to a higher bar: a full usage screen is still
    /// stored. This is the case that matters most, because most console tools that print help do
    /// exit non-zero afterwards.
    /// </summary>
    [Fact]
    public void CliHelp_StillStoresAFullUsageScreenFromAToolThatFailed()
    {
        var usage = "Usage: accesschk [-s][-e][-u][-r][-w] [[-a]|[-k]|[-p [-f]]|[-o]|[-c]|[-d]] [username] objectname\n"
            + string.Join("\n", Enumerable.Range(0, 12).Select(i => $"  -{(char)('a' + i)}  Option number {i} described at some length here."));
        Assert.True(usage.Length >= 300);
        Assert.True(CliHelpEnricher.LooksLikeHelp(usage, -1));
    }

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

    [Fact]
    public async Task Details_CarryCapabilityVocabularyFromHarvestedProse()
    {
        var entity = CreateEntity("Task Manager", EntityKind.Application, "taskmgr.exe");
        var wikipedia = "Task Manager is a task manager, system monitor, and startup manager included with Microsoft Windows. "
                        + "It can be used to set process priorities, start and stop services, and forcibly terminate processes.";

        var profile = await new HeuristicProfileSynthesizer().SynthesizeAsync(
            entity,
            [new EnrichmentDocument { EntityId = entity.Id, Provider = "wikipedia", IsOnline = true, Text = wikipedia }]);

        // The summary is one sentence by design, so without a details field the words that make an
        // entity findable are harvested and then discarded.
        Assert.NotNull(profile.Details);
        Assert.Contains("terminate processes", profile.Details!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Details_RejectMachineReadableListings()
    {
        var entity = CreateEntity("Windows Notepad System", EntityKind.OptionalFeature, "notepad");
        var uriDump = "Default browser settings ms-settings:defaultbrowsersettings Manage optional features "
                      + "ms-settings:optionalfeatures Offline Maps ms-settings:maps ms-settings:maps-downloadmaps "
                      + "Storage Sense ms-settings:storagesense Sign-in options ms-settings:signinoptions";

        var profile = await new HeuristicProfileSynthesizer().SynthesizeAsync(
            entity,
            [new EnrichmentDocument { EntityId = entity.Id, Provider = "learn", IsOnline = true, Text = uriDump }]);

        // A reference table contributes no vocabulary a user would type, while adding tokens that
        // match at random.
        Assert.Null(profile.Details);
    }

    [Fact]
    public async Task ActionVerbs_AreNotDerivedFromTheEntityName()
    {
        var entity = CreateEntity("Registry Editor", EntityKind.Application, "regedit.exe");

        // No enrichment succeeded, so the summary is the generated "Open {name}" placeholder.
        var profile = await new HeuristicProfileSynthesizer().SynthesizeAsync(entity, []);

        // Deriving "edit" from the name manufactures evidence of function out of the label alone,
        // and made Registry Editor outrank every text editor on the machine for "edit a file".
        Assert.DoesNotContain("edit", profile.Synonyms, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActionVerbs_AreStillDerivedFromIndependentDescriptions()
    {
        var entity = CreateEntity("Clipchamp", EntityKind.Application, "clipchamp.exe");

        var profile = await new HeuristicProfileSynthesizer().SynthesizeAsync(
            entity,
            [new EnrichmentDocument { EntityId = entity.Id, Provider = "winget", IsOnline = true, Text = "Online video editor by Microsoft." }]);

        // "editor" here is evidence rather than a restatement of the name, so the verb it implies
        // is exactly the vocabulary bridge the synonyms field exists to provide.
        Assert.Contains("edit", profile.Synonyms, StringComparer.OrdinalIgnoreCase);
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
