using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SemanticStart.App;

/// <summary>
/// A hotkey field that is set by pressing the combination, showing it as one chip per key.
///
/// It replaces an editable combo box that required the chord to be typed by name. That was the
/// wrong shape for the job twice over: it asked the user to know the spelling of a key ("Win+Alt+
/// Space", but "PrintScreen" or "Prior"?), and it rendered nothing at all when the saved chord was
/// not one of the presets, because applying an editable ComboBox's template re-coerces Text from
/// SelectedItem - which is null - and wipes whatever the constructor put there.
/// </summary>
public partial class HotKeyRecorder : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty HotKeyProperty = DependencyProperty.Register(
        nameof(HotKey),
        typeof(string),
        typeof(HotKeyRecorder),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnHotKeyChanged));

    /// <summary>Raised only when a press produced a usable chord, so callers never see partial input.</summary>
    public event EventHandler? HotKeyChanged;

    /// <summary>Raised when a press was rejected, carrying the reason to show the user.</summary>
    public event EventHandler<string>? HotKeyRejected;

    private bool _recording;

    public HotKeyRecorder()
    {
        InitializeComponent();
        Loaded += (_, _) => Render();
    }

    public string HotKey
    {
        get => (string)GetValue(HotKeyProperty);
        set => SetValue(HotKeyProperty, value);
    }

    private static void OnHotKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((HotKeyRecorder)d).Render();

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        StartRecording();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        StopRecording();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (!_recording)
            return;

        // Tab has to keep moving focus, or the field becomes a keyboard trap that can only be
        // left with the mouse. Escape abandons the edit and keeps the existing chord.
        if (e.Key == Key.Tab)
            return;

        if (e.Key == Key.Escape)
        {
            StopRecording();
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        // Alt-modified presses arrive as Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Modifiers alone are the normal first half of every chord, so they are swallowed to keep
        // the field quiet until a main key lands.
        if (HotKeyCapture.IsModifierOnly(key))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;

        if (HotKeyCapture.TryCapture(key, Keyboard.Modifiers, out var chord, out var error) && chord is not null)
        {
            HotKey = chord;
            StopRecording();
            HotKeyChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (error is not null)
            HotKeyRejected?.Invoke(this, error);
    }

    private void StartRecording()
    {
        _recording = true;
        Surface.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        Render();
    }

    private void StopRecording()
    {
        _recording = false;
        Surface.BorderBrush = (System.Windows.Media.Brush)FindResource("SearchBoxBorderBrush");
        Render();
    }

    private void Render()
    {
        if (Chips is null || Prompt is null)
            return;

        var chips = HotKeyCapture.Chips(HotKey);

        // While recording, the prompt replaces the chips so it is obvious the next press is being
        // captured rather than typed into something.
        var showPrompt = _recording || chips.Count == 0;
        Prompt.Visibility = showPrompt ? Visibility.Visible : Visibility.Collapsed;
        Chips.Visibility = showPrompt ? Visibility.Collapsed : Visibility.Visible;
        Chips.ItemsSource = chips;
    }
}
