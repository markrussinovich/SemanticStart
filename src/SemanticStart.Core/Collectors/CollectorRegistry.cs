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
    ];
}
