using System.Windows.Media;
using Kyvoq.App.Services;

namespace Kyvoq.App.Tests.Services;

/// <summary>
/// 验证自定义强调色的持久化转换。
/// </summary>
public sealed class ThemeServiceTests
{
    /// <summary>
    /// 验证 RGB 颜色转换为 ARGB 后可以无损还原且始终保持不透明。
    /// </summary>
    [Fact]
    public void AccentColorConversion_ShouldRoundTripOpaqueColor()
    {
        var source = Color.FromRgb(0x33, 0x66, 0x99);

        var argb = ThemeService.ToArgb(source);
        var restored = ThemeService.ToColor(argb);

        Assert.Equal(0xFF336699u, argb);
        Assert.Equal(Color.FromArgb(0xFF, 0x33, 0x66, 0x99), restored);
    }
}
