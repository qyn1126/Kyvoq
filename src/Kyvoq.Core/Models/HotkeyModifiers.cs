namespace Kyvoq.Core.Models;

/// <summary>
/// 表示全局快捷键的修饰键。
/// </summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008
}
