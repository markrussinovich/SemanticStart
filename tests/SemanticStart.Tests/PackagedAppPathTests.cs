using SemanticStart.App;
using SemanticStart.Core.Collectors;
using SemanticStart.Core.Model;
using SemanticStart.Core.Query;
using Xunit;

namespace SemanticStart.Tests;

/// <summary>
/// The AppsFolder identifies an application only by AppUserModelId. Everything that wants a path -
/// deduplication against other collectors, the local file enrichers, and the details panel - has to
/// get it from the package manifest, so these cover that translation and what the UI does with it.
/// </summary>
public sealed class PackagedAppPathTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ss-pkg-" + Guid.NewGuid().ToString("N"));

    public PackagedAppPathTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("Microsoft.SysinternalsSuite_8wekyb3d8bbwe!Autoruns", "Microsoft.SysinternalsSuite_8wekyb3d8bbwe", "Autoruns")]
    [InlineData(@"shell:AppsFolder\Microsoft.SysinternalsSuite_8wekyb3d8bbwe!ZoomIt", "Microsoft.SysinternalsSuite_8wekyb3d8bbwe", "ZoomIt")]
    [InlineData("Contoso.App_1234567890abc", "Contoso.App_1234567890abc", null)]
    [InlineData("Contoso.App_1234567890abc!", "Contoso.App_1234567890abc", null)]
    [InlineData("", null, null)]
    public void SplitAppUserModelId_SeparatesFamilyFromApplication(string input, string? family, string? applicationId)
    {
        var (actualFamily, actualApp) = PackageCatalog.SplitAppUserModelId(input);

        Assert.Equal(family, actualFamily);
        Assert.Equal(applicationId, actualApp);
    }

    /// <summary>
    /// The Sysinternals Suite ships more than seventy applications from one manifest, so picking
    /// the executable that belongs to this application id - rather than the first one listed - is
    /// the whole job.
    /// </summary>
    [Fact]
    public async Task ResolveExecutable_PicksTheApplicationNamedByTheIdentifier()
    {
        WriteManifest(
            ("Autoruns", @"Tools\Autoruns.exe"),
            ("ZoomIt", @"Tools\ZoomIt.exe"));

        var resolved = await PackageCatalog.ResolveExecutableInAsync(_dir, "ZoomIt");

        Assert.Equal(Path.Combine(_dir, @"Tools\ZoomIt.exe"), resolved);
    }

    /// <summary>A package with one application has nothing to disambiguate, so a bare family resolves.</summary>
    [Fact]
    public async Task ResolveExecutable_FallsBackToTheOnlyApplicationWhenNoIdIsGiven()
    {
        WriteManifest(("App", @"Contoso.exe"));

        var resolved = await PackageCatalog.ResolveExecutableInAsync(_dir, applicationId: null);

        Assert.Equal(Path.Combine(_dir, "Contoso.exe"), resolved);
    }

    /// <summary>
    /// A manifest that names an executable which is not on disk must resolve to nothing rather than
    /// to a path that does not exist: a fabricated path in the details panel is worse than no path.
    /// </summary>
    [Fact]
    public async Task ResolveExecutable_ReturnsNothingWhenTheExecutableIsAbsent()
    {
        WriteManifest(("Autoruns", @"Tools\Autoruns.exe"), ("Ghost", @"Tools\Ghost.exe"));

        Assert.Null(await PackageCatalog.ResolveExecutableInAsync(_dir, "Ghost"));
    }

    [Fact]
    public async Task ResolveExecutable_ReturnsNothingWithoutAManifest()
    {
        Assert.Null(await PackageCatalog.ResolveExecutableInAsync(_dir, "Autoruns"));
    }

    /// <summary>
    /// The Sysinternals Suite starts ZoomIt through a shared RunUnpackaged.exe stub. Reporting the
    /// stub would name the wrong file in the details panel and, worse, hand deduplication a key
    /// that every application in the package shares - which would collapse them all into one.
    /// </summary>
    [Fact]
    public async Task ResolveExecutable_PrefersTheNamedBinaryOverAStubSharedByManyApplications()
    {
        WriteManifest(("ZoomIt", "RunUnpackaged.exe"), ("Procmon", "RunUnpackaged.exe"));
        Touch(@"Tools\ZoomIt.exe");

        var resolved = await PackageCatalog.ResolveExecutableInAsync(_dir, "ZoomIt");

        Assert.Equal(Path.Combine(_dir, @"Tools\ZoomIt.exe"), resolved);
    }

    /// <summary>
    /// With no binary named after the application there is nothing truthful to report, and the
    /// stub must not be substituted: two applications resolving to one path is exactly how a
    /// package loses every entry but its first.
    /// </summary>
    [Fact]
    public async Task ResolveExecutable_ReportsNothingRatherThanAStubItCannotDisambiguate()
    {
        WriteManifest(("ZoomIt", "RunUnpackaged.exe"), ("Procmon", "RunUnpackaged.exe"));

        Assert.Null(await PackageCatalog.ResolveExecutableInAsync(_dir, "ZoomIt"));
        Assert.Null(await PackageCatalog.ResolveExecutableInAsync(_dir, "Procmon"));
    }

    /// <summary>
    /// The stub is a stub whether or not a second application shares it. On this machine ZoomIt is
    /// the only entry pointing at RunUnpackaged.exe, and reporting that path told the user the
    /// application lives in a file that has nothing to do with it.
    /// </summary>
    [Fact]
    public async Task ResolveExecutable_PrefersTheNamedBinaryEvenWhenOnlyOneApplicationUsesTheStub()
    {
        WriteManifest(("ZoomIt", "RunUnpackaged.exe"), ("Autoruns", @"Tools\Autoruns.exe"));
        Touch(@"Tools\ZoomIt.exe");

        var resolved = await PackageCatalog.ResolveExecutableInAsync(_dir, "ZoomIt");

        Assert.Equal(Path.Combine(_dir, @"Tools\ZoomIt.exe"), resolved);
    }

    /// <summary>
    /// The manifest stays authoritative when nothing contradicts it: an application whose id is a
    /// generic "App" is not evidence of a stub, so its declared executable is still the answer.
    /// </summary>
    [Fact]
    public async Task ResolveExecutable_KeepsTheManifestExecutableWhenNoBetterNamedBinaryExists()
    {
        WriteManifest(("App", @"Notepad\Notepad.exe"), ("Other", @"Tools\Other.exe"));

        var resolved = await PackageCatalog.ResolveExecutableInAsync(_dir, "App");

        Assert.Equal(Path.Combine(_dir, @"Notepad\Notepad.exe"), resolved);
    }

    /// <summary>
    /// The details panel used to show nothing at all for packaged apps, because the only thing the
    /// AppsFolder supplies is an AppUserModelId and that is deliberately never displayed.
    /// </summary>
    [Fact]
    public void DetailsPanel_ShowsTheResolvedPathForAPackagedApp()
    {
        var item = new SearchResultItem(Hit(
            launchTarget: "Microsoft.SysinternalsSuite_8wekyb3d8bbwe!Autoruns",
            targetPath: @"C:\Program Files\WindowsApps\Microsoft.SysinternalsSuite\Tools\Autoruns.exe"));

        Assert.True(item.HasLaunchTarget);
        Assert.Equal(@"C:\Program Files\WindowsApps\Microsoft.SysinternalsSuite\Tools\Autoruns.exe", item.LaunchTarget);
    }

    [Fact]
    public void DetailsPanel_StillHidesAnUnresolvedAppUserModelId()
    {
        var item = new SearchResultItem(Hit(
            launchTarget: "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
            targetPath: null));

        Assert.False(item.HasLaunchTarget);
        Assert.Equal(string.Empty, item.LaunchTarget);
    }

    [Fact]
    public void DetailsPanel_PrefersAnAlreadyReadableLaunchTarget()
    {
        var item = new SearchResultItem(Hit(
            launchTarget: @"C:\Windows\System32\mspaint.exe",
            targetPath: @"C:\Windows\System32\somewhere-else.exe"));

        Assert.Equal(@"C:\Windows\System32\mspaint.exe", item.LaunchTarget);
    }

    private static SearchHit Hit(string launchTarget, string? targetPath)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (targetPath is not null)
            metadata["targetPath"] = targetPath;

        return new SearchHit
        {
            Entity = new Entity
            {
                Id = "test:1",
                Kind = EntityKind.PackagedApp,
                DisplayName = "Autoruns",
                LaunchKind = LaunchKind.AppsFolder,
                LaunchTarget = launchTarget,
                Source = "appsfolder",
                RawMetadata = metadata,
            },
            Summary = "Identifies programs configured to run at startup.",
            Score = 1,
        };
    }

    private void Touch(string relativePath)
    {
        var full = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Empty);
    }

    private void WriteManifest(params (string Id, string Executable)[] applications)
    {
        var entries = string.Join(Environment.NewLine, applications
            .Select(a => $"""    <Application Id="{a.Id}" Executable="{a.Executable}" EntryPoint="Windows.FullTrustApplication" />"""));

        File.WriteAllText(Path.Combine(_dir, "AppxManifest.xml"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications>
            {entries}
              </Applications>
            </Package>
            """);

        foreach (var (_, executable) in applications.Where(a => !a.Executable.Contains("Ghost")))
        {
            var full = Path.Combine(_dir, executable);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, string.Empty);
        }
    }
}
