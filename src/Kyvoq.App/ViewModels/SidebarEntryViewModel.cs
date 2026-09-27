namespace Kyvoq.App.ViewModels;

/// <summary>
/// 为普通分组和只读功能分组提供统一的侧栏选择入口。
/// </summary>
public abstract class SidebarEntryViewModel : ObservableObject
{
    public abstract string Name { get; }
    public virtual bool IsSteam => false;
}

/// <summary>
/// 表示只在运行时存在的 Steam 账号分组。
/// </summary>
public sealed class SteamSidebarEntryViewModel : SidebarEntryViewModel
{
    public override string Name => "Steam";
    public override bool IsSteam => true;
}
