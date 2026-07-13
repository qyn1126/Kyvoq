using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kyvoq.Core.Models;

namespace Kyvoq.App.Services;

/// <summary>
/// 异步提取 Shell 图标，并维护内存与磁盘两级缓存。
/// </summary>
public sealed class IconCacheService
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const int DefaultMaximumMemoryEntries = 512;
    private readonly string cacheDirectory;
    private readonly int maximumMemoryEntries;
    private readonly object memoryCacheSync = new();
    private readonly Dictionary<string, MemoryCacheEntry> memoryCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> usageOrder = new();

    /// <summary>
    /// 获取当前内存缓存条目数量，供容量回归测试使用。
    /// </summary>
    internal int MemoryCacheCount
    {
        get
        {
            lock (memoryCacheSync)
            {
                return memoryCache.Count;
            }
        }
    }

    /// <summary>
    /// 使用指定磁盘目录创建图标缓存。
    /// </summary>
    /// <param name="cacheDirectory">PNG 缩略图缓存目录。</param>
    /// <param name="maximumMemoryEntries">内存中最多保留的成功图标数量。</param>
    public IconCacheService(
        string cacheDirectory,
        int maximumMemoryEntries = DefaultMaximumMemoryEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        if (maximumMemoryEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMemoryEntries));
        }

        this.cacheDirectory = Path.GetFullPath(cacheDirectory);
        this.maximumMemoryEntries = maximumMemoryEntries;
    }

    /// <summary>
    /// 异步获取项目图标，优先读取缓存。
    /// </summary>
    /// <param name="item">需要显示图标的启动项目。</param>
    /// <returns>冻结后可跨线程使用的图像；提取失败时返回空。</returns>
    public async Task<ImageSource?> GetIconAsync(LauncherItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var key = CreateCacheKey(item);
        Lazy<Task<ImageSource?>> loader;
        lock (memoryCacheSync)
        {
            if (memoryCache.TryGetValue(key, out var cachedEntry))
            {
                usageOrder.Remove(cachedEntry.UsageNode);
                usageOrder.AddLast(cachedEntry.UsageNode);
                loader = cachedEntry.Loader;
            }
            else
            {
                loader = new Lazy<Task<ImageSource?>>(
                    () => LoadOrExtractAsync(item.Clone(), key),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                var usageNode = usageOrder.AddLast(key);
                memoryCache.Add(key, new MemoryCacheEntry(loader, usageNode));
                while (memoryCache.Count > maximumMemoryEntries)
                {
                    var oldestNode = usageOrder.First!;
                    usageOrder.RemoveFirst();
                    memoryCache.Remove(oldestNode.Value);
                }
            }
        }

        ImageSource? source;
        try
        {
            source = await loader.Value.ConfigureAwait(false);
        }
        catch
        {
            RemoveMemoryEntry(key, loader);
            throw;
        }

        if (source is null)
        {
            RemoveMemoryEntry(key, loader);
        }

        return source;
    }

    /// <summary>
    /// 仅当缓存键仍对应指定加载任务时移除条目，避免并发重试误删新结果。
    /// </summary>
    /// <param name="key">需要移除的缓存键。</param>
    /// <param name="loader">预期仍与缓存键关联的加载任务。</param>
    private void RemoveMemoryEntry(string key, Lazy<Task<ImageSource?>> loader)
    {
        lock (memoryCacheSync)
        {
            if (!memoryCache.TryGetValue(key, out var entry)
                || !ReferenceEquals(entry.Loader, loader))
            {
                return;
            }

            memoryCache.Remove(key);
            usageOrder.Remove(entry.UsageNode);
        }
    }

    /// <summary>
    /// 清理超过指定数量或长时间未使用的磁盘图标。
    /// </summary>
    /// <param name="maximumFiles">最多保留的缓存文件数量。</param>
    /// <param name="maximumAge">缓存文件的最长保留时间。</param>
    /// <returns>表示清理操作的任务。</returns>
    public Task TrimAsync(int maximumFiles = 5000, TimeSpan? maximumAge = null) => Task.Run(() =>
    {
        if (!Directory.Exists(cacheDirectory))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - (maximumAge ?? TimeSpan.FromDays(90));
        var files = new DirectoryInfo(cacheDirectory)
            .EnumerateFiles("*.png")
            .OrderByDescending(file => file.LastAccessTimeUtc)
            .ToArray();
        foreach (var file in files.Where((file, index) => index >= maximumFiles || file.LastAccessTimeUtc < cutoff))
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    });

    /// <summary>
    /// 从磁盘缓存读取图标，不存在时从 Shell 提取并保存。
    /// </summary>
    /// <param name="item">启动项目副本。</param>
    /// <param name="key">图标缓存键。</param>
    /// <returns>加载或提取的图像。</returns>
    private async Task<ImageSource?> LoadOrExtractAsync(LauncherItem item, string key)
    {
        var cachePath = Path.Combine(cacheDirectory, $"{key}.png");
        if (File.Exists(cachePath))
        {
            try
            {
                return await Task.Run(() => LoadBitmap(cachePath)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
        }

        var source = await Task.Run(() => ExtractIcon(item)).ConfigureAwait(false);
        if (source is not BitmapSource bitmap)
        {
            return source;
        }

        try
        {
            Directory.CreateDirectory(cacheDirectory);
            await Task.Run(() => SavePng(bitmap, cachePath)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return source;
    }

    /// <summary>
    /// 优先加载自定义图片，否则提取目标关联的 Shell 图标。
    /// </summary>
    /// <param name="item">待提取图标的启动项目。</param>
    /// <returns>冻结后的图标；失败时返回空。</returns>
    private static ImageSource? ExtractIcon(LauncherItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.CustomIconPath) && File.Exists(item.CustomIconPath))
        {
            try
            {
                return LoadBitmap(item.CustomIconPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
        }

        var target = item.Target;
        if (string.IsNullOrWhiteSpace(target) || item.TargetType == LauncherTargetType.Url)
        {
            return null;
        }

        var flags = ShgfiIcon | ShgfiLargeIcon;
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            flags |= ShgfiUseFileAttributes;
        }

        var info = new ShellFileInfo();
        var result = SHGetFileInfo(
            target,
            FileAttributeNormal,
            ref info,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            flags);
        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(64, 64));
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    /// <summary>
    /// 以不锁定源文件的方式读取位图。
    /// </summary>
    /// <param name="path">图片文件路径。</param>
    /// <returns>冻结后的位图。</returns>
    private static BitmapSource LoadBitmap(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var bitmap = new WriteableBitmap(decoder.Frames[0]);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// 将位图编码为 PNG 缓存文件。
    /// </summary>
    /// <param name="bitmap">需要编码的位图。</param>
    /// <param name="path">缓存文件路径。</param>
    private static void SavePng(BitmapSource bitmap, string path)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(stream);
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// 根据图标来源及其修改时间计算稳定缓存键。
    /// </summary>
    /// <param name="item">启动项目。</param>
    /// <returns>十六进制 SHA-256 缓存键。</returns>
    private static string CreateCacheKey(LauncherItem item)
    {
        var source = !string.IsNullOrWhiteSpace(item.CustomIconPath)
            ? item.CustomIconPath
            : item.Target;
        long timestamp = 0;
        try
        {
            if (File.Exists(source))
            {
                timestamp = File.GetLastWriteTimeUtc(source).Ticks;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        var value = $"{source.ToUpperInvariant()}|{timestamp}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>
    /// 保存一个内存缓存加载任务及其最近使用顺序节点。
    /// </summary>
    /// <param name="Loader">合并并发图标请求的延迟加载任务。</param>
    /// <param name="UsageNode">最近使用顺序链表中的节点。</param>
    private sealed record MemoryCacheEntry(
        Lazy<Task<ImageSource?>> Loader,
        LinkedListNode<string> UsageNode);

    /// <summary>
    /// 调用 Windows Shell 获取文件关联图标。
    /// </summary>
    /// <param name="path">文件或目录路径。</param>
    /// <param name="fileAttributes">目标不存在时使用的文件属性。</param>
    /// <param name="fileInfo">接收 Shell 文件信息的结构。</param>
    /// <param name="fileInfoSize">结构体字节数。</param>
    /// <param name="flags">请求的 Shell 信息标志。</param>
    /// <returns>成功时返回非零句柄。</returns>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    /// <summary>
    /// 释放 Shell API 返回的原生图标句柄。
    /// </summary>
    /// <param name="iconHandle">需要释放的图标句柄。</param>
    /// <returns>释放成功时返回 <see langword="true"/>。</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }
}
