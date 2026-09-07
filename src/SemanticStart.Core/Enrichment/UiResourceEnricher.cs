using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Enrichment;

/// <summary>
/// Harvests the labels a program shows for its own features, by reading the menu and dialog
/// resources out of its executable.
///
/// This exists because prose about a tool routinely fails to say what the tool visibly does.
/// Process Explorer was reported missing from "view memory usage"; the word memory appears
/// nowhere in its manifest, its version resource, its Microsoft Learn page or its Wikipedia
/// article, all of which describe it in terms of handles and DLLs. Its menus, meanwhile, offer
/// "Physical Memory History" and its columns are named "Physical Memory Usage". A program's own
/// interface is first-hand evidence of its capabilities and is available offline on every
/// machine, which no article is.
///
/// Deliberately restricted to RT_MENU and RT_DIALOG. Scanning a whole executable for text finds
/// the same labels but buries them in symbol names, format strings and error messages, and the
/// corpus has already measured that widening the lexical candidate pool with loosely related
/// text costs relevance. Menu items and dialog captions are the subset written to be read by a
/// user, which is the same subset that names features.
/// </summary>
public sealed class UiResourceEnricher : IEnricher
{
    /// <summary>
    /// Enough to cover a large application's menus without letting one program contribute a
    /// document that dwarfs everything else in the index.
    /// </summary>
    private const int MaxCaptions = 160;

    private const int MaxCaptionLength = 48;

    /// <summary>
    /// Where a label stops naming a feature and starts explaining one. Menu items and buttons run
    /// to a few words; dialog body text runs to a sentence. Measured on the corpus: cutting at
    /// five words loses real labels and costs a case, and not cutting at all lets explanatory
    /// prose through to be shredded into word soup by the dedupe downstream.
    /// </summary>
    private const int MaxCaptionWords = 8;

    /// <summary>
    /// Three-character runs out of a resource blob are far more often two bytes of structure
    /// followed by a letter than they are a real caption, and the genuine three-letter menu items
    /// - Run, New, Del, Cut - are all standard chrome that is discarded anyway.
    /// </summary>
    private const int MinCaptionLength = 4;

    public string Provider => "ui-resources";
    public bool RequiresNetwork => false;
    public bool CanEnrich(Entity entity) => ResolveExecutable(entity) is not null;

    public Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveExecutable(entity);
        if (path is null)
            return Task.FromResult<IReadOnlyList<EnrichmentDocument>>([]);

        var captions = ReadCaptions(path, cancellationToken);
        if (captions.Count == 0)
            return Task.FromResult<IReadOnlyList<EnrichmentDocument>>([]);

