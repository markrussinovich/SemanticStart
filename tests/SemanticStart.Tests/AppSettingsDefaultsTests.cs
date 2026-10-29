using System.Text.Json;
using SemanticStart.App;

namespace SemanticStart.Tests;

public sealed class AppSettingsDefaultsTests
{
    /// <summary>
    /// A settings file written before the option existed, or an index built without first-run
    /// setup, must get the same default first-run setup offers rather than silently indexing
    /// offline.
    /// </summary>
    [Fact]
    public void SettingsWithoutTheOnlineKeyDefaultToOn()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{ "HotKey": "Win+Alt+." }""");

        Assert.NotNull(settings);
        Assert.True(settings.AllowOnlineEnrichment);
    }

    [Fact]
    public void AnExplicitOptOutIsKept()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{ "AllowOnlineEnrichment": false }""");

        Assert.NotNull(settings);
        Assert.False(settings.AllowOnlineEnrichment);
    }
}
