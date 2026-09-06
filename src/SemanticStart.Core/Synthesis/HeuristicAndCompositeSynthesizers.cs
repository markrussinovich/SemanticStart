using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Enrichment;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Synthesis;

public sealed class HeuristicProfileSynthesizer : IProfileSynthesizer
{
    public string Generator => "fallback";
    public bool IsAvailable => true;

    public Task<SynthesizedProfile> SynthesizeAsync(Entity entity, IReadOnlyList<EnrichmentDocument> documents, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var described = BestDescription(entity, documents);
        var summary = BuildSummary(entity, described);
        var category = BuildCategory(entity);
        var tasks = ProfileText.Distinctive(BuildTasks(entity, documents, described), 10).ToArray();
        var synonyms = BuildSynonyms(entity, documents)
            .Concat(ActionVerbs(summary, entity.DisplayName))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToArray();
        return Task.FromResult(new SynthesizedProfile { EntityId = entity.Id, Summary = summary, Tasks = tasks, Synonyms = synonyms, Category = category, Details = ProfileText.Details(documents), Generator = Generator });
    }

    /// <summary>
    /// Derives the verb a user would type from the agent noun a vendor writes. Product prose is
    /// full of "-er"/"-or" nouns naming the tool, while queries use the corresponding verb: a
    /// profile saying "code editor" is the right answer for "edit a file", "screen recorder" for
    /// "record my screen", "file manager" for "manage files". Stemming does not bridge this,
    /// because Porter deliberately leaves agent nouns intact, so the two forms never met in the
    /// lexical index and entities were not retrieved at all.
    ///
    /// This is morphology, not vocabulary: it derives from whatever text the entity actually has
    /// and so applies to programs that did not exist when this was written.
    ///
    /// Words belonging to the entity's own name are excluded, because deriving a verb from a name
    /// is circular - it manufactures evidence of function out of the label alone. Registry Editor
    /// and Local Group Policy Editor have no harvested description at all, so their summaries are
    /// the placeholder "Open {name}"; taking "edit" from that made both of them outrank every text
    /// editor on the machine for "edit a file", despite neither having anything to do with files.
    /// A description that independently says "video editor" or "code editor" still contributes,
    /// because that word is evidence rather than a restatement of the name.
    /// </summary>
    private static IEnumerable<string> ActionVerbs(string text, string displayName)
    {
        var nameWords = new HashSet<string>(
            Regex.Split(displayName ?? string.Empty, @"\W+").Where(w => w.Length > 0).Select(w => w.ToLowerInvariant()),
            StringComparer.Ordinal);

        foreach (var raw in Regex.Split(text, @"\W+"))
        {
            if (raw.Length < 6) continue;
            var word = raw.ToLowerInvariant();
            if (nameWords.Contains(word)) continue;
            if (!word.EndsWith("er", StringComparison.Ordinal) && !word.EndsWith("or", StringComparison.Ordinal)) continue;

            var stem = word[..^2];
            if (stem.Length < 3) continue;

            // "debugger" -> "debugg" -> "debug"; "manager" -> "manag" -> "manage".
            if (stem.Length > 3 && stem[^1] == stem[^2] && !"aeiou".Contains(stem[^1]))
                yield return stem[..^1];
            else
            {
                yield return stem;
                if (!"aeiou".Contains(stem[^1]))
                    yield return stem + "e";
            }
        }
    }

    internal static IReadOnlyList<string> GetHeuristicSynonyms(Entity entity) => BuildSynonyms(entity).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();

    private static string BuildSummary(Entity entity, string? described)
    {
        var best = described;
        if (string.IsNullOrWhiteSpace(best))
            best = entity.Kind switch
            {
                EntityKind.SettingsPage => $"Open Windows settings for {entity.DisplayName}.",
                EntityKind.ManagementConsole => $"Open the {entity.DisplayName} management console.",
                EntityKind.ControlPanelApplet => $"Open the {entity.DisplayName} Control Panel applet.",
                EntityKind.SystemTool => $"Run the Windows {entity.DisplayName} system tool.",
                _ => $"Open {entity.DisplayName}."
            };
        best = CleanSentence(best);
        return best.EndsWith('.') ? best : best + ".";
    }

