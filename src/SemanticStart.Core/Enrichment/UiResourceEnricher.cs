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
                    foreach (var caption in ExtractStrings(bytes))
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
    /// Pulls the UTF-16 runs out of a menu or dialog resource.
    ///
    /// The two formats are parsed rather than decoded structurally on purpose. Both have several
    /// incompatible versions - MENUEX and DIALOGEX differ from their originals in header size and
    /// in per-item fields - and a parser that mis-steps on one variant silently produces
    /// nonsense. Reading the strings out of a blob that only ever contains an interface makes the
    /// worst case a few extra words rather than garbage, and the filtering below removes the
    /// structural leftovers that are not captions.
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

    private static IEnumerable<string?> CandidatePaths(Entity entity)
    {
        if (entity.RawMetadata.TryGetValue("targetPath", out var target)) yield return target;
        yield return entity.LaunchTarget;
        yield return entity.IconSource;
        if (entity.RawMetadata.TryGetValue("fileName", out var fileName) && !Path.IsPathRooted(fileName))
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), fileName);
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
