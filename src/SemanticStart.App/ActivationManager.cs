using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SemanticStart.App;

/// <summary>
/// Owns the one way the overlay is opened: a global hotkey registered with the shell.
///
/// An earlier version also offered to take over the Start key with a WH_KEYBOARD_LL hook. That is
/// gone. A low-level hook sits in the input path of every keystroke on the machine, cannot see
/// input while an elevated window has focus, is silently dropped when it exceeds
/// LowLevelHooksTimeout, and trips security software - a large, permanently load-bearing risk to
/// the user's keyboard in exchange for saving one modifier. RegisterHotKey has none of those
/// properties: the shell delivers the message or it does not.
/// </summary>
public sealed class ActivationManager : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const int ModAlt = 0x0001;
    private const int ModNoRepeat = 0x4000;
    private const int HotKeyId = 1;
    private const int ErrorHotKeyAlreadyRegistered = 1409;

    /// <summary>The hotkey actually registered, which may differ from the configured one.</summary>
    public string? ActiveHotKey { get; private set; }

    /// <summary>
    /// Raised after registration with the hotkey in force and whether it differs from what the
    /// user asked for. A null hotkey means nothing could be registered at all.
    /// </summary>
    public event Action<string?, bool>? HotKeyRegistered;

    private readonly Dispatcher _dispatcher;
    private readonly Action _activate;
    private AppSettings _settings;
    private HwndSource? _source;
    private bool _disposed;

    public ActivationManager(Dispatcher dispatcher, Action activate, AppSettings settings)
    {
        _dispatcher = dispatcher;
        _activate = activate;
        _settings = settings;
    }

    public void Start()
    {
        _source = new HwndSource(new HwndSourceParameters("SemanticStartHotKeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0x800000,
        });
        _source.AddHook(WndProc);
        // ApplySettings registers the hotkey; calling it here as well produced a duplicate
        // registration pass in the log and left the first pass's hotkey owned by this thread.
        ApplySettings(_settings);
    }

    /// <summary>
    /// Releases the global hotkey so the chord can be typed into the recorder instead of
    /// activating the overlay. Without this the one shortcut a user is most likely to press while
    /// editing - the one already assigned - is swallowed by the OS and delivered to us as an
    /// activation, so the field never sees it and the overlay appears on top of the settings
    /// window.
    /// </summary>
    public void SuspendForCapture() => UnregisterHotKey();

    /// <summary>Restores whatever the current settings ask for after <see cref="SuspendForCapture"/>.</summary>
    public void ResumeAfterCapture() => ApplySettings(_settings);

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        RegisterConfiguredHotKey();
    }

    public void Dispose()
    {
        _disposed = true;
        UnregisterHotKey();
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
    }

    private void ActivateSoon()
    {
        if (_disposed)
            return;
        _dispatcher.BeginInvoke(_activate, DispatcherPriority.Normal);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey)
        {
            handled = true;
            ActivateSoon();
        }
        return IntPtr.Zero;
    }

    private void RegisterConfiguredHotKey()
    {
        if (_source?.Handle is not { } handle || handle == IntPtr.Zero)
            return;

        UnregisterHotKey();

        // A hotkey is owned by whichever process registers it first, so a contended combination is
        // frequently already taken. Failing here used to be logged and otherwise ignored, which
        // left the app running with no way to open it. Fall back to the first combination that is
        // actually free and report what we ended up with so the UI can tell the user.
        foreach (var candidate in CandidateHotKeys())
        {
            var (modifiers, key) = ParseHotKey(candidate);
            if (RegisterHotKey(handle, HotKeyId, modifiers | ModNoRepeat, key))
            {
                ActiveHotKey = candidate;
                Log.Info($"Registered hotkey {candidate} (mod=0x{modifiers:X}, vk=0x{key:X}).");
                HotKeyRegistered?.Invoke(candidate, !string.Equals(candidate, _settings.HotKey, StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(candidate, _settings.HotKey, StringComparison.OrdinalIgnoreCase))
                    Log.Info($"{_settings.HotKey} was unavailable; registered {candidate} instead.");
                return;
            }

            var error = Marshal.GetLastWin32Error();
            Log.Info($"RegisterHotKey failed for {candidate} (mod=0x{modifiers:X}, vk=0x{key:X}, hwnd=0x{handle:X}, id={HotKeyId}); Win32={error}");

            // ERROR_HOTKEY_ALREADY_REGISTERED is also returned when the *id* is in use for this
            // window, which is indistinguishable from the combination being taken by another
            // process. Retry once on a fresh id to tell the two apart.
            if (error == ErrorHotKeyAlreadyRegistered)
            {
                var probeId = HotKeyId + 1;
                if (RegisterHotKey(handle, probeId, modifiers | ModNoRepeat, key))
                {
                    UnregisterHotKey(handle, probeId);
                    Log.Info($"  ...but {candidate} registered fine under id {probeId}, so id {HotKeyId} was the problem.");
                }
            }
        }

        ActiveHotKey = null;
        HotKeyRegistered?.Invoke(null, true);
        Log.Info("No hotkey could be registered; use the tray icon to open SemanticStart.");
    }

    /// <summary>
    /// The configured hotkey first, then progressively less contended combinations. None of these
    /// may be Win+Alt+Space: that is PowerToys' Command Palette, and falling back onto it would
    /// reintroduce the very collision the default was changed to avoid.
    /// </summary>
    private IEnumerable<string> CandidateHotKeys()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in new[]
                 {
                     _settings.HotKey,
                     AppSettings.DefaultHotKey,
                     "Win+Alt+,",
                     "Win+Alt+;",
                     "Win+Ctrl+G",
                     "Win+Alt+X",
                     "Ctrl+Alt+.",
                     "Ctrl+Shift+.",
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
                yield return candidate;
        }
    }

    private void UnregisterHotKey()
    {
        if (_source?.Handle is { } handle && handle != IntPtr.Zero)
            UnregisterHotKey(handle, HotKeyId);
    }

    /// <summary>
    /// Parses a chord for registration. Validation lives in <see cref="HotKeySpec"/> so the text
    /// the user typed is judged by exactly the rules that will later be used to register it.
    /// Anything unparseable falls back to the default rather than to a silently different chord.
    /// </summary>
    private static (int Modifiers, int Key) ParseHotKey(string hotKey)
    {
        if (HotKeySpec.TryParse(hotKey, out var spec, out _) && spec is not null)
            return (spec.Modifiers, spec.VirtualKey);

        Log.Info($"Could not parse hotkey '{hotKey}'; falling back to {AppSettings.DefaultHotKey}.");
        return HotKeySpec.TryParse(AppSettings.DefaultHotKey, out var fallback, out _) && fallback is not null
            ? (fallback.Modifiers, fallback.VirtualKey)
            : (ModAlt, KeyInterop.VirtualKeyFromKey(Key.OemPeriod));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
