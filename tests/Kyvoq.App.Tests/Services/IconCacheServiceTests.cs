using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kyvoq.App.Services;
using Kyvoq.Core.Models;

namespace Kyvoq.App.Tests.Services;

/// <summary>
/// 验证图标缓存读取和基于修改时间的失效行为。
/// </summary>
public sealed class IconCacheServiceTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "Kyvoq.App.Tests",
        Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 删除测试生成的图片和磁盘缓存。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    /// <summary>
    /// 验证自定义图标文件改变后会生成新缓存键并读取新像素。
    /// </summary>
    [Fact]
    public async Task GetIconAsync_ShouldInvalidateChangedCustomIcon()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var iconPath = Path.Combine(temporaryDirectory, "custom.png");
        var cache = new IconCacheService(Path.Combine(temporaryDirectory, "cache"));
        var item = new LauncherItem
        {
            Name = "图标测试",
            Target = @"C:\Missing.exe",
            CustomIconPath = iconPath
        };
        WriteSolidPng(iconPath, 220, 40, 50);
        var first = Assert.IsAssignableFrom<BitmapSource>(await cache.GetIconAsync(item));

        WriteSolidPng(iconPath, 30, 90, 220);
        File.SetLastWriteTimeUtc(iconPath, DateTime.UtcNow.AddSeconds(2));
        var second = Assert.IsAssignableFrom<BitmapSource>(await cache.GetIconAsync(item));

        Assert.NotEqual(ReadFirstPixel(first), ReadFirstPixel(second));
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(temporaryDirectory, "cache"), "*.png").Count());
    }

    /// <summary>
    /// 验证同一缓存键首次加载失败后不会永久缓存空结果，后续请求可以重新加载成功。
    /// </summary>
    [Fact]
    public async Task GetIconAsync_ShouldRetryAfterTransientFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(temporaryDirectory);
        var iconPath = Path.Combine(temporaryDirectory, "transient.png");
        await File.WriteAllTextAsync(iconPath, "invalid image", cancellationToken);
        File.SetLastWriteTimeUtc(iconPath, DateTime.UtcNow.AddMinutes(-1));
        var stableTimestamp = File.GetLastWriteTimeUtc(iconPath);
        var cache = new IconCacheService(Path.Combine(temporaryDirectory, "cache"));
        var item = new LauncherItem
        {
            Name = "临时失败图标",
            Target = "https://example.com",
            TargetType = LauncherTargetType.Url,
            CustomIconPath = iconPath
        };

        var first = await cache.GetIconAsync(item);
        WriteSolidPng(iconPath, 30, 90, 220);
        File.SetLastWriteTimeUtc(iconPath, stableTimestamp);
        var second = await cache.GetIconAsync(item);

        Assert.Null(first);
        Assert.IsAssignableFrom<BitmapSource>(second);
    }

    /// <summary>
    /// 验证内存图标缓存超过容量时淘汰最久未使用的条目。
    /// </summary>
    [Fact]
    public async Task GetIconAsync_ShouldLimitMemoryCacheSize()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var cache = new IconCacheService(
            Path.Combine(temporaryDirectory, "cache"),
            maximumMemoryEntries: 2);
        for (var index = 0; index < 3; index++)
        {
            var iconPath = Path.Combine(temporaryDirectory, $"bounded-{index}.png");
            WriteSolidPng(iconPath, (byte)(30 + index), 90, 220);
            var item = new LauncherItem
            {
                Name = $"容量图标 {index}",
                Target = @"C:\Missing.exe",
                CustomIconPath = iconPath
            };

            Assert.NotNull(await cache.GetIconAsync(item));
        }

        Assert.Equal(2, cache.MemoryCacheCount);
    }

    /// <summary>
    /// 创建指定颜色的两像素 PNG 文件。
    /// </summary>
    /// <param name="path">图片输出路径。</param>
    /// <param name="red">红色通道。</param>
    /// <param name="green">绿色通道。</param>
    /// <param name="blue">蓝色通道。</param>
    private static void WriteSolidPng(string path, byte red, byte green, byte blue)
    {
        var pixels = new byte[]
        {
            blue, green, red, 255,
            blue, green, red, 255,
            blue, green, red, 255,
            blue, green, red, 255
        };
        var bitmap = BitmapSource.Create(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            8);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    /// <summary>
    /// 读取位图左上角像素并组合为可比较整数。
    /// </summary>
    /// <param name="bitmap">待读取位图。</param>
    /// <returns>BGRA 字节组合后的整数。</returns>
    private static int ReadFirstPixel(BitmapSource bitmap)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        return BitConverter.ToInt32(pixel);
    }
}
