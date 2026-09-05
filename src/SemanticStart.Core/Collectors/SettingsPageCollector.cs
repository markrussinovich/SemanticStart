using System.Runtime.CompilerServices;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Collectors;

public sealed class SettingsPageCollector : IEntityCollector
{
    private static readonly IReadOnlyList<SettingsPage> Pages =
    [
        new("Display", "ms-settings:display", "Change monitor layout, brightness, scale, resolution, and advanced display settings."),
        new("Scale", "ms-settings:display-advanced", "Review advanced display details and related scaling options."),
        new("Night Light", "ms-settings:nightlight", "Reduce blue light and schedule warmer display colors at night."),
        new("Sound", "ms-settings:sound", "Manage volume, output, input, and sound device settings."),
        new("Volume Mixer", "ms-settings:apps-volume", "Set per-app volume levels and audio input or output devices."),
        new("Sound Output Devices", "ms-settings:sound-devices", "Manage speakers, headphones, microphones, and other sound devices."),
        new("Notifications", "ms-settings:notifications", "Control app notifications, banners, sounds, and notification center behavior."),
        new("Focus Assist", "ms-settings:quiethours", "Reduce distractions by controlling focus and do-not-disturb rules."),
        new("Power & Battery", "ms-settings:powersleep", "Manage power mode, battery usage, and sleep settings."),
        new("Battery Saver", "ms-settings:batterysaver", "Configure battery saver and power-saving behavior."),
        new("Storage", "ms-settings:storagesense", "View disk usage and manage storage across apps, files, and drives."),
        new("Storage Sense", "ms-settings:storagepolicies", "Automatically clean temporary files, recycle bin, and cloud-backed content."),
        new("Disk Cleanup Recommendations", "ms-settings:storagerecommendations", "Find cleanup recommendations for temporary files, large files, and unused apps."),
        new("Nearby Sharing", "ms-settings:crossdevice", "Share files and links with nearby Windows devices."),
        new("Multitasking", "ms-settings:multitasking", "Configure snap windows, desktops, Alt+Tab, and multitasking behavior."),
        new("Activation", "ms-settings:activation", "Check Windows activation status and change product key."),
        new("Troubleshoot", "ms-settings:troubleshoot", "Find and run troubleshooters for common Windows problems."),
        new("Other Troubleshooters", "ms-settings:troubleshoot-other", "Run individual troubleshooters for network, audio, printer, update, and more."),
        new("Recovery", "ms-settings:recovery", "Reset this PC, advanced startup, and recovery options."),
        new("Projecting to This PC", "ms-settings:project", "Configure wireless projection and Miracast receiving settings."),
        new("Remote Desktop", "ms-settings:remotedesktop", "Enable and configure Remote Desktop access to this PC."),
        new("Clipboard", "ms-settings:clipboard", "Manage clipboard history, sync, and suggested actions."),
        new("Bluetooth & Devices", "ms-settings:bluetooth", "Manage Bluetooth, paired devices, and device discovery."),
        new("Devices", "ms-settings:connecteddevices", "Manage connected peripherals and wireless displays."),
        new("Printers & Scanners", "ms-settings:printers", "Add, remove, and manage printers, scanners, queues, and preferences."),
        new("Mouse", "ms-settings:mousetouchpad", "Adjust mouse buttons, pointer speed, wheel, and related pointer settings."),
        new("Touchpad", "ms-settings:devices-touchpad", "Configure touchpad gestures, taps, sensitivity, and scrolling."),
        new("Pen & Windows Ink", "ms-settings:pen", "Configure pen shortcuts, handwriting, and Windows Ink behavior."),
        new("AutoPlay", "ms-settings:autoplay", "Choose default actions for removable drives, memory cards, and devices."),
        new("USB", "ms-settings:usb", "Manage USB connection notifications and battery saver behavior."),
        new("Phone Link", "ms-settings:mobile-devices", "Manage mobile device connections and Phone Link integration."),
        new("Network Status", "ms-settings:network-status", "View network connection status and network troubleshooting entry points."),
        new("Wi-Fi", "ms-settings:network-wifi", "Manage Wi-Fi networks, adapters, and wireless properties."),
        new("Ethernet", "ms-settings:network-ethernet", "Manage wired network adapters and Ethernet connection properties."),
        new("VPN", "ms-settings:network-vpn", "Add, remove, and connect VPN profiles."),
        new("Airplane Mode", "ms-settings:network-airplanemode", "Turn airplane mode and wireless radios on or off."),
        new("Mobile Hotspot", "ms-settings:network-mobilehotspot", "Share this PC's internet connection as a hotspot."),
        new("Proxy", "ms-settings:network-proxy", "Configure automatic and manual proxy server settings."),
        new("Dial-up", "ms-settings:network-dialup", "Set up and manage dial-up network connections."),
        new("Personalization", "ms-settings:personalization", "Customize Windows appearance, themes, background, and colors."),
        new("Background", "ms-settings:personalization-background", "Change desktop wallpaper, slideshow, and background fit."),
        new("Colors", "ms-settings:personalization-colors", "Choose light or dark mode, accent color, and transparency effects."),
        new("Themes", "ms-settings:themes", "Select, save, and manage Windows themes."),
        new("Lock Screen", "ms-settings:lockscreen", "Customize lock screen background, status apps, and sign-in screen image."),
        new("Start", "ms-settings:personalization-start", "Configure Start menu layout, recommendations, and folders."),
        new("Taskbar", "ms-settings:taskbar", "Customize taskbar alignment, icons, behaviors, and system tray."),
        new("Fonts", "ms-settings:fonts", "Preview, install, and manage fonts."),
        new("Installed Apps", "ms-settings:appsfeatures", "Uninstall, move, reset, and manage installed applications."),
        new("Default Apps", "ms-settings:defaultapps", "Choose default apps by file type, protocol, or application."),
        new("Optional Features", "ms-settings:optionalfeatures", "Add, remove, and manage Windows optional features."),
        new("Startup Apps", "ms-settings:startupapps", "Choose which apps start automatically when signing in."),
        new("Accounts", "ms-settings:accounts", "Manage user accounts, Microsoft account, work access, and sign-in settings."),
        new("Your Info", "ms-settings:yourinfo", "View and manage account picture and local or Microsoft account information."),
        new("Email & Accounts", "ms-settings:emailandaccounts", "Manage accounts used by email, calendar, contacts, and apps."),
        new("Sign-in Options", "ms-settings:signinoptions", "Configure password, PIN, fingerprint, face, security key, and dynamic lock."),
        new("Windows Hello Face", "ms-settings:signinoptions-launchfaceenrollment", "Start Windows Hello face recognition setup."),
        new("Family", "ms-settings:family-group", "Manage family members, parental controls, and family safety settings."),
        new("Other Users", "ms-settings:otherusers", "Add and manage local users, guests, and kiosk access."),
        new("Windows Backup", "ms-settings:backup", "Back up files, apps, preferences, and credentials to your Microsoft account."),
        new("Date & Time", "ms-settings:dateandtime", "Set time zone, clock, automatic time, and additional clocks."),
        new("Region", "ms-settings:regionformatting", "Choose country, regional format, and locale formats."),
        new("Language", "ms-settings:regionlanguage", "Add languages and configure Windows display language."),
        new("Typing", "ms-settings:typing", "Configure typing suggestions, autocorrect, touch keyboard, and hardware keyboard options."),
        new("Speech", "ms-settings:speech", "Manage speech language, voice recognition, and text-to-speech voices."),
        new("Xbox Game Bar", "ms-settings:gaming-gamebar", "Configure Xbox Game Bar shortcuts, capture controls, and widgets."),
        new("Game Mode", "ms-settings:gaming-gamemode", "Optimize Windows for gameplay and game performance."),
        new("Text Size", "ms-settings:easeofaccess-display", "Increase text size and adjust display accessibility options."),
        new("Visual Effects", "ms-settings:easeofaccess-visualeffects", "Configure animations, transparency, scrollbars, and notification timing."),
        new("Mouse Pointer", "ms-settings:easeofaccess-mousepointer", "Change mouse pointer size, color, and touch feedback."),
        new("Magnifier", "ms-settings:easeofaccess-magnifier", "Zoom the screen and configure Magnifier reading and tracking."),
        new("Color Filters", "ms-settings:easeofaccess-colorfilter", "Apply color filters for color blindness or visual comfort."),
        new("Contrast Themes", "ms-settings:easeofaccess-highcontrast", "Use high contrast themes for improved readability."),
        new("Narrator", "ms-settings:easeofaccess-narrator", "Configure the built-in screen reader and voice settings."),
        new("Accessibility Audio", "ms-settings:easeofaccess-audio", "Make audio easier to hear and show audio alerts visually."),
        new("Captions", "ms-settings:easeofaccess-closedcaptioning", "Customize caption style for videos and system captions."),
        new("Windows Speech Recognition", "ms-settings:easeofaccess-speechrecognition", "Control Windows with speech recognition."),
        new("Eye Control", "ms-settings:easeofaccess-eyecontrol", "Configure eye tracking control for supported devices."),
        new("Privacy & Security", "ms-settings:privacy", "Manage privacy permissions, security settings, and diagnostic controls."),
        new("Windows Security", "ms-settings:windowsdefender", "Open virus protection, firewall, account, device, and app security status."),
        new("Find My Device", "ms-settings:findmydevice", "Locate this device and manage find-my-device settings."),
        new("For Developers", "ms-settings:developers", "Enable developer mode, device portal, and development-related settings."),
        new("Device Encryption", "ms-settings:deviceencryption", "View and manage device encryption when supported by hardware."),
        new("BitLocker", "ms-settings:privacy-deviceencryption", "Review encryption and BitLocker-related device protection settings."),
        new("Location Privacy", "ms-settings:privacy-location", "Control location access for Windows, apps, and services."),
        new("Camera Privacy", "ms-settings:privacy-webcam", "Control camera access for apps and Windows features."),
        new("Microphone Privacy", "ms-settings:privacy-microphone", "Control microphone access for apps and Windows features."),
        new("Windows Update", "ms-settings:windowsupdate", "Check for updates and manage Windows Update status."),
        new("Update History", "ms-settings:windowsupdate-history", "View installed quality, driver, definition, and feature updates."),
        new("Advanced Windows Update Options", "ms-settings:windowsupdate-options", "Configure active hours, optional updates, restart options, and update policies."),
        new("Windows Insider Program", "ms-settings:windowsinsider", "Join or manage Windows Insider preview builds."),
        new("Delivery Optimization", "ms-settings:delivery-optimization", "Control update downloads from Microsoft, local network, or internet peers."),
    ];

    public string Source => "mssettings";

    public bool IsSupported => OperatingSystem.IsWindows();

    public async IAsyncEnumerable<Entity> CollectAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        if (!IsSupported)
            yield break;

        foreach (var page in Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entity = new Entity
            {
                Id = EntityId.Create(Source, page.Uri),
                Kind = EntityKind.SettingsPage,
                DisplayName = page.Name,
                LaunchKind = LaunchKind.Uri,
                LaunchTarget = page.Uri,
                Source = Source,
                RawMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["description"] = page.Description,
                },
            };

            yield return CollectorEntity.WithContentHash(entity);
        }
    }

    private sealed record SettingsPage(string Name, string Uri, string Description);
}
