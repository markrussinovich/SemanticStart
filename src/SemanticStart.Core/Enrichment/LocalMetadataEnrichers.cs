using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Enrichment;

public sealed class ShortcutMetadataEnricher : IEnricher
{
    public string Provider => "shortcut";
    public bool RequiresNetwork => false;
    public bool CanEnrich(Entity entity) => entity.RawMetadata.ContainsKey("comment") || entity.RawMetadata.ContainsKey("startMenuFolder");

    public Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lines = new List<string>();
        if (entity.RawMetadata.TryGetValue("comment", out var comment) && !string.IsNullOrWhiteSpace(comment)) lines.Add(comment.Trim());
        if (entity.RawMetadata.TryGetValue("startMenuFolder", out var folder) && !string.IsNullOrWhiteSpace(folder))
        {
            var parts = folder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0) lines.Add($"Categorized under {string.Join(", ", parts)} in the Start Menu.");
        }
        return Task.FromResult<IReadOnlyList<EnrichmentDocument>>(lines.Count == 0 ? [] : [new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = false, Text = string.Join(Environment.NewLine, lines) }]);
    }
}

public sealed class AdjacentDocsEnricher : IEnricher
{
    private const int MaxFileBytes = 256 * 1024;
    private const int MaxReadChars = 8 * 1024;
    public string Provider => "local-docs";
    public bool RequiresNetwork => false;
    public bool CanEnrich(Entity entity) => ResolveInstallDirectory(entity) is not null;

    public async Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        var dir = ResolveInstallDirectory(entity);
        if (dir is null) return [];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        var docs = new List<EnrichmentDocument>();
        try
        {
            foreach (var file in EnumerateCandidates(dir).Take(8))
            {
                cts.Token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (file.EndsWith(".chm", StringComparison.OrdinalIgnoreCase))
                {
                    docs.Add(new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = false, Text = $"Local help file available: {name}.", SourceUri = file });
                    continue;
                }
                var info = new FileInfo(file);
                if (!info.Exists || info.Length <= 0 || info.Length > MaxFileBytes) continue;
                var text = await File.ReadAllTextAsync(file, cts.Token).ConfigureAwait(false);
                text = StripMarkdown(text);
                if (text.Length > MaxReadChars) text = text[..MaxReadChars];
                if (!string.IsNullOrWhiteSpace(text)) docs.Add(new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = false, Text = $"{name}: {text}", SourceUri = file });
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or NotSupportedException) { }
        return docs;
    }

    private static IEnumerable<string> EnumerateCandidates(string root)
    {
        var dirs = new Queue<(string Path, int Depth)>();
        dirs.Enqueue((root, 0));
        while (dirs.Count > 0)
        {
            var (dir, depth) = dirs.Dequeue();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { continue; }
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("README", StringComparison.OrdinalIgnoreCase) || name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LICENSE.", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".chm", StringComparison.OrdinalIgnoreCase)) yield return file;
            }
            if (depth >= 1) continue;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { continue; }
            foreach (var child in children.Take(12)) dirs.Enqueue((child, depth + 1));
        }
    }

    private static string StripMarkdown(string text)
    {
        text = Regex.Replace(text, @"```[\s\S]*?```", " ");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^\)]+\)", " ");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^\)]+\)", "$1");
        text = Regex.Replace(text, @"^[#>*\-\s]+", "", RegexOptions.Multiline);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string? ResolveInstallDirectory(Entity entity)
    {
        foreach (var candidate in new[] { entity.LaunchTarget, entity.IconSource, entity.RawMetadata.GetValueOrDefault("targetPath"), entity.RawMetadata.GetValueOrDefault("workingDirectory") })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                var path = Environment.ExpandEnvironmentVariables(candidate.Split(',')[0].Trim('"'));
                if (Directory.Exists(path)) return IsNoisyWindowsDirectory(path) ? null : path;
                if (File.Exists(path))
                {
                    var dir = Path.GetDirectoryName(path);
                    return dir is null || IsNoisyWindowsDirectory(dir) ? null : dir;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return null;
    }

    private static bool IsNoisyWindowsDirectory(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows)) return false;
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var system32 = Path.Combine(windows, "System32");
        var sysWow64 = Path.Combine(windows, "SysWOW64");
        return full.Equals(Path.GetFullPath(windows).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || full.Equals(Path.GetFullPath(system32).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || full.Equals(Path.GetFullPath(sysWow64).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class CliHelpEnricher : IEnricher
{
    private static readonly IReadOnlyDictionary<string, string[]> Arguments = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["ipconfig.exe"] = ["/?"], ["ping.exe"] = ["/?"], ["tracert.exe"] = ["/?"], ["netstat.exe"] = ["/?"], ["nslookup.exe"] = ["/?"], ["route.exe"] = ["/?"], ["arp.exe"] = ["/?"], ["netsh.exe"] = ["/?"], ["sfc.exe"] = ["/?"], ["dism.exe"] = ["/?"], ["chkdsk.exe"] = ["/?"], ["diskpart.exe"] = ["/?"], ["tasklist.exe"] = ["/?"], ["taskkill.exe"] = ["/?"], ["systeminfo.exe"] = ["/?"], ["whoami.exe"] = ["/?"], ["gpupdate.exe"] = ["/?"], ["gpresult.exe"] = ["/?"], ["robocopy.exe"] = ["/?"], ["xcopy.exe"] = ["/?"], ["schtasks.exe"] = ["/?"], ["sc.exe"] = ["/?"], ["reg.exe"] = ["/?"], ["bcdedit.exe"] = ["/?"], ["powercfg.exe"] = ["/?"], ["driverquery.exe"] = ["/?"], ["pathping.exe"] = ["/?"], ["certutil.exe"] = ["/?"], ["cipher.exe"] = ["/?"], ["compact.exe"] = ["/?"], ["fsutil.exe"] = ["/?"], ["icacls.exe"] = ["/?"], ["takeown.exe"] = ["/?"], ["shutdown.exe"] = ["/?"]
    };
    public string Provider => "cli-help";
    public bool RequiresNetwork => false;
    public bool CanEnrich(Entity entity)
    {
        var path = ResolveSafeSystem32Path(entity);
        return entity.Kind == EntityKind.SystemTool && path is not null && Arguments.ContainsKey(Path.GetFileName(path));
    }
    public async Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        var path = ResolveSafeSystem32Path(entity);
        if (path is null || !Arguments.TryGetValue(Path.GetFileName(path), out var args)) return [];
        try
        {
            var result = await ProcessRunner.RunAsync(path, args, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            var text = result.Output.Trim();
            if (text.Length > 4096) text = text[..4096];
            return string.IsNullOrWhiteSpace(text) ? [] : [new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = false, Text = text, SourceUri = path }];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return []; }
    }
    private static string? ResolveSafeSystem32Path(Entity entity)
    {
        try
        {
            var path = Environment.ExpandEnvironmentVariables(entity.LaunchTarget.Trim('"'));
            if (!File.Exists(path)) return null;
            var full = Path.GetFullPath(path);
            var system32 = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.System)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(system32, StringComparison.OrdinalIgnoreCase)) return null;
            return full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}

internal static class ProcessRunner
{
    public static async Task<(int ExitCode, string Output)> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var waitTask = process.WaitForExitAsync(cancellationToken);
        var finished = await Task.WhenAny(waitTask, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false) == waitTask;
        if (!finished)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (-1, output.ToString());
        }
        await waitTask.ConfigureAwait(false);
        return (process.ExitCode, output.ToString());
    }
}
