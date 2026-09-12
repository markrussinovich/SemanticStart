using SemanticStart.Core.Model;

namespace SemanticStart.Core.Launching;

/// <summary>
/// The command line that starts an entity, rendered as text a user can paste into a terminal or
/// the Run dialog.
///
/// This deliberately mirrors <see cref="ShellEntityLauncher"/> case for case rather than just
/// handing back <see cref="Entity.LaunchTarget"/>. For half the launch kinds the target is not a
/// command at all - an applet name, a snap-in file, an AppUserModelId - and pasting it on its own
/// does nothing. What makes those runnable is the host program the launcher puts in front of them,
/// so that is what gets copied.
/// </summary>
public static class LaunchCommandLine
{
    public static string For(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var target = entity.LaunchTarget?.Trim() ?? string.Empty;
        if (target.Length == 0)
            return string.Empty;

        return entity.LaunchKind switch
        {
            LaunchKind.AppsFolder => "explorer.exe " + Quote(@"shell:AppsFolder\" + target),
            LaunchKind.ControlPanel => "control.exe " + Quote(target),
            LaunchKind.Mmc => "mmc.exe " + Quote(target),

            // A URI is typed as-is into the Run dialog or the address bar; wrapping it in a host
            // program would only be correct for one of the shells it might be pasted into.
            LaunchKind.Uri => target,

            _ => WithArguments(Quote(target), entity.LaunchArguments),
        };
    }

    private static string WithArguments(string command, string? arguments) =>
        string.IsNullOrWhiteSpace(arguments) ? command : command + " " + arguments.Trim();

    /// <summary>
    /// Quotes only when the value contains a space. Quoting everything would be equally correct
    /// for a shell, but the copied text is read by a person at least as often as it is run, and
    /// most of these paths need no quotes at all.
    /// </summary>
    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}
