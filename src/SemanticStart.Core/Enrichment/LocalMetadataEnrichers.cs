using System.Diagnostics;
using System.Runtime.InteropServices;
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
            foreach (var file in EnumerateCandidates(dir, OwnerStem(entity)).Take(8))
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

    /// <summary>
    /// The executable stem this entity is, used to tell its own documentation from a neighbour's.
    /// </summary>
    private static string? OwnerStem(Entity entity)
    {
        foreach (var candidate in new[] { entity.RawMetadata.GetValueOrDefault("targetPath"), entity.LaunchTarget })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileNameWithoutExtension(candidate);
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidates(string root, string? ownerStem)
    {
        var dirs = new Queue<(string Path, int Depth)>();
        dirs.Enqueue((root, 0));
        while (dirs.Count > 0)
        {
            var (dir, depth) = dirs.Dequeue();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { continue; }

            // A directory can hold many programs, and then a document named after one of them is
            // evidence about that one alone. The Sysinternals suite installs seventy-odd tools
            // side by side with a handful of help files, and without this every one of those tools
            // was documented as having AdExplorer's, ADInsight's and Dbgview's help available.
            var neighbours = files
                .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileNameWithoutExtension)
                .Where(s => !string.IsNullOrEmpty(s) && !string.Equals(s, ownerStem, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);

                if (neighbours.Contains(Path.GetFileNameWithoutExtension(name)))
                    continue;

                // Legal and changelog files are never descriptions. Visual Studio Code was
                // summarised as "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY
                // KIND..." because its LICENSE sorted ahead of the winget description, which left
                // nothing in its embedded text saying it is a code editor.
                if (IsLegalOrChangelog(name))
                    continue;

                if (name.StartsWith("README", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("HELP", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("DOC", StringComparison.OrdinalIgnoreCase)
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

    private static bool IsLegalOrChangelog(string name)
        => LegalPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] LegalPrefixes =
    [
        "LICENSE", "LICENCE", "COPYING", "NOTICE", "THIRD-PARTY", "THIRDPARTY", "EULA",
        "CHANGELOG", "CHANGES", "RELEASE_NOTES", "RELEASENOTES", "CONTRIBUTING", "CODE_OF_CONDUCT",
        "SECURITY", "AUTHORS", "PATENTS"
    ];

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
    /// <summary>Characters of output a tool must produce before its reply counts as documentation.</summary>
    private const int SucceededMinimum = 120;

    /// <summary>
    /// The same bar for a tool that exited non-zero. Set from measurement: on a real machine every
    /// short non-zero reply was a complaint ("idna" 123 chars, "py.test" 160, "httpx" 167) while
    /// the substantial ones were genuine usage screens that merely exit non-zero out of habit.
    /// </summary>
    private const int FailedMinimum = 300;

    public string Provider => "cli-help";    public bool RequiresNetwork => false;
    public bool CanEnrich(Entity entity) => ResolveTarget(entity) is not null;

    public async Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(entity);
        if (target is null) return [];
        try
        {
            // A tool that answers one switch with usage and exits non-zero, and another with the
            // same usage and exits cleanly, is telling us which switch it actually meant. gh is
            // the clear case: "-?" yields 411 characters led by "unknown shorthand flag" and exits
            // 1, while "-h" yields 2714 characters of real help and exits 0. So a clean exit ends
            // the search immediately and a failing one is only held as a fallback.
            EnrichmentDocument? fallback = null;

            foreach (var switches in target.Candidates)
            {
                var result = await ProcessRunner.RunAsync(target.Path, switches, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                var text = RepairWideOutput(result.Output).Trim();
                if (text.Length > 4096) text = text[..4096];
                if (!LooksLikeHelp(text, result.ExitCode)) continue;

                var document = new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = false, Text = text, SourceUri = target.Path };
                if (result.ExitCode == 0) return [document];
                fallback ??= document;
            }

            return fallback is null ? [] : [fallback];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return []; }
    }

    /// <summary>
    /// Whether the captured output is documentation rather than a complaint about the switch.
    ///
    /// A tool that does not recognise "-?" answers on one short line - node.exe replies
    /// "node.exe: bad option: -?" - and storing that was worse than storing nothing. It is indexed
    /// as though it described the tool, so it both fails to document it and spends the tool's one
    /// piece of evidence on the words "bad option" and a copy of its own path. Across a whole
    /// source of console tools that is enough common text to measurably dilute the lexical arm.
    ///
    /// The test is structural rather than a list of error phrases, because the phrasing is the
    /// tool author's choice and is localized, while the shape is not: usage text is many lines and
    /// hundreds of characters, and a rejection is one line and a few dozen.
    ///
    /// The exit code raises that bar rather than deciding it. A non-zero exit is weak evidence of
    /// a rejection and cannot be an outright veto: measured across the console tools on a real
    /// machine, 81 of the 112 that produce usable help exit non-zero after printing it - the whole
    /// Sysinternals suite exits -1, robocopy exits 16, sc exits 1639, and ipconfig, netstat,
    /// route, cipher and shutdown all exit 1. Rejecting those would discard most of what this
    /// enricher exists to collect. What the exit code does predict is that a *short* reply is a
    /// complaint rather than terse documentation, so a failing tool has to produce substantially
    /// more text to be believed.
    ///
    /// Length alone cannot catch every failure, because some tools answer "-?" by crashing, and a
    /// crash dump is long and multi-line. Those are rejected by recognising the dump format, which
    /// is not the same as guessing at error phrasing: a Python traceback header and a .NET
    /// unhandled-exception banner are emitted verbatim by the runtime, are never localized, and
    /// are chosen by nobody. The pip-installed console shims on PATH are the population that
    /// matters here - asking one for help raises KeyError and stores a stack trace full of
    /// interpreter paths, which then matches queries it has nothing to do with.
    /// </summary>
    internal static bool LooksLikeHelp(string text, int exitCode)
    {
        if (text.Length < (exitCode == 0 ? SucceededMinimum : FailedMinimum)) return false;
        if (IsCrashOutput(text)) return false;

        var lines = 0;
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            if (!line.IsWhiteSpace() && ++lines >= 2)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Removes NUL characters from captured console output.
    ///
    /// Console output is handed over already decoded, using one encoding for every tool, so a tool
    /// that emits UTF-16 arrives as its real characters interleaved with NULs. That wreckage still
    /// cleared the length test, and then storage truncated at the first NUL, so several
    /// Sysinternals tools stored an empty document while genuinely having pages of help to give.
    ///
    /// Dropping the NULs recovers the text, because these tools write ASCII and the high byte of
    /// every character is therefore zero. This deliberately does not test how dense the NULs are:
    /// Sysmon pads only part of its output and came out 13% NUL, under a threshold set for fully
    /// interleaved text, so it kept its NULs and stored nothing. There is no case where a NUL in
    /// console output carries meaning, so the safe rule is to drop every one of them.
    /// </summary>
    internal static string RepairWideOutput(string text)
    {
        var nuls = 0;
        foreach (var ch in text)
            if (ch == '\0') nuls++;

        if (nuls == 0) return text;

        var buffer = new char[text.Length - nuls];
        var next = 0;
        foreach (var ch in text)
            if (ch != '\0') buffer[next++] = ch;

        return new string(buffer);
    }

    /// <summary>
    /// Whether the output is a runtime crash dump rather than anything the tool meant to say.    /// These markers are runtime-generated and unlocalized, so matching them is format detection
    /// rather than a guess at how an author worded an error.
    /// </summary>
    private static bool IsCrashOutput(string text) =>
        text.Contains("Traceback (most recent call last)", StringComparison.Ordinal)
        || text.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decides whether this entity is a command-line tool that can be asked to describe itself,
    /// and with which switches.
    ///
    /// Two populations qualify. The Windows tools in System32 are named explicitly because their
    /// help switches vary and a wrong guess on some of them does real work rather than printing
    /// usage. Everything else must prove it is a console program by its PE subsystem, which is
    /// what makes the capability general: it is how accesschk and pssuspend get documentation on a
    /// machine where they are installed, without anyone having written their names down here.
    ///
    /// Those get a ladder of switches rather than one, because there is no single convention and
    /// assuming one silently lost tools that document themselves perfectly well under a different
    /// spelling. The first reply that looks like help wins, so the common case still costs a
    /// single run.
    ///
    /// "--help" and "-help" are deliberately absent. The Git family treats "--help" as a request
    /// to *open the documentation*, so probing git-lfs, scalar, gitk or git-upload-pack with it
    /// launches a web browser on the user's desktop. A help probe must never perform an action,
    /// and a switch whose meaning is "show the user something" cannot be made safe by inspecting
    /// what it returned, because the damage is done by then. The remaining three are inert: "-?"
    /// and "--?" are requests for usage everywhere they are understood and an unknown-option
    /// error everywhere else, and "-h" is the short form that tools implement in-process.
    /// </summary>
    internal static readonly string[][] HelpSwitchLadder =
        [["-?"], ["--?"], ["-h"]];

    private static HelpTarget? ResolveTarget(Entity entity)
    {
        var system32 = ResolveSafeSystem32Path(entity);
        if (entity.Kind == EntityKind.SystemTool && system32 is not null && Arguments.TryGetValue(Path.GetFileName(system32), out var args))
            return new HelpTarget(system32, [args]);

        if (entity.Kind != EntityKind.SystemTool || !entity.RawMetadata.ContainsKey("consoleSubsystem"))
            return null;

        var path = entity.RawMetadata.GetValueOrDefault("targetPath");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        return new HelpTarget(path, HelpSwitchLadder);
    }

    private sealed record HelpTarget(string Path, IReadOnlyList<string[]> Candidates);
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
    /// <summary>
    /// Hard cap on captured output. A help screen is a few kilobytes; anything past this is a tool
    /// that ignored its arguments and started doing work, and none of it is documentation.
    /// </summary>
    private const int MaxCapturedCharacters = 64 * 1024;

    public static async Task<(int ExitCode, string Output)> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);

        // Standard output and standard error are delivered on two different threadpool threads, so
        // the buffer they share has to be locked. Without this the two racing appends corrupt the
        // builder's internal length and it throws "Destination is too short" from inside the event
        // handler - on a threadpool thread, where there is nobody to catch it, so the whole index
        // build dies partway through. It only shows up on tools that write to both streams at
        // once, which made it look like an occasional bad tool rather than a bug here.
        var output = new StringBuilder();
        var gate = new object();

        void Capture(string? line)
        {
            if (line is null)
                return;

            lock (gate)
            {
                if (output.Length >= MaxCapturedCharacters)
                    return;

                output.AppendLine(line);
            }
        }

        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);
        process.Start();

        // Contain the probe and everything it starts.
        //
        // Some console executables are only launcher stubs: gitk.exe parses nothing, starts the
        // Tcl/Tk interface as a separate process and exits immediately, so by the time the timeout
        // fires there is nothing left to kill and the window stays on the user's desktop. Killing
        // the process tree cannot help, because the survivor is no longer in the tree - its parent
        // is already gone. A job object is held by the children rather than by the parent, so
        // closing it takes the orphans with it.
        using var job = ProcessJob.TryContain(process);

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var waitTask = process.WaitForExitAsync(cancellationToken);
        var finished = await Task.WhenAny(waitTask, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false) == waitTask;
        if (!finished)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            lock (gate) return (-1, output.ToString());
        }
        await waitTask.ConfigureAwait(false);
        lock (gate) return (process.ExitCode, output.ToString());
    }
}

