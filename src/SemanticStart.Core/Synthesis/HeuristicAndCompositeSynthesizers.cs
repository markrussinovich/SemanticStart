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
        var tasks = BuildTasks(entity, documents, summary).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        var synonyms = BuildSynonyms(entity).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
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
        foreach (var key in new[] { "description", "fileDescription", "comment" })
            if (entity.RawMetadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        foreach (var provider in new[] { "pe-version", "msix-manifest", "shortcut", "local-docs", "learn", "winget" })
        {
            var doc = documents.FirstOrDefault(d => d.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));
            var value = ExtractUsefulLine(doc?.Text);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static string? KnownDescription(Entity entity)
    {
        var name = entity.DisplayName.ToLowerInvariant();
        var target = (entity.RawMetadata.GetValueOrDefault("fileName") ?? Path.GetFileName(entity.LaunchTarget) ?? entity.LaunchTarget).ToLowerInvariant();
        if (target == "notepad.exe" || name.Contains("notepad")) return "Create, open, and edit plain text notes and files.";
        if (target == "cleanmgr.exe" || name.Contains("disk cleanup")) return "Free disk space by removing temporary and unnecessary files.";
        if (target == "devmgmt.msc" || name.Contains("device manager")) return "View and manage hardware devices, drivers, and device status.";
        if (entity.LaunchTarget.Equals("ms-settings:display", StringComparison.OrdinalIgnoreCase) || name == "display") return "Change monitor layout, brightness, scale, resolution, and advanced display settings.";
        return null;
    }

    private static string? ExtractUsefulLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var raw in text.Split(['\r', '\n', '.'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("File description:", StringComparison.OrdinalIgnoreCase)) line = line[17..].Trim();
            if (line.StartsWith("Description:", StringComparison.OrdinalIgnoreCase)) line = line[12..].Trim();
            if (line.StartsWith("Product name:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Company:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Length >= 8
                && !line.Contains("copyright", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("all rights reserved", StringComparison.OrdinalIgnoreCase)
                && !line.Contains(@"{\rtf", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }
        return null;
    }

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
        var name = entity.DisplayName.ToLowerInvariant();
        var target = (entity.RawMetadata.GetValueOrDefault("fileName") ?? Path.GetFileName(entity.LaunchTarget) ?? entity.LaunchTarget).ToLowerInvariant();
        if (target == "notepad.exe" || name.Contains("notepad")) { yield return "take quick notes"; yield return "edit a plain text file"; yield return "open a text document"; yield break; }
        if (target == "cleanmgr.exe" || name.Contains("disk cleanup")) { yield return "free up disk space"; yield return "delete temporary files"; yield return "clean up old Windows files"; yield break; }
        if (target == "devmgmt.msc" || name.Contains("device manager")) { yield return "manage hardware devices"; yield return "update device drivers"; yield return "troubleshoot a missing device"; yield break; }
        if (entity.LaunchTarget.Equals("ms-settings:display", StringComparison.OrdinalIgnoreCase) || name == "display") { yield return "change screen resolution"; yield return "adjust display scale"; yield return "arrange monitors"; yield return "change brightness"; yield break; }

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

    private static IEnumerable<string> IntentPhrases(string text)
    {
        text = text.ToLowerInvariant();
        var mappings = new (string Key, string Task)[]
        {
            ("disk", "manage disks and storage"), ("temporary", "remove temporary files"), ("driver", "manage device drivers"),
            ("device", "manage connected devices"), ("network", "troubleshoot network connections"), ("printer", "manage printers"),
            ("display", "change display settings"), ("sound", "adjust sound devices"), ("power", "change power settings"),
            ("security", "review security settings"), ("update", "manage Windows updates"), ("file", "work with files")
        };
        foreach (var (key, task) in mappings) if (text.Contains(key, StringComparison.Ordinal)) yield return task;
    }

    private static IEnumerable<string> BuildSynonyms(Entity entity)
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
