using System.Diagnostics;
using System.Runtime.CompilerServices;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Collectors;

/// <summary>
/// Collects the executables reachable by name from a command line, by walking the directories on
/// PATH.
///
/// This is the source for developer tooling, which nothing else finds. A tool installed by an
/// archive drop, a package manager, or a portable extraction registers no uninstall entry, writes
/// no Start Menu shortcut, and adds no App Paths alias - it only puts a directory on PATH. That
/// covers most of what is actually typed on a developer machine: ripgrep, ffmpeg, kubectl, the
/// contents of a Python Scripts directory. <see cref="CommandAliasCollector"/> reads Windows'
/// registry of invocable commands; this reads the other mechanism Windows offers for the same
/// thing, and the two barely overlap.
///
/// PATH is read from the Machine and User scopes rather than from this process. The process copy
/// is a snapshot taken at startup, so for a tray app that stays running for days it goes stale the
/// moment an installer appends a directory - the exact case where a rescan is wanted.
/// </summary>
public sealed class PathExecutableCollector : IEntityCollector
{
    /// <summary>
    /// Only .exe is collected. The extension is what carries a version resource, which is the
    /// entire basis for a display name and a description here, and it is also what
    /// <see cref="Indexing.IndexBuilder"/> recognises when deduplicating on the resolved binary.
    /// A .cmd or .bat shim - npm.cmd, and most of a Python Scripts directory - would index as a
    /// bare name with nothing to say about itself, which the ranker rejects from semantic results
    /// anyway. Its sibling .exe is usually present regardless.
    /// </summary>
    private const string ExecutablePattern = "*.exe";

    /// <summary>
    /// A PATH entry is a tool directory. One holding more executables than this is a system or
    /// install root that reached PATH by accident or by a very broad configuration, and walking it
    /// would bury the index. Skipping it wholesale is better than truncating it arbitrarily, since
    /// a partial directory is worse than an absent one: it looks complete and is not.
    /// </summary>
    private const int MaxExecutablesPerDirectory = 512;

    public string Source => "path";

    public bool IsSupported => OperatingSystem.IsWindows();

    private readonly Func<string> _readPath;

    public PathExecutableCollector()
        : this(ReadComposedPath)
    {
    }

    /// <summary>
    /// Seam for tests, which must not read - still less write - the real machine's PATH.
    /// </summary>
    internal PathExecutableCollector(Func<string> readPath) =>
        _readPath = readPath ?? throw new ArgumentNullException(nameof(readPath));

    public async IAsyncEnumerable<Entity> CollectAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        if (!IsSupported)
            yield break;

        // PATH resolution is first-match-wins, so the first directory holding a given file name is
        // the one that actually runs. Later copies are shadowed and must not be indexed: offering
        // to launch a python.exe that typing "python" would never reach is a lie about the machine.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in EnumerateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, ExecutablePattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
            {
                continue;
            }

            if (files.Length > MaxExecutablesPerDirectory)
                continue;

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!claimed.Add(Path.GetFileName(file)))
                    continue;

                var entity = TryCreateEntity(file, directory);
                if (entity is not null)
                    yield return entity;
            }
        }
    }

    /// <summary>
    /// The PATH directories, in resolution order, deduplicated and filtered to those worth walking.
    /// </summary>
    private IEnumerable<string> EnumerateDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in _readPath().Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = NormalizeDirectory(raw);
            if (directory is null || !seen.Add(directory))
                continue;

            if (IsUnderWindowsDirectory(directory))
                continue;

            yield return directory;
        }
    }

    /// <summary>
    /// Composes PATH the way Windows does when it creates a process: the machine value first, then
    /// the user value appended. Reading the two scopes separately is what makes this see an edit
    /// made after this process started.
    /// </summary>
    private static string ReadComposedPath()
    {
        if (!OperatingSystem.IsWindows())
            return Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        string? machine = null;
        string? user = null;

        try
        {
            machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
            user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Registry access denied; the process copy is stale but correct enough to proceed.
        }

        var composed = string.Join(
            Path.PathSeparator,
            new[] { machine, user }.Where(v => !string.IsNullOrWhiteSpace(v)));

        // Neither scope readable - a locked-down account, or a non-interactive context - leaves the
        // process copy as the only remaining evidence of what PATH is.
        return composed.Length > 0 ? composed : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    }

    private static string? NormalizeDirectory(string value)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(expanded) || !Path.IsPathFullyQualified(expanded))
                return null;

            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Skips the Windows tree, which is on every machine's PATH and holds several hundred
    /// executables. They are not a gap this collector needs to fill: the ones worth finding are
    /// already collected by <see cref="SystemToolCollector"/> with descriptions written for them,
    /// and by the AppsFolder and Start Menu. Indexing the remainder would add several times more
    /// entries than the whole rest of this source, nearly all of them undocumented internal
    /// binaries, and would point the help-text enricher at hundreds of unknown system executables.
    /// </summary>
    private static bool IsUnderWindowsDirectory(string directory)
    {
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrWhiteSpace(windows))
                return false;

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(windows)) + Path.DirectorySeparatorChar;
            return (directory + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private Entity? TryCreateEntity(string path, string directory)
    {
        try
        {
            var info = new FileInfo(path);

            // MSIX execution aliases are zero-byte reparse points. Every executable in
            // WindowsApps is one of these, and they carry no icon, no version resource, and no
            // description - there is nothing to index but a file name, and the packaged app behind
            // the alias is already collected from the AppsFolder under its real name.
            if (info.Length == 0)
                return null;

            var version = TryReadVersionInfo(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            if (stem.Length == 0)
                return null;

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["targetPath"] = path,
                ["command"] = stem,
                ["pathDirectory"] = directory,
            };

            if (Clean(version?.FileDescription) is { } description)
                metadata["description"] = description;

            var console = PortableExecutable.IsConsoleSubsystem(path);
            if (console)
                metadata["consoleSubsystem"] = "true";

            var entity = new Entity
            {
                Id = EntityId.Create(Source, path),
                Kind = console ? EntityKind.SystemTool : EntityKind.Application,
                DisplayName = ChooseDisplayName(stem, version),
                LaunchKind = LaunchKind.Executable,
                LaunchTarget = path,
                IconSource = path,
                Publisher = Clean(version?.CompanyName),
                Source = Source,
                RawMetadata = metadata,
            };

            return CollectorEntity.WithContentHash(entity);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The command is what the user types, so it is the name that must match. A product name is
    /// preferred only when it is recognisably about this tool, which promotes "Sysinternals
    /// AccessChk" over "accesschk" while rejecting the suite-wide or OS-wide product strings that
    /// would otherwise give a dozen unrelated tools the same name.
    /// </summary>
    private static string ChooseDisplayName(string stem, FileVersionInfo? version)
    {
        var product = Clean(version?.ProductName);
        if (product is not null
            && product.Length <= 64
            && product.Contains(stem, StringComparison.OrdinalIgnoreCase))
        {
            return product;
        }

        return stem;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static FileVersionInfo? TryReadVersionInfo(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
