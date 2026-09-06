using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SemanticStart.App;

public sealed class ActivationManager : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int ModAlt = 0x0001;
    private const int ModControl = 0x0002;
    private const int ModShift = 0x0004;
    private const int ModWin = 0x0008;
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
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private readonly LowLevelProc _keyboardProc;
    private readonly LowLevelProc _mouseProc;
    private readonly DispatcherTimer _watchdog;
    private bool _disposed;
    private bool _winHeld;
    private bool _contaminated;
    private bool _mouseActivity;
    private DateTimeOffset _lastHookEvent = DateTimeOffset.MinValue;

    public ActivationManager(Dispatcher dispatcher, Action activate, AppSettings settings)
    {
        _dispatcher = dispatcher;
        _activate = activate;
        _settings = settings;
        // Keep strong references to these delegates; otherwise GC can collect them and Windows
        // will later call an invalid low-level hook callback.
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
        _watchdog = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        _watchdog.Tick += (_, _) => EnsureHooksHealthy();
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
        _watchdog.Start();
    }

    /// <summary>
    /// Releases the global hotkey and the Start-key hook so the chord can be typed into the
    /// recorder instead of activating the overlay. Without this the one shortcut a user is most
    /// likely to press while editing - the one already assigned - is swallowed by the OS and
    /// delivered to us as an activation, so the field never sees it and the overlay appears on
    /// top of the settings window.
    /// </summary>
    public void SuspendForCapture()
    {
        UnregisterHotKey();
        UninstallHooks();
    }

    /// <summary>Restores whatever the current settings ask for after <see cref="SuspendForCapture"/>.</summary>
    public void ResumeAfterCapture() => ApplySettings(_settings);

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        RegisterConfiguredHotKey();
        if (_settings.TakeOverStartKey)
            InstallHooks();
        else
            UninstallHooks();
    }

    public void Dispose()
    {
        _disposed = true;
        _watchdog.Stop();
        UnregisterHotKey();
        UninstallHooks();
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
    /// The configured hotkey first, then progressively less contended combinations.
    /// </summary>
    private IEnumerable<string> CandidateHotKeys()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in new[]
                 {
                     _settings.HotKey,
                     "Win+Alt+Space",
                     "Win+Alt+S",
                     "Win+Ctrl+G",
                     "Win+Alt+X",
                     "Ctrl+Alt+Space",
                     "Ctrl+Alt+S",
                     "Ctrl+Shift+Space",
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
            : (ModAlt, KeyInterop.VirtualKeyFromKey(Key.Space));
    }

    private void EnsureHooksHealthy()
    {
        if (!_settings.TakeOverStartKey || _disposed)
            return;

        if (_keyboardHook == IntPtr.Zero || DateTimeOffset.Now - _lastHookEvent > TimeSpan.FromMinutes(5))
            InstallHooks();
    }

    private void InstallHooks()
    {
        if (_disposed)
            return;

        UninstallHooks();
        try
        {
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, GetModuleHandle(null), 0);
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
            _lastHookEvent = DateTimeOffset.Now;
            if (_keyboardHook == IntPtr.Zero)
                Log.Info($"Keyboard hook install failed; Win32={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Hook install failed");
            UninstallHooks();
        }
    }

    private void UninstallHooks()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        _winHeld = false;
        _contaminated = false;
        _mouseActivity = false;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Fail-open: any hook failure must pass input to Windows so the real Start menu works.
        try
        {
            if (nCode < 0 || _disposed)
                return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            _lastHookEvent = DateTimeOffset.Now;
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var isDown = wParam == WmKeyDown || wParam == WmSysKeyDown;
            var isUp = wParam == WmKeyUp || wParam == WmSysKeyUp;
            var isWin = info.vkCode is VkLWin or VkRWin;

            if (isDown && isWin)
            {
                _winHeld = true;
                _contaminated = false;
                _mouseActivity = false;
            }
            else if (isDown && _winHeld)
            {
                _contaminated = true;
            }
            else if (isUp && isWin)
            {
                var solo = _winHeld && !_contaminated && !_mouseActivity;
                _winHeld = false;
                _contaminated = false;
                _mouseActivity = false;
                if (solo)
                {
                    // Windows opens Start on solo Win keyup; swallowing keydown alone leaves the
                    // system in a bad state and does not reliably suppress Start.
                    ActivateSoon();
                    return new IntPtr(1);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Keyboard hook callback failed");
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && _winHeld && (wParam == WmMouseMove || wParam == WmLButtonDown || wParam == WmRButtonDown || wParam == WmMButtonDown))
                _mouseActivity = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Mouse hook callback failed");
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
