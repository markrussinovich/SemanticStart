using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace SemanticStart.App;

/// <summary>
/// The app's own icon, read from the embedded multi-resolution .ico.
/// </summary>
internal static class AppIcon
{
    // Names the assembly explicitly. A bare "/Assets/..." resolves against the entry assembly,
    // which is this app when it runs normally but the test host when the window is constructed
    // from a test - where it fails with "Cannot locate resource".
    private static readonly Uri ResourceUri = new("pack://application:,,,/SemanticStart.App;component/Assets/SemanticStart.ico");

    /// <summary>
    /// The icon at the size the shell uses for the notification area.
    ///
    /// The size matters: handing the tray a 32px icon leaves Windows to shrink it, and a magnifier
    /// resampled down to 16px loses the ring. Asking for the small-icon metric makes the loader
    /// pick the entry drawn at that size instead.
    /// </summary>
    public static Drawing.Icon LoadSmall()
    {
        try
        {
            var stream = System.Windows.Application.GetResourceStream(ResourceUri)?.Stream;
            if (stream is not null)
            {
                using (stream)
                    return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading the app icon failed");
        }

        // A tray app with no icon is a tray app the user cannot reach, so fall back to something
        // rather than leaving the notification area empty.
        return Drawing.SystemIcons.Application;
    }
}
