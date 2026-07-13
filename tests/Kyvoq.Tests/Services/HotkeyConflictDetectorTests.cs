using Kyvoq.Core.Models;
using Kyvoq.Core.Services;

namespace Kyvoq.Tests.Services;

/// <summary>
/// 验证配置内部快捷键冲突检测。
/// </summary>
public sealed class HotkeyConflictDetectorTests
{
    /// <summary>
    /// 验证重复绑定同一组合键的全部项目都会被标记为冲突。
    /// </summary>
    [Fact]
    public void FindItemConflicts_ShouldMarkEveryDuplicatedItem()
    {
        var gesture = new HotkeyGesture
        {
            Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt,
            VirtualKey = 0x41
        };
        var first = new LauncherItem { Name = "A", Target = @"C:\A.exe", Hotkey = gesture };
        var second = new LauncherItem { Name = "B", Target = @"C:\B.exe", Hotkey = gesture with { } };

        var conflicts = HotkeyConflictDetector.FindItemConflicts(
            HotkeyGesture.CreateDefaultMainWindow(),
            [first, second]);

        Assert.Equal(2, conflicts.Count);
        Assert.Contains(first.Id, conflicts);
        Assert.Contains(second.Id, conflicts);
    }

    /// <summary>
    /// 验证占用主界面呼出快捷键的项目会被标记为冲突。
    /// </summary>
    [Fact]
    public void FindItemConflicts_ShouldMarkItemMatchingMainWindowHotkey()
    {
        var summon = HotkeyGesture.CreateDefaultMainWindow();
        var item = new LauncherItem
        {
            Name = "冲突项目",
            Target = @"C:\Conflict.exe",
            Hotkey = summon with { }
        };

        var conflicts = HotkeyConflictDetector.FindItemConflicts(summon, [item]);

        Assert.Contains(item.Id, conflicts);
    }

    /// <summary>
    /// 验证不同且有效的项目快捷键不会产生误报。
    /// </summary>
    [Fact]
    public void FindItemConflicts_ShouldIgnoreDistinctGestures()
    {
        var first = new LauncherItem
        {
            Name = "A",
            Target = @"C:\A.exe",
            Hotkey = new HotkeyGesture { Modifiers = HotkeyModifiers.Control, VirtualKey = 0x41 }
        };
        var second = new LauncherItem
        {
            Name = "B",
            Target = @"C:\B.exe",
            Hotkey = new HotkeyGesture { Modifiers = HotkeyModifiers.Control, VirtualKey = 0x42 }
        };

        var conflicts = HotkeyConflictDetector.FindItemConflicts(
            HotkeyGesture.CreateDefaultMainWindow(),
            [first, second]);

        Assert.Empty(conflicts);
    }
}
