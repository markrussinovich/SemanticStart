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

    /// <summary>
    /// The dictation chord's id. 2 is skipped because the probe below registers HotKeyId + 1 to
    /// tell an id clash apart from a chord clash.
    /// </summary>
    private const int DictationHotKeyId = 3;
    private const int ErrorHotKeyAlreadyRegistered = 1409;

    /// <summary>The hotkey actually registered, which may differ from the configured one.</summary>
    public string? ActiveHotKey { get; private set; }

    /// <summary>The dictation hotkey actually registered, or null if none could be.</summary>
    public string? ActiveDictationHotKey { get; private set; }

    /// <summary>
    /// Raised after registration with the hotkey in force and whether it differs from what the
    /// user asked for. A null hotkey means nothing could be registered at all.
    /// </summary>
    public event Action<string?, bool>? HotKeyRegistered;

    /// <summary>The same report for the dictation chord. Not raised when dictation is off.</summary>
    public event Action<string?, bool>? DictationHotKeyRegistered;

    private readonly Dispatcher _dispatcher;
    private readonly Action _activate;
    private readonly Action? _activateDictation;
    private AppSettings _settings;
    private HwndSource? _source;
    private HotKeySpec? _dictationSpec;

    // Which ids the shell is actually holding for us. Releasing an id that was never taken returns
    // ERROR_HOTKEY_NOT_REGISTERED, which is harmless but makes the ordinary case - dictation off,
    // so nothing to release - indistinguishable from a genuine failure to let a chord go. Tracking
    // it means the only UnregisterHotKey failures that reach the log are real ones.
    private bool _activationHotKeyHeld;
    private bool _dictationHotKeyHeld;
    private bool _disposed;

    public ActivationManager(Dispatcher dispatcher, Action activate, AppSettings settings, Action? activateDictation = null)
    {
        _dispatcher = dispatcher;
        _activate = activate;
        _settings = settings;
        _activateDictation = activateDictation;
    }

    /// <summary>
    /// Whether the dictation chord's main key is still physically down, for push-to-talk. The
    /// modifiers are not polled: a chord is released by letting the whole thing go, and treating an
    /// early release of Win or Alt as the end of the utterance would cut people off mid-word.
    ///
    /// RegisterHotKey reports presses only - there is no WM_HOTKEY on release - so holding a key
    /// cannot be observed through it at all. The honest alternatives are a WH_KEYBOARD_LL hook,
    /// which this class exists to avoid, or asking the keyboard for its current state, which is
    /// what this does. It is a read, not an interception: nothing is inserted into anyone's input
    /// path. The cost is that release is noticed on the next poll rather than at the instant it
    /// happens, and that a key already released before listening began reads as released, which is
    /// the safe direction to be wrong in.
    /// </summary>
    public bool IsDictationHotKeyHeld()
        => _dictationSpec is { } spec && (GetAsyncKeyState(spec.VirtualKey) & 0x8000) != 0;

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

    private void ActivateDictationSoon()
    {
        if (_disposed || _activateDictation is null)
            return;
        _dispatcher.BeginInvoke(_activateDictation, DispatcherPriority.Normal);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey)
        {
            handled = true;
            if ((int)wParam == DictationHotKeyId)
                ActivateDictationSoon();
            else
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
                _activationHotKeyHeld = true;
                ActiveHotKey = candidate;
                Log.Info($"Registered hotkey {candidate} (mod=0x{modifiers:X}, vk=0x{key:X}).");
                HotKeyRegistered?.Invoke(candidate, !string.Equals(candidate, _settings.HotKey, StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(candidate, _settings.HotKey, StringComparison.OrdinalIgnoreCase))
                    Log.Info($"{_settings.HotKey} was unavailable; registered {candidate} instead.");
                RegisterDictationHotKey(handle);
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
                    Release(handle, probeId, candidate);
                    Log.Info($"  ...but {candidate} registered fine under id {probeId}, so id {HotKeyId} was the problem.");
                }
            }
        }

        _activationHotKeyHeld = false;
        ActiveHotKey = null;
        HotKeyRegistered?.Invoke(null, true);
        Log.Info("No hotkey could be registered; use the tray icon to open SemanticStart.");
        RegisterDictationHotKey(handle);
    }

    /// <summary>
    /// Registers the dictation chord on its own id, with its own fallback chain, so a contended
    /// dictation chord costs dictation only and never the activation hotkey.
    /// </summary>
    private void RegisterDictationHotKey(IntPtr handle)
    {
        _dictationHotKeyHeld = false;
        _dictationSpec = null;
        ActiveDictationHotKey = null;

        if (!_settings.DictationEnabled || _activateDictation is null)
            return;

        foreach (var candidate in CandidateDictationHotKeys())
        {
            if (!HotKeySpec.TryParse(candidate, out var spec, out _) || spec is null)
                continue;

            if (RegisterHotKey(handle, DictationHotKeyId, spec.Modifiers | ModNoRepeat, spec.VirtualKey))
            {
                _dictationHotKeyHeld = true;
                _dictationSpec = spec;
                ActiveDictationHotKey = candidate;
                var substituted = !string.Equals(candidate, _settings.DictationHotKey, StringComparison.OrdinalIgnoreCase);
                Log.Info($"Registered dictation hotkey {candidate} (mod=0x{spec.Modifiers:X}, vk=0x{spec.VirtualKey:X}).");
                if (substituted)
                    Log.Info($"{_settings.DictationHotKey} was unavailable; registered {candidate} for dictation instead.");
                DictationHotKeyRegistered?.Invoke(candidate, substituted);
                return;
            }

            Log.Info($"RegisterHotKey failed for dictation chord {candidate}; Win32={Marshal.GetLastWin32Error()}");
        }

        DictationHotKeyRegistered?.Invoke(null, true);
        Log.Info("No dictation hotkey could be registered; start dictation from the overlay instead.");
    }

    /// <summary>
    /// The dictation equivalent of <see cref="CandidateHotKeys"/>. The same Win+Alt+Space exclusion
    /// applies, and none of these may be the activation chord: registering both on one combination
    /// would leave whichever id lost the race permanently dead.
    /// </summary>
    private IEnumerable<string> CandidateDictationHotKeys()
    {
        // Compared in normalized form rather than as typed: "Win+Alt+/" and a chord spelled any
        // other way that parses to the same modifiers and key are the same registration, and the
        // exclusion has to hold for the chord rather than for the string.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Exclude(ActiveHotKey);
        Exclude(_settings.HotKey);

        foreach (var candidate in new[]
                 {
                     _settings.DictationHotKey,
                     AppSettings.DefaultDictationHotKey,
                     "Win+Alt+'",
                     "Win+Ctrl+/",
                     "Win+Alt+M",
                     "Ctrl+Alt+/",
                     "Ctrl+Shift+/",
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(Normalize(candidate)))
                yield return candidate;
        }

        void Exclude(string? chord)
        {
            if (!string.IsNullOrWhiteSpace(chord))
                seen.Add(Normalize(chord));
        }
    }

    /// <summary>The chord in the one spelling every equivalent chord shares.</summary>
    private static string Normalize(string chord)
        => HotKeySpec.TryParse(chord, out var spec, out _) && spec is not null ? spec.Normalized : chord;

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
        {
            if (_activationHotKeyHeld)
                Release(handle, HotKeyId, ActiveHotKey);
            if (_dictationHotKeyHeld)
                Release(handle, DictationHotKeyId, ActiveDictationHotKey);
        }

        _activationHotKeyHeld = false;
        _dictationHotKeyHeld = false;
        _dictationSpec = null;
        ActiveDictationHotKey = null;
    }

    /// <summary>
    /// Hands one chord back to the shell. The result is checked rather than discarded: a hotkey we
    /// believe we hold and cannot release stays registered for the lifetime of the window, so the
    /// next registration pass would fail for a reason nothing else would explain.
    /// </summary>
    private static void Release(IntPtr handle, int id, string? chord)
    {
        if (!UnregisterHotKey(handle, id))
            Log.Info($"UnregisterHotKey failed for {chord ?? "(unknown chord)"} (hwnd=0x{handle:X}, id={id}); Win32={Marshal.GetLastWin32Error()}");
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
