using SemanticStart.Core.Model;
using SemanticStart.Core.Query;
using Xunit;

namespace SemanticStart.Tests;

/// <summary>
/// The details panel shows the longer prose harvested for an entity, which makes it matter whether
/// that prose is about the entity at all. On this machine 67 of the 320 profiles carrying details
/// hold the same paragraph: every <c>ms-settings:</c> page links to one Microsoft Learn article
/// about the URI scheme, so Windows Sandbox was described as "how to launch Windows Settings from
/// your Windows apps".
/// </summary>
public sealed class SharedDetailFilterTests
{
    private const string Boilerplate =
        "Learn how to launch Windows Settings from your Windows apps using the ms-settings URI scheme. This topic describes the URIs for every page.";

    [Fact]
    public void ProseCarriedByManyEntitiesIsNotADescriptionOfAnyOfThem()
    {
        var shared = SharedDetailFilter.Build([
            Profile(Boilerplate),
            Profile(Boilerplate),
            Profile(Boilerplate),
        ]);

        Assert.Null(SharedDetailFilter.Describing(shared, Profile(Boilerplate)));
    }

    [Fact]
    public void ProseUniqueToAnEntityIsKept()
    {
        var shared = SharedDetailFilter.Build([
            Profile(Boilerplate),
            Profile(Boilerplate),
            Profile(Boilerplate),
            Profile("Freeware system monitor for Windows. Process Explorer is a task manager."),
        ]);

        Assert.Equal(
            "Freeware system monitor for Windows. Process Explorer is a task manager.",
            SharedDetailFilter.Describing(shared, Profile("Freeware system monitor for Windows. Process Explorer is a task manager.")));
    }

    /// <summary>
    /// Two entities sharing prose is the normal signature of one program collected twice - the
    /// AppsFolder entry and the Start shortcut, say - and the description is right for both.
    /// </summary>
    [Fact]
    public void APairIsTreatedAsTheSameProgramTwice_NotAsBoilerplate()
    {
        var duplicated = "Backup component of Windows 10 and Windows 11. Windows Backup was released for Windows 11.";

        var shared = SharedDetailFilter.Build([Profile(duplicated), Profile(duplicated)]);

        Assert.Equal(duplicated, SharedDetailFilter.Describing(shared, Profile(duplicated)));
    }

    /// <summary>
    /// Boilerplate is not always copied byte for byte; the tail often carries the page it came
    /// from. Comparing the opening catches those as the same text.
    /// </summary>
    [Fact]
    public void BoilerplateIsRecognizedEvenWhenTheTailDiffers()
    {
        var shared = SharedDetailFilter.Build([
            Profile(Boilerplate + " See the Bluetooth page."),
            Profile(Boilerplate + " See the Sandbox page."),
            Profile(Boilerplate + " See the Storage page."),
        ]);

        Assert.Null(SharedDetailFilter.Describing(shared, Profile(Boilerplate + " See the Display page.")));
    }

    [Fact]
    public void AProfileWithoutProseHasNothingToShow()
    {
        var shared = SharedDetailFilter.Build([Profile(null)]);

        Assert.Null(SharedDetailFilter.Describing(shared, Profile(null)));
        Assert.Null(SharedDetailFilter.Describing(shared, profile: null));
    }

    private static SynthesizedProfile Profile(string? details) => new()
    {
        EntityId = "test:1",
        Summary = "A summary.",
        Details = details,
        Generator = "heuristic",
    };
}
