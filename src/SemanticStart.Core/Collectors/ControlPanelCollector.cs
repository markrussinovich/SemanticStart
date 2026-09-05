using System.Diagnostics;
using System.Runtime.CompilerServices;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Collectors;

public sealed class ControlPanelCollector : IEntityCollector
{
    private static readonly IReadOnlyDictionary<string, string> FriendlyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["appwiz.cpl"] = "Programs and Features",
        ["bthprops.cpl"] = "Bluetooth Devices",
        ["desk.cpl"] = "Display Settings",
        ["firewall.cpl"] = "Windows Defender Firewall",
        ["hdwwiz.cpl"] = "Add Hardware",
        ["inetcpl.cpl"] = "Internet Properties",
        ["intl.cpl"] = "Region",
        ["irprops.cpl"] = "Infrared",
        ["joy.cpl"] = "Game Controllers",
        ["main.cpl"] = "Mouse Properties",
        ["mmsys.cpl"] = "Sound",
        ["ncpa.cpl"] = "Network Connections",
        ["powercfg.cpl"] = "Power Options",
        ["sysdm.cpl"] = "System Properties",
        ["tabletpc.cpl"] = "Tablet PC Settings",
        ["telephon.cpl"] = "Phone and Modem",
        ["timedate.cpl"] = "Date and Time",
        ["wscui.cpl"] = "Security and Maintenance",
        ["compmgmt.msc"] = "Computer Management",
        ["devmgmt.msc"] = "Device Manager",
        ["diskmgmt.msc"] = "Disk Management",
        ["eventvwr.msc"] = "Event Viewer",
        ["gpedit.msc"] = "Local Group Policy Editor",
        ["lusrmgr.msc"] = "Local Users and Groups",
        ["perfmon.msc"] = "Performance Monitor",
        ["printmanagement.msc"] = "Print Management",
        ["rsop.msc"] = "Resultant Set of Policy",
        ["secpol.msc"] = "Local Security Policy",
        ["services.msc"] = "Services",
        ["taskschd.msc"] = "Task Scheduler",
        ["wf.msc"] = "Windows Defender Firewall with Advanced Security",
        ["wmimgmt.msc"] = "WMI Control",
    };

    public string Source => "controlpanel";

    public bool IsSupported => OperatingSystem.IsWindows() && Directory.Exists(SystemDirectory);

    public async IAsyncEnumerable<Entity> CollectAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        if (!IsSupported)
            yield break;

        foreach (var pattern in new[] { "*.cpl", "*.msc" })
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(SystemDirectory, pattern, SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entity = TryCreateEntity(file);
                if (entity is not null)
                    yield return entity;
            }
        }
    }

    private Entity? TryCreateEntity(string path)
    {
        try
        {
            var fileName = Path.GetFileName(path);
            var isCpl = path.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase);
            var displayName = FriendlyNames.TryGetValue(fileName, out var friendly)
                ? friendly
                : FileVersionInfo.GetVersionInfo(path).FileDescription;

            if (string.IsNullOrWhiteSpace(displayName))
                displayName = Path.GetFileNameWithoutExtension(path);

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fileName"] = fileName,
            };

            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
                metadata["fileDescription"] = description;

            var entity = new Entity
            {
                Id = EntityId.Create(Source, path),
                Kind = isCpl ? EntityKind.ControlPanelApplet : EntityKind.ManagementConsole,
                DisplayName = displayName,
                LaunchKind = isCpl ? LaunchKind.ControlPanel : LaunchKind.Mmc,
                LaunchTarget = path,
                IconSource = path,
                Source = Source,
                RawMetadata = metadata,
            };

            return CollectorEntity.WithContentHash(entity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string SystemDirectory => Environment.GetFolderPath(Environment.SpecialFolder.System);
}
