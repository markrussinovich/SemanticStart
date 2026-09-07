namespace SemanticStart.Core.Query;

/// <summary>
/// The relevance contract for the product, expressed as queries a user would actually type.
///
/// This exists because relevance regressions are silent: a ranking tweak that fixes one query
/// routinely breaks five others, and nothing in a normal build catches it. Every case here is a
/// claim about behaviour we are committing to, and the harness runs them all on every change.
/// </summary>
public sealed record RelevanceCase
{
    /// <summary>What the user types.</summary>
    public required string Query { get; init; }

    /// <summary>
    /// Display names that would each be a correct top hit. Multiple are allowed because several
    /// machines and several tools can legitimately satisfy one intent.
    /// </summary>
    public string[] AcceptableResults { get; init; } = [];

    /// <summary>
    /// Display names that must not be shown for this query. These guard precision: returning
    /// obvious junk is worse than returning fewer than the requested number of results.
    /// </summary>
    public string[] ForbiddenResults { get; init; } = [];

    /// <summary>
    /// Display names that must appear somewhere in the results, at any rank. Separate from
    /// <see cref="AcceptableResults"/>, which asks only that *one* of several good answers reached
    /// the top: this asks that a specific answer was not lost, which is a recall question rather
    /// than an ordering one.
    /// </summary>
    public string[] RequiredResults { get; init; } = [];

    /// <summary>When true, the correct result is an empty list with a clean no-match contract.</summary>
    public bool ExpectNoResults { get; init; }

    /// <summary>Optional ceiling for result count when the corpus is asserting cutoff behaviour.</summary>
    public int? MaxResults { get; init; }

    /// <summary>Rank the expected result must appear within.</summary>
    public int WithinTopN { get; init; } = 3;

    /// <summary>
    /// Number of trailing characters that may be removed from the query without changing the
    /// answer. The overlay searches on every keystroke, so a user does not see one result for a
    /// query - they see the sequence of results for every prefix of it, and a result that appears,
    /// vanishes and reappears as a word is finished reads as broken even when the final answer is
    /// right. Reported exactly that way: "it still changes the results between 'process',
    /// 'processe' and 'processes'".
    ///
    /// Set this and the harness re-runs the case for each shortened query, asserting the same
    /// recall and the same required results. It is opt-in because it is not universally true: one
    /// character less of "wor" is "wo", and a two-letter prefix legitimately means something else.
    /// It belongs on cases whose query is a phrase, where the last few characters finish a word
    /// the rest of the query has already established.
    /// </summary>
    public int StableTrailingCharacters { get; init; }

    /// <summary>What this case is protecting, shown when it fails.</summary>
    public string? Rationale { get; init; }
}

