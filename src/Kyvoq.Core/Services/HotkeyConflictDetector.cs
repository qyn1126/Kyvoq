using Kyvoq.Core.Models;

namespace Kyvoq.Core.Services;

/// <summary>
/// 检测配置内部项目快捷键之间及其与主界面快捷键的冲突。
/// </summary>
public static class HotkeyConflictDetector
{
    /// <summary>
    /// 返回与主界面快捷键相同或被多个项目重复使用的项目标识。
    /// </summary>
    /// <param name="mainWindowHotkey">主界面呼出快捷键。</param>
    /// <param name="items">全部启动项目。</param>
    /// <returns>存在配置内冲突的项目标识集合。</returns>
    public static IReadOnlySet<Guid> FindItemConflicts(
        HotkeyGesture mainWindowHotkey,
        IEnumerable<LauncherItem> items)
    {
        ArgumentNullException.ThrowIfNull(mainWindowHotkey);
        ArgumentNullException.ThrowIfNull(items);
        var conflicts = new HashSet<Guid>();
        var groups = items
            .Where(item => item.Hotkey.IsValid())
            .GroupBy(item => item.Hotkey);
        foreach (var group in groups)
        {
            var groupedItems = group.ToArray();
            if (group.Key == mainWindowHotkey || groupedItems.Length > 1)
            {
                conflicts.UnionWith(groupedItems.Select(item => item.Id));
            }
        }

        return conflicts;
    }
}
