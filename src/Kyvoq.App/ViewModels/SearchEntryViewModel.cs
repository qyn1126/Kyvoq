using System.Windows.Media;
using Kyvoq.Core.Models;

namespace Kyvoq.App.ViewModels;

/// <summary>
/// 表示快速搜索面板中的一个跨分组结果。
/// </summary>
public sealed class SearchEntryViewModel
{
    public string GroupName { get; }

    public LauncherItemViewModel ItemViewModel { get; }

    public LauncherItem Item => ItemViewModel.Model;

    public string Name => ItemViewModel.Name;

    public string Target => ItemViewModel.Target;

    public string HotkeyText => ItemViewModel.HotkeyText;

    public string Initial => ItemViewModel.Initial;

    public ImageSource? Icon => ItemViewModel.Icon;

    /// <summary>
    /// 创建带分组上下文的搜索结果视图模型。
    /// </summary>
    /// <param name="groupName">项目所在分组名称。</param>
    /// <param name="itemViewModel">复用主窗口图标状态的项目视图模型。</param>
    public SearchEntryViewModel(string groupName, LauncherItemViewModel itemViewModel)
    {
        GroupName = groupName;
        ItemViewModel = itemViewModel;
    }
}
