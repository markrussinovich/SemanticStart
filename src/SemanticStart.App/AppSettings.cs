using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using SemanticStart.Core;
using SemanticStart.Core.Synthesis;

namespace SemanticStart.App;

public sealed record AppSettings
{
    /// <summary>
    /// Chosen against four constraints. Every bare Win+&lt;letter&gt; is registered by the shell
    /// itself, so no app can take one. Xbox Game Bar squats on much of the Win+Alt+&lt;letter&gt;
    /// family (D, F, G, R, T, W) on a stock Windows 11 install, and Win+Shift+S is Screen Snip
    /// while Win+Ctrl+S is Speech Recognition. Modifier+Space is the established launcher idiom -
    /// which is exactly the problem, because PowerToys' Command Palette already uses Win+Alt+Space
    /// and a hotkey belongs to whichever process registers it first, so on a machine with PowerToys
    /// installed we would simply never open. The period keeps the same one-handed bottom-row shape
    /// as Space without the collision.
    /// </summary>
    internal const string DefaultHotKey = "Win+Alt+.";

    /// <summary>
    /// Defaults we have shipped before, migrated away from on load. Win+Alt+Space is here because
    /// it collides with PowerToys' Command Palette, which is the reason it stopped being default.
    /// </summary>
    internal static readonly string[] LegacyDefaultHotKeys = ["Alt+Space", "Win+Shift+S", "Win+Alt+S", "Win+Alt+Space"];

    /// <summary>
    /// Opens the overlay and starts listening. Chosen on the same grounds as
    /// <see cref="DefaultHotKey"/>: it keeps the Win+Alt+&lt;punctuation&gt; shape, which is the part
    /// of the keyboard the shell and Game Bar have left alone, and sits next to the activation
    /// chord so the two are one key apart.
    /// </summary>
    internal const string DefaultDictationHotKey = "Win+Alt+/";

    public string HotKey { get; init; } = DefaultHotKey;

    /// <summary>
    /// Whether the dictation hotkey is registered and the speech model is kept warm. Off until
    /// asked for: turning it on is what authorises the ~665 MB model download, in the same way the
    /// first index build is what authorises the embedding model download.
    /// </summary>
    public bool DictationEnabled { get; init; }

    public string DictationHotKey { get; init; } = DefaultDictationHotKey;

    /// <summary>
    /// How much silence ends an utterance. Clamped to 100-2000 ms on load. 220 ms is short enough
    /// that a finished phrase searches immediately and long enough to survive the pause in the
    /// middle of "visual... studio".
    /// </summary>
    public int DictationTrailingSilenceMilliseconds { get; init; } = 220;

    /// <summary>
    /// Hold the hotkey to talk instead of pressing it once to start and again to stop. Off by
    /// default because release detection is necessarily approximate - see
    /// <see cref="ActivationManager.IsDictationHotKeyHeld"/> - whereas toggling is exact.
    /// </summary>
    public bool DictationPushToTalk { get; init; }

    /// <summary>
    /// On by default, matching first-run setup. Most installed programs describe themselves with
    /// nothing but their own name, so without online documentation many tools can be found only
    /// by users who already know what they are called. Only entity names are sent, and only while
    /// indexing; search queries never leave the machine.
    /// </summary>
    public bool AllowOnlineEnrichment { get; init; } = true;
    public int ResultLimit { get; init; } = 8;
    public bool LaunchAtLogin { get; init; }

    /// <summary>
    /// How long to wait for typing to stop before searching. See OverlayViewModel.DebounceSearch
    /// for why a settled query is worth waiting for.
    /// </summary>
    public int SearchDebounceMilliseconds { get; init; } = 500;

    /// <summary>
    /// Whether the user has been through first-run setup. The first index build is the expensive,
    /// hard-to-undo one - it is what decides whether online documentation was used at all - so that
    /// choice has to be offered before it starts, not after.
    /// </summary>
    public bool SetupCompleted { get; init; }

    /// <summary>
    /// Whether the index keeps itself current without being asked: shortly after startup, when the
    /// Start Menu changes, and on an interval after that.
    ///
    /// On by default, because the alternative is an index that silently rots. Installing a program
    /// is exactly the moment a user is most likely to reach for a launcher to run it, and until
    /// something rescans, the one thing they just installed is the one thing that cannot be found.
    /// </summary>
    public bool BackgroundRefresh { get; init; } = true;

