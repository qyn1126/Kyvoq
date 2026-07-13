using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using Kyvoq.Core.Models;

namespace Kyvoq.App.Controls;

/// <summary>
/// 捕获并显示包含修饰键的全局快捷键组合。
/// </summary>
public sealed class HotkeyBox : TextBox
{
    private HotkeyGesture gesture = HotkeyGesture.Empty();

    public event EventHandler? GestureChanged;

    public HotkeyGesture Gesture
    {
        get => gesture;
        private set
        {
            gesture = value;
            Text = value.IsEmpty ? "未设置" : value.ToString();
            GestureChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 创建只读但可捕获按键的快捷键输入框。
    /// </summary>
    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        MinHeight = 36;
        Padding = new Thickness(12, 4, 12, 4);
        VerticalContentAlignment = VerticalAlignment.Center;
        Cursor = Cursors.Hand;
        ToolTip = "点击后按下组合键；Backspace 可清除";
        Gesture = HotkeyGesture.Empty();
    }

    /// <summary>
    /// 从已有配置更新当前快捷键。
    /// </summary>
    /// <param name="value">需要显示的快捷键。</param>
    public void SetGesture(HotkeyGesture? value) => Gesture = value ?? HotkeyGesture.Empty();

    /// <summary>
    /// 捕获按键并转换为 Windows 虚拟键组合。
    /// </summary>
    /// <param name="eventArgs">键盘事件参数。</param>
    protected override void OnPreviewKeyDown(KeyEventArgs eventArgs)
    {
        base.OnPreviewKeyDown(eventArgs);
        var key = eventArgs.Key == Key.System ? eventArgs.SystemKey : eventArgs.Key;
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            return;
        }

        eventArgs.Handled = true;
        if (key is Key.Back or Key.Delete or Key.Escape)
        {
            Gesture = HotkeyGesture.Empty();
            return;
        }

        if (IsModifierKey(key))
        {
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        Gesture = new HotkeyGesture
        {
            Modifiers = modifiers,
            VirtualKey = (uint)KeyInterop.VirtualKeyFromKey(key)
        };
    }

    /// <summary>
    /// 判断按键是否只是修饰键本身。
    /// </summary>
    /// <param name="key">待判断按键。</param>
    /// <returns>属于 Ctrl、Alt、Shift 或 Windows 键时返回 <see langword="true"/>。</returns>
    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin;
}
