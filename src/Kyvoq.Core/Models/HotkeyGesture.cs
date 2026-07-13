using System.Text.Json.Serialization;

namespace Kyvoq.Core.Models;

/// <summary>
/// 表示可持久化的全局快捷键组合。
/// </summary>
public sealed record HotkeyGesture
{
    public HotkeyModifiers Modifiers { get; init; }

    public uint VirtualKey { get; init; }

    [JsonIgnore]
    public bool IsEmpty => VirtualKey == 0;

    /// <summary>
    /// 创建空快捷键。
    /// </summary>
    /// <returns>未绑定按键的快捷键。</returns>
    public static HotkeyGesture Empty() => new();

    /// <summary>
    /// 创建默认的主界面呼出快捷键 Alt+Space。
    /// </summary>
    /// <returns>默认主界面快捷键。</returns>
    public static HotkeyGesture CreateDefaultMainWindow() => new()
    {
        Modifiers = HotkeyModifiers.Alt,
        VirtualKey = 0x20
    };

    /// <summary>
    /// 判断快捷键是否适合注册为全局快捷键。
    /// </summary>
    /// <returns>按键和修饰位均处于 Windows 支持范围时返回 <see langword="true"/>。</returns>
    public bool IsValid()
    {
        const HotkeyModifiers supportedModifiers = HotkeyModifiers.Alt
            | HotkeyModifiers.Control
            | HotkeyModifiers.Shift
            | HotkeyModifiers.Windows;
        return VirtualKey is > 0 and <= 0xFE
            && Modifiers != HotkeyModifiers.None
            && (Modifiers & ~supportedModifiers) == 0;
    }

    /// <summary>
    /// 返回便于用户阅读的快捷键文本。
    /// </summary>
    /// <returns>组合键文本；空快捷键返回空字符串。</returns>
    public override string ToString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(GetKeyName(VirtualKey));
        return string.Join('+', parts);
    }

    /// <summary>
    /// 将 Windows 虚拟键代码转换为可读名称。
    /// </summary>
    /// <param name="virtualKey">Windows 虚拟键代码。</param>
    /// <returns>可读按键名称。</returns>
    private static string GetKeyName(uint virtualKey)
    {
        if (virtualKey is >= 0x30 and <= 0x39 || virtualKey is >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x6F}";
        }

        return virtualKey switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            _ => $"VK_{virtualKey:X2}"
        };
    }
}