    /// <summary>
    /// How long between background refreshes. Clamped to 15 minutes and one day on load.
    ///
    /// An hour is cheap because the refresh is incremental: discovery runs, content hashes are
    /// compared, and everything unchanged is skipped before any documentation is gathered or any
    /// text is embedded. What it costs is a pass over the collectors, which is seconds.
    /// </summary>
    public int RefreshIntervalMinutes { get; init; } = 60;
}

public sealed class AppSettingsService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string RunValueName = "SemanticStart";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppSettings Load()
    {
        try
        {
            AppPaths.EnsureCreated();
            if (!File.Exists(AppPaths.SettingsFile))
                return new AppSettings { LaunchAtLogin = IsLaunchAtLoginEnabled() };

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions) ?? new AppSettings();

            // Anyone still on an old default is moved to the current one. A user who deliberately
            // picked one of those combinations loses that choice here, which is the right trade:
            // Alt+Space breaks the system menu on every window and is very unlikely to be intended.
            var hotKey = string.IsNullOrWhiteSpace(settings.HotKey)
                || AppSettings.LegacyDefaultHotKeys.Contains(settings.HotKey, StringComparer.OrdinalIgnoreCase)
                    ? AppSettings.DefaultHotKey
                    : settings.HotKey;

            var dictationHotKey = string.IsNullOrWhiteSpace(settings.DictationHotKey)
                ? AppSettings.DefaultDictationHotKey
                : settings.DictationHotKey;

            return settings with
            {
                HotKey = hotKey,
                DictationHotKey = dictationHotKey,
                DictationTrailingSilenceMilliseconds = Math.Clamp(settings.DictationTrailingSilenceMilliseconds, 100, 2000),
                ResultLimit = Math.Clamp(settings.ResultLimit, 3, 20),
                SearchDebounceMilliseconds = Math.Clamp(settings.SearchDebounceMilliseconds, 0, 2000),
                RefreshIntervalMinutes = Math.Clamp(settings.RefreshIntervalMinutes, 15, 24 * 60),
                LaunchAtLogin = IsLaunchAtLoginEnabled(),
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load settings");
            return new AppSettings { LaunchAtLogin = IsLaunchAtLoginEnabled() };
        }
    }

    /// <summary>
    /// Whether anything has ever been written. False on a first run, where Save is a real action
    /// even with nothing edited: it is what turns the defaults on screen into a stored choice.
    /// </summary>
    public bool HasSavedSettings => File.Exists(AppPaths.SettingsFile);

    public void Save(AppSettings settings)
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, JsonOptions));
            SetLaunchAtLogin(settings.LaunchAtLogin);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings");
        }
    }

    private static bool IsLaunchAtLoginEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var registered = key?.GetValue(RunValueName) as string;
            var executable = CurrentExecutablePath();
            if (string.IsNullOrWhiteSpace(registered) || string.IsNullOrWhiteSpace(executable))
                return false;

            using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: false);
            if (!IsStartupApproved(approved?.GetValue(RunValueName)))
                return false;

            var expected = BuildStartupCommand(executable);
            if (string.Equals(registered, expected, StringComparison.OrdinalIgnoreCase))
                return true;

            // Migrate registrations written before startup launches carried an explicit marker.
            if (string.Equals(registered, QuoteArgument(executable), StringComparison.OrdinalIgnoreCase))
            {
                SetLaunchAtLogin(enabled: true);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read startup registration");
            return false;
        }
    }

    private static void SetLaunchAtLogin(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                var exe = CurrentExecutablePath();
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    key.SetValue(RunValueName, BuildStartupCommand(exe));

                    // Task Manager records disabled startup entries separately from the Run key.
                    // Re-enabling the checkbox is an explicit request to clear that veto.
                    using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: true);
                    approved?.DeleteValue(RunValueName, throwOnMissingValue: false);
                }
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update startup registration");
        }
    }

    internal static string BuildStartupCommand(string executablePath)
        => $"{QuoteArgument(executablePath)} --startup";

    internal static bool IsStartupApproved(object? value)
        => value is not byte[] { Length: > 0 } bytes || bytes[0] != 3;

    private static string? CurrentExecutablePath()
        => Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;

    private static string QuoteArgument(string value)
        => $"\"{value.Replace("\"", "\\\"")}\"";
}
