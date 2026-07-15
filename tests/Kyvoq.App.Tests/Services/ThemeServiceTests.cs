using System.Windows.Media;
using Kyvoq.App.Services;
using Kyvoq.Core.Models;
using Wpf.Ui.Controls;

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

    /// <summary>
    /// 验证领域材质设置会映射到对应的 WPF UI 背景类型。
    /// </summary>
    /// <param name="material">待映射的材质设置。</param>
    /// <param name="expected">预期的 WPF UI 背景类型。</param>
    [Theory]
    [InlineData(WindowMaterial.Solid, WindowBackdropType.None)]
    [InlineData(WindowMaterial.Mica, WindowBackdropType.Mica)]
    [InlineData(WindowMaterial.MicaAlt, WindowBackdropType.Tabbed)]
    [InlineData(WindowMaterial.Acrylic, WindowBackdropType.Acrylic)]
    public void ToBackdropType_ShouldMapEveryWindowMaterial(
        WindowMaterial material,
        WindowBackdropType expected)
    {
        Assert.Equal(expected, ThemeService.ToBackdropType(material));
    }

    /// <summary>
    /// 验证原生材质在浅色和深色主题下都不被 WPF 内容底色覆盖。
    /// </summary>
    /// <param name="dark">是否使用深色主题。</param>
    /// <param name="backdropType">待验证的透明材质类型。</param>
    [Theory]
    [InlineData(false, WindowBackdropType.Mica)]
    [InlineData(false, WindowBackdropType.Tabbed)]
    [InlineData(false, WindowBackdropType.Acrylic)]
    [InlineData(true, WindowBackdropType.Mica)]
    [InlineData(true, WindowBackdropType.Tabbed)]
    [InlineData(true, WindowBackdropType.Acrylic)]
    public void GetWindowBackgroundColor_ShouldLeaveNativeBackdropUncovered(
        bool dark,
        WindowBackdropType backdropType)
    {
        var color = ThemeService.GetWindowBackgroundColor(dark, backdropType);

        Assert.Equal(byte.MinValue, color.A);
    }

    /// <summary>
    /// 验证纯色模式使用随主题变化的不透明回退色。
    /// </summary>
    /// <param name="dark">是否使用深色主题。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetWindowBackgroundColor_ShouldUseOpaqueFallbackForSolid(bool dark)
    {
        var color = ThemeService.GetWindowBackgroundColor(dark, WindowBackdropType.None);

        Assert.Equal(byte.MaxValue, color.A);
        Assert.Equal(
            dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3),
            color);
    }
}
