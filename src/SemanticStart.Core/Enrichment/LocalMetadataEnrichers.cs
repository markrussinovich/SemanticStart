using System.Diagnostics;
using System.Text;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Enrichment;

public sealed class ShortcutMetadataEnricher : IEnricher
{
    public string Provider => "shortcut";
    public bool RequiresNetwork => false;
    public bool CanEnrich(Entity entity) => entity.RawMetadata.ContainsKey("comment") || entity.RawMetadata.ContainsKey("startMenuFolder");

    public Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lines = new List<string>();
        if (entity.RawMetadata.TryGetValue("comment", out var comment) && !string.IsNullOrWhiteSpace(comment)) lines.Add(comment.Trim());
        if (entity.RawMetadata.TryGetValue("startMenuFolder", out var folder) && !string.IsNullOrWhiteSpace(folder))
        {
            var parts = folder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0) lines.Add($"Categorized under {string.Join(", ", parts)} in the Start Menu.");
        }
        return Task.FromResult<IReadOnlyList<EnrichmentDocument>>(lines.Count == 0 ? [] : [new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = false, Text = string.Join(Environment.NewLine, lines) }]);
    }
}

public sealed class CuratedWindowsIntentEnricher : IEnricher
{
    private static readonly IReadOnlyDictionary<string, IntentEntry> ByLaunchTarget = new Dictionary<string, IntentEntry>(StringComparer.OrdinalIgnoreCase)
    {
        ["ms-settings:display"] = new("Change screen brightness, monitor layout, resolution, scale, HDR, refresh rate, and display orientation.", ["make things bigger on screen", "change screen resolution", "fix blurry text", "arrange multiple monitors", "adjust brightness"], ["screen", "monitor", "resolution", "scaling", "text size"]),
        ["ms-settings:display-advanced"] = new("Review advanced display information, refresh rate, adapter properties, and monitor capabilities.", ["change refresh rate", "view monitor details", "fix display adapter problems"], ["advanced display", "refresh rate", "graphics adapter"]),
        ["ms-settings:easeofaccess-display"] = new("Increase text size and make Windows easier to see without changing app layouts.", ["make text bigger", "increase font size", "make Windows easier to read", "improve readability"], ["text size", "font size", "accessibility display", "make text larger"]),
        ["ms-settings:easeofaccess-magnifier"] = new("Zoom the screen with Magnifier and configure reading, tracking, and zoom behavior.", ["zoom in on the screen", "magnify text", "make small things visible"], ["screen magnifier", "zoom", "accessibility zoom"]),
        ["ms-settings:sound"] = new("Manage system volume, speakers, headphones, microphones, input devices, output devices, and sound troubleshooting.", ["choose default microphone", "change speaker output", "fix no sound", "test microphone", "adjust input volume"], ["audio", "volume", "speakers", "headphones", "microphone", "input device", "output device"]),
        ["ms-settings:sound-devices"] = new("Manage sound input and output devices including speakers, headsets, microphones, and default audio endpoints.", ["set the default microphone", "pick a headset microphone", "disable an audio device", "rename speakers", "test sound devices"], ["audio devices", "recording devices", "playback devices", "default microphone"]),
        ["ms-settings:apps-volume"] = new("Set per-app volume, output speakers, and input microphone devices in the Windows volume mixer.", ["change one app's volume", "route an app to headphones", "select a microphone for an app"], ["volume mixer", "app volume", "audio routing"]),
        ["ms-settings:privacy-microphone"] = new("Control which apps and Windows features are allowed to use the microphone.", ["allow microphone access", "fix microphone permission", "stop apps from listening", "enable microphone for Teams or Zoom"], ["mic privacy", "microphone permissions", "recording privacy"]),
        ["ms-settings:powersleep"] = new("Manage power mode, battery usage, energy recommendations, sleep, screen timeout, lid close actions, and power saving settings.", ["find why battery is draining", "make battery last longer", "change sleep timeout", "change what happens when I close the lid", "reduce power use", "view battery usage by app"], ["battery", "power", "energy", "sleep", "power mode", "lid close action"]),
        ["ms-settings:batterysaver"] = new("Configure battery saver to reduce background activity and extend battery life.", ["save battery", "turn on battery saver", "reduce background battery drain"], ["battery saver", "low power mode", "power saving"]),
        ["ms-settings:storagesense"] = new("View disk usage and manage storage consumed by apps, temporary files, documents, and drives.", ["free up disk space", "find what is using storage", "delete temporary files", "manage drive space"], ["storage", "disk space", "drive usage"]),
        ["ms-settings:storagerecommendations"] = new("Find Windows cleanup recommendations for temporary files, large files, unused apps, and synced cloud content.", ["clean up disk space", "remove large unused files", "delete temporary files"], ["cleanup recommendations", "disk cleanup", "temporary files"]),
        ["ms-settings:printers"] = new("Add, remove, troubleshoot, and configure printers, scanners, print queues, and default printer behavior.", ["add a printer", "set default printer", "fix printing", "clear print queue", "scan a document"], ["printers", "scanners", "print queue", "default printer"]),
        ["ms-settings:network-status"] = new("View network and internet connection status, adapter properties, data usage, and troubleshooting entry points.", ["why is my internet not working", "fix internet connection", "troubleshoot network", "see if Wi-Fi is connected", "reset network adapter"], ["internet", "network", "connection", "adapter", "network and internet"]),
        ["ms-settings:network-wifi"] = new("Manage Wi-Fi adapters, available wireless networks, known networks, and Wi-Fi properties.", ["connect to Wi-Fi", "forget a wireless network", "fix wireless internet", "view Wi-Fi password or properties"], ["wifi", "wireless", "internet"]),
        ["ms-settings:network-proxy"] = new("Configure automatic proxy discovery, setup scripts, and manual proxy servers for internet access.", ["set a proxy", "fix corporate internet proxy", "configure proxy server"], ["proxy", "proxy server", "internet proxy"]),
        ["ms-settings:defaultapps"] = new("Choose default apps for web browsing, email, music, photos, video, file types, and URL protocols.", ["change default browser", "open links in a different app", "change file association", "set default email app"], ["default programs", "file associations", "default browser"]),
        ["ms-settings:startupapps"] = new("Choose which apps and programs start automatically when signing in to Windows.", ["stop programs starting when I boot", "disable startup apps", "make login faster", "prevent an app from launching at startup"], ["startup apps", "startup programs", "login items"]),
        ["ms-settings:remotedesktop"] = new("Enable and configure Remote Desktop so another computer can connect to this PC remotely.", ["connect to another computer remotely", "allow remote desktop connections", "remote into this PC", "configure RDP access"], ["remote desktop", "rdp", "mstsc", "remote access"]),
        ["ms-settings:deviceencryption"] = new("View and manage device encryption to protect data on the system drive.", ["encrypt my hard drive", "turn on device encryption", "protect files if the PC is stolen"], ["device encryption", "drive encryption", "BitLocker"]),
        ["ms-settings:privacy-deviceencryption"] = new("Review BitLocker and device encryption protection for Windows drives.", ["encrypt my hard drive", "manage BitLocker", "turn on drive encryption", "protect a laptop drive"], ["BitLocker", "device encryption", "drive encryption"]),
        ["ms-settings:windowsupdate"] = new("Check for Windows updates and manage update status, pauses, restart requirements, and update settings.", ["install updates", "check for updates", "pause updates", "fix Windows Update"], ["updates", "patches", "windows update"]),
        ["ms-settings:troubleshoot-other"] = new("Run Windows troubleshooters for network, audio, printers, Windows Update, Bluetooth, camera, and other common problems.", ["fix sound problems", "fix printer problems", "run a troubleshooter", "repair network problems"], ["troubleshooters", "fix problems", "diagnostics"]),
    };