    private static string? BestDescription(Entity entity, IReadOnlyList<EnrichmentDocument> documents)
    {
        var known = KnownDescription(entity);
        if (!string.IsNullOrWhiteSpace(known)) return known;
        foreach (var key in new[] { "description", "fileDescription", "comment" })
            if (entity.RawMetadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                value = EnrichmentTextNormalizer.ToPlainText(value);
                if (!EnrichmentTextNormalizer.IsLikelyMarkupLine(value) && AddsInformation(entity, value)) return value;
            }

        // Ordered by how directly the source describes the product itself. A packaging manifest or
        // a winget entry contains a description the publisher wrote *about the program*; a
        // documentation article is prose about a topic, and its opening sentence is frequently
        // about something narrower than the product. Power Automate is the clear case: its Learn
        // hit was a service-region table, while its winget entry says "Automate workflows across
        // modern and legacy applications on your desktop". Article text remains ahead of shortcut
        // and adjacent-file text, which describe the installation rather than the program, and it
        // is the only useful source for inbox Windows features, which have no packaging entry.
        //
        // An encyclopedia lead sits between the two. It is written to explain a thing to someone
        // who does not know it, so it names capabilities in ordinary words, where vendor
        // documentation names them in product terms or describes the article instead of the
        // product. It ranks below the publisher's own manifest, which is authoritative about what
        // the program is, and above documentation prose.
        foreach (var provider in new[] { "pe-version", "msix-manifest", "winget", "wikipedia", "learn", "publisher-site", "local-docs", "shortcut" })

        {
            var doc = documents.FirstOrDefault(d => d.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));
            var value = ExtractUsefulLine(doc?.Text);
            if (!string.IsNullOrWhiteSpace(value) && AddsInformation(entity, value)) return value;
        }
        return null;
    }

    private static readonly HashSet<string> UninformativeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "for", "of", "to", "in", "on", "with", "by", "from", "is", "are",
        "app", "apps", "application", "applications", "display", "name", "tool", "tools", "utility",
        "program", "software", "microsoft", "windows", "suite", "package", "version", "open", "run", "start"
    };

    /// <summary>
    /// Rejects a candidate description that only restates the entity's own name. MSIX manifests are
    /// the worst offender: the Sysinternals suite declares every application's description to be its
    /// own name, so ZoomIt was summarised as "ZoomIt Application display" and that empty text then
    /// pre-empted its real documentation from Microsoft Learn. A description must contribute at
    /// least two words that are neither part of the name nor generic packaging vocabulary.
    /// </summary>
    private static bool AddsInformation(Entity entity, string candidate)
    {
        if (IsMachineNoise(candidate) || IsShelfBoilerplate(candidate))
            return false;

        var nameWords = Regex.Split(entity.DisplayName, @"\W+")
            .Where(w => w.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var informative = Regex.Split(candidate, @"\W+")
            .Where(w => w.Length > 2 && !nameWords.Contains(w) && !UninformativeWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return informative >= 2;
    }

    /// <summary>
    /// Rejects text carrying machine-generated identifiers: region codes, GUIDs, deployment slugs,
    /// hashes. Power Automate was summarised as "URI Power Platform Region Is preview
    /// prodnorwayeastmmrns-1-whuok7nwdzy2s.", harvested from an app manifest. That is not a
    /// description of anything, but it contains the word "Power" twice, which was enough to make it
    /// the top lexical hit for "set low power" and outrank the actual battery settings page.
    ///
    /// The test is structural rather than a list of known-bad strings: a token that is long, mixes
    /// letters and digits, and is not a recognisable word is an identifier, whatever product it
    /// came from.
    /// </summary>
    private static bool IsMachineNoise(string candidate)
    {
        var tokens = Regex.Split(candidate, @"[\s,;:()\[\]]+").Where(t => t.Length > 0).ToArray();
        if (tokens.Length == 0)
            return true;

        var noisy = tokens.Count(IsIdentifierLike);
        return noisy > 0;
    }

    private static bool IsIdentifierLike(string token)
    {
        var t = token.Trim('.', ',', '-', '_', ')', '(');
        if (t.Length < 12)
            return false;

        var longestRun = 0;
        var run = 0;
        var runHasDigit = false;
        var runHasLetter = false;
        var mixedRun = false;
        foreach (var ch in t)
        {
            if (char.IsLetterOrDigit(ch))
            {
                run++;
                runHasDigit |= char.IsDigit(ch);
                runHasLetter |= char.IsLetter(ch);
                if (run >= 12 && runHasDigit && runHasLetter) mixedRun = true;
                longestRun = Math.Max(longestRun, run);
            }
            else { run = 0; runHasDigit = false; runHasLetter = false; }
        }

        // Two shapes of identifier, chosen so that ordinary product vocabulary survives:
        // an unbroken run far longer than an English word ("prodnorwayeastmmrns"), or a long run
        // mixing letters and digits ("whuok7nwdzy2s"). "PowerShell7" and "Windows11" are shorter
        // than both thresholds and are kept.
        return longestRun >= 16 || mixedRun;
    }

    /// <summary>
    /// Rejects descriptions that describe the shortcut rather than the program. Visual Studio Code
    /// was summarised as "Visual Studio Code Categorized under Visual Studio Code in the Start
    /// Menu", which is true, useless, and crowded out its real description. The Start Menu folder
    /// is a useful *category* signal and is still indexed as such; it is never a summary.
    /// </summary>
    private static bool IsShelfBoilerplate(string candidate)
        => ShelfMarkers.Any(m => candidate.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] ShelfMarkers =
    [
        "categorized under", "in the start menu", "start menu folder", "shortcut to",
        "installed under", "located in", "this shortcut"
    ];

    private static string? KnownDescription(Entity entity)
    {
        // Intentionally empty. Hand-written descriptions for specific programs were removed:
        // they could only ever describe the handful of entities someone thought of while writing
        // this file, which is worthless on a machine with a different set of software installed.
        // Descriptions now come exclusively from the entity's own metadata and harvested
        // documentation, so quality improvements here benefit every entity rather than a list.
        _ = entity;
        return null;
    }

    private static string? ExtractUsefulLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var label in new[] { "Summary", "Short Description", "Description", "Application description" })
        {
            var labeled = ExtractFirstLabeledValue(text, label);
            if (!string.IsNullOrWhiteSpace(labeled) && IsUsefulDescriptionLine(labeled))
                return labeled;
        }

        foreach (var raw in text.Split(['\r', '\n', '.'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (EnrichmentTextNormalizer.IsLikelyMarkupLine(line)) continue;
            line = EnrichmentTextNormalizer.ToPlainText(line);
            if (line.StartsWith("File description:", StringComparison.OrdinalIgnoreCase)) line = line[17..].Trim();
            if (line.StartsWith("Description:", StringComparison.OrdinalIgnoreCase)) line = line[12..].Trim();
            if (line.StartsWith("Short Description:", StringComparison.OrdinalIgnoreCase)) line = line[18..].Trim();
            if (line.StartsWith("Summary:", StringComparison.OrdinalIgnoreCase)) line = line[8..].Trim();
            if (line.StartsWith("Product name:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Company:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Publisher:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Homepage:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Tasks:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Synonyms:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Tags:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Moniker:", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsUsefulDescriptionLine(line))
            {
                return line;
            }
        }
        return null;
    }

    private static string? ExtractFirstLabeledValue(string text, string label)
    {
        var pattern = $@"(?i)\b{Regex.Escape(label)}\s*:\s*(?<value>.*?)(?=\b(?:Summary|Tasks|Synonyms|Tags|Moniker|Description|Short Description|Application description|Homepage|Publisher|Name|Product name|Company)\s*:|$)";
        foreach (Match match in Regex.Matches(text, pattern, RegexOptions.Singleline))
        {
            var value = EnrichmentTextNormalizer.ToPlainText(match.Groups["value"].Value);
            if (IsUsefulDescriptionLine(value))
                return value;
        }

        return null;
    }

    private static bool IsUsefulDescriptionLine(string line)
        => line.Length >= 8
           && line.Count(char.IsWhiteSpace) >= 2
           && !line.Contains("copyright", StringComparison.OrdinalIgnoreCase)
           && !line.Contains("all rights reserved", StringComparison.OrdinalIgnoreCase)
           && !line.Contains("Theme Auto Light Dark", StringComparison.OrdinalIgnoreCase)
           && !line.StartsWith("Table of contents", StringComparison.OrdinalIgnoreCase)
           && !line.StartsWith("Index ", StringComparison.OrdinalIgnoreCase)
           && !line.Contains('»')
           && !Regex.IsMatch(line, @"^\d+(?:\.\d+)*\s+\w")
           && !line.Contains(@"{\rtf", StringComparison.OrdinalIgnoreCase)
           && !line.Contains("://", StringComparison.OrdinalIgnoreCase)
           && !line.StartsWith("genindex-", StringComparison.OrdinalIgnoreCase)
           && !EnrichmentTextNormalizer.IsLikelyMarkupLine(line);

    private static string BuildCategory(Entity entity)
    {
        if (entity.RawMetadata.TryGetValue("startMenuFolder", out var folder) && !string.IsNullOrWhiteSpace(folder))
            return folder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "Applications";
        var target = (entity.RawMetadata.GetValueOrDefault("fileName") ?? Path.GetFileName(entity.LaunchTarget) ?? entity.DisplayName).ToLowerInvariant();
        if (target is "cleanmgr.exe" or "chkdsk.exe" or "diskpart.exe" or "fsutil.exe" or "compact.exe" or "cipher.exe" or "diskmgmt.msc") return "Storage & Disk";
        if (target is "devmgmt.msc" or "hdwwiz.cpl" or "driverquery.exe" or "dxdiag.exe") return "Devices & Hardware";
        if (target is "ipconfig.exe" or "ping.exe" or "tracert.exe" or "netstat.exe" or "nslookup.exe" or "route.exe" or "arp.exe" or "netsh.exe") return "Networking";
        if (target is "regedit.exe" or "reg.exe" or "services.msc" or "taskschd.msc" or "eventvwr.msc" or "perfmon.msc" or "resmon.exe") return "System Administration";
        if (entity.LaunchTarget.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) return "Windows Settings";
        return entity.Kind switch
        {
            EntityKind.Application or EntityKind.PackagedApp => "Applications",
            EntityKind.ControlPanelApplet => "Control Panel",
            EntityKind.ManagementConsole => "Management Consoles",
            EntityKind.OptionalFeature => "Windows Features",
            EntityKind.SystemTool => "System Tools",
            EntityKind.ShellLocation => "Files & Folders",
            _ => "Utilities"
        };
    }

    /// <summary>
    /// Derives the intent phrases a user might search for. <paramref name="described"/> is the
    /// harvested description or null when nothing was found; the placeholder summary is
    /// deliberately not accepted here. Feeding the placeholder in meant intents were inferred from
    /// an entity's own name: the Windows optional feature "Browser Internet Explorer" was assigned
    /// "search the web" and "connect to the internet" purely because those words appear in its
    /// title, and it then outranked Microsoft Edge for exactly that query. An entity with no
    /// documentation now claims no intents.
    /// </summary>
    private static IEnumerable<string> BuildTasks(Entity entity, IReadOnlyList<EnrichmentDocument> documents, string? described)
    {

        foreach (var phrase in ExtractLabeledPhrases(documents, "Tasks")) yield return phrase;
        foreach (var phrase in IntentPhrases(entity.DisplayName + " " + described)) yield return phrase;
        yield return entity.Kind switch
        {
            EntityKind.SettingsPage => "change Windows settings",
            EntityKind.ManagementConsole => "manage Windows components",
            EntityKind.ControlPanelApplet => "configure Control Panel settings",
            EntityKind.SystemTool => "troubleshoot Windows from the command line",
            _ => "open " + entity.DisplayName.ToLowerInvariant()
        };
    }

    /// <summary>
    /// Maps vocabulary in an entity's own one-line description to the intent phrasing a user would
    /// type. The text examined is deliberately narrow. Feeding it the harvested document bodies
    /// instead matched boilerplate: a Learn page footer mentioning the internet was enough to tell
    /// the index that a screen-annotation tool helps you connect to the internet, and those phrases
    /// then steered the entity's embedding away from what it actually does.
    /// Matching is on whole words, so "power" no longer fires on "PowerPoint".
    /// </summary>
    private static IEnumerable<string> IntentPhrases(string text)
    {
        var mappings = new (string Key, string Task)[]
        {
            ("browser", "search the web"), ("web", "browse websites and search the internet"), ("internet", "connect to the internet"),
            ("microphone", "choose the default microphone"), ("audio input", "set the audio input device"), ("sound", "adjust sound devices"),
            ("battery", "find why battery is draining"), ("energy", "reduce power use"), ("power", "change power settings"),
            ("text size", "make text bigger"), ("font size", "increase font size"), ("accessibility", "make Windows easier to use"),
            ("disk", "manage disks and storage"), ("temporary", "remove temporary files"), ("driver", "manage device drivers"),
            ("device", "manage connected devices"), ("network", "troubleshoot network connections"), ("printer", "manage printers"),
            ("display", "change display settings"), ("security", "review security settings"), ("update", "manage Windows updates"), ("file", "work with files")
        };

        foreach (var (key, task) in mappings)
        {
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(key)}s?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                yield return task;
        }
    }

    private static IEnumerable<string> BuildSynonyms(Entity entity, IReadOnlyList<EnrichmentDocument>? documents = null)
    {
        yield return entity.DisplayName;
        var name = RemoveVendorPrefix(entity.DisplayName);
        if (!name.Equals(entity.DisplayName, StringComparison.OrdinalIgnoreCase)) yield return name;
        foreach (var token in SplitTokens(name)) yield return token;
        var initials = string.Concat(SplitTokens(name).Where(t => t.Length > 0).Select(t => char.ToLowerInvariant(t[0])));
        if (initials.Length is >= 2 and <= 6) yield return initials;
        var file = entity.RawMetadata.GetValueOrDefault("fileName") ?? Path.GetFileName(entity.LaunchTarget);
        if (!string.IsNullOrWhiteSpace(file))
        {
            yield return file;
            yield return Path.GetFileNameWithoutExtension(file);
        }
        if (entity.LaunchTarget.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) yield return entity.LaunchTarget;
        if (documents is not null)
        {
            foreach (var synonym in ExtractLabeledPhrases(documents, "Synonyms")) yield return synonym;
            foreach (var tag in ExtractLabeledPhrases(documents, "Tags")) yield return tag;
            foreach (var moniker in ExtractLabeledPhrases(documents, "Moniker")) yield return moniker;
        }
    }

    private static IEnumerable<string> ExtractLabeledPhrases(IReadOnlyList<EnrichmentDocument> documents, string label)
    {
        var pattern = $@"(?i)\b{Regex.Escape(label)}\s*:\s*(?<value>.*?)(?=\b(?:Summary|Tasks|Synonyms|Tags|Moniker|Description|Short Description|Homepage|Publisher|Name)\s*:|$)";
        foreach (var doc in documents)
        {
            foreach (Match match in Regex.Matches(doc.Text, pattern, RegexOptions.Singleline))
            {
                foreach (var phrase in Regex.Split(match.Groups["value"].Value, @"[;,|]"))
                {
                    var clean = EnrichmentTextNormalizer.ToPlainText(phrase);
                    if (clean.Length >= 2 && !EnrichmentTextNormalizer.IsLikelyMarkupLine(clean))
                        yield return clean;
                }
            }
        }
    }

    private static string RemoveVendorPrefix(string name) => Regex.Replace(name, @"^(Microsoft|Windows|Microsoft Windows)\s+", "", RegexOptions.IgnoreCase).Trim();
    private static IEnumerable<string> SplitTokens(string value) => Regex.Split(value, @"[^A-Za-z0-9]+|(?<=[a-z])(?=[A-Z])").Where(t => t.Length > 1).Select(t => t.ToLowerInvariant());
    private static string CleanSentence(string value) => Regex.Replace(value.Trim(), @"\s+", " ");
}

public sealed class CompositeProfileSynthesizer : IProfileSynthesizer
{
    private readonly IProfileSynthesizer _llm;
    private readonly HeuristicProfileSynthesizer _fallback;
    public string Generator => _llm.IsAvailable ? _llm.Generator : _fallback.Generator;
    public bool IsAvailable => true;
    public CompositeProfileSynthesizer(IProfileSynthesizer? llm = null, HeuristicProfileSynthesizer? fallback = null)
    {
        _llm = llm ?? new LocalLlmProfileSynthesizer();
        _fallback = fallback ?? new HeuristicProfileSynthesizer();
    }

    public async Task<SynthesizedProfile> SynthesizeAsync(Entity entity, IReadOnlyList<EnrichmentDocument> documents, CancellationToken cancellationToken = default)
    {
        var heuristic = await _fallback.SynthesizeAsync(entity, documents, cancellationToken).ConfigureAwait(false);
        if (!_llm.IsAvailable) return heuristic;
        try
        {
            // The synthesizer owns its own per-request timeout. A cap here covered queue wait as
            // well as the request itself, so once calls were serialised behind a single local model
            // every entity's budget expired before its turn and the whole index silently fell back.
            var profile = await _llm.SynthesizeAsync(entity, documents, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(profile.Summary) || profile.Tasks.Count == 0) return heuristic;

            // Synonyms are merged, tasks are not. Concatenating both task lists was tried and was
            // measurably worse (38/41 against 39/41): the embedded document is a fixed budget, and
            // padding it with near-duplicate phrasings of the same intent dilutes the signal that
            // makes an entity findable. The model's phrasing wins outright when it produced any.
            var synonyms = profile.Synonyms.Concat(heuristic.Synonyms).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToArray();
            return profile with { Synonyms = synonyms, Category = string.IsNullOrWhiteSpace(profile.Category) ? heuristic.Category : profile.Category };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return heuristic;
        }
    }
}