public static class RelevanceCorpus
{
    /// <summary>
    /// Intent phrases that share no vocabulary with the target's name. These are the cases the
    /// stock Start menu cannot answer, and the entire justification for the semantic arm.
    /// </summary>
    public static IReadOnlyList<RelevanceCase> SemanticIntent { get; } =
    [
        new()
        {
            Query = "default microphone",
            AcceptableResults = ["Microphone Privacy", "Sound Input Devices", "Sound", "Input"],
            Rationale = "Regression: usage or raw-score boosts must not outrank the best hybrid semantic+lexical microphone settings hit.",
        },
        new()
        {
            Query = "search the web",
            AcceptableResults = ["Microsoft Edge", "Edge", "Bing", "Search"],
            ForbiddenResults = ["Calculator", "Services", "ADInsight", "Phone Link"],
            Rationale = "Natural-language web intent should not be buried by unrelated frequently used apps.",
        },
        new()
        {
            Query = "todo list",
            AcceptableResults = ["Microsoft To Do", "To Do", "Sticky Notes", "Tasks"],
            WithinTopN = 1,
            Rationale = "Regression: 'list' is a word most of the index can claim and 'todo' is a word almost none can, so a command that lists running processes must not answer half the query and win on it.",
        },
        new()
        {
            Query = "file edit",
            AcceptableResults = ["Notepad", "Visual Studio Code", "WordPad", "Word", "Files", "File Explorer"],
            WithinTopN = 1,
            Rationale = "Reported as ranking Registry Editor first. A registry editor is still an editor and may appear, but a single launch of it earlier that day must not outweigh a text editor the semantic arm prefers by a wide margin on a query about files.",
        },
        new()
        {
            Query = "list processes",
            AcceptableResults = ["Tasklist", "Task Manager", "Sysinternals PsList", "PsList", "Process Explorer", "Sysinternals Process Explorer"],
            RequiredResults = ["Task Manager"],
            WithinTopN = 3,
            StableTrailingCharacters = 2,
            Rationale = "Reported as missing Task Manager. The command-line tools answer it and may lead, but the program Windows ships for looking at running processes has to be in the list. It was retrieved by both arms and rejected by both floors by a hair - 57% of the best cosine against 60%, 40% of the best BM25 against 45% - because BM25 divides by field length and Tasklist's whole summary is 'List running processes and services' while Task Manager's evidence is a sentence inside a paragraph. Raising the weight of the field holding that paragraph does not help; agreement between the arms is what distinguishes it. Also reported as unstable while typing, which is why the last two characters are asserted: the lexical arm folds 'process', 'processe' and 'processes' to identical results, so any difference between them came from cosine floors, which a half-typed word moves.",
        },
        new()
        {
            Query = "view memory usage",
            AcceptableResults = ["RAMMap", "RamMap", "Resource Monitor", "Task Manager", "Performance Monitor", "VMMap"],
            RequiredResults = ["Process Explorer"],
            WithinTopN = 5,
            Rationale = "Reported as missing Process Explorer. It was a pure source-coverage gap: the word 'memory' appears zero times in everything that had been written about it - the MSIX manifest and version resource give only its name, its Microsoft Learn page talks about handles and DLLs, and its Wikipedia lead calls it a freeware system monitor. The vector arm placed it at 0.285, which is the model recognising what its neighbours are, not evidence of what it does, and no ranking change could have fixed that. It is answered now by a source that does state the capability: the program's own View menu offers 'Physical Memory History' and 'System Information', harvested by UiResourceEnricher into the features column.",
        },
        new()
        {
            Query = "process memory usage",
            AcceptableResults = ["Resource Monitor", "Task Manager", "Performance Monitor", "VMMap", "RAMMap", "Sysinternals Process Explorer", "Process Explorer"],
            RequiredResults = ["Resource Monitor"],
            WithinTopN = 5,
            Rationale = "Reported as missing Resource Monitor, which reports per-process memory and is what Windows itself ships for this. The Sysinternals memory analyzers are accepted for the top slot because they answer it too, but Resource Monitor is required outright: it already ranks for its own name, so accepting VMMap alone would let the case pass while the reported gap remained. It was a source-coverage gap - nothing indexed about it used the word 'process', because its Wikipedia lead calls it a utility that displays hardware resource information and only the article's Features section names processes. Harvesting that section reached it and cost 'file edit', 'uninstall a program' and 0.05 MRR, because a longer details column widens the FTS candidate pool even at zero weight. What answered it instead was the program's own interface, once UiResourceEnricher learned to read the module the entry's icon names rather than the shared host it launches: the entry is resmon.exe, it resolves to perfmon.exe, and perfmon's menus describe Performance Monitor. Its icon points at wdc.dll, whose menus offer 'End Process', 'Suspend Process' and 'Analyze Wait Chain'.",
        },
        new()
        {
            Query = "todo",
            AcceptableResults = ["Microsoft To Do", "To Do"],
            RequiredResults = ["Outlook (classic)"],
            WithinTopN = 1,
            Rationale = "Reported. Users type the compound with no separator, so an entity whose own text says 'to-dos' must still be reachable from 'todo'; the app named for it still leads.",
        },
        new()
        {
            Query = "free up disk space",
            AcceptableResults = ["Disk Cleanup", "Storage", "Storage Sense", "cleanmgr"],
            Rationale = "Canonical intent query. No shared words with 'Disk Cleanup' beyond 'disk'.",
        },
        new()
        {
            Query = "make the text bigger",
            AcceptableResults = ["Text size", "Display", "Accessibility", "Magnifier"],
            Rationale = "Accessibility intent phrased the way a non-technical user says it.",
        },
        new()
        {
            Query = "my laptop battery drains too fast",
            AcceptableResults = ["Power & battery", "Power Options", "Battery saver", "powercfg"],
            Rationale = "Complaint-shaped query, not a noun. Pure lexical search cannot resolve this.",
        },
        new()
        {
            Query = "why is my battery draining",
            AcceptableResults = ["Power & battery", "Power Options", "Battery saver", "powercfg"],
            Rationale = "Shorter wording of the battery-drain complaint used for manual ranking checks.",
        },
        new()
        {
            Query = "see what is slowing down my computer",
            AcceptableResults = ["Task Manager", "Resource Monitor", "Performance Monitor", "resmon"],
            Rationale = "Diagnostic intent with zero name overlap.",
        },
        new()
        {
            Query = "record my screen",
            AcceptableResults = ["Xbox Game Bar", "Snipping Tool", "Steps Recorder", "Camera"],
        },
        new()
        {
            Query = "connect to another computer remotely",
            AcceptableResults = ["Remote Desktop Connection", "Remote Desktop", "mstsc", "Quick Assist"],
        },
        new()
        {
            Query = "stop programs starting when I boot",
            AcceptableResults = ["Startup Apps", "Task Manager", "Startup apps"],
        },
        new()
        {
            Query = "why is my internet not working",
            AcceptableResults = ["Network & internet", "Troubleshoot", "Network Connections", "ipconfig"],
        },
        new()
        {
            Query = "uninstall a program",
            AcceptableResults = ["Installed apps", "Programs and Features", "Apps & features", "appwiz"],
        },
        new()
        {
            Query = "change my password",
            AcceptableResults = ["Sign-in options", "Accounts", "Your info"],
        },
        new()
        {
            Query = "turn off notifications while I present",
            AcceptableResults = ["Focus assist", "Notifications", "Focus"],
        },
        new()
        {
            Query = "kill a process",
            AcceptableResults = ["Task Manager", "Process Explorer", "Taskkill"],
            Rationale = "Everyday phrasing for ending a process; Task Manager's profile says \"end\", not \"kill\", so this depends on curated intent vocabulary.",
        },
        new()
        {
            Query = "record my screen",
            AcceptableResults = ["Snipping Tool", "ZoomIt", "Steps Recorder", "Xbox Game Bar"],
            WithinTopN = 2,
            Rationale = "Screen-capture intent. Asserted at top 2 rather than forbidding Lock Screen: a settings page that shares the word \"screen\" is tolerable noise further down, and forbidding it invites over-fitting the ranker.",
        },
        new()
        {
            Query = "annotate the screen during a demo",
            AcceptableResults = ["ZoomIt", "Snipping Tool"],
            Rationale = "MSIX-packaged tool whose manifest description is only its own name; requires curated or online documentation.",
        },
        new()
        {
            Query = "which process has this file locked",            AcceptableResults = ["Resource Monitor", "Process Explorer", "Task Manager", "resmon", "Handle"],
            Rationale = "Expert intent; the answer is a tool most users cannot name. Handle is accepted "
                + "because it is literally the tool that answers this question - it exists to report which "
                + "process holds a handle to a file - and it became reachable once command-line tools hidden "
                + "inside installed suites were collected. Accepting it is not a relaxation: it is a stricter "
                + "answer than Resource Monitor, which requires the user to know where to look once it opens.",
        },
        new()
        {
            Query = "see what files a process has open",
            AcceptableResults = ["Process Explorer", "Resource Monitor", "Process Monitor", "handle", "OpenFiles"],
            Rationale = "Expert troubleshooting query where lexical and semantic evidence should beat generic file apps.",
        },
        new()
        {
            Query = "encrypt my hard drive",
            AcceptableResults = ["BitLocker", "Device encryption", "Manage BitLocker"],
        },
        new()
        {
            Query = "share files with a nearby pc",
            AcceptableResults = ["Nearby sharing", "Nearby Sharing", "Phone Link"],
        },
        new()
        {
            Query = "check for windows updates",
            AcceptableResults = ["Windows Update", "Update history"],
        },
        new()
        {
            Query = "change what happens when I close the lid",
            AcceptableResults = ["Power Options", "Power & battery", "powercfg"],
            Rationale = "KNOWN GAP: no profile text mentions 'lid', so every candidate sits near "
                        + "the noise floor and ordering is arbitrary. Needs an enrichment source "
                        + "that supplies lid-close vocabulary for the power pages.",
        },
        new()
        {
            Query = "change my screen resolution",
            AcceptableResults = ["Display", "Advanced display"],
            Rationale = "Display settings intent with strong profile text should rank ahead of generic screen matches.",
        },
        new()
        {
            Query = "fix a corrupted system file",
            AcceptableResults = ["System File Checker", "sfc", "dism", "chkdsk", "Recovery"],
        },
        new()
        {
            Query = "manage my printers",
            AcceptableResults = ["Printers & scanners", "Devices and Printers"],
        },

        // Reported from live use. Each one failed because the entity's synthesized description was
        // wrong, not because ranking was wrong: a Learn page title, an unrelated article, or a
        // LICENSE file had become the text that gets embedded.
        new()
        {
            Query = "kill a process",
            AcceptableResults = ["Task Manager", "Taskkill", "Process Explorer"],
            Rationale = "Any of the process-ending tools is a good answer; the command-line one leading is fine. This case exists to catch the state where none of them are reachable by intent at all.",
        },
        new()
        {
            Query = "suspend a process",
            AcceptableResults = ["Process Explorer", "Task Manager", "Resource Monitor", "PsSuspend"],
            ForbiddenResults = ["Process Monitor"],
            Rationale = "Process Monitor traces activity and cannot suspend; Process Explorer can. PsSuspend "
                + "is accepted because suspending a process is the only thing it does, and it now reaches the "
                + "index. The forbidden entry is what carries this case: the point has always been that a "
                + "tool must be able to perform the action, not merely share vocabulary with it.",
        },
        new()
        {
            Query = "set low power",
            AcceptableResults = ["Battery Saver", "Power Options", "Power & Battery", "Power Configuration"],
            ForbiddenResults = ["Power Automate"],
            Rationale = "Power Automate shares only the word 'Power' and was described by its licensing limits page.",
        },
        new()
        {
            Query = "end a frozen app",
            AcceptableResults = ["Task Manager", "Taskkill"],
            Rationale = "Second phrasing of the same intent, sharing no words with the first, so a fix cannot be a single lucky vocabulary overlap. Either process-ending tool is a good answer.",
        },
        new()
        {
            Query = "edit a file",
            AcceptableResults = ["Notepad", "Visual Studio Code", "WordPad"],
            ForbiddenResults = ["Registry Editor", "Local Group Policy Editor"],
            Rationale = "VS Code was summarised by its LICENSE file, so nothing in its text said 'editor'. The forbidden entries edit a specific system store rather than files, and were matching purely because '-editor' yields the verb 'edit'; the object of the query has to count for something.",
        },
        new()
        {
            Query = "edit",
            AcceptableResults = ["Notepad"],
            WithinTopN = 5,
            Rationale = "Reported as missing Notepad, Paint and Clipchamp. A bare verb returned only entities named '<something> Editor', all of whose summaries merely restate their own name ('Open Registry Editor.'). Those placeholder summaries occupy the highest-weighted intent fields while carrying no information, and being three words long BM25 inflates them enormously - which both outranks tools that genuinely edit things and raises the pruning floor so far that Notepad, Paint and Clipchamp are dropped entirely. Asserting Notepad alone because the harness can only require one of a set; it is the most canonical of the three.",
        },
        new()
        {
            Query = "edit doc",
            AcceptableResults = ["Visual Studio Code"],
            WithinTopN = 5,
            Rationale = "Reported as missing Visual Studio Code. Deliberately does not accept Notepad or Word, which already rank and would let the case pass while the reported gap remained. VS Code ranks first for 'edit code' and 'code editor', so its profile is sound; the shortfall is that nothing in its indexed text relates it to documents.",
        },
        new()
        {
            Query = "check access",
            AcceptableResults = ["AccessChk"],
            Rationale = "Reported. Deliberately does not accept AccessEnum, which already ranks first and would mask the gap. AccessChk reports effective permissions and is the exact answer. It was originally unreachable: a console tool shipped inside an installed suite, invisible to the AppsFolder and outside the system-tool collector's System32 scan. Alias collection closed that gap and it now ranks fourth, so what remains is genuinely a ranking problem - AccessEnum and AccessChk have near-identical documentation, and nothing in either distinguishes enumerating shares from reporting effective permissions.",
        },
        new()
        {
            Query = "vsco",
            AcceptableResults = ["Visual Studio Code"],
            WithinTopN = 1,
            Rationale = "Reported: 'vsc' found Visual Studio Code but 'vsco' and 'vscod' made it vanish, so the target disappeared mid-word and only returned at 'vscode'. The pure-initials test stopped matching after the third keystroke and the subsequence fallback scores below the surfacing bar, so it could never bring anything back on its own. Every prefix of a name the user is typing has to keep working.",
        },
        new()
        {
            Query = "msinfo32",
            AcceptableResults = ["System Information"],
            WithinTopN = 1,
            Rationale = "People address a program by the name it has on disk. This returned WOW64 before the launch target was matched literally, because nothing in the display name or documentation contains the string 'msinfo32'.",
        },
        new()
        {
            Query = "secpol",
            AcceptableResults = ["Local Security Policy"],
            WithinTopN = 1,
            Rationale = "Same defect as msinfo32 for an MMC snap-in rather than an executable, which is why the check covers .msc and .cpl targets and not just .exe.",
        },
        new()
        {
            Query = "write code",
            AcceptableResults = ["Visual Studio Code"],
            ForbiddenResults = ["Microsoft Visual Studio Code (User)"],
            Rationale = "Found while diagnosing the reports above. One installed copy of VS Code is indexed twice: the AppsFolder entry carries a real description, while the uninstall-registry entry has only the placeholder 'Open Microsoft Visual Studio Code (User).' - and the placeholder one ranks higher, because a short summary of pure name tokens sits closer to a short query than real prose does. Deduplication compares display names only, so it never noticed that both resolve to the same Code.exe.",
        },
        new()
        {
            Query = "ask ai",
            AcceptableResults = ["Copilot", "GitHub Copilot", "Foundry Local CLI", "Claude"],
            Rationale = "Copilot had no description beyond 'Open Copilot.'",
        },
        new()
        {
            Query = "record a video",
            AcceptableResults = ["Microsoft Clipchamp", "Clipchamp", "Camera", "ZoomIt", "Sound Recorder"],
            Rationale = "Clipchamp was described by a Microsoft 365 'video analytics' page title.",
        },
    ];