    private static readonly IReadOnlyDictionary<string, IntentEntry> ByFileName = new Dictionary<string, IntentEntry>(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge.exe"] = new("Browse websites, search the web, use web apps, manage tabs, downloads, favorites, and browser privacy.", ["search the web", "open a website", "browse the internet", "change browser settings", "download a file"], ["browser", "web browser", "internet", "edge"]),
        ["chrome.exe"] = new("Browse websites, search the web, use web apps, manage tabs, downloads, bookmarks, extensions, and browser privacy.", ["search the web", "open a website", "browse the internet", "manage browser extensions", "download a file"], ["browser", "web browser", "internet", "google chrome", "chrome"]),
        ["firefox.exe"] = new("Browse websites, search the web, use web apps, manage tabs, downloads, bookmarks, extensions, and browser privacy.", ["search the web", "open a website", "browse the internet", "manage browser add-ons", "download a file"], ["browser", "web browser", "internet", "mozilla firefox", "firefox"]),
        ["winword.exe"] = new("Create, write, edit, format, review, and print word-processing documents.", ["write a document", "create a report", "edit a Word file", "format a letter", "review a document"], ["word processor", "word document", "docx", "Microsoft Word", "Office Word"]),
        ["excel.exe"] = new("Create, edit, analyze, chart, and format spreadsheets, workbooks, tables, formulas, and data.", ["edit a spreadsheet", "create a workbook", "analyze data", "make a chart", "work with formulas"], ["spreadsheet", "workbook", "xlsx", "Microsoft Excel", "Office Excel"]),
        ["powerpnt.exe"] = new("Create, edit, present, and share slide presentations, slideshows, and slide decks.", ["create a presentation", "make slides", "build a slide deck", "present a slideshow", "edit PowerPoint slides"], ["presentation", "slides", "slide deck", "slideshow", "pptx", "Microsoft PowerPoint", "Office PowerPoint"]),
        ["outlook.exe"] = new("Read, send, organize, and search email, calendars, meetings, contacts, and tasks.", ["write an email", "schedule a meeting", "check my calendar", "search mail", "manage contacts"], ["email", "mail", "calendar", "Microsoft Outlook", "Office Outlook"]),
        ["onenote.exe"] = new("Capture, organize, sync, and search notes, notebooks, pages, drawings, images, and meeting notes.", ["take notes", "organize a notebook", "write meeting notes", "clip research", "sync notes"], ["notes", "notebook", "Microsoft OneNote", "Office OneNote"]),
        ["teams.exe"] = new("Chat, meet, call, collaborate, share files, and join video meetings with Microsoft Teams.", ["join a meeting", "chat with coworkers", "start a video call", "share my screen", "collaborate with a team"], ["chat", "meetings", "video call", "Microsoft Teams", "Teams"]),
        ["onedrive.exe"] = new("Sync, back up, share, and access cloud files and folders with OneDrive.", ["sync my files", "back up desktop documents and pictures", "share a cloud file", "access OneDrive folders"], ["cloud storage", "file sync", "Microsoft OneDrive", "OneDrive"]),
        ["mmsys.cpl"] = ByLaunchTarget["ms-settings:sound"],
        ["powercfg.cpl"] = ByLaunchTarget["ms-settings:powersleep"],
        ["desk.cpl"] = ByLaunchTarget["ms-settings:display"],
        ["ncpa.cpl"] = new("Open classic Network Connections to enable, disable, rename, and configure network adapters.", ["why is my internet not working", "change adapter options", "disable a network adapter", "set IP address", "configure DNS"], ["network adapters", "adapter options", "connections", "Network Connections"]),
        ["inetcpl.cpl"] = new("Configure legacy Internet Options including browser security zones, certificates, proxy, privacy, and connection settings.", ["change internet options", "configure proxy", "manage browser certificates", "clear browsing settings"], ["internet options", "internet properties", "security zones"]),
        ["devmgmt.msc"] = new("View hardware devices, update drivers, disable devices, scan for hardware changes, and troubleshoot device errors.", ["update a driver", "fix an unknown device", "disable hardware", "view device status"], ["device manager", "drivers", "hardware"]),
        ["diskmgmt.msc"] = new("Create, format, extend, shrink, and assign letters to disks, partitions, and volumes.", ["partition a drive", "format a disk", "change drive letter", "initialize a new disk"], ["disk management", "partitions", "volumes"]),
        ["taskmgr.exe"] = new("View and end running apps and processes, inspect startup apps, performance, users, services, and resource usage.", ["see what is slowing down my PC", "end a frozen app", "kill a process", "end a process", "force quit an application", "close a hung program", "disable startup apps", "check CPU or memory usage"], ["task manager", "processes", "startup apps", "taskmgr"]),
        ["services.msc"] = new("Start, stop, disable, and configure Windows services that run in the background.", ["stop a service", "disable background service", "restart Windows service", "change service startup type"], ["services", "background services", "service manager"]),
        ["eventvwr.msc"] = new("Review Windows event logs for application, security, setup, and system errors or warnings.", ["find crash logs", "view system errors", "troubleshoot event logs", "inspect audit events"], ["event viewer", "logs", "windows logs"]),
        ["mstsc.exe"] = new("Connect to another Windows computer using Remote Desktop Protocol.", ["connect to another computer remotely", "open an RDP session", "remote into a PC", "connect to a work computer"], ["remote desktop connection", "rdp", "mstsc"]),
        ["powercfg.exe"] = new("Manage power plans, battery reports, sleep states, hibernation, wake timers, lid close actions, and energy diagnostics from the command line.", ["find why battery is draining", "create a battery report", "see what woke my computer", "change what happens when I close the lid", "change power plan"], ["battery report", "energy report", "power configuration", "lid close action"]),
        ["cleanmgr.exe"] = new("Free disk space by removing temporary files, recycle bin contents, thumbnails, and old Windows cleanup files.", ["free up disk space", "delete temporary files", "clean the drive", "remove old Windows files"], ["disk cleanup", "cleanup", "temporary files"]),
    };

