using System.Windows.Media.Imaging;
using Kyvoq.App.Services;

namespace Kyvoq.App.Tests.Services;

/// <summary>
/// 验证内嵌品牌图标可用于托盘和 WPF 窗口。
/// </summary>
public sealed class BrandIconFactoryTests
{
    /// <summary>
    /// 验证 Win32 图标和冻结后的 WPF 图像均能成功创建。
    /// </summary>
    [Fact]
    public void CreateIcon_ShouldProduceExpectedDimensions()
    {
        using var icon = BrandIconFactory.CreateIcon();
        var image = Assert.IsAssignableFrom<BitmapSource>(BrandIconFactory.CreateImageSource());

        Assert.Equal(64, icon.Width);
        Assert.Equal(64, icon.Height);
        Assert.Equal(64, image.PixelWidth);
        Assert.Equal(64, image.PixelHeight);
        Assert.True(image.IsFrozen);
    }
}