    /// <summary>
    /// Literal-name queries. These guard the regression that matters most: adding semantics must
    /// never make the product worse than the Start menu at the thing users do constantly.
    /// </summary>
    public static IReadOnlyList<RelevanceCase> LiteralName { get; } =
    [
        new()
        {
            Query = "powerpoint",
            AcceptableResults = ["PowerPoint"],
            ForbiddenResults = ["Calculator"],
            WithinTopN = 1,
            Rationale = "An exact Office app name should not be padded with unrelated vector-only apps.",
        },
        new()
        {
            Query = "microphone",
            AcceptableResults = ["Microphone Privacy", "Sound Input Devices", "Sound"],
            WithinTopN = 1,
            Rationale = "Single-token literal prefix must keep Start-menu-style behaviour.",
        },
        new()
        {
            Query = "notepad",
            AcceptableResults = ["Notepad"],
            WithinTopN = 1,
            Rationale = "An exact name must be rank 1. Non-negotiable.",
        },
        new()
        {
            Query = "task manager",
            AcceptableResults = ["Task Manager"],
            WithinTopN = 1,
        },
        new()
        {
            Query = "calc",
            AcceptableResults = ["Calculator"],
            WithinTopN = 2,
            Rationale = "Prefix of a common app.",
        },
        new()
        {
            Query = "cmd",
            AcceptableResults = ["Command Prompt", "cmd"],
            WithinTopN = 2,
        },
        new()
        {
            Query = "devmgmt",
            AcceptableResults = ["Device Manager"],
            WithinTopN = 2,
            Rationale = "Users type the snap-in filename, not the friendly name.",
        },
        new()
        {
            Query = "regedit",
            AcceptableResults = ["Registry Editor", "regedit"],
            WithinTopN = 2,
        },
    ];

