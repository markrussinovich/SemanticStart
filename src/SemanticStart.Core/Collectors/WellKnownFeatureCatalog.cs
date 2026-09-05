using Microsoft.Win32;

namespace SemanticStart.Core.Collectors;

/// <summary>
/// Curated catalog of the Windows optional features users actually look for.
///
/// This exists because the complete feature list is only enumerable through DISM, which requires
/// elevation, and SemanticStart is a non-elevated user-level utility. The registry fallback that
/// works without admin (the CBS CapabilityIndex) covers barely 58 capabilities and omits almost
/// everything interesting: WSL, Sandbox, Hyper-V, .NET 3.5, IIS.
///
/// The servicing package list under CBS *is* readable without admin, but it holds thousands of
/// internal component packages that would swamp the index with jargon. Pairing a curated catalog
/// with a presence check against that list gives clean, searchable names and useful descriptions
/// without either elevation or noise.
/// </summary>
internal static class WellKnownFeatureCatalog
{
    private const string PackagesPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages";

    internal sealed record FeatureDefinition(
        string PackagePrefix,
        string DisplayName,
        string Description);

    /// <summary>
    /// Descriptions are written in the vocabulary a user would search with, not in Microsoft's
    /// packaging vocabulary, because this text is what gets embedded.
    /// </summary>
    internal static IReadOnlyList<FeatureDefinition> All { get; } =
    [
        new("Microsoft-Windows-Subsystem-Linux", "Windows Subsystem for Linux (WSL)",
            "Run Linux distributions and Linux command line tools natively on Windows. Install Ubuntu, Debian, or other Linux distros without a virtual machine or dual boot."),
        new("VirtualMachinePlatform", "Virtual Machine Platform",
            "Platform support required to run virtual machines, needed by WSL 2 and Windows Sandbox."),
        new("Containers-DisposableClientVM", "Windows Sandbox",
            "Run untrusted applications in a lightweight isolated desktop environment that is discarded when closed. Safely test suspicious software or files."),
        new("Microsoft-Hyper-V", "Hyper-V",
            "Create and run virtual machines on Windows. Full hypervisor for running other operating systems in a VM."),
        new("HypervisorPlatform", "Windows Hypervisor Platform",
            "Lets third party virtualization software such as VMware, VirtualBox, or Android emulators run alongside Hyper-V."),
        new("Containers", "Containers",
            "Support for running Windows and Docker containers."),
        new("NetFx3", ".NET Framework 3.5",
            "Legacy .NET runtime required by many older desktop applications, including .NET 2.0 and 3.0 programs."),
        new("Microsoft-Windows-NetFx4", ".NET Framework 4 Advanced Services",
            "Advanced .NET Framework 4 services including WCF and ASP.NET support."),
        new("Microsoft-Windows-IIS-WebServer", "Internet Information Services (IIS)",
            "Host websites and web applications locally. Microsoft's built-in web server."),
        new("TelnetClient", "Telnet Client",
            "Connect to remote servers over the telnet protocol from the command line. Useful for testing whether a network port is open."),
        new("TFTP", "TFTP Client",
            "Transfer files using the Trivial File Transfer Protocol, often used for network device firmware."),
        new("SMB1Protocol", "SMB 1.0/CIFS File Sharing Support",
            "Legacy file sharing protocol needed to reach very old network drives and NAS devices. Insecure and disabled by default."),
        new("OpenSSH-Client", "OpenSSH Client",
            "Connect to remote machines securely over SSH from the command line."),
        new("OpenSSH-Server", "OpenSSH Server",
            "Accept incoming SSH connections so you can remotely log in to this PC."),
        new("Printing-PrintToPDFServices", "Microsoft Print to PDF",
            "Print any document to a PDF file instead of to a physical printer."),
        new("Printing-XPSServices", "XPS Services",
            "Support for creating and viewing XPS documents."),
        new("WorkFolders-Client", "Work Folders",
            "Sync work files from a corporate file server to this PC."),
        new("MSRDC-Infrastructure", "Remote Differential Compression",
            "Efficiently synchronize files over a network by transferring only the changed parts."),
        new("Windows-Defender-ApplicationGuard", "Microsoft Defender Application Guard",
            "Open untrusted websites and documents inside an isolated container to protect the PC from malware."),
        new("LegacyComponents", "Legacy Components",
            "Older compatibility components such as DirectPlay, required by some vintage games."),
        new("DirectPlay", "DirectPlay",
            "Legacy DirectX networking component required by some older games."),
        new("MediaPlayback", "Media Features",
            "Core audio and video playback support, including Windows Media Player."),
        new("WindowsMediaPlayer", "Windows Media Player Legacy",
            "The classic Windows Media Player desktop application for playing music and video."),
        new("SimpleTCP", "Simple TCPIP Services",
            "Legacy TCP/IP utilities such as echo, daytime, and quote of the day."),
        new("Microsoft-Windows-Client-EmbeddedExp", "Custom Logon and Shell Launcher",
            "Kiosk and embedded experience features such as Shell Launcher and Unbranded Boot."),
        new("Client-ProjFS", "Windows Projected File System",
            "Virtual file system support used by developer tools such as Git VFS."),
        new("Microsoft-Windows-Wifi-Direct", "Wi-Fi Direct Services",
            "Connect directly to nearby devices over Wi-Fi without a router, used for wireless printing and screen projection."),
        new("SearchEngine-Client", "Windows Search",
            "Background indexing that powers file and content search across the PC."),
        new("HostGuardian", "Host Guardian Hyper-V Support",
            "Attestation support for running shielded virtual machines."),
        new("MultiPoint", "MultiPoint Connector",
            "Allows this PC to be monitored and managed by MultiPoint Manager and Dashboard."),
    ];

    /// <summary>
    /// Reads the CBS servicing package list once and reports which catalog entries are present on
    /// the system image, along with a best-effort install state.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ResolveStates(CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var packages = Registry.LocalMachine.OpenSubKey(PackagesPath);
            if (packages is null)
                return states;

            foreach (var packageKeyName in packages.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Package keys are "<Name>~<publisher>~<arch>~~<version>"; only the name matters.
                var tilde = packageKeyName.IndexOf('~');
                var name = tilde > 0 ? packageKeyName[..tilde] : packageKeyName;

                foreach (var definition in All)
                {
                    if (!name.StartsWith(definition.PackagePrefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Installed wins over any other state seen for the same feature, because a
                    // feature spans several packages and only some of them are ever applied.
                    if (states.TryGetValue(definition.PackagePrefix, out var existing)
                        && existing == "Installed")
                    {
                        continue;
                    }

                    states[definition.PackagePrefix] = ReadState(packages, packageKeyName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            return states;
        }

        return states;
    }

    /// <summary>CBS records install state as a numeric CurrentState; 112 (0x70) means installed.</summary>
    private static string ReadState(RegistryKey packages, string packageKeyName)
    {
        try
        {
            using var package = packages.OpenSubKey(packageKeyName);
            if (package?.GetValue("CurrentState") is int state)
            {
                return state switch
                {
                    112 => "Installed",
                    64 or 0 => "NotPresent",
                    _ => "Available",
                };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
        }

        return "Available";
    }
}
