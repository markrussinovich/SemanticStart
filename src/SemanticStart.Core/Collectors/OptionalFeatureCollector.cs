using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Collectors;

public sealed class OptionalFeatureCollector : IEntityCollector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FeatureInfoTimeout = TimeSpan.FromSeconds(40);
    private const string CapabilityIndexPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\CapabilityIndex";
    private const string PackagesPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages";

    public string Source => "optionalfeature";

    public bool IsSupported => OperatingSystem.IsWindows() && (File.Exists(DismPath) || Registry.LocalMachine.OpenSubKey(CapabilityIndexPath) is not null);

    public async IAsyncEnumerable<Entity> CollectAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            yield break;

        IReadOnlyList<Entity> entities = [];
        try
        {
            if (File.Exists(DismPath))
                entities = await CollectFeaturesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            entities = [];
        }

        if (entities.Count == 0)
            entities = CollectCapabilitiesFromRegistry(cancellationToken);

        // The curated catalog is always merged in, not used only as a fallback. Even when DISM
        // succeeds it reports raw feature keys with no descriptive text, and the catalog supplies
        // the user-facing names and intent vocabulary that make these features findable.
        var seen = new HashSet<string>(entities.Select(e => e.DisplayName), StringComparer.OrdinalIgnoreCase);

        foreach (var entity in CollectWellKnownFeatures(cancellationToken))
        {
            if (seen.Add(entity.DisplayName))
                entities = [.. entities, entity];
        }

        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entity;
        }
    }

    /// <summary>Emits the curated feature catalog, annotated with install state read from CBS.</summary>
    private IEnumerable<Entity> CollectWellKnownFeatures(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> states;

        try
        {
            states = WellKnownFeatureCatalog.ResolveStates(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            yield break;
        }

        foreach (var definition in WellKnownFeatureCatalog.All)
        {
            if (!states.TryGetValue(definition.PackagePrefix, out var state))
                continue;

            var entity = new Entity
            {
                Id = EntityId.Create(Source, definition.PackagePrefix),
                Kind = EntityKind.OptionalFeature,
                DisplayName = definition.DisplayName,
                LaunchKind = LaunchKind.Uri,
                LaunchTarget = "ms-settings:optionalfeatures",
                Source = Source,
                RawMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["featureName"] = definition.PackagePrefix,
                    ["description"] = definition.Description,
                    ["state"] = state,
                    ["provider"] = "catalog",
                },
            };

            yield return CollectorEntity.WithContentHash(entity);
        }
    }

    private async Task<IReadOnlyList<Entity>> CollectFeaturesAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var startInfo = new ProcessStartInfo
        {
            FileName = DismPath,
            Arguments = "/online /get-features /format:table /english",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            return [];

        var outputTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
        var errorTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return [];
        }

        var output = await outputTask.ConfigureAwait(false);
        _ = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
            return [];

        var entities = ParseDismOutput(output);
        return entities.Count == 0
            ? entities
            : await EnrichDismDescriptionsAsync(entities, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<Entity> ParseDismOutput(string output)
    {
        var entities = new List<Entity>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("Feature Name", StringComparison.OrdinalIgnoreCase) || line.StartsWith("---", StringComparison.Ordinal))
                continue;

            var separator = line.IndexOf('|');
            if (separator <= 0)
                continue;

            var name = line[..separator].Trim();
            var state = line[(separator + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(state))
                entities.Add(CreateEntity(name, FriendlyName(name), state, "dism"));
        }

        return entities;
    }

    private async Task<IReadOnlyList<Entity>> EnrichDismDescriptionsAsync(IReadOnlyList<Entity> entities, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(FeatureInfoTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var enriched = new List<Entity>(entities.Count);
        foreach (var entity in entities)
        {
            if (linkedCts.IsCancellationRequested)
            {
                enriched.Add(entity);
                continue;
            }

            if (!entity.RawMetadata.TryGetValue("featureName", out var featureName) || string.IsNullOrWhiteSpace(featureName))
            {
                enriched.Add(entity);
                continue;
            }

            try
            {
                var output = await RunDismAsync($"/online /get-featureinfo /featurename:{featureName} /english", TimeSpan.FromSeconds(3), linkedCts.Token).ConfigureAwait(false);
                var description = ParseDismFeatureInfoDescription(output);
                enriched.Add(string.IsNullOrWhiteSpace(description)
                    ? entity
                    : CreateEntity(featureName, entity.DisplayName, entity.RawMetadata.GetValueOrDefault("state") ?? "Unknown", "dism", description));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                enriched.Add(entity);
            }
        }

        return enriched;
    }

    private static string? ParseDismFeatureInfoDescription(string output)
    {
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("Description", StringComparison.OrdinalIgnoreCase))
                continue;

            var separator = line.IndexOf(':');
            if (separator <= 0 || separator + 1 >= line.Length)
                continue;

            var description = line[(separator + 1)..].Trim();
            return IsUsefulDescription(description) ? description : null;
        }

        return null;
    }

    private IReadOnlyList<Entity> CollectCapabilitiesFromRegistry(CancellationToken cancellationToken)
    {
        var entities = new List<Entity>();
        try
        {
            using var index = Registry.LocalMachine.OpenSubKey(CapabilityIndexPath);
            using var packages = Registry.LocalMachine.OpenSubKey(PackagesPath);
            if (index is null)
                return [];

            foreach (var capabilityName in index.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var capability = index.OpenSubKey(capabilityName);
                    if (capability is null)
                        continue;

                    var state = "Unknown";
                    foreach (var packageName in capability.GetValueNames())
                    {
                        var packageState = GetPackageState(packages, packageName);
                        if (packageState is not null)
                        {
                            state = packageState;
                            if (state.Equals("Installed", StringComparison.OrdinalIgnoreCase))
                                break;
                        }
                    }

                    var description = ReadRegistryDescription(capability);
                    entities.Add(CreateEntity(capabilityName, FriendlyName(capabilityName), state, "registry", description));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }

        return entities;
    }

    private Entity CreateEntity(string key, string displayName, string state, string provider, string? description = null)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["featureName"] = key,
            ["state"] = state,
            ["provider"] = provider,
        };
        if (IsUsefulDescription(description))
            metadata["description"] = description!;

        var entity = new Entity
        {
            Id = EntityId.Create(Source, key),
            Kind = EntityKind.OptionalFeature,
            DisplayName = displayName,
            LaunchKind = LaunchKind.Uri,
            LaunchTarget = "ms-settings:optionalfeatures",
            Source = Source,
            RawMetadata = metadata,
        };

        return CollectorEntity.WithContentHash(entity);
    }

    private static string? ReadRegistryDescription(RegistryKey capability)
    {
        foreach (var valueName in new[] { "Description", "DisplayName" })
        {
            try
            {
                var value = Convert.ToString(capability.GetValue(valueName));
                if (IsUsefulDescription(value))
                    return value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or InvalidOperationException)
            {
            }
        }

        return null;
    }

    private static bool IsUsefulDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        return trimmed.Length >= 12
               && trimmed.Count(char.IsWhiteSpace) >= 2
               && !trimmed.Equals("None", StringComparison.OrdinalIgnoreCase)
               && !trimmed.Equals("Not Available", StringComparison.OrdinalIgnoreCase)
               && !trimmed.StartsWith("@", StringComparison.Ordinal);
    }

    private static async Task<string> RunDismAsync(string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var startInfo = new ProcessStartInfo
        {
            FileName = DismPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            return string.Empty;

        var outputTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
        var errorTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return string.Empty;
        }

        var output = await outputTask.ConfigureAwait(false);
        _ = await errorTask.ConfigureAwait(false);
        return process.ExitCode == 0 ? output : string.Empty;
    }

    private static string? GetPackageState(RegistryKey? packages, string packageName)
    {
        if (packages is null)
            return null;

        try
        {
            using var package = packages.OpenSubKey(packageName);
            if (package is null)
                return null;

            return Convert.ToInt32(package.GetValue("CurrentState", 0)) switch
            {
                112 => "Installed",
                80 => "Staged",
                64 => "Install Pending",
                48 => "Superseded",
                32 => "Absent",
                _ => "Unknown",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Turns a raw DISM/registry feature key into something a user would recognise.
    ///
    /// Raw keys look like "Windows-HyperV-OptionalFeature-VirtualMachinePlatform-Client-Disabled":
    /// packaging boilerplate and install state are baked into the identifier. Left alone these
    /// names are unreadable in the UI and, because the display name leads the embedded text,
    /// they also poison the vector with noise tokens like "Client" and "Disabled".
    /// </summary>
    private static string FriendlyName(string featureName)
    {
        if (WellKnownNames.TryGetValue(featureName, out var known))
            return known;

        var normalized = featureName.Replace('-', ' ').Replace('_', ' ').Replace('.', ' ');

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !NoiseTokens.Contains(t))
            .Select(SplitPascalCase);

        var result = string.Join(' ', tokens).Trim();

        // Match against the well-known table again: stripping boilerplate often reveals a name
        // we do have a proper label for.
        foreach (var (key, value) in WellKnownNames)
        {
            if (result.Replace(" ", string.Empty).Equals(key.Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return result.Length == 0 ? featureName : result;
    }

    /// <summary>Packaging and state tokens that carry no meaning for a user or for the embedding.</summary>
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "OptionalFeature", "Package", "Client", "Disabled", "Enabled",
        "DisabledWithPayloadRemoved", "amd64", "x64", "x86", "arm64", "neutral",
    };

    /// <summary>
    /// Names users actually search for. Without these, capability keys embed as jargon and a
    /// query like "run linux on windows" cannot reach the Linux subsystem feature.
    /// </summary>
    private static readonly Dictionary<string, string> WellKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft-Windows-Subsystem-Linux"] = "Windows Subsystem for Linux (WSL)",
        ["WindowsSubsystemforLinux"] = "Windows Subsystem for Linux (WSL)",
        ["VirtualMachinePlatform"] = "Virtual Machine Platform",
        ["Microsoft-Hyper-V"] = "Hyper-V",
        ["Microsoft-Hyper-V-All"] = "Hyper-V",
        ["HypervisorPlatform"] = "Windows Hypervisor Platform",
        ["Containers-DisposableClientVM"] = "Windows Sandbox",
        ["Containers"] = "Containers",
        ["NetFx3"] = ".NET Framework 3.5",
        ["NetFx4-AdvSrvs"] = ".NET Framework 4 Advanced Services",
        ["IIS-WebServerRole"] = "Internet Information Services (IIS)",
        ["TelnetClient"] = "Telnet Client",
        ["TFTP"] = "TFTP Client",
        ["SMB1Protocol"] = "SMB 1.0/CIFS File Sharing Support",
        ["OpenSSH.Client"] = "OpenSSH Client",
        ["OpenSSH.Server"] = "OpenSSH Server",
        ["Microsoft-Windows-NetFx3-OC-Package"] = ".NET Framework 3.5",
        ["Printing-PrintToPDFServices-Features"] = "Microsoft Print to PDF",
        ["Printing-XPSServices-Features"] = "XPS Services",
        ["WorkFolders-Client"] = "Work Folders Client",
        ["MSRDC-Infrastructure"] = "Remote Differential Compression",
        ["Windows-Defender-ApplicationGuard"] = "Microsoft Defender Application Guard",
        ["LegacyComponents"] = "Legacy Components",
        ["MediaPlayback"] = "Media Features",
        ["WindowsMediaPlayer"] = "Windows Media Player",
        ["SearchEngine-Client-Package"] = "Windows Search",
        ["Recall"] = "Recall",
        ["VirtualMachinePlatform-Client"] = "Virtual Machine Platform",
    };

    /// <summary>"VirtualMachinePlatform" -> "Virtual Machine Platform", leaving acronyms intact.</summary>
    private static string SplitPascalCase(string token)
    {
        if (token.Length <= 2)
            return token;

        var sb = new System.Text.StringBuilder(token.Length + 8);

        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];

            if (i > 0 && char.IsUpper(c))
            {
                var prev = token[i - 1];
                var nextIsLower = i + 1 < token.Length && char.IsLower(token[i + 1]);

                // Break before a new word, but keep runs of capitals (IIS, PDF) together.
                if (char.IsLower(prev) || char.IsDigit(prev) || (char.IsUpper(prev) && nextIsLower))
                    sb.Append(' ');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string DismPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");
}
