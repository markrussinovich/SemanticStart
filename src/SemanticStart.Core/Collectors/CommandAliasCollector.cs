using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Collectors;

/// <summary>
/// Collects the commands a user can invoke by name, from the registry's App Paths key.
///
/// This closes a gap that no other collector can reach. Every existing source enumerates things
/// Windows chose to *show*: the AppsFolder, the Start Menu, the uninstall list. Command-line tools
/// shipped inside an installed suite appear in none of them, because their MSIX manifests declare
/// AppListEntry="none" precisely so Start will not list them. On a machine with the Sysinternals
/// Suite installed that hides roughly seventy tools - accesschk, handle, psexec, pssuspend,
/// sigcheck - which are exactly the tools whose names a user is least likely to remember and most
/// needs searching for.
///
/// App Paths is the right source rather than a directory scan because it is Windows' own registry
/// of invocable-by-name commands, and because it resolves an MSIX execution alias (a zero-byte
/// reparse point that carries no icon and no version resource) to the real executable behind it,
/// which is what the enrichers need in order to read anything about the tool at all.
/// </summary>
public sealed class CommandAliasCollector : IEntityCollector
{
    public string Source => "command";

    public bool IsSupported => OperatingSystem.IsWindows();

    public async IAsyncEnumerable<Entity> CollectAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        if (!IsSupported)
            yield break;

        foreach (var root in GetRegistryRoots())
        {
            RegistryKey? key;
            try
            {
                key = root.BaseKey.OpenSubKey(root.SubKey);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                continue;
            }

            using (key)
            {
                if (key is null)
                    continue;

                string[] names;
                try
                {
                    names = key.GetSubKeyNames();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    continue;
                }

                foreach (var name in names)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entity = TryCreateEntity(key, name);
                    if (entity is not null)
                        yield return entity;
                }
            }
        }
    }

    private Entity? TryCreateEntity(RegistryKey parent, string aliasName)
    {
        try
        {
            if (!aliasName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return null;

            using var subKey = parent.OpenSubKey(aliasName);
            var target = Normalize(Convert.ToString(subKey?.GetValue(null)));
            if (target is null || !File.Exists(target))
                return null;

            var version = TryReadVersionInfo(target);

            // The version resource is the only description available for a tool that Windows hides
            // from Start, and for this class of tool it is unusually good: accesschk.exe describes
            // itself as "Reports effective permissions for securable objects". It is also the only
            // way to recover a human display name, since the alias and the file on disk are both
            // named in lowercase.
            var stem = Path.GetFileNameWithoutExtension(aliasName);
            var displayName = ChooseDisplayName(stem, version);

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["targetPath"] = target,
                ["command"] = stem,
                ["appPathAlias"] = aliasName,
            };

            if (!string.IsNullOrWhiteSpace(version?.FileDescription))
                metadata["description"] = version!.FileDescription!.Trim();

            var console = PortableExecutable.IsConsoleSubsystem(target);
            if (console)
                metadata["consoleSubsystem"] = "true";

            var entity = new Entity
            {
                Id = EntityId.Create(Source, target),
                Kind = console ? EntityKind.SystemTool : EntityKind.Application,
                DisplayName = displayName,
                LaunchKind = LaunchKind.Executable,
                LaunchTarget = target,
                IconSource = target,
                Publisher = Clean(version?.CompanyName),
                Source = Source,
                RawMetadata = metadata,
            };

            return CollectorEntity.WithContentHash(entity);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Prefers the product name when it is actually about this tool, which yields "Sysinternals
    /// AccessChk" rather than the bare "accesschk". Products that name the whole operating system
    /// rather than the tool - "Microsoft Windows Operating System" - fail the containment test and
    /// fall back to the command itself, which is at least what the user would type.
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

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            var trimmed = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            return Path.IsPathFullyQualified(trimmed) ? Path.GetFullPath(trimmed) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IEnumerable<RegistryRoot> GetRegistryRoots()
    {
        const string appPaths = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths";
        yield return new RegistryRoot(Registry.CurrentUser, appPaths);
        yield return new RegistryRoot(Registry.LocalMachine, appPaths);
        yield return new RegistryRoot(Registry.LocalMachine, "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\App Paths");
    }

    private sealed record RegistryRoot(RegistryKey BaseKey, string SubKey);
}

/// <summary>
/// Just enough PE header parsing to tell a console tool from a windowed application.
/// </summary>
internal static class PortableExecutable
{
    private const int SubsystemWindowsCui = 3;

    /// <summary>
    /// True when the image declares the console subsystem. This is the only reliable way to know
    /// whether running the file with a help switch will print text and exit rather than opening a
    /// window, which is what gates the help-text enricher.
    /// </summary>
    public static bool IsConsoleSubsystem(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
                return false;

            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 0x5C > stream.Length)
                return false;

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x0000_4550)
                return false;

            // Skip the COFF file header to reach the optional header, whose magic distinguishes
            // PE32 from PE32+; the subsystem field sits at the same offset in both.
            stream.Position = peOffset + 4 + 20;
            var magic = reader.ReadUInt16();
            if (magic is not (0x10B or 0x20B))
                return false;

            stream.Position = peOffset + 4 + 20 + 68;
            return reader.ReadUInt16() == SubsystemWindowsCui;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
