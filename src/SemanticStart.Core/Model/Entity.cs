namespace SemanticStart.Core.Model;

/// <summary>
/// The category of thing that was indexed. Determines how the entity is launched
/// and how it is presented in the overlay.
/// </summary>
public enum EntityKind
{
    /// <summary>Classic Win32 desktop application.</summary>
    Application,

    /// <summary>MSIX/UWP/Store packaged application.</summary>
    PackagedApp,

    /// <summary>A page in the modern Settings app, launched via an ms-settings: URI.</summary>
    SettingsPage,

    /// <summary>A legacy Control Panel applet (.cpl).</summary>
    ControlPanelApplet,

    /// <summary>An MMC snap-in (.msc).</summary>
    ManagementConsole,

    /// <summary>A Windows optional feature or capability (may not be installed yet).</summary>
    OptionalFeature,

    /// <summary>A built-in command line tool shipped with Windows.</summary>
    SystemTool,

    /// <summary>A shell folder or well-known location.</summary>
    ShellLocation,
}

/// <summary>
/// How an entity is activated. Kept separate from <see cref="EntityKind"/> because several
/// kinds share a launch mechanism (for example both applications and packaged apps can be
/// launched through the AppsFolder).
/// </summary>
public enum LaunchKind
{
    /// <summary>Launch via shell:AppsFolder\{AppUserModelId}. Works for Win32 and MSIX alike.</summary>
    AppsFolder,

    /// <summary>Start an executable directly.</summary>
    Executable,

    /// <summary>Resolve and start a .lnk shortcut.</summary>
    Shortcut,

    /// <summary>Open a URI with the registered protocol handler (ms-settings:, http:, ...).</summary>
    Uri,

    /// <summary>Run control.exe against a named applet.</summary>
    ControlPanel,

    /// <summary>Run mmc.exe against a snap-in file.</summary>
    Mmc,
}

/// <summary>
/// A single indexable thing: an installed app, a Settings page, a built-in Windows feature.
/// Produced by collectors, then progressively enriched.
/// </summary>
public sealed record Entity
{
    /// <summary>
    /// Stable identity, unique across the whole index. Collectors must generate this
    /// deterministically (see <see cref="EntityId"/>) so that reindexing an unchanged
    /// machine produces identical ids and the incremental path can skip work.
    /// </summary>
    public required string Id { get; init; }

    public required EntityKind Kind { get; init; }

    /// <summary>Name shown to the user, e.g. "Microsoft Word".</summary>
    public required string DisplayName { get; init; }

    public required LaunchKind LaunchKind { get; init; }

    /// <summary>
    /// The launch payload, interpreted according to <see cref="LaunchKind"/>: an AppUserModelId,
    /// an executable path, a URI, or an applet name.
    /// </summary>
    public required string LaunchTarget { get; init; }

    /// <summary>Optional arguments passed at launch.</summary>
    public string? LaunchArguments { get; init; }

    /// <summary>Where to pull the icon from. Usually an exe, dll, or logo png path.</summary>
    public string? IconSource { get; init; }

    /// <summary>Publisher or vendor, when known. Useful as a disambiguating signal.</summary>
    public string? Publisher { get; init; }

    /// <summary>Which collector produced this entity. Used for diagnostics and dedupe precedence.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// Raw key/value metadata captured by the collector (version resources, manifest fields,
    /// Start Menu folder, registry values). Feeds enrichment; not shown directly to the user.
    /// </summary>
    public IReadOnlyDictionary<string, string> RawMetadata { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Content hash of the identity-and-metadata of this entity. When this is unchanged from
    /// the stored value, the expensive enrich/synthesize/embed stages are skipped.
    /// </summary>
    public string? ContentHash { get; init; }
}

/// <summary>
/// Deterministic entity id construction. Ids are lowercase "source:key" so they are stable
/// across runs and machines, which is what makes incremental indexing possible.
/// </summary>
public static class EntityId
{
    public static string Create(string source, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return $"{source.Trim().ToLowerInvariant()}:{key.Trim().ToLowerInvariant()}";
    }
}
