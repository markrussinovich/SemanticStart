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
            var accent = ReadAccentColor(IsDarkMode);
            var resources = _application.Resources;

            if (IsDarkMode)
            {
                Set(resources, "PanelBrush", Rgb(0x2C, 0x2C, 0x2C));
                Set(resources, "PanelBorderBrush", Argb(0x18, 0xFF, 0xFF, 0xFF));
                Set(resources, "HeaderTextBrush", Argb(0xFF, 0xFF, 0xFF, 0xFF));
                Set(resources, "PrimaryTextBrush", Argb(0xFF, 0xFF, 0xFF, 0xFF));
                Set(resources, "SecondaryTextBrush", Argb(0xC5, 0xFF, 0xFF, 0xFF));
                Set(resources, "SearchBoxBrush", Argb(0x0F, 0xFF, 0xFF, 0xFF));
                Set(resources, "SearchBoxBorderBrush", Argb(0x18, 0xFF, 0xFF, 0xFF));
                Set(resources, "ItemHoverBrush", Argb(0x0F, 0xFF, 0xFF, 0xFF));
                Set(resources, "ItemSelectedBrush", Argb(0x1A, 0xFF, 0xFF, 0xFF));
                Set(resources, "IconBackplateBrush", Argb(0x0F, 0xFF, 0xFF, 0xFF));
                Set(resources, "BadgeBrush", Argb(0x14, 0xFF, 0xFF, 0xFF));
                Set(resources, "DividerBrush", Argb(0x14, 0xFF, 0xFF, 0xFF));
                resources["ShadowOpacity"] = 0.60;
            }
            else
            {
                Set(resources, "PanelBrush", Rgb(0xF3, 0xF3, 0xF3));
                Set(resources, "PanelBorderBrush", Argb(0x14, 0x00, 0x00, 0x00));
                Set(resources, "HeaderTextBrush", Argb(0xE4, 0x00, 0x00, 0x00));
                Set(resources, "PrimaryTextBrush", Argb(0xE4, 0x00, 0x00, 0x00));
                Set(resources, "SecondaryTextBrush", Argb(0x9B, 0x00, 0x00, 0x00));
                Set(resources, "SearchBoxBrush", Rgb(0xFB, 0xFB, 0xFB));
                Set(resources, "SearchBoxBorderBrush", Argb(0x30, 0x00, 0x00, 0x00));
                Set(resources, "ItemHoverBrush", Argb(0x0A, 0x00, 0x00, 0x00));
                Set(resources, "ItemSelectedBrush", Argb(0x14, 0x00, 0x00, 0x00));
                Set(resources, "IconBackplateBrush", Argb(0x08, 0x00, 0x00, 0x00));
                Set(resources, "BadgeBrush", Argb(0x0C, 0x00, 0x00, 0x00));
                Set(resources, "DividerBrush", Argb(0x0F, 0x00, 0x00, 0x00));
                resources["ShadowOpacity"] = 0.28;
            }

            Set(resources, "AccentBrush", accent);

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
    /// Reads the user's accent colour and nudges it toward legibility against the panel. The raw
    /// DWM accent is chosen for window chrome, and on its own it can be too dark to read on a dark
    /// surface or too light on a light one.
    /// </summary>
    private static Color ReadAccentColor(bool isDark)
    {
        var accent = isDark ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0x00, 0x5F, 0xB8);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int packed)
            {
                // DWM stores the value as 0xAABBGGRR, not ARGB.
                var r = (byte)(packed & 0xFF);
                var g = (byte)((packed >> 8) & 0xFF);
                var b = (byte)((packed >> 16) & 0xFF);
                accent = Blend(Color.FromRgb(r, g, b), isDark ? Colors.White : Colors.Black, isDark ? 0.35 : 0.15);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading the system accent colour failed");
        }

        return accent;
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        byte Mix(byte a, byte b) => (byte)Math.Clamp(a + ((b - a) * amount), 0, 255);
        return Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }
}