    /// <summary>
    /// Short prefixes typed mid-keystroke. The overlay updates on every character, so these must
    /// stay correct even though the string is far too short to embed meaningfully.
    /// </summary>
    public static IReadOnlyList<RelevanceCase> Prefix { get; } =
    [
        new()
        {
            Query = "wor",
            AcceptableResults = ["Word"],
            WithinTopN = 1,
            Rationale = "Short Office app prefix must be rank 1 even though the token is poor semantic input.",
        },
        new()
        {
            Query = "notep",
            AcceptableResults = ["Notepad"],
            WithinTopN = 1,
            Rationale = "Partial Notepad prefix must prefer the app over optional feature profile text.",
        },
        new() { Query = "not", AcceptableResults = ["Notepad", "Notifications"], WithinTopN = 3 },
        new() { Query = "tas", AcceptableResults = ["Task Manager", "Task Scheduler", "Taskbar"], WithinTopN = 3 },
        new() { Query = "blue", AcceptableResults = ["Bluetooth & devices", "Bluetooth"], WithinTopN = 3 },
    ];

    /// <summary>
    /// Precision and no-result contracts. These intentionally include content-gap cases where the
    /// right answer has not yet been described well enough by the index; the ranking layer should
    /// prefer returning nothing over filling the list with generic-token or vector-noise matches.
    /// </summary>
    public static IReadOnlyList<RelevanceCase> Precision { get; } =
    [
        new()
        {
            Query = "create presentation",
            AcceptableResults = ["PowerPoint"],
            WithinTopN = 1,
            ForbiddenResults = ["Notepad", "Hyper-V", "Scheduled Tasks", "Claude"],
            Rationale = "PowerPoint must lead. The original form of this case instead capped the result list at one, which failed even when PowerPoint ranked first, because a second plausible entry followed it; that asserted a property of the pruner rather than of the ranking, and the forbidden list already covers the results that would be genuinely wrong.",
        },
        new()
        {
            Query = "asdfghjkl",
            ExpectNoResults = true,
            Rationale = "A nonsense query should produce the empty-state path, not MiniLM noise.",
        },
    ];

    public static IReadOnlyList<RelevanceCase> All { get; } =
        [.. SemanticIntent, .. LiteralName, .. Prefix, .. Precision];
}
