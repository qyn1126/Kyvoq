using Kyvoq.Core.Models;

namespace Kyvoq.Tests.Models;

/// <summary>
/// 验证全局快捷键模型的有效性和显示文本。
/// </summary>
public sealed class HotkeyGestureTests
{
    /// <summary>
    /// 验证默认主界面快捷键固定显示为 Alt+Space。
    /// </summary>
    [Fact]
    public void CreateDefaultMainWindow_ShouldCreateExpectedGesture()
    {
        var gesture = HotkeyGesture.CreateDefaultMainWindow();

        Assert.True(gesture.IsValid());
        Assert.Equal("Alt+Space", gesture.ToString());
    }

    /// <summary>
    /// 验证缺少修饰键的按键不能注册为全局快捷键。
    /// </summary>
    [Fact]
    public void IsValid_ShouldRejectKeyWithoutModifier()
    {
        var gesture = new HotkeyGesture { VirtualKey = 0x41 };

        Assert.False(gesture.IsValid());
    }

    /// <summary>
    /// 验证功能键和多个修饰键能生成稳定可读文本。
    /// </summary>
    [Fact]
    public void ToString_ShouldFormatFunctionKeyAndModifiers()
    {
        var gesture = new HotkeyGesture
        {
            Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Windows,
            VirtualKey = 0x75
        };

        Assert.Equal("Ctrl+Shift+Win+F6", gesture.ToString());
    }

    /// <summary>
    /// 验证超出 Windows 范围的虚拟键和未知修饰位不能注册。
    /// </summary>
    [Fact]
    public void IsValid_ShouldRejectUnsupportedNativeValues()
    {
        var invalidKey = new HotkeyGesture
        {
            Modifiers = HotkeyModifiers.Control,
            VirtualKey = 0x1FF
        };
        var invalidModifier = new HotkeyGesture
        {
            Modifiers = (HotkeyModifiers)0x1000,
            VirtualKey = 0x41
        };

        Assert.False(invalidKey.IsValid());
        Assert.False(invalidModifier.IsValid());
    }
}
