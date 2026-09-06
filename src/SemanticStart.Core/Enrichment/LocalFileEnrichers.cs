using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Collectors;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Enrichment;

public sealed class PeVersionEnricher : IEnricher
{
    public string Provider => "pe-version";
    public bool RequiresNetwork => false;
    public bool CanEnrich(Entity entity) => ResolvePath(entity) is not null;

    public Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(entity);
        if (path is null)
            return Task.FromResult<IReadOnlyList<EnrichmentDocument>>([]);

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var parts = new List<string>();
            Add(parts, "File description", info.FileDescription);
            Add(parts, "Product name", info.ProductName);
            Add(parts, "Company", info.CompanyName);
            Add(parts, "Comments", info.Comments);
            Add(parts, "Copyright", info.LegalCopyright);
            return Task.FromResult<IReadOnlyList<EnrichmentDocument>>(parts.Count == 0 ? [] :
                [new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = false, Text = string.Join(Environment.NewLine, parts), SourceUri = path }]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Task.FromResult<IReadOnlyList<EnrichmentDocument>>([]);
        }
    }

    private static void Add(List<string> parts, string label, string? value)
    {
        value = CleanValue(value);
        if (value is not null)
            parts.Add($"{label}: {value}");
    }

    private static string? CleanValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = Regex.Replace(value.Trim(), @"\s+", " ");
        var lower = value.ToLowerInvariant();
        if (lower is "application" or "app" or "program" or "todo" or "none" or "unknown" or "n/a") return null;
        if (Regex.IsMatch(value, @"^\$\{.+\}$|^@\{.+\}$") || value.Length < 3) return null;
        return value;
    }

    private static string? ResolvePath(Entity entity)
    {
        foreach (var candidate in CandidatePaths(entity))
        {
            var path = StripIconIndex(candidate);
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim('"'));
                if (File.Exists(path) && (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                    return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return null;
    }

    private static IEnumerable<string?> CandidatePaths(Entity entity)
    {
        yield return entity.LaunchTarget;
        yield return entity.IconSource;
        if (entity.RawMetadata.TryGetValue("targetPath", out var target)) yield return target;
        if (entity.RawMetadata.TryGetValue("fileName", out var fileName) && !Path.IsPathRooted(fileName))
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), fileName);
    }

    private static string? StripIconIndex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var comma = value.LastIndexOf(',');
        return comma > 2 && int.TryParse(value[(comma + 1)..], out _) ? value[..comma] : value;
    }
}

public sealed class MsixManifestEnricher : IEnricher
{
    public string Provider => "msix-manifest";
    public bool RequiresNetwork => false;
    public bool CanEnrich(Entity entity) => entity.Kind == EntityKind.PackagedApp && GetPackageFamilyName(entity) is not null;

    public async Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        var family = GetPackageFamilyName(entity);
        if (family is null) return [];
        var installLocation = await ResolveInstallLocationAsync(family, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(installLocation)) return [];
        var manifest = Path.Combine(installLocation, "AppxManifest.xml");
        if (!File.Exists(manifest)) return [];

        try
        {
            await using var stream = File.OpenRead(manifest);
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            var root = doc.Root;
            if (root is null) return [];
            var lines = new List<string>();
            Add(lines, "Display name", root.Descendants().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value);
            Add(lines, "Description", root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Description")?.Value);
            Add(lines, "Publisher", root.Descendants().FirstOrDefault(e => e.Name.LocalName == "PublisherDisplayName")?.Value);

            // A package can host many applications - the Sysinternals Suite ships more than seventy
            // in one manifest. Taking every VisualElements element gave each of them the same
            // multi-kilobyte blob describing all the others, so ZoomIt was summarised as
            // "Sysinternals Suite Application display." Select the one Application whose Id matches
            // this entity's AppUserModelId, and only fall back to all of them for single-app
            // packages where there is no ambiguity.
            var appId = GetApplicationId(entity);
            var applications = root.Descendants().Where(e => e.Name.LocalName == "Application").ToList();
            var scope = appId is null
                ? applications
                : applications
                    .Where(a => string.Equals(a.Attribute("Id")?.Value, appId, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (scope.Count == 0)
                scope = applications.Count == 1 ? applications : [];

            foreach (var visual in scope.SelectMany(a => a.Descendants().Where(e => e.Name.LocalName == "VisualElements")))
            {
                Add(lines, "Application description", visual.Attributes().FirstOrDefault(a => a.Name.LocalName == "Description")?.Value);
                Add(lines, "Application display name", visual.Attributes().FirstOrDefault(a => a.Name.LocalName == "DisplayName")?.Value);
            }
            lines = lines.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return lines.Count == 0 ? [] : [new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = false, Text = string.Join(Environment.NewLine, lines), SourceUri = manifest }];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException)
        {
            return [];
        }
    }

    private static void Add(List<string> lines, string label, string? value)
    {
        value = Normalize(value);
        if (!string.IsNullOrWhiteSpace(value)) lines.Add($"{label}: {value}");
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = Regex.Replace(value.Trim(), @"\s+", " ");
        return value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    private static string? GetApplicationId(Entity entity)
    {
        var aumid = entity.RawMetadata.GetValueOrDefault("appUserModelId") ?? entity.LaunchTarget;
        var bang = aumid.IndexOf('!');
        return bang >= 0 && bang < aumid.Length - 1 ? aumid[(bang + 1)..] : null;
    }

    private static async Task<string?> ResolveInstallLocationAsync(string packageFamilyName, CancellationToken cancellationToken) =>
        await PackageCatalog.ResolveInstallLocationAsync(packageFamilyName, cancellationToken).ConfigureAwait(false);

    private static string? GetPackageFamilyName(Entity entity)
    {
        if (entity.RawMetadata.TryGetValue("packageFamilyName", out var pf) && !string.IsNullOrWhiteSpace(pf)) return pf.Trim();
        if (entity.RawMetadata.TryGetValue("appUserModelId", out var aumid) && aumid.Contains('!')) return aumid.Split('!', 2)[0];
        return entity.LaunchTarget.Contains('!') ? entity.LaunchTarget.Split('!', 2)[0] : null;
    }

}
