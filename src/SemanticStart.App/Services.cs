using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SemanticStart.Core;
using SemanticStart.Core.Model;

namespace SemanticStart.App;

/// <summary>
/// Produces the real icon for an entity.
///
/// Extraction goes through <c>IShellItemImageFactory</c>, which is what Start itself uses. That
/// matters for two reasons. It accepts a shell parsing name rather than a file path, so
/// <c>shell:AppsFolder\{AppUserModelId}</c> resolves directly — most indexed applications have no
/// usable file path at all. And it returns the icon a packaged (MSIX/UWP) app actually ships,
/// which is a set of PNG assets referenced by the manifest; those apps have no embedded ICO
/// resource, so the older <c>SHGetFileInfo</c>/<c>ExtractAssociatedIcon</c> path can never render
/// them and silently falls back to a generic glyph.
/// </summary>
public sealed class IconProvider : IDisposable
{
    /// <summary>
    /// Icons are decoded at this pixel size. The list renders them at 26 DIP, so this covers
    /// display scaling up to 350% without resampling artefacts.
    /// </summary>
    private const int IconPixelSize = 96;
    private const int WorkerCount = 4;

    private readonly Dictionary<string, ImageSource?> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread[] _workers;

    public IconProvider()
    {
        // Shell imaging handlers are apartment-sensitive, so extraction cannot use arbitrary
        // thread-pool threads. A small set of dedicated STA workers lets the visible result icons
        // resolve concurrently without overwhelming shell extensions.
        _workers = new Thread[WorkerCount];
        for (var i = 0; i < _workers.Length; i++)
        {
            var worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"SemanticStart.IconExtraction.{i + 1}",
                Priority = ThreadPriority.BelowNormal,
            };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            _workers[i] = worker;
        }
    }

    public Task<ImageSource?> GetIconAsync(Entity entity, CancellationToken cancellationToken)
    {
        lock (_memoryCache)
        {
            if (_memoryCache.TryGetValue(entity.Id, out var cached))
                return Task.FromResult(cached);
        }

        var completion = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Job()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                var image = Resolve(entity);
                lock (_memoryCache)
                    _memoryCache[entity.Id] = image;
                completion.TrySetResult(image);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Icon extraction failed for {entity.Id}");
                completion.TrySetResult(null);
            }
        }

        try
        {
            _work.Add(Job, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            completion.TrySetResult(null);
        }

        return completion.Task;
    }

    private void WorkerLoop()
    {
        foreach (var job in _work.GetConsumingEnumerable())
        {
            try
            {
                job();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Icon worker job failed");
            }
        }
    }

    private static ImageSource? Resolve(Entity entity)
    {
        var cachePath = GetCachePath(entity.Id);
        if (File.Exists(cachePath))
            return LoadBitmap(cachePath);

        var image = ExtractFromShell(entity);
        if (image is null)
            return null;

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(cachePath);
            encoder.Save(stream);
        }
        catch (IOException ex)
        {
            // A cache write failure must not cost the user their icon.
            Log.Error(ex, $"Icon cache write failed for {entity.Id}");
        }

        return image;
    }

    /// <summary>
    /// Walks the candidate parsing names for an entity and returns the first icon the shell can
    /// produce. Ordering matters: the entity's own identity is tried before any per-kind fallback,
    /// so a real app icon always beats a generic Settings gear.
    /// </summary>
    private static BitmapSource? ExtractFromShell(Entity entity)
    {
        foreach (var name in ParsingNames(entity))
        {
            var image = TryGetShellImage(name);
            if (image is not null)
                return image;
        }

        return null;
    }

    private static IEnumerable<string> ParsingNames(Entity entity)
    {
        if (entity.LaunchKind == LaunchKind.AppsFolder && !string.IsNullOrWhiteSpace(entity.LaunchTarget))
            yield return @"shell:AppsFolder\" + entity.LaunchTarget;

        foreach (var candidate in new[] { entity.IconSource, entity.LaunchTarget })
        {
            var path = NormalizePath(candidate);
            if (path is not null)
                yield return path;
        }

        // Kind-based fallbacks. These are deliberately last: they are correct but generic, and
        // would otherwise shadow a better icon.
        var windows = Path.GetDirectoryName(Environment.SystemDirectory);
        var fallback = entity.Kind switch
        {
            EntityKind.SettingsPage or EntityKind.OptionalFeature when windows is not null =>
                Path.Combine(windows, "ImmersiveControlPanel", "SystemSettings.exe"),
            EntityKind.ControlPanelApplet => Path.Combine(Environment.SystemDirectory, "control.exe"),
            EntityKind.ManagementConsole => Path.Combine(Environment.SystemDirectory, "mmc.exe"),
            EntityKind.ShellLocation when windows is not null => Path.Combine(windows, "explorer.exe"),
            _ => null,
        };

        if (fallback is not null && File.Exists(fallback))
            yield return fallback;
    }

    /// <summary>
    /// Reduces a stored icon reference to a real file. Registry <c>DisplayIcon</c> values and
    /// shortcut icon locations routinely carry a trailing resource index ("app.exe,3") and may be
    /// quoted, neither of which the shell accepts as a parsing name.
    /// </summary>
    private static string? NormalizePath(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        var path = candidate.Trim().Trim('"');

        var comma = path.LastIndexOf(',');
        if (comma > 0 && int.TryParse(path[(comma + 1)..].Trim(), out _))
            path = path[..comma].Trim().Trim('"');

        if (path.Length == 0)
            return null;

        try
        {
            path = Environment.ExpandEnvironmentVariables(path);
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static BitmapSource? TryGetShellImage(string parsingName)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        IShellItemImageFactory? factory = null;
        var bitmap = IntPtr.Zero;

        try
        {
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out factory) != 0 || factory is null)
                return null;

            var size = new Size { Cx = IconPixelSize, Cy = IconPixelSize };
            if (factory.GetImage(size, SiigbfIconOnly | SiigbfBiggerSizeOk, out bitmap) != 0 || bitmap == IntPtr.Zero)
                return null;

            return ConvertBitmap(bitmap);
        }
        catch (COMException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);
            if (factory is not null)
                Marshal.ReleaseComObject(factory);
        }
    }

    private static BitmapSource? ConvertBitmap(IntPtr hBitmap)
    {
        var info = new BitmapInfo();
        if (GetObject(hBitmap, Marshal.SizeOf<BitmapInfo>(), ref info) == 0)
            return null;

        if (info.BitsPixel != 32 || info.Bits == IntPtr.Zero || info.Width <= 0 || info.Height <= 0)
            return null;

        var stride = info.WidthBytes;
        var buffer = new byte[stride * info.Height];
        Marshal.Copy(info.Bits, buffer, 0, buffer.Length);

        if (IsBottomUp(hBitmap))
            FlipRows(buffer, stride, info.Height);

        // Some icon sources hand back a fully transparent alpha channel for what is really an
        // opaque image. Rendering that as-is produces an invisible icon, which is indistinguishable
        // from having no icon at all, so treat an all-zero alpha channel as opaque.
        var hasAlpha = false;
        for (var i = 3; i < buffer.Length; i += 4)
        {
            if (buffer[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }

        if (!hasAlpha)
        {
            for (var i = 3; i < buffer.Length; i += 4)
                buffer[i] = 255;
        }

        var source = BitmapSource.Create(
            info.Width, info.Height, 96, 96, PixelFormats.Pbgra32, null, buffer, stride);
        source.Freeze();
        return source;
    }

    /// <summary>
    /// True when the bitmap's pixel rows are stored bottom-to-top.
    /// <para>
    /// A <c>BITMAP</c> from <c>GetObject</c> reports only a positive height and says nothing about
    /// row order, so it cannot be used to decide this. Orientation lives in the DIB section's
    /// <c>biHeight</c>, which is negative for top-down bitmaps and positive for bottom-up ones.
    /// Icon sources differ: packaged-app PNG assets come back top-down while some icons extracted
    /// from executable resources come back bottom-up, which rendered those icons upside down.
    /// </para>
    /// </summary>
    private static bool IsBottomUp(IntPtr hBitmap)
    {
        var section = new DibSection();
        var size = Marshal.SizeOf<DibSection>();
        return GetObject(hBitmap, size, ref section) == size && section.Header.Height > 0;
    }

    private static void FlipRows(byte[] buffer, int stride, int height)
    {
        var row = new byte[stride];
        for (var top = 0; top < height / 2; top++)
        {
            var bottom = height - 1 - top;
            Buffer.BlockCopy(buffer, top * stride, row, 0, stride);
            Buffer.BlockCopy(buffer, bottom * stride, buffer, top * stride, stride);
            Buffer.BlockCopy(row, 0, buffer, bottom * stride, stride);
        }
    }

    /// <summary>
    /// Bumped whenever icon decoding changes, so stale cache entries produced by an earlier
    /// (buggy) decode are ignored instead of masking the fix.
    /// </summary>
    private const int IconCacheVersion = 2;

    private static string GetCachePath(string id)
    {
        AppPaths.EnsureCreated();
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"v{IconCacheVersion}:{id}"))).ToLowerInvariant();
        return Path.Combine(AppPaths.IconCacheDirectory, hash + ".png");
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.DecodePixelWidth = IconPixelSize;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public void Dispose()
    {
        _work.CompleteAdding();
    }

    private const int SiigbfBiggerSizeOk = 0x00000001;
    private const int SiigbfIconOnly = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DibSection
    {
        public BitmapInfo Bitmap;
        public BitmapInfoHeader Header;
        public uint Bitfield0;
        public uint Bitfield1;
        public uint Bitfield2;
        public IntPtr Section;
        public uint Offset;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(Size size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int count, ref BitmapInfo info);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr handle, int count, ref DibSection section);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