        var text = "Interface labels: " + string.Join(", ", captions) + ".";
        return Task.FromResult<IReadOnlyList<EnrichmentDocument>>(
        [
            new EnrichmentDocument
            {
                EntityId = entity.Id,
                Provider = Provider,
                IsOnline = false,
                Text = text,
                SourceUri = path,
            }
        ]);
    }

    internal static IReadOnlyList<string> ReadCaptions(string path, CancellationToken cancellationToken = default)
    {
        var module = IntPtr.Zero;
        try
        {
            module = NativeMethods.LoadLibraryEx(path, IntPtr.Zero, NativeMethods.LoadLibraryAsDatafile | NativeMethods.LoadLibraryAsImageResource);
            if (module == IntPtr.Zero)
                return [];

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<string>();

            foreach (var type in NativeMethods.CaptionResourceTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var bytes in EnumerateResources(module, type))
                {
                    var raw = type == NativeMethods.RtMenu
                        ? ReadMenu(bytes)
                        : [.. ExtractStrings(bytes)];

                    // Judged per resource rather than per caption, because what identifies an
                    // About box is the company and copyright line sitting alongside the rest.
                    if (IsAboutBox(raw))
                        continue;

                    foreach (var caption in raw)
                    {
                        if (ordered.Count >= MaxCaptions)
                            return ordered;
                        if (seen.Add(caption))
                            ordered.Add(caption);
                    }
                }
            }

            return ordered;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return [];
        }
        finally
        {
            if (module != IntPtr.Zero)
                NativeMethods.FreeLibrary(module);
        }
    }

    private static List<byte[]> EnumerateResources(IntPtr module, IntPtr type)
    {
        return [.. Enumerate(module, type)];
    }

    private static List<byte[]> Enumerate(IntPtr module, IntPtr type)
    {
        var blobs = new List<byte[]>();

        bool Callback(IntPtr hModule, IntPtr resourceType, IntPtr name, IntPtr param)
        {
            var handle = NativeMethods.FindResource(hModule, name, resourceType);
            if (handle == IntPtr.Zero)
                return true;

            var size = NativeMethods.SizeofResource(hModule, handle);
            if (size is 0 or > 512 * 1024)
                return true;

            var data = NativeMethods.LoadResource(hModule, handle);
            if (data == IntPtr.Zero)
                return true;

            var pointer = NativeMethods.LockResource(data);
            if (pointer == IntPtr.Zero)
                return true;

            var buffer = new byte[size];
            Marshal.Copy(pointer, buffer, 0, (int)size);
            blobs.Add(buffer);
            return true;
        }

        // EnumResourceNames reports failure when a module simply has none of the requested type,
        // which is the common case rather than an error, so the return value is not checked.
        NativeMethods.EnumResourceNames(module, type, Callback, IntPtr.Zero);
        return blobs;
    }

    /// <summary>
    /// The captions of a menu resource, read structurally.
    ///
    /// Scanning a menu for text cannot work, and the reason is in the format rather than in the
    /// filtering. A MENUITEM stores its command id in the two bytes immediately before its label,
    /// with no separator; when the low byte of that id happens to be printable and the high byte
    /// is zero, it reads as the first character of the caption. Performance Monitor's menus came
    /// out as "hExit", "pStart Monitoring", "qStop Monitoring" and "ySnap to Compare". The noise
    /// is not at the edge of the string where a filter could reach it - it is inside the word.
    ///
    /// Both layouts are handled: the original, where a popup item is a flags word followed by
    /// text and a command item has its id between the two, and MENUEX, which is DWORD-aligned and
    /// carries type, state and id before a resource-info word.
    ///
    /// A parser that mis-steps on an unfamiliar variant produces silent nonsense, so anything
    /// that walks off the end of the blob falls back to scanning it. That keeps the failure mode
    /// no worse than what this replaces.
    /// </summary>
    internal static List<string> ReadMenu(byte[] blob)
    {
        try
        {
            var captions = new List<string>();
            if (blob.Length < 4)
                return captions;

            var version = ReadUInt16(blob, 0);
            if (version == 1)
            {
                var headerSize = ReadUInt16(blob, 2);
                ReadMenuExItems(blob, 4 + headerSize, captions, 0);
            }
            else
            {
                var headerSize = ReadUInt16(blob, 2);
                ReadMenuItems(blob, 4 + headerSize, captions, 0);
            }

            return captions;
        }
        catch (ArgumentOutOfRangeException)
        {
            return [.. ExtractStrings(blob)];
        }
    }

    private const int MaxMenuDepth = 12;

    private static int ReadMenuItems(byte[] blob, int offset, List<string> captions, int depth)
    {
        while (offset + 2 <= blob.Length)
        {
            var flags = ReadUInt16(blob, offset);
            offset += 2;

            var popup = (flags & 0x0010) != 0;
            if (!popup)
                offset += 2;

            offset = ReadNulTerminated(blob, offset, out var text);
            if (Clean(text) is { } caption)
                captions.Add(caption);

            if (popup && depth < MaxMenuDepth)
                offset = ReadMenuItems(blob, offset, captions, depth + 1);

            if ((flags & 0x0080) != 0)
                return offset;
        }

        return offset;
    }

    private static int ReadMenuExItems(byte[] blob, int offset, List<string> captions, int depth)
    {
        while (true)
        {
            offset = Align(offset);
            if (offset + 14 > blob.Length)
                return offset;

            var popup = (blob[offset + 12] & 0x01) != 0;
            var last = (blob[offset + 12] & 0x80) != 0;
            offset = ReadNulTerminated(blob, offset + 14, out var text);
            if (Clean(text) is { } caption)
                captions.Add(caption);

            if (popup)
            {
                offset = Align(offset) + 4;
                if (depth < MaxMenuDepth)
                    offset = ReadMenuExItems(blob, offset, captions, depth + 1);
            }

            if (last)
                return offset;
        }
    }

    private static int Align(int offset) => (offset + 3) & ~3;

    private static ushort ReadUInt16(byte[] blob, int offset)
    {
        if (offset + 2 > blob.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return (ushort)(blob[offset] | (blob[offset + 1] << 8));
    }

    private static int ReadNulTerminated(byte[] blob, int offset, out string text)
    {
        var builder = new StringBuilder();
        while (true)
        {
            if (offset + 2 > blob.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            var value = (char)(blob[offset] | (blob[offset + 1] << 8));
            offset += 2;
            if (value == '\0')
                break;

            builder.Append(value);
        }

        text = builder.ToString();
        return offset;
    }

    /// <summary>
    /// True for the one dialog every program has that describes its publisher rather than itself.
    /// Performance Monitor's contributed "APPLICATION", "Microsoft Windows Operating System" and
    /// "Microsoft Corporation. All rights reserved" - a vendor's name and a legal notice, which
    /// say nothing about the program and are shared by most of the index.
    /// </summary>
    internal static bool IsAboutBox(IReadOnlyList<string> captions) =>
        captions.Any(c => AboutBoxNotice.IsMatch(c));

    private static readonly Regex AboutBoxNotice = new(
        @"rights reserved|copyright|\(c\)\s*\d|Â©",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Pulls the UTF-16 runs out of a dialog resource.
    ///
    /// The format is scanned rather than decoded structurally on purpose. DIALOG and DIALOGEX
    /// differ in header size and in per-control fields, and a parser that mis-steps on one
    /// variant silently produces nonsense. Unlike a menu, a dialog stores each control's caption
    /// as its own NUL-terminated string with structure on either side, so scanning costs only the
    /// occasional structural leftover at an edge, which the filtering below removes.
    /// </summary>
    internal static IEnumerable<string> ExtractStrings(byte[] blob)
    {
        var builder = new StringBuilder();
        for (var i = 0; i + 1 < blob.Length; i += 2)
        {
            var value = (char)(blob[i] | (blob[i + 1] << 8));
            if (value is >= ' ' and <= '\u2122' && !char.IsControl(value))
            {
                builder.Append(value);
                continue;
            }

            if (Clean(builder.ToString()) is { } finished)
                yield return finished;
            builder.Clear();
        }

        if (Clean(builder.ToString()) is { } last)
            yield return last;
    }

    private static string? Clean(string raw)
    {
        if (raw.Length == 0)
            return null;

        // A menu item carries its accelerator after a tab; a dialog control's caption can be
        // followed by a format specifier. Neither says anything about what the item does.
        var cut = raw.IndexOf('\t');
        if (cut >= 0)
            raw = raw[..cut];

        raw = raw.Replace("&", string.Empty);

        // A resource blob is read as one stream, so a caption can arrive with the tail of the
        // preceding structure glued to its front - "MS Shell Dlg" turns up prefixed with two
        // characters of a padding word. Dropping a leading run of non-ASCII restores the caption
        // and, in a resource that is entirely non-ASCII, empties it, which is the right outcome
        // for an index whose queries are English.
        raw = Regex.Replace(raw, @"^[^\x20-\x7E]+", string.Empty);

        // Judged before the edges are trimmed, because trimming angle brackets off a placeholder
        // leaves a word that looks like an ordinary caption.
        if (raw.Contains('\\') || raw.Contains('%') || raw.Contains('<') || raw.Contains('{'))
            return null;

        // A run of text lifted out of a binary can begin or end mid-structure, so trim back to
        // the part that reads as a caption before judging it.
        raw = raw.Trim(TrimmedEdges);
        raw = Regex.Replace(raw, @"\s+", " ").Trim();

        if (raw.Length is < MinCaptionLength or > MaxCaptionLength)
            return null;

        // Three-character captions are real - Run, Del, New - but so is most of the noise, and
        // the noise is distinguishable by not being purely alphabetic.
        if (raw.Length == MinCaptionLength && !raw.All(char.IsLetter))
            return null;

        // Dialog resources name the window class and font of every control they contain, and
        // those names are indistinguishable from captions by shape alone.
        if (ControlClasses.Contains(raw))
            return null;

        // Keyboard accelerators are stored as their own strings in some menus. They describe how
        // to reach a feature, never what it is.
        if (raw.Contains('+'))
            return null;

        // Enumerated items - CPU 0 through CPU 31, "1 second", "Realtime: 24" - are one interface
        // element repeated, and a caption list is meant to name distinct capabilities.
        if (char.IsDigit(raw[0]) || char.IsDigit(raw[^1]))
            return null;

        // A caption is a phrase. Anything without two consecutive letters is structure.
        if (!Regex.IsMatch(raw, "[A-Za-z]{2}"))
            return null;

        // Format placeholders - "yyyyMMddHH", "dddd", "NNNNNN", "MMddHHmm" - are how a dialog
        // spells out a date or serial pattern to the user. They are shaped unlike any word: a
        // letter repeated three times running, or a long run with no vowel in it at all. Left in,
        // they cost twice, once as tokens that can never be searched for and once as length that
        // dilutes the words worth matching.
        if (raw.Split(' ').Any(IsFormatPattern))
            return null;

        // A label names a thing; explanatory dialog text describes it in a sentence. Beyond five
        // words a caption has stopped naming a feature and started explaining one, and the
        // explanation is prose that the word-level dedupe downstream can only shred.
        if (raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > MaxCaptionWords)
            return null;

        // Letters must dominate, which excludes version strings, GUID fragments and the runs of
        // punctuation that separate dialog controls.
        var letters = raw.Count(char.IsLetter);
        if (letters * 2 < raw.Length)
            return null;

        // Standard menu and common-dialog vocabulary appears in almost every program that has a
        // menu at all, so it identifies nothing while adding every such program to the candidate
        // pool for queries like "save file". This is a statement about the Windows interface
        // rather than about any particular program, which is what keeps it out of the territory
        // of hand-written per-program knowledge.
        return UniversalChrome.Contains(raw) ? null : raw;
    }

    private static readonly char[] TrimmedEdges = [' ', '\t', '.', ':', '>', '<', '(', ')', '[', ']', '-', ',', '\'', '"', '/'];

    /// <summary>
    /// Whether a word is a date or serial format placeholder rather than something a user could
    /// ever type. Three of the same letter running does not occur in English; neither does a
    /// five-letter run without a vowel, which is long enough to spare the acronyms - CPU, GPU,
    /// DNS, HTML - that are worth keeping.
    /// </summary>
    private static bool IsFormatPattern(string word)
    {
        if (word.Length < MinCaptionLength || !word.All(char.IsLetter))
            return false;

        for (var i = 2; i < word.Length; i++)
        {
            if (char.ToLowerInvariant(word[i]) == char.ToLowerInvariant(word[i - 1])
                && char.ToLowerInvariant(word[i - 1]) == char.ToLowerInvariant(word[i - 2]))
                return true;
        }

        return word.Length >= 5 && !word.Any(c => Vowels.Contains(char.ToLowerInvariant(c)));
    }

    private const string Vowels = "aeiou";

    private static readonly HashSet<string> UniversalChrome = new(StringComparer.OrdinalIgnoreCase)
    {
        "File", "Edit", "View", "Help", "Tools", "Options", "Window", "Settings", "Format",
        "New", "Open", "Save", "Save As", "Save all", "Print", "Print Preview", "Page Setup",
        "Exit", "Close", "Quit", "Cancel", "Apply", "Reset", "Defaults", "Restore Defaults",
        "Undo", "Redo", "Cut", "Copy", "Paste", "Delete", "Select All", "Clear", "Clear All",
        "Find", "Find Next", "Find Previous", "Replace", "Go To", "Refresh", "Reload",
        "Properties", "About", "Preferences", "Customize", "Font", "Fonts", "Colors", "Color",
        "Zoom", "Zoom In", "Zoom Out", "Toolbar", "Toolbars", "Status Bar", "Full Screen",
        "Always On Top", "Minimize", "Maximize", "Restore", "Back", "Forward", "Next",
        "Previous", "Finish", "Continue", "Browse", "Add", "Remove", "Rename", "Import",
        "Export", "Yes", "No", "Ok", "Details", "Advanced", "General", "Dark", "Light",
        "Theme", "Language", "Update", "Updates", "Check for Updates", "Send Feedback",
        "Recent Files", "Word Wrap", "Show", "Hide", "Enable", "Disable", "Start", "Stop",
        "Pause", "Resume", "Run", "Search", "Filter", "Sort", "Edit Menu", "Context",
    };

    private static readonly HashSet<string> ControlClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button", "Static", "Edit", "ListBox", "ComboBox", "ScrollBar", "RichEdit",
        "SysListView32", "SysTreeView32", "SysTabControl32", "SysHeader32", "SysAnimate32",
        "SysDateTimePick32", "SysMonthCal32", "SysLink", "SysIPAddress32", "SysPager",
        "msctls_progress32", "msctls_trackbar32", "msctls_updown32", "msctls_statusbar32",
        "msctls_hotkey32", "ToolbarWindow32", "tooltips_class32", "ReBarWindow32",
        "MS Shell Dlg", "MS Sans Serif", "Tahoma", "Segoe UI", "Microsoft Sans Serif",
        "Arial", "Courier New", "Consolas", "Verdana",
    };

    private static string? ResolveExecutable(Entity entity)
    {
        foreach (var candidate in CandidatePaths(entity))
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            try
            {
                var path = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
                var comma = path.LastIndexOf(',');
                if (comma > 2 && int.TryParse(path[(comma + 1)..], out _))
                    path = path[..comma];

                if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (File.Exists(path))
                    return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
            {
            }
        }

        return null;
    }

    /// <summary>
    /// The paths that might hold this entity's interface, best first.
    ///
    /// The icon leads when the entry is launched indirectly - either the target takes arguments,
    /// or the entry's own file name is not the executable it resolves to. Both mean the resolved
    /// binary serves more than this one entry, and reading it describes the wrong feature:
    /// Resource Monitor is "resmon.exe", resolves to perfmon.exe, and perfmon's menus describe
    /// Performance Monitor with no mention of a process anywhere. The icon is the one part of
    /// such an entry that has to be specific to it, so it names the module that implements it -
    /// Resource Monitor's points at wdc.dll, whose menus offer "End Process", "Suspend Process"
    /// and "Analyze Wait Chain". A directly launched entry keeps its own target in front, which
    /// is what stops a shared icon library such as shell32.dll from speaking for an ordinary app.
    /// </summary>
    private static IEnumerable<string?> CandidatePaths(Entity entity)
    {
        var target = entity.RawMetadata.GetValueOrDefault("targetPath");
        if (!string.IsNullOrWhiteSpace(entity.LaunchArguments) || LaunchesThroughAnotherBinary(entity, target))
            yield return entity.IconSource;

        yield return target;
        yield return entity.LaunchTarget;
        yield return entity.IconSource;
        if (entity.RawMetadata.TryGetValue("fileName", out var fileName) && !Path.IsPathRooted(fileName))
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), fileName);
    }

    /// <summary>
    /// True when the entry's own file name is not the executable it ends up running. The
    /// difference is the fingerprint of a stub: a small binary whose only job is to start a
    /// shared host, which is why the host cannot be read as a description of this entry.
    /// </summary>
    private static bool LaunchesThroughAnotherBinary(Entity entity, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return false;

        if (!entity.RawMetadata.TryGetValue("fileName", out var fileName) || string.IsNullOrWhiteSpace(fileName))
            return false;

        return !string.Equals(Path.GetFileName(target), Path.GetFileName(fileName), StringComparison.OrdinalIgnoreCase);
    }

    private static class NativeMethods
    {
        public const uint LoadLibraryAsDatafile = 0x00000002;
        public const uint LoadLibraryAsImageResource = 0x00000020;

        public static readonly IntPtr RtMenu = 4;
        public static readonly IntPtr RtDialog = 5;
        public static readonly IntPtr[] CaptionResourceTypes = [RtMenu, RtDialog];

        public delegate bool EnumResNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LoadLibraryExW")]
        public static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumResourceNamesW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResNameProc callback, IntPtr param);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindResourceW")]
        public static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LockResource(IntPtr data);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint SizeofResource(IntPtr module, IntPtr resource);
    }
}