    public string Provider => "windows-intent-catalog";
    public bool RequiresNetwork => false;

    public bool CanEnrich(Entity entity)
        => TryResolve(entity) is not null;

    public Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = TryResolve(entity);
        if (entry is null)
            return Task.FromResult<IReadOnlyList<EnrichmentDocument>>([]);

        var text = $"Summary: {entry.Summary}{Environment.NewLine}Tasks: {string.Join("; ", entry.Tasks)}{Environment.NewLine}Synonyms: {string.Join("; ", entry.Synonyms)}";
        return Task.FromResult<IReadOnlyList<EnrichmentDocument>>(
            [new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = false, Text = text }]);
    }

    private static IntentEntry? TryResolve(Entity entity)
    {
        if (ByLaunchTarget.TryGetValue(entity.LaunchTarget, out var byTarget))
            return byTarget;

        var fileName = entity.RawMetadata.GetValueOrDefault("fileName") ?? Path.GetFileName(entity.LaunchTarget);
        if (!string.IsNullOrWhiteSpace(fileName) && ByFileName.TryGetValue(fileName, out var byFile))
            return byFile;

        var name = entity.DisplayName;
        if (name.Contains("Microsoft Edge", StringComparison.OrdinalIgnoreCase) || name.Equals("Edge", StringComparison.OrdinalIgnoreCase))
            return ByFileName["msedge.exe"];
        if (name.Equals("Task Manager", StringComparison.OrdinalIgnoreCase))
            return ByFileName["taskmgr.exe"];

        var normalized = NormalizeAppName(name);
        if (ByAppName.TryGetValue(normalized, out var byName))
            return byName;

        return null;
    }

    private static readonly IReadOnlyDictionary<string, IntentEntry> ByAppName = new Dictionary<string, IntentEntry>(StringComparer.OrdinalIgnoreCase)
    {
        ["edge"] = ByFileName["msedge.exe"],
        ["microsoft edge"] = ByFileName["msedge.exe"],
        ["chrome"] = ByFileName["chrome.exe"],
        ["google chrome"] = ByFileName["chrome.exe"],
        ["firefox"] = ByFileName["firefox.exe"],
        ["mozilla firefox"] = ByFileName["firefox.exe"],
        ["word"] = ByFileName["winword.exe"],
        ["microsoft word"] = ByFileName["winword.exe"],
        ["excel"] = ByFileName["excel.exe"],
        ["microsoft excel"] = ByFileName["excel.exe"],
        ["powerpoint"] = ByFileName["powerpnt.exe"],
        ["microsoft powerpoint"] = ByFileName["powerpnt.exe"],
        ["outlook"] = ByFileName["outlook.exe"],
        ["microsoft outlook"] = ByFileName["outlook.exe"],
        ["onenote"] = ByFileName["onenote.exe"],
        ["microsoft onenote"] = ByFileName["onenote.exe"],
        ["teams"] = ByFileName["teams.exe"],
        ["microsoft teams"] = ByFileName["teams.exe"],
        ["onedrive"] = ByFileName["onedrive.exe"],
        ["microsoft onedrive"] = ByFileName["onedrive.exe"],

    };

    private static string NormalizeAppName(string name)
        => name.Replace("®", string.Empty, StringComparison.Ordinal)
            .Replace("™", string.Empty, StringComparison.Ordinal)
            .Replace("(Preview)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();

    private sealed record IntentEntry(string Summary, string[] Tasks, string[] Synonyms);
}

