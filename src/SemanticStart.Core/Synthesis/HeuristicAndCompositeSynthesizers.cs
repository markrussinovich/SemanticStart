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
        var summary = BuildSummary(entity, documents);
        var category = BuildCategory(entity);
        var tasks = BuildTasks(entity, documents, summary).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
        var synonyms = BuildSynonyms(entity, documents).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        return Task.FromResult(new SynthesizedProfile { EntityId = entity.Id, Summary = summary, Tasks = tasks, Synonyms = synonyms, Category = category, Generator = Generator });
    }

    internal static IReadOnlyList<string> GetHeuristicSynonyms(Entity entity) => BuildSynonyms(entity).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();

    private static string BuildSummary(Entity entity, IReadOnlyList<EnrichmentDocument> documents)
    {
        var best = BestDescription(entity, documents);
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
        var curated = documents.FirstOrDefault(d => d.Provider.Equals("windows-intent-catalog", StringComparison.OrdinalIgnoreCase));
        var curatedValue = ExtractUsefulLine(curated?.Text);
        if (!string.IsNullOrWhiteSpace(curatedValue)) return curatedValue;
        foreach (var key in new[] { "description", "fileDescription", "comment" })
            if (entity.RawMetadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                value = EnrichmentTextNormalizer.ToPlainText(value);
                if (!EnrichmentTextNormalizer.IsLikelyMarkupLine(value)) return value;
            }
        foreach (var provider in new[] { "windows-intent-catalog", "pe-version", "msix-manifest", "shortcut", "local-docs", "learn", "winget", "publisher-site" })
        {
            var doc = documents.FirstOrDefault(d => d.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));
            var value = ExtractUsefulLine(doc?.Text);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static string? KnownDescription(Entity entity)
    {
        if (MatchesExact(entity, "Notepad", "notepad.exe")) return "Create, open, and edit plain text notes and files.";
        if (MatchesExact(entity, "Disk Cleanup", "cleanmgr.exe")) return "Free disk space by removing temporary and unnecessary files.";
        if (MatchesExact(entity, "Device Manager", "devmgmt.msc")) return "View and manage hardware devices, drivers, and device status.";
        if (entity.LaunchTarget.Equals("ms-settings:display", StringComparison.OrdinalIgnoreCase) || entity.DisplayName.Equals("Display", StringComparison.OrdinalIgnoreCase)) return "Change monitor layout, brightness, scale, resolution, and advanced display settings.";
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

    private static IEnumerable<string> BuildTasks(Entity entity, IReadOnlyList<EnrichmentDocument> documents, string summary)
    {
        if (MatchesExact(entity, "Notepad", "notepad.exe")) { yield return "take quick notes"; yield return "edit a plain text file"; yield return "open a text document"; yield break; }
        if (MatchesExact(entity, "Disk Cleanup", "cleanmgr.exe")) { yield return "free up disk space"; yield return "delete temporary files"; yield return "clean up old Windows files"; yield break; }
        if (MatchesExact(entity, "Device Manager", "devmgmt.msc")) { yield return "manage hardware devices"; yield return "update device drivers"; yield return "troubleshoot a missing device"; yield break; }
        if (entity.LaunchTarget.Equals("ms-settings:display", StringComparison.OrdinalIgnoreCase) || entity.DisplayName.Equals("Display", StringComparison.OrdinalIgnoreCase)) { yield return "change screen resolution"; yield return "adjust display scale"; yield return "arrange monitors"; yield return "change brightness"; yield break; }

        foreach (var phrase in ExtractLabeledPhrases(documents, "Tasks")) yield return phrase;
        foreach (var phrase in IntentPhrases(summary + " " + string.Join(' ', documents.Select(d => d.Text)))) yield return phrase;
        yield return entity.Kind switch
        {
            EntityKind.SettingsPage => "change Windows settings",
            EntityKind.ManagementConsole => "manage Windows components",
            EntityKind.ControlPanelApplet => "configure Control Panel settings",
            EntityKind.SystemTool => "troubleshoot Windows from the command line",
            _ => "open " + entity.DisplayName.ToLowerInvariant()
        };
    }

    private static bool MatchesExact(Entity entity, string displayName, string fileName)
    {
        if (entity.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase))
            return true;

        var target = entity.RawMetadata.GetValueOrDefault("fileName") ?? Path.GetFileName(entity.LaunchTarget);
        return target is not null && target.Equals(fileName, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> IntentPhrases(string text)
    {
        text = text.ToLowerInvariant();
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
        foreach (var (key, task) in mappings) if (text.Contains(key, StringComparison.Ordinal)) yield return task;
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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var profile = await _llm.SynthesizeAsync(entity, documents, cts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(profile.Summary) || profile.Tasks.Count == 0) return heuristic;
            var synonyms = profile.Synonyms.Concat(heuristic.Synonyms).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToArray();
            return profile with { Synonyms = synonyms, Category = string.IsNullOrWhiteSpace(profile.Category) ? heuristic.Category : profile.Category };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return heuristic;
        }
    }
}
