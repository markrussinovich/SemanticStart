using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace SemanticStart.App;

/// <summary>
/// Applies Windows 11 Fluent colours to the app's resource dictionary and keeps them in step with
/// the system.
///
/// Colours are pushed into <see cref="Application.Resources"/> under fixed keys rather than being
/// hard-coded in XAML, so the overlay only has to reference them with <c>DynamicResource</c> to
/// follow the user from light to dark without being recreated.
/// </summary>
public static class ThemeService
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static Application? _application;

    public static bool IsDarkMode { get; private set; }

    public static void Initialize(Application application)
    {
        _application = application;
        Apply();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void Shutdown() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    /// <summary>Re-reads the system theme and reapplies brushes if anything changed.</summary>
    public static void Refresh() => Apply();

    /// <summary>
    /// Switches a window's non-client area (title bar, border) between the light and dark system
    /// chrome. WPF does not do this on its own, so a dark window would otherwise keep a white
    /// title bar.
    /// </summary>
    public static void ApplyWindowChrome(Window window)
    {
        void Set()
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                return;

            int dark = IsDarkMode ? 1 : 0;
            // 20 is DWMWA_USE_IMMERSIVE_DARK_MODE on current builds; 19 on Windows 10 1809-1903.
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        }

        if (window.IsLoaded)
            Set();
        else
            window.SourceInitialized += (_, _) => Set();

        _chromeWindows.Add(new WeakReference<Window>(window));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static readonly List<WeakReference<Window>> _chromeWindows = new();

    private static void RefreshWindowChrome()
    {
        foreach (var reference in _chromeWindows.ToArray())
        {
            if (!reference.TryGetTarget(out var window))
            {
                _chromeWindows.Remove(reference);
                continue;
            }

            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                continue;

            int dark = IsDarkMode ? 1 : 0;
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle))
            return;

        // The notification arrives on the SystemEvents thread; resources must be touched on the UI
        // thread.
        _application?.Dispatcher.BeginInvoke(new Action(Apply));
    }

    private static void Apply()
    {
        if (_application is null)
            return;

        try
        {
            IsDarkMode = ReadIsDarkMode();
            var accent = ReadAccentColors(IsDarkMode);
            var resources = _application.Resources;

            if (IsDarkMode)
            {
                // Values mirror the Windows 11 (WinUI) dark common colours; the comments name the
                // system resource each one stands in for.
                Set(resources, "PanelBrush", Rgb(0x2C, 0x2C, 0x2C));                     // AcrylicBackgroundFillColorDefaultFallback
                Set(resources, "PanelBorderBrush", Argb(0x33, 0x00, 0x00, 0x00));        // SurfaceStrokeColorFlyout
                Set(resources, "HeaderTextBrush", Argb(0xFF, 0xFF, 0xFF, 0xFF));         // TextFillColorPrimary
                Set(resources, "PrimaryTextBrush", Argb(0xFF, 0xFF, 0xFF, 0xFF));        // TextFillColorPrimary
                Set(resources, "SecondaryTextBrush", Argb(0xC5, 0xFF, 0xFF, 0xFF));      // TextFillColorSecondary
                Set(resources, "TertiaryTextBrush", Argb(0x87, 0xFF, 0xFF, 0xFF));       // TextFillColorTertiary
                Set(resources, "DisabledTextBrush", Argb(0x5D, 0xFF, 0xFF, 0xFF));       // TextFillColorDisabled
                Set(resources, "SearchBoxBrush", Argb(0x0F, 0xFF, 0xFF, 0xFF));          // ControlFillColorDefault
                Set(resources, "InputActiveBrush", Argb(0xFF, 0x1F, 0x1F, 0x1F));        // ControlFillColorInputActive
                Set(resources, "SearchBoxBorderBrush", Argb(0x12, 0xFF, 0xFF, 0xFF));    // ControlStrokeColorDefault
                Set(resources, "ControlStrongStrokeBrush", Argb(0x8B, 0xFF, 0xFF, 0xFF));// ControlStrongStrokeColorDefault
                Set(resources, "FocusStrokeBrush", Argb(0xFF, 0xFF, 0xFF, 0xFF));        // FocusStrokeColorOuter
                Set(resources, "ItemHoverBrush", Argb(0x0F, 0xFF, 0xFF, 0xFF));          // SubtleFillColorSecondary
                Set(resources, "ItemSelectedBrush", Argb(0x16, 0xFF, 0xFF, 0xFF));       // ListViewItemBackgroundSelected
                Set(resources, "IconBackplateBrush", Argb(0x0F, 0xFF, 0xFF, 0xFF));      // ControlAltFillColorSecondary
                Set(resources, "BadgeBrush", Argb(0x12, 0xFF, 0xFF, 0xFF));              // ControlAltFillColorTertiary
                Set(resources, "DividerBrush", Argb(0x15, 0xFF, 0xFF, 0xFF));            // DividerStrokeColorDefault
                Set(resources, "ScrollBarThumbBrush", Argb(0x8B, 0xFF, 0xFF, 0xFF));     // ControlStrongFillColorDefault
                Set(resources, "ScrollBarThumbHoverBrush", Argb(0xC5, 0xFF, 0xFF, 0xFF));
                Set(resources, "ScrollBarButtonHoverBrush", Argb(0x0F, 0xFF, 0xFF, 0xFF));
                resources["ShadowOpacity"] = 0.60;
            }
            else
            {
                Set(resources, "PanelBrush", Rgb(0xF9, 0xF9, 0xF9));                     // AcrylicBackgroundFillColorDefaultFallback
                Set(resources, "PanelBorderBrush", Argb(0x0F, 0x00, 0x00, 0x00));        // SurfaceStrokeColorFlyout
                Set(resources, "HeaderTextBrush", Argb(0xE4, 0x00, 0x00, 0x00));         // TextFillColorPrimary
                Set(resources, "PrimaryTextBrush", Argb(0xE4, 0x00, 0x00, 0x00));        // TextFillColorPrimary
                Set(resources, "SecondaryTextBrush", Argb(0x9E, 0x00, 0x00, 0x00));      // TextFillColorSecondary
                Set(resources, "TertiaryTextBrush", Argb(0x72, 0x00, 0x00, 0x00));       // TextFillColorTertiary
                Set(resources, "DisabledTextBrush", Argb(0x5C, 0x00, 0x00, 0x00));       // TextFillColorDisabled
                Set(resources, "SearchBoxBrush", Argb(0xB3, 0xFF, 0xFF, 0xFF));          // ControlFillColorDefault
                Set(resources, "InputActiveBrush", Argb(0xFF, 0xFF, 0xFF, 0xFF));        // ControlFillColorInputActive
                Set(resources, "SearchBoxBorderBrush", Argb(0x0F, 0x00, 0x00, 0x00));    // ControlStrokeColorDefault
                Set(resources, "ControlStrongStrokeBrush", Argb(0x72, 0x00, 0x00, 0x00));// ControlStrongStrokeColorDefault
                Set(resources, "FocusStrokeBrush", Argb(0xE4, 0x00, 0x00, 0x00));        // FocusStrokeColorOuter
                Set(resources, "ItemHoverBrush", Argb(0x09, 0x00, 0x00, 0x00));          // SubtleFillColorSecondary
                Set(resources, "ItemSelectedBrush", Argb(0x0F, 0x00, 0x00, 0x00));       // ListViewItemBackgroundSelected
                Set(resources, "IconBackplateBrush", Argb(0x06, 0x00, 0x00, 0x00));      // ControlAltFillColorSecondary
                Set(resources, "BadgeBrush", Argb(0x0A, 0x00, 0x00, 0x00));              // ControlAltFillColorTertiary
                Set(resources, "DividerBrush", Argb(0x0F, 0x00, 0x00, 0x00));            // DividerStrokeColorDefault
                Set(resources, "ScrollBarThumbBrush", Argb(0x72, 0x00, 0x00, 0x00));     // ControlStrongFillColorDefault
                Set(resources, "ScrollBarThumbHoverBrush", Argb(0x9E, 0x00, 0x00, 0x00));
                Set(resources, "ScrollBarButtonHoverBrush", Argb(0x09, 0x00, 0x00, 0x00));
                resources["ShadowOpacity"] = 0.28;
            }

            Set(resources, "AccentBrush", accent.Fill);
            // Windows dims the accent fill for hover and press rather than tinting it.
            Set(resources, "AccentHoverBrush", Argb(0xE6, accent.Fill.R, accent.Fill.G, accent.Fill.B));
            Set(resources, "AccentPressedBrush", Argb(0xCC, accent.Fill.R, accent.Fill.G, accent.Fill.B));
            Set(resources, "AccentTextBrush", accent.Text);
            // The accent lightens in dark mode, so the label on top of it turns black there. That
            // inversion is what TextOnAccentFillColorPrimary does, and skipping it is one of the
            // most visible ways an app stops looking like the shell.
            Set(resources, "TextOnAccentBrush", IsDarkMode
                ? Argb(0xFF, 0x00, 0x00, 0x00)
                : Argb(0xFF, 0xFF, 0xFF, 0xFF));

            // DropShadowEffect.Color takes a Color, not a Brush, so this one cannot go through
            // the brush helper above.
            resources["ShadowColor"] = Color.FromRgb(0x00, 0x00, 0x00);

            RefreshWindowChrome();
        }
        catch (Exception ex)
        {
            // Theming must never be fatal; the XAML defaults remain in place.
            Log.Error(ex, "Applying the system theme failed");
        }
    }

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    private static bool ReadIsDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        // The value is AppsUseLightTheme, so 0 means dark. Absent means light.
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    /// <summary>
    /// Reads the accent colours Windows itself uses for UI.
    ///
    /// The value under DWM\AccentColor is the window-chrome accent, and it is the wrong one to
    /// paint controls with: on a dark surface it is often too dark to read, and on a light one too
    /// light. Windows solves this by shipping a whole ramp - three lighter and three darker shades
    /// of the same hue - in Explorer\Accent\AccentPalette, and WinUI picks from it by theme. Doing
    /// the same here gives exactly the accent the rest of the shell shows, rather than an
    /// approximation of it.
    /// </summary>
    private static (Color Fill, Color Text) ReadAccentColors(bool isDark)
    {
        // Defaults are the shades of the stock blue accent, for the rare machine with no palette.
        var fill = isDark ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0x00, 0x67, 0xC0);
        var text = isDark ? Color.FromRgb(0x99, 0xEB, 0xFF) : Color.FromRgb(0x00, 0x3E, 0x92);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");

            if (key?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 32)
            {
                // Eight RGBA entries, lightest first: light3, light2, light1, accent, dark1,
                // dark2, dark3, and a complement the shell uses elsewhere.
                Color Shade(int index) =>
                    Color.FromRgb(palette[index * 4], palette[(index * 4) + 1], palette[(index * 4) + 2]);

                // AccentFillColorDefault: light2 on dark, dark1 on light.
                fill = isDark ? Shade(1) : Shade(4);
                // AccentTextFillColorPrimary: light3 on dark, dark2 on light.
                text = isDark ? Shade(0) : Shade(5);
                return (fill, text);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading the Windows accent palette failed");
        }

        // No palette, so derive the ramp from the chrome accent the same way Windows would.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int packed)
            {
                // DWM stores the value as 0xAABBGGRR, not ARGB.
                var r = (byte)(packed & 0xFF);
                var g = (byte)((packed >> 8) & 0xFF);
                var b = (byte)((packed >> 16) & 0xFF);
                var baseColor = Color.FromRgb(r, g, b);

                fill = Blend(baseColor, isDark ? Colors.White : Colors.Black, isDark ? 0.35 : 0.15);
                text = Blend(baseColor, isDark ? Colors.White : Colors.Black, isDark ? 0.60 : 0.35);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading the system accent colour failed");
        }

        return (fill, text);
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        byte Mix(byte a, byte b) => (byte)Math.Clamp(a + ((b - a) * amount), 0, 255);
        return Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }
}
