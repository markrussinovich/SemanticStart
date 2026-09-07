using System.Text;
using SemanticStart.Core.Enrichment;

namespace SemanticStart.Tests;

/// <summary>
/// A menu resource stores each item's command id in the two bytes immediately before its label,
/// with nothing in between. Scanning the blob for text therefore reads the low byte of the id as
/// the first character of the caption whenever it happens to be printable, which is how
/// Performance Monitor's menus arrived as "hExit", "pStart Monitoring" and "ySnap to Compare".
/// The damage is inside the word, where no filter can reach it, so the format is parsed.
///
/// Both layouts are covered, and so is the fallback: an unfamiliar variant must degrade to
/// scanning rather than to silence.
/// </summary>
public sealed class MenuResourceTests
{
    [Fact]
    public void ClassicMenu_DoesNotGlueTheCommandIdOntoTheCaption()
    {
        // Ids chosen to be printable ASCII, which is the condition that produced the defect.
        var blob = ClassicMenu(
            (0x0000, 0x0068, "Suspend Process"),
            (0x0000, 0x0070, "Start Monitoring"),
            (0x0080, 0x0079, "Snap to Compare"));

        Assert.Equal(["Suspend Process", "Start Monitoring", "Snap to Compare"], UiResourceEnricher.ReadMenu(blob));
    }

    [Fact]
    public void ClassicMenu_ReadsItemsInsideAPopup()
    {
        var blob = ClassicMenu(
            (0x0010, 0, "Monitor"),
            (0x0000, 0x0068, "Suspend Process"),
            (0x0080, 0x0069, "Resume Process"));

        var captions = UiResourceEnricher.ReadMenu(blob);

        Assert.Contains("Suspend Process", captions);
        Assert.Contains("Resume Process", captions);
    }

    [Fact]
    public void ExtendedMenu_ReadsCaptionsWithoutTheirStructure()
    {
        var blob = ExtendedMenu(
            (0x0068, false, false, "Load Hive"),
            (0x0069, false, false, "Connect Network Registry"),
            (0x006A, false, true, "DWORD (32-bit) Value"));

        Assert.Equal(
            ["Load Hive", "Connect Network Registry", "DWORD (32-bit) Value"],
            UiResourceEnricher.ReadMenu(blob));
    }

    /// <summary>
    /// A blob that runs out mid-item must not be reported as an empty menu, which would look like
    /// a program with no interface rather than like a format this does not understand. Scanning
    /// recovers the words but not the caption - the leading "A" here is the command id, which is
    /// the whole reason the format is parsed when it can be.
    /// </summary>
    [Fact]
    public void TruncatedMenu_FallsBackToScanning()
    {
        var whole = ClassicMenu((0x0080, 0x0041, "Analyze Wait Chain"));
        var truncated = whole[..^2];

        var recovered = string.Join(" ", UiResourceEnricher.ReadMenu(truncated));

        Assert.Contains("Wait Chain", recovered, StringComparison.Ordinal);
    }

    private static byte[] ClassicMenu(params (int Flags, int Id, string Text)[] items)
    {
        var buffer = new List<byte> { 0, 0, 0, 0 };
        foreach (var (flags, id, text) in items)
        {
            buffer.AddRange(Word(flags));
            if ((flags & 0x0010) == 0)
                buffer.AddRange(Word(id));
            buffer.AddRange(Encoding.Unicode.GetBytes(text));
            buffer.AddRange([0, 0]);
        }

        return [.. buffer];
    }

    private static byte[] ExtendedMenu(params (int Id, bool Popup, bool Last, string Text)[] items)
    {
        var buffer = new List<byte>();
        buffer.AddRange(Word(1));
        buffer.AddRange(Word(4));
        buffer.AddRange([0, 0, 0, 0]);

        foreach (var (id, popup, last, text) in items)
        {
            while (buffer.Count % 4 != 0)
                buffer.Add(0);

            buffer.AddRange([0, 0, 0, 0]);
            buffer.AddRange([0, 0, 0, 0]);
            buffer.AddRange(Word(id));
            buffer.AddRange(Word(0));
            buffer.AddRange(Word((popup ? 0x01 : 0) | (last ? 0x80 : 0)));
            buffer.AddRange(Encoding.Unicode.GetBytes(text));
            buffer.AddRange([0, 0]);
        }

        return [.. buffer];
    }

    private static byte[] Word(int value) => [(byte)(value & 0xFF), (byte)((value >> 8) & 0xFF)];

    /// <summary>
    /// Every program has one dialog that describes its publisher instead of itself. Performance
    /// Monitor's contributed "APPLICATION", the product name and a copyright line - a vendor and a
    /// legal notice, shared by most of the index and saying nothing about what the program does.
    /// Recognised per resource rather than per caption, because it is the notice sitting alongside
    /// the rest that identifies the dialog.
    /// </summary>
    [Fact]
    public void AboutBox_IsRejectedWholeRatherThanLineByLine()
    {
        Assert.True(UiResourceEnricher.IsAboutBox(
            ["APPLICATION", "Microsoft\u00ae Windows\u00ae Operating System", "Microsoft Corporation. All rights reserved"]));
    }

    [Fact]
    public void OrdinaryDialog_IsNotMistakenForAnAboutBox()
    {
        Assert.False(UiResourceEnricher.IsAboutBox(["Export range", "Selected branch", "Value data"]));
    }
}
