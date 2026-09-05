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
    public required string[] AcceptableResults { get; init; }

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
            Query = "which process has this file locked",
            AcceptableResults = ["Resource Monitor", "Process Explorer", "Task Manager", "resmon"],
            Rationale = "Expert intent; the answer is a tool most users cannot name.",
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
            Query = "fix a corrupted system file",
            AcceptableResults = ["System File Checker", "sfc", "dism", "chkdsk", "Recovery"],
        },
        new()
        {
            Query = "manage my printers",
            AcceptableResults = ["Printers & scanners", "Devices and Printers"],
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
        new() { Query = "not", AcceptableResults = ["Notepad", "Notifications"], WithinTopN = 3 },
        new() { Query = "tas", AcceptableResults = ["Task Manager", "Task Scheduler", "Taskbar"], WithinTopN = 3 },
        new() { Query = "blue", AcceptableResults = ["Bluetooth & devices", "Bluetooth"], WithinTopN = 3 },
    ];

    public static IReadOnlyList<RelevanceCase> All { get; } =
        [.. SemanticIntent, .. LiteralName, .. Prefix];
}
