using System.Collections.ObjectModel;
using Kyvoq.App.Services;
using Kyvoq.Core.Models;

namespace Kyvoq.App.ViewModels;

/// <summary>
/// 为启动分组提供可观察项目集合。
/// </summary>
public sealed class LauncherGroupViewModel : ObservableObject
{
    public LauncherGroup Model { get; }

    public Guid Id => Model.Id;

    public string Name => Model.Name;

    public int ItemCount => Items.Count;

    public ObservableCollection<LauncherItemViewModel> Items { get; }

    /// <summary>
    /// 从分组模型创建视图模型。
    /// </summary>
    /// <param name="model">底层分组模型。</param>
    /// <param name="iconCache">项目图标缓存。</param>
    public LauncherGroupViewModel(LauncherGroup model, IconCacheService iconCache)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Items = new ObservableCollection<LauncherItemViewModel>(
            model.Items.OrderBy(item => item.SortOrder)
                .Select(item => new LauncherItemViewModel(item, iconCache)));
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ItemCount));
    }

    /// <summary>
    /// 通知界面分组名称已经变化。
    /// </summary>
    public void RefreshName()
    {
        OnPropertyChanged(nameof(Name));
    }
}