/// <summary>
/// A Windows job object that kills everything still inside it when it is disposed.
///
/// This exists because asking an unknown executable to describe itself can start something that
/// outlives the answer. It is best-effort by design: if the job cannot be created or the process
/// cannot be assigned to it, the probe still runs, because losing documentation for every console
/// tool would be a worse outcome than the occasional stray window this is meant to prevent.
/// </summary>
internal sealed class ProcessJob : IDisposable
{
    private const int ExtendedLimitInformation = 9;
    private const int KillOnJobClose = 0x2000;

    private IntPtr handle;

    private ProcessJob(IntPtr handle) => this.handle = handle;

    public static ProcessJob? TryContain(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return null;

        var limits = new ExtendedLimit();
        limits.BasicLimitInformation.LimitFlags = KillOnJobClose;

        var size = Marshal.SizeOf<ExtendedLimit>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (!SetInformationJobObject(job, ExtendedLimitInformation, buffer, (uint)size))
            {
                CloseHandle(job);
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        try
        {
            if (!AssignProcessToJobObject(job, process.Handle))
            {
                CloseHandle(job);
                return null;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // The process finished before it could be assigned, which is the common case for a
            // tool that simply printed its usage. There is nothing left to contain.
            CloseHandle(job);
            return null;
        }

        return new ProcessJob(job);
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        CloseHandle(handle);
        handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public IntPtr MinimumWorkingSetSize;
        public IntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public IntPtr ProcessMemoryLimit;
        public IntPtr JobMemoryLimit;
        public IntPtr PeakProcessMemoryUsed;
        public IntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
