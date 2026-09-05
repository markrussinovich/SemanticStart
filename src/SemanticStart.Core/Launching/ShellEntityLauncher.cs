using System.Diagnostics;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Launching;

/// <summary>
/// Activates indexed entities. Every launch goes through the shell rather than CreateProcess so
/// that protocol handlers, AppsFolder AppUserModelIds, and file associations all behave exactly
/// as they do from the real Start menu.
/// </summary>
public sealed class ShellEntityLauncher : IEntityLauncher
{
    private readonly IIndexStore? _store;

    /// <param name="store">Optional. When supplied, launches are recorded to bias future ranking.</param>
    public ShellEntityLauncher(IIndexStore? store = null) => _store = store;

    public async Task LaunchAsync(
        Entity entity,
        LaunchOptions options = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (options.OpenContainingFolder)
        {
            OpenContainingFolder(entity);
        }
        else
        {
            Launch(entity, options);
        }

        if (_store is not null)
        {
            try
            {
                await _store.RecordLaunchAsync(entity.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Ranking telemetry must never break the thing the user actually asked for.
                Debug.WriteLine($"Failed to record launch for {entity.Id}: {ex.Message}");
            }
        }
    }

    private static void Launch(Entity entity, LaunchOptions options)
    {
        switch (entity.LaunchKind)
        {
            case LaunchKind.AppsFolder:
                // explorer.exe is the only reliable way to activate an AppUserModelId without
                // taking a dependency on the packaged-app activation COM interfaces.
                Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{entity.LaunchTarget}",
                    UseShellExecute = true,
                });
                break;

            case LaunchKind.Uri:
                Start(new ProcessStartInfo
                {
                    FileName = entity.LaunchTarget,
                    UseShellExecute = true,
                });
                break;

            case LaunchKind.ControlPanel:
                Start(new ProcessStartInfo
                {
                    FileName = "control.exe",
                    Arguments = Quote(entity.LaunchTarget),
                    UseShellExecute = true,
                    Verb = options.RunAsAdministrator ? "runas" : string.Empty,
                });
                break;

            case LaunchKind.Mmc:
                Start(new ProcessStartInfo
                {
                    FileName = "mmc.exe",
                    Arguments = Quote(entity.LaunchTarget),
                    UseShellExecute = true,
                    Verb = options.RunAsAdministrator ? "runas" : string.Empty,
                });
                break;

            case LaunchKind.Shortcut:
            case LaunchKind.Executable:
            default:
                Start(new ProcessStartInfo
                {
                    FileName = entity.LaunchTarget,
                    Arguments = entity.LaunchArguments ?? string.Empty,
                    UseShellExecute = true,
                    WorkingDirectory = GetWorkingDirectory(entity.LaunchTarget),
                    Verb = options.RunAsAdministrator ? "runas" : string.Empty,
                });
                break;
        }
    }

    private static void OpenContainingFolder(Entity entity)
    {
        var path = entity.LaunchKind switch
        {
            LaunchKind.Executable or LaunchKind.Shortcut => entity.LaunchTarget,
            LaunchKind.Mmc => entity.LaunchTarget,
            _ => entity.IconSource,
        };

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,{Quote(path)}",
            UseShellExecute = true,
        });
    }

    private static void Start(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 is ERROR_CANCELLED: the user dismissed the UAC prompt. Not an error.
        }
    }

    private static string? GetWorkingDirectory(string target)
    {
        try
        {
            return Path.GetDirectoryName(target);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}