public sealed class AdjacentDocsEnricher : IEnricher
{
    private const int MaxFileBytes = 256 * 1024;
    private const int MaxReadChars = 8 * 1024;
    public string Provider => "local-docs";
    public bool RequiresNetwork => false;
    public bool CanEnrich(Entity entity) => ResolveInstallDirectory(entity) is not null;

    public async Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        var dir = ResolveInstallDirectory(entity);
        if (dir is null) return [];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        var docs = new List<EnrichmentDocument>();
        try
        {
            foreach (var file in EnumerateCandidates(dir).Take(8))
            {
                cts.Token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (file.EndsWith(".chm", StringComparison.OrdinalIgnoreCase))
                {
                    docs.Add(new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = false, Text = $"Local help file available: {name}.", SourceUri = file });
                    continue;
                }
                var info = new FileInfo(file);
                if (!info.Exists || info.Length <= 0 || info.Length > MaxFileBytes) continue;
                var text = await File.ReadAllTextAsync(file, cts.Token).ConfigureAwait(false);
                text = file.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
                    ? EnrichmentTextNormalizer.ToPlainText(text)
                    : EnrichmentTextNormalizer.StripMarkdown(text);
                if (text.Length > MaxReadChars) text = text[..MaxReadChars];
                if (!string.IsNullOrWhiteSpace(text)) docs.Add(new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = false, Text = $"{name}: {text}", SourceUri = file });
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or NotSupportedException) { }
        return docs;
    }

    private static IEnumerable<string> EnumerateCandidates(string root)
    {
        var dirs = new Queue<(string Path, int Depth)>();
        dirs.Enqueue((root, 0));
        while (dirs.Count > 0)
        {
            var (dir, depth) = dirs.Dequeue();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { continue; }
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("README", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("HELP", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("DOC", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("LICENSE.", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    || (IsHtml(file) && (name.StartsWith("README", StringComparison.OrdinalIgnoreCase)
                                         || name.StartsWith("HELP", StringComparison.OrdinalIgnoreCase)
                                         || name.StartsWith("DOC", StringComparison.OrdinalIgnoreCase)
                                         || name.StartsWith("MANUAL", StringComparison.OrdinalIgnoreCase)))
                    || file.EndsWith(".chm", StringComparison.OrdinalIgnoreCase)) yield return file;
            }
            if (depth >= 1) continue;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { continue; }
            foreach (var child in children.Take(12)) dirs.Enqueue((child, depth + 1));
        }
    }

    private static bool IsHtml(string file)
        => file.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveInstallDirectory(Entity entity)
    {
        foreach (var candidate in new[] { entity.LaunchTarget, entity.IconSource, entity.RawMetadata.GetValueOrDefault("targetPath"), entity.RawMetadata.GetValueOrDefault("workingDirectory") })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                var path = Environment.ExpandEnvironmentVariables(candidate.Split(',')[0].Trim('"'));
                if (Directory.Exists(path)) return IsNoisyWindowsDirectory(path) ? null : path;
                if (File.Exists(path))
                {
                    var dir = Path.GetDirectoryName(path);
                    return dir is null || IsNoisyWindowsDirectory(dir) ? null : dir;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return null;
    }

    private static bool IsNoisyWindowsDirectory(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows)) return false;
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var system32 = Path.Combine(windows, "System32");
        var sysWow64 = Path.Combine(windows, "SysWOW64");
        return full.Equals(Path.GetFullPath(windows).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || full.Equals(Path.GetFullPath(system32).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || full.Equals(Path.GetFullPath(sysWow64).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class CliHelpEnricher : IEnricher
{
    private static readonly IReadOnlyDictionary<string, string[]> Arguments = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["ipconfig.exe"] = ["/?"], ["ping.exe"] = ["/?"], ["tracert.exe"] = ["/?"], ["netstat.exe"] = ["/?"], ["nslookup.exe"] = ["/?"], ["route.exe"] = ["/?"], ["arp.exe"] = ["/?"], ["netsh.exe"] = ["/?"], ["sfc.exe"] = ["/?"], ["dism.exe"] = ["/?"], ["chkdsk.exe"] = ["/?"], ["diskpart.exe"] = ["/?"], ["tasklist.exe"] = ["/?"], ["taskkill.exe"] = ["/?"], ["systeminfo.exe"] = ["/?"], ["whoami.exe"] = ["/?"], ["gpupdate.exe"] = ["/?"], ["gpresult.exe"] = ["/?"], ["robocopy.exe"] = ["/?"], ["xcopy.exe"] = ["/?"], ["schtasks.exe"] = ["/?"], ["sc.exe"] = ["/?"], ["reg.exe"] = ["/?"], ["bcdedit.exe"] = ["/?"], ["powercfg.exe"] = ["/?"], ["driverquery.exe"] = ["/?"], ["pathping.exe"] = ["/?"], ["certutil.exe"] = ["/?"], ["cipher.exe"] = ["/?"], ["compact.exe"] = ["/?"], ["fsutil.exe"] = ["/?"], ["icacls.exe"] = ["/?"], ["takeown.exe"] = ["/?"], ["shutdown.exe"] = ["/?"]
    };
    public string Provider => "cli-help";
    public bool RequiresNetwork => false;
    public bool CanEnrich(Entity entity)
    {
        var path = ResolveSafeSystem32Path(entity);
        return entity.Kind == EntityKind.SystemTool && path is not null && Arguments.ContainsKey(Path.GetFileName(path));
    }
    public async Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        var path = ResolveSafeSystem32Path(entity);
        if (path is null || !Arguments.TryGetValue(Path.GetFileName(path), out var args)) return [];
        try
        {
            var result = await ProcessRunner.RunAsync(path, args, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            var text = result.Output.Trim();
            if (text.Length > 4096) text = text[..4096];
            return string.IsNullOrWhiteSpace(text) ? [] : [new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = false, Text = text, SourceUri = path }];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return []; }
    }
    private static string? ResolveSafeSystem32Path(Entity entity)
    {
        try
        {
            var path = Environment.ExpandEnvironmentVariables(entity.LaunchTarget.Trim('"'));
            if (!File.Exists(path)) return null;
            var full = Path.GetFullPath(path);
            var system32 = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.System)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(system32, StringComparison.OrdinalIgnoreCase)) return null;
            return full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}

internal static class ProcessRunner
{
    public static async Task<(int ExitCode, string Output)> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var waitTask = process.WaitForExitAsync(cancellationToken);
        var finished = await Task.WhenAny(waitTask, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false) == waitTask;
        if (!finished)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (-1, output.ToString());
        }
        await waitTask.ConfigureAwait(false);
        return (process.ExitCode, output.ToString());
    }
}
