using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SemanticStart.Core.Abstractions;
using Forms = System.Windows.Forms;

namespace SemanticStart.App;

public partial class OverlayWindow : Window
{
    private readonly OverlayViewModel _viewModel;

    /// <summary>Raised when the user clicks the settings button in the footer.</summary>
    public Action? SettingsRequested { get; set; }

    public OverlayWindow(OverlayViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // Expanding a result grows it downwards, so the panel the user just asked to read is
        // routinely the part that ends up below the fold. Scroll it back into view.
        ResultsList.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,
            new RoutedEventHandler(DetailsToggle_Checked));
    }

    private void DetailsToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        var container = ItemsControl.ContainerFromElement(ResultsList, source) as ListBoxItem;
        if (container is null)
            return;

        // The panel has not been measured yet at Checked time, so its height is still zero and
        // scrolling now would aim at the collapsed row. Wait for the layout pass it triggers.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var viewport = FindScrollViewer(ResultsList)?.ViewportHeight ?? 0;

            // A result taller than the viewport cannot be shown whole. Bringing all of it into
            // view would scroll to its bottom and push the name off the top, so ask only for as
            // much as fits, measured from the top.
            if (viewport > 0 && container.ActualHeight > viewport)
                container.BringIntoView(new Rect(0, 0, container.ActualWidth, viewport));
            else
                container.BringIntoView();
        });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
            return viewer;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
                return found;
        }

        return null;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        HideAndReset();
        SettingsRequested?.Invoke();
    }

    public void ShowOverlay()
    {
        // Registry theme changes are not always broadcast, so re-read on every activation.
        ThemeService.Refresh();
        PositionNearStartMenu();
        Show();
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void HideAndReset()
    {
        Hide();
        _viewModel.Clear();
    }

    /// <summary>
    /// Positions the overlay in physical pixels rather than through <see cref="Window.Left"/> and
    /// <see cref="Window.Top"/>.
    /// <para>
    /// WPF expresses those properties in units scaled by the DPI of the monitor the window is
    /// *currently* on, but the monitor we want to move to may have a different scale factor. Doing
    /// the arithmetic in device-independent units therefore lands the window in the wrong place on
    /// mixed-DPI setups (the common laptop + external display case). Working in physical pixels and
    /// asking the target monitor for its own effective DPI is exact regardless of scaling.
    /// </para>
    /// </summary>
    private void PositionNearStartMenu()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var cursor = Forms.Cursor.Position;
        var work = Forms.Screen.FromPoint(cursor).WorkingArea;

        var scale = GetScaleForPoint(cursor);
        var widthPx = (int)Math.Round(Width * scale);
        var heightPx = (int)Math.Round(Height * scale);
        var marginPx = (int)Math.Round(BottomMarginDip * scale);

        var x = work.Left + Math.Max(0, (work.Width - widthPx) / 2);
        var y = work.Top + Math.Max(0, work.Height - heightPx - marginPx);

        SetWindowPos(handle, IntPtr.Zero, x, y, widthPx, heightPx, SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>Effective scale factor of the monitor containing <paramref name="point"/>.</summary>
    private double GetScaleForPoint(System.Drawing.Point point)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = point.X, Y = point.Y }, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
            return dpiX / 96.0;

        // Pre-8.1 fallback: our own window's DPI is the best answer available.
        return VisualTreeHelper.GetDpi(this).DpiScaleX;
    }

    private const double BottomMarginDip = 24;
    private const int MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, int flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private async void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.Escape)
            {
                HideAndReset();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Down || (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.J))
            {
                _viewModel.MoveSelection(1);
                ResultsList.ScrollIntoView(_viewModel.SelectedItem);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Up || (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.K))
            {
                _viewModel.MoveSelection(-1);
                ResultsList.ScrollIntoView(_viewModel.SelectedItem);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                var modifiers = Keyboard.Modifiers;
                var options = new LaunchOptions
                {
                    RunAsAdministrator = modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Shift),
                    OpenContainingFolder = modifiers.HasFlag(ModifierKeys.Control) && modifiers.HasFlag(ModifierKeys.Shift),
                };
                await _viewModel.LaunchSelectedAsync(options);
                HideAndReset();
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Overlay key handling failed");
        }
    }

    private async void Result_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.DataContext is SearchResultItem item)
            {
                ResultsList.SelectedItem = item;
                await _viewModel.LaunchSelectedAsync(default);
                HideAndReset();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Mouse launch failed");
        }
    }

    private void Window_Deactivated(object sender, EventArgs e) => HideAndReset();
}
