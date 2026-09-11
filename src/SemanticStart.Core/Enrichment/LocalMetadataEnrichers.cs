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
            var result = await ProcessRunner.RunAsync(target.Path, target.Arguments, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            var text = RepairWideOutput(result.Output).Trim();
            if (text.Length > 4096) text = text[..4096];
            if (!LooksLikeHelp(text, result.ExitCode)) return [];
            return [new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = false, Text = text, SourceUri = target.Path }];
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
    /// Recovers text from a tool that wrote UTF-16 to a redirected pipe.
    ///
    /// Console output is handed over already decoded, using one encoding for every tool, so a tool
    /// that emits UTF-16 arrives as its real characters interleaved with NULs. That wreckage still
    /// cleared the length test, and then normalization stripped it back to nothing, so six
    /// Sysinternals tools - procdump, sdelete, sigcheck, Sysmon, Coreinfo and psping - stored an
    /// empty document while genuinely having pages of help to give.
    ///
    /// Dropping the NULs recovers the text, because these tools write ASCII and the high byte of
    /// every character is therefore zero. The density test is what keeps this from corrupting
    /// anything else: correctly decoded output contains no NULs at all, so it is returned
    /// untouched, and only output that is at least a quarter NUL is treated as mis-decoded.
    /// </summary>
    internal static string RepairWideOutput(string text)
    {
        var nuls = 0;
        foreach (var ch in text)
            if (ch == '\0') nuls++;

        if (nuls == 0 || nuls * 4 < text.Length) return text;

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
    /// and with which switch.
    ///
    /// Two populations qualify. The Windows tools in System32 are named explicitly because their
    /// help switches vary and a wrong guess on some of them does real work rather than printing
    /// usage. Everything else must prove it is a console program by its PE subsystem, which is
    /// what makes the capability general: it is how accesschk and pssuspend get documentation on a
    /// machine where they are installed, without anyone having written their names down here.
    /// </summary>
    private static HelpTarget? ResolveTarget(Entity entity)
    {
        var system32 = ResolveSafeSystem32Path(entity);
        if (entity.Kind == EntityKind.SystemTool && system32 is not null && Arguments.TryGetValue(Path.GetFileName(system32), out var args))
            return new HelpTarget(system32, args);

        if (entity.Kind != EntityKind.SystemTool || !entity.RawMetadata.ContainsKey("consoleSubsystem"))
            return null;

        var path = entity.RawMetadata.GetValueOrDefault("targetPath");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        // "-?" is the convention these tools share, and unlike a bare invocation it cannot be
        // mistaken for a request to act. Tools that do not recognise it print usage anyway, which
        // is the text we wanted.
        return new HelpTarget(path, ["-?"]);
    }

    private sealed record HelpTarget(string Path, string[] Arguments);
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
