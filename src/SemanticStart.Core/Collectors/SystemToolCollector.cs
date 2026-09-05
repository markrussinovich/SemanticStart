using System.Runtime.CompilerServices;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Collectors;

public sealed class SystemToolCollector : IEntityCollector
{
    private static readonly IReadOnlyList<SystemTool> Tools =
    [
        new("ipconfig.exe", "IP Configuration", "Display and refresh IP network configuration."),
        new("ping.exe", "Ping", "Test network reachability and latency to a host."),
        new("tracert.exe", "Trace Route", "Trace the network path packets take to a destination."),
        new("netstat.exe", "Netstat", "Show active network connections, ports, and protocol statistics."),
        new("nslookup.exe", "NSLookup", "Query DNS records and troubleshoot name resolution."),
        new("route.exe", "Route", "View and modify the IP routing table."),
        new("arp.exe", "ARP", "Display and modify the address resolution protocol cache."),
        new("netsh.exe", "Netsh", "Configure network components from the command line."),
        new("sfc.exe", "System File Checker", "Scan and repair protected Windows system files."),
        new("dism.exe", "Deployment Image Servicing and Management", "Service Windows images, features, packages, and component store health."),
        new("chkdsk.exe", "Check Disk", "Check a volume for file system and disk errors."),
        new("diskpart.exe", "DiskPart", "Manage disks, partitions, and volumes."),
        new("tasklist.exe", "Tasklist", "List running processes and services."),
        new("taskkill.exe", "Taskkill", "Terminate processes by process id or image name."),
        new("systeminfo.exe", "System Information", "Display operating system, hardware, hotfix, and configuration details."),
        new("whoami.exe", "Whoami", "Show the current user, groups, and privileges."),
        new("gpupdate.exe", "Group Policy Update", "Refresh local and domain Group Policy settings."),
        new("gpresult.exe", "Group Policy Result", "Report applied Group Policy settings for a user or computer."),
        new("robocopy.exe", "Robocopy", "Robustly copy files and directory trees."),
        new("xcopy.exe", "Xcopy", "Copy files and directory trees with legacy options."),
        new("schtasks.exe", "Scheduled Tasks", "Create, query, change, run, and delete scheduled tasks."),
        new("sc.exe", "Service Control", "Query, configure, start, and stop Windows services."),
        new("reg.exe", "Registry Console Tool", "Query and modify the Windows registry from the command line."),
        new("bcdedit.exe", "Boot Configuration Data Editor", "View and modify boot configuration data."),
        new("powercfg.exe", "Power Configuration", "Manage power plans, sleep, hibernate, and battery reports."),
        new("wmic.exe", "Windows Management Instrumentation Command-line", "Query WMI information using the legacy WMIC tool."),
        new("driverquery.exe", "Driver Query", "List installed device drivers and driver properties."),
        new("pathping.exe", "PathPing", "Combine ping and route tracing with packet loss statistics."),
        new("telnet.exe", "Telnet Client", "Open a Telnet connection when the optional client is installed."),
        new("ftp.exe", "FTP Client", "Transfer files with the built-in command-line FTP client."),
        new("certutil.exe", "Certificate Utility", "Manage certificates, encode files, hash files, and troubleshoot PKI."),
        new("cipher.exe", "Cipher", "Manage EFS encryption and securely wipe free space."),
        new("compact.exe", "Compact", "Show or change NTFS file compression."),
        new("fsutil.exe", "File System Utility", "Query and configure advanced file system settings."),
        new("icacls.exe", "ICACLS", "View and modify file and folder access control lists."),
        new("takeown.exe", "Take Ownership", "Take ownership of files or folders."),
        new("shutdown.exe", "Shutdown", "Shut down, restart, sign out, or hibernate the computer."),
        new("msinfo32.exe", "System Information", "Open the graphical System Information utility."),
        new("resmon.exe", "Resource Monitor", "Open Resource Monitor for CPU, memory, disk, and network activity."),
        new("perfmon.exe", "Performance Monitor", "Open Performance Monitor for counters and data collector sets."),
        new("cleanmgr.exe", "Disk Cleanup", "Free disk space by removing temporary and unnecessary files."),
        new("mstsc.exe", "Remote Desktop Connection", "Connect to another computer using Remote Desktop."),
        new("mrt.exe", "Malicious Software Removal Tool", "Run Microsoft's malware removal scanner."),
        new("dxdiag.exe", "DirectX Diagnostic Tool", "Show DirectX, display, sound, and input diagnostics."),
        new("regedit.exe", "Registry Editor", "Open the graphical Windows Registry Editor."),
        new("cmd.exe", "Command Prompt", "Open the classic Windows command interpreter."),
        new("powershell.exe", "Windows PowerShell", "Open Windows PowerShell for scripting and automation."),
        new("wt.exe", "Windows Terminal", "Open Windows Terminal."),
    ];

    public string Source => "systemtool";

    public bool IsSupported => OperatingSystem.IsWindows() && Directory.Exists(SystemDirectory);

    public async IAsyncEnumerable<Entity> CollectAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        if (!IsSupported)
            yield break;

        foreach (var tool in Tools)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveToolPath(tool.FileName);
            if (path is null)
                continue;

            var entity = new Entity
            {
                Id = EntityId.Create(Source, path),
                Kind = EntityKind.SystemTool,
                DisplayName = tool.DisplayName,
                LaunchKind = LaunchKind.Executable,
                LaunchTarget = path,
                IconSource = path,
                Source = Source,
                RawMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["description"] = tool.Description,
                    ["fileName"] = tool.FileName,
                },
            };

            yield return CollectorEntity.WithContentHash(entity);
        }
    }

    private static string? ResolveToolPath(string fileName)
    {
        var systemPath = Path.Combine(SystemDirectory, fileName);
        if (File.Exists(systemPath))
            return systemPath;

        if (fileName.Equals("wt.exe", StringComparison.OrdinalIgnoreCase))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                var windowsAppsPath = Path.Combine(localAppData, "Microsoft", "WindowsApps", fileName);
                if (File.Exists(windowsAppsPath))
                    return windowsAppsPath;
            }
        }

        return null;
    }

    private static string SystemDirectory => Environment.GetFolderPath(Environment.SpecialFolder.System);

    private sealed record SystemTool(string FileName, string DisplayName, string Description);
}
