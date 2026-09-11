using SemanticStart.Core.Abstractions;

namespace SemanticStart.Core.Collectors;

public static class CollectorRegistry
{
    public static IReadOnlyList<IEntityCollector> CreateAll() =>
    [
        new AppsFolderCollector(),
        new StartShortcutCollector(),
        new UninstallRegistryCollector(),
        new SettingsPageCollector(),
        new ControlPanelCollector(),
        new OptionalFeatureCollector(),
        new SystemToolCollector(),
        new CommandAliasCollector(),

        // Last, so every other source wins deduplication. A PATH directory holds the binary but
        // knows nothing about it; the AppsFolder, Start Menu, and uninstall registry all carry a
        // real display name, and the curated system tools carry a written description. When the
        // same executable is found twice, that is the record worth keeping.
        new PathExecutableCollector(),
    ];
}
