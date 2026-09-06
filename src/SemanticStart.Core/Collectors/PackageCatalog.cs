using System.Xml.Linq;
using SemanticStart.Core.Enrichment;

namespace SemanticStart.Core.Collectors;

/// <summary>
/// Answers where an MSIX package lives on disk and which executable one of its applications runs.
///
/// The AppsFolder identifies an application by AppUserModelId - "Family_PublisherId!ApplicationId" -
/// which is an opaque handle: it cannot be shown to the user, compared with a path from any other
/// collector, or handed to a file-based enricher. Everything needed to translate it is in the
/// package manifest, so it is read once per package and cached for the whole build.
///
/// Both the package map and the manifests are cached for process lifetime. Indexing touches every
/// packaged app, and a machine with the Sysinternals Suite installed asks about one manifest more
/// than seventy times.
/// </summary>
public static class PackageCatalog
{
    private static Task<IReadOnlyDictionary<string, string>>? _packageMap;
    private static readonly SemaphoreSlim PackageMapLock = new(1, 1);

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> ManifestCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim ManifestLock = new(1, 1);

    /// <summary>
    /// Install directory for a package family, or null when the package is not registered to this
    /// user or its files are not readable.
    /// </summary>
    public static async Task<string?> ResolveInstallLocationAsync(string packageFamilyName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName))
            return null;

        var map = await GetPackageMapAsync(cancellationToken).ConfigureAwait(false);
        if (map.TryGetValue(packageFamilyName, out var location) && Directory.Exists(location))
            return location;

        try
        {
            // Fallback for when PowerShell is unavailable. WindowsApps directories are named
            // Name_Version_Arch__PublisherId, so a package family name (Name_PublisherId) is not a
            // prefix of them; the two halves have to be matched separately.
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (!Directory.Exists(root))
                return null;

            var split = packageFamilyName.LastIndexOf('_');
            if (split <= 0)
                return null;

            var name = packageFamilyName[..split];
            var publisherId = packageFamilyName[(split + 1)..];

            return Directory
                .EnumerateDirectories(root, name + "_*__" + publisherId, SearchOption.TopDirectoryOnly)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "AppxManifest.xml")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Full path of the executable an AppUserModelId activates, or null when it cannot be
    /// determined. A package with exactly one application resolves even when the identifier
    /// carries no application id, because there is nothing to disambiguate.
    /// </summary>
    public static async Task<string?> ResolveExecutableAsync(string appUserModelId, CancellationToken cancellationToken = default)
    {
        var (family, applicationId) = SplitAppUserModelId(appUserModelId);
        if (family is null)
            return null;

        var installLocation = await ResolveInstallLocationAsync(family, cancellationToken).ConfigureAwait(false);
        if (installLocation is null)
            return null;

        return await ResolveExecutableInAsync(installLocation, applicationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Same resolution against a known install directory. Separate so the manifest handling can be
    /// exercised without a package registered on the machine running the test.
    /// </summary>
    public static async Task<string?> ResolveExecutableInAsync(string installLocation, string? applicationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
            return null;

        var executables = await GetExecutablesAsync(installLocation, cancellationToken).ConfigureAwait(false);
        if (executables.Count == 0)
            return null;

        string? relative = null;

        if (applicationId is not null)
            executables.TryGetValue(applicationId, out relative);

        // A single-application package is unambiguous, so an identifier that names no application
        // - or names one the manifest spells differently - still resolves.
        if (relative is null && executables.Count == 1)
            relative = executables.Values.First();

        if (string.IsNullOrWhiteSpace(relative))
            return null;

        // Several applications declaring the same executable means it is a launcher stub that
        // dispatches on its command line - the Sysinternals Suite starts ZoomIt through a shared
        // RunUnpackaged.exe. Such a path identifies nothing: showing it would name the wrong file,
        // and using it as a dedupe key would collapse every application in the package into
        // whichever one was seen first. Prefer a binary named after the application, and report
        // nothing at all rather than the stub.
        if (executables.Values.Count(v => string.Equals(v, relative, StringComparison.OrdinalIgnoreCase)) > 1)
            return applicationId is null ? null : FindExecutableNamed(installLocation, applicationId);

        var full = Path.Combine(installLocation, relative.Replace('/', '\\'));
        return File.Exists(full) ? full : null;
    }

    /// <summary>
    /// Looks for a binary named after the application id, in the package root or one level below,
    /// which is where suites keep the real executables their stub launcher starts.
    /// </summary>
    private static string? FindExecutableNamed(string installLocation, string applicationId)
    {
        try
        {
            var fileName = applicationId + ".exe";
            var direct = Path.Combine(installLocation, fileName);
            if (File.Exists(direct))
                return direct;

            foreach (var directory in Directory.EnumerateDirectories(installLocation))
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }

        return null;
    }

    /// <summary>
    /// Splits "Family_PublisherId!ApplicationId" into its two halves. The application id is
    /// optional; some AppsFolder entries carry the family alone.
    /// </summary>
    public static (string? Family, string? ApplicationId) SplitAppUserModelId(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
            return (null, null);

        var value = appUserModelId.Trim();
        if (value.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
            value = value["shell:AppsFolder\\".Length..];

        var bang = value.IndexOf('!');
        if (bang < 0)
            return (value.Length > 0 ? value : null, null);

        var family = value[..bang];
        var appId = bang < value.Length - 1 ? value[(bang + 1)..] : null;
        return (family.Length > 0 ? family : null, appId);
    }

    /// <summary>Application id to executable path, relative to the install location, for one package.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> GetExecutablesAsync(string installLocation, CancellationToken cancellationToken)
    {
        await ManifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ManifestCache.TryGetValue(installLocation, out var cached))
                return cached;
        }
        finally
        {
            ManifestLock.Release();
        }

        var executables = await ReadManifestExecutablesAsync(installLocation, cancellationToken).ConfigureAwait(false);

        await ManifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ManifestCache[installLocation] = executables;
        }
        finally
        {
            ManifestLock.Release();
        }

        return executables;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadManifestExecutablesAsync(string installLocation, CancellationToken cancellationToken)
    {
        var empty = (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifest = Path.Combine(installLocation, "AppxManifest.xml");
        if (!File.Exists(manifest))
            return empty;

        try
        {
            await using var stream = File.OpenRead(manifest);
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var application in doc.Root?.Descendants().Where(e => e.Name.LocalName == "Application") ?? [])
            {
                var id = application.Attribute("Id")?.Value;
                var executable = application.Attribute("Executable")?.Value;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(executable))
                    map[id] = executable;
            }

            return map;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException)
        {
            return empty;
        }
    }

    /// <summary>
    /// Maps package family name to install location for every package registered to the user.
    ///
    /// This is resolved once for the whole index rather than per entity. An earlier version ran a
    /// PowerShell process per packaged app, which on a normal machine means well over a hundred
    /// process launches, and it asked for a parameter that does not exist: Get-AppxPackage has no
    /// -PackageFamilyName. Every call therefore failed with a parameter binding error, so no MSIX
    /// app in the index ever received a manifest document.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> GetPackageMapAsync(CancellationToken cancellationToken)
    {
        if (_packageMap is not null)
            return await _packageMap.ConfigureAwait(false);

        await PackageMapLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _packageMap ??= LoadPackageMapAsync(cancellationToken);
        }
        finally
        {
            PackageMapLock.Release();
        }

        return await _packageMap.ConfigureAwait(false);
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadPackageMapAsync(CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(ps))
            return map;

        const string Script = "Get-AppxPackage | ForEach-Object { $_.PackageFamilyName + '|' + $_.InstallLocation }";
        var output = await ProcessRunner
            .RunAsync(ps, ["-NoProfile", "-NonInteractive", "-Command", Script], TimeSpan.FromSeconds(60), cancellationToken)
            .ConfigureAwait(false);

        foreach (var line in output.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', 2);
            if (parts.Length != 2)
                continue;

            var family = parts[0].Trim();
            var location = parts[1].Trim();
            if (family.Length > 0 && location.Length > 0)
                map[family] = location;
        }

        return map;
    }
}
