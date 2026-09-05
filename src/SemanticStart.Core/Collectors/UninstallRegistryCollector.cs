using System.Runtime.CompilerServices;
using Microsoft.Win32;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Collectors;

public sealed class UninstallRegistryCollector : IEntityCollector
{
    public string Source => "uninstall";

    public bool IsSupported => OperatingSystem.IsWindows();

    public async IAsyncEnumerable<Entity> CollectAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        if (!IsSupported)
            yield break;

        foreach (var root in GetRegistryRoots())
        {
            RegistryKey? key = null;
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

                string[] subKeyNames;
                try
                {
                    subKeyNames = key.GetSubKeyNames();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    continue;
                }

                foreach (var subKeyName in subKeyNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entity = TryCreateEntity(key, subKeyName, root.IdentityPrefix);
                    if (entity is not null)
                        yield return entity;
                }
            }
        }
    }

    private Entity? TryCreateEntity(RegistryKey parent, string subKeyName, string identityPrefix)
    {
        try
        {
            using var subKey = parent.OpenSubKey(subKeyName);
            if (subKey is null)
                return null;

            var displayName = Convert.ToString(subKey.GetValue("DisplayName"));
            if (string.IsNullOrWhiteSpace(displayName))
                return null;

            if (Convert.ToInt32(subKey.GetValue("SystemComponent", 0)) == 1)
                return null;

            var displayIcon = Convert.ToString(subKey.GetValue("DisplayIcon"));
            var publisher = Convert.ToString(subKey.GetValue("Publisher"));
            var installLocation = Convert.ToString(subKey.GetValue("InstallLocation"));
            var displayVersion = Convert.ToString(subKey.GetValue("DisplayVersion"));
            var uninstallString = Convert.ToString(subKey.GetValue("UninstallString"));

            var launchTarget = ResolveLaunchTarget(installLocation, displayIcon, uninstallString) ?? displayIcon ?? installLocation ?? uninstallString ?? displayName;
            if (string.IsNullOrWhiteSpace(launchTarget))
                return null;

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["registryKey"] = subKeyName,
            };
            AddIfPresent(metadata, "displayVersion", displayVersion);
            AddIfPresent(metadata, "installLocation", installLocation);
            AddIfPresent(metadata, "displayIcon", displayIcon);
            AddIfPresent(metadata, "uninstallString", uninstallString);

            var entity = new Entity
            {
                Id = EntityId.Create(Source, $"{identityPrefix}\\{subKeyName}"),
                Kind = EntityKind.Application,
                DisplayName = displayName,
                LaunchKind = LaunchKind.Executable,
                LaunchTarget = launchTarget,
                IconSource = displayIcon,
                Publisher = string.IsNullOrWhiteSpace(publisher) ? null : publisher,
                Source = Source,
                RawMetadata = metadata,
            };

            return CollectorEntity.WithContentHash(entity);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ResolveLaunchTarget(string? installLocation, string? displayIcon, string? uninstallString)
    {
        var iconPath = ExtractExecutablePath(displayIcon);
        if (IsExistingExecutable(iconPath))
            return iconPath;

        var installExe = FindExecutableInInstallLocation(installLocation);
        if (installExe is not null)
            return installExe;

        var uninstallExe = ExtractExecutablePath(uninstallString);
        if (!string.IsNullOrWhiteSpace(uninstallExe))
            return uninstallExe;

        return !string.IsNullOrWhiteSpace(iconPath) ? iconPath : null;
    }

    private static string? FindExecutableInInstallLocation(string? installLocation)
    {
        var path = TrimRegistryPath(installLocation);
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return null;

        try
        {
            return Directory.EnumerateFiles(path, "*.exe", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p.Contains("unins", StringComparison.OrdinalIgnoreCase) || p.Contains("setup", StringComparison.OrdinalIgnoreCase))
                .ThenBy(p => p.Length)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool IsExistingExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && File.Exists(path);

    private static string? ExtractExecutablePath(string? value)
    {
        var path = TrimRegistryPath(value);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var exeIndex = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
            return path[..(exeIndex + 4)].Trim('"', ' ');

        return path;
    }

    private static string? TrimRegistryPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = Environment.ExpandEnvironmentVariables(value.Trim());
        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote > 1)
                return trimmed[1..endQuote];
        }

        var comma = trimmed.LastIndexOf(',');
        if (comma > 0 && int.TryParse(trimmed[(comma + 1)..].Trim(), out _))
            trimmed = trimmed[..comma];

        return trimmed.Trim('"', ' ');
    }

    private static void AddIfPresent(Dictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            metadata[key] = value;
    }

    private static IEnumerable<RegistryRoot> GetRegistryRoots()
    {
        const string uninstall = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall";
        yield return new RegistryRoot(Registry.LocalMachine, uninstall, "hklm64");
        yield return new RegistryRoot(Registry.LocalMachine, "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall", "hklm32");
        yield return new RegistryRoot(Registry.CurrentUser, uninstall, "hkcu");
    }

    private sealed record RegistryRoot(RegistryKey BaseKey, string SubKey, string IdentityPrefix);
}
