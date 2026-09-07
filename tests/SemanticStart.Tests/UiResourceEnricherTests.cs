using System.Text;
using SemanticStart.Core.Enrichment;

namespace SemanticStart.Tests;

/// <summary>
/// Exercises the caption filter against synthetic resource blobs, so the rules hold on any
/// machine rather than only on one with a particular program installed.
/// </summary>
public class UiResourceEnricherTests
{
    [Fact]
    public void KeepsCaptionsThatNameACapability()
    {
        var captions = Extract("Physical Memory History", "Find Handle or DLL", "Kill Process Tree");

        Assert.Equal(["Physical Memory History", "Find Handle or DLL", "Kill Process Tree"], captions);
    }

    [Fact]
    public void StripsTheAcceleratorMarkerFromAMenuItem()
    {
        Assert.Equal(["Select Columns"], Extract("Se&lect Columns"));
    }

    [Fact]
    public void DropsTheAcceleratorThatFollowsACaption()
    {
        Assert.Equal(["Show Lower Pane"], Extract("Show Lower Pane\tCtrl+L"));
    }

    [Fact]
    public void DropsWindowClassAndFontNamesThatDialogsCarry()
    {
        Assert.Empty(Extract("SysListView32", "MS Shell Dlg", "Segoe UI"));
    }

    [Fact]
    public void DropsRepeatedEnumeratedItems()
    {
        Assert.Empty(Extract("CPU 12", "10 seconds", "Realtime: 24"));
    }

    /// <summary>
    /// The point of the enricher is to say what a program does that others do not. A menu bar
    /// full of standard verbs describes every program with a menu bar, so admitting it would put
    /// all of them in the candidate pool for an ordinary query.
    /// </summary>
    [Fact]
    public void DropsStandardMenuVocabulary()
    {
        Assert.Empty(Extract("File", "Save As", "Zoom In", "Always On Top", "About"));
    }

    [Fact]
    public void RecoversACaptionPrefixedByTheTailOfAPrecedingStructure()
    {
        Assert.Equal(["Commit History"], Extract("\u0490\u0100Commit History"));
    }

    [Fact]
    public void DropsShortRunsThatAreNotWords()
    {
        Assert.Empty(Extract("sBA", "twH", "AG)", "xH2"));
    }

    [Fact]
    public void DropsPathsAndFormatStrings()
    {
        Assert.Empty(Extract(@"C:\Windows\System32", "Loaded %d modules", "<none>"));
    }

    /// <summary>
    /// Two captions separated by a NUL is exactly how a menu resource stores them, so a run that
    /// spans the separator must not be reported as one string.
    /// </summary>
    [Fact]
    public void TreatsTheResourceSeparatorAsACaptionBoundary()
    {
        var captions = Extract("Kill Process", "Suspend Process");

        Assert.Equal(2, captions.Count);
    }

    /// <summary>
    /// A dialog that spells out a date or serial pattern for the user contributes tokens nobody
    /// could ever search for, and every one of them lengthens the field that BM25 divides by.
    /// </summary>
    [Fact]
    public void DropsDateAndSerialFormatPlaceholders()
    {
        Assert.Empty(Extract("yyyyMMddHH", "dddd", "NNNNNN", "MMddHHmm", "yyyyDDD"));
    }

    /// <summary>
    /// The same shape test must not take the acronyms that genuinely name capabilities.
    /// </summary>
    [Fact]
    public void KeepsAcronymsThatNameACapability()
    {
        Assert.Equal(["CPU Context", "HTML Report", "DNS Cache"], Extract("CPU Context", "HTML Report", "DNS Cache"));
    }

    /// <summary>
    /// Explanatory dialog text is prose. Kept, the word-level dedupe downstream shreds it into
    /// disconnected words that match queries no one meant.
    /// </summary>
    [Fact]
    public void DropsExplanatorySentencesButKeepsLongLabels()
    {
        Assert.Empty(Extract("Which of these would you like to use for this collector set"));
        Assert.Equal(["Overwrite the existing log file"], Extract("Overwrite the existing log file"));
    }

    private static List<string> Extract(params string[] strings)
    {
        var buffer = new List<byte>();
        foreach (var value in strings)
        {
            buffer.AddRange(Encoding.Unicode.GetBytes(value));
            buffer.AddRange([0, 0]);
        }

        return UiResourceEnricher.ExtractStrings([.. buffer]).ToList();
    }
}
