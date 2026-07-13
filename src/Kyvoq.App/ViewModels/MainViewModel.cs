using System.Collections.ObjectModel;
using Kyvoq.App.Services;
using Kyvoq.Core.Models;
using Kyvoq.Core.Persistence;
using Kyvoq.Core.Services;

namespace Kyvoq.App.ViewModels;

/// <summary>
/// 协调主窗口分组、项目和延迟持久化。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IConfigurationStore configurationStore;
    private readonly IconCacheService iconCache;
    private readonly object saveSync = new();
    private LauncherConfiguration configuration;
    private LauncherGroupViewModel? selectedGroup;
    private CancellationTokenSource? pendingSave;
    private bool disposed;

    public event EventHandler? ConfigurationChanged;

    public event EventHandler? HotkeyConfigurationChanged;

    public event Action<string>? SaveFailed;

    public LauncherConfiguration Configuration => configuration;

    public ObservableCollection<LauncherGroupViewModel> Groups { get; } = [];

    public RangeObservableCollection<LauncherItemViewModel> VisibleItems { get; } = [];

    public LauncherGroupViewModel? SelectedGroup
    {
        get => selectedGroup;
        set
        {
            if (SetProperty(ref selectedGroup, value))
            {
                RefreshVisibleItems();
                OnPropertyChanged(nameof(SelectedGroupTitle));
                OnPropertyChanged(nameof(SelectedGroupSubtitle));
            }
        }
    }

    public string SelectedGroupTitle => SelectedGroup?.Name ?? "启动项目";

    public string SelectedGroupSubtitle => SelectedGroup is null
        ? "没有可用分组"
        : $"{SelectedGroup.ItemCount} 个项目";

    /// <summary>
    /// 使用加载完成的配置创建主视图模型。
    /// </summary>
    /// <param name="configuration">当前用户配置。</param>
    /// <param name="configurationStore">配置持久化服务。</param>
    /// <param name="iconCache">图标缓存服务。</param>
    public MainViewModel(
        LauncherConfiguration configuration,
        IConfigurationStore configurationStore,
        IconCacheService iconCache)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.configurationStore = configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
        this.iconCache = iconCache ?? throw new ArgumentNullException(nameof(iconCache));
        ReloadGroups();
    }

    /// <summary>
    /// 新增分组并选中该分组。
    /// </summary>
    /// <param name="name">新分组名称。</param>
    /// <returns>创建的分组视图模型。</returns>
    public LauncherGroupViewModel AddGroup(string name)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var normalizedName = RequireName(name, "分组名称");
        var model = new LauncherGroup
        {
            Name = normalizedName,
            SortOrder = configuration.Groups.Count
        };
        configuration.Groups.Add(model);
        var viewModel = new LauncherGroupViewModel(model, iconCache);
        Groups.Add(viewModel);
        SelectedGroup = viewModel;
        NotifyConfigurationChanged();
        return viewModel;
    }

    /// <summary>
    /// 修改指定分组名称。
    /// </summary>
    /// <param name="groupId">分组标识。</param>
    /// <param name="name">新的分组名称。</param>
    public void RenameGroup(Guid groupId, string name)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var group = FindGroupViewModel(groupId);
        group.Model.Name = RequireName(name, "分组名称");
        group.RefreshName();
        OnPropertyChanged(nameof(SelectedGroupTitle));
        NotifyConfigurationChanged();
    }

    /// <summary>
    /// 调整指定分组的相对位置。
    /// </summary>
    /// <param name="groupId">分组标识。</param>
    /// <param name="offset">相对移动距离。</param>
    /// <returns>实际移动时返回 <see langword="true"/>。</returns>
    public bool MoveGroup(Guid groupId, int offset)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var group = FindGroupViewModel(groupId);
        var sourceIndex = Groups.IndexOf(group);
        if (!ConfigurationOperations.MoveGroup(configuration, groupId, offset))
        {
            return false;
        }

        var targetIndex = configuration.Groups.FindIndex(candidate => candidate.Id == groupId);
        Groups.Move(sourceIndex, targetIndex);
        SelectedGroup = group;
        OnPropertyChanged(nameof(SelectedGroupTitle));
        OnPropertyChanged(nameof(SelectedGroupSubtitle));
        NotifyConfigurationChanged();
        return true;
    }

    /// <summary>
    /// 向指定分组添加启动项目。
    /// </summary>
    /// <param name="groupId">目标分组标识。</param>
    /// <param name="item">待添加项目。</param>
    /// <returns>新项目视图模型。</returns>
    public LauncherItemViewModel AddItem(Guid groupId, LauncherItem item)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(item);
        var group = FindGroupViewModel(groupId);
        NormalizeItem(item);
        if (item.Id == Guid.Empty || FindItem(item.Id) is not null)
        {
            item.Id = Guid.NewGuid();
        }

        item.SortOrder = group.Model.Items.Count;
        group.Model.Items.Add(item);
        var viewModel = new LauncherItemViewModel(item, iconCache);
        group.Items.Add(viewModel);
        RefreshVisibleItems();
        OnPropertyChanged(nameof(SelectedGroupSubtitle));
        NotifyConfigurationChanged(hotkeysChanged: item.Hotkey.IsValid());
        return viewModel;
    }

    /// <summary>
    /// 使用编辑后的副本覆盖现有启动项目。
    /// </summary>
    /// <param name="updatedItem">包含原项目标识的编辑结果。</param>
    public void UpdateItem(LauncherItem updatedItem)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(updatedItem);
        NormalizeItem(updatedItem);
        var location = FindItem(updatedItem.Id)
            ?? throw new KeyNotFoundException("找不到需要更新的启动项目。");
        var hotkeysChanged = location.Item.Model.Hotkey != updatedItem.Hotkey;
        updatedItem.SortOrder = location.Item.Model.SortOrder;
        var index = location.Group.Model.Items.FindIndex(item => item.Id == updatedItem.Id);
        location.Group.Model.Items[index] = updatedItem;
        var viewModelIndex = location.Group.Items.IndexOf(location.Item);
        location.Group.Items[viewModelIndex] = new LauncherItemViewModel(updatedItem, iconCache)
        {
            HasHotkeyConflict = location.Item.HasHotkeyConflict
        };
        RefreshVisibleItems();
        NotifyConfigurationChanged(hotkeysChanged);
    }

    /// <summary>
    /// 删除指定启动项目。
    /// </summary>
    /// <param name="itemId">待删除项目标识。</param>
    public DeletedItemSnapshot RemoveItem(Guid itemId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var snapshot = ConfigurationOperations.RemoveItem(configuration, itemId);
        var location = FindGroupViewModel(snapshot.GroupId);
        location.Items.Remove(location.Items.First(item => item.Id == itemId));
        RefreshVisibleItems();
        OnPropertyChanged(nameof(SelectedGroupSubtitle));
        NotifyConfigurationChanged(hotkeysChanged: snapshot.Item.Hotkey.IsValid());
        return snapshot;
    }

    /// <summary>
    /// 恢复最近删除的启动项目。
    /// </summary>
    /// <param name="snapshot">项目删除时生成的快照。</param>
    public void RestoreItem(DeletedItemSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ConfigurationOperations.RestoreItem(configuration, snapshot);
        var group = FindGroupViewModel(snapshot.GroupId);
        var itemIndex = group.Model.Items.FindIndex(item => item.Id == snapshot.Item.Id);
        group.Items.Insert(itemIndex, new LauncherItemViewModel(snapshot.Item, iconCache));
        if (ReferenceEquals(SelectedGroup, group))
        {
            RefreshVisibleItems();
        }
        else
        {
            SelectedGroup = group;
        }

        OnPropertyChanged(nameof(SelectedGroupSubtitle));
        NotifyConfigurationChanged(hotkeysChanged: snapshot.Item.Hotkey.IsValid());
    }

    /// <summary>
    /// 删除分组及其项目并返回可撤销快照。
    /// </summary>
    /// <param name="groupId">待删除分组标识。</param>
    /// <returns>包含原始分组位置和内容的快照。</returns>
    public DeletedGroupSnapshot RemoveGroup(Guid groupId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var snapshot = ConfigurationOperations.RemoveGroup(configuration, groupId);
        Groups.Remove(FindGroupViewModel(groupId));
        SelectedGroup = Groups[Math.Min(snapshot.GroupIndex, Groups.Count - 1)];
        NotifyConfigurationChanged(
            hotkeysChanged: snapshot.Group.Items.Any(item => item.Hotkey.IsValid()));
        return snapshot;
    }

    /// <summary>
    /// 恢复最近删除的分组及其全部项目。
    /// </summary>
    /// <param name="snapshot">分组删除时生成的快照。</param>
    public void RestoreGroup(DeletedGroupSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ConfigurationOperations.RestoreGroup(configuration, snapshot);
        var group = new LauncherGroupViewModel(snapshot.Group, iconCache);
        Groups.Insert(Math.Clamp(snapshot.GroupIndex, 0, Groups.Count), group);
        SelectedGroup = group;
        NotifyConfigurationChanged(
            hotkeysChanged: snapshot.Group.Items.Any(item => item.Hotkey.IsValid()));
    }

    /// <summary>
    /// 把项目移动到指定分组末尾。
    /// </summary>
    /// <param name="itemId">待移动项目标识。</param>
    /// <param name="targetGroupId">目标分组标识。</param>
    public void MoveItem(Guid itemId, Guid targetGroupId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var source = FindItem(itemId)
            ?? throw new KeyNotFoundException("找不到需要移动的启动项目。");
        var targetGroup = FindGroupViewModel(targetGroupId);
        ConfigurationOperations.MoveItem(configuration, itemId, targetGroupId);
        source.Group.Items.Remove(source.Item);
        var targetIndex = targetGroup.Model.Items.FindIndex(item => item.Id == itemId);
        targetGroup.Items.Insert(targetIndex, source.Item);
        if (ReferenceEquals(SelectedGroup, targetGroup))
        {
            RefreshVisibleItems();
        }
        else
        {
            SelectedGroup = targetGroup;
        }

        OnPropertyChanged(nameof(SelectedGroupSubtitle));
        NotifyConfigurationChanged();
    }

    /// <summary>
    /// 在当前分组中重新排列项目。
    /// </summary>
    /// <param name="itemId">待移动项目标识。</param>
    /// <param name="beforeItemId">目标项目标识；为空时移动到末尾。</param>
    public void ReorderItem(Guid itemId, Guid? beforeItemId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var group = SelectedGroup ?? throw new InvalidOperationException("当前没有选中的分组。");
        var item = group.Items.FirstOrDefault(candidate => candidate.Id == itemId)
            ?? throw new KeyNotFoundException("找不到需要排序的启动项目。");
        var sourceIndex = group.Items.IndexOf(item);
        ConfigurationOperations.ReorderItem(group.Model, itemId, beforeItemId);
        var targetIndex = group.Model.Items.FindIndex(candidate => candidate.Id == itemId);
        if (sourceIndex != targetIndex)
        {
            group.Items.Move(sourceIndex, targetIndex);
            RefreshVisibleItems();
        }

        NotifyConfigurationChanged();
    }

    /// <summary>
    /// 使用导入配置替换当前全部数据。
    /// </summary>
    /// <param name="replacement">完成校验的导入配置。</param>
    /// <returns>配置保存完成时结束的任务。</returns>
    public async Task ReplaceConfigurationAsync(LauncherConfiguration replacement)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        configuration = ConfigurationValidator.ValidateAndNormalize(replacement);
        ReloadGroups();
        await configurationStore.SaveAsync(configuration.Clone());
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        HotkeyConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 使用设置窗口返回的完整设置替换当前设置。
    /// </summary>
    /// <param name="settings">新的应用设置。</param>
    public void UpdateSettings(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var updatedSettings = settings?.Clone() ?? throw new ArgumentNullException(nameof(settings));
        var hotkeysChanged = configuration.Settings.MainWindowHotkey != updatedSettings.MainWindowHotkey
            || configuration.Settings.ItemHotkeysEnabled != updatedSettings.ItemHotkeysEnabled;
        configuration.Settings = updatedSettings;
        NotifyConfigurationChanged(hotkeysChanged);
    }

    /// <summary>
    /// 更新主窗口尺寸、位置与最大化状态。
    /// </summary>
    /// <param name="width">还原状态下的窗口宽度。</param>
    /// <param name="height">还原状态下的窗口高度。</param>
    /// <param name="left">还原状态下的左侧坐标。</param>
    /// <param name="top">还原状态下的顶部坐标。</param>
    /// <param name="isMaximized">窗口是否处于最大化状态。</param>
    public void UpdateWindowPlacement(double width, double height, double left, double top, bool isMaximized)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var settings = configuration.Settings;
        if (Math.Abs(settings.WindowWidth - width) < 0.5
            && Math.Abs(settings.WindowHeight - height) < 0.5
            && Math.Abs((settings.WindowLeft ?? left) - left) < 0.5
            && Math.Abs((settings.WindowTop ?? top) - top) < 0.5
            && settings.IsMaximized == isMaximized)
        {
            return;
        }

        settings.WindowWidth = width;
        settings.WindowHeight = height;
        settings.WindowLeft = left;
        settings.WindowTop = top;
        settings.IsMaximized = isMaximized;
        ScheduleSave();
    }

    /// <summary>
    /// 查找指定标识的启动项目及所在分组。
    /// </summary>
    /// <param name="itemId">启动项目标识。</param>
    /// <returns>找到的位置；不存在时返回空。</returns>
    public (LauncherGroupViewModel Group, LauncherItemViewModel Item)? FindItem(Guid itemId)
    {
        foreach (var group in Groups)
        {
            var item = group.Items.FirstOrDefault(candidate => candidate.Id == itemId);
            if (item is not null)
            {
                return (group, item);
            }
        }

        return null;
    }

    /// <summary>
    /// 枚举当前配置中的全部项目视图模型。
    /// </summary>
    /// <returns>按分组及项目顺序排列的项目。</returns>
    public IReadOnlyList<LauncherItemViewModel> GetAllItems() =>
        Groups.SelectMany(group => group.Items).ToArray();

    /// <summary>
    /// 标记未能成功注册全局快捷键的项目。
    /// </summary>
    /// <param name="conflictingItemIds">快捷键冲突的项目标识。</param>
    public void SetHotkeyConflicts(IReadOnlySet<Guid> conflictingItemIds)
    {
        ArgumentNullException.ThrowIfNull(conflictingItemIds);
        foreach (var item in GetAllItems())
        {
            item.HasHotkeyConflict = conflictingItemIds.Contains(item.Id);
        }
    }

    /// <summary>
    /// 立即完成尚未执行的延迟保存。
    /// </summary>
    /// <returns>配置写入完成时结束的任务。</returns>
    public async Task FlushSaveAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        CancellationTokenSource? save;
        lock (saveSync)
        {
            save = pendingSave;
            pendingSave = null;
        }

        save?.Cancel();
        save?.Dispose();
        await configurationStore.SaveAsync(configuration.Clone());
    }

    /// <summary>
    /// 释放延迟保存使用的取消令牌。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lock (saveSync)
        {
            pendingSave?.Cancel();
            pendingSave?.Dispose();
            pendingSave = null;
        }
    }

    /// <summary>
    /// 从当前模型重新创建分组视图模型，并尽量保留选中项。
    /// </summary>
    /// <param name="preferredGroupId">希望保持选中的分组标识。</param>
    private void ReloadGroups(Guid? preferredGroupId = null)
    {
        var selectionId = preferredGroupId ?? SelectedGroup?.Id;
        Groups.Clear();
        foreach (var group in configuration.Groups.OrderBy(group => group.SortOrder))
        {
            Groups.Add(new LauncherGroupViewModel(group, iconCache));
        }

        SelectedGroup = Groups.FirstOrDefault(group => group.Id == selectionId) ?? Groups.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedGroupTitle));
        OnPropertyChanged(nameof(SelectedGroupSubtitle));
    }

    /// <summary>
    /// 根据当前分组刷新网格内容。
    /// </summary>
    private void RefreshVisibleItems()
    {
        if (SelectedGroup is null)
        {
            VisibleItems.ReplaceAll([]);
            return;
        }

        VisibleItems.ReplaceAll(SelectedGroup.Items);
    }

    /// <summary>
    /// 通知配置发生变化，并安排短延迟保存。
    /// </summary>
    /// <param name="hotkeysChanged">是否需要重新注册全局快捷键。</param>
    private void NotifyConfigurationChanged(bool hotkeysChanged = false)
    {
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        if (hotkeysChanged)
        {
            HotkeyConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }

        ScheduleSave();
    }

    /// <summary>
    /// 合并短时间内的多次修改，在 250 毫秒后保存配置。
    /// </summary>
    private void ScheduleSave()
    {
        CancellationTokenSource current;
        lock (saveSync)
        {
            pendingSave?.Cancel();
            pendingSave?.Dispose();
            pendingSave = new CancellationTokenSource();
            current = pendingSave;
        }

        _ = SaveAfterDelayAsync(current);
    }

    /// <summary>
    /// 等待防抖间隔后保存当前配置。
    /// </summary>
    /// <param name="save">本次延迟保存的取消源。</param>
    /// <returns>保存完成或取消时结束的任务。</returns>
    private async Task SaveAfterDelayAsync(CancellationTokenSource save)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), save.Token);
            await configurationStore.SaveAsync(configuration.Clone(), save.Token);
            lock (saveSync)
            {
                if (ReferenceEquals(pendingSave, save))
                {
                    pendingSave = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SaveFailed?.Invoke($"保存配置失败：{exception.Message}");
        }
        finally
        {
            save.Dispose();
        }
    }

    /// <summary>
    /// 查找指定分组视图模型。
    /// </summary>
    /// <param name="groupId">分组标识。</param>
    /// <returns>对应分组视图模型。</returns>
    /// <exception cref="KeyNotFoundException">分组不存在时抛出。</exception>
    private LauncherGroupViewModel FindGroupViewModel(Guid groupId) =>
        Groups.FirstOrDefault(group => group.Id == groupId)
        ?? throw new KeyNotFoundException("找不到指定分组。");

    /// <summary>
    /// 规范化启动项目的可编辑字段并重新识别目标类型。
    /// </summary>
    /// <param name="item">需要规范化的项目。</param>
    private static void NormalizeItem(LauncherItem item)
    {
        item.Name = RequireName(item.Name, "项目名称");
        item.Target = RequireName(item.Target, "启动目标");
        item.Arguments = item.Arguments?.Trim() ?? string.Empty;
        item.EnvironmentVariables ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        item.CustomIconPath = item.CustomIconPath?.Trim() ?? string.Empty;
        item.Hotkey ??= HotkeyGesture.Empty();
        item.TargetType = TargetClassifier.Classify(item.Target);
        if (item.TargetType != LauncherTargetType.Application)
        {
            item.Arguments = string.Empty;
            item.EnvironmentVariables.Clear();
            item.RunAsAdministrator = false;
        }
    }

    /// <summary>
    /// 校验名称并去除首尾空白。
    /// </summary>
    /// <param name="value">待校验文本。</param>
    /// <param name="fieldName">字段显示名称。</param>
    /// <returns>规范化文本。</returns>
    private static string RequireName(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new ArgumentException($"{fieldName}不能为空。", nameof(value));
        }

        return normalized;
    }

}
