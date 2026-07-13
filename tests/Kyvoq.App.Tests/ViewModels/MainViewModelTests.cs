using System.IO;
using Kyvoq.App.Services;
using Kyvoq.App.ViewModels;
using Kyvoq.Core.Models;
using Kyvoq.Core.Persistence;

namespace Kyvoq.App.Tests.ViewModels;

/// <summary>
/// 验证主视图模型对分组和项目变更采用增量集合更新。
/// </summary>
public sealed class MainViewModelTests
{
    /// <summary>
    /// 验证移动、重排和恢复单个项目时保留未受影响及被移动视图模型的身份与冲突状态。
    /// </summary>
    [Fact]
    public void ItemOperations_ShouldPreserveExistingViewModelsAndConflictState()
    {
        var firstItem = CreateItem("A", 0);
        var secondItem = CreateItem("B", 1);
        var thirdItem = CreateItem("C", 0);
        var firstGroup = new LauncherGroup
        {
            Name = "第一组",
            SortOrder = 0,
            Items = [firstItem, secondItem]
        };
        var secondGroup = new LauncherGroup
        {
            Name = "第二组",
            SortOrder = 1,
            Items = [thirdItem]
        };
        var configuration = new LauncherConfiguration
        {
            Groups = [firstGroup, secondGroup]
        };
        using var viewModel = new MainViewModel(
            configuration,
            new StubConfigurationStore(),
            new IconCacheService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        var firstGroupViewModel = viewModel.Groups[0];
        var secondGroupViewModel = viewModel.Groups[1];
        var firstItemViewModel = firstGroupViewModel.Items[0];
        var secondItemViewModel = firstGroupViewModel.Items[1];
        viewModel.SetHotkeyConflicts(new HashSet<Guid> { secondItem.Id });

        viewModel.MoveItem(firstItem.Id, secondGroup.Id);
        viewModel.SelectedGroup = secondGroupViewModel;
        viewModel.ReorderItem(firstItem.Id, thirdItem.Id);
        var removed = viewModel.RemoveItem(thirdItem.Id);
        viewModel.RestoreItem(removed);
        viewModel.MoveGroup(secondGroup.Id, -1);

        Assert.Same(firstGroupViewModel, viewModel.Groups[1]);
        Assert.Same(secondGroupViewModel, viewModel.Groups[0]);
        Assert.Same(firstItemViewModel, viewModel.FindItem(firstItem.Id)?.Item);
        Assert.Same(secondItemViewModel, viewModel.FindItem(secondItem.Id)?.Item);
        Assert.True(secondItemViewModel.HasHotkeyConflict);
    }

    /// <summary>
    /// 验证仅修改项目名称时不会通知应用重新应用全局快捷键。
    /// </summary>
    [Fact]
    public void UpdateItem_ShouldNotNotifyHotkeyChangeWhenGestureIsUnchanged()
    {
        var item = CreateItem("A", 0);
        var configuration = new LauncherConfiguration
        {
            Groups =
            [
                new LauncherGroup
                {
                    Name = "常用",
                    Items = [item]
                }
            ]
        };
        using var viewModel = new MainViewModel(
            configuration,
            new StubConfigurationStore(),
            new IconCacheService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        var hotkeyNotifications = 0;
        viewModel.HotkeyConfigurationChanged += (_, _) => hotkeyNotifications++;
        viewModel.SetHotkeyConflicts(new HashSet<Guid> { item.Id });
        var updated = item.Clone();
        updated.Name = "已重命名";

        viewModel.UpdateItem(updated);

        Assert.Equal(0, hotkeyNotifications);
        Assert.True(viewModel.FindItem(item.Id)?.Item.HasHotkeyConflict);
    }

    /// <summary>
    /// 创建具有稳定排序号的测试项目。
    /// </summary>
    /// <param name="name">项目名称。</param>
    /// <param name="sortOrder">项目排序号。</param>
    /// <returns>测试项目。</returns>
    private static LauncherItem CreateItem(string name, int sortOrder) => new()
    {
        Name = name,
        Target = $@"C:\{name}.exe",
        SortOrder = sortOrder
    };

    /// <summary>
    /// 提供不访问磁盘的配置存储测试替身。
    /// </summary>
    private sealed class StubConfigurationStore : IConfigurationStore
    {
        /// <summary>
        /// 返回默认配置加载结果。
        /// </summary>
        /// <param name="cancellationToken">测试取消令牌。</param>
        /// <returns>默认配置加载结果。</returns>
        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConfigurationLoadResult(
                LauncherConfiguration.CreateDefault(),
                ConfigurationLoadState.CreatedDefault,
                string.Empty));

        /// <summary>
        /// 模拟保存成功。
        /// </summary>
        /// <param name="configuration">待保存配置。</param>
        /// <param name="cancellationToken">测试取消令牌。</param>
        /// <returns>已完成任务。</returns>
        public Task SaveAsync(
            LauncherConfiguration configuration,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// 模拟导出成功。
        /// </summary>
        /// <param name="configuration">待导出配置。</param>
        /// <param name="destinationPath">导出路径。</param>
        /// <param name="cancellationToken">测试取消令牌。</param>
        /// <returns>已完成任务。</returns>
        public Task ExportAsync(
            LauncherConfiguration configuration,
            string destinationPath,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// 返回默认导入配置。
        /// </summary>
        /// <param name="sourcePath">导入路径。</param>
        /// <param name="cancellationToken">测试取消令牌。</param>
        /// <returns>默认配置。</returns>
        public Task<LauncherConfiguration> ImportAsync(
            string sourcePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(LauncherConfiguration.CreateDefault());
    }
}
