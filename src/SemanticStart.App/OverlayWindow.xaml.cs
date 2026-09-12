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
        ClearCopiedFlash();
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

            if (TryToggleDetails(e.Key))
            {
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

    /// <summary>
    /// All navigation keys are taken here, on the way down, because the text box claims Left and
    /// Right for the caret and the list claims Up and Down for its own navigation. Nothing ever
    /// moves the keyboard focus out of the search box: the query stays typeable at every moment,
    /// and which of the two the arrows are steering is tracked by
    /// <see cref="OverlayViewModel.IsResultsActive"/> instead of by where the focus happens to be.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        try
        {
            var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

            if (control && e.Key == Key.C && TryCopySelectedCommandLine())
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Down || (control && e.Key == Key.J))
            {
                // Down out of the query lands on the first result rather than skipping past it.
                if (!_viewModel.IsResultsActive)
                    EnterResults();
                else
                    MoveSelection(1);

                e.Handled = true;
                return;
            }

            if (e.Key == Key.Up || (control && e.Key == Key.K))
            {
                // Up walks back out the way Down walked in: past the first result is the query.
                if (!_viewModel.IsResultsActive)
                    return;

                if (_viewModel.SelectedIndex <= 0)
                    _viewModel.IsResultsActive = false;
                else
                    MoveSelection(-1);

                e.Handled = true;
                return;
            }

            if ((e.Key == Key.Right || e.Key == Key.Left) && TryToggleDetails(e.Key))
                e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Overlay navigation key handling failed");
        }
    }

    /// <summary>Hands the arrow keys to the results, starting at the first one.</summary>
    private void EnterResults()
    {
        if (_viewModel.Results.Count == 0)
            return;

        _viewModel.SelectedIndex = 0;
        _viewModel.IsResultsActive = true;
        ResultsList.ScrollIntoView(_viewModel.SelectedItem);
    }

    private void MoveSelection(int delta)
    {
        _viewModel.MoveSelection(delta);
        ResultsList.ScrollIntoView(_viewModel.SelectedItem);
    }

    /// <summary>
    /// Every new query re-ranks the list, so the highlight the arrows were steering no longer means
    /// anything: hand them back to the text.
    /// </summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // TextChanged fires while the binding is applied, before the constructor has finished.
        if (_viewModel is null)
            return;

        _viewModel.IsResultsActive = false;
    }

    /// <summary>
    /// Opens or closes the selected result's description: Ctrl+D toggles, Right opens and Left
    /// closes. While the arrows still belong to the query they only act once the caret has run out
    /// of text to move through, so editing comes first.
    /// </summary>
    private bool TryToggleDetails(Key key)
    {
        if (_viewModel.SelectedItem is not { } item)
            return false;

        bool? requested = key switch
        {
            Key.D when Keyboard.Modifiers.HasFlag(ModifierKeys.Control) => !item.IsExpanded,
            Key.Right when IsCaretPastQuery(atEnd: true) => true,
            Key.Left when IsCaretPastQuery(atEnd: false) => false,
            _ => null
        };

        // Leaving a no-op unhandled matters for the arrows: pressing Left on a collapsed result
        // while editing should still be an ordinary caret move.
        if (requested is not { } expand || expand == item.IsExpanded)
            return false;

        // The toggle button's Checked handler scrolls the newly revealed panel into view.
        item.IsExpanded = expand;
        return true;
    }

    /// <summary>
    /// Ctrl+C copies the selected result's command line, but only when it would otherwise do
    /// nothing. The keyboard focus never leaves the query box, so Ctrl+C with text selected there
    /// still has to mean copy that text; this only claims the shortcut when the selection is empty
    /// and a result is highlighted.
    /// </summary>
    private bool TryCopySelectedCommandLine()
    {
        if (SearchBox.SelectionLength > 0)
            return false;

        if (_viewModel.SelectedItem is not { HasCommandLine: true } item)
            return false;

        CopyCommandLine(item);
        return true;
    }

    private bool IsCaretPastQuery(bool atEnd)    {
        if (_viewModel.IsResultsActive)
            return true;

        if (SearchBox.SelectionLength > 0)
            return false;

        return atEnd ? SearchBox.CaretIndex >= SearchBox.Text.Length : SearchBox.CaretIndex == 0;
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

    private void CopyCommandLine_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SearchResultItem item)
            CopyCommandLine(item);
    }

    /// <summary>
    /// Puts a result's command line on the clipboard.
    /// <para>
    /// The clipboard is a shared resource that exactly one process owns at a time, and a process
    /// that has it open makes every other write fail; the failure arrives as a COM error rather
    /// than a return value. Nothing about copying is worth interrupting the user for, so a refusal
    /// is reported in the status line and the button simply does not acknowledge.
    /// </para>
    /// </summary>
    private void CopyCommandLine(SearchResultItem item)
    {
        if (item.CommandLine is not { Length: > 0 } command)
            return;

        try
        {
            // Copy=true leaves the text on the clipboard after this process exits, which is the
            // behaviour a user expects of anything they copied.
            System.Windows.Clipboard.SetDataObject(command, copy: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Copying a command line to the clipboard failed");
            _viewModel.ReportStatus("Could not write to the clipboard");
            return;
        }

        FlashCopied(item);
    }

    /// <summary>
    /// Turns the copy glyph into a checkmark for a moment. Writing the clipboard changes nothing
    /// the user can see, so without an acknowledgement the button looks broken.
    /// </summary>
    private void FlashCopied(SearchResultItem item)
    {
        // Only one row may be showing the checkmark, or a second copy leaves the first one stuck.
        if (_copiedItem is { } previous && !ReferenceEquals(previous, item))
            previous.JustCopied = false;

        _copiedItem = item;
        item.JustCopied = true;

        _copiedTimer ??= CreateCopiedTimer();
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    private DispatcherTimer CreateCopiedTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        timer.Tick += (_, _) => ClearCopiedFlash();
        return timer;
    }

    private void ClearCopiedFlash()
    {
        _copiedTimer?.Stop();

        if (_copiedItem is { } item)
            item.JustCopied = false;

        _copiedItem = null;
    }

    private SearchResultItem? _copiedItem;
    private DispatcherTimer? _copiedTimer;
}
