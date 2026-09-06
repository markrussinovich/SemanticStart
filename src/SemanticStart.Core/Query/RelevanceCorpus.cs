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

    /// <summary>When true, the correct result is an empty list with a clean no-match contract.</summary>
    public bool ExpectNoResults { get; init; }

    /// <summary>Optional ceiling for result count when the corpus is asserting cutoff behaviour.</summary>
    public int? MaxResults { get; init; }

    /// <summary>Rank the expected result must appear within.</summary>
    public int WithinTopN { get; init; } = 3;

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
                        + "the noise floor and ordering is arbitrary. Expected to pass once LLM "
                        + "synthesis (rather than the heuristic fallback) generates task phrases.",
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
