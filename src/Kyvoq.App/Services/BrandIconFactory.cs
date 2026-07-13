using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace Kyvoq.App.Services;

/// <summary>
/// 从应用内嵌资源创建统一的 Kyvoq 品牌图标。
/// </summary>
public static class BrandIconFactory
{
    private const string IconResourceName = "Kyvoq.App.Assets.Kyvoq.ico";

    /// <summary>
    /// 创建供通知区域和 Win32 窗口使用的独立图标。
    /// </summary>
    /// <returns>由调用方负责释放的 64 像素内嵌品牌图标。</returns>
    public static Drawing.Icon CreateIcon()
    {
        using var stream = typeof(BrandIconFactory).Assembly.GetManifestResourceStream(IconResourceName)
            ?? throw new InvalidOperationException("找不到内嵌的 Kyvoq 品牌图标。");
        using var icon = new Drawing.Icon(stream, 64, 64);
        return (Drawing.Icon)icon.Clone();
    }

    /// <summary>
    /// 创建可供 WPF 窗口任务栏使用的冻结图像。
    /// </summary>
    /// <returns>品牌图标的 WPF 图像源。</returns>
    public static ImageSource CreateImageSource()
    {
        using var icon = CreateIcon();
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(64, 64));
        source.Freeze();
        return source;
    }

}
