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
    /// Chosen against three constraints. Every bare Win+&lt;letter&gt; is registered by the shell
    /// itself, so no app can take one. Xbox Game Bar squats on much of the Win+Alt+&lt;letter&gt;
    /// family (D, F, G, R, T, W) on a stock Windows 11 install, and Win+Shift+S is Screen Snip
    /// while Win+Ctrl+S is Speech Recognition. Modifier+Space is also the established launcher
    /// idiom (Spotlight, Alfred, Raycast, PowerToys Run), and Win, Alt, and Space sit next to each
    /// other at the bottom left, so one hand can hit it without the pinky and the letter fighting
    /// over the same finger.
    /// </summary>
    public const string DefaultHotKey = "Win+Alt+Space";

    /// <summary>Pre-1.0 defaults, migrated away from on load.</summary>
    internal static readonly string[] LegacyDefaultHotKeys = ["Alt+Space", "Win+Shift+S", "Win+Alt+S"];

    public string HotKey { get; init; } = DefaultHotKey;
    public bool TakeOverStartKey { get; init; }
    public bool AllowOnlineEnrichment { get; init; }
    public int ResultLimit { get; init; } = 8;
    public bool LaunchAtLogin { get; init; }
    public LocalLlmMode LocalLlmMode { get; init; } = LocalLlmMode.Auto;
    public string LocalLlmEndpointBaseUrl { get; init; } = string.Empty;
    public string LocalLlmModelName { get; init; } = LocalLlmOptions.DefaultModelName;

    public LocalLlmOptions ToLocalLlmOptions() => new()
    {
        Mode = LocalLlmMode,
        EndpointBaseUrl = LocalLlmEndpointBaseUrl,
        ModelName = LocalLlmModelName,
    };
}

public sealed class AppSettingsService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
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

            return settings with
            {
                HotKey = hotKey,
                ResultLimit = Math.Clamp(settings.ResultLimit, 3, 20),
                LaunchAtLogin = IsLaunchAtLoginEnabled(),
                LocalLlmEndpointBaseUrl = settings.LocalLlmEndpointBaseUrl?.Trim() ?? string.Empty,
                LocalLlmModelName = string.IsNullOrWhiteSpace(settings.LocalLlmModelName)
                    ? LocalLlmOptions.DefaultModelName
                    : settings.LocalLlmModelName.Trim(),
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load settings");
            return new AppSettings { LaunchAtLogin = IsLaunchAtLoginEnabled() };
        }
    }

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
            return !string.IsNullOrWhiteSpace(key?.GetValue(RunValueName) as string);
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
                var exe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exe))
                    key.SetValue(RunValueName, $"\"{exe}\"");
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
}
