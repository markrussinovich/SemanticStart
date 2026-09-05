using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Collectors;

public sealed class StartShortcutCollector : IEntityCollector
{
    private const int MaxPath = 260;

    public string Source => "startmenu";

    public bool IsSupported => OperatingSystem.IsWindows() && GetRoots().Any(Directory.Exists);

    public async IAsyncEnumerable<Entity> CollectAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        if (!IsSupported)
            yield break;

        foreach (var root in GetRoots().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                    .Where(p => p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entity = TryCreateEntity(file, root);
                if (entity is not null)
                    yield return entity;
            }
        }
    }

    private Entity? TryCreateEntity(string path, string root)
    {
        try
        {
            var displayName = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(displayName))
                return null;

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            var folder = GetStartMenuFolder(path, root);
            if (!string.IsNullOrWhiteSpace(folder))
                metadata["startMenuFolder"] = folder;

            string? icon = null;
            string? arguments = null;

            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var info = ReadShellLink(path);
                if (!string.IsNullOrWhiteSpace(info.TargetPath))
                    metadata["targetPath"] = info.TargetPath;
                if (!string.IsNullOrWhiteSpace(info.WorkingDirectory))
                    metadata["workingDirectory"] = info.WorkingDirectory;
                if (!string.IsNullOrWhiteSpace(info.Comment))
                    metadata["comment"] = info.Comment;
                if (!string.IsNullOrWhiteSpace(info.IconLocation))
                    icon = info.IconLocation;
                if (!string.IsNullOrWhiteSpace(info.Arguments))
                    arguments = info.Arguments;
            }
            else
            {
                var url = ReadInternetShortcutUrl(path);
                if (!string.IsNullOrWhiteSpace(url))
                    metadata["url"] = url;
            }

            var entity = new Entity
            {
                Id = EntityId.Create(Source, path),
                Kind = EntityKind.Application,
                DisplayName = displayName,
                LaunchKind = LaunchKind.Shortcut,
                LaunchTarget = path,
                LaunchArguments = arguments,
                IconSource = icon,
                Source = Source,
                RawMetadata = metadata,
            };

            return CollectorEntity.WithContentHash(entity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static ShellLinkInfo ReadShellLink(string path)
    {
        object? shellLinkObject = null;
        try
        {
            shellLinkObject = new ShellLink();
            var persistFile = (IPersistFile)shellLinkObject;
            persistFile.Load(path, 0);
            var shellLink = (IShellLinkW)shellLinkObject;

            var target = new StringBuilder(MaxPath);
            var args = new StringBuilder(1024);
            var description = new StringBuilder(1024);
            var workingDirectory = new StringBuilder(MaxPath);
            var icon = new StringBuilder(MaxPath);
            var findData = new WIN32_FIND_DATAW();

            _ = shellLink.GetPath(target, target.Capacity, ref findData, 0);
            _ = shellLink.GetArguments(args, args.Capacity);
            _ = shellLink.GetDescription(description, description.Capacity);
            _ = shellLink.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);
            _ = shellLink.GetIconLocation(icon, icon.Capacity, out var iconIndex);

            var iconLocation = icon.ToString();
            if (!string.IsNullOrWhiteSpace(iconLocation) && iconIndex != 0)
                iconLocation = $"{iconLocation},{iconIndex}";

            return new ShellLinkInfo(target.ToString(), args.ToString(), description.ToString(), workingDirectory.ToString(), iconLocation);
        }
        finally
        {
            if (shellLinkObject is not null)
                Marshal.FinalReleaseComObject(shellLinkObject);
        }
    }

    private static string? ReadInternetShortcutUrl(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                return line[4..].Trim();
        }

        return null;
    }

    private static string? GetStartMenuFolder(string path, string root)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent) || parent.Equals(root, StringComparison.OrdinalIgnoreCase))
            return null;

        return Path.GetFileName(parent);
    }

    private static IEnumerable<string> GetRoots()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
            yield return Path.Combine(programData, "Microsoft", "Windows", "Start Menu", "Programs");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
            yield return Path.Combine(appData, "Microsoft", "Windows", "Start Menu", "Programs");
    }

    private sealed record ShellLinkInfo(string TargetPath, string Arguments, string Comment, string WorkingDirectory, string IconLocation);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, ref WIN32_FIND_DATAW pfd, uint fFlags);
        int GetIDList(out IntPtr ppidl);
        int SetIDList(IntPtr pidl);
        int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        int GetHotkey(out short pwHotkey);
        int SetHotkey(short wHotkey);
        int GetShowCmd(out int piShowCmd);
        int SetShowCmd(int iShowCmd);
        int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        int Resolve(IntPtr hwnd, uint fFlags);
        int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint DwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME FtCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME FtLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME FtLastWriteTime;
        public uint NFileSizeHigh;
        public uint NFileSizeLow;
        public uint DwReserved0;
        public uint DwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)] public string CFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string CAlternateFileName;
    }
}
