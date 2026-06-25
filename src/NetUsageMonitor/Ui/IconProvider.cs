using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetUsageMonitor.Native;

namespace NetUsageMonitor.Ui;

/// <summary>
/// Extracts and caches application icons (as frozen, thread-safe <see cref="ImageSource"/>s) keyed by
/// executable path — the same icons Explorer/Task Manager show.
/// </summary>
public sealed class IconProvider
{
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private ImageSource? _genericIcon;
    private bool _genericResolved;

    public ImageSource? GetIcon(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath))
            return GenericIcon;

        return _cache.GetOrAdd(exePath, path =>
        {
            try
            {
                if (!File.Exists(path))
                    return GenericIcon;
                return ExtractSmallIcon(path, useFileAttributes: false) ?? GenericIcon;
            }
            catch
            {
                return GenericIcon;
            }
        });
    }

    private ImageSource? GenericIcon
    {
        get
        {
            if (!_genericResolved)
            {
                try { _genericIcon = ExtractSmallIcon("application.exe", useFileAttributes: true); }
                catch { _genericIcon = null; }
                _genericResolved = true;
            }
            return _genericIcon;
        }
    }

    private static ImageSource? ExtractSmallIcon(string path, bool useFileAttributes)
    {
        var info = new NativeMethods.SHFILEINFO();
        uint flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON;
        if (useFileAttributes) flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;

        IntPtr result = NativeMethods.SHGetFileInfoW(
            path,
            useFileAttributes ? NativeMethods.FILE_ATTRIBUTE_NORMAL : 0,
            ref info,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
            flags);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); // make it usable from any thread
            return source;
        }
        finally
        {
            NativeMethods.DestroyIcon(info.hIcon);
        }
    }
}
