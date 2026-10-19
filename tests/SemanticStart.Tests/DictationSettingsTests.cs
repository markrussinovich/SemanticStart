using System.Text.Json;
using SemanticStart.App;
using SemanticStart.Core;
using SemanticStart.Core.Speech;

namespace SemanticStart.Tests;

/// <summary>
/// Dictation is opt-in, and the settings are what that opt-in is made of: whether the hotkey is
/// registered at all, which chord it is, and how long a pause ends a phrase. The defaults are
/// asserted here because a settings file written before dictation existed has none of these keys,
/// and the behaviour that file gets is whatever the record says.
/// </summary>
public sealed class DictationSettingsTests
{
    [Fact]
    public void DictationIsOffUntilAskedFor()
    {
        // Turning it on is what authorises the model download, so the default cannot be true.
        Assert.False(new AppSettings().DictationEnabled);
    }

    [Fact]
    public void TheDictationChordDefaultsToOneKeyFromTheActivationChord()
    {
        Assert.Equal("Win+Alt+/", AppSettings.DefaultDictationHotKey);
        Assert.Equal(AppSettings.DefaultDictationHotKey, new AppSettings().DictationHotKey);
        Assert.NotEqual(AppSettings.DefaultHotKey, AppSettings.DefaultDictationHotKey);
    }

    /// <summary>
    /// The settings default and the detector default are the same number for the same reason, and
    /// if they drift the value shown in settings stops describing what the detector does.
    /// </summary>
    [Fact]
    public void TrailingSilenceDefaultsToTheDetectorsOwnDefault()
    {
        Assert.Equal(220, new AppSettings().DictationTrailingSilenceMilliseconds);
        Assert.Equal(
            SpeechEndpointDetector.DefaultTrailingSilenceMilliseconds,
            new AppSettings().DictationTrailingSilenceMilliseconds);
    }

    [Fact]
    public void PushToTalkIsOffByDefault()
    {
        // Toggling is exact; release detection is approximate, so the exact behaviour is default.
        Assert.False(new AppSettings().DictationPushToTalk);
    }

    /// <summary>
    /// The file every existing user has: no dictation keys at all. It must read as dictation off,
    /// with the shipping chord and timing ready for the day they turn it on.
    /// </summary>
    [Fact]
    public void ASettingsFileWrittenBeforeDictationExistedGetsTheDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{ "HotKey": "Win+Alt+." }""");

        Assert.NotNull(settings);
        Assert.False(settings.DictationEnabled);
        Assert.False(settings.DictationPushToTalk);
        Assert.Equal(AppSettings.DefaultDictationHotKey, settings.DictationHotKey);
        Assert.Equal(220, settings.DictationTrailingSilenceMilliseconds);
    }

    [Fact]
    public void ExplicitDictationChoicesAreKept()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """
            {
              "DictationEnabled": true,
              "DictationHotKey": "Ctrl+Alt+D",
              "DictationTrailingSilenceMilliseconds": 800,
              "DictationPushToTalk": true
            }
            """);

        Assert.NotNull(settings);
        Assert.True(settings.DictationEnabled);
        Assert.Equal("Ctrl+Alt+D", settings.DictationHotKey);
        Assert.Equal(800, settings.DictationTrailingSilenceMilliseconds);
        Assert.True(settings.DictationPushToTalk);
    }

    /// <summary>
    /// A value below 100 ms ends an utterance inside a single word, and above two seconds the
    /// overlay sits there after the user has finished, which reads as a hang. Both are reachable
    /// by hand-editing settings.json, so the clamp is applied on the way in rather than trusted to
    /// the settings page.
    /// </summary>
    [Theory]
    [InlineData(0, 100)]
    [InlineData(99, 100)]
    [InlineData(-1, 100)]
    [InlineData(100, 100)]
    [InlineData(500, 500)]
    [InlineData(2000, 2000)]
    [InlineData(5000, 2000)]
    public void TrailingSilenceIsClampedOnLoad(int stored, int expected)
    {
        var settings = LoadFrom($$"""{ "DictationTrailingSilenceMilliseconds": {{stored}} }""");

        Assert.Equal(expected, settings.DictationTrailingSilenceMilliseconds);
    }

    /// <summary>
    /// A blank chord is what an emptied text box saves. Falling back to the default keeps the
    /// feature reachable rather than registering nothing and reporting no reason.
    /// </summary>
    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("null")]
    public void ABlankDictationChordFallsBackToTheDefault(string stored)
    {
        var settings = LoadFrom($$"""{ "DictationHotKey": {{stored}} }""");

        Assert.Equal(AppSettings.DefaultDictationHotKey, settings.DictationHotKey);
    }

    [Fact]
    public void AChosenDictationChordSurvivesLoad()
    {
        var settings = LoadFrom("""{ "DictationEnabled": true, "DictationHotKey": "Ctrl+Alt+D", "DictationPushToTalk": true }""");

        Assert.True(settings.DictationEnabled);
        Assert.Equal("Ctrl+Alt+D", settings.DictationHotKey);
        Assert.True(settings.DictationPushToTalk);
    }

    /// <summary>
    /// Load reads the real settings path, which <see cref="TestEnvironment"/> has already pointed
    /// at a scratch directory for the run. The file is removed afterwards so the next test that
    /// loads settings sees a first run rather than this one's leftovers.
    ///
    /// <para>
    /// The activation chord is deliberately never written here: <c>SettingsWindowSmokeTests</c>
    /// asserts the default hotkey renders, and it constructs its own service.
    /// </para>
    /// </summary>
    private static AppSettings LoadFrom(string json)
    {
        AppPaths.EnsureCreated();
        try
        {
            File.WriteAllText(AppPaths.SettingsFile, json);
            return new AppSettingsService().Load();
        }
        finally
        {
            File.Delete(AppPaths.SettingsFile);
        }
    }
}
